// Webhook processor routing: upsert → re-ingest, delete → withdraw,
// record-gone upsert → withdraw, unknown type dropped, debounce integration.
// All against mocked Clarizen + Graph, driven with a virtual clock.

using System.Net;
using System.Text.Json.Nodes;
using ClarizenConnector.AclEngine;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;
using ClarizenConnector.Item;
using ClarizenConnector.Webhook;

namespace ClarizenConnector.Tests;

public class WebhookProcessorTests : IDisposable
{
    private const string Connector = "ClarizenAdaptiveWork";

    private readonly TempDir _dir = new();
    private readonly SyncStateScope _stateScope = new();
    private readonly IdentityStore _store;
    private readonly AppConfig _config = TestConfig.Make();
    private readonly SchemaConfig _schema;
    private readonly FakeGraphClient _graph;
    private readonly ClarizenClient _clarizen;

    /// <summary>Local ids that currently exist in the mock Clarizen source.</summary>
    private readonly HashSet<string> _existing = new(StringComparer.Ordinal) { "1", "2" };

    public WebhookProcessorTests()
    {
        _store = new IdentityStore("WebhookProc", Path.Combine(_dir.Path, "identity.db"));
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

        var handler = new MockHttpHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/authentication/login"))
                return MockHttpHandler.Json(HttpStatusCode.OK, """{"sessionId": "s"}""");
            // RetrieveAsync issues "SELECT ... WHERE SYSID = <id>".
            var entities = new JsonArray();
            foreach (var id in _existing)
            {
                if (body.Contains($"SYSID = {id}"))
                {
                    entities.Add(new JsonObject
                    {
                        ["id"] = $"/Task/{id}",
                        ["Name"] = $"Task {id}",
                        ["Owner"] = new JsonObject { ["id"] = "/User/1" },
                    });
                }
            }
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

    public void Dispose()
    {
        _store.Dispose();
        _stateScope.Dispose();
        _dir.Dispose();
    }

    private Func<string, IItemInventory> InventoryFactory => connectionId =>
        new ItemInventory(connectionId, Path.Combine(_dir.Path, $"inventory_{connectionId}.db"));

    private (WebhookProcessor Processor, IngestPipeline Pipeline) Build()
    {
        var mapper = new PrincipalMapper(_store, "{}");
        var resolver = new AclResolver(
            mapper, new DirectorySnapshot(), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
        var pipeline = new IngestPipeline(
            _config, _schema, _clarizen, _graph, resolver, new ItemConverter(_config),
            ha: null, inventoryFactory: InventoryFactory);
        // Zero window so events are immediately ready in DrainOnceAsync.
        var processor = new WebhookProcessor(_schema, _clarizen, pipeline, TimeSpan.Zero);
        return (processor, pipeline);
    }

    private List<string> InventoryIds()
    {
        using var inventory = InventoryFactory(Connector);
        return inventory.IdsForObject("Task");
    }

    [Fact]
    public async Task Upsert_ReIngestsById()
    {
        var (processor, _) = Build();
        processor.Enqueue(new WebhookEvent("Task", "1", ChangeKind.Upsert));
        await processor.DrainOnceAsync();

        var put = Assert.Single(_graph.Sent, s => s.Method == HttpMethod.Put);
        Assert.Equal($"external/connections/{Connector}/items/Task_1", put.Path);
        Assert.Equal(new[] { "Task_1" }, InventoryIds());
    }

    [Fact]
    public async Task Delete_WithdrawsItem()
    {
        // Seed the inventory as if Task_2 had been ingested earlier.
        using (var inventory = InventoryFactory(Connector))
            inventory.RecordSeen(new[] { ("Task_2", "Task") }, DateTime.UtcNow);

        var (processor, _) = Build();
        processor.Enqueue(new WebhookEvent("Task", "2", ChangeKind.Delete));
        await processor.DrainOnceAsync();

        Assert.Contains(_graph.Sent, s =>
            s.Method == HttpMethod.Delete && s.Path == $"external/connections/{Connector}/items/Task_2");
        Assert.Empty(InventoryIds());
        // A delete never re-queries the source.
        Assert.DoesNotContain(_graph.Sent, s => s.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task Upsert_ForRecordGoneFromSource_Withdraws()
    {
        using (var inventory = InventoryFactory(Connector))
            inventory.RecordSeen(new[] { ("Task_9", "Task") }, DateTime.UtcNow);

        var (processor, _) = Build();
        // 9 is not in _existing → RetrieveAsync returns null → withdraw.
        processor.Enqueue(new WebhookEvent("Task", "9", ChangeKind.Upsert));
        await processor.DrainOnceAsync();

        Assert.Contains(_graph.Sent, s =>
            s.Method == HttpMethod.Delete && s.Path.EndsWith("items/Task_9"));
        Assert.DoesNotContain(_graph.Sent, s => s.Method == HttpMethod.Put);
        Assert.Empty(InventoryIds());
    }

    [Fact]
    public async Task UnknownObjectType_Dropped()
    {
        var (processor, _) = Build();
        processor.Enqueue(new WebhookEvent("Ghost", "1", ChangeKind.Upsert));
        await processor.DrainOnceAsync();
        Assert.Empty(_graph.Sent);
    }

    [Fact]
    public async Task DebounceWindow_HoldsUntilElapsed()
    {
        var mapper = new PrincipalMapper(_store, "{}");
        var resolver = new AclResolver(
            mapper, new DirectorySnapshot(), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
        var pipeline = new IngestPipeline(
            _config, _schema, _clarizen, _graph, resolver, new ItemConverter(_config),
            ha: null, inventoryFactory: InventoryFactory);

        var now = new DateTime(2026, 7, 12, 10, 0, 0, DateTimeKind.Utc);
        var processor = new WebhookProcessor(_schema, _clarizen, pipeline, TimeSpan.FromMilliseconds(1000))
        {
            UtcNow = () => now,
        };

        processor.Enqueue(new WebhookEvent("Task", "1", ChangeKind.Upsert));
        await processor.DrainOnceAsync();
        Assert.DoesNotContain(_graph.Sent, s => s.Method == HttpMethod.Put);  // window not elapsed

        now = now.AddMilliseconds(1100);
        await processor.DrainOnceAsync();
        Assert.Contains(_graph.Sent, s => s.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task ApplyFailure_DoesNotThrow()
    {
        var (processor, _) = Build();
        _graph.FailingItemIds.Add("Task_1");  // PUT fails
        processor.Enqueue(new WebhookEvent("Task", "1", ChangeKind.Upsert));
        await processor.DrainOnceAsync();  // must complete without throwing
        // Failed ingest is not recorded in the inventory.
        Assert.Empty(InventoryIds());
    }
}
