// Improvement round 1 tests: delta deliveries with tombstone withdrawal,
// tiered fuzzy entity resolution with review queue, and natively shard-aware
// retry-failed.

using AltrataConnector.Altrata;
using AltrataConnector.Commands;
using AltrataConnector.Config;
using AltrataConnector.Graph;
using AltrataConnector.Identity;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

// ---- delta deliveries & tombstones -------------------------------------------

public class TombstoneDetectionTests
{
    private static FeedRecord Record(params (string K, string? V)[] fields) => new()
    {
        Dataset = Datasets.PersonProfile,
        Fields = fields.ToDictionary(f => f.K, f => f.V, StringComparer.OrdinalIgnoreCase),
    };

    [Theory]
    [InlineData("op", "delete")]
    [InlineData("op", "DELETED")]
    [InlineData("action", "remove")]
    [InlineData("change_type", "purge")]
    [InlineData("is_deleted", "true")]
    [InlineData("deleted", "1")]
    public void DeleteMarkersAreTombstones(string field, string value)
    {
        Assert.True(Record(("id", "P1"), (field, value)).IsTombstone);
    }

    [Theory]
    [InlineData("op", "upsert")]
    [InlineData("op", "update")]
    [InlineData("is_deleted", "false")]
    [InlineData("deleted", "0")]
    public void NonDeleteMarkersAreNot(string field, string value)
    {
        Assert.False(Record(("id", "P1"), (field, value)).IsTombstone);
    }

    [Fact]
    public void PlainRecordsAreNotTombstones()
    {
        Assert.False(Record(("id", "P1"), ("person_name", "Ada")).IsTombstone);
    }
}

public class DeltaDeliveryTests
{
    private const string DeltaJson = """
        [{"id":"P1","person_name":"Ada Lovelace"},
         {"id":"P2","person_name":"Charles Babbage"},
         {"id":"P9","op":"delete"}]
        """;

    [Fact]
    public async Task TombstonesWithdrawItemsAndReconcile()
    {
        using var harness = new CrawlHarness();
        // P9 was ingested by an earlier full delivery.
        harness.Identity.RecordIngestedItem(new IngestedItem(
            "PersonProfile-P9", Datasets.PersonProfile, "oldhash", DateTime.UtcNow));
        TestFixtures.WriteDelivery(harness.FeedPath, "d1-delta",
            ("persons.json", Datasets.PersonProfile, DeltaJson, 3));

        var result = await harness.Engine.RunAsync(CrawlKind.Full);

        // Upserts PUT, tombstone withdrawn, registry updated.
        Assert.Equal(2, result.ItemsIngested);
        Assert.Equal(1, result.ItemsDeleted);
        Assert.Equal(0, result.ItemsDeadLettered);
        Assert.Contains("PersonProfile-P9", harness.Graph.DeletedItems);
        Assert.DoesNotContain(harness.Identity.ListIngestedItems(), i => i.ItemId == "PersonProfile-P9");

        // ingested + deleted + deadLettered == manifest ⇒ reconciled.
        Assert.Equal(Reconciliation.StatusReconciled, result.Reconciliations[0].Status);
        Assert.Equal(1, result.Reconciliations[0].TotalDeleted);
        Assert.True(harness.State.IsDeliveryProcessed("d1-delta"));
    }

    [Fact]
    public async Task FailedTombstoneIsDeadLetteredAsDeleteOp()
    {
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1-delta",
            ("persons.json", Datasets.PersonProfile, DeltaJson, 3));
        harness.Graph.FailingDeletes.Add("PersonProfile-P9");

        var result = await harness.Engine.RunAsync(CrawlKind.Full);

        Assert.Equal(2, result.ItemsIngested);
        Assert.Equal(0, result.ItemsDeleted);
        Assert.Equal(1, result.ItemsDeadLettered);

        var deadLetters = harness.State.ReadDeadLetters();
        Assert.Single(deadLetters);
        Assert.Equal("PersonProfile-P9", deadLetters[0].ItemId);
        Assert.Equal(DeadLetterOps.Delete, deadLetters[0].Op);
        Assert.Empty(deadLetters[0].PayloadJson);

        // Still reconciled: every manifest record is accounted for.
        Assert.Equal(Reconciliation.StatusReconciled, result.Reconciliations[0].Status);
    }

    [Fact]
    public async Task RetryFailedReplaysDeleteOps()
    {
        var root = TestFixtures.NewTempDir("retrydel");
        var graph = new FakeGraphClient();
        using var runtime = TestFixtures.NewRuntime(TestFixtures.NewConfig(), graph, root);
        runtime.Identity.RecordIngestedItem(new IngestedItem(
            "PersonProfile-P9", Datasets.PersonProfile, "h", DateTime.UtcNow));
        runtime.State.AddDeadLetter(new DeadLetterRecord
        {
            ItemId = "PersonProfile-P9",
            Dataset = Datasets.PersonProfile,
            Op = DeadLetterOps.Delete,
        });

        var result = await CommandRegistry.RetryFailedAsync(runtime, clearOnSuccess: true);

        Assert.Equal(true, result);
        Assert.Contains("PersonProfile-P9", graph.DeletedItems);
        Assert.Empty(graph.PutItems);                       // delete replay never PUTs
        Assert.Empty(runtime.State.ReadDeadLetters());
        Assert.Equal(0, runtime.Identity.CountIngestedItems());
    }

    [Fact]
    public void LegacyDeadLetterLinesDefaultToUpsertOp()
    {
        // Queue files written before the op field existed must stay replayable.
        var record = System.Text.Json.JsonSerializer.Deserialize<DeadLetterRecord>(
            """{"ItemId":"x","Dataset":"PersonProfile","Error":"boom"}""");
        Assert.NotNull(record);
        Assert.Equal(DeadLetterOps.Upsert, record!.Op);
    }
}

public class DeleteBatchTests
{
    [Fact]
    public async Task NotFoundInsideTheBatchCountsAsIdempotentSuccess()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueJson(200, """{"access_token":"tok","expires_in":3600}""");
        var bodies = new List<string>();
        handler.Enqueue(req =>
        {
            bodies.Add(req.Content!.ReadAsStringAsync().Result);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"responses":[{"id":"0","status":204},{"id":"1","status":404}]}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        });

        var client = new GraphClient(TestFixtures.NewConfig(), handler, (_, _) => Task.CompletedTask);
        var results = await client.DeleteItemsBatchAsync(new[] { "i1", "i2" });

        Assert.All(results, r => Assert.True(r.Success));   // 404 = already gone
        Assert.Contains("\"method\":\"DELETE\"", bodies[0]);
        Assert.Contains("/items/i1", bodies[0]);
        Assert.Contains("/items/i2", bodies[0]);
    }

    [Fact]
    public async Task NotFoundIsStillAFailureForPuts()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueJson(200, """{"access_token":"tok","expires_in":3600}""");
        handler.EnqueueJson(200, """{"responses":[{"id":"0","status":404}]}""");

        var client = new GraphClient(TestFixtures.NewConfig(), handler, (_, _) => Task.CompletedTask);
        var item = new ExternalItem
        {
            Id = "P1",
            Acl = new[] { new AclEntry { Type = "user", Value = "u" } },
            Properties = new Dictionary<string, object?>(),
        };
        var results = await client.PutItemsBatchAsync(new[] { item });
        Assert.False(results[0].Success);
        Assert.Equal(404, results[0].Status);
    }
}

// ---- tiered fuzzy entity resolution --------------------------------------------

public class FuzzyMatcherTests
{
    [Theory]
    [InlineData("ada lovelace", "ada lovelace", 1.0)]
    [InlineData("ada lovelace", "ada k lovelace", 2.0 / 3.0)]
    [InlineData("ada lovelace", "charles babbage", 0.0)]
    [InlineData(null, "ada", 0.0)]
    [InlineData("ada", "", 0.0)]
    public void TokenOverlapIsJaccard(string? a, string? b, double expected)
    {
        Assert.Equal(expected, FuzzyMatcher.TokenOverlap(a, b), 6);
    }

    [Fact]
    public void ScoreWeightsNameEmployerAndRole()
    {
        var candidate = new CrmContact
        {
            Id = "C1",
            Name = "ada lovelace",
            Employer = "analytical engines",
            Role = "chief scientist",
        };
        var score = FuzzyMatcher.Score("ada lovelace", "analytical engines", "chief scientist", candidate);
        Assert.Equal(1.0, score.Total, 4);  // .6 + .3 + .1

        var noRole = FuzzyMatcher.Score("ada lovelace", "analytical engines", null, candidate);
        Assert.Equal(0.9, noRole.Total, 4);

        var nameOnly = FuzzyMatcher.Score("ada lovelace", null, null, candidate);
        Assert.Equal(0.6, nameOnly.Total, 4);
    }
}

public class TieredResolutionTests : IDisposable
{
    private readonly SqliteIdentityStore _store;
    private readonly string _root;

    public TieredResolutionTests()
    {
        _root = TestFixtures.NewTempDir("fuzzy");
        _store = new SqliteIdentityStore(Path.Combine(_root, "identity.db"));
        _store.ReplaceCrmContacts(new[]
        {
            new CrmContact
            {
                Id = "C1", Email = "ada@contoso.com", Name = "Ada Lovelace",
                Employer = "Analytical Engines", Role = "Chief Scientist",
            },
            new CrmContact { Id = "C2", Name = "Charles Babbage", Employer = "Difference Machines" },
        });
    }

    public void Dispose() => _store.Dispose();

    private sealed class RecordingSink : IMatchReviewSink
    {
        public List<MatchReviewEntry> Entries { get; } = new();
        public void Add(MatchReviewEntry entry) => Entries.Add(entry);
    }

    private EntityResolver Resolver(bool enabled = true, double threshold = 0.85,
        double floor = 0.6, IMatchReviewSink? sink = null) =>
        new(_store, new FuzzyOptions
        {
            Enabled = enabled, Threshold = threshold, ReviewFloor = floor, Review = sink,
        });

    [Fact]
    public void FuzzyTierIsOffByDefault()
    {
        var resolver = new EntityResolver(_store);  // no options → deterministic only
        Assert.Null(resolver.Match(null, "Ada Lovelace", "Analytical Engines International",
            "Chief Scientist"));
    }

    [Fact]
    public void DeterministicEmailStillWinsOverFuzzy()
    {
        var match = Resolver().Match("ADA@CONTOSO.COM", "Someone Else", "Elsewhere");
        Assert.NotNull(match);
        Assert.Equal(EntityResolver.RuleEmail, match!.MatchRule);
        Assert.Equal(1.0, match.Confidence);
    }

    [Fact]
    public void AboveThresholdAutoLinksWithProvenance()
    {
        // name 1.0 (.6) + employer 2/3 (.2) + role 1.0 (.1) = 0.9 ≥ 0.85
        var match = Resolver().Match(null, "Ada Lovelace", "Analytical Engines International",
            "Chief Scientist");
        Assert.NotNull(match);
        Assert.Equal("C1", match!.CrmContactId);
        Assert.Equal(EntityResolver.RuleFuzzy, match.MatchRule);
        Assert.Equal(0.9, match.Confidence, 2);
    }

    [Fact]
    public void BelowThresholdGoesToTheReviewQueueNotTheCrosswalk()
    {
        var sink = new RecordingSink();
        // name 1.0 (.6) + employer 2/3 (.2) + role 0 = 0.8 → [0.6, 0.85) → review
        var resolver = Resolver(sink: sink);
        var match = resolver.Resolve("P42", null, "Ada Lovelace",
            "Analytical Engines International");

        Assert.Null(match);
        Assert.Null(_store.GetCrosswalk("P42"));
        var entry = Assert.Single(sink.Entries);
        Assert.Equal("P42", entry.AltrataId);
        Assert.Equal("C1", entry.CandidateContactId);
        Assert.Equal(0.8, entry.Score, 2);
        Assert.Equal(1.0, entry.NameScore, 2);
    }

    [Fact]
    public void BelowTheFloorIsSilentlyUnmatched()
    {
        var sink = new RecordingSink();
        var match = Resolver(sink: sink).Match(null, "Ada Lovelace King", "Totally Unrelated Co");
        Assert.Null(match);
        Assert.Empty(sink.Entries);  // 2/3·.6 = .4 < floor
    }

    [Fact]
    public void FuzzyResolvePersistsProvenanceInTheCrosswalk()
    {
        var resolver = Resolver();
        var match = resolver.Resolve("P77", null, "Ada Lovelace",
            "Analytical Engines International", role: "Chief Scientist");
        Assert.NotNull(match);
        var entry = _store.GetCrosswalk("P77");
        Assert.NotNull(entry);
        Assert.Equal(EntityResolver.RuleFuzzy, entry!.MatchRule);
    }

    [Fact]
    public void ReviewQueueWritesJsonl()
    {
        var queue = new MatchReviewQueue("AltrataTest", logsDir: Path.Combine(_root, "logs"));
        queue.Add(new MatchReviewEntry
        {
            AltrataId = "P1", CandidateContactId = "C1", Score = 0.75,
        });
        var entries = queue.ReadAll();
        Assert.Single(entries);
        Assert.Equal(0.75, entries[0].Score);
        Assert.True(File.Exists(queue.Path));
    }

    [Fact]
    public void RoleColumnRoundTripsThroughTheStore()
    {
        var contact = _store.FindContactByEmail("ada@contoso.com");
        Assert.NotNull(contact);
        Assert.Equal("chief scientist", contact!.Role);  // stored normalized
        Assert.Equal(2, _store.ListCrmContacts().Count);
    }
}

public class FuzzyCrawlIntegrationTests
{
    [Fact]
    public async Task FuzzyLinkedItemCarriesRuleAndConfidenceProperties()
    {
        var crmDir = TestFixtures.NewTempDir("fuzzycrm");
        var crmPath = Path.Combine(crmDir, "contacts.json");
        File.WriteAllText(crmPath, """
            [{"id":"C9","name":"Ada Lovelace","employer":"Analytical Engines","title":"Chief Scientist"}]
            """);

        using var harness = new CrawlHarness(crmContactsPath: crmPath,
            configure: c => c with
            {
                EntityFuzzyMatching = true,
                EntityMatchThreshold = 0.85,
                EntityReviewFloor = 0.6,
            });
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, """
                [{"id":"P1","person_name":"Ada Lovelace","employer":"Analytical Engines International","title":"Chief Scientist"}]
                """, 1));

        await harness.Engine.RunAsync(CrawlKind.Full);

        var item = Assert.Single(harness.Graph.PutItems);
        Assert.Equal("C9", item.Properties["crmContactId"]);
        Assert.Equal(EntityResolver.RuleFuzzy, item.Properties["crmMatchRule"]);
        Assert.Equal("0.90", item.Properties["crmMatchConfidence"]);
    }

    [Fact]
    public async Task DeterministicMatchStampsConfidenceOne()
    {
        var crmDir = TestFixtures.NewTempDir("detcrm");
        var crmPath = Path.Combine(crmDir, "contacts.json");
        File.WriteAllText(crmPath, """[{"id":"C1","email":"ada@x.com","name":"Ada"}]""");

        using var harness = new CrawlHarness(crmContactsPath: crmPath);
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile,
                TestFixtures.PersonJson(("P1", "Ada", "ada@x.com", null)), 1));

        await harness.Engine.RunAsync(CrawlKind.Full);

        var item = Assert.Single(harness.Graph.PutItems);
        Assert.Equal("email", item.Properties["crmMatchRule"]);
        Assert.Equal("1.00", item.Properties["crmMatchConfidence"]);
    }
}

public class EntityKnobValidationTests : IDisposable
{
    public EntityKnobValidationTests()
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
        foreach (var name in new[]
                 {
                     "CONNECTOR_ID", "CONNECTOR_NAME", "CONNECTOR_DESCRIPTION", "AAD_APP_CLIENT_ID",
                     "AAD_APP_TENANT_ID", "SECRET_AAD_APP_CLIENT_SECRET",
                     "ENTITY_FUZZY_MATCHING", "ENTITY_MATCH_THRESHOLD", "ENTITY_REVIEW_FLOOR",
                 })
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void DefaultsAreOffStrictAndOrdered()
    {
        var config = AppConfig.Load();
        Assert.False(config.EntityFuzzyMatching);
        Assert.Equal(0.85, config.EntityMatchThreshold);
        Assert.Equal(0.6, config.EntityReviewFloor);
    }

    [Fact]
    public void KnobsAreRead()
    {
        Environment.SetEnvironmentVariable("ENTITY_FUZZY_MATCHING", "true");
        Environment.SetEnvironmentVariable("ENTITY_MATCH_THRESHOLD", "0.9");
        Environment.SetEnvironmentVariable("ENTITY_REVIEW_FLOOR", "0.5");
        var config = AppConfig.Load();
        Assert.True(config.EntityFuzzyMatching);
        Assert.Equal(0.9, config.EntityMatchThreshold);
        Assert.Equal(0.5, config.EntityReviewFloor);
    }

    [Fact]
    public void OutOfRangeThresholdFailsValidation()
    {
        Environment.SetEnvironmentVariable("ENTITY_MATCH_THRESHOLD", "1.5");
        Assert.Throws<ConfigurationError>(() => AppConfig.Load());
    }

    [Fact]
    public void FloorAboveThresholdFailsValidation()
    {
        Environment.SetEnvironmentVariable("ENTITY_MATCH_THRESHOLD", "0.7");
        Environment.SetEnvironmentVariable("ENTITY_REVIEW_FLOOR", "0.8");
        Assert.Throws<ConfigurationError>(() => AppConfig.Load());
    }
}

// ---- shard-aware retry-failed ---------------------------------------------------

public class ShardAwareRetryFailedTests : IDisposable
{
    private const string ShardMap = """
        {
          "altrataPeopleA": ["PersonProfile", "CareerHistory", "BoardMembership"],
          "altrataWealthB": ["WealthIndicator", "RelationshipPath", "Organization"]
        }
        """;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, null);
        CommandRegistry.ShardRuntimeFactory = null;
    }

    [Fact]
    public async Task EachShardQueueReplaysAgainstItsOwnConnection()
    {
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, ShardMap);
        var root = TestFixtures.NewTempDir("shardretry");

        var shardGraphs = new Dictionary<string, FakeGraphClient>();
        var shardRuntimes = new Dictionary<string, Runtime>();
        CommandRegistry.ShardRuntimeFactory = (_, shard) =>
        {
            var graph = new FakeGraphClient();
            shardGraphs[shard.ConnectionId] = graph;
            var runtime = TestFixtures.NewRuntime(
                TestFixtures.NewConfig() with { ConnectorId = shard.ConnectionId },
                graph, Path.Combine(root, shard.ConnectionId));
            shardRuntimes[shard.ConnectionId] = runtime;
            return runtime;
        };

        // Seed each shard's queue: one upsert on A, one delete on B.
        var seedA = new FileStateStore("altrataPeopleA",
            logsDir: Path.Combine(root, "altrataPeopleA", "logs"),
            dataDir: Path.Combine(root, "altrataPeopleA", "data"));
        seedA.AddDeadLetter(new DeadLetterRecord
        {
            ItemId = "PersonProfile-P1",
            Dataset = Datasets.PersonProfile,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new ExternalItem
            {
                Id = "PersonProfile-P1",
                Acl = new[] { new AclEntry { Type = "user", Value = "alice@contoso.com" } },
                Properties = new Dictionary<string, object?>(),
            }),
        });
        var seedB = new FileStateStore("altrataWealthB",
            logsDir: Path.Combine(root, "altrataWealthB", "logs"),
            dataDir: Path.Combine(root, "altrataWealthB", "data"));
        seedB.AddDeadLetter(new DeadLetterRecord
        {
            ItemId = "WealthIndicator-W1",
            Dataset = Datasets.WealthIndicator,
            Op = DeadLetterOps.Delete,
        });

        var baseGraph = new FakeGraphClient();
        var baseRuntime = TestFixtures.NewRuntime(TestFixtures.NewConfig(), baseGraph,
            Path.Combine(root, "base"));

        var result = await CommandRegistry.RetryFailedAsync(baseRuntime, clearOnSuccess: true);
        Assert.Equal(true, result);

        // Shard A replayed its upsert on ITS connection; shard B its delete.
        Assert.Single(shardGraphs["altrataPeopleA"].PutItems);
        Assert.Empty(shardGraphs["altrataPeopleA"].DeletedItems);
        Assert.Contains("WealthIndicator-W1", shardGraphs["altrataWealthB"].DeletedItems);
        Assert.Empty(shardGraphs["altrataWealthB"].PutItems);
        Assert.Empty(baseGraph.PutItems);  // nothing leaked onto the base connection

        // Both shard queues cleared.
        Assert.Empty(seedA.ReadDeadLetters());
        Assert.Empty(seedB.ReadDeadLetters());
        baseRuntime.Dispose();
    }

    [Fact]
    public async Task ShardFailuresStayInThatShardsQueue()
    {
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, ShardMap);
        var root = TestFixtures.NewTempDir("shardretry2");

        CommandRegistry.ShardRuntimeFactory = (_, shard) =>
        {
            var graph = new FakeGraphClient();
            if (shard.ConnectionId == "altrataWealthB")
                graph.FailingDeletes.Add("WealthIndicator-W1");
            return TestFixtures.NewRuntime(
                TestFixtures.NewConfig() with { ConnectorId = shard.ConnectionId },
                graph, Path.Combine(root, shard.ConnectionId));
        };

        var seedB = new FileStateStore("altrataWealthB",
            logsDir: Path.Combine(root, "altrataWealthB", "logs"),
            dataDir: Path.Combine(root, "altrataWealthB", "data"));
        seedB.AddDeadLetter(new DeadLetterRecord
        {
            ItemId = "WealthIndicator-W1",
            Dataset = Datasets.WealthIndicator,
            Op = DeadLetterOps.Delete,
        });

        using var baseRuntime = TestFixtures.NewRuntime(TestFixtures.NewConfig(),
            new FakeGraphClient(), Path.Combine(root, "base"));

        var result = await CommandRegistry.RetryFailedAsync(baseRuntime, clearOnSuccess: false);

        Assert.Equal(false, result);
        var remaining = seedB.ReadDeadLetters();
        Assert.Single(remaining);
        Assert.Equal(2, remaining[0].Attempts);              // attempt count bumped
        Assert.Equal(DeadLetterOps.Delete, remaining[0].Op); // op preserved for the next replay
    }
}

public class DeletedReconciliationMathTests
{
    [Fact]
    public void DeletedCountsTowardReconciliation()
    {
        var file = new FileReconciliation
        {
            File = "p.json", Dataset = "PersonProfile",
            ManifestCount = 10, ParsedCount = 10, Ingested = 7, Deleted = 2, DeadLettered = 1,
        };
        Assert.True(file.Reconciled);
        Assert.Equal(0, file.Delta);

        var summary = Reconciliation.Summarize("d1", new[] { file });
        Assert.Equal(Reconciliation.StatusReconciled, summary.Status);
        Assert.Equal(2, summary.TotalDeleted);
    }

    [Fact]
    public void MissingDeletesBreakReconciliation()
    {
        var file = new FileReconciliation
        {
            File = "p.json", Dataset = "PersonProfile",
            ManifestCount = 10, ParsedCount = 10, Ingested = 7, Deleted = 1, DeadLettered = 1,
        };
        Assert.False(file.Reconciled);
    }
}
