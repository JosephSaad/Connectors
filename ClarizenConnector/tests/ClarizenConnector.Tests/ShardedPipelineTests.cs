// End-to-end pipeline tests for connection sharding (per-shard routing and
// state isolation) and worker-crash resilience (close-with-failed-claims
// behaviour on the single-node path), all against mocked Clarizen + Graph.

using System.Net;
using System.Text.Json.Nodes;
using ClarizenConnector.AclEngine;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;
using ClarizenConnector.Item;

namespace ClarizenConnector.Tests;

public class ShardedPipelineTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SyncStateScope _stateScope = new();
    private readonly IdentityStore _store;
    private readonly AppConfig _config = TestConfig.Make(connectorId: "BaseConn");
    private readonly SchemaConfig _schema;
    private readonly FakeGraphClient _graph;
    private readonly ClarizenClient _clarizen;
    private readonly MockHttpHandler _clarizenHandler;

    public ShardedPipelineTests()
    {
        _store = new IdentityStore("ShardTests", Path.Combine(_dir.Path, "identity.db"));
        _store.Upsert(new PrincipalMapping("/User/1", "user", "o@example.com", "entra-owner", DateTime.UtcNow));

        _schema = new SchemaConfig
        {
            ObjectList = new List<ObjectConfig>
            {
                MakeObject("Alpha"),
                MakeObject("Beta"),
            },
        };

        // One record per object type; queries against "Crash" blow up with a
        // non-retryable 400 (worker-crash scenario).
        _clarizenHandler = new MockHttpHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/authentication/login"))
                return MockHttpHandler.Json(HttpStatusCode.OK, """{"sessionId": "s"}""");
            if (body.Contains("FROM Crash"))
                return MockHttpHandler.Json(HttpStatusCode.BadRequest, """{"errorCode": "QueryError"}""");
            var objectType = body.Contains("FROM Alpha") ? "Alpha" : "Beta";
            var page = new JsonObject
            {
                ["entities"] = new JsonArray(new JsonObject
                {
                    ["id"] = $"/{objectType}/1",
                    ["Name"] = $"{objectType} One",
                    ["Owner"] = new JsonObject { ["id"] = "/User/1" },
                }),
                ["paging"] = new JsonObject { ["hasMore"] = false },
            };
            return MockHttpHandler.Json(HttpStatusCode.OK, page.ToJsonString());
        });
        _clarizen = new ClarizenClient(
            _config, new ApiBudget(1_000_000, callsPerMinute: 6_000_000), _clarizenHandler);
        _clarizen.DelayAsync = (_, _) => Task.CompletedTask;
        _graph = new FakeGraphClient(_config);
    }

    private static ObjectConfig MakeObject(string name) => new()
    {
        ObjectName = name,
        DisplayName = name,
        AclMode = "ownerOnly",
        SelectedFields = new Dictionary<string, string>
        {
            ["Name"] = "Title",
            ["Owner"] = "Owner",
            ["LastUpdatedOn"] = "LastUpdatedOn",
        },
    };

    internal Func<string, IItemInventory> InventoryFactory => connectionId =>
        new ItemInventory(connectionId, Path.Combine(_dir.Path, $"inventory_{connectionId}.db"));

    private IngestPipeline Pipeline(SchemaConfig? schema = null)
    {
        var mapper = new PrincipalMapper(_store, "{}");
        var resolver = new AclResolver(
            mapper, new DirectorySnapshot(), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
        return new IngestPipeline(
            _config, schema ?? _schema, _clarizen, _graph, resolver, new ItemConverter(_config),
            ha: null, inventoryFactory: InventoryFactory);
    }

    public void Dispose()
    {
        _store.Dispose();
        _stateScope.Dispose();
        _dir.Dispose();
    }

    private const string ShardMap = """{"shardA": ["Alpha"], "shardB": ["Beta"]}""";

    [Fact]
    public async Task ShardedRun_RoutesEachObjectTypeToItsOwnConnection()
    {
        using var scope = new EnvScope((ShardingConfig.EnvVar, ShardMap));
        var summary = await Pipeline().RunAsync(fullCrawl: true);

        Assert.Equal(2, summary.Ingested);
        Assert.Contains(_graph.Sent, s =>
            s.Method == HttpMethod.Put && s.Path == "external/connections/shardA/items/Alpha_1");
        Assert.Contains(_graph.Sent, s =>
            s.Method == HttpMethod.Put && s.Path == "external/connections/shardB/items/Beta_1");
        Assert.DoesNotContain(_graph.Sent, s => s.Path.Contains("/BaseConn/"));
    }

    [Fact]
    public async Task ShardedRun_KeepsPerShardStateKeys()
    {
        using var scope = new EnvScope((ShardingConfig.EnvVar, ShardMap));
        await Pipeline().RunAsync(fullCrawl: true);

        // Each shard records its own sync timestamp; the base id records none.
        Assert.NotNull(SyncState.ReadLastSync("shardA"));
        Assert.NotNull(SyncState.ReadLastSync("shardB"));
        Assert.Null(SyncState.ReadLastSync("BaseConn"));
        Assert.Null(SyncState.ReadCheckpoint("shardA"));
        Assert.Null(SyncState.ReadCheckpoint("shardB"));
    }

    [Fact]
    public async Task ShardedRun_ObjectFilterStillApplies()
    {
        using var scope = new EnvScope((ShardingConfig.EnvVar, ShardMap));
        var summary = await Pipeline().RunAsync(fullCrawl: true, onlyObjects: new[] { "Beta" });

        Assert.Equal(1, summary.Ingested);
        Assert.DoesNotContain(_graph.Sent, s => s.Path.Contains("Alpha_1"));
        // Restricted runs never advance the delta cursor — for any shard.
        Assert.Null(SyncState.ReadLastSync("shardA"));
        Assert.Null(SyncState.ReadLastSync("shardB"));
    }

    [Fact]
    public async Task MisconfiguredShardMap_AbortsTheRun()
    {
        using var scope = new EnvScope((ShardingConfig.EnvVar, """{"shardA": ["Alpha"]}"""));
        var exc = await Assert.ThrowsAsync<ArgumentException>(
            () => Pipeline().RunAsync(fullCrawl: true));
        Assert.Contains("not assigned to any shard", exc.Message);
        Assert.DoesNotContain(_graph.Sent, s => s.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task IngestSingle_RoutesToTheOwningShardConnection()
    {
        using var scope = new EnvScope((ShardingConfig.EnvVar, ShardMap));
        var record = new ClarizenRecord("Beta", new JsonObject
        {
            ["id"] = "/Beta/7",
            ["Name"] = "Solo",
            ["Owner"] = new JsonObject { ["id"] = "/User/1" },
        });
        var (ok, _) = await Pipeline().IngestSingleAsync(record, _schema.FindObject("Beta")!);
        Assert.True(ok);
        var put = Assert.Single(_graph.Sent, s => s.Method == HttpMethod.Put);
        Assert.Equal("external/connections/shardB/items/Beta_7", put.Path);
    }

    // ── Worker-crash resilience (single-node close-with-failed semantics) ────

    [Fact]
    public async Task ObjectWorkerCrash_IsDeadLettered_CrawlStillCompletes()
    {
        using var scope = new EnvScope((ShardingConfig.EnvVar, null));
        var schema = new SchemaConfig
        {
            ObjectList = new List<ObjectConfig> { MakeObject("Crash"), MakeObject("Alpha") },
        };

        var summary = await Pipeline(schema).RunAsync(fullCrawl: true);

        // The crash is terminal for its object type but not for the crawl.
        Assert.Equal(new[] { "Crash" }, summary.FailedObjects);
        Assert.Equal(1, summary.Ingested);  // Alpha still ingested
        Assert.False(summary.Stopped);

        var entry = Assert.Single(SyncState.ReadFailedRecords("BaseConn"));
        Assert.Equal("WORKER_CRASH", entry["item_id"]!.GetValue<string>());
        Assert.Equal("Crash", entry["object_type"]!.GetValue<string>());

        // The crawl completed: sync timestamp recorded, checkpoint cleared —
        // dead-letter retry is the recovery path for the failed object.
        Assert.NotNull(SyncState.ReadLastSync("BaseConn"));
        Assert.Null(SyncState.ReadCheckpoint("BaseConn"));
    }
}
