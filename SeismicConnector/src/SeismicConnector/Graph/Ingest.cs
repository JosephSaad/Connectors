// Graph/Ingest.cs
// ---------------
// The ingestion pipeline: full + incremental crawls with per-object
// checkpointing and crash-resume, the No-MNE exclusion gate, version-aware
// supersede, unpublish/expiry withdrawal, ACL resolution, $batch ingestion
// with bounded workers, dead-lettering and metrics.
//
// Crawl shape
// -----------
//   Library items    — one externalItem per (non-excluded) teamsite, when the
//                      "Library" object is enabled in config/schema.json.
//   ContentItem      — per teamsite, current published version only.
//   Withdrawal pass  — (full crawls) expired items, items no longer present
//                      in Seismic, and items whose exclusion state flipped.
//   Late-exclusion   — (incremental crawls) tracked items the incremental did
//                      NOT re-list are re-checked against the current
//                      exclusion rules (metadata-only) and withdrawn if a
//                      compliance flag landed without a modifiedAt bump.
//
// Checkpoints: SyncState.WriteCheckpoint(connectorId, sinceIso, objectKey,
// chunkIndex) after every chunk; a crash resumes at the first incomplete
// chunk of each object as long as `since` matches. A graceful stop
// (ServiceStop / Ctrl+X) finishes the in-flight chunk, flushes the pending
// Graph batch, saves the checkpoint and returns.

using System.Text.Json.Nodes;
using SeismicConnector.Config;
using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector.Graph;

/// <summary>Outcome of a permission-change re-ACL check for one item.</summary>
public enum ReAclOutcome
{
    /// <summary>The resolved ACL fingerprint matches the indexed one.</summary>
    NoChange,

    /// <summary>The item had no stored fingerprint; the ACL was applied to set a baseline.</summary>
    Baseline,

    /// <summary>A known fingerprint changed — the effective permissions drifted.</summary>
    Drifted,

    /// <summary>Permissions could not be resolved; the ACL was left unchanged (no widening).</summary>
    Unresolved,
}

/// <summary>Thread-safe crawl counters (also surfaced by the dashboard).</summary>
public sealed class IngestionStats
{
    private long _ingested;
    private long _failed;
    private long _skipped;
    private long _excluded;
    private long _withdrawn;
    private long _aclSkipped;
    private long _reAcled;
    private long _aclDrift;

    public long Ingested => Interlocked.Read(ref _ingested);
    public long Failed => Interlocked.Read(ref _failed);
    public long Skipped => Interlocked.Read(ref _skipped);
    public long Excluded => Interlocked.Read(ref _excluded);
    public long Withdrawn => Interlocked.Read(ref _withdrawn);
    public long AclSkipped => Interlocked.Read(ref _aclSkipped);
    public long ReAcled => Interlocked.Read(ref _reAcled);
    public long AclDrift => Interlocked.Read(ref _aclDrift);

    public void AddIngested(long n = 1)
    {
        Interlocked.Add(ref _ingested, n);
        Metrics.IncItemsIngested(n);
    }

    public void AddFailed(long n = 1)
    {
        Interlocked.Add(ref _failed, n);
        Metrics.IncItemsFailed(n);
    }

    public void AddSkipped(long n = 1)
    {
        Interlocked.Add(ref _skipped, n);
        Metrics.IncItemsSkipped(n);
    }

    public void AddExcluded(long n = 1) => Interlocked.Add(ref _excluded, n);

    public void AddWithdrawn(long n = 1)
    {
        Interlocked.Add(ref _withdrawn, n);
        Metrics.IncItemsDeleted(n);
    }

    public void AddAclSkipped(long n = 1) => Interlocked.Add(ref _aclSkipped, n);

    public void AddReAcled(long n = 1)
    {
        Interlocked.Add(ref _reAcled, n);
        Metrics.IncItemsReAcled(n);
    }

    public void AddAclDrift(long n = 1)
    {
        Interlocked.Add(ref _aclDrift, n);
        Metrics.IncAclDriftDetected(n);
    }

    public override string ToString() =>
        $"ingested={Ingested} failed={Failed} skipped={Skipped} excluded={Excluded} "
        + $"withdrawn={Withdrawn} acl_skipped={AclSkipped} reacled={ReAcled} acl_drift={AclDrift}";
}

/// <summary>
/// Adaptive Graph $batch concurrency: starts at the configured max, steps
/// down by one on every throttled batch (429 seen), and ramps back up by one
/// after 3 consecutive clean batches. 503s never signal throttle.
/// </summary>
internal sealed class AdaptiveConcurrency
{
    private static readonly IAppLogger Logger = Logging.GetLogger("seismic_connector.ingest");

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

    public int Current
    {
        get { lock (_lock) return _current; }
    }

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
    private static readonly IAppLogger Logger = Logging.GetLogger("seismic_connector.ingest");
    private static readonly IAppLogger Progress = Logging.GetLogger("progress");

    private readonly AppConfig _config;
    private readonly SeismicClient _seismic;
    private readonly GraphClient _graph;
    private readonly IIdentityStore _store;
    private readonly ExclusionFilter _filter;
    private readonly AclMapper _aclMapper;
    private readonly ItemTransformer _transformer;
    private readonly HaCoordinator _ha;
    private readonly ContentClassifier? _classifier;

    public IngestionStats Stats { get; }

    /// <summary>Reconciliation report for the current crawl (swapped per run).</summary>
    public ReconciliationReport Report { get; set; } = new();

    /// <summary>Classification manifest for the current crawl (swapped per run when export is on).</summary>
    public ClassificationManifest Manifest { get; set; } = new();

    /// <summary>
    /// Append-only, hash-chained audit ledger of compliance DECISIONS (No-MNE
    /// exclusions + classification ACL restrictions). Swapped per run when
    /// DECISION_LEDGER=true; a no-op in-memory ledger otherwise.
    /// </summary>
    public DecisionLedger Ledger { get; set; } = new();

    /// <summary>Current phase, surfaced on the dashboard.</summary>
    public volatile string Phase = "idle";

    public IngestPipeline(
        AppConfig config,
        SeismicClient seismic,
        GraphClient graph,
        IIdentityStore store,
        ExclusionFilter? filter = null,
        AclMapper? aclMapper = null,
        ItemTransformer? transformer = null,
        HaCoordinator? ha = null,
        IngestionStats? stats = null)
    {
        Stats = stats ?? new IngestionStats();
        _config = config;
        _seismic = seismic;
        _graph = graph;
        _store = store;
        _filter = filter ?? new ExclusionFilter(config.Exclusions);
        _aclMapper = aclMapper ?? new AclMapper(store, config.Seismic.FallbackAcl, config.Graph.TenantId);
        _transformer = transformer ?? new ItemTransformer(ttlDays: config.Seismic.GraphItemTtlDays);
        _ha = ha ?? new HaCoordinator(config.Connector.Id);
        _classifier = config.Seismic.Classification ? new ContentClassifier(config.Classification) : null;
        _concurrency = new AdaptiveConcurrency(config.Ingest.BatchWorkers);
    }

    private readonly AdaptiveConcurrency _concurrency;

    /// <summary>The HA crawl session this run participates in (set per crawl).</summary>
    private Guid _crawlId;

    /// <summary>Per-crawl usage analytics map (SEISMIC_ENRICH_USAGE); empty when disabled.</summary>
    private Dictionary<string, SeismicContentUsage> _usageByContentId = new(StringComparer.Ordinal);

    /// <summary>
    /// Fingerprint (contentId → ACL fingerprint) for items prepared this chunk,
    /// so the tracked-item upsert after a successful $batch persists the ACL
    /// fingerprint alongside the version. Populated in PrepareItemAsync,
    /// consumed in SendOneBatchAsync.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _pendingFingerprints =
        new(StringComparer.Ordinal);

    /// <summary>
    /// contentId → true when this item was ACL-locked to the classification
    /// enforcement group in ClassifyItem (CLASSIFICATION_ENFORCE_ACL). Consumed
    /// in SendOneBatchAsync so the persisted tracked item remembers the lock and
    /// the re-ACL sweep never re-widens it to source principals.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _pendingLocked =
        new(StringComparer.Ordinal);

    /// <summary>Set once a critical breaker is seen open during a crawl (degraded pause).</summary>
    private bool _degradedPause;

    /// <summary>
    /// Test seam: overrides the critical-breaker check so degraded-mode pausing
    /// can be exercised deterministically. Null in production → reads the
    /// process-wide <see cref="CircuitBreakerRegistry"/>.
    /// </summary>
    internal Func<bool>? CriticalBreakerOpenProbe { get; set; }

    private string ConnectorId => _config.Connector.Id;

    private bool StopRequested => ServiceStop.Requested;

    /// <summary>True when a critical dependency breaker is open (fail fast / pause).</summary>
    private bool CriticalBreakerOpen =>
        CriticalBreakerOpenProbe?.Invoke() ?? CircuitBreakerRegistry.AnyCriticalOpen;

    /// <summary>Pause condition at a safe boundary: graceful stop OR a critical breaker open.</summary>
    private bool ShouldPause => StopRequested || CriticalBreakerOpen;

    // ── crawls ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Run a crawl. <paramref name="fullCrawl"/> true → everything plus the
    /// withdrawal pass; false → incremental from the last sync timestamp.
    /// <paramref name="objectTypeFilter"/> restricts to one schema object
    /// (ingest-object --type). Returns false when any item dead-lettered.
    /// </summary>
    public async Task<bool> RunCrawlAsync(
        bool fullCrawl,
        string? objectTypeFilter = null,
        CancellationToken ct = default,
        bool? runWithdrawalPass = null)
    {
        Metrics.IncCrawlsStarted();
        _pendingFingerprints.Clear();
        _pendingLocked.Clear();
        _degradedPause = false;
        var crawlStartUtc = DateTime.UtcNow;
        var since = fullCrawl ? (DateTime?)null : SyncState.ReadLastSync(ConnectorId);
        var sinceIso = since?.ToString("o");
        var checkpoint = ReadResumableCheckpoint(sinceIso);
        var enabledObjects = _config.EnabledObjects
            .Where(o => objectTypeFilter is null
                || o.Equals(objectTypeFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (enabledObjects.Count == 0)
        {
            Progress.Warning(objectTypeFilter is null
                ? "No enabled objects in config/schema.json — nothing to do."
                : $"Object type '{objectTypeFilter}' is not enabled in config/schema.json.");
            return false;
        }

        // Distributed tracing: open the root crawl-cycle span and mint the
        // correlation id that flows to every child task, log, dead-letter and
        // report entry for this crawl. Inert (returns a scope with a cheap id,
        // no span) when the OTLP exporter is off.
        using var cycle = Tracing.BeginCycle(
            ConnectorId, fullCrawl ? "full" : "incremental", objectTypeFilter);
        Progress.Info(fullCrawl
            ? $"Starting FULL crawl [correlation {cycle.CorrelationId}]"
            : $"Starting INCREMENTAL crawl (since {(sinceIso ?? "beginning — no previous sync")}) "
              + $"[correlation {cycle.CorrelationId}]");

        // Fail-safe: if a critical dependency breaker is already OPEN, do not
        // start new work — pause into degraded mode and let a later cycle probe
        // for recovery. Nothing was touched, so there is no state to lose.
        if (CriticalBreakerOpen)
        {
            await EnterDegradedAsync("at crawl start").ConfigureAwait(false);
            return true;
        }

        // HA mode: open (or join) this cycle's coordinated crawl session.
        var haHandle = _ha.OpenOrJoinCrawl(fullCrawl ? "full" : "incremental", sinceIso);
        _crawlId = haHandle.CrawlId;

        // Engagement enrichment (SEISMIC_ENRICH_USAGE): one analytics fetch per
        // crawl; best-effort — a failure yields an empty map, never a crash.
        if (_config.Seismic.EnrichUsage)
        {
            Phase = "fetching usage analytics";
            _usageByContentId = await _seismic.GetContentUsageAsync(ct).ConfigureAwait(false);
        }

        Phase = "listing teamsites";
        List<SeismicTeamsite> teamsites;
        using (Tracing.StartActivity("seismic.list_teamsites"))
        {
            teamsites = await _seismic.GetTeamsitesAsync(ct).ConfigureAwait(false);
        }
        cycle.SetTag("seismic.teamsite_count", teamsites.Count);
        var teamsitesById = teamsites.ToDictionary(t => t.Id, StringComparer.Ordinal);

        var hadFailures = false;
        var stoppedEarly = false;
        // Teamsites THIS node actually crawled this cycle — the withdrawal
        // pass may only reap within them (plus teamsites gone from the source).
        var processedTeamsiteIds = new HashSet<string>(StringComparer.Ordinal);

        // 1. Library metadata items (one HA claim covers the whole object type).
        if (enabledObjects.Contains("Library", StringComparer.OrdinalIgnoreCase))
        {
            if (_ha.TryClaim(_crawlId, "object:Library"))
            {
                Phase = "ingesting libraries";
                var librariesOk = false;
                try
                {
                    using (Tracing.StartActivity("crawl.libraries"))
                    {
                        librariesOk = await IngestLibrariesAsync(teamsites, sinceIso, checkpoint, ct)
                            .ConfigureAwait(false);
                    }
                    hadFailures |= !librariesOk;
                }
                finally
                {
                    stoppedEarly = ShouldPause;
                    if (!stoppedEarly)
                        _ha.CompleteClaim(_crawlId, "object:Library", librariesOk);
                    // Graceful stop / degraded pause: leave the claim held so its
                    // heartbeat goes stale and another node (or the next start)
                    // resumes it.
                }
            }
            else if (HaCoordinator.Enabled)
            {
                Logger.Info("Library object claimed/completed by another node — skipping.");
            }
        }

        // 2. Content items per teamsite.
        if (!stoppedEarly
            && enabledObjects.Contains("ContentItem", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var teamsite in teamsites)
            {
                if (StopRequested)
                {
                    stoppedEarly = true;
                    break;
                }
                // Fail-safe: a critical breaker opened → pause at this safe
                // boundary (no new teamsite started); resume on a later cycle.
                if (CriticalBreakerOpen)
                {
                    _degradedPause = true;
                    stoppedEarly = true;
                    break;
                }

                if (_filter.IsTeamsiteExcluded(teamsite))
                {
                    await HandleExcludedTeamsiteAsync(teamsite, ct).ConfigureAwait(false);
                    continue;
                }

                var resource = $"teamsite:{teamsite.Id}";
                if (!_ha.TryClaim(_crawlId, resource))
                {
                    if (Logger.IsEnabledFor(LogLevels.Debug))
                        Logger.Debug($"Teamsite '{teamsite.Name}' claimed/completed by another node — skipping.");
                    continue;
                }
                var teamsiteOk = false;
                try
                {
                    Phase = $"crawling {teamsite.Name}";
                    using var teamsiteSpan = Tracing.StartActivity("crawl.teamsite");
                    teamsiteSpan?.SetTag("seismic.teamsite_id", teamsite.Id);
                    teamsiteSpan?.SetTag("seismic.teamsite_name", teamsite.Name);
                    processedTeamsiteIds.Add(teamsite.Id);
                    teamsiteOk = await IngestTeamsiteAsync(
                        teamsite, since, sinceIso, checkpoint, ct).ConfigureAwait(false);
                    hadFailures |= !teamsiteOk;
                }
                finally
                {
                    if (!ShouldPause)
                        _ha.CompleteClaim(_crawlId, resource, teamsiteOk);
                    // On a graceful stop / degraded pause the claim is intentionally
                    // left held — its heartbeat expires and another node reclaims.
                }

                // A breaker may have opened while this teamsite ran (its chunk
                // loop paused mid-way) — stop before the next teamsite.
                if (_degradedPause || StopRequested)
                {
                    stoppedEarly = true;
                    break;
                }
            }
        }

        // 3. Withdrawal pass — full crawls only (incrementals see a partial
        // world). Default: only unfiltered crawls run it; a sharded crawl
        // passes runWithdrawalPass=true on the shard that owns ContentItem.
        var withdrawalPass = runWithdrawalPass ?? objectTypeFilter is null;
        if (!stoppedEarly && fullCrawl && withdrawalPass)
        {
            Phase = "withdrawal pass";
            using (Tracing.StartActivity("crawl.withdrawal"))
            {
                await WithdrawalPassAsync(crawlStartUtc, teamsitesById, processedTeamsiteIds, ct)
                    .ConfigureAwait(false);
            }
        }
        else if (!stoppedEarly)
        {
            // Expiry withdrawal is cheap and safe on every crawl.
            Phase = "expiry pass";
            await WithdrawExpiredAsync(ct).ConfigureAwait(false);

            // Late-exclusion pass for incrementals: an incremental only
            // re-lists modifiedAt >= lastSync, so a compliance flag applied
            // WITHOUT bumping modifiedAt is never re-listed here and its
            // withdrawal would otherwise wait for the next FULL crawl. Re-check
            // the tracked inventory this crawl did NOT visit against the
            // current exclusion rules and withdraw newly-excluded items.
            if (!fullCrawl)
            {
                Phase = "late-exclusion pass";
                using (Tracing.StartActivity("crawl.late_exclusion"))
                {
                    await LateExclusionPassAsync(crawlStartUtc, teamsitesById, ct).ConfigureAwait(false);
                }
            }
        }

        if (stoppedEarly)
        {
            // Graceful stop OR degraded pause: the crawl session stays OPEN
            // (claims left held for reclaim); the checkpoint already covers the
            // completed chunks, so the next run resumes with no state loss.
            if (_degradedPause)
                await EnterDegradedAsync("mid-crawl").ConfigureAwait(false);
            else
                Progress.Warning(
                    "Graceful stop: checkpoint saved — the next run resumes where this one stopped. "
                    + $"[{Stats}]");
            return !hadFailures;
        }

        // Close-with-failed-claims semantics (docs/HA.md): the crawl closes even
        // when some claims failed (session status 'failed'); exactly ONE node
        // performs the close, and only that node records the sync timestamp and
        // clears the checkpoint. Single-node mode is always the closer.
        var closeResult = _ha.TryCloseCrawl(_crawlId);
        if (closeResult == HaCloseResult.ClosedByThisNode)
        {
            SyncState.WriteLastSync(ConnectorId, crawlStartUtc);
            SyncState.ClearCheckpoint(ConnectorId);
        }
        else
        {
            Logger.Info(closeResult == HaCloseResult.StillOpen
                ? $"[HA] Crawl {_crawlId} still in progress on other node(s) — sync state will be recorded by the closing node."
                : $"[HA] Crawl {_crawlId} closed by another node — skipping sync-state write.");
        }
        Metrics.IncCrawlsCompleted();
        Metrics.MarkCrawlCompletedNow();
        Metrics.SetDeadLetterDepth(SyncState.ReadFailedRecords(ConnectorId).Count);
        await Alerting.MaybeAlertDeadLetterAsync(
            ConnectorId, SyncState.ReadFailedRecords(ConnectorId).Count).ConfigureAwait(false);

        Phase = "idle";
        Progress.Info($"Crawl complete. [{Stats}]");
        return !hadFailures;
    }

    private JsonObject? ReadResumableCheckpoint(string? sinceIso)
    {
        var checkpoint = SyncState.ReadCheckpoint(ConnectorId);
        if (checkpoint is null)
            return null;
        var checkpointSince = checkpoint["since"]?.GetValue<string>();
        if (checkpointSince != sinceIso)
            return null;  // different crawl boundary — not resumable
        Progress.Info("Found matching checkpoint — resuming after the last completed chunk.");
        return checkpoint;
    }

    private static int CompletedChunks(JsonObject? checkpoint, string objectKey)
    {
        if (checkpoint?["completed"] is not JsonObject completed)
            return 0;
        return completed.TryGetPropertyValue(objectKey, out var node) && node is not null
            ? node.GetValue<int>()
            : 0;
    }

    // ── library items ────────────────────────────────────────────────────────

    private async Task<bool> IngestLibrariesAsync(
        IReadOnlyList<SeismicTeamsite> teamsites,
        string? sinceIso,
        JsonObject? checkpoint,
        CancellationToken ct)
    {
        var eligible = teamsites.Where(t => !_filter.IsTeamsiteExcluded(t)).ToList();
        var ok = true;
        var chunkIndex = 0;
        foreach (var chunk in Chunk(eligible, _config.Ingest.ChunkSize))
        {
            chunkIndex++;
            if (chunkIndex <= CompletedChunks(checkpoint, "Library"))
            {
                if (Logger.IsEnabledFor(LogLevels.Debug))
                    Logger.Debug(
                        $"Resume: Library chunk {chunkIndex} ({chunk.Count} teamsite(s)) already "
                        + "checkpointed before the pause — skipping.");
                Stats.AddSkipped(chunk.Count);
                continue;
            }

            var batchItems = new List<(string ItemId, JsonNode Item)>();
            foreach (var teamsite in chunk)
            {
                // Library items carry no item-level permissions — they take the
                // teamsite's own permissions (empty → fallback policy applies).
                var acl = _aclMapper.Resolve(Array.Empty<SeismicPermission>(), teamsite.Permissions);
                if (acl.Skipped)
                {
                    Stats.AddAclSkipped();
                    Logger.Info($"Library '{teamsite.Name}': no mappable principals; skipped (fallback=skip).");
                    continue;
                }
                // Transient identity gap (LOW-1): the teamsite HAS principals but
                // none currently maps, so a fallback=tenant everyone-entry here is
                // not a real resolution. Never (re-)PUT the library item with the
                // everyone-ACL during an identity gap — a later crawl re-resolves.
                // (A teamsite with genuinely no principals is NOT Unresolved and
                // still follows the documented tenant fallback.)
                if (acl.Unresolved)
                {
                    Stats.AddAclSkipped();
                    Logger.Info(
                        $"Library '{teamsite.Name}': principals unresolved (stale identity store) — "
                        + "not granting everyone; leaving it unchanged this crawl.");
                    continue;
                }
                batchItems.Add(($"lib-{teamsite.Id}", new JsonObject
                {
                    ["id"] = $"lib-{teamsite.Id}",
                    ["acl"] = acl.ToJsonArray(),
                    ["properties"] = new JsonObject
                    {
                        ["title"] = teamsite.Name,
                        ["contentId"] = teamsite.Id,
                        ["teamsiteId"] = teamsite.Id,
                        ["teamsiteName"] = teamsite.Name,
                        ["format"] = "library",
                        ["versionId"] = "",
                        ["distribution"] = "internal-only",
                        ["url"] = "",
                        ["ownerEmail"] = "",
                    },
                    ["content"] = new JsonObject
                    {
                        ["type"] = "text",
                        ["value"] = teamsite.Description ?? teamsite.Name,
                    },
                }));
            }

            ok &= await FlushBatchAsync(batchItems, "Library", ct).ConfigureAwait(false);
            SyncState.WriteCheckpoint(ConnectorId, sinceIso, "Library", chunkIndex);
            // Finish/flush the in-flight chunk (done above), checkpoint, THEN
            // pause on stop or an open critical breaker.
            if (StopRequested)
                return ok;
            if (CriticalBreakerOpen)
            {
                _degradedPause = true;
                return ok;
            }
        }
        return ok;
    }

    // ── content items ────────────────────────────────────────────────────────

    private async Task<bool> IngestTeamsiteAsync(
        SeismicTeamsite teamsite,
        DateTime? since,
        string? sinceIso,
        JsonObject? checkpoint,
        CancellationToken ct)
    {
        var objectKey = $"ContentItem:{teamsite.Id}";
        List<SeismicContent> contents;
        try
        {
            using (Tracing.StartActivity("seismic.list_contents"))
            {
                contents = await _seismic.GetContentsAsync(teamsite.Id, since, ct).ConfigureAwait(false);
            }
        }
        catch (CircuitOpenException ex)
        {
            // Seismic breaker opened between the boundary check and this call —
            // pause; the teamsite's claim is left held for the next cycle.
            Logger.Warning(
                $"Teamsite '{teamsite.Name}' ({teamsite.Id}): listing short-circuited — breaker "
                + $"'{ex.BreakerName}' is open. Pausing the crawl at this boundary; the claim stays "
                + "held and a later cycle resumes this teamsite.");
            _degradedPause = true;
            return true;
        }
        catch (SeismicApiError ex)
        {
            Logger.Error(
                $"Teamsite '{teamsite.Name}' ({teamsite.Id}): listing failed — {ex.Message}. "
                + "Dead-lettered as Teamsite; retry-failed re-probes it and the next crawl re-lists.");
            SyncState.AppendFailedRecords(
                SyncState.FailedRecordsPath(ConnectorId),
                new[] { teamsite.Id },
                "Teamsite",
                $"listing failed: {ex.Message}");
            Stats.AddFailed();
            return false;
        }

        Logger.Info($"Teamsite '{teamsite.Name}': {contents.Count} item(s) to consider.");
        var ok = true;
        var chunkIndex = 0;
        var nowUtc = DateTime.UtcNow;

        foreach (var chunk in Chunk(contents, _config.Ingest.ChunkSize))
        {
            chunkIndex++;
            var completedChunk = chunkIndex <= CompletedChunks(checkpoint, objectKey);
            // Resume diagnosability: say ONCE per completed chunk that its items
            // are being reconciled (not re-ingested) so an operator reading the
            // log understands why a resumed crawl PUTs far fewer items.
            if (completedChunk && Logger.IsEnabledFor(LogLevels.Debug))
                Logger.Debug(
                    $"Resume: chunk {chunkIndex} of {objectKey} already checkpointed — reconciling its "
                    + $"{chunk.Count} item(s) against the fresh listing (tracked items re-decided, "
                    + "untracked trusted to the checkpoint).");

            var batchItems = new List<(string ItemId, JsonNode Item)>();
            foreach (var content in chunk)
            {
                ct.ThrowIfCancellationRequested();

                // Resume reconcile: this chunk's Graph work completed before the
                // pause, but the world may have moved while we were stopped.
                //   * Untracked items are trusted to the checkpoint (already
                //     sent; nothing to reconcile against) — never re-PUT.
                //   * Tracked items are re-decided from the FRESH listing via
                //     PrepareItemAsync: an unchanged version is a cheap
                //     last-seen touch (so the withdrawal reaper cannot eat
                //     completed work), a changed version re-ingests (no stale
                //     version survives a resume), and a newly-excluded /
                //     unpublished / expired item is withdrawn (a compliance
                //     flag that lands mid-pause is never lost to a stale
                //     checkpoint).
                if (completedChunk && _store.GetTrackedItem(content.Id) is null)
                {
                    Stats.AddSkipped();
                    continue;
                }

                var outcome = await PrepareItemAsync(content, teamsite, nowUtc, ct).ConfigureAwait(false);
                if (outcome is not null)
                    batchItems.Add(outcome.Value);
            }

            // Even on a graceful stop / degraded pause the in-flight chunk is
            // flushed and checkpointed before returning (see ServiceStop
            // semantics) — so no in-flight work is lost across the transition.
            ok &= await FlushBatchAsync(batchItems, "ContentItem", ct, chunk, teamsite).ConfigureAwait(false);
            if (!completedChunk)
                SyncState.WriteCheckpoint(ConnectorId, sinceIso, objectKey, chunkIndex);
            _ha.Heartbeat(_crawlId, $"teamsite:{teamsite.Id}");
            if (StopRequested)
                return ok;
            // Fail-safe: a critical breaker (e.g. Graph) opened while ingesting
            // this teamsite — stop preparing further chunks; the checkpoint we
            // just wrote lets the next cycle resume exactly here.
            if (CriticalBreakerOpen)
            {
                _degradedPause = true;
                return ok;
            }
        }
        return ok;
    }

    /// <summary>
    /// Decide what to do with one content item: exclusion → (maybe) withdraw;
    /// unpublished/expired → withdraw; unchanged version → mark seen; else
    /// build the externalItem for the batch.
    /// </summary>
    internal async Task<(string ItemId, JsonNode Item)?> PrepareItemAsync(
        SeismicContent content,
        SeismicTeamsite? teamsite,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var tracked = _store.GetTrackedItem(content.Id);

        // 1. No-MNE exclusion gate — evaluated before ANYTHING is fetched.
        var decision = _filter.Evaluate(content, teamsite);
        if (decision.Excluded)
        {
            Stats.AddExcluded();
            Report.RecordExcluded(content.Id, content.TeamsiteId, decision.Rule!, decision.Reason!);
            Ledger.Append(content.Id, DecisionLedger.DecisionExclude, $"{decision.Rule}: {decision.Reason}");
            Logger.Info($"EXCLUDED {content.Id} ({decision.Rule}): {decision.Reason}");
            if (tracked?.Status == "ingested")
            {
                // Late flag: previously ingested, now excluded → withdraw.
                await WithdrawItemAsync(content.Id, decision.Rule!, $"late exclusion: {decision.Reason}", ct)
                    .ConfigureAwait(false);
            }
            _store.UpsertTrackedItem(new TrackedItem(
                content.Id, content.CurrentVersionId, content.TeamsiteId,
                content.ExpiresAt?.ToUniversalTime(), nowUtc, "excluded"));
            return null;
        }

        // 2. Unpublished / no published version → withdraw if we had it.
        if (!content.Status.Equals("published", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(content.CurrentVersionId))
        {
            if (tracked?.Status == "ingested")
            {
                await WithdrawItemAsync(content.Id, "unpublished",
                    $"content status is '{content.Status}'", ct).ConfigureAwait(false);
                _store.RemoveTrackedItem(content.Id);
            }
            return null;
        }

        // 3. Compliance expiry — never ingest expired content.
        if (content.ExpiresAt is not null && content.ExpiresAt.Value.ToUniversalTime() <= nowUtc)
        {
            if (tracked?.Status == "ingested")
            {
                await WithdrawItemAsync(content.Id, "expired",
                    $"content expired at {content.ExpiresAt:o}", ct).ConfigureAwait(false);
                _store.RemoveTrackedItem(content.Id);
            }
            return null;
        }

        // 4. Version-aware supersede: unchanged published version → touch only.
        if (tracked is { Status: "ingested" } && tracked.VersionId == content.CurrentVersionId)
        {
            Stats.AddSkipped();
            // Permission-change re-ACL (PERMISSION_REACL): the content payload is
            // unchanged, but the resolved permission set may have drifted (a
            // teamsite/content-profile change). Re-resolve and, if the ACL
            // fingerprint changed, refresh the ACL WITHOUT re-sending content.
            if (_config.Seismic.PermissionReacl)
            {
                await ReAclIfDriftedAsync(content, teamsite, tracked, nowUtc, repair: true, ct).ConfigureAwait(false);
            }
            else
            {
                _store.UpsertTrackedItem(tracked with { LastSeenUtc = nowUtc });
            }
            // isEnabledFor gate (hot path): this fires once per unchanged item
            // on every crawl — never build the message unless DEBUG is live.
            if (Logger.IsEnabledFor(LogLevels.Debug))
                Logger.Debug($"SKIP {content.Id}: version {content.CurrentVersionId} unchanged");
            return null;
        }

        // 5. Resolve ACL (item permissions win; empty item permissions inherit
        // the teamsite's — everyone with access to the library sees its content).
        var acl = _aclMapper.Resolve(content.Permissions, teamsite?.Permissions);

        // Transient identity gap FIRST — before the genuine-skip branch below.
        // The source HAS principals but none currently maps (stale/partial
        // identity store), so any fallback here is NOT a real resolution:
        //   * fallback=tenant → the entries are the tenant-everyone fallback;
        //     PUTting/re-ACLing to it would widen a restricted item tenant-wide
        //     (LOW-1: this must hold for a brand-NEW or LIBRARY item too, not
        //     only an already-ingested one);
        //   * fallback=skip → the genuine-skip branch would WITHDRAW an
        //     already-ingested item, then re-ingest it when identity recovers
        //     (LOW-2: needless withdraw/re-ingest churn).
        // Either way: leave a tracked item exactly as-is and do NOT ingest a new
        // one. A later crawl re-resolves once the identity store recovers.
        // (Genuinely-no-principals content is NOT Unresolved — it falls through
        // to the documented fallback policy below.)
        if (acl.Unresolved)
        {
            Stats.AddAclSkipped();
            Logger.Warning(
                $"Item {content.Id} ('{content.Name}'): principals unresolved (stale identity store) — "
                + "leaving it unchanged (no widening, no withdrawal; the source's principals are not "
                + "currently mappable to Entra). A later crawl re-resolves once identity recovers.");
            if (tracked is { Status: "ingested" })
                _store.UpsertTrackedItem(tracked with { LastSeenUtc = nowUtc });
            return null;
        }

        if (acl.Skipped)
        {
            // Genuinely no mappable principals AND fallback=skip: not indexable.
            Stats.AddAclSkipped();
            Logger.Warning(
                $"Item {content.Id} ('{content.Name}'): no Seismic principal mapped to Entra; "
                + "skipped (SEISMIC_FALLBACK_ACL=skip).");
            if (tracked?.Status == "ingested")
            {
                await WithdrawItemAsync(content.Id, "acl-unmappable",
                    "no principal could be mapped to Entra ID", ct).ConfigureAwait(false);
                _store.RemoveTrackedItem(content.Id);
            }
            return null;
        }

        // 6. Download + transform (current published version only).
        byte[]? payload = null;
        if (CompositeExtractor.Default.CanExtract(content.Format))
        {
            using var extractSpan = Tracing.StartActivity("content.extract");
            extractSpan?.SetTag("seismic.content_id", content.Id);
            extractSpan?.SetTag("seismic.format", content.Format);
            payload = await _seismic.DownloadContentAsync(content.TeamsiteId, content.Id, ct)
                .ConfigureAwait(false);
        }
        _usageByContentId.TryGetValue(content.Id, out var usage);

        // LiveDoc field/variable enrichment — the extra API call is gated so
        // it fires ONLY for LiveDoc items and ONLY when the feature is enabled
        // (non-LiveDoc items and disabled config incur zero new calls).
        List<SeismicLiveDocField>? liveDocFields = null;
        if (_config.Seismic.LiveDocFieldIndexing && content.IsLiveDoc)
        {
            liveDocFields = await _seismic.GetLiveDocFieldsAsync(content.TeamsiteId, content.Id, ct)
                .ConfigureAwait(false);
            Metrics.RecordExtraction("livedoc-fields", success: liveDocFields.Count > 0);
        }

        // Remember the resolved ACL fingerprint so the post-$batch tracked-item
        // upsert persists it (powers later permission-change detection).
        _pendingFingerprints[content.Id] = acl.Fingerprint();

        using var transformSpan = Tracing.StartActivity("item.transform");
        transformSpan?.SetTag("seismic.content_id", content.Id);
        var item = _transformer.Transform(content, teamsite, payload, acl, usage, liveDocFields);

        // Data classification & sensitivity TAGGING (CLASSIFICATION): runs over
        // the FINAL indexed text and folds the item's distribution restriction
        // into a unified taxonomy tag. The tag is ADVISORY (a connector-applied
        // classification, NOT a Purview-enforced label). Only ever applied to
        // content that IS ingested — the No-MNE exclusion above already dropped
        // excluded items.
        if (_classifier is not null)
            ClassifyItem(content, item);

        return (content.Id, item);
    }

    /// <summary>
    /// Classify one prepared item and stamp the advisory sensitivity properties
    /// + metrics/manifest. When CLASSIFICATION_ENFORCE_ACL is on, a top-tier
    /// (Restricted) tag additionally RESTRICTS the item's Graph ACL to the
    /// configured Entra group (advisory tag → real access control) and records
    /// the restriction in the decision ledger.
    /// </summary>
    private void ClassifyItem(SeismicContent content, JsonNode item)
    {
        using var span = Tracing.StartActivity("item.classify");
        var text = item["content"]?["value"]?.GetValue<string>() ?? string.Empty;
        var result = _classifier!.Classify(text, content.Distribution);

        var properties = item["properties"]!.AsObject();
        properties["sensitivityLabel"] = result.Label.ToString();
        properties["detectedCategories"] = new JsonArray(
            result.Categories.Select(c => (JsonNode)c).ToArray());

        span?.SetTag("classification.label", result.Label.ToString());
        span?.SetTag("classification.category_count", result.Categories.Count);
        Metrics.RecordClassification(result.Label.ToString().ToLowerInvariant(), result.Categories);
        Manifest.RecordItem(content.Id, content.TeamsiteId, result.Label, result.Categories);

        // Optional enforcement (default OFF): lock a Restricted item down to the
        // configured Entra group at the Graph ACL. Config load guarantees the
        // group is present whenever the flag is on.
        if (_config.Seismic.ClassificationEnforceAcl
            && result.Label == SensitivityLabel.Restricted
            && !string.IsNullOrWhiteSpace(_config.Seismic.ClassificationEnforceGroup))
        {
            var group = _config.Seismic.ClassificationEnforceGroup!;
            var enforced = EnforcedGroupAcl(group);
            item["acl"] = enforced.ToJsonArray();
            // Persist the APPLIED (group) fingerprint and mark the item locked so
            // the tracked-item upsert after $batch records both. The re-ACL sweep
            // then keeps the item locked to the group instead of re-widening it to
            // the resolved source principals on a later source-permission drift.
            _pendingFingerprints[content.Id] = enforced.Fingerprint();
            _pendingLocked[content.Id] = true;
            Ledger.Append(
                content.Id,
                DecisionLedger.DecisionAclRestrict,
                $"classification={result.Label}; ACL restricted to Entra group {group}");
            span?.SetTag("classification.acl_restricted", true);
            Logger.Info(
                $"ACL-RESTRICT {content.Id}: Restricted tag → ACL locked to Entra group {group} "
                + "(CLASSIFICATION_ENFORCE_ACL).");
        }
    }

    /// <summary>The single-group ACL a classification-locked (Restricted) item is
    /// restricted to under CLASSIFICATION_ENFORCE_ACL.</summary>
    private static AclResult EnforcedGroupAcl(string group) =>
        new(new[] { AclEntry.GrantGroup(group) }, UnmappedPrincipals: 0, Skipped: false);

    /// <summary>
    /// PUT a prepared batch through the $batch retry ladder, dispatching
    /// sub-batches in adaptive windows: the window width follows
    /// AdaptiveConcurrency (dialled 1..GRAPH_BATCH_WORKERS by 429 feedback).
    /// Returns false when any item failed permanently.
    /// </summary>
    private async Task<bool> FlushBatchAsync(
        IReadOnlyList<(string ItemId, JsonNode Item)> batchItems,
        string objectType,
        CancellationToken ct,
        IReadOnlyList<SeismicContent>? sourceChunk = null,
        SeismicTeamsite? teamsite = null)
    {
        if (batchItems.Count == 0)
            return true;

        var contentById = sourceChunk?.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var nowUtc = DateTime.UtcNow;
        var ok = true;
        var batches = Chunk(batchItems, _config.Ingest.GraphBatchSize).ToList();

        // Adaptive window dispatch: take Current sub-batches at a time so a
        // throttled window immediately narrows the next one.
        var i = 0;
        while (i < batches.Count)
        {
            ct.ThrowIfCancellationRequested();
            var workers = _concurrency.Current;
            var window = batches.Skip(i).Take(workers).ToList();
            var tasks = window
                .Select(batch => SendOneBatchAsync(batch, objectType, contentById, teamsite, nowUtc, ct))
                .ToList();
            foreach (var result in await Task.WhenAll(tasks).ConfigureAwait(false))
                ok &= result;
            i += window.Count;
        }
        return ok;
    }

    private async Task<bool> SendOneBatchAsync(
        IReadOnlyList<(string ItemId, JsonNode Item)> batch,
        string objectType,
        Dictionary<string, SeismicContent>? contentById,
        SeismicTeamsite? teamsite,
        DateTime nowUtc,
        CancellationToken ct)
    {
        using var batchSpan = Tracing.StartActivity("graph.batch_ingest");
        batchSpan?.SetTag("graph.batch_size", batch.Count);
        batchSpan?.SetTag("seismic.object_type", objectType);
        IReadOnlyList<BatchItemResult> results;
        try
        {
            var outcome = await _graph.PutExternalItemsBatchWithRetryAsync(ConnectorId, batch, ct)
                .ConfigureAwait(false);
            results = outcome.Results;
            // Adaptive concurrency feedback: 429s narrow the window, clean
            // batches widen it again (503s deliberately signal nothing).
            if (outcome.SawThrottle)
                _concurrency.OnThrottle();
            else
                _concurrency.OnSuccess();
        }
        catch (Exception ex) when (ex is GraphApiError or CircuitOpenException)
        {
            // Whole envelope failed after retries, or the Graph breaker is open
            // (fail-fast) — dead-letter every item so it is re-drivable, and let
            // the chunk-boundary check pause the crawl into degraded mode. No
            // data is lost: the dead-letter queue and checkpoint are consistent.
            if (ex is CircuitOpenException)
                _degradedPause = true;
            Logger.Error(
                $"Graph $batch envelope failed for {batch.Count} {objectType} item(s)"
                + (teamsite is null ? "" : $" in teamsite '{teamsite.Name}' ({teamsite.Id})")
                + $" [{string.Join(", ", batch.Take(3).Select(b => b.ItemId))}"
                + (batch.Count > 3 ? ", ..." : "") + $"] — {ex.Message} "
                + (ex is CircuitOpenException
                    ? "All items dead-lettered; the crawl pauses into degraded mode and retry-failed re-drives them."
                    : "All items dead-lettered for retry-failed."));
            var requestBodies = batch.ToDictionary(
                b => b.ItemId, b => (JsonNode?)b.Item.DeepClone());
            SyncState.AppendFailedRecords(
                SyncState.FailedRecordsPath(ConnectorId),
                batch.Select(b => b.ItemId).ToList(),
                objectType,
                ex.Message,
                requestBodies);
            Stats.AddFailed(batch.Count);
            return false;
        }

        var ok = true;
        foreach (var result in results)
        {
            if (result.Success)
            {
                Stats.AddIngested();
                if (contentById is not null && contentById.TryGetValue(result.ItemId, out var content))
                {
                    _pendingFingerprints.TryRemove(content.Id, out var fingerprint);
                    _pendingLocked.TryRemove(content.Id, out var locked);
                    _store.UpsertTrackedItem(new TrackedItem(
                        content.Id, content.CurrentVersionId, content.TeamsiteId,
                        content.ExpiresAt?.ToUniversalTime(), nowUtc, "ingested")
                    {
                        AclFingerprint = fingerprint,
                        ClassificationLocked = locked,
                    });
                }
            }
            else
            {
                ok = false;
                Stats.AddFailed();
                // Per-item failure inside a 2xx envelope: name the item, its
                // teamsite and the Graph error so the operator can act on THIS
                // subject; the full request/response bodies go to dead-letter.
                Logger.Error(
                    $"Ingest failed for {objectType} {result.ItemId}"
                    + (teamsite is null ? "" : $" (teamsite '{teamsite.Name}' {teamsite.Id})")
                    + $": HTTP {result.Status} {result.Error ?? "(no error body)"} — "
                    + "dead-lettered with request/response for retry-failed.");
                var request = batch.FirstOrDefault(b => b.ItemId == result.ItemId).Item;
                SyncState.AppendFailedRecords(
                    SyncState.FailedRecordsPath(ConnectorId),
                    new List<(string, string)> { (result.ItemId, result.Error ?? $"HTTP {result.Status}") },
                    objectType,
                    requestBodies: request is null
                        ? null
                        : new Dictionary<string, JsonNode?> { [result.ItemId] = request.DeepClone() },
                    responseBodies: result.ResponseBody is null
                        ? null
                        : new Dictionary<string, JsonNode?> { [result.ItemId] = result.ResponseBody });
            }
        }
        return ok;
    }

    // ── withdrawal ───────────────────────────────────────────────────────────

    private async Task HandleExcludedTeamsiteAsync(SeismicTeamsite teamsite, CancellationToken ct)
    {
        Report.RecordExcluded(teamsite.Id, teamsite.Id, "restricted-library",
            $"whole teamsite '{teamsite.Name}' is excluded by configuration");
        Stats.AddExcluded();
        Logger.Info($"Teamsite '{teamsite.Name}' is excluded — withdrawing any previously ingested items.");

        // Late library-level flag: withdraw everything we ever ingested from it.
        foreach (var tracked in _store.GetAllTrackedItems()
                     .Where(t => t.TeamsiteId == teamsite.Id && t.Status == "ingested"))
        {
            await WithdrawItemAsync(tracked.ItemId, "restricted-library",
                $"teamsite '{teamsite.Name}' became restricted", ct).ConfigureAwait(false);
            _store.UpsertTrackedItem(tracked with { Status = "excluded", LastSeenUtc = DateTime.UtcNow });
        }
    }

    private async Task WithdrawalPassAsync(
        DateTime crawlStartUtc,
        IReadOnlyDictionary<string, SeismicTeamsite> teamsitesById,
        IReadOnlySet<string> processedTeamsiteIds,
        CancellationToken ct)
    {
        await WithdrawExpiredAsync(ct).ConfigureAwait(false);

        // Items no longer present in Seismic (deleted / moved / unpublished).
        var outOfReapScope = 0;
        foreach (var tracked in _store.GetItemsNotSeenSince(crawlStartUtc))
        {
            // Reap only what THIS crawl can vouch for:
            //   * the teamsite was processed by this node → the item truly
            //     vanished from a listing we just walked;
            //   * the teamsite is absent from the source listing entirely →
            //     the whole library is gone and its items go with it.
            // A teamsite that is listed but was NOT processed here — another
            // HA node's claim, a claim-blocked resource, or an excluded
            // teamsite (handled by its own withdrawal path) — is out of
            // scope: its items' last-seen belongs to whoever owns it.
            var teamsiteGone = !teamsitesById.ContainsKey(tracked.TeamsiteId);
            if (!teamsiteGone && !processedTeamsiteIds.Contains(tracked.TeamsiteId))
            {
                outOfReapScope++;
                if (Logger.IsEnabledFor(LogLevels.Debug))
                    Logger.Debug(
                        $"Reap-scope: stale item {tracked.ItemId} left alone — teamsite "
                        + $"{tracked.TeamsiteId} is listed but was not processed by this node "
                        + "(owned/claimed elsewhere); its owner's crawl decides.");
                continue;
            }
            if (teamsiteGone && Logger.IsEnabledFor(LogLevels.Debug))
                Logger.Debug(
                    $"Reap: item {tracked.ItemId} withdrawn because its whole teamsite "
                    + $"{tracked.TeamsiteId} is gone from the Seismic listing.");
            await WithdrawItemAsync(tracked.ItemId, "not-in-source",
                "content no longer present/published in Seismic", ct).ConfigureAwait(false);
            _store.RemoveTrackedItem(tracked.ItemId);
        }
        // One summary line (not per item) so multi-node runs can explain why
        // stale items survived this node's withdrawal pass.
        if (outOfReapScope > 0)
            Logger.Info(
                $"Withdrawal pass: {outOfReapScope} stale tracked item(s) in teamsites this node did "
                + "not process — out of reap scope here; the owning node's crawl reaps them.");
    }

    /// <summary>
    /// Incremental-crawl backstop for late exclusion flags (No-MNE): re-check
    /// every tracked INGESTED item the incremental did not re-list (its
    /// last-seen predates this crawl) against the current exclusion rules,
    /// re-listing each affected teamsite metadata-only (no downloads), and
    /// withdraw items that are now excluded. Items missing from the source
    /// listing are left to the full crawl's not-in-source reaper (an
    /// incremental sees a partial world and must not over-withdraw).
    /// </summary>
    internal async Task LateExclusionPassAsync(
        DateTime crawlStartUtc,
        IReadOnlyDictionary<string, SeismicTeamsite> teamsitesById,
        CancellationToken ct)
    {
        var staleByTeamsite = _store.GetAllTrackedItems()
            .Where(t => t.Status == "ingested" && t.LastSeenUtc < crawlStartUtc)
            .GroupBy(t => t.TeamsiteId, StringComparer.Ordinal);

        foreach (var group in staleByTeamsite)
        {
            if (ShouldPause)
                return;
            // Teamsites not visible to this crawl (HA: owned by another node)
            // are out of scope; wholly-excluded teamsites were already handled
            // by HandleExcludedTeamsiteAsync earlier in this crawl.
            if (!teamsitesById.TryGetValue(group.Key, out var teamsite)
                || _filter.IsTeamsiteExcluded(teamsite))
            {
                continue;
            }

            List<SeismicContent> contents;
            try
            {
                contents = await _seismic.GetContentsAsync(teamsite.Id, null, ct).ConfigureAwait(false);
            }
            catch (CircuitOpenException ex)
            {
                // Seismic breaker opened mid-pass: abandon the REST of the pass
                // (no withdrawals were decided for unvisited teamsites, so
                // nothing is lost) and let the next crawl re-check them.
                Logger.Warning(
                    $"Late-exclusion pass paused at teamsite '{teamsite.Name}' ({teamsite.Id}): breaker "
                    + $"'{ex.BreakerName}' is open — remaining tracked items are re-checked next crawl.");
                _degradedPause = true;
                return;
            }
            catch (SeismicApiError ex)
            {
                Logger.Warning(
                    $"Late-exclusion pass: listing teamsite '{teamsite.Name}' ({teamsite.Id}) failed "
                    + $"({ex.Message}) — its items will be re-checked on the next crawl.");
                continue;
            }
            var contentById = contents.ToDictionary(c => c.Id, StringComparer.Ordinal);

            foreach (var tracked in group)
            {
                ct.ThrowIfCancellationRequested();
                if (!contentById.TryGetValue(tracked.ItemId, out var content))
                    continue;  // not listed — the full crawl's reaper owns that case
                var decision = _filter.Evaluate(content, teamsite);
                if (!decision.Excluded)
                    continue;
                Stats.AddExcluded();
                Report.RecordExcluded(content.Id, content.TeamsiteId, decision.Rule!, decision.Reason!);
                await WithdrawItemAsync(content.Id, decision.Rule!,
                    $"late exclusion (incremental): {decision.Reason}", ct).ConfigureAwait(false);
                _store.UpsertTrackedItem(tracked with { Status = "excluded", LastSeenUtc = DateTime.UtcNow });
            }
        }
    }

    private async Task WithdrawExpiredAsync(CancellationToken ct)
    {
        foreach (var tracked in _store.GetExpiredItems(DateTime.UtcNow))
        {
            await WithdrawItemAsync(tracked.ItemId, "expired",
                $"content expired at {tracked.ExpiresAtUtc:o}", ct).ConfigureAwait(false);
            _store.RemoveTrackedItem(tracked.ItemId);
        }
    }

    /// <summary>DELETE one externalItem and record the withdrawal.</summary>
    internal async Task WithdrawItemAsync(string itemId, string rule, string reason, CancellationToken ct)
    {
        try
        {
            await _graph.DeleteExternalItemAsync(ConnectorId, itemId, ct).ConfigureAwait(false);
            Stats.AddWithdrawn();
            Report.RecordWithdrawn(itemId, null, rule, reason);
            Logger.Info($"WITHDRAWN {itemId} ({rule}): {reason}");
        }
        catch (GraphApiError ex)
        {
            // The item STAYS in the index until the retry succeeds — say so,
            // with the withdrawal's own rule/reason as the subject context.
            Logger.Error(
                $"WITHDRAW FAILED {itemId} ({rule}: {reason}) — {ex.Message}. Item remains in the "
                + "index; dead-lettered as Withdrawal so retry-failed re-attempts the delete.");
            Stats.AddFailed();
            SyncState.AppendFailedRecords(
                SyncState.FailedRecordsPath(ConnectorId),
                new List<(string, string)> { (itemId, $"withdraw failed: {ex.Message}") },
                "Withdrawal");
        }
    }

    // ── degraded mode ────────────────────────────────────────────────────────

    /// <summary>
    /// Enter degraded mode: log it, count it, and raise a (best-effort) alert.
    /// The crawl has already paused at a safe checkpoint boundary; nothing is
    /// lost because completed chunks are checkpointed and any in-flight failures
    /// are dead-lettered. A later cycle probes the half-open breaker and resumes.
    /// </summary>
    private async Task EnterDegradedAsync(string where)
    {
        _degradedPause = true;
        Metrics.IncDegradedPauses();
        var open = string.Join(", ", CircuitBreakerRegistry.OpenCriticalNames);
        if (string.IsNullOrEmpty(open))
            open = "(test probe)";
        Progress.Warning(
            $"DEGRADED MODE ({where}): critical dependency breaker open [{open}] — paused at a safe "
            + $"checkpoint boundary; the next cycle probes for recovery and resumes. [{Stats}]");
        await Alerting.RaiseAsync(
            "degraded_mode",
            $"Connector '{ConnectorId}' paused into degraded mode ({where}); open breakers: {open}",
            new Dictionary<string, object?> { ["open_breakers"] = CircuitBreakerRegistry.OpenCriticalNames })
            .ConfigureAwait(false);
    }

    // ── permission-change re-ACL ─────────────────────────────────────────────

    /// <summary>
    /// Resolve <paramref name="content"/>'s current permissions and compare the
    /// ACL fingerprint against the tracked item's. Compliance-safe: when the
    /// permissions cannot be resolved — the fallback would skip the item, OR
    /// the source has principals but none currently maps (a stale identity
    /// store; with SEISMIC_FALLBACK_ACL=tenant the resolver hands back the
    /// tenant-everyone fallback) — the existing ACL is LEFT UNCHANGED: a
    /// re-ACL never widens access on a resolution failure. Otherwise:
    /// <list type="bullet">
    ///   <item><b>NoChange</b> — fingerprint matches; nothing to do.</item>
    ///   <item><b>Baseline</b> — the item had no stored fingerprint (ingested
    ///     before re-ACL support); the ACL is (re)applied to establish the
    ///     baseline. Counted as a re-ACL, not as drift.</item>
    ///   <item><b>Drifted</b> — a known fingerprint changed; with
    ///     <paramref name="repair"/> the ACL is PATCHed (no content re-send)
    ///     and the fingerprint updated. Counted as drift and (if repaired) a
    ///     re-ACL.</item>
    /// </list>
    /// </summary>
    internal async Task<ReAclOutcome> ReAclIfDriftedAsync(
        SeismicContent content, SeismicTeamsite? teamsite, TrackedItem tracked, DateTime nowUtc,
        bool repair, CancellationToken ct)
    {
        // Classification lock (CLASSIFICATION_ENFORCE_ACL): an item that
        // classified as Restricted was locked to the enforcement group at ingest.
        // Re-ACL resolves the SOURCE principal set; applying it here would
        // silently RE-WIDEN a compliance-locked item on a source-permission
        // drift. Keep it locked to the group and never PATCH it to source — the
        // lock is the whole point of the control. (Content changes that alter the
        // classification are re-evaluated on the full-crawl transform path, which
        // resets the lock; re-ACL only touches unchanged content.)
        if (tracked.ClassificationLocked
            && _config.Seismic.ClassificationEnforceAcl
            && !string.IsNullOrWhiteSpace(_config.Seismic.ClassificationEnforceGroup))
        {
            return await ReAclLockedAsync(content, tracked, nowUtc, repair, ct).ConfigureAwait(false);
        }

        var acl = _aclMapper.Resolve(content.Permissions, teamsite?.Permissions);
        if (acl.Skipped || acl.Unresolved)
        {
            // Compliance-safe: do NOT widen the ACL when principals can't be
            // resolved. This covers BOTH fallback policies — skip (no entries)
            // AND tenant (whose everyone-entry here is only the fallback for a
            // transient identity gap, not a real resolution; PATCHing it would
            // re-ACL a restricted item tenant-wide). Leave the indexed ACL
            // as-is and log.
            Logger.Warning(
                $"Re-ACL {content.Id} ('{content.Name}'): permissions unresolved — leaving the existing "
                + "ACL unchanged (no widening).");
            _store.UpsertTrackedItem(tracked with { LastSeenUtc = nowUtc });
            return ReAclOutcome.Unresolved;
        }

        var fingerprint = acl.Fingerprint();
        if (tracked.AclFingerprint == fingerprint)
        {
            _store.UpsertTrackedItem(tracked with { LastSeenUtc = nowUtc });
            return ReAclOutcome.NoChange;
        }

        var isBaseline = tracked.AclFingerprint is null;
        if (!isBaseline)
        {
            Stats.AddAclDrift();
            Logger.Info(
                $"ACL DRIFT {content.Id}: fingerprint {tracked.AclFingerprint} -> {fingerprint} "
                + $"({acl.Entries.Count} principal(s))");
        }

        if (repair)
        {
            try
            {
                await _graph.PatchExternalItemAclAsync(ConnectorId, content.Id, acl.ToJsonArray(), ct)
                    .ConfigureAwait(false);
                Stats.AddReAcled();
                _store.UpsertTrackedItem(tracked with
                {
                    AclFingerprint = fingerprint,
                    LastSeenUtc = nowUtc,
                    // Reaching the source-based path means the item is NOT
                    // classification-locked (or enforcement was disabled); clear
                    // any stale lock so state matches the applied source ACL.
                    ClassificationLocked = false,
                });
            }
            catch (GraphApiError ex)
            {
                // The indexed ACL still reflects the OLD principal set — flag it
                // loudly (permissions are compliance-relevant) and dead-letter.
                Logger.Error(
                    $"RE-ACL FAILED {content.Id} ('{content.Name}', teamsite {content.TeamsiteId}): "
                    + $"fingerprint {tracked.AclFingerprint ?? "(none)"} -> {fingerprint} not applied — "
                    + $"{ex.Message}. The indexed ACL is unchanged; dead-lettered as ReAcl for retry-failed.");
                Stats.AddFailed();
                SyncState.AppendFailedRecords(
                    SyncState.FailedRecordsPath(ConnectorId),
                    new List<(string, string)> { (content.Id, $"re-acl failed: {ex.Message}") },
                    "ReAcl");
                return isBaseline ? ReAclOutcome.Baseline : ReAclOutcome.Drifted;
            }
        }
        else
        {
            // Audit-only (dry run): still bump last-seen so the item isn't reaped.
            _store.UpsertTrackedItem(tracked with { LastSeenUtc = nowUtc });
        }

        return isBaseline ? ReAclOutcome.Baseline : ReAclOutcome.Drifted;
    }

    /// <summary>
    /// Re-ACL path for a classification-locked item: the target ACL is ALWAYS the
    /// enforcement group, never the resolved source principals — so a
    /// source-permission drift can never re-widen a Restricted item. The stored
    /// fingerprint is the group's, so a matching lock is a no-op; a mismatch means
    /// the enforcement group was reconfigured, which we re-apply (and ledger) —
    /// still a lock, never a widen.
    /// </summary>
    private async Task<ReAclOutcome> ReAclLockedAsync(
        SeismicContent content, TrackedItem tracked, DateTime nowUtc, bool repair, CancellationToken ct)
    {
        var group = _config.Seismic.ClassificationEnforceGroup!;
        var enforced = EnforcedGroupAcl(group);
        var fingerprint = enforced.Fingerprint();

        if (tracked.AclFingerprint == fingerprint)
        {
            _store.UpsertTrackedItem(tracked with { LastSeenUtc = nowUtc, ClassificationLocked = true });
            return ReAclOutcome.NoChange;
        }

        // The enforcement group changed (or this is a pre-lock baseline): re-lock
        // to the CURRENT group. This is the only ACL change a locked item allows.
        var isBaseline = tracked.AclFingerprint is null;
        if (!isBaseline)
        {
            Stats.AddAclDrift();
            Logger.Info(
                $"CLASSIFICATION RE-LOCK {content.Id}: enforcement group changed, ACL kept locked to "
                + $"Entra group {group} (never widened to source).");
        }

        if (repair)
        {
            try
            {
                await _graph.PatchExternalItemAclAsync(ConnectorId, content.Id, enforced.ToJsonArray(), ct)
                    .ConfigureAwait(false);
                Stats.AddReAcled();
                Ledger.Append(
                    content.Id,
                    DecisionLedger.DecisionAclRestrict,
                    $"re-acl: classification lock re-applied; ACL restricted to Entra group {group}");
                _store.UpsertTrackedItem(tracked with
                {
                    AclFingerprint = fingerprint,
                    LastSeenUtc = nowUtc,
                    ClassificationLocked = true,
                });
            }
            catch (GraphApiError ex)
            {
                Logger.Error(
                    $"CLASSIFICATION RE-LOCK FAILED {content.Id} ('{content.Name}', teamsite {content.TeamsiteId}): "
                    + $"ACL lock to Entra group {group} not applied — {ex.Message}. The indexed ACL is "
                    + "unchanged; dead-lettered as ReAcl for retry-failed.");
                Stats.AddFailed();
                SyncState.AppendFailedRecords(
                    SyncState.FailedRecordsPath(ConnectorId),
                    new List<(string, string)> { (content.Id, $"re-acl (classification lock) failed: {ex.Message}") },
                    "ReAcl");
                return isBaseline ? ReAclOutcome.Baseline : ReAclOutcome.Drifted;
            }
        }
        else
        {
            _store.UpsertTrackedItem(tracked with { LastSeenUtc = nowUtc, ClassificationLocked = true });
        }

        return isBaseline ? ReAclOutcome.Baseline : ReAclOutcome.Drifted;
    }

    // ── targeted single-item path (ingest-item / webhook events) ────────────

    /// <summary>Ingest or withdraw one content item by id. Used by ingest-item and webhook events.</summary>
    public async Task<bool> IngestSingleAsync(
        string contentId, string? teamsiteId = null, CancellationToken ct = default)
    {
        SeismicContent? content;
        SeismicTeamsite? teamsite = null;
        if (teamsiteId is not null)
        {
            content = await _seismic.GetContentAsync(teamsiteId, contentId, ct).ConfigureAwait(false);
            teamsite = (await _seismic.GetTeamsitesAsync(ct).ConfigureAwait(false))
                .FirstOrDefault(t => t.Id == teamsiteId);
        }
        else
        {
            content = await _seismic.FindContentAsync(contentId, ct).ConfigureAwait(false);
            if (content is not null)
            {
                teamsite = (await _seismic.GetTeamsitesAsync(ct).ConfigureAwait(false))
                    .FirstOrDefault(t => t.Id == content.TeamsiteId);
            }
        }

        if (content is null)
        {
            // Gone from Seismic: withdraw if we ever had it.
            var tracked = _store.GetTrackedItem(contentId);
            if (tracked?.Status == "ingested")
            {
                await WithdrawItemAsync(contentId, "not-in-source",
                    "content deleted in Seismic (event)", ct).ConfigureAwait(false);
                _store.RemoveTrackedItem(contentId);
                return true;
            }
            Progress.Warning($"Content '{contentId}' not found in Seismic.");
            return false;
        }

        // Teamsite exclusion gate — explicit and FAIL-CLOSED. PrepareItemAsync
        // applies id-based teamsite rules from content.TeamsiteId alone, but
        // the name-based and Seismic-isRestricted rules need the resolved
        // teamsite; without this gate a webhook/ingest-item call could slip a
        // restricted library's item into the index.
        if (teamsite is null)
        {
            Progress.Warning(
                $"Content '{contentId}': teamsite '{content.TeamsiteId}' could not be resolved — "
                + "refusing single-item ingest (fail closed: teamsite exclusion rules cannot be evaluated).");
            return false;
        }
        if (_filter.IsTeamsiteExcluded(teamsite))
        {
            Stats.AddExcluded();
            Report.RecordExcluded(content.Id, teamsite.Id, "restricted-library",
                $"teamsite '{teamsite.Name}' is excluded by configuration (single-item path)");
            Logger.Info($"EXCLUDED {content.Id}: teamsite '{teamsite.Name}' is excluded (single-item path).");
            var trackedItem = _store.GetTrackedItem(content.Id);
            if (trackedItem?.Status == "ingested")
            {
                await WithdrawItemAsync(content.Id, "restricted-library",
                    $"teamsite '{teamsite.Name}' is excluded (single-item path)", ct).ConfigureAwait(false);
            }
            _store.UpsertTrackedItem(new TrackedItem(
                content.Id, content.CurrentVersionId, content.TeamsiteId,
                content.ExpiresAt?.ToUniversalTime(), DateTime.UtcNow, "excluded"));
            return true;
        }

        if (_config.Seismic.EnrichUsage && _usageByContentId.Count == 0)
            _usageByContentId = await _seismic.GetContentUsageAsync(ct).ConfigureAwait(false);

        var prepared = await PrepareItemAsync(content, teamsite, DateTime.UtcNow, ct).ConfigureAwait(false);
        if (prepared is null)
            return true;  // excluded / withdrawn / unchanged — handled inside

        return await FlushBatchAsync(
            new[] { prepared.Value }, "ContentItem", ct,
            new[] { content }, teamsite).ConfigureAwait(false);
    }

    /// <summary>Process drained webhook events (delete events withdraw immediately).</summary>
    public async Task ProcessEventsAsync(IReadOnlyList<ContentEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0)
            return;
        // A webhook batch is its own correlated unit of work.
        using var scope = Tracing.BeginNamedScope("webhook.process", ConnectorId, "webhook");
        scope.SetTag("webhook.event_count", events.Count);
        foreach (var evt in events)
        {
            if (StopRequested)
                return;
            using var eventSpan = Tracing.StartActivity("webhook.event");
            eventSpan?.SetTag("webhook.type", evt.Type);
            eventSpan?.SetTag("seismic.content_id", evt.ContentId);
            try
            {
                if (evt.IsDelete)
                {
                    var tracked = _store.GetTrackedItem(evt.ContentId);
                    if (tracked?.Status == "ingested")
                    {
                        await WithdrawItemAsync(evt.ContentId, "not-in-source",
                            $"webhook event '{evt.Type}'", ct).ConfigureAwait(false);
                        _store.RemoveTrackedItem(evt.ContentId);
                    }
                }
                else
                {
                    await IngestSingleAsync(evt.ContentId, evt.TeamsiteId, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;  // graceful stop — never treated as an event failure
            }
            catch (Exception ex)
            {
                // Event-dispatch boundary: the batch was already drained from the
                // queue, so letting one bad event escape would DROP every event
                // after it (and kill continuous mode). Dead-letter the event id —
                // retry-failed re-drives it through IngestSingleAsync, which
                // converges both publish and delete cases — and continue.
                Logger.Error(
                    $"Webhook event '{evt.Type}' for content {evt.ContentId}"
                    + (string.IsNullOrEmpty(evt.TeamsiteId) ? "" : $" (teamsite {evt.TeamsiteId})")
                    + " failed — dead-lettered for retry-failed; continuing with the remaining events. "
                    + "The next reconciling crawl also heals it.", ex);
                Stats.AddFailed();
                SyncState.AppendFailedRecords(
                    SyncState.FailedRecordsPath(ConnectorId),
                    new List<(string, string)>
                    {
                        (evt.ContentId, $"webhook event '{evt.Type}' failed: {ex.Message}"),
                    },
                    "ContentItem");
            }
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    internal static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.Skip(i).Take(size).ToList();
    }
}
