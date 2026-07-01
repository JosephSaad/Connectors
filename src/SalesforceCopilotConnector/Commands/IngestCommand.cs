// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// ingest command — re-ingest items into an existing connection.
//
// Assumes that ``full-deployment`` has already been run at least once so that
// the Graph external connection and schema exist.  Steps:
//
// 1. Load configuration.
// 2. Initialise the Graph API client.
// 3. Verify the connection is in the ``ready`` state.
// 4. Ingest all Salesforce items with ACL resolution.
//
// Usage::
//
//     run.py ingest
//     run.py ingest --verbose
//
// Returns ``true`` on success, ``false`` on failure (exit code 1).

using SalesforceCopilotConnector.Config;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Commands;

public static class IngestCommand
{
    // ── Test hooks ───────────────────────────────────────────────────────────
    // Mirror Python monkeypatching of the module-level imports in commands/ingest.py
    // (e.g. patch("commands.ingest.ingest_content")). Default to the real
    // implementations; behavior is identical when unhooked.
    internal static Func<AppConfig> LoadConfigHook = Settings.LoadConfig;
    internal static Func<AppConfig, GraphClient, Task<bool>> IsConnectionReadyHook = Connection.IsConnectionReadyAsync;
    internal static Func<AppConfig, GraphClient, DateTime?, IngestionDashboard?, Task<IngestionStats>> IngestContentHook =
        (config, client, since, dashboard) => Ingest.IngestContentAsync(config, client, since: since, dashboard: dashboard);

    /// <summary>Clamp hours to the valid range [12, 168].</summary>
    internal static int ClampHours(int hours) => Math.Max(12, Math.Min(168, hours));

    /// <summary>Clamp <paramref name="value"/> to [lo, hi].</summary>
    private static int Clamp(int value, int lo, int hi) => Math.Max(lo, Math.Min(hi, value));

    /// <summary>
    /// Execute a single ingestion run.
    ///
    /// <paramref name="since"/>: if set, only fetch SF records modified after this time
    /// (incremental). Null means full crawl.
    /// </summary>
    private static async Task<bool> RunIngestAsync(ParsedArgs args, DateTime? since = null)
    {
        var syncType = since != null ? "incremental" : "full";

        var verbose = args.GetBool("verbose");
        var useDashboard = !verbose && Dashboard.HasRich;
        var prefix = syncType == "full" ? "ingestion" : "incremental";
        var (logFile, summaryFile) = CommandRegistry.SetupLogging(prefix, verbose: verbose, dashboardMode: useDashboard);
        var logger = Logging.GetLogger("ingestion_only");
        var progress = Logging.GetLogger("progress");
        var startTime = CommandRegistry.MonotonicSeconds();
        IngestionStats? stats = null;
        AppConfig? config = null;

        try
        {
            logger.Info($"📄 Logging to: {logFile}");
            logger.Info(new string('=', 70));
            logger.Info("INGESTION ONLY: Ingest Items with ACLs");
            logger.Info(new string('=', 70));

            config = LoadConfigHook();

            // Full or incremental based on 'since' parameter
            if (since == null)
            {
                SyncState.ClearFailedRecords(config.Connector.Id);
                SyncState.ClearCheckpoint(config.Connector.Id);
            }

            progress.Info($"Starting {syncType} ingestion for connector '{config.Connector.Id}'...");
            if (since != null)
                progress.Info($"  Incremental sync (since {CommandRegistry.PyIsoFormat(since.Value)})");
            else
                progress.Info("  Full sync (all records)");
            logger.Info($"  Connector ID: {config.Connector.Id}");
            logger.Info($"  Salesforce Instance: {config.Connector.Salesforce.InstanceUrl}");

            logger.Info("\n" + new string('=', 70));
            logger.Info("STEP 1: Initialize Graph API Client");
            logger.Info(new string('=', 70));
            var client = new GraphClient(
                apiVersion: config.Tuning.GraphApiVersion,
                maxRetries: config.Tuning.GraphMaxRetries,
                retryBackoffBase: config.Tuning.GraphRetryBackoffBase);
            logger.Info("✓ Graph client initialized");
            progress.Info("  Graph client initialized");

            logger.Info("\n" + new string('=', 70));
            logger.Info("STEP 2: Verify Connection Ready");
            logger.Info(new string('=', 70));
            if (!await IsConnectionReadyHook(config, client))
            {
                logger.Error("❌ Connection not ready! Run 'python run.py full-deployment' first.");
                return false;
            }
            logger.Info($"✓ Connection is ready: {config.Connector.Id}");
            progress.Info($"  Connection '{config.Connector.Id}' verified (existing)");

            logger.Info("\n" + new string('=', 70));
            logger.Info("STEP 3: Ingest Items with ACLs");
            logger.Info(new string('=', 70));
            logger.Info($"  Instance: {config.Connector.Salesforce.InstanceUrl}");
            logger.Info($"  API Version: {config.Connector.Salesforce.ApiVersion}");
            progress.Info("  Starting ingestion...");

            IngestionDashboard? dashboard = null;
            if (useDashboard)
            {
                var syncLabel = since != null
                    ? $"Incremental (since {CommandRegistry.PyIsoFormat(since.Value)})"
                    : "Full sync";
                var aclLabel = config.UseGroupAcl ? "GROUP" : (config.UseNewAclEngine ? "NEW" : "LEGACY");
                var relLog = logFile;
                try
                {
                    var rel = Path.GetRelativePath(config.RepoRoot, logFile);
                    if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
                        relLog = rel;
                }
                catch
                {
                }
                var dlRel = SyncState.FailedRecordsPath(config.Connector.Id);
                try
                {
                    var rel = Path.GetRelativePath(config.RepoRoot, dlRel);
                    if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
                        dlRel = rel;
                }
                catch
                {
                }
                dashboard = new IngestionDashboard(config.Connector.Id, syncLabel, aclLabel, relLog, dlRel);
                dashboard.Start();
            }

            try
            {
                // Identity Crawl: only on full sync, not incremental
                if (syncType == "full" && config.UseGroupAcl)
                {
                    progress.Info("  Running identity sync (group-based ACL)...");
                    var identityStats = await Identity.RunIdentitySyncAsync(config, client);
                    logger.Info(
                        $"Identity sync: created={identityStats.GroupsCreated} updated={identityStats.GroupsUpdated} " +
                        $"deleted={identityStats.GroupsDeleted} unchanged={identityStats.GroupsUnchanged}");
                }

                stats = await IngestContentHook(config, client, since, dashboard);
            }
            finally
            {
                if (dashboard != null)
                {
                    dashboard.Stop();
                    CommandRegistry.RestoreConsoleLogging();
                }
            }

            logger.Info($"Ingestion completed ({syncType})");

            // Record content crawl stats in SQLite
            try
            {
                Identity.RecordContentCrawl(config, stats, syncType: syncType);
            }
            catch (Exception recErr)
            {
                logger.Warning($"Could not record content crawl stats: {recErr.Message}");
            }

            var elapsed = CommandRegistry.MonotonicSeconds() - startTime;
            CommandRegistry.WriteSummary(summaryFile, logFile, stats, "existing (verified)", config.Connector.Id, elapsed, "INGESTION");
            return stats.FailedCount == 0;
        }
        catch (Exception e)
        {
            var elapsed = CommandRegistry.MonotonicSeconds() - startTime;
            stats ??= new IngestionStats();
            CommandRegistry.WriteSummary(summaryFile, logFile, stats, "existing (verified)",
                config?.Connector?.Id ?? "unknown",
                elapsed, "INGESTION (CRASHED)");
            Logging.GetLogger("ingestion_only").Error($"❌ Fatal error during ingestion: {e.Message}", e);
            return false;
        }
    }

    /// <summary>
    /// Ingest items only — connection &amp; schema must already exist.
    ///
    /// When ``--incremental`` is passed, the first run uses the last successful
    /// content crawl timestamp from SQLite so only changed records are fetched.
    /// Falls back to a full crawl when no prior run is found.
    ///
    /// When ``--continuous`` is passed, ingestion repeats on a fixed schedule.
    /// </summary>
    public static async Task<bool?> CmdIngestAsync(ParsedArgs args)
    {
        DateTime? since = null;
        if (args.GetBool("incremental"))
        {
            try
            {
                var config = LoadConfigHook();
                since = Identity.GetLastContentCrawlTime(config);
            }
            catch
            {
            }
            if (since != null)
                Logging.GetLogger("progress").Info($"--incremental: resuming from {CommandRegistry.PyIsoFormat(since.Value)}");
            else
                Logging.GetLogger("progress").Info("--incremental: no previous crawl found, running full crawl");
        }
        var success = await RunIngestAsync(args, since: since);

        var continuous = args.GetBool("continuous");
        if (!continuous)
            return success;

        var fullHours = Clamp(args.GetInt("full_crawl_hours", 24), 12, 168);
        var incrHours = Clamp(args.GetInt("incremental_hours", 4), 1, 168);
        var incrInterval = incrHours * 3600;
        var fullInterval = fullHours * 3600;

        var progressLogger = Logging.GetLogger("progress");
        progressLogger.Info(
            "\n🔁 Continuous mode enabled:\n" +
            $"   Full crawl every {fullHours} hour(s)\n" +
            $"   Incremental crawl every {incrHours} hour(s)\n" +
            "   Press Ctrl+C to stop.\n");

        var lastFullTime = CommandRegistry.MonotonicSeconds();

        while (true)
        {
            progressLogger.Info($"⏳ Next incremental crawl in {incrHours} hour(s)...");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(incrInterval), ServiceStop.Token);
            }
            catch (OperationCanceledException)
            {
                // Service stop while idle between crawls — exit cleanly.
            }
            if (ServiceStop.Requested)
            {
                progressLogger.Info("🛑 Service stop requested — leaving continuous mode.");
                return true;
            }

            CommandRegistry.ResetLogging();

            var elapsedSinceFull = CommandRegistry.MonotonicSeconds() - lastFullTime;
            if (elapsedSinceFull >= fullInterval)
            {
                progressLogger.Info("🔄 Starting scheduled FULL crawl...");
                await RunIngestAsync(args, since: null);
                lastFullTime = CommandRegistry.MonotonicSeconds();
            }
            else
            {
                progressLogger.Info("🔄 Starting scheduled INCREMENTAL crawl...");
                since = null;
                try
                {
                    var config = LoadConfigHook();
                    since = Identity.GetLastContentCrawlTime(config);
                }
                catch
                {
                }
                await RunIngestAsync(args, since: since);
            }
        }
    }
}
