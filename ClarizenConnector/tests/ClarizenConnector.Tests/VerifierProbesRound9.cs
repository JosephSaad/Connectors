// Verifier probes, round 9 — two defects, one root cause: the connector's
// answer to "is this Graph property declared?" was being decided by things that
// have to be MAINTAINED (an inventory of stamper call sites; an assumption that
// someone else already reported a bad file) instead of by the write path itself.
//
// FIX 1 — AN UNDECLARED STAMP WAS DEMOTED TO A BAD SOURCE ROW.
//   GraphPropertyBag already throws UndeclaredGraphPropertyException at the line
//   that stamps an undeclared name, from ANY call site. But the crawl's
//   per-record and per-object isolation catches treated that throw like a
//   poisoned row: dead-letter it and continue. So a stamp added anywhere outside
//   StampedPropertyInventory's five hard-coded call sites left the build-time
//   drift test AND the validate-config preflight green while the crawl
//   dead-lettered 100% of records and closed "successfully" with an empty index.
//   Observed before the fix, with `item.Properties["CrawlBatchIdProbeB"] = ...`
//   added to the per-record crawl loop:
//       crawl -> ingested=0 failed=1 puts=0      (drift probes: 5/5 green)
//   A1/A2 pin the new behaviour: the crawl ABORTS, naming the property, and
//   nothing is PUT. A2 does it once per property the crawl actually stamps, so
//   the guarantee is tied to no particular call site.
//
// FIX 2 — THE PREFLIGHT SELF-DISABLED ON A DEGENERATE graph-schema.json.
//   AddSchemaDriftFindings wrapped both file loads in `catch { return; }` on the
//   claim that a load failure "was already reported above". For a graph-schema
//   of `[]` (or entries with an empty name) it was NOT: the array/field checks
//   accept it, so validate-config --strict reported zero errors and zero
//   warnings for a file that makes the first stamp of the crawl throw.
//   Observed before the fix:
//       ValidateCore(schema.json, "[]") -> errors=[] warnings=[] okStrict=True
//       runtime, same file, item.Properties["Title"]="v"
//         -> InvalidDataException: declares no property names
//   B1/B2 pin the two degenerate shapes; B3/B4 pin that the "already reported"
//   claim is now CHECKED, not assumed, and does not double-report.

using System.Text.Json.Nodes;
using ClarizenConnector.Commands;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;

namespace ClarizenConnector.Tests;

/// <summary>
/// Points GraphPropertyRegistry at the real deployed declaration MINUS the named
/// properties. Models the operator who edited config/graph-schema.json and
/// dropped a name the code still stamps — the exact drift the guard exists for.
/// </summary>
internal sealed class ReducedGraphSchemaScope : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string? _previous;

    public ReducedGraphSchemaScope(params string[] removePropertyNames)
    {
        _previous = GraphPropertyRegistry.OverridePath;

        var baseline = JsonNode
            .Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config", "graph-schema.json")))!
            .AsArray();
        var kept = new JsonArray();
        foreach (var property in baseline.ToList())
        {
            baseline.Remove(property);
            var name = property!["name"]?.GetValue<string>();
            if (name is not null && removePropertyNames.Contains(name, StringComparer.Ordinal))
                continue;
            kept.Add(property);
        }

        var path = Path.Combine(_dir.Path, "graph-schema.json");
        File.WriteAllText(path, kept.ToJsonString());
        GraphPropertyRegistry.OverridePath = path;
    }

    public void Dispose()
    {
        GraphPropertyRegistry.OverridePath = _previous;
        _dir.Dispose();
    }
}

public class VerifierRound9WritePathEnforcementProbes
{
    // ── FIX 1: the write path, not an inventory, is the enforcement ───────────

    /// <summary>The property names a real crawl actually puts on the wire.</summary>
    private static async Task<List<string>> StampedByARealCrawlAsync()
    {
        using var fixture = new GraphIngestTests.Fixture(recordCount: 1);
        await fixture.Pipeline().RunAsync(fullCrawl: true);
        var put = fixture.Graph.Sent.Single(s => s.Method == HttpMethod.Put);
        return put.Body!["properties"]!.AsObject().Select(p => p.Key).ToList();
    }

    // A1: the headline behaviour. An undeclared stamp aborts the crawl, loudly,
    // naming the property — instead of being dead-lettered as if the SOURCE row
    // were at fault. Before the fix this returned a summary (ingested=0,
    // failed=1) and the command exited as a completed crawl.
    [Fact]
    public async Task A1_UndeclaredStamp_AbortsTheCrawl_AndPutsNothing()
    {
        using var scope = new ReducedGraphSchemaScope("Title");
        using var fixture = new GraphIngestTests.Fixture(recordCount: 3, chunkSize: 2);

        var exc = await Assert.ThrowsAsync<UndeclaredGraphPropertyException>(
            () => fixture.Pipeline().RunAsync(fullCrawl: true));

        Assert.Equal("Title", exc.PropertyName);
        Assert.Contains("Title", exc.Message, StringComparison.Ordinal);
        // Nothing reached Graph: no PUT, no $batch. The failure is loud, and the
        // property was NOT silently dropped from an item that shipped anyway.
        Assert.DoesNotContain(fixture.Graph.Sent, s => s.Method == HttpMethod.Put);
        Assert.DoesNotContain(fixture.Graph.Sent, s => s.Path == "$batch");
    }

    // A2: PER STAMP SITE, not collectively. Whatever the crawl stamps — from the
    // converter, the classifiers, the gate, the enricher, or a line someone adds
    // to Ingest.cs tomorrow — removing that one name from the declaration must
    // abort the crawl. Nothing here names a call site, so the guarantee cannot
    // be satisfied by a curated list of stampers.
    [Fact]
    public async Task A2_EveryPropertyTheCrawlStamps_AbortsTheCrawlWhenUndeclared()
    {
        var stamped = await StampedByARealCrawlAsync();
        Assert.NotEmpty(stamped);

        foreach (var name in stamped)
        {
            using var scope = new ReducedGraphSchemaScope(name);
            using var fixture = new GraphIngestTests.Fixture(recordCount: 1);

            var exc = await Assert.ThrowsAsync<UndeclaredGraphPropertyException>(
                () => fixture.Pipeline().RunAsync(fullCrawl: true));
            Assert.Equal(name, exc.PropertyName);
            Assert.DoesNotContain(fixture.Graph.Sent, s => s.Method == HttpMethod.Put);
        }
    }

    // NOTE: the escalation must NOT widen into "any transform error aborts the
    // crawl". Ordinary per-row and per-object isolation is covered by
    // DiagnosabilityTests.TransformFailure_DeadLetterCarriesChunkIndexAndExceptionType
    // and .TdwUnparseableFile_DeadLetterRecord_NamesExportFileAndExceptionType,
    // which stay green; no duplicate is added here.

    // ── FIX 2: a degenerate declaration is an ERROR at preflight ──────────────

    private static string RealSchemaJson =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config", "schema.json"));

    private static ValidateConfig.Result ValidateWithGraphSchema(string graphSchemaBody)
    {
        using var env = new EnvScope();
        foreach (var required in ValidateConfig.RequiredEnvVars)
            env.Set(required, "x");
        env.Set("CONNECTOR_ID", "ProbeConnector");

        using var dir = new TempDir();
        var schemaFile = Path.Combine(dir.Path, "schema.json");
        File.WriteAllText(schemaFile, RealSchemaJson);
        var graphFile = Path.Combine(dir.Path, "graph-schema.json");
        File.WriteAllText(graphFile, graphSchemaBody);
        return ValidateConfig.ValidateCore(schemaFile, graphFile);
    }

    // B1: the reported shape — an empty array. Before the fix: errors=[],
    // warnings=[], Ok(strict)=true, while the crawl dead-lettered everything.
    [Fact]
    public void B1_EmptyGraphSchemaArray_IsAPreflightError()
    {
        var result = ValidateWithGraphSchema("[]");

        Assert.Contains(result.Errors, e =>
            e.StartsWith(ValidateConfig.GraphSchemaJsonErrorPrefix, StringComparison.Ordinal)
            && e.Contains("unusable", StringComparison.Ordinal));
        Assert.False(result.Ok(strict: false));
        Assert.False(result.Ok(strict: true));
    }

    // B2: the second degenerate shape, covered separately — a non-empty array
    // whose entries carry an empty name. Structurally it passes every earlier
    // check (it HAS 'name' and 'type'), so only the drift path can catch it.
    [Fact]
    public void B2_GraphSchemaWithOnlyEmptyNames_IsAPreflightError()
    {
        var result = ValidateWithGraphSchema("""[{"name": "", "type": "String"}]""");

        Assert.Contains(result.Errors, e =>
            e.StartsWith(ValidateConfig.GraphSchemaJsonErrorPrefix, StringComparison.Ordinal)
            && e.Contains("unusable", StringComparison.Ordinal));
        Assert.False(result.Ok(strict: false));
    }

    // B3: the "it was already reported above" claim is now CHECKED. For a file
    // that genuinely was reported above (unparseable JSON), the drift path stays
    // quiet — exactly one finding, not two.
    [Fact]
    public void B3_UnparseableGraphSchema_IsReportedExactlyOnce()
    {
        var result = ValidateWithGraphSchema("{ this is not json");

        var graphErrors = result.Errors
            .Where(e => e.StartsWith(ValidateConfig.GraphSchemaJsonErrorPrefix, StringComparison.Ordinal))
            .ToList();
        Assert.Single(graphErrors);
        Assert.Contains("invalid", graphErrors[0], StringComparison.Ordinal);
    }

    // B4: a non-array graph-schema is likewise reported once — the earlier check
    // fires, the drift path recognises that and adds nothing.
    [Fact]
    public void B4_NonArrayGraphSchema_IsReportedExactlyOnce()
    {
        var result = ValidateWithGraphSchema("""{"name": "Title"}""");

        var graphErrors = result.Errors
            .Where(e => e.StartsWith(ValidateConfig.GraphSchemaJsonErrorPrefix, StringComparison.Ordinal))
            .ToList();
        Assert.Single(graphErrors);
        Assert.Contains("must be a JSON array", graphErrors[0], StringComparison.Ordinal);
    }

    // B5: the healthy case still passes — the fix must not have made a good
    // config fail. (Guards against "make everything an error" as a cheap pass.)
    [Fact]
    public void B5_TheDeployedGraphSchema_ProducesNoDriftError()
    {
        var body = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "config", "graph-schema.json"));
        var result = ValidateWithGraphSchema(body);

        Assert.DoesNotContain(result.Errors, e =>
            e.StartsWith(ValidateConfig.GraphSchemaJsonErrorPrefix, StringComparison.Ordinal));
    }

    // B6: an unloadable schema.json is reported once, by the earlier block; the
    // drift path proves that rather than assuming it.
    [Fact]
    public void B6_UnloadableSchemaJson_IsReportedExactlyOnce()
    {
        using var env = new EnvScope();
        foreach (var required in ValidateConfig.RequiredEnvVars)
            env.Set(required, "x");
        env.Set("CONNECTOR_ID", "ProbeConnector");

        using var dir = new TempDir();
        var schemaFile = Path.Combine(dir.Path, "schema.json");
        File.WriteAllText(schemaFile, "{ not json");
        var graphFile = Path.Combine(dir.Path, "graph-schema.json");
        File.WriteAllText(
            graphFile,
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config", "graph-schema.json")));

        var result = ValidateConfig.ValidateCore(schemaFile, graphFile);

        var schemaErrors = result.Errors
            .Where(e => e.StartsWith(ValidateConfig.SchemaJsonErrorPrefix, StringComparison.Ordinal))
            .ToList();
        Assert.Single(schemaErrors);
        Assert.DoesNotContain(result.Errors, e =>
            e.Contains("Graph schema drift could not be checked", StringComparison.Ordinal));
    }
}
