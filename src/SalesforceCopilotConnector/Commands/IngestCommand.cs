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

    /// <summary>
    /// Actual sync type ("full"/"incremental") of the most recent
    /// <see cref="RunIngestAsync"/> call, AFTER HA crawl-kind adoption (see
    /// <see cref="Deploy.LastRunActualSyncType"/> for the rationale).
    /// </summary>
    internal static string? LastRunActualSyncType;

    /// <summary>Clamp hours to the valid range [12, 168].</summary>
    internal static int ClampHours(int hours) => Math.Max(12, Math.Min(168, hours));

    /// <summary>Clamp <paramref name="value"/> to [lo, hi].</summary>
    private static int Clamp(int value, int lo, int hi) => Math.Max(lo, Math.Min(hi, value));

    /// <summary>
    /// Execute a single ingestion run.
    ///
    /// <paramref name="since"/>: if set, only fetch SF records modified after this time
    /// (incremental). Null means full crawl.
    /// <paramref name="haCycleDueUtc"/>: HA mode only — the time this scheduled cycle
    /// became due; used by OpenOrJoinCrawl to dedupe cycles across nodes.
    /// </summary>
    private static async Task<bool> RunIngestAsync(
        ParsedArgs args, DateTime? since = null, DateTime? haCycleDueUtc = null,
        AppConfig? configOverride = null)
    {
        var syncType = since != null ? "incremental" : "full";
        LastRunActualSyncType = syncType;

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

            config = configOverride ?? LoadConfigHook();
            Metrics.IncCrawlsStarted();

            // Full or incremental based on 'since' parameter
            // (in HA mode only the node that CREATES the crawl clears the shared
            // state — see the OpenOrJoinCrawl block below)
            if (since == null && !HaCoordinator.IsHaMode)
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

            // ── HA mode: open or join the coordinated crawl for this cycle ────
            Guid? haCrawlId = null;
            if (HaCoordinator.IsHaMode)
            {
                var haCrawl = await HaCoordinator.OpenOrJoinCrawlAsync(
                    config.Connector.Id,
                    crawlKind: syncType,
                    sinceIso: since != null ? CommandRegistry.PyIsoFormat(since.Value) : null,
                    objectTypes: ApiClient.ObjectConfigs.Select(c => c.ObjectType)
                        .Where(t => config.ShardObjectTypes == null || config.ShardObjectTypes.Contains(t))
                        .ToList(),
                    cycleDueUtc: haCycleDueUtc);
                if (haCrawl == null)
                {
                    progress.Info("  [HA] Cycle already completed by another node — skipping this run.");
                    logger.Info("[HA] Skipping cycle — last sync in SQL is fresher than this node's due time.");
                    return true;
                }
                haCrawlId = haCrawl.CrawlId;
                if (haCrawl.Created)
                {
                    logger.Info($"[HA] Opened crawl {haCrawl.CrawlId} (kind={syncType}, node={HaCoordinator.NodeId})");
                    // The crawl creator resets the shared state for a full crawl.
                    if (since == null)
                    {
                        SyncState.ClearFailedRecords(config.Connector.Id);
                        SyncState.ClearCheckpoint(config.Connector.Id);
                    }
                }
                else
                {
                    logger.Info($"[HA] Joined crawl {haCrawl.CrawlId} (node={HaCoordinator.NodeId})");
                    // The joined crawl's kind wins (see Deploy.cs) — adopt it so the
                    // identity-sync decision, labels and recorded crawl type match.
                    if (!string.IsNullOrEmpty(haCrawl.CrawlKind) && haCrawl.CrawlKind != syncType)
                    {
                        logger.Warning(
                            $"[HA] Requested a {syncType} crawl but joined an open {haCrawl.CrawlKind} crawl — " +
                            "this run follows the joined crawl's kind.");
                        syncType = haCrawl.CrawlKind!;
                        LastRunActualSyncType = syncType;
                    }
                    // Adopt the crawl's incremental boundary so checkpoints line up across nodes.
                    if (haCrawl.HasSinceIso)
                    {
                        since = string.IsNullOrEmpty(haCrawl.SinceIso)
                            ? null
                            : SyncState.ParseIsoFormat(haCrawl.SinceIso!);
                    }
                }
                progress.Info(
                    $"  [HA] {(haCrawl.Created ? "Opened" : "Joined")} crawl {haCrawl.CrawlId} as node '{HaCoordinator.NodeId}'");
            }

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

            var syncStart = DateTime.UtcNow;
            // Ingested-item inventory (reconcile support): record every successful item
            // PUT / delete so `reconcile` can detect index-vs-source drift. Hooks are
            // cleared in the finally so the process-wide static seams never outlive this
            // crawl (mirrors the ObjectWorkSourceFactory set/clear pattern below).
            IItemInventory? inventory = null;
            try
            {
                var inv = ItemInventory.Open(config.Connector.Id);
                inventory = inv;
                Ingest.InventoryRecordHook = pairs => inv.RecordSeen(pairs, DateTime.UtcNow);
                Ingest.InventoryDeleteHook = ids => inv.Remove(ids);

                // Identity Crawl: always on full; on incremental only when
                // IDENTITY_SYNC_ON_INCREMENTAL=true. Default (unset) = full-only, as before.
                if (config.UseGroupAcl && (syncType == "full" || EnvFlags.IdentitySyncOnIncremental))
                {
                    var incremental = syncType != "full";
                    progress.Info(
                        incremental
                            ? "  Running incremental identity sync (group-based ACL)..."
                            : "  Running identity sync (group-based ACL)...");
                    var identityStats = await Identity.RunIdentitySyncAsync(config, client, incremental: incremental);
                    logger.Info(
                        $"Identity sync: created={identityStats.GroupsCreated} updated={identityStats.GroupsUpdated} " +
                        $"deleted={identityStats.GroupsDeleted} unchanged={identityStats.GroupsUnchanged}");
                }

                syncStart = DateTime.UtcNow;
                if (haCrawlId != null)
                {
                    // Workers pull object types from coordinator claims for this crawl.
                    var crawlId = haCrawlId.Value;
                    Ingest.ObjectWorkSourceFactory =
                        (_, _) => Task.FromResult(HaCoordinator.CreateWorkSource(crawlId));
                }
                stats = await IngestContentHook(config, client, since, dashboard);
            }
            finally
            {
                Ingest.InventoryRecordHook = null;
                Ingest.InventoryDeleteHook = null;
                inventory?.Dispose();
                if (haCrawlId != null)
                    Ingest.ObjectWorkSourceFactory = null;
                if (dashboard != null)
                {
                    dashboard.Stop();
                    CommandRegistry.RestoreConsoleLogging();
                }
            }

            logger.Info($"Ingestion completed ({syncType})");

            // Observability: fold outcome into /metrics and alert on dead-letter threshold
            // (no-ops when HEALTH_PORT / ALERT_* env vars are unset).
            CommandRegistry.RecordCrawlMetrics(stats);
            await Alerting.MaybeAlertDeadLetterAsync(
                config.Connector.Id, CommandRegistry.DeadLetterDepth(config.Connector.Id));

            // HA mode: only the node whose CloseCrawlIfComplete call performed the
            // close records the crawl + sync timestamp; every other node skips it.
            var haClosedCrawl = true;
            if (haCrawlId != null)
            {
                if (ServiceStop.Requested)
                {
                    // Graceful stop: claims stay held for reclaim, and SQL calls run
                    // under the now-cancelled ServiceStop token — closing here would
                    // throw and misreport the routine stop as a crash.
                    haClosedCrawl = false;
                    logger.Info(
                        $"[HA] Service stop requested — leaving crawl {haCrawlId} open; " +
                        "another node (or the next start) resumes and closes it.");
                }
                else
                {
                    haClosedCrawl = await HaCoordinator.CloseCrawlIfCompleteAsync(haCrawlId.Value);
                    if (haClosedCrawl)
                    {
                        logger.Info($"[HA] Crawl {haCrawlId} complete — this node closes it and records sync state.");
                        SyncState.ClearCheckpoint(config.Connector.Id);
                        SyncState.WriteLastSync(config.Connector.Id, syncStart);
                    }
                    else
                    {
                        logger.Info(
                            $"[HA] Crawl {haCrawlId} still in progress on other node(s) — " +
                            "sync state will be recorded by the closing node.");
                    }
                }
            }

            // Record content crawl stats in SQLite
            if (haClosedCrawl)
            {
                try
                {
                    Identity.RecordContentCrawl(config, stats, syncType: syncType);
                }
                catch (Exception recErr)
                {
                    logger.Warning($"Could not record content crawl stats: {recErr.Message}");
                }
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
            await Alerting.RaiseAsync("crawl_failed", $"Ingestion ({syncType}) failed: {e.Message}",
                new { connector = config?.Connector?.Id, error = e.GetType().Name });
            return false;
        }
    }

    /// <summary>
    /// Run one ingest cycle: sharded (one Graph connection per shard) when
    /// <c>GRAPH_CONNECTION_SHARDS</c> is set, else a single ingest. Disabled ⇒ byte-identical
    /// to the single-connection path. See docs/SHARDING.md.
    /// </summary>
    private static async Task<(bool Ok, bool RanFull)> RunIngestCycleAsync(
        ParsedArgs args, DateTime? since, DateTime? haCycleDueUtc = null)
    {
        if (!ShardingConfig.IsEnabled)
        {
            var ok = await RunIngestAsync(args, since: since, haCycleDueUtc: haCycleDueUtc);
            return (ok, LastRunActualSyncType == "full");
        }

        var progress = Logging.GetLogger("progress");
        AppConfig baseConfig;
        try
        {
            baseConfig = LoadConfigHook();
        }
        catch (Exception e)
        {
            Logging.GetLogger("ingestion_only").Error($"❌ Could not load config for sharding: {e.Message}", e);
            return (false, false);
        }

        if (!ShardingConfig.TryLoad(baseConfig, out var shards, out var error))
        {
            Logging.GetLogger("ingestion_only").Error($"❌ Invalid GRAPH_CONNECTION_SHARDS: {error}");
            return (false, false);
        }

        progress.Info($"🔀 Connection sharding enabled — {shards.Count} shard(s) across separate Graph connections.");
        var allOk = true;
        var allFull = true;
        foreach (var shard in shards)
        {
            if (ServiceStop.Requested)
                break;
            // Reset log handlers between shards — each shard's SetupLogging would
            // otherwise stack onto the previous shard's (duplicate lines, cross-shard
            // log bleed, corrupted dashboards). Mirrors the continuous loop's reset.
            CommandRegistry.ResetLogging();
            progress.Info($"── Shard '{shard.ConnectionId}': {string.Join(", ", shard.ObjectTypes)} ──");
            var shardConfig = ShardingConfig.ForShard(baseConfig, shard);
            var ok = await RunIngestAsync(
                args, since: since, haCycleDueUtc: haCycleDueUtc, configOverride: shardConfig);
            allOk = allOk && ok;
            allFull = allFull && LastRunActualSyncType == "full";
        }
        return (allOk, allFull);
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
        // Observability endpoint for the lifetime of the command (no-op unless HEALTH_PORT set).
        using var health = CommandRegistry.MaybeStartHealthEndpoint();

        // HA mode requires the SQL Server backend (shared state + coordination).
        if (HaCoordinator.IsHaMode && !SyncState.UseSqlServer)
        {
            Logging.GetLogger("progress").Error(
                "❌ HA_MODE=true requires USE_SQL_SERVER=true and SQL_CONNECTION_STRING " +
                "(point it at the AG listener). Aborting.");
            return false;
        }

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
        var (success, _) = await RunIngestCycleAsync(args, since);

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

            // HA: the moment this node's cycle became due — OpenOrJoinCrawl uses it
            // to skip cycles another node already completed.
            var cycleDue = DateTime.UtcNow;

            var elapsedSinceFull = CommandRegistry.MonotonicSeconds() - lastFullTime;
            if (elapsedSinceFull >= fullInterval)
            {
                progressLogger.Info("🔄 Starting scheduled FULL crawl...");
                var (_, ranFull) = await RunIngestCycleAsync(args, null, cycleDue);
                // HA: joining an open incremental crawl means the full crawl did NOT
                // run — keep the slot due so the next cycle attempts it again.
                if (ranFull)
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
                _ = await RunIngestCycleAsync(args, since, cycleDue);
            }
        }
    }
}
