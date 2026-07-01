// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// full-deployment command — complete end-to-end connector setup.
//
// Performs the following steps in order:
//
// 1. Load configuration from environment variables and config files.
// 2. Initialise the Microsoft Graph API client.
// 3. Create or verify the external connection.
// 4. Register the Graph connector schema.
// 5. Configure search display settings (result type / adaptive card).
// 6. Wait for the connection to reach the ``ready`` state.
// 7. Ingest all Salesforce items with ACL resolution.
//
// Usage::
//
//     run.py full-deployment           # quiet console
//     run.py full-deployment --verbose  # detailed console output
//
// Returns ``true`` on success, ``false`` on failure (exit code 1).

using SalesforceCopilotConnector.Config;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Commands;

public static class Deploy
{
    // ── Test hooks ───────────────────────────────────────────────────────────
    // Mirror Python monkeypatching of the module-level imports in commands/deploy.py
    // (e.g. patch("commands.deploy.ensure_connection")). Default to the real
    // implementations; behavior is identical when unhooked.
    internal static Func<AppConfig> LoadConfigHook = Settings.LoadConfig;
    internal static Action<string> ClearCheckpointHook = SyncState.ClearCheckpoint;
    internal static Func<AppConfig, GraphClient, double, Task<string?>> EnsureConnectionHook = Connection.EnsureConnectionAsync;
    internal static Func<AppConfig, GraphClient, Task> EnsureSchemaHook = Schema.EnsureSchemaAsync;
    internal static Func<AppConfig, GraphClient, Task> SetSearchSettingsHook = Connection.SetSearchSettingsAsync;
    internal static Func<AppConfig, GraphClient, Task<bool>> IsConnectionReadyHook = Connection.IsConnectionReadyAsync;
    internal static Func<AppConfig, GraphClient, Task<SyncSessionStats>> RunIdentitySyncHook = Identity.RunIdentitySyncAsync;
    internal static Func<AppConfig, GraphClient, DateTime?, IngestionDashboard?, Task<IngestionStats>> IngestContentHook =
        (config, client, since, dashboard) => Ingest.IngestContentAsync(config, client, since: since, dashboard: dashboard);
    internal static Action<AppConfig, IngestionStats, string> RecordContentCrawlHook = Identity.RecordContentCrawl;
    internal static Func<AppConfig, DateTime?> GetLastContentCrawlTimeHook = Identity.GetLastContentCrawlTime;

    /// <summary>Clamp hours to the valid range [12, 168].</summary>
    internal static int ClampHours(int hours) => Math.Max(12, Math.Min(168, hours));

    /// <summary>Clamp <paramref name="value"/> to [lo, hi].</summary>
    private static int Clamp(int value, int lo, int hi) => Math.Max(lo, Math.Min(hi, value));

    /// <summary>
    /// Execute a single deployment run.
    ///
    /// <paramref name="since"/>: if set, only fetch SF records modified after this time
    /// (incremental). Null means full crawl.
    /// </summary>
    internal static async Task<bool> RunFullDeploymentAsync(ParsedArgs args, DateTime? since = null)
    {
        var syncType = since != null ? "incremental" : "full";

        var verbose = args.GetBool("verbose");
        var useDashboard = !verbose && Dashboard.HasRich;
        var prefix = syncType == "full" ? "deployment" : "incremental";
        var (logFile, summaryFile) = CommandRegistry.SetupLogging(prefix, verbose: verbose, dashboardMode: useDashboard);
        var logger = Logging.GetLogger("deployment");
        var progress = Logging.GetLogger("progress");
        var startTime = CommandRegistry.MonotonicSeconds();
        string? connectionStatus = null;
        IngestionStats? stats = null;
        AppConfig? config = null;

        try
        {
            logger.Info($"📄 Logging to: {logFile}");
            logger.Info(new string('=', 70));
            logger.Info("FULL DEPLOYMENT: Connection → Schema → Ingestion with ACLs");
            logger.Info(new string('=', 70));

            config = LoadConfigHook();

            // Full or incremental based on 'since' parameter
            if (since == null)
                ClearCheckpointHook(config.Connector.Id);

            progress.Info($"Starting {syncType} deployment for connector '{config.Connector.Id}'...");
            logger.Info("Configuration loaded:");
            logger.Info($"  Connector ID: {config.Connector.Id}");
            logger.Info($"  Connector Name: {config.Connector.Name}");
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
            logger.Info("STEP 2: Create/Ensure Connection");
            logger.Info(new string('=', 70));
            var initialTimestamp = CommandRegistry.MonotonicSeconds();
            connectionStatus = await EnsureConnectionHook(config, client, initialTimestamp);
            if (connectionStatus == null)
            {
                logger.Error("❌ Failed to create/ensure connection");
                return false;
            }
            logger.Info($"✓ Connection ready: {config.Connector.Id}");

            logger.Info("\n" + new string('=', 70));
            logger.Info("STEP 3: Register Schema");
            logger.Info(new string('=', 70));
            await EnsureSchemaHook(config, client);
            logger.Info("✓ Schema registered");
            progress.Info("  Schema registered");

            logger.Info("\n" + new string('=', 70));
            logger.Info("STEP 4: Configure Search Settings");
            logger.Info(new string('=', 70));
            await SetSearchSettingsHook(config, client);
            logger.Info("✓ Search settings configured");
            progress.Info("  Search settings configured");

            logger.Info("\n" + new string('=', 70));
            logger.Info("STEP 5: Verify Connection Ready");
            logger.Info(new string('=', 70));
            if (!await IsConnectionReadyHook(config, client))
            {
                logger.Warning("⚠ Connection not ready yet, waiting...");
                await Task.Delay(TimeSpan.FromSeconds(5));
                if (!await IsConnectionReadyHook(config, client))
                {
                    logger.Error("❌ Connection still not ready");
                    return false;
                }
            }
            logger.Info("✓ Connection is ready for ingestion");
            progress.Info("  Connection ready");

            logger.Info("\n" + new string('=', 70));
            logger.Info("STEP 6: Ingest Items with ACLs");
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
                // Groups don't change frequently, no need to re-crawl every incremental cycle
                if (syncType == "full" && config.UseGroupAcl)
                {
                    progress.Info("  Running identity sync (group-based ACL)...");
                    var identityStats = await RunIdentitySyncHook(config, client);
                    logger.Info(
                        $"Identity sync: created={identityStats.GroupsCreated} updated={identityStats.GroupsUpdated} " +
                        $"deleted={identityStats.GroupsDeleted} unchanged={identityStats.GroupsUnchanged}");
                }

                var syncStart = DateTime.UtcNow;  // noqa: F841 — used by record_content_crawl via session
                _ = syncStart;
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
                RecordContentCrawlHook(config, stats, syncType);
            }
            catch (Exception recErr)
            {
                logger.Warning($"Could not record content crawl stats: {recErr.Message}");
            }

            var elapsed = CommandRegistry.MonotonicSeconds() - startTime;
            CommandRegistry.WriteSummary(summaryFile, logFile, stats, connectionStatus, config.Connector.Id, elapsed, "FULL DEPLOYMENT");
            return stats.FailedCount == 0;
        }
        catch (Exception e)
        {
            var elapsed = CommandRegistry.MonotonicSeconds() - startTime;
            stats ??= new IngestionStats();
            CommandRegistry.WriteSummary(summaryFile, logFile, stats, connectionStatus,
                config?.Connector?.Id ?? "unknown",
                elapsed, "FULL DEPLOYMENT (CRASHED)");
            Logging.GetLogger("deployment").Error($"❌ Fatal error during deployment: {e.Message}", e);
            return false;
        }
    }

    /// <summary>
    /// Deploy connection → schema → ingest items with ACLs.
    ///
    /// When ``--incremental`` is passed, the first run uses the last successful
    /// content crawl timestamp from SQLite (if available) so only changed records
    /// are fetched.  Falls back to a full crawl when no prior run is found.
    ///
    /// When ``--continuous`` is passed, subsequent iterations re-ingest on a
    /// fixed schedule.
    /// </summary>
    public static async Task<bool?> CmdFullDeploymentAsync(ParsedArgs args)
    {
        DateTime? since = null;
        if (args.GetBool("incremental"))
        {
            try
            {
                var config = LoadConfigHook();
                since = GetLastContentCrawlTimeHook(config);
            }
            catch
            {
            }
            if (since != null)
                Logging.GetLogger("progress").Info($"--incremental: resuming from {CommandRegistry.PyIsoFormat(since.Value)}");
            else
                Logging.GetLogger("progress").Info("--incremental: no previous crawl found, running full crawl");
        }
        var success = await RunFullDeploymentAsync(args, since: since);

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
                await RunFullDeploymentAsync(args, since: null);
                lastFullTime = CommandRegistry.MonotonicSeconds();
            }
            else
            {
                progressLogger.Info("🔄 Starting scheduled INCREMENTAL crawl...");
                since = null;
                try
                {
                    var config = LoadConfigHook();
                    since = GetLastContentCrawlTimeHook(config);
                }
                catch
                {
                }
                await RunFullDeploymentAsync(args, since: since);
            }
        }
    }
}
