// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for checkpoint, delta sync, and Ctrl+X resume behavior — port of
// tests/test_checkpoint_resume.py.
//
// Simulates the scenarios:
// 1. Normal completion → checkpoint cleared, sync timestamp saved
// 2. Ctrl+X stop → checkpoint preserved, sync timestamp NOT saved
// 3. Resume after Ctrl+X → skips already-completed chunks
// 4. --full flag → clears checkpoint and starts fresh

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Config;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Item;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests;

/// <summary>
/// The Ingest test hooks, SyncState.LogsDir, and ApiClient.SfSession are process-global,
/// so every test class that mutates them must join this collection to avoid parallel
/// interference.
/// </summary>
[CollectionDefinition("IngestGlobalState")]
public class IngestGlobalStateCollectionDefinition
{
}

/// <summary>
/// Shared test doubles for the ingest pipeline (Python patches the graph.ingest
/// module attributes with MagicMocks).
/// </summary>
internal static class CheckpointSupport
{
    /// <summary>Converter stub with no registered objects (avoids disk config loads).</summary>
    internal sealed class StubConverter : SalesforceConverter
    {
        public StubConverter()
            : base(
                "https://test.my.salesforce.com",
                config: new JsonObject { ["objectList"] = new JsonArray() })
        {
        }

        public override List<string> ObjectNames => new();

        public override SalesforceObjectHandler? GetHandler(string objectName) => null;
    }

    /// <summary>Transformer fake (Python: ``mock_transformer_cls.return_value``).</summary>
    internal sealed class FakeTransformer : SalesforceItemTransformer
    {
        public Func<JsonObject, List<Dictionary<string, string>>?, List<JsonObject>> OnTransform { get; set; }
            = (_, _) => new List<JsonObject> { Item("x") };

        public FakeTransformer()
            : base("https://test.my.salesforce.com", new JsonArray(), new StubConverter())
        {
        }

        // Python: mock_transformer_cls.return_value.handlers = {}
        public override Dictionary<string, SalesforceObjectHandler> Handlers => new();

        public override List<JsonObject> TransformRecord(
            JsonObject item, List<Dictionary<string, string>>? acl = null)
            => OnTransform(item, acl);
    }

    /// <summary>Legacy ACL resolver fake (Python: ``mock_acl_cls.return_value``).</summary>
    internal sealed class FakeAclResolver : AclResolver
    {
        private readonly Func<
            Dictionary<string, List<JsonObject>>,
            Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>>> _resolve;

        public FakeAclResolver(
            AppConfig config,
            Func<
                Dictionary<string, List<JsonObject>>,
                Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>>> resolve)
            : base(config)
        {
            _resolve = resolve;
        }

        public override Task<Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>>>
            ResolveAsync(Dictionary<string, List<JsonObject>> recordsByObjectType)
            => Task.FromResult(_resolve(recordsByObjectType));

        public override Task PrewarmCachesAsync() => Task.CompletedTask;
    }

    /// <summary>Graph client fake (Python: ``client = MagicMock()``).</summary>
    internal sealed class FakeGraphClient : GraphClient
    {
        public Func<List<JsonObject>, List<JsonObject>?> OnBatch { get; set; }
            = _ => new List<JsonObject>();

        private int _batchCallCount;

        public int BatchCallCount => _batchCallCount;

        public override Task<List<JsonObject>> BatchRequestsAsync(List<JsonObject> requestsPayload)
        {
            Interlocked.Increment(ref _batchCallCount);
            return Task.FromResult(OnBatch(requestsPayload)!);
        }
    }

    /// <summary>
    /// Dashboard fake for the Ctrl+X test: pressing Ctrl+X during the Graph push is
    /// simulated by setting stop_requested from chunk_ingested.
    /// </summary>
    internal sealed class StopOnChunkIngestedDashboard : IngestionDashboard
    {
        private volatile bool _stop;

        public StopOnChunkIngestedDashboard()
            : base("test-connector", "FULL", "LEGACY", "test.log")
        {
        }

        public override bool StopRequested => _stop;

        public override void ChunkIngested(string objType, int success, int failed)
        {
            _stop = true;
        }
    }

    /// <summary>Snapshot + restore of all process-global ingest state.</summary>
    internal sealed class IngestHookGuard : IDisposable
    {
        private readonly Func<string, SalesforceObjectConfig?> _getObjectConfig = Ingest.GetObjectConfigHook;
        private readonly Func<AppConfig, SalesforceObjectConfig, DateTime?, int, IAsyncEnumerable<List<JsonObject>>> _iterObjectChunks = Ingest.IterObjectChunksHook;
        private readonly Func<AppConfig, Dictionary<string, SalesforceObjectHandler>, GraphClient, AclResolver> _legacyAclResolverFactory = Ingest.LegacyAclResolverFactory;
        private readonly Func<string, JsonArray, string, SalesforceItemTransformer> _transformerFactory = Ingest.TransformerFactory;
        private readonly Func<AppConfig, DateTime?, Task<Dictionary<string, int>>> _getObjectCounts = Ingest.GetObjectCountsHook;
        private readonly string _logsDir = SyncState.LogsDir;

        public void Dispose()
        {
            Ingest.GetObjectConfigHook = _getObjectConfig;
            Ingest.IterObjectChunksHook = _iterObjectChunks;
            Ingest.LegacyAclResolverFactory = _legacyAclResolverFactory;
            Ingest.TransformerFactory = _transformerFactory;
            Ingest.GetObjectCountsHook = _getObjectCounts;
            SyncState.LogsDir = _logsDir;
        }
    }

    /// <summary>Copy *config* with a different DebugObjectType (Python: ``dataclasses.replace``).</summary>
    internal static AppConfig WithDebugObjectType(AppConfig config, string? debugObjectType) => new()
    {
        ClientId = config.ClientId,
        TenantId = config.TenantId,
        RepoRoot = config.RepoRoot,
        SchemaConfig = config.SchemaConfig,
        OwdFieldMap = config.OwdFieldMap,
        ParentMap = config.ParentMap,
        OwdOverrides = config.OwdOverrides,
        ObjectNames = config.ObjectNames,
        UseNewAclEngine = config.UseNewAclEngine,
        UseGroupAcl = config.UseGroupAcl,
        UseEntityDefinitionOwd = config.UseEntityDefinitionOwd,
        DebugObjectType = debugObjectType,
        DebugItemId = config.DebugItemId,
        Tuning = config.Tuning,
        Connector = config.Connector,
    };

    internal static JsonObject Item(string id) => new()
    {
        ["id"] = id,
        ["properties"] = new JsonObject(),
        ["acl"] = new JsonArray(),
        ["content"] = new JsonObject { ["value"] = "" },
    };

    internal static JsonObject Resp(string id, int status) => new()
    {
        ["id"] = id,
        ["status"] = status,
    };

    internal static async IAsyncEnumerable<List<JsonObject>> Chunks(params List<JsonObject>[] chunks)
    {
        foreach (var chunk in chunks)
            yield return chunk;
        await Task.CompletedTask;
    }

    /// <summary>Resolver return value: ``{"Account": {record_id: []}}``.</summary>
    internal static Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>> EmptyAclsFor(
        string objectType, IEnumerable<JsonObject> records) => new()
    {
        [objectType] = records.ToDictionary(
            r => r["Id"]!.GetValue<string>(),
            _ => new List<Dictionary<string, string>>()),
    };
}

[Collection("IngestGlobalState")]
public class CheckpointResumeTests : IDisposable
{
    private static readonly SalesforceObjectConfig AccountCfg =
        new(ObjectType: "Account", Fields: new[] { "Id" });

    private readonly CheckpointSupport.IngestHookGuard _guard = new();
    private readonly string _tmpPath;

    public CheckpointResumeTests()
    {
        // Redirect all sync state files to a temp directory (autouse ``_isolate_sync_state``).
        _tmpPath = Directory.CreateTempSubdirectory("checkpoint_resume_").FullName;
        SyncState.LogsDir = _tmpPath;
    }

    public void Dispose()
    {
        _guard.Dispose();
        try
        {
            Directory.Delete(_tmpPath, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static List<JsonObject> MakeRecords(string objectType, int count, int start = 1)
    {
        var records = new List<JsonObject>();
        for (var i = start; i < start + count; i++)
        {
            records.Add(new JsonObject
            {
                ["Id"] = $"{objectType[..3]}{i:D4}",
                ["objectType"] = objectType,
                ["url"] = $"https://sf/{i}",
            });
        }
        return records;
    }

    private static void InstallHooks(List<JsonObject> records)
    {
        Ingest.IterObjectChunksHook = (_, _, _, _) => CheckpointSupport.Chunks(records);
        Ingest.GetObjectConfigHook = _ => AccountCfg;
        Ingest.LegacyAclResolverFactory = (config, _, _) =>
            new CheckpointSupport.FakeAclResolver(
                config, _ => CheckpointSupport.EmptyAclsFor("Account", records));
        Ingest.TransformerFactory = (_, _, _) => new CheckpointSupport.FakeTransformer();
    }

    [Fact]
    public async Task NormalCompletionClearsCheckpoint()
    {
        // After a full successful run, the checkpoint file should be deleted.
        var cfg = CheckpointSupport.WithDebugObjectType(TestFixtures.TestConfig(), "Account");
        var connectorId = cfg.Connector.Id;

        // Pre-plant a checkpoint to verify it gets cleared
        SyncState.WriteCheckpoint(connectorId, null, "Account", 2);
        Assert.NotNull(SyncState.ReadCheckpoint(connectorId));

        var records = MakeRecords("Account", 3);
        InstallHooks(records);

        var client = new CheckpointSupport.FakeGraphClient
        {
            OnBatch = _ => new List<JsonObject> { CheckpointSupport.Resp("0", 200) },
        };

        var stats = await Ingest.IngestContentAsync(cfg, client, since: null, dashboard: null);
        Assert.Equal(3, stats.TotalFetched);
        // Checkpoint should be cleared after normal completion
        Assert.Null(SyncState.ReadCheckpoint(connectorId));
    }

    [Fact]
    public async Task ServiceStopPreservesCheckpoint()
    {
        // New in the C# port: a Windows-service stop (Infrastructure.ServiceStop)
        // must behave exactly like Ctrl+X — finish the chunk, keep the checkpoint.
        var cfg = CheckpointSupport.WithDebugObjectType(TestFixtures.TestConfig(), "Account");
        var connectorId = cfg.Connector.Id;

        var records = MakeRecords("Account", 5);
        InstallHooks(records);
        Ingest.GetObjectCountsHook = (_, _) => Task.FromResult(new Dictionary<string, int>());

        var client = new CheckpointSupport.FakeGraphClient
        {
            OnBatch = _ =>
            {
                // The SCM stop arrives while the Graph push is in flight.
                Infrastructure.ServiceStop.Request();
                return new List<JsonObject> { CheckpointSupport.Resp("0", 200) };
            },
        };

        try
        {
            var stats = await Ingest.IngestContentAsync(cfg, client, since: null, dashboard: null);

            // Account was processed (5 records)
            Assert.Equal(5, stats.TotalFetched);

            // Checkpoint must still exist (NOT cleared)
            var cp = SyncState.ReadCheckpoint(connectorId);
            Assert.NotNull(cp);
            Assert.Equal(1, cp!["completed"]!["Account"]!.GetValue<int>());
        }
        finally
        {
            Infrastructure.ServiceStop.Reset();
        }
    }

    [Fact]
    public async Task CtrlxPreservesCheckpoint()
    {
        // When Ctrl+X is pressed, the checkpoint must NOT be cleared.
        var cfg = CheckpointSupport.WithDebugObjectType(TestFixtures.TestConfig(), "Account");
        var connectorId = cfg.Connector.Id;

        var records = MakeRecords("Account", 5);
        InstallHooks(records);
        // Python patches salesforce.api_client.get_object_counts to return {}.
        Ingest.GetObjectCountsHook = (_, _) => Task.FromResult(new Dictionary<string, int>());

        var client = new CheckpointSupport.FakeGraphClient
        {
            OnBatch = _ => new List<JsonObject> { CheckpointSupport.Resp("0", 200) },
        };

        // Simulate Ctrl+X: set stop_requested during Graph push.
        var dashboard = new CheckpointSupport.StopOnChunkIngestedDashboard();

        var stats = await Ingest.IngestContentAsync(cfg, client, since: null, dashboard: dashboard);

        // Account was processed (5 records)
        Assert.Equal(5, stats.TotalFetched);

        // Checkpoint must still exist (NOT cleared)
        var cp = SyncState.ReadCheckpoint(connectorId);
        Assert.NotNull(cp);
        Assert.Equal(1, cp!["completed"]!["Account"]!.GetValue<int>());
    }

    [Fact]
    public async Task ResumeSkipsCheckpointedChunks()
    {
        // After a Ctrl+X stop, the next run should skip already-completed chunks.
        var cfg = CheckpointSupport.WithDebugObjectType(TestFixtures.TestConfig(), "Account");
        var connectorId = cfg.Connector.Id;

        // Pre-plant a checkpoint: Account chunk 1 was completed
        SyncState.WriteCheckpoint(connectorId, null, "Account", 1);

        // Same records as before (Account chunk 1 = 5 records)
        var records = MakeRecords("Account", 5);
        InstallHooks(records);

        var client = new CheckpointSupport.FakeGraphClient
        {
            OnBatch = _ => new List<JsonObject> { CheckpointSupport.Resp("0", 200) },
        };

        var stats = await Ingest.IngestContentAsync(cfg, client, since: null, dashboard: null);

        // The chunk was skipped (checkpointed), so skipped_count should be 5
        Assert.Equal(5, stats.SkippedCount);
        // No new records actually processed through ACL/Graph
        Assert.Equal(0, stats.SuccessCount);

        // Checkpoint cleared after successful completion
        Assert.Null(SyncState.ReadCheckpoint(connectorId));
    }

    [Fact]
    public void FullFlagClearsCheckpoint()
    {
        // The --full flag should explicitly clear the checkpoint.
        var connectorId = TestFixtures.TestConfig().Connector.Id;
        SyncState.WriteCheckpoint(connectorId, null, "Account", 5);
        Assert.NotNull(SyncState.ReadCheckpoint(connectorId));

        SyncState.ClearCheckpoint(connectorId);
        Assert.Null(SyncState.ReadCheckpoint(connectorId));
    }

    [Fact]
    public async Task StaleCheckpointIgnoredWhenSinceChanges()
    {
        // A checkpoint with a different `since` value should be ignored (not used for skipping).
        var cfg = CheckpointSupport.WithDebugObjectType(TestFixtures.TestConfig(), "Account");
        var connectorId = cfg.Connector.Id;

        // Checkpoint from a previous run with since=None
        SyncState.WriteCheckpoint(connectorId, null, "Account", 3);

        var records = MakeRecords("Account", 5);
        InstallHooks(records);

        var client = new CheckpointSupport.FakeGraphClient
        {
            OnBatch = _ => new List<JsonObject> { CheckpointSupport.Resp("0", 200) },
        };

        // Run with a different `since` — checkpoint should NOT match
        var since = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var stats = await Ingest.IngestContentAsync(cfg, client, since: since, dashboard: null);

        // All 5 records processed (checkpoint was ignored because since changed)
        Assert.Equal(5, stats.TotalFetched);
        Assert.Equal(0, stats.SkippedCount);
    }
}
