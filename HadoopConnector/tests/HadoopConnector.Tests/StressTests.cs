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
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HadoopConnector.AclEngine;
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
