// Ingested-item inventory (SQLite + SQL Server backends) and the
// deletion/tombstone sweep: full-crawl existence sweeps, the mass-deletion
// safety guard, and inventory bookkeeping across success/failure paths.

using System.Net;
using System.Text.Json.Nodes;
using ClarizenConnector.AclEngine;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;
using ClarizenConnector.Item;

namespace ClarizenConnector.Tests;

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
    private const string Connector = "ClarizenAdaptiveWork";

    private readonly TempDir _dir = new();
    private readonly SyncStateScope _stateScope = new();
    private readonly IdentityStore _store;
    private readonly AppConfig _config = TestConfig.Make();
    private readonly SchemaConfig _schema;
    private readonly FakeGraphClient _graph;
    private readonly ClarizenClient _clarizen;

    /// <summary>Mutable source id set the Clarizen mock serves ("/Task/{n}").</summary>
    private readonly List<int> _sourceRecordNumbers = new() { 1, 2, 3 };

    public DeletionSweepTests()
    {
        _store = new IdentityStore("SweepTests", Path.Combine(_dir.Path, "identity.db"));
        _store.Upsert(new PrincipalMapping("/User/1", "user", "o@example.com", "entra-owner", DateTime.UtcNow));

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
                        ["Owner"] = "Owner",
                        ["LastUpdatedOn"] = "LastUpdatedOn",
                    },
                },
            },
        };

        var handler = new MockHttpHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/authentication/login"))
                return MockHttpHandler.Json(HttpStatusCode.OK, """{"sessionId": "s"}""");
            var entities = new JsonArray();
            foreach (var n in _sourceRecordNumbers)
            {
                entities.Add(new JsonObject
                {
                    ["id"] = $"/Task/{n}",
                    ["Name"] = $"Task {n}",
                    ["Owner"] = new JsonObject { ["id"] = "/User/1" },
                });
            }
            var page = new JsonObject
            {
                ["entities"] = entities,
                ["paging"] = new JsonObject { ["hasMore"] = false },
            };
            return MockHttpHandler.Json(HttpStatusCode.OK, page.ToJsonString());
        });
        _clarizen = new ClarizenClient(
            _config, new ApiBudget(1_000_000, callsPerMinute: 6_000_000), handler);
        _clarizen.DelayAsync = (_, _) => Task.CompletedTask;
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

    private IngestPipeline Pipeline()
    {
        var mapper = new PrincipalMapper(_store, "{}");
        var resolver = new AclResolver(
            mapper, new DirectorySnapshot(), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
        return new IngestPipeline(
            _config, _schema, _clarizen, _graph, resolver, new ItemConverter(_config),
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
        Assert.Equal(new[] { "Task_1", "Task_2", "Task_3" }, InventoryIds());
    }

    [Fact]
    public async Task FailedPuts_AreNotRecordedInInventory()
    {
        _graph.FailingItemIds.Add("Task_2");
        await Pipeline().RunAsync(fullCrawl: true);
        Assert.Equal(new[] { "Task_1", "Task_3" }, InventoryIds());
    }

    [Fact]
    public async Task SecondFullCrawl_DeletesRecordsRemovedFromSource()
    {
        await Pipeline().RunAsync(fullCrawl: true);
        Assert.Equal(3, InventoryIds().Count);

        // Task 3 vanishes from Clarizen; the next full crawl must withdraw it.
        _sourceRecordNumbers.Remove(3);
        var summary = await Pipeline().RunAsync(fullCrawl: true);

        Assert.Equal(1, summary.Deleted);
        Assert.Equal(new[] { "Task_1", "Task_2" }, InventoryIds());
        Assert.Contains(_graph.Sent, s =>
            s.Method == HttpMethod.Delete
            && s.Path == $"external/connections/{Connector}/items/Task_3");
    }

    [Fact]
    public async Task MultipleStaleItems_AreDeletedViaBatch()
    {
        await Pipeline().RunAsync(fullCrawl: true);
        _sourceRecordNumbers.Clear();
        _sourceRecordNumbers.Add(1);  // 2 of 3 stale (66% — under-guard inventory of 3)

        var summary = await Pipeline().RunAsync(fullCrawl: true);
        Assert.Equal(2, summary.Deleted);
        Assert.Equal(new[] { "Task_1" }, InventoryIds());
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
            inventory.RecordSeen(new[] { ("Task_999", "Task") }, DateTime.UtcNow);
        }
        var summary = await Pipeline().RunAsync(fullCrawl: false);
        Assert.Equal(0, summary.Deleted);
        Assert.Contains("Task_999", InventoryIds());
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
        Assert.Contains("Task_3", InventoryIds());
    }

    [Fact]
    public async Task SafetyGuard_SkipsImplausibleMassDeletion_AndAlerts()
    {
        // Seed a large inventory (≥ MinInventoryForSafetyGuard) whose ids are
        // mostly absent from the 3-record source → sweep must refuse.
        using (var inventory = InventoryFactory(Connector))
        {
            inventory.RecordSeen(
                Enumerable.Range(100, 30).Select(n => ($"Task_{n}", "Task")), DateTime.UtcNow);
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

    [Fact]
    public async Task SafetyGuard_CanBeWidened_ViaMaxPercent()
    {
        using (var inventory = InventoryFactory(Connector))
        {
            inventory.RecordSeen(
                Enumerable.Range(100, 30).Select(n => ($"Task_{n}", "Task")), DateTime.UtcNow);
        }
        using var scope = new EnvScope(("DELETION_SYNC_MAX_PERCENT", "100"));

        var summary = await Pipeline().RunAsync(fullCrawl: true);
        Assert.Equal(30, summary.Deleted);
        Assert.Empty(summary.SweepSkipped);
        Assert.Equal(new[] { "Task_1", "Task_2", "Task_3" }, InventoryIds());
    }

    [Fact]
    public async Task FailedDeletes_StayInInventoryForNextSweep()
    {
        await Pipeline().RunAsync(fullCrawl: true);
        _sourceRecordNumbers.Remove(3);
        _graph.FailingItemIds.Add("Task_3");  // DELETE for Task_3 fails

        var summary = await Pipeline().RunAsync(fullCrawl: true);
        Assert.Equal(0, summary.Deleted);
        Assert.Contains("Task_3", InventoryIds());  // retried on the next sweep
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
                Enumerable.Range(100, 10).Select(n => ($"Task_{n}", "Task")), DateTime.UtcNow);
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
                Enumerable.Range(100, 30).Select(n => ($"Task_{n}", "Task")), DateTime.UtcNow);
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
