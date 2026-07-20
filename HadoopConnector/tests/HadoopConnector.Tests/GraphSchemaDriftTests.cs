// GraphSchemaDriftTests
// ---------------------
// The produced-vs-declared cross-check: `validate-config --strict` must verify
// that config/graph-schema.json declares every Graph property the object list
// PRODUCES on external items. Before this check existed (probe B1, round 11):
//
//     graph-schema.json = [{"name":"Irrelevant","type":"String"}]
//     schema.json selectedFields {"Name":"Title","Comp":"TotallyUndeclaredProp"}
//     -> --strict GREEN, naming neither TotallyUndeclaredProp nor any
//        always-emitted property — and every item is rejected by Graph at push.
//
// What "produces" means is DERIVED from production symbols, not restated:
// ProducedGraphProperties executes ItemConverter.Convert (so drop/mask/_bdh_
// routing is the converter's own) and reads AlwaysEmittedProperties.Names —
// UNCONDITIONALLY, flag on or off, because gating the classifier pair on
// CLASSIFICATION would recreate the exact flag-flip asymmetry of mutant M8/M16:
// the flag is what operators flip last, so a green --strict run with the flag
// off must never turn red the day it is switched on.

using System.Text.Json.Nodes;
using HadoopConnector.Commands;
using HadoopConnector.Config;
using HadoopConnector.Item;

namespace HadoopConnector.Tests;

public class GraphSchemaDriftTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static EnvScope Env(string? classification = null) => new(
        ("CONNECTOR_ID", "BdhHadoopMart"),
        ("AAD_APP_TENANT_ID", "tenant"),
        ("AAD_APP_CLIENT_ID", "client"),
        ("SECRET_AAD_APP_CLIENT_SECRET", "secret"),
        ("HDFS_MODE", "webhdfs"),
        ("HDFS_NAMENODE_URL", "http://namenode.example:9870/webhdfs/v1"),
        ("BDH_EXPORT_PATH", null),
        ("BDH_FILTERS_PATH", null),
        ("ALLOW_FULL_SCAN", null),
        ("USE_KEY_VAULT", null),
        ("USE_SQL_SERVER", null),
        ("HA_MODE", null),
        ("LOG_FORMAT", null),
        ("CLASSIFICATION", classification),
        ("CLASSIFICATION_ENFORCE_ACL", null),
        (ShardingConfig.EnvVar, null));

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir.Path, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string SchemaPath => Write("schema.json", """
        {"objectList": [{
            "objectName": "Contact", "displayName": "Contact", "aclMode": "ownerOnly",
            "selectedFields": {"Name": "Title", "Description": "_bdh_Description"}
        }]}
        """);

    private string FiltersPath => Write("filters.json", """
        {"objects": {"Contact": {"partition": [{"key": "dt", "op": "withinLastDays", "value": "30"}]}},
         "fullScanAllowed": []}
        """);

    private string ClassificationPath => Write("classification-ok.json", """
        {"categories": [{"name": "PII", "patterns": [{"name": "email", "regex": "\\w+@\\w+"}]}]}
        """);

    /// <summary>graph-schema.json declaring exactly <paramref name="names"/>.</summary>
    private string GraphSchemaDeclaring(string file, params string[] names) =>
        Write(file, "[" + string.Join(",",
            names.Select(n => $$"""{"name": "{{n}}", "type": "String"}""")) + "]");

    private static string[] CompleteFor(params string[] mapped) =>
        AlwaysEmittedProperties.Names.Concat(mapped).ToArray();

    // ── the probe-B1 defect, now an ERROR ────────────────────────────────────

    /// <summary>A selectedFields-mapped Graph property missing from
    /// graph-schema.json fails preflight by NAME — Graph would reject every
    /// item for that object at push time, so the config cannot work. ERROR, not
    /// warning: no --strict flag combination may bless it.</summary>
    [Fact]
    public void UndeclaredMappedProperty_IsPreflightError()
    {
        using var env = Env();
        var schema = Write("schema-undeclared.json", """
            {"objectList": [{
                "objectName": "Contact", "displayName": "Contact", "aclMode": "ownerOnly",
                "selectedFields": {"Name": "Title", "Comp": "TotallyUndeclaredProp"}
            }]}
            """);
        var graphSchema = GraphSchemaDeclaring("gs-undeclared.json", CompleteFor("Title"));

        var result = ValidateConfig.ValidateCore(
            schema, graphSchema, FiltersPath, ClassificationPath, strict: true);

        var error = Assert.Single(result.Errors,
            e => e.Contains("TotallyUndeclaredProp", StringComparison.Ordinal));
        Assert.Contains("graph-schema.json", error, StringComparison.Ordinal);
        Assert.False(result.Ok(strict: true));
    }

    // ── the always-emitted set, per property, flag on AND off ────────────────

    public static TheoryData<string, string?> AlwaysEmittedTimesFlag()
    {
        var data = new TheoryData<string, string?>();
        foreach (var name in AlwaysEmittedProperties.Names)
        {
            data.Add(name, null);      // CLASSIFICATION unset
            data.Add(name, "true");    // CLASSIFICATION=true
        }
        return data;
    }

    /// <summary>
    /// Every always-emitted property — including the classifier pair
    /// SensitivityLabel and DetectedCategories — must be declared, and the
    /// verdict must NOT depend on CLASSIFICATION. This is where item 1 and
    /// item 2 of round 11 meet: gating the classifier pair on the flag would
    /// bless today a schema file that rejects every item the day the flag is
    /// flipped, with a green validate-config run in the change ticket.
    /// </summary>
    [Theory]
    [MemberData(nameof(AlwaysEmittedTimesFlag))]
    public void MissingAlwaysEmittedProperty_IsPreflightError_FlagOnOrOff(
        string missing, string? classificationFlag)
    {
        using var env = Env(classificationFlag);
        var declared = CompleteFor("Title")
            .Where(n => !string.Equals(n, missing, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var graphSchema = GraphSchemaDeclaring(
            $"gs-missing-{missing}-{classificationFlag ?? "off"}.json", declared);

        var result = ValidateConfig.ValidateCore(
            SchemaPath, graphSchema, FiltersPath, ClassificationPath, strict: true);

        Assert.Contains(result.Errors,
            e => e.Contains(missing, StringComparison.Ordinal)
                 && e.Contains("graph-schema.json", StringComparison.Ordinal));
        Assert.False(result.Ok(strict: true));
    }

    /// <summary>The complete declaration is green — the check is not a blanket
    /// refusal — and the verdict is the same with the flag on and off.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    public void CompleteDeclaration_PassesStrict_FlagOnOrOff(string? classificationFlag)
    {
        using var env = Env(classificationFlag);
        var graphSchema = GraphSchemaDeclaring(
            $"gs-complete-{classificationFlag ?? "off"}.json", CompleteFor("Title"));

        var result = ValidateConfig.ValidateCore(
            SchemaPath, graphSchema, FiltersPath, ClassificationPath, strict: true);

        Assert.True(result.Ok(strict: true),
            string.Join(" | ", result.Errors.Concat(result.Warnings)));
    }

    // ── the three emission rules, mirrored from production, per clause ───────

    /// <summary>A DROPPED column emits nothing, so its mapped property needs no
    /// declaration — requiring it would force operators to declare dead schema.</summary>
    [Fact]
    public void DroppedColumn_TargetNeedsNoDeclaration()
    {
        using var env = Env();
        var schema = Write("schema-drop.json", """
            {"objectList": [{
                "objectName": "Contact", "displayName": "Contact", "aclMode": "ownerOnly",
                "selectedFields": {"Name": "Title", "Comp": "CompProp"},
                "columnPolicies": {"Comp": "drop"}
            }]}
            """);
        var graphSchema = GraphSchemaDeclaring("gs-drop.json", CompleteFor("Title"));

        var result = ValidateConfig.ValidateCore(
            schema, graphSchema, FiltersPath, ClassificationPath, strict: true);

        Assert.DoesNotContain(result.Errors,
            e => e.Contains("CompProp", StringComparison.Ordinal));
        Assert.True(result.Ok(strict: true),
            string.Join(" | ", result.Errors.Concat(result.Warnings)));
    }

    /// <summary>A MASKED column still emits its property (carrying the marker),
    /// so the declaration is still required.</summary>
    [Fact]
    public void MaskedColumn_TargetStillRequiresDeclaration()
    {
        using var env = Env();
        var schema = Write("schema-mask.json", """
            {"objectList": [{
                "objectName": "Contact", "displayName": "Contact", "aclMode": "ownerOnly",
                "selectedFields": {"Name": "Title", "Comp": "CompProp"},
                "columnPolicies": {"Comp": "mask"}
            }]}
            """);
        var graphSchema = GraphSchemaDeclaring("gs-mask.json", CompleteFor("Title"));

        var result = ValidateConfig.ValidateCore(
            schema, graphSchema, FiltersPath, ClassificationPath, strict: true);

        Assert.Contains(result.Errors,
            e => e.Contains("CompProp", StringComparison.Ordinal));
        Assert.False(result.Ok(strict: true));
    }

    /// <summary>A _bdh_ placeholder routes the column into the content body
    /// under its own COLUMN name — it is not a Graph property and must not be
    /// demanded from (or counted against) graph-schema.json.</summary>
    [Fact]
    public void BdhPlaceholder_IsNotAGraphProperty()
    {
        using var env = Env();
        // SchemaPath maps Description -> _bdh_Description; declare only the rest.
        var graphSchema = GraphSchemaDeclaring("gs-bdh.json", CompleteFor("Title"));

        var result = ValidateConfig.ValidateCore(
            SchemaPath, graphSchema, FiltersPath, ClassificationPath, strict: true);

        Assert.True(result.Ok(strict: true),
            string.Join(" | ", result.Errors.Concat(result.Warnings)));
        Assert.DoesNotContain(
            result.Errors.Concat(result.Warnings).Concat(result.Notices),
            f => f.Contains("_bdh_", StringComparison.Ordinal)
                 || f.Contains("Description", StringComparison.Ordinal));
    }

    /// <summary>
    /// The inventory itself, pinned per rule against a config exercising all
    /// four routes at once: always-emitted + plain + masked appear; dropped,
    /// _bdh_ names and raw COLUMN names do not.
    /// </summary>
    [Fact]
    public void ProducedInventory_AppliesAllThreeRules()
    {
        var schema = SchemaConfig.Load(Write("schema-rules.json", """
            {"objectList": [{
                "objectName": "Contact", "displayName": "Contact", "aclMode": "ownerOnly",
                "selectedFields": {
                    "Name": "Title",
                    "Salary": "SalaryProp",
                    "Notes": "NotesProp",
                    "Description": "_bdh_Description"
                },
                "columnPolicies": {"Salary": "mask", "Notes": "drop"}
            }]}
            """));

        var produced = ProducedGraphProperties.Collect(schema)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var always in AlwaysEmittedProperties.Names)
            Assert.Contains(always, produced);
        Assert.Contains("Title", produced);        // plain mapping
        Assert.Contains("SalaryProp", produced);   // masked: still emitted
        Assert.DoesNotContain("NotesProp", produced);          // dropped: emits nothing
        Assert.DoesNotContain("_bdh_Description", produced);   // content-routed
        Assert.DoesNotContain("Description", produced);        // column name is not a property
        Assert.DoesNotContain("Salary", produced);
        Assert.DoesNotContain("Notes", produced);
        Assert.DoesNotContain("Name", produced);
    }

    // ── the reverse direction is informational, never gating ─────────────────

    /// <summary>Declared-but-unproduced is a NOTICE: dead schema is harmless
    /// (pre-declaring ahead of a config rollout is exactly the over-declaring
    /// this check encourages), but it is also the usual trace of a one-sided
    /// rename, so it must be visible.</summary>
    [Fact]
    public void DeclaredButUnproducedProperty_IsANoticeNotAFinding()
    {
        using var env = Env();
        var graphSchema = GraphSchemaDeclaring(
            "gs-extra.json", CompleteFor("Title", "PreDeclaredForRollout"));

        var result = ValidateConfig.ValidateCore(
            SchemaPath, graphSchema, FiltersPath, ClassificationPath, strict: true);

        Assert.True(result.Ok(strict: true),
            string.Join(" | ", result.Errors.Concat(result.Warnings)));
        Assert.Contains(result.Notices,
            n => n.Contains("PreDeclaredForRollout", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Errors.Concat(result.Warnings),
            f => f.Contains("PreDeclaredForRollout", StringComparison.Ordinal));
    }

    /// <summary>Graph property names are case-insensitive, so a declaration
    /// differing only in case is a declaration, not drift — in either direction.</summary>
    [Fact]
    public void CaseDifference_IsNotDrift()
    {
        using var env = Env();
        var graphSchema = GraphSchemaDeclaring("gs-case.json",
            CompleteFor("tItLe").Select(n => n.ToLowerInvariant()).ToArray());

        var result = ValidateConfig.ValidateCore(
            SchemaPath, graphSchema, FiltersPath, ClassificationPath, strict: true);

        Assert.True(result.Ok(strict: true),
            string.Join(" | ", result.Errors.Concat(result.Warnings)));
        Assert.Empty(result.Notices.Where(
            n => n.Contains("produces", StringComparison.Ordinal)));
    }

    // ── degenerate graph-schema.json files ───────────────────────────────────

    public static TheoryData<string, string> DegenerateGraphSchemas() => new()
    {
        { "empty-array", "[]" },
        { "bare-null", "null" },
        { "malformed", "[{\"name\": \"Title\"" },
        { "empty-name", """[{"name": "", "type": "String"}]""" },
        { "whitespace-name", """[{"name": "   ", "type": "String"}]""" },
        { "non-string-name", """[{"name": 123, "type": "String"}]""" },
        { "missing-type", """[{"name": "Title"}]""" },
        { "duplicate-names", """[{"name": "Title", "type": "String"}, {"name": "title", "type": "Int64"}]""" },
    };

    /// <summary>
    /// A degenerate graph-schema.json is ONE actionable preflight ERROR naming
    /// the file — never a green run, never a raw stack, and never double-reported
    /// by the drift check on top of the parse finding (the drift diff against a
    /// half-parsed declaration would be half-true, so it must not run at all).
    /// The Clarizen connector's blanket catch(Exception){return;} around its
    /// drift computation is the failure mode this test exists to keep out.
    /// </summary>
    [Theory]
    [MemberData(nameof(DegenerateGraphSchemas))]
    public void DegenerateGraphSchema_IsOneActionableError(string label, string json)
    {
        using var env = Env();
        var graphSchema = Write($"gs-degenerate-{label}.json", json);

        var result = ValidateConfig.ValidateCore(
            SchemaPath, graphSchema, FiltersPath, ClassificationPath, strict: true);

        Assert.False(result.Ok(strict: true), $"'{label}' passed --strict green.");
        Assert.Single(result.Errors,
            e => e.Contains("graph-schema.json", StringComparison.Ordinal));
        // The drift check stays silent when the declaration did not parse clean —
        // its absence must be BECAUSE the parse error is already recorded above.
        Assert.DoesNotContain(result.Errors,
            e => e.Contains("emits on external items", StringComparison.Ordinal));
    }

    /// <summary>The empty-array error must say what is missing, or the operator
    /// is left diffing files by hand.</summary>
    [Fact]
    public void EmptyGraphSchema_ErrorNamesTheAlwaysEmittedSet()
    {
        using var env = Env();
        var graphSchema = Write("gs-empty-actionable.json", "[]");

        var result = ValidateConfig.ValidateCore(
            SchemaPath, graphSchema, FiltersPath, ClassificationPath, strict: true);

        var error = Assert.Single(result.Errors,
            e => e.Contains("graph-schema.json", StringComparison.Ordinal));
        Assert.All(AlwaysEmittedProperties.Names,
            name => Assert.Contains(name, error, StringComparison.Ordinal));
    }

    /// <summary>When schema.json itself fails to load, the drift check cannot
    /// compute a produced set — it must skip WITHOUT hiding the schema error and
    /// without emitting a half-true drift finding.</summary>
    [Fact]
    public void SchemaLoadFailure_SkipsDriftCheck_WithoutSilence()
    {
        using var env = Env();
        var schema = Write("schema-broken.json", """{"objectList": [{"objectName": ""}]}""");
        var graphSchema = GraphSchemaDeclaring("gs-schema-broken.json", CompleteFor("Title"));

        var result = ValidateConfig.ValidateCore(
            schema, graphSchema, FiltersPath, ClassificationPath, strict: true);

        Assert.False(result.Ok(strict: true));
        Assert.Contains(result.Errors,
            e => e.Contains("schema.json invalid", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Errors,
            e => e.Contains("emits on external items", StringComparison.Ordinal));
    }

    /// <summary>
    /// A failure to COMPUTE the produced set is an ERROR, never silence. The
    /// Clarizen connector's drift check wrapped this in a blanket
    /// <c>catch {}</c>, and a degenerate file passed --strict green while the
    /// crawl threw — this test is what keeps that defect from being imported.
    /// (No config loadable through SchemaConfig.Load makes the production
    /// collector throw today, so the failure is injected.)
    /// </summary>
    [Fact]
    public void DriftComputationFailure_IsAnError_NeverSilence()
    {
        var result = new ValidateConfig.Result();
        var schema = SchemaConfig.Load(SchemaPath);
        var declared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Title"] = "String",
        };

        ValidateConfig.AddGraphSchemaDriftFindings(
            result, schema, declared,
            producedCollector: _ => throw new InvalidOperationException("synthetic failure"));

        var error = Assert.Single(result.Errors);
        Assert.Contains("cross-check could not run", error, StringComparison.Ordinal);
        Assert.Contains("synthetic failure", error, StringComparison.Ordinal);
        Assert.False(result.Ok(strict: false));
    }

    // ── the shipped config pair ──────────────────────────────────────────────

    private static string RepoConfig(string file) =>
        Path.Combine(AppContext.BaseDirectory, "config", file);

    /// <summary>
    /// The shipped schema.json / graph-schema.json pair has NO drift, in either
    /// direction, with the flag on and off: the produced set and the declared
    /// set are exactly equal, so the new check adds ZERO findings to a fresh
    /// clone. (The only shipped --strict failure remains Account's deliberately
    /// un-attested coarse ACL — see RepoSchema_ShipsNoPreCheckedAttestation.)
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    public void ShippedConfigPair_HasZeroDrift_FlagOnOrOff(string? classificationFlag)
    {
        var produced = ProducedGraphProperties
            .Collect(SchemaConfig.Load(RepoConfig("schema.json")))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var declared = JsonNode.Parse(File.ReadAllText(RepoConfig("graph-schema.json")))!
            .AsArray()
            .Select(p => p!["name"]!.GetValue<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(declared, produced);

        using var env = Env(classificationFlag);
        var result = ValidateConfig.ValidateCore(
            RepoConfig("schema.json"), RepoConfig("graph-schema.json"),
            RepoConfig("filters.json"), RepoConfig("classification.json"), strict: true);

        Assert.DoesNotContain(
            result.Errors.Concat(result.Warnings).Concat(result.Notices),
            f => f.Contains("emits on external items", StringComparison.Ordinal)
                 || f.Contains("produces", StringComparison.Ordinal));
    }
}
