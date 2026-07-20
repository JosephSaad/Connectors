// Commands/IngestCommands.cs — ingest / ingest-object / ingest-item.

using System.Diagnostics;
using SeismicConnector.Infrastructure;

namespace SeismicConnector.Commands;

public static class IngestCommands
{
    /// <summary>ingest: content crawl against an existing connection (supports --continuous).</summary>
    public static async Task<object?> CmdIngest(ParsedArgs args)
    {
        var continuous = args.GetFlag("continuous");
        var intervals = CommandRegistry.ReadContinuousIntervals(args);
        var (logFile, summaryFile) = CommandRegistry.SetupLogging("ingest", args.Verbose);
        var progress = Logging.GetLogger("progress");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var runtime = Runtime.Create();
            var state = await runtime.Connection.GetConnectionStateAsync(ServiceStop.Token);
            if (state is null)
            {
                progress.Error(
                    $"Connection '{runtime.Config.Connector.Id}' does not exist — run setup-connection first.");
                return false;
            }

            if (continuous)
            {
                return await Deploy.RunContinuousAsync(
                    runtime, identity: null, logFile, summaryFile,
                    intervals.FullCrawlHours, intervals.IncrementalHours);
            }

            var ok = await RunIngestOnceAsync(
                runtime, logFile, summaryFile, state, stopwatch.Elapsed.TotalSeconds);
            if (!ok)
                await Alerting.RaiseAsync("crawl_failed", "ingest completed with failures");
            return ok;
        }
        catch (OperationCanceledException)
        {
            progress.Warning("Stopped.");
            return true;
        }
        catch (Exception ex)
        {
            progress.Error($"ingest failed: {ex.Message}");
            // Full exception (type + stack) to the log file — the progress line
            // above is the console-friendly summary.
            Logging.GetLogger("seismic_connector").Error("ingest failed", ex);
            await Alerting.RaiseAsync("crawl_failed", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// The single-pass body of <c>ingest</c>: open the run's audit artifacts,
    /// run one incremental crawl cycle, close the artifacts and write the run
    /// summary. Factored out of <see cref="CmdIngest"/> (which owns only the CLI
    /// boilerplate — logging setup, <see cref="Runtime.Create"/>, the
    /// connection-exists check and the --continuous branch) so the artifact
    /// wiring can be driven directly in tests against a hand-built Runtime,
    /// without a network or a loadable environment.
    /// </summary>
    internal static async Task<bool> RunIngestOnceAsync(
        Runtime runtime,
        string logFile,
        string summaryFile,
        string? connectionState,
        double elapsedSeconds)
    {
        using var report = runtime.OpenReport(logFile);
        using var manifest = runtime.OpenManifest(logFile);
        using var ledger = runtime.OpenLedger();
        var ok = await Deploy.RunCrawlCycleAsync(runtime, fullCrawl: false);
        report.Finish();
        manifest.Finish();
        CommandRegistry.WriteSummary(
            summaryFile, logFile, runtime.Pipeline.Stats, connectionState,
            runtime.Config.Connector.Id, elapsedSeconds, "ingest", report.FilePath);
        return ok;
    }

    /// <summary>ingest-object --type X: one schema object type, full pass.</summary>
    public static async Task<object?> CmdIngestObject(ParsedArgs args)
    {
        var objectType = args.GetString("type")!;
        var (logFile, summaryFile) = CommandRegistry.SetupLogging("ingest-object", args.Verbose);
        var progress = Logging.GetLogger("progress");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var runtime = Runtime.Create();
            if (!runtime.Config.EnabledObjects.Contains(objectType, StringComparer.OrdinalIgnoreCase))
            {
                progress.Error(
                    $"Object type '{objectType}' is not an enabled object in config/schema.json "
                    + $"(enabled: {string.Join(", ", runtime.Config.EnabledObjects)}).");
                return false;
            }
            return await RunIngestObjectOnceAsync(
                runtime, objectType, logFile, summaryFile, stopwatch.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            progress.Error($"ingest-object failed: {ex.Message}");
            Logging.GetLogger("seismic_connector").Error("ingest-object failed", ex);
            return false;
        }
    }

    /// <summary>The body of <c>ingest-object</c> — see <see cref="RunIngestOnceAsync"/> for why it is factored out.</summary>
    internal static async Task<bool> RunIngestObjectOnceAsync(
        Runtime runtime, string objectType, string logFile, string summaryFile, double elapsedSeconds)
    {
        using var report = runtime.OpenReport(logFile);
        using var manifest = runtime.OpenManifest(logFile);
        using var ledger = runtime.OpenLedger();
        var ok = await runtime.Pipeline.RunCrawlAsync(
            fullCrawl: true, objectTypeFilter: objectType, ct: ServiceStop.Token);
        report.Finish();
        manifest.Finish();
        CommandRegistry.WriteSummary(
            summaryFile, logFile, runtime.Pipeline.Stats, null,
            runtime.Config.Connector.Id, elapsedSeconds,
            $"ingest-object {objectType}", report.FilePath);
        return ok;
    }

    /// <summary>ingest-item --id X [--teamsite Y]: targeted single-item ingest.</summary>
    public static async Task<object?> CmdIngestItem(ParsedArgs args)
    {
        var contentId = args.GetString("id")!;
        var teamsiteId = args.GetString("teamsite");
        var (logFile, summaryFile) = CommandRegistry.SetupLogging("ingest-item", args.Verbose);
        var progress = Logging.GetLogger("progress");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var runtime = Runtime.Create();
            return await RunIngestItemOnceAsync(
                runtime, contentId, teamsiteId, logFile, summaryFile, stopwatch.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            progress.Error($"ingest-item {contentId} failed: {ex.Message}");
            Logging.GetLogger("seismic_connector").Error($"ingest-item {contentId} failed", ex);
            return false;
        }
    }

    /// <summary>The body of <c>ingest-item</c> — see <see cref="RunIngestOnceAsync"/> for why it is factored out.</summary>
    internal static async Task<bool> RunIngestItemOnceAsync(
        Runtime runtime, string contentId, string? teamsiteId,
        string logFile, string summaryFile, double elapsedSeconds)
    {
        using var report = runtime.OpenReport(logFile);
        using var manifest = runtime.OpenManifest(logFile);
        using var ledger = runtime.OpenLedger();
        var ok = await runtime.Pipeline.IngestSingleAsync(contentId, teamsiteId, ServiceStop.Token);
        report.Finish();
        manifest.Finish();
        CommandRegistry.WriteSummary(
            summaryFile, logFile, runtime.Pipeline.Stats, null,
            runtime.Config.Connector.Id, elapsedSeconds,
            $"ingest-item {contentId}", report.FilePath);
        return ok;
    }
}
