using ClarizenConnector.Commands;
using ClarizenConnector.Config;

namespace ClarizenConnector.Tests;

public class ValidateConfigTests
{
    private static EnvScope GoodEnv() => new(
        ("CONNECTOR_ID", "ClarizenAdaptiveWork"),
        ("CLARIZEN_USERNAME", "svc@example.com"),
        ("SECRET_CLARIZEN_PASSWORD", "pw"),
        ("AAD_APP_TENANT_ID", "tenant"),
        ("AAD_APP_CLIENT_ID", "client"),
        ("SECRET_AAD_APP_CLIENT_SECRET", "secret"),
        ("USE_KEY_VAULT", null),
        ("USE_SQL_SERVER", null),
        ("SQL_CONNECTION_STRING", null),
        ("HA_MODE", null),
        ("FINANCIAL_DATA_MODE", null),
        ("FINANCIAL_DATA_GROUP_ID", null),
        ("TDW_EXPORT_PATH", null),
        ("LOG_FORMAT", null),
        ("CLARIZEN_API_CALLS_PER_DAY", null),
        ("CLARIZEN_WEBHOOK_PORT", null),
        ("CLARIZEN_WEBHOOK_SECRET", null));

    private static string SchemaPath =>
        Path.Combine(AppContext.BaseDirectory, "config", "schema.json");

    private static string GraphSchemaPath =>
        Path.Combine(AppContext.BaseDirectory, "config", "graph-schema.json");

    [Fact]
    public void GoodConfig_PassesStrict()
    {
        using var scope = GoodEnv();
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.True(result.Ok(strict: true));
    }

    [Fact]
    public void MissingRequiredVars_AreErrors()
    {
        using var scope = GoodEnv();
        scope.Set("CLARIZEN_USERNAME", null);
        scope.Set("SECRET_AAD_APP_CLIENT_SECRET", null);
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath);
        Assert.Contains(result.Errors, e => e.Contains("CLARIZEN_USERNAME"));
        Assert.Contains(result.Errors, e => e.Contains("SECRET_AAD_APP_CLIENT_SECRET"));
        Assert.False(result.Ok(strict: false));
    }

    [Theory]
    [InlineData("ab")]                       // too short
    [InlineData("has spaces")]               // invalid chars
    [InlineData("Bad-Punctuation")]          // invalid chars
    [InlineData("MicrosoftClarizen")]        // reserved prefix
    [InlineData("SharePointFeed")]           // reserved prefix
    public void InvalidConnectorId_IsError(string connectorId)
    {
        using var scope = GoodEnv();
        scope.Set("CONNECTOR_ID", connectorId);
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void ValidConnectorId_HelperAgrees()
    {
        Assert.Null(AppConfig.ValidateConnectorId("ClarizenAdaptiveWork"));
        Assert.NotNull(AppConfig.ValidateConnectorId("Microsoft123"));
        Assert.NotNull(AppConfig.ValidateConnectorId("x"));
    }

    [Fact]
    public void HaWithoutSql_IsError()
    {
        using var scope = GoodEnv();
        scope.Set("HA_MODE", "true");
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath);
        Assert.Contains(result.Errors, e => e.Contains("HA_MODE"));

        scope.Set("USE_SQL_SERVER", "true");
        scope.Set("SQL_CONNECTION_STRING", "Server=x;Database=y");
        var fixedResult = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath);
        Assert.DoesNotContain(fixedResult.Errors, e => e.Contains("HA_MODE"));
    }

    [Fact]
    public void WebhookPortWithoutSecret_IsError()
    {
        using var scope = GoodEnv();
        scope.Set("CLARIZEN_WEBHOOK_PORT", "8443");
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath);
        Assert.Contains(result.Errors, e => e.Contains("CLARIZEN_WEBHOOK_SECRET"));

        scope.Set("CLARIZEN_WEBHOOK_SECRET", "shhh");
        var fixedResult = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath);
        Assert.DoesNotContain(fixedResult.Errors, e => e.Contains("CLARIZEN_WEBHOOK_SECRET"));
    }

    [Fact]
    public void FinancialAclModeWithoutGroup_IsError()
    {
        using var scope = GoodEnv();
        scope.Set("FINANCIAL_DATA_MODE", "acl");
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath);
        Assert.Contains(result.Errors, e => e.Contains("FINANCIAL_DATA_GROUP_ID"));

        scope.Set("FINANCIAL_DATA_GROUP_ID", "guid");
        var fixedResult = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath);
        Assert.DoesNotContain(fixedResult.Errors, e => e.Contains("FINANCIAL_DATA_GROUP_ID"));
    }

    [Fact]
    public void UnknownFinancialMode_IsError()
    {
        using var scope = GoodEnv();
        scope.Set("FINANCIAL_DATA_MODE", "redact");
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath);
        Assert.Contains(result.Errors, e => e.Contains("FINANCIAL_DATA_MODE"));
    }

    [Fact]
    public void MissingTdwPath_IsWarningOnly()
    {
        using var scope = GoodEnv();
        scope.Set("TDW_EXPORT_PATH", "/nonexistent/tdw/exports");
        var result = ValidateConfig.ValidateCore(SchemaPath, GraphSchemaPath);
        Assert.Empty(result.Errors);
        Assert.Contains(result.Warnings, w => w.Contains("TDW_EXPORT_PATH"));
        Assert.True(result.Ok(strict: false));
        Assert.False(result.Ok(strict: true));  // strict fails on warnings
    }

    [Fact]
    public void MissingConfigFiles_AreErrors()
    {
        using var scope = GoodEnv();
        var result = ValidateConfig.ValidateCore("/no/schema.json", "/no/graph-schema.json");
        Assert.Equal(2, result.Errors.Count(e => e.Contains("Missing config file")));
    }

    [Fact]
    public void RepoSchemas_LoadAndValidate()
    {
        var schema = SchemaConfig.Load(SchemaPath);
        Assert.Equal(9, schema.ObjectList.Count);
        Assert.Contains(schema.ObjectList, o => o.ObjectName == "Project");
        Assert.Contains(schema.ObjectList, o => o.ObjectName == "RegularResourceLink");

        var project = schema.FindObject("project")!;  // case-insensitive
        Assert.Equal(4, project.FinancialFields.Count);
        Assert.Equal("projectMembers", project.AclMode);

        var timesheet = schema.FindObject("Timesheet")!;
        Assert.Equal("ownerOnly", timesheet.AclMode);
    }

    [Fact]
    public void SchemaConfig_RejectsFinancialFieldNotInSelectedFields()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "schema.json");
        File.WriteAllText(path, """
            {"objectList": [{"objectName": "X", "selectedFields": {"Name": "Title"},
                             "financialFields": ["Ghost"]}]}
            """);
        Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(path));
    }

    [Fact]
    public void SchemaConfig_RejectsDuplicatesAndEmpty()
    {
        using var dir = new TempDir();
        var duplicate = Path.Combine(dir.Path, "dup.json");
        File.WriteAllText(duplicate, """
            {"objectList": [
                {"objectName": "X", "selectedFields": {"Name": "Title"}},
                {"objectName": "x", "selectedFields": {"Name": "Title"}}]}
            """);
        Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(duplicate));

        var empty = Path.Combine(dir.Path, "empty.json");
        File.WriteAllText(empty, """{"objectList": []}""");
        Assert.Throws<InvalidDataException>(() => SchemaConfig.Load(empty));
    }

    [Fact]
    public void AppConfig_Load_ReadsEnvironment()
    {
        using var scope = GoodEnv();
        scope.Set("CLARIZEN_API_CALLS_PER_DAY", "5000");
        scope.Set("INGEST_GRAPH_BATCH_SIZE", "50");  // clamped to 20
        var config = AppConfig.Load();
        Assert.Equal("ClarizenAdaptiveWork", config.ConnectorId);
        Assert.Equal(5000, config.ClarizenApiCallsPerDay);
        Assert.Equal(20, config.GraphBatchSize);
        Assert.Equal("https://api.clarizen.com/v2.0/services", config.ClarizenBaseUrl);
        scope.Set("INGEST_GRAPH_BATCH_SIZE", null);
        scope.Set("CLARIZEN_API_CALLS_PER_DAY", null);
    }

    [Fact]
    public void AppConfig_Load_MissingRequired_Throws()
    {
        using var scope = GoodEnv();
        scope.Set("CONNECTOR_ID", null);
        var exc = Assert.Throws<ArgumentException>(() => AppConfig.Load());
        Assert.Contains("CONNECTOR_ID", exc.Message);
    }
}
