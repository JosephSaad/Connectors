// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests.TestSalesforce;

/// <summary>
/// Environment variables are process-global, so every test class that mutates them
/// must join this collection to avoid parallel interference.
/// </summary>
[CollectionDefinition("EnvVars")]
public class EnvVarsCollectionDefinition
{
}

/// <summary>Port of tests/test_salesforce/test_settings.py.</summary>
[Collection("EnvVars")]
public class SettingsTests
{
    // ── build_object_name_list ────────────────────────────────────────────────

    [Fact]
    public void BuildObjectNameListExtractsAllNames()
    {
        // build_object_name_list returns every objectName from the schema.
        var schema = new JsonObject
        {
            ["objectList"] = new JsonArray(
                new JsonObject { ["objectName"] = "Account", ["owdField"] = "DefaultAccountAccess" },
                new JsonObject { ["objectName"] = "Contact" },
                new JsonObject { ["objectName"] = "Case", ["owdField"] = "DefaultCaseAccess" }),
        };
        var result = Settings.BuildObjectNameList(schema);
        Assert.Equal(new List<string> { "Account", "Contact", "Case" }, result);
    }

    [Fact]
    public void BuildObjectNameListSkipsEntriesWithoutObjectName()
    {
        // Entries missing 'objectName' should be silently skipped.
        var schema = new JsonObject
        {
            ["objectList"] = new JsonArray(
                new JsonObject { ["objectName"] = "Account" },
                new JsonObject { ["description"] = "orphan entry with no objectName" },
                new JsonObject { ["objectName"] = "Case" }),
        };
        var result = Settings.BuildObjectNameList(schema);
        Assert.Equal(new List<string> { "Account", "Case" }, result);
    }

    [Fact]
    public void BuildObjectNameListEmptySchema()
    {
        // Empty objectList should return an empty list.
        Assert.Empty(Settings.BuildObjectNameList(new JsonObject { ["objectList"] = new JsonArray() }));
        Assert.Empty(Settings.BuildObjectNameList(new JsonObject()));
    }

    // ── load_config ───────────────────────────────────────────────────────────

    private static readonly string[] ConfigEnvNames =
    {
        "CONNECTOR_ID",
        "CONNECTOR_NAME",
        "CONNECTOR_DESCRIPTION",
        "AAD_APP_CLIENT_ID",
        "AAD_APP_TENANT_ID",
        "AZURE_CLIENT_ID",
        "AZURE_TENANT_ID",
        "AZURE_CLIENT_SECRET",
        "SALESFORCE_INSTANCE_URL",
        "SALESFORCE_API_VERSION",
        "SALESFORCE_CLIENT_ID",
        "SECRET_SALESFORCE_CLIENT_SECRET",
        "SALESFORCE_CLIENT_SECRET",
        "GRAPH_MAX_RETRIES",
        "GRAPH_RETRY_BACKOFF_BASE",
        "CONNECTION_TIMEOUT_SECONDS",
        "CONNECTION_RETRY_INTERVAL_SECONDS",
        "SCHEMA_RETRY_INTERVAL_SECONDS",
        "SALESFORCE_BATCH_SIZE",
        "ACL_MAX_PARENT_DEPTH",
        "GRAPH_API_VERSION",
        "USE_ENTITY_DEFINITION_OWD",
        "GRAPH_CONCURRENT_BATCHES",
        "GRAPH_BATCH_WORKERS",
    };

    /// <summary>
    /// Snapshot + delete the config-related env vars, and remember the original
    /// LOCAL_ENV_FILES entries; everything is restored on Dispose (the Python tests
    /// use monkeypatch.delenv / monkeypatch.setattr for this).
    /// </summary>
    private sealed class ConfigEnvScope : IDisposable
    {
        private readonly Dictionary<string, string?> _savedEnv = new();
        private readonly string[] _savedLocalEnvFiles;

        public ConfigEnvScope()
        {
            foreach (var name in ConfigEnvNames)
            {
                _savedEnv[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, null);
            }
            _savedLocalEnvFiles = (string[])Settings.LocalEnvFiles.Clone();
        }

        public void RedirectLocalEnvFiles(string tmpPath)
        {
            Settings.LocalEnvFiles[0] = Path.Combine(tmpPath, ".env.local");
            Settings.LocalEnvFiles[1] = Path.Combine(tmpPath, ".env.local.user");
            Settings.LocalEnvFiles[2] = Path.Combine(tmpPath, "python_connector.env.local");
            Settings.LocalEnvFiles[3] = Path.Combine(tmpPath, "python_connector.env.local.user");
        }

        public void Dispose()
        {
            for (var i = 0; i < _savedLocalEnvFiles.Length; i++)
                Settings.LocalEnvFiles[i] = _savedLocalEnvFiles[i];
            foreach (var (name, value) in _savedEnv)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static readonly string[] BaseEnvFileLines =
    {
        "CONNECTOR_ID=ACL22032026",
        "CONNECTOR_NAME=ACL22032026",
        "CONNECTOR_DESCRIPTION=Test connector",
        "AAD_APP_CLIENT_ID=00000000-0000-0000-0000-000000000000",
        "AAD_APP_TENANT_ID=00000000-0000-0000-0000-000000000000",
        "SALESFORCE_INSTANCE_URL=https://example.my.salesforce.com",
        "SALESFORCE_API_VERSION=v48.0",
        "SALESFORCE_CLIENT_ID=test-salesforce-client-id",
        "SECRET_SALESFORCE_CLIENT_SECRET=test-salesforce-client-secret",
        "GRAPH_MAX_RETRIES=4",
        "GRAPH_RETRY_BACKOFF_BASE=2",
        "CONNECTION_TIMEOUT_SECONDS=600",
        "CONNECTION_RETRY_INTERVAL_SECONDS=15",
        "SCHEMA_RETRY_INTERVAL_SECONDS=15",
        "SALESFORCE_BATCH_SIZE=100",
        "ACL_MAX_PARENT_DEPTH=5",
    };

    [Fact]
    public void LoadConfigDoesNotReadExampleEnvFiles()
    {
        var tmpPath = Directory.CreateTempSubdirectory("settings_tests_").FullName;
        var exampleEnv = Path.Combine(tmpPath, ".env.local.example");
        var localEnv = Path.Combine(tmpPath, ".env.local");

        File.WriteAllText(exampleEnv, "CONNECTOR_ID=ignored\n");
        File.WriteAllText(localEnv, string.Join("\n", BaseEnvFileLines) + "\n");

        using var scope = new ConfigEnvScope();
        scope.RedirectLocalEnvFiles(tmpPath);

        var config = Settings.LoadConfig();
        // New EntityDefinition fields should have correct defaults
        Assert.False(config.UseEntityDefinitionOwd);
        Assert.IsType<List<string>>(config.ObjectNames);
        Assert.True(config.ObjectNames.Count > 0);  // schema.json has objects
    }

    [Fact]
    public void LoadConfigUseEntityDefinitionOwdTrue()
    {
        // USE_ENTITY_DEFINITION_OWD=true should be parsed into config.
        var tmpPath = Directory.CreateTempSubdirectory("settings_tests_").FullName;
        var localEnv = Path.Combine(tmpPath, ".env.local");
        File.WriteAllText(
            localEnv,
            string.Join("\n", BaseEnvFileLines.Append("USE_ENTITY_DEFINITION_OWD=true")) + "\n");

        using var scope = new ConfigEnvScope();
        scope.RedirectLocalEnvFiles(tmpPath);

        var config = Settings.LoadConfig();
        Assert.True(config.UseEntityDefinitionOwd);
    }

    [Fact]
    public void LoadConfigGraphBatchWorkersAliasesConcurrentBatches()
    {
        // GRAPH_BATCH_WORKERS (the documented operator knob) feeds
        // GraphConcurrentBatches when GRAPH_CONCURRENT_BATCHES is not set.
        var tmpPath = Directory.CreateTempSubdirectory("settings_tests_").FullName;
        var localEnv = Path.Combine(tmpPath, ".env.local");
        File.WriteAllText(
            localEnv,
            string.Join("\n", BaseEnvFileLines.Append("GRAPH_BATCH_WORKERS=5")) + "\n");

        using var scope = new ConfigEnvScope();
        scope.RedirectLocalEnvFiles(tmpPath);

        var config = Settings.LoadConfig();
        Assert.Equal(5, config.Tuning.GraphConcurrentBatches);
    }

    [Fact]
    public void LoadConfigGraphConcurrentBatchesWinsOverAlias()
    {
        // Explicit GRAPH_CONCURRENT_BATCHES takes precedence; unset both → default 8.
        var tmpPath = Directory.CreateTempSubdirectory("settings_tests_").FullName;
        var localEnv = Path.Combine(tmpPath, ".env.local");
        File.WriteAllText(
            localEnv,
            string.Join("\n", BaseEnvFileLines
                .Append("GRAPH_CONCURRENT_BATCHES=6")
                .Append("GRAPH_BATCH_WORKERS=5")) + "\n");

        using var scope = new ConfigEnvScope();
        scope.RedirectLocalEnvFiles(tmpPath);

        var config = Settings.LoadConfig();
        Assert.Equal(6, config.Tuning.GraphConcurrentBatches);
    }
}
