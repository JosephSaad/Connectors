// Graph/Reconciler.cs
// -------------------
// Index-vs-source drift reconciliation, built on the ingested-item inventory
// (Graph/Inventory.cs). For every object type it compares three views:
//
//   source    — the BDH record set (the same filtered fetch path the crawler
//               uses: partition pruning + record predicates + row cap),
//   inventory — what the connector believes it has ingested,
//
// and reports the two drift classes:
//
//   MISSING — in the source but not in the inventory (never ingested, failed
//             and dead-lettered, or added since the last crawl). The next
//             crawl / `retry-failed` picks these up; reconcile only reports.
//   STALE   — in the inventory but gone from the source (deleted in BDH /
//             Salesforce). `reconcile --fix` DELETEs these from the Graph
//             connection and drops them from the inventory — the same
//             withdrawal the full-crawl deletion sweep performs, minus the
//             mass-deletion guard (an explicit --fix is operator intent).
//
// Sharding-aware: each object type reconciles against the connection that
// owns it, with that connection's inventory.

using HadoopConnector.Hdfs;
using HadoopConnector.Config;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Graph;

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

public sealed class DriftReport
{
    public List<ObjectDrift> Objects { get; } = new();

    public bool HasDrift => Objects.Any(o => o.HasDrift);

    public bool HasRemainingDrift => Objects.Any(o => o.HasRemainingDrift);

    public int TotalMissing => Objects.Sum(o => o.Missing.Count);

    public int TotalStale => Objects.Sum(o => o.Stale.Count);

    public int TotalFixed => Objects.Sum(o => o.FixedCount);
}

public sealed class Reconciler
{
    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector.reconcile");

    private readonly AppConfig _config;
    private readonly SchemaConfig _schema;
    private readonly BdhFetcher _fetcher;
    private readonly GraphClient _graph;
    private readonly Func<string, IItemInventory> _inventoryFactory;

    public Reconciler(
        AppConfig config,
        SchemaConfig schema,
        BdhFetcher fetcher,
        GraphClient graph,
        Func<string, IItemInventory>? inventoryFactory = null)
    {
        _config = config;
        _schema = schema;
        _fetcher = fetcher;
        _graph = graph;
        _inventoryFactory = inventoryFactory ?? ItemInventory.Open;
    }

    /// <summary>Pure drift computation (testable).</summary>
    internal static (List<string> Missing, List<string> Stale) ComputeDrift(
        IEnumerable<string> sourceIds, IEnumerable<string> indexedIds)
    {
        var source = sourceIds.ToHashSet(StringComparer.Ordinal);
        var indexed = indexedIds.ToHashSet(StringComparer.Ordinal);
        return (
            source.Where(id => !indexed.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList(),
            indexed.Where(id => !source.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList());
    }

    public async Task<DriftReport> ReconcileAsync(
        string? onlyType = null, bool fix = false, CancellationToken ct = default)
    {
        // Object type → owning connection id (sharding-aware; bad map aborts).
        var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ShardingConfig.TryLoad(_schema, out var shards, out var shardError))
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
        foreach (var objectConfig in _schema.ObjectList)
        {
            ct.ThrowIfCancellationRequested();
            if (onlyType is not null
                && !string.Equals(objectConfig.ObjectName, onlyType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var connectionId = owner.GetValueOrDefault(objectConfig.ObjectName, _config.ConnectorId);
            var fetch = await _fetcher.FetchAsync(objectConfig, fullCrawl: true, sinceUtc: null, ct)
                .ConfigureAwait(false);
            var sourceIds = fetch.Records.Select(r => r.ItemId).ToList();
            if (fetch.Truncated)
            {
                // A row-capped source set is incomplete: stale detection would
                // report (and --fix would delete) live records. Report counts
                // only and never fix from a truncated fetch.
                Logger.Warning(
                    $"{objectConfig.ObjectName}: source fetch truncated by the row cap — "
                    + "stale detection skipped for this object (tighten filters or raise "
                    + "BDH_MAX_RECORDS_PER_OBJECT).");
                using var truncatedInventory = _inventoryFactory(connectionId);
                report.Objects.Add(new ObjectDrift
                {
                    ObjectName = objectConfig.ObjectName,
                    ConnectionId = connectionId,
                    SourceCount = sourceIds.Count,
                    IndexedCount = truncatedInventory.IdsForObject(objectConfig.ObjectName).Count,
                });
                continue;
            }

            using var inventory = _inventoryFactory(connectionId);
            var indexedIds = inventory.IdsForObject(objectConfig.ObjectName);
            var (missing, stale) = ComputeDrift(sourceIds, indexedIds);

            var drift = new ObjectDrift
            {
                ObjectName = objectConfig.ObjectName,
                ConnectionId = connectionId,
                SourceCount = sourceIds.Count,
                IndexedCount = indexedIds.Count,
                Missing = missing,
                Stale = stale,
            };

            if (fix && stale.Count > 0)
            {
                var cfg = connectionId == _config.ConnectorId
                    ? _config
                    : _config.CloneForConnection(connectionId);
                foreach (var itemId in stale)
                {
                    ct.ThrowIfCancellationRequested();
                    var response = await _graph.DeleteAsync(
                        $"external/connections/{cfg.ConnectorId}/items/{itemId}", ct).ConfigureAwait(false);
                    if (response.IsSuccess || (int)response.StatusCode == 404)
                    {
                        inventory.Remove(new[] { itemId });
                        drift.FixedCount++;
                        Metrics.IncItemsDeleted();
                    }
                    else
                    {
                        drift.FixFailures.Add(
                            $"{itemId}: HTTP {(int)response.StatusCode}: {response.RawBody}");
                    }
                }
                Logger.Info(
                    $"{objectConfig.ObjectName}: fixed {drift.FixedCount}/{stale.Count} stale item(s)"
                    + (drift.FixFailures.Count > 0 ? $" ({drift.FixFailures.Count} failed)" : string.Empty));
            }

            report.Objects.Add(drift);
        }
        return report;
    }
}
