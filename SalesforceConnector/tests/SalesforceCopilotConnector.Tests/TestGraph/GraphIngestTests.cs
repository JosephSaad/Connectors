// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for the graph.ingest ingestion pipeline — port of tests/test_graph/test_graph_ingest.py.

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Config;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests.TestGraph;

public class GraphIngestTests : IDisposable
{
    // A minimal object config used when mocking get_object_config
    private static readonly SalesforceObjectConfig AccountCfg = new("Account", new[] { "Id" });

    private readonly AppConfig _testConfig = TestFixtures.TestConfig();
    private readonly FakeGraphClient _mockClient = new();

    private readonly string _tmpPath;
    private readonly string _originalLogsDir;
    private readonly Func<string, SalesforceObjectConfig?> _originalGetObjectConfig;
    private readonly Func<AppConfig, SalesforceObjectConfig, DateTime?, int, IAsyncEnumerable<List<JsonObject>>> _originalIterChunks;
    private readonly Func<AppConfig, Dictionary<string, Item.SalesforceObjectHandler>, GraphClient, AclResolver> _originalResolverFactory;
    private readonly Func<string, JsonArray, string, SalesforceItemTransformer> _originalTransformerFactory;

    public GraphIngestTests()
    {
        // Port of the autouse ``_patch_sync_state`` fixture — prevent ingest_content
        // from touching the real filesystem (checkpoint / dead-letter go to tmp_path).
        _tmpPath = Path.Combine(Path.GetTempPath(), "sf-ingest-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpPath);
        _originalLogsDir = SyncState.LogsDir;
        SyncState.LogsDir = _tmpPath;

        _originalGetObjectConfig = Ingest.GetObjectConfigHook;
        _originalIterChunks = Ingest.IterObjectChunksHook;
        _originalResolverFactory = Ingest.LegacyAclResolverFactory;
        _originalTransformerFactory = Ingest.TransformerFactory;
    }

    public void Dispose()
    {
        Ingest.GetObjectConfigHook = _originalGetObjectConfig;
        Ingest.IterObjectChunksHook = _originalIterChunks;
        Ingest.LegacyAclResolverFactory = _originalResolverFactory;
        Ingest.TransformerFactory = _originalTransformerFactory;
        SyncState.LogsDir = _originalLogsDir;
        try
        {
            Directory.Delete(_tmpPath, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    // ── Test doubles (Python @patch of graph.ingest module attributes) ───────

    /// <summary>Fake legacy ACL resolver — mirrors ``@patch("graph.ingest.LegacyAclResolver")``.</summary>
    private sealed class FakeAclResolver : AclResolver
    {
        public Func<Dictionary<string, List<JsonObject>>, Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>>> OnResolve { get; set; }
            = _ => new Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>>();

        public FakeAclResolver(AppConfig config) : base(config)
        {
        }

        public override Task<Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>>> ResolveAsync(
            Dictionary<string, List<JsonObject>> recordsByObjectType)
        {
            return Task.FromResult(OnResolve(recordsByObjectType));
        }

        public override Task PrewarmCachesAsync() => Task.CompletedTask;
    }

    /// <summary>Fake transformer — mirrors ``@patch("graph.ingest.SalesforceItemTransformer")``.</summary>
    private sealed class FakeTransformer : SalesforceItemTransformer
    {
        public Func<JsonObject, List<Dictionary<string, string>>?, List<JsonObject>> OnTransform { get; set; }
            = (_, _) => new List<JsonObject>();

        public FakeTransformer(string instanceUrl, JsonArray schema, string tenantId)
            : base(instanceUrl, schema, tenantId: tenantId)
        {
        }

        public override List<JsonObject> TransformRecord(
            JsonObject item, List<Dictionary<string, string>>? acl = null)
        {
            return OnTransform(item, acl);
        }
    }

    /// <summary>Equivalent of Python ``dataclasses.replace(test_config, ...)``.</summary>
    private static AppConfig Replace(AppConfig cfg, string? debugObjectType = null, string? debugItemId = null)
    {
        return new AppConfig
        {
            ClientId = cfg.ClientId,
            TenantId = cfg.TenantId,
            Connector = cfg.Connector,
            RepoRoot = cfg.RepoRoot,
            Tuning = cfg.Tuning,
            SchemaConfig = cfg.SchemaConfig,
            OwdFieldMap = cfg.OwdFieldMap,
            ParentMap = cfg.ParentMap,
            OwdOverrides = cfg.OwdOverrides,
            ObjectNames = cfg.ObjectNames,
            UseNewAclEngine = cfg.UseNewAclEngine,
            UseGroupAcl = cfg.UseGroupAcl,
            UseEntityDefinitionOwd = cfg.UseEntityDefinitionOwd,
            DebugObjectType = debugObjectType ?? cfg.DebugObjectType,
            DebugItemId = debugItemId ?? cfg.DebugItemId,
        };
    }

    private static async IAsyncEnumerable<List<JsonObject>> Chunks(params List<JsonObject>[] chunks)
    {
        await Task.CompletedTask;
        foreach (var chunk in chunks)
            yield return chunk;
    }

    private FakeAclResolver InstallFakeResolver()
    {
        var resolver = new FakeAclResolver(_testConfig);
        Ingest.LegacyAclResolverFactory = (_, _, _) => resolver;
        return resolver;
    }

    private FakeTransformer InstallFakeTransformer()
    {
        FakeTransformer? transformer = null;
        Ingest.TransformerFactory = (instanceUrl, schema, tenantId) =>
            transformer ??= new FakeTransformer(instanceUrl, schema, tenantId);
        // Build eagerly so tests can configure OnTransform before ingest runs.
        return (FakeTransformer)Ingest.TransformerFactory(
            _testConfig.Connector.Salesforce.InstanceUrl,
            _testConfig.Connector.Schema,
            _testConfig.TenantId);
    }

    // ── load_content / delete_content ────────────────────────────────────────

    [Fact]
    public async Task LoadContentPutsCorrectUrl()
    {
        var item = new JsonObject
        {
            ["id"] = "item-1",
            ["properties"] = new JsonObject { ["Url"] = "https://example.com" },
            ["acl"] = new JsonArray(),
            ["content"] = new JsonObject { ["value"] = "" },
        };
        await Ingest.LoadContentAsync(_testConfig, _mockClient, item);
        var expectedUrl =
            $"{GraphClient.ExternalConnectionsPath}/{_testConfig.Connector.Id}/items/{Uri.EscapeDataString("item-1")}";
        Assert.Single(_mockClient.PutCalls);
        Assert.Equal(expectedUrl, _mockClient.PutCalls[0].Url);
    }

    [Fact]
    public async Task DeleteContentCallsDelete()
    {
        await Ingest.DeleteContentAsync(_testConfig, _mockClient, "item-1");
        Assert.Single(_mockClient.DeleteCalls);
    }

    // ── ingest_content ───────────────────────────────────────────────────────

    [Fact]
    public async Task IngestNoItemsReturnsEmptyStats()
    {
        Ingest.IterObjectChunksHook = (_, _, _, _) => Chunks();
        Ingest.GetObjectConfigHook = _ => AccountCfg;
        InstallFakeResolver();
        var cfg = Replace(_testConfig, debugObjectType: "Account");

        var stats = await Ingest.IngestContentAsync(cfg, _mockClient);
        Assert.Equal(0, stats.TotalFetched);
        Assert.Equal(0, stats.SuccessCount);
    }

    [Fact]
    public async Task IngestTransformsAndLoads()
    {
        Ingest.GetObjectConfigHook = _ => AccountCfg;
        var cfg = Replace(_testConfig, debugObjectType: "Account");
        var raw = new List<JsonObject>
        {
            new() { ["Id"] = "001", ["objectType"] = "Account", ["url"] = "https://sf.com/001" },
        };
        Ingest.IterObjectChunksHook = (_, _, _, _) => Chunks(raw);

        var resolver = InstallFakeResolver();
        resolver.OnResolve = _ => new Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>>
        {
            ["Account"] = new()
            {
                ["001"] = new List<Dictionary<string, string>>
                {
                    new() { ["accessType"] = "grant", ["type"] = "everyone", ["value"] = "everyone" },
                },
            },
        };

        var transformed = new List<JsonObject>
        {
            new()
            {
                ["id"] = "001",
                ["properties"] = new JsonObject { ["Url"] = "https://sf.com/001" },
                ["acl"] = new JsonArray(),
                ["content"] = new JsonObject { ["value"] = "" },
            },
        };
        var transformer = InstallFakeTransformer();
        transformer.OnTransform = (_, _) => transformed;

        _mockClient.OnBatch = _ => new List<JsonObject>
        {
            new() { ["id"] = "0", ["status"] = 200 },
        };

        var stats = await Ingest.IngestContentAsync(cfg, _mockClient);
        Assert.Equal(1, stats.TotalFetched);
        Assert.Equal(1, stats.SuccessCount);
    }

    [Fact]
    public async Task IngestHandlesDeletedItems()
    {
        Ingest.GetObjectConfigHook = _ => AccountCfg;
        var cfg = Replace(_testConfig, debugObjectType: "Account");
        var raw = new List<JsonObject>
        {
            new()
            {
                ["Id"] = "002",
                ["objectType"] = "Account",
                ["url"] = "https://sf.com/002",
                ["IsDeleted"] = true,
            },
        };
        Ingest.IterObjectChunksHook = (_, _, _, _) => Chunks(raw);
        InstallFakeResolver();

        var transformer = InstallFakeTransformer();
        transformer.OnTransform = (_, _) => new List<JsonObject>
        {
            new() { ["type"] = "deleted", ["id"] = "002" },
        };

        _mockClient.OnBatch = _ => new List<JsonObject>
        {
            new() { ["id"] = "0", ["status"] = 200 },
        };

        var stats = await Ingest.IngestContentAsync(cfg, _mockClient);
        Assert.Equal(1, stats.DeletedCount);
    }

    [Fact]
    public async Task IngestHandlesFailedItems()
    {
        Ingest.GetObjectConfigHook = _ => AccountCfg;
        var cfg = Replace(_testConfig, debugObjectType: "Account");
        var raw = new List<JsonObject>
        {
            new() { ["Id"] = "003", ["objectType"] = "Account", ["url"] = "https://sf.com/003" },
        };
        Ingest.IterObjectChunksHook = (_, _, _, _) => Chunks(raw);
        InstallFakeResolver();

        var transformer = InstallFakeTransformer();
        transformer.OnTransform = (_, _) => new List<JsonObject>
        {
            new()
            {
                ["id"] = "003",
                ["properties"] = new JsonObject(),
                ["acl"] = new JsonArray(),
                ["content"] = new JsonObject { ["value"] = "" },
            },
        };

        _mockClient.OnBatch = _ => new List<JsonObject>
        {
            new() { ["id"] = "0", ["status"] = 400, ["body"] = "bad request" },
        };

        var stats = await Ingest.IngestContentAsync(cfg, _mockClient);
        Assert.Equal(1, stats.FailedCount);
        Assert.Contains("003", stats.FailedIds);
    }

    [Fact]
    public async Task IngestAclFailureFallsBackToPublic()
    {
        Ingest.GetObjectConfigHook = _ => AccountCfg;
        var cfg = Replace(_testConfig, debugObjectType: "Account");
        var raw = new List<JsonObject>
        {
            new() { ["Id"] = "004", ["objectType"] = "Account", ["url"] = "https://sf.com/004" },
        };
        Ingest.IterObjectChunksHook = (_, _, _, _) => Chunks(raw);

        var resolver = InstallFakeResolver();
        resolver.OnResolve = _ => throw new InvalidOperationException("ACL boom");

        var transformer = InstallFakeTransformer();
        transformer.OnTransform = (_, _) => new List<JsonObject>
        {
            new()
            {
                ["id"] = "004",
                ["properties"] = new JsonObject(),
                ["acl"] = new JsonArray(),
                ["content"] = new JsonObject { ["value"] = "" },
            },
        };

        _mockClient.OnBatch = _ => new List<JsonObject>
        {
            new() { ["id"] = "0", ["status"] = 200 },
        };

        var stats = await Ingest.IngestContentAsync(cfg, _mockClient);
        Assert.True(stats.AclFallbackUsed);
    }

    [Fact]
    public async Task IngestFiltersByDebugItemId()
    {
        Ingest.GetObjectConfigHook = _ => AccountCfg;
        var cfg = Replace(_testConfig, debugObjectType: "Account", debugItemId: "005");
        var raw = new List<JsonObject>
        {
            new() { ["Id"] = "005", ["objectType"] = "Account", ["url"] = "https://sf.com/005" },
            new() { ["Id"] = "006", ["objectType"] = "Account", ["url"] = "https://sf.com/006" },
        };
        Ingest.IterObjectChunksHook = (_, _, _, _) => Chunks(raw);
        InstallFakeResolver();

        var transformer = InstallFakeTransformer();
        transformer.OnTransform = (_, _) => new List<JsonObject>
        {
            new()
            {
                ["id"] = "005",
                ["properties"] = new JsonObject(),
                ["acl"] = new JsonArray(),
                ["content"] = new JsonObject { ["value"] = "" },
            },
        };

        _mockClient.OnBatch = _ => new List<JsonObject>
        {
            new() { ["id"] = "0", ["status"] = 200 },
        };

        var stats = await Ingest.IngestContentAsync(cfg, _mockClient);
        Assert.Equal(1, stats.TotalFetched);
    }

    [Fact]
    public async Task IngestFiltersByDebugObjectType()
    {
        Ingest.GetObjectConfigHook = _ => AccountCfg;
        var cfg = Replace(_testConfig, debugObjectType: "Account");
        var raw = new List<JsonObject>
        {
            new() { ["Id"] = "007", ["objectType"] = "Account", ["url"] = "https://sf.com/007" },
        };
        Ingest.IterObjectChunksHook = (_, _, _, _) => Chunks(raw);
        InstallFakeResolver();

        var transformer = InstallFakeTransformer();
        transformer.OnTransform = (_, _) => new List<JsonObject>
        {
            new()
            {
                ["id"] = "007",
                ["properties"] = new JsonObject(),
                ["acl"] = new JsonArray(),
                ["content"] = new JsonObject { ["value"] = "" },
            },
        };

        _mockClient.OnBatch = _ => new List<JsonObject>
        {
            new() { ["id"] = "0", ["status"] = 200 },
        };

        var stats = await Ingest.IngestContentAsync(cfg, _mockClient);
        Assert.Equal(1, stats.TotalFetched);
    }
}
