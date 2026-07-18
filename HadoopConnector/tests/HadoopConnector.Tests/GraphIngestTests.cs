using System.Net;
using System.Text.Json.Nodes;
using HadoopConnector.AclEngine;
using HadoopConnector.Hdfs;
using HadoopConnector.Config;
using HadoopConnector.Filters;
using HadoopConnector.Graph;
using HadoopConnector.Item;

namespace HadoopConnector.Tests;

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
    private const string Connector = "BdhHadoopMart";

    /// <summary>Salesforce-shaped record id ("T000000000001", ...).</summary>
    private static string Tid(int n) => $"T{n:D12}";

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
        var map = new Dictionary<string, string> { ["1"] = Tid(1), ["2"] = Tid(2) };
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
            ["1"] = Tid(1), ["2"] = Tid(2), ["3"] = Tid(3),
        };
        var failures = IngestPipeline.ParseBatchFailures(body, map);
        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, f => f.ItemId == Tid(2) && f.Error.Contains("429"));
        Assert.Contains(failures, f => f.ItemId == Tid(3) && f.Error.Contains("500"));
    }

    [Fact]
    public void ParseBatchFailures_MissingResponseEntry_IsFailure()
    {
        var body = JsonNode.Parse("""{"responses": [{"id": "1", "status": 200}]}""");
        var map = new Dictionary<string, string> { ["1"] = Tid(1), ["2"] = Tid(2) };
        var failure = Assert.Single(IngestPipeline.ParseBatchFailures(body, map));
        Assert.Equal(Tid(2), failure.ItemId);
    }

    [Fact]
    public void ParseBatchFailures_MalformedEnvelope_FailsEverything()
    {
        var map = new Dictionary<string, string> { ["1"] = Tid(1), ["2"] = Tid(2) };
        Assert.Equal(2, IngestPipeline.ParseBatchFailures(JsonNode.Parse("{}"), map).Count);
        Assert.Equal(2, IngestPipeline.ParseBatchFailures(null, map).Count);
    }

    // ── Pipeline fixture ─────────────────────────────────────────────────────

    private sealed class Fixture : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly SyncStateScope StateScope = new();
        public readonly AppConfig Config;
        public readonly SchemaConfig Schema;
        public readonly FakeGraphClient Graph;
        public readonly FakeBdhSource Source;
        public readonly IdentityStore Store;

        public Fixture(int recordCount = 3, int chunkSize = 2, int batchSize = 20)
        {
            Config = TestConfig.Make(
                ingestChunkSize: chunkSize, graphBatchSize: batchSize, allowFullScan: true);
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
                            ["OwnerId"] = "OwnerId",
                        },
                    },
                },
            };

            Source = new FakeBdhSource();
            Source.Add("Task/dt=2026-07-15/part-0000.jsonl", Jsonl(1, recordCount));

            Graph = new FakeGraphClient(Config);
            Store = new IdentityStore("PipelineTests", Path.Combine(Dir.Path, "identity.db"));
            Store.Upsert(new PrincipalMapping(
                "005U0000001", "user", "owner@example.com", "entra-owner", DateTime.UtcNow));
        }

        /// <summary>Newline-joined JSONL rows for record numbers [from, from+count).</summary>
        public static string Jsonl(int from, int count) => string.Join("\n",
            Enumerable.Range(from, count).Select(i =>
                $$"""{"Id":"{{Tid(i)}}","Name":"Task {{i}}","OwnerId":"005U0000001"}"""));

        /// <summary>Hermetic per-fixture inventory (temp SQLite, keyed per connection id).</summary>
        public Func<string, IItemInventory> InventoryFactory => connectionId =>
            new ItemInventory(connectionId, Path.Combine(Dir.Path, $"inventory_{connectionId}.db"));

        public AclResolver Resolver() => new(
            new PrincipalMapper(Store), adminGroupId: string.Empty, fallbackGroupId: string.Empty);

        public IngestPipeline Pipeline(FilterSet? filters = null, AppConfig? config = null)
        {
            var cfg = config ?? Config;
            var fetcher = new BdhFetcher(cfg, Source, filters ?? FilterSet.Empty);
            return new IngestPipeline(
                cfg, Schema, fetcher, Graph, Resolver(), new ItemConverter(cfg),
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
            s.Method == HttpMethod.Put && s.Path.EndsWith($"items/{Tid(3)}"));
    }

    [Fact]
    public async Task ItemPayload_CarriesPropertiesContentAclAndFreshness()
    {
        using var fixture = new Fixture(recordCount: 1);
        await fixture.Pipeline().RunAsync(fullCrawl: true);

        var put = fixture.Graph.Sent.Single(s => s.Method == HttpMethod.Put);
        Assert.Equal($"external/connections/{Connector}/items/{Tid(1)}", put.Path);
        var payload = put.Body!.AsObject();
        Assert.Equal("Task 1", payload["properties"]!["Title"]!.GetValue<string>());
        Assert.Equal("Task", payload["properties"]!["ObjectName"]!.GetValue<string>());
        // Freshness markers: source system + the partition dt the row came from.
        Assert.Equal(ItemConverter.SourceSystem,
            payload["properties"]!["SourceSystem"]!.GetValue<string>());
        Assert.Equal("2026-07-15", payload["properties"]!["DataAsOf"]!.GetValue<string>());
        Assert.Contains("Task 1", payload["content"]!["value"]!.GetValue<string>());
        var acl = payload["acl"]!.AsArray();
        Assert.Contains(acl, e => e!["value"]!.GetValue<string>() == "entra-owner");
    }

    [Fact]
    public async Task Failures_AreDeadLettered_WithRequestBody()
    {
        using var fixture = new Fixture(recordCount: 3, chunkSize: 3);
        fixture.Graph.FailingItemIds.Add(Tid(2));

        var summary = await fixture.Pipeline().RunAsync(fullCrawl: true);
        Assert.Equal(2, summary.Ingested);
        Assert.Equal(1, summary.Failed);

        var entry = Assert.Single(SyncState.ReadFailedRecords(Connector));
        Assert.Equal(Tid(2), entry["item_id"]!.GetValue<string>());
        Assert.Equal("Task", entry["object_type"]!.GetValue<string>());
        Assert.Equal(Tid(2), entry["request_body"]!["id"]!.GetValue<string>());
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

        // Only records 3 / 4 were PUT.
        var putIds = fixture.Graph.Sent
            .Where(s => s.Path == "$batch")
            .SelectMany(s => s.Body!["requests"]!.AsArray())
            .Select(r => r!["body"]!["id"]!.GetValue<string>())
            .ToList();
        Assert.Equal(new[] { Tid(3), Tid(4) }, putIds);
    }

    [Fact]
    public async Task IncrementalCrawl_PrunesPartitionsOlderThanWatermark()
    {
        using var fixture = new Fixture(recordCount: 2);
        // A stale partition below the (since − BDH_LAG_HOURS) watermark: with
        // since = 2026-07-16 and the default 24h lag, minDt = 2026-07-15, so
        // dt=2026-07-01 must be pruned with zero file I/O.
        fixture.Source.Add("Task/dt=2026-07-01/part-0000.jsonl", Fixture.Jsonl(99, 1));
        SyncState.WriteLastSync(Connector, new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc));

        var summary = await fixture.Pipeline().RunAsync(fullCrawl: false);

        Assert.Equal(2, summary.Ingested);  // only the dt=2026-07-15 rows
        Assert.DoesNotContain(fixture.Graph.Sent, s => s.Path.Contains(Tid(99)));
        // The pruned partition's file was never opened.
        Assert.Equal(1, fixture.Source.OpenCalls);
    }

    // Replaces the removed TDW-export test: the fail-closed scale guard is the
    // new source-side safety valve. An unfiltered object (no filters.json entry,
    // ALLOW_FULL_SCAN=false) is refused per-object; the crawl still completes.
    [Fact]
    public async Task FullScanGuard_UnfilteredObject_FailsObject_CrawlStillCompletes()
    {
        using var fixture = new Fixture(recordCount: 3);
        var guarded = TestConfig.Make(allowFullScan: false);

        var summary = await fixture.Pipeline(config: guarded).RunAsync(fullCrawl: true);

        Assert.Equal(new[] { "Task" }, summary.FailedObjects);
        Assert.Equal(0, summary.Ingested);
        Assert.DoesNotContain(fixture.Graph.Sent, s =>
            s.Method == HttpMethod.Put || s.Path == "$batch");
        // Per-object refusal, not degraded: the crawl closes and stamps its cursor.
        Assert.NotNull(SyncState.ReadLastSync(Connector));
        Assert.Empty(SyncState.ReadFailedRecords(Connector));  // guard is not a dead-letter
    }

    // Filtered path through the whole pipeline: record predicates from
    // config/filters.json limit what is fetched and therefore what is PUT.
    [Fact]
    public async Task RecordPredicateFilter_OnlyMatchingRowsAreIngested()
    {
        using var fixture = new Fixture(recordCount: 3);
        var filters = FilterSet.Parse(
            """{"objects": {"Task": {"allOf": [{"field": "Id", "op": "equals", "value": """
            + $"\"{Tid(2)}\"" + "}]}}}");

        var summary = await fixture.Pipeline(filters).RunAsync(fullCrawl: true);

        Assert.Equal(1, summary.Ingested);
        var put = Assert.Single(fixture.Graph.Sent, s => s.Method == HttpMethod.Put);
        Assert.EndsWith($"items/{Tid(2)}", put.Path);
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
