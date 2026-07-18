// Reconciler tests: drift computation, report shape, --fix repair path,
// truncated-fetch (row cap) stale-fix skip and shard-aware connection
// routing — all against an in-memory BDH source and a mocked Graph.

using HadoopConnector.Hdfs;
using HadoopConnector.Config;
using HadoopConnector.Filters;
using HadoopConnector.Graph;

namespace HadoopConnector.Tests;

public class ReconcileTests : IDisposable
{
    private const string Connector = "BdhHadoopMart";

    private readonly TempDir _dir = new();
    private readonly AppConfig _config = TestConfig.Make(allowFullScan: true);
    private readonly SchemaConfig _schema;
    private readonly FakeGraphClient _graph;

    /// <summary>Mutable source record-number sets served per object type.</summary>
    private readonly Dictionary<string, List<int>> _source = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Task"] = new List<int> { 1, 2 },
        ["Risk"] = new List<int> { 5 },
    };

    /// <summary>Salesforce-shaped record ids ("T000000000001" / "R000000000005").</summary>
    private static string Tid(int n) => $"T{n:D12}";
    private static string Rid(int n) => $"R{n:D12}";

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

    /// <summary>Builds a reconciler over a fresh source snapshot of _source.</summary>
    private Reconciler Make(AppConfig? config = null)
    {
        var cfg = config ?? _config;
        var source = new FakeBdhSource();
        foreach (var (objectType, numbers) in _source)
        {
            var prefix = objectType.StartsWith("Task", StringComparison.Ordinal) ? "T" : "R";
            source.Add($"{objectType}/dt=2026-07-15/part-0000.jsonl", string.Join("\n",
                numbers.Select(n =>
                    $$"""{"Id":"{{prefix}}{{n:D12}}","Name":"{{objectType}} {{n}}"}""")));
        }
        var fetcher = new BdhFetcher(cfg, source, FilterSet.Empty);
        return new Reconciler(cfg, _schema, fetcher, _graph, InventoryFactory);
    }

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
        // Indexed: Task 1 (ok), Task 9 (stale). Missing: Task 2, Risk 5.
        Seed(Connector, (Tid(1), "Task"), (Tid(9), "Task"));

        var report = await Make().ReconcileAsync();

        Assert.True(report.HasDrift);
        Assert.Equal(2, report.Objects.Count);
        var task = report.Objects.Single(o => o.ObjectName == "Task");
        Assert.Equal(2, task.SourceCount);
        Assert.Equal(2, task.IndexedCount);
        Assert.Equal(new[] { Tid(2) }, task.Missing);
        Assert.Equal(new[] { Tid(9) }, task.Stale);
        var risk = report.Objects.Single(o => o.ObjectName == "Risk");
        Assert.Equal(new[] { Rid(5) }, risk.Missing);
        Assert.Empty(risk.Stale);
        Assert.Equal(2, report.TotalMissing);
        Assert.Equal(1, report.TotalStale);
    }

    [Fact]
    public async Task Reconcile_NoDrift_WhenInventoryMatchesSource()
    {
        Seed(Connector, (Tid(1), "Task"), (Tid(2), "Task"), (Rid(5), "Risk"));
        var report = await Make().ReconcileAsync();
        Assert.False(report.HasDrift);
        Assert.False(report.HasRemainingDrift);
    }

    [Fact]
    public async Task Reconcile_TypeFilter_RestrictsScope()
    {
        Seed(Connector, (Tid(9), "Task"));
        var report = await Make().ReconcileAsync(onlyType: "task");
        var drift = Assert.Single(report.Objects);
        Assert.Equal("Task", drift.ObjectName);
    }

    // ── --fix ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Fix_DeletesStaleItems_AndPrunesInventory()
    {
        Seed(Connector, (Tid(1), "Task"), (Tid(9), "Task"), (Tid(8), "Task"));

        var report = await Make().ReconcileAsync(fix: true);

        var task = report.Objects.Single(o => o.ObjectName == "Task");
        Assert.Equal(2, task.FixedCount);
        Assert.Empty(task.FixFailures);
        Assert.Contains(_graph.Sent, s =>
            s.Method == HttpMethod.Delete
            && s.Path == $"external/connections/{Connector}/items/{Tid(9)}");
        using var inventory = InventoryFactory(Connector);
        Assert.Equal(new[] { Tid(1) }, inventory.IdsForObject("Task"));
        // Missing items remain drift (the next crawl handles them) — but no
        // stale drift remains. Missing: Task 2 plus never-ingested Risk 5.
        Assert.Equal(2, report.TotalMissing);
        Assert.True(report.HasRemainingDrift);
        Assert.Equal(0, report.TotalStale - report.TotalFixed);
    }

    [Fact]
    public async Task Fix_FailedDelete_ReportedAndKeptInInventory()
    {
        Seed(Connector, (Tid(9), "Task"));
        _graph.FailingItemIds.Add(Tid(9));

        var report = await Make().ReconcileAsync(fix: true);
        var task = report.Objects.Single(o => o.ObjectName == "Task");
        Assert.Equal(0, task.FixedCount);
        Assert.Single(task.FixFailures);
        using var inventory = InventoryFactory(Connector);
        Assert.Contains(Tid(9), inventory.IdsForObject("Task"));
    }

    // ── Row cap (truncated source fetch) ─────────────────────────────────────

    // A row-capped fetch saw an INCOMPLETE source id set: stale detection would
    // flag (and --fix would delete) live records, so the reconciler must report
    // counts only and never fix from a truncated fetch.
    [Fact]
    public async Task TruncatedFetch_SkipsStaleDetection_AndNeverFixes()
    {
        Seed(Connector, (Tid(9), "Task"));  // stale — but the fetch is capped
        var capped = TestConfig.Make(allowFullScan: true, maxRecordsPerObject: 1);

        var report = await Make(capped).ReconcileAsync(onlyType: "Task", fix: true);

        var task = Assert.Single(report.Objects);
        Assert.Equal(1, task.SourceCount);   // capped at 1 of the 2 source rows
        Assert.Equal(1, task.IndexedCount);
        Assert.Empty(task.Missing);          // drift not computed on a partial set
        Assert.Empty(task.Stale);
        Assert.Equal(0, task.FixedCount);
        Assert.DoesNotContain(_graph.Sent, s => s.Method == HttpMethod.Delete);
        using var inventory = InventoryFactory(Connector);
        Assert.Contains(Tid(9), inventory.IdsForObject("Task"));  // untouched
    }

    // ── Sharding ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sharded_ReconcilesAgainstOwningConnection()
    {
        using var scope = new EnvScope(
            (ShardingConfig.EnvVar, """{"shardA": ["Task"], "shardB": ["Risk"]}"""));
        Seed("shardA", (Tid(9), "Task"));   // stale, lives in shard A's inventory

        var report = await Make().ReconcileAsync(fix: true);

        var task = report.Objects.Single(o => o.ObjectName == "Task");
        Assert.Equal("shardA", task.ConnectionId);
        Assert.Contains(_graph.Sent, s =>
            s.Method == HttpMethod.Delete
            && s.Path == $"external/connections/shardA/items/{Tid(9)}");
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
