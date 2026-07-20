// ConfigPreflightClosureTests
// --------------------------
// Two closures, both of the same shape: a config that passed
// `validate-config --strict` green and then did something the preflight report
// said it would not.
//
//   1. classification.json — the fourth config file, never validated by
//      preflight. A null/wrong-typed value there gave GREEN, then killed the
//      crawl on a raw System.Text.Json stack naming neither the file nor the key.
//      The invariant asserted here is stated as a test, not as prose:
//      preflight-green ⇒ the crawl-time loader does not throw.
//
//   2. The Graph properties this connector emits on every item outside
//      selectedFields — AlwaysEmittedProperties.Names. A columnPolicies entry
//      named after one reported a restriction it did not deliver; a
//      selectedFields entry mapped ONTO one destroyed it. Both directions, per
//      always-emitted property.
//
//      The set is contributed by TWO emitters, not one: ItemConverter.Convert
//      writes five, and SensitivityClassifier.Classify — which runs after it —
//      writes two more. The check originally read only ItemConverter's list, so
//      the classifier's two were accepted by preflight and then overwritten at
//      crawl time. The closure tests below execute BOTH emitters.

using System.Text.Json.Nodes;
using HadoopConnector.Commands;
using HadoopConnector.Config;
using HadoopConnector.Content;
using HadoopConnector.Graph;
using HadoopConnector.Hdfs;
using HadoopConnector.Item;

namespace HadoopConnector.Tests;

public class ConfigPreflightClosureTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static EnvScope ClassificationEnv(string? classification = "true") => new(
        ("CONNECTOR_ID", "BdhHadoopMart"),
        ("AAD_APP_TENANT_ID", "tenant"),
        ("AAD_APP_CLIENT_ID", "client"),
        ("SECRET_AAD_APP_CLIENT_SECRET", "secret"),
        ("HDFS_MODE", "webhdfs"),
        ("HDFS_NAMENODE_URL", "http://namenode.example:9870/webhdfs/v1"),
        ("BDH_EXPORT_PATH", null),
        ("BDH_FILTERS_PATH", null),
        ("BDH_MAX_RECORDS_PER_OBJECT", null),
        ("BDH_LAG_HOURS", null),
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

    private string GraphSchemaPath => Write("graph-schema.json", """
        [{"name": "Title", "type": "String", "isSearchable": true}]
        """);

    private string FiltersPath => Write("filters.json", """
        {"objects": {"Contact": {"partition": [{"key": "dt", "op": "withinLastDays", "value": "30"}]}},
         "fullScanAllowed": []}
        """);

    // ── 1. classification.json is in the preflight-validated set ─────────────

    /// <summary>Every classification.json shape that used to reach the crawl. The
    /// bare string is the file content; <c>null</c> means the file is ABSENT.</summary>
    public static TheoryData<string, string?> ClassificationShapes() => new()
    {
        { "categories-null",        """{"categories": null}""" },
        { "categories-object",      """{"categories": {}}""" },
        { "categories-array-null",  """{"categories": [null]}""" },
        { "categories-string",      """{"categories": "PII"}""" },
        { "root-null",              "null" },
        { "patterns-null",          """{"categories": [{"name": "PII", "patterns": null}]}""" },
        { "categories-of-strings",  """{"categories": ["PII"]}""" },
        { "patterns-object",        """{"categories": [{"name": "PII", "patterns": {}}]}""" },
        { "patterns-array-null",    """{"categories": [{"name": "PII", "patterns": [null]}]}""" },
        { "regex-non-string",       """{"categories": [{"name": "PII", "patterns": [{"regex": 7}]}]}""" },
        { "name-non-string",        """{"categories": [{"name": 7, "patterns": [{"regex": "a"}]}]}""" },
        { "malformed-json",         """{"categories": [""" },
        { "file-absent",            null },
    };

    /// <summary>
    /// THE INVARIANT, executed rather than asserted in prose: for every shape, if
    /// <c>validate-config --strict</c> comes back green then the loader the CRAWL
    /// calls must not throw. A regression in either direction — preflight going
    /// green on a shape that still kills the crawl, or the loader throwing on a
    /// shape preflight blessed — fails this test.
    /// </summary>
    [Theory]
    [MemberData(nameof(ClassificationShapes))]
    public void PreflightGreen_ImpliesClassifierLoads(string label, string? content)
    {
        using var env = ClassificationEnv();
        var path = Path.Combine(_dir.Path, $"classification-{label}.json");
        if (content is not null)
            File.WriteAllText(path, content);

        var result = ValidateConfig.ValidateCore(
            SchemaPath, GraphSchemaPath, FiltersPath, path, strict: true);

        if (!result.Ok(strict: true))
            return;     // preflight caught it — nothing to guarantee about the crawl

        var exception = Record.Exception(() => ContentClassifier.Load(path));
        Assert.True(
            exception is null,
            $"'{label}' passed validate-config --strict GREEN, then the crawl-time loader threw "
            + $"{exception?.GetType().FullName}: {exception?.Message}");
    }

    /// <summary>
    /// The complement: every one of these shapes must actually be REPORTED, and
    /// the report must name classification — the original harm was a stack that
    /// named neither the file nor the key, so a finding that does not say
    /// 'classification' is no better than the crash.
    /// </summary>
    [Theory]
    [MemberData(nameof(ClassificationShapes))]
    public void BrokenClassificationConfig_IsReportedByName(string label, string? content)
    {
        using var env = ClassificationEnv();
        var path = Path.Combine(_dir.Path, $"reported-{label}.json");
        if (content is not null)
            File.WriteAllText(path, content);

        var result = ValidateConfig.ValidateCore(
            SchemaPath, GraphSchemaPath, FiltersPath, path, strict: true);

        Assert.False(result.Ok(strict: true), $"'{label}' passed --strict green.");
        var findings = result.Errors.Concat(result.Warnings).ToList();
        Assert.Contains(
            findings,
            message => message.Contains("classification", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A valid classification.json is not a finding — the rejections
    /// above must not be a blanket refusal.</summary>
    [Fact]
    public void ValidClassificationConfig_IsGreen()
    {
        using var env = ClassificationEnv();
        var path = Write("classification-ok.json", """
            {"categories": [{"name": "PII", "patterns": [{"name": "email", "regex": "\\w+@\\w+"}]}]}
            """);

        var result = ValidateConfig.ValidateCore(
            SchemaPath, GraphSchemaPath, FiltersPath, path, strict: true);

        Assert.True(result.Ok(strict: true), string.Join(" | ", result.Errors.Concat(result.Warnings)));
        Assert.Single(ContentClassifier.Load(path).Categories);
    }

    /// <summary>classification.json is only read when CLASSIFICATION=true, so an
    /// ABSENT file with the feature off is not an error. (A file that is present
    /// but broken is still reported — it is broken either way.)</summary>
    [Fact]
    public void AbsentClassificationConfig_IsNotAnError_WhenClassificationOff()
    {
        using var env = ClassificationEnv(classification: null);
        var absent = Path.Combine(_dir.Path, "no-such-classification.json");

        var result = ValidateConfig.ValidateCore(
            SchemaPath, GraphSchemaPath, FiltersPath, absent, strict: true);

        Assert.True(result.Ok(strict: true), string.Join(" | ", result.Errors.Concat(result.Warnings)));
    }

    /// <summary>
    /// classification.json is validated UNCONDITIONALLY — not only when
    /// CLASSIFICATION=true. That decision was undecided by any test: every other
    /// classification-preflight case here sets the flag on, so wrapping the load
    /// in `if (classificationEnabled)` (mutant M8) left the suite green.
    /// <para>
    /// It matters because the flag is the thing operators flip LAST. A broken
    /// classification.json that preflight blessed while the feature was off
    /// becomes a crawl that dies at startup the moment someone turns
    /// classification on — with a green validate-config run in the change ticket
    /// saying the config was fine. A file that is present is validated whether or
    /// not it is currently read. (An ABSENT file with the flag off stays a
    /// non-event — see AbsentClassificationConfig_IsNotAnError_WhenClassificationOff.)
    /// </para>
    /// </summary>
    [Fact]
    public void BrokenClassificationConfig_IsReported_EvenWhenClassificationOff()
    {
        using var env = ClassificationEnv(classification: null);
        var path = Write("classification-off-broken.json", """{"categories": {"nope": true}}""");

        var result = ValidateConfig.ValidateCore(
            SchemaPath, GraphSchemaPath, FiltersPath, path, strict: true);

        Assert.False(
            result.Ok(strict: true),
            "a broken classification.json passed --strict green with CLASSIFICATION unset");
        Assert.Contains(
            result.Errors,
            e => e.Contains("classification.json invalid", StringComparison.Ordinal));
    }

    /// <summary>The null-is-empty semantics do not become a SILENT detection gap:
    /// a config that loads with zero usable categories while CLASSIFICATION=true
    /// classifies nothing, and preflight says so.</summary>
    [Fact]
    public void EmptyCategorySet_WithClassificationOn_IsReported()
    {
        using var env = ClassificationEnv();
        var path = Write("classification-empty.json", """{"categories": []}""");

        var result = ValidateConfig.ValidateCore(
            SchemaPath, GraphSchemaPath, FiltersPath, path, strict: true);

        Assert.False(result.Ok(strict: true));
        Assert.Contains(result.Warnings, w => w.Contains("NO usable categories", StringComparison.Ordinal));
        // and it really does load as empty rather than throwing
        Assert.Empty(ContentClassifier.Load(path).Categories);
    }

    /// <summary>
    /// The LOADER's own contract, independent of preflight (preflight catches any
    /// exception, so these two properties are invisible to the tests above).
    /// <para>
    /// (i) null-is-empty: `"categories": null` LOADS, as an empty set — the same
    /// semantics schema.json has (docs/CONFIG_NULL_SEMANTICS.md) rather than a
    /// second, contradictory rule for the fourth config file.
    /// </para></summary>
    [Fact]
    public void ClassifierLoader_ReadsNullCategoriesAsEmpty()
    {
        var classifier = ContentClassifier.FromJson("""{"categories": null}""", "classification.json");
        Assert.Empty(classifier.Categories);
    }

    /// <summary>
    /// (ii) The original harm signature was a stack naming NEITHER the file nor
    /// the key. Every rejection this loader makes must name both — the file it
    /// was given, and the JSON path of the offending value.
    /// </summary>
    [Theory]
    [InlineData("""{"categories": {}}""", "$.categories")]
    [InlineData("""{"categories": [null]}""", "$.categories[0]")]
    [InlineData("""{"categories": ["PII"]}""", "$.categories[0]")]
    [InlineData("""{"categories": [{"name": "PII", "patterns": {}}]}""", "$.categories[0].patterns")]
    [InlineData("""{"categories": [{"name": "PII", "patterns": [null]}]}""", "$.categories[0].patterns[0]")]
    [InlineData("""{"categories": [{"name": "PII", "patterns": [{"regex": 7}]}]}""", "$.categories[0].patterns[0].regex")]
    [InlineData("""{"categories": [{"name": 7}]}""", "$.categories[0].name")]
    [InlineData("null", "$")]
    public void ClassifierLoader_RejectionNamesFileAndJsonPath(string json, string jsonPath)
    {
        var exception = Assert.Throws<InvalidDataException>(
            () => ContentClassifier.FromJson(json, "/etc/bdh/classification.json"));

        Assert.Contains("/etc/bdh/classification.json", exception.Message, StringComparison.Ordinal);
        Assert.Contains(jsonPath, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A missing FILE is reported by the loader as an InvalidDataException
    /// naming the path too, not as a bare FileNotFoundException.</summary>
    [Fact]
    public void ClassifierLoader_MissingFile_NamesThePath()
    {
        var absent = Path.Combine(_dir.Path, "definitely-not-here.json");
        var exception = Assert.Throws<InvalidDataException>(() => ContentClassifier.Load(absent));
        Assert.Contains(absent, exception.Message, StringComparison.Ordinal);
    }

    // ── 2. the always-emitted Graph properties ───────────────────────────────

    /// <summary>
    /// The list the two rejections below are sourced from must actually BE the
    /// set of properties Convert emits by itself. Asserted behaviourally: convert
    /// a record with IconUrl and DataAsOf populated, subtract the property names
    /// that came from selectedFields, and compare what is left. A standard
    /// property added to Convert without being added to StandardPropertyNames
    /// fails here — this is what stops the check drifting from the emission.
    /// </summary>
    [Fact]
    public void StandardPropertyNames_MatchesWhatConvertEmits()
    {
        var objectConfig = new ObjectConfig
        {
            ObjectName = "Contact",
            DisplayName = "Contact",
            SelectedFields = new Dictionary<string, string> { ["Email"] = "Email" },
            IconUrl = "https://icons.example/contact.png",
        };
        var record = new BdhRecord(
            "Contact",
            new JsonObject { ["Id"] = "REC001", ["Email"] = "e@x" },
            "2026-01-01",
            "Contact/dt=2026-01-01/part-000.jsonl");

        var item = new ItemConverter(TestConfig.Make(), "https://login.salesforce.com")
            .Convert(record, objectConfig, new List<AclEntry>());

        var emittedByItself = item.Properties.Keys
            .Except(objectConfig.SelectedFields.Values, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            ItemConverter.StandardPropertyNames.ToHashSet(StringComparer.OrdinalIgnoreCase),
            emittedByItself);
    }

    /// <summary>
    /// The SECOND emitter's closure, asserted the same way and for the same
    /// reason: SensitivityClassifier.Classify runs after Convert and stamps
    /// properties of its own. Executed end-to-end — convert a record, snapshot
    /// the keys, classify, and compare what APPEARED against the classifier's
    /// contributed list. A property the classifier starts writing without adding
    /// it to AlwaysEmittedPropertyNames fails here.
    /// </summary>
    [Fact]
    public void ClassifierAlwaysEmittedNames_MatchesWhatClassifyEmits()
    {
        var objectConfig = new ObjectConfig
        {
            ObjectName = "Contact",
            DisplayName = "Contact",
            SelectedFields = new Dictionary<string, string> { ["Email"] = "Email" },
        };
        var record = new BdhRecord(
            "Contact",
            new JsonObject { ["Id"] = "REC001", ["Email"] = "e@x" },
            "2026-01-01",
            "Contact/dt=2026-01-01/part-000.jsonl");
        var item = new ItemConverter(TestConfig.Make(), "https://login.salesforce.com")
            .Convert(record, objectConfig, new List<AclEntry>());
        var beforeClassify = item.Properties.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        new SensitivityClassifier(ContentClassifier.Load(
                Path.Combine(AppContext.BaseDirectory, "config", "classification.json")))
            .Classify(item, objectConfig);

        var addedByClassify = item.Properties.Keys
            .Where(name => !beforeClassify.Contains(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            SensitivityClassifier.AlwaysEmittedPropertyNames.ToHashSet(StringComparer.OrdinalIgnoreCase),
            addedByClassify);
    }

    /// <summary>
    /// The AGGREGATE the collision check actually reads must be the union of the
    /// per-emitter lists and nothing else. This is what stops the check being
    /// re-narrowed to one emitter (the original defect) or drifting into a
    /// hand-maintained copy: the two closure tests above pin each emitter's list
    /// to what it emits, and this pins Names to those lists.
    /// </summary>
    [Fact]
    public void AlwaysEmittedNames_IsExactlyTheUnionOfTheEmitterLists()
    {
        Assert.Equal(
            ItemConverter.StandardPropertyNames
                .Concat(SensitivityClassifier.AlwaysEmittedPropertyNames)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            AlwaysEmittedProperties.Names.ToHashSet(StringComparer.OrdinalIgnoreCase));

        // No emitter's list may be missing from the registry, and no name may be
        // contributed twice (a duplicate would mean two emitters fighting over
        // one property, which is the very failure this set exists to prevent).
        Assert.Equal(
            AlwaysEmittedProperties.Names.Count,
            AlwaysEmittedProperties.Names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(ItemConverter.StandardPropertyNames, AlwaysEmittedProperties.Contributors);
        Assert.Contains(SensitivityClassifier.AlwaysEmittedPropertyNames, AlwaysEmittedProperties.Contributors);
    }

    public static TheoryData<string> StandardProperties()
    {
        var data = new TheoryData<string>();
        foreach (var name in AlwaysEmittedProperties.Names)
            data.Add(name);
        return data;
    }

    /// <summary>
    /// DIRECTION (a), one case PER standard property: a columnPolicies entry
    /// named after a standard property made preflight print a restriction the
    /// item did not deliver (dropped=[Url] over a populated, indexed Url).
    /// </summary>
    [Theory]
    [MemberData(nameof(StandardProperties))]
    public void ColumnPolicyNamedAfterStandardProperty_IsRejected(string standard)
    {
        var path = Write($"policy-{standard}.json", $$"""
            {"objectList": [{
                "objectName": "Contact", "displayName": "Contact", "aclMode": "ownerOnly",
                "selectedFields": {"{{standard}}": "{{standard}}Col", "Email": "Email"},
                "columnPolicies": {"{{standard}}": "drop"}
            }]}
            """);

        var exception = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));
        Assert.Contains(standard, exception.Message, StringComparison.Ordinal);
        Assert.Contains("EVERY item", exception.Message, StringComparison.Ordinal);
        // The message must enumerate the COMPLETE set, not one emitter's share of
        // it, and must say when the classifier's two are written — an operator
        // shown a short list concludes the name they used is safe.
        Assert.All(
            AlwaysEmittedProperties.Names,
            name => Assert.Contains(name, exception.Message, StringComparison.Ordinal));
        Assert.Contains("CLASSIFICATION=true", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// DIRECTION (b), one case PER standard property: a selectedFields column
    /// mapped ONTO a standard property overwrote it on every record — the Url
    /// case silently deleted the deep link from every item in the index.
    /// </summary>
    [Theory]
    [MemberData(nameof(StandardProperties))]
    public void SelectedFieldMappedOntoStandardProperty_IsRejected(string standard)
    {
        var path = Write($"mapped-{standard}.json", $$"""
            {"objectList": [{
                "objectName": "Contact", "displayName": "Contact", "aclMode": "ownerOnly",
                "selectedFields": {"Comp": "{{standard}}", "Email": "Email"}
            }]}
            """);

        var exception = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));
        Assert.Contains(standard, exception.Message, StringComparison.Ordinal);
        Assert.Contains("EVERY item", exception.Message, StringComparison.Ordinal);
        // The message must enumerate the COMPLETE set, not one emitter's share of
        // it, and must say when the classifier's two are written — an operator
        // shown a short list concludes the name they used is safe.
        Assert.All(
            AlwaysEmittedProperties.Names,
            name => Assert.Contains(name, exception.Message, StringComparison.Ordinal));
        Assert.Contains("CLASSIFICATION=true", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Both rejections are case-insensitive — Graph property names are,
    /// so a rejection one letter of casing could evade is not a rejection.</summary>
    [Theory]
    [InlineData("lower-value", """{"objectList":[{"objectName":"C","displayName":"C","aclMode":"ownerOnly","selectedFields":{"Comp":"uRl","Email":"Email"}}]}""")]
    [InlineData("lower-policy", """{"objectList":[{"objectName":"C","displayName":"C","aclMode":"ownerOnly","selectedFields":{"uRl":"UrlCol","Email":"Email"},"columnPolicies":{"uRl":"mask"}}]}""")]
    [InlineData("padded-value", """{"objectList":[{"objectName":"C","displayName":"C","aclMode":"ownerOnly","selectedFields":{"Comp":"  Url  ","Email":"Email"}}]}""")]
    public void StandardPropertyCollision_IsCaseInsensitiveAndTrimmed(string label, string json)
    {
        var path = Write($"case-{label}.json", json);
        Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));
    }

    /// <summary>
    /// The Trim() clause of the always-emitted lookup, covered ON ITS OWN.
    /// <para>
    /// The padded case in the theory above cannot reach it: ValidateSelectedFields
    /// trims the property before the lookup, so the lookup receives "Url" already
    /// trimmed and matches with or without its own Trim(). ValidateColumnPolicies
    /// passes the column name RAW, so this is the only path where the clause
    /// decides anything. Dropping it (mutant M7) turns this config from REJECTED
    /// into ACCEPTED: preflight then prints dropped=[ Url ] while a populated,
    /// indexed Url property rides on every item — the exact report-is-not-true
    /// failure the rejection exists to stop.
    /// </para>
    /// </summary>
    [Fact]
    public void ColumnPolicyOnPaddedStandardPropertyName_IsRejected()
    {
        var path = Write("padded-policy-column.json", """
            {"objectList": [{
                "objectName": "Contact", "displayName": "Contact", "aclMode": "ownerOnly",
                "selectedFields": {"Id": "Id", " Url ": "Other"},
                "columnPolicies": {" Url ": "drop"}
            }]}
            """);

        var exception = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));
        Assert.Contains("EVERY item", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Url", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>The rejections must not be a blanket refusal: a _bdh_ placeholder
    /// is a content-body marker, not a Graph property name, so it cannot collide;
    /// and an ordinary mapping is untouched.</summary>
    [Fact]
    public void NonCollidingConfigs_StillLoad()
    {
        var path = Write("non-colliding.json", """
            {"objectList": [{
                "objectName": "Contact", "displayName": "Contact", "aclMode": "ownerOnly",
                "selectedFields": {"Url": "_bdh_Url", "Comp": "CompProp", "Email": "Email"},
                "columnPolicies": {"Comp": "mask"}
            }]}
            """);

        var obj = SchemaConfig.Load(path).ObjectList[0];
        Assert.Equal(new[] { "Comp" }, obj.MaskedColumns);
    }

    // ── graph-schema.json: non-string name, duplicate property ───────────────

    [Fact]
    public void GraphSchema_NonStringPropertyName_IsRejected()
    {
        using var env = ClassificationEnv(classification: null);
        var graphSchema = Write("graph-bad-name.json", """[{"name": 123, "type": "String"}]""");

        var result = ValidateConfig.ValidateCore(SchemaPath, graphSchema, FiltersPath, strict: true);

        Assert.Contains(result.Errors, e => e.Contains("'name' must be a string", StringComparison.Ordinal));
    }

    [Fact]
    public void GraphSchema_DuplicatePropertyName_IsRejected()
    {
        using var env = ClassificationEnv(classification: null);
        var graphSchema = Write("graph-dupe.json", """
            [{"name": "Title", "type": "String"}, {"name": "title", "type": "Int64"}]
            """);

        var result = ValidateConfig.ValidateCore(SchemaPath, graphSchema, FiltersPath, strict: true);

        Assert.Contains(result.Errors, e => e.Contains("declared more than once", StringComparison.Ordinal));
    }
}
