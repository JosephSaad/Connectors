// StressTests.cs
// --------------
// Bounded, CI-fast stress tests that push the pipeline's scaling/robustness
// invariants using the SAME fakes as the rest of the suite (FakeBdhSource,
// FakeGraphClient, MockHttpHandler, SyncStateScope) — no real network. The
// heavy-scale numbers live in tools/StressHarness; these run in every build at
// a fraction of the volume but assert the same invariants deterministically.
//
// Coverage mirrors the seven stress scenarios:
//   1. Filter layer at scale — partition pruning opens ZERO pruned files;
//      records_scanned vs records_matched; selective filter reads far less.
//   2. Memory bounds — a large streamed file is not materialized (bounded
//      working set); BoundedStream aborts an oversize read; the row cap marks
//      the crawl partial and suppresses the deletion sweep.
//   3. Fail-closed guard — an unfiltered object refuses to crawl with zero I/O.
//   4. Dead-letter concurrency — many writers, zero loss/interleaving/corruption.
//   5. Circuit breaker under sustained failures — trips, fails fast, and (the
//      regression) releases the HalfOpen slot on a token-fetch throw so it can
//      never wedge; degraded mode pauses at a safe boundary.
//   6. Checkpoint/resume under interruption — no duplication, no lost records.
//   7. $batch throughput with induced 429s — Retry-After honored, no lost items,
//      correct final count, adaptive concurrency reacts.

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HadoopConnector.AclEngine;
using HadoopConnector.Commands;
using HadoopConnector.Config;
using HadoopConnector.Filters;
using HadoopConnector.Graph;
using HadoopConnector.Hdfs;
using HadoopConnector.Infrastructure;
using HadoopConnector.Item;

namespace HadoopConnector.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Test-only helpers
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>A lazily-generated JSONL stream: yields <c>rowCount</c> rows without
/// ever holding them all in memory, so a "huge" file can be streamed through the
/// bounded reader with a working set of one row.</summary>
internal sealed class LazyJsonlStream : Stream
{
    private readonly int _rowCount;
    private readonly Func<int, string> _rowFactory;
    private byte[] _buffer = Array.Empty<byte>();
    private int _bufferPos;
    private int _nextRow;
    public long TotalBytesProduced { get; private set; }

    public LazyJsonlStream(int rowCount, Func<int, string> rowFactory)
    {
        _rowCount = rowCount;
        _rowFactory = rowFactory;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_bufferPos >= _buffer.Length)
        {
            if (_nextRow >= _rowCount)
                return 0;
            _buffer = Encoding.UTF8.GetBytes(_rowFactory(_nextRow++) + "\n");
            _bufferPos = 0;
        }
        var n = Math.Min(count, _buffer.Length - _bufferPos);
        Array.Copy(_buffer, _bufferPos, buffer, offset, n);
        _bufferPos += n;
        TotalBytesProduced += n;
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>One object, one recent partition, one file of <c>rowCount</c>
/// lazily-generated JSONL rows. Counts opens so a test can prove streaming.</summary>
internal sealed class GeneratingBdhSource : IBdhSource
{
    private readonly string _objectRoot;
    private readonly string _dt;
    private readonly int _rowCount;
    private readonly Func<int, string> _rowFactory;

    public int OpenCalls { get; private set; }
    public long LastFileBytes { get; private set; }

    public GeneratingBdhSource(string objectRoot, string dt, int rowCount, Func<int, string> rowFactory)
    {
        _objectRoot = objectRoot;
        _dt = dt;
        _rowCount = rowCount;
        _rowFactory = rowFactory;
    }

    public string Description => "generating";

    public Task<List<HdfsFileStatus>> ListAsync(string relativePath, CancellationToken ct = default)
    {
        var p = relativePath.Trim('/');
        if (p == _objectRoot)
            return Task.FromResult(new List<HdfsFileStatus> { new($"dt={_dt}", true, 0, 0) });
        if (p == $"{_objectRoot}/dt={_dt}")
            // A plausible reported size (under BDH_MAX_FILE_BYTES) so the oversize
            // guard does not skip it; the ACTUAL stream is generated lazily and is
            // far larger than any buffer, which is the whole point.
            return Task.FromResult(new List<HdfsFileStatus> { new("part-0000.jsonl", false, 50_000_000, 0) });
        throw new HdfsException($"Directory not found: '{relativePath}'.") { StatusCode = 404 };
    }

    public Task<bool> ExistsAsync(string relativePath, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<Stream> OpenAsync(string relativePath, CancellationToken ct = default)
    {
        OpenCalls++;
        var stream = new LazyJsonlStream(_rowCount, _rowFactory);
        return Task.FromResult<Stream>(stream);
    }

    public void Dispose() { }
}

public class FilterLayerScaleStressTests
{
    /// <summary>Graph-safe, Salesforce-shaped id.</summary>
    private static string Cid(int n) => $"C{n:D12}";

    // Scenario 1: partition pruning opens ZERO pruned files; scanned vs matched;
    // a highly selective filter reads far less than the total corpus.
    [Fact]
    public async Task PartitionPruning_OpensZeroPrunedFiles_AndSelectiveFilterReadsFarLess()
    {
        var now = new DateTime(2026, 7, 17, 0, 0, 0, DateTimeKind.Utc);
        const int days = 120;
        const int rowsPerFile = 250;
        var regions = new[] { "EU", "US" };

        var source = new FakeBdhSource();
        var globalId = 0;
        var totalRows = 0;
        for (var d = 0; d < days; d++)
        {
            var dt = now.AddDays(-d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (var region in regions)
            {
                var sb = new StringBuilder();
                for (var r = 0; r < rowsPerFile; r++)
                {
                    globalId++;
                    totalRows++;
                    // Only every 50th row is "Active" → strong selectivity.
                    var status = r % 50 == 0 ? "Active" : "Inactive";
                    sb.Append($$"""{"Id":"{{Cid(globalId)}}","Status":"{{status}}","Region":"{{region}}"}""");
                    sb.Append('\n');
                }
                source.Add($"Contact/region={region}/dt={dt}/part-0000.jsonl", sb.ToString());
            }
        }

        // Filter: partition on region=EU (prunes the whole US subtree with zero
        // I/O) + record predicate Status=Active (streamed selectivity).
        var filter = new ObjectFilter
        {
            Partition = new List<FilterPredicate>
            {
                new() { Field = "region", Op = FilterOp.Equals, Value = "EU" },
            },
            AnyOf = new List<FilterGroup>
            {
                new() { AllOf = { new FilterPredicate { Field = "Status", Op = FilterOp.Equals, Value = "Active" } } },
            },
        };
        var filters = new FilterSet(
            new Dictionary<string, ObjectFilter>(StringComparer.OrdinalIgnoreCase) { ["Contact"] = filter },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        // Incremental watermark: since − lag(0) = 10 days ago → older dt pruned.
        var config = TestConfig.Make(lagHours: 0, allowFullScan: false);
        var since = now.AddDays(-10);
        var fetcher = new BdhFetcher(config, source, filters, nowUtc: () => now);
        var objectConfig = new ObjectConfig { ObjectName = "Contact", DisplayName = "Contact" };

        // Expected survivors: EU partitions whose dt >= (since − lag).date.
        var bound = DateOnly.FromDateTime(since);
        var survivingDays = Enumerable.Range(0, days)
            .Select(d => DateOnly.FromDateTime(now.AddDays(-d)))
            .Count(dt => dt >= bound);

        var sw = Stopwatch.StartNew();
        var result = await fetcher.FetchAsync(objectConfig, fullCrawl: false, since);
        sw.Stop();

        var stats = result.Stats;

        // ZERO file opens for pruned dirs: exactly one file per surviving EU
        // partition was opened; no US file and no out-of-window EU file was touched.
        Assert.Equal(survivingDays, source.OpenCalls);
        Assert.Equal(survivingDays, stats.PartitionsScanned);
        Assert.Equal(survivingDays, stats.FilesRead);
        // Pruned = out-of-window EU dt partitions (each pruned individually) + the
        // whole US region subtree (pruned ONCE, its dt dirs never even listed).
        var expectedPruned = (days - survivingDays) + 1;
        Assert.Equal(expectedPruned, stats.PartitionsPruned);

        // records_scanned = only the surviving partitions' rows (not the corpus).
        Assert.Equal(survivingDays * rowsPerFile, stats.RecordsScanned);

        // Selective filter: matched ≪ scanned ≪ total corpus.
        var expectedMatched = survivingDays * (rowsPerFile / 50);
        Assert.Equal(expectedMatched, stats.RecordsMatched);
        Assert.Equal(expectedMatched, result.Records.Count);
        Assert.True(stats.RecordsScanned < totalRows / 10,
            $"pruning should read <10% of the {totalRows}-row corpus, scanned {stats.RecordsScanned}");
        Assert.True(stats.RecordsMatched * 20 < stats.RecordsScanned,
            "selective record filter should match a small fraction of scanned rows");

        // Throughput sanity (non-flaky floor): parsing thousands of rows is ms-scale.
        var rowsPerSec = stats.RecordsScanned / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        Assert.True(rowsPerSec > 1000, $"throughput unexpectedly low: {rowsPerSec:F0} rows/s");
    }
}

public class MemoryBoundStressTests
{
    private static string Bid(int n) => $"B{n:D12}";

    // Scenario 2a: a large streamed file is processed with a bounded working set —
    // 200k rows scanned, but only the matched handful is materialized.
    [Fact]
    public async Task LargeStreamedFile_IsNotMaterialized_BoundedWorkingSet()
    {
        const int rows = 200_000;
        string Row(int i) =>
            $$"""{"Id":"{{Bid(i)}}","Status":"{{(i % 1000 == 0 ? "Active" : "Inactive")}}"}""";

        var source = new GeneratingBdhSource("Big", "2026-07-17", rows, Row);
        var filter = new ObjectFilter
        {
            AnyOf = new List<FilterGroup>
            {
                new() { AllOf = { new FilterPredicate { Field = "Status", Op = FilterOp.Equals, Value = "Active" } } },
            },
        };
        var filters = new FilterSet(
            new Dictionary<string, ObjectFilter>(StringComparer.OrdinalIgnoreCase) { ["Big"] = filter },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var config = TestConfig.Make(maxRecordsPerObject: 0); // no cap for this measurement
        var fetcher = new BdhFetcher(config, source, filters);
        var result = await fetcher.FetchAsync(
            new ObjectConfig { ObjectName = "Big", DisplayName = "Big" }, fullCrawl: true, sinceUtc: null);

        Assert.Equal(rows, result.Stats.RecordsScanned);       // every row streamed…
        Assert.Equal(rows / 1000, result.Records.Count);       // …but only 200 held
        Assert.Equal(1, source.OpenCalls);
        // Working set (materialized records) is a tiny fraction of rows scanned.
        Assert.True(result.Records.Count * 100 < result.Stats.RecordsScanned);
    }

    // Scenario 2b: the bounded reader aborts an oversize read instead of
    // exhausting memory — proves the file is never fully materialized.
    [Fact]
    public void BoundedStream_AbortsOversizeRead_AtScale()
    {
        var inner = new LazyJsonlStream(1_000_000, i => $$"""{"Id":"{{Bid(i)}}"}""");
        using var bounded = new BoundedStream(inner, maxBytes: 64 * 1024);
        var buffer = new byte[8192];
        var ex = Assert.Throws<InvalidDataException>(() =>
        {
            while (bounded.Read(buffer, 0, buffer.Length) > 0) { }
        });
        Assert.Contains("read bound", ex.Message);
        Assert.True(bounded.BytesRead <= 64 * 1024 + buffer.Length);
    }

    // Scenario 2c: the row cap marks the crawl partial AND suppresses the
    // deletion sweep, so a truncated fetch can never mass-delete live items.
    [Fact]
    public async Task RowCap_MarksPartial_AndSuppressesDeletionSweep()
    {
        using var dir = new TempDir();
        using var state = new SyncStateScope();
        const string connector = "BdhHadoopMart";

        var source = new FakeBdhSource();
        var sb = new StringBuilder();
        for (var i = 1; i <= 500; i++)
            sb.Append($$"""{"Id":"{{Bid(i)}}","Status":"Active","OwnerId":"o1"}""").Append('\n');
        source.Add("Contact/dt=2026-07-15/part-0000.jsonl", sb.ToString());

        var config = TestConfig.Make(
            ingestChunkSize: 50, maxRecordsPerObject: 100, allowFullScan: true);
        var schema = new SchemaConfig
        {
            ObjectList = new List<ObjectConfig>
            {
                new() { ObjectName = "Contact", DisplayName = "Contact", AclMode = "public" },
            },
        };

        // Seed the inventory with a live id absent from the (truncated) source —
        // a correct un-truncated sweep would delete it; a truncated one must not.
        Func<string, IItemInventory> inv = id => new ItemInventory(id, Path.Combine(dir.Path, $"inv_{id}.db"));
        using (var seed = inv(connector))
            seed.RecordSeen(new[] { ("Z999999999999", "Contact") }, DateTime.UtcNow);

        var graph = new FakeGraphClient(config);
        var pipeline = BuildPipeline(config, schema, source, graph, dir.Path, inv);

        var summary = await pipeline.RunAsync(fullCrawl: true);

        Assert.Contains("Contact", summary.PartialObjects);
        Assert.Contains("Contact", summary.SweepSkipped);
        Assert.Equal(0, summary.Deleted);
        Assert.DoesNotContain(graph.Sent, s => s.Method == HttpMethod.Delete || s.Path == "$batch" && DeletesInBatch(s.Body));
        using var check = inv(connector);
        Assert.Contains("Z999999999999", check.IdsForObject("Contact")); // live item survived
    }

    private static bool DeletesInBatch(JsonNode? body) =>
        body?["requests"] is JsonArray reqs
        && reqs.Any(r => r?["method"]?.GetValue<string>() == "DELETE");

    internal static IngestPipeline BuildPipeline(
        AppConfig config, SchemaConfig schema, IBdhSource source, GraphClient graph,
        string dirPath, Func<string, IItemInventory> inv)
    {
        var fetcher = new BdhFetcher(config, source, FilterSet.Empty);
        var resolver = new AclResolver(
            new PrincipalMapper(new IdentityStore("s", Path.Combine(dirPath, "id.db"))),
            adminGroupId: string.Empty, fallbackGroupId: "grp-all");
        return new IngestPipeline(
            config, schema, fetcher, graph, resolver, new ItemConverter(config),
            ha: null, inventoryFactory: inv);
    }
}

public class FailClosedGuardStressTests
{
    // Scenario 3: at scale an unfiltered object refuses to crawl — no 150M scan —
    // and the refusal happens BEFORE any listing/opening (zero I/O).
    [Fact]
    public async Task UnfilteredObjects_RefuseToCrawl_WithZeroIo()
    {
        var source = new FakeBdhSource();
        foreach (var obj in new[] { "Contact", "Account", "Lead", "Case", "Opportunity" })
            source.Add($"{obj}/dt=2026-07-15/part-0000.jsonl", """{"Id":"X000000000001"}""");

        var config = TestConfig.Make(allowFullScan: false);
        var fetcher = new BdhFetcher(config, source, FilterSet.Empty);

        foreach (var obj in new[] { "Contact", "Account", "Lead", "Case", "Opportunity" })
        {
            await Assert.ThrowsAsync<FullScanRefusedException>(() =>
                fetcher.FetchAsync(new ObjectConfig { ObjectName = obj, DisplayName = obj },
                    fullCrawl: true, sinceUtc: null));
        }

        // The guard fired before the scanner ran: nothing listed, nothing opened.
        Assert.Equal(0, source.ListCalls);
        Assert.Equal(0, source.OpenCalls);
    }

    [Fact]
    public async Task OptedInObject_IsAllowed_AndScans()
    {
        var source = new FakeBdhSource();
        source.Add("Account/dt=2026-07-15/part-0000.jsonl", """{"Id":"A000000000001"}""");
        var filters = new FilterSet(
            new Dictionary<string, ObjectFilter>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Account" });
        var config = TestConfig.Make(allowFullScan: false);
        var fetcher = new BdhFetcher(config, source, filters);
        var result = await fetcher.FetchAsync(
            new ObjectConfig { ObjectName = "Account", DisplayName = "Account" }, fullCrawl: true, sinceUtc: null);
        Assert.Single(result.Records);
    }
}

public class DeadLetterConcurrencyStressTests
{
    // Scenario 4: many concurrent workers writing failures — zero loss,
    // interleaving or corruption (the hardened process-wide-lock invariant).
    [Theory]
    [InlineData(32, 250)]
    [InlineData(64, 400)]
    public async Task ConcurrentWriters_NoLoss_NoCorruption(int workers, int perWorker)
    {
        using var scope = new SyncStateScope();
        const string connector = "StressConnector";

        var tasks = Enumerable.Range(0, workers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < perWorker; i++)
            {
                SyncState.AppendFailedRecords(
                    connector,
                    new List<(string, string)> { ($"W{w:D3}_I{i:D5}", $"err worker {w} item {i}") },
                    "Contact");
            }
        })).ToArray();
        await Task.WhenAll(tasks);

        var expected = workers * perWorker;

        // Every physical line parses cleanly (no interleaving / torn writes).
        var lines = File.ReadAllLines(SyncState.FailedRecordsPath(connector))
            .Where(l => l.Trim().Length > 0).ToList();
        Assert.Equal(expected, lines.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var obj = JsonNode.Parse(line)!.AsObject(); // throws if corrupt
            ids.Add(obj["item_id"]!.GetValue<string>());
        }
        // Every id present exactly once → no loss, no duplication.
        Assert.Equal(expected, ids.Count);
    }
}

public class CircuitBreakerStressTests
{
    private static GraphClient MakeGraph(MockHttpHandler handler, CircuitBreaker breaker, int maxRetries = 0)
    {
        var client = new GraphClient(TestConfig.Make(graphMaxRetries: maxRetries), handler, breaker);
        client.DelayAsync = (_, _) => Task.CompletedTask;
        return client;
    }

    /// <summary>Trip a breaker then advance its injected clock past OpenDuration so
    /// it is HalfOpen and ready to admit probes.</summary>
    private static (CircuitBreaker Breaker, Action AdvancePastOpen) HalfOpenBreaker(int halfOpenTrials)
    {
        var clock = DateTime.UtcNow;
        var breaker = new CircuitBreaker("graph", new CircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = 2,
            OpenDuration = TimeSpan.FromSeconds(30),
            Window = TimeSpan.FromSeconds(60),
            HalfOpenTrials = halfOpenTrials,
        }, () => clock);
        breaker.TripForTests();
        clock = clock.AddSeconds(31);
        return (breaker, () => clock = clock.AddSeconds(31));
    }

    // Scenario 5a: sustained 5xx trips the breaker; once open, calls fail fast
    // WITHOUT hitting the network.
    [Fact]
    public async Task SustainedFailures_Trip_ThenFailFast_UnderLoad()
    {
        var breaker = new CircuitBreaker("graph", new CircuitBreakerOptions
        {
            Enabled = true, FailureThreshold = 5, OpenDuration = TimeSpan.FromSeconds(30),
            Window = TimeSpan.FromSeconds(60), HalfOpenTrials = 1,
        });
        var handler = new MockHttpHandler((_, _) =>
            MockHttpHandler.Json(HttpStatusCode.InternalServerError, "boom"));
        var client = MakeGraph(handler, breaker) ; client.OverrideToken = "t";

        // Drive many calls; the breaker trips at the threshold and the rest are
        // rejected fast (no network) — never a 150M-row hammer during an outage.
        var circuitOpenCount = 0;
        for (var i = 0; i < 200; i++)
        {
            var resp = await client.GetAsync("connections");
            if (resp.CircuitOpen) circuitOpenCount++;
        }

        Assert.Equal(CircuitState.Open, breaker.State);
        Assert.True(breaker.Trips >= 1);
        // The vast majority short-circuited without a network call.
        Assert.True(circuitOpenCount > 180, $"expected fail-fast rejections, got {circuitOpenCount}");
        Assert.True(handler.Requests.Count < 20, $"too many network calls during outage: {handler.Requests.Count}");
    }

    // Scenario 5b (REGRESSION): a HalfOpen probe whose TOKEN fetch throws must
    // RELEASE its probe slot. Before the settle-once hardening the slot leaked
    // and — after HalfOpenTrials such throws — the breaker admitted no more
    // probes and wedged Open forever, even after Graph recovered.
    [Fact]
    public async Task HalfOpenProbe_TokenFetchThrows_DoesNotWedgeBreaker()
    {
        var (breaker, _) = HalfOpenBreaker(halfOpenTrials: 2);
        Assert.Equal(CircuitState.HalfOpen, breaker.State);

        var tokenFails = true;
        var handler = new MockHttpHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2", StringComparison.OrdinalIgnoreCase))
            {
                return tokenFails
                    ? MockHttpHandler.Json(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""")
                    : MockHttpHandler.Json(HttpStatusCode.OK, """{"access_token":"tok","expires_in":3600}""");
            }
            return MockHttpHandler.Json(HttpStatusCode.OK, "{}");
        });
        // Real token flow (no OverrideToken) so the token fetch actually throws.
        var client = new GraphClient(TestConfig.Make(graphMaxRetries: 0), handler, breaker);
        client.DelayAsync = (_, _) => Task.CompletedTask;

        // Fire more failing probes than there are HalfOpen slots. With the leak
        // this permanently exhausts the slots.
        for (var i = 0; i < 6; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("connections"));
            Assert.Equal(CircuitState.HalfOpen, breaker.State); // still probing, not wedged
        }

        // Graph/token recovers → healthy probes must still be ADMITTED and close
        // the breaker. If the slot had leaked, these would return CircuitOpen and
        // the breaker would never reset.
        tokenFails = false;
        var r1 = await client.GetAsync("connections");
        Assert.False(r1.CircuitOpen);
        var r2 = await client.GetAsync("connections");
        Assert.False(r2.CircuitOpen);
        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal(1, breaker.Resets);
    }

    // Companion: a HalfOpen probe that ends in a terminal 5xx re-opens cleanly
    // (slot released via OnFailure), and a later window can still recover.
    [Fact]
    public async Task HalfOpenProbe_ServerError_ReopensCleanly_ThenRecovers()
    {
        var (breaker, advance) = HalfOpenBreaker(halfOpenTrials: 1);
        var fail = true;
        var handler = new MockHttpHandler((_, _) => fail
            ? MockHttpHandler.Json(HttpStatusCode.InternalServerError, "boom")
            : MockHttpHandler.Json(HttpStatusCode.OK, "{}"));
        var client = MakeGraph(handler, breaker); client.OverrideToken = "t";

        // Probe fails (5xx) → back to Open.
        await client.GetAsync("connections");
        Assert.Equal(CircuitState.Open, breaker.State);

        // Recover: advance past OpenDuration, heal the dependency, probe closes it.
        advance();
        fail = false;
        var resp = await client.GetAsync("connections");
        Assert.False(resp.CircuitOpen);
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    // Scenario 5c: degraded mode pauses at a safe object boundary — no work
    // started, no sync-cursor advance, checkpoint retained.
    [Fact]
    public async Task DegradedMode_PausesAtSafeBoundary_NoSyncAdvance()
    {
        using var dir = new TempDir();
        using var state = new SyncStateScope();
        try
        {
            Breakers.Initialize(new CircuitBreakerOptions { Enabled = true, FailureThreshold = 1 });
            Breakers.Graph.TripForTests();

            var source = new FakeBdhSource();
            source.Add("Contact/dt=2026-07-15/part-0000.jsonl", """{"Id":"C000000000001","Status":"Active"}""");
            var config = TestConfig.Make(allowFullScan: true);
            var schema = new SchemaConfig
            {
                ObjectList = new List<ObjectConfig>
                {
                    new() { ObjectName = "Contact", DisplayName = "Contact", AclMode = "public" },
                },
            };
            var graph = new FakeGraphClient(config);
            Func<string, IItemInventory> inv = id => new ItemInventory(id, Path.Combine(dir.Path, $"inv_{id}.db"));
            var pipeline = MemoryBoundStressTests.BuildPipeline(config, schema, source, graph, dir.Path, inv);

            var summary = await pipeline.RunAsync(fullCrawl: true);

            Assert.True(summary.Degraded);
            Assert.Equal(0, summary.Ingested);
            Assert.DoesNotContain(graph.Sent, s => s.Method == HttpMethod.Put || s.Path == "$batch");
            Assert.Null(SyncState.ReadLastSync("BdhHadoopMart"));
        }
        finally
        {
            Breakers.ResetForTests();
        }
    }
}

public class CheckpointResumeStressTests
{
    private const string Connector = "BdhHadoopMart";
    private static string Cid(int n) => $"C{n:D12}";

    // Scenario 6: interrupt a crawl mid-way (graceful stop after the first
    // chunk), then resume — no duplication (completed chunks are skipped, not
    // re-sent) and no lost records (every source id ends up ingested once).
    [Fact]
    public async Task InterruptedCrawl_Resumes_NoDuplication_NoLoss()
    {
        using var dir = new TempDir();
        using var state = new SyncStateScope();
        const int total = 200;

        var source = new FakeBdhSource();
        var sb = new StringBuilder();
        for (var i = 1; i <= total; i++)
            sb.Append($$"""{"Id":"{{Cid(i)}}","Status":"Active"}""").Append('\n');
        source.Add("Contact/dt=2026-07-15/part-0000.jsonl", sb.ToString());

        var config = TestConfig.Make(ingestChunkSize: 50, allowFullScan: true);
        var schema = new SchemaConfig
        {
            ObjectList = new List<ObjectConfig>
            {
                new() { ObjectName = "Contact", DisplayName = "Contact", AclMode = "public" },
            },
        };
        Func<string, IItemInventory> inv = id => new ItemInventory(id, Path.Combine(dir.Path, $"inv_{id}.db"));

        var graph1 = new FakeGraphClient(config);
        try
        {
            ServiceStop.Reset();
            var pipeline1 = MemoryBoundStressTests.BuildPipeline(config, schema, source, graph1, dir.Path, inv);
            // Request a graceful stop the instant the first chunk (50) completes.
            pipeline1.OnProgress = (_, done, _) => { if (done >= 50) ServiceStop.Request(); };
            var run1 = await pipeline1.RunAsync(fullCrawl: true);

            Assert.True(run1.Stopped);
            var checkpoint = SyncState.ReadCheckpoint(Connector);
            Assert.NotNull(checkpoint);
            Assert.Equal(1, checkpoint!["completed"]!["Contact"]!.GetValue<int>());
            Assert.Null(SyncState.ReadLastSync(Connector)); // cursor NOT advanced on stop

            ServiceStop.Reset();

            var graph2 = new FakeGraphClient(config);
            var pipeline2 = MemoryBoundStressTests.BuildPipeline(config, schema, source, graph2, dir.Path, inv);
            var run2 = await pipeline2.RunAsync(fullCrawl: true);

            Assert.False(run2.Stopped);
            Assert.True(run2.SkippedChunks >= 1);              // completed chunk skipped
            Assert.NotNull(SyncState.ReadLastSync(Connector)); // finished → cursor advanced

            var ingested1 = IngestedIds(graph1);
            var ingested2 = IngestedIds(graph2);

            // No duplication: the resumed run never re-sends a checkpointed chunk.
            Assert.Empty(ingested1.Intersect(ingested2, StringComparer.Ordinal));
            // No loss: union covers every source id exactly once.
            Assert.Equal(total, ingested1.Count + ingested2.Count);
            using var check = inv(Connector);
            Assert.Equal(total, check.IdsForObject("Contact").Count);
        }
        finally
        {
            ServiceStop.Reset();
        }
    }

    private static HashSet<string> IngestedIds(FakeGraphClient graph)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (method, path, body) in graph.Sent)
        {
            if (path == "$batch" && body?["requests"] is JsonArray reqs)
            {
                foreach (var r in reqs)
                {
                    if (r?["method"]?.GetValue<string>() == "PUT" && r["body"] is JsonObject b)
                        ids.Add(b["id"]!.GetValue<string>());
                }
            }
            else if (method == HttpMethod.Put)
            {
                ids.Add(path[(path.LastIndexOf('/') + 1)..]);
            }
        }
        return ids;
    }
}

public class BatchThroughputStressTests
{
    private const string Connector = "BdhHadoopMart";
    private static string Cid(int n) => $"C{n:D12}";

    /// <summary>Records every item id the (real) Graph client PUTs through $batch,
    /// and throttles each DISTINCT batch exactly once with a whole-response 429 +
    /// Retry-After. Throttling by batch signature (not physical request count) is
    /// deterministic under concurrent retries: every logical call is throttled
    /// once, then succeeds on retry — so the retry ladder is exercised and no
    /// item can be lost to retry exhaustion.</summary>
    private sealed class ThrottlingBatchHandler : HttpMessageHandler
    {
        private readonly HashSet<string> _throttledSignatures = new(StringComparer.Ordinal);
        private readonly object _lock = new();
        public ConcurrentDictionary<string, byte> ReceivedIds { get; } = new();
        public int Throttled429 { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var envelope = JsonNode.Parse(body)!.AsObject();
            var ids = envelope["requests"]!.AsArray()
                .Select(r => r!["body"] is JsonObject b ? b["id"]!.GetValue<string>() : "")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            var signature = string.Join(",", ids);

            bool throttle;
            lock (_lock)
            {
                throttle = _throttledSignatures.Add(signature); // first sighting → throttle once
                if (throttle) Throttled429++;
            }

            if (throttle)
            {
                var resp = new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                };
                resp.Headers.TryAddWithoutValidation("Retry-After", "1");
                return resp;
            }

            var responses = new JsonArray();
            foreach (var r in envelope["requests"]!.AsArray())
            {
                var reqId = r!["id"]!.GetValue<string>();
                if (r["body"] is JsonObject itemBody)
                    ReceivedIds.TryAdd(itemBody["id"]!.GetValue<string>(), 1);
                responses.Add(new JsonObject { ["id"] = reqId, ["status"] = 200 });
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    new JsonObject { ["responses"] = responses }.ToJsonString(),
                    Encoding.UTF8, "application/json"),
            };
        }
    }

    // Scenario 7: large ingest through the adaptive $batch path with induced
    // 429s — Retry-After honored, no lost items, correct final count.
    [Fact]
    public async Task BatchIngest_With429s_HonorsRetryAfter_NoLoss_CorrectCount()
    {
        using var dir = new TempDir();
        using var state = new SyncStateScope();
        const int total = 600;

        var source = new FakeBdhSource();
        var sb = new StringBuilder();
        for (var i = 1; i <= total; i++)
            sb.Append($$"""{"Id":"{{Cid(i)}}","Status":"Active"}""").Append('\n');
        source.Add("Contact/dt=2026-07-15/part-0000.jsonl", sb.ToString());

        var config = TestConfig.Make(
            ingestChunkSize: 100, graphBatchSize: 20, graphMaxRetries: 4,
            backoffBase: 1.0, allowFullScan: true);
        var schema = new SchemaConfig
        {
            ObjectList = new List<ObjectConfig>
            {
                new() { ObjectName = "Contact", DisplayName = "Contact", AclMode = "public" },
            },
        };

        var handler = new ThrottlingBatchHandler();
        var capturedDelays = new ConcurrentBag<TimeSpan>();
        var graph = new GraphClient(config, handler) { OverrideToken = "t" };
        graph.DelayAsync = (delay, _) => { capturedDelays.Add(delay); return Task.CompletedTask; };

        Func<string, IItemInventory> inv = id => new ItemInventory(id, Path.Combine(dir.Path, $"inv_{id}.db"));
        var pipeline = MemoryBoundStressTests.BuildPipeline(config, schema, source, graph, dir.Path, inv);

        var summary = await pipeline.RunAsync(fullCrawl: true);

        // Correct final count, zero losses.
        Assert.Equal(total, summary.Ingested);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(total, handler.ReceivedIds.Count);   // every id reached Graph
        Assert.True(handler.Throttled429 >= 5, $"expected induced 429s, got {handler.Throttled429}");

        // Retry-After honored: the server's 1s value drove a retry wait.
        Assert.Contains(capturedDelays, d => Math.Abs(d.TotalSeconds - 1.0) < 0.01);

        using var check = inv(Connector);
        Assert.Equal(total, check.IdsForObject("Contact").Count);
    }

    // Adaptive concurrency reacts to per-item 429 throttling and the pipeline
    // still accounts for every item (ingested + dead-lettered = total).
    [Fact]
    public async Task AdaptiveConcurrency_DialsDownOnThrottle_AllItemsAccounted()
    {
        var config = TestConfig.Make(graphBatchSize: 5);
        var concurrency = new AdaptiveConcurrency(config.GraphBatchWorkers);
        Assert.Equal(8, concurrency.Max);
        Assert.Equal(8, concurrency.Current);

        // A throttled window dials the concurrency down toward 1…
        for (var i = 0; i < 10; i++) concurrency.OnThrottle();
        Assert.Equal(1, concurrency.Current);

        // …and three clean windows ramp it back up by one step.
        concurrency.OnSuccess();
        concurrency.OnSuccess();
        concurrency.OnSuccess();
        Assert.Equal(2, concurrency.Current);
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// ROUND 2 — stress additions targeting the eight post-review fixes:
//
//   R2-1  Oversize/Incomplete at scale — randomized multi-partition crawls with
//         oversize files scattered at arbitrary positions: the sweep is
//         suppressed EVERY time any file was skipped, partial accounting is
//         exact, and the reconciler never marks un-read records stale.
//   R2-2  Guard bypass matrix — filter kind × fullScanAllowed × ALLOW_FULL_SCAN
//         × entry point (FetchAsync full/incremental, FindByIdAsync): the
//         fail-closed guard admits exactly the documented combinations.
//   R2-3  WebHDFS OpenAsync retry ladder under flapping datanodes — 429/5xx
//         waves with/without Retry-After (oversized values clamped to 60 s)
//         across concurrent opens: no breaker miscount, no HalfOpen slot leak,
//         throughput recovers.
//   R2-4  IdentitySync fail-loud under churn — directory exports flipping
//         between complete / row-capped / oversize-skipped: every incomplete
//         load throws + alerts and no partial directory is ever applied.
//   R2-6  Watermark/lag edge storm — dt partitions exactly at/before/after the
//         (since − BDH_LAG_HOURS) watermark, DST-like lags, malformed/missing
//         dt directories: nothing is wrongly included or excluded.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>Shared helpers for the round-2 randomized layouts.</summary>
internal static class R2
{
    /// <summary>JSONL row with a Graph-safe id (~52 bytes with the newline).</summary>
    internal static string Row(string id) =>
        $$"""{"Id":"{{id}}","Status":"Active","OwnerId":"o1"}""";

    internal static ObjectConfig Obj(string name) =>
        new() { ObjectName = name, DisplayName = name, AclMode = "public" };

    internal static SchemaConfig Schema(params string[] names) => new()
    {
        ObjectList = names.Select(Obj).ToList(),
    };

    internal static FilterSet AllowAll(params string[] names) => new(
        new Dictionary<string, ObjectFilter>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(names, StringComparer.OrdinalIgnoreCase));
}

// ─────────────────────────────────────────────────────────────────────────────
// R2-1: oversize-file skips at scale — sweep suppression + exact accounting
// ─────────────────────────────────────────────────────────────────────────────

public class OversizeSweepStressTests
{
    private const long MaxFileBytes = 2000;

    /// <summary>Build a random multi-partition layout. Normal files hold 20 rows
    /// (~1 KB &lt; MaxFileBytes); oversize files hold 50 rows (~2.6 KB &gt;
    /// MaxFileBytes) whose ids go to <paramref name="hidden"/> — they exist in
    /// BDH but are never read.</summary>
    private static (int TotalFiles, int OversizeFiles) BuildLayout(
        FakeBdhSource source, Random rng, int crawl,
        HashSet<string> visible, HashSet<string> hidden, int? forceOversize = null)
    {
        var partitions = 3 + rng.Next(6);          // 3..8 dt partitions
        var totalFiles = 0;
        var oversizeFiles = 0;
        var oversizeBudget = forceOversize ?? -1;  // -1 → random scatter
        for (var p = 0; p < partitions; p++)
        {
            var dt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddDays(p).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var files = 1 + rng.Next(3);           // 1..3 files per partition
            for (var f = 0; f < files; f++)
            {
                totalFiles++;
                var oversize = oversizeBudget >= 0
                    ? oversizeFiles < oversizeBudget  // forced: first N files oversize
                    : rng.Next(4) == 0;               // random: ~25% of files oversize
                var sb = new StringBuilder();
                if (oversize)
                {
                    oversizeFiles++;
                    for (var r = 0; r < 50; r++)
                    {
                        var id = $"H{crawl:D2}{p:D2}{f:D2}{r:D4}";
                        hidden.Add(id);
                        sb.Append(R2.Row(id)).Append('\n');
                    }
                }
                else
                {
                    for (var r = 0; r < 20; r++)
                    {
                        var id = $"V{crawl:D2}{p:D2}{f:D2}{r:D4}";
                        visible.Add(id);
                        sb.Append(R2.Row(id)).Append('\n');
                    }
                }
                source.Add($"Contact/dt={dt}/part-{f:D4}.jsonl", sb.ToString());
            }
        }
        return (totalFiles, oversizeFiles);
    }

    // R2-1a: randomized fetcher-level crawls — SkippedOversize/Incomplete set
    // exactly when ≥1 file skipped; per-stage accounting exact every time.
    [Theory]
    [InlineData(101)]
    [InlineData(202)]
    [InlineData(303)]
    public async Task RandomOversizeScatter_ExactAccounting_IncompleteIffAnySkip(int seed)
    {
        var rng = new Random(seed);
        const int crawls = 20;
        var sawSkip = 0;
        var sawClean = 0;

        for (var c = 0; c < crawls; c++)
        {
            var source = new FakeBdhSource();
            var visible = new HashSet<string>(StringComparer.Ordinal);
            var hidden = new HashSet<string>(StringComparer.Ordinal);
            // Crawl 0 is a forced clean control (a ~25%-per-file scatter can
            // legitimately produce zero clean crawls in 20 draws); crawl 1 is a
            // forced single-skip control; the rest scatter randomly.
            int? force = c == 0 ? 0 : c == 1 ? 1 : null;
            var (totalFiles, oversizeFiles) = BuildLayout(source, rng, c, visible, hidden, force);

            var config = TestConfig.Make(allowFullScan: true, maxFileBytes: MaxFileBytes);
            var fetcher = new BdhFetcher(config, source, FilterSet.Empty);
            var result = await fetcher.FetchAsync(R2.Obj("Contact"), fullCrawl: true, sinceUtc: null);

            // The partial flag is set EVERY time any file was skipped — never
            // for a clean crawl, never missed for a dirty one.
            Assert.Equal(oversizeFiles > 0, result.SkippedOversize);
            Assert.Equal(oversizeFiles > 0, result.Incomplete);
            Assert.False(result.Truncated);

            // Exact partial-object accounting.
            Assert.Equal(oversizeFiles, result.Stats.FilesSkippedOversize);
            Assert.Equal(totalFiles - oversizeFiles, result.Stats.FilesRead);
            Assert.Equal(visible, result.Records.Select(r => r.ItemId).ToHashSet(StringComparer.Ordinal));
            Assert.Equal(visible.Count, (int)result.Stats.RecordsMatched);
            Assert.DoesNotContain(result.Records, r => hidden.Contains(r.ItemId));

            if (oversizeFiles > 0) sawSkip++; else sawClean++;
        }

        // The randomization actually exercised both branches.
        Assert.True(sawSkip >= 3, $"seed produced only {sawSkip} skip crawls");
        Assert.True(sawClean >= 1, $"seed produced only {sawClean} clean crawls");
    }

    // R2-1b: full-pipeline randomized crawls — the deletion sweep is suppressed
    // EVERY time a file was skipped and NO un-read (hidden) record is ever
    // deleted: false deletions must be 0 across all crawls.
    [Fact]
    public async Task RandomizedCrawls_SweepSuppressedEveryTime_ZeroFalseDeletions()
    {
        var rng = new Random(777);
        const int crawls = 12;
        var sweepsSuppressed = 0;
        var sweepsRun = 0;
        var falseDeletions = 0;

        for (var c = 0; c < crawls; c++)
        {
            using var dir = new TempDir();
            using var state = new SyncStateScope();
            using var env = new EnvScope(
                ("DELETION_SYNC", null), ("DELETION_SYNC_MAX_ITEMS", null),
                ("DELETION_SYNC_MAX_PERCENT", null), ("GRAPH_CONNECTION_SHARDS", null));
            const string connector = "BdhHadoopMart";

            var source = new FakeBdhSource();
            var visible = new HashSet<string>(StringComparer.Ordinal);
            var hidden = new HashSet<string>(StringComparer.Ordinal);
            // First two crawls are forced controls (clean / with 2 oversize
            // files); the rest scatter randomly.
            int? force = c == 0 ? 0 : c == 1 ? 2 : null;
            var (_, oversizeFiles) = BuildLayout(source, rng, c, visible, hidden, force);

            var config = TestConfig.Make(
                ingestChunkSize: 100, allowFullScan: true, maxFileBytes: MaxFileBytes);
            var schema = R2.Schema("Contact");
            Func<string, IItemInventory> inv =
                id => new ItemInventory(id, Path.Combine(dir.Path, $"inv_{id}.db"));

            // Seed the inventory: every hidden id (live in BDH, just un-read this
            // crawl) plus one genuinely-stale id.
            const string staleId = "STALE00000001";
            using (var seed = inv(connector))
            {
                seed.RecordSeen(
                    hidden.Select(h => (h, "Contact")).Append((staleId, "Contact")),
                    DateTime.UtcNow);
            }

            var graph = new FakeGraphClient(config);
            var pipeline = MemoryBoundStressTests.BuildPipeline(config, schema, source, graph, dir.Path, inv);
            var summary = await pipeline.RunAsync(fullCrawl: true);

            using var check = inv(connector);
            var after = check.IdsForObject("Contact").ToHashSet(StringComparer.Ordinal);
            falseDeletions += hidden.Count(h => !after.Contains(h));

            if (oversizeFiles > 0)
            {
                sweepsSuppressed++;
                Assert.Contains("Contact", summary.PartialObjects);
                Assert.Contains("Contact", summary.SweepSkipped);
                Assert.Equal(0, summary.Deleted);
                // Sweep suppressed as a whole: even the genuinely-stale id survives.
                Assert.Contains(staleId, after);
                Assert.DoesNotContain(graph.Sent, s =>
                    s.Method == HttpMethod.Delete
                    || (s.Path == "$batch" && HasDelete(s.Body)));
            }
            else
            {
                sweepsRun++;
                Assert.DoesNotContain("Contact", summary.PartialObjects);
                Assert.DoesNotContain("Contact", summary.SweepSkipped);
                // A clean crawl still sweeps correctly: exactly the stale id goes.
                Assert.Equal(1, summary.Deleted);
                Assert.DoesNotContain(staleId, after);
            }
            // Every visible id ingested and present regardless of branch.
            Assert.True(visible.IsSubsetOf(after), "ingested ids must stay in the inventory");
        }

        Assert.Equal(0, falseDeletions);
        Assert.True(sweepsSuppressed >= 3, $"only {sweepsSuppressed} suppressed sweeps exercised");
        Assert.True(sweepsRun >= 1, $"only {sweepsRun} clean sweeps exercised");
    }

    private static bool HasDelete(JsonNode? body) =>
        body?["requests"] is JsonArray reqs
        && reqs.Any(r => r?["method"]?.GetValue<string>() == "DELETE");

    // R2-1c: the reconciler never marks un-read records stale — an oversize skip
    // downgrades that object to counts-only (no Missing/Stale, no --fix
    // deletes), while OTHER objects still reconcile normally.
    [Fact]
    public async Task Reconciler_OversizeSkip_NeverMarksUnreadStale_OthersStillReconcile()
    {
        using var dir = new TempDir();
        using var state = new SyncStateScope();
        using var env = new EnvScope(("GRAPH_CONNECTION_SHARDS", null));
        const string connector = "BdhHadoopMart";

        var source = new FakeBdhSource();
        var sVis = Enumerable.Range(0, 20).Select(i => $"SVIS{i:D9}").ToList();
        var sHid = Enumerable.Range(0, 50).Select(i => $"SHID{i:D9}").ToList();
        var kIds = Enumerable.Range(0, 20).Select(i => $"KEEP{i:D9}").ToList();
        source.Add("Skippy/dt=2026-07-15/vis.jsonl",
            string.Join('\n', sVis.Select(R2.Row)) + "\n");
        source.Add("Skippy/dt=2026-07-16/huge.jsonl",
            string.Join('\n', sHid.Select(R2.Row)) + "\n");   // ~2.6 KB > MaxFileBytes
        source.Add("Clean/dt=2026-07-15/ok.jsonl",
            string.Join('\n', kIds.Select(R2.Row)) + "\n");

        var config = TestConfig.Make(maxFileBytes: MaxFileBytes);
        var schema = R2.Schema("Skippy", "Clean");
        var fetcher = new BdhFetcher(config, source, R2.AllowAll("Skippy", "Clean"));
        Func<string, IItemInventory> inv =
            id => new ItemInventory(id, Path.Combine(dir.Path, $"inv_{id}.db"));

        const string cleanStale = "KSTALE0000001";
        using (var seed = inv(connector))
        {
            seed.RecordSeen(sVis.Concat(sHid).Select(id => (id, "Skippy")), DateTime.UtcNow);
            seed.RecordSeen(kIds.Append(cleanStale).Select(id => (id, "Clean")), DateTime.UtcNow);
        }

        var graph = new FakeGraphClient(config);
        var reconciler = new Reconciler(config, schema, fetcher, graph, inv);
        var report = await reconciler.ReconcileAsync(fix: true);

        var skippy = report.Objects.Single(o => o.ObjectName == "Skippy");
        var clean = report.Objects.Single(o => o.ObjectName == "Clean");

        // Skippy: counts-only — the un-read (hidden) records are NOT stale.
        Assert.Empty(skippy.Stale);
        Assert.Empty(skippy.Missing);
        Assert.Equal(0, skippy.FixedCount);
        Assert.Equal(sVis.Count, skippy.SourceCount);
        Assert.Equal(sVis.Count + sHid.Count, skippy.IndexedCount);

        // Clean: reconciles normally — exactly its stale id fixed.
        Assert.Equal(new[] { cleanStale }, clean.Stale);
        Assert.Equal(1, clean.FixedCount);

        // No DELETE ever touched a hidden id; only the clean stale id was deleted.
        var deletes = graph.Sent.Where(s => s.Method == HttpMethod.Delete).Select(s => s.Path).ToList();
        Assert.Single(deletes);
        Assert.Contains(cleanStale, deletes[0]);
        Assert.DoesNotContain(deletes, d => sHid.Any(d.Contains));

        using var check = inv(connector);
        Assert.Equal(sHid.Count + sVis.Count,
            check.IdsForObject("Skippy").Count);              // hidden ids all survived
        Assert.DoesNotContain(cleanStale, check.IdsForObject("Clean"));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// R2-2: fail-closed guard bypass matrix (FetchAsync + FindByIdAsync)
// ─────────────────────────────────────────────────────────────────────────────

public class GuardBypassMatrixStressTests
{
    private sealed record FilterCase(string Name, string? Json, bool EffectivelyFiltered);

    /// <summary>Every filter shape × whether it must satisfy the guard.
    /// A predicate on a NON-dt key never guarantees pruning (absent keys are
    /// skipped), and dt isNotNull matches every present dt — neither counts.</summary>
    private static readonly FilterCase[] FilterCases =
    {
        new("none", null, false),
        new("emptyFilter", """{"objects":{"Contact":{}}}""", false),
        new("recordOnly",
            """{"objects":{"Contact":{"allOf":[{"field":"Status","op":"equals","value":"Active"}]}}}""",
            true),
        new("dtWithinLastDays",
            """{"objects":{"Contact":{"partition":[{"key":"dt","op":"withinLastDays","value":"30"}]}}}""",
            true),
        new("dtEquals",
            """{"objects":{"Contact":{"partition":[{"key":"dt","op":"equals","value":"2026-07-15"}]}}}""",
            true),
        new("dtBefore",
            """{"objects":{"Contact":{"partition":[{"key":"dt","op":"before","value":"2026-07-16"}]}}}""",
            true),
        new("nonDtOnly",
            """{"objects":{"Contact":{"partition":[{"key":"region","op":"equals","value":"EU"}]}}}""",
            false),
        new("dtPlusNonDt",
            """{"objects":{"Contact":{"partition":[{"key":"region","op":"equals","value":"EU"},{"key":"dt","op":"withinLastDays","value":"30"}]}}}""",
            true),
        new("recordPlusNonDt",
            """{"objects":{"Contact":{"partition":[{"key":"region","op":"equals","value":"EU"}],"allOf":[{"field":"Status","op":"equals","value":"Active"}]}}}""",
            true),
        // dt isNotNull matches every present dt value, so it can never prune a
        // single partition — a non-pruning partition-only config MUST refuse.
        new("dtIsNotNull",
            """{"objects":{"Contact":{"partition":[{"key":"dt","op":"isNotNull"}]}}}""",
            false),
        // dt isNull prunes every conventional dt partition (over-prunes, reads
        // nothing) — it IS a pruning predicate, so it satisfies the guard.
        new("dtIsNull",
            """{"objects":{"Contact":{"partition":[{"key":"dt","op":"isNull"}]}}}""",
            true),
    };

    private static FilterSet BuildFilters(FilterCase fc, bool listed)
    {
        var baseSet = fc.Json is null ? FilterSet.Empty : FilterSet.Parse(fc.Json);
        if (!listed)
            return baseSet;
        return new FilterSet(
            baseSet.Objects.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Contact" });
    }

    // R2-2a: the full matrix — filter kind × fullScanAllowed × ALLOW_FULL_SCAN
    // × entry point. Guard admits exactly (effectivelyFiltered || listed ||
    // allowEnv); every refusal happens with ZERO source I/O. 0 bypasses.
    [Fact]
    public async Task GuardMatrix_FailsClosed_EveryCombination()
    {
        var now = new DateTime(2026, 7, 17, 0, 0, 0, DateTimeKind.Utc);
        var mismatches = new List<string>();
        var combos = 0;
        var refusals = 0;

        foreach (var fc in FilterCases)
        foreach (var listed in new[] { false, true })
        foreach (var allowEnv in new[] { false, true })
        foreach (var entry in new[] { "fetch-full", "fetch-incr", "find-by-id" })
        {
            combos++;
            var expectAllowed = fc.EffectivelyFiltered || listed || allowEnv;

            var source = new FakeBdhSource()
                .Add("Contact/dt=2026-07-15/p.jsonl", R2.Row("C000000000001") + "\n");
            var config = TestConfig.Make(allowFullScan: allowEnv, lagHours: 24);
            var fetcher = new BdhFetcher(config, source, BuildFilters(fc, listed), () => now);
            var obj = R2.Obj("Contact");

            var refused = false;
            try
            {
                switch (entry)
                {
                    case "fetch-full":
                        await fetcher.FetchAsync(obj, fullCrawl: true, sinceUtc: null);
                        break;
                    case "fetch-incr":
                        await fetcher.FetchAsync(obj, fullCrawl: false, sinceUtc: now.AddDays(-5));
                        break;
                    default:
                        await fetcher.FindByIdAsync(obj, "C000000000001");
                        break;
                }
            }
            catch (FullScanRefusedException)
            {
                refused = true;
                refusals++;
            }

            if (refused == expectAllowed)
            {
                mismatches.Add(
                    $"{fc.Name} listed={listed} env={allowEnv} entry={entry}: "
                    + (refused ? "REFUSED but should be allowed" : "BYPASS — allowed but must refuse"));
            }
            if (refused && (source.ListCalls != 0 || source.OpenCalls != 0))
            {
                mismatches.Add(
                    $"{fc.Name} listed={listed} env={allowEnv} entry={entry}: refusal touched I/O "
                    + $"(lists={source.ListCalls}, opens={source.OpenCalls})");
            }

            // validate-config --strict must agree with the guard predicate
            // (deliberately ignoring the ALLOW_FULL_SCAN emergency override).
            var strict = fetcher.UnfilteredObjects(new[] { obj });
            var expectStrictFlag = !fc.EffectivelyFiltered && !listed;
            if (strict.Contains("Contact") != expectStrictFlag)
            {
                mismatches.Add(
                    $"{fc.Name} listed={listed}: validate-config --strict disagrees with the guard");
            }
        }

        Assert.True(combos == FilterCases.Length * 2 * 2 * 3, $"matrix incomplete: {combos}");
        Assert.True(refusals > 0, "matrix never exercised a refusal");
        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} guard defect(s):\n  " + string.Join("\n  ", mismatches));
    }

    // R2-2b (REGRESSION, focused): a partition-only filter of dt isNotNull can
    // never prune (every present dt matches), so the guard must refuse it —
    // otherwise "add any dt predicate" silently re-opens the 150M full scan.
    [Fact]
    public async Task DtIsNotNull_PartitionOnlyFilter_MustRefuse()
    {
        var filters = FilterSet.Parse(
            """{"objects":{"Contact":{"partition":[{"key":"dt","op":"isNotNull"}]}}}""");
        var source = new FakeBdhSource().Add("Contact/dt=2026-07-15/p.jsonl", R2.Row("C1") + "\n");
        var fetcher = new BdhFetcher(TestConfig.Make(), source, filters);

        await Assert.ThrowsAsync<FullScanRefusedException>(
            () => fetcher.FetchAsync(R2.Obj("Contact"), fullCrawl: true, sinceUtc: null));
        await Assert.ThrowsAsync<FullScanRefusedException>(
            () => fetcher.FindByIdAsync(R2.Obj("Contact"), "C000000000001"));
        Assert.Equal(0, source.ListCalls);

        // …and validate-config --strict flags it.
        Assert.Equal(new[] { "Contact" }, fetcher.UnfilteredObjects(new[] { R2.Obj("Contact") }));
    }

    // R2-2c (REGRESSION): FindByIdAsync silently skips oversize files, so a
    // null result is NOT proof the record is gone — the detailed lookup must
    // report the incomplete search so retry-failed keeps (not drops) the entry.
    [Fact]
    public async Task FindByIdDetailed_RecordHiddenInOversizeFile_ReportsIncompleteSearch()
    {
        const long maxFileBytes = 2000;
        var source = new FakeBdhSource();
        source.Add("Contact/dt=2026-07-15/small.jsonl",
            R2.Row("CSMALL000001") + "\n" + R2.Row("CSMALL000002") + "\n");
        source.Add("Contact/dt=2026-07-16/huge.jsonl",
            string.Join('\n', Enumerable.Range(0, 50).Select(i => R2.Row($"CHUGE{i:D7}"))) + "\n");

        var fetcher = new BdhFetcher(
            TestConfig.Make(maxFileBytes: maxFileBytes), source, R2.AllowAll("Contact"));
        var obj = R2.Obj("Contact");

        // The target lives only in the oversize file → not found, but the
        // search was INCOMPLETE and must say so.
        var incomplete = await fetcher.FindByIdDetailedAsync(obj, "CHUGE0000007");
        Assert.Null(incomplete.Record);
        Assert.True(incomplete.SkippedOversize);
        Assert.True(incomplete.Incomplete);
        Assert.False(RetryFailed.ShouldDropMissing(incomplete)); // dead-letter entry kept

        // A genuinely-absent id with no skip anywhere IS a definitive miss.
        var cleanSource = new FakeBdhSource().Add(
            "Contact/dt=2026-07-15/small.jsonl", R2.Row("CSMALL000001") + "\n");
        var cleanFetcher = new BdhFetcher(
            TestConfig.Make(maxFileBytes: maxFileBytes), cleanSource, R2.AllowAll("Contact"));
        var definitive = await cleanFetcher.FindByIdDetailedAsync(obj, "CGONE0000001");
        Assert.Null(definitive.Record);
        Assert.False(definitive.SkippedOversize);
        Assert.False(definitive.Incomplete);
        Assert.True(RetryFailed.ShouldDropMissing(definitive)); // safe to drop

        // A record FOUND in a readable file wins even when some other file was
        // skipped — the result is complete for the caller's purpose.
        var found = await fetcher.FindByIdDetailedAsync(obj, "CSMALL000002");
        Assert.NotNull(found.Record);
        Assert.Equal("CSMALL000002", found.Record!.ItemId);
        Assert.False(found.Incomplete);

        // Compat: the simple lookup still returns null/record identically.
        Assert.Null(await fetcher.FindByIdAsync(obj, "CHUGE0000007"));
        Assert.NotNull(await fetcher.FindByIdAsync(obj, "CSMALL000002"));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// R2-3: WebHDFS OpenAsync retry ladder under flapping datanodes
// ─────────────────────────────────────────────────────────────────────────────

public class OpenRetryLadderStressTests
{
    private const string Namenode = "http://nn.example:9870/webhdfs/v1";

    /// <summary>Thread-safe scripted handler keyed on request path, tracking a
    /// per-path attempt counter — safe under many concurrent OPENs.</summary>
    private sealed class FlappingHandler : HttpMessageHandler
    {
        private readonly Func<string, int, HttpResponseMessage> _script;
        private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);
        private readonly object _lock = new();
        private int _requests;

        public FlappingHandler(Func<string, int, HttpResponseMessage> script) => _script = script;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _requests);
            var path = request.RequestUri!.AbsolutePath;
            int attempt;
            lock (_lock)
            {
                _attempts.TryGetValue(path, out attempt);
                _attempts[path] = attempt + 1;
            }
            return Task.FromResult(_script(path, attempt));
        }
    }

    private static HttpResponseMessage Resp(int status, string body = "{}", string? retryAfter = null)
    {
        var resp = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (retryAfter is not null)
            resp.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return resp;
    }

    private static CircuitBreaker Breaker(Func<DateTime>? clock = null, int threshold = 5) =>
        new("hdfs", new CircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = threshold,
            OpenDuration = TimeSpan.FromSeconds(30),
            Window = TimeSpan.FromSeconds(600),
            HalfOpenTrials = 2,
        }, clock);

    // R2-3a: 120 concurrent opens through 429/503 waves (Retry-After honored
    // exactly; 7200 s clamped to 60 s) — every open recovers, the breaker never
    // counts a recovered flap (0 trips), and the retry volume is exact.
    [Fact]
    public async Task ConcurrentFlappingOpens_AllRecover_NoBreakerMiscount_DelaysClamped()
    {
        const int files = 120;
        static int FailuresFor(int i) => 1 + (i % 3);          // 1..3 pre-success failures
        static int Wave(int i) => i % 4;

        var handler = new FlappingHandler((path, attempt) =>
        {
            var i = int.Parse(
                Path.GetFileNameWithoutExtension(path)["part-".Length..],
                CultureInfo.InvariantCulture);
            if (attempt < FailuresFor(i))
            {
                return Wave(i) switch
                {
                    0 => Resp(503),                            // exp backoff 2/4/8 s
                    1 => Resp(429, retryAfter: "3"),           // honored exactly
                    2 => Resp(503, retryAfter: "7200"),        // clamped to 60 s
                    _ => Resp(503, retryAfter: "1"),
                };
            }
            return Resp(200, $"ok-{i}");
        });

        var breaker = Breaker();
        var client = new WebHdfsClient(Namenode, "/data/bdh", "svc", null, handler, breaker);
        var delays = new ConcurrentBag<double>();
        client.DelayAsync = (d, _) => { delays.Add(d.TotalSeconds); return Task.CompletedTask; };

        var sw = Stopwatch.StartNew();
        var bodies = await Task.WhenAll(Enumerable.Range(0, files).Select(async i =>
        {
            await using var stream = await client.OpenAsync($"Contact/dt=2026-07-15/part-{i}.jsonl");
            using var reader = new StreamReader(stream);
            return (i, Body: await reader.ReadToEndAsync());
        }));
        sw.Stop();

        // Every open recovered with the right content.
        Assert.All(bodies, b => Assert.Equal($"ok-{b.i}", b.Body));

        // No breaker miscount: recovered flaps never count as failures.
        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal(0, breaker.Trips);

        // Exact retry volume: Σ failures + one success per file.
        var expectedRetries = Enumerable.Range(0, files).Sum(FailuresFor);
        Assert.Equal(files + expectedRetries, handler.Requests);
        Assert.Equal(expectedRetries, delays.Count);

        // Retry-After honored exactly and clamped to 60 s; backoff otherwise.
        Assert.All(delays, d => Assert.True(d <= 60.0, $"delay {d}s exceeds the 60s clamp"));
        int RetriesInWave(int w) => Enumerable.Range(0, files).Where(i => Wave(i) == w).Sum(FailuresFor);
        Assert.Equal(RetriesInWave(1), delays.Count(d => Math.Abs(d - 3.0) < 0.001));
        Assert.Equal(RetriesInWave(2), delays.Count(d => Math.Abs(d - 60.0) < 0.001));
        Assert.Equal(RetriesInWave(3), delays.Count(d => Math.Abs(d - 1.0) < 0.001));
        Assert.Equal(RetriesInWave(0), delays.Count(d => d is 2.0 or 4.0 or 8.0));
    }

    // R2-3b: terminal 5xx failures trip the breaker exactly at threshold; the
    // open breaker fails fast with zero network; HalfOpen probes that end in
    // terminal 429 release their slot every time (no leak, no wedge); recovery
    // closes the breaker and full concurrent throughput resumes.
    [Fact]
    public async Task TerminalFailures_TripAtThreshold_FailFast_NoHalfOpenSlotLeak_ThenRecover()
    {
        var clock = new DateTime(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
        var breaker = Breaker(() => clock);
        var healed = false;

        var handler = new FlappingHandler((path, _) =>
        {
            if (path.Contains("/bad/", StringComparison.Ordinal))
                return Resp(503, "boom");
            if (path.Contains("/probe429/", StringComparison.Ordinal) && !healed)
                return Resp(429, "slow down");
            return Resp(200, "ok");
        });
        var client = new WebHdfsClient(Namenode, "/data/bdh", "svc", null, handler, breaker);
        client.DelayAsync = (_, _) => Task.CompletedTask;

        // Phase 1 — five terminal 503 opens (each exhausts MaxRetries=4 → 5 HTTP
        // calls) count 5 real failures → the breaker trips exactly at threshold.
        for (var i = 0; i < 5; i++)
        {
            var exc = await Assert.ThrowsAsync<HdfsException>(
                () => client.OpenAsync($"bad/part-{i}.jsonl"));
            Assert.Equal(503, exc.StatusCode);
        }
        Assert.Equal(CircuitState.Open, breaker.State);
        Assert.Equal(1, breaker.Trips);
        Assert.Equal(25, handler.Requests);

        // Phase 2 — 50 concurrent opens against the OPEN breaker: all rejected
        // fast, ZERO network calls.
        var rejections = await Task.WhenAll(Enumerable.Range(0, 50).Select(async i =>
        {
            try { await client.OpenAsync($"good/part-{i}.jsonl"); return false; }
            catch (CircuitOpenException) { return true; }
        }));
        Assert.All(rejections, r => Assert.True(r));
        Assert.Equal(25, handler.Requests);                    // still zero network

        // Phase 3 — past OpenDuration the breaker is HalfOpen. Eight successive
        // probes end in TERMINAL 429 (ignored outcome): each must release its
        // probe slot (settle-once). With a leak, probe 3+ would be rejected and
        // the breaker could never close.
        clock = clock.AddSeconds(31);
        Assert.Equal(CircuitState.HalfOpen, breaker.State);
        for (var i = 0; i < 8; i++)
        {
            var exc = await Assert.ThrowsAsync<HdfsException>(
                () => client.OpenAsync($"probe429/part-{i}.jsonl"));
            Assert.Equal(429, exc.StatusCode);
            Assert.Equal(CircuitState.HalfOpen, breaker.State); // never wedged Open/exhausted
        }

        // Phase 4 — the datanodes heal: HalfOpenTrials successes close it.
        healed = true;
        (await client.OpenAsync("good/heal-0.jsonl")).Dispose();
        (await client.OpenAsync("good/heal-1.jsonl")).Dispose();
        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal(1, breaker.Resets);

        // Phase 5 — throughput recovers: 40 concurrent opens all succeed.
        var before = handler.Requests;
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(async i =>
        {
            await using var s = await client.OpenAsync($"good/final-{i}.jsonl");
            using var r = new StreamReader(s);
            return await r.ReadToEndAsync();
        }));
        Assert.All(results, r => Assert.Equal("ok", r));
        Assert.Equal(before + 40, handler.Requests);           // one call each, no retries
        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal(1, breaker.Trips);                        // no new trips after recovery
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// R2-4: IdentitySync fail-loud under directory churn
// ─────────────────────────────────────────────────────────────────────────────

public class IdentityChurnStressTests
{
    /// <summary>IBdhSource wrapper whose inner source can be swapped between
    /// syncs (the nightly BDH User export changing shape).</summary>
    private sealed class MutableSource : IBdhSource
    {
        public FakeBdhSource Inner { get; set; } = new();
        public string Description => "mutable";
        public Task<List<HdfsFileStatus>> ListAsync(string p, CancellationToken ct = default) =>
            Inner.ListAsync(p, ct);
        public Task<bool> ExistsAsync(string p, CancellationToken ct = default) =>
            Inner.ExistsAsync(p, ct);
        public Task<Stream> OpenAsync(string p, CancellationToken ct = default) =>
            Inner.OpenAsync(p, ct);
        public void Dispose() { }
    }

    private sealed class RecordingIdentityStore : IIdentityStore
    {
        private readonly Dictionary<string, PrincipalMapping> _rows = new(StringComparer.Ordinal);
        public int UpsertCalls { get; private set; }
        public void Upsert(PrincipalMapping mapping)
        {
            UpsertCalls++;
            _rows[mapping.SourceId] = mapping;
        }
        public PrincipalMapping? Find(string sourceId) => _rows.GetValueOrDefault(sourceId);
        public List<PrincipalMapping> All() => _rows.Values.ToList();
        public int ResolvedCount() => _rows.Values.Count(m => m.EntraId is not null);
        public int Count() => _rows.Count;
        public void Clear() => _rows.Clear();
        public void Dispose() { }
    }

    private static string UserRow(int n) =>
        $$"""{"Id":"U{{n:D7}}","Email":"user{{n}}@contoso.com","Name":"User {{n}}","IsActive":"true"}""";

    /// <summary>Install a complete/capped/oversize export in the source.
    /// Row cap = 100; file bound = 16384 bytes.</summary>
    private static void InstallExport(MutableSource source, string kind, int users)
    {
        var inner = new FakeBdhSource();
        switch (kind)
        {
            case "complete":  // ≤ cap, ≤ file bound
                inner.Add("User/dt=2026-07-15/part-0000.jsonl",
                    string.Join('\n', Enumerable.Range(1, users).Select(UserRow)) + "\n");
                break;
            case "capped":    // 150 rows > 100-row cap (file still under the byte bound)
                inner.Add("User/dt=2026-07-15/part-0000.jsonl",
                    string.Join('\n', Enumerable.Range(1, 150).Select(UserRow)) + "\n");
                break;
            default:          // oversize: one readable file + one skipped (> 16384 bytes)
                inner.Add("User/dt=2026-07-15/part-0000.jsonl",
                    string.Join('\n', Enumerable.Range(1, 40).Select(UserRow)) + "\n");
                inner.Add("User/dt=2026-07-15/part-0001.jsonl",
                    string.Join('\n', Enumerable.Range(1000, 260).Select(UserRow)) + "\n");
                break;
        }
        source.Inner = inner;
    }

    // R2-4: exports flip complete → row-capped → complete → oversize-skipped →
    // complete (+ a randomized churn tail). EVERY incomplete load throws AND
    // alerts; NO partial directory is ever applied to the store; a subsequent
    // complete load recovers cleanly.
    [Fact]
    public async Task ChurningDirectory_IncompleteAlwaysThrowsAndAlerts_NoPartialEverApplied()
    {
        using var env = new EnvScope((Alerting.WebhookUrlEnvVar, "https://hooks.example/alerts"));
        var alertHandler = new MockHttpHandler((_, _) => MockHttpHandler.Json(HttpStatusCode.OK, "{}"));
        var previousClient = Alerting.HttpClient;
        Alerting.HttpClient = new HttpClient(alertHandler);
        try
        {
            var source = new MutableSource();
            var config = TestConfig.Make(maxRecordsPerObject: 100, maxFileBytes: 16384);
            var fetcher = new BdhFetcher(config, source, FilterSet.Empty);

            // Graph resolves every email lookup to a fixed Entra id.
            var graphHandler = new MockHttpHandler((request, _) =>
                request.RequestUri!.AbsoluteUri.Contains("oauth2", StringComparison.OrdinalIgnoreCase)
                    ? MockHttpHandler.Json(HttpStatusCode.OK, """{"access_token":"t","expires_in":3600}""")
                    : MockHttpHandler.Json(HttpStatusCode.OK, """{"id":"aad-00000000"}"""));
            var graph = new GraphClient(config, graphHandler) { OverrideToken = "t" };
            graph.DelayAsync = (_, _) => Task.CompletedTask;

            var sync = new IdentitySync(fetcher, graph, config);
            var store = new RecordingIdentityStore();

            // Fixed schedule + seeded random churn tail.
            var schedule = new List<(string Kind, int Users)>
            {
                ("complete", 60), ("capped", 0), ("complete", 70), ("oversize", 0), ("complete", 80),
            };
            var rng = new Random(4242);
            for (var i = 0; i < 20; i++)
            {
                schedule.Add(rng.Next(3) switch
                {
                    0 => ("complete", 30 + rng.Next(60)),
                    1 => ("capped", 0),
                    _ => ("oversize", 0),
                });
            }

            var applied = 0;
            var incompleteThrows = 0;
            var maxAppliedUsers = 0;
            int IdentityAlerts() => alertHandler.Requests.Count(r =>
                r.Body.Contains("\"identity_directory_incomplete\"", StringComparison.Ordinal));

            foreach (var (kind, users) in schedule)
            {
                InstallExport(source, kind, users);
                var storeCountBefore = store.Count();
                var upsertsBefore = store.UpsertCalls;
                var identityAlertsBefore = IdentityAlerts();

                if (kind == "complete")
                {
                    var directory = await sync.LoadDirectoryAsync();
                    Assert.Equal(users, directory.Count);
                    var result = await sync.SyncAsync(directory, store, persist: true);
                    Assert.Equal(users, result.UsersTotal);
                    Assert.Equal(users, result.UsersResolved);   // graph resolved everyone
                    applied++;
                    maxAppliedUsers = Math.Max(maxAppliedUsers, users);
                    Assert.Equal(maxAppliedUsers, store.Count()); // ids overlap; upserts keyed
                    Assert.Equal(identityAlertsBefore, IdentityAlerts()); // no spurious alert
                }
                else
                {
                    var exc = await Assert.ThrowsAsync<InvalidDataException>(
                        () => sync.LoadDirectoryAsync());
                    Assert.Contains("INCOMPLETE", exc.Message);
                    incompleteThrows++;

                    // Fail-loud: exactly one identity_directory_incomplete alert
                    // fired for this load (a capped load ALSO raises the
                    // fetcher's row_cap_hit alert first — a second, distinct kind).
                    Assert.Equal(identityAlertsBefore + 1, IdentityAlerts());
                    var alert = JsonNode.Parse(alertHandler.Requests[^1].Body)!;
                    Assert.Equal("identity_directory_incomplete", alert["kind"]!.GetValue<string>());
                    var truncated = alert["data"]!["truncated"]!.GetValue<bool>();
                    var skipped = alert["data"]!["skippedOversize"]!.GetValue<bool>();
                    Assert.Equal(kind == "capped", truncated);
                    Assert.Equal(kind == "oversize", skipped);

                    // NO partial application: the store is byte-for-byte untouched.
                    Assert.Equal(storeCountBefore, store.Count());
                    Assert.Equal(upsertsBefore, store.UpsertCalls);
                }
            }

            // The churn really exercised both branches, and only complete loads
            // ever touched the store.
            Assert.True(applied >= 5, $"only {applied} complete loads applied");
            Assert.True(incompleteThrows >= 7, $"only {incompleteThrows} incomplete loads seen");
            Assert.Equal(incompleteThrows, IdentityAlerts());
            Assert.Equal(maxAppliedUsers, store.Count());
        }
        finally
        {
            Alerting.HttpClient = previousClient;
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// R2-6: watermark / lag boundary storm
// ─────────────────────────────────────────────────────────────────────────────

public class WatermarkLagEdgeStressTests
{
    private static string Dt(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static FakeBdhSource SourceWithDts(IEnumerable<DateOnly> dts)
    {
        var source = new FakeBdhSource();
        foreach (var d in dts)
            source.Add($"Contact/dt={Dt(d)}/p.jsonl", R2.Row($"C{d:yyyyMMdd}X001") + "\n");
        return source;
    }

    // R2-6a: (since time-of-day × lag hours) matrix, five partitions straddling
    // each boundary — inclusion/exclusion is exact in every combination,
    // including DST-like 23/25-hour lags and sub-second since offsets.
    [Fact]
    public async Task BoundaryMatrix_InclusionExact_AcrossLagAndTimeOfDay()
    {
        var baseDay = new DateTime(2026, 7, 17, 0, 0, 0, DateTimeKind.Utc);
        var sinceOffsets = new[]
        {
            TimeSpan.Zero,                          // midnight exactly
            TimeSpan.FromMilliseconds(1),           // just past midnight
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(12),
            new TimeSpan(0, 23, 59, 59, 999),       // end of day
        };
        var lags = new[] { 0, 1, 23, 24, 25, 48 };  // incl. DST-like 23/25
        var boundaryCases = 0;
        var partitionsEvaluated = 0;

        foreach (var offset in sinceOffsets)
        foreach (var lag in lags)
        {
            var since = baseDay + offset;
            // Independent model (DateTime arithmetic, not the code under test's
            // DateOnly path): a partition is included iff dt >= (since − lag).Date.
            var bound = DateOnly.FromDateTime(since.AddHours(-lag).Date);

            var dts = Enumerable.Range(-2, 5).Select(k => bound.AddDays(k)).ToList();
            var source = SourceWithDts(dts);
            var fetcher = new BdhFetcher(
                TestConfig.Make(lagHours: lag, allowFullScan: true), source, FilterSet.Empty);
            var result = await fetcher.FetchAsync(
                R2.Obj("Contact"), fullCrawl: false, sinceUtc: since);

            var includedDts = result.Records
                .Select(r => r.DataAsOf!)
                .ToHashSet(StringComparer.Ordinal);
            var expectedIncluded = dts.Where(d => d >= bound).Select(Dt)
                .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(expectedIncluded, includedDts);
            Assert.Equal(3, result.Stats.PartitionsScanned);   // bound, +1, +2
            Assert.Equal(2, result.Stats.PartitionsPruned);    // −1, −2
            boundaryCases++;
            partitionsEvaluated += dts.Count;
        }

        Assert.Equal(sinceOffsets.Length * lags.Length, boundaryCases);   // 30 combos
        Assert.Equal(150, partitionsEvaluated);
    }

    // Hand-computed literals pinning the exact boundary semantics.
    [Theory]
    [InlineData("2026-07-17T00:00:00.000", 24, "2026-07-16")]  // midnight − 24h → previous day
    [InlineData("2026-07-17T00:00:00.001", 24, "2026-07-16")]  // 1 ms past midnight — same bound
    [InlineData("2026-07-17T00:30:00.000", 25, "2026-07-15")]  // DST-like 25h crosses an extra day
    [InlineData("2026-07-17T00:30:00.000", 23, "2026-07-16")]  // DST-like 23h does not
    [InlineData("2026-07-17T23:59:59.999", 0, "2026-07-17")]   // zero lag, end of day
    [InlineData("2026-03-29T02:30:00.000", 24, "2026-03-28")]  // EU spring-forward day
    [InlineData("2026-10-25T02:30:00.000", 25, "2026-10-24")]  // EU fall-back day
    public async Task BoundaryLiterals_MinDtExactly(string sinceIso, int lag, string expectedMinDt)
    {
        var since = DateTime.Parse(
            sinceIso, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        var minDt = DateOnly.ParseExact(expectedMinDt, "yyyy-MM-dd", CultureInfo.InvariantCulture);

        Assert.Equal(minDt, BdhFetcher.MinDtFor(fullCrawl: false, since, lag));

        // And the scan behaves accordingly: exactly-at-bound included, the day
        // before pruned.
        var source = SourceWithDts(new[] { minDt.AddDays(-1), minDt });
        var fetcher = new BdhFetcher(
            TestConfig.Make(lagHours: lag, allowFullScan: true), source, FilterSet.Empty);
        var result = await fetcher.FetchAsync(R2.Obj("Contact"), fullCrawl: false, sinceUtc: since);
        Assert.Equal(new[] { Dt(minDt) }, result.Records.Select(r => r.DataAsOf).ToArray());
        Assert.Equal(1, result.Stats.PartitionsPruned);
    }

    // R2-6b: malformed dt values, non-partition directories and root-level
    // files are FAIL-SAFE INCLUDED (never silently watermark-pruned); missing
    // dt days simply do not appear; a full crawl prunes nothing.
    [Fact]
    public async Task MalformedAndMissingDtDirs_FailSafeIncluded_NeverWronglyPruned()
    {
        // since − 24h = 2026-07-15T00:00 → minDt 2026-07-15.
        var since = new DateTime(2026, 7, 16, 0, 0, 0, DateTimeKind.Utc);

        var source = new FakeBdhSource();
        source.Add("Contact/dt=2026-07-10/p.jsonl", R2.Row("COLD00000001") + "\n");  // pruned
        // dt=2026-07-14 deliberately MISSING (BDH skipped a nightly load).
        source.Add("Contact/dt=2026-07-15/p.jsonl", R2.Row("CAT000000001") + "\n");  // exactly at bound
        source.Add("Contact/dt=2026-07-16/p.jsonl", R2.Row("CNEW00000001") + "\n");
        source.Add("Contact/dt=2026-02-30/p.jsonl", R2.Row("CBAD00000001") + "\n");  // invalid date
        source.Add("Contact/dt=garbage/p.jsonl", R2.Row("CGARBAGE0001") + "\n");     // unparseable
        source.Add("Contact/extras/p.jsonl", R2.Row("CEXTRA000001") + "\n");         // non-partition dir
        source.Add("Contact/root-file.jsonl", R2.Row("CROOT0000001") + "\n");        // file at object root

        var fetcher = new BdhFetcher(
            TestConfig.Make(lagHours: 24, allowFullScan: true), source, FilterSet.Empty);
        var incremental = await fetcher.FetchAsync(
            R2.Obj("Contact"), fullCrawl: false, sinceUtc: since);

        var ids = incremental.Records.Select(r => r.ItemId).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "CAT000000001", "CNEW00000001", "CBAD00000001",
                "CGARBAGE0001", "CEXTRA000001", "CROOT0000001",
            },
            ids);
        Assert.DoesNotContain("COLD00000001", ids);            // the only pruned partition
        Assert.Equal(1, incremental.Stats.PartitionsPruned);

        // Full crawl: watermark off — everything included, nothing pruned.
        var full = await fetcher.FetchAsync(R2.Obj("Contact"), fullCrawl: true, sinceUtc: null);
        Assert.Equal(7, full.Records.Count);
        Assert.Equal(0, full.Stats.PartitionsPruned);
    }

    // R2-6c: a month of consecutive boundaries — since=day@06:00 with a 24h lag
    // must include exactly {day−1, day} and prune day−2, for every day: no
    // off-by-one anywhere in the walk.
    [Fact]
    public async Task ConsecutiveDailyBoundaries_NoOffByOne_AcrossAMonth()
    {
        var boundaries = 0;
        for (var day = 2; day <= 29; day++)
        {
            var since = new DateTime(2026, 6, day, 6, 0, 0, DateTimeKind.Utc);
            var bound = DateOnly.FromDateTime(since.AddHours(-24));   // = day−1
            var dts = new[] { bound.AddDays(-1), bound, bound.AddDays(1) };

            var source = SourceWithDts(dts);
            var fetcher = new BdhFetcher(
                TestConfig.Make(lagHours: 24, allowFullScan: true), source, FilterSet.Empty);
            var result = await fetcher.FetchAsync(
                R2.Obj("Contact"), fullCrawl: false, sinceUtc: since);

            Assert.Equal(
                new HashSet<string>(StringComparer.Ordinal) { Dt(bound), Dt(bound.AddDays(1)) },
                result.Records.Select(r => r.DataAsOf!).ToHashSet(StringComparer.Ordinal));
            Assert.Equal(1, result.Stats.PartitionsPruned);
            boundaries++;
        }
        Assert.Equal(28, boundaries);
    }

    // Watermark + dt partition filter compose: the filter prunes inside the
    // watermark window, and the watermark prunes below the filter's reach.
    [Fact]
    public async Task WatermarkAndDtFilter_Compose_BothPrune()
    {
        var now = new DateTime(2026, 7, 17, 0, 0, 0, DateTimeKind.Utc);
        var since = now;                                       // minDt = 07-16 (lag 24)
        var dts = Enumerable.Range(0, 10)
            .Select(k => new DateOnly(2026, 7, 8).AddDays(k)).ToList();  // 07-08..07-17
        var source = SourceWithDts(dts);

        // Filter: dt before 2026-07-17 → 07-17 pruned by the FILTER while
        // 07-08..07-15 are pruned by the WATERMARK; only 07-16 survives both.
        var filters = FilterSet.Parse(
            """{"objects":{"Contact":{"partition":[{"key":"dt","op":"before","value":"2026-07-17"}]}}}""");
        var fetcher = new BdhFetcher(TestConfig.Make(lagHours: 24), source, filters, () => now);
        var result = await fetcher.FetchAsync(R2.Obj("Contact"), fullCrawl: false, sinceUtc: since);

        Assert.Equal(new[] { "2026-07-16" }, result.Records.Select(r => r.DataAsOf).ToArray());
        Assert.Equal(9, result.Stats.PartitionsPruned);        // 8 watermark + 1 filter
        Assert.Equal(1, result.Stats.PartitionsScanned);
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Shared SQLite inventory — several node connections on ONE database file
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Coverage this connector previously lacked entirely: every other inventory test
/// drives ONE connection, and a single connection cannot contend with itself.
/// Two HA crawl nodes sharing a state database each hold their own connection,
/// and that is the only shape that produces "database is locked".
///
/// The gap mattered. This connector's whole Windows suite was green while the
/// same SQLite misconfiguration was failing the equivalent test in Clarizen,
/// Salesforce and Altrata — it simply had no test that contended.
/// </summary>
public class SharedSqliteInventoryStressTests
{
    [Fact]
    public void SharedSqliteInventory_MultiNodeWriters_NoLockErrors_NoLoss()
    {
        using var dir = new TempDir();
        var dbPath = Path.Combine(dir.Path, "shared_inventory.db");
        const string connector = "HaShared";
        const int nodeCount = 6;
        const int iterations = 60;
        const int batch = 20;

        long lockErrors = 0;

        Parallel.For(0, nodeCount, n =>
        {
            // Each node holds its OWN connection to the SAME file.
            using var inventory = new ItemInventory(connector, dbPath);
            for (var iter = 0; iter < iterations; iter++)
            {
                try
                {
                    inventory.RecordSeen(
                        Enumerable.Range(0, batch).Select(k => ($"n{n}_i{iter}_k{k}", "Task")),
                        DateTime.UtcNow);
                    if (iter % 5 == 4)
                    {
                        inventory.Remove(
                            Enumerable.Range(0, 10).Select(k => $"n{n}_i{iter - 4}_k{k}"));
                    }
                }
                catch (SqliteException exc) when (exc.SqliteErrorCode is 5 or 6)
                {
                    // 5 = SQLITE_BUSY, 6 = SQLITE_LOCKED.
                    Interlocked.Increment(ref lockErrors);
                }
            }
        });

        Assert.Equal(0, Interlocked.Read(ref lockErrors));

        // Exact bookkeeping: nothing lost, nothing phantom.
        const int removedPerNode = (iterations / 5) * 10;
        var expected = nodeCount * (iterations * batch - removedPerNode);
        using var check = new ItemInventory(connector, dbPath);
        Assert.Equal(expected, check.Count());
    }
}
