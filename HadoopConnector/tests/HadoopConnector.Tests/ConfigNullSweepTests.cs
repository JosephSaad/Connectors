// ConfigNullSweepTests.cs
// -----------------------
// The JSON-null class, swept rather than sampled.
//
// A previous round fixed ONE property (coarseAclAcknowledgedFor) at its setter.
// The reasoning was right and the scope was wrong: System.Text.Json OVERWRITES a
// `= new()` / `= string.Empty` property initializer with null whenever the JSON
// says null, so EVERY reference-typed member of the config model had the same
// defect, and five more shapes were still live — including one
// ("selectedFields" VALUE null) that passed `validate-config --strict` with zero
// errors and then NRE'd on every record, silently dead-lettering 100% of the
// object at 150M-row scale.
//
// These tests therefore do not enumerate property names. They REFLECT over the
// model, set each JSON-bindable member to null in turn, and assert the invariant
// for all of them at once:
//
//   * no NullReferenceException, ever — at load or downstream; and
//   * either the config loads with NO null anywhere in the model, or it is
//     rejected with an InvalidDataException that NAMES the offending key.
//
// Because the sweep is generated from the model's own metadata, a member added
// later is covered the day it is added, and a member that regresses fails here
// without anyone remembering to write a case for it.

using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HadoopConnector.Commands;
using HadoopConnector.Config;
using HadoopConnector.Graph;
using HadoopConnector.Hdfs;
using HadoopConnector.Item;

namespace HadoopConnector.Tests;

public class ConfigNullSweepTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Write(string content)
    {
        var path = Path.Combine(_dir.Path, $"schema-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    // ── the model's own metadata drives the sweep ───────────────────────────

    /// <summary>Every JSON key the deserializer can bind on a config type: the
    /// same set <see cref="ConfigNullNormalizer"/> walks, derived the same way,
    /// so the two cannot disagree about what "every member" means.</summary>
    private static IEnumerable<string> JsonKeysOf(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
            .Where(p => !p.IsDefined(typeof(JsonIgnoreAttribute), inherit: true))
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                         ?? char.ToLowerInvariant(p.Name[0]) + p.Name[1..]);

    public static TheoryData<string> ObjectConfigKeys()
    {
        var data = new TheoryData<string>();
        foreach (var key in JsonKeysOf(typeof(ObjectConfig)))
            data.Add(key);
        return data;
    }

    public static TheoryData<string> SchemaConfigKeys()
    {
        var data = new TheoryData<string>();
        foreach (var key in JsonKeysOf(typeof(SchemaConfig)))
            data.Add(key);
        return data;
    }

    // The sweep is worthless if the model reflects as empty, so pin the shape it
    // is expected to have. This also fails loudly when a member is ADDED, which
    // is the moment to think about its null behaviour.
    [Fact]
    public void TheSweepActuallyCoversTheWholeModel()
    {
        var keys = JsonKeysOf(typeof(ObjectConfig)).ToList();
        Assert.Equal(
            new[]
            {
                "objectName", "displayName", "selectedFields", "columnPolicies", "aclMode",
                "aclGroupId", "coarseAclAcknowledged", "coarseAclAcknowledgedFor", "ownerField",
                "ownerEmailField", "sourcePath", "iconUrl", "sensitivityDefault",
            }.OrderBy(k => k, StringComparer.Ordinal),
            keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(new[] { "objectList" }, JsonKeysOf(typeof(SchemaConfig)));
    }

    /// <summary>A minimal object that loads cleanly, as a mutable JSON tree.</summary>
    private static JsonObject BaselineObject() =>
        JsonNode.Parse("""
            {"objectName": "Contact",
             "displayName": "Contact",
             "aclMode": "ownerOnly",
             "selectedFields": {"Id": "RecordId", "Name": "Title"},
             "columnPolicies": {"Name": "mask"},
             "ownerField": "OwnerId",
             "ownerEmailField": "OwnerEmail",
             "sourcePath": "Contact",
             "iconUrl": "https://example/icon.png",
             "sensitivityDefault": "Internal"}
            """)!.AsObject();

    // ── THE SWEEP ───────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ObjectConfigKeys))]
    public void EveryObjectMemberSetToNull_NeverNREs_AndIsEitherCleanOrNamed(string key)
    {
        var obj = BaselineObject();
        obj[key] = null;
        var path = Write(new JsonObject { ["objectList"] = new JsonArray(obj) }.ToJsonString());

        AssertNullShapeIsHandled(path, key);
    }

    [Theory]
    [MemberData(nameof(SchemaConfigKeys))]
    public void EveryRootMemberSetToNull_NeverNREs_AndIsEitherCleanOrNamed(string key)
    {
        var root = new JsonObject { ["objectList"] = new JsonArray(BaselineObject()) };
        root[key] = null;
        var path = Write(root.ToJsonString());

        AssertNullShapeIsHandled(path, key);
    }

    // Dictionary VALUES are the shape the blocker used, and reflection over
    // PROPERTIES would never reach them: sweep every value of every dictionary
    // member the same way.
    [Theory]
    [InlineData("selectedFields", "Name")]
    [InlineData("selectedFields", "Id")]
    [InlineData("columnPolicies", "Name")]
    public void EveryDictionaryValueSetToNull_NeverNREs_AndIsEitherCleanOrNamed(
        string member, string entryKey)
    {
        var obj = BaselineObject();
        obj[member]!.AsObject()[entryKey] = null;
        var path = Write(new JsonObject { ["objectList"] = new JsonArray(obj) }.ToJsonString());

        // The message must name the ENTRY, not merely the member — an operator
        // with 40 selected fields needs to know which one.
        AssertNullShapeIsHandled(path, entryKey);
    }

    /// <summary>The invariant, asserted identically for every shape: no NRE
    /// anywhere (load OR the downstream reads that made the blocker silent), and
    /// either a fully non-null model or a named InvalidDataException.</summary>
    private static void AssertNullShapeIsHandled(string path, string expectedNameInMessage)
    {
        SchemaConfig? config = null;
        var exc = Record.Exception(() => config = SchemaConfig.Load(path));

        Assert.IsNotType<NullReferenceException>(exc);
        if (exc is not null)
        {
            Assert.IsType<InvalidDataException>(exc);
            Assert.Contains(expectedNameInMessage, exc.Message, StringComparison.OrdinalIgnoreCase);
            return;
        }

        // Loaded ⇒ nothing in the model is null, and every downstream consumer
        // that dereferences it survives. A config that loads and then dies at
        // conversion time is the exact failure this class produced.
        AssertNoNullsAnywhere(config!);
        AssertConvertsRecords(config!);
    }

    // ── the "loaded clean" half of the invariant ────────────────────────────

    /// <summary>Reflective assertion that NO reference member reachable from the
    /// model is null — properties, collection elements and dictionary values
    /// alike. This is what "null-safe by construction" has to mean; checking the
    /// handful of members someone remembered is what failed last time.</summary>
    private static void AssertNoNullsAnywhere(object node, string where = "$")
    {
        foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
                continue;
            var at = $"{where}.{property.Name}";
            var value = property.GetValue(node);
            if (value is null)
            {
                Assert.Fail($"{at} is null after a clean load — the model is not null-safe.");
                continue;
            }
            AssertValueHasNoNulls(value, at);
        }
    }

    private static void AssertValueHasNoNulls(object value, string at)
    {
        switch (value)
        {
            case string:
                return;
            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    Assert.True(entry.Value is not null, $"{at}[{entry.Key}] is null after a clean load.");
                    AssertValueHasNoNulls(entry.Value!, $"{at}[{entry.Key}]");
                }
                return;
            case IEnumerable sequence when value is not string:
                var index = 0;
                foreach (var element in sequence)
                {
                    Assert.True(element is not null, $"{at}[{index}] is null after a clean load.");
                    AssertValueHasNoNulls(element!, $"{at}[{index++}]");
                }
                return;
            default:
                if (value.GetType().Assembly == typeof(SchemaConfig).Assembly)
                    AssertNoNullsAnywhere(value, at);
                return;
        }
    }

    /// <summary>Run real records through the real converter — the deref site that
    /// turned a green preflight into a 100% dead-letter.</summary>
    private static void AssertConvertsRecords(SchemaConfig config)
    {
        var converter = new ItemConverter(TestConfig.Make());
        foreach (var obj in config.ObjectList)
        {
            for (var i = 0; i < 3; i++)
            {
                var fields = JsonNode.Parse(
                    $$"""{"Id": "id{{i}}", "Name": "n{{i}}", "Comp__c": "100", "OwnerId": "u1"}""")!.AsObject();
                var record = new BdhRecord(obj.ObjectName, fields, "2026-07-19");
                var item = converter.Convert(record, obj, new List<AclEntry>());
                Assert.NotNull(item.Content);
                Assert.DoesNotContain(item.Properties.Keys, k => string.IsNullOrWhiteSpace(k));
            }
        }
    }

    // ── THE BLOCKER, verbatim ───────────────────────────────────────────────

    private const string BlockerConfig = """
        {"objectList":[{"objectName":"Contact","aclMode":"ownerOnly",
          "selectedFields":{"Id":"Id","Name":"Title","Comp__c":null}}]}
        """;

    // Before: loaded cleanly, --strict Ok=True errors=0 warnings=0, then NRE on
    // every record. Now: rejected at load, naming the column.
    [Fact]
    public void NullSelectedFieldValue_IsRejectedAtLoad_NamingTheColumn()
    {
        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(Write(BlockerConfig)));

        Assert.Contains("Comp__c", exc.Message, StringComparison.Ordinal);
        Assert.Contains("Contact", exc.Message, StringComparison.Ordinal);
        Assert.Contains("selectedFields", exc.Message, StringComparison.Ordinal);
    }

    // The dangerous half was the GREEN preflight. --strict must now fail.
    [Fact]
    public void NullSelectedFieldValue_FailsPreflightInsteadOfPassingItGreen()
    {
        using var scope = GoodEnv();
        var path = Write(BlockerConfig);

        var result = ValidateConfig.ValidateCore(path, GraphSchemaPath, FiltersPath, strict: true);

        Assert.False(result.Ok(strict: true));
        Assert.Contains(result.Errors, e => e.Contains("schema.json invalid", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("Comp__c", StringComparison.Ordinal));
    }

    // A null value among many good ones must be found, not averaged away.
    [Fact]
    public void OneNullAmongManyGoodSelectedFields_IsStillRejected()
    {
        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(Write("""
            {"objectList":[{"objectName":"Contact","aclMode":"ownerOnly","selectedFields":{
              "Id":"RecordId","Name":"Title","A__c":"A","B__c":"B","C__c":null,"D__c":"D"}}]}
            """)));

        Assert.Contains("C__c", exc.Message, StringComparison.Ordinal);
    }

    // The other side of the mapping: a column name that names no column. JSON
    // cannot write a null KEY, but it can write an empty or blank one, and the
    // outcome must be the same rejection rather than a property emitted empty on
    // every record.
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\\t")]
    public void AnEmptySelectedFieldsColumnName_IsRejectedAtLoad(string column)
    {
        var selectedFields = $$"""{"Id":"RecordId","{{column}}":"Title"}""";
        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(Write($$"""
            {"objectList":[{"objectName":"Contact","aclMode":"ownerOnly",
              "selectedFields":{{selectedFields}}}]}
            """)));

        Assert.Contains("empty column name", exc.Message, StringComparison.Ordinal);
        Assert.Contains("Contact", exc.Message, StringComparison.Ordinal);
    }

    // ── null LIST elements: no empty form, so a named load error ────────────

    [Fact]
    public void NullObjectListElement_IsRejected_NamingItsIndex()
    {
        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(Write("""
            {"objectList":[null]}
            """)));

        Assert.Contains("$.objectList[0]", exc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullElementAfterAValidObject_NamesTheRightIndex()
    {
        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(Write("""
            {"objectList":[{"objectName":"Contact","selectedFields":{"Name":"Title"}}, null]}
            """)));

        Assert.Contains("$.objectList[1]", exc.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("$.objectList[0]", exc.Message, StringComparison.Ordinal);
    }

    // ── a WRONG-TYPED value is a config error too, not a raw parse crash ────

    [Theory]
    [InlineData("\"coarseAclAcknowledged\": null", "coarseAclAcknowledged")]
    [InlineData("\"coarseAclAcknowledged\": \"yes\"", "coarseAclAcknowledged")]
    [InlineData("\"selectedFields\": []", "selectedFields")]
    [InlineData("\"objectList\": {}", "objectList")]
    public void AWrongTypedValue_IsAnInvalidDataExceptionNamingTheKey(string fragment, string key)
    {
        var json = fragment.Contains("objectList", StringComparison.Ordinal)
            ? $"{{{fragment}}}"
            : $$"""{"objectList":[{"objectName":"Contact","selectedFields":{"Name":"Title"},{{fragment}}}]}""";

        var exc = Record.Exception(() => SchemaConfig.Load(Write(json)));

        Assert.IsType<InvalidDataException>(exc);
        Assert.Contains(key, exc.Message, StringComparison.Ordinal);
    }

    // ── the "legal empty" half: nulls that MUST keep loading ────────────────

    // Every optional member null at once. This is the null == empty semantics
    // holding across the whole model in one config, and it must still crawl.
    [Fact]
    public void EveryOptionalMemberNullAtOnce_LoadsAndConverts()
    {
        var path = Write("""
            {"objectList":[{
              "objectName": "Contact",
              "displayName": null,
              "aclMode": "ownerOnly",
              "selectedFields": {"Id": "RecordId", "Name": "Title"},
              "columnPolicies": null,
              "coarseAclAcknowledgedFor": null,
              "ownerField": null,
              "ownerEmailField": null,
              "sourcePath": null,
              "iconUrl": null,
              "sensitivityDefault": null
            }]}
            """);

        var config = SchemaConfig.Load(path);
        var obj = config.FindObject("Contact")!;

        AssertNoNullsAnywhere(config);
        AssertConvertsRecords(config);
        // …and the empty values fall through to the same defaults an ABSENT key
        // would have produced, so null and absent are not two different configs.
        Assert.Equal("OwnerId", obj.EffectiveOwnerField);
        Assert.Equal("OwnerEmail", obj.EffectiveOwnerEmailField);
        Assert.False(obj.HasColumnPolicies);
        Assert.False(obj.HasBoundCoarseAclAttestation);
    }

    // The documented exception to null == absent, pinned so it cannot drift
    // silently: aclMode is the one member with a non-empty initializer, and an
    // explicit null is the EMPTY aclMode — rejected, not defaulted.
    [Fact]
    public void NullAclMode_IsRejectedRatherThanDefaultedToOwnerOnly()
    {
        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(Write("""
            {"objectList":[{"objectName":"Contact","aclMode":null,"selectedFields":{"Name":"Title"}}]}
            """)));

        Assert.Contains("aclMode", exc.Message, StringComparison.Ordinal);
    }

    // ── ItemConverter's own guard, per member of the routing decision ───────

    [Theory]
    [InlineData(null, ItemConverter.FieldRoute.Unusable)]
    [InlineData("", ItemConverter.FieldRoute.Unusable)]
    [InlineData("   ", ItemConverter.FieldRoute.Unusable)]
    [InlineData("\t", ItemConverter.FieldRoute.Unusable)]
    [InlineData("_bdh_Notes", ItemConverter.FieldRoute.Content)]
    [InlineData("Title", ItemConverter.FieldRoute.Property)]
    [InlineData(" _bdh_Notes", ItemConverter.FieldRoute.Property)]  // not a placeholder: matches emission
    public void RouteFor_ClassifiesEveryMappingShape(string? property, ItemConverter.FieldRoute expected) =>
        Assert.Equal(expected, ItemConverter.RouteFor(property));

    // A hand-built config (no Load, so no normalisation) with a null on the
    // PROPERTY side must not NRE — covering loop 1 on its own.
    [Fact]
    public void Convert_WithANullPropertyName_EmitsNothingForItRatherThanCrashing()
    {
        var obj = new ObjectConfig
        {
            ObjectName = "Contact",
            SelectedFields = new Dictionary<string, string>
            {
                ["Id"] = "RecordId",
                ["Comp__c"] = null!,
            },
        };
        var record = new BdhRecord(
            "Contact", JsonNode.Parse("""{"Id":"a1","Comp__c":"XXSENTINEL7"}""")!.AsObject());

        var item = new ItemConverter(TestConfig.Make()).Convert(record, obj, new List<AclEntry>());

        Assert.Equal("a1", item.Properties["RecordId"]);
        Assert.DoesNotContain("XXSENTINEL7", JsonSerializer.Serialize(item.Properties), StringComparison.Ordinal);
        Assert.DoesNotContain("XXSENTINEL7", item.Content, StringComparison.Ordinal);
    }

    // …and the CONTENT loop on its own: a null mapping must not be mistaken for a
    // _bdh_ placeholder and must not print the value into the grounding text.
    [Fact]
    public void BuildContent_WithANullPropertyName_DoesNotEmitTheValue()
    {
        var obj = new ObjectConfig
        {
            ObjectName = "Contact",
            DisplayName = "Contact",
            SelectedFields = new Dictionary<string, string>
            {
                ["Name"] = "Title",
                ["Notes__c"] = null!,
            },
        };
        var record = new BdhRecord(
            "Contact", JsonNode.Parse("""{"Name":"Ada","Notes__c":"YYSENTINEL8"}""")!.AsObject());

        var content = new ItemConverter(TestConfig.Make()).BuildContent(record, obj);

        Assert.Contains("Ada", content, StringComparison.Ordinal);
        Assert.DoesNotContain("YYSENTINEL8", content, StringComparison.Ordinal);
    }

    // DirectPropertyFields is read by validate-config; it dereferenced the same
    // value and must not NRE on a hand-built config either.
    [Fact]
    public void DirectPropertyFields_SkipsUnusableMappingsInsteadOfCrashing()
    {
        var obj = new ObjectConfig
        {
            SelectedFields = new Dictionary<string, string>
            {
                ["Id"] = "RecordId",
                ["A__c"] = null!,
                ["B__c"] = "  ",
                ["C__c"] = "_bdh_C",
            },
        };

        Assert.Equal(new[] { "Id" }, obj.DirectPropertyFields.Select(kv => kv.Key));
    }

    // ── env helpers (mirrors ConfigLoadRobustnessTests) ─────────────────────

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
            File.WriteAllText(path, """
                [{"name": "Title", "type": "String", "isSearchable": true},
                 {"name": "RecordId", "type": "String", "isSearchable": false},
                 {"name": "Comp", "type": "String", "isSearchable": false},
                 {"name": "Other", "type": "String", "isSearchable": false}]
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
