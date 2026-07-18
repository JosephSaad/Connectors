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

    private string GraphSchemaPath => Write("graph-schema.json", """
        [{"name": "Title", "type": "String", "isSearchable": true}]
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
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
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
