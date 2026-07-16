// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Logic tests for the HA work-source seam in Graph.Ingest — no SQL Server
// required. A fake IObjectWorkSource drives IngestContentAsync over two
// object types via Ingest.ObjectWorkSourceFactory, mirroring how
// HaCoordinator.CreateWorkSource() is installed by the commands in HA mode.

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Config;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests;

[Collection("IngestGlobalState")]
public class HaIngestWorkSourceTests : IDisposable
{
    private readonly CheckpointSupport.IngestHookGuard _guard = new();
    private readonly string _tmpPath;

    public HaIngestWorkSourceTests()
    {
        // Redirect all sync state files to a temp directory.
        _tmpPath = Directory.CreateTempSubdirectory("ha_worksource_").FullName;
        SyncState.LogsDir = _tmpPath;
    }

    public void Dispose()
    {
        // IngestHookGuard predates the HA seam — restore it explicitly.
        Ingest.ObjectWorkSourceFactory = null;
        _guard.Dispose();
        try
        {
            Directory.Delete(_tmpPath, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    /// <summary>In-memory claim queue standing in for HaCoordinator's SQL claims.</summary>
    private sealed class FakeWorkSource : IObjectWorkSource
    {
        private readonly Queue<string> _pending;
        private readonly object _lock = new();

        public List<string> HeartbeatsStarted { get; } = new();

        public List<(string ObjectType, bool Succeeded)> Completed { get; } = new();

        public FakeWorkSource(params string[] objectTypes)
        {
            _pending = new Queue<string>(objectTypes);
        }

        public Task<string?> ClaimNextAsync()
        {
            lock (_lock)
            {
                return Task.FromResult(_pending.Count > 0 ? _pending.Dequeue() : null);
            }
        }

        public IDisposable BeginHeartbeat(string objectType)
        {
            lock (_lock)
            {
                HeartbeatsStarted.Add(objectType);
            }
            return new NoopDisposable();
        }

        public Task CompleteAsync(string objectType, bool succeeded)
        {
            lock (_lock)
            {
                Completed.Add((objectType, succeeded));
            }
            return Task.CompletedTask;
        }
    }

    private static List<JsonObject> MakeRecords(string objectType, int count)
    {
        var records = new List<JsonObject>();
        for (var i = 1; i <= count; i++)
        {
            records.Add(new JsonObject
            {
                ["Id"] = $"{objectType[..3]}{i:D4}",
                ["objectType"] = objectType,
                ["url"] = $"https://sf/{objectType}/{i}",
            });
        }
        return records;
    }

    private static void InstallHooks(Dictionary<string, List<JsonObject>> recordsByType)
    {
        Ingest.GetObjectConfigHook = objectType =>
            new SalesforceObjectConfig(ObjectType: objectType, Fields: new[] { "Id" });
        Ingest.IterObjectChunksHook = (_, objCfg, _, _) =>
            CheckpointSupport.Chunks(recordsByType[objCfg.ObjectType]);
        Ingest.LegacyAclResolverFactory = (config, _, _) => new CheckpointSupport.FakeAclResolver(
            config,
            request => request.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToDictionary(
                    record => record["Id"]!.GetValue<string>(),
                    _ => new List<Dictionary<string, string>>())));
        Ingest.TransformerFactory = (_, _, _) => new CheckpointSupport.FakeTransformer();
    }

    [Fact]
    public async Task WorkSourceDrivesBothObjectTypesAndCompletesClaims()
    {
        // Two claimed object types must both be ingested, each with a heartbeat
        // scope and a claims-complete callback (status done).
        var cfg = CheckpointSupport.WithDebugObjectType(TestFixtures.TestConfig(), "Account");
        InstallHooks(new Dictionary<string, List<JsonObject>>
        {
            ["Account"] = MakeRecords("Account", 2),
            ["Contact"] = MakeRecords("Contact", 3),
        });

        var workSource = new FakeWorkSource("Account", "Contact");
        Ingest.ObjectWorkSourceFactory = (_, _) => Task.FromResult<IObjectWorkSource>(workSource);

        var client = new CheckpointSupport.FakeGraphClient
        {
            OnBatch = payload => payload
                .Select(request => CheckpointSupport.Resp(request["id"]!.ToString(), 200))
                .ToList(),
        };

        var stats = await Ingest.IngestContentAsync(cfg, client, since: null, dashboard: null);

        // Both object types were ingested even though the static list only had "Account".
        Assert.Equal(5, stats.TotalFetched);
        Assert.Equal(5, stats.SuccessCount);
        Assert.Equal(0, stats.FailedCount);
        Assert.Equal(2, stats.ObjectTypeCounts["Account"]);
        Assert.Equal(3, stats.ObjectTypeCounts["Contact"]);

        // A heartbeat scope was opened per claimed object.
        Assert.Equal(new[] { "Account", "Contact" }, workSource.HeartbeatsStarted);

        // The claims-complete callback fired once per object, status done.
        Assert.Equal(2, workSource.Completed.Count);
        Assert.Contains(("Account", true), workSource.Completed);
        Assert.Contains(("Contact", true), workSource.Completed);
    }

    [Fact]
    public async Task WorkerExceptionCompletesClaimAsFailed()
    {
        // An object worker crash must complete that claim with status failed and
        // must not stop the other claimed objects from being ingested.
        var cfg = CheckpointSupport.WithDebugObjectType(TestFixtures.TestConfig(), "Account");
        var recordsByType = new Dictionary<string, List<JsonObject>>
        {
            ["Account"] = MakeRecords("Account", 2),
            ["Contact"] = MakeRecords("Contact", 3),
        };
        InstallHooks(recordsByType);
        Ingest.IterObjectChunksHook = (_, objCfg, _, _) =>
            objCfg.ObjectType == "Contact"
                ? throw new InvalidOperationException("boom")
                : CheckpointSupport.Chunks(recordsByType[objCfg.ObjectType]);

        var workSource = new FakeWorkSource("Account", "Contact");
        Ingest.ObjectWorkSourceFactory = (_, _) => Task.FromResult<IObjectWorkSource>(workSource);

        var client = new CheckpointSupport.FakeGraphClient
        {
            OnBatch = payload => payload
                .Select(request => CheckpointSupport.Resp(request["id"]!.ToString(), 200))
                .ToList(),
        };

        var stats = await Ingest.IngestContentAsync(cfg, client, since: null, dashboard: null);

        // Account still ingested; Contact crashed before fetching anything.
        Assert.Equal(2, stats.TotalFetched);
        Assert.Equal(2, stats.SuccessCount);

        // Claim completion statuses: Account done, Contact failed.
        Assert.Equal(2, workSource.Completed.Count);
        Assert.Contains(("Account", true), workSource.Completed);
        Assert.Contains(("Contact", false), workSource.Completed);

        // The crash is recorded in the dead-letter file for operators.
        var deadLetters = SyncState.ReadFailedRecords(cfg.Connector.Id);
        Assert.Contains(deadLetters, record =>
            record["item_id"]?.ToString() == "WORKER_CRASH"
            && record["object_type"]?.ToString() == "Contact");
    }

    [Fact]
    public async Task NullFactoryKeepsDefaultBehavior()
    {
        // With the hook left null the static schema list drives ingestion and the
        // checkpoint is cleared on completion, exactly as before the HA seam.
        var cfg = CheckpointSupport.WithDebugObjectType(TestFixtures.TestConfig(), "Account");
        var records = MakeRecords("Account", 3);
        InstallHooks(new Dictionary<string, List<JsonObject>> { ["Account"] = records });
        Assert.Null(Ingest.ObjectWorkSourceFactory);

        var client = new CheckpointSupport.FakeGraphClient
        {
            OnBatch = payload => payload
                .Select(request => CheckpointSupport.Resp(request["id"]!.ToString(), 200))
                .ToList(),
        };

        var stats = await Ingest.IngestContentAsync(cfg, client, since: null, dashboard: null);

        Assert.Equal(3, stats.TotalFetched);
        Assert.Equal(3, stats.SuccessCount);
        Assert.Null(SyncState.ReadCheckpoint(cfg.Connector.Id));
    }
}
