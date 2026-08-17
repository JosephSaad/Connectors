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

    /// <summary>
    /// Actual sync type ("full"/"incremental") of the most recent
    /// <see cref="RunFullDeploymentAsync"/> call, AFTER HA crawl-kind adoption —
    /// a node that requested a full crawl but joined an open incremental one ran
    /// incrementally. The continuous loop uses this to decide whether the
    /// scheduled full-crawl slot was really consumed.
    /// </summary>
    internal static string? LastRunActualSyncType;

    /// <summary>Clamp hours to the valid range [12, 168].</summary>
    internal static int ClampHours(int hours) => Math.Max(12, Math.Min(168, hours));

    /// <summary>Clamp <paramref name="value"/> to [lo, hi].</summary>
    private static int Clamp(int value, int lo, int hi) => Math.Max(lo, Math.Min(hi, value));

    /// <summary>
    /// Execute a single deployment run.
    ///
    /// <paramref name="since"/>: if set, only fetch SF records modified after this time
    /// (incremental). Null means full crawl.
    /// <paramref name="haCycleDueUtc"/>: HA mode only — the time this scheduled cycle
    /// became due; used by OpenOrJoinCrawl to dedupe cycles across nodes.
    /// </summary>
    internal static async Task<bool> RunFullDeploymentAsync(
        ParsedArgs args, DateTime? since = null, DateTime? haCycleDueUtc = null,
        AppConfig? configOverride = null)
    {
        var syncType = since != null ? "incremental" : "full";
        LastRunActualSyncType = syncType;

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

            config = configOverride ?? LoadConfigHook();
            Metrics.IncCrawlsStarted();
            Metrics.ResetObjectProgress();  // per-object gauges restart each crawl

            // Full or incremental based on 'since' parameter
            // (in HA mode only the node that CREATES the crawl clears the shared
            // checkpoint — see the OpenOrJoinCrawl block below)
            if (since == null && !HaCoordinator.IsHaMode)
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
                    // The crawl creator resets the shared checkpoint for a full crawl.
                    if (since == null)
                        ClearCheckpointHook(config.Connector.Id);
                }
                else
                {
                    logger.Info($"[HA] Joined crawl {haCrawl.CrawlId} (node={HaCoordinator.NodeId})");
                    // Adopt the crawl's incremental boundary so checkpoints line up across nodes.
                    if (haCrawl.HasSinceIso)
                    {
                        since = string.IsNullOrEmpty(haCrawl.SinceIso)
                            ? null
                            : SyncState.ParseIsoFormat(haCrawl.SinceIso!);
                    }
                    // The joined crawl may be a different kind than this node requested
                    // (a due FULL landing on an open INCREMENTAL, or vice versa). The
                    // crawl that exists wins: adopt its kind so identity sync, labels
                    // and the recorded crawl type match what actually runs — and so
                    // the continuous loop doesn't consume its full-crawl slot on a
                    // run that was really incremental.
                    if (!string.IsNullOrEmpty(haCrawl.CrawlKind) && haCrawl.CrawlKind != syncType)
                    {
                        logger.Warning(
                            $"[HA] Requested a {syncType} crawl but joined an open {haCrawl.CrawlKind} crawl — " +
                            "this run follows the joined crawl's kind.");
                        syncType = haCrawl.CrawlKind!;
                        LastRunActualSyncType = syncType;
                    }
                }
                progress.Info(
                    $"  [HA] {(haCrawl.Created ? "Opened" : "Joined")} crawl {haCrawl.CrawlId} as node '{HaCoordinator.NodeId}'");
            }

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
                    // Cosmetic only: dashboard shows the absolute log path when a
                    // relative path cannot be computed (different volume, bad root).
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
                    // Cosmetic only — same as the log-path fallback above.
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

                // Identity Crawl: always on full sync; on incremental only when
                // IDENTITY_SYNC_ON_INCREMENTAL=true (large orgs whose groups drift
                // between full crawls). Default (unset) = today's full-only behavior.
                if (config.UseGroupAcl && (syncType == "full" || EnvFlags.IdentitySyncOnIncremental))
                {
                    var incremental = syncType != "full";
                    progress.Info(
                        incremental
                            ? "  Running incremental identity sync (group-based ACL)..."
                            : "  Running identity sync (group-based ACL)...");
                    var identityStats = incremental
                        ? await Identity.RunIdentitySyncAsync(config, client, incremental: true)
                        : await RunIdentitySyncHook(config, client);
                    logger.Info(
                        $"Identity sync: created={identityStats.GroupsCreated} updated={identityStats.GroupsUpdated} " +
                        $"deleted={identityStats.GroupsDeleted} unchanged={identityStats.GroupsUnchanged}");
                }

                syncStart = DateTime.UtcNow;  // noqa: F841 — used by record_content_crawl via session
                _ = syncStart;
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

            // Observability: fold this crawl's outcome into the /metrics registry and
            // alert if the dead-letter queue crossed its threshold (both no-ops when
            // the corresponding env vars are unset).
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
                    // Graceful stop: our claims were intentionally left held for
                    // another node to reclaim, and every SQL call runs under the
                    // now-cancelled ServiceStop token — attempting the close would
                    // throw and misreport this routine stop as a crash.
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
                    RecordContentCrawlHook(config, stats, syncType);
                }
                catch (Exception recErr)
                {
                    logger.Warning($"Could not record content crawl stats: {recErr.Message}");
                }

                // Automatic deletion sweep — FULL crawls only (an incremental crawl never sees the
                // complete source, so absence proves nothing) and only once the sync state is
                // recorded. haClosedCrawl gives exactly-once semantics for free (single-node always;
                // in HA only the crawl-closing node). The sweep opens its OWN inventory handles via
                // the Reconciler default factory and must NEVER fail the crawl — every error is
                // caught and logged. See docs/DELETION_SYNC.md.
                if (syncType == "full" && SalesforceFlags.DeletionSync && !ServiceStop.Requested)
                {
                    try
                    {
                        var sweeper = new Reconciler(config, client);
                        var sweep = await sweeper.SweepDeletionsAsync(
                            SalesforceFlags.DeletionSyncMaxPercent, ServiceStop.Token);
                        stats.SweepDeletedCount = sweep.Deleted;
                        stats.SweepSkipped.AddRange(sweep.Skipped);
                        logger.Info(
                            $"[DeletionSweep] withdrew {sweep.Deleted} deleted item(s); "
                            + $"skipped {sweep.Skipped.Count} object(s) on the safety guard");
                    }
                    catch (Exception sweepErr)
                    {
                        logger.Warning(
                            "[DeletionSweep] sweep failed (non-fatal; next full crawl or "
                            + $"reconcile --fix retries): {sweepErr.Message}");
                    }
                }
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
            await Alerting.RaiseAsync("crawl_failed", $"Full deployment ({syncType}) failed: {e.Message}",
                new { connector = config?.Connector?.Id, error = e.GetType().Name });
            return false;
        }
    }

    /// <summary>
    /// Run one deployment cycle. With <c>GRAPH_CONNECTION_SHARDS</c> set, iterate each shard
    /// as its own Graph connection (covering its slice of the schema) and AND the per-shard
    /// results; otherwise a single full deployment. Sharding is the throughput lever
    /// (docs/SHARDING.md): N shards ≈ N × the per-connection Graph ceiling. Disabled ⇒
    /// byte-identical to the single-connection path.
    /// </summary>
    private static async Task<(bool Ok, bool RanFull)> RunDeploymentCycleAsync(
        ParsedArgs args, DateTime? since, DateTime? haCycleDueUtc = null)
    {
        if (!ShardingConfig.IsEnabled)
        {
            var ok = await RunFullDeploymentAsync(args, since: since, haCycleDueUtc: haCycleDueUtc);
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
            Logging.GetLogger("deployment").Error($"❌ Could not load config for sharding: {e.Message}", e);
            return (false, false);
        }

        if (!ShardingConfig.TryLoad(baseConfig, out var shards, out var error))
        {
            Logging.GetLogger("deployment").Error($"❌ Invalid GRAPH_CONNECTION_SHARDS: {error}");
            return (false, false);
        }

        progress.Info($"🔀 Connection sharding enabled — {shards.Count} shard(s) across separate Graph connections.");
        var allOk = true;
        var allFull = true;
        foreach (var shard in shards)
        {
            if (ServiceStop.Requested)
                break;
            // Each shard run installs its own log handlers via SetupLogging; without
            // a reset between shards the handlers stack (k× duplicate lines by shard
            // k, cross-shard log bleed, corrupted dashboards). Mirror the continuous
            // loop's per-cycle reset at per-shard granularity.
            CommandRegistry.ResetLogging();
            progress.Info($"── Shard '{shard.ConnectionId}': {string.Join(", ", shard.ObjectTypes)} ──");
            var shardConfig = ShardingConfig.ForShard(baseConfig, shard);
            var ok = await RunFullDeploymentAsync(
                args, since: since, haCycleDueUtc: haCycleDueUtc, configOverride: shardConfig);
            allOk = allOk && ok;
            allFull = allFull && LastRunActualSyncType == "full";
        }
        return (allOk, allFull);
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
        // Observability: serve /health, /ready, /metrics for the lifetime of the command
        // when HEALTH_PORT is set (no-op otherwise). Covers single and continuous runs.
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
                since = GetLastContentCrawlTimeHook(config);
            }
            catch (Exception exc)
            {
                // Fall back to a FULL crawl (since == null), exactly as before —
                // but say WHY, or a state-store outage silently masquerades as
                // "no previous crawl found".
                Logging.GetLogger("progress").Warning(
                    $"--incremental: could not read the last crawl time ({exc.GetType().Name}: {exc.Message}) "
                    + "— falling back to a full crawl");
            }
            if (since != null)
                Logging.GetLogger("progress").Info($"--incremental: resuming from {CommandRegistry.PyIsoFormat(since.Value)}");
            else
                Logging.GetLogger("progress").Info("--incremental: no previous crawl found, running full crawl");
        }
        var (success, _) = await RunDeploymentCycleAsync(args, since);

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
                var (_, ranFull) = await RunDeploymentCycleAsync(args, null, cycleDue);
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
                    since = GetLastContentCrawlTimeHook(config);
                }
                catch (Exception exc)
                {
                    // Same fallback as the one-shot path: run this cycle as a FULL
                    // crawl but make the degradation visible to operators.
                    Logging.GetLogger("progress").Warning(
                        $"Scheduled incremental crawl: could not read the last crawl time "
                        + $"({exc.GetType().Name}: {exc.Message}) — running a full crawl this cycle");
                }
                _ = await RunDeploymentCycleAsync(args, since, cycleDue);
            }
        }
    }
}
