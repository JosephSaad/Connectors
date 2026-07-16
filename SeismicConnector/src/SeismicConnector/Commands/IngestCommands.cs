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

            using var report = runtime.OpenReport(logFile);
            using var manifest = runtime.OpenManifest(logFile);
            var ok = await Deploy.RunCrawlCycleAsync(runtime, fullCrawl: false);
            report.Finish();
            manifest.Finish();
            CommandRegistry.WriteSummary(
                summaryFile, logFile, runtime.Pipeline.Stats, state,
                runtime.Config.Connector.Id, stopwatch.Elapsed.TotalSeconds, "ingest", report.FilePath);
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
            await Alerting.RaiseAsync("crawl_failed", ex.Message);
            return false;
        }
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
            using var report = runtime.OpenReport(logFile);
            using var manifest = runtime.OpenManifest(logFile);
            var ok = await runtime.Pipeline.RunCrawlAsync(
                fullCrawl: true, objectTypeFilter: objectType, ct: ServiceStop.Token);
            report.Finish();
            manifest.Finish();
            CommandRegistry.WriteSummary(
                summaryFile, logFile, runtime.Pipeline.Stats, null,
                runtime.Config.Connector.Id, stopwatch.Elapsed.TotalSeconds,
                $"ingest-object {objectType}", report.FilePath);
            return ok;
        }
        catch (Exception ex)
        {
            progress.Error($"ingest-object failed: {ex.Message}");
            return false;
        }
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
            using var report = runtime.OpenReport(logFile);
            using var manifest = runtime.OpenManifest(logFile);
            var ok = await runtime.Pipeline.IngestSingleAsync(contentId, teamsiteId, ServiceStop.Token);
            report.Finish();
            manifest.Finish();
            CommandRegistry.WriteSummary(
                summaryFile, logFile, runtime.Pipeline.Stats, null,
                runtime.Config.Connector.Id, stopwatch.Elapsed.TotalSeconds,
                $"ingest-item {contentId}", report.FilePath);
            return ok;
        }
        catch (Exception ex)
        {
            progress.Error($"ingest-item failed: {ex.Message}");
            return false;
        }
    }
}
