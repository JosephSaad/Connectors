// Tests for the SF-parity hardening wave: adaptive concurrency, throughput
// knobs, sovereign-cloud Graph endpoint override, dead-letter concurrency
// safety (stress-harness invariants) and hot-path log gating.

using System.Net;
using System.Text.Json.Nodes;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Tests;

public class AdaptiveConcurrencyTests
{
    [Fact]
    public void StartsAtMax_ThrottleDialsDownTowardOne()
    {
        var concurrency = new AdaptiveConcurrency(4);
        Assert.Equal(4, concurrency.Current);
        concurrency.OnThrottle();
        Assert.Equal(3, concurrency.Current);
        concurrency.OnThrottle();
        concurrency.OnThrottle();
        concurrency.OnThrottle();
        concurrency.OnThrottle();  // floor is 1, never 0
        Assert.Equal(1, concurrency.Current);
    }

    [Fact]
    public void ThreeConsecutiveSuccesses_RampUpByOne()
    {
        var concurrency = new AdaptiveConcurrency(4);
        concurrency.OnThrottle();
        concurrency.OnThrottle();
        Assert.Equal(2, concurrency.Current);

        concurrency.OnSuccess();
        concurrency.OnSuccess();
        Assert.Equal(2, concurrency.Current);  // streak of 2 is not enough
        concurrency.OnSuccess();
        Assert.Equal(3, concurrency.Current);
    }

    [Fact]
    public void ThrottleResetsTheSuccessStreak()
    {
        var concurrency = new AdaptiveConcurrency(4);
        concurrency.OnThrottle();               // 3
        concurrency.OnSuccess();
        concurrency.OnSuccess();
        concurrency.OnThrottle();               // 2, streak reset
        concurrency.OnSuccess();
        concurrency.OnSuccess();
        Assert.Equal(2, concurrency.Current);   // still needs a third
    }

    [Fact]
    public void NeverExceedsMax_AndMinimumMaxIsOne()
    {
        var concurrency = new AdaptiveConcurrency(2);
        for (var i = 0; i < 12; i++)
            concurrency.OnSuccess();
        Assert.Equal(2, concurrency.Current);

        var floor = new AdaptiveConcurrency(0);
        Assert.Equal(1, floor.Current);
    }

    [Fact]
    public void BatchContains429_DetectsPerItemThrottle()
    {
        Assert.True(IngestPipeline.BatchContains429(JsonNode.Parse(
            """{"responses": [{"id": "1", "status": 200}, {"id": "2", "status": 429}]}""")));
        Assert.False(IngestPipeline.BatchContains429(JsonNode.Parse(
            """{"responses": [{"id": "1", "status": 200}]}""")));
        Assert.False(IngestPipeline.BatchContains429(JsonNode.Parse("{}")));
        Assert.False(IngestPipeline.BatchContains429(null));
    }
}

public class ThroughputKnobsTests
{
    private static EnvScope BaseEnv() => new(
        ("CONNECTOR_ID", "ClarizenAdaptiveWork"),
        ("CONNECTOR_NAME", "t"),
        ("CONNECTOR_DESCRIPTION", "t"),
        ("CLARIZEN_USERNAME", "u"),
        ("SECRET_CLARIZEN_PASSWORD", "p"),
        ("AAD_APP_TENANT_ID", "tenant-id"),
        ("AAD_APP_CLIENT_ID", "c"),
        ("SECRET_AAD_APP_CLIENT_SECRET", "s"),
        ("USE_KEY_VAULT", null),
        ("FINANCIAL_DATA_MODE", null),
        ("GRAPH_BATCH_WORKERS", null),
        ("GRAPH_CONCURRENT_BATCHES", null),
        ("GRAPH_BATCH_SIZE", null),
        ("INGEST_GRAPH_BATCH_SIZE", null),
        ("GRAPH_BASE_URL", null),
        ("GRAPH_SCOPE", null),
        ("GRAPH_API_VERSION", null),
        ("AAD_APP_OAUTH_AUTHORITY_HOST", null));

    [Fact]
    public void GraphBatchWorkers_Defaults_To8()
    {
        using var scope = BaseEnv();
        Assert.Equal(8, AppConfig.Load().GraphBatchWorkers);
    }

    [Fact]
    public void GraphConcurrentBatches_WinsOverGraphBatchWorkers()
    {
        using var scope = BaseEnv();
        scope.Set("GRAPH_BATCH_WORKERS", "4");
        Assert.Equal(4, AppConfig.Load().GraphBatchWorkers);

        scope.Set("GRAPH_CONCURRENT_BATCHES", "12");
        Assert.Equal(12, AppConfig.Load().GraphBatchWorkers);
    }

    [Fact]
    public void GraphBatchSize_AliasAccepted_ExplicitIngestNameWins()
    {
        using var scope = BaseEnv();
        scope.Set("GRAPH_BATCH_SIZE", "10");
        Assert.Equal(10, AppConfig.Load().GraphBatchSize);

        scope.Set("INGEST_GRAPH_BATCH_SIZE", "15");
        Assert.Equal(15, AppConfig.Load().GraphBatchSize);

        scope.Set("INGEST_GRAPH_BATCH_SIZE", "50");  // Graph hard cap
        Assert.Equal(20, AppConfig.Load().GraphBatchSize);
    }

    [Fact]
    public void RetryKnobs_AreRead()
    {
        using var scope = BaseEnv();
        scope.Set("GRAPH_MAX_RETRIES", "7");
        scope.Set("GRAPH_RETRY_BACKOFF_BASE", "1.5");
        var config = AppConfig.Load();
        Assert.Equal(7, config.GraphMaxRetries);
        Assert.Equal(1.5, config.GraphRetryBackoffBase, 6);
        scope.Set("GRAPH_MAX_RETRIES", null);
        scope.Set("GRAPH_RETRY_BACKOFF_BASE", null);
    }

    // ── Sovereign-cloud Graph endpoint override ──────────────────────────────

    [Fact]
    public void SovereignDefaults_PublicCloud()
    {
        using var scope = BaseEnv();
        var client = new GraphClient(AppConfig.Load());
        Assert.Equal("https://graph.microsoft.com/v1.0", client.BaseUrl);
        Assert.Equal("https://graph.microsoft.com/.default", client.TokenScope);
        Assert.Equal(
            "https://login.microsoftonline.com/tenant-id/oauth2/v2.0/token",
            client.TokenEndpoint);
    }

    [Fact]
    public void SovereignOverrides_ThreadThroughEndpointScopeVersionAndAuthority()
    {
        using var scope = BaseEnv();
        scope.Set("GRAPH_BASE_URL", "https://graph.microsoft.us/");
        scope.Set("GRAPH_API_VERSION", "beta");
        scope.Set("AAD_APP_OAUTH_AUTHORITY_HOST", "https://login.microsoftonline.us");
        var client = new GraphClient(AppConfig.Load());

        Assert.Equal("https://graph.microsoft.us/beta", client.BaseUrl);
        // Scope defaults to the sovereign base URL, not the public cloud.
        Assert.Equal("https://graph.microsoft.us/.default", client.TokenScope);
        Assert.Equal(
            "https://login.microsoftonline.us/tenant-id/oauth2/v2.0/token",
            client.TokenEndpoint);

        scope.Set("GRAPH_SCOPE", "https://custom.audience/.default");
        var custom = new GraphClient(AppConfig.Load());
        Assert.Equal("https://custom.audience/.default", custom.TokenScope);
    }

    [Fact]
    public async Task TokenRequest_GoesToSovereignAuthority_WithSovereignScope()
    {
        using var scope = BaseEnv();
        scope.Set("GRAPH_BASE_URL", "https://graph.microsoft.us");
        scope.Set("AAD_APP_OAUTH_AUTHORITY_HOST", "https://login.microsoftonline.us");

        var handler = new MockHttpHandler((request, _) =>
            request.RequestUri!.Host.Contains("login.microsoftonline.us")
                ? MockHttpHandler.Json(HttpStatusCode.OK,
                    """{"access_token": "sovereign-token", "expires_in": 3600}""")
                : MockHttpHandler.Json(HttpStatusCode.OK, "{}"));
        var client = new GraphClient(AppConfig.Load(), handler);
        client.DelayAsync = (_, _) => Task.CompletedTask;

        var response = await client.GetAsync("external/connections/X");
        Assert.True(response.IsSuccess);

        var tokenRequest = handler.Requests.First(r => r.Url.Contains("login.microsoftonline.us"));
        Assert.Contains("scope=https%3A%2F%2Fgraph.microsoft.us%2F.default", tokenRequest.Body);
        // The Graph call itself hit the sovereign resource endpoint.
        Assert.Contains(handler.Requests, r => r.Url.StartsWith("https://graph.microsoft.us/v1.0/"));
    }
}

/// <summary>
/// Dead-letter concurrency invariants (from the SF stress harness, which
/// caught interleaved-line corruption under concurrent writers): after N
/// parallel writers append disjoint id sets, the JSONL contains every id
/// exactly once and zero unparseable lines.
/// </summary>
public class DeadLetterConcurrencyTests
{
    private const string Connector = "StressConnector";


    /// <summary>
    /// Reading the queue WHILE a crawl appends to it must not throw.
    ///
    /// Distinct from the concurrent-writer test above, and the distinction is the
    /// whole point: writers are serialised by the in-process dead-letter lock, so
    /// only ever one write handle exists at a time. A READER takes its own handle.
    /// On Windows, where share modes are enforced, a reader whose share mode
    /// excludes Write is refused while an appending writer holds the file — so
    /// end-of-run dead-letter accounting and retry-failed fail mid-crawl. POSIX
    /// treats share modes as advisory, so this passes on Linux/macOS regardless
    /// and only ever fails on Windows Server, the deployment target.
    /// </summary>
    [Fact]
    public async Task ConcurrentAppendAndRead_ReaderNeverThrows()
    {
        using var scope = new SyncStateScope();
        var stop = false;
        var readerErrors = 0;

        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                try
                {
                    _ = SyncState.ReadFailedRecords(Connector);
                }
                catch
                {
                    Interlocked.Increment(ref readerErrors);
                }
            }
        });

        Parallel.For(0, 200, i =>
            SyncState.AppendFailedRecords(
                Connector,
                new List<(string, string)> { ($"item-{i}", $"error {i}") },
                "Task"));

        Volatile.Write(ref stop, true);
        await reader.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, readerErrors);
        Assert.Equal(200, SyncState.ReadFailedRecords(Connector).Count);
    }
    [Fact]
    public async Task ConcurrentAppends_NoCorruptLines_NoLostRecords()
    {
        using var scope = new SyncStateScope();
        const int writers = 16;
        const int perWriter = 50;

        var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                SyncState.AppendFailedRecords(
                    Connector,
                    new List<(string, string)> { ($"Task_{w}_{i}", $"error {w}/{i}") },
                    "Task",
                    requestBodies: new Dictionary<string, JsonNode?>
                    {
                        // A payload big enough that a torn write would corrupt lines.
                        [$"Task_{w}_{i}"] = new JsonObject
                        {
                            ["id"] = $"Task_{w}_{i}",
                            ["blob"] = new string('x', 512),
                        },
                    });
            }
        })).ToArray();
        await Task.WhenAll(tasks);

        // Invariant 1: every line parses (no interleaved/torn writes).
        var corrupt = 0;
        var ids = new List<string>();
        foreach (var line in File.ReadLines(SyncState.FailedRecordsPath(Connector)))
        {
            if (line.Trim().Length == 0)
                continue;
            try
            {
                var record = JsonNode.Parse(line)!.AsObject();
                ids.Add(record["item_id"]!.GetValue<string>());
            }
            catch
            {
                corrupt++;
            }
        }
        Assert.Equal(0, corrupt);

        // Invariant 2: exactly the appended ids, once each.
        Assert.Equal(writers * perWriter, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

        // Invariant 3: the tolerant reader agrees.
        Assert.Equal(writers * perWriter, SyncState.ReadFailedRecords(Connector).Count);
    }

    [Fact]
    public async Task ConcurrentAppends_AcrossObjectTypes_KeepPerRecordObjectType()
    {
        using var scope = new SyncStateScope();
        var tasks = new[] { "Project", "Task", "Risk", "Issue" }.Select(objectType => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++)
            {
                SyncState.AppendFailedRecords(
                    Connector,
                    new List<(string, string)> { ($"{objectType}_{i}", "boom") },
                    objectType);
            }
        })).ToArray();
        await Task.WhenAll(tasks);

        var entries = SyncState.ReadFailedRecords(Connector);
        Assert.Equal(100, entries.Count);
        foreach (var entry in entries)
        {
            var itemId = entry["item_id"]!.GetValue<string>();
            var objectType = entry["object_type"]!.GetValue<string>();
            Assert.StartsWith(objectType + "_", itemId);
        }
    }

    /// <summary>
    /// Reading the checkpoint WHILE another node writes it must neither throw nor
    /// lose the checkpoint.
    ///
    /// Two distinct defects sit behind this, and the second is the dangerous one.
    ///
    /// A reader takes its own handle, and <c>File.ReadAllText</c> asks for
    /// <c>FileShare.Read</c>, which refuses to coexist with a writer's write
    /// access. Windows enforces that and POSIX does not, so an overlapping read
    /// throws <c>IOException</c> on Windows Server — the deployment target — and
    /// never on the macOS and Linux machines this is written on. That is loud.
    ///
    /// The quiet one: <c>File.WriteAllText</c> truncates in place, so a reader can
    /// observe a half-written file. Torn JSON is swallowed as
    /// <c>JsonException</c> and reported as "no checkpoint" — so a resume silently
    /// restarts the crawl from the beginning and re-ingests everything, with
    /// nothing in the logs to say why. This test therefore asserts not only that
    /// the reader never throws, but that it never sees the checkpoint vanish.
    /// </summary>
    [Fact]
    public async Task ConcurrentCheckpointWriteAndRead_ReaderNeverThrowsAndNeverLosesTheCheckpoint()
    {
        using var scope = new SyncStateScope();
        const string Since = "2026-01-01T00:00:00Z";

        // Establish a checkpoint first: from here on, a null read can only mean
        // the reader caught the file mid-truncate.
        SyncState.WriteCheckpoint(CheckpointConnector, Since, "Task", 0);
        Assert.NotNull(SyncState.ReadCheckpoint(CheckpointConnector));

        var stop = false;
        var readerErrors = 0;
        var vanished = 0;

        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                try
                {
                    if (SyncState.ReadCheckpoint(CheckpointConnector) is null)
                    {
                        Interlocked.Increment(ref vanished);
                    }
                }
                catch
                {
                    Interlocked.Increment(ref readerErrors);
                }
            }
        });

        for (var i = 1; i <= 300; i++)
        {
            SyncState.WriteCheckpoint(CheckpointConnector, Since, "Task", i);
        }

        Volatile.Write(ref stop, true);
        await reader.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, readerErrors);
        Assert.Equal(0, vanished);

        // And the surviving checkpoint is the newest one, not a resurrected older
        // copy left behind by a lost race.
        var final = SyncState.ReadCheckpoint(CheckpointConnector);
        Assert.NotNull(final);
        Assert.Equal(300, final!["completed"]!["Task"]!.GetValue<int>());
    }

    private const string CheckpointConnector = "CheckpointRaceConnector";

}

public class LogGatingTests : IDisposable
{
    public LogGatingTests() => Logging.ResetForTests();

    public void Dispose() => Logging.ResetForTests();

    [Fact]
    public void ParseLevel_Mapping()
    {
        Assert.Equal(LogLevel.Debug, Logging.ParseLevel("DEBUG"));
        Assert.Equal(LogLevel.Info, Logging.ParseLevel("info"));
        Assert.Equal(LogLevel.Warning, Logging.ParseLevel("WARN"));
        Assert.Equal(LogLevel.Warning, Logging.ParseLevel("Warning"));
        Assert.Equal(LogLevel.Error, Logging.ParseLevel("error"));
        Assert.Equal(LogLevel.Debug, Logging.ParseLevel(null));      // no floor
        Assert.Equal(LogLevel.Debug, Logging.ParseLevel("bogus"));
    }

    [Fact]
    public void IsEnabledFor_NoFileSink_FollowsConsoleThreshold()
    {
        using var scope = new EnvScope(("LOG_LEVEL", null));
        var logger = Logging.GetLogger("clarizen_connector.test");
        Logging.ConsoleLevel = LogLevel.Warning;  // default (no --verbose)
        Assert.False(logger.IsEnabledFor(LogLevel.Debug));
        Assert.False(logger.IsEnabledFor(LogLevel.Info));
        Assert.True(logger.IsEnabledFor(LogLevel.Warning));
        Assert.True(logger.IsEnabledFor(LogLevel.Error));

        Logging.ConsoleLevel = LogLevel.Debug;    // --verbose
        Assert.True(logger.IsEnabledFor(LogLevel.Debug));
    }

    [Fact]
    public void IsEnabledFor_FileSinkOpen_AcceptsDebug()
    {
        using var scope = new EnvScope(("LOG_LEVEL", null));
        using var dir = new TempDir();
        var previousRoot = Logging.LogsRoot;
        try
        {
            Logging.LogsRoot = dir.Path;
            Logging.Initialize("gating_test", verbose: false);
            // Console threshold is WARNING, but the run-dir file captures DEBUG.
            var logger = Logging.GetLogger("clarizen_connector.test");
            Assert.True(logger.IsEnabledFor(LogLevel.Debug));
        }
        finally
        {
            Logging.ResetForTests();
            Logging.LogsRoot = previousRoot;
        }
    }

    [Fact]
    public void LogLevelEnv_RaisesTheFloor_ForGateAndSinks()
    {
        using var dir = new TempDir();
        var previousRoot = Logging.LogsRoot;
        try
        {
            Logging.LogsRoot = dir.Path;
            Logging.Initialize("gating_floor", verbose: true);
            var logger = Logging.GetLogger("clarizen_connector.test");

            using (new EnvScope(("LOG_LEVEL", "WARNING")))
            {
                Assert.False(logger.IsEnabledFor(LogLevel.Debug));
                Assert.False(logger.IsEnabledFor(LogLevel.Info));
                Assert.True(logger.IsEnabledFor(LogLevel.Warning));

                var emitted = new List<LogLevel>();
                Logging.TestSink = (level, _, _) => emitted.Add(level);
                logger.Debug("dropped");
                logger.Warning("kept");
                Assert.Equal(new[] { LogLevel.Warning }, emitted);
            }
        }
        finally
        {
            Logging.ResetForTests();
            Logging.LogsRoot = previousRoot;
        }
    }
}
