// Commands/Deploy.cs
// ------------------
// full-deployment: connection → schema → identity crawl → content crawl.
// --continuous keeps the process alive with scheduled full and incremental
// crawls, webhook-event draining between cycles, the Spectre.Console live
// dashboard, and graceful stop via Ctrl+C / SCM (ServiceStop).

using System.Diagnostics;
using SeismicConnector.Config;
using SeismicConnector.Graph;
using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector.Commands;

public static class Deploy
{
    public static async Task<object?> CmdFullDeployment(ParsedArgs args)
    {
        var continuous = args.GetFlag("continuous");
        var intervals = CommandRegistry.ReadContinuousIntervals(args);
        var (logFile, summaryFile) = CommandRegistry.SetupLogging("full-deployment", args.Verbose);
        var progress = Logging.GetLogger("progress");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var runtime = Runtime.Create();
            await EnsureConnectionsAsync(runtime);

            var identity = new IdentitySync(runtime.Seismic, runtime.Graph, runtime.Store);

            if (!continuous)
            {
                await identity.RunAsync(persist: true, ct: ServiceStop.Token);
                using var report = runtime.OpenReport(logFile);
                using var manifest = runtime.OpenManifest(logFile);
                var ok = await RunCrawlCycleAsync(runtime, fullCrawl: true);
                report.Finish();
                manifest.Finish();
                var state = await runtime.Connection.GetConnectionStateAsync(ServiceStop.Token);
                CommandRegistry.WriteSummary(
                    summaryFile, logFile, runtime.Pipeline.Stats, state,
                    runtime.Config.Connector.Id, stopwatch.Elapsed.TotalSeconds,
                    "full-deployment", report.FilePath);
                if (!ok)
                    await Alerting.RaiseAsync("crawl_failed", "full-deployment completed with failures");
                return ok;
            }

            return await RunContinuousAsync(
                runtime, identity, logFile, summaryFile,
                intervals.FullCrawlHours, intervals.IncrementalHours);
        }
        catch (OperationCanceledException)
        {
            progress.Warning("Stopped.");
            return true;
        }
        catch (Exception ex)
        {
            progress.Error($"full-deployment failed: {ex.Message}");
            Logging.GetLogger("seismic_connector").Error("full-deployment failed", ex);
            await Alerting.RaiseAsync("crawl_failed", ex.Message);
            return false;
        }
    }

    /// <summary>Shared continuous scheduler (also used by `ingest --continuous`).</summary>
    internal static async Task<object?> RunContinuousAsync(
        Runtime runtime,
        IdentitySync? identity,
        string logFile,
        string summaryFile,
        int fullCrawlHours,
        int incrementalHours)
    {
        var progress = Logging.GetLogger("progress");
        var config = runtime.Config;

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            progress.Warning("Ctrl+C — finishing the current chunk and saving the checkpoint...");
            ServiceStop.Request();
        };

        using var webhook = WebhookReceiver.StartIfConfigured(config.Seismic.WebhookPort);
        using var dashboard = Dashboard.StartIfInteractive(runtime.Pipeline, webhook);

        var nextFull = DateTime.UtcNow;                    // first cycle is a full crawl
        var nextIncremental = DateTime.UtcNow.AddHours(incrementalHours);

        while (!ServiceStop.Requested)
        {
            var now = DateTime.UtcNow;
            var fullDue = now >= nextFull;
            var incrementalDue = now >= nextIncremental;

            if (fullDue || incrementalDue)
            {
                LogPruner.Prune(CommandRegistry.LogsDir);
                var isFull = fullDue;
                if (isFull && identity is not null)
                    await identity.RunAsync(persist: true, ct: ServiceStop.Token);
                else if (!isFull && identity is not null && EnvFlags.IdentitySyncOnIncremental)
                    await identity.RunAsync(persist: true, ct: ServiceStop.Token);

                using (var report = runtime.OpenReport(logFile))
                using (var manifest = runtime.OpenManifest(logFile))
                {
                    var ok = await RunCrawlCycleAsync(runtime, fullCrawl: isFull);
                    report.Finish();
                    manifest.Finish();
                    if (!ok)
                        await Alerting.RaiseAsync("crawl_failed",
                            $"{(isFull ? "full" : "incremental")} crawl completed with failures");
                }
                if (isFull)
                    nextFull = DateTime.UtcNow.AddHours(fullCrawlHours);
                if (isFull || incrementalDue)
                    nextIncremental = DateTime.UtcNow.AddHours(incrementalHours);
                progress.Info(
                    $"Next full crawl: {nextFull:u}; next incremental: {nextIncremental:u}");
            }

            // Drain webhook events between cycles for near-real-time updates.
            if (webhook is not null)
            {
                var events = webhook.DrainEvents();
                if (events.Count > 0)
                {
                    progress.Info($"Processing {events.Count} webhook event(s)...");
                    await runtime.Pipeline.ProcessEventsAsync(events, ServiceStop.Token);
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ServiceStop.Token);
            }
            catch (OperationCanceledException)
            {
                // Graceful stop requested during the inter-cycle sleep — exit
                // the loop; "Continuous mode stopped." is logged just below.
                break;
            }
        }

        progress.Info("Continuous mode stopped.");
        CommandRegistry.WriteSummary(
            summaryFile, logFile, runtime.Pipeline.Stats, null,
            config.Connector.Id, 0, "continuous");
        return true;
    }

    /// <summary>
    /// Ensure the Graph connection(s) + schema exist: the single connection by
    /// default, or every shard's connection when GRAPH_CONNECTION_SHARDS is set
    /// (docs/SHARDING.md). Invalid sharding config fails fast.
    /// </summary>
    internal static async Task EnsureConnectionsAsync(Runtime runtime)
    {
        if (ShardingConfig.TryLoad(runtime.Config, out var shards, out var shardError))
        {
            foreach (var shard in shards)
            {
                var shardConfig = ShardingConfig.ForShard(runtime.Config, shard);
                var manager = new ConnectionManager(runtime.Graph, shardConfig);
                await manager.EnsureConnectionAsync(ServiceStop.Token);
                await manager.EnsureSchemaAsync(ServiceStop.Token);
            }
            return;
        }
        if (shardError is not null)
            throw new Seismic.ConfigException(shardError);
        await runtime.Connection.EnsureConnectionAsync(ServiceStop.Token);
        await runtime.Connection.EnsureSchemaAsync(ServiceStop.Token);
    }

    /// <summary>
    /// One crawl cycle: the single-connection path by default; with sharding
    /// enabled, each shard ingests ONLY its object types into its own Graph
    /// connection (the shard owning ContentItem also runs the withdrawal
    /// pass). Stats/report/exclusions are shared across shards.
    /// </summary>
    internal static async Task<bool> RunCrawlCycleAsync(Runtime runtime, bool fullCrawl)
    {
        var config = runtime.Config;
        if (ShardingConfig.TryLoad(config, out var shards, out var shardError))
        {
            var ok = true;
            foreach (var shard in shards)
            {
                if (ServiceStop.Requested)
                    break;
                var shardConfig = ShardingConfig.ForShard(config, shard);
                var pipeline = new IngestPipeline(
                    shardConfig, runtime.Seismic, runtime.Graph, runtime.Store,
                    stats: runtime.Pipeline.Stats)
                {
                    Report = runtime.Pipeline.Report,
                };
                foreach (var objectType in shard.ObjectTypes)
                {
                    if (ServiceStop.Requested)
                        break;
                    var withdraw = objectType.Equals("ContentItem", StringComparison.OrdinalIgnoreCase);
                    ok &= await pipeline.RunCrawlAsync(
                        fullCrawl, objectType, ServiceStop.Token, runWithdrawalPass: withdraw);
                }
            }
            return ok;
        }
        if (shardError is not null)
        {
            Logging.GetLogger("progress").Error(shardError);
            return false;
        }
        return await runtime.Pipeline.RunCrawlAsync(fullCrawl, ct: ServiceStop.Token);
    }
}
