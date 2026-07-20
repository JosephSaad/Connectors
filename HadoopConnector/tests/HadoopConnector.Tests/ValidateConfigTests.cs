using HadoopConnector.Commands;
using HadoopConnector.Config;

namespace HadoopConnector.Tests;

public class ValidateConfigTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private EnvScope GoodEnv() => new(
        ("CONNECTOR_ID", "BdhHadoopMart"),
        ("AAD_APP_TENANT_ID", "tenant"),
        ("AAD_APP_CLIENT_ID", "client"),
        ("SECRET_AAD_APP_CLIENT_SECRET", "secret"),
        ("HDFS_MODE", "webhdfs"),
        ("HDFS_NAMENODE_URL", "http://namenode.example:9870/webhdfs/v1"),
        ("BDH_EXPORT_PATH", null),
        ("BDH_ROOT_PATH", null),
        ("BDH_FILTERS_PATH", null),
        ("BDH_MAX_RECORDS_PER_OBJECT", null),
        ("BDH_LAG_HOURS", null),
        ("ALLOW_FULL_SCAN", null),
        ("USE_KEY_VAULT", null),
        ("USE_SQL_SERVER", null),
        ("SQL_CONNECTION_STRING", null),
        ("HA_MODE", null),
        ("LOG_FORMAT", null),
        (ShardingConfig.EnvVar, null));

    // ── minimal-but-valid config fixtures ────────────────────────────────────

    private string SchemaPath => Write("schema.json", """
        {"objectList": [{
            "objectName": "Contact",
            "displayName": "Contact",
            "aclMode": "ownerOnly",
            "selectedFields": {"Name": "Title", "Description": "_bdh_Description"}
        }]}
        """);

    // Declares the mapped property plus the ALWAYS-EMITTED set: since the
    // produced-vs-declared cross-check, a graph-schema.json that omits a
    // property the connector emits (including the classifier pair, flag on or
    // off) is a preflight ERROR, so a green-path fixture must be complete.
    private string GraphSchemaPath => Write("graph-schema.json", """
        [{"name": "Title", "type": "String", "isSearchable": true},
         {"name": "ObjectName", "type": "String"},
         {"name": "Url", "type": "String"},
         {"name": "IconUrl", "type": "String"},
         {"name": "SourceSystem", "type": "String"},
         {"name": "DataAsOf", "type": "String"},
         {"name": "SensitivityLabel", "type": "String"},
         {"name": "DetectedCategories", "type": "StringCollection"}]
        """);

    private string FiltersPath => Write("filters.json", """
        {"objects": {"Contact": {"partition": [{"key": "dt", "op": "withinLastDays", "value": "30"}]}},
         "fullScanAllowed": []}
        """);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir.Path, name);
        File.WriteAllText(path, content);
        return path;
    }

    private const string AclSchemaGroupId = "11111111-2222-3333-4444-555555555555";

    /// <summary>A one-object schema with a chosen aclMode / attestation, for the
    /// coarse-ACL posture checks (WP-HD-2).
    /// <para>
    /// <paramref name="acknowledged"/>=true emits a BOUND attestation — the
    /// sign-off plus the posture token it covers (coarseAclAcknowledgedFor).
    /// That binding is what an attestation IS: an unbound bool records only that
    /// someone signed, so it survives a later widening of the very exposure it
    /// was meant to cover and is no longer accepted (see
    /// RestrictionBypassProbeTests).
    /// </para></summary>
    private string AclSchema(string aclMode, bool? acknowledged = null, string name = "acl-schema")
    {
        var group = aclMode == "group"
            ? $"\"aclGroupId\": \"{AclSchemaGroupId}\", "
            : string.Empty;
        var postureToken = aclMode == "group" ? $"group:{AclSchemaGroupId}" : aclMode;
        var ack = acknowledged is null
            ? string.Empty
            : $"\"coarseAclAcknowledged\": {(acknowledged.Value ? "true" : "false")}, "
              + (acknowledged.Value ? $"\"coarseAclAcknowledgedFor\": \"{postureToken}\", " : string.Empty);
        return Write($"{name}-{aclMode}-{acknowledged?.ToString() ?? "unset"}.json", $$"""
            {"objectList": [{
                "objectName": "Contact",
                "displayName": "Contact",
                "aclMode": "{{aclMode}}", {{group}}{{ack}}
                "selectedFields": {"Name": "Title", "Description": "_bdh_Description"}
            }]}
            """);
    }

    /// <summary>filters.json in which Contact is NOT effectively filtered but is
    /// exempt from the fail-closed scale guard — isolates the coarse-ACL finding
    /// from the unrelated full-scan finding.</summary>
    private string UnfilteredExemptFiltersPath => Write("unfiltered-exempt-filters.json",
        """{"objects": {}, "fullScanAllowed": ["Contact"]}""");

    private static string RepoConfig(string file) =>
        Path.Combine(AppContext.BaseDirectory, "config", file);

    // ── env vars / connector id ──────────────────────────────────────────────

    [Fact]
    public void GoodConfig_PassesStrict()
    {
        using var scope = GoodEnv();
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath, FiltersPath, strict: true);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.True(result.Ok(strict: true));
    }

    [Fact]
    public void MissingRequiredVars_AreErrors()
    {
        using var scope = GoodEnv();
        scope.Set("AAD_APP_TENANT_ID", null);
        scope.Set("SECRET_AAD_APP_CLIENT_SECRET", null);
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath, FiltersPath);
        Assert.Contains(result.Errors, e => e.Contains("AAD_APP_TENANT_ID"));
        Assert.Contains(result.Errors, e => e.Contains("SECRET_AAD_APP_CLIENT_SECRET"));
        Assert.False(result.Ok(strict: false));
    }

    [Theory]
    [InlineData("ab")]                       // too short
    [InlineData("has spaces")]               // invalid chars
    [InlineData("Bad-Punctuation")]          // invalid chars
    [InlineData("MicrosoftBdhMart")]         // reserved prefix
    [InlineData("SharePointFeed")]           // reserved prefix
    public void InvalidConnectorId_IsError(string connectorId)
    {
        using var scope = GoodEnv();
        scope.Set("CONNECTOR_ID", connectorId);
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath, FiltersPath);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void ValidConnectorId_HelperAgrees()
    {
        Assert.Null(AppConfig.ValidateConnectorId("BdhHadoopMart"));
        Assert.NotNull(AppConfig.ValidateConnectorId("Microsoft123"));
        Assert.NotNull(AppConfig.ValidateConnectorId("x"));
    }

    // ── HDFS_MODE consistency ────────────────────────────────────────────────

    [Fact]
    public void InvalidHdfsMode_IsError()
    {
        using var scope = GoodEnv();
        scope.Set("HDFS_MODE", "ftp");
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath, FiltersPath);
        Assert.Contains(result.Errors, e => e.Contains("HDFS_MODE"));
    }

    [Fact]
    public void Webhdfs_WithoutNamenodeUrl_IsError()
    {
        using var scope = GoodEnv();
        scope.Set("HDFS_NAMENODE_URL", null);
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath, FiltersPath);
        Assert.Contains(result.Errors, e => e.Contains("HDFS_NAMENODE_URL"));
    }

    [Fact]
    public void Localpath_WithoutExportPath_IsError()
    {
        using var scope = GoodEnv();
        scope.Set("HDFS_MODE", "localpath");
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath, FiltersPath);
        Assert.Contains(result.Errors, e => e.Contains("BDH_EXPORT_PATH"));
    }

    [Fact]
    public void Localpath_MissingDirectory_IsWarningOnly()
    {
        using var scope = GoodEnv();
        scope.Set("HDFS_MODE", "localpath");
        scope.Set("BDH_EXPORT_PATH", "/nonexistent/bdh/exports");
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath, FiltersPath);
        Assert.Empty(result.Errors);
        Assert.Contains(result.Warnings, w => w.Contains("BDH_EXPORT_PATH"));
        Assert.True(result.Ok(strict: false));
        Assert.False(result.Ok(strict: true));  // strict fails on warnings
    }

    [Fact]
    public void Localpath_ExistingDirectory_Passes()
    {
        using var scope = GoodEnv();
        scope.Set("HDFS_MODE", "localpath");
        scope.Set("HDFS_NAMENODE_URL", null);   // not required in localpath mode
        scope.Set("BDH_EXPORT_PATH", _dir.Path);
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath, FiltersPath);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    // ── filters.json (THE scale control) ─────────────────────────────────────

    [Fact]
    public void MalformedFilters_IsError_NeverIgnored()
    {
        using var scope = GoodEnv();
        var broken = Write("broken-filters.json", "{not json");
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath, broken);
        Assert.Contains(result.Errors, e => e.Contains("filters.json invalid"));
    }

    [Fact]
    public void MissingFiltersFile_IsWarning()
    {
        using var scope = GoodEnv();
        var result = ValidateConfig.ValidateCore(
            SchemaPath, GraphSchemaPath, Path.Combine(_dir.Path, "no-such-filters.json"));
        Assert.Contains(result.Warnings, w => w.Contains("Missing filter config"));
    }

    [Fact]
    public void UnfilteredObject_Warns_AndErrorsUnderStrict()
    {
        using var scope = GoodEnv();
        var noFilters = Write("empty-filters.json", """{"objects": {}, "fullScanAllowed": []}""");

        var lax = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath, noFilters);
        Assert.Empty(lax.Errors);
        Assert.Contains(lax.Warnings, w => w.Contains("Contact") && w.Contains("NO filter"));

        var strict = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath, noFilters, strict: true);
        Assert.Contains(strict.Errors, e => e.Contains("Contact") && e.Contains("NO filter"));
    }

    // Regression (MEDIUM-3): an object whose ONLY filter is a partition predicate
    // on a NON-dt key prunes nothing (MatchesPartition skips absent keys), so it
    // must be treated as unfiltered — a warning, and an error under --strict.
    [Fact]
    public void NonPruningPartitionOnlyFilter_Warns_AndErrorsUnderStrict()
    {
        using var scope = GoodEnv();
        var inert = Write("inert-filters.json", """
            {"objects": {"Contact": {"partition": [{"key": "region", "op": "equals", "value": "EMEA"}]}},
             "fullScanAllowed": []}
            """);

        var lax = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath, inert);
        Assert.Empty(lax.Errors);
        Assert.Contains(lax.Warnings, w => w.Contains("Contact") && w.Contains("non-pruning"));

        var strict = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath, inert, strict: true);
        Assert.Contains(strict.Errors, e => e.Contains("Contact") && e.Contains("non-pruning"));
    }

    // Regression (LOW-8): a numeric operator with a date operand is a config
    // error — validate-config (and --strict) must reject it, not let a filter
    // that silently prunes everything ship.
    [Fact]
    public void NumericOpWithDateOperand_FailsValidation()
    {
        using var scope = GoodEnv();
        var badFilters = Write("bad-numeric-date-filters.json", """
            {"objects": {"Contact": {"allOf": [{"field": "CloseDate", "op": "gte", "value": "2026-01-01"}]}},
             "fullScanAllowed": []}
            """);
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath, badFilters, strict: true);
        Assert.Contains(result.Errors, e => e.Contains("filters.json invalid") && e.Contains("date-looking"));
        Assert.False(result.Ok(strict: true));
    }

    // A dt partition predicate DOES prune (dt is always present) → effectively
    // filtered, so it passes even under --strict.
    [Fact]
    public void DtPartitionOnlyFilter_PassesStrict()
    {
        using var scope = GoodEnv();
        var dtOnly = Write("dt-filters.json", """
            {"objects": {"Contact": {"partition": [{"key": "dt", "op": "withinLastDays", "value": "30"}]}},
             "fullScanAllowed": []}
            """);
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath, dtOnly, strict: true);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("Contact"));
        Assert.DoesNotContain(result.Errors, e => e.Contains("Contact"));
    }

    [Fact]
    public void UnfilteredObject_ExemptViaFullScanAllowed()
    {
        using var scope = GoodEnv();
        var exempt = Write("exempt-filters.json",
            """{"objects": {}, "fullScanAllowed": ["Contact"]}""");
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath, exempt, strict: true);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void UnfilteredObject_ExemptViaAllowFullScanEnv()
    {
        using var scope = GoodEnv();
        scope.Set("ALLOW_FULL_SCAN", "true");
        var noFilters = Write("empty-filters2.json", """{"objects": {}, "fullScanAllowed": []}""");
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath, noFilters, strict: true);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    // ── coarse-ACL posture (WP-HD-2, INTERIM control pending Ranger) ─────────
    //
    // aclMode 'public' grants everyoneExceptGuests and 'group' grants ONE flat
    // Entra group — neither can express the row/column restrictions the source
    // enforces. The posture must be VISIBLE (always warned) and explicitly
    // ATTESTED (coarseAclAcknowledged) before --strict will pass.

    [Theory]
    [InlineData("group")]
    [InlineData("public")]
    public void CoarseAclMode_Warns(string aclMode)
    {
        using var scope = GoodEnv();
        var result = ValidateConfig.ValidateCore(
            AclSchema(aclMode), GraphSchemaPath, FiltersPath);

        Assert.Contains(result.Warnings,
            w => w.Contains("Contact") && w.Contains($"coarse aclMode '{aclMode}'"));
    }

    [Fact]
    public void CoarseAclWarning_NamesTheEffectiveFilteredState()
    {
        using var scope = GoodEnv();
        // FiltersPath gives Contact a pruning dt predicate → effectively filtered.
        var filtered = ValidateConfig.ValidateCore(
            AclSchema("public"), GraphSchemaPath, FiltersPath);
        Assert.Contains(filtered.Warnings, w => w.Contains("is effectively filtered"));

        var unfiltered = ValidateConfig.ValidateCore(
            AclSchema("public"), GraphSchemaPath, UnfilteredExemptFiltersPath);
        Assert.Contains(unfiltered.Warnings, w => w.Contains("is ALSO NOT effectively filtered"));
    }

    // The coarse+unfiltered case is the worst case: the whole object (150M+ rows)
    // is exposed at a coarse grant. Its message must be materially stronger than
    // the coarse+filtered one, not merely a flipped adjective.
    [Fact]
    public void CoarseAclUnfiltered_MessageIsMateriallyStronger()
    {
        using var scope = GoodEnv();
        var filtered = ValidateConfig.ValidateCore(
            AclSchema("public"), GraphSchemaPath, FiltersPath)
            .Warnings.Single(w => w.Contains("coarse aclMode"));
        var unfiltered = ValidateConfig.ValidateCore(
            AclSchema("public"), GraphSchemaPath, UnfilteredExemptFiltersPath)
            .Warnings.Single(w => w.Contains("coarse aclMode"));

        Assert.Contains("WORST CASE", unfiltered);
        Assert.DoesNotContain("WORST CASE", filtered);
        Assert.Contains("ENTIRE object", unfiltered);
        Assert.True(unfiltered.Length > filtered.Length,
            "the coarse+unfiltered message must say strictly more than coarse+filtered");
    }

    [Theory]
    [InlineData("group")]
    [InlineData("public")]
    public void CoarseAcl_WithoutAttestation_FailsStrict(string aclMode)
    {
        using var scope = GoodEnv();
        var result = ValidateConfig.ValidateCore(
            AclSchema(aclMode), GraphSchemaPath, FiltersPath, strict: true);

        Assert.Contains(result.Errors,
            e => e.Contains("Contact") && e.Contains($"coarse aclMode '{aclMode}'"));
        Assert.False(result.Ok(strict: true));
    }

    // An explicit false is not an attestation — it must behave exactly like an
    // absent property, so "we looked and said no" cannot be mistaken for sign-off.
    [Fact]
    public void CoarseAcl_AttestationExplicitlyFalse_FailsStrict()
    {
        using var scope = GoodEnv();
        var result = ValidateConfig.ValidateCore(
            AclSchema("group", acknowledged: false), GraphSchemaPath, FiltersPath, strict: true);
        Assert.Contains(result.Errors, e => e.Contains("coarse aclMode 'group'"));
    }

    [Theory]
    [InlineData("group")]
    [InlineData("public")]
    public void CoarseAcl_WithAttestation_PassesStrict_ButStillWarns(string aclMode)
    {
        using var scope = GoodEnv();
        var result = ValidateConfig.ValidateCore(
            AclSchema(aclMode, acknowledged: true), GraphSchemaPath, FiltersPath, strict: true);

        // The exposure does NOT disappear: it is still reported, every run.
        Assert.Contains(result.AcknowledgedWarnings,
            w => w.Contains("Contact") && w.Contains($"coarse aclMode '{aclMode}'"));
        Assert.Contains(result.AcknowledgedWarnings,
            w => w.Contains("ACKNOWLEDGED in schema.json") && w.Contains("not removed or reduced"));
        // …it just no longer blocks the gate.
        Assert.DoesNotContain(result.Errors, e => e.Contains("coarse aclMode"));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("coarse aclMode"));
        Assert.True(result.Ok(strict: true));
    }

    // Attesting the coarse posture says nothing about the scale guard: an
    // attested-but-unfiltered object must still trip the full-scan guard.
    [Fact]
    public void CoarseAclAttestation_DoesNotSuppressTheFullScanGuard()
    {
        using var scope = GoodEnv();
        var noFilters = Write("coarse-nofilters.json", """{"objects": {}, "fullScanAllowed": []}""");
        var result = ValidateConfig.ValidateCore(
            AclSchema("public", acknowledged: true), GraphSchemaPath, noFilters, strict: true);

        Assert.Contains(result.Errors, e => e.Contains("Contact") && e.Contains("NO filter"));
        Assert.False(result.Ok(strict: true));
    }

    [Fact]
    public void OwnerOnlyObject_ProducesNoCoarseAclFinding()
    {
        using var scope = GoodEnv();
        var result = ValidateConfig.ValidateCore(
            AclSchema("ownerOnly"), GraphSchemaPath, FiltersPath, strict: true);

        Assert.DoesNotContain(result.Warnings, w => w.Contains("coarse aclMode"));
        Assert.DoesNotContain(result.Errors, e => e.Contains("coarse aclMode"));
        Assert.Empty(result.AcknowledgedWarnings);
        Assert.True(result.Ok(strict: true));
    }

    // ── coarseAclAcknowledged in SchemaConfig ────────────────────────────────

    [Fact]
    public void CoarseAclAcknowledged_RoundTripsThroughLoad()
    {
        var path = Write("ack-roundtrip.json", """
            {"objectList": [
                {"objectName": "A", "aclMode": "public", "coarseAclAcknowledged": true,
                 "selectedFields": {"Name": "Title"}},
                {"objectName": "B", "aclMode": "group", "aclGroupId": "g-1",
                 "coarseAclAcknowledged": false, "selectedFields": {"Name": "Title"}},
                {"objectName": "C", "aclMode": "group", "aclGroupId": "g-2",
                 "selectedFields": {"Name": "Title"}}]}
            """);
        var schema = SchemaConfig.Load(path);

        Assert.True(schema.FindObject("A")!.CoarseAclAcknowledged);
        Assert.False(schema.FindObject("B")!.CoarseAclAcknowledged);
        Assert.False(schema.FindObject("C")!.CoarseAclAcknowledged);   // absent → false
        Assert.True(schema.FindObject("A")!.IsCoarseAcl);
        Assert.True(schema.FindObject("C")!.IsCoarseAcl);
    }

    // An attestation on an ownerOnly object attests to nothing today, and
    // silently PRE-APPROVES the exposure if someone later flips that object to
    // group/public — the exact review this control exists to force. Reject it.
    [Fact]
    public void CoarseAclAcknowledged_OnOwnerOnlyObject_IsRejected()
    {
        var path = Write("ack-owneronly.json", """
            {"objectList": [{"objectName": "X", "aclMode": "ownerOnly",
                             "coarseAclAcknowledged": true,
                             "selectedFields": {"Name": "Title"}}]}
            """);
        var exc = Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));
        Assert.Contains("coarseAclAcknowledged", exc.Message);
        Assert.Contains("ownerOnly", exc.Message);
    }

    [Fact]
    public void CoarseAclAcknowledgedFalse_OnOwnerOnlyObject_IsAccepted()
    {
        var path = Write("ack-owneronly-false.json", """
            {"objectList": [{"objectName": "X", "aclMode": "ownerOnly",
                             "coarseAclAcknowledged": false,
                             "selectedFields": {"Name": "Title"}}]}
            """);
        var schema = SchemaConfig.Load(path);
        Assert.False(schema.FindObject("X")!.IsCoarseAcl);
    }

    // ── scale / freshness knobs ──────────────────────────────────────────────

    [Fact]
    public void RowCapDisabled_IsWarning()
    {
        using var scope = GoodEnv();
        scope.Set("BDH_MAX_RECORDS_PER_OBJECT", "0");
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath, FiltersPath);
        Assert.Contains(result.Warnings, w => w.Contains("BDH_MAX_RECORDS_PER_OBJECT"));
    }

    [Fact]
    public void LagBelowNightlySync_IsWarning()
    {
        using var scope = GoodEnv();
        scope.Set("BDH_LAG_HOURS", "12");
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath, FiltersPath);
        Assert.Contains(result.Warnings, w => w.Contains("BDH_LAG_HOURS"));
    }

    // ── cross-flag consistency ───────────────────────────────────────────────

    [Fact]
    public void HaWithoutSql_IsError()
    {
        using var scope = GoodEnv();
        scope.Set("HA_MODE", "true");
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath, FiltersPath);
        Assert.Contains(result.Errors, e => e.Contains("HA_MODE"));

        scope.Set("USE_SQL_SERVER", "true");
        scope.Set("SQL_CONNECTION_STRING", "Server=x;Database=y");
        var fixedResult = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath, FiltersPath);
        Assert.DoesNotContain(fixedResult.Errors, e => e.Contains("HA_MODE"));
    }

    [Fact]
    public void KeyVaultWithoutUri_IsError()
    {
        using var scope = GoodEnv();
        scope.Set("USE_KEY_VAULT", "true");
        scope.Set("KEY_VAULT_URI", null);
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath, FiltersPath);
        Assert.Contains(result.Errors, e => e.Contains("KEY_VAULT_URI"));
    }

    [Fact]
    public void UnknownLogFormat_IsWarning()
    {
        using var scope = GoodEnv();
        scope.Set("LOG_FORMAT", "xml");
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath, FiltersPath);
        Assert.Contains(result.Warnings, w => w.Contains("LOG_FORMAT"));
    }

    [Fact]
    public void MissingConfigFiles_AreErrors()
    {
        using var scope = GoodEnv();
        var result = ValidateConfig.ValidateCore(
            "/no/schema.json", "/no/graph-schema.json", FiltersPath);
        Assert.Equal(2, result.Errors.Count(e => e.Contains("Missing config file")));
    }

    // ── repo config sanity ───────────────────────────────────────────────────

    [Fact]
    public void RepoSchemas_LoadAndValidate()
    {
        var schema = SchemaConfig.Load(RepoConfig("schema.json"));
        Assert.Equal(5, schema.ObjectList.Count);
        Assert.Contains(schema.ObjectList, o => o.ObjectName == "Contact");
        Assert.Contains(schema.ObjectList, o => o.ObjectName == "Opportunity");

        var contact = schema.FindObject("contact")!;  // case-insensitive
        Assert.Equal("ownerOnly", contact.AclMode);
        Assert.Equal("OwnerId", contact.EffectiveOwnerField);

        var account = schema.FindObject("Account")!;
        Assert.Equal("group", account.AclMode);
        Assert.False(string.IsNullOrWhiteSpace(account.AclGroupId));
    }

    [Fact]
    public void RepoFilters_CoverEveryRepoSchemaObject()
    {
        using var scope = GoodEnv();
        var result = ValidateConfig.ValidateCore(
            RepoConfig("schema.json"), RepoConfig("graph-schema.json"),
            RepoConfig("filters.json"), strict: true);
        // Every shipped object is filtered, so the ONLY finding permitted here is
        // the coarse-ACL posture of 'Account' (aclMode=group), which ships
        // deliberately un-attested — see RepoSchema_ShipsNoPreCheckedAttestation.
        Assert.All(result.Errors, e => Assert.Contains("coarse aclMode", e));
        Assert.Empty(result.Warnings);
    }

    // The shipped config must never pre-check the attestation. A copied 'true'
    // would sign off an over-sharing exposure that no human ever reviewed, so
    // `validate-config --strict` failing on a fresh clone is the INTENDED
    // behaviour: the operator must look at Account's ACL posture and say so.
    [Fact]
    public void RepoSchema_ShipsNoPreCheckedAttestation()
    {
        var schema = SchemaConfig.Load(RepoConfig("schema.json"));
        Assert.All(schema.ObjectList, o => Assert.False(o.CoarseAclAcknowledged));

        var account = schema.FindObject("Account")!;
        Assert.Equal("group", account.AclMode);
        Assert.True(account.IsCoarseAcl);
    }

    // ── SchemaConfig.Validate rejections ─────────────────────────────────────

    [Fact]
    public void SchemaConfig_RejectsDuplicatesAndEmpty()
    {
        var duplicate = Write("dup.json", """
            {"objectList": [
                {"objectName": "X", "selectedFields": {"Name": "Title"}},
                {"objectName": "x", "selectedFields": {"Name": "Title"}}]}
            """);
        Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(duplicate));

        var empty = Write("empty.json", """{"objectList": []}""");
        Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(empty));
    }

    [Fact]
    public void SchemaConfig_RejectsObjectWithoutSelectedFields()
    {
        var path = Write("nofields.json", """
            {"objectList": [{"objectName": "X", "selectedFields": {}}]}
            """);
        Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));
    }

    [Fact]
    public void SchemaConfig_RejectsInvalidAclMode()
    {
        var path = Write("badacl.json", """
            {"objectList": [{"objectName": "X", "aclMode": "projectMembers",
                             "selectedFields": {"Name": "Title"}}]}
            """);
        Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));
    }

    [Fact]
    public void SchemaConfig_RejectsGroupModeWithoutGroupId()
    {
        var path = Write("groupless.json", """
            {"objectList": [{"objectName": "X", "aclMode": "group",
                             "selectedFields": {"Name": "Title"}}]}
            """);
        Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));
    }

    // ── AppConfig.Load ───────────────────────────────────────────────────────

    [Fact]
    public void AppConfig_Load_ReadsEnvironment()
    {
        using var scope = GoodEnv();
        scope.Set("INGEST_GRAPH_BATCH_SIZE", "50");  // clamped to 20
        scope.Set("BDH_ROOT_PATH", "data/bdh/");     // normalized to /data/bdh
        var config = AppConfig.Load();
        Assert.Equal("BdhHadoopMart", config.ConnectorId);
        Assert.Equal("webhdfs", config.HdfsMode);
        Assert.Equal("http://namenode.example:9870/webhdfs/v1", config.HdfsNamenodeUrl);
        Assert.Equal("/data/bdh", config.BdhRootPath);
        Assert.Equal(20, config.GraphBatchSize);
        scope.Set("INGEST_GRAPH_BATCH_SIZE", null);
    }

    [Fact]
    public void AppConfig_Load_MissingRequired_Throws()
    {
        using var scope = GoodEnv();
        scope.Set("CONNECTOR_ID", null);
        var exc = Assert.Throws<ArgumentException>(() => AppConfig.Load());
        Assert.Contains("CONNECTOR_ID", exc.Message);
    }

    [Fact]
    public void AppConfig_Load_LocalpathRequiresExportPath()
    {
        using var scope = GoodEnv();
        scope.Set("HDFS_MODE", "localpath");
        scope.Set("BDH_EXPORT_PATH", null);
        var exc = Assert.Throws<ArgumentException>(() => AppConfig.Load());
        Assert.Contains("BDH_EXPORT_PATH", exc.Message);
    }
}
