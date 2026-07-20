// RestrictionBypassProbeTests.cs
// ------------------------------
// Regression tests for two PROVED bypasses of controls that reported success
// while restricting nothing, plus the dead-letter question they raised.
//
//   1. A columnPolicies entry on the IDENTITY column ("Id") loaded cleanly and
//      validate-config announced "1 dropped", but the value still reached the
//      index three ways — externalItem.id, the Url deep link built from it
//      (ItemConverter.BuildUrl) and the content title line's id fallback when
//      Name was dropped too. The id is structurally load-bearing (Graph cannot
//      index an item without it; inventory, deletion sync, dead-letter and
//      retry-failed all key on it), so it CANNOT be enforced — the policy is
//      therefore rejected at config load rather than reported as honoured.
//
//   2. coarseAclAcknowledged was an UNBOUND bool: it recorded that *someone*
//      signed off, not WHAT they signed off. Widening group → public, or
//      swapping aclGroupId for a far broader group, silently reused the old
//      sign-off and --strict still passed. The attestation now has to name the
//      posture it covers (coarseAclAcknowledgedFor), and a posture change
//      invalidates it.
//
//   3. Dead-letter payloads: asserted here to be built from the CONVERTED item,
//      so column policies already cover them in DEADLETTER_PAYLOAD_MODE=full.

using System.Text.Json.Nodes;
using HadoopConnector.Commands;
using HadoopConnector.Config;
using HadoopConnector.Graph;
using HadoopConnector.Hdfs;
using HadoopConnector.Item;

namespace HadoopConnector.Tests;

public class RestrictionBypassProbeTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir.Path, name);
        File.WriteAllText(path, content);
        return path;
    }

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

    // Mapped properties + the always-emitted set — required by the
    // produced-vs-declared cross-check for any green-path run.
    private string GraphSchemaPath => Write("graph-schema.json", """
        [{"name": "Title", "type": "String", "isSearchable": true},
         {"name": "RecordId", "type": "String", "isSearchable": false},
         {"name": "ObjectName", "type": "String"},
         {"name": "Url", "type": "String"},
         {"name": "IconUrl", "type": "String"},
         {"name": "SourceSystem", "type": "String"},
         {"name": "DataAsOf", "type": "String"},
         {"name": "SensitivityLabel", "type": "String"},
         {"name": "DetectedCategories", "type": "StringCollection"}]
        """);

    private string FiltersPath => Write("filters.json", """
        {"objects": {"Contact": {"partition": [{"key": "dt", "op": "withinLastDays", "value": "30"}]},
                     "Opportunity": {"partition": [{"key": "dt", "op": "withinLastDays", "value": "30"}]}},
         "fullScanAllowed": []}
        """);

    // ── PROBE 1: a policy on the identity column ─────────────────────────────

    private const string IdValue = "0065e00000SENTINELID";

    /// <summary>A schema whose identity column is selected AND policied — the
    /// exact shape the probe used.</summary>
    private string IdPolicySchema(string action, string name) => Write(name, $$"""
        {"objectList": [{
            "objectName": "Opportunity",
            "displayName": "Opportunity",
            "aclMode": "ownerOnly",
            "selectedFields": {"Id": "RecordId", "Name": "Title"},
            "columnPolicies": {"Id": "{{action}}"}
        }]}
        """);

    [Theory]
    [InlineData("drop")]
    [InlineData("mask")]
    public void ColumnPolicyOnTheIdentityColumn_IsRejectedAtLoad(string action)
    {
        var path = IdPolicySchema(action, $"id-policy-{action}.json");
        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));

        Assert.Contains("Opportunity", exc.Message, StringComparison.Ordinal);
        Assert.Contains("Id", exc.Message, StringComparison.Ordinal);
        // The message must say WHY it cannot be honoured, naming both surviving
        // surfaces, so the operator is not left thinking the tool is fussy.
        Assert.Contains("externalItem id", exc.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Url", exc.Message, StringComparison.Ordinal);
        Assert.Contains("selectedFields", exc.Message, StringComparison.Ordinal);
    }

    // Case-insensitivity matters: BdhRecord.RawId reads "Id" case-insensitively,
    // so "id"/"ID" name the SAME load-bearing column and must be rejected too —
    // otherwise the rejection is trivially side-stepped by changing one letter.
    [Theory]
    [InlineData("id")]
    [InlineData("ID")]
    [InlineData("iD")]
    public void ColumnPolicyOnTheIdentityColumn_IsRejectedRegardlessOfCase(string column)
    {
        var path = Write($"id-policy-case-{column}.json", $$"""
            {"objectList": [{
                "objectName": "Opportunity",
                "selectedFields": {"{{column}}": "RecordId", "Name": "Title"},
                "columnPolicies": {"{{column}}": "drop"}
            }]}
            """);
        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));
        Assert.Contains("externalItem id", exc.Message, StringComparison.OrdinalIgnoreCase);
    }

    // THE headline claim: validate-config must never announce a restriction that
    // the converter cannot deliver. With the load rejection in place the config
    // never reaches the posture notice at all — it is an ERROR, not "1 dropped".
    [Fact]
    public void ValidateConfig_NeverReportsTheIdentityColumnAsDropped()
    {
        using var scope = GoodEnv();
        var schema = IdPolicySchema("drop", "id-policy-validate.json");
        var result = ValidateConfig.ValidateCore(schema, GraphSchemaPath, FiltersPath, strict: true);

        Assert.Contains(result.Errors, e => e.Contains("schema.json invalid", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Notices, n => n.Contains("1 dropped", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Notices,
            n => n.Contains("dropped=[Id]", StringComparison.Ordinal));
        Assert.False(result.Ok(strict: true));
    }

    // The three surfaces the probe proved the value survives on. This test pins
    // the SURVIVAL down deliberately: it documents exactly why the policy cannot
    // be enforced, so a future "just gate it in ItemConverter too" change has to
    // confront the fact that all three are structural.
    [Fact]
    public void IdentityColumnValue_StructurallyReachesIdUrlAndTitleFallback()
    {
        var objectConfig = new ObjectConfig
        {
            ObjectName = "Opportunity",
            DisplayName = "Opportunity",
            SelectedFields = new Dictionary<string, string> { ["Name"] = "Title" },
            ColumnPolicies = new Dictionary<string, string> { ["Name"] = "drop" },
        };
        var record = new BdhRecord("Opportunity", new JsonObject
        {
            ["Id"] = IdValue,
            ["Name"] = "SENTINEL-NAME",
        });
        var item = new ItemConverter(TestConfig.Make(), "https://org.example/records")
            .Convert(record, objectConfig, new List<AclEntry>());

        Assert.Equal(IdValue, item.Id);
        Assert.Equal($"https://org.example/records/{IdValue}", item.Properties["Url"]);
        Assert.Contains(IdValue, item.Content, StringComparison.Ordinal);
    }

    // A policy on a NON-identity column is untouched by the new rejection.
    [Fact]
    public void ColumnPolicyOnAnOrdinaryColumn_StillLoads()
    {
        var path = Write("id-policy-control.json", """
            {"objectList": [{
                "objectName": "Opportunity",
                "selectedFields": {"Id": "RecordId", "Name": "Title", "Comp__c": "Comp"},
                "columnPolicies": {"Comp__c": "drop"}
            }]}
            """);
        var obj = SchemaConfig.Load(path).FindObject("Opportunity")!;
        Assert.Equal(ColumnPolicyAction.Drop, obj.PolicyFor("Comp__c"));
        Assert.Equal(ColumnPolicyAction.None, obj.PolicyFor("Id"));
    }

    // ── PROBE 2: the coarse-ACL attestation must be bound to its posture ──────

    private const string NarrowGroup = "11111111-2222-3333-4444-555555555555";
    private const string BroadGroup = "99999999-8888-7777-6666-555555555555";

    private string CoarseSchema(
        string name, string aclMode, string? groupId, bool acknowledged, string? attestedFor)
    {
        var group = groupId is null ? string.Empty : $"\"aclGroupId\": \"{groupId}\", ";
        var ack = acknowledged ? "\"coarseAclAcknowledged\": true, " : string.Empty;
        var bound = attestedFor is null
            ? string.Empty
            : $"\"coarseAclAcknowledgedFor\": \"{attestedFor}\", ";
        return Write(name, $$"""
            {"objectList": [{
                "objectName": "Contact",
                "displayName": "Contact",
                "aclMode": "{{aclMode}}", {{group}}{{ack}}{{bound}}
                "selectedFields": {"Name": "Title"}
            }]}
            """);
    }

    // THE proved bypass: sign off on 'group', widen to 'public', and the stale
    // 'true' pre-approved an exposure nobody reviewed.
    [Fact]
    public void AttestationSignedForGroup_DoesNotSurviveWideningToPublic()
    {
        var path = CoarseSchema("widen-group-to-public.json", "public", null, true,
            $"group:{NarrowGroup}");
        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));

        Assert.Contains("coarseAclAcknowledgedFor", exc.Message, StringComparison.Ordinal);
        Assert.Contains($"group:{NarrowGroup}", exc.Message, StringComparison.Ordinal);
        Assert.Contains("public", exc.Message, StringComparison.Ordinal);
        Assert.Contains("Contact", exc.Message, StringComparison.Ordinal);
    }

    // The same bypass one notch quieter: same aclMode, far broader group.
    [Fact]
    public void AttestationSignedForOneGroup_DoesNotSurviveSwappingToABroaderGroup()
    {
        var path = CoarseSchema("widen-group-swap.json", "group", BroadGroup, true,
            $"group:{NarrowGroup}");
        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));

        Assert.Contains(NarrowGroup, exc.Message, StringComparison.Ordinal);
        Assert.Contains(BroadGroup, exc.Message, StringComparison.Ordinal);
    }

    // …and the narrowing direction is invalidated too. 'Narrower' is a judgement
    // this connector cannot make (it does not know Entra group memberships), so
    // ANY posture change forces a fresh sign-off rather than guessing.
    [Fact]
    public void AttestationSignedForPublic_DoesNotSurviveNarrowingToGroup()
    {
        var path = CoarseSchema("narrow-public-to-group.json", "group", NarrowGroup, true, "public");
        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));
        Assert.Contains("public", exc.Message, StringComparison.Ordinal);
    }

    // A matching attestation is the one thing that passes --strict.
    [Theory]
    [InlineData("public", null, "public")]
    [InlineData("group", NarrowGroup, "group:" + NarrowGroup)]
    public void AttestationBoundToTheEffectivePosture_PassesStrict(
        string aclMode, string? groupId, string attestedFor)
    {
        using var scope = GoodEnv();
        var path = CoarseSchema($"bound-ok-{aclMode}.json", aclMode, groupId, true, attestedFor);
        var result = ValidateConfig.ValidateCore(path, GraphSchemaPath, FiltersPath, strict: true);

        Assert.Contains(result.AcknowledgedWarnings,
            w => w.Contains("Contact", StringComparison.Ordinal)
                 && w.Contains($"coarse aclMode '{aclMode}'", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Errors, e => e.Contains("coarse aclMode", StringComparison.Ordinal));
        Assert.True(result.Ok(strict: true));
    }

    // Entra group object ids are case-insensitive GUIDs; a casing difference is
    // not a posture change and must not force a spurious re-review.
    [Fact]
    public void AttestationGroupIdComparison_IsCaseInsensitive()
    {
        using var scope = GoodEnv();
        var path = CoarseSchema("bound-case.json", "group", NarrowGroup.ToUpperInvariant(), true,
            $"GROUP:{NarrowGroup}");
        var result = ValidateConfig.ValidateCore(path, GraphSchemaPath, FiltersPath, strict: true);
        Assert.True(result.Ok(strict: true));
    }

    // The UNBOUND legacy form — 'true' with nothing recorded about what was
    // signed. It is exactly the state the probe exploited, so it is no longer an
    // attestation: --strict fails and the message says what to add.
    [Theory]
    [InlineData("public", null)]
    [InlineData("group", NarrowGroup)]
    public void UnboundAttestation_NoLongerSatisfiesStrict(string aclMode, string? groupId)
    {
        using var scope = GoodEnv();
        var path = CoarseSchema($"unbound-{aclMode}.json", aclMode, groupId, true, null);
        var result = ValidateConfig.ValidateCore(path, GraphSchemaPath, FiltersPath, strict: true);

        var error = Assert.Single(result.Errors,
            e => e.Contains("coarse aclMode", StringComparison.Ordinal));
        Assert.Contains("coarseAclAcknowledgedFor", error, StringComparison.Ordinal);
        Assert.False(result.Ok(strict: true));
        // Not silently accepted as a sign-off either.
        Assert.Empty(result.AcknowledgedWarnings);
    }

    // A binding without a sign-off is a half-deleted attestation, not a sign-off.
    [Fact]
    public void BindingWithoutTheAcknowledgementFlag_IsRejectedAtLoad()
    {
        var path = CoarseSchema("binding-no-flag.json", "public", null, false, "public");
        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));
        Assert.Contains("coarseAclAcknowledged", exc.Message, StringComparison.Ordinal);
    }

    // Same reasoning as the existing ownerOnly rule for the bool: a binding left
    // behind on a narrowed object pre-approves the next widening.
    [Fact]
    public void BindingOnAnOwnerOnlyObject_IsRejectedAtLoad()
    {
        var path = CoarseSchema("binding-owneronly.json", "ownerOnly", null, false, "public");
        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));
        Assert.Contains("ownerOnly", exc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnrecognizedAttestationPostureToken_IsRejectedAtLoad()
    {
        var path = CoarseSchema("binding-garbage.json", "public", null, true, "everyone");
        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));
        Assert.Contains("coarseAclAcknowledgedFor", exc.Message, StringComparison.Ordinal);
    }

    // The binding round-trips and exposes the effective posture token, so an
    // operator can read off exactly what string to sign.
    [Fact]
    public void EffectivePostureToken_IsReadableFromTheObject()
    {
        var path = Write("posture-token.json", $$$"""
            {"objectList": [
                {"objectName": "A", "aclMode": "public", "selectedFields": {"Name": "Title"}},
                {"objectName": "B", "aclMode": "group", "aclGroupId": "{{{NarrowGroup}}}",
                 "selectedFields": {"Name": "Title"}},
                {"objectName": "C", "aclMode": "ownerOnly", "selectedFields": {"Name": "Title"}}]}
            """);
        var schema = SchemaConfig.Load(path);

        Assert.Equal("public", schema.FindObject("A")!.CoarseAclPostureToken);
        Assert.Equal($"group:{NarrowGroup}", schema.FindObject("B")!.CoarseAclPostureToken);
        Assert.Equal(string.Empty, schema.FindObject("C")!.CoarseAclPostureToken);
    }

    // ── PROBE 3: dead-letter payloads and column policies ────────────────────

    // Dead-letter request bodies are built from the CONVERTED ExternalItem
    // (Graph/Ingest.cs: item.ToJson()), never from the raw BdhRecord — so a
    // dropped/masked column is already absent from the payload before
    // DEADLETTER_PAYLOAD_MODE is even consulted. This test pins that structural
    // property so a future change that dead-letters the raw record fails here.
    [Fact]
    public void DeadLetterPayloadInFullMode_CarriesNoRestrictedColumnValue()
    {
        using var env = new EnvScope((DeadLetterRedaction.ModeEnvVar, "full"));
        using var state = new SyncStateScope();

        const string comp = "SENTINEL-COMP-482000-GBP";
        const string notes = "SENTINEL-NOTES-severance";
        var objectConfig = new ObjectConfig
        {
            ObjectName = "Opportunity",
            DisplayName = "Opportunity",
            SelectedFields = new Dictionary<string, string>
            {
                ["Name"] = "Title",
                ["Compensation__c"] = "Compensation",
                ["PrivateNotes__c"] = "_bdh_PrivateNotes",
            },
            ColumnPolicies = new Dictionary<string, string>
            {
                ["Compensation__c"] = "drop",
                ["PrivateNotes__c"] = "mask",
            },
        };
        var record = new BdhRecord("Opportunity", new JsonObject
        {
            ["Id"] = IdValue,
            ["Name"] = "Migrate tenant",
            ["Compensation__c"] = comp,
            ["PrivateNotes__c"] = notes,
        });
        var item = new ItemConverter(TestConfig.Make()).Convert(record, objectConfig, new List<AclEntry>());

        // Exactly what Ingest.SendOneAsync dead-letters on a Graph failure.
        SyncState.AppendFailedRecords(
            "BdhHadoopMart",
            new List<(string, string)> { (item.Id, "[Graph] 400 badRequest") },
            "Opportunity",
            new Dictionary<string, JsonNode?> { [item.Id] = item.ToJson() });

        var written = string.Join(
            "\n", Directory.EnumerateFiles(state.LogsDir).Select(File.ReadAllText));

        Assert.False(DeadLetterRedaction.RedactionEnabled);   // full mode really is on
        Assert.Contains(item.Id, written, StringComparison.Ordinal);   // ids are kept, by design
        Assert.DoesNotContain(comp, written, StringComparison.Ordinal);
        Assert.DoesNotContain(notes, written, StringComparison.Ordinal);
        Assert.Contains(ColumnPolicy.MaskMarker, written, StringComparison.Ordinal);
    }
}
