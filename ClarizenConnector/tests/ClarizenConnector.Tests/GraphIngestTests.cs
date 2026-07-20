using System.Net;
using System.Text.Json.Nodes;
using ClarizenConnector.AclEngine;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;
using ClarizenConnector.Item;

namespace ClarizenConnector.Tests;

/// <summary>GraphClient stand-in: scripts responses per request, records everything.</summary>
public sealed class FakeGraphClient : GraphClient
{
    public List<(HttpMethod Method, string Path, JsonNode? Body)> Sent { get; } = new();

    /// <summary>Item ids that should fail inside a $batch (status 400).</summary>
    public HashSet<string> FailingItemIds { get; } = new(StringComparer.Ordinal);

    /// <summary>When true, every request fails fast as if the Graph circuit were open.</summary>
    public bool CircuitOpen { get; set; }

    public FakeGraphClient(AppConfig config) : base(config)
    {
        OverrideToken = "fake";
    }

    public override Task<GraphResponse> SendWithRetryAsync(
        HttpMethod method, string path, JsonNode? body, CancellationToken ct = default)
    {
        if (CircuitOpen)
        {
            return Task.FromResult(new GraphResponse
            {
                StatusCode = HttpStatusCode.ServiceUnavailable,
                RawBody = "circuit open",
                CircuitOpen = true,
            });
        }

        Sent.Add((method, path, body?.DeepClone()));

        if (path == "$batch" && body is JsonObject envelope
            && envelope["requests"] is JsonArray requests)
        {
            var responses = new JsonArray();
            foreach (var request in requests)
            {
                var requestId = request!["id"]!.GetValue<string>();
                // PUT entries carry the item id in the body; DELETE entries
                // have no body — take the last url segment instead.
                var url = request["url"]!.GetValue<string>();
                var itemId = request["body"] is JsonObject itemBody
                    ? itemBody["id"]!.GetValue<string>()
                    : url[(url.LastIndexOf('/') + 1)..];
                responses.Add(new JsonObject
                {
                    ["id"] = requestId,
                    ["status"] = FailingItemIds.Contains(itemId) ? 400 : 200,
                    ["body"] = FailingItemIds.Contains(itemId)
                        ? new JsonObject { ["error"] = "bad item" }
                        : null,
                });
            }
            return Task.FromResult(new GraphResponse
            {
                StatusCode = HttpStatusCode.OK,
                Body = new JsonObject { ["responses"] = responses },
            });
        }

        if (method == HttpMethod.Put || method == HttpMethod.Delete)
        {
            var itemId = path[(path.LastIndexOf('/') + 1)..];
            return Task.FromResult(new GraphResponse
            {
                StatusCode = FailingItemIds.Contains(itemId)
                    ? HttpStatusCode.BadRequest
                    : HttpStatusCode.OK,
                RawBody = FailingItemIds.Contains(itemId) ? "bad item" : string.Empty,
            });
        }

        return Task.FromResult(new GraphResponse { StatusCode = HttpStatusCode.OK });
    }
}

public class GraphIngestTests
{
    private const string Connector = "ClarizenAdaptiveWork";

    // ── Pure helpers ─────────────────────────────────────────────────────────

    [Fact]
    public void Chunk_SplitsEvenly()
    {
        var chunks = IngestPipeline.Chunk(Enumerable.Range(1, 10).ToList(), 4);
        Assert.Equal(3, chunks.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, chunks[0]);
        Assert.Equal(new[] { 9, 10 }, chunks[2]);
    }

    [Fact]
    public void ParseBatchFailures_AllSuccess()
    {
        var body = JsonNode.Parse("""
            {"responses": [{"id": "1", "status": 200}, {"id": "2", "status": 201}]}
            """);
        var map = new Dictionary<string, string> { ["1"] = "Task_1", ["2"] = "Task_2" };
        Assert.Empty(IngestPipeline.ParseBatchFailures(body, map));
    }

    [Fact]
    public void ParseBatchFailures_MixedResults()
    {
        var body = JsonNode.Parse("""
            {"responses": [
                {"id": "1", "status": 200},
                {"id": "2", "status": 429, "body": {"error": "throttled"}},
                {"id": "3", "status": 500}
            ]}
            """);
        var map = new Dictionary<string, string>
        {
            ["1"] = "Task_1", ["2"] = "Task_2", ["3"] = "Task_3",
        };
        var failures = IngestPipeline.ParseBatchFailures(body, map);
        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, f => f.ItemId == "Task_2" && f.Error.Contains("429"));
        Assert.Contains(failures, f => f.ItemId == "Task_3" && f.Error.Contains("500"));
    }

    [Fact]
    public void ParseBatchFailures_MissingResponseEntry_IsFailure()
    {
        var body = JsonNode.Parse("""{"responses": [{"id": "1", "status": 200}]}""");
        var map = new Dictionary<string, string> { ["1"] = "Task_1", ["2"] = "Task_2" };
        var failure = Assert.Single(IngestPipeline.ParseBatchFailures(body, map));
        Assert.Equal("Task_2", failure.ItemId);
    }

    [Fact]
    public void ParseBatchFailures_MalformedEnvelope_FailsEverything()
    {
        var map = new Dictionary<string, string> { ["1"] = "Task_1", ["2"] = "Task_2" };
        Assert.Equal(2, IngestPipeline.ParseBatchFailures(JsonNode.Parse("{}"), map).Count);
        Assert.Equal(2, IngestPipeline.ParseBatchFailures(null, map).Count);
    }

    // ── Pipeline fixture ─────────────────────────────────────────────────────

    internal sealed class Fixture : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly SyncStateScope StateScope = new();
        public readonly AppConfig Config;
        public readonly SchemaConfig Schema;
        public readonly FakeGraphClient Graph;
        public readonly ClarizenClient Clarizen;
        public readonly IdentityStore Store;
        public readonly MockHttpHandler ClarizenHandler;
        public int ClarizenQueryCalls;

        public Fixture(int recordCount = 3, int chunkSize = 2, int batchSize = 20)
        {
            Config = TestConfig.Make(ingestChunkSize: chunkSize, graphBatchSize: batchSize);
            Schema = new SchemaConfig
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

            var entities = new JsonArray();
            for (var i = 1; i <= recordCount; i++)
            {
                entities.Add(new JsonObject
                {
                    ["id"] = $"/Task/{i}",
                    ["Name"] = $"Task {i}",
                    ["Owner"] = new JsonObject { ["id"] = "/User/1", ["name"] = "Owner One" },
                });
            }
            ClarizenHandler = new MockHttpHandler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/authentication/login"))
                    return MockHttpHandler.Json(HttpStatusCode.OK, """{"sessionId": "s"}""");
                ClarizenQueryCalls++;
                var page = new JsonObject
                {
                    ["entities"] = entities.DeepClone(),
                    ["paging"] = new JsonObject { ["hasMore"] = false },
                };
                return MockHttpHandler.Json(HttpStatusCode.OK, page.ToJsonString());
            });
            Clarizen = new ClarizenClient(
                Config, new ApiBudget(1_000_000, callsPerMinute: 6_000_000), ClarizenHandler);
            Clarizen.DelayAsync = (_, _) => Task.CompletedTask;

            Graph = new FakeGraphClient(Config);
            Store = new IdentityStore("PipelineTests", Path.Combine(Dir.Path, "identity.db"));
            Store.Upsert(new PrincipalMapping(
                "/User/1", "user", "owner@example.com", "entra-owner", DateTime.UtcNow));
        }

        /// <summary>Hermetic per-fixture inventory (temp SQLite, keyed per connection id).</summary>
        public Func<string, IItemInventory> InventoryFactory => connectionId =>
            new ItemInventory(connectionId, Path.Combine(Dir.Path, $"inventory_{connectionId}.db"));

        public IngestPipeline Pipeline()
        {
            var mapper = new PrincipalMapper(Store, "{}");
            var resolver = new AclResolver(
                mapper, new DirectorySnapshot(), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
            return new IngestPipeline(
                Config, Schema, Clarizen, Graph, resolver, new ItemConverter(Config),
                ha: null, inventoryFactory: InventoryFactory);
        }

        public void Dispose()
        {
            Store.Dispose();
            StateScope.Dispose();
            Dir.Dispose();
        }
    }

    [Fact]
    public async Task FullCrawl_IngestsAllRecords_WritesSyncState_ClearsCheckpoint()
    {
        using var fixture = new Fixture(recordCount: 3, chunkSize: 2);
        var summary = await fixture.Pipeline().RunAsync(fullCrawl: true);

        Assert.Equal(3, summary.Ingested);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(3, summary.PerObject["Task"]);
        Assert.NotNull(SyncState.ReadLastSync(Connector));
        Assert.Null(SyncState.ReadCheckpoint(Connector));

        // 2 chunks: [2 items → $batch], [1 item → single PUT]
        Assert.Contains(fixture.Graph.Sent, s => s.Path == "$batch");
        Assert.Contains(fixture.Graph.Sent, s =>
            s.Method == HttpMethod.Put && s.Path.EndsWith("items/Task_3"));
    }

    [Fact]
    public async Task ItemPayload_CarriesPropertiesContentAndAcl()
    {
        using var fixture = new Fixture(recordCount: 1);
        await fixture.Pipeline().RunAsync(fullCrawl: true);

        var put = fixture.Graph.Sent.Single(s => s.Method == HttpMethod.Put);
        Assert.Equal($"external/connections/{Connector}/items/Task_1", put.Path);
        var payload = put.Body!.AsObject();
        Assert.Equal("Task 1", payload["properties"]!["Title"]!.GetValue<string>());
        Assert.Equal("Task", payload["properties"]!["ObjectName"]!.GetValue<string>());
        Assert.Contains("Task 1", payload["content"]!["value"]!.GetValue<string>());
        var acl = payload["acl"]!.AsArray();
        Assert.Contains(acl, e => e!["value"]!.GetValue<string>() == "entra-owner");
    }

    [Fact]
    public async Task Failures_AreDeadLettered_WithRequestBody()
    {
        using var fixture = new Fixture(recordCount: 3, chunkSize: 3);
        fixture.Graph.FailingItemIds.Add("Task_2");

        var summary = await fixture.Pipeline().RunAsync(fullCrawl: true);
        Assert.Equal(2, summary.Ingested);
        Assert.Equal(1, summary.Failed);

        var entry = Assert.Single(SyncState.ReadFailedRecords(Connector));
        Assert.Equal("Task_2", entry["item_id"]!.GetValue<string>());
        Assert.Equal("Task", entry["object_type"]!.GetValue<string>());
        Assert.Equal("Task_2", entry["request_body"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task CheckpointResume_SkipsCompletedChunks()
    {
        using var fixture = new Fixture(recordCount: 4, chunkSize: 2);
        // Simulate a crash after chunk 1 of a FULL crawl (since = null).
        SyncState.WriteCheckpoint(Connector, null, "Task", 1);

        var summary = await fixture.Pipeline().RunAsync(fullCrawl: true);
        Assert.Equal(2, summary.Ingested);       // only chunk 2 processed
        Assert.Equal(1, summary.SkippedChunks);

        // Only Task_3 / Task_4 were PUT.
        var putIds = fixture.Graph.Sent
            .Where(s => s.Path == "$batch")
            .SelectMany(s => s.Body!["requests"]!.AsArray())
            .Select(r => r!["body"]!["id"]!.GetValue<string>())
            .ToList();
        Assert.Equal(new[] { "Task_3", "Task_4" }, putIds);
    }

    [Fact]
    public async Task IncrementalCrawl_UsesDeltaCursorInQuery()
    {
        using var fixture = new Fixture(recordCount: 1);
        SyncState.WriteLastSync(Connector, new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc));

        await fixture.Pipeline().RunAsync(fullCrawl: false);

        // Parse the payload (ToJsonString escapes '>' as > in the raw body).
        var queryBody = fixture.ClarizenHandler.Requests
            .First(r => r.Url.EndsWith("/data/query")).Body;
        var czql = JsonNode.Parse(queryBody)!["q"]!.GetValue<string>();
        Assert.Contains("LastUpdatedOn > 2026-07-01T12:00:00Z", czql);
    }

    [Fact]
    public async Task TdwExport_IsPreferredForFullCrawls_NoApiCalls()
    {
        using var tdw = new TempDir();
        File.WriteAllText(
            Path.Combine(tdw.Path, "Task.csv"),
            "id,Name,Owner\n/Task/9,Bulk Task,/User/1\n");

        using var fixture = new Fixture(recordCount: 1);
        var config = TestConfig.Make(tdwExportPath: tdw.Path);
        var mapper = new PrincipalMapper(fixture.Store, "{}");
        var resolver = new AclResolver(
            mapper, new DirectorySnapshot(), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
        var pipeline = new IngestPipeline(
            config, fixture.Schema, fixture.Clarizen, fixture.Graph, resolver,
            new ItemConverter(config), ha: null, inventoryFactory: fixture.InventoryFactory);

        var summary = await pipeline.RunAsync(fullCrawl: true);
        Assert.Equal(1, summary.Ingested);
        Assert.Equal(0, fixture.ClarizenQueryCalls);  // bulk path spent no API budget
        Assert.Contains(fixture.Graph.Sent, s => s.Path.EndsWith("items/Task_9"));
    }

    [Fact]
    public async Task RecordsWithoutAcl_AreSkippedNotIngested()
    {
        using var fixture = new Fixture(recordCount: 2, chunkSize: 2);
        fixture.Store.Clear();  // owner no longer resolvable → empty ACL

        var summary = await fixture.Pipeline().RunAsync(fullCrawl: true);
        Assert.Equal(0, summary.Ingested);
        Assert.Equal(2, summary.NoAclSkipped);
        Assert.DoesNotContain(fixture.Graph.Sent, s => s.Method == HttpMethod.Put);
        Assert.DoesNotContain(fixture.Graph.Sent, s => s.Path == "$batch");
    }
}
