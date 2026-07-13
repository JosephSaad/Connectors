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

using System.Text.Json.Nodes;
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

/// <summary>Compares the ingested-item inventory against the live Salesforce source per object type.</summary>
public sealed class Reconciler
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector.reconcile");

    private readonly AppConfig _config;
    private readonly GraphClient _graph;
    private readonly Func<string, IItemInventory> _inventoryFactory;
    private readonly Func<SalesforceObjectConfig, CancellationToken, Task<List<string>>> _sourceIdsFetcher;

    // Salesforce access token, acquired lazily and reused across object types
    // within one ReconcileAsync call (reset at the top of each call).
    private string? _sourceToken;

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
                foreach (var itemId in stale)
                {
                    ct.ThrowIfCancellationRequested();
                    var path =
                        $"{GraphClient.ExternalConnectionsPath}/{connectionId}/items/{Uri.EscapeDataString(itemId)}";
                    try
                    {
                        // GraphClient.DeleteAsync throws GraphApiError on any non-2xx; a
                        // clean return means the item was deleted.
                        await _graph.DeleteAsync(path).ConfigureAwait(false);
                        inventory.Remove(new[] { itemId });
                        drift.FixedCount++;
                        Metrics.IncItemsDeleted();
                    }
                    catch (GraphApiError ex) when (ex.StatusCode == 404)
                    {
                        // Already gone from the Graph connection — treat as deleted.
                        inventory.Remove(new[] { itemId });
                        drift.FixedCount++;
                        Metrics.IncItemsDeleted();
                    }
                    catch (GraphApiError ex)
                    {
                        drift.FixFailures.Add($"{itemId}: HTTP {ex.StatusCode}: {ex.Message}");
                    }
                }
                Logger.Info(
                    $"{objectConfig.ObjectType}: fixed {drift.FixedCount}/{stale.Count} stale item(s)"
                    + (drift.FixFailures.Count > 0 ? $" ({drift.FixFailures.Count} failed)" : string.Empty));
            }

            report.Objects.Add(drift);
        }
        return report;
    }

    /// <summary>
    /// Default source fetcher: acquire a Salesforce token once (cached for this call) and enumerate
    /// the live record set for <paramref name="objectConfig"/>, collecting each record's <c>Id</c>.
    /// </summary>
    private async Task<List<string>> DefaultFetchSourceIdsAsync(
        SalesforceObjectConfig objectConfig, CancellationToken ct)
    {
        _sourceToken ??= await ApiClient.GetSalesforceAccessTokenAsync(_config).ConfigureAwait(false);

        var ids = new List<string>();
        await foreach (var record in ApiClient.FetchSalesforceRecordsAsync(
                           _config, _sourceToken, objectConfig, since: null).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var id = (record["Id"] as JsonNode)?.ToString();
            if (!string.IsNullOrEmpty(id))
                ids.Add(id!);
        }
        return ids;
    }
}
