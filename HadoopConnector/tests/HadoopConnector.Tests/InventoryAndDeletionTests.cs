// Ingested-item inventory (SQLite + SQL Server backends) and the
// deletion/tombstone sweep: full-crawl existence sweeps, the mass-deletion
// safety guards, the row-cap partial-crawl skip, and inventory bookkeeping
// across success/failure paths.

using System.Net;
using HadoopConnector.AclEngine;
using HadoopConnector.Hdfs;
using HadoopConnector.Config;
using HadoopConnector.Filters;
using HadoopConnector.Graph;
using HadoopConnector.Item;

namespace HadoopConnector.Tests;

public class ItemInventoryTests
{
    private static ItemInventory Open(TempDir dir) =>
        new("InvTests", Path.Combine(dir.Path, "inventory.db"));

    [Fact]
    public void RecordSeen_Read_Remove_RoundTrip()
    {
        using var dir = new TempDir();
        using var inventory = Open(dir);

        inventory.RecordSeen(
            new[] { ("Task_1", "Task"), ("Task_2", "Task"), ("Project_1", "Project") },
            new DateTime(2026, 7, 12, 9, 0, 0, DateTimeKind.Utc));

        Assert.Equal(3, inventory.Count());
        Assert.Equal(new[] { "Task_1", "Task_2" }, inventory.IdsForObject("Task"));
        Assert.Equal(new[] { "Project_1" }, inventory.IdsForObject("Project"));
        Assert.Empty(inventory.IdsForObject("Ghost"));

        inventory.Remove(new[] { "Task_1" });
        Assert.Equal(new[] { "Task_2" }, inventory.IdsForObject("Task"));
        Assert.Equal(2, inventory.Count());
    }

    [Fact]
    public void RecordSeen_IsUpsert_NoDuplicates()
    {
        using var dir = new TempDir();
        using var inventory = Open(dir);
        inventory.RecordSeen(new[] { ("Task_1", "Task") }, DateTime.UtcNow);
        inventory.RecordSeen(new[] { ("Task_1", "Task") }, DateTime.UtcNow);
        Assert.Equal(1, inventory.Count());
    }

    [Fact]
    public void AllByObject_GroupsIds()
    {
        using var dir = new TempDir();
        using var inventory = Open(dir);
        inventory.RecordSeen(
            new[] { ("Task_1", "Task"), ("Risk_1", "Risk"), ("Task_2", "Task") }, DateTime.UtcNow);
        var all = inventory.AllByObject();
        Assert.Equal(2, all.Count);
        Assert.Equal(new[] { "Task_1", "Task_2" }, all["Task"]);
        Assert.Equal(new[] { "Risk_1" }, all["Risk"]);
    }

    [Fact]
    public void Persistence_SurvivesReopen()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "inventory.db");
        using (var inventory = new ItemInventory("InvTests", path))
        {
            inventory.RecordSeen(new[] { ("Task_9", "Task") }, DateTime.UtcNow);
        }
        using (var reopened = new ItemInventory("InvTests", path))
        {
            Assert.Equal(new[] { "Task_9" }, reopened.IdsForObject("Task"));
        }
    }

    [Fact]
    public void Remove_MissingIds_IsNoOp()
    {
        using var dir = new TempDir();
        using var inventory = Open(dir);
        inventory.RecordSeen(new[] { ("Task_1", "Task") }, DateTime.UtcNow);
        inventory.Remove(new[] { "Ghost_1" });
        Assert.Equal(1, inventory.Count());
    }
}

public class SqlServerItemInventoryTests
{
    [Fact]
    public void RecordSeen_UsesConnectorScopedMerge()
    {
        var sql = new FakeSqlGateway();
        var inventory = new SqlServerItemInventory("Conn", sql);
        inventory.RecordSeen(new[] { ("Task_1", "Task") }, DateTime.UtcNow);

        var call = Assert.Single(sql.Calls);
        Assert.Contains("MERGE dbo.ItemInventory", call.Sql);
        Assert.Equal("Conn", sql.ArgValue(0, "@connector"));
        Assert.Equal("Task_1", sql.ArgValue(0, "@item"));
    }

    [Fact]
    public void Reads_MapRowsAndCounts()
    {
        var sql = new FakeSqlGateway();
        var inventory = new SqlServerItemInventory("Conn", sql);

        sql.QueryResults.Enqueue(new List<Dictionary<string, object?>>
        {
            new() { ["ItemId"] = "Task_1" },
            new() { ["ItemId"] = "Task_2" },
        });
        Assert.Equal(new[] { "Task_1", "Task_2" }, inventory.IdsForObject("Task"));

        sql.QueryResults.Enqueue(new List<Dictionary<string, object?>>
        {
            new() { ["ItemId"] = "Task_1", ["ObjectType"] = "Task" },
            new() { ["ItemId"] = "Risk_1", ["ObjectType"] = "Risk" },
        });
        var all = inventory.AllByObject();
        Assert.Equal(new[] { "Task_1" }, all["Task"]);

        sql.ScalarResults.Enqueue(7);
        Assert.Equal(7, inventory.Count());

        inventory.Remove(new[] { "Task_1" });
        Assert.Contains(sql.Calls, c => c.Sql.Contains("DELETE FROM dbo.ItemInventory"));
    }
}

/// <summary>Deletion/tombstone sweep behaviour through the full pipeline.</summary>
public class DeletionSweepTests : IDisposable
{
    private const string Connector = "BdhHadoopMart";

    private readonly TempDir _dir = new();
    private readonly SyncStateScope _stateScope = new();
    private readonly IdentityStore _store;
    private readonly AppConfig _config = TestConfig.Make(allowFullScan: true);
    private readonly SchemaConfig _schema;
    private readonly FakeGraphClient _graph;

    /// <summary>Mutable source record-number set the BDH fixture serves.</summary>
    private readonly List<int> _sourceRecordNumbers = new() { 1, 2, 3 };

    /// <summary>Salesforce-shaped record id ("T000000000001", ...).</summary>
    private static string Tid(int n) => $"T{n:D12}";

    public DeletionSweepTests()
    {
        _store = new IdentityStore("SweepTests", Path.Combine(_dir.Path, "identity.db"));
        _store.Upsert(new PrincipalMapping(
            "005U0000001", "user", "o@example.com", "entra-owner", DateTime.UtcNow));

        _schema = new SchemaConfig
        {
            ObjectList = new List<ObjectConfig>
            {
                new()
                {
                    ObjectName = "Task",
                    DisplayName = "Task",
                    AclMode = "ownerOnly",
                    SelectedFields = new Dictionary<string, string>
                    {
                        ["Name"] = "Title",
                        ["OwnerId"] = "OwnerId",
                    },
                },
            },
        };
        _graph = new FakeGraphClient(_config);
    }

    public void Dispose()
    {
        _store.Dispose();
        _stateScope.Dispose();
        _dir.Dispose();
    }

    private Func<string, IItemInventory> InventoryFactory => connectionId =>
        new ItemInventory(connectionId, Path.Combine(_dir.Path, $"inventory_{connectionId}.db"));

    /// <summary>Builds a pipeline whose BDH source serves the CURRENT
    /// _sourceRecordNumbers set (rebuilt per call, like a fresh nightly export).</summary>
    private IngestPipeline Pipeline(AppConfig? config = null)
    {
        var cfg = config ?? _config;
        var source = new FakeBdhSource();
        source.Add("Task/dt=2026-07-15/part-0000.jsonl", string.Join("\n",
            _sourceRecordNumbers.Select(n =>
                $$"""{"Id":"{{Tid(n)}}","Name":"Task {{n}}","OwnerId":"005U0000001"}""")));
        var fetcher = new BdhFetcher(cfg, source, FilterSet.Empty);
        var resolver = new AclResolver(
            new PrincipalMapper(_store), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
        return new IngestPipeline(
            cfg, _schema, fetcher, _graph, resolver, new ItemConverter(cfg),
            ha: null, inventoryFactory: InventoryFactory);
    }

    private List<string> InventoryIds()
    {
        using var inventory = InventoryFactory(Connector);
        return inventory.IdsForObject("Task");
    }

    [Fact]
    public async Task FullCrawl_RecordsSuccessfulPutsInInventory()
    {
        var summary = await Pipeline().RunAsync(fullCrawl: true);
        Assert.Equal(3, summary.Ingested);
        Assert.Equal(0, summary.Deleted);
        Assert.Equal(new[] { Tid(1), Tid(2), Tid(3) }, InventoryIds());
    }

    [Fact]
    public async Task FailedPuts_AreNotRecordedInInventory()
    {
        _graph.FailingItemIds.Add(Tid(2));
        await Pipeline().RunAsync(fullCrawl: true);
        Assert.Equal(new[] { Tid(1), Tid(3) }, InventoryIds());
    }

    [Fact]
    public async Task SecondFullCrawl_DeletesRecordsRemovedFromSource()
    {
        await Pipeline().RunAsync(fullCrawl: true);
        Assert.Equal(3, InventoryIds().Count);

        // Record 3 vanishes from BDH; the next full crawl must withdraw it.
        _sourceRecordNumbers.Remove(3);
        var summary = await Pipeline().RunAsync(fullCrawl: true);

        Assert.Equal(1, summary.Deleted);
        Assert.Equal(new[] { Tid(1), Tid(2) }, InventoryIds());
        Assert.Contains(_graph.Sent, s =>
            s.Method == HttpMethod.Delete
            && s.Path == $"external/connections/{Connector}/items/{Tid(3)}");
    }

    [Fact]
    public async Task MultipleStaleItems_AreDeletedViaBatch()
    {
        await Pipeline().RunAsync(fullCrawl: true);
        _sourceRecordNumbers.Clear();
        _sourceRecordNumbers.Add(1);  // 2 of 3 stale (66% — under-guard inventory of 3)

        var summary = await Pipeline().RunAsync(fullCrawl: true);
        Assert.Equal(2, summary.Deleted);
        Assert.Equal(new[] { Tid(1) }, InventoryIds());
        // Two ids → one $batch with DELETE entries.
        var batch = _graph.Sent.Last(s => s.Path == "$batch");
        var methods = batch.Body!["requests"]!.AsArray()
            .Select(r => r!["method"]!.GetValue<string>())
            .Distinct()
            .ToList();
        Assert.Equal(new[] { "DELETE" }, methods);
    }

    [Fact]
    public async Task IncrementalCrawl_NeverSweeps()
    {
        await Pipeline().RunAsync(fullCrawl: true);
        // Seed a stale id, then run an incremental — it must survive.
        using (var inventory = InventoryFactory(Connector))
        {
            inventory.RecordSeen(new[] { (Tid(999), "Task") }, DateTime.UtcNow);
        }
        var summary = await Pipeline().RunAsync(fullCrawl: false);
        Assert.Equal(0, summary.Deleted);
        Assert.Contains(Tid(999), InventoryIds());
        Assert.DoesNotContain(_graph.Sent, s => s.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task DeletionSyncDisabled_SkipsSweep()
    {
        using var scope = new EnvScope(("DELETION_SYNC", "false"));
        await Pipeline().RunAsync(fullCrawl: true);
        _sourceRecordNumbers.Remove(3);
        var summary = await Pipeline().RunAsync(fullCrawl: true);
        Assert.Equal(0, summary.Deleted);
        Assert.Contains(Tid(3), InventoryIds());
    }

    // Row cap (BDH_MAX_RECORDS_PER_OBJECT): a truncated fetch saw an INCOMPLETE
    // source id set, so the sweep must be skipped and the object recorded as
    // partial — otherwise live records would be mass-deleted as "stale".
    [Fact]
    public async Task RowCap_TruncatedFetch_SkipsSweep_AndRecordsPartialObject()
    {
        using (var inventory = InventoryFactory(Connector))
        {
            inventory.RecordSeen(new[] { (Tid(999), "Task") }, DateTime.UtcNow);
        }
        var capped = TestConfig.Make(allowFullScan: true, maxRecordsPerObject: 2);

        var summary = await Pipeline(capped).RunAsync(fullCrawl: true);

        Assert.Equal(new[] { "Task" }, summary.PartialObjects);
        Assert.Equal(new[] { "Task" }, summary.SweepSkipped);
        Assert.Equal(2, summary.Ingested);  // capped at 2 of the 3 source rows
        Assert.Equal(0, summary.Deleted);
        Assert.Contains(Tid(999), InventoryIds());  // stale id untouched
        Assert.DoesNotContain(_graph.Sent, s => s.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task SafetyGuard_SkipsImplausibleMassDeletion_AndAlerts()
    {
        // Seed a large inventory (≥ MinInventoryForSafetyGuard) whose ids are
        // mostly absent from the 3-record source → sweep must refuse.
        using (var inventory = InventoryFactory(Connector))
        {
            inventory.RecordSeen(
                Enumerable.Range(100, 30).Select(n => (Tid(n), "Task")), DateTime.UtcNow);
        }

        using var alertScope = new EnvScope(
            (Infrastructure.Alerting.WebhookUrlEnvVar, "https://hooks.example/x"),
            ("DELETION_SYNC_MAX_PERCENT", null));
        var alertHandler = new MockHttpHandler((_, _) =>
            MockHttpHandler.Json(HttpStatusCode.OK, "{}"));
        Infrastructure.Alerting.HttpClient = new HttpClient(alertHandler);

        var summary = await Pipeline().RunAsync(fullCrawl: true);

        Assert.Equal(0, summary.Deleted);
        Assert.Equal(new[] { "Task" }, summary.SweepSkipped);
        Assert.DoesNotContain(_graph.Sent, s => s.Method == HttpMethod.Delete);
        Assert.Equal(33, InventoryIds().Count);  // 30 seeded + 3 ingested, untouched

        var alert = Assert.Single(alertHandler.Requests);
        Assert.Contains("deletion_sweep_skipped", alert.Body);
    }

    // Regression (MEDIUM-2): a tiny (< MinInventoryForSafetyGuard) inventory that
    // goes ALL-stale because the full-crawl source returned zero rows (outage /
    // truncated nightly export) must be refused — before the fix the percent
    // guard's >= 20 floor let a 19-item connection be wiped 100% unguarded.
    [Fact]
    public async Task SmallAllStaleInventory_EmptySource_IsBlocked()
    {
        using (var inventory = InventoryFactory(Connector))
        {
            inventory.RecordSeen(
                Enumerable.Range(1, 19).Select(n => (Tid(n), "Task")), DateTime.UtcNow);
        }
        _sourceRecordNumbers.Clear();  // source returns zero rows

        var summary = await Pipeline().RunAsync(fullCrawl: true);

        Assert.Equal(0, summary.Deleted);
        Assert.Equal(new[] { "Task" }, summary.SweepSkipped);
        Assert.Equal(19, InventoryIds().Count);  // nothing wiped
        Assert.DoesNotContain(_graph.Sent, s => s.Method == HttpMethod.Delete);
    }

    // The small-inventory guard still honours the explicit 100 = "disable"
    // escape hatch, so an operator can force the wipe after confirming the drop.
    [Fact]
    public async Task SmallAllStaleInventory_EmptySource_CanBeWidened_ViaMaxPercent()
    {
        using (var inventory = InventoryFactory(Connector))
        {
            inventory.RecordSeen(
                Enumerable.Range(1, 19).Select(n => (Tid(n), "Task")), DateTime.UtcNow);
        }
        _sourceRecordNumbers.Clear();
        using var scope = new EnvScope(("DELETION_SYNC_MAX_PERCENT", "100"));

        var summary = await Pipeline().RunAsync(fullCrawl: true);

        Assert.Equal(19, summary.Deleted);
        Assert.Empty(summary.SweepSkipped);
        Assert.Empty(InventoryIds());
    }

    // Guard rail: a NON-empty source with a high stale percentage on a small
    // inventory is still allowed (the empty-source signal is what engages the
    // guard below the size floor — a healthy source that simply shed items does
    // not). Complements MultipleStaleItems_AreDeletedViaBatch.
    [Fact]
    public async Task SmallInventory_NonEmptySource_HighStalePercent_StillSweeps()
    {
        using (var inventory = InventoryFactory(Connector))
        {
            inventory.RecordSeen(
                Enumerable.Range(1, 10).Select(n => (Tid(n), "Task")), DateTime.UtcNow);
        }
        // Source still returns rows (1,2,3) → not an outage; 7 of 10 go stale.
        var summary = await Pipeline().RunAsync(fullCrawl: true);

        Assert.Equal(7, summary.Deleted);
        Assert.Empty(summary.SweepSkipped);
        Assert.Equal(new[] { Tid(1), Tid(2), Tid(3) }, InventoryIds());
    }

    [Fact]
    public async Task SafetyGuard_CanBeWidened_ViaMaxPercent()
    {
        using (var inventory = InventoryFactory(Connector))
        {
            inventory.RecordSeen(
                Enumerable.Range(100, 30).Select(n => (Tid(n), "Task")), DateTime.UtcNow);
        }
        using var scope = new EnvScope(("DELETION_SYNC_MAX_PERCENT", "100"));

        var summary = await Pipeline().RunAsync(fullCrawl: true);
        Assert.Equal(30, summary.Deleted);
        Assert.Empty(summary.SweepSkipped);
        Assert.Equal(new[] { Tid(1), Tid(2), Tid(3) }, InventoryIds());
    }

    [Fact]
    public async Task FailedDeletes_StayInInventoryForNextSweep()
    {
        await Pipeline().RunAsync(fullCrawl: true);
        _sourceRecordNumbers.Remove(3);
        _graph.FailingItemIds.Add(Tid(3));  // DELETE for record 3 fails

        var summary = await Pipeline().RunAsync(fullCrawl: true);
        Assert.Equal(0, summary.Deleted);
        Assert.Contains(Tid(3), InventoryIds());  // retried on the next sweep
    }

    [Fact]
    public async Task AbsoluteCap_GuardsSmallInventories_BelowPercentGuardFloor()
    {
        // Regression: with an inventory below MinInventoryForSafetyGuard (20)
        // the percent guard never engaged, so ANY fraction — including 100% —
        // of a small object type could be mass-deleted. The absolute
        // DELETION_SYNC_MAX_ITEMS cap must engage at any inventory size.
        using (var inventory = InventoryFactory(Connector))
        {
            inventory.RecordSeen(
                Enumerable.Range(100, 10).Select(n => (Tid(n), "Task")), DateTime.UtcNow);
        }
        using var scope = new EnvScope(("DELETION_SYNC_MAX_ITEMS", "5"));

        var summary = await Pipeline().RunAsync(fullCrawl: true);  // 10 stale > cap of 5

        Assert.Equal(0, summary.Deleted);
        Assert.Equal(new[] { "Task" }, summary.SweepSkipped);
        Assert.DoesNotContain(_graph.Sent, s => s.Method == HttpMethod.Delete);
        Assert.Equal(13, InventoryIds().Count);  // 10 seeded + 3 ingested, untouched
    }

    [Fact]
    public async Task NegativeMaxPercent_DoesNotDisableGuard()
    {
        // Regression: DELETION_SYNC_MAX_PERCENT=-1 used to silently disable
        // the percent guard. It must fall back to the default (25) instead.
        using (var inventory = InventoryFactory(Connector))
        {
            inventory.RecordSeen(
                Enumerable.Range(100, 30).Select(n => (Tid(n), "Task")), DateTime.UtcNow);
        }
        using var scope = new EnvScope(("DELETION_SYNC_MAX_PERCENT", "-1"));

        var summary = await Pipeline().RunAsync(fullCrawl: true);  // 30/33 stale ≫ 25%

        Assert.Equal(0, summary.Deleted);
        Assert.Equal(new[] { "Task" }, summary.SweepSkipped);
        Assert.DoesNotContain(_graph.Sent, s => s.Method == HttpMethod.Delete);
    }

    [Fact]
    public void GuardEnvParsing_ClampsAndDefaults()
    {
        using (new EnvScope(("DELETION_SYNC_MAX_PERCENT", "-1")))
            Assert.Equal(25, IngestPipeline.DeletionSyncMaxPercent());
        using (new EnvScope(("DELETION_SYNC_MAX_PERCENT", "150")))
            Assert.Equal(25, IngestPipeline.DeletionSyncMaxPercent());
        using (new EnvScope(("DELETION_SYNC_MAX_PERCENT", "0")))
            Assert.Equal(0, IngestPipeline.DeletionSyncMaxPercent());   // explicit disable
        using (new EnvScope(("DELETION_SYNC_MAX_PERCENT", "40")))
            Assert.Equal(40, IngestPipeline.DeletionSyncMaxPercent());
        using (new EnvScope(("DELETION_SYNC_MAX_PERCENT", null)))
            Assert.Equal(25, IngestPipeline.DeletionSyncMaxPercent());

        using (new EnvScope(("DELETION_SYNC_MAX_ITEMS", "-5")))
            Assert.Equal(
                IngestPipeline.DefaultDeletionSyncMaxItems, IngestPipeline.DeletionSyncMaxItems());
        using (new EnvScope(("DELETION_SYNC_MAX_ITEMS", "0")))
            Assert.Equal(0, IngestPipeline.DeletionSyncMaxItems());     // explicit disable
        using (new EnvScope(("DELETION_SYNC_MAX_ITEMS", "42")))
            Assert.Equal(42, IngestPipeline.DeletionSyncMaxItems());
        using (new EnvScope(("DELETION_SYNC_MAX_ITEMS", null)))
            Assert.Equal(
                IngestPipeline.DefaultDeletionSyncMaxItems, IngestPipeline.DeletionSyncMaxItems());
    }
}
