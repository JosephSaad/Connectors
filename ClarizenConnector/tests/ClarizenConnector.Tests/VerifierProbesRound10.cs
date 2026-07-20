// Verifier probes, round 10 — closing the two gaps around the write-path
// enforcement that round 9 shipped.
//
// FIX 1 — A BLANK GRAPH PROPERTY NAME WAS NOT LOUD.
//   AssertDeclared rejected a blank/whitespace name with ArgumentException, not
//   UndeclaredGraphPropertyException. The crawl's per-record catch(Exception)
//   therefore demoted it to a dead-lettered bad row: the crawl CLOSED NORMALLY
//   and THE SYNC CURSOR ADVANCED past records that never reached the index.
//   Observed before the fix, SelectedFields = {"Name": ""}, 4 records:
//       crawl exception: NONE / summary ingested=0 failed=4 failedObjects=0
//       graph calls with body = 0
//       lastSync written = True ; checkpoint = null ; dead-lettered = 4
//       direct stamp of "" and "   " -> ArgumentException
//   Two fixes, because two layers let it through:
//     (a) the blank check now raises UndeclaredGraphPropertyException, the same
//         type (and now the same BASE type) as an undeclared name, so the crawl's
//         escalation catch re-throws it and the run aborts;
//     (b) SchemaConfig.Validate rejects a blank selectedFields property VALUE at
//         config load, so a config that cannot possibly work never starts a crawl.
//   AUDIT (the brief asked for it): the same "fatal configuration error disguised
//   as a bad row" shape existed for the REGISTRY LOAD ITSELF. A graph-schema.json
//   that is missing, is not a JSON array, or declares no usable names throws from
//   inside AssertDeclared — i.e. on the per-record stamp path. Observed before the
//   fix with an override pointing at "[]", 4 records:
//       crawl exception: NONE / ingested=0 failed=4 / lastSync written = True
//   Those loads are now wrapped in GraphSchemaUnavailableException, which shares
//   the GraphSchemaConfigurationException base the crawl escalates on.
//
// FIX 2 — ExternalItem.ToJson's DOCUMENTED LAYER-2 GUARANTEE WAS FALSE INSIDE A
//   SUSPENSION SCOPE. GraphPropertyRegistry.SuspendEnforcement is internal, so it
//   is reachable from anywhere in the connector assembly, and ToJson's re-check
//   honoured it. Observed before the fix:
//       using (GraphPropertyRegistry.SuspendEnforcement()) {
//         item.Properties["HV10_SENTINEL_TOJSON"]="HV10_LEAK";
//         body = item.ToJson().ToJsonString(); }
//       -> exception = NONE
//       -> body = {"id":"x","properties":{"HV10_SENTINEL_TOJSON":"HV10_LEAK"},...}
//   ToJson now calls AssertDeclaredIgnoringSuspension. Safe because the only
//   production caller of SuspendEnforcement, StampedPropertyInventory.Capture,
//   reads property NAMES off the item and never serializes or sends it — E3/E4
//   pin that it still works.
//
// SCOPE NOTE, stated so it is not mistaken for a claim: these probes cover the
// blank-name and registry-load paths. The "config/graph-schema.json missing
// entirely" branch of EnsureLoaded (ResolvePath() returning null) is NOT covered
// behaviourally — the test host always has a config directory on both the
// working-directory and AppContext.BaseDirectory probes, so the branch is not
// reachable from a test. It is the same wrap as the branch D2/D3 do cover.

using System.Text.Json.Nodes;
using ClarizenConnector.Commands;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;

namespace ClarizenConnector.Tests;

/// <summary>Points GraphPropertyRegistry at an arbitrary graph-schema.json body.</summary>
internal sealed class GraphSchemaBodyScope : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string? _previous;

    public GraphSchemaBodyScope(string body)
    {
        _previous = GraphPropertyRegistry.OverridePath;
        var path = Path.Combine(_dir.Path, "graph-schema.json");
        File.WriteAllText(path, body);
        GraphPropertyRegistry.OverridePath = path;
    }

    public void Dispose()
    {
        GraphPropertyRegistry.OverridePath = _previous;
        _dir.Dispose();
    }
}

public class VerifierRound10BlankPropertyNameIsFatal
{
    /// <summary>Every blank shape, covered per shape rather than collectively:
    /// empty, spaces, tab. A guard written as <c>Length == 0</c> passes the first
    /// and fails the rest.</summary>
    public static TheoryData<string> BlankNames() => new() { "", "   ", "\t" };

    // ── C: the crawl aborts and the cursor is held ────────────────────────────

    // C1: THE HEADLINE. A blank Graph property name aborts the crawl, sends
    // nothing, dead-letters nothing, and — the actual harm — does NOT advance the
    // sync cursor. Before the fix: no exception, crawl completed, lastSync
    // written, 4 records dead-lettered, 0 items indexed.
    [Theory]
    [MemberData(nameof(BlankNames))]
    public async Task C1_BlankPropertyName_AbortsTheCrawl_AndHoldsTheCursor(string blank)
    {
        using var fixture = new GraphIngestTests.Fixture(recordCount: 4, chunkSize: 2);
        fixture.Schema.ObjectList[0].SelectedFields =
            new Dictionary<string, string> { ["Name"] = blank };

        var exc = await Assert.ThrowsAsync<UndeclaredGraphPropertyException>(
            () => fixture.Pipeline().RunAsync(fullCrawl: true));

        Assert.Equal(blank, exc.PropertyName);
        Assert.DoesNotContain(fixture.Graph.Sent, s => s.Method == HttpMethod.Put);
        Assert.DoesNotContain(fixture.Graph.Sent, s => s.Path == "$batch");
        // The cursor is the point: an advanced lastSync means the next delta crawl
        // skips these records forever.
        Assert.Null(SyncState.ReadLastSync(fixture.Config.ConnectorId));
        Assert.Empty(SyncState.ReadFailedRecords(fixture.Config.ConnectorId));
    }

    // C2: the blank name is fatal by TYPE, from the write path, for every blank
    // shape — and it carries the base type the crawl's escalation catch keys on.
    // (S3 in VerifierProbesRound7 covers the same via the indexer; this pins the
    // base-type relationship the escalation actually depends on.)
    [Theory]
    [MemberData(nameof(BlankNames))]
    public void C2_BlankName_IsAGraphSchemaConfigurationException(string blank)
    {
        var item = new ExternalItem { Id = "Task_1" };
        var exc = Assert.Throws<UndeclaredGraphPropertyException>(
            () => item.Properties.Set(blank, "probe"));
        Assert.IsAssignableFrom<GraphSchemaConfigurationException>(exc);
        Assert.Equal(0, item.Properties.Count);
    }

    // C3: the blank check runs AHEAD of the suspension check — a decision, pinned.
    // If it moved below, StampedPropertyInventory would silently COLLECT a blank
    // name, validate-config would report it as ordinary drift instead of as a
    // load failure, and the crawl would be the first thing to notice.
    [Theory]
    [MemberData(nameof(BlankNames))]
    public void C3_BlankName_IsFatalEvenWhileEnforcementIsSuspended(string blank)
    {
        var item = new ExternalItem { Id = "Task_1" };
        using (GraphPropertyRegistry.SuspendEnforcement())
        {
            Assert.Throws<UndeclaredGraphPropertyException>(() => item.Properties.Set(blank, "v"));
        }
        Assert.False(GraphPropertyRegistry.EnforcementSuspended);
    }

    // ── D: config load rejects what cannot work; registry load is fatal too ────

    private static SchemaConfig SchemaMapping(string propertyName) => new()
    {
        ObjectList = new List<ObjectConfig>
        {
            new()
            {
                ObjectName = "Task",
                SelectedFields = new Dictionary<string, string> { ["Name"] = propertyName },
            },
        },
    };

    // D1: SchemaConfig.Validate rejects a blank property VALUE at load, per blank
    // shape. Before the fix all three were ACCEPTED.
    [Theory]
    [MemberData(nameof(BlankNames))]
    public void D1_BlankSelectedFieldsValue_IsRejectedAtConfigLoad(string blank)
    {
        var exc = Assert.Throws<InvalidDataException>(() => SchemaMapping(blank).Validate("probe.json"));
        Assert.Contains("BLANK Graph property name", exc.Message, StringComparison.Ordinal);
        Assert.Contains("Name", exc.Message, StringComparison.Ordinal);
    }

    // D1b: the complement — a good mapping still loads, and the RESERVED-name
    // rejection the blank check now runs in front of still fires. Guards against
    // "reject everything" and against the new clause shadowing the old one.
    [Fact]
    public void D1b_ValidateStillAcceptsGoodMappings_AndStillRejectsReservedOnes()
    {
        SchemaMapping("Title").Validate("probe.json");   // must not throw

        var reserved = Assert.Throws<InvalidDataException>(
            () => SchemaMapping(SchemaConfig.ReservedPropertyNames[0]).Validate("probe.json"));
        Assert.Contains("RESERVED", reserved.Message, StringComparison.Ordinal);

        // and the real deployed schema.json still loads.
        SchemaConfig.Load(Path.Combine(AppContext.BaseDirectory, "config", "schema.json"));
    }

    // D2: THE AUDIT FINDING. A graph-schema.json the registry cannot use is a
    // configuration fault raised from inside the per-record stamp path. Before the
    // fix it was an InvalidDataException that catch(Exception) dead-lettered,
    // record by record, while the crawl closed and the cursor advanced.
    [Theory]
    [InlineData("[]")]                                      // empty array
    [InlineData("""[{"name": "", "type": "String"}]""")]     // no usable names
    [InlineData("""{"name": "Title"}""")]                    // not an array
    [InlineData("{ this is not json")]                       // unparseable
    public async Task D2_UnusableGraphSchema_AbortsTheCrawl_AndHoldsTheCursor(string body)
    {
        using var scope = new GraphSchemaBodyScope(body);
        using var fixture = new GraphIngestTests.Fixture(recordCount: 4, chunkSize: 2);

        var exc = await Assert.ThrowsAsync<GraphSchemaUnavailableException>(
            () => fixture.Pipeline().RunAsync(fullCrawl: true));

        Assert.IsAssignableFrom<GraphSchemaConfigurationException>(exc);
        Assert.NotNull(exc.InnerException);
        Assert.DoesNotContain(fixture.Graph.Sent, s => s.Method == HttpMethod.Put);
        Assert.DoesNotContain(fixture.Graph.Sent, s => s.Path == "$batch");
        Assert.Null(SyncState.ReadLastSync(fixture.Config.ConnectorId));
        Assert.Empty(SyncState.ReadFailedRecords(fixture.Config.ConnectorId));
    }

    // D3: the same fault raised from a bare stamp, so the wrap is pinned
    // independently of the crawl's plumbing.
    [Fact]
    public void D3_UnusableGraphSchema_IsFatalAtTheStamp()
    {
        using var scope = new GraphSchemaBodyScope("[]");
        var item = new ExternalItem { Id = "Task_1" };

        var exc = Assert.Throws<GraphSchemaUnavailableException>(
            () => item.Properties.Set("Title", "v"));
        Assert.IsType<InvalidDataException>(exc.InnerException);
    }

    // D4: the complement — a healthy declaration still loads and still stamps.
    [Fact]
    public void D4_AHealthyDeclaration_StillStamps()
    {
        var item = new ExternalItem { Id = "Task_1" };
        item.Properties["Title"] = "v";
        Assert.Equal(1, item.Properties.Count);
    }


    // D5: the pre-existing MITIGATION is preserved, not replaced. validate-config
    // flagged this config before the fix (via the generic "Could not determine
    // which Graph properties the connector stamps" error, because the inventory's
    // stamp threw). It must still flag it after — now from the earlier and more
    // specific schema.json load check. Asserted as "reports at least one error
    // naming the blank mapping", so neither route can regress to silence.
    [Fact]
    public void D5_ValidateConfig_StillFlagsABlankPropertyMapping()
    {
        using var env = new EnvScope();
        foreach (var required in ValidateConfig.RequiredEnvVars)
            env.Set(required, "x");
        env.Set("CONNECTOR_ID", "ProbeConnector");

        using var dir = new TempDir();
        var schemaFile = Path.Combine(dir.Path, "schema.json");
        File.WriteAllText(
            schemaFile,
            """{"objectList":[{"objectName":"Task","selectedFields":{"Name":""}}]}""");
        var graphFile = Path.Combine(dir.Path, "graph-schema.json");
        File.WriteAllText(
            graphFile,
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config", "graph-schema.json")));

        var result = ValidateConfig.ValidateCore(schemaFile, graphFile);

        Assert.NotEmpty(result.Errors);
        Assert.False(result.Ok(strict: false));
        Assert.Contains(result.Errors, e => e.Contains("BLANK Graph property name", StringComparison.Ordinal));
    }

    // ── E: ToJson's layer-2 guarantee is unconditional ────────────────────────

    // E1: THE REPORTED LEAK. Inside a suspension scope, ToJson used to serialize
    // an undeclared property with no error at all.
    [Fact]
    public void E1_ToJson_RejectsUndeclaredProperty_EvenInsideASuspensionScope()
    {
        var item = new ExternalItem { Id = "x" };
        using (GraphPropertyRegistry.SuspendEnforcement())
        {
            item.Properties["HV10_SENTINEL_TOJSON"] = "HV10_LEAK";
            var exc = Assert.Throws<UndeclaredGraphPropertyException>(() => item.ToJson());
            Assert.Equal("HV10_SENTINEL_TOJSON", exc.PropertyName);
        }
    }

    // E2: the OTHER clause of the ignoring-suspension check — the blank-name one —
    // covered separately from the declared-name one, and covered so that the two
    // are actually distinguishable.
    //
    // Asserting only "a blank name throws UndeclaredGraphPropertyException" does
    // NOT cover this clause: with the clause deleted, a blank name falls through
    // to the declared-name lookup, which also throws UndeclaredGraphPropertyException
    // with the same PropertyName. (Confirmed by mutation: that weaker assertion
    // left the deleted clause alive.) The discriminator is a graph-schema.json
    // the registry CANNOT LOAD: only the blank clause, which runs before any load
    // is attempted, still reports the blank name rather than the load failure.
    [Theory]
    [MemberData(nameof(BlankNames))]
    public void E2_BlankName_IsDiagnosedBeforeTheDeclarationIsEvenLoaded(string blank)
    {
        using var scope = new GraphSchemaBodyScope("[]");
        using (GraphPropertyRegistry.SuspendEnforcement())
        {
            // The bag itself refuses a blank name, so reach the serializer's own
            // clause by asserting the registry call ToJson makes, directly.
            var exc = Assert.Throws<UndeclaredGraphPropertyException>(
                () => GraphPropertyRegistry.AssertDeclaredIgnoringSuspension(blank));
            Assert.Equal(blank, exc.PropertyName);
        }

        // Same discriminator on the write path, so both entry points are pinned.
        var item = new ExternalItem { Id = "x" };
        Assert.Throws<UndeclaredGraphPropertyException>(() => item.Properties.Set(blank, "v"));
    }

    // E3: the fix must not break the one production caller of SuspendEnforcement.
    // StampedPropertyInventory reads NAMES and never serializes, so it still
    // collects undeclared stamps — which is the whole reason the scope exists.
    [Fact]
    public void E3_StampedPropertyInventory_StillCollectsAgainstAReducedDeclaration()
    {
        var schema = SchemaConfig.Load(Path.Combine(AppContext.BaseDirectory, "config", "schema.json"));
        using var scope = new ReducedGraphSchemaScope("Title");

        var stamped = StampedPropertyInventory.Collect(schema);

        Assert.Contains("Title", stamped);   // observed despite not being declared
    }

    // E4: ToJson still serializes a fully declared item inside a suspension —
    // the check was made unconditional, not unconditionally fatal.
    [Fact]
    public void E4_ToJson_StillSerializesDeclaredProperties_InsideASuspensionScope()
    {
        var item = new ExternalItem { Id = "x" };
        item.Properties["Title"] = "v";
        using (GraphPropertyRegistry.SuspendEnforcement())
        {
            var body = item.ToJson();
            Assert.Equal("v", body["properties"]!["Title"]!.GetValue<string>());
        }
    }
}
