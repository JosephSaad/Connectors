// Reconciler tests: drift computation, report shape, --fix repair path and
// shard-aware connection routing — all against mocked Clarizen + Graph.

using System.Net;
using System.Text.Json.Nodes;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;

namespace ClarizenConnector.Tests;

public class ReconcileTests : IDisposable
{
    private const string Connector = "ClarizenAdaptiveWork";

    private readonly TempDir _dir = new();
    private readonly AppConfig _config = TestConfig.Make();
    private readonly SchemaConfig _schema;
    private readonly FakeGraphClient _graph;
    private readonly ClarizenClient _clarizen;

    private readonly Dictionary<string, List<int>> _source = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Task"] = new List<int> { 1, 2 },
        ["Risk"] = new List<int> { 5 },
    };

    public ReconcileTests()
    {
        _schema = new SchemaConfig
        {
            ObjectList = new List<ObjectConfig>
            {
                MakeObject("Task"),
                MakeObject("Risk"),
            },
        };
        var handler = new MockHttpHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/authentication/login"))
                return MockHttpHandler.Json(HttpStatusCode.OK, """{"sessionId": "s"}""");
            var objectType = body.Contains("FROM Task") ? "Task" : "Risk";
            var entities = new JsonArray();
            foreach (var n in _source[objectType])
                entities.Add(new JsonObject { ["id"] = $"/{objectType}/{n}", ["Name"] = $"{objectType} {n}" });
            return MockHttpHandler.Json(HttpStatusCode.OK, new JsonObject
            {
                ["entities"] = entities,
                ["paging"] = new JsonObject { ["hasMore"] = false },
            }.ToJsonString());
        });
        _clarizen = new ClarizenClient(
            _config, new ApiBudget(1_000_000, callsPerMinute: 6_000_000), handler);
        _clarizen.DelayAsync = (_, _) => Task.CompletedTask;
        _graph = new FakeGraphClient(_config);
    }

    private static ObjectConfig MakeObject(string name) => new()
    {
        ObjectName = name,
        DisplayName = name,
        SelectedFields = new Dictionary<string, string> { ["Name"] = "Title" },
    };

    public void Dispose() => _dir.Dispose();

    private Func<string, IItemInventory> InventoryFactory => connectionId =>
        new ItemInventory(connectionId, Path.Combine(_dir.Path, $"inventory_{connectionId}.db"));

    private Reconciler Make() => new(_config, _schema, _clarizen, _graph, InventoryFactory);

    private void Seed(string connectionId, params (string ItemId, string ObjectType)[] items)
    {
        using var inventory = InventoryFactory(connectionId);
        inventory.RecordSeen(items, DateTime.UtcNow);
    }

    // ── Pure drift computation ───────────────────────────────────────────────

    [Fact]
    public void ComputeDrift_SplitsMissingAndStale()
    {
        var (missing, stale) = Reconciler.ComputeDrift(
            sourceIds: new[] { "A", "B", "C" },
            indexedIds: new[] { "B", "C", "D", "E" });
        Assert.Equal(new[] { "A" }, missing);
        Assert.Equal(new[] { "D", "E" }, stale);
    }

    [Fact]
    public void ComputeDrift_NoDrift_BothEmpty()
    {
        var (missing, stale) = Reconciler.ComputeDrift(new[] { "A" }, new[] { "A" });
        Assert.Empty(missing);
        Assert.Empty(stale);
    }

    // ── Report ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reconcile_ReportsPerObjectDrift()
    {
        // Indexed: Task_1 (ok), Task_9 (stale). Missing: Task_2, Risk_5.
        Seed(Connector, ("Task_1", "Task"), ("Task_9", "Task"));

        var report = await Make().ReconcileAsync();

        Assert.True(report.HasDrift);
        Assert.Equal(2, report.Objects.Count);
        var task = report.Objects.Single(o => o.ObjectName == "Task");
        Assert.Equal(2, task.SourceCount);
        Assert.Equal(2, task.IndexedCount);
        Assert.Equal(new[] { "Task_2" }, task.Missing);
        Assert.Equal(new[] { "Task_9" }, task.Stale);
        var risk = report.Objects.Single(o => o.ObjectName == "Risk");
        Assert.Equal(new[] { "Risk_5" }, risk.Missing);
        Assert.Empty(risk.Stale);
        Assert.Equal(2, report.TotalMissing);
        Assert.Equal(1, report.TotalStale);
    }

    [Fact]
    public async Task Reconcile_NoDrift_WhenInventoryMatchesSource()
    {
        Seed(Connector, ("Task_1", "Task"), ("Task_2", "Task"), ("Risk_5", "Risk"));
        var report = await Make().ReconcileAsync();
        Assert.False(report.HasDrift);
        Assert.False(report.HasRemainingDrift);
    }

    [Fact]
    public async Task Reconcile_TypeFilter_RestrictsScope()
    {
        Seed(Connector, ("Task_9", "Task"));
        var report = await Make().ReconcileAsync(onlyType: "task");
        var drift = Assert.Single(report.Objects);
        Assert.Equal("Task", drift.ObjectName);
    }

    // ── --fix ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Fix_DeletesStaleItems_AndPrunesInventory()
    {
        Seed(Connector, ("Task_1", "Task"), ("Task_9", "Task"), ("Task_8", "Task"));

        var report = await Make().ReconcileAsync(fix: true);

        var task = report.Objects.Single(o => o.ObjectName == "Task");
        Assert.Equal(2, task.FixedCount);
        Assert.Empty(task.FixFailures);
        Assert.Contains(_graph.Sent, s =>
            s.Method == HttpMethod.Delete
            && s.Path == $"external/connections/{Connector}/items/Task_9");
        using var inventory = InventoryFactory(Connector);
        Assert.Equal(new[] { "Task_1" }, inventory.IdsForObject("Task"));
        // Missing items remain drift (the next crawl handles them) — but no
        // stale drift remains. Missing: Task_2 plus never-ingested Risk_5.
        Assert.Equal(2, report.TotalMissing);
        Assert.True(report.HasRemainingDrift);
        Assert.Equal(0, report.TotalStale - report.TotalFixed);
    }

    [Fact]
    public async Task Fix_FailedDelete_ReportedAndKeptInInventory()
    {
        Seed(Connector, ("Task_9", "Task"));
        _graph.FailingItemIds.Add("Task_9");

        var report = await Make().ReconcileAsync(fix: true);
        var task = report.Objects.Single(o => o.ObjectName == "Task");
        Assert.Equal(0, task.FixedCount);
        Assert.Single(task.FixFailures);
        using var inventory = InventoryFactory(Connector);
        Assert.Contains("Task_9", inventory.IdsForObject("Task"));
    }

    // ── Sharding ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sharded_ReconcilesAgainstOwningConnection()
    {
        using var scope = new EnvScope(
            (ShardingConfig.EnvVar, """{"shardA": ["Task"], "shardB": ["Risk"]}"""));
        Seed("shardA", ("Task_9", "Task"));   // stale, lives in shard A's inventory

        var report = await Make().ReconcileAsync(fix: true);

        var task = report.Objects.Single(o => o.ObjectName == "Task");
        Assert.Equal("shardA", task.ConnectionId);
        Assert.Contains(_graph.Sent, s =>
            s.Method == HttpMethod.Delete
            && s.Path == "external/connections/shardA/items/Task_9");
        var risk = report.Objects.Single(o => o.ObjectName == "Risk");
        Assert.Equal("shardB", risk.ConnectionId);
    }

    [Fact]
    public async Task Sharded_BadMap_Aborts()
    {
        using var scope = new EnvScope((ShardingConfig.EnvVar, """{"shardA": ["Task"]}"""));
        await Assert.ThrowsAsync<ArgumentException>(() => Make().ReconcileAsync());
    }
}
