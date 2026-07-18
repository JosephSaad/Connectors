// Graph/Ingest.cs
// ---------------
// The ingestion pipeline: Clarizen records → ACL resolution → externalItem
// conversion → Graph $batch PUTs, with checkpointing, dead-lettering,
// adaptive concurrency and graceful stop.
//
// Flow per object type (config/schema.json order):
//   1. Fetch records — full crawls prefer the TDW bulk export when
//      TDW_EXPORT_PATH has a file for the object (no API budget spent);
//      otherwise the live REST API with an optional modifiedDate delta cursor.
//   2. Chunk into INGEST_CHUNK_SIZE records. Chunks whose index is <= the
//      checkpointed value are skipped (crash/stop resume).
//   3. For each chunk: resolve ACLs, convert, PUT via Graph $batch
//      (INGEST_GRAPH_BATCH_SIZE ≤ 20 requests per POST). Sub-batches are
//      dispatched in parallel windows sized by AdaptiveConcurrency
//      (GRAPH_CONCURRENT_BATCHES / GRAPH_BATCH_WORKERS, default 8): a 429
//      dials concurrency down toward 1, three consecutive clean windows dial
//      it back up. Per-item failures are appended to the dead-letter queue.
//   4. Checkpoint the chunk. ServiceStop is observed at chunk boundaries: the
//      in-flight chunk finishes, the pending batch flushes, the checkpoint is
//      saved, and the run returns cleanly.
//
// Sharding (GRAPH_CONNECTION_SHARDS, docs/SHARDING.md): when enabled the run
// loops the shards — each shard is an independent Graph connection with its
// own checkpoints, dead-letter queue, sync timestamp and (in HA) its own
// crawl/claim rows. A misconfigured shard map aborts the run.
//
// Worker crashes: an unexpected per-object exception is dead-lettered
// (WORKER_CRASH), the HA claim is marked 'failed' (terminal), and the crawl
// still completes/closes — matching the SF connector's pinned
// close-with-failed-claims semantics.

using System.Text.Json.Nodes;
using ClarizenConnector.AclEngine;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Infrastructure;
using ClarizenConnector.Item;

namespace ClarizenConnector.Graph;

public sealed class IngestSummary
{
    public int Ingested { get; set; }
    public int Failed { get; set; }
    public int Deleted { get; set; }
    public int SkippedChunks { get; set; }
    public int NoAclSkipped { get; set; }
    public Dictionary<string, int> PerObject { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> FailedObjects { get; } = new();
    /// <summary>Object types whose deletion sweep was skipped by the safety guard.</summary>
    public List<string> SweepSkipped { get; } = new();
    public bool Stopped { get; set; }
    public bool QuotaExhausted { get; set; }
    /// <summary>True when the run paused because a critical dependency's breaker
    /// opened (degraded mode). The checkpoint is retained and the sync cursor is
    /// not advanced, so the next cycle resumes cleanly.</summary>
    public bool Degraded { get; set; }
}

/// <summary>Tracks concurrency level: dials down on 429, dials up on sustained success.</summary>
internal sealed class AdaptiveConcurrency
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector.ingest");

    private readonly int _max;
    private int _current;
    private int _successStreak;
    private readonly object _lock = new();

    public AdaptiveConcurrency(int maxWorkers)
    {
        _max = Math.Max(1, maxWorkers);
        _current = _max;
        _successStreak = 0;
    }

    public int Current => _current;

    public int Max => _max;

    public void OnSuccess()
    {
        lock (_lock)
        {
            _successStreak += 1;
            // Ramp up after 3 consecutive successes to recover quickly.
            if (_successStreak >= 3 && _current < _max)
            {
                _current += 1;
                _successStreak = 0;
                Logger.Info($"Graph concurrency ramped up to {_current}");
            }
        }
    }

    public void OnThrottle()
    {
        lock (_lock)
        {
            var prev = _current;
            _current = Math.Max(1, _current - 1);
            _successStreak = 0;
            if (_current != prev)
                Logger.Warning($"Graph 429 throttling — concurrency reduced to {_current}");
        }
    }
}

public sealed class IngestPipeline
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector.ingest");

    private readonly AppConfig _config;
    private readonly SchemaConfig _schema;
    private readonly ClarizenClient _clarizen;
    private readonly GraphClient _graph;
    private readonly AclResolver _aclResolver;
    private readonly ItemConverter _converter;
    private readonly HaCoordinator? _ha;
    private readonly AdaptiveConcurrency _concurrency;
    private readonly Func<string, IItemInventory> _inventoryFactory;
    private readonly Item.AttachmentEnricher? _attachmentEnricher;
    private readonly Item.SensitivityClassifier? _classifier;
    private Item.ClassificationManifest? _manifest;
    private readonly object _statsLock = new();
    private readonly object _inventoryLock = new();

    /// <summary>Safety guard floor: the percent-based mass-deletion guard only
    /// engages once the inventory holds at least this many items for the
    /// connection (percentages are meaningless on tiny inventories — the
    /// absolute DELETION_SYNC_MAX_ITEMS cap covers those).</summary>
    internal const int MinInventoryForSafetyGuard = 20;

    /// <summary>Default absolute cap for one deletion sweep (DELETION_SYNC_MAX_ITEMS):
    /// never auto-delete more than this many items in one sweep, regardless of
    /// inventory size or the percent guard. 0 disables the cap explicitly.</summary>
    internal const int DefaultDeletionSyncMaxItems = 1000;

    /// <summary>Progress callback (objectType, done, total) for the dashboard.</summary>
    public Action<string, int, int>? OnProgress { get; set; }

    public IngestPipeline(
        AppConfig config,
        SchemaConfig schema,
        ClarizenClient clarizen,
        GraphClient graph,
        AclResolver aclResolver,
        ItemConverter converter,
        HaCoordinator? ha = null,
        Func<string, IItemInventory>? inventoryFactory = null,
        Item.AttachmentEnricher? attachmentEnricher = null,
        Item.SensitivityClassifier? sensitivityClassifier = null)
    {
        _config = config;
        _schema = schema;
        _clarizen = clarizen;
        _graph = graph;
        _aclResolver = aclResolver;
        _converter = converter;
        _ha = ha;
        _concurrency = new AdaptiveConcurrency(config.GraphBatchWorkers);
        _inventoryFactory = inventoryFactory ?? ItemInventory.Open;
        // Enrichment is a strict no-op unless ATTACHMENT_INGESTION=true, so
        // constructing the enricher always is harmless (and keeps the default
        // path unchanged when the flag is off).
        _attachmentEnricher = attachmentEnricher
            ?? new Item.AttachmentEnricher(config, clarizen);
        // Classification is off by default: only build the classifier when
        // CLASSIFICATION=true, so no properties are added and behaviour is
        // unchanged otherwise. A provided instance wins (tests inject patterns).
        _classifier = sensitivityClassifier
            ?? (config.Classification
                ? new Item.SensitivityClassifier(Content.ContentClassifier.Load())
                : null);
    }

    /// <summary>DELETION_SYNC — existence-sweep deletion sync on full crawls. Default ON.</summary>
    internal static bool DeletionSyncEnabled
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("DELETION_SYNC");
            return string.IsNullOrWhiteSpace(raw) || EnvFlags.IsTrue("DELETION_SYNC");
        }
    }

    internal AdaptiveConcurrency Concurrency => _concurrency;

    // ── Crawl driver ─────────────────────────────────────────────────────────

    public async Task<IngestSummary> RunAsync(
        bool fullCrawl, IReadOnlyList<string>? onlyObjects = null, CancellationToken ct = default)
    {
        // One correlation id + root span per crawl cycle, so the whole run is
        // followable end-to-end across logs, dead-letter records and traces.
        // Begin() adopts the span's TraceId (when a listener is active) so the
        // correlation id and the trace id are one and the same.
        using var cycleSpan = Tracing.StartCrawlCycle(
            fullCrawl ? "full" : "incremental", _config.ConnectorId);
        using var correlation = CorrelationContext.Begin();
        cycleSpan?.SetTag("clarizen.correlation_id", CorrelationContext.Current);
        Logger.Info($"Crawl cycle starting (correlation_id={CorrelationContext.Current}).");

        var summary = new IngestSummary();
        Metrics.IncCrawlsStarted();

        // Optional Purview-aligned classification manifest for this crawl.
        _manifest = _classifier is not null && _config.ClassificationManifest
            ? new Item.ClassificationManifest(_config.ConnectorId, Config.SyncState.LogsDir)
            : null;

        if (ShardingConfig.TryLoad(_schema, out var shards, out var shardError))
        {
            Logger.Info(
                $"Connection sharding active: {shards.Count} shard(s) "
                + $"({string.Join(", ", shards.Select(s => s.ConnectionId))})");
            foreach (var shard in shards)
            {
                var shardObjects = _schema.ObjectList
                    .Where(o => shard.ObjectTypes.Contains(o.ObjectName, StringComparer.OrdinalIgnoreCase))
                    .Where(o => onlyObjects is null
                                || onlyObjects.Contains(o.ObjectName, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (shardObjects.Count == 0)
                    continue;
                await RunConnectionAsync(
                        _config.CloneForConnection(shard.ConnectionId),
                        shardObjects, fullCrawl, restricted: onlyObjects is not null, summary, ct)
                    .ConfigureAwait(false);
                if (summary.Stopped || summary.QuotaExhausted || summary.Degraded)
                    break;
            }
        }
        else if (shardError is not null)
        {
            // Refuse to run a partial/overlapping crawl on a bad shard map.
            throw new ArgumentException(shardError);
        }
        else
        {
            var objects = _schema.ObjectList
                .Where(o => onlyObjects is null
                            || onlyObjects.Contains(o.ObjectName, StringComparer.OrdinalIgnoreCase))
                .ToList();
            await RunConnectionAsync(
                    _config, objects, fullCrawl, restricted: onlyObjects is not null, summary, ct)
                .ConfigureAwait(false);
        }

        var finished = !summary.Stopped && !summary.QuotaExhausted && !summary.Degraded;
        if (finished)
        {
            Metrics.IncCrawlsCompleted();
            Metrics.MarkCrawlCompletedNow();
        }

        var deadLetterDepth = ShardingConfig
            .EffectiveConnectionIds(_config, _schema)
            .Sum(id => SyncState.ReadFailedRecords(id).Count);
        Metrics.SetDeadLetterDepth(deadLetterDepth);
        await Alerting.MaybeAlertDeadLetterAsync(_config.ConnectorId, deadLetterDepth)
            .ConfigureAwait(false);

        // Flush the classification manifest (per-item lines + summary).
        _manifest?.Flush();
        _manifest = null;

        return summary;
    }

    /// <summary>Classify one item and record it to the crawl manifest (no-op when
    /// CLASSIFICATION is off). Called after attachment enrichment so extracted
    /// text is included in the scan.</summary>
    private void ApplyClassification(ExternalItem item, ObjectConfig objectConfig)
    {
        if (_classifier is null)
            return;
        var outcome = _classifier.Classify(item, objectConfig);
        _manifest?.Record(item.Id, objectConfig.ObjectName, outcome.Label, outcome.Categories);
    }

    /// <summary>One crawl pass against a single Graph connection (the whole run, or one shard).</summary>
    private async Task RunConnectionAsync(
        AppConfig cfg,
        List<ObjectConfig> objects,
        bool fullCrawl,
        bool restricted,
        IngestSummary summary,
        CancellationToken ct)
    {
        var since = fullCrawl ? null : SyncState.ReadLastSync(cfg.ConnectorId);
        var sinceIso = since is { } s ? SyncState.IsoFormat(s) : null;
        var crawlStartUtc = DateTime.UtcNow;

        string? crawlKey = null;
        if (_ha is not null)
        {
            crawlKey = _ha.OpenOrJoinCrawl(
                cfg.ConnectorId, fullCrawl ? "full" : "incremental", crawlStartUtc);
        }

        var checkpoint = SyncState.ReadCheckpoint(cfg.ConnectorId);
        var checkpointSince = checkpoint?["since"]?.GetValue<string>();
        var completedMap = checkpointSince == sinceIso
            ? checkpoint?["completed"]?.AsObject()
            : null;

        using var inventory = _inventoryFactory(cfg.ConnectorId);

        foreach (var objectConfig in objects)
        {
            if (ServiceStop.Requested || ct.IsCancellationRequested)
            {
                summary.Stopped = true;
                break;
            }

            // Degraded mode: a critical dependency's breaker is open. Pause at
            // this SAFE OBJECT BOUNDARY — no new object is started, the sync
            // cursor is not advanced, and completed checkpoints stand, so the
            // next cycle resumes cleanly. Fail-fast instead of hammering.
            if (Breakers.IsAnyCriticalOpen)
            {
                summary.Degraded = true;
                Logger.Warning(
                    "Degraded mode: pausing new object crawls — circuit open for "
                    + $"{string.Join(", ", Breakers.OpenCritical())}. Checkpoint retained; "
                    + "the next cycle resumes when the dependency recovers.");
                break;
            }

            if (_ha is not null && crawlKey is not null
                && !_ha.TryClaimObject(crawlKey, objectConfig.ObjectName))
            {
                Logger.Info($"HA: '{objectConfig.ObjectName}' claimed by another node; skipping.");
                continue;
            }

            using var heartbeat = _ha is not null && crawlKey is not null
                ? StartHeartbeat(crawlKey, objectConfig.ObjectName)
                : null;
            using var objectSpan = Tracing.StartObjectCrawl(objectConfig.ObjectName, cfg.ConnectorId);

            try
            {
                var completedChunks = completedMap?[objectConfig.ObjectName]?.GetValue<int>() ?? 0;
                await IngestObjectAsync(
                        cfg, objectConfig, fullCrawl, since, sinceIso, completedChunks,
                        inventory, summary, ct)
                    .ConfigureAwait(false);
                if (_ha is not null && crawlKey is not null
                    && !summary.Stopped && !summary.QuotaExhausted && !summary.Degraded)
                {
                    _ha.CompleteObject(crawlKey, objectConfig.ObjectName);
                }
            }
            catch (ClarizenQuotaExceededException exc)
            {
                Logger.Warning($"[{objectConfig.ObjectName}] {exc.Message}");
                summary.QuotaExhausted = true;
                break;
            }
            catch (CircuitOpenException exc)
            {
                // Fetch/ingest was rejected by a breaker mid-object — degraded,
                // not a crash. Nothing dead-lettered; checkpoint retained.
                summary.Degraded = true;
                Logger.Warning(
                    $"[{objectConfig.ObjectName}] Degraded mode: {exc.Message} "
                    + "Checkpoint retained; the next cycle resumes.");
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // A cancellation that lands MID-CHUNK (between the chunk-boundary
                // checks) is a graceful stop, not a worker crash: nothing is
                // dead-lettered, the last completed chunk's checkpoint stands,
                // and the sync cursor is not advanced. In HA the claim simply
                // goes stale and is reclaimed after the lease timeout.
                summary.Stopped = true;
                Logger.Warning(
                    $"[{objectConfig.ObjectName}] cancelled mid-chunk — graceful stop; "
                    + "checkpoint retained, nothing dead-lettered.");
                break;
            }
            catch (Exception exc)
            {
                // Worker crash: dead-letter it, mark the claim failed (terminal)
                // and keep going — failed claims never block the crawl close.
                Logger.Error(
                    $"[{objectConfig.ObjectName}] object worker crashed — continuing with the next object type",
                    exc);
                SyncState.AppendFailedRecords(
                    cfg.ConnectorId,
                    new List<(string, string)>
                    {
                        ("WORKER_CRASH",
                         $"[{objectConfig.ObjectName}] {exc.GetType().Name}: {exc.Message}"),
                    },
                    objectConfig.ObjectName);
                summary.FailedObjects.Add(objectConfig.ObjectName);
                if (_ha is not null && crawlKey is not null)
                    _ha.FailObject(crawlKey, objectConfig.ObjectName);
            }

            if (summary.Stopped || summary.Degraded)
                break;
        }

        // Degraded pauses are treated like a graceful stop: DO NOT advance the
        // sync cursor or clear the checkpoint, so the run resumes exactly here.
        var finished = !summary.Stopped && !summary.QuotaExhausted && !summary.Degraded;
        if (finished)
        {
            // In HA exactly one node closes the crawl and records the sync
            // timestamp — even when some claims failed (dead-letter retry is
            // the recovery path). Single-node behaves as its own closer.
            var closeAndStamp = _ha is null
                || (crawlKey is not null
                    && _ha.TryCloseCrawl(crawlKey, objects.Select(o => o.ObjectName).ToList()));
            if (closeAndStamp && !restricted)
            {
                SyncState.WriteLastSync(cfg.ConnectorId, crawlStartUtc);
                SyncState.ClearCheckpoint(cfg.ConnectorId);
            }
        }
    }

    private IDisposable StartHeartbeat(string crawlKey, string objectType)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _ha!.HeartbeatSeconds));
        return new Timer(
            _ =>
            {
                try
                {
                    _ha.Heartbeat(crawlKey, objectType);
                }
                catch (Exception exc)
                {
                    // Best-effort: a missed beat only matters if it persists past
                    // the lease timeout, so warn (with the exception type — SQL
                    // faults here are the classic cause) and keep crawling.
                    Logger.Warning(
                        $"HA heartbeat failed for '{objectType}' (crawl '{crawlKey}'): "
                        + $"{exc.GetType().Name}: {exc.Message}");
                }
            },
            null, interval, interval);
    }

    // ── Per-object ingestion ─────────────────────────────────────────────────

    private async Task IngestObjectAsync(
        AppConfig cfg,
        ObjectConfig objectConfig,
        bool fullCrawl,
        DateTime? since,
        string? sinceIso,
        int completedChunks,
        IItemInventory inventory,
        IngestSummary summary,
        CancellationToken ct)
    {
        var records = await FetchRecordsAsync(objectConfig, fullCrawl, since, ct).ConfigureAwait(false);
        Logger.Info($"{objectConfig.ObjectName}: {records.Count} record(s) to process.");

        var chunks = Chunk(records, cfg.IngestChunkSize);
        var done = 0;
        for (var chunkIndex = 1; chunkIndex <= chunks.Count; chunkIndex++)
        {
            if (ServiceStop.Requested || ct.IsCancellationRequested)
            {
                summary.Stopped = true;
                Logger.Warning(
                    $"Graceful stop: checkpoint saved at {objectConfig.ObjectName} chunk {chunkIndex - 1}.");
                return;
            }

            var chunk = chunks[chunkIndex - 1];
            if (chunkIndex <= completedChunks)
            {
                summary.SkippedChunks++;
                Metrics.IncItemsSkipped(chunk.Count);
                done += chunk.Count;
                OnProgress?.Invoke(objectConfig.ObjectName, done, records.Count);
                continue;
            }

            await IngestChunkAsync(cfg, objectConfig, chunk, chunkIndex, inventory, summary, ct)
                .ConfigureAwait(false);
            if (summary.Degraded)
            {
                // The Graph breaker opened mid-chunk (some sub-batches failed
                // fast). Do NOT checkpoint this chunk — it is fully retried on
                // resume (PUT is idempotent, so re-sending the succeeded items
                // is safe). Stop here at the chunk boundary.
                Logger.Warning(
                    $"Degraded mode: circuit opened during {objectConfig.ObjectName} chunk {chunkIndex}; "
                    + $"checkpoint held at chunk {chunkIndex - 1}, chunk retried on resume.");
                return;
            }
            SyncState.WriteCheckpoint(cfg.ConnectorId, sinceIso, objectConfig.ObjectName, chunkIndex);
            done += chunk.Count;
            OnProgress?.Invoke(objectConfig.ObjectName, done, records.Count);
        }

        // Deletion/tombstone sync: a FULL crawl saw the complete source id set
        // for this object type, so anything still in the inventory but absent
        // from the source was deleted in Clarizen — withdraw it from Graph.
        if (fullCrawl && DeletionSyncEnabled)
        {
            await SweepDeletionsAsync(cfg, objectConfig, records, inventory, summary, ct)
                .ConfigureAwait(false);
        }
    }

    internal Task<List<ClarizenRecord>> FetchRecordsAsync(
        ObjectConfig objectConfig, bool fullCrawl, DateTime? since, CancellationToken ct) =>
        FetchSourceRecordsAsync(_config, _clarizen, objectConfig, fullCrawl, since, ct);

    /// <summary>Shared source fetch (pipeline + reconcile): TDW bulk export for
    /// full reads when available, live REST API (with delta cursor) otherwise.</summary>
    internal static async Task<List<ClarizenRecord>> FetchSourceRecordsAsync(
        AppConfig config,
        ClarizenClient clarizen,
        ObjectConfig objectConfig,
        bool fullCrawl,
        DateTime? since,
        CancellationToken ct)
    {
        if (fullCrawl && !string.IsNullOrWhiteSpace(config.TdwExportPath))
        {
            var reader = new TdwBulkReader(config.TdwExportPath!);
            if (reader.HasExport(objectConfig.ObjectName))
            {
                using var tdwSpan = Tracing.StartSourceFetch(objectConfig.ObjectName, "tdw");
                var tdwRecords = reader.ReadObject(objectConfig.ObjectName)
                    .Select(row => new ClarizenRecord(objectConfig.ObjectName, row))
                    .ToList();
                return ValidateSourceRecordIds(objectConfig.ObjectName, tdwRecords, "TDW export");
            }
            Logger.Info(
                $"{objectConfig.ObjectName}: no TDW export found; falling back to the REST API.");
        }

        using var restSpan = Tracing.StartSourceFetch(objectConfig.ObjectName, "rest");
        var czql = ClarizenClient.BuildQuery(objectConfig, since);
        var rows = await clarizen.QueryAllAsync(czql, config.ClarizenQueryLimit, ct)
            .ConfigureAwait(false);
        return ValidateSourceRecordIds(
            objectConfig.ObjectName,
            rows.Select(row => new ClarizenRecord(objectConfig.ObjectName, row)).ToList(),
            "REST result");
    }

    /// <summary>
    /// Reject records with no recognizable id. An empty <see cref="ClarizenRecord.RawId"/>
    /// would collapse to the shared ItemId "{ObjectType}_", so every such row
    /// overwrites the same Graph item AND poisons the deletion sweep (the real
    /// ids all look stale). If NO record carries an id — a TDW export whose id
    /// column is missing or renamed — the whole batch is rejected with a hard
    /// error instead of being silently collapsed to a single item.
    /// </summary>
    internal static List<ClarizenRecord> ValidateSourceRecordIds(
        string objectName, List<ClarizenRecord> records, string source)
    {
        if (records.Count == 0)
            return records;

        var withIds = records.Where(r => !string.IsNullOrEmpty(r.RawId)).ToList();
        if (withIds.Count == 0)
        {
            throw new InvalidDataException(
                $"{objectName}: {source} has no recognized id column — none of the "
                + $"{records.Count} record(s) carry an 'id'/'Id' value. Refusing to ingest: "
                + "every row would collapse to one external item and corrupt deletion sync. "
                + "Fix the export to include the entity id column and re-run.");
        }
        if (withIds.Count < records.Count)
        {
            Logger.Error(
                $"{objectName}: dropping {records.Count - withIds.Count} of {records.Count} "
                + $"{source} record(s) with an empty id — each would collapse to the shared "
                + $"item id '{objectName}_'. Fix the export/source rows to include ids.");
        }
        return withIds;
    }

    // ── Deletion/tombstone sweep ─────────────────────────────────────────────

    /// <summary>
    /// Existence sweep for one object type after a full crawl: inventory ids
    /// absent from the source are DELETEd from the Graph connection and
    /// dropped from the inventory. Two mass-deletion safety guards skip the
    /// sweep — with a warning and a webhook alert — when an implausible amount
    /// of the inventory would vanish at once (source outage / truncated TDW
    /// export): an absolute per-sweep cap (DELETION_SYNC_MAX_ITEMS, default
    /// 1000, engaged at any inventory size) and a percentage guard
    /// (DELETION_SYNC_MAX_PERCENT, default 25, engaged once the inventory
    /// holds at least MinInventoryForSafetyGuard items — OR, at any size, when
    /// the full-crawl source returned zero rows, so a tiny all-stale inventory
    /// can no longer be wiped 100% unguarded).
    /// </summary>
    internal async Task SweepDeletionsAsync(
        AppConfig cfg,
        ObjectConfig objectConfig,
        List<ClarizenRecord> sourceRecords,
        IItemInventory inventory,
        IngestSummary summary,
        CancellationToken ct)
    {
        var sourceIds = sourceRecords.Select(r => r.ItemId).ToHashSet(StringComparer.Ordinal);
        List<string> indexed;
        lock (_inventoryLock)
        {
            indexed = inventory.IdsForObject(objectConfig.ObjectName);
        }
        var stale = indexed.Where(id => !sourceIds.Contains(id)).ToList();
        if (stale.Count == 0)
            return;

        using var sweepSpan = Tracing.StartDeletionSweep(objectConfig.ObjectName);
        sweepSpan?.SetTag("clarizen.stale_count", stale.Count);

        // Absolute cap first: never auto-delete more than DELETION_SYNC_MAX_ITEMS
        // in one sweep, regardless of inventory size — this also guards small
        // inventories where the percent check below does not engage.
        var maxItems = DeletionSyncMaxItems();
        if (maxItems > 0 && stale.Count > maxItems)
        {
            Logger.Warning(
                $"{objectConfig.ObjectName}: deletion sweep SKIPPED — {stale.Count} item(s) would "
                + $"be deleted, above the DELETION_SYNC_MAX_ITEMS={maxItems} absolute safety cap. "
                + "If this drop is real, raise the cap (or set it to 0 to disable) and re-run.");
            summary.SweepSkipped.Add(objectConfig.ObjectName);
            await Alerting.RaiseAsync(
                "deletion_sweep_skipped",
                $"Deletion sweep for '{objectConfig.ObjectName}' skipped: {stale.Count} stale item(s) "
                + $"exceed the absolute cap ({maxItems}) on connector '{cfg.ConnectorId}'",
                new Dictionary<string, object?>
                {
                    ["objectType"] = objectConfig.ObjectName,
                    ["stale"] = stale.Count,
                    ["inventory"] = indexed.Count,
                    ["maxItems"] = maxItems,
                }).ConfigureAwait(false);
            return;
        }

        // Percent guard. It engages when the inventory is large enough for a
        // percentage to be meaningful (>= MinInventoryForSafetyGuard) OR whenever
        // the source returned NO rows for this object on a FULL crawl — an empty
        // source is the classic outage / truncated-TDW-export signature and would
        // otherwise wipe a small (< MinInventoryForSafetyGuard) inventory 100%
        // completely unguarded, since the absolute cap only trips above
        // DELETION_SYNC_MAX_ITEMS. 0 and 100 stay the explicit "disable the
        // percent guard" values.
        var maxPercent = DeletionSyncMaxPercent();
        var sourceReturnedNothing = sourceRecords.Count == 0;
        if (maxPercent is > 0 and < 100
            && (indexed.Count >= MinInventoryForSafetyGuard || sourceReturnedNothing))
        {
            var stalePercent = 100.0 * stale.Count / indexed.Count;
            if (stalePercent > maxPercent)
            {
                Logger.Warning(
                    $"{objectConfig.ObjectName}: deletion sweep SKIPPED — {stale.Count}/{indexed.Count} "
                    + $"({stalePercent:0.#}%) of the inventory would be deleted, above the "
                    + $"DELETION_SYNC_MAX_PERCENT={maxPercent} safety guard. If this drop is real, "
                    + "raise the threshold (or set it to 0/100 to disable the guard) and re-run.");
                summary.SweepSkipped.Add(objectConfig.ObjectName);
                await Alerting.RaiseAsync(
                    "deletion_sweep_skipped",
                    $"Deletion sweep for '{objectConfig.ObjectName}' skipped: {stale.Count}/{indexed.Count} "
                    + $"items stale (> {maxPercent}%) on connector '{cfg.ConnectorId}'",
                    new Dictionary<string, object?>
                    {
                        ["objectType"] = objectConfig.ObjectName,
                        ["stale"] = stale.Count,
                        ["inventory"] = indexed.Count,
                        ["threshold"] = maxPercent,
                    }).ConfigureAwait(false);
                return;
            }
        }

        Logger.Info(
            $"{objectConfig.ObjectName}: deleting {stale.Count} item(s) no longer present in Clarizen...");
        foreach (var batch in Chunk(stale, cfg.GraphBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var failures = await DeleteBatchAsync(cfg, batch, ct).ConfigureAwait(false);
            var failedIds = failures.Select(f => f.ItemId).ToHashSet(StringComparer.Ordinal);
            var deleted = batch.Where(id => !failedIds.Contains(id)).ToList();
            lock (_inventoryLock)
            {
                inventory.Remove(deleted);
            }
            summary.Deleted += deleted.Count;
            Metrics.IncItemsDeleted(deleted.Count);
            foreach (var (itemId, error) in failures)
            {
                // Kept in the inventory — the next full-crawl sweep retries it.
                Logger.Warning($"Delete failed for {itemId} (will retry next sweep): {error}");
            }
        }
    }

    /// <summary>DELETION_SYNC_MAX_PERCENT (default 25). A value outside [0, 100]
    /// is a misconfiguration and falls back to the default with a warning —
    /// a negative value must NOT silently disable the guard. 0 and 100 remain
    /// the explicit, documented "disable the percent guard" values.</summary>
    internal static int DeletionSyncMaxPercent()
    {
        var value = EnvFlags.GetInt("DELETION_SYNC_MAX_PERCENT", 25);
        if (value is < 0 or > 100)
        {
            Logger.Warning(
                $"DELETION_SYNC_MAX_PERCENT={value} is outside [0, 100]; "
                + "using the default (25) instead.");
            return 25;
        }
        return value;
    }

    /// <summary>DELETION_SYNC_MAX_ITEMS (default 1000): absolute per-sweep cap.
    /// 0 disables the cap explicitly; a negative value is a misconfiguration
    /// and falls back to the default with a warning.</summary>
    internal static int DeletionSyncMaxItems()
    {
        var value = EnvFlags.GetInt("DELETION_SYNC_MAX_ITEMS", DefaultDeletionSyncMaxItems);
        if (value < 0)
        {
            Logger.Warning(
                $"DELETION_SYNC_MAX_ITEMS={value} is negative; "
                + $"using the default ({DefaultDeletionSyncMaxItems}) instead.");
            return DefaultDeletionSyncMaxItems;
        }
        return value;
    }

    /// <summary>
    /// DELETE up to 20 items in one $batch (single item → plain DELETE).
    /// HTTP 404 counts as success — the item is already gone from the index.
    /// </summary>
    internal async Task<List<(string ItemId, string Error)>> DeleteBatchAsync(
        AppConfig cfg, IReadOnlyList<string> itemIds, CancellationToken ct)
    {
        static bool IsGone(string error) => error.StartsWith("HTTP 404", StringComparison.Ordinal);

        if (itemIds.Count == 0)
            return new List<(string, string)>();

        if (itemIds.Count == 1)
        {
            var only = itemIds[0];
            var single = await _graph.DeleteAsync(ItemPath(cfg, only), ct).ConfigureAwait(false);
            if (single.IsSuccess || (int)single.StatusCode == 404)
                return new List<(string, string)>();
            return new List<(string, string)>
            {
                (only, $"HTTP {(int)single.StatusCode}: {single.RawBody}"),
            };
        }

        var requests = new JsonArray();
        var idToItem = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < itemIds.Count; i++)
        {
            var requestId = (i + 1).ToString();
            idToItem[requestId] = itemIds[i];
            requests.Add(new JsonObject
            {
                ["id"] = requestId,
                ["method"] = "DELETE",
                ["url"] = "/" + ItemPath(cfg, itemIds[i]),
            });
        }

        var response = await _graph.PostAsync("$batch", new JsonObject { ["requests"] = requests }, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            var error = $"$batch HTTP {(int)response.StatusCode}: {response.RawBody}";
            return itemIds.Select(id => (id, error)).ToList();
        }
        return ParseBatchFailures(response.Body, idToItem)
            .Where(f => !IsGone(f.Error))
            .ToList();
    }

    internal static List<List<T>> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        var chunks = new List<List<T>>();
        for (var i = 0; i < source.Count; i += size)
            chunks.Add(source.Skip(i).Take(size).ToList());
        return chunks;
    }

    // ── Chunk → Graph $batch (adaptive parallel dispatch) ────────────────────

    private async Task IngestChunkAsync(
        AppConfig cfg,
        ObjectConfig objectConfig,
        List<ClarizenRecord> chunk,
        int chunkIndex,
        IItemInventory inventory,
        IngestSummary summary,
        CancellationToken ct)
    {
        // Hot path: per-item debug strings are only built when a sink will
        // actually accept DEBUG (Python's isEnabledFor(DEBUG) gate).
        var debugEnabled = Logger.IsEnabledFor(LogLevel.Debug);

        var transformSpan = Tracing.StartTransform(objectConfig.ObjectName, chunk.Count);
        var items = new List<ExternalItem>();
        // Per-row error isolation: a malformed source row (a value the
        // converter/enricher/classifier cannot process) is dead-lettered
        // INDIVIDUALLY and the rest of the chunk continues — one poisoned row
        // in a 100k-row TDW export must never abort the whole object crawl.
        var transformFailures = new List<(string ItemId, string Error)>();
        foreach (var record in chunk)
        {
            try
            {
                var acl = _aclResolver.Resolve(record, objectConfig);
                if (acl.Count == 0)
                {
                    // Never ingest an item nobody is allowed to see — and never
                    // ingest it world-readable by accident either.
                    Logger.Warning(
                        $"Skipping {record.ItemId}: no resolvable ACL principals "
                        + "(set FALLBACK_ACL_GROUP_ID to grant a default group).");
                    summary.NoAclSkipped++;
                    Metrics.IncItemsSkipped();
                    continue;
                }
                var item = _converter.Convert(record, objectConfig, acl);
                if (_attachmentEnricher is not null && _attachmentEnricher.ShouldEnrich(objectConfig))
                {
                    await _attachmentEnricher.EnrichAsync(item, record, objectConfig, ct)
                        .ConfigureAwait(false);
                }
                // Classify AFTER enrichment so attachment text is in the scan.
                ApplyClassification(item, objectConfig);
                if (debugEnabled)
                {
                    Logger.Debug(
                        $"[{objectConfig.ObjectName}] converted {item.Id}: "
                        + $"{item.Properties.Count} properties, {item.Acl.Count} ACL entries, "
                        + $"content {item.Content.Length} chars");
                }
                items.Add(item);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;  // a real stop request — handled as a graceful stop upstream
            }
            catch (Exception exc)
            {
                // Full exception (type + stack) in the log: a transform crash is
                // unexpected, and "which converter line threw on which record"
                // is exactly what an operator needs to fix the source row.
                Logger.Error(
                    $"[{objectConfig.ObjectName}] record {record.ItemId} (chunk {chunkIndex}) "
                    + "failed to transform — dead-lettered, continuing with the rest of the chunk",
                    exc);
                transformFailures.Add(
                    (record.ItemId,
                     $"[transform chunk {chunkIndex}] {exc.GetType().Name}: {exc.Message}"));
            }
        }
        if (transformFailures.Count > 0)
        {
            lock (_statsLock)
            {
                summary.Failed += transformFailures.Count;
            }
            Metrics.IncItemsFailed(transformFailures.Count);
            SyncState.AppendFailedRecords(
                cfg.ConnectorId, transformFailures, objectConfig.ObjectName);
        }
        transformSpan?.SetTag("clarizen.item_count", items.Count);
        transformSpan?.Dispose();

        using var ingestSpan = Tracing.StartGraphIngest(objectConfig.ObjectName, items.Count);

        var subBatches = Chunk(items, cfg.GraphBatchSize);

        async Task<bool> SendOneAsync(List<ExternalItem> batch)
        {
            var (failures, sawThrottle, circuitOpen) =
                await PutBatchAsync(cfg, batch, ct).ConfigureAwait(false);
            if (circuitOpen)
            {
                // Graph breaker is open — this batch was NOT sent. Do not
                // dead-letter (these items are not failures) and do not record
                // inventory. Signal degraded so the caller stops + retains the
                // checkpoint; the chunk is retried on resume.
                lock (_statsLock)
                    summary.Degraded = true;
                return false;
            }
            var failedIds = failures.Select(f => f.ItemId).ToHashSet(StringComparer.Ordinal);
            var succeeded = batch.Count - failures.Count;

            lock (_statsLock)
            {
                summary.Ingested += succeeded;
                summary.PerObject[objectConfig.ObjectName] =
                    summary.PerObject.GetValueOrDefault(objectConfig.ObjectName) + succeeded;
                if (failures.Count > 0)
                    summary.Failed += failures.Count;
            }
            if (succeeded > 0)
            {
                // Inventory only ever records CONFIRMED puts — failed items are
                // absent so a later successful ingest (or sweep) stays truthful.
                var seenUtc = DateTime.UtcNow;
                var seen = batch
                    .Where(item => !failedIds.Contains(item.Id))
                    .Select(item => (item.Id, objectConfig.ObjectName))
                    .ToList();
                lock (_inventoryLock)
                {
                    inventory.RecordSeen(seen, seenUtc);
                }
            }
            Metrics.IncItemsIngested(succeeded);
            if (failures.Count > 0)
            {
                Metrics.IncItemsFailed(failures.Count);
                var requestBodies = batch
                    .Where(item => failedIds.Contains(item.Id))
                    .ToDictionary(item => item.Id, item => (JsonNode?)item.ToJson());
                SyncState.AppendFailedRecords(
                    cfg.ConnectorId, failures, objectConfig.ObjectName, requestBodies);
            }
            return sawThrottle;
        }

        void HandleSubBatchCrash(List<ExternalItem> failedBatch, Exception exc)
        {
            Logger.Error(
                $"[{objectConfig.ObjectName}] unexpected error sending a sub-batch of chunk {chunkIndex} — "
                + $"{failedBatch.Count} item(s) dead-lettered ({failedBatch[0].Id}"
                + $"{(failedBatch.Count > 1 ? $"..{failedBatch[^1].Id}" : string.Empty)}): {exc.Message}",
                exc);
            lock (_statsLock)
            {
                summary.Failed += failedBatch.Count;
            }
            Metrics.IncItemsFailed(failedBatch.Count);
            SyncState.AppendFailedRecords(
                cfg.ConnectorId,
                failedBatch
                    .Select(item => (item.Id,
                        $"[Graph chunk {chunkIndex}] sub-batch crash: {exc.GetType().Name}: {exc.Message}"))
                    .ToList(),
                objectConfig.ObjectName);
        }

        // Dispatch sub-batches in windows sized by the adaptive concurrency
        // dial: a throttled window shrinks the next one, clean windows grow it.
        var i = 0;
        while (i < subBatches.Count)
        {
            ct.ThrowIfCancellationRequested();
            var workers = Math.Max(1, _concurrency.Current);
            var window = subBatches.Skip(i).Take(workers).ToList();

            var sawThrottle = false;
            var tasks = window.Select(batch => (Batch: batch, Task: SendOneAsync(batch))).ToList();
            foreach (var entry in tasks)
            {
                try
                {
                    if (await entry.Task.ConfigureAwait(false))
                        sawThrottle = true;
                }
                catch (Exception exc)
                {
                    HandleSubBatchCrash(entry.Batch, exc);
                }
            }

            // Degraded mid-chunk: stop dispatching the rest of this chunk (they
            // would only fail fast too). The chunk is not checkpointed, so it is
            // retried whole on resume.
            if (summary.Degraded)
                break;

            if (sawThrottle)
                _concurrency.OnThrottle();
            else
                _concurrency.OnSuccess();

            if (debugEnabled)
            {
                Logger.Debug(
                    $"[{objectConfig.ObjectName}] window of {window.Count} sub-batch(es) sent "
                    + $"(concurrency now {_concurrency.Current})");
            }
            i += window.Count;
        }
    }

    /// <summary>
    /// PUT up to 20 items in one Graph $batch against <paramref name="cfg"/>'s
    /// connection. Returns per-item failures plus whether any 429 was observed
    /// (outer response or per-item status) for the adaptive-concurrency dial.
    /// </summary>
    internal async Task<(List<(string ItemId, string Error)> Failures, bool SawThrottle, bool CircuitOpen)>
        PutBatchAsync(AppConfig cfg, IReadOnlyList<ExternalItem> items, CancellationToken ct)
    {
        if (items.Count == 0)
            return (new List<(string, string)>(), false, false);

        // Single item — plain PUT (cheaper than a $batch envelope).
        if (items.Count == 1)
        {
            var only = items[0];
            var single = await _graph.PutAsync(ItemPath(cfg, only.Id), only.ToJson(), ct)
                .ConfigureAwait(false);
            if (single.CircuitOpen)
                return (new List<(string, string)>(), false, true);
            var throttled = (int)single.StatusCode == 429;
            return single.IsSuccess
                ? (new List<(string, string)>(), throttled, false)
                : (new List<(string, string)>
                {
                    (only.Id, $"HTTP {(int)single.StatusCode}: {single.RawBody}"),
                }, throttled, false);
        }

        var requests = new JsonArray();
        var idToItem = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var requestId = (i + 1).ToString();
            idToItem[requestId] = items[i].Id;
            requests.Add(new JsonObject
            {
                ["id"] = requestId,
                ["method"] = "PUT",
                ["url"] = "/" + ItemPath(cfg, items[i].Id),
                ["headers"] = new JsonObject { ["Content-Type"] = "application/json" },
                ["body"] = items[i].ToJson(),
            });
        }

        var response = await _graph.PostAsync("$batch", new JsonObject { ["requests"] = requests }, ct)
            .ConfigureAwait(false);
        if (response.CircuitOpen)
            return (new List<(string, string)>(), false, true);
        if (!response.IsSuccess)
        {
            var error = $"$batch HTTP {(int)response.StatusCode}: {response.RawBody}";
            return (items.Select(item => (item.Id, error)).ToList(), (int)response.StatusCode == 429, false);
        }
        var failures = ParseBatchFailures(response.Body, idToItem);
        var sawThrottle = BatchContains429(response.Body);
        return (failures, sawThrottle, false);
    }

    internal string ItemPath(AppConfig cfg, string itemId) =>
        $"external/connections/{cfg.ConnectorId}/items/{itemId}";

    /// <summary>Any per-request 429 inside a $batch envelope (adaptive-concurrency signal).</summary>
    internal static bool BatchContains429(JsonNode? batchBody)
    {
        if (batchBody?["responses"] is not JsonArray responses)
            return false;
        foreach (var entry in responses)
        {
            if (entry is JsonObject obj && (obj["status"]?.GetValue<int>() ?? 0) == 429)
                return true;
        }
        return false;
    }

    /// <summary>Extract per-request failures from a $batch response body. Testable, pure.</summary>
    internal static List<(string ItemId, string Error)> ParseBatchFailures(
        JsonNode? batchBody, IReadOnlyDictionary<string, string> requestIdToItemId)
    {
        var failures = new List<(string, string)>();
        if (batchBody?["responses"] is not JsonArray responses)
        {
            // A missing envelope means we cannot prove success for anything.
            foreach (var itemId in requestIdToItemId.Values)
                failures.Add((itemId, "Malformed $batch response (no 'responses' array)"));
            return failures;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in responses)
        {
            if (entry is not JsonObject obj)
                continue;
            var requestId = obj["id"]?.GetValue<string>() ?? string.Empty;
            if (!requestIdToItemId.TryGetValue(requestId, out var itemId))
                continue;
            seen.Add(requestId);
            var status = obj["status"]?.GetValue<int>() ?? 0;
            if (status is >= 200 and < 300)
                continue;
            var body = obj["body"]?.ToJsonString() ?? string.Empty;
            failures.Add((itemId, $"HTTP {status}: {body}"));
        }

        // Requests missing from the response envelope are failures too.
        foreach (var (requestId, itemId) in requestIdToItemId)
        {
            if (!seen.Contains(requestId))
                failures.Add((itemId, "No response entry in $batch envelope"));
        }
        return failures;
    }

    // ── Single-item ingestion (ingest-item / retry-failed) ───────────────────

    public async Task<(bool Ok, string? Error)> IngestSingleAsync(
        ClarizenRecord record, ObjectConfig objectConfig, CancellationToken ct = default)
    {
        var acl = _aclResolver.Resolve(record, objectConfig);
        if (acl.Count == 0)
            return (false, "No resolvable ACL principals");
        var item = _converter.Convert(record, objectConfig, acl);
        if (_attachmentEnricher is not null && _attachmentEnricher.ShouldEnrich(objectConfig))
            await _attachmentEnricher.EnrichAsync(item, record, objectConfig, ct).ConfigureAwait(false);
        ApplyClassification(item, objectConfig);

        // Under sharding a single item routes to the connection that owns its
        // object type (state keys and search results stay consistent).
        var cfg = _config;
        if (ShardingConfig.TryLoad(_schema, out var shards, out _))
        {
            var owner = shards.FirstOrDefault(s =>
                s.ObjectTypes.Contains(objectConfig.ObjectName, StringComparer.OrdinalIgnoreCase));
            if (owner is not null)
                cfg = _config.CloneForConnection(owner.ConnectionId);
        }

        var response = await _graph.PutAsync(ItemPath(cfg, item.Id), item.ToJson(), ct)
            .ConfigureAwait(false);
        if (response.IsSuccess)
        {
            using (var inventory = _inventoryFactory(cfg.ConnectorId))
            {
                inventory.RecordSeen(
                    new[] { (item.Id, objectConfig.ObjectName) }, DateTime.UtcNow);
            }
            Metrics.IncItemsIngested();
            return (true, null);
        }
        Metrics.IncItemsFailed();
        return (false, $"HTTP {(int)response.StatusCode}: {response.RawBody}");
    }

    /// <summary>
    /// Withdraw a single external item (event-driven tombstone / targeted
    /// delete): DELETE it from the connection that owns its object type and
    /// drop it from that connection's inventory. HTTP 404 is treated as
    /// already-gone (success). Shard-aware, so it reuses the deletion-sync
    /// machinery for one id.
    /// </summary>
    public async Task<(bool Ok, string? Error)> WithdrawSingleAsync(
        ObjectConfig objectConfig, string localId, CancellationToken ct = default)
    {
        var itemId = $"{objectConfig.ObjectName}_{localId}";
        var cfg = _config;
        if (ShardingConfig.TryLoad(_schema, out var shards, out _))
        {
            var owner = shards.FirstOrDefault(s =>
                s.ObjectTypes.Contains(objectConfig.ObjectName, StringComparer.OrdinalIgnoreCase));
            if (owner is not null)
                cfg = _config.CloneForConnection(owner.ConnectionId);
        }

        var failures = await DeleteBatchAsync(cfg, new[] { itemId }, ct).ConfigureAwait(false);
        if (failures.Count > 0)
            return (false, failures[0].Error);

        using (var inventory = _inventoryFactory(cfg.ConnectorId))
        {
            inventory.Remove(new[] { itemId });
        }
        Metrics.IncItemsDeleted();
        return (true, null);
    }
}
