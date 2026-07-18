// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for silent drop fixes across the ingestion pipeline — port of
// tests/test_silent_drop_fixes.py.
//
// Covers every scenario where items could previously vanish without a trace:
// 1. Null record ID in converter
// 2. transform_record exception / empty return
// 3. ACL resolution failure → dead-letter logging
// 4. Worker thread crash → stats + dead-letter tracking
// 5. Sub-batch dispatcher crash → remaining sub-batches survive
// 6. Pipeline wait crash → remaining chunks survive
// 7. iter_object_chunks mid-pagination failure → partial buffer yielded
// 8. append_failed_records I/O failure → fallback logging
// 9. Count reconciliation — mismatch detection
// 10. Empty/null Graph batch response → all items marked failed
// 11. Missing items in Graph batch response → unmatched items marked failed

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Config;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Item;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests;

// ── Shared helpers ────────────────────────────────────────────────────────────

internal static class SilentDropSupport
{
    internal static readonly SalesforceObjectConfig AccountCfg =
        new(ObjectType: "Account", Fields: new[] { "Id" });

    /// <summary>Mirrors Python's builtin ValueError so error-text assertions match.</summary>
    internal sealed class ValueError : Exception
    {
        public ValueError(string message) : base(message) { }
    }

    /// <summary>Mirrors Python's builtin RuntimeError.</summary>
    internal sealed class RuntimeError : Exception
    {
        public RuntimeError(string message) : base(message) { }
    }

    /// <summary>Mirrors Python's builtin ConnectionError.</summary>
    internal sealed class ConnectionError : Exception
    {
        public ConnectionError(string message) : base(message) { }
    }

    internal static JsonObject MakeRecord(string recordId, string objectType = "Account") => new()
    {
        ["Id"] = recordId,
        ["objectType"] = objectType,
        ["url"] = $"https://sf.com/{recordId}",
    };

    /// <summary>Read and parse the dead-letter JSONL file ([] when it does not exist).</summary>
    internal static List<JsonObject> ReadDeadLetter(string path)
    {
        var entries = new List<JsonObject>();
        if (!File.Exists(path))
            return entries;
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Trim().Length > 0)
                entries.Add(JsonNode.Parse(line)!.AsObject());
        }
        return entries;
    }
}

// =============================================================================
// 1. Null record ID in converter
// =============================================================================

/// <summary>item/converter.py — records without an Id are logged, not silently dropped.</summary>
public class NullRecordIdConverterTests
{
    /// <summary>Base record fields shared by the null-ID scenarios.</summary>
    private static JsonObject BaseRecord(string name) => new()
    {
        ["Name"] = name,
        ["IsDeleted"] = false,
        ["objectType"] = "Account",
        ["attributes"] = new JsonObject { ["type"] = "Account" },
        ["OwnerId"] = "005abc",
        ["Owner"] = new JsonObject
        {
            ["Name"] = "User",
            ["UserRole"] = new JsonObject { ["Id"] = "r1", ["ParentRoleId"] = null },
        },
        ["CreatedDate"] = "2024-01-01T00:00:00.000+0000",
        ["LastModifiedDate"] = "2024-06-01T00:00:00.000+0000",
        ["CreatedById"] = "005abc",
        ["CreatedBy"] = new JsonObject { ["Name"] = "Creator" },
        ["LastModifiedById"] = "005abc",
        ["LastModifiedBy"] = new JsonObject { ["Name"] = "Modifier" },
    };

    private static List<JsonObject> Convert(JsonObject record)
    {
        var converter = new SalesforceConverter(instanceUrl: "https://test.my.salesforce.com");
        return converter.Convert(
            new JsonObject { ["records"] = new JsonArray(record) },
            objectName: "Account");
    }

    [Fact]
    public void RecordWithoutIdReturnsNone()
    {
        // No "Id" key at all
        var record = BaseRecord("Ghost Record");
        var items = Convert(record);
        Assert.Empty(items);
    }

    [Fact]
    public void RecordWithNoneIdReturnsNone()
    {
        var record = BaseRecord("Null ID Record");
        record["Id"] = null;
        var items = Convert(record);
        Assert.Empty(items);
    }

    [Fact]
    public void RecordWithEmptyIdReturnsNone()
    {
        var record = BaseRecord("Empty ID");
        record["Id"] = "";
        var items = Convert(record);
        Assert.Empty(items);
    }

    [Fact]
    public void ValidRecordsNotAffected()
    {
        var goodRecord = BaseRecord("Good Record");
        goodRecord["Id"] = "001valid";
        var items = Convert(goodRecord);
        Assert.Single(items);
        Assert.Equal("001valid", items[0]["id"]!.GetValue<string>());
    }
}

// =============================================================================
// 2. transform_record exception / empty return → dead-letter
// =============================================================================

/// <summary>graph/ingest.py — transform_record failures are caught and written to dead-letter.</summary>
public class TransformRecordFailureTests
{
    private readonly AppConfig _config;
    private readonly string _dlPath;
    private readonly IngestionStats _stats;
    private readonly object _statsLock;
    private readonly CheckpointSupport.FakeGraphClient _client;
    private readonly AdaptiveConcurrency _concurrency;

    public TransformRecordFailureTests()
    {
        _config = CheckpointSupport.WithDebugObjectType(TestFixtures.TestConfig(), "Account");
        _dlPath = Path.Combine(Directory.CreateTempSubdirectory("silent_drop_").FullName, "dl.jsonl");
        _stats = new IngestionStats();
        _statsLock = new object();
        _client = new CheckpointSupport.FakeGraphClient();
        _concurrency = new AdaptiveConcurrency(1);
    }

    [Fact]
    public async Task TransformExceptionWritesToDeadLetter()
    {
        // If transform_record raises, the item is marked failed in stats and dead-letter.
        var transformer = new CheckpointSupport.FakeTransformer
        {
            OnTransform = (_, _) => throw new SilentDropSupport.ValueError("bad field type"),
        };
        var records = new List<JsonObject> { SilentDropSupport.MakeRecord("001") };
        var aclMap = new Dictionary<string, List<Dictionary<string, string>>>
        {
            ["001"] = new()
            {
                new() { ["accessType"] = "grant", ["type"] = "everyone", ["value"] = "e" },
            },
        };

        await Ingest.IngestChunkGraphBatchAsync(
            _config, _client, transformer, records, aclMap,
            _stats, 20, dlPath: _dlPath, objectType: "Account",
            concurrency: _concurrency, statsLock: _statsLock);

        var failures = SilentDropSupport.ReadDeadLetter(_dlPath);
        var failure = Assert.Single(failures);
        Assert.Equal("001", failure["item_id"]!.GetValue<string>());
        Assert.Contains("ValueError", failure["error"]!.GetValue<string>());

        Assert.Equal(1, _stats.FailedCount);
    }

    [Fact]
    public async Task TransformEmptyReturnWritesToDeadLetter()
    {
        // If transform_record returns [], the record is marked failed.
        var transformer = new CheckpointSupport.FakeTransformer
        {
            OnTransform = (_, _) => new List<JsonObject>(),
        };
        var records = new List<JsonObject> { SilentDropSupport.MakeRecord("002") };
        var aclMap = new Dictionary<string, List<Dictionary<string, string>>>
        {
            ["002"] = new(),
        };

        await Ingest.IngestChunkGraphBatchAsync(
            _config, _client, transformer, records, aclMap,
            _stats, 20, dlPath: _dlPath, objectType: "Account",
            concurrency: _concurrency, statsLock: _statsLock);

        var failures = SilentDropSupport.ReadDeadLetter(_dlPath);
        var failure = Assert.Single(failures);
        Assert.Equal("002", failure["item_id"]!.GetValue<string>());
        Assert.Contains("empty", failure["error"]!.GetValue<string>().ToLowerInvariant());

        Assert.Equal(1, _stats.FailedCount);
    }

    [Fact]
    public async Task TransformFailureDoesNotBlockRemainingRecords()
    {
        // One record's transform failure must not prevent others from being ingested.
        var callCount = 0;
        var transformer = new CheckpointSupport.FakeTransformer
        {
            OnTransform = (record, _) =>
            {
                Interlocked.Increment(ref callCount);
                var recordId = record["Id"]!.GetValue<string>();
                if (recordId == "BAD")
                    throw new SilentDropSupport.RuntimeError("corrupt record");
                return new List<JsonObject> { CheckpointSupport.Item(recordId) };
            },
        };
        var records = new List<JsonObject>
        {
            SilentDropSupport.MakeRecord("OK1"),
            SilentDropSupport.MakeRecord("BAD"),
            SilentDropSupport.MakeRecord("OK2"),
        };
        var aclMap = records.ToDictionary(
            r => r["Id"]!.GetValue<string>(),
            _ => new List<Dictionary<string, string>>());

        _client.OnBatch = _ => new List<JsonObject>
        {
            CheckpointSupport.Resp("0", 200),
            CheckpointSupport.Resp("1", 200),
        };

        await Ingest.IngestChunkGraphBatchAsync(
            _config, _client, transformer, records, aclMap,
            _stats, 20, dlPath: _dlPath, objectType: "Account",
            concurrency: _concurrency, statsLock: _statsLock);

        // BAD failed, OK1 and OK2 should have been sent
        Assert.Equal(1, _stats.FailedCount);
        Assert.Equal(2, _stats.SuccessCount);
        Assert.Equal(3, callCount);  // All three were attempted
    }
}

// =============================================================================
// 3. ACL resolution failure → dead-letter logging
// =============================================================================

/// <summary>graph/ingest.py — ACL resolution crash logs affected items to dead-letter.</summary>
[Collection("IngestGlobalState")]
public class AclFallbackDeadLetterTests : IDisposable
{
    private readonly CheckpointSupport.IngestHookGuard _guard = new();

    public AclFallbackDeadLetterTests()
    {
        SyncState.LogsDir = Directory.CreateTempSubdirectory("silent_drop_").FullName;
    }

    public void Dispose() => _guard.Dispose();

    [Fact]
    public async Task AclCrashWritesAllAffectedItemsToDeadLetter()
    {
        var cfg = CheckpointSupport.WithDebugObjectType(TestFixtures.TestConfig(), "Account");
        var records = Enumerable.Range(0, 5)
            .Select(i => SilentDropSupport.MakeRecord($"00{i}"))
            .ToList();
        Ingest.IterObjectChunksHook = (_, _, _, _) => CheckpointSupport.Chunks(records);
        Ingest.GetObjectConfigHook = _ => SilentDropSupport.AccountCfg;
        Ingest.LegacyAclResolverFactory = (config, _, _) => new CheckpointSupport.FakeAclResolver(
            config, _ => throw new SilentDropSupport.RuntimeError("ACL engine crash"));
        Ingest.TransformerFactory = (_, _, _) => new CheckpointSupport.FakeTransformer();

        var client = new CheckpointSupport.FakeGraphClient
        {
            OnBatch = _ => new List<JsonObject> { CheckpointSupport.Resp("0", 200) },
        };

        var stats = await Ingest.IngestContentAsync(cfg, client);
        Assert.True(stats.AclFallbackUsed);

        // Verify the dead-letter file received all 5 affected item IDs
        var entries = SilentDropSupport.ReadDeadLetter(SyncState.FailedRecordsPath(cfg.Connector.Id));
        var aclFallbackEntries = entries
            .Where(e => e["error"]!.GetValue<string>().Contains("ACL"))
            .ToList();
        Assert.True(aclFallbackEntries.Count >= 1);
        var allIds = aclFallbackEntries.Select(e => e["item_id"]!.GetValue<string>()).ToList();
        foreach (var record in records)
            Assert.Contains(record["Id"]!.GetValue<string>(), allIds);
    }
}

// =============================================================================
// 4. Worker thread crash → stats + dead-letter
// =============================================================================

/// <summary>graph/ingest.py — worker thread exception updates stats and dead-letter.</summary>
[Collection("IngestGlobalState")]
public class WorkerThreadCrashTests : IDisposable
{
    private readonly CheckpointSupport.IngestHookGuard _guard = new();
    private readonly string _dlPath;

    public WorkerThreadCrashTests()
    {
        SyncState.LogsDir = Directory.CreateTempSubdirectory("silent_drop_").FullName;
        _dlPath = Path.Combine(SyncState.LogsDir, "dl.jsonl");
    }

    public void Dispose() => _guard.Dispose();

    private static async IAsyncEnumerable<List<JsonObject>> CrashingIter(List<JsonObject> records)
    {
        // iter_object_chunks yields one good chunk then crashes —
        // this simulates a mid-stream failure that escapes all chunk-level
        // try/excepts inside _ingest_single_object_type.
        yield return records;
        await Task.CompletedTask;
        throw new SilentDropSupport.RuntimeError("Unexpected crash in SF fetch after first chunk");
    }

    [Fact]
    public async Task WorkerCrashRecordsFailureInStats()
    {
        // If _ingest_single_object_type raises, failed_count reflects fetched records.
        var cfg = CheckpointSupport.WithDebugObjectType(TestFixtures.TestConfig(), "Account");
        var records = Enumerable.Range(0, 3)
            .Select(i => SilentDropSupport.MakeRecord($"00{i}"))
            .ToList();

        Ingest.IterObjectChunksHook = (_, _, _, _) => CrashingIter(records);
        Ingest.GetObjectConfigHook = _ => SilentDropSupport.AccountCfg;
        Ingest.LegacyAclResolverFactory = (config, _, _) => new CheckpointSupport.FakeAclResolver(
            config, _ => CheckpointSupport.EmptyAclsFor("Account", records));
        Ingest.TransformerFactory = (_, _, _) => new CheckpointSupport.FakeTransformer();

        var client = new CheckpointSupport.FakeGraphClient
        {
            OnBatch = _ => Enumerable.Range(0, 3)
                .Select(i => CheckpointSupport.Resp(i.ToString(), 200))
                .ToList(),
        };

        await Ingest.IngestContentAsync(cfg, client);

        // The worker crashed — a WORKER_CRASH entry must be in the dead-letter file
        var entries = SilentDropSupport.ReadDeadLetter(SyncState.FailedRecordsPath(cfg.Connector.Id));
        var workerCrashEntries = entries
            .Where(e => e["item_id"]!.GetValue<string>().Contains("WORKER_CRASH"))
            .ToList();
        Assert.True(workerCrashEntries.Count >= 1);
    }
}

// =============================================================================
// 5. Sub-batch dispatcher crash → remaining sub-batches survive
// =============================================================================

/// <summary>graph/ingest.py — one sub-batch crash doesn't kill remaining sub-batches.</summary>
public class SubBatchDispatcherCrashTests
{
    private readonly AppConfig _config;
    private readonly string _dlPath;
    private readonly IngestionStats _stats;
    private readonly object _statsLock;
    private readonly AdaptiveConcurrency _concurrency;

    public SubBatchDispatcherCrashTests()
    {
        _config = CheckpointSupport.WithDebugObjectType(TestFixtures.TestConfig(), "Account");
        _dlPath = Path.Combine(Directory.CreateTempSubdirectory("silent_drop_").FullName, "dl.jsonl");
        _stats = new IngestionStats();
        _statsLock = new object();
        _concurrency = new AdaptiveConcurrency(1);
    }

    [Fact]
    public async Task BatchPostExceptionContinuesRemainingBatches()
    {
        // If the Graph $batch POST throws for one sub-batch, the next sub-batch still runs.
        var transformer = new CheckpointSupport.FakeTransformer
        {
            OnTransform = (record, _) =>
                new List<JsonObject> { CheckpointSupport.Item(record["Id"]!.GetValue<string>()) },
        };
        // 25 records → 2 sub-batches of 20 + 5
        var records = Enumerable.Range(0, 25)
            .Select(i => SilentDropSupport.MakeRecord($"R{i:D3}"))
            .ToList();
        var aclMap = records.ToDictionary(
            r => r["Id"]!.GetValue<string>(),
            _ => new List<Dictionary<string, string>>());

        var callCount = 0;
        var client = new CheckpointSupport.FakeGraphClient();
        client.OnBatch = payload =>
        {
            var call = Interlocked.Increment(ref callCount);
            if (call == 1)
                throw new SilentDropSupport.ConnectionError("network timeout on first sub-batch");
            return Enumerable.Range(0, payload.Count)
                .Select(i => CheckpointSupport.Resp(i.ToString(), 200))
                .ToList();
        };

        await Ingest.IngestChunkGraphBatchAsync(
            _config, client, transformer, records, aclMap,
            _stats, 20, dlPath: _dlPath, objectType: "Account",
            concurrency: _concurrency, statsLock: _statsLock);

        // First sub-batch (20 items) failed, second (5 items) should have succeeded
        Assert.Equal(20, _stats.FailedCount);
        Assert.Equal(5, _stats.SuccessCount);
        Assert.Equal(2, callCount);  // Both sub-batches were attempted
    }
}

// =============================================================================
// 6. Pipeline wait crash → remaining chunks survive
// =============================================================================

/// <summary>graph/ingest.py — Graph upload failure for chunk N doesn't kill chunk N+1.</summary>
[Collection("IngestGlobalState")]
public class PipelineWaitCrashTests : IDisposable
{
    private readonly CheckpointSupport.IngestHookGuard _guard = new();

    public PipelineWaitCrashTests()
    {
        SyncState.LogsDir = Directory.CreateTempSubdirectory("silent_drop_").FullName;
    }

    public void Dispose() => _guard.Dispose();

    [Fact]
    public async Task ChunkFailureDoesNotAbortRemainingChunks()
    {
        var cfg = CheckpointSupport.WithDebugObjectType(TestFixtures.TestConfig(), "Account");
        var chunk1 = new List<JsonObject>
        {
            SilentDropSupport.MakeRecord("C1_001"),
            SilentDropSupport.MakeRecord("C1_002"),
        };
        var chunk2 = new List<JsonObject>
        {
            SilentDropSupport.MakeRecord("C2_001"),
            SilentDropSupport.MakeRecord("C2_002"),
        };
        Ingest.IterObjectChunksHook = (_, _, _, _) => CheckpointSupport.Chunks(chunk1, chunk2);
        Ingest.GetObjectConfigHook = _ => SilentDropSupport.AccountCfg;
        Ingest.LegacyAclResolverFactory = (config, _, _) => new CheckpointSupport.FakeAclResolver(
            config, _ => CheckpointSupport.EmptyAclsFor("Account", chunk1.Concat(chunk2)));
        // transform_record returns items with correct IDs
        Ingest.TransformerFactory = (_, _, _) => new CheckpointSupport.FakeTransformer
        {
            OnTransform = (record, _) =>
                new List<JsonObject> { CheckpointSupport.Item(record["Id"]!.GetValue<string>()) },
        };

        var batchCallCount = 0;
        var client = new CheckpointSupport.FakeGraphClient();
        client.OnBatch = payload =>
        {
            var call = Interlocked.Increment(ref batchCallCount);
            if (call == 1)
            {
                // First chunk's Graph push fails
                throw new SilentDropSupport.ConnectionError("Graph API unreachable");
            }
            return Enumerable.Range(0, payload.Count)
                .Select(i => CheckpointSupport.Resp(i.ToString(), 200))
                .ToList();
        };

        var stats = await Ingest.IngestContentAsync(cfg, client);

        // Both chunks were fetched
        Assert.Equal(4, stats.TotalFetched);
        // Second chunk should have succeeded despite first chunk's failure
        Assert.True(batchCallCount >= 2);
        Assert.True(stats.SuccessCount >= 2);
    }
}

// =============================================================================
// 7. iter_object_chunks mid-pagination failure → partial buffer yielded
// =============================================================================

/// <summary>salesforce/api_client.py — partial buffer is yielded when fetch crashes mid-stream.</summary>
[Collection("IngestGlobalState")]  // ApiClient.SfSession is process-global
public class IterObjectChunksMidPaginationFailureTests
{
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    private static HttpResponseMessage Json(int statusCode, JsonNode payload) => new((HttpStatusCode)statusCode)
    {
        Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    /// <summary>
    /// Minimal config (Python uses a MagicMock cfg). The empty connector ID disables
    /// the SQLite field cache, mirroring the mock's inert cache helpers.
    /// </summary>
    private static AppConfig MakeCfg() => new()
    {
        Connector = new ConnectorSettings
        {
            Id = "",
            Salesforce = new SalesforceSettings
            {
                InstanceUrl = "https://test.my.salesforce.com",
                ApiVersion = "v60.0",
                ClientId = "client-id",
                ClientSecret = "client-secret",
            },
        },
        Tuning = new TuningSettings { SalesforceQueryLimit = 0 },
        DebugItemId = null,
    };

    private static JsonArray RecordsJson(int count, int start = 0)
    {
        var records = new JsonArray();
        for (var i = start; i < start + count; i++)
            records.Add(new JsonObject { ["Id"] = $"00{i}" });
        return records;
    }

    private static async Task<List<List<JsonObject>>> CollectChunksAsync(
        AppConfig cfg, SalesforceObjectConfig objConfig, int chunkSize)
    {
        var chunks = new List<List<JsonObject>>();
        await foreach (var chunk in ApiClient.IterObjectChunksAsync(cfg, objConfig, null, chunkSize))
            chunks.Add(chunk);
        return chunks;
    }

    private static async Task<T> WithSfSessionAsync<T>(HttpMessageHandler handler, Func<Task<T>> action)
    {
        var original = ApiClient.SfSession;
        ApiClient.SfSession = new HttpClient(handler);
        try
        {
            return await action();
        }
        finally
        {
            ApiClient.SfSession = original;
        }
    }

    private static HttpResponseMessage TokenResponse()
        => Json(200, new JsonObject { ["access_token"] = "token" });

    [Fact]
    public async Task PartialBufferYieldedOnFetchCrash()
    {
        // If fetch_salesforce_records crashes after 3 records, those 3 are still yielded.
        var cfg = MakeCfg();
        var objConfig = new SalesforceObjectConfig(ObjectType: "Account", Fields: new[] { "Id" });

        var handler = new RoutingHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/services/oauth2/token"))
                return TokenResponse();
            if (url.Contains("crash-page"))
            {
                // Salesforce connection reset mid-pagination
                return Json(400, new JsonArray(new JsonObject
                {
                    ["message"] = "Salesforce connection reset",
                    ["errorCode"] = "UNKNOWN_EXCEPTION",
                }));
            }
            return Json(200, new JsonObject
            {
                ["records"] = RecordsJson(3),
                ["nextRecordsUrl"] = "/crash-page",
            });
        });

        var chunks = await WithSfSessionAsync(handler, () => CollectChunksAsync(cfg, objConfig, 100));

        // All 3 records should be in one partial chunk
        Assert.Single(chunks);
        Assert.Equal(3, chunks[0].Count);
    }

    [Fact]
    public async Task FullChunksBeforeCrashArePreserved()
    {
        // Records yielded before the crash in full chunks are preserved.
        var cfg = MakeCfg();
        var objConfig = new SalesforceObjectConfig(ObjectType: "Account", Fields: new[] { "Id" });

        var handler = new RoutingHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/services/oauth2/token"))
                return TokenResponse();
            if (url.Contains("crash-page"))
            {
                // 5 records, then crash — with chunk_size=3, we get chunk of 3 + partial of 2
                return Json(400, new JsonArray(new JsonObject
                {
                    ["message"] = "SOQL timeout",
                    ["errorCode"] = "UNKNOWN_EXCEPTION",
                }));
            }
            return Json(200, new JsonObject
            {
                ["records"] = RecordsJson(5),
                ["nextRecordsUrl"] = "/crash-page",
            });
        });

        var chunks = await WithSfSessionAsync(handler, () => CollectChunksAsync(cfg, objConfig, 3));

        Assert.Equal(2, chunks.Count);
        Assert.Equal(3, chunks[0].Count);  // Full chunk
        Assert.Equal(2, chunks[1].Count);  // Partial buffer after crash
    }

    [Fact]
    public async Task SkipObjectErrorStillPropagates()
    {
        // _SkipObjectError must propagate (it's not a data loss — the object doesn't exist).
        var cfg = MakeCfg();
        var objConfig = new SalesforceObjectConfig(ObjectType: "FakeObj", Fields: new[] { "Id" });

        var handler = new RoutingHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/services/oauth2/token"))
                return TokenResponse();
            return Json(400, new JsonArray(new JsonObject
            {
                ["message"] = "FakeObj not available",
                ["errorCode"] = "INVALID_TYPE",
            }));
        });

        await Assert.ThrowsAsync<_SkipObjectError>(
            () => WithSfSessionAsync(handler, () => CollectChunksAsync(cfg, objConfig, 100)));
    }

    [Fact]
    public async Task NoCrashYieldsAllRecords()
    {
        // Normal operation is not affected by the try/except.
        var cfg = MakeCfg();
        var objConfig = new SalesforceObjectConfig(ObjectType: "Account", Fields: new[] { "Id" });

        var handler = new RoutingHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/services/oauth2/token"))
                return TokenResponse();
            return Json(200, new JsonObject { ["records"] = RecordsJson(7) });
        });

        var chunks = await WithSfSessionAsync(handler, () => CollectChunksAsync(cfg, objConfig, 3));

        Assert.Equal(3, chunks.Count);  // 3 + 3 + 1
        Assert.Equal(3, chunks[0].Count);
        Assert.Equal(3, chunks[1].Count);
        Assert.Single(chunks[2]);
    }
}

// =============================================================================
// 8. append_failed_records I/O failure → fallback to logger
// =============================================================================

/// <summary>config/sync_state.py — I/O errors don't lose failure information.</summary>
[Collection("EnvVars")]  // RequestAndResponseBodiesIncluded pins DEADLETTER_PAYLOAD_MODE
public class AppendFailedRecordsIOErrorTests
{
    private readonly string _tmpPath = Directory.CreateTempSubdirectory("silent_drop_").FullName;

    [Fact]
    public void IoErrorDoesNotRaise()
    {
        // If the dead-letter file can't be written, no exception propagates.
        var badPathGrandParent = Path.Combine(_tmpPath, "nonexistent_subdir");
        Directory.CreateDirectory(badPathGrandParent);
        // This should be a file, not a directory, so writing under it fails
        File.WriteAllText(Path.Combine(badPathGrandParent, "nested"), "block");
        var badFilePath = Path.Combine(badPathGrandParent, "nested", "dl.jsonl");

        // Should not raise — error is logged instead
        SyncState.AppendFailedRecords(
            badFilePath,
            new List<(string, string)> { ("item1", "some error"), ("item2", "another error") },
            "Account");
    }

    [Fact]
    public void SuccessfulWriteCreatesEntries()
    {
        // Normal operation: entries are written correctly.
        var dlPath = Path.Combine(_tmpPath, "dl.jsonl");
        SyncState.AppendFailedRecords(
            dlPath,
            new List<(string, string)> { ("item1", "error1"), ("item2", "error2") },
            "Account");
        var entries = SilentDropSupport.ReadDeadLetter(dlPath);
        Assert.Equal(2, entries.Count);
        var entry1 = entries[0];
        Assert.Equal("item1", entry1["item_id"]!.GetValue<string>());
        Assert.Equal("Account", entry1["object_type"]!.GetValue<string>());
        Assert.Equal("error1", entry1["error"]!.GetValue<string>());
    }

    [Fact]
    public void TupleAndStringFormatsBothWork()
    {
        // Both (id, error) tuples and plain id strings are handled.
        var dlPath = Path.Combine(_tmpPath, "dl.jsonl");
        SyncState.AppendFailedRecords(
            dlPath,
            new List<string> { "id1", "id2" },
            "Contact",
            error: "shared error");
        var entries = SilentDropSupport.ReadDeadLetter(dlPath);
        Assert.Equal(2, entries.Count);
        foreach (var entry in entries)
            Assert.Equal("shared error", entry["error"]!.GetValue<string>());
    }

    [Fact]
    public void EmptyFailuresWritesNothing()
    {
        // Empty list produces no file.
        var dlPath = Path.Combine(_tmpPath, "dl.jsonl");
        SyncState.AppendFailedRecords(dlPath, new List<(string, string)>(), "Account");
        Assert.False(File.Exists(dlPath));
    }

    [Fact]
    public void RequestAndResponseBodiesIncluded()
    {
        // Request/response bodies are included in the JSONL entry. This asserts the
        // VERBATIM property value round-trips, so it pins full mode explicitly (the
        // shipped default is now redacted, which would hash properties.Name).
        var savedMode = Environment.GetEnvironmentVariable(DeadLetterRedaction.ModeEnvVar);
        Environment.SetEnvironmentVariable(DeadLetterRedaction.ModeEnvVar, "full");
        try
        {
            var dlPath = Path.Combine(_tmpPath, "dl.jsonl");
            SyncState.AppendFailedRecords(
                dlPath,
                new List<(string, string)> { ("item1", "error1") },
                "Account",
                requestBodies: new Dictionary<string, JsonNode?>
                {
                    ["item1"] = new JsonObject
                    {
                        ["properties"] = new JsonObject { ["Name"] = "Test" },
                    },
                },
                responseBodies: new Dictionary<string, JsonNode?>
                {
                    ["item1"] = new JsonObject
                    {
                        ["status"] = 400,
                        ["body"] = new JsonObject
                        {
                            ["error"] = new JsonObject { ["message"] = "bad" },
                        },
                    },
                });
            var entry = Assert.Single(SilentDropSupport.ReadDeadLetter(dlPath));
            Assert.True(entry.ContainsKey("request_body"));
            Assert.True(entry.ContainsKey("response_body"));
            Assert.Equal("Test", entry["request_body"]!["properties"]!["Name"]!.GetValue<string>());
        }
        finally
        {
            Environment.SetEnvironmentVariable(DeadLetterRedaction.ModeEnvVar, savedMode);
        }
    }
}

// =============================================================================
// 9. Count reconciliation — mismatch detection
// =============================================================================

/// <summary>graph/ingest.py — final count reconciliation detects silent drops.</summary>
[Collection("IngestGlobalState")]
public class CountReconciliationTests : IDisposable
{
    private readonly CheckpointSupport.IngestHookGuard _guard = new();

    public CountReconciliationTests()
    {
        SyncState.LogsDir = Directory.CreateTempSubdirectory("silent_drop_").FullName;
    }

    public void Dispose() => _guard.Dispose();

    private static void InstallHooks(AppConfig cfg, List<JsonObject> records)
    {
        Ingest.IterObjectChunksHook = (_, _, _, _) => CheckpointSupport.Chunks(records);
        Ingest.GetObjectConfigHook = _ => SilentDropSupport.AccountCfg;
        Ingest.LegacyAclResolverFactory = (config, _, _) => new CheckpointSupport.FakeAclResolver(
            config, _ => CheckpointSupport.EmptyAclsFor("Account", records));
        Ingest.TransformerFactory = (_, _, _) => new CheckpointSupport.FakeTransformer
        {
            OnTransform = (record, _) =>
                new List<JsonObject> { CheckpointSupport.Item(record["Id"]!.GetValue<string>()) },
        };
    }

    [Fact]
    public async Task CountsMatchOnFullSuccess()
    {
        var cfg = CheckpointSupport.WithDebugObjectType(TestFixtures.TestConfig(), "Account");
        var records = Enumerable.Range(0, 5)
            .Select(i => SilentDropSupport.MakeRecord($"00{i}"))
            .ToList();
        InstallHooks(cfg, records);

        var client = new CheckpointSupport.FakeGraphClient
        {
            OnBatch = _ => Enumerable.Range(0, 5)
                .Select(i => CheckpointSupport.Resp(i.ToString(), 200))
                .ToList(),
        };

        var stats = await Ingest.IngestContentAsync(cfg, client);
        var accounted = stats.SuccessCount + stats.FailedCount + stats.DeletedCount + stats.SkippedCount;
        Assert.Equal(stats.TotalFetched, accounted);
        Assert.Equal(5, stats.TotalFetched);
    }

    [Fact]
    public async Task CountsMatchWithMixedResults()
    {
        // Mix of successes and failures should still reconcile.
        var cfg = CheckpointSupport.WithDebugObjectType(TestFixtures.TestConfig(), "Account");
        var records = Enumerable.Range(0, 4)
            .Select(i => SilentDropSupport.MakeRecord($"00{i}"))
            .ToList();
        InstallHooks(cfg, records);

        // 2 succeed, 2 fail
        var client = new CheckpointSupport.FakeGraphClient
        {
            OnBatch = _ => new List<JsonObject>
            {
                CheckpointSupport.Resp("0", 200),
                CheckpointSupport.Resp("1", 200),
                new JsonObject { ["id"] = "2", ["status"] = 400, ["body"] = "bad" },
                new JsonObject { ["id"] = "3", ["status"] = 400, ["body"] = "bad" },
            },
        };

        var stats = await Ingest.IngestContentAsync(cfg, client);
        var accounted = stats.SuccessCount + stats.FailedCount + stats.DeletedCount + stats.SkippedCount;
        Assert.Equal(stats.TotalFetched, accounted);
        Assert.Equal(2, stats.SuccessCount);
        Assert.Equal(2, stats.FailedCount);
    }

    [Fact]
    public async Task TransformFailuresIncludedInReconciliation()
    {
        // Records that fail during transform are counted in failed_count.
        var cfg = CheckpointSupport.WithDebugObjectType(TestFixtures.TestConfig(), "Account");
        var records = new List<JsonObject>
        {
            SilentDropSupport.MakeRecord("OK1"),
            SilentDropSupport.MakeRecord("BAD"),
            SilentDropSupport.MakeRecord("OK2"),
        };
        InstallHooks(cfg, records);
        Ingest.TransformerFactory = (_, _, _) => new CheckpointSupport.FakeTransformer
        {
            OnTransform = (record, _) =>
            {
                var recordId = record["Id"]!.GetValue<string>();
                if (recordId == "BAD")
                    throw new SilentDropSupport.ValueError("corrupt");
                return new List<JsonObject> { CheckpointSupport.Item(recordId) };
            },
        };

        var client = new CheckpointSupport.FakeGraphClient
        {
            OnBatch = _ => new List<JsonObject>
            {
                CheckpointSupport.Resp("0", 200),
                CheckpointSupport.Resp("1", 200),
            },
        };

        var stats = await Ingest.IngestContentAsync(cfg, client);
        var accounted = stats.SuccessCount + stats.FailedCount + stats.DeletedCount + stats.SkippedCount;
        Assert.Equal(stats.TotalFetched, accounted);
        Assert.Equal(1, stats.FailedCount);
        Assert.Equal(2, stats.SuccessCount);
    }
}

// =============================================================================
// 10. Empty/null Graph batch response → all items marked failed
// =============================================================================

/// <summary>graph/ingest.py — empty or null $batch response marks all items as failed.</summary>
public class EmptyBatchResponseTests
{
    private readonly AppConfig _config;
    private readonly string _dlPath;
    private readonly IngestionStats _stats;
    private readonly object _statsLock;
    private readonly AdaptiveConcurrency _concurrency;

    public EmptyBatchResponseTests()
    {
        _config = CheckpointSupport.WithDebugObjectType(TestFixtures.TestConfig(), "Account");
        _dlPath = Path.Combine(Directory.CreateTempSubdirectory("silent_drop_").FullName, "dl.jsonl");
        _stats = new IngestionStats();
        _statsLock = new object();
        _concurrency = new AdaptiveConcurrency(1);
    }

    private static CheckpointSupport.FakeTransformer PerRecordTransformer() => new()
    {
        OnTransform = (record, _) =>
            new List<JsonObject> { CheckpointSupport.Item(record["Id"]!.GetValue<string>()) },
    };

    [Fact]
    public async Task EmptyResponseMarksAllItemsFailed()
    {
        // If batch_requests returns [], every item must be counted as failed.
        var transformer = PerRecordTransformer();
        var records = Enumerable.Range(0, 5)
            .Select(i => SilentDropSupport.MakeRecord($"E{i:D3}"))
            .ToList();
        var aclMap = records.ToDictionary(
            r => r["Id"]!.GetValue<string>(),
            _ => new List<Dictionary<string, string>>());

        var client = new CheckpointSupport.FakeGraphClient
        {
            OnBatch = _ => new List<JsonObject>(),  // Empty response
        };

        await Ingest.IngestChunkGraphBatchAsync(
            _config, client, transformer, records, aclMap,
            _stats, 20, dlPath: _dlPath, objectType: "Account",
            concurrency: _concurrency, statsLock: _statsLock);

        // All 5 item IDs should be in the failures
        var entries = SilentDropSupport.ReadDeadLetter(_dlPath);
        Assert.NotEmpty(entries);
        var failedIds = entries.Select(e => e["item_id"]!.GetValue<string>()).ToList();
        foreach (var record in records)
            Assert.Contains(record["Id"]!.GetValue<string>(), failedIds);

        Assert.Equal(5, _stats.FailedCount);
    }

    [Fact]
    public async Task NoneResponseMarksAllItemsFailed()
    {
        // If batch_requests returns None, every item must be counted as failed.
        var transformer = PerRecordTransformer();
        var records = Enumerable.Range(0, 3)
            .Select(i => SilentDropSupport.MakeRecord($"N{i:D3}"))
            .ToList();
        var aclMap = records.ToDictionary(
            r => r["Id"]!.GetValue<string>(),
            _ => new List<Dictionary<string, string>>());

        var client = new CheckpointSupport.FakeGraphClient
        {
            OnBatch = _ => null,  // Null response
        };

        await Ingest.IngestChunkGraphBatchAsync(
            _config, client, transformer, records, aclMap,
            _stats, 20, dlPath: _dlPath, objectType: "Account",
            concurrency: _concurrency, statsLock: _statsLock);

        Assert.Equal(3, _stats.FailedCount);
    }
}

// =============================================================================
// 11. Missing items in Graph batch response → unmatched items marked failed
// =============================================================================

/// <summary>graph/ingest.py — items with no matching response entry are marked failed.</summary>
public class MissingItemsInBatchResponseTests
{
    private readonly AppConfig _config;
    private readonly string _dlPath;
    private readonly IngestionStats _stats;
    private readonly object _statsLock;
    private readonly AdaptiveConcurrency _concurrency;

    public MissingItemsInBatchResponseTests()
    {
        _config = CheckpointSupport.WithDebugObjectType(TestFixtures.TestConfig(), "Account");
        _dlPath = Path.Combine(Directory.CreateTempSubdirectory("silent_drop_").FullName, "dl.jsonl");
        _stats = new IngestionStats();
        _statsLock = new object();
        _concurrency = new AdaptiveConcurrency(1);
    }

    private static CheckpointSupport.FakeTransformer PerRecordTransformer() => new()
    {
        OnTransform = (record, _) =>
            new List<JsonObject> { CheckpointSupport.Item(record["Id"]!.GetValue<string>()) },
    };

    private async Task RunAsync(
        CheckpointSupport.FakeGraphClient client,
        List<JsonObject> records)
    {
        var aclMap = records.ToDictionary(
            r => r["Id"]!.GetValue<string>(),
            _ => new List<Dictionary<string, string>>());
        await Ingest.IngestChunkGraphBatchAsync(
            _config, client, PerRecordTransformer(), records, aclMap,
            _stats, 20, dlPath: _dlPath, objectType: "Account",
            concurrency: _concurrency, statsLock: _statsLock);
    }

    [Fact]
    public async Task PartialResponseDetectsMissingItems()
    {
        // If Graph returns responses for 3 of 5 items, the other 2 are marked failed.
        var records = Enumerable.Range(0, 5)
            .Select(i => SilentDropSupport.MakeRecord($"P{i:D3}"))
            .ToList();

        // Only return responses for items 0, 1, 2 — items 3 and 4 are missing
        var client = new CheckpointSupport.FakeGraphClient
        {
            OnBatch = _ => new List<JsonObject>
            {
                CheckpointSupport.Resp("0", 200),
                CheckpointSupport.Resp("1", 200),
                CheckpointSupport.Resp("2", 200),
            },
        };

        await RunAsync(client, records);

        Assert.Equal(3, _stats.SuccessCount);
        Assert.Equal(2, _stats.FailedCount);
        // The 2 missing items should be in failed_ids
        Assert.Equal(2, _stats.FailedIds.Count);
    }

    [Fact]
    public async Task MismatchedResponseIdsDetected()
    {
        // If response IDs don't match request IDs, unmatched items are marked failed.
        var records = Enumerable.Range(0, 3)
            .Select(i => SilentDropSupport.MakeRecord($"M{i:D3}"))
            .ToList();

        // Return responses with wrong IDs — none match
        var client = new CheckpointSupport.FakeGraphClient
        {
            OnBatch = _ => new List<JsonObject>
            {
                CheckpointSupport.Resp("99", 200),
                CheckpointSupport.Resp("98", 200),
                CheckpointSupport.Resp("97", 200),
            },
        };

        await RunAsync(client, records);

        // All 3 items should be failed — none were matched
        Assert.Equal(0, _stats.SuccessCount);
        Assert.Equal(3, _stats.FailedCount);
    }

    [Fact]
    public async Task ResponseWithNoIdFieldDoesNotCauseSilentDrop()
    {
        // Response entries with missing 'id' field must not cause items to vanish.
        var records = Enumerable.Range(0, 2)
            .Select(i => SilentDropSupport.MakeRecord($"X{i:D3}"))
            .ToList();

        // One response has no "id", the other has a valid "id"
        var client = new CheckpointSupport.FakeGraphClient
        {
            OnBatch = _ => new List<JsonObject>
            {
                new JsonObject { ["status"] = 200 },   // Missing "id" field
                CheckpointSupport.Resp("1", 200),      // Valid
            },
        };

        await RunAsync(client, records);

        // Item "0" got no matching response → failed; item "1" matched → success
        Assert.Equal(1, _stats.SuccessCount);
        Assert.Equal(1, _stats.FailedCount);
    }

    [Fact]
    public async Task AllItemsMatchedMeansZeroMissing()
    {
        // When all items have matching responses, no items are marked as missing.
        var records = Enumerable.Range(0, 4)
            .Select(i => SilentDropSupport.MakeRecord($"OK{i:D2}"))
            .ToList();

        var client = new CheckpointSupport.FakeGraphClient
        {
            OnBatch = _ => Enumerable.Range(0, 4)
                .Select(i => CheckpointSupport.Resp(i.ToString(), 200))
                .ToList(),
        };

        await RunAsync(client, records);

        Assert.Equal(4, _stats.SuccessCount);
        Assert.Equal(0, _stats.FailedCount);
        // No dead-letter entries for missing items
        foreach (var entry in SilentDropSupport.ReadDeadLetter(_dlPath))
            Assert.DoesNotContain("No response", entry["error"]!.GetValue<string>());
    }
}
