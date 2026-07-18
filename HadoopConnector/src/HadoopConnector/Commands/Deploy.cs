// Commands/Deploy.cs
// ------------------
// `full-deployment` — connection → schema → identity sync → full crawl.
// `ingest`          — content crawl only (connection & schema must exist).
//
// Both support --continuous with scheduled full crawls (--full-crawl-hours,
// 12–168, default 24) and incremental crawls (--incremental-hours, 1–168,
// default 4). The scheduler observes ServiceStop.Token so a service stop (or
// Ctrl+C) never waits out a multi-hour delay; crawl loops observe it at chunk
// boundaries and resume from the checkpoint on the next start.

using HadoopConnector.Infrastructure;

namespace HadoopConnector.Commands;

public static class Deploy
{
    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector");

    public const int DefaultFullCrawlHours = 24;
    public const int DefaultIncrementalHours = 4;

    public static Task<object?> RunFullDeploymentAsync(ParsedArgs args) =>
        RunAsync(args, provision: true, runPrefix: "full_deployment");

    public static Task<object?> RunIngestAsync(ParsedArgs args) =>
        RunAsync(args, provision: false, runPrefix: "ingest");

    /// <summary>Bounded backoff before re-running after a degraded pause: the
    /// breaker open-duration (so a probe is admissible), clamped to [10s, 5m].</summary>
    internal static TimeSpan DegradedRetryDelay()
    {
        var openSeconds = EnvFlags.GetInt("CIRCUIT_BREAKER_OPEN_SECONDS", 30);
        return TimeSpan.FromSeconds(Math.Clamp(openSeconds, 10, 300));
    }

    /// <summary>Validate the continuous-mode schedule bounds (testable, pure).</summary>
    public static (int FullHours, int IncrementalHours) ValidateSchedule(int fullHours, int incrementalHours)
    {
        if (fullHours is < 12 or > 168)
            throw new CliExit(2, "error: --full-crawl-hours must be between 12 and 168");
        if (incrementalHours is < 1 or > 168)
            throw new CliExit(2, "error: --incremental-hours must be between 1 and 168");
        return (fullHours, incrementalHours);
    }

    private static async Task<object?> RunAsync(ParsedArgs args, bool provision, string runPrefix)
    {
        var continuous = args.HasFlag("--continuous");
        var (fullHours, incrementalHours) = ValidateSchedule(
            args.GetInt("--full-crawl-hours", DefaultFullCrawlHours),
            args.GetInt("--incremental-hours", DefaultIncrementalHours));

        using var context = Runtime.Create(args, runPrefix);
        Dashboard.Banner(provision ? "Full deployment" : "Ingest");

        try
        {
            if (provision)
            {
                await context.ProvisionConnectionsAsync(ServiceStop.Token);
            }

            // First cycle is always a full crawl (identity sync included).
            var first = await RunCycleAsync(context, fullCrawl: true);
            if (!continuous)
                return first.Ok;

            var nextFull = DateTime.UtcNow.AddHours(fullHours);
            var nextIncremental = DateTime.UtcNow.AddHours(incrementalHours);
            Dashboard.Line(
                $"Continuous mode: full every {fullHours}h, incremental every {incrementalHours}h. "
                + "Ctrl+C / service stop exits gracefully.");

            // If the first cycle degraded, retry soon (bounded) rather than
            // waiting a full interval — the breaker's open-duration governs when
            // a probe is admitted, so we recover promptly without hammering.
            if (first.Degraded)
                nextIncremental = DateTime.UtcNow.Add(DegradedRetryDelay());

            while (!ServiceStop.Requested)
            {
                var nowUtc = DateTime.UtcNow;
                var next = nextFull <= nextIncremental ? nextFull : nextIncremental;
                var wait = next - nowUtc;
                if (wait > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(wait, ServiceStop.Token);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                }

                LogPruner.PruneIfConfigured(Logging.LogsRoot);
                var isFull = nextFull <= nextIncremental;
                if (isFull)
                {
                    nextFull = DateTime.UtcNow.AddHours(fullHours);
                    nextIncremental = DateTime.UtcNow.AddHours(incrementalHours);
                }
                else
                {
                    nextIncremental = DateTime.UtcNow.AddHours(incrementalHours);
                }
                var result = await RunCycleAsync(context, fullCrawl: isFull);
                if (result.Degraded)
                {
                    // Schedule the next cycle soon so recovery is timely.
                    var soon = DateTime.UtcNow.Add(DegradedRetryDelay());
                    nextIncremental = soon;
                    if (soon < nextFull)
                        nextFull = soon;
                }
            }
            Dashboard.Line("Continuous mode stopped.");
            return true;
        }
        catch (CliExit)
        {
            throw;
        }
        catch (Exception exc)
        {
            Logger.Error($"{runPrefix} failed", exc);
            await Alerting.RaiseAsync("crawl_failed", exc.Message);
            return false;
        }
    }

    /// <summary>Outcome of one crawl cycle.</summary>
    internal readonly record struct CycleResult(bool Ok, bool Degraded);

    /// <summary>One crawl cycle: (optional) identity sync, then the content crawl.</summary>
    internal static async Task<CycleResult> RunCycleAsync(RuntimeContext context, bool fullCrawl)
    {
        var kind = fullCrawl ? "Full" : "Incremental";
        Dashboard.Line($"{kind} crawl starting...");
        context.InvalidateUserDirectory();

        try
        {
            if (fullCrawl || EnvFlags.IdentitySyncOnIncremental)
            {
                var directory = await context.GetUserDirectoryAsync(ServiceStop.Token);
                await context.IdentitySync.SyncAsync(
                    directory, context.IdentityStore, persist: true, ServiceStop.Token);
            }

            var pipeline = await context.BuildPipelineAsync(ServiceStop.Token);
            var summary = await pipeline.RunAsync(fullCrawl, ct: ServiceStop.Token);
            Dashboard.CrawlSummary(kind, summary);

            if (summary.Degraded)
            {
                await Alerting.RaiseAsync(
                    "degraded_mode",
                    $"{kind} crawl paused in degraded mode (circuit open for "
                    + $"{string.Join(", ", Infrastructure.Breakers.OpenCritical())}) on connector "
                    + $"'{context.Config.ConnectorId}'. Checkpoint retained; will resume on recovery.");
            }
            else if (summary.Failed > 0)
            {
                await Alerting.RaiseAsync(
                    "crawl_failures",
                    $"{kind} crawl finished with {summary.Failed} failed item(s) "
                    + $"for connector '{context.Config.ConnectorId}'",
                    new Dictionary<string, object?> { ["failed"] = summary.Failed });
            }
            return new CycleResult(summary.Failed == 0 && !summary.Degraded, summary.Degraded);
        }
        catch (OperationCanceledException)
        {
            Logger.Warning($"{kind} crawl cancelled by graceful stop; checkpoint retained.");
            return new CycleResult(true, false);
        }
        catch (Exception exc)
        {
            Logger.Error($"{kind} crawl failed", exc);
            await Alerting.RaiseAsync("crawl_failed", exc.Message);
            return new CycleResult(false, false);
        }
    }
}
