// ConfigLoadRobustnessTests.cs
// ----------------------------
// Two defects found verifying the WP-HD-2 / WP-HD-3 controls, both in the same
// place: SchemaConfig.Validate.
//
//   1. CRASH. "coarseAclAcknowledgedFor": null is valid JSON and deserializes
//      the property to null, which SchemaConfig dereferenced unguarded — in TWO
//      places, ValidateAttestationBinding and HasBoundCoarseAclAttestation. The
//      config load died with a NullReferenceException instead of the clear,
//      actionable message every other attestation mistake produces.
//
//      A JSON null is how JSON writes "absent", and an ABSENT binding is
//      deliberately not a load error (older configs still parse) but does not
//      satisfy --strict. null therefore behaves exactly like absent, empty and
//      whitespace-only: it loads, it is NOT accepted as an attestation, and
//      --strict fails for that object with the "unbound sign-off" message that
//      names the posture token to record.
//
//   2. MIS-REPORT. A policed column whose NAME is the Graph property a DIFFERENT
//      column maps to — selectedFields {"Id": "RecordId", "RecordId": "Other"}
//      with columnPolicies {"RecordId": "drop"} — loaded cleanly and preflight
//      printed "dropped=[RecordId]", while a populated property called RecordId
//      went on reaching the index from column Id. The policy is real but the
//      report is not readable as true, which is the same silent-failure mode the
//      identity-column and unselected-column rejections exist to stop. Rejected
//      at load.

using HadoopConnector.Commands;
using HadoopConnector.Config;

namespace HadoopConnector.Tests;

public class ConfigLoadRobustnessTests : IDisposable
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
         {"name": "Other", "type": "String", "isSearchable": false},
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

    // ── DEFECT 1: a null attestation binding must not crash the load ─────────

    private const string GroupId = "11111111-2222-3333-4444-555555555555";

    /// <summary>A coarse (public) object whose binding is written verbatim into
    /// the JSON — so the test can supply the literal <c>null</c>, which is the
    /// shape that crashed, as opposed to the C# null that means "omit the key".
    /// </summary>
    private string BindingSchema(string name, string bindingJson, bool acknowledged) => Write(
        name, $$"""
        {"objectList": [{
            "objectName": "Contact",
            "displayName": "Contact",
            "aclMode": "public",
            {{(acknowledged ? "\"coarseAclAcknowledged\": true," : string.Empty)}}
            "coarseAclAcknowledgedFor": {{bindingJson}},
            "selectedFields": {"Name": "Title"}
        }]}
        """);

    // THE crash. A JSON null is a legal value for the key and must produce a
    // validation outcome, never a NullReferenceException.
    [Fact]
    public void NullAttestationBinding_DoesNotThrowNullReferenceAtLoad()
    {
        var path = BindingSchema("binding-null.json", "null", acknowledged: true);

        var exc = Record.Exception(() => SchemaConfig.Load(path));

        Assert.IsNotType<NullReferenceException>(exc);
        Assert.Null(exc);   // …and, like an absent binding, it is not a load error at all
    }

    // null / "" / "   " are all "no posture was recorded". They load (an absent
    // binding is deliberately not a load error) and reach the SAME unbound state,
    // so no reader has to special-case one of the three. The stored value is left
    // verbatim — every consumer trims — but it is never null, which is the
    // property that keeps the deref sites safe.
    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("\"\\t \"")]
    public void EmptyEquivalentAttestationBindings_LoadAndAreTreatedAsUnbound(string bindingJson)
    {
        var path = BindingSchema(
            $"binding-empty-{Guid.NewGuid():N}.json", bindingJson, acknowledged: true);

        var obj = SchemaConfig.Load(path).FindObject("Contact")!;

        Assert.NotNull(obj.CoarseAclAcknowledgedFor);
        Assert.True(string.IsNullOrWhiteSpace(obj.CoarseAclAcknowledgedFor));
        Assert.True(obj.CoarseAclAcknowledged);
        // Not an attestation: reading this must not throw either (the second,
        // separate deref site that a call-site-only guard would have left live).
        Assert.False(obj.HasBoundCoarseAclAttestation);
    }

    // A JSON null specifically — the shape that crashed — is normalised to empty
    // on the way in, so the property is never null for any later reader.
    [Fact]
    public void NullAttestationBinding_IsNormalisedToEmptyRatherThanLeftNull()
    {
        var path = BindingSchema("binding-null-normalised.json", "null", acknowledged: true);

        var obj = SchemaConfig.Load(path).FindObject("Contact")!;

        Assert.Equal(string.Empty, obj.CoarseAclAcknowledgedFor);
    }

    // The point of the fix: instead of a stack trace, the operator gets the SAME
    // actionable message the already-handled unbound case produces, naming the
    // posture token to record.
    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void EmptyEquivalentAttestationBinding_FailsStrictWithTheUnboundMessage(string bindingJson)
    {
        using var scope = GoodEnv();
        var path = BindingSchema(
            $"binding-strict-{Guid.NewGuid():N}.json", bindingJson, acknowledged: true);

        var result = ValidateConfig.ValidateCore(path, GraphSchemaPath, FiltersPath, strict: true);

        var error = Assert.Single(result.Errors,
            e => e.Contains("coarse aclMode", StringComparison.Ordinal));
        Assert.Contains("coarseAclAcknowledgedFor", error, StringComparison.Ordinal);
        Assert.Contains("UNBOUND", error, StringComparison.Ordinal);
        Assert.Contains("\"public\"", error, StringComparison.Ordinal);   // the token to record
        Assert.False(result.Ok(strict: true));
        Assert.Empty(result.AcknowledgedWarnings);
        // The schema itself loaded — the finding is about posture, not syntax.
        Assert.DoesNotContain(result.Errors,
            e => e.Contains("schema.json invalid", StringComparison.Ordinal));
    }

    // A null binding with no sign-off is just an object with no attestation, and
    // must read as such rather than as a half-deleted one.
    [Fact]
    public void NullAttestationBinding_WithoutTheFlag_ReadsAsNoSignOffRecorded()
    {
        using var scope = GoodEnv();
        var path = BindingSchema("binding-null-noflag.json", "null", acknowledged: false);

        var result = ValidateConfig.ValidateCore(path, GraphSchemaPath, FiltersPath, strict: true);

        var error = Assert.Single(result.Errors,
            e => e.Contains("coarse aclMode", StringComparison.Ordinal));
        Assert.Contains("No sign-off recorded", error, StringComparison.Ordinal);
        Assert.False(result.Ok(strict: true));
    }

    // …and on an ownerOnly object a null binding is not a leftover binding, so
    // the ownerOnly rejection must NOT fire: it is indistinguishable from the key
    // being absent, which loads cleanly today.
    [Fact]
    public void NullAttestationBinding_OnAnOwnerOnlyObject_LoadsCleanly()
    {
        var path = Write("binding-null-owneronly.json", """
            {"objectList": [{
                "objectName": "Contact",
                "aclMode": "ownerOnly",
                "coarseAclAcknowledgedFor": null,
                "selectedFields": {"Name": "Title"}
            }]}
            """);

        var obj = SchemaConfig.Load(path).FindObject("Contact")!;

        Assert.Equal(string.Empty, obj.CoarseAclAcknowledgedFor);
        Assert.False(obj.IsCoarseAcl);
        Assert.False(obj.HasBoundCoarseAclAttestation);
    }

    // The fourth case the fix has to keep working: a VALID token still binds,
    // still passes --strict, and is still reported as an accepted exposure.
    [Fact]
    public void ValidAttestationBinding_StillBindsAndPassesStrict()
    {
        using var scope = GoodEnv();
        var path = BindingSchema("binding-valid.json", "\"public\"", acknowledged: true);

        var obj = SchemaConfig.Load(path).FindObject("Contact")!;
        Assert.True(obj.HasBoundCoarseAclAttestation);
        Assert.Equal("public", obj.CoarseAclPostureToken);

        var result = ValidateConfig.ValidateCore(path, GraphSchemaPath, FiltersPath, strict: true);
        Assert.Contains(result.AcknowledgedWarnings,
            w => w.Contains("Contact", StringComparison.Ordinal));
        Assert.True(result.Ok(strict: true));
    }

    // A token with incidental surrounding whitespace is the posture, not a typo —
    // it must still bind rather than be demoted to unbound by the null guard.
    [Fact]
    public void AttestationBindingWithSurroundingWhitespace_StillBinds()
    {
        using var scope = GoodEnv();
        var path = Write("binding-padded.json", $$"""
            {"objectList": [{
                "objectName": "Contact",
                "aclMode": "group",
                "aclGroupId": "{{GroupId}}",
                "coarseAclAcknowledged": true,
                "coarseAclAcknowledgedFor": "  group:{{GroupId}}  ",
                "selectedFields": {"Name": "Title"}
            }]}
            """);

        var obj = SchemaConfig.Load(path).FindObject("Contact")!;
        Assert.True(obj.HasBoundCoarseAclAttestation);

        var result = ValidateConfig.ValidateCore(path, GraphSchemaPath, FiltersPath, strict: true);
        Assert.True(result.Ok(strict: true));
    }

    // A garbage (non-empty) token is still a hard load error — the null guard
    // must not have widened the "treat as unbound" path to swallow real mistakes.
    [Fact]
    public void NonEmptyGarbageAttestationBinding_IsStillRejectedAtLoad()
    {
        var path = BindingSchema("binding-garbage.json", "\"everyone\"", acknowledged: true);

        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));
        Assert.Contains("coarseAclAcknowledgedFor", exc.Message, StringComparison.Ordinal);
        Assert.Contains("everyone", exc.Message, StringComparison.Ordinal);
    }

    // ── DEFECT 2: a policed column named after another column's property ─────

    /// <summary>The exact reported shape: column <c>Id</c> maps to Graph property
    /// <c>RecordId</c>, and a SECOND column is literally called
    /// <c>RecordId</c>.</summary>
    private string CollisionSchema(string name, string policedColumn, string action) => Write(
        name, $$"""
        {"objectList": [{
            "objectName": "Opportunity",
            "displayName": "Opportunity",
            "aclMode": "ownerOnly",
            "selectedFields": {"Id": "RecordId", "RecordId": "Other", "Name": "Title"},
            "columnPolicies": {"{{policedColumn}}": "{{action}}"}
        }]}
        """);

    [Theory]
    [InlineData("drop")]
    [InlineData("mask")]
    public void PolicyOnAColumnNamedAfterAnotherColumnsGraphProperty_IsRejectedAtLoad(string action)
    {
        var path = CollisionSchema($"collision-{action}.json", "RecordId", action);

        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));

        Assert.Contains("Opportunity", exc.Message, StringComparison.Ordinal);
        Assert.Contains("RecordId", exc.Message, StringComparison.Ordinal);
        // The message must name the OTHER column, or the operator cannot see
        // which mapping to rename.
        Assert.Contains("'Id'", exc.Message, StringComparison.Ordinal);
        Assert.Contains("selectedFields", exc.Message, StringComparison.Ordinal);
    }

    // Graph property names and BDH column names both match case-insensitively
    // elsewhere in this file, so a casing difference must not side-step the
    // rejection — otherwise it is defeated by one letter.
    [Theory]
    [InlineData("recordid")]
    [InlineData("RECORDID")]
    public void PropertyNameCollision_IsRejectedRegardlessOfCase(string policedColumn)
    {
        var path = Write($"collision-case-{policedColumn}.json", $$"""
            {"objectList": [{
                "objectName": "Opportunity",
                "selectedFields": {"Id": "RecordId", "{{policedColumn}}": "Other", "Name": "Title"},
                "columnPolicies": {"{{policedColumn}}": "drop"}
            }]}
            """);

        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));
        Assert.Contains("Graph property", exc.Message, StringComparison.OrdinalIgnoreCase);
    }

    // THE headline claim, mirroring ValidateConfig_NeverReportsTheIdentityColumnAsDropped:
    // preflight must never print a restriction whose report is not readable as
    // true. With the load rejection it is an ERROR, not "1 dropped".
    [Fact]
    public void ValidateConfig_NeverReportsACollidingColumnAsDropped()
    {
        using var scope = GoodEnv();
        var path = CollisionSchema("collision-validate.json", "RecordId", "drop");

        var result = ValidateConfig.ValidateCore(path, GraphSchemaPath, FiltersPath, strict: true);

        Assert.Contains(result.Errors, e => e.Contains("schema.json invalid", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Notices, n => n.Contains("1 dropped", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Notices,
            n => n.Contains("dropped=[RecordId]", StringComparison.Ordinal));
        Assert.False(result.Ok(strict: true));
    }

    // ── DEFECT 2: the cases that must KEEP loading ──────────────────────────

    // A column mapped to a property of its own name is the ordinary case, not a
    // collision — the report names one thing and one thing only.
    [Fact]
    public void PolicyOnAColumnMappedToAPropertyOfItsOwnName_StillLoads()
    {
        var path = Write("collision-self.json", """
            {"objectList": [{
                "objectName": "Opportunity",
                "selectedFields": {"RecordId": "RecordId", "Name": "Title"},
                "columnPolicies": {"RecordId": "drop"}
            }]}
            """);

        var obj = SchemaConfig.Load(path).FindObject("Opportunity")!;
        Assert.Equal(ColumnPolicyAction.Drop, obj.PolicyFor("RecordId"));
    }

    // …including when the policy key differs in case from the selectedFields key.
    [Fact]
    public void SelfMappedColumnPoliciedByADifferentCase_StillLoads()
    {
        var path = Write("collision-self-case.json", """
            {"objectList": [{
                "objectName": "Opportunity",
                "selectedFields": {"RecordId": "RecordId", "Name": "Title"},
                "columnPolicies": {"recordid": "mask"}
            }]}
            """);

        var obj = SchemaConfig.Load(path).FindObject("Opportunity")!;
        Assert.Equal(ColumnPolicyAction.Mask, obj.PolicyFor("RecordId"));
    }

    // When the colliding property is ITSELF dropped it never reaches the index,
    // so "dropped=[RecordId]" is accurate and there is nothing to reject. The
    // rejection is about a report that misleads, not about the names alone.
    [Fact]
    public void CollidingPropertyWhoseSourceColumnIsAlsoDropped_StillLoads()
    {
        var path = Write("collision-both-dropped.json", """
            {"objectList": [{
                "objectName": "Opportunity",
                "selectedFields": {"Alias__c": "RecordId", "RecordId": "Other", "Name": "Title"},
                "columnPolicies": {"Alias__c": "drop", "RecordId": "drop"}
            }]}
            """);

        var obj = SchemaConfig.Load(path).FindObject("Opportunity")!;
        Assert.Equal(2, obj.DroppedColumnCount);
    }

    // …but MASKING the source column still emits a property of that name (with
    // the marker), so the report is still misleading and it is still rejected.
    [Fact]
    public void CollidingPropertyWhoseSourceColumnIsMerelyMasked_IsRejectedAtLoad()
    {
        var path = Write("collision-source-masked.json", """
            {"objectList": [{
                "objectName": "Opportunity",
                "selectedFields": {"Alias__c": "RecordId", "RecordId": "Other", "Name": "Title"},
                "columnPolicies": {"Alias__c": "mask", "RecordId": "drop"}
            }]}
            """);

        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));
        Assert.Contains("RecordId", exc.Message, StringComparison.Ordinal);
    }

    // A _bdh_ placeholder is NOT a Graph property name — it is a marker meaning
    // "route this column into the content body under its own COLUMN name", so it
    // can never collide with anything and must not trigger a false rejection.
    [Fact]
    public void PolicedColumnNamedAfterABdhPlaceholder_StillLoads()
    {
        var path = Write("collision-bdh.json", """
            {"objectList": [{
                "objectName": "Opportunity",
                "selectedFields": {"Notes__c": "_bdh_Notes", "_bdh_Notes": "Other", "Name": "Title"},
                "columnPolicies": {"_bdh_Notes": "drop"}
            }]}
            """);

        var obj = SchemaConfig.Load(path).FindObject("Opportunity")!;
        Assert.Equal(ColumnPolicyAction.Drop, obj.PolicyFor("_bdh_Notes"));
    }

    // An ordinary policy on a schema that happens to contain an Id → RecordId
    // mapping (the shipped shape) is untouched by the new rejection.
    [Fact]
    public void OrdinaryPolicyAlongsideAnIdMapping_StillLoads()
    {
        var path = Write("collision-control.json", """
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

    // A collision with NO policy on the colliding column is out of scope: the
    // rejection is about a policy report, and rejecting it would break schemas
    // that never claimed a restriction in the first place.
    [Fact]
    public void PropertyNameCollisionWithNoPolicyOnThatColumn_StillLoads()
    {
        var path = Write("collision-nopolicy.json", """
            {"objectList": [{
                "objectName": "Opportunity",
                "selectedFields": {"Id": "RecordId", "RecordId": "Other", "Comp__c": "Comp"},
                "columnPolicies": {"Comp__c": "drop"}
            }]}
            """);

        var obj = SchemaConfig.Load(path).FindObject("Opportunity")!;
        Assert.Equal(ColumnPolicyAction.Drop, obj.PolicyFor("Comp__c"));
        Assert.Equal(ColumnPolicyAction.None, obj.PolicyFor("RecordId"));
    }

    // The identity-column rejection is the more specific finding and must keep
    // winning when both apply, so the operator is told the structural reason
    // rather than a naming one.
    [Fact]
    public void PolicyOnTheIdentityColumn_StillReportsTheIdentityReason()
    {
        var path = CollisionSchema("collision-identity.json", "Id", "drop");

        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));
        Assert.Contains("externalItem id", exc.Message, StringComparison.OrdinalIgnoreCase);
    }
}
