// DuplicateGraphPropertyTests.cs
// ------------------------------
// Two selectedFields columns mapped to the SAME Graph property.
//
// Only one of them can occupy externalItem.properties[name]; the winner is
// whichever the dictionary enumerates last. The reported instance was a lost
// RESTRICTION rather than merely lost data:
//
//     selectedFields {"Id":"Id","Salary":"Comp","Bonus":"Comp"}
//     columnPolicies {"Salary":"mask"}
//   ⇒ preflight printed masked=[Salary]
//   ⇒ item carried properties {"Comp":"YYSENTINEL8"} — Bonus's real value, no
//     [RESTRICTED] marker anywhere.
//
// The masked value did not leak, but the report claimed a restriction the item
// did not deliver — the same family as the identity-column, unselected-column
// and colliding-property rejections already shipped.
//
// The fix is at the mapping, not at the policy: ANY two columns mapping to one
// property are rejected at load. That closes shapes nobody enumerated — the
// drop-loser, the three-way pile-up, the casing variant, the plain silent
// data-loss case with no policy at all — instead of patching mask-then-overwrite.
//
// The centrepiece here is a BRUTE-FORCE sweep that generates the mapping/policy
// space and asserts one invariant on every config that survives load: what the
// object REPORTS as restricted is what the converted item actually delivers.

using System.Text.Json;
using System.Text.Json.Nodes;
using HadoopConnector.Commands;
using HadoopConnector.Config;
using HadoopConnector.Graph;
using HadoopConnector.Hdfs;
using HadoopConnector.Item;

namespace HadoopConnector.Tests;

public class DuplicateGraphPropertyTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Write(string content)
    {
        var path = Path.Combine(_dir.Path, $"schema-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    // ── the reported instance ───────────────────────────────────────────────

    private const string ReportedConfig = """
        {"objectList":[{"objectName":"Contact","aclMode":"ownerOnly",
          "selectedFields":{"Id":"Id","Salary":"Comp","Bonus":"Comp"},
          "columnPolicies":{"Salary":"mask"}}]}
        """;

    [Fact]
    public void TheReportedMaskOverwrite_IsRejectedAtLoad_NamingBothColumns()
    {
        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(Write(ReportedConfig)));

        Assert.Contains("Salary", exc.Message, StringComparison.Ordinal);
        Assert.Contains("Bonus", exc.Message, StringComparison.Ordinal);
        Assert.Contains("Comp", exc.Message, StringComparison.Ordinal);
        Assert.Contains("Contact", exc.Message, StringComparison.Ordinal);
    }

    // The headline claim: preflight must never print masked=[Salary] for an item
    // that carries no marker. With the load rejection it is an ERROR instead.
    [Fact]
    public void ValidateConfig_NeverReportsAnOverwrittenColumnAsMasked()
    {
        using var scope = GoodEnv();
        var path = Write(ReportedConfig);

        var result = ValidateConfig.ValidateCore(path, GraphSchemaPath, FiltersPath, strict: true);

        Assert.False(result.Ok(strict: true));
        Assert.Contains(result.Errors, e => e.Contains("schema.json invalid", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Notices,
            n => n.Contains("masked=[Salary]", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Notices, n => n.Contains("1 masked", StringComparison.Ordinal));
    }

    // ── the shapes nobody named ─────────────────────────────────────────────

    [Theory]
    // plain silent data loss: no policy involved at all
    [InlineData("""{"Id":"RecordId","A__c":"Comp","B__c":"Comp"}""", "{}")]
    // the loser carries the drop — the drop is silently voided
    [InlineData("""{"Id":"RecordId","A__c":"Comp","B__c":"Comp"}""", """{"A__c":"drop"}""")]
    // both policed
    [InlineData("""{"Id":"RecordId","A__c":"Comp","B__c":"Comp"}""", """{"A__c":"mask","B__c":"drop"}""")]
    // casing: the collision must not be defeated by one letter
    [InlineData("""{"Id":"RecordId","A__c":"Comp","B__c":"comp"}""", """{"A__c":"mask"}""")]
    [InlineData("""{"Id":"RecordId","A__c":"COMP","B__c":"Comp"}""", "{}")]
    // incidental whitespace is the same Graph property, not a second one
    [InlineData("""{"Id":"RecordId","A__c":"Comp","B__c":" Comp "}""", "{}")]
    // three columns onto one property
    [InlineData("""{"Id":"RecordId","A__c":"Comp","B__c":"Comp","C__c":"Comp"}""", "{}")]
    // the identity property itself
    [InlineData("""{"Id":"RecordId","A__c":"RecordId"}""", "{}")]
    public void EveryDuplicateMappingShape_IsRejectedAtLoad(string selectedFields, string columnPolicies)
    {
        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(Write($$"""
            {"objectList":[{"objectName":"Contact","aclMode":"ownerOnly",
              "selectedFields":{{selectedFields}},"columnPolicies":{{columnPolicies}}}]}
            """)));

        Assert.Contains("same Graph property", exc.Message, StringComparison.Ordinal);
    }

    // ── what must KEEP loading ──────────────────────────────────────────────

    // _bdh_ values are not property names: they route the column into the content
    // body under its own COLUMN name, so two of them produce two distinct lines
    // and overwrite nothing. Rejecting these would be a false positive.
    [Fact]
    public void TwoColumnsSharingABdhPlaceholder_StillLoadAndBothReachTheContent()
    {
        var config = SchemaConfig.Load(Write("""
            {"objectList":[{"objectName":"Contact","displayName":"Contact","aclMode":"ownerOnly",
              "selectedFields":{"Id":"RecordId","Name":"Title",
                                "Notes__c":"_bdh_Text","Detail__c":"_bdh_Text"}}]}
            """));
        var obj = config.FindObject("Contact")!;

        var record = new BdhRecord("Contact", JsonNode.Parse(
            """{"Id":"a1","Name":"Ada","Notes__c":"FIRSTLINE","Detail__c":"SECONDLINE"}""")!.AsObject());
        var content = new ItemConverter(TestConfig.Make()).BuildContent(record, obj);

        Assert.Contains("FIRSTLINE", content, StringComparison.Ordinal);
        Assert.Contains("SECONDLINE", content, StringComparison.Ordinal);
    }

    // Distinct properties, including near-misses, must not be rejected.
    [Theory]
    [InlineData("""{"Id":"RecordId","A__c":"Comp","B__c":"Comp2"}""")]
    [InlineData("""{"Id":"RecordId","A__c":"Comp","B__c":"CompX"}""")]
    [InlineData("""{"Id":"RecordId","A__c":"Comp"}""")]
    public void DistinctMappings_StillLoad(string selectedFields)
    {
        var config = SchemaConfig.Load(Write($$"""
            {"objectList":[{"objectName":"Contact","aclMode":"ownerOnly",
              "selectedFields":{{selectedFields}}}]}
            """));

        Assert.NotNull(config.FindObject("Contact"));
    }

    // The shipped config must not be collateral damage.
    [Fact]
    public void TheShippedSchemaStillLoads()
    {
        var shipped = Path.Combine(RepoRoot(), "config", "schema.json");
        Assert.True(File.Exists(shipped), $"expected the shipped schema at {shipped}");

        var config = SchemaConfig.Load(shipped);

        Assert.NotEmpty(config.ObjectList);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HadoopConnector.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    // ── BRUTE FORCE: report accuracy over the generated mapping space ───────

    /// <summary>
    /// Generate the mapping/policy space and assert the invariant that the whole
    /// columnPolicies mechanism rests on, for EVERY config that survives load:
    /// what the object reports as dropped/masked is what the item delivers.
    /// <para>
    /// Nothing here compares production to a re-implementation of production. The
    /// oracle is the object's OWN report (DroppedColumns / MaskedColumns) checked
    /// against the REAL converter's output, with a unique sentinel per column so
    /// a value that survives is detectable wherever it lands. The reported defect
    /// is one point in this space; so are the shapes that were never named.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryGeneratedConfigThatLoads_DeliversExactlyTheRestrictionsItReports()
    {
        string[] propertyChoices = { "Comp", "comp", " Comp ", "Other", "_bdh_Text", "Title" };
        string[] policyChoices = { "none", "mask", "drop" };

        var loaded = 0;
        var rejected = 0;
        foreach (var propertyA in propertyChoices)
        foreach (var propertyB in propertyChoices)
        foreach (var policyA in policyChoices)
        foreach (var policyB in policyChoices)
        {
            var policies = new List<string>();
            if (policyA != "none")
                policies.Add($"\"A__c\":\"{policyA}\"");
            if (policyB != "none")
                policies.Add($"\"B__c\":\"{policyB}\"");

            var policyJson = "{" + string.Join(",", policies) + "}";
            var json = $$"""
                {"objectList":[{"objectName":"Contact","displayName":"Contact","aclMode":"ownerOnly",
                  "selectedFields":{"Id":"RecordId","A__c":"{{propertyA}}","B__c":"{{propertyB}}"},
                  "columnPolicies":{{policyJson}}}]}
                """;

            SchemaConfig? config = null;
            var exc = Record.Exception(() => config = SchemaConfig.Load(Write(json)));
            Assert.IsNotType<NullReferenceException>(exc);
            if (exc is not null)
            {
                Assert.IsType<InvalidDataException>(exc);
                rejected++;
                continue;
            }

            loaded++;
            AssertReportMatchesItem(config!.FindObject("Contact")!, json);
        }

        // Both halves of the space must be non-trivially populated, or the sweep
        // is asserting nothing: a fix that rejected everything would also pass.
        Assert.True(loaded > 40, $"only {loaded} configs loaded — the sweep proves little");
        Assert.True(rejected > 20, $"only {rejected} configs were rejected — collisions are not being caught");
    }

    private static void AssertReportMatchesItem(ObjectConfig obj, string json)
    {
        const string SentinelA = "AAASENTINEL111";
        const string SentinelB = "BBBSENTINEL222";
        var record = new BdhRecord("Contact", JsonNode.Parse(
            $$"""{"Id":"rec1","A__c":"{{SentinelA}}","B__c":"{{SentinelB}}"}""")!.AsObject());

        var item = new ItemConverter(TestConfig.Make()).Convert(record, obj, new List<AclEntry>());
        var properties = JsonSerializer.Serialize(item.Properties);
        var everything = properties + "\n" + item.Content;

        foreach (var (column, sentinel) in new[] { ("A__c", SentinelA), ("B__c", SentinelB) })
        {
            var mapping = obj.SelectedFields[column];
            switch (obj.PolicyFor(column))
            {
                case ColumnPolicyAction.Drop:
                    Assert.Contains(column, obj.DroppedColumns);
                    Assert.False(everything.Contains(sentinel, StringComparison.Ordinal),
                        $"{column} is reported DROPPED but its value is in the item.\n{json}\n{everything}");
                    break;

                case ColumnPolicyAction.Mask:
                    Assert.Contains(column, obj.MaskedColumns);
                    Assert.False(everything.Contains(sentinel, StringComparison.Ordinal),
                        $"{column} is reported MASKED but its value is in the item.\n{json}\n{everything}");
                    // …and the restriction must be VISIBLE where the column is
                    // emitted, which is the half the overwrite destroyed.
                    if (ItemConverter.RouteFor(mapping) == ItemConverter.FieldRoute.Property)
                    {
                        Assert.True(
                            item.Properties.TryGetValue(mapping, out var value)
                            && Equals(value, ColumnPolicy.MaskMarker),
                            $"{column} is reported MASKED but property '{mapping}' does not carry the "
                            + $"marker.\n{json}\n{properties}");
                    }
                    else
                    {
                        Assert.Contains($"{column}: {ColumnPolicy.MaskMarker}", item.Content,
                            StringComparison.Ordinal);
                    }
                    break;

                default:
                    // Unrestricted: the value must actually be emitted somewhere,
                    // or a config that loads is silently losing data.
                    Assert.True(everything.Contains(sentinel, StringComparison.Ordinal),
                        $"{column} is reported UNRESTRICTED but its value reaches nothing.\n{json}\n{everything}");
                    break;
            }
        }
    }

    // ── env helpers ─────────────────────────────────────────────────────────

    private EnvScope GoodEnv() => new(
        ("CONNECTOR_ID", "BdhHadoopMart"),
        ("AAD_APP_TENANT_ID", "tenant"),
        ("AAD_APP_CLIENT_ID", "client"),
        ("SECRET_AAD_APP_CLIENT_SECRET", "secret"),
        ("HDFS_MODE", "webhdfs"),
        ("HDFS_NAMENODE_URL", "http://namenode.example:9870/webhdfs/v1"),
        ("BDH_EXPORT_PATH", null),
        ("ALLOW_FULL_SCAN", null),
        ("USE_KEY_VAULT", null),
        ("USE_SQL_SERVER", null),
        ("HA_MODE", null),
        ("LOG_FORMAT", null),
        (ShardingConfig.EnvVar, null));

    private string GraphSchemaPath
    {
        get
        {
            var path = Path.Combine(_dir.Path, "graph-schema.json");
            // Mapped properties + the always-emitted set — required by the
            // produced-vs-declared cross-check for any green-path run.
            File.WriteAllText(path, """
                [{"name": "Id", "type": "String", "isSearchable": false},
                 {"name": "Comp", "type": "String", "isSearchable": false},
                 {"name": "RecordId", "type": "String", "isSearchable": false},
                 {"name": "ObjectName", "type": "String"},
                 {"name": "Url", "type": "String"},
                 {"name": "IconUrl", "type": "String"},
                 {"name": "SourceSystem", "type": "String"},
                 {"name": "DataAsOf", "type": "String"},
                 {"name": "SensitivityLabel", "type": "String"},
                 {"name": "DetectedCategories", "type": "StringCollection"}]
                """);
            return path;
        }
    }

    private string FiltersPath
    {
        get
        {
            var path = Path.Combine(_dir.Path, "filters.json");
            File.WriteAllText(path, """
                {"objects": {"Contact": {"partition": [{"key": "dt", "op": "withinLastDays", "value": "30"}]}},
                 "fullScanAllowed": []}
                """);
            return path;
        }
    }
}
