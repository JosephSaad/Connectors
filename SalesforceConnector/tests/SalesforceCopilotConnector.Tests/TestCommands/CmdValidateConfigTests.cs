// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for the validate-config preflight command.
//
// Hermetic by construction: LoadConfig and the Salesforce/Graph token probes are
// patched via the command's test hooks, and the structural checks are pointed at a
// temporary config directory through SFCONNECTOR_HOME. No real network, Salesforce,
// or Graph calls are made.

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Commands;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests.TestCommands;

/// <summary>
/// Patches validate-config's external seams and redirects the config directory to a
/// temp folder; restores everything (hooks, env var, logging override) on Dispose.
/// Joins the shared "CommandHooks" collection so it never runs alongside another
/// class that mutates the same static hooks (the runner also disables cross-collection
/// parallelism, so the SFCONNECTOR_HOME env mutation is safe too).
/// </summary>
internal sealed class ValidateConfigPatches : IDisposable
{
    public AppConfig Config;

    public int SalesforceTokenCallCount;
    public int GraphTokenCallCount;
    public Func<AppConfig, Task<string>>? SalesforceTokenImpl;
    public Func<AppConfig, Task>? GraphTokenImpl;

    private readonly string? _savedHome;
    private readonly string? _savedAzureSecret;
    private readonly Func<AppConfig> _savedLoadConfigHook;
    private readonly Func<AppConfig, Task<string>> _savedSalesforceTokenHook;
    private readonly Func<AppConfig, Task> _savedGraphTokenHook;
    public readonly string ConfigHome;

    public ValidateConfigPatches()
    {
        Config = NoCredsConfig();
        _savedHome = Environment.GetEnvironmentVariable("SFCONNECTOR_HOME");
        _savedAzureSecret = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");
        // Snapshot the real hooks BEFORE replacing them so Dispose restores exactly.
        _savedLoadConfigHook = ValidateConfig.LoadConfigHook;
        _savedSalesforceTokenHook = ValidateConfig.SalesforceTokenHook;
        _savedGraphTokenHook = ValidateConfig.GraphTokenHook;

        // Fresh temp home with a config/ subdir the structural checks will read.
        ConfigHome = Directory.CreateTempSubdirectory("validate_config_tests_").FullName;
        Directory.CreateDirectory(Path.Combine(ConfigHome, "config"));
        Environment.SetEnvironmentVariable("SFCONNECTOR_HOME", ConfigHome);
        // No Azure secret by default → Graph connectivity SKIPs unless a test opts in.
        Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", null);

        ValidateConfig.LoadConfigHook = () => Config;
        ValidateConfig.SalesforceTokenHook = config =>
        {
            SalesforceTokenCallCount++;
            return SalesforceTokenImpl is not null
                ? SalesforceTokenImpl(config)
                : Task.FromResult("fake-sf-token");
        };
        ValidateConfig.GraphTokenHook = config =>
        {
            GraphTokenCallCount++;
            return GraphTokenImpl is not null ? GraphTokenImpl(config) : Task.CompletedTask;
        };
        CommandRegistry.SetupLoggingOverride = (_, _, _) => ("fake_log.log", "fake_summary.log");
    }

    /// <summary>Write the three config files into the temp config dir.</summary>
    public void WriteConfigFiles(string schemaJson, string graphSchemaJson, string templateJson)
    {
        var dir = Path.Combine(ConfigHome, "config");
        File.WriteAllText(Path.Combine(dir, "schema.json"), schemaJson);
        File.WriteAllText(Path.Combine(dir, "graph-schema.json"), graphSchemaJson);
        File.WriteAllText(Path.Combine(dir, "template.json"), templateJson);
    }

    public void WriteWellFormedConfigFiles() => WriteConfigFiles(
        WellFormedSchema, WellFormedGraphSchema, WellFormedTemplate);

    /// <summary>A config with no Salesforce or Azure credentials → connectivity SKIPs.</summary>
    public static AppConfig NoCredsConfig()
    {
        var baseConfig = TestFixtures.TestConfig();
        return new AppConfig
        {
            ClientId = "",
            TenantId = "",
            Connector = new ConnectorSettings
            {
                Id = baseConfig.Connector.Id,
                Name = baseConfig.Connector.Name,
                Description = baseConfig.Connector.Description,
                Schema = baseConfig.Connector.Schema,
                Template = baseConfig.Connector.Template,
                Salesforce = new SalesforceSettings
                {
                    InstanceUrl = "",
                    ApiVersion = baseConfig.Connector.Salesforce.ApiVersion,
                    ClientId = "",
                    ClientSecret = "",
                },
            },
            RepoRoot = baseConfig.RepoRoot,
            Tuning = baseConfig.Tuning,
            SchemaConfig = baseConfig.SchemaConfig,
            OwdFieldMap = baseConfig.OwdFieldMap,
            ParentMap = baseConfig.ParentMap,
            OwdOverrides = baseConfig.OwdOverrides,
            ObjectNames = baseConfig.ObjectNames,
            UseNewAclEngine = false,
            UseGroupAcl = false,
            UseEntityDefinitionOwd = false,
            DebugObjectType = null,
            DebugItemId = null,
        };
    }

    /// <summary>Same as NoCredsConfig but with Salesforce credentials populated.</summary>
    public static AppConfig SalesforceCredsConfig()
    {
        var noCreds = NoCredsConfig();
        return new AppConfig
        {
            ClientId = noCreds.ClientId,
            TenantId = noCreds.TenantId,
            Connector = new ConnectorSettings
            {
                Id = noCreds.Connector.Id,
                Name = noCreds.Connector.Name,
                Description = noCreds.Connector.Description,
                Schema = noCreds.Connector.Schema,
                Template = noCreds.Connector.Template,
                Salesforce = new SalesforceSettings
                {
                    InstanceUrl = "https://test.my.salesforce.com",
                    ApiVersion = "v60.0",
                    ClientId = "sf-client-id",
                    ClientSecret = "sf-client-secret",
                },
            },
            RepoRoot = noCreds.RepoRoot,
            Tuning = noCreds.Tuning,
            SchemaConfig = noCreds.SchemaConfig,
            OwdFieldMap = noCreds.OwdFieldMap,
            ParentMap = noCreds.ParentMap,
            OwdOverrides = noCreds.OwdOverrides,
            ObjectNames = noCreds.ObjectNames,
            UseNewAclEngine = false,
            UseGroupAcl = false,
            UseEntityDefinitionOwd = false,
            DebugObjectType = null,
            DebugItemId = null,
        };
    }

    /// <summary>NoCredsConfig with Azure client/tenant populated (secret comes from env).</summary>
    public static AppConfig GraphCredsConfig()
    {
        var noCreds = NoCredsConfig();
        return new AppConfig
        {
            ClientId = "azure-client-id",
            TenantId = "00000000-0000-0000-0000-000000000099",
            Connector = noCreds.Connector,
            RepoRoot = noCreds.RepoRoot,
            Tuning = noCreds.Tuning,
            SchemaConfig = noCreds.SchemaConfig,
            OwdFieldMap = noCreds.OwdFieldMap,
            ParentMap = noCreds.ParentMap,
            OwdOverrides = noCreds.OwdOverrides,
            ObjectNames = noCreds.ObjectNames,
            UseNewAclEngine = false,
            UseGroupAcl = false,
            UseEntityDefinitionOwd = false,
            DebugObjectType = null,
            DebugItemId = null,
        };
    }

    public void Dispose()
    {
        ValidateConfig.LoadConfigHook = _savedLoadConfigHook;
        ValidateConfig.SalesforceTokenHook = _savedSalesforceTokenHook;
        ValidateConfig.GraphTokenHook = _savedGraphTokenHook;
        CommandRegistry.SetupLoggingOverride = null;
        Environment.SetEnvironmentVariable("SFCONNECTOR_HOME", _savedHome);
        Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", _savedAzureSecret);
        try
        {
            Directory.Delete(ConfigHome, recursive: true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }

    // ── Sample config file contents ───────────────────────────────────────────

    public const string WellFormedSchema = """
    {
      "objectList": [
        {
          "objectName": "Account",
          "owdField": "DefaultAccountAccess",
          "selectedFields": { "Name": "Name", "Type": "Type" }
        },
        {
          "objectName": "Contact",
          "parentObjectName": "Account",
          "selectedFields": { "Name": "Name", "Email": "Email" }
        }
      ]
    }
    """;

    public const string WellFormedGraphSchema = """
    [
      { "name": "Id", "type": "String" },
      { "name": "Name", "type": "String" }
    ]
    """;

    public const string WellFormedTemplate = """
    { "type": "AdaptiveCard", "version": "1.3" }
    """;
}

[Collection("CommandHooks")]
public sealed class CmdValidateConfigTests : IDisposable
{
    private readonly ValidateConfigPatches _patches = new();

    public void Dispose() => _patches.Dispose();

    private static ParsedArgs MockArgs(bool strict = false, bool verbose = false)
    {
        var args = new ParsedArgs();
        args.Set("verbose", verbose);
        args.Set("strict", strict);
        return args;
    }

    [Fact]
    public async Task WellFormedConfigPassesStructuralChecks()
    {
        // Valid schema/graph-schema/template + no creds (connectivity SKIPs) → PASS.
        _patches.WriteWellFormedConfigFiles();

        var result = await ValidateConfig.RunAsync(MockArgs());

        Assert.True(result);
    }

    [Fact]
    public async Task MalformedSchemaFails()
    {
        // Empty objectList is a hard structural failure → overall FAIL.
        _patches.WriteConfigFiles(
            schemaJson: """{ "objectList": [] }""",
            graphSchemaJson: ValidateConfigPatches.WellFormedGraphSchema,
            templateJson: ValidateConfigPatches.WellFormedTemplate);

        var result = await ValidateConfig.RunAsync(MockArgs());

        Assert.False(result);
    }

    [Fact]
    public async Task InvalidJsonInConfigFileFails()
    {
        // graph-schema.json is not parseable JSON at all → hard FAIL.
        _patches.WriteConfigFiles(
            schemaJson: ValidateConfigPatches.WellFormedSchema,
            graphSchemaJson: "{ this is not valid json",
            templateJson: ValidateConfigPatches.WellFormedTemplate);

        var result = await ValidateConfig.RunAsync(MockArgs());

        Assert.False(result);
    }

    [Fact]
    public async Task ConnectivitySkipsCleanlyWhenCredsAbsent()
    {
        // No SF/Azure creds → both connectivity probes SKIP, nothing is invoked,
        // and the run still passes on the strength of the structural checks.
        _patches.WriteWellFormedConfigFiles();

        var result = await ValidateConfig.RunAsync(MockArgs());

        Assert.True(result);
        Assert.Equal(0, _patches.SalesforceTokenCallCount);
        Assert.Equal(0, _patches.GraphTokenCallCount);
    }

    [Fact]
    public async Task SalesforceProbeRunsWhenCredsPresent()
    {
        // With SF creds present the probe runs through the hook (no real network)
        // and a returned token yields a PASS → overall PASS.
        _patches.Config = ValidateConfigPatches.SalesforceCredsConfig();
        _patches.WriteWellFormedConfigFiles();

        var result = await ValidateConfig.RunAsync(MockArgs());

        Assert.True(result);
        Assert.Equal(1, _patches.SalesforceTokenCallCount);
    }

    [Fact]
    public async Task SalesforceAuthErrorFailsWhenCredsPresent()
    {
        // An auth rejection (creds present) is a hard FAIL, not a network WARN.
        _patches.Config = ValidateConfigPatches.SalesforceCredsConfig();
        _patches.SalesforceTokenImpl = _ => throw new InvalidOperationException(
            "Failed to authenticate with Salesforce: 401 Unauthorized - {\"error\":\"invalid_client\"}");
        _patches.WriteWellFormedConfigFiles();

        var result = await ValidateConfig.RunAsync(MockArgs());

        Assert.False(result);
    }

    [Fact]
    public async Task SalesforceNetworkErrorWarnsButPassesByDefault()
    {
        // A transport failure (offline) is a WARN, not a FAIL, when not strict.
        _patches.Config = ValidateConfigPatches.SalesforceCredsConfig();
        _patches.SalesforceTokenImpl = _ => throw new HttpRequestException("no route to host");
        _patches.WriteWellFormedConfigFiles();

        var result = await ValidateConfig.RunAsync(MockArgs());

        Assert.True(result);
    }

    [Fact]
    public async Task StrictPromotesConnectivityWarnToFail()
    {
        // Under --strict the same transport failure becomes a hard FAIL.
        _patches.Config = ValidateConfigPatches.SalesforceCredsConfig();
        _patches.SalesforceTokenImpl = _ => throw new HttpRequestException("no route to host");
        _patches.WriteWellFormedConfigFiles();

        var result = await ValidateConfig.RunAsync(MockArgs(strict: true));

        Assert.False(result);
    }

    [Fact]
    public async Task GraphProbeRunsWhenCredsPresent()
    {
        // Azure client+tenant on the config plus AZURE_CLIENT_SECRET in the env →
        // the Graph probe runs through the hook (no real token request) → PASS.
        _patches.Config = ValidateConfigPatches.GraphCredsConfig();
        Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", "azure-secret");
        _patches.GraphTokenImpl = _ => Task.CompletedTask;
        _patches.WriteWellFormedConfigFiles();

        var result = await ValidateConfig.RunAsync(MockArgs());

        Assert.True(result);
        Assert.Equal(1, _patches.GraphTokenCallCount);
    }

    [Fact]
    public async Task ConfigLoadFailureIsReportedAsFail()
    {
        // A config load error (e.g. missing env var / invalid connector id) → FAIL.
        _patches.WriteWellFormedConfigFiles();
        ValidateConfig.LoadConfigHook = () =>
            throw new ArgumentException("Invalid configuration: Missing CONNECTOR_ID");

        var result = await ValidateConfig.RunAsync(MockArgs());

        Assert.False(result);
    }
}
