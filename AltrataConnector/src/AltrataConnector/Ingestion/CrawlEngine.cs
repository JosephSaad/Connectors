// Ingestion/CrawlEngine.cs
// ------------------------
// Orchestrates full and incremental crawls of the SFTP-drop feed directory.
//
// Pipeline per crawl:
//   1. Seat sync — refuse to run with zero seats (entitlement fails closed).
//      Seat-hash change → batched re-ACL pass over every item whose stored
//      ACL hash differs, then commit the new hash.
//   2. Load CRM contacts (CRM_CONTACTS_PATH) for entity resolution.
//   3. Enumerate deliveries: full crawl processes every delivery on disk (PUTs
//      are idempotent); incremental only those missing from the ledger.
//   4. Per delivery: validate manifest SHA-256s (mismatch ⇒ reject + alert,
//      never ingest), then stream records per file in superchunks of
//      GRAPH_BATCH_SIZE × GRAPH_BATCH_WORKERS records, ingested through the
//      Graph $batch pipeline (adaptive concurrency + per-item 429 ladder in
//      GraphClient). The checkpoint advances per superchunk; a graceful stop
//      finishes (flushes) the in-flight superchunk first.
//   5. Records that fail transform or exhaust Graph retries are dead-lettered
//      (batch-append, corruption-safe under concurrent writers); the crawl
//      continues.
//   6. Reconciliation report per delivery (ingested + dead-lettered must equal
//      the manifest count); only a reconciled delivery is marked processed.
//   7. Close: in HA mode exactly one node closes the crawl and records the
//      sync timestamp — even when some deliveries FAILED, in which case the
//      recorded status is 'failed' (close-with-failed-claims semantics, see
//      docs/HA.md). Then the retention pass runs.

using System.Diagnostics;
using System.Text.Json;
using AltrataConnector.Altrata;
using AltrataConnector.Config;
using AltrataConnector.Entitlement;
using AltrataConnector.Graph;
using AltrataConnector.Identity;
using AltrataConnector.Infrastructure;
using AltrataConnector.State;

namespace AltrataConnector.Ingestion;

public sealed class CrawlProgress
{
    private long _ingested;
    private long _failed;
    private long _deleted;
    private long _suppressed;

    public string CurrentDelivery { get; set; } = "";
    public string CurrentDataset { get; set; } = "";
    public long Ingested => Interlocked.Read(ref _ingested);
    public long Failed => Interlocked.Read(ref _failed);
    public long Deleted => Interlocked.Read(ref _deleted);
    public long Suppressed => Interlocked.Read(ref _suppressed);

    public void AddIngested() => Interlocked.Increment(ref _ingested);
    public void AddFailed() => Interlocked.Increment(ref _failed);
    public void AddDeleted() => Interlocked.Increment(ref _deleted);
    public void AddSuppressed() => Interlocked.Increment(ref _suppressed);
}

public sealed record CrawlResult(
    int DeliveriesProcessed,
    int DeliveriesRejected,
    long ItemsIngested,
    long ItemsDeadLettered,
    bool Stopped,
    IReadOnlyList<DeliveryReconciliation> Reconciliations)
{
    /// <summary>Tombstones withdrawn from the index (delta deliveries).</summary>
    public long ItemsDeleted { get; init; }

    /// <summary>Records skipped because the subject is erasure-suppressed.</summary>
    public long ItemsSuppressed { get; init; }

    /// <summary>True when the crawl paused at a safe boundary because a critical
    /// dependency breaker opened (degraded mode). Checkpoint saved; resumes on
    /// the next crawl once the breaker half-opens and probes succeed.</summary>
    public bool Degraded { get; init; }

    /// <summary>False when another HA node won the crawl close (that node
    /// recorded the sync timestamp instead of this one).</summary>
    public bool ClosedByThisNode { get; init; } = true;

    /// <summary>closed | failed | (null when stopped / closed elsewhere).</summary>
    public string? CloseStatus { get; init; }
}

public sealed class CrawlEngine
{
    private static readonly IAppLogger Logger = Logging.GetLogger("altrata_connector.crawl");

    private readonly AppConfig _config;
    private readonly IGraphClient _graph;
    private readonly IStateStore _state;
    private readonly IIdentityStore _identity;
    private readonly SeatService _seats;
    private readonly IAlertSink _alerts;
    private readonly HaCoordinator? _ha;
    private readonly IDecisionLedger? _decisions;

    /// <summary>Pre-built ContentGate (CS-1). Null in production — the engine
    /// builds one per crawl from the environment. Injected by tests so a fake
    /// scanner (e.g. an unavailable one) can drive the fail-mode matrix.</summary>
    private readonly ContentGate? _contentGate;

    public CrawlProgress Progress { get; } = new();

    public CrawlEngine(AppConfig config, IGraphClient graph, IStateStore state,
        IIdentityStore identity, SeatService seats, IAlertSink alerts, HaCoordinator? ha = null,
        IDecisionLedger? decisions = null, ContentGate? contentGate = null)
    {
        _config = config;
        _graph = graph;
        _state = state;
        _identity = identity;
        _seats = seats;
        _alerts = alerts;
        _ha = ha;
        _decisions = decisions;
        _contentGate = contentGate;
    }

    /// <summary>True when classification-enforcement ACL restrictions should be
    /// recorded to the decision ledger (all opt-in prerequisites met).</summary>
    private bool AclRestrictionLedgerActive =>
        _decisions != null && _config.Classification && _config.ClassificationEnforceAcl
        && !string.IsNullOrWhiteSpace(_config.ClassificationEnforceGroupId);

    // ---- crawl entry points ---------------------------------------------------

    public Task<CrawlResult> RunAsync(string kind, string? datasetFilter = null,
        CancellationToken ct = default) =>
        RunAsync(kind, datasetFilter == null ? null : new[] { Datasets.Canonical(datasetFilter) }, ct);

    public async Task<CrawlResult> RunAsync(string kind, IReadOnlyCollection<string>? datasetFilters,
        CancellationToken ct = default)
    {
        // One correlation id + root span per crawl; both flow to every child
        // stage, log line, dead-letter record and reconciliation report.
        using var correlation = CorrelationContext.Begin();
        using var span = Telemetry.Span("crawl");
        Telemetry.SetTag(span, "altrata.crawl.kind", kind);

        Metrics.Set("altrata_crawl_in_progress", 1);
        try
        {
            return await RunCoreAsync(kind, datasetFilters, span, ct);
        }
        finally
        {
            Metrics.Set("altrata_crawl_in_progress", 0);
        }
    }

    private async Task<CrawlResult> RunCoreAsync(string kind,
        IReadOnlyCollection<string>? datasetFilters, Activity? rootSpan, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_config.FeedPath))
            throw new ConfigurationError("FEED_PATH is not set — nothing to crawl");

        // Fail fast on a garbage DEADLETTER_PAYLOAD_MODE before any work: a
        // crawl must never discover mid-delivery that it cannot dead-letter.
        _ = DeadLetterPolicy.Mode;

        // 1. Entitlement first: seats + potential re-ACL pass.
        SeatSyncResult seatSync;
        using (var seatSpan = Telemetry.Span("seat.sync"))
        {
            seatSync = _seats.SyncSeats();
            Telemetry.SetTag(seatSpan, "altrata.seat.count", seatSync.Seats.Count);
            Telemetry.SetTag(seatSpan, "altrata.seat.hash", seatSync.SeatHash);
            Telemetry.SetTag(seatSpan, "altrata.seat.changed", seatSync.Changed);
        }
        var acl = SeatAclBuilder.BuildAcl(seatSync.Seats);
        // #5 Entitlement freshness: the seat list is re-loaded on EVERY crawl, so
        // new items always get the current ACL and a seat change is detected each
        // crawl. IDENTITY_SYNC_ON_INCREMENTAL (default true) additionally runs the
        // re-ACL SWEEP over existing items on INCREMENTAL crawls, shrinking
        // entitlement lag to the incremental cadence (not real-time — a mid-cycle
        // seat removal is enforced at the next crawl). Set false to defer that
        // potentially large sweep to full crawls only.
        var reAclNow = kind != CrawlKind.Incremental || _config.IdentitySyncOnIncremental;
        if (seatSync.RequiresReAcl && reAclNow)
            await ReAclPassAsync(seatSync, ct);
        else if (seatSync.RequiresReAcl)
            Logger.Info("Seat list changed but IDENTITY_SYNC_ON_INCREMENTAL=false — re-ACL sweep " +
                        "deferred to the next full crawl (newly ingested items still use the current seats).");
        else if (seatSync.Changed)
            _seats.CommitSeatHash(seatSync.SeatHash);  // first ever sync — nothing to re-ACL

        // 2. CRM contacts for entity resolution.
        var resolver = LoadCrmContacts();

        // Discover deliveries. The path index is rebuilt from the FULL set on
        // disk (deterministic snapshot of current feed contents), while
        // ingestion of an incremental crawl only processes unprocessed ones.
        var allDeliveries = FeedReader.DiscoverDeliveries(_config.FeedPath!);

        // 2b. Relationship-path index (opt-in) — built before the transformer so
        // person items can join their bounded path summary at transform time.
        var pathIndex = BuildPathIndex(allDeliveries);
        var transformer = new ItemTransformer(resolver, pathIndex, BuildClassificationOptions(),
            _config.GraphItemTtlDays);

        // 2c. ContentGate (CS-1): built ONCE per crawl so the pattern table is
        // compiled once. Null when CONTENT_GATE is off — the ingest loop then
        // does literally no extra work and the wire output is unchanged.
        var contentGate = _contentGate ?? ContentGate.FromEnv();
        if (contentGate != null)
        {
            Logger.Info($"Content gate ENABLED (text fail mode: " +
                        $"{contentGate.Options.TextFailMode.ToString().ToLowerInvariant()}, " +
                        $"max scan {contentGate.Options.MaxScanMb} MB). No malware scanner: this " +
                        "connector ingests no binary content; file integrity is the SHA-256 manifest gate.");
        }

        // 3. Deliveries to process.
        var deliveries = kind == CrawlKind.Incremental
            ? allDeliveries.Where(d => !_state.IsDeliveryProcessed(d.Id)).ToList()
            : allDeliveries;
        Telemetry.SetTag(rootSpan, "altrata.crawl.deliveries", deliveries.Count);
        Logger.Info($"{kind} crawl: {deliveries.Count} delivery(ies) to process" +
                    (datasetFilters != null ? $" (dataset filter: {string.Join(",", datasetFilters)})" : ""));

        var reconciliations = new List<DeliveryReconciliation>();
        var processed = 0;
        var rejected = 0;
        var stopped = false;
        var degraded = false;

        foreach (var delivery in deliveries)
        {
            if (ServiceStop.Requested || ct.IsCancellationRequested)
            {
                stopped = true;
                break;
            }

            // Degraded mode: a critical dependency (Graph) is down — pause new
            // work at this delivery boundary rather than hammer/dead-letter.
            // The next crawl half-opens the breaker and probes; success resumes.
            if (_graph.BreakerState == CircuitState.Open)
            {
                Logger.Warning($"Graph breaker OPEN — pausing before delivery '{delivery.Id}' (degraded mode).");
                degraded = true;
                stopped = true;
                break;
            }

            // HA: one node per delivery.
            var leaseName = $"delivery:{delivery.Id}";
            if (_ha != null && !_ha.TryAcquire(leaseName))
            {
                Logger.Info($"Delivery '{delivery.Id}' is leased by another node — skipping");
                continue;
            }

            try
            {
                var summary = await ProcessDeliveryAsync(delivery, acl, transformer, contentGate, datasetFilters, ct);
                reconciliations.Add(summary);
                switch (summary.Status)
                {
                    case Reconciliation.StatusRejected:
                        rejected++;
                        break;
                    case Reconciliation.StatusReconciled:
                        processed++;
                        break;
                    default:
                        if (ServiceStop.Requested || ct.IsCancellationRequested)
                            stopped = true;  // partial delivery due to graceful stop
                        break;
                }
            }
            catch (CircuitOpenException)
            {
                // Breaker opened mid-delivery. The checkpoint was saved at the
                // superchunk boundary before the failing batch, so no state is
                // lost; pause here (degraded) and resume next crawl.
                Logger.Warning($"Graph breaker opened during delivery '{delivery.Id}' — pausing (degraded mode); checkpoint saved.");
                degraded = true;
                stopped = true;
            }
            finally
            {
                _ha?.Release(leaseName);
            }

            if (stopped || ServiceStop.Requested || ct.IsCancellationRequested)
            {
                stopped = true;
                break;
            }
        }

        // 6. Bookkeeping + alerts. Only REPLAYABLE records count toward the
        // dead-letter depth alert: transform failures are deterministic and
        // un-replayable (they need a feed fix + re-ingest, or an explicit
        // `retry-failed --retire-unreplayable`), so they must not trip the
        // replay-queue alert forever. They stay visible via their own gauge.
        var deadLetters = _state.ReadDeadLetters();
        var replayableDepth = deadLetters.Count(r => r.IsReplayable);
        Metrics.SetDeadLetterDepth(replayableDepth);
        Metrics.Set("altrata_deadletter_unreplayable", deadLetters.Count - replayableDepth);
        await _alerts.CheckDeadLetterDepthAsync(replayableDepth);

        // 7. Close (pinned close-with-failed-claims semantics — docs/HA.md):
        //    a crawl with failed claims still CLOSES (status 'failed', never
        //    wedged open); in HA mode exactly one node performs the close and
        //    records the sync timestamp, and a retry by the winner stays a win.
        var closedByThisNode = true;
        string? closeStatus = null;
        if (!stopped)
        {
            var anyFailed = reconciliations.Any(r =>
                r.Status is Reconciliation.StatusRejected or Reconciliation.StatusMismatch);
            closeStatus = anyFailed ? "failed" : "closed";

            if (_ha != null && deliveries.Count > 0)
            {
                var crawlId = $"{kind}:{deliveries[^1].Id}";
                closedByThisNode = _ha.TryCloseCrawl(crawlId, anyFailed, _state);
                if (!closedByThisNode)
                {
                    closeStatus = null;
                    Logger.Info($"[HA] Crawl '{crawlId}' closed by another node — sync state recorded there.");
                }
                else if (anyFailed)
                {
                    Logger.Warning($"[HA] Crawl '{crawlId}' closed WITH FAILED claims — status 'failed' recorded.");
                }
            }

            if (closedByThisNode)
            {
                _state.SetLastSync(kind, DateTime.UtcNow);
                Metrics.Set($"altrata_last_{kind}_crawl_timestamp_seconds",
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                Retention.Apply(_config, _state);
            }
        }

        Metrics.Set("altrata_suppression_list_size", _state.ListSuppressedSubjects().Count);
        return new CrawlResult(processed, rejected, Progress.Ingested, Progress.Failed,
            stopped, reconciliations)
        {
            ItemsDeleted = Progress.Deleted,
            ItemsSuppressed = Progress.Suppressed,
            Degraded = degraded,
            ClosedByThisNode = closedByThisNode,
            CloseStatus = closeStatus,
        };
    }

    // ---- delivery processing ----------------------------------------------------

    private async Task<DeliveryReconciliation> ProcessDeliveryAsync(Delivery delivery,
        IReadOnlyList<AclEntry> acl, ItemTransformer transformer, ContentGate? contentGate,
        IReadOnlyCollection<string>? datasetFilters, CancellationToken ct)
    {
        using var deliverySpan = Telemetry.Span("delivery");
        Telemetry.SetTag(deliverySpan, "altrata.delivery.id", delivery.Id);
        Telemetry.SetTag(deliverySpan, "altrata.delivery.files", delivery.Manifest.Files.Count);

        Progress.CurrentDelivery = delivery.Id;
        Logger.Info($"Processing delivery '{delivery.Id}' ({delivery.Manifest.Files.Count} file(s))");

        // 4a. Checksum gate — reject the whole delivery on any mismatch.
        try
        {
            using var manifestSpan = Telemetry.Span("manifest.validate");
            Telemetry.SetTag(manifestSpan, "altrata.manifest.files", delivery.Manifest.Files.Count);
            FeedReader.ValidateChecksums(delivery);
        }
        catch (Exception exc) when (exc is ChecksumMismatchException or FileNotFoundException)
        {
            Telemetry.SetTag(deliverySpan, "altrata.delivery.status", Reconciliation.StatusRejected);
            Logger.Error($"Delivery '{delivery.Id}' REJECTED: {exc.Message}");
            Metrics.Increment("altrata_deliveries_rejected_total");
            // Immutable decision audit (#11): the delivery is EXCLUDED from the
            // index — record it in the tamper-evident decision ledger (PII-safe:
            // delivery id + checksum/file reason only).
            _decisions?.RecordExclusion(delivery.Id, $"delivery rejected at checksum gate: {exc.Message}");
            await _alerts.SendAsync("critical", "delivery_rejected", exc.Message,
                new Dictionary<string, object?> { ["deliveryId"] = delivery.Id });
            var rejectedSummary = Reconciliation.Summarize(
                delivery.Id, Array.Empty<FileReconciliation>(), exc.Message);
            Reconciliation.WriteReport(_config.ConnectorId, rejectedSummary);
            return rejectedSummary;
        }

        // 4b. Resume position from a checkpoint left by a crash/stop.
        var checkpoint = _state.GetCheckpoint();
        var resuming = checkpoint != null && checkpoint.DeliveryId == delivery.Id;
        if (resuming)
            Logger.Info($"Resuming '{delivery.Id}' at {checkpoint!.FileName}[{checkpoint.RecordIndex}]");

        var seatHash = SeatAclBuilder.ComputeSeatHash(_identity.GetSeats());
        var fileResults = new List<FileReconciliation>();
        var stoppedMidway = false;
        // Per-delivery classification manifest accumulator (opt-in). Item id →
        // label + category labels only; never any personal value.
        var classificationEntries = _config.Classification && _config.ClassificationManifest
            ? new List<ClassificationEntry>()
            : null;
        // #11 ACL-restriction audit accumulator: item ids whose ACL was tightened
        // to the reviewer group by classification enforcement (#6b). Collected
        // per-delivery and ledgered as ONE summary decision below (low-volume,
        // never per-ingest). Only active when all enforcement prerequisites hold.
        var aclRestrictedItemIds = AclRestrictionLedgerActive ? new List<string>() : null;

        foreach (var file in delivery.Manifest.Files)
        {
            var dataset = Datasets.Canonical(file.Dataset);
            if (datasetFilters != null &&
                !datasetFilters.Contains(dataset, StringComparer.OrdinalIgnoreCase))
                continue;
            Progress.CurrentDataset = dataset;

            var path = Path.Combine(delivery.Directory, file.Name);
            IReadOnlyList<FeedRecord> records;
            try
            {
                // Re-verify the manifest SHA-256 against the SAME open handle
                // the records are parsed from: a file swapped after the 4a gate
                // (TOCTOU) is rejected here instead of being ingested under the
                // manifest's hash.
                records = FeedReader.ReadRecords(path, dataset, file.Sha256);
            }
            catch (Exception exc) when (exc is ChecksumMismatchException or FileNotFoundException
                                            or FeedFileTooLargeException)
            {
                _state.ClearCheckpoint();
                Telemetry.SetTag(deliverySpan, "altrata.delivery.status", Reconciliation.StatusRejected);
                Logger.Error($"Delivery '{delivery.Id}' REJECTED at read time: {exc.Message}");
                Metrics.Increment("altrata_deliveries_rejected_total");
                // Immutable decision audit (#11): exclusion at read time (TOCTOU
                // swap / read failure) — ledgered PII-safe.
                _decisions?.RecordExclusion(delivery.Id, $"delivery rejected at read time: {exc.Message}");
                await _alerts.SendAsync("critical", "delivery_rejected", exc.Message,
                    new Dictionary<string, object?> { ["deliveryId"] = delivery.Id });
                var readRejected = Reconciliation.Summarize(delivery.Id, fileResults, exc.Message);
                Reconciliation.WriteReport(_config.ConnectorId, readRejected);
                return readRejected;
            }
            catch (Exception exc) when (exc is JsonException or FormatException or NotSupportedException)
            {
                // Malformed-but-checksum-consistent feed file (bad JSON/CSV or
                // an unsupported extension). This still aborts the whole crawl
                // (unchanged — the publisher must fix the file), but names the
                // delivery/file/dataset first: a bare JsonException surfacing at
                // the top level identifies a byte offset and nothing else.
                Logger.Error($"Delivery '{delivery.Id}' file '{file.Name}' ({dataset}) failed to parse — " +
                             $"{exc.GetType().Name}: {exc.Message}. Aborting the crawl; fix the feed file and re-run.");
                throw;
            }
            if (records.Count != file.RecordCount)
                Logger.Warning($"'{file.Name}': manifest says {file.RecordCount} records, parsed {records.Count}");

            var startIndex = 0;
            if (resuming && checkpoint!.FileName == file.Name && checkpoint.Dataset == dataset)
            {
                startIndex = checkpoint.RecordIndex;
                resuming = false;  // resume point consumed
            }
            else if (resuming)
            {
                // Files before the checkpointed one were fully ingested — count
                // them as ingested for reconciliation without re-PUTting.
                var alreadyDone = delivery.Manifest.Files
                    .TakeWhile(f => f.Name != checkpoint!.FileName)
                    .Any(f => f.Name == file.Name);
                if (alreadyDone)
                {
                    fileResults.Add(new FileReconciliation
                    {
                        File = file.Name,
                        Dataset = dataset,
                        ManifestCount = file.RecordCount,
                        ParsedCount = records.Count,
                        Ingested = records.Count,
                        DeadLettered = 0,
                    });
                    continue;
                }
            }

            using var datasetSpan = Telemetry.Span("dataset.ingest");
            Telemetry.SetTag(datasetSpan, "altrata.dataset", dataset);
            Telemetry.SetTag(datasetSpan, "altrata.records.count", records.Count);

            var (ingested, deleted, suppressed, deadLettered, stopped) = await IngestRecordsAsync(
                delivery, file, dataset, records, startIndex, acl, transformer, contentGate, seatHash,
                classificationEntries, aclRestrictedItemIds, ct);

            Telemetry.SetTag(datasetSpan, "altrata.records.ingested", ingested);
            Telemetry.SetTag(datasetSpan, "altrata.records.deleted", deleted);
            Telemetry.SetTag(datasetSpan, "altrata.records.suppressed", suppressed);
            Telemetry.SetTag(datasetSpan, "altrata.records.deadlettered", deadLettered);

            // A resumed file's earlier records were ingested in the previous run.
            fileResults.Add(new FileReconciliation
            {
                File = file.Name,
                Dataset = dataset,
                ManifestCount = file.RecordCount,
                ParsedCount = records.Count,
                Ingested = ingested + startIndex,
                Deleted = deleted,
                Suppressed = suppressed,
                DeadLettered = deadLettered,
            });

            if (stopped)
            {
                stoppedMidway = true;
                break;
            }
        }

        if (stoppedMidway)
        {
            // Keep the checkpoint; the delivery is neither reconciled nor rejected yet.
            var partial = Reconciliation.Summarize(delivery.Id, fileResults, error: null);
            Logger.Warning($"Delivery '{delivery.Id}' interrupted by graceful stop — checkpoint saved");
            return partial with { Status = "interrupted" };
        }

        // 4c. Done with the delivery — clear the checkpoint, reconcile, report.
        _state.ClearCheckpoint();
        if (classificationEntries is { Count: > 0 })
        {
            var manifestPath = ClassificationManifest.Write(
                _config.ConnectorId, delivery.Id, classificationEntries);
            Logger.Info($"Delivery '{delivery.Id}': classification manifest ({classificationEntries.Count} item(s)) — {manifestPath}");
        }
        // #11 ACL-restriction decision: one tamper-evident summary entry per
        // delivery when classification enforcement (#6b) locked top-tier items to
        // the reviewer group. Low-volume by design (per delivery, not per item).
        if (aclRestrictedItemIds is { Count: > 0 })
        {
            _decisions!.RecordAclRestriction(delivery.Id,
                $"{aclRestrictedItemIds.Count} Restricted item(s) ACL-locked to group " +
                $"{_config.ClassificationEnforceGroupId} (CLASSIFICATION_ENFORCE_ACL)");
            Metrics.Increment("altrata_items_acl_restricted_total", aclRestrictedItemIds.Count);
            Logger.Info($"Delivery '{delivery.Id}': {aclRestrictedItemIds.Count} item(s) ACL-restricted " +
                        "to the reviewer group (classification enforcement) — decision ledgered.");
        }
        var summary = Reconciliation.Summarize(delivery.Id, fileResults);
        Telemetry.SetTag(deliverySpan, "altrata.delivery.status", summary.Status);
        var reportPath = Reconciliation.WriteReport(_config.ConnectorId, summary);
        Logger.Info($"Delivery '{delivery.Id}': {summary.Status} " +
                    $"(manifest {summary.TotalManifestRecords}, ingested {summary.TotalIngested}, " +
                    $"dead-lettered {summary.TotalDeadLettered}) — report: {reportPath}");

        if (summary.Status == Reconciliation.StatusReconciled)
        {
            var now = DateTime.UtcNow;
            _state.MarkDeliveryProcessed(delivery.Id, now);
            _state.SetValue($"delivery_processed_{delivery.Id}", now.ToString("o"));
            Metrics.Increment("altrata_deliveries_processed_total");
        }
        else
        {
            await _alerts.SendAsync("warning", "reconciliation_mismatch",
                $"Delivery '{delivery.Id}' did not reconcile: manifest {summary.TotalManifestRecords}, " +
                $"ingested {summary.TotalIngested}, dead-lettered {summary.TotalDeadLettered}",
                new Dictionary<string, object?> { ["deliveryId"] = delivery.Id });
        }
        return summary;
    }

    /// <summary>
    /// Batched ingest of one file's records. A superchunk is GRAPH_BATCH_SIZE ×
    /// GRAPH_BATCH_WORKERS records; the Graph client splits it into ≤20-item
    /// $batch calls and runs them in adaptive waves. The checkpoint advances
    /// once per superchunk, which is also the graceful-stop flush unit.
    /// </summary>
    private async Task<(int Ingested, int Deleted, int Suppressed, int DeadLettered, bool Stopped)> IngestRecordsAsync(
        Delivery delivery, ManifestFile file, string dataset, IReadOnlyList<FeedRecord> records,
        int startIndex, IReadOnlyList<AclEntry> acl, ItemTransformer transformer,
        ContentGate? contentGate, string seatHash,
        List<ClassificationEntry>? classificationEntries, List<string>? aclRestrictedItemIds,
        CancellationToken ct)
    {
        var ingested = 0;
        var deleted = 0;
        var suppressed = 0;
        var deadLettered = 0;
        var superChunk = Math.Max(1, _config.GraphBatchSize) * Math.Max(1, _config.GraphBatchWorkers);
        // Dead-letter payload protection (DEADLETTER_PAYLOAD_MODE, default
        // redacted): with redaction on, queue records carry ids/subject-hashes/
        // error/attempts ONLY — never the transformed profile payload.
        var redactPayloads = DeadLetterPolicy.IsRedacted;

        var index = startIndex;
        while (index < records.Count)
        {
            // Superchunk boundary: save checkpoint + honour graceful stop.
            _state.SaveCheckpoint(new CrawlCheckpoint
            {
                DeliveryId = delivery.Id,
                Dataset = dataset,
                FileName = file.Name,
                RecordIndex = index,
            });
            if (index > startIndex && (ServiceStop.Requested || ct.IsCancellationRequested))
                return (ingested, deleted, suppressed, deadLettered, true);

            // Degraded mode: if the Graph breaker is Open, pause at this safe
            // boundary (checkpoint is saved) rather than shipping into a
            // fail-fast that would dead-letter the superchunk. Propagates up as
            // a degraded pause.
            if (_graph.BreakerState == CircuitState.Open)
                throw new CircuitOpenException("graph");

            var end = Math.Min(records.Count, index + superChunk);
            var deadLetters = new List<DeadLetterRecord>();

            // Transform the superchunk. Delta tombstones are split off for
            // withdrawal; erasure-suppressed subjects are skipped entirely;
            // transform failures dead-letter here; entitlement violations abort.
            var items = new List<ExternalItem>(end - index);
            // ContentGate quarantines collected here and audited AFTER the
            // transform loop: the ledger append and the alert must not run
            // inside the loop's catch-all, or a ledger fault would be recorded
            // as a transform failure.
            var quarantined = new List<ContentScanVerdict>();
            var tombstones = new List<string>();
            // itemId → raw person id, for PersonProfile tombstones so a
            // withdrawn person is also dropped from the path index.
            var tombstonedPersonIds = new Dictionary<string, string>(StringComparer.Ordinal);
            var payloadJson = new Dictionary<string, string>(StringComparer.Ordinal);
            // itemId → subject person id(s), for the erasure reverse index.
            var itemSubjects = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            // itemId → classification entry, harvested from the item's props (no values).
            var classificationByItem = classificationEntries == null
                ? null
                : new Dictionary<string, ClassificationEntry>(StringComparer.Ordinal);
            for (var i = index; i < end; i++)
            {
                var record = records[i];
                IReadOnlyList<string> subjects = Array.Empty<string>();
                try
                {
                    if (record.IsTombstone)
                    {
                        var recordId = record.Id ?? throw new TransformException(
                            $"{dataset} tombstone record has no id — cannot withdraw");
                        var itemId = ItemTransformer.BuildItemId(dataset, recordId);
                        tombstones.Add(itemId);
                        if (dataset == Datasets.PersonProfile)
                            tombstonedPersonIds[itemId] = recordId;
                        continue;
                    }

                    // Erasure durability: never re-ingest an erased subject
                    // (any subject of the record being suppressed skips it).
                    subjects = ItemTransformer.SubjectIds(record);
                    if (subjects.Any(_state.IsSubjectSuppressed))
                    {
                        suppressed++;
                        Progress.AddSuppressed();
                        Metrics.Increment("altrata_items_suppressed_total");
                        continue;
                    }

                    var item = transformer.Transform(record, acl);

                    // ---- ContentGate (CS-1) ----------------------------------
                    // Scan the FINAL indexed text before it can become Copilot
                    // grounding context. QUARANTINE, not drop: a positive
                    // verdict routes the item to the existing dead-letter queue
                    // with a PII-safe reason and stays re-drivable.
                    if (contentGate != null)
                    {
                        var gated = contentGate.Inspect(item);
                        item = gated.Item;                       // carries the scan-status property
                        if (gated.Verdict.Quarantine)
                        {
                            deadLetters.Add(new DeadLetterRecord
                            {
                                ItemId = item.Id,
                                Dataset = dataset,
                                DeliveryId = delivery.Id,
                                // PII-safe: category only, never the matched text.
                                Error = gated.Verdict.Reason,
                                // Upsert (not 'transform'): the item exists and
                                // replaying it is meaningful once reviewed, so
                                // it stays REPLAYABLE for retry-failed.
                                Op = DeadLetterOps.Upsert,
                                PayloadJson = redactPayloads ? "" : JsonSerializer.Serialize(item),
                                Redacted = redactPayloads,
                                SubjectIds = redactPayloads ? Array.Empty<string>() : subjects,
                                SubjectHashes = DeadLetterPolicy.HashSubjects(subjects),
                            });
                            quarantined.Add(gated.Verdict);
                            continue;
                        }
                    }

                    items.Add(item);
                    payloadJson[item.Id] = JsonSerializer.Serialize(item);
                    if (subjects.Count > 0)
                        itemSubjects[item.Id] = subjects;
                    if (classificationByItem != null)
                        classificationByItem[item.Id] = ClassificationEntryFor(item);
                }
                catch (EntitlementViolationException)
                {
                    // Never dead-letter an entitlement violation: it aborts the
                    // crawl (fail closed) and is logged + alerted at the
                    // command boundary (WithRuntime).
                    throw;
                }
                catch (Exception exc)
                {
                    // Transform failures are deterministic (no item exists to
                    // PUT), so they are marked with the un-replayable op:
                    // retry-failed reports/retires them instead of throwing on
                    // a missing payload forever, and they don't count toward
                    // the replay-queue alert depth.
                    deadLetters.Add(new DeadLetterRecord
                    {
                        ItemId = ItemTransformer.BuildItemId(dataset, record.Id ?? $"row{i}"),
                        Dataset = dataset,
                        DeliveryId = delivery.Id,
                        Error = exc.Message,
                        Op = DeadLetterOps.Transform,
                        PayloadJson = "",
                        SubjectIds = redactPayloads ? Array.Empty<string>() : subjects,
                        SubjectHashes = DeadLetterPolicy.HashSubjects(subjects),
                    });
                    // Same visibility as a failed PUT: dataset + record id +
                    // delivery + why (ids only — never a profile field value).
                    Logger.Warning($"Transform dead-lettered {dataset} record '{record.Id ?? $"row{i}"}' " +
                                   $"(delivery '{delivery.Id}'): {exc.GetType().Name}: {exc.Message}");
                    if (Logger.IsEnabledFor(Connector.Chassis.LogLevel.Debug))
                        Logger.Debug($"Transform failed for {dataset} record {record.Id ?? i.ToString()}: {exc}");
                }
            }

            // ContentGate audit trail, outside the transform loop's catch-all:
            // one tamper-evident ledger entry per quarantined item plus the
            // EXISTING alert path. Both carry the item id and the category only.
            foreach (var verdict in quarantined)
            {
                _decisions?.RecordQuarantine(verdict.ItemId, verdict.Reason);
                await _alerts.SendAsync("warning", "content_gate_blocked",
                    $"Content gate quarantined item '{verdict.ItemId}' ({verdict.Reason})",
                    new Dictionary<string, object?>
                    {
                        ["itemId"] = verdict.ItemId,
                        ["category"] = verdict.Category,
                        ["deliveryId"] = delivery.Id,
                    });
            }

            // Ship the superchunk through the $batch pipeline: upserts then tombstones.
            IReadOnlyList<BatchOpResult> putResults;
            IReadOnlyList<BatchOpResult> deleteResults;
            try
            {
                putResults = items.Count > 0
                    ? await _graph.PutItemsBatchAsync(items, ct)
                    : Array.Empty<BatchOpResult>();
                deleteResults = tombstones.Count > 0
                    ? await _graph.DeleteItemsBatchAsync(tombstones, ct)
                    : Array.Empty<BatchOpResult>();
            }
            catch (OperationCanceledException)
            {
                // Graceful stop mid-superchunk: resume from the superchunk start
                // (PUTs and DELETEs are idempotent, so re-shipping is safe).
                _state.SaveCheckpoint(new CrawlCheckpoint
                {
                    DeliveryId = delivery.Id,
                    Dataset = dataset,
                    FileName = file.Name,
                    RecordIndex = index,
                });
                if (deadLetters.Count > 0)
                    _state.AddDeadLetters(StampCorrelation(deadLetters));
                return (ingested, deleted, suppressed, deadLettered + deadLetters.Count, true);
            }

            var resolved = new HashSet<string>(StringComparer.Ordinal);
            var succeededPuts = new List<string>();
            foreach (var result in putResults)
            {
                resolved.Add(result.ItemId);
                if (result.Success)
                {
                    // Inventory + reverse index are journaled BEFORE the
                    // suppression re-read below: a forget-subject racing this
                    // superchunk either takes its withdrawal snapshot after
                    // these rows exist (and withdraws them itself), or it
                    // committed its suppression first (and the re-read below
                    // catches it) — there is no ordering in which both sides
                    // miss the item.
                    _identity.RecordIngestedItem(new IngestedItem(result.ItemId, dataset, seatHash, DateTime.UtcNow));
                    // Reverse index for per-subject erasure (DSAR).
                    if (itemSubjects.TryGetValue(result.ItemId, out var subjectIds))
                        _identity.RecordItemSubjects(result.ItemId, subjectIds.ToList());
                    succeededPuts.Add(result.ItemId);
                }
                else
                {
                    var failedSubjects = itemSubjects.GetValueOrDefault(result.ItemId)
                                         ?? Array.Empty<string>();
                    deadLetters.Add(new DeadLetterRecord
                    {
                        ItemId = result.ItemId,
                        Dataset = dataset,
                        DeliveryId = delivery.Id,
                        Error = result.Error ?? $"HTTP {result.Status}",
                        // Payload protection: redacted queues never hold the
                        // profile; replay re-fetches from the feed delivery.
                        PayloadJson = redactPayloads ? "" : payloadJson.GetValueOrDefault(result.ItemId, ""),
                        Redacted = redactPayloads,
                        SubjectIds = redactPayloads ? Array.Empty<string>() : failedSubjects,
                        SubjectHashes = DeadLetterPolicy.HashSubjects(failedSubjects),
                    });
                    Logger.Warning($"Dead-lettered {dataset} item {result.ItemId}: {result.Error}");
                }
            }

            // DSAR race guard (round-2): a forget-subject can run to completion
            // between this superchunk's per-record suppression pre-check and
            // this point. Such a PUT silently resurrects an erased subject: the
            // erasure's withdrawal snapshot may predate these items entirely.
            // Re-read the suppression list AFTER the successful PUTs were
            // journaled above and withdraw every item whose subject is
            // suppressed by now — suppression wins, whichever side finishes
            // last (forget-subject commits its suppression BEFORE taking its
            // withdrawal snapshot, so the two guards interlock with no window).
            var suppressedNow = itemSubjects.Count == 0 || succeededPuts.Count == 0
                ? null
                : new HashSet<string>(_state.ListSuppressedSubjects(), StringComparer.Ordinal);
            // #11: item ids whose ACL was tightened to the reviewer group by
            // classification enforcement (#6b) — a top-tier item is detected by
            // its Restricted classification tag (only present when classification
            // is on, which enforcement requires).
            var itemsById = aclRestrictedItemIds == null
                ? null
                : items.ToDictionary(i => i.Id, StringComparer.Ordinal);
            var resurrected = new List<string>();
            foreach (var itemId in succeededPuts)
            {
                if (suppressedNow is { Count: > 0 }
                    && itemSubjects.TryGetValue(itemId, out var subjectsOfItem)
                    && subjectsOfItem.Any(suppressedNow.Contains))
                {
                    resurrected.Add(itemId);   // erased mid-flight: withdraw below
                    continue;
                }
                // Classification manifest: id → label + categories (no values).
                if (classificationEntries != null && classificationByItem!.TryGetValue(itemId, out var ce))
                    classificationEntries.Add(ce);
                if (aclRestrictedItemIds != null
                    && itemsById!.TryGetValue(itemId, out var putItem)
                    && putItem.Properties.TryGetValue(ItemTransformer.AdvisorySensitivityProp, out var labelValue)
                    && labelValue as string == SensitivityLabel.Restricted.ToString())
                {
                    aclRestrictedItemIds.Add(itemId);
                }
                ingested++;
                Progress.AddIngested();
                Metrics.Increment("altrata_items_ingested_total");
                if (_config.GraphItemTtlDays > 0)
                    Metrics.Increment("altrata_items_expiring_total");
            }

            // Tombstone withdrawals: success removes the item from the registry;
            // failure dead-letters a delete op so retry-failed replays the DELETE.
            foreach (var result in deleteResults)
            {
                resolved.Add(result.ItemId);
                if (result.Success)
                {
                    _identity.RemoveIngestedItem(result.ItemId);
                    // A withdrawn person drops from the path index too.
                    if (_config.RelationshipPaths
                        && tombstonedPersonIds.TryGetValue(result.ItemId, out var personId))
                        _identity.RemovePersonFromPathIndex(personId);
                    deleted++;
                    Progress.AddDeleted();
                    Metrics.Increment("altrata_items_deleted_total");
                }
                else
                {
                    deadLetters.Add(new DeadLetterRecord
                    {
                        ItemId = result.ItemId,
                        Dataset = dataset,
                        DeliveryId = delivery.Id,
                        Error = result.Error ?? $"HTTP {result.Status}",
                        Op = DeadLetterOps.Delete,
                        PayloadJson = "",
                        SubjectHashes = tombstonedPersonIds.TryGetValue(result.ItemId, out var tombstonedPerson)
                            ? DeadLetterPolicy.HashSubjects(new[] { tombstonedPerson })
                            : Array.Empty<string>(),
                    });
                    Logger.Warning($"Dead-lettered tombstone {dataset} item {result.ItemId}: {result.Error}");
                }
            }

            // Compensating withdrawals for the DSAR race guard. Each record is
            // accounted as suppressed (it was consumed and must never surface);
            // a failed or interrupted withdrawal queues a replayable DELETE so
            // retry-failed completes the erasure — the item is never left both
            // live and untracked.
            if (resurrected.Count > 0)
            {
                IReadOnlyList<BatchOpResult> compensations;
                try
                {
                    compensations = await _graph.DeleteItemsBatchAsync(resurrected, ct);
                }
                catch (OperationCanceledException)
                {
                    compensations = resurrected
                        .Select(id => new BatchOpResult(id, false, 0, "graceful stop during erasure-race withdrawal"))
                        .ToList();
                }
                foreach (var result in compensations)
                {
                    if (!result.Success)
                    {
                        _state.AddDeadLetter(new DeadLetterRecord
                        {
                            ItemId = result.ItemId,
                            Dataset = dataset,
                            DeliveryId = delivery.Id,
                            Error = $"erasure-race withdrawal: {result.Error ?? $"HTTP {result.Status}"}",
                            Op = DeadLetterOps.Delete,
                            PayloadJson = "",
                            CorrelationId = CorrelationContext.Current,
                            SubjectHashes = DeadLetterPolicy.HashSubjects(
                                itemSubjects.GetValueOrDefault(result.ItemId) ?? Array.Empty<string>()),
                        });
                        Logger.Warning($"Erasure-race withdrawal dead-lettered for {result.ItemId} " +
                                       $"(delivery '{delivery.Id}'): {result.Error} — retry-failed completes the erasure.");
                    }
                    else
                    {
                        // A compensating withdrawal is part of an erasure and
                        // MUST be visible per item: this PUT landed while the
                        // subject's DSAR completed, and this DELETE undid it.
                        Logger.Warning($"Erasure-race withdrawal completed for {result.ItemId} " +
                                       $"(delivery '{delivery.Id}') — subject erased while the PUT was in flight; suppression wins.");
                    }
                    // Remove the rows journaled above (idempotent alongside the
                    // racing forget's own removal).
                    _identity.RemoveIngestedItem(result.ItemId);
                    suppressed++;
                    Progress.AddSuppressed();
                    Metrics.Increment("altrata_items_suppressed_total");
                }
                Logger.Warning($"DSAR race: withdrew {resurrected.Count} item(s) whose subject was erased " +
                               "while their PUT was in flight (suppression wins).");
            }

            // Defensive: anything the batch pipeline never reported on is a failure.
            foreach (var item in items)
            {
                if (!resolved.Contains(item.Id))
                {
                    var unaccountedSubjects = itemSubjects.GetValueOrDefault(item.Id)
                                              ?? Array.Empty<string>();
                    deadLetters.Add(new DeadLetterRecord
                    {
                        ItemId = item.Id,
                        Dataset = dataset,
                        DeliveryId = delivery.Id,
                        Error = "[Graph] item unaccounted for by the batch pipeline",
                        PayloadJson = redactPayloads ? "" : payloadJson.GetValueOrDefault(item.Id, ""),
                        Redacted = redactPayloads,
                        SubjectIds = redactPayloads ? Array.Empty<string>() : unaccountedSubjects,
                        SubjectHashes = DeadLetterPolicy.HashSubjects(unaccountedSubjects),
                    });
                }
            }
            foreach (var itemId in tombstones)
            {
                if (!resolved.Contains(itemId))
                {
                    deadLetters.Add(new DeadLetterRecord
                    {
                        ItemId = itemId,
                        Dataset = dataset,
                        DeliveryId = delivery.Id,
                        Error = "[Graph] tombstone unaccounted for by the batch pipeline",
                        Op = DeadLetterOps.Delete,
                        PayloadJson = "",
                    });
                }
            }

            if (deadLetters.Count > 0)
            {
                _state.AddDeadLetters(StampCorrelation(deadLetters));  // one lock acquisition per superchunk
                deadLettered += deadLetters.Count;
                for (var i = 0; i < deadLetters.Count; i++)
                {
                    Progress.AddFailed();
                    Metrics.Increment("altrata_items_failed_total");
                }
            }

            index = end;
        }

        // File finished: advance the checkpoint past its end.
        _state.SaveCheckpoint(new CrawlCheckpoint
        {
            DeliveryId = delivery.Id,
            Dataset = dataset,
            FileName = file.Name,
            RecordIndex = records.Count,
        });
        return (ingested, deleted, suppressed, deadLettered, false);
    }

    /// <summary>Stamp the current correlation id onto dead-letter records so a
    /// failure is followable back to the crawl that produced it.</summary>
    private static List<DeadLetterRecord> StampCorrelation(List<DeadLetterRecord> records)
    {
        for (var i = 0; i < records.Count; i++)
            records[i] = records[i] with { CorrelationId = CorrelationContext.Current };
        return records;
    }

    // ---- re-ACL pass ---------------------------------------------------------------

    /// <summary>
    /// Seat list changed: rewrite the ACL of every item whose stored ACL hash
    /// differs from the new seat hash — via the Graph $batch pipeline — then
    /// commit the hash. The hash is committed ONLY after a complete pass, so an
    /// interrupted or partially failed pass re-triggers on the next run.
    /// </summary>
    internal async Task<int> ReAclPassAsync(SeatSyncResult seatSync, CancellationToken ct)
    {
        using var reaclSpan = Telemetry.Span("seat.reacl");
        var acl = SeatAclBuilder.BuildAcl(seatSync.Seats);
        var stale = _identity.ListItemsWithAclHashOtherThan(seatSync.SeatHash);
        Telemetry.SetTag(reaclSpan, "altrata.items.count", stale.Count);
        Logger.Info($"Seat list changed — re-ACL pass over {stale.Count} item(s)");
        var updated = 0;
        var failed = 0;

        var superChunk = Math.Max(1, _config.GraphBatchSize) * Math.Max(1, _config.GraphBatchWorkers);
        for (var offset = 0; offset < stale.Count; offset += superChunk)
        {
            if (ServiceStop.Requested || ct.IsCancellationRequested)
            {
                Logger.Warning($"Re-ACL pass interrupted after {updated}/{stale.Count} items; " +
                               "hash not committed — the pass will resume next run");
                return updated;
            }

            var chunk = stale.Skip(offset).Take(superChunk).ToList();
            var updates = chunk.Select(item => new AclUpdate(item.ItemId, acl)).ToList();
            var byId = chunk.ToDictionary(item => item.ItemId, StringComparer.Ordinal);

            var results = await _graph.UpdateItemAclsBatchAsync(updates, ct);
            foreach (var result in results)
            {
                if (result.Success && byId.ContainsKey(result.ItemId))
                {
                    // UPDATE-only stamp (round-2 DSAR-race fix): if a concurrent
                    // forget-subject erased this item after the stale snapshot
                    // was taken, the row is gone and must NOT be re-inserted —
                    // an upsert here would leave a ghost inventory row claiming
                    // ownership of an item the DSAR already withdrew.
                    if (_identity.TryUpdateAclHash(result.ItemId, seatSync.SeatHash, DateTime.UtcNow))
                        updated++;
                    else
                        Logger.Info($"Re-ACL: item {result.ItemId} vanished mid-pass (erased) — not re-recorded");
                }
                else if (!result.Success)
                {
                    failed++;
                    Logger.Error($"Re-ACL failed for {result.ItemId}: {result.Error}");
                }
            }
        }

        if (failed > 0)
        {
            Logger.Warning($"Re-ACL pass incomplete: {updated} updated, {failed} FAILED; " +
                           "hash not committed — the pass re-runs next crawl");
            await _alerts.SendAsync("warning", "reacl_incomplete",
                $"Re-ACL pass left {failed} item(s) on the previous seat ACL");
            return updated;
        }

        _seats.CommitSeatHash(seatSync.SeatHash);
        Telemetry.SetTag(reaclSpan, "altrata.reacl.updated", updated);
        Telemetry.SetTag(reaclSpan, "altrata.reacl.failed", failed);
        Metrics.Increment("altrata_reacl_passes_total");
        Logger.Info($"Re-ACL pass complete: {updated} item(s) updated");
        return updated;
    }

    // ---- relationship path index ------------------------------------------------------

    /// <summary>
    /// Rebuild the persisted path index from the current feed contents and
    /// return the in-memory view for the transform join, or null when
    /// RELATIONSHIP_PATHS is off. Deterministic full rebuild: tombstoned
    /// relationship records are skipped and tombstoned persons are dropped, so
    /// the index always reflects the live feed (delta-aware, reconcilable).
    /// </summary>
    internal RelationshipPathIndex? BuildPathIndex(IReadOnlyList<Delivery> deliveries)
    {
        if (!_config.RelationshipPaths)
            return null;

        using var pathSpan = Telemetry.Span("path.index.build");
        var build = new PathIndexBuild();
        // Organizations first so BoardMembership org-id → name resolution works,
        // then relationships/board memberships/person tombstones.
        foreach (var pass in new[]
                 {
                     new[] { Datasets.Organization },
                     new[] { Datasets.RelationshipPath, Datasets.BoardMembership, Datasets.PersonProfile },
                 })
        {
            foreach (var delivery in deliveries)
            {
                try
                {
                    FeedReader.ValidateChecksums(delivery);
                    foreach (var file in delivery.Manifest.Files)
                    {
                        var dataset = Datasets.Canonical(file.Dataset);
                        if (!pass.Contains(dataset))
                            continue;
                        // Verified read: hash + parse from one open handle
                        // (see ProcessDeliveryAsync — same TOCTOU guard).
                        var records = FeedReader.ReadRecords(
                            Path.Combine(delivery.Directory, file.Name), dataset, file.Sha256);
                        PathExtractor.Accumulate(build, dataset, records);
                    }
                }
                catch (Exception exc) when (exc is ChecksumMismatchException or FileNotFoundException
                                                or FeedFileTooLargeException)
                {
                    // Rejected deliveries never feed the index (consistent with ingestion).
                    Logger.Warning($"Path index: skipping delivery '{delivery.Id}' ({exc.Message})");
                }
                catch (Exception exc) when (exc is JsonException or FormatException or NotSupportedException)
                {
                    // Same parse-diagnostic rule as ingestion: the crawl still
                    // aborts (unchanged), but the log names the delivery that
                    // fed the index a malformed file before the exception flies.
                    Logger.Error($"Path index: delivery '{delivery.Id}' failed to parse — " +
                                 $"{exc.GetType().Name}: {exc.Message}. Aborting the crawl; fix the feed file and re-run.");
                    throw;
                }
            }
        }

        _identity.ReplacePathIndex(build.Edges, build.PersonOrgs);
        foreach (var personId in build.WithdrawnPersonIds)
            _identity.RemovePersonFromPathIndex(personId);

        var (edges, orgs) = _identity.LoadPathIndex();
        Metrics.Set("altrata_path_index_edges", edges.Count);
        Telemetry.SetTag(pathSpan, "altrata.path.edges", edges.Count);
        Telemetry.SetTag(pathSpan, "altrata.path.memberships", orgs.Count);
        Logger.Info($"Relationship-path index built: {edges.Count} edge(s), {orgs.Count} membership(s)" +
                    (build.WithdrawnPersonIds.Count > 0
                        ? $", {build.WithdrawnPersonIds.Count} withdrawn person(s) dropped"
                        : ""));
        return RelationshipPathIndex.Build(edges, orgs, _config.RelationshipTopOrgs);
    }

    // ---- CRM contacts -----------------------------------------------------------------

    /// <summary>Fuzzy-tier options derived from config (review queue included).</summary>
    internal FuzzyOptions BuildFuzzyOptions() => new()
    {
        Enabled = _config.EntityFuzzyMatching,
        Threshold = _config.EntityMatchThreshold,
        ReviewFloor = _config.EntityReviewFloor,
        Review = new MatchReviewQueue(_config.ConnectorId),
    };

    /// <summary>Classification options from config (default off = no extra props).</summary>
    internal ClassificationOptions BuildClassificationOptions() => new()
    {
        Enabled = _config.Classification,
        Residency = _config.DataResidency,
        EnforceAcl = _config.ClassificationEnforceAcl,
        EnforceGroupId = _config.ClassificationEnforceGroupId,
    };

    /// <summary>Harvest a manifest entry from the item's classification props —
    /// item id, label and category labels ONLY (never a personal value).</summary>
    private static ClassificationEntry ClassificationEntryFor(ExternalItem item)
    {
        var label = item.Properties.TryGetValue(ItemTransformer.AdvisorySensitivityProp, out var l)
            ? l?.ToString() ?? SensitivityLabel.Internal.ToString()
            : SensitivityLabel.Internal.ToString();
        var categories = item.Properties.TryGetValue(ItemTransformer.DetectedCategoriesProp, out var c)
            && c is IEnumerable<string> list
            ? list.ToList()
            : new List<string>();
        return new ClassificationEntry(item.Id, label, categories);
    }

    private EntityResolver? LoadCrmContacts()
    {
        var fuzzy = BuildFuzzyOptions();
        if (string.IsNullOrEmpty(_config.CrmContactsPath))
            return new EntityResolver(_identity, fuzzy);  // use whatever is already in the store
        if (!File.Exists(_config.CrmContactsPath))
        {
            Logger.Warning($"CRM_CONTACTS_PATH '{_config.CrmContactsPath}' not found — entity resolution uses the existing store");
            return new EntityResolver(_identity, fuzzy);
        }
        using var span = Telemetry.Span("entity.load");
        IReadOnlyList<CrmContact> contacts;
        try
        {
            contacts = LoadCrmContactFile(_config.CrmContactsPath);
        }
        catch (Exception exc)
        {
            // Config-input failure → abort the crawl (unchanged: silently
            // resolving against a stale store would mislink subjects), but
            // fail fast NAMING the setting instead of a bare parse exception.
            Logger.Error($"CRM_CONTACTS_PATH '{_config.CrmContactsPath}' could not be loaded — " +
                         $"{exc.GetType().Name}: {exc.Message}. Fix or unset the file; aborting the crawl.");
            throw;
        }
        _identity.ReplaceCrmContacts(contacts);
        Telemetry.SetTag(span, "altrata.crm.contacts", contacts.Count);
        Logger.Info($"Loaded {contacts.Count} CRM contact(s) for entity resolution" +
                    (fuzzy.Enabled ? $" (fuzzy tier on, threshold {fuzzy.Threshold:0.##})" : ""));
        return new EntityResolver(_identity, fuzzy);
    }

    /// <summary>CRM contacts file: JSON array [{id,email,name,employer}] or CSV.</summary>
    internal static IReadOnlyList<CrmContact> LoadCrmContactFile(string path)
    {
        var contacts = new List<CrmContact>();
        if (Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            var rows = FeedReader.ParseCsv(File.ReadLines(path), Datasets.PersonProfile);
            foreach (var row in rows)
            {
                var id = row.Get("id") ?? row.Get("contact_id");
                if (id == null)
                    continue;
                contacts.Add(new CrmContact
                {
                    Id = id,
                    Email = row.Get("email"),
                    Name = row.Get("name") ?? row.Get("full_name"),
                    Employer = row.Get("employer") ?? row.Get("company") ?? row.Get("account_name"),
                    Role = row.Get("role") ?? row.Get("title") ?? row.Get("job_title"),
                });
            }
        }
        else
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var id = GetString(element, "id") ?? GetString(element, "contact_id");
                if (id == null)
                    continue;
                contacts.Add(new CrmContact
                {
                    Id = id,
                    Email = GetString(element, "email"),
                    Name = GetString(element, "name") ?? GetString(element, "full_name"),
                    Employer = GetString(element, "employer") ?? GetString(element, "company"),
                    Role = GetString(element, "role") ?? GetString(element, "title")
                           ?? GetString(element, "job_title"),
                });
            }
        }
        return contacts;

        static string? GetString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
