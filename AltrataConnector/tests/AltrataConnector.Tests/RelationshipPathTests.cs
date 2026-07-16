// Improvement round 2: relationship-path materialization — index build from
// feeds, degree/count computation, bounded top-orgs, summary string, property
// mapping, seat-only ACL invariant, on/off toggle, and withdrawal drop.

using AltrataConnector.Altrata;
using AltrataConnector.Config;
using AltrataConnector.Entitlement;
using AltrataConnector.Graph;
using AltrataConnector.Identity;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

// ---- pure index computation ---------------------------------------------------

public class RelationshipPathIndexTests
{
    private static PathEdge Direct(string a, string b, double strength = 1.0,
        string? aName = null, string? bName = null) =>
        new(a, b, strength, 0, aName, bName);

    [Fact]
    public void FirstAndSecondDegreeAreComputedFromDirectEdges()
    {
        // P1—P2, P1—P3, P2—P4, P3—P4, P4—P5
        var index = RelationshipPathIndex.Build(new[]
        {
            Direct("P1", "P2"), Direct("P1", "P3"),
            Direct("P2", "P4"), Direct("P3", "P4"),
            Direct("P4", "P5"),
        }, Array.Empty<PersonOrg>());

        var p1 = index.Summarize("P1")!;
        Assert.Equal(2, p1.FirstDegreeCount);          // P2, P3
        Assert.Equal(1, p1.SecondDegreeCount);         // P4 (via P2/P3); P1 & first-degree excluded
        Assert.Equal(2, p1.PathCount);                 // two edges touch P1

        var p4 = index.Summarize("P4")!;
        Assert.Equal(3, p4.FirstDegreeCount);          // P2, P3, P5
        Assert.Equal(1, p4.SecondDegreeCount);         // P1 (via P2/P3); P5 has no further edges
    }

    [Fact]
    public void IndirectPathsCountButAreNotFirstDegree()
    {
        var index = RelationshipPathIndex.Build(new[]
        {
            Direct("P1", "P2"),
            new PathEdge("P1", "P9", 0.5, 2),  // 2 intermediaries — not a direct edge
        }, Array.Empty<PersonOrg>());

        var p1 = index.Summarize("P1")!;
        Assert.Equal(1, p1.FirstDegreeCount);   // only P2 is direct
        Assert.Equal(0, p1.SecondDegreeCount);
        Assert.Equal(2, p1.PathCount);          // both edges counted
    }

    [Fact]
    public void PersonWithNoPathsSummarizesToNull()
    {
        var index = RelationshipPathIndex.Build(new[] { Direct("P1", "P2") }, Array.Empty<PersonOrg>());
        Assert.Null(index.Summarize("P404"));
    }

    [Fact]
    public void SelfLoopsAndBlankEndpointsAreIgnored()
    {
        var index = RelationshipPathIndex.Build(new[]
        {
            new PathEdge("P1", "P1", 1, 0),   // self-loop
            new PathEdge("", "P2", 1, 0),     // blank
            Direct("P1", "P3"),
        }, Array.Empty<PersonOrg>());
        Assert.Equal(1, index.EdgeCount);
        Assert.Equal(1, index.Summarize("P1")!.FirstDegreeCount);
    }

    [Fact]
    public void TopConnectedOrgsAreBoundedAndRankedDeterministically()
    {
        // P1's neighbours P2,P3,P4,P5; their orgs give Acme×3, Beta×2, Gamma×1, Delta×1
        var edges = new[] { Direct("P1", "P2"), Direct("P1", "P3"), Direct("P1", "P4"), Direct("P1", "P5") };
        var orgs = new[]
        {
            new PersonOrg("P2", "Acme"), new PersonOrg("P3", "Acme"), new PersonOrg("P4", "Acme"),
            new PersonOrg("P2", "Beta"), new PersonOrg("P3", "Beta"),
            new PersonOrg("P4", "Gamma"), new PersonOrg("P5", "Delta"),
        };
        var index = RelationshipPathIndex.Build(edges, orgs, topOrgLimit: 3);

        var top = index.Summarize("P1")!.TopConnectedOrgs;
        Assert.Equal(new[] { "Acme", "Beta", "Delta" }, top);  // count desc; Delta<Gamma alphabetically at count 1
    }

    [Fact]
    public void TopOrgLimitIsHonoured()
    {
        var edges = new[] { Direct("P1", "P2") };
        var orgs = new[]
        {
            new PersonOrg("P2", "A"), new PersonOrg("P2", "B"),
            new PersonOrg("P2", "C"), new PersonOrg("P2", "D"),
        };
        Assert.Equal(2, RelationshipPathIndex.Build(edges, orgs, topOrgLimit: 2)
            .Summarize("P1")!.TopConnectedOrgs.Count);
    }

    [Theory]
    [InlineData(1, 0, "Acme", "1 path directly to Acme")]
    [InlineData(2, 1, "Acme", "2 paths via 1 intermediary to Acme")]
    [InlineData(5, 3, "Beta Corp", "5 paths via 3 intermediaries to Beta Corp")]
    public void SummaryStringReadsNaturally(int paths, int intermediaries, string target, string expected)
    {
        Assert.Equal(expected, RelationshipPathIndex.BuildSummaryString(paths, intermediaries, target));
    }

    [Fact]
    public void StrongestPathPicksMaxStrengthAndTargetsTheOtherEndpointsOrg()
    {
        var edges = new[]
        {
            new PathEdge("P1", "P2", 0.3, 1, "Ada", "Bob"),
            new PathEdge("P1", "P3", 0.9, 2, "Ada", "Cara"),  // strongest
        };
        var orgs = new[] { new PersonOrg("P3", "Analytical Engines") };
        var index = RelationshipPathIndex.Build(edges, orgs);

        var summary = index.Summarize("P1")!.StrongestPathSummary;
        Assert.Equal("2 paths via 2 intermediaries to Analytical Engines", summary);
    }

    [Fact]
    public void StrongestPathFallsBackToNameThenIdWhenNoOrg()
    {
        var byName = RelationshipPathIndex.Build(
            new[] { new PathEdge("P1", "P2", 1, 0, "Ada", "Bob Smith") }, Array.Empty<PersonOrg>());
        Assert.Equal("1 path directly to Bob Smith", byName.Summarize("P1")!.StrongestPathSummary);

        var byId = RelationshipPathIndex.Build(
            new[] { new PathEdge("P1", "P2", 1, 0) }, Array.Empty<PersonOrg>());
        Assert.Equal("1 path directly to P2", byId.Summarize("P1")!.StrongestPathSummary);
    }
}

// ---- extraction from feeds ----------------------------------------------------

public class PathExtractorTests
{
    private static FeedRecord Rec(string dataset, params (string K, string? V)[] fields) => new()
    {
        Dataset = dataset,
        Fields = fields.ToDictionary(f => f.K, f => f.V, StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public void RelationshipEdgesAreExtractedWithStrengthAndIntermediaries()
    {
        var build = new PathIndexBuild();
        PathExtractor.Accumulate(build, Datasets.RelationshipPath, new[]
        {
            Rec(Datasets.RelationshipPath, ("id", "R1"), ("from_person_id", "P1"),
                ("to_person_id", "P2"), ("path_strength", "0.8"), ("intermediary_count", "0"),
                ("from_person_name", "Ada"), ("to_person_name", "Bob")),
            Rec(Datasets.RelationshipPath, ("id", "R2"), ("from_person_id", "P1"),
                ("to_person_id", "P9"), ("path_strength", "0.4"), ("intermediary_count", "2")),
        });

        Assert.Equal(2, build.Edges.Count);
        Assert.Equal(0.8, build.Edges[0].Strength);
        Assert.Equal(0, build.Edges[0].Intermediaries);
        Assert.Equal("Ada", build.Edges[0].PersonAName);
        Assert.Equal(2, build.Edges[1].Intermediaries);
    }

    [Fact]
    public void TombstonedRelationshipsAndBoardRowsAreSkipped()
    {
        var build = new PathIndexBuild();
        PathExtractor.Accumulate(build, Datasets.RelationshipPath, new[]
        {
            Rec(Datasets.RelationshipPath, ("id", "R1"), ("from_person_id", "P1"),
                ("to_person_id", "P2"), ("op", "delete")),
        });
        PathExtractor.Accumulate(build, Datasets.BoardMembership, new[]
        {
            Rec(Datasets.BoardMembership, ("id", "B1"), ("person_id", "P1"),
                ("org_name", "Acme"), ("is_deleted", "true")),
        });
        Assert.Empty(build.Edges);
        Assert.Empty(build.PersonOrgs);
    }

    [Fact]
    public void BoardMembershipResolvesOrgIdViaTheOrganizationDataset()
    {
        var build = new PathIndexBuild();
        PathExtractor.Accumulate(build, Datasets.Organization, new[]
        {
            Rec(Datasets.Organization, ("org_id", "O1"), ("organization_name", "Acme Corp")),
        });
        PathExtractor.Accumulate(build, Datasets.BoardMembership, new[]
        {
            Rec(Datasets.BoardMembership, ("id", "B1"), ("person_id", "P1"), ("org_id", "O1")),
            Rec(Datasets.BoardMembership, ("id", "B2"), ("person_id", "P2"), ("org_name", "Beta")),
        });
        Assert.Equal("Acme Corp", build.PersonOrgs.Single(o => o.PersonId == "P1").Org);
        Assert.Equal("Beta", build.PersonOrgs.Single(o => o.PersonId == "P2").Org);
    }

    [Fact]
    public void PersonProfileTombstonesAreCollectedAsWithdrawals()
    {
        var build = new PathIndexBuild();
        PathExtractor.Accumulate(build, Datasets.PersonProfile, new[]
        {
            Rec(Datasets.PersonProfile, ("id", "P1"), ("person_name", "Ada")),
            Rec(Datasets.PersonProfile, ("id", "P9"), ("op", "delete")),
        });
        Assert.Equal(new[] { "P9" }, build.WithdrawnPersonIds);
    }
}

// ---- store round-trip + withdrawal --------------------------------------------

public class PathIndexStoreTests : IDisposable
{
    private readonly SqliteIdentityStore _store;

    public PathIndexStoreTests() =>
        _store = new SqliteIdentityStore(Path.Combine(TestFixtures.NewTempDir("pathstore"), "identity.db"));

    public void Dispose() => _store.Dispose();

    [Fact]
    public void EdgesAndOrgsRoundTrip()
    {
        _store.ReplacePathIndex(
            new[] { new PathEdge("P1", "P2", 0.7, 0, "Ada", "Bob") },
            new[] { new PersonOrg("P2", "Acme") });

        var (edges, orgs) = _store.LoadPathIndex();
        Assert.Single(edges);
        Assert.Equal("P1", edges[0].PersonA);
        Assert.Equal("P2", edges[0].PersonB);
        Assert.Equal(0.7, edges[0].Strength);
        Assert.Equal("Ada", edges[0].PersonAName);
        Assert.Equal("Acme", orgs.Single().Org);
        Assert.Equal(1, _store.CountPathEdges());
    }

    [Fact]
    public void ReplaceIsAFullRebuild()
    {
        _store.ReplacePathIndex(new[] { new PathEdge("P1", "P2", 1, 0) }, Array.Empty<PersonOrg>());
        _store.ReplacePathIndex(new[] { new PathEdge("P3", "P4", 1, 0) }, Array.Empty<PersonOrg>());
        var (edges, _) = _store.LoadPathIndex();
        Assert.Single(edges);
        Assert.Equal("P3", edges[0].PersonA);
    }

    [Fact]
    public void RemovePersonDropsAllTheirEdgesAndOrgs()
    {
        _store.ReplacePathIndex(new[]
        {
            new PathEdge("P1", "P2", 1, 0),
            new PathEdge("P2", "P3", 1, 0),
            new PathEdge("P4", "P5", 1, 0),
        }, new[] { new PersonOrg("P2", "Acme"), new PersonOrg("P4", "Beta") });

        _store.RemovePersonFromPathIndex("P2");

        var (edges, orgs) = _store.LoadPathIndex();
        Assert.DoesNotContain(edges, e => e.PersonA == "P2" || e.PersonB == "P2");
        Assert.Single(edges);  // only P4—P5 survives
        Assert.DoesNotContain(orgs, o => o.PersonId == "P2");
    }

    [Fact]
    public void WipeAllClearsThePathIndex()
    {
        _store.ReplacePathIndex(new[] { new PathEdge("P1", "P2", 1, 0) },
            new[] { new PersonOrg("P1", "Acme") });
        _store.WipeAll();
        Assert.Equal(0, _store.CountPathEdges());
        Assert.Empty(_store.LoadPathIndex().Edges);
    }
}

// ---- transformer property mapping + seat invariant ----------------------------

public class PathTransformerTests
{
    private static readonly IReadOnlyList<AclEntry> SeatAcl =
        new[] { new AclEntry { Type = "user", Value = "alice@contoso.com" } };

    private static FeedRecord Person(string id, string name) => new()
    {
        Dataset = Datasets.PersonProfile,
        Fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = id, ["person_name"] = name,
        },
    };

    private static RelationshipPathIndex SampleIndex() => RelationshipPathIndex.Build(new[]
    {
        new PathEdge("P1", "P2", 0.9, 0, "Ada", "Bob"),
        new PathEdge("P1", "P3", 0.5, 0, "Ada", "Cara"),
        new PathEdge("P2", "P4", 0.5, 0),
    }, new[] { new PersonOrg("P2", "Acme"), new PersonOrg("P3", "Beta") });

    [Fact]
    public void PathPropertiesAreMaterializedOntoPersonItems()
    {
        var transformer = new ItemTransformer(pathIndex: SampleIndex());
        var item = transformer.Transform(Person("P1", "Ada"), SeatAcl);

        Assert.Equal(2, item.Properties["firstDegreeCount"]);
        Assert.Equal(1, item.Properties["secondDegreeCount"]);   // P4 via P2
        Assert.Equal(2, item.Properties["pathCount"]);
        Assert.Equal(new List<string> { "Acme", "Beta" }, item.Properties["topConnectedOrgs"]);
        Assert.Equal("2 paths directly to Acme", item.Properties["strongestPathSummary"]);
    }

    [Fact]
    public void PersonWithoutPathsGetsNoPathProperties()
    {
        var transformer = new ItemTransformer(pathIndex: SampleIndex());
        var item = transformer.Transform(Person("P404", "Nobody"), SeatAcl);
        Assert.False(item.Properties.ContainsKey("firstDegreeCount"));
        Assert.False(item.Properties.ContainsKey("strongestPathSummary"));
    }

    [Fact]
    public void NonPersonDatasetsAreNeverMaterialized()
    {
        var transformer = new ItemTransformer(pathIndex: SampleIndex());
        var org = new FeedRecord
        {
            Dataset = Datasets.Organization,
            Fields = new Dictionary<string, string?> { ["org_id"] = "P1", ["organization_name"] = "X" },
        };
        var item = transformer.Transform(org, SeatAcl);
        Assert.False(item.Properties.ContainsKey("firstDegreeCount"));
    }

    [Fact]
    public void NoIndexMeansNoPathProperties()
    {
        var transformer = new ItemTransformer();  // pathIndex null (feature off)
        var item = transformer.Transform(Person("P1", "Ada"), SeatAcl);
        Assert.False(item.Properties.ContainsKey("firstDegreeCount"));
    }

    [Fact]
    public void PathMaterializationKeepsTheAclSeatOnly()
    {
        var transformer = new ItemTransformer(pathIndex: SampleIndex());
        var item = transformer.Transform(Person("P1", "Ada"), SeatAcl);

        Assert.Same(SeatAcl, item.Acl);
        Assert.All(item.Acl, e => Assert.Equal("user", e.Type));
        Assert.DoesNotContain(item.Acl, e => e.Type is "everyone" or "everyoneExceptGuests");
    }

    [Fact]
    public void EveryoneAclIsRejectedEvenOnTheMaterializedPath()
    {
        var transformer = new ItemTransformer(pathIndex: SampleIndex());
        var everyone = new[] { new AclEntry { Type = "everyone", Value = "all" } };
        Assert.Throws<EntitlementViolationException>(() => transformer.Transform(Person("P1", "Ada"), everyone));
    }
}

// ---- config knobs -------------------------------------------------------------

public class RelationshipPathKnobTests : IDisposable
{
    public RelationshipPathKnobTests()
    {
        foreach (var (k, v) in new[]
                 {
                     ("CONNECTOR_ID", "AltrataPathTest"), ("CONNECTOR_NAME", "t"),
                     ("CONNECTOR_DESCRIPTION", "t"), ("AAD_APP_CLIENT_ID", "c"),
                     ("AAD_APP_TENANT_ID", "t"), ("SECRET_AAD_APP_CLIENT_SECRET", "s"),
                 })
            Environment.SetEnvironmentVariable(k, v);
    }

    public void Dispose()
    {
        foreach (var k in new[]
                 {
                     "CONNECTOR_ID", "CONNECTOR_NAME", "CONNECTOR_DESCRIPTION", "AAD_APP_CLIENT_ID",
                     "AAD_APP_TENANT_ID", "SECRET_AAD_APP_CLIENT_SECRET",
                     "RELATIONSHIP_PATHS", "RELATIONSHIP_TOP_ORGS",
                 })
            Environment.SetEnvironmentVariable(k, null);
    }

    [Fact]
    public void OffByDefault()
    {
        var config = AppConfig.Load();
        Assert.False(config.RelationshipPaths);
        Assert.Equal(3, config.RelationshipTopOrgs);
    }

    [Fact]
    public void KnobsAreRead()
    {
        Environment.SetEnvironmentVariable("RELATIONSHIP_PATHS", "true");
        Environment.SetEnvironmentVariable("RELATIONSHIP_TOP_ORGS", "5");
        var config = AppConfig.Load();
        Assert.True(config.RelationshipPaths);
        Assert.Equal(5, config.RelationshipTopOrgs);
    }

    [Fact]
    public void OutOfRangeTopOrgsFailsValidation()
    {
        Environment.SetEnvironmentVariable("RELATIONSHIP_TOP_ORGS", "0");
        Assert.Throws<ConfigurationError>(() => AppConfig.Load());
        Environment.SetEnvironmentVariable("RELATIONSHIP_TOP_ORGS", "11");
        Assert.Throws<ConfigurationError>(() => AppConfig.Load());
    }
}

// ---- end-to-end crawl integration ---------------------------------------------

public class RelationshipPathCrawlTests
{
    private const string Persons = """
        [{"id":"P1","person_name":"Ada Lovelace"},
         {"id":"P2","person_name":"Bob"},
         {"id":"P3","person_name":"Cara"},
         {"id":"P4","person_name":"Dan"}]
        """;
    private const string Rels = """
        [{"id":"R1","from_person_id":"P1","to_person_id":"P2","path_strength":"0.9","intermediary_count":"0"},
         {"id":"R2","from_person_id":"P1","to_person_id":"P3","path_strength":"0.5","intermediary_count":"0"},
         {"id":"R3","from_person_id":"P2","to_person_id":"P4","path_strength":"0.5","intermediary_count":"0"}]
        """;
    private const string Board = """
        [{"id":"B1","person_id":"P2","org_name":"Acme"},
         {"id":"B2","person_id":"P3","org_name":"Beta"}]
        """;

    private static void WriteFullDelivery(CrawlHarness harness, string id)
    {
        TestFixtures.WriteDelivery(harness.FeedPath, id,
            ("persons.json", Datasets.PersonProfile, Persons, 4),
            ("rels.json", Datasets.RelationshipPath, Rels, 3),
            ("board.json", Datasets.BoardMembership, Board, 2));
    }

    [Fact]
    public async Task PathPropertiesAreMaterializedDuringAFullCrawl()
    {
        using var harness = new CrawlHarness(configure: c => c with { RelationshipPaths = true });
        WriteFullDelivery(harness, "d1");

        await harness.Engine.RunAsync(CrawlKind.Full);

        var p1 = harness.Graph.PutItems.Single(i => i.Id == "PersonProfile-P1");
        Assert.Equal(2, p1.Properties["firstDegreeCount"]);
        Assert.Equal(1, p1.Properties["secondDegreeCount"]);
        Assert.Equal("2 paths directly to Acme", p1.Properties["strongestPathSummary"]);
        Assert.Equal(new List<string> { "Acme", "Beta" }, p1.Properties["topConnectedOrgs"]);

        // Every person item is still seat-only — path props never widen ACLs.
        foreach (var item in harness.Graph.PutItems)
            Assert.DoesNotContain(item.Acl, e => e.Type is "everyone" or "everyoneExceptGuests");

        // The index was persisted (3 edges) for reconcilability/inspection.
        Assert.Equal(3, harness.Identity.CountPathEdges());
    }

    [Fact]
    public async Task DisabledByDefaultLeavesItemsUnchanged()
    {
        using var harness = new CrawlHarness();  // RELATIONSHIP_PATHS off
        WriteFullDelivery(harness, "d1");

        await harness.Engine.RunAsync(CrawlKind.Full);

        var p1 = harness.Graph.PutItems.Single(i => i.Id == "PersonProfile-P1");
        Assert.False(p1.Properties.ContainsKey("firstDegreeCount"));
        Assert.Equal(0, harness.Identity.CountPathEdges());  // no index built
    }

    [Fact]
    public async Task WithdrawnPersonDropsFromThePathIndex()
    {
        using var harness = new CrawlHarness(configure: c => c with { RelationshipPaths = true });
        // Delta delivery: P3 is tombstoned; the rest still present.
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, """
                [{"id":"P1","person_name":"Ada"},{"id":"P2","person_name":"Bob"},{"id":"P3","op":"delete"}]
                """, 3),
            ("rels.json", Datasets.RelationshipPath, """
                [{"id":"R1","from_person_id":"P1","to_person_id":"P2","intermediary_count":"0"},
                 {"id":"R2","from_person_id":"P1","to_person_id":"P3","intermediary_count":"0"}]
                """, 2),
            ("board.json", Datasets.BoardMembership,
                """[{"id":"B1","person_id":"P3","org_name":"Beta"}]""", 1));

        await harness.Engine.RunAsync(CrawlKind.Full);

        // P3 was withdrawn → dropped from the index; its edge (P1—P3) is gone.
        var (edges, orgs) = harness.Identity.LoadPathIndex();
        Assert.DoesNotContain(edges, e => e.PersonA == "P3" || e.PersonB == "P3");
        Assert.DoesNotContain(orgs, o => o.PersonId == "P3");

        // P1 now has only one first-degree connection (P2).
        var p1 = harness.Graph.PutItems.Single(i => i.Id == "PersonProfile-P1");
        Assert.Equal(1, p1.Properties["firstDegreeCount"]);
    }

    [Fact]
    public async Task ChecksumRejectedDeliveryDoesNotFeedTheIndex()
    {
        using var harness = new CrawlHarness(configure: c => c with { RelationshipPaths = true });
        var delivery = TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("rels.json", Datasets.RelationshipPath, Rels, 3));
        File.AppendAllText(Path.Combine(delivery.Directory, "rels.json"), "tampered");

        await harness.Engine.RunAsync(CrawlKind.Full);

        Assert.Equal(0, harness.Identity.CountPathEdges());  // rejected delivery skipped
    }
}
