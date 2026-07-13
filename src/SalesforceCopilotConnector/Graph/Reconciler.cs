// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Graph/Reconciler.cs
// -------------------
// Index-vs-source drift reconciliation, built on the ingested-item inventory
// (Graph/ItemInventory.cs). For every object type it compares two views:
//
//   source    — the live Salesforce record set (the same fetch path the crawler
//               uses, FetchSalesforceRecordsAsync r["Id"]),
//   inventory — what the connector believes it has ingested,
//
// and reports the two drift classes:
//
//   MISSING — in the source but not in the inventory (never ingested, failed and
//             dead-lettered, or added since the last crawl). The next crawl /
//             `retry-failed` picks these up; reconcile only reports them.
//   STALE   — in the inventory but gone from the source (deleted in Salesforce).
//             `reconcile --fix` DELETEs these from the Graph connection and drops
//             them from the inventory.
//
// Sharding-aware: each object type reconciles against the connection that owns
// it (ShardingConfig), using that connection's inventory.

using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Graph;

/// <summary>Drift for a single object type against its owning connection.</summary>
public sealed class ObjectDrift
{
    public required string ObjectName { get; init; }
    public required string ConnectionId { get; init; }
    public int SourceCount { get; init; }
    public int IndexedCount { get; init; }
    public List<string> Missing { get; init; } = new();
    public List<string> Stale { get; init; } = new();
    public int FixedCount { get; set; }
    public List<string> FixFailures { get; } = new();

    public bool HasDrift => Missing.Count > 0 || Stale.Count > 0;

    /// <summary>Drift remaining after any --fix pass (missing is never "fixed" here).</summary>
    public bool HasRemainingDrift => Missing.Count > 0 || Stale.Count - FixedCount > 0;
}

/// <summary>Aggregate drift across every object type.</summary>
public sealed class DriftReport
{
    public List<ObjectDrift> Objects { get; } = new();

    public bool HasDrift => Objects.Any(o => o.HasDrift);

    public bool HasRemainingDrift => Objects.Any(o => o.HasRemainingDrift);

    public int TotalMissing => Objects.Sum(o => o.Missing.Count);

    public int TotalStale => Objects.Sum(o => o.Stale.Count);

    public int TotalFixed => Objects.Sum(o => o.FixedCount);
}

/// <summary>Outcome of an automatic post-full-crawl deletion sweep across every object type.</summary>
public sealed class SweepResult
{
    /// <summary>Total items withdrawn from Graph connections (and dropped from their inventories).</summary>
    public int Deleted { get; set; }

    /// <summary>Object types whose sweep the mass-deletion safety guard skipped (nothing deleted).</summary>
    public List<string> Skipped { get; } = new();

    /// <summary>Hard delete failures ("{id}: HTTP ...") — kept in the inventory, retried next sweep.</summary>
    public List<string> Failures { get; } = new();

    /// <summary>Per-object-type deleted counts (only object types that were actually swept).</summary>
    public Dictionary<string, int> PerObject { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Compares the ingested-item inventory against the live Salesforce source per object type.</summary>
public sealed class Reconciler
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector.reconcile");

    private readonly AppConfig _config;
    private readonly GraphClient _graph;
    private readonly Func<string, IItemInventory> _inventoryFactory;
    private readonly Func<SalesforceObjectConfig, CancellationToken, Task<List<string>>> _sourceIdsFetcher;

    // Salesforce access token, acquired lazily and reused across object types
    // within one ReconcileAsync / SweepDeletionsAsync call (reset at the top of each).
    private string? _sourceToken;

    /// <summary>
    /// Mass-deletion safety-guard floor: the guard only engages once an object type's inventory
    /// holds at least this many items, so a small/new connection is never blocked from pruning.
    /// </summary>
    internal const int MinInventoryForSafetyGuard = 20;

    public Reconciler(
        AppConfig config,
        GraphClient graph,
        Func<string, IItemInventory>? inventoryFactory = null,
        Func<SalesforceObjectConfig, CancellationToken, Task<List<string>>>? sourceIdsFetcher = null)
    {
        _config = config;
        _graph = graph;
        _inventoryFactory = inventoryFactory ?? ItemInventory.Open;
        _sourceIdsFetcher = sourceIdsFetcher ?? DefaultFetchSourceIdsAsync;
    }

    /// <summary>Pure drift computation (testable): ordinal hashset diff, ordinal-sorted output.</summary>
    internal static (List<string> Missing, List<string> Stale) ComputeDrift(
        IEnumerable<string> sourceIds, IEnumerable<string> indexedIds)
    {
        var source = sourceIds.ToHashSet(StringComparer.Ordinal);
        var indexed = indexedIds.ToHashSet(StringComparer.Ordinal);
        return (
            source.Where(id => !indexed.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList(),
            indexed.Where(id => !source.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Reconcile every object type (or just <paramref name="onlyType"/>). When <paramref name="fix"/>
    /// is set, stale items are DELETEd from the Graph connection and dropped from the inventory.
    /// </summary>
    public async Task<DriftReport> ReconcileAsync(
        string? onlyType = null, bool fix = false, CancellationToken ct = default)
    {
        _sourceToken = null;  // fresh token per call for the default fetcher

        // Object type → owning connection id (sharding-aware; a bad shard map aborts).
        var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ShardingConfig.TryLoad(_config, out var shards, out var shardError))
        {
            foreach (var shard in shards)
                foreach (var objectType in shard.ObjectTypes)
                    owner[objectType] = shard.ConnectionId;
        }
        else if (shardError is not null)
        {
            throw new ArgumentException(shardError);
        }

        var report = new DriftReport();
        foreach (var objectConfig in ApiClient.ObjectConfigs)
        {
            ct.ThrowIfCancellationRequested();
            if (onlyType is not null
                && !string.Equals(objectConfig.ObjectType, onlyType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var connectionId = owner.GetValueOrDefault(objectConfig.ObjectType, _config.Connector.Id);
            var sourceIds = await _sourceIdsFetcher(objectConfig, ct).ConfigureAwait(false);

            using var inventory = _inventoryFactory(connectionId);
            var indexedIds = inventory.IdsForObject(objectConfig.ObjectType);
            var (missing, stale) = ComputeDrift(sourceIds, indexedIds);

            var drift = new ObjectDrift
            {
                ObjectName = objectConfig.ObjectType,
                ConnectionId = connectionId,
                SourceCount = sourceIds.Count,
                IndexedCount = indexedIds.Count,
                Missing = missing,
                Stale = stale,
            };

            if (fix && stale.Count > 0)
            {
                // reconcile --fix intentionally has NO mass-deletion guard: it is an explicit,
                // on-demand operator action and its own consent. The AUTOMATIC post-crawl sweep
                // (SweepDeletionsAsync) IS guarded, because it fires unattended on every full crawl.
                var (fixedCount, failures) = await DeleteStaleAsync(
                    objectConfig.ObjectType, connectionId, stale, inventory, ct).ConfigureAwait(false);
                drift.FixedCount = fixedCount;
                drift.FixFailures.AddRange(failures);
                Logger.Info(
                    $"{objectConfig.ObjectType}: fixed {drift.FixedCount}/{stale.Count} stale item(s)"
                    + (drift.FixFailures.Count > 0 ? $" ({drift.FixFailures.Count} failed)" : string.Empty));
            }

            report.Objects.Add(drift);
        }
        return report;
    }

    /// <summary>
    /// DELETE each stale id from <paramref name="objectType"/>'s owning Graph connection and drop it
    /// from <paramref name="inventory"/>. Shared by <c>reconcile --fix</c> and the automatic sweep so
    /// both behave identically: a clean delete — or HTTP 404 (already gone) — prunes the inventory and
    /// counts as deleted (<see cref="Metrics.IncItemsDeleted()"/> per delete); any other
    /// <see cref="GraphApiError"/> is collected as a hard failure and the id is KEPT in the inventory
    /// so the next sweep / <c>reconcile --fix</c> retries it.
    /// </summary>
    private async Task<(int Deleted, List<string> Failures)> DeleteStaleAsync(
        string objectType,
        string connectionId,
        IReadOnlyList<string> staleIds,
        IItemInventory inventory,
        CancellationToken ct)
    {
        var deleted = 0;
        var failures = new List<string>();
        foreach (var itemId in staleIds)
        {
            ct.ThrowIfCancellationRequested();
            var path =
                $"{GraphClient.ExternalConnectionsPath}/{connectionId}/items/{Uri.EscapeDataString(itemId)}";
            try
            {
                // GraphClient.DeleteAsync throws GraphApiError on any non-2xx; a clean return
                // means the item was deleted.
                await _graph.DeleteAsync(path).ConfigureAwait(false);
                inventory.Remove(new[] { itemId });
                deleted++;
                Metrics.IncItemsDeleted();
            }
            catch (GraphApiError ex) when (ex.StatusCode == 404)
            {
                // Already gone from the Graph connection — treat as deleted.
                inventory.Remove(new[] { itemId });
                deleted++;
                Metrics.IncItemsDeleted();
            }
            catch (GraphApiError ex)
            {
                failures.Add($"{objectType}/{itemId}: HTTP {ex.StatusCode}: {ex.Message}");
            }
        }
        return (deleted, failures);
    }

    /// <summary>
    /// Automatic post-full-crawl existence sweep: for every configured object type (restricted to
    /// this connection's shard when <c>ShardObjectTypes</c> is set), withdraw from the Graph
    /// connection every inventory id that is ABSENT from a FRESH, same-filter, id-only query of
    /// Salesforce (<see cref="ApiClient.FetchRecordIdsAsync"/>). Those ids were deleted in Salesforce.
    ///
    /// <para>The source id set is NEVER "the ids ingested during this crawl": an item that still
    /// exists in Salesforce but merely failed to ingest this run (transient error) is present in the
    /// fresh query and therefore never swept — this is the load-bearing data-loss guarantee.</para>
    ///
    /// <para><b>Mass-deletion guard.</b> Once an object type's inventory holds at least
    /// <see cref="MinInventoryForSafetyGuard"/> items and <paramref name="guardPercent"/> is in
    /// (0, 100), if more than <paramref name="guardPercent"/>% of the inventory would be deleted the
    /// object is SKIPPED (nothing deleted): a warning is logged, a <c>deletion_sweep_skipped</c>
    /// alert is raised, and the type is added to <see cref="SweepResult.Skipped"/>. This protects the
    /// index from a source outage / wrong filter / partial fetch nuking it. <c>reconcile --fix</c>
    /// deliberately has no such guard.</para>
    /// </summary>
    public async Task<SweepResult> SweepDeletionsAsync(int guardPercent, CancellationToken ct = default)
    {
        _sourceToken = null;  // fresh token per call for the default fetcher

        // Object type → owning connection id (sharding-aware; a bad shard map aborts) — identical
        // to ReconcileAsync so an object always sweeps against the connection that ingested it.
        var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ShardingConfig.TryLoad(_config, out var shards, out var shardError))
        {
            foreach (var shard in shards)
                foreach (var objectType in shard.ObjectTypes)
                    owner[objectType] = shard.ConnectionId;
        }
        else if (shardError is not null)
        {
            throw new ArgumentException(shardError);
        }

        // When wired into a per-shard crawl the config carries that shard's object subset; sweep
        // only those so each object is swept exactly once (by its owning shard). Unset (the default
        // / standalone reconcile path) ⇒ every configured object, exactly like ReconcileAsync.
        var shardObjects = _config.ShardObjectTypes is null
            ? null
            : new HashSet<string>(_config.ShardObjectTypes, StringComparer.Ordinal);

        var result = new SweepResult();
        foreach (var objectConfig in ApiClient.ObjectConfigs)
        {
            ct.ThrowIfCancellationRequested();
            var objectType = objectConfig.ObjectType;
            if (shardObjects is not null && !shardObjects.Contains(objectType))
                continue;

            var connectionId = owner.GetValueOrDefault(objectType, _config.Connector.Id);

            // Source of truth: a FRESH id-only, same-filter query (NEVER the ingested set).
            var sourceIds = (await _sourceIdsFetcher(objectConfig, ct).ConfigureAwait(false))
                .ToHashSet(StringComparer.Ordinal);

            using var inventory = _inventoryFactory(connectionId);
            var indexed = inventory.IdsForObject(objectType);
            var stale = indexed.Where(id => !sourceIds.Contains(id)).ToList();
            if (stale.Count == 0)
                continue;

            // ── Mass-deletion safety guard ────────────────────────────────────────────────────
            if (indexed.Count >= MinInventoryForSafetyGuard && guardPercent > 0 && guardPercent < 100)
            {
                var stalePercent = 100.0 * stale.Count / indexed.Count;
                if (stalePercent > guardPercent)
                {
                    Logger.Warning(
                        $"{objectType}: deletion sweep SKIPPED — {stale.Count}/{indexed.Count} "
                        + $"({stalePercent:0.#}%) of the inventory would be deleted, above the "
                        + $"DELETION_SYNC_MAX_PERCENT={guardPercent} safety guard. If this drop is real, "
                        + "raise the threshold (or set it to 0/100 to disable the guard) and re-run, "
                        + "or use `reconcile --fix`.");
                    result.Skipped.Add(objectType);
                    await Alerting.RaiseAsync(
                        "deletion_sweep_skipped",
                        $"Deletion sweep for '{objectType}' skipped: {stale.Count}/{indexed.Count} "
                        + $"items stale (> {guardPercent}%) on connector '{_config.Connector.Id}'",
                        new Dictionary<string, object?>
                        {
                            ["objectType"] = objectType,
                            ["stale"] = stale.Count,
                            ["inventory"] = indexed.Count,
                            ["threshold"] = guardPercent,
                        }).ConfigureAwait(false);
                    continue;
                }
            }

            Logger.Info(
                $"{objectType}: sweeping {stale.Count} item(s) no longer present in Salesforce...");
            var (deleted, failures) = await DeleteStaleAsync(
                objectType, connectionId, stale, inventory, ct).ConfigureAwait(false);
            result.Deleted += deleted;
            result.Failures.AddRange(failures);
            result.PerObject[objectType] = deleted;
        }
        return result;
    }

    /// <summary>
    /// Default source fetcher (used by both <see cref="ReconcileAsync"/> and
    /// <see cref="SweepDeletionsAsync"/>): acquire a Salesforce token once (cached for this call) and
    /// enumerate the live <c>Id</c> set for <paramref name="objectConfig"/> via the lightweight
    /// id-only, same-filter query — no record bodies are transferred.
    /// </summary>
    private async Task<List<string>> DefaultFetchSourceIdsAsync(
        SalesforceObjectConfig objectConfig, CancellationToken ct)
    {
        _sourceToken ??= await ApiClient.GetSalesforceAccessTokenAsync(_config).ConfigureAwait(false);

        var ids = new List<string>();
        await foreach (var id in ApiClient.FetchRecordIdsAsync(
                           _config, _sourceToken, objectConfig, ct).ConfigureAwait(false))
        {
            ids.Add(id);
        }
        return ids;
    }
}
