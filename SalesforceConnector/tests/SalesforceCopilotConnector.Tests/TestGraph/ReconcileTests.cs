// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for the reconciler (Graph/Reconciler.cs): pure drift computation, drift
// classification against an injected source, the --fix repair path (success,
// 404-as-fixed, hard-failure, mixed), and shard-aware connection routing.
// Salesforce and Graph are both faked, so no network is touched.

using System.Text.Json;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests.TestGraph;

// Joins "EnvVars" because the sharding tests mutate GRAPH_CONNECTION_SHARDS;
// every test saves/restores it (and resets the process-global Metrics counters).
[Collection("EnvVars")]
public sealed class ReconcileTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _savedShards;
    private readonly AppConfig _config = TestFixtures.TestConfig();
    private readonly FakeGraphClient _graph = new();

    // Two real object types from the configured object list (schema-derived, so
    // always present in ApiClient.ObjectConfigs and in _config.ObjectNames).
    private static readonly string TypeA = ApiClient.ObjectConfigs[0].ObjectType;

    public ReconcileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sfc_recon_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _savedShards = Environment.GetEnvironmentVariable(ShardingConfig.EnvVar);
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, null);
        Metrics.ResetForTests();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, _savedShards);
        Metrics.ResetForTests();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private IItemInventory InventoryFactory(string connectionId) =>
        new ItemInventory(connectionId, Path.Combine(_tempDir, $"inventory_{connectionId}.db"));

    private void Seed(string connectionId, params (string ItemId, string ObjectType)[] items)
    {
        using var inventory = InventoryFactory(connectionId);
        inventory.RecordSeen(items, DateTime.UtcNow);
    }

    // Source fetcher backed by a per-object-type dictionary (missing key ⇒ no source records).
    private static Func<SalesforceObjectConfig, CancellationToken, Task<List<string>>> Source(
        Dictionary<string, List<string>> byType) =>
        (cfg, _) => Task.FromResult(
            byType.TryGetValue(cfg.ObjectType, out var ids) ? new List<string>(ids) : new List<string>());

    private Reconciler Make(Dictionary<string, List<string>>? source = null) =>
        new(_config, _graph, InventoryFactory, Source(source ?? new Dictionary<string, List<string>>()));

    // ── Pure drift computation ───────────────────────────────────────────────

    [Fact]
    public void ComputeDrift_Disjoint_AllMissingAllStale()
    {
        var (missing, stale) = Reconciler.ComputeDrift(
            sourceIds: new[] { "A", "B" }, indexedIds: new[] { "C", "D" });
        Assert.Equal(new[] { "A", "B" }, missing);
        Assert.Equal(new[] { "C", "D" }, stale);
    }

    [Fact]
    public void ComputeDrift_Overlap_SplitsMissingAndStale()
    {
        var (missing, stale) = Reconciler.ComputeDrift(
            sourceIds: new[] { "A", "B", "C" }, indexedIds: new[] { "B", "C", "D", "E" });
        Assert.Equal(new[] { "A" }, missing);
        Assert.Equal(new[] { "D", "E" }, stale);
    }

    [Fact]
    public void ComputeDrift_EmptySource_AllStale()
    {
        var (missing, stale) = Reconciler.ComputeDrift(Array.Empty<string>(), new[] { "B", "A" });
        Assert.Empty(missing);
        Assert.Equal(new[] { "A", "B" }, stale);  // ordinal-sorted
    }

    [Fact]
    public void ComputeDrift_EmptyIndex_AllMissing()
    {
        var (missing, stale) = Reconciler.ComputeDrift(new[] { "B", "A" }, Array.Empty<string>());
        Assert.Equal(new[] { "A", "B" }, missing);  // ordinal-sorted
        Assert.Empty(stale);
    }

    [Fact]
    public void ComputeDrift_Identical_NoDrift()
    {
        var (missing, stale) = Reconciler.ComputeDrift(new[] { "A", "B" }, new[] { "B", "A" });
        Assert.Empty(missing);
        Assert.Empty(stale);
    }

    [Fact]
    public void ComputeDrift_SortsOrdinally()
    {
        var (missing, _) = Reconciler.ComputeDrift(
            sourceIds: new[] { "Z", "a", "A", "10", "2" }, indexedIds: Array.Empty<string>());
        // Ordinal order by UTF-16 code unit: digits < uppercase < lowercase.
        Assert.Equal(new[] { "10", "2", "A", "Z", "a" }, missing);
    }

    // ── Classification ───────────────────────────────────────────────────────

    [Fact]
    public async Task Reconcile_ReportsPerObjectDrift()
    {
        // TypeA: indexed {_1, _2}, source {_1, _3} ⇒ missing _3, stale _2.
        Seed(_config.Connector.Id, ($"{TypeA}_1", TypeA), ($"{TypeA}_2", TypeA));
        var source = new Dictionary<string, List<string>>
        {
            [TypeA] = new() { $"{TypeA}_1", $"{TypeA}_3" },
        };

        var report = await Make(source).ReconcileAsync();

        var drift = report.Objects.Single(o => o.ObjectName == TypeA);
        Assert.Equal(2, drift.SourceCount);
        Assert.Equal(2, drift.IndexedCount);
        Assert.Equal(new[] { $"{TypeA}_3" }, drift.Missing);
        Assert.Equal(new[] { $"{TypeA}_2" }, drift.Stale);
        Assert.Equal(_config.Connector.Id, drift.ConnectionId);
        Assert.True(report.HasDrift);
    }

    [Fact]
    public async Task Reconcile_NoDrift_WhenInventoryMatchesSource()
    {
        Seed(_config.Connector.Id, ($"{TypeA}_1", TypeA));
        var source = new Dictionary<string, List<string>> { [TypeA] = new() { $"{TypeA}_1" } };

        var report = await Make(source).ReconcileAsync();

        Assert.False(report.HasDrift);
        Assert.False(report.HasRemainingDrift);
    }

    [Fact]
    public async Task Reconcile_TypeFilter_RestrictsScope_CaseInsensitive()
    {
        Seed(_config.Connector.Id, ($"{TypeA}_9", TypeA));

        var report = await Make().ReconcileAsync(onlyType: TypeA.ToLowerInvariant());

        var drift = Assert.Single(report.Objects);
        Assert.Equal(TypeA, drift.ObjectName);
        Assert.Equal(new[] { $"{TypeA}_9" }, drift.Stale);
    }

    // ── --fix ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Fix_DeletesStale_PrunesInventory_AndCounts()
    {
        Seed(_config.Connector.Id, ($"{TypeA}_keep", TypeA), ($"{TypeA}_gone", TypeA));
        var source = new Dictionary<string, List<string>> { [TypeA] = new() { $"{TypeA}_keep" } };

        var report = await Make(source).ReconcileAsync(fix: true);

        var drift = report.Objects.Single(o => o.ObjectName == TypeA);
        Assert.Equal(1, drift.FixedCount);
        Assert.Empty(drift.FixFailures);
        Assert.Contains(_graph.DeleteCalls, c =>
            c.Url == $"{GraphClient.ExternalConnectionsPath}/{_config.Connector.Id}/items/{TypeA}_gone");

        using var inventory = InventoryFactory(_config.Connector.Id);
        Assert.Equal(new[] { $"{TypeA}_keep" }, inventory.IdsForObject(TypeA));
        Assert.Equal(1, Metrics.ItemsDeleted);
        Assert.False(report.HasRemainingDrift);
    }

    [Fact]
    public async Task Fix_404_CountsAsFixed_AndPruned()
    {
        Seed(_config.Connector.Id, ($"{TypeA}_missing404", TypeA));
        _graph.OnDelete = _ => throw new GraphApiError(404, "Not Found");

        var report = await Make().ReconcileAsync(fix: true);

        var drift = report.Objects.Single(o => o.ObjectName == TypeA);
        Assert.Equal(1, drift.FixedCount);
        Assert.Empty(drift.FixFailures);
        using var inventory = InventoryFactory(_config.Connector.Id);
        Assert.Empty(inventory.IdsForObject(TypeA));
        Assert.Equal(1, Metrics.ItemsDeleted);
    }

    [Fact]
    public async Task Fix_HardFailure_ReportedAndKeptInInventory()
    {
        Seed(_config.Connector.Id, ($"{TypeA}_boom", TypeA));
        _graph.OnDelete = _ => throw new GraphApiError(500, "Internal Server Error");

        var report = await Make().ReconcileAsync(fix: true);

        var drift = report.Objects.Single(o => o.ObjectName == TypeA);
        Assert.Equal(0, drift.FixedCount);
        Assert.Single(drift.FixFailures);
        Assert.Contains("500", drift.FixFailures[0]);
        using var inventory = InventoryFactory(_config.Connector.Id);
        Assert.Contains($"{TypeA}_boom", inventory.IdsForObject(TypeA));
        Assert.Equal(0, Metrics.ItemsDeleted);
        Assert.True(report.HasRemainingDrift);
    }

    [Fact]
    public async Task Fix_MixedOutcomes_SuccessAnd404AndFailure()
    {
        Seed(_config.Connector.Id,
            ($"{TypeA}_ok", TypeA), ($"{TypeA}_404", TypeA), ($"{TypeA}_500", TypeA));
        _graph.OnDelete = path =>
        {
            if (path.EndsWith($"/{TypeA}_404", StringComparison.Ordinal)) throw new GraphApiError(404, "Not Found");
            if (path.EndsWith($"/{TypeA}_500", StringComparison.Ordinal)) throw new GraphApiError(500, "Boom");
            return new JsonObject();
        };

        var report = await Make().ReconcileAsync(fix: true);

        var drift = report.Objects.Single(o => o.ObjectName == TypeA);
        Assert.Equal(2, drift.FixedCount);   // ok + 404
        Assert.Single(drift.FixFailures);    // 500
        Assert.Equal(2, Metrics.ItemsDeleted);

        using var inventory = InventoryFactory(_config.Connector.Id);
        Assert.Equal(new[] { $"{TypeA}_500" }, inventory.IdsForObject(TypeA));  // only the failure remains
    }

    // ── Sharding ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sharded_ReconcilesAgainstOwningConnection()
    {
        // TypeA → shardA; every other configured object → shardB (valid full map).
        var rest = _config.ObjectNames
            .Where(n => !string.Equals(n, TypeA, StringComparison.Ordinal))
            .ToArray();
        var map = new Dictionary<string, string[]>
        {
            ["shardA"] = new[] { TypeA },
            ["shardB"] = rest,
        };
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, JsonSerializer.Serialize(map));
        Seed("shardA", ($"{TypeA}_stale", TypeA));  // stale, lives in shard A's inventory

        var report = await Make().ReconcileAsync(fix: true);

        var driftA = report.Objects.Single(o => o.ObjectName == TypeA);
        Assert.Equal("shardA", driftA.ConnectionId);
        Assert.Equal(1, driftA.FixedCount);
        Assert.Contains(_graph.DeleteCalls, c =>
            c.Url == $"{GraphClient.ExternalConnectionsPath}/shardA/items/{TypeA}_stale");

        // Every other configured object routes to shard B.
        var restSet = new HashSet<string>(rest, StringComparer.Ordinal);
        Assert.All(
            report.Objects.Where(o => restSet.Contains(o.ObjectName)),
            o => Assert.Equal("shardB", o.ConnectionId));
    }

    [Fact]
    public async Task Sharded_BadMap_Aborts()
    {
        // Only one object assigned ⇒ the rest are unassigned ⇒ TryLoad reports an
        // error ⇒ ReconcileAsync throws (operator must fix the shard map first).
        Environment.SetEnvironmentVariable(
            ShardingConfig.EnvVar,
            JsonSerializer.Serialize(new Dictionary<string, string[]> { ["shardA"] = new[] { TypeA } }));

        await Assert.ThrowsAsync<ArgumentException>(() => Make().ReconcileAsync());
    }
}
