// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests;

/// <summary>
/// Shared test fixtures — port of tests/conftest.py.
/// </summary>
public static class TestFixtures
{
    public const string ApiVersion = "v60.0";
    public const string InstanceUrl = "https://test.my.salesforce.com";

    /// <summary>Deterministic tenant GUID for test assertions (conftest `tenant_id`).</summary>
    public const string TenantId = "00000000-0000-0000-0000-000000000001";

    /// <summary>
    /// Build a fully populated AppConfig using real schema files but mock credentials
    /// (conftest `test_config`).
    /// </summary>
    public static AppConfig TestConfig()
    {
        var schema = Settings.LoadSchemaConfig();
        return new AppConfig
        {
            ClientId = "00000000-0000-0000-0000-000000000000",
            TenantId = TenantId,
            RepoRoot = Settings.RepoRoot,
            SchemaConfig = schema,
            OwdFieldMap = Settings.BuildOwdFieldMap(schema),
            ParentMap = Settings.BuildParentMap(schema),
            OwdOverrides = new Dictionary<string, string>(),
            ObjectNames = (schema["objectList"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .Where(o => o.ContainsKey("objectName"))
                .Select(o => o["objectName"]!.ToString())
                .ToList(),
            UseNewAclEngine = false,
            UseGroupAcl = false,
            UseEntityDefinitionOwd = false,
            DebugObjectType = null,
            DebugItemId = null,
            Tuning = new TuningSettings
            {
                GraphApiVersion = "v1.0",
                GraphMaxRetries = 4,
                GraphRetryBackoffBase = 2,
                ConnectionTimeoutSeconds = 600,
                ConnectionRetryIntervalSeconds = 15,
                SchemaRetryIntervalSeconds = 15,
                SalesforceQueryLimit = 0,
                SalesforceBatchSize = 100,
                AclMaxParentDepth = 5,
                IngestChunkSize = 500,
                IngestGraphBatchSize = 20,
                GraphConcurrentBatches = 1,
                ParallelObjectWorkers = 1,
            },
            Connector = new ConnectorSettings
            {
                Id = "SalesforceCRMTestAutomation",
                Name = "Salesforce CRM Test Automation",
                Description = "Mock connector configuration for automated tests.",
                Schema = Settings.LoadGraphSchema(),
                Template = new JsonObject { ["id"] = "display" },
                Salesforce = new SalesforceSettings
                {
                    InstanceUrl = InstanceUrl,
                    ApiVersion = ApiVersion,
                    ClientId = "mock-salesforce-client-id",
                    ClientSecret = "mock-salesforce-client-secret",
                },
            },
        };
    }
}
