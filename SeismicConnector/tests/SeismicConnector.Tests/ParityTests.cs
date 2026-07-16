// Tests for the hardening-parity features: sovereign-cloud endpoint override,
// throughput knobs, the $batch 429/503 retry ladder, adaptive concurrency,
// connection sharding (validation + routing), the pinned HA
// close-with-failed-claims semantics, and dead-letter integrity under
// concurrent writers.

using System.Net;
using System.Text.Json.Nodes;
using SeismicConnector.Config;
using SeismicConnector.Graph;
using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector.Tests;

// ── sovereign-cloud endpoint override ────────────────────────────────────────

public class SovereignCloudTests : IDisposable
{
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GRAPH_BASE_URL", null);
        Environment.SetEnvironmentVariable("GRAPH_SCOPE", null);
        Environment.SetEnvironmentVariable("GRAPH_API_VERSION", null);
        Environment.SetEnvironmentVariable("AAD_APP_OAUTH_AUTHORITY_HOST", null);
    }

    private static GraphClient Client(FakeHttpHandler? handler = null) =>
        new(TestConfig.Build().Graph, handler ?? new FakeHttpHandler());

    [Fact]
    public void Defaults_ArePublicCloud()
    {
        var client = Client();
        Assert.Equal("https://graph.microsoft.com/v1.0", client.ApiRoot);
        Assert.Equal("https://graph.microsoft.com/.default", client.Scope);
        Assert.Equal("https://login.microsoftonline.com", client.AuthorityHost);
    }

    [Fact]
    public void GraphBaseUrl_OverridesEndpointAndScope_Live()
    {
        var client = Client();
        Environment.SetEnvironmentVariable("GRAPH_BASE_URL", "https://graph.microsoft.us/");
        // Read live per access — no client rebuild needed.
        Assert.Equal("https://graph.microsoft.us/v1.0", client.ApiRoot);
        Assert.Equal("https://graph.microsoft.us/.default", client.Scope);
    }

    [Fact]
    public void GraphScope_OverridesAudienceIndependently()
    {
        var client = Client();
        Environment.SetEnvironmentVariable("GRAPH_BASE_URL", "https://graph.microsoft.us");
        Environment.SetEnvironmentVariable("GRAPH_SCOPE", "https://custom.audience/.default");
        Assert.Equal("https://custom.audience/.default", client.Scope);
    }

    [Fact]
    public void GraphApiVersion_Override()
    {
        var client = Client();
        Environment.SetEnvironmentVariable("GRAPH_API_VERSION", "beta");
        Assert.Equal("https://graph.microsoft.com/beta", client.ApiRoot);
    }

    [Fact]
    public async Task TokenAcquisition_UsesAuthorityHostAndScope()
    {
        Environment.SetEnvironmentVariable("AAD_APP_OAUTH_AUTHORITY_HOST", "https://login.microsoftonline.us");
        Environment.SetEnvironmentVariable("GRAPH_BASE_URL", "https://graph.microsoft.us");

        var handler = new FakeHttpHandler();
        handler.When(HttpMethod.Post, "/oauth2/v2.0/token", (_, _) =>
            FakeHttpHandler.Json(HttpStatusCode.OK, """{"access_token":"sov-token","expires_in":3600}"""));
        handler.When(HttpMethod.Get, "/external/connections", (request, _) =>
        {
            Assert.Equal("Bearer sov-token", request.Headers.Authorization?.ToString());
            return FakeHttpHandler.Json(HttpStatusCode.OK, """{"state":"ready"}""");
        });

        var client = Client(handler);  // NO OverrideAccessToken — real token path
        var result = await client.GetAsync("/external/connections/X");

        Assert.Equal("ready", result?["state"]?.GetValue<string>());
        var tokenRequest = handler.Requests.Single(r => r.Url.Contains("/oauth2/v2.0/token"));
        Assert.StartsWith("https://login.microsoftonline.us/tenant-guid/", tokenRequest.Url);
        Assert.Contains("scope=" + Uri.EscapeDataString("https://graph.microsoft.us/.default"),
            tokenRequest.Body);
        // Sovereign Graph call went to the overridden endpoint.
        Assert.Contains(handler.Requests, r => r.Url.StartsWith("https://graph.microsoft.us/v1.0/"));
    }
}

// ── throughput knobs ─────────────────────────────────────────────────────────

public class ThroughputKnobTests : IDisposable
{
    private readonly string _configDir =
        Path.Combine(Path.GetTempPath(), "seismic-knobs-" + Guid.NewGuid().ToString("N"));

    private static readonly (string, string)[] RequiredEnv =
    {
        ("CONNECTOR_ID", "SeismicKnobs"),
        ("CONNECTOR_NAME", "n"),
        ("CONNECTOR_DESCRIPTION", "d"),
        ("SEISMIC_TENANT", "contoso"),
        ("SEISMIC_CLIENT_ID", "c"),
        ("SECRET_SEISMIC_CLIENT_SECRET", "s"),
        ("AAD_APP_TENANT_ID", "t"),
        ("AAD_APP_CLIENT_ID", "c"),
        ("SECRET_AAD_APP_CLIENT_SECRET", "s"),
    };

    private static readonly string[] KnobVars =
    {
        "GRAPH_BATCH_WORKERS", "GRAPH_CONCURRENT_BATCHES",
        "INGEST_GRAPH_BATCH_SIZE", "GRAPH_BATCH_SIZE",
        "GRAPH_MAX_RETRIES", "GRAPH_RETRY_BACKOFF_BASE",
    };

    public ThroughputKnobTests()
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(Path.Combine(_configDir, "schema.json"),
            """{"objects":[{"name":"ContentItem","enabled":true},{"name":"Library","enabled":true}]}""");
        foreach (var (key, value) in RequiredEnv)
            Environment.SetEnvironmentVariable(key, value);
    }

    public void Dispose()
    {
        foreach (var (key, _) in RequiredEnv)
            Environment.SetEnvironmentVariable(key, null);
        foreach (var key in KnobVars)
            Environment.SetEnvironmentVariable(key, null);
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch
        {
        }
    }

    private AppConfig Load() => AppConfig.Load(_configDir);

    [Fact]
    public void Defaults_WhenNothingSet()
    {
        var config = Load();
        Assert.Equal(4, config.Ingest.BatchWorkers);
        Assert.Equal(20, config.Ingest.GraphBatchSize);
        Assert.Equal(4, config.Graph.MaxRetries);
        Assert.Equal(2, config.Graph.RetryBackoffBase);
    }

    [Fact]
    public void GraphBatchWorkers_SetsWorkers()
    {
        Environment.SetEnvironmentVariable("GRAPH_BATCH_WORKERS", "12");
        Assert.Equal(12, Load().Ingest.BatchWorkers);
    }

    [Fact]
    public void GraphConcurrentBatches_WinsOverBatchWorkers()
    {
        Environment.SetEnvironmentVariable("GRAPH_BATCH_WORKERS", "12");
        Environment.SetEnvironmentVariable("GRAPH_CONCURRENT_BATCHES", "3");
        Assert.Equal(3, Load().Ingest.BatchWorkers);
    }

    [Fact]
    public void GraphBatchSize_AliasIsAccepted_AndCappedAt20()
    {
        Environment.SetEnvironmentVariable("GRAPH_BATCH_SIZE", "10");
        Assert.Equal(10, Load().Ingest.GraphBatchSize);

        Environment.SetEnvironmentVariable("GRAPH_BATCH_SIZE", "50");
        Assert.Equal(20, Load().Ingest.GraphBatchSize);  // Graph API hard cap
    }

    [Fact]
    public void IngestGraphBatchSize_WinsOverAlias()
    {
        Environment.SetEnvironmentVariable("GRAPH_BATCH_SIZE", "10");
        Environment.SetEnvironmentVariable("INGEST_GRAPH_BATCH_SIZE", "15");
        Assert.Equal(15, Load().Ingest.GraphBatchSize);
    }

    [Fact]
    public void RetryKnobs_AreHonoured()
    {
        Environment.SetEnvironmentVariable("GRAPH_MAX_RETRIES", "7");
        Environment.SetEnvironmentVariable("GRAPH_RETRY_BACKOFF_BASE", "5");
        var config = Load();
        Assert.Equal(7, config.Graph.MaxRetries);
        Assert.Equal(5, config.Graph.RetryBackoffBase);

        // The backoff base flows into the transport ladder: 5, 10, 20 ...
        var client = new GraphClient(config.Graph, new FakeHttpHandler()) { OverrideAccessToken = "t" };
        Assert.Equal(5.0, client.NextDelaySeconds(0, null));
        Assert.Equal(10.0, client.NextDelaySeconds(1, null));
    }
}

// ── $batch 429/503 retry ladder ──────────────────────────────────────────────

public class BatchRetryLadderTests
{
    private static (GraphClient Client, FakeHttpHandler Handler, List<List<string>> EnvelopeIds) LadderClient(
        Func<string, int, (int Status, string? RetryAfter)> statusFor)
    {
        var handler = new FakeHttpHandler();
        var envelopeIds = new List<List<string>>();
        handler.When(HttpMethod.Post, "/$batch", (_, body) =>
        {
            var envelope = JsonNode.Parse(body!)!.AsObject();
            var ids = envelope["requests"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()).ToList();
            envelopeIds.Add(ids);
            var round = envelopeIds.Count - 1;
            var responses = new JsonArray();
            foreach (var id in ids)
            {
                var (status, retryAfter) = statusFor(id, round);
                var response = new JsonObject { ["id"] = id, ["status"] = status };
                if (retryAfter is not null)
                    response["headers"] = new JsonObject { ["Retry-After"] = retryAfter };
                if (status >= 300)
                    response["body"] = new JsonObject
                    {
                        ["error"] = new JsonObject { ["message"] = $"HTTP {status}" },
                    };
                responses.Add(response);
            }
            return FakeHttpHandler.Json(HttpStatusCode.OK,
                new JsonObject { ["responses"] = responses }.ToJsonString());
        });
        var client = new GraphClient(TestConfig.Build().Graph, handler)
        {
            OverrideAccessToken = "token",
            DelayAsync = (_, _) => Task.CompletedTask,
        };
        return (client, handler, envelopeIds);
    }

    private static List<(string, JsonNode)> Items(params string[] ids) =>
        ids.Select(id => (id, (JsonNode)new JsonObject { ["id"] = id })).ToList();

    [Fact]
    public async Task Throttled429_IsRetried_OnlyThrottledItemsResent()
    {
        var (client, _, envelopes) = LadderClient((id, round) =>
            id == "hot" && round == 0 ? (429, "9") : (200, null));

        var outcome = await client.PutExternalItemsBatchWithRetryAsync("Conn", Items("hot", "cold"));

        Assert.True(outcome.SawThrottle);
        Assert.All(outcome.Results, r => Assert.True(r.Success));
        Assert.Equal(2, envelopes.Count);
        Assert.Equal(new[] { "hot", "cold" }, envelopes[0]);
        Assert.Equal(new[] { "hot" }, envelopes[1]);         // only the 429 item re-sent
        // Inter-round wait honoured the sub-response Retry-After (9 > computed 2).
        Assert.Equal(9.0, client.ObservedDelaysSeconds.Single());
    }

    [Fact]
    public async Task ServiceUnavailable503_IsRetried_WithoutThrottleSignal()
    {
        var (client, _, envelopes) = LadderClient((id, round) =>
            round == 0 ? (503, null) : (200, null));

        var outcome = await client.PutExternalItemsBatchWithRetryAsync("Conn", Items("a"));

        Assert.False(outcome.SawThrottle);  // 503 is not a rate limit
        Assert.True(outcome.Results.Single().Success);
        Assert.Equal(2, envelopes.Count);
    }

    [Fact]
    public async Task ExhaustedRetries_MarksItemsFailed()
    {
        var (client, _, envelopes) = LadderClient((_, _) => (429, "1"));

        var outcome = await client.PutExternalItemsBatchWithRetryAsync("Conn", Items("stuck"));

        Assert.True(outcome.SawThrottle);
        var failed = outcome.Results.Single();
        Assert.False(failed.Success);
        Assert.Equal(429, failed.Status);
        Assert.Contains("after all retries", failed.Error);
        Assert.Equal(4, envelopes.Count);  // initial + MaxRetries(3)
    }

    [Fact]
    public async Task PermanentFailure_IsNotRetried()
    {
        var (client, _, envelopes) = LadderClient((id, _) =>
            id == "bad" ? (400, null) : (200, null));

        var outcome = await client.PutExternalItemsBatchWithRetryAsync("Conn", Items("bad", "good"));

        Assert.Single(envelopes);  // no retry round for a 400
        Assert.False(outcome.Results.Single(r => r.ItemId == "bad").Success);
        Assert.True(outcome.Results.Single(r => r.ItemId == "good").Success);
        Assert.False(outcome.SawThrottle);
    }

    [Fact]
    public async Task InterRoundWait_UsesComputedBackoffWhenNoRetryAfter()
    {
        var (client, _, _) = LadderClient((id, round) =>
            round < 2 ? (429, null) : (200, null));

        var outcome = await client.PutExternalItemsBatchWithRetryAsync("Conn", Items("a"));
        Assert.True(outcome.Results.Single().Success);
        // Rounds waited base·2^0=2 then base·2^1=4 (jitter off).
        Assert.Equal(new[] { 2.0, 4.0 }, client.ObservedDelaysSeconds);
    }
}

public class AdaptiveConcurrencyTests
{
    [Fact]
    public void StartsAtMax_StepsDownOnThrottle_NeverBelowOne()
    {
        var concurrency = new AdaptiveConcurrency(3);
        Assert.Equal(3, concurrency.Current);
        concurrency.OnThrottle();
        Assert.Equal(2, concurrency.Current);
        concurrency.OnThrottle();
        concurrency.OnThrottle();
        concurrency.OnThrottle();
        Assert.Equal(1, concurrency.Current);  // floor
    }

    [Fact]
    public void RampsUpAfterThreeConsecutiveSuccesses_CappedAtMax()
    {
        var concurrency = new AdaptiveConcurrency(3);
        concurrency.OnThrottle();
        concurrency.OnThrottle();
        Assert.Equal(1, concurrency.Current);

        concurrency.OnSuccess();
        concurrency.OnSuccess();
        Assert.Equal(1, concurrency.Current);  // streak of 2 is not enough
        concurrency.OnSuccess();
        Assert.Equal(2, concurrency.Current);

        for (var i = 0; i < 12; i++)
            concurrency.OnSuccess();
        Assert.Equal(3, concurrency.Current);  // never above max
    }

    [Fact]
    public void ThrottleResetsSuccessStreak()
    {
        var concurrency = new AdaptiveConcurrency(2);
        concurrency.OnThrottle();
        concurrency.OnSuccess();
        concurrency.OnSuccess();
        concurrency.OnThrottle();  // streak reset (and already at 1)
        concurrency.OnSuccess();
        concurrency.OnSuccess();
        Assert.Equal(1, concurrency.Current);
        concurrency.OnSuccess();
        Assert.Equal(2, concurrency.Current);
    }
}

// ── connection sharding ──────────────────────────────────────────────────────

public class ShardingConfigTests : IDisposable
{
    public void Dispose() => Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, null);

    private static AppConfig Config() => TestConfig.Build();

    [Fact]
    public void Unset_IsDisabledWithoutError()
    {
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, null);
        Assert.False(ShardingConfig.IsEnabled);
        Assert.False(ShardingConfig.TryLoad(Config(), out var shards, out var error));
        Assert.Null(error);
        Assert.Empty(shards);
    }

    [Fact]
    public void ValidTwoShardConfig_Loads()
    {
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar,
            """{"seismicContent":["ContentItem"],"seismicLibs":["Library"]}""");
        Assert.True(ShardingConfig.TryLoad(Config(), out var shards, out var error));
        Assert.Null(error);
        Assert.Equal(2, shards.Count);
        Assert.Equal("seismicContent", shards[0].ConnectionId);
        Assert.Equal(new[] { "ContentItem" }, shards[0].ObjectTypes);
    }

    [Theory]
    [InlineData("not json at all", "not valid JSON")]
    [InlineData("[1,2]", "must be a JSON object")]
    [InlineData("{}", "declares no shards")]
    public void MalformedValues_ReportError(string value, string expected)
    {
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, value);
        Assert.False(ShardingConfig.TryLoad(Config(), out _, out var error));
        Assert.Contains(expected, error);
    }

    [Fact]
    public void UnknownObjectType_IsReported()
    {
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar,
            """{"shardA":["ContentItem","Library","Bogus"]}""");
        Assert.False(ShardingConfig.TryLoad(Config(), out _, out var error));
        Assert.Contains("unknown object type 'Bogus'", error);
    }

    [Fact]
    public void UnassignedObject_IsReported()
    {
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar,
            """{"shardA":["ContentItem"]}""");
        Assert.False(ShardingConfig.TryLoad(Config(), out _, out var error));
        Assert.Contains("not assigned to any shard", error);
        Assert.Contains("Library", error);
    }

    [Fact]
    public void DoublyAssignedObject_IsReported()
    {
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar,
            """{"shardA":["ContentItem","Library"],"shardB":["ContentItem"]}""");
        Assert.False(ShardingConfig.TryLoad(Config(), out _, out var error));
        Assert.Contains("assigned to multiple shards", error);
    }

    [Fact]
    public void InvalidConnectionId_IsReported()
    {
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar,
            """{"SharePointShard":["ContentItem","Library"]}""");
        Assert.False(ShardingConfig.TryLoad(Config(), out _, out var error));
        Assert.Contains("Invalid connection id 'SharePointShard'", error);
    }

    [Fact]
    public void ForShard_BindsConnectionId_SharesEverythingElse()
    {
        var baseConfig = Config();
        var shard = new Shard("seismicContent", new[] { "ContentItem" });
        var shardConfig = ShardingConfig.ForShard(baseConfig, shard);
        Assert.Equal("seismicContent", shardConfig.Connector.Id);
        Assert.Equal(baseConfig.Connector.Name, shardConfig.Connector.Name);
        Assert.Same(baseConfig.Seismic, shardConfig.Seismic);
        Assert.Same(baseConfig.Schema, shardConfig.Schema);
    }

    [Fact]
    public async Task ShardedPipelines_RouteItemsToTheirOwnConnections()
    {
        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1"));

        // Content shard ingests ContentItem into its own connection...
        var contentPipeline = new IngestPipeline(
            harness.Config.ForConnection("seismicContent"),
            harness.Seismic, harness.Graph, harness.Store);
        Assert.True(await contentPipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        // ...and the library shard ingests Library into ITS own connection.
        var libraryPipeline = new IngestPipeline(
            harness.Config.ForConnection("seismicLibs"),
            harness.Seismic, harness.Graph, harness.Store,
            aclMapper: new AclMapper(harness.Store, "tenant", "tenant-guid"));
        Assert.True(await libraryPipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "Library"));

        var batchUrls = harness.GraphHandler.Requests
            .Where(r => r.Method == HttpMethod.Post && r.Url.Contains("/$batch"))
            .Select(r => r.Body!)
            .ToList();
        // Item URLs inside the envelopes carry the shard connection ids.
        Assert.Contains(batchUrls, b => b.Contains("/external/connections/seismicContent/items/c1"));
        Assert.Contains(batchUrls, b => b.Contains("/external/connections/seismicLibs/items/lib-ts1"));
        Assert.DoesNotContain(batchUrls, b => b.Contains($"/external/connections/{harness.Config.Connector.Id}/"));
    }
}

// ── HA close-with-failed-claims (pinned semantics) ───────────────────────────

public class HaCloseDecisionTests
{
    [Fact]
    public void ClaimsInFlight_BlockTheClose()
    {
        var (perform, status, result) = HaCoordinator.CloseDecision(
            anyClaimedRemaining: true, anyFailedClaims: false,
            sessionStatus: "open", closedBy: null, nodeId: "n1");
        Assert.False(perform);
        Assert.Equal("open", status);
        Assert.Equal(HaCloseResult.StillOpen, result);
    }

    [Fact]
    public void AllClaimsDone_ClosesAsClosed()
    {
        var (perform, status, result) = HaCoordinator.CloseDecision(
            false, false, "open", null, "n1");
        Assert.True(perform);
        Assert.Equal("closed", status);
        Assert.Equal(HaCloseResult.ClosedByThisNode, result);
    }

    [Fact]
    public void FailedClaims_StillClose_ButAsFailed()
    {
        // THE pinned semantic: failed claims do not block the close — the
        // session records 'failed' and the closer still wins (and records
        // sync state; failures are re-driven via retry-failed).
        var (perform, status, result) = HaCoordinator.CloseDecision(
            anyClaimedRemaining: false, anyFailedClaims: true,
            sessionStatus: "open", closedBy: null, nodeId: "n1");
        Assert.True(perform);
        Assert.Equal("failed", status);
        Assert.Equal(HaCloseResult.ClosedByThisNode, result);
    }

    [Fact]
    public void FailedClaims_WithWorkStillInFlight_DoNotClose()
    {
        var (perform, _, result) = HaCoordinator.CloseDecision(
            anyClaimedRemaining: true, anyFailedClaims: true,
            sessionStatus: "open", closedBy: null, nodeId: "n1");
        Assert.False(perform);
        Assert.Equal(HaCloseResult.StillOpen, result);
    }

    [Fact]
    public void CommitAckLossRetry_ByCloser_StillReportsClosedByThisNode()
    {
        // The close COMMITted but the ack was lost; the closer retries. The
        // result derives from ClosedBy, so it still reports true.
        var (perform, _, result) = HaCoordinator.CloseDecision(
            false, true, sessionStatus: "failed", closedBy: "n1", nodeId: "n1");
        Assert.False(perform);  // no second UPDATE
        Assert.Equal(HaCloseResult.ClosedByThisNode, result);
    }

    [Fact]
    public void OtherNodesSeeClosedElsewhere_ExactlyOneWinner()
    {
        var (_, _, closer) = HaCoordinator.CloseDecision(false, false, "closed", "n1", "n1");
        var (_, _, loser) = HaCoordinator.CloseDecision(false, false, "closed", "n1", "n2");
        Assert.Equal(HaCloseResult.ClosedByThisNode, closer);
        Assert.Equal(HaCloseResult.ClosedElsewhere, loser);
    }
}

// ── dead-letter integrity under concurrency (stress-harness invariants) ─────

public class DeadLetterConcurrencyTests
{
    [Fact]
    public void ManyConcurrentWriters_NoInterleavedOrTornLines()
    {
        using var state = new TempStateDir();
        var path = SyncState.FailedRecordsPath("Conn");
        const int writers = 8;
        const int recordsPerWriter = 50;

        Parallel.For(0, writers, writer =>
        {
            for (var i = 0; i < recordsPerWriter; i++)
            {
                var id = $"w{writer}-r{i}";
                SyncState.AppendFailedRecords(
                    path,
                    new List<(string, string)> { (id, $"error for {id} with a long-ish payload {new string('x', 200)}") },
                    "ContentItem",
                    requestBodies: new Dictionary<string, JsonNode?>
                    {
                        [id] = new JsonObject
                        {
                            ["id"] = id,
                            ["content"] = new JsonObject { ["value"] = new string('y', 300) },
                        },
                    });
            }
        });

        // Invariant 1: every line parses as standalone JSON (ReadFailedRecords
        // throws on a torn/interleaved line).
        var records = SyncState.ReadFailedRecords("Conn");

        // Invariant 2: no records lost, none duplicated.
        Assert.Equal(writers * recordsPerWriter, records.Count);
        var ids = records.Select(r => r["item_id"]!.GetValue<string>()).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

        // Invariant 3: request bodies survived intact (payload length preserved).
        Assert.All(records, r =>
            Assert.Equal(300, r["request_body"]!["content"]!["value"]!.GetValue<string>().Length));
    }

    [Fact]
    public async Task ConcurrentAppendAndRead_NeverTearsRecords()
    {
        using var state = new TempStateDir();
        var path = SyncState.FailedRecordsPath("Conn");
        var stop = false;
        var readerErrors = 0;

        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                try
                {
                    _ = SyncState.ReadFailedRecords("Conn");
                }
                catch
                {
                    Interlocked.Increment(ref readerErrors);
                }
            }
        });

        Parallel.For(0, 200, i =>
            SyncState.AppendFailedRecords(path, new List<string> { $"item-{i}" }, "ContentItem", "err"));
        Volatile.Write(ref stop, true);
        await reader.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, readerErrors);
        Assert.Equal(200, SyncState.ReadFailedRecords("Conn").Count);
    }
}
