// Degraded mode: when a critical breaker is open the crawl pauses at a safe
// checkpoint boundary — no sync-cursor advance, no dead-lettering of good
// items, clean resume — and /ready flips to not-ready while liveness stays up.

using System.Net;
using HadoopConnector.AclEngine;
using HadoopConnector.Hdfs;
using HadoopConnector.Config;
using HadoopConnector.Filters;
using HadoopConnector.Graph;
using HadoopConnector.Infrastructure;
using HadoopConnector.Item;

namespace HadoopConnector.Tests;

public class DegradedModePipelineTests : IDisposable
{
    private const string Connector = "BdhHadoopMart";

    private readonly TempDir _dir = new();
    private readonly SyncStateScope _stateScope = new();
    private readonly IdentityStore _store;
    private readonly AppConfig _config = TestConfig.Make(ingestChunkSize: 2, allowFullScan: true);
    private readonly SchemaConfig _schema;
    private readonly FakeGraphClient _graph;
    private readonly FakeBdhSource _source;

    /// <summary>Salesforce-shaped record id ("T000000000001", ...).</summary>
    private static string Tid(int n) => $"T{n:D12}";

    public DegradedModePipelineTests()
    {
        _store = new IdentityStore("Degraded", Path.Combine(_dir.Path, "identity.db"));
        _store.Upsert(new PrincipalMapping(
            "005U0000001", "user", "o@example.com", "entra-owner", DateTime.UtcNow));
        _schema = new SchemaConfig
        {
            ObjectList = new List<ObjectConfig>
            {
                new()
                {
                    ObjectName = "Task",
                    DisplayName = "Task",
                    AclMode = "ownerOnly",
                    SelectedFields = new Dictionary<string, string>
                    {
                        ["Name"] = "Title",
                        ["OwnerId"] = "OwnerId",
                    },
                },
            },
        };
        _source = new FakeBdhSource();
        _source.Add("Task/dt=2026-07-15/part-0000.jsonl", string.Join("\n",
            Enumerable.Range(1, 4).Select(n =>
                $$"""{"Id":"{{Tid(n)}}","Name":"Task {{n}}","OwnerId":"005U0000001"}""")));
        _graph = new FakeGraphClient(_config);
    }

    public void Dispose()
    {
        _store.Dispose();
        _stateScope.Dispose();
        _dir.Dispose();
        Breakers.ResetForTests();
    }

    private Func<string, IItemInventory> InventoryFactory => id =>
        new ItemInventory(id, Path.Combine(_dir.Path, $"inv_{id}.db"));

    private IngestPipeline Pipeline()
    {
        var fetcher = new BdhFetcher(_config, _source, FilterSet.Empty);
        var resolver = new AclResolver(
            new PrincipalMapper(_store), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
        return new IngestPipeline(
            _config, _schema, fetcher, _graph, resolver, new ItemConverter(_config),
            ha: null, inventoryFactory: InventoryFactory);
    }

    [Fact]
    public async Task CriticalBreakerOpen_PausesAtObjectBoundary_NoSyncAdvance()
    {
        Breakers.Initialize(new CircuitBreakerOptions { Enabled = true, FailureThreshold = 1 });
        Breakers.Graph.TripForTests();  // sustained Graph outage

        var summary = await Pipeline().RunAsync(fullCrawl: true);

        Assert.True(summary.Degraded);
        Assert.Equal(0, summary.Ingested);
        Assert.DoesNotContain(_graph.Sent, s => s.Method == HttpMethod.Put);  // no work started
        // Degraded pause is like a graceful stop: cursor NOT advanced.
        Assert.Null(SyncState.ReadLastSync(Connector));
    }

    [Fact]
    public async Task HdfsCircuitOpen_DuringFetch_DegradesRun_NoDeadLetter()
    {
        // Breaker registry inert (default) so the object-boundary check passes;
        // the source then fails fast mid-fetch, as the real WebHDFS client does
        // when the hdfs breaker is open during a sustained namenode outage.
        _source.FailOn = _ => new CircuitOpenException("hdfs");

        var summary = await Pipeline().RunAsync(fullCrawl: true);

        Assert.True(summary.Degraded);
        Assert.Equal(0, summary.Ingested);
        Assert.DoesNotContain(_graph.Sent, s => s.Method == HttpMethod.Put || s.Path == "$batch");
        // A fetch-side outage is degraded, not a worker crash: nothing
        // dead-lettered, cursor unmoved, checkpoint retained for a clean resume.
        Assert.Empty(SyncState.ReadFailedRecords(Connector));
        Assert.Null(SyncState.ReadLastSync(Connector));
    }

    [Fact]
    public async Task CircuitOpenMidChunk_DoesNotDeadLetter_OrCheckpoint()
    {
        // Breaker registry inert (default) so the object-boundary check passes;
        // the fake Graph then fails fast mid-chunk (as the real client would).
        _graph.CircuitOpen = true;

        var summary = await Pipeline().RunAsync(fullCrawl: true);

        Assert.True(summary.Degraded);
        Assert.Equal(0, summary.Ingested);
        // Good items are NOT dead-lettered during an outage.
        Assert.Empty(SyncState.ReadFailedRecords(Connector));
        // The chunk is not checkpointed → nothing to resume-skip; cursor unmoved.
        Assert.Null(SyncState.ReadLastSync(Connector));
        Assert.Null(SyncState.ReadCheckpoint(Connector));
    }

    [Fact]
    public async Task Resume_AfterRecovery_IsClean_NoStateLoss()
    {
        // First cycle degrades mid-chunk (fail fast), leaving no checkpoint.
        _graph.CircuitOpen = true;
        var first = await Pipeline().RunAsync(fullCrawl: true);
        Assert.True(first.Degraded);
        Assert.Empty(SyncState.ReadFailedRecords(Connector));

        // Dependency recovers; the next cycle ingests everything cleanly.
        _graph.CircuitOpen = false;
        var second = await Pipeline().RunAsync(fullCrawl: true);

        Assert.False(second.Degraded);
        Assert.Equal(4, second.Ingested);
        Assert.NotNull(SyncState.ReadLastSync(Connector));
        using var inventory = InventoryFactory(Connector);
        Assert.Equal(4, inventory.IdsForObject("Task").Count);
    }
}

public class HealthReadinessDegradedTests : IDisposable
{
    public void Dispose() => Breakers.ResetForTests();

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public async Task Readiness_FlipsToNotReady_WhenCriticalBreakerOpen_LivenessStaysUp()
    {
        Breakers.Initialize(new CircuitBreakerOptions { Enabled = true, FailureThreshold = 1 });
        var port = FreePort();
        using var scope = new EnvScope((HealthEndpoint.PortEnvVar, port.ToString()));
        using var endpoint = HealthEndpoint.StartIfConfigured();
        Assert.NotNull(endpoint);
        using var http = new HttpClient();

        // Healthy: ready = 200.
        Assert.Equal("READY", await http.GetStringAsync($"http://localhost:{port}/ready"));

        // Trip a critical breaker → degraded.
        Breakers.Graph.TripForTests();

        var ready = await http.GetAsync($"http://localhost:{port}/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Contains("graph", await ready.Content.ReadAsStringAsync());

        // Liveness stays 200 — a degraded connector must not be killed.
        var health = await http.GetAsync($"http://localhost:{port}/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }
}

public class DegradedRetryDelayTests
{
    [Fact]
    public void DegradedRetryDelay_ClampedToOpenDuration()
    {
        using (new EnvScope(("CIRCUIT_BREAKER_OPEN_SECONDS", "45")))
            Assert.Equal(TimeSpan.FromSeconds(45), Commands.Deploy.DegradedRetryDelay());
        using (new EnvScope(("CIRCUIT_BREAKER_OPEN_SECONDS", "2")))
            Assert.Equal(TimeSpan.FromSeconds(10), Commands.Deploy.DegradedRetryDelay());  // min
        using (new EnvScope(("CIRCUIT_BREAKER_OPEN_SECONDS", "9999")))
            Assert.Equal(TimeSpan.FromSeconds(300), Commands.Deploy.DegradedRetryDelay());  // max
    }
}
