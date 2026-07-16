// Tests for the behaviors ported from the reference connector's hardening
// commits: sovereign-cloud endpoint override, $batch pipeline + 429 ladder,
// adaptive concurrency, throughput knobs, sharding, dead-letter concurrency,
// hot-path log gating, and HA close-with-failed-claims semantics.

using System.Text.Json.Nodes;
using AltrataConnector.Altrata;
using AltrataConnector.Config;
using AltrataConnector.Entitlement;
using AltrataConnector.Graph;
using AltrataConnector.Identity;
using AltrataConnector.Infrastructure;
using AltrataConnector.Ingestion;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

// ---- sovereign-cloud endpoint override ---------------------------------------

public class SovereignCloudTests : IDisposable
{
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GRAPH_BASE_URL", null);
        Environment.SetEnvironmentVariable("GRAPH_SCOPE", null);
    }

    [Fact]
    public void DefaultsAreThePublicCloud()
    {
        Environment.SetEnvironmentVariable("GRAPH_BASE_URL", null);
        Environment.SetEnvironmentVariable("GRAPH_SCOPE", null);
        Assert.Equal("https://graph.microsoft.com", GraphClient.GraphBaseUrl);
        Assert.Equal("https://graph.microsoft.com/.default", GraphClient.GraphScope);
    }

    [Fact]
    public void BaseUrlOverrideDrivesScopeDefault()
    {
        Environment.SetEnvironmentVariable("GRAPH_BASE_URL", "https://graph.microsoft.us/");
        Assert.Equal("https://graph.microsoft.us", GraphClient.GraphBaseUrl);  // trailing slash trimmed
        Assert.Equal("https://graph.microsoft.us/.default", GraphClient.GraphScope);

        Environment.SetEnvironmentVariable("GRAPH_SCOPE", "https://graph.chinacloudapi.cn/.default");
        Assert.Equal("https://graph.chinacloudapi.cn/.default", GraphClient.GraphScope);  // explicit wins
    }

    [Fact]
    public async Task TokenAndRequestsUseTheOverriddenEndpoint()
    {
        Environment.SetEnvironmentVariable("GRAPH_BASE_URL", "https://graph.microsoft.us");
        var tokenBodies = new List<string>();
        var handler = new ScriptedHandler();
        handler.Enqueue(req =>
        {
            tokenBodies.Add(req.Content!.ReadAsStringAsync().Result);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"tok","expires_in":3600}"""),
            };
        });
        handler.EnqueueJson(200, "{}");

        var client = new GraphClient(TestFixtures.NewConfig(), handler, (_, _) => Task.CompletedTask);
        await client.DeleteItemAsync("x1");

        Assert.Contains("graph.microsoft.us", Uri.UnescapeDataString(tokenBodies[0]));  // scope audience
        Assert.Equal("graph.microsoft.us", handler.Requests[1].RequestUri!.Host);       // API host
        Assert.StartsWith("https://graph.microsoft.us/v1.0", client.BaseUrl);
    }
}

// ---- 429 hardening: Retry-After clamp on the single-request path ---------------

public class RetryAfterClampTests
{
    [Fact]
    public async Task RetryAfterAboveSixtySecondsIsClampedToTheCap()
    {
        var delays = new List<double>();
        var handler = new ScriptedHandler();
        handler.EnqueueJson(200, """{"access_token":"tok","expires_in":3600}""");
        handler.EnqueueJson(429, "{}", r => r.Headers.Add("Retry-After", "3600"));
        handler.EnqueueJson(200, "{}");

        var client = new GraphClient(TestFixtures.NewConfig(), handler,
            (seconds, _) => { delays.Add(seconds); return Task.CompletedTask; });
        await client.DeleteItemAsync("x1");

        Assert.Single(delays);
        Assert.Equal(GraphClient.MaxRetryWaitSeconds, delays[0]);  // clamped, not 3600
    }
}

// ---- $batch pipeline -------------------------------------------------------------

public class GraphBatchPipelineTests
{
    private static ExternalItem Item(string id) => new()
    {
        Id = id,
        Acl = new[] { new AclEntry { Type = "user", Value = "alice@contoso.com" } },
        Properties = new Dictionary<string, object?> { ["title"] = id },
    };

    private static (GraphClient Client, ScriptedHandler Handler, List<double> Delays, List<string> BatchBodies)
        Setup(int maxRetries = 2)
    {
        var handler = new ScriptedHandler();
        var delays = new List<double>();
        var bodies = new List<string>();
        var config = TestFixtures.NewConfig() with { GraphMaxRetries = maxRetries };
        var client = new GraphClient(config, handler,
            (seconds, _) => { delays.Add(seconds); return Task.CompletedTask; });
        handler.EnqueueJson(200, """{"access_token":"tok","expires_in":3600}""");
        return (client, handler, delays, bodies);
    }

    private static void EnqueueBatch(ScriptedHandler handler, List<string> bodies, string responsesJson)
    {
        handler.Enqueue(req =>
        {
            bodies.Add(req.Content!.ReadAsStringAsync().Result);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"responses":{{responsesJson}}}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        });
    }

    [Fact]
    public async Task SplitsIntoSubBatchesAndReportsPerItemResults()
    {
        var (client, handler, _, bodies) = Setup();
        // batch size 2 → [P1,P2] then [P3]
        EnqueueBatch(handler, bodies, """
            [{"id":"0","status":200},
             {"id":"1","status":400,"body":{"error":{"code":"InvalidRequest","message":"bad acl"}}}]
            """);
        EnqueueBatch(handler, bodies, """[{"id":"0","status":201}]""");

        var results = await client.PutItemsBatchAsync(new[] { Item("P1"), Item("P2"), Item("P3") });
        var byId = results.ToDictionary(r => r.ItemId);

        Assert.True(byId["P1"].Success);
        Assert.False(byId["P2"].Success);
        Assert.Equal(400, byId["P2"].Status);
        Assert.Contains("bad acl", byId["P2"].Error);
        Assert.True(byId["P3"].Success);
        Assert.Equal(2, bodies.Count);
        Assert.Contains("/items/P1", bodies[0]);
        Assert.DoesNotContain("/items/P3", bodies[0]);
    }

    [Fact]
    public async Task Only429ItemsAreResentAndRetryAfterRaisesTheWait()
    {
        var (client, handler, delays, bodies) = Setup();
        EnqueueBatch(handler, bodies, """
            [{"id":"0","status":200},
             {"id":"1","status":429,"headers":{"Retry-After":"7"}}]
            """);
        EnqueueBatch(handler, bodies, """[{"id":"0","status":204}]""");

        var results = await client.PutItemsBatchAsync(new[] { Item("P1"), Item("P2") });

        Assert.All(results, r => Assert.True(r.Success));
        Assert.Single(delays);
        Assert.Equal(7.0, delays[0]);                     // Retry-After honoured (>= computed backoff)
        Assert.Contains("/items/P2", bodies[1]);          // only the throttled item re-sent
        Assert.DoesNotContain("/items/P1", bodies[1]);
        Assert.Contains("\"id\":\"0\"", bodies[1]);       // renumbered for the retry round
    }

    [Fact]
    public async Task Status503RetriesWithoutCountingAsThrottle()
    {
        Metrics.ResetForTests();
        var (client, handler, _, bodies) = Setup();
        EnqueueBatch(handler, bodies, """[{"id":"0","status":503},{"id":"1","status":200}]""");
        EnqueueBatch(handler, bodies, """[{"id":"0","status":200}]""");

        var results = await client.PutItemsBatchAsync(new[] { Item("P1"), Item("P2") });

        Assert.All(results, r => Assert.True(r.Success));
        Assert.Equal(0, Metrics.Get("altrata_graph_throttle_429_total"));
        Metrics.ResetForTests();
    }

    [Fact]
    public async Task ItemsMissingFromTheResponseAreFailures()
    {
        var (client, handler, _, bodies) = Setup();
        EnqueueBatch(handler, bodies, """[{"id":"0","status":200}]""");  // P2 unaccounted

        var results = await client.PutItemsBatchAsync(new[] { Item("P1"), Item("P2") });
        var byId = results.ToDictionary(r => r.ItemId);

        Assert.True(byId["P1"].Success);
        Assert.False(byId["P2"].Success);
        Assert.Contains("No response received", byId["P2"].Error);
    }

    [Fact]
    public async Task EmptyBatchResponseFailsAllItems()
    {
        var (client, handler, _, bodies) = Setup();
        EnqueueBatch(handler, bodies, "[]");

        var results = await client.PutItemsBatchAsync(new[] { Item("P1"), Item("P2") });
        Assert.All(results, r => Assert.False(r.Success));
        Assert.All(results, r => Assert.Contains("empty response", r.Error));
    }

    [Fact]
    public async Task ExhaustedRetriesMarkThrottledItemsAsPermanent429Failures()
    {
        var (client, handler, delays, bodies) = Setup(maxRetries: 2);
        for (var i = 0; i < 3; i++)  // attempts 0..2
            EnqueueBatch(handler, bodies, """[{"id":"0","status":429}]""");

        var results = await client.PutItemsBatchAsync(new[] { Item("P1") });

        Assert.Single(results);
        Assert.False(results[0].Success);
        Assert.Equal(429, results[0].Status);
        Assert.Contains("throttled after all retries", results[0].Error);
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public async Task SeatInvariantHoldsOnTheBatchedPathBeforeAnyRequest()
    {
        var (client, handler, _, _) = Setup();
        var everyone = new ExternalItem
        {
            Id = "P1",
            Acl = new[] { new AclEntry { Type = "everyone", Value = "all" } },
            Properties = new Dictionary<string, object?>(),
        };

        await Assert.ThrowsAsync<EntitlementViolationException>(
            () => client.PutItemsBatchAsync(new[] { everyone }));
        Assert.Empty(handler.Requests);  // rejected before any HTTP (even the token)

        await Assert.ThrowsAsync<EntitlementViolationException>(
            () => client.UpdateItemAclsBatchAsync(new[]
            {
                new AclUpdate("P1", new[] { new AclEntry { Type = "everyoneExceptGuests", Value = "all" } }),
            }));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task BatchedAclUpdatesPatchEachItem()
    {
        var (client, handler, _, bodies) = Setup();
        EnqueueBatch(handler, bodies, """[{"id":"0","status":200},{"id":"1","status":200}]""");

        var acl = new[] { new AclEntry { Type = "user", Value = "alice@contoso.com" } };
        var results = await client.UpdateItemAclsBatchAsync(new[]
        {
            new AclUpdate("i1", acl), new AclUpdate("i2", acl),
        });

        Assert.All(results, r => Assert.True(r.Success));
        Assert.Contains("\"method\":\"PATCH\"", bodies[0]);
        Assert.Contains("\"acl\"", bodies[0]);
        Assert.DoesNotContain("everyone", bodies[0]);
    }

    [Fact]
    public async Task PostBatchRejectsMoreThanTwentyRequests()
    {
        var (client, _, _, _) = Setup();
        var payload = new JsonArray();
        for (var i = 0; i < 21; i++)
            payload.Add(new JsonObject { ["id"] = i.ToString(), ["method"] = "PUT", ["url"] = "/x" });
        await Assert.ThrowsAsync<ArgumentException>(() => client.PostBatchAsync(payload, default));
    }
}

public class AdaptiveConcurrencyTests
{
    [Fact]
    public void DialsDownOnThrottleWithAFloorOfOne()
    {
        Metrics.ResetForTests();
        var adaptive = new AdaptiveConcurrency(3);
        Assert.Equal(3, adaptive.Current);
        adaptive.OnThrottle();
        Assert.Equal(2, adaptive.Current);
        adaptive.OnThrottle();
        adaptive.OnThrottle();
        adaptive.OnThrottle();
        Assert.Equal(1, adaptive.Current);  // floor
        Assert.Equal(4, Metrics.Get("altrata_graph_throttle_429_total"));
        Metrics.ResetForTests();
    }

    [Fact]
    public void RampsUpAfterThreeConsecutiveSuccessesUpToTheMax()
    {
        var adaptive = new AdaptiveConcurrency(4);
        adaptive.OnThrottle();
        adaptive.OnThrottle();
        Assert.Equal(2, adaptive.Current);

        adaptive.OnSuccess();
        adaptive.OnSuccess();
        Assert.Equal(2, adaptive.Current);  // streak of 2 is not enough
        adaptive.OnSuccess();
        Assert.Equal(3, adaptive.Current);  // 3rd success ramps

        adaptive.OnThrottle();              // throttle resets the streak
        adaptive.OnSuccess();
        adaptive.OnSuccess();
        Assert.Equal(2, adaptive.Current);

        for (var i = 0; i < 12; i++)
            adaptive.OnSuccess();
        Assert.Equal(4, adaptive.Current);  // capped at max
    }
}

// ---- throughput knobs ---------------------------------------------------------------

public class BatchWorkersKnobTests : IDisposable
{
    private static readonly string[] Vars =
    {
        "CONNECTOR_ID", "CONNECTOR_NAME", "CONNECTOR_DESCRIPTION", "AAD_APP_CLIENT_ID",
        "AAD_APP_TENANT_ID", "SECRET_AAD_APP_CLIENT_SECRET",
        "GRAPH_BATCH_WORKERS", "GRAPH_CONCURRENT_BATCHES", "GRAPH_BATCH_SIZE",
    };

    public BatchWorkersKnobTests()
    {
        Environment.SetEnvironmentVariable("CONNECTOR_ID", "AltrataKnobTest");
        Environment.SetEnvironmentVariable("CONNECTOR_NAME", "t");
        Environment.SetEnvironmentVariable("CONNECTOR_DESCRIPTION", "t");
        Environment.SetEnvironmentVariable("AAD_APP_CLIENT_ID", "c");
        Environment.SetEnvironmentVariable("AAD_APP_TENANT_ID", "t");
        Environment.SetEnvironmentVariable("SECRET_AAD_APP_CLIENT_SECRET", "s");
    }

    public void Dispose()
    {
        foreach (var name in Vars)
            Environment.SetEnvironmentVariable(name, null);
    }

    [Fact]
    public void DefaultIsEightWorkers()
    {
        Assert.Equal(8, AppConfig.Load().GraphBatchWorkers);
    }

    [Fact]
    public void GraphBatchWorkersIsRead()
    {
        Environment.SetEnvironmentVariable("GRAPH_BATCH_WORKERS", "4");
        Assert.Equal(4, AppConfig.Load().GraphBatchWorkers);
    }

    [Fact]
    public void ConcurrentBatchesAliasWinsWhenBothAreSet()
    {
        Environment.SetEnvironmentVariable("GRAPH_BATCH_WORKERS", "4");
        Environment.SetEnvironmentVariable("GRAPH_CONCURRENT_BATCHES", "2");
        Assert.Equal(2, AppConfig.Load().GraphBatchWorkers);
    }

    [Fact]
    public void BatchSizeIsClampedToTheGraphMaximum()
    {
        Environment.SetEnvironmentVariable("GRAPH_BATCH_SIZE", "50");
        Assert.Equal(20, AppConfig.Load().GraphBatchSize);
    }
}

// ---- connection sharding --------------------------------------------------------------

public class ShardingConfigTests : IDisposable
{
    public void Dispose() => Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, null);

    private const string ValidMap = """
        {
          "altrataPeopleA": ["PersonProfile", "CareerHistory", "BoardMembership"],
          "altrataWealthB": ["WealthIndicator", "RelationshipPath"],
          "altrataOrgsC":   ["Organization"]
        }
        """;

    [Fact]
    public void DisabledWhenEnvUnset()
    {
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, null);
        Assert.False(ShardingConfig.IsEnabled);
        Assert.False(ShardingConfig.TryLoad(out _, out var error));
        Assert.Null(error);  // disabled, not an error
    }

    [Fact]
    public void ValidPartitionLoads()
    {
        Assert.True(ShardingConfig.TryParse(ValidMap, out var shards, out var error));
        Assert.Null(error);
        Assert.Equal(3, shards.Count);
        Assert.Equal(Datasets.All.Length, shards.Sum(s => s.Datasets.Count));
        Assert.Contains(shards, s => s.ConnectionId == "altrataWealthB"
                                     && s.Datasets.Contains("WealthIndicator"));
    }

    [Fact]
    public void InvalidJsonIsReported()
    {
        Assert.False(ShardingConfig.TryParse("{not json", out _, out var error));
        Assert.Contains("not valid JSON", error);
    }

    [Fact]
    public void UnknownDatasetIsReported()
    {
        Assert.False(ShardingConfig.TryParse(
            """{"shardA": ["PersonProfile", "Bogus"]}""", out _, out var error));
        Assert.Contains("unknown dataset 'Bogus'", error);
    }

    [Fact]
    public void MissingDatasetsAreReported()
    {
        Assert.False(ShardingConfig.TryParse(
            """{"shardA": ["PersonProfile"]}""", out _, out var error));
        Assert.Contains("'Organization' is not assigned", error);
    }

    [Fact]
    public void DoubleAssignmentIsReported()
    {
        Assert.False(ShardingConfig.TryParse("""
            {
              "shardA": ["PersonProfile", "Organization", "BoardMembership", "RelationshipPath", "WealthIndicator", "CareerHistory"],
              "shardB": ["WealthIndicator"]
            }
            """, out _, out var error));
        Assert.Contains("assigned to both", error);
    }

    [Fact]
    public void InvalidConnectionIdIsReported()
    {
        Assert.False(ShardingConfig.TryParse(
            """{"Microsoft Shard!": ["PersonProfile", "Organization", "BoardMembership", "RelationshipPath", "WealthIndicator", "CareerHistory"]}""",
            out _, out var error));
        Assert.Contains("connection id", error!);
    }

    [Fact]
    public void EmptyShardArrayIsReported()
    {
        Assert.False(ShardingConfig.TryParse("""{"shardA": []}""", out _, out var error));
        Assert.Contains("non-empty array", error);
    }

    [Fact]
    public void ShardIdEqualToBaseConnectorIdIsRejected()
    {
        // A shard named the same as the base CONNECTOR_ID aliases the base's
        // own state store, so shard-aware commands would process/dispose it
        // twice. Reject it (case-insensitively) with a clear error.
        var map = """
            {
              "altrataBase":   ["PersonProfile", "CareerHistory", "BoardMembership"],
              "altrataWealthB":["WealthIndicator", "RelationshipPath", "Organization"]
            }
            """;
        Assert.False(ShardingConfig.TryParse(map, baseConnectorId: "AltrataBase",
            out _, out var error));
        Assert.Contains("must not equal the base", error!);
        Assert.Contains("altrataBase", error!);
    }

    [Fact]
    public void ShardIdCollisionIsSurfacedThroughTryLoad()
    {
        Environment.SetEnvironmentVariable("CONNECTOR_ID", "AltrataBase");
        try
        {
            Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, """
                {
                  "AltrataBase":   ["PersonProfile", "CareerHistory", "BoardMembership"],
                  "altrataWealthB":["WealthIndicator", "RelationshipPath", "Organization"]
                }
                """);
            Assert.False(ShardingConfig.TryLoad(out _, out var error));
            Assert.Contains("must not equal the base", error!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONNECTOR_ID", null);
        }
    }

    [Fact]
    public void DistinctShardIdsStillLoadWithBaseConnectorIdSet()
    {
        // Sanity: a base id that matches NO shard must not trip the new check.
        Assert.True(ShardingConfig.TryParse(ValidMap, baseConnectorId: "AltrataBase",
            out var shards, out var error));
        Assert.Null(error);
        Assert.Equal(3, shards.Count);
    }

    [Fact]
    public void ForShardRebindsTheConnectionId()
    {
        var baseConfig = TestFixtures.NewConfig();
        var shard = new Shard("altrataWealthB", new[] { Datasets.WealthIndicator });
        var shardConfig = ShardingConfig.ForShard(baseConfig, shard);

        Assert.Equal("altrataWealthB", shardConfig.ConnectorId);
        Assert.Contains("altrataWealthB", shardConfig.ConnectorName);
        Assert.Equal(baseConfig.AadClientId, shardConfig.AadClientId);      // everything else shared
        Assert.Equal(baseConfig.SeatListPath, shardConfig.SeatListPath);
        Assert.Equal("AltrataTest", baseConfig.ConnectorId);                // base untouched
    }
}

// ---- dead-letter concurrency ------------------------------------------------------------

public class DeadLetterConcurrencyTests
{
    [Fact]
    public async Task ConcurrentWritersNeverCorruptTheJsonlFile()
    {
        // Stress-harness invariant from the reference connector: N concurrent
        // writers (even via DIFFERENT store instances over the same file) must
        // produce exactly N×M parseable lines with no interleaving.
        var root = TestFixtures.NewTempDir("dlq_stress");
        var storeA = new FileStateStore("AltrataTest",
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));
        var storeB = new FileStateStore("AltrataTest",
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));

        const int writers = 16;
        const int perWriter = 50;
        var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            var store = w % 2 == 0 ? storeA : storeB;
            for (var i = 0; i < perWriter; i++)
            {
                var record = new DeadLetterRecord
                {
                    ItemId = $"item-{w}-{i}",
                    Dataset = "PersonProfile",
                    DeliveryId = "d1",
                    Error = new string('e', 200),  // long enough to expose torn writes
                    PayloadJson = $$"""{"id":"item-{{w}}-{{i}}","junk":"{{new string('x', 300)}}"}""",
                };
                if (i % 2 == 0)
                    store.AddDeadLetter(record);
                else
                    store.AddDeadLetters(new[] { record });
            }
        })).ToArray();
        await Task.WhenAll(tasks);

        // Every physical line parses (no torn/interleaved writes)...
        var rawLines = File.ReadAllLines(storeA.DeadLetterPath)
            .Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        Assert.Equal(writers * perWriter, rawLines.Length);
        foreach (var line in rawLines)
            System.Text.Json.JsonDocument.Parse(line);  // throws on corruption

        // ...and every record is present exactly once.
        var records = storeA.ReadDeadLetters();
        Assert.Equal(writers * perWriter, records.Count);
        Assert.Equal(writers * perWriter, records.Select(r => r.ItemId).Distinct().Count());
    }
}

// ---- hot-path log gating ------------------------------------------------------------------

public class LogGatingTests : IDisposable
{
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LOG_LEVEL", null);
        Environment.SetEnvironmentVariable("LOGS_DIR", null);
        Logging.EndRun();
    }

    [Fact]
    public void DebugIsDisabledByDefaultSoHotPathFormattingIsSkipped()
    {
        Environment.SetEnvironmentVariable("LOG_LEVEL", null);
        var logger = Logging.GetLogger("gating_test");
        Assert.False(logger.IsDebugEnabled);

        Environment.SetEnvironmentVariable("LOG_LEVEL", "debug");
        Assert.True(logger.IsDebugEnabled);
        Environment.SetEnvironmentVariable("LOG_LEVEL", "DEBUG");
        Assert.True(logger.IsDebugEnabled);
    }

    [Fact]
    public void DebugLinesAreFilteredFromTheLogFileUnlessEnabled()
    {
        var logs = TestFixtures.NewTempDir("gating_logs");
        Environment.SetEnvironmentVariable("LOGS_DIR", logs);
        Logging.StartRun("gating");
        var logger = Logging.GetLogger("gating_test");
        try
        {
            Environment.SetEnvironmentVariable("LOG_LEVEL", null);
            logger.Debug("SHOULD_NOT_APPEAR");
            Environment.SetEnvironmentVariable("LOG_LEVEL", "debug");
            logger.Debug("SHOULD_APPEAR");
            logger.Info("INFO_MARKER");
        }
        finally
        {
            Logging.EndRun();
        }

        var logFile = Directory.EnumerateFiles(logs, "connector.log", SearchOption.AllDirectories).Single();
        var content = File.ReadAllText(logFile);
        Assert.DoesNotContain("SHOULD_NOT_APPEAR", content);
        Assert.Contains("SHOULD_APPEAR", content);
        Assert.Contains("INFO_MARKER", content);
    }
}

// ---- HA close-with-failed-claims -------------------------------------------------------------

public class HaCloseCrawlTests
{
    private static FileStateStore NewState()
    {
        var root = TestFixtures.NewTempDir("haclose");
        return new FileStateStore("AltrataTest",
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));
    }

    [Fact]
    public void ExactlyOneNodeWinsAndTheWinIsPinnedForRetries()
    {
        var leases = new InMemoryLeaseStore();
        var state = NewState();
        var nodeA = new HaCoordinator(leases, "node-a");
        var nodeB = new HaCoordinator(leases, "node-b");

        Assert.True(nodeA.TryCloseCrawl("full:d1", anyFailedClaims: false, state));
        Assert.False(nodeB.TryCloseCrawl("full:d1", anyFailedClaims: false, state));
        // Pin semantics: a retry by the winner (lost ack) still reports true.
        Assert.True(nodeA.TryCloseCrawl("full:d1", anyFailedClaims: false, state));

        Assert.Equal("closed", state.GetValue("crawl_status_full:d1"));
        Assert.Equal("node-a", state.GetValue("crawl_closed_by_full:d1"));
    }

    [Fact]
    public void FailedClaimsStillCloseTheCrawlWithFailedStatus()
    {
        var leases = new InMemoryLeaseStore();
        var state = NewState();
        var node = new HaCoordinator(leases, "node-a");

        Assert.True(node.TryCloseCrawl("full:d2", anyFailedClaims: true, state));
        Assert.Equal("failed", state.GetValue("crawl_status_full:d2"));
    }

    [Fact]
    public async Task LosingNodeDoesNotRecordTheSyncTimestamp()
    {
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P1", "A", null, null)), 1));

        var leases = new InMemoryLeaseStore();
        // Another node already closed this crawl.
        Assert.True(leases.TryAcquire("crawl-close:full:d1", "node-other",
            HaCoordinator.CloseLeaseTtl, DateTime.UtcNow));

        var ha = new HaCoordinator(leases, "node-this");
        var engine = new CrawlEngine(harness.Config, harness.Graph, harness.State,
            harness.Identity, harness.Seats, harness.Alerts, ha);
        var result = await engine.RunAsync(CrawlKind.Full);

        Assert.False(result.ClosedByThisNode);
        Assert.Null(result.CloseStatus);
        Assert.Null(harness.State.GetLastSync(CrawlKind.Full));  // recorded by the closing node
        Assert.Single(harness.Graph.PutItems);                   // ingestion itself still ran
    }

    [Fact]
    public async Task RejectedDeliveryClosesTheCrawlAsFailedNotWedgedOpen()
    {
        using var harness = new CrawlHarness();
        var delivery = TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P1", "A", null, null)), 1));
        File.AppendAllText(Path.Combine(delivery.Directory, "persons.json"), "tampered");

        var ha = new HaCoordinator(new InMemoryLeaseStore(), "node-a");
        var engine = new CrawlEngine(harness.Config, harness.Graph, harness.State,
            harness.Identity, harness.Seats, harness.Alerts, ha);
        var result = await engine.RunAsync(CrawlKind.Full);

        Assert.True(result.ClosedByThisNode);
        Assert.Equal("failed", result.CloseStatus);
        Assert.Equal("failed", harness.State.GetValue("crawl_status_full:d1"));
        Assert.NotNull(harness.State.GetLastSync(CrawlKind.Full));  // winner records sync state either way
    }
}

// ---- batched re-ACL failure handling ------------------------------------------------------------

public class BatchedReAclTests
{
    [Fact]
    public async Task ReAclFailureLeavesHashUncommittedSoThePassReruns()
    {
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P1", "A", null, null)), 1));
        await harness.Engine.RunAsync(CrawlKind.Full);
        var originalHash = harness.State.GetValue(StateKeys.SeatListHash);

        // Seat change + a failing ACL PATCH for the existing item.
        harness.WriteSeats("alice@contoso.com", "bob@contoso.com", "carol@contoso.com");
        harness.Graph.FailingAclUpdates.Add("PersonProfile-P1");

        var seatSync = harness.Seats.SyncSeats();
        Assert.True(seatSync.RequiresReAcl);
        var updated = await harness.Engine.ReAclPassAsync(seatSync, default);

        Assert.Equal(0, updated);
        Assert.Equal(originalHash, harness.State.GetValue(StateKeys.SeatListHash));  // NOT committed
        Assert.Contains(harness.Alerts.Alerts, a => a.Event == "reacl_incomplete");

        // Fix the failure — the next pass completes and commits.
        harness.Graph.FailingAclUpdates.Clear();
        var retrySync = harness.Seats.SyncSeats();
        Assert.True(retrySync.RequiresReAcl);
        var retried = await harness.Engine.ReAclPassAsync(retrySync, default);
        Assert.Equal(1, retried);
        Assert.Equal(retrySync.SeatHash, harness.State.GetValue(StateKeys.SeatListHash));
    }
}
