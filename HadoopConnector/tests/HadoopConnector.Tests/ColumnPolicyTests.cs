// ColumnPolicyTests.cs
// --------------------
// WP-HD-3: per-COLUMN drop/mask policies.
//
// THE CENTRAL RISK these tests exist to pin down: ItemConverter assembles an
// item in TWO independent passes over selectedFields — one emitting Graph
// PROPERTIES (Convert) and one appending unmapped fields to the searchable
// CONTENT body (BuildContent). A restriction gated in only ONE pass LEAKS: the
// value vanishes from the property but survives in the text Copilot grounds on
// (or the reverse). Every enforcement test below therefore asserts on BOTH
// surfaces, for BOTH routings (direct property and _bdh_ content), and never
// on just the one the field happens to be mapped to.

using System.Text.Json.Nodes;
using HadoopConnector.Commands;
using HadoopConnector.Config;
using HadoopConnector.Graph;
using HadoopConnector.Hdfs;
using HadoopConnector.Item;

namespace HadoopConnector.Tests;

public class ColumnPolicyTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // Distinctive sentinels: if any of these strings survives anywhere in the
    // item, a substring search finds it. No accidental matches.
    private const string CompValue = "SENTINEL-COMP-482000-GBP";
    private const string NotesValue = "SENTINEL-NOTES-severance-terms";
    private const string NameValue = "SENTINEL-NAME-Migrate-tenant";

    private static ObjectConfig Config(Dictionary<string, string>? policies = null) => new()
    {
        ObjectName = "Opportunity",
        DisplayName = "Opportunity",
        SelectedFields = new Dictionary<string, string>
        {
            ["Name"] = "Title",
            ["StageName"] = "Stage",
            ["Compensation__c"] = "Compensation",       // direct Graph property
            ["Description"] = "_bdh_Description",       // content-routed
            ["PrivateNotes__c"] = "_bdh_PrivateNotes",  // content-routed
        },
        ColumnPolicies = policies ?? new Dictionary<string, string>(),
        IconUrl = "https://icons.example/opportunity.png",
    };

    private static BdhRecord Record() => new(
        "Opportunity",
        new JsonObject
        {
            ["Id"] = "0065e00000abcde",
            ["Name"] = NameValue,
            ["StageName"] = "Negotiation",
            ["Compensation__c"] = CompValue,
            ["Description"] = "Move and verify",
            ["PrivateNotes__c"] = NotesValue,
        },
        "2026-07-15",
        "Opportunity/dt=2026-07-15/part-000.jsonl");

    private static readonly List<AclEntry> Acl = new()
    {
        new AclEntry(AclEntryType.User, "u1", AclAccessType.Grant),
    };

    private static ExternalItem Convert(Dictionary<string, string>? policies = null) =>
        new ItemConverter(TestConfig.Make()).Convert(Record(), Config(policies), Acl);

    /// <summary>Every property value flattened to text — so a leak is caught no
    /// matter which property name it hid under.</summary>
    private static string AllPropertyText(ExternalItem item) =>
        string.Join("\n", item.Properties.Select(kv => $"{kv.Key}={kv.Value}"));

    // ── drop: gone from BOTH surfaces ────────────────────────────────────────

    // THE test the work package calls out by name: a dropped column appears in
    // NEITHER item.Properties NOR the content body.
    [Fact]
    public void Drop_DirectProperty_AppearsInNeitherPropertiesNorContent()
    {
        var item = Convert(new() { ["Compensation__c"] = "drop" });

        Assert.False(item.Properties.ContainsKey("Compensation"));
        Assert.DoesNotContain(CompValue, AllPropertyText(item), StringComparison.Ordinal);
        Assert.DoesNotContain(CompValue, item.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Compensation", item.Content, StringComparison.Ordinal);

        // …and the unrestricted siblings are untouched.
        Assert.Equal("Negotiation", item.Properties["Stage"]);
        Assert.Contains("Move and verify", item.Content, StringComparison.Ordinal);
    }

    // Same assertion for a _bdh_-routed column: this is the direction that leaks
    // when only the property loop is gated.
    [Fact]
    public void Drop_ContentRoutedColumn_AppearsInNeitherPropertiesNorContent()
    {
        var item = Convert(new() { ["PrivateNotes__c"] = "drop" });

        Assert.DoesNotContain(NotesValue, item.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivateNotes__c", item.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(NotesValue, AllPropertyText(item), StringComparison.Ordinal);
        Assert.False(item.Properties.ContainsKey("_bdh_PrivateNotes"));
        Assert.False(item.Properties.ContainsKey("PrivateNotes__c"));

        Assert.Contains("Move and verify", item.Content, StringComparison.Ordinal);
    }

    // ── mask: key kept, value replaced, on BOTH surfaces ─────────────────────

    [Fact]
    public void Mask_DirectProperty_KeepsNameReplacesValue_AndLeaksNowhere()
    {
        var item = Convert(new() { ["Compensation__c"] = "mask" });

        Assert.True(item.Properties.ContainsKey("Compensation"));
        Assert.Equal(ColumnPolicy.MaskMarker, item.Properties["Compensation"]);
        Assert.DoesNotContain(CompValue, AllPropertyText(item), StringComparison.Ordinal);
        Assert.DoesNotContain(CompValue, item.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_ContentRoutedColumn_KeepsKeyReplacesValue_AndLeaksNowhere()
    {
        var item = Convert(new() { ["PrivateNotes__c"] = "mask" });

        Assert.Contains($"PrivateNotes__c: {ColumnPolicy.MaskMarker}", item.Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(NotesValue, item.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(NotesValue, AllPropertyText(item), StringComparison.Ordinal);
    }

    // The content body opens with a title line read straight off the "Name"
    // column — a THIRD emission path for a record value, outside both loops. A
    // policy on Name that only reached the two loops would drop the property and
    // still print the value at the top of the text Copilot reads.
    [Fact]
    public void Drop_OnTheTitleColumn_AlsoRemovesItFromTheContentTitleLine()
    {
        var item = Convert(new() { ["Name"] = "drop" });

        Assert.DoesNotContain(NameValue, item.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(NameValue, AllPropertyText(item), StringComparison.Ordinal);
        Assert.False(item.Properties.ContainsKey("Title"));
    }

    [Fact]
    public void Mask_OnTheTitleColumn_AlsoMasksTheContentTitleLine()
    {
        var item = Convert(new() { ["Name"] = "mask" });

        Assert.DoesNotContain(NameValue, item.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(NameValue, AllPropertyText(item), StringComparison.Ordinal);
        Assert.Equal(ColumnPolicy.MaskMarker, item.Properties["Title"]);
        Assert.Contains(ColumnPolicy.MaskMarker, item.Content, StringComparison.Ordinal);
    }

    // BDH exports vary in column casing and BdhRecord.Get is case-insensitive;
    // a policy that matched case-sensitively would silently not apply.
    [Fact]
    public void PolicyColumnMatching_IsCaseInsensitive()
    {
        var item = Convert(new() { ["compensation__C"] = "drop", ["privatenotes__c"] = "mask" });

        Assert.False(item.Properties.ContainsKey("Compensation"));
        Assert.DoesNotContain(CompValue, AllPropertyText(item), StringComparison.Ordinal);
        Assert.DoesNotContain(NotesValue, item.Content, StringComparison.Ordinal);
        Assert.Contains($"PrivateNotes__c: {ColumnPolicy.MaskMarker}", item.Content,
            StringComparison.Ordinal);
    }

    // A masked column keeps its key even when the underlying value is empty, so
    // the item shape never reveals whether a restricted field was populated.
    [Fact]
    public void Mask_EmptyValue_StillEmitsTheKeyAndMarker()
    {
        var record = new BdhRecord("Opportunity", new JsonObject
        {
            ["Id"] = "0065e00000abcde",
            ["Name"] = NameValue,
            ["PrivateNotes__c"] = "",
        });
        var item = new ItemConverter(TestConfig.Make())
            .Convert(record, Config(new() { ["PrivateNotes__c"] = "mask" }), Acl);

        Assert.Contains($"PrivateNotes__c: {ColumnPolicy.MaskMarker}", item.Content,
            StringComparison.Ordinal);
    }

    // ── no policies ⇒ behaviour byte-identical to today ──────────────────────

    [Fact]
    public void NoPolicies_ProducesByteIdenticalPropertiesAndContent()
    {
        var withoutMap = new ItemConverter(TestConfig.Make())
            .Convert(Record(), Config(), Acl);
        var explicitlyEmpty = new ItemConverter(TestConfig.Make())
            .Convert(Record(), Config(new Dictionary<string, string>()), Acl);

        Assert.Equal(AllPropertyText(withoutMap), AllPropertyText(explicitlyEmpty));
        Assert.Equal(withoutMap.Content, explicitlyEmpty.Content);

        // Pinned literals: the pre-WP-HD-3 output, so a regression in the
        // unrestricted path cannot pass by matching itself.
        Assert.Equal(NameValue, withoutMap.Properties["Title"]);
        Assert.Equal("Negotiation", withoutMap.Properties["Stage"]);
        Assert.Equal(CompValue, withoutMap.Properties["Compensation"]);
        Assert.False(withoutMap.Properties.ContainsKey("_bdh_Description"));
        Assert.Equal(
            $"Opportunity: {NameValue}\n"
            + "Description: Move and verify\n"
            + $"PrivateNotes__c: {NotesValue}\n"
            + "Data as of: 2026-07-15 (BDH nightly sync)",
            withoutMap.Content.Replace("\r\n", "\n"));
        Assert.DoesNotContain(ColumnPolicy.MaskMarker, withoutMap.Content, StringComparison.Ordinal);
    }

    // ── config parsing / validation ──────────────────────────────────────────

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir.Path, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ColumnPolicies_RoundTripThroughLoad()
    {
        var path = Write("cp-roundtrip.json", """
            {"objectList": [{
                "objectName": "Opportunity",
                "selectedFields": {"Name": "Title", "Comp__c": "Comp", "Notes__c": "_bdh_Notes"},
                "columnPolicies": {"Comp__c": "drop", "Notes__c": "mask"}
            }]}
            """);
        var obj = SchemaConfig.Load(path).FindObject("Opportunity")!;

        Assert.Equal(2, obj.ColumnPolicies.Count);
        Assert.Equal(ColumnPolicyAction.Drop, obj.PolicyFor("Comp__c"));
        Assert.Equal(ColumnPolicyAction.Mask, obj.PolicyFor("Notes__c"));
        Assert.Equal(ColumnPolicyAction.None, obj.PolicyFor("Name"));
        Assert.Equal(1, obj.DroppedColumnCount);
        Assert.Equal(1, obj.MaskedColumnCount);
    }

    [Fact]
    public void NoColumnPolicies_ParsesToAnEmptyMapAndNoAction()
    {
        var path = Write("cp-absent.json", """
            {"objectList": [{"objectName": "X", "selectedFields": {"Name": "Title"}}]}
            """);
        var obj = SchemaConfig.Load(path).FindObject("X")!;

        Assert.Empty(obj.ColumnPolicies);
        Assert.Equal(ColumnPolicyAction.None, obj.PolicyFor("Name"));
        Assert.False(obj.HasColumnPolicies);
    }

    [Theory]
    [InlineData("restrictToGroup")]   // deliberately out of scope (deferred to Ranger)
    [InlineData("redact")]
    [InlineData("DROP ")]
    [InlineData("")]
    public void UnknownColumnPolicyAction_IsRejectedAtLoad(string action)
    {
        var path = Write($"cp-bad-action-{action.Trim().Length}-{Guid.NewGuid():N}.json", $$"""
            {"objectList": [{
                "objectName": "Opportunity",
                "selectedFields": {"Comp__c": "Comp"},
                "columnPolicies": {"Comp__c": "{{action}}"}
            }]}
            """);
        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));

        Assert.Contains("columnPolicies", exc.Message, StringComparison.Ordinal);
        Assert.Contains("Opportunity", exc.Message, StringComparison.Ordinal);
        Assert.Contains("Comp__c", exc.Message, StringComparison.Ordinal);
        Assert.Contains("drop", exc.Message, StringComparison.Ordinal);
        Assert.Contains("mask", exc.Message, StringComparison.Ordinal);
    }

    // A policy naming a column that is not selected does nothing at all — it is
    // a typo that silently leaves the real column unrestricted, which is the
    // worst possible failure mode for a control like this. Fail the load.
    [Fact]
    public void ColumnPolicyOnUnselectedColumn_IsRejectedAtLoad()
    {
        var path = Write("cp-unknown-column.json", """
            {"objectList": [{
                "objectName": "Opportunity",
                "selectedFields": {"Comp__c": "Comp"},
                "columnPolicies": {"Compensation__c": "drop"}
            }]}
            """);
        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));

        Assert.Contains("Compensation__c", exc.Message, StringComparison.Ordinal);
        Assert.Contains("selectedFields", exc.Message, StringComparison.Ordinal);
        Assert.Contains("Opportunity", exc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnPolicyMatchingSelectedFieldByDifferentCase_IsAccepted()
    {
        var path = Write("cp-case.json", """
            {"objectList": [{
                "objectName": "Opportunity",
                "selectedFields": {"Comp__c": "Comp"},
                "columnPolicies": {"COMP__C": "drop"}
            }]}
            """);
        var obj = SchemaConfig.Load(path).FindObject("Opportunity")!;
        Assert.Equal(ColumnPolicyAction.Drop, obj.PolicyFor("Comp__c"));
    }

    // Two keys differing only in case would both match the same column under the
    // case-insensitive lookup; with different actions, which one wins would be
    // dictionary-order luck. Never guess on a restriction.
    [Fact]
    public void ConflictingCaseDuplicatePolicies_AreRejectedAtLoad()
    {
        var path = Write("cp-dup.json", """
            {"objectList": [{
                "objectName": "Opportunity",
                "selectedFields": {"Comp__c": "Comp"},
                "columnPolicies": {"Comp__c": "drop", "comp__c": "mask"}
            }]}
            """);
        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));
        Assert.Contains("columnPolicies", exc.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate", exc.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Column policies and the wave-1 coarse-ACL attestation are independent
    // controls on the same object and must not interfere.
    [Fact]
    public void ColumnPolicies_CoexistWithTheCoarseAclAttestation()
    {
        var path = Write("cp-with-attestation.json", """
            {"objectList": [{
                "objectName": "Account",
                "aclMode": "public",
                "coarseAclAcknowledged": true,
                "selectedFields": {"Name": "Title", "Margin__c": "Margin"},
                "columnPolicies": {"Margin__c": "mask"}
            }]}
            """);
        var obj = SchemaConfig.Load(path).FindObject("Account")!;

        Assert.True(obj.CoarseAclAcknowledged);
        Assert.True(obj.IsCoarseAcl);
        Assert.Equal(ColumnPolicyAction.Mask, obj.PolicyFor("Margin__c"));
    }

    // ── validate-config surfacing ────────────────────────────────────────────

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

    private string GraphSchemaPath => Write("graph-schema.json", """
        [{"name": "Title", "type": "String", "isSearchable": true}]
        """);

    private string FiltersPath => Write("filters.json", """
        {"objects": {"Contact": {"partition": [{"key": "dt", "op": "withinLastDays", "value": "30"}]}},
         "fullScanAllowed": []}
        """);

    [Fact]
    public void ValidateConfig_ReportsPerObjectDropAndMaskCounts()
    {
        using var scope = GoodEnv();
        var schema = Write("cp-validate.json", """
            {"objectList": [{
                "objectName": "Contact",
                "aclMode": "ownerOnly",
                "selectedFields": {"Name": "Title", "SSN__c": "Ssn", "Comp__c": "_bdh_Comp"},
                "columnPolicies": {"SSN__c": "mask", "Comp__c": "drop"}
            }]}
            """);
        var result = ValidateConfig.ValidateCore(schema, GraphSchemaPath, FiltersPath, strict: true);

        var notice = Assert.Single(result.Notices, n => n.Contains("Contact", StringComparison.Ordinal));
        Assert.Contains("1 dropped", notice, StringComparison.Ordinal);
        Assert.Contains("1 masked", notice, StringComparison.Ordinal);
        Assert.Contains("Comp__c", notice, StringComparison.Ordinal);
        Assert.Contains("SSN__c", notice, StringComparison.Ordinal);

        // Informational only: a configured restriction is posture, not a finding.
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.True(result.Ok(strict: true));
    }

    // The mask marker is a STRING. Masking a column whose Graph property is
    // declared Int64/Double/DateTime would push a string into a typed property
    // and Graph rejects the item — a restriction that silently stops the record
    // being indexed at all, discovered only mid-crawl. Catch it at preflight.
    [Fact]
    public void MaskOnNonStringGraphProperty_IsAWarning()
    {
        using var scope = GoodEnv();
        var graphSchema = Write("gs-typed.json", """
            [{"name": "Title", "type": "String", "isSearchable": true},
             {"name": "Amount", "type": "Int64", "isRefinable": true}]
            """);
        var schema = Write("cp-typed.json", """
            {"objectList": [{
                "objectName": "Contact",
                "aclMode": "ownerOnly",
                "selectedFields": {"Name": "Title", "Amount": "Amount"},
                "columnPolicies": {"Amount": "mask"}
            }]}
            """);
        var result = ValidateConfig.ValidateCore(schema, graphSchema, FiltersPath);

        var warning = Assert.Single(result.Warnings, w => w.Contains("Amount", StringComparison.Ordinal));
        Assert.Contains("Int64", warning, StringComparison.Ordinal);
        Assert.Contains("mask", warning, StringComparison.Ordinal);
    }

    // …but a mask on a String property, or a drop on a typed one, is fine.
    [Fact]
    public void MaskOnStringProperty_AndDropOnTypedProperty_AreNotWarnings()
    {
        using var scope = GoodEnv();
        var graphSchema = Write("gs-typed2.json", """
            [{"name": "Title", "type": "String", "isSearchable": true},
             {"name": "Amount", "type": "Int64", "isRefinable": true}]
            """);
        var schema = Write("cp-typed2.json", """
            {"objectList": [{
                "objectName": "Contact",
                "aclMode": "ownerOnly",
                "selectedFields": {"Name": "Title", "Amount": "Amount"},
                "columnPolicies": {"Amount": "drop", "Name": "mask"}
            }]}
            """);
        var result = ValidateConfig.ValidateCore(schema, graphSchema, FiltersPath);

        Assert.Empty(result.Warnings);
        Assert.Empty(result.Errors);
    }

    // The absence of any per-column restriction is itself the posture this
    // connector shipped with, so it must be stated rather than left silent.
    [Fact]
    public void ValidateConfig_WithNoPoliciesAnywhere_SaysSoExplicitly()
    {
        using var scope = GoodEnv();
        var schema = Write("cp-validate-none.json", """
            {"objectList": [{
                "objectName": "Contact",
                "aclMode": "ownerOnly",
                "selectedFields": {"Name": "Title", "SSN__c": "Ssn"}
            }]}
            """);
        var result = ValidateConfig.ValidateCore(schema, GraphSchemaPath, FiltersPath, strict: true);

        Assert.Contains(result.Notices,
            n => n.Contains("No per-column", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(result.Warnings);
        Assert.True(result.Ok(strict: true));
    }

    // Which columns are restricted is a data-owner decision, so the shipped
    // config restricts none — and preflight must SAY that rather than leave the
    // absence of the control looking like the control being satisfied. Mirrors
    // ValidateConfigTests.RepoSchema_ShipsNoPreCheckedAttestation.
    [Fact]
    public void RepoSchema_ShipsNoColumnPolicies_AndPreflightSaysSo()
    {
        var repoSchema = Path.Combine(AppContext.BaseDirectory, "config", "schema.json");
        var schema = SchemaConfig.Load(repoSchema);
        Assert.All(schema.ObjectList, o => Assert.False(o.HasColumnPolicies));

        using var scope = GoodEnv();
        var result = ValidateConfig.ValidateCore(
            repoSchema,
            Path.Combine(AppContext.BaseDirectory, "config", "graph-schema.json"),
            Path.Combine(AppContext.BaseDirectory, "config", "filters.json"));

        Assert.Contains(result.Notices,
            n => n.Contains("No per-column", StringComparison.OrdinalIgnoreCase));
    }
}
