// Scenarios.cs
// ------------
// The seven heavy-scale stress scenarios. Each drives REAL pipeline components
// (BdhFetcher / PartitionScanner / FilterEngine / IngestPipeline / GraphClient /
// CircuitBreaker / SyncState) with in-harness fakes at the same seams the unit
// suite uses, but at 10^5–10^6 volume, and measures throughput / pruning /
// working set / breaker transitions.

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

namespace StressHarness;

internal sealed class ScenarioResult
{
    public required string Name { get; init; }
    public List<string> Metrics { get; } = new();
    public List<string> Passed { get; } = new();
    public List<string> Failed { get; } = new();
    public double WallSecs { get; set; }
    public double PeakRssMb { get; set; }
    public bool Pass => Failed.Count == 0;

    public void Check(bool ok, string desc) => (ok ? Passed : Failed).Add(desc);
}

/// <summary>Samples process working set on a background loop; reports the peak.</summary>
internal sealed class MemorySampler : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _task;
    private long _peak;

    public MemorySampler()
    {
        var proc = Process.GetCurrentProcess();
        _task = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                proc.Refresh();
                var ws = proc.WorkingSet64;
                if (ws > Interlocked.Read(ref _peak))
                    Interlocked.Exchange(ref _peak, ws);
                try { await Task.Delay(15, _cts.Token); } catch (OperationCanceledException) { }
            }
        });
    }

    public double PeakMb => Interlocked.Read(ref _peak) / (1024.0 * 1024.0);

    public void Dispose()
    {
        _cts.Cancel();
        try { _task.Wait(500); } catch { /* best effort */ }
        _cts.Dispose();
    }
}

/// <summary>GraphClient stand-in that records PUTs and can fail-fast / fail items.</summary>
internal sealed class HarnessGraphClient : GraphClient
{
    public ConcurrentDictionary<string, byte> PutIds { get; } = new();
    public bool CircuitOpen { get; set; }
    public HashSet<string> FailingIds { get; } = new(StringComparer.Ordinal);
    private int _batchCalls;
    public int BatchCalls => Volatile.Read(ref _batchCalls);

    public HarnessGraphClient(AppConfig config) : base(config) => OverrideToken = "fake";

    public override Task<GraphResponse> SendWithRetryAsync(
        HttpMethod method, string path, JsonNode? body, CancellationToken ct = default)
    {
        if (CircuitOpen)
            return Task.FromResult(new GraphResponse
            { StatusCode = HttpStatusCode.ServiceUnavailable, CircuitOpen = true });

        if (path == "$batch" && body is JsonObject env && env["requests"] is JsonArray requests)
        {
            Interlocked.Increment(ref _batchCalls);
            var responses = new JsonArray();
            foreach (var r in requests)
            {
                var reqId = r!["id"]!.GetValue<string>();
                var itemId = r["body"] is JsonObject b
                    ? b["id"]!.GetValue<string>()
                    : r["url"]!.GetValue<string>() is var u ? u[(u.LastIndexOf('/') + 1)..] : "";
                var fail = FailingIds.Contains(itemId);
                if (!fail && r["body"] is not null) PutIds.TryAdd(itemId, 1);
                responses.Add(new JsonObject { ["id"] = reqId, ["status"] = fail ? 400 : 200 });
            }
            return Task.FromResult(new GraphResponse
            {
                StatusCode = HttpStatusCode.OK,
                Body = new JsonObject { ["responses"] = responses },
            });
        }
        if (method == HttpMethod.Put)
        {
            var itemId = path[(path.LastIndexOf('/') + 1)..];
            var fail = FailingIds.Contains(itemId);
            if (!fail) PutIds.TryAdd(itemId, 1);
            return Task.FromResult(new GraphResponse
            { StatusCode = fail ? HttpStatusCode.BadRequest : HttpStatusCode.OK });
        }
        return Task.FromResult(new GraphResponse { StatusCode = HttpStatusCode.OK });
    }
}

/// <summary>Real-network-shaped handler that throttles each distinct $batch once
/// with a 429 + Retry-After, then succeeds — for the real GraphClient retry ladder.</summary>
internal sealed class ThrottlingHandler : HttpMessageHandler
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    public ConcurrentDictionary<string, byte> ReceivedIds { get; } = new();
    public int Throttles { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var env = JsonNode.Parse(body)!.AsObject();
        var ids = env["requests"]!.AsArray()
            .Select(r => r!["body"] is JsonObject b ? b["id"]!.GetValue<string>() : "")
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
        var sig = string.Join(",", ids);

        bool throttle;
        lock (_lock) { throttle = _seen.Add(sig); if (throttle) Throttles++; }
        if (throttle)
        {
            var resp = new HttpResponseMessage((HttpStatusCode)429)
            { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            resp.Headers.TryAddWithoutValidation("Retry-After", "1");
            return resp;
        }
        var responses = new JsonArray();
        foreach (var r in env["requests"]!.AsArray())
        {
            if (r!["body"] is JsonObject b) ReceivedIds.TryAdd(b["id"]!.GetValue<string>(), 1);
            responses.Add(new JsonObject { ["id"] = r["id"]!.GetValue<string>(), ["status"] = 200 });
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                new JsonObject { ["responses"] = responses }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }
}

internal sealed class ScenarioRunner
{
    private readonly string _workdir;
    private readonly int _scale;

    public ScenarioRunner(string workdir, int scale)
    {
        _workdir = workdir;
        _scale = scale;
        SyncState.LogsDir = Path.Combine(workdir, "logs");
        Directory.CreateDirectory(SyncState.LogsDir);
    }

    private Func<string, IItemInventory> Inventory => id =>
        new ItemInventory(id, Path.Combine(_workdir, $"inv_{id}.db"));

    private IngestPipeline BuildPipeline(AppConfig cfg, SchemaConfig schema, IBdhSource src, GraphClient graph)
    {
        var fetcher = new BdhFetcher(cfg, src, FilterSet.Empty);
        var resolver = new AclResolver(
            new PrincipalMapper(new IdentityStore("s", Path.Combine(_workdir, "id.db"))),
            adminGroupId: string.Empty, fallbackGroupId: "grp-all");
        return new IngestPipeline(cfg, schema, fetcher, graph, resolver, new ItemConverter(cfg),
            ha: null, inventoryFactory: Inventory);
    }

    private static SchemaConfig OneObject(string name) => new()
    {
        ObjectList = new List<ObjectConfig>
        { new() { ObjectName = name, DisplayName = name, AclMode = "public" } },
    };

    // ── 1. Filter layer at scale ─────────────────────────────────────────────
    public async Task<ScenarioResult> FilterAtScaleAsync()
    {
        var res = new ScenarioResult { Name = "filter-scale" };
        var regions = new[] { "EU", "US", "APAC" };
        var dtCount = 200;
        var rowsPerFile = 3000 * _scale;            // scale multiplier
        var now = new DateTime(2026, 7, 17, 0, 0, 0, DateTimeKind.Utc);
        var dts = Enumerable.Range(0, dtCount)
            .Select(d => now.AddDays(-d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList();

        string Row(string region, string dt, int i) =>
            $$"""{"Id":"C{{region[0]}}{{Math.Abs(dt.GetHashCode()) % 100000:D5}}{{i:D9}}","Status":"{{(i % 20 == 0 ? "Active" : "Inactive")}}","Region":"{{region}}"}""";

        var source = new SyntheticBdhSource("Contact", regions, dts, rowsPerFile, Row);

        // region=EU prunes US+APAC subtrees (zero opens); Status=Active is selective.
        var filter = new ObjectFilter
        {
            Partition = { new FilterPredicate { Field = "region", Op = FilterOp.Equals, Value = "EU" } },
            AnyOf = { new FilterGroup { AllOf = { new FilterPredicate { Field = "Status", Op = FilterOp.Equals, Value = "Active" } } } },
        };
        var filters = new FilterSet(
            new Dictionary<string, ObjectFilter>(StringComparer.OrdinalIgnoreCase) { ["Contact"] = filter },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var cfg = HarnessConfig.Make(maxRecordsPerObject: 0, lagHours: 0);
        var fetcher = new BdhFetcher(cfg, source, filters, nowUtc: () => now);
        var obj = new ObjectConfig { ObjectName = "Contact", DisplayName = "Contact" };

        // Peak-RSS sampling so the CI perf-smoke job can assert a working-set
        // bound from this scenario's summary line (see .github/workflows/ci.yml).
        using var mem = new MemorySampler();
        var sw = Stopwatch.StartNew();
        var result = await fetcher.FetchAsync(obj, fullCrawl: true, sinceUtc: null);
        sw.Stop();
        res.WallSecs = sw.Elapsed.TotalSeconds;
        res.PeakRssMb = mem.PeakMb;

        var s = result.Stats;
        var scannedRegions = 1;                    // only EU survives the partition filter
        var expectedScanned = scannedRegions * dtCount * rowsPerFile;
        var expectedOpens = scannedRegions * dtCount;
        var rowsPerSec = s.RecordsScanned / Math.Max(0.001, res.WallSecs);

        res.Metrics.Add($"corpus rows            : {source.TotalRows:N0} ({regions.Length} regions × {dtCount} dt × {rowsPerFile:N0})");
        res.Metrics.Add($"partitions scanned     : {s.PartitionsScanned:N0}");
        res.Metrics.Add($"partitions pruned      : {s.PartitionsPruned:N0}");
        res.Metrics.Add($"file opens             : {source.OpenCalls:N0} (US/APAC + pruned = 0 opens)");
        res.Metrics.Add($"records scanned        : {s.RecordsScanned:N0}");
        res.Metrics.Add($"records matched        : {s.RecordsMatched:N0}");
        res.Metrics.Add($"throughput             : {rowsPerSec:N0} rows/s");
        res.Metrics.Add($"selectivity            : matched {100.0 * s.RecordsMatched / source.TotalRows:F2}% of corpus, "
                        + $"scanned {100.0 * s.RecordsScanned / source.TotalRows:F1}% of corpus");

        res.Check(source.OpenCalls == expectedOpens, $"zero opens on pruned dirs (opened {source.OpenCalls} == {expectedOpens} surviving)");
        res.Check(s.PartitionsPruned == (regions.Length - 1) * 1, $"US+APAC subtrees pruned once each ({s.PartitionsPruned})");
        res.Check(s.RecordsScanned == expectedScanned, $"records_scanned == surviving rows ({s.RecordsScanned:N0})");
        res.Check(s.RecordsMatched == expectedScanned / 20, $"selective filter matched 1/20 ({s.RecordsMatched:N0})");
        res.Check(s.RecordsScanned < source.TotalRows, "pruning read fewer rows than the corpus");
        return res;
    }

    // ── 2. Memory bounds ─────────────────────────────────────────────────────
    public async Task<ScenarioResult> MemoryBoundsAsync()
    {
        var res = new ScenarioResult { Name = "memory-bounds" };
        var rows = 2_000_000 * _scale;
        var source = new SyntheticBdhSource(
            "Big", new[] { "EU" }, new List<string> { "2026-07-17" }, rows,
            (r, dt, i) => $$"""{"Id":"B{{i:D12}}","Status":"{{(i % 1000 == 0 ? "Active" : "Inactive")}}"}""");

        var filter = new ObjectFilter
        { AnyOf = { new FilterGroup { AllOf = { new FilterPredicate { Field = "Status", Op = FilterOp.Equals, Value = "Active" } } } } };
        var filters = new FilterSet(
            new Dictionary<string, ObjectFilter>(StringComparer.OrdinalIgnoreCase) { ["Big"] = filter },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var cfg = HarnessConfig.Make(maxRecordsPerObject: 0);
        var fetcher = new BdhFetcher(cfg, source, filters);

        using var mem = new MemorySampler();
        var baseline = Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0);
        var sw = Stopwatch.StartNew();
        var result = await fetcher.FetchAsync(
            new ObjectConfig { ObjectName = "Big", DisplayName = "Big" }, fullCrawl: true, sinceUtc: null);
        sw.Stop();
        res.WallSecs = sw.Elapsed.TotalSeconds;
        res.PeakRssMb = mem.PeakMb;

        var held = result.Records.Count;
        res.Metrics.Add($"rows streamed          : {result.Stats.RecordsScanned:N0}");
        res.Metrics.Add($"rows matched/held      : {held:N0}");
        res.Metrics.Add($"working set held ratio : {(double)held / result.Stats.RecordsScanned:P3} of streamed rows");
        res.Metrics.Add($"peak working set       : {res.PeakRssMb:N0} MB (baseline ~{baseline:N0} MB)");
        res.Metrics.Add($"file opens             : {source.OpenCalls}");

        res.Check(result.Stats.RecordsScanned == rows, $"streamed all {rows:N0} rows");
        res.Check(held == rows / 1000, $"held only matched {held:N0} (bounded working set)");
        res.Check(held * 100 < result.Stats.RecordsScanned, "working set ≪ rows scanned (not materialized)");
        res.Check(res.PeakRssMb < 1500, $"peak working set bounded ({res.PeakRssMb:N0} MB, no OOM-style growth)");
        return res;
    }

    // ── 3. Fail-closed guard under load ──────────────────────────────────────
    public async Task<ScenarioResult> FailClosedAsync()
    {
        var res = new ScenarioResult { Name = "fail-closed" };
        var objs = new[] { "Contact", "Account", "Lead", "Case", "Opportunity", "Campaign", "Order", "Asset" };
        var source = new SyntheticBdhSource(
            "Contact", new[] { "EU" }, new List<string> { "2026-07-17" }, 100,
            (r, dt, i) => $$"""{"Id":"X{{i:D12}}"}""");
        var cfg = HarnessConfig.Make(allowFullScan: false);
        var fetcher = new BdhFetcher(cfg, source, FilterSet.Empty);

        var refused = 0;
        var sw = Stopwatch.StartNew();
        foreach (var o in objs)
        {
            try { await fetcher.FetchAsync(new ObjectConfig { ObjectName = o, DisplayName = o }, true, null); }
            catch (FullScanRefusedException) { refused++; }
        }
        sw.Stop();
        res.WallSecs = sw.Elapsed.TotalSeconds;
        res.Metrics.Add($"objects tested         : {objs.Length}");
        res.Metrics.Add($"unfiltered refused     : {refused}");
        res.Metrics.Add($"file opens             : {source.OpenCalls} (guard fires before any I/O)");
        res.Check(refused == objs.Length, "every unfiltered object refused (no 150M scan)");
        res.Check(source.OpenCalls == 0 && source.ListCalls == 0, "zero I/O — guard fired before scanning");
        return res;
    }

    // ── 4. Dead-letter concurrency ───────────────────────────────────────────
    public async Task<ScenarioResult> DeadLetterConcurrencyAsync()
    {
        var res = new ScenarioResult { Name = "dead-letter" };
        const string connector = "StressDeadLetter";
        SyncState.ClearFailedRecords(connector);
        var workers = 128;
        var perWorker = 1000 * _scale;

        var sw = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, workers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < perWorker; i++)
                SyncState.AppendFailedRecords(connector,
                    new List<(string, string)> { ($"W{w:D3}_I{i:D7}", $"boom {w}/{i}") }, "Contact");
        })));
        sw.Stop();
        res.WallSecs = sw.Elapsed.TotalSeconds;

        var expected = workers * perWorker;
        var lines = File.ReadAllLines(SyncState.FailedRecordsPath(connector)).Where(l => l.Trim().Length > 0).ToList();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var corrupt = 0;
        foreach (var l in lines)
        {
            try { ids.Add(JsonNode.Parse(l)!.AsObject()["item_id"]!.GetValue<string>()); }
            catch { corrupt++; }
        }
        res.Metrics.Add($"concurrent writers     : {workers}");
        res.Metrics.Add($"failures written       : {expected:N0}");
        res.Metrics.Add($"lines on disk          : {lines.Count:N0}");
        res.Metrics.Add($"distinct ids           : {ids.Count:N0}");
        res.Metrics.Add($"corrupt/torn lines     : {corrupt}");
        res.Metrics.Add($"write throughput       : {expected / Math.Max(0.001, res.WallSecs):N0} rows/s");
        res.Check(lines.Count == expected, $"no loss: {lines.Count:N0} == {expected:N0}");
        res.Check(corrupt == 0, "no torn/interleaved lines (all parse)");
        res.Check(ids.Count == expected, "no duplication: every id present exactly once");
        SyncState.ClearFailedRecords(connector);
        return res;
    }

    // ── 5. Circuit breaker under sustained failures ──────────────────────────
    public async Task<ScenarioResult> CircuitBreakerAsync()
    {
        var res = new ScenarioResult { Name = "circuit-breaker" };
        var clock = DateTime.UtcNow;
        var breaker = new CircuitBreaker("graph", new CircuitBreakerOptions
        {
            Enabled = true, FailureThreshold = 5, OpenDuration = TimeSpan.FromSeconds(30),
            Window = TimeSpan.FromSeconds(60), HalfOpenTrials = 2,
        }, () => clock);

        var serverFails = true;
        var tokenFails = false;
        var handler = new LambdaHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2", StringComparison.OrdinalIgnoreCase))
                return tokenFails
                    ? Json(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""")
                    : Json(HttpStatusCode.OK, """{"access_token":"tok","expires_in":3600}""");
            return serverFails ? Json(HttpStatusCode.InternalServerError, "boom") : Json(HttpStatusCode.OK, "{}");
        });
        var client = new GraphClient(HarnessConfig.Make(graphMaxRetries: 0), handler, breaker) { OverrideToken = "t" };
        client.DelayAsync = (_, _) => Task.CompletedTask;

        var sw = Stopwatch.StartNew();
        // Sustained failures under load → trip + fail fast.
        var failFast = 0;
        for (var i = 0; i < 500; i++)
        {
            var r = await client.GetAsync("connections");
            if (r.CircuitOpen) failFast++;
        }
        var trippedState = breaker.State;

        // Recover: advance past OpenDuration, heal, two probes close it.
        clock = clock.AddSeconds(31);
        serverFails = false;
        await client.GetAsync("connections");
        await client.GetAsync("connections");
        var recovered = breaker.State;

        // Wedge regression: HalfOpen probes whose TOKEN fetch throws must release
        // their slot (real token flow → 401). Re-arm HalfOpen first.
        var clock2 = DateTime.UtcNow;
        var breaker2 = new CircuitBreaker("graph2", new CircuitBreakerOptions
        {
            Enabled = true, FailureThreshold = 2, OpenDuration = TimeSpan.FromSeconds(30),
            Window = TimeSpan.FromSeconds(60), HalfOpenTrials = 2,
        }, () => clock2);
        breaker2.TripForTests();
        clock2 = clock2.AddSeconds(31);
        tokenFails = true;
        var client2 = new GraphClient(HarnessConfig.Make(graphMaxRetries: 0), handler, breaker2);
        client2.DelayAsync = (_, _) => Task.CompletedTask;
        var threwEachTime = true;
        for (var i = 0; i < 8; i++)
        {
            try { await client2.GetAsync("connections"); threwEachTime = false; }
            catch (InvalidOperationException) { /* expected token failure */ }
        }
        var stillHalfOpen = breaker2.State == CircuitState.HalfOpen;
        tokenFails = false;
        await client2.GetAsync("connections");
        await client2.GetAsync("connections");
        var wedgeRecovered = breaker2.State == CircuitState.Closed;
        sw.Stop();
        res.WallSecs = sw.Elapsed.TotalSeconds;

        res.Metrics.Add($"calls under outage     : 500");
        res.Metrics.Add($"failed-fast (no net)   : {failFast:N0}");
        res.Metrics.Add($"breaker trips          : {breaker.Trips}");
        res.Metrics.Add($"state after outage     : {trippedState}");
        res.Metrics.Add($"state after recovery   : {recovered} (resets={breaker.Resets})");
        res.Metrics.Add($"token-throw probes      : 8 (all threw, slot released each)");
        res.Metrics.Add($"breaker2 after throws   : {breaker2.State} (not wedged)");

        res.Check(trippedState == CircuitState.Open, "sustained 5xx tripped the breaker");
        res.Check(failFast > 450, $"open breaker fails fast without network ({failFast}/500)");
        res.Check(recovered == CircuitState.Closed, "half-open probes recovered the breaker");
        res.Check(threwEachTime, "every token-throw probe threw (never returned wedged CircuitOpen)");
        res.Check(stillHalfOpen, "breaker stayed HalfOpen through token throws (slot released, not leaked)");
        res.Check(wedgeRecovered, "healthy probes still admitted → breaker closed (no wedge)");
        return res;
    }

    // ── 6. Checkpoint/resume under interruption ──────────────────────────────
    public async Task<ScenarioResult> CheckpointResumeAsync()
    {
        var res = new ScenarioResult { Name = "checkpoint-resume" };
        const string connector = "StressCheckpoint";
        var total = 20_000 * _scale;
        var source = new SyntheticBdhSource(
            "Contact", new[] { "EU" }, new List<string> { "2026-07-15" }, total,
            (r, dt, i) => $$"""{"Id":"C{{i + 1:D12}}","Status":"Active"}""");
        var cfg = HarnessConfig.Make(connectorId: connector, ingestChunkSize: 500, allowFullScan: true);
        var schema = OneObject("Contact");

        SyncState.ClearCheckpoint(connector);
        SyncState.ClearFailedRecords(connector);
        var invPath = Path.Combine(_workdir, $"inv_{connector}.db");
        if (File.Exists(invPath)) File.Delete(invPath);

        var sw = Stopwatch.StartNew();
        ServiceStop.Reset();
        var graph1 = new HarnessGraphClient(cfg);
        var p1 = BuildPipeline(cfg, schema, source, graph1);
        var half = total / 2;
        p1.OnProgress = (_, done, _) => { if (done >= half) ServiceStop.Request(); };
        var run1 = await p1.RunAsync(fullCrawl: true);

        var checkpoint = SyncState.ReadCheckpoint(connector);
        ServiceStop.Reset();

        var graph2 = new HarnessGraphClient(cfg);
        var p2 = BuildPipeline(cfg, schema, source, graph2);
        var run2 = await p2.RunAsync(fullCrawl: true);
        sw.Stop();
        res.WallSecs = sw.Elapsed.TotalSeconds;

        var ids1 = graph1.PutIds.Keys.ToHashSet(StringComparer.Ordinal);
        var ids2 = graph2.PutIds.Keys.ToHashSet(StringComparer.Ordinal);
        var overlap = ids1.Intersect(ids2, StringComparer.Ordinal).Count();
        using var inv = Inventory(connector);
        var finalCount = inv.IdsForObject("Contact").Count;

        res.Metrics.Add($"total records          : {total:N0}");
        res.Metrics.Add($"run1 stopped           : {run1.Stopped} (checkpoint chunk {checkpoint?["completed"]?["Contact"]?.GetValue<int>()})");
        res.Metrics.Add($"run1 ingested          : {ids1.Count:N0}");
        res.Metrics.Add($"run2 resumed skipped   : {run2.SkippedChunks} chunk(s)");
        res.Metrics.Add($"run2 ingested          : {ids2.Count:N0}");
        res.Metrics.Add($"re-sent (overlap)      : {overlap}");
        res.Metrics.Add($"final inventory        : {finalCount:N0}");

        res.Check(run1.Stopped, "run1 interrupted at a chunk boundary");
        res.Check(run2.SkippedChunks >= 1, "run2 skipped completed chunks (resume)");
        res.Check(overlap == 0, "no duplication: resumed run re-sent nothing");
        res.Check(ids1.Count + ids2.Count == total, "no loss: union covers every id once");
        res.Check(finalCount == total, $"final inventory == all {total:N0} ids");
        SyncState.ClearCheckpoint(connector);
        return res;
    }

    // ── 7. $batch throughput with induced 429s ───────────────────────────────
    public async Task<ScenarioResult> BatchThroughputAsync()
    {
        var res = new ScenarioResult { Name = "batch-429" };
        const string connector = "StressBatch";
        var total = 20_000 * _scale;
        var source = new SyntheticBdhSource(
            "Contact", new[] { "EU" }, new List<string> { "2026-07-15" }, total,
            (r, dt, i) => $$"""{"Id":"C{{i + 1:D12}}","Status":"Active"}""");
        var cfg = HarnessConfig.Make(connectorId: connector, ingestChunkSize: 500, graphBatchSize: 20,
            graphBatchWorkers: 8, graphMaxRetries: 4, backoffBase: 1.0, allowFullScan: true);
        var schema = OneObject("Contact");

        var invPath = Path.Combine(_workdir, $"inv_{connector}.db");
        if (File.Exists(invPath)) File.Delete(invPath);
        SyncState.ClearCheckpoint(connector);

        var handler = new ThrottlingHandler();
        var delays = new ConcurrentBag<double>();
        var graph = new GraphClient(cfg, handler) { OverrideToken = "t" };
        graph.DelayAsync = (d, _) => { delays.Add(d.TotalSeconds); return Task.CompletedTask; };
        var pipeline = BuildPipeline(cfg, schema, source, graph);

        var sw = Stopwatch.StartNew();
        var summary = await pipeline.RunAsync(fullCrawl: true);
        sw.Stop();
        res.WallSecs = sw.Elapsed.TotalSeconds;

        var honored = delays.Count(d => Math.Abs(d - 1.0) < 0.01);
        res.Metrics.Add($"items ingested         : {summary.Ingested:N0}");
        res.Metrics.Add($"items failed           : {summary.Failed:N0}");
        res.Metrics.Add($"distinct ids at Graph  : {handler.ReceivedIds.Count:N0}");
        res.Metrics.Add($"$batch 429s injected   : {handler.Throttles:N0}");
        res.Metrics.Add($"Retry-After (1s) honored: {honored:N0} waits");
        res.Metrics.Add($"ingest throughput      : {summary.Ingested / Math.Max(0.001, res.WallSecs):N0} items/s");

        res.Check(summary.Ingested == total, $"correct final count ({summary.Ingested:N0} == {total:N0})");
        res.Check(summary.Failed == 0, "no lost items");
        res.Check(handler.ReceivedIds.Count == total, "every id reached Graph");
        res.Check(honored > 0, "server Retry-After honored on retries");
        SyncState.ClearCheckpoint(connector);
        return res;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // ROUND 2 — post-fix scenarios
    // ═════════════════════════════════════════════════════════════════════════

    // ── 8. Oversize/Incomplete at scale: sweep suppressed EVERY time ─────────
    public async Task<ScenarioResult> OversizeSweepAsync()
    {
        var res = new ScenarioResult { Name = "oversize-sweep" };
        var rng = new Random(90210);
        const int crawls = 12;
        var rowsPerFile = 400 * _scale;
        const long maxFileBytes = 512L * 1024;
        var schema = OneObject("Contact");

        long rowsIngested = 0;
        var filesSkippedTotal = 0;
        var sweepsSuppressed = 0;
        var sweepsRun = 0;
        var falseDeletions = 0;
        var staleRetainedWhenSuppressed = 0;
        var sw = Stopwatch.StartNew();

        for (var c = 0; c < crawls; c++)
        {
            // One connector (→ one inventory DB) per crawl: SQLite pools
            // connections per file, so deleting/recreating a single shared DB
            // mid-process leaves stale pooled handles behind.
            var connector = $"StressOversize{c:D2}";
            var cfg = HarnessConfig.Make(connectorId: connector, ingestChunkSize: 1000,
                maxRecordsPerObject: 0, maxFileBytes: maxFileBytes, allowFullScan: true);
            SyncState.ClearCheckpoint(connector);
            SyncState.ClearFailedRecords(connector);
            var invPath = Path.Combine(_workdir, $"inv_{connector}.db");
            if (File.Exists(invPath)) File.Delete(invPath);   // leftover from a previous run

            var source = new ScatterBdhSource();
            var hidden = new List<string>();
            var visibleCount = 0;
            var oversizeFiles = 0;
            var totalFiles = 0;
            int? force = c == 0 ? 0 : c == 1 ? 2 : null;   // clean + dirty controls
            for (var p = 0; p < 6; p++)
            {
                var dt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
                    .AddDays(p).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var files = 2 + rng.Next(2);               // 2..3 files per partition
                for (var f = 0; f < files; f++)
                {
                    totalFiles++;
                    var oversize = force is { } n ? oversizeFiles < n : rng.Next(4) == 0;
                    int cc = c, pp = p, ff = f;
                    if (oversize)
                    {
                        oversizeFiles++;
                        for (var r = 0; r < 50; r++)
                            hidden.Add($"H{cc:D2}{pp:D2}{ff:D2}{r:D5}");
                        // Reported length above the bound → skipped, rows never read.
                        source.AddFile($"Contact/dt={dt}/part-{f:D4}.jsonl", 50, maxFileBytes + 1024,
                            r => $$"""{"Id":"H{{cc:D2}}{{pp:D2}}{{ff:D2}}{{r:D5}}","Status":"Active"}""");
                    }
                    else
                    {
                        visibleCount += rowsPerFile;
                        source.AddFile($"Contact/dt={dt}/part-{f:D4}.jsonl", rowsPerFile, 1024,
                            r => $$"""{"Id":"V{{cc:D2}}{{pp:D2}}{{ff:D2}}{{r:D7}}","Status":"Active"}""");
                    }
                }
            }

            // Seed: every hidden id (live in BDH, un-read this crawl) + 1 stale.
            const string staleId = "STALE00000001";
            using (var seed = Inventory(connector))
            {
                seed.RecordSeen(
                    hidden.Select(h => (h, "Contact")).Append((staleId, "Contact")), DateTime.UtcNow);
            }

            var graph = new HarnessGraphClient(cfg);
            var pipeline = BuildPipeline(cfg, schema, source, graph);
            var summary = await pipeline.RunAsync(fullCrawl: true);
            rowsIngested += summary.Ingested;
            filesSkippedTotal += oversizeFiles;

            using var check = Inventory(connector);
            var after = check.IdsForObject("Contact").ToHashSet(StringComparer.Ordinal);
            falseDeletions += hidden.Count(h => !after.Contains(h));

            if (summary.Ingested != visibleCount)
                res.Failed.Add($"crawl {c}: ingested {summary.Ingested} != visible {visibleCount}");
            if (oversizeFiles > 0)
            {
                sweepsSuppressed++;
                if (summary.Deleted != 0 || !summary.SweepSkipped.Contains("Contact")
                    || !summary.PartialObjects.Contains("Contact"))
                {
                    res.Failed.Add($"crawl {c}: {oversizeFiles} skip(s) but sweep NOT suppressed "
                                   + $"(deleted={summary.Deleted})");
                }
                if (after.Contains(staleId)) staleRetainedWhenSuppressed++;
                else res.Failed.Add($"crawl {c}: suppressed sweep still deleted the stale id");
            }
            else
            {
                sweepsRun++;
                if (summary.Deleted != 1 || after.Contains(staleId))
                    res.Failed.Add($"crawl {c}: clean sweep did not delete exactly the stale id");
            }
        }
        sw.Stop();
        res.WallSecs = sw.Elapsed.TotalSeconds;

        res.Metrics.Add($"crawls run             : {crawls} ({sweepsSuppressed} with skips, {sweepsRun} clean)");
        res.Metrics.Add($"oversize files skipped : {filesSkippedTotal}");
        res.Metrics.Add($"items ingested         : {rowsIngested:N0}");
        res.Metrics.Add($"false deletions        : {falseDeletions} (un-read live records deleted)");
        res.Metrics.Add($"ingest throughput      : {rowsIngested / Math.Max(0.001, res.WallSecs):N0} items/s");
        res.Check(falseDeletions == 0, "0 false deletions across all crawls");
        res.Check(sweepsSuppressed >= 3, $"sweep suppressed on EVERY skip crawl ({sweepsSuppressed})");
        res.Check(sweepsRun >= 1, $"clean crawls still sweep correctly ({sweepsRun})");
        res.Check(staleRetainedWhenSuppressed == sweepsSuppressed,
            "suppression is all-or-nothing (even genuine stale retained)");
        return res;
    }

    // ── 9. Guard-bypass fuzz matrix ──────────────────────────────────────────
    public async Task<ScenarioResult> GuardMatrixAsync()
    {
        var res = new ScenarioResult { Name = "guard-matrix" };
        var now = new DateTime(2026, 7, 17, 0, 0, 0, DateTimeKind.Utc);
        var cases = new (string Name, string? Json, bool Effective)[]
        {
            ("none", null, false),
            ("emptyFilter", """{"objects":{"Contact":{}}}""", false),
            ("recordOnly", """{"objects":{"Contact":{"allOf":[{"field":"Status","op":"equals","value":"Active"}]}}}""", true),
            ("dtWithinLastDays", """{"objects":{"Contact":{"partition":[{"key":"dt","op":"withinLastDays","value":"30"}]}}}""", true),
            ("dtEquals", """{"objects":{"Contact":{"partition":[{"key":"dt","op":"equals","value":"2026-07-15"}]}}}""", true),
            ("dtBefore", """{"objects":{"Contact":{"partition":[{"key":"dt","op":"before","value":"2026-07-16"}]}}}""", true),
            ("nonDtOnly", """{"objects":{"Contact":{"partition":[{"key":"region","op":"equals","value":"EU"}]}}}""", false),
            ("dtPlusNonDt", """{"objects":{"Contact":{"partition":[{"key":"region","op":"equals","value":"EU"},{"key":"dt","op":"withinLastDays","value":"30"}]}}}""", true),
            ("recordPlusNonDt", """{"objects":{"Contact":{"partition":[{"key":"region","op":"equals","value":"EU"}],"allOf":[{"field":"Status","op":"equals","value":"Active"}]}}}""", true),
            ("dtIsNotNull", """{"objects":{"Contact":{"partition":[{"key":"dt","op":"isNotNull"}]}}}""", false),
            ("dtIsNull", """{"objects":{"Contact":{"partition":[{"key":"dt","op":"isNull"}]}}}""", true),
        };

        var combos = 0;
        var refusals = 0;
        var allowed = 0;
        var bypasses = 0;
        var falseRefusals = 0;
        var ioOnRefusal = 0;
        var strictDisagreements = 0;
        var sw = Stopwatch.StartNew();

        foreach (var fc in cases)
        foreach (var listed in new[] { false, true })
        foreach (var allowEnv in new[] { false, true })
        foreach (var entry in new[] { "fetch-full", "fetch-incr", "find-by-id" })
        {
            combos++;
            var expectAllowed = fc.Effective || listed || allowEnv;

            var source = new ScatterBdhSource().AddFile(
                "Contact/dt=2026-07-15/p.jsonl", 10, 1024,
                r => $$"""{"Id":"C{{r:D12}}","Status":"Active"}""");
            var baseSet = fc.Json is null ? FilterSet.Empty : FilterSet.Parse(fc.Json);
            var filters = listed
                ? new FilterSet(
                    baseSet.Objects.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Contact" })
                : baseSet;
            var cfg = HarnessConfig.Make(allowFullScan: allowEnv, lagHours: 24);
            var fetcher = new BdhFetcher(cfg, source, filters, () => now);
            var obj = new ObjectConfig { ObjectName = "Contact", DisplayName = "Contact" };

            var refused = false;
            try
            {
                switch (entry)
                {
                    case "fetch-full": await fetcher.FetchAsync(obj, true, null); break;
                    case "fetch-incr": await fetcher.FetchAsync(obj, false, now.AddDays(-5)); break;
                    default: await fetcher.FindByIdAsync(obj, "C000000000001"); break;
                }
                allowed++;
            }
            catch (FullScanRefusedException)
            {
                refused = true;
                refusals++;
            }

            if (!refused && !expectAllowed)
            {
                bypasses++;
                res.Failed.Add($"BYPASS: {fc.Name} listed={listed} env={allowEnv} {entry}");
            }
            if (refused && expectAllowed)
            {
                falseRefusals++;
                res.Failed.Add($"FALSE REFUSAL: {fc.Name} listed={listed} env={allowEnv} {entry}");
            }
            if (refused && (source.ListCalls != 0 || source.OpenCalls != 0))
            {
                ioOnRefusal++;
                res.Failed.Add($"I/O ON REFUSAL: {fc.Name} listed={listed} env={allowEnv} {entry}");
            }
            if (fetcher.UnfilteredObjects(new[] { obj }).Contains("Contact") != (!fc.Effective && !listed))
            {
                strictDisagreements++;
                res.Failed.Add($"STRICT DISAGREES: {fc.Name} listed={listed}");
            }
        }
        sw.Stop();
        res.WallSecs = sw.Elapsed.TotalSeconds;

        res.Metrics.Add($"combinations tested    : {combos} ({cases.Length} filter kinds × listed × env × 3 entry points)");
        res.Metrics.Add($"refused (fail-closed)  : {refusals}");
        res.Metrics.Add($"allowed                : {allowed}");
        res.Metrics.Add($"guard bypasses         : {bypasses}");
        res.Metrics.Add($"false refusals         : {falseRefusals}");
        res.Metrics.Add($"I/O before refusal     : {ioOnRefusal}");
        res.Metrics.Add($"strict-mode mismatches : {strictDisagreements}");
        res.Check(combos == cases.Length * 12, "full matrix enumerated");
        res.Check(bypasses == 0, "0 bypasses — guard fails closed in every combination");
        res.Check(falseRefusals == 0, "0 false refusals — every legitimate config admitted");
        res.Check(ioOnRefusal == 0, "every refusal fired before any source I/O");
        res.Check(strictDisagreements == 0, "validate-config --strict agrees with the runtime guard");
        return res;
    }

    // ── 10. OpenAsync retry ladder under flapping datanodes ──────────────────
    public async Task<ScenarioResult> OpenRetryAsync()
    {
        var res = new ScenarioResult { Name = "open-retry" };
        const string namenode = "http://nn.example:9870/webhdfs/v1";
        var files = 300 * _scale;

        // Phase A — flapping waves: every open fails 1..3 times (503 bare /
        // 429+Retry-After:3 / 503+Retry-After:7200→clamp 60 / 503+Retry-After:1)
        // then succeeds. Concurrent; instant delay seam.
        int FailuresFor(int i) => 1 + (i % 3);
        var handlerA = new FlappingHandler((path, attempt) =>
        {
            var name = path[(path.LastIndexOf('/') + 1)..];
            var i = int.Parse(name["part-".Length..name.IndexOf('.')], CultureInfo.InvariantCulture);
            if (attempt < FailuresFor(i))
            {
                return (i % 4) switch
                {
                    0 => Json(HttpStatusCode.ServiceUnavailable, "boom"),
                    1 => WithRetryAfter(Json((HttpStatusCode)429, "slow"), "3"),
                    2 => WithRetryAfter(Json(HttpStatusCode.ServiceUnavailable, "boom"), "7200"),
                    _ => WithRetryAfter(Json(HttpStatusCode.ServiceUnavailable, "boom"), "1"),
                };
            }
            return Json(HttpStatusCode.OK, $"ok-{i}");
        });
        var clockA = DateTime.UtcNow;
        var breakerA = new CircuitBreaker("hdfs", new CircuitBreakerOptions
        {
            Enabled = true, FailureThreshold = 5, OpenDuration = TimeSpan.FromSeconds(30),
            Window = TimeSpan.FromSeconds(600), HalfOpenTrials = 2,
        }, () => clockA);
        var clientA = new WebHdfsClient(namenode, "/data/bdh", "svc", null, handlerA, breakerA);
        var delays = new ConcurrentBag<double>();
        clientA.DelayAsync = (d, _) => { delays.Add(d.TotalSeconds); return Task.CompletedTask; };

        var swA = Stopwatch.StartNew();
        var wrongBodies = 0;
        await Task.WhenAll(Enumerable.Range(0, files).Select(async i =>
        {
            await using var s = await clientA.OpenAsync($"Contact/dt=2026-07-15/part-{i}.jsonl");
            using var r = new StreamReader(s);
            if (await r.ReadToEndAsync() != $"ok-{i}") Interlocked.Increment(ref wrongBodies);
        }));
        swA.Stop();

        var expectedRetries = Enumerable.Range(0, files).Sum(FailuresFor);
        int WaveRetries(int w) => Enumerable.Range(0, files).Where(i => i % 4 == w).Sum(FailuresFor);
        var clamped = delays.Count(d => Math.Abs(d - 60.0) < 0.001);
        var honored3 = delays.Count(d => Math.Abs(d - 3.0) < 0.001);
        var honored1 = delays.Count(d => Math.Abs(d - 1.0) < 0.001);
        var backoff = delays.Count(d => d is 2.0 or 4.0 or 8.0);

        // Phase B — terminal failures trip at threshold, open fails fast with
        // zero network, HalfOpen terminal-429 probes release slots, recovery.
        var healed = false;
        var handlerB = new FlappingHandler((path, _) =>
            path.Contains("/bad/", StringComparison.Ordinal)
                ? Json(HttpStatusCode.ServiceUnavailable, "boom")
                : path.Contains("/probe429/", StringComparison.Ordinal) && !healed
                    ? Json((HttpStatusCode)429, "slow")
                    : Json(HttpStatusCode.OK, "ok"));
        var clockB = DateTime.UtcNow;
        var breakerB = new CircuitBreaker("hdfs2", new CircuitBreakerOptions
        {
            Enabled = true, FailureThreshold = 5, OpenDuration = TimeSpan.FromSeconds(30),
            Window = TimeSpan.FromSeconds(600), HalfOpenTrials = 2,
        }, () => clockB);
        var clientB = new WebHdfsClient(namenode, "/data/bdh", "svc", null, handlerB, breakerB);
        clientB.DelayAsync = (_, _) => Task.CompletedTask;

        var terminalFailures = 0;
        for (var i = 0; i < 5; i++)
        {
            try { await clientB.OpenAsync($"bad/part-{i}.jsonl"); }
            catch (HdfsException) { terminalFailures++; }
        }
        var trippedAtThreshold = breakerB.State == CircuitState.Open && breakerB.Trips == 1;
        var reqsAfterTrip = handlerB.Requests;

        var failFast = 0;
        await Task.WhenAll(Enumerable.Range(0, 100).Select(async i =>
        {
            try { await clientB.OpenAsync($"good/ff-{i}.jsonl"); }
            catch (CircuitOpenException) { Interlocked.Increment(ref failFast); }
        }));
        var zeroNetworkWhileOpen = handlerB.Requests == reqsAfterTrip;

        clockB = clockB.AddSeconds(31);
        var probeSurvived = 0;
        for (var i = 0; i < 8; i++)
        {
            try { await clientB.OpenAsync($"probe429/part-{i}.jsonl"); }
            catch (HdfsException) { if (breakerB.State == CircuitState.HalfOpen) probeSurvived++; }
        }
        healed = true;
        (await clientB.OpenAsync("good/heal-0.jsonl")).Dispose();
        (await clientB.OpenAsync("good/heal-1.jsonl")).Dispose();
        var recovered = breakerB.State == CircuitState.Closed && breakerB.Resets == 1;

        var swC = Stopwatch.StartNew();
        var post = 200 * _scale;
        var postOk = 0;
        await Task.WhenAll(Enumerable.Range(0, post).Select(async i =>
        {
            await using var s = await clientB.OpenAsync($"good/final-{i}.jsonl");
            using var r = new StreamReader(s);
            if (await r.ReadToEndAsync() == "ok") Interlocked.Increment(ref postOk);
        }));
        swC.Stop();
        res.WallSecs = swA.Elapsed.TotalSeconds + swC.Elapsed.TotalSeconds;

        res.Metrics.Add($"flap phase             : {files:N0} concurrent opens, {expectedRetries:N0} induced failures");
        res.Metrics.Add($"HTTP calls (flap)      : {handlerA.Requests:N0} (== opens + retries: {files + expectedRetries:N0})");
        res.Metrics.Add($"Retry-After honored    : 3s×{honored3:N0} 1s×{honored1:N0} (waves {WaveRetries(1):N0}/{WaveRetries(3):N0})");
        res.Metrics.Add($"oversized RA clamped   : {clamped:N0} × 60s (wave {WaveRetries(2):N0})");
        res.Metrics.Add($"exp backoff waits      : {backoff:N0} (2/4/8s, wave {WaveRetries(0):N0})");
        res.Metrics.Add($"flap throughput        : {files / Math.Max(0.001, swA.Elapsed.TotalSeconds):N0} opens/s");
        res.Metrics.Add($"breaker after flap     : {breakerA.State}, trips={breakerA.Trips}");
        res.Metrics.Add($"terminal 503 opens     : {terminalFailures} → tripped at threshold: {trippedAtThreshold}");
        res.Metrics.Add($"fail-fast while open   : {failFast}/100 rejected, zero network: {zeroNetworkWhileOpen}");
        res.Metrics.Add($"HalfOpen 429 probes    : 8 (slot released every time: {probeSurvived == 8})");
        res.Metrics.Add($"recovered              : {recovered} (resets={breakerB.Resets})");
        res.Metrics.Add($"post-recovery          : {postOk:N0}/{post:N0} opens ok, {post / Math.Max(0.001, swC.Elapsed.TotalSeconds):N0} opens/s");

        res.Check(wrongBodies == 0, "every flapping open recovered with the right content");
        res.Check(breakerA.State == CircuitState.Closed && breakerA.Trips == 0,
            "no breaker miscount: recovered flaps never tripped it");
        res.Check(handlerA.Requests == files + expectedRetries, "exact retry volume (no slot/retry leak)");
        res.Check(delays.Count == expectedRetries && delays.All(d => d <= 60.0),
            "every wait ≤ 60s clamp");
        res.Check(honored3 == WaveRetries(1) && honored1 == WaveRetries(3), "Retry-After honored exactly");
        res.Check(clamped == WaveRetries(2), "oversized Retry-After clamped to 60s every time");
        res.Check(trippedAtThreshold, "terminal failures tripped exactly at threshold");
        res.Check(failFast == 100 && zeroNetworkWhileOpen, "open breaker fails fast with zero network");
        res.Check(probeSurvived == 8, "no HalfOpen slot leak under terminal-429 probes");
        res.Check(recovered, "healthy probes closed the breaker");
        res.Check(postOk == post, "full throughput after recovery");
        return res;
    }

    // ── 11. IdentitySync fail-loud churn ─────────────────────────────────────
    public async Task<ScenarioResult> IdentityChurnAsync()
    {
        var res = new ScenarioResult { Name = "identity-churn" };
        var completeUsers = Math.Min(1500, 300 * _scale);
        var cfg = HarnessConfig.Make(maxRecordsPerObject: 2000, maxFileBytes: 256L * 1024);

        var source = new MutableBdhSource();
        var fetcher = new BdhFetcher(cfg, source, FilterSet.Empty);
        var graphHandler = new LambdaHandler(_ => Json(HttpStatusCode.OK, """{"id":"aad-user-1"}"""));
        var graph = new GraphClient(cfg, graphHandler) { OverrideToken = "t" };
        graph.DelayAsync = (_, _) => Task.CompletedTask;
        var sync = new IdentitySync(fetcher, graph, cfg);
        var store = new RecordingIdentityStore();

        static string UserRow(int n) =>
            $$"""{"Id":"U{{n:D7}}","Email":"user{{n}}@contoso.com","Name":"User {{n}}","IsActive":"true"}""";

        void Install(string kind, int users)
        {
            var inner = new ScatterBdhSource();
            switch (kind)
            {
                case "complete":
                    inner.AddFile("User/dt=2026-07-15/part-0000.jsonl", users, 1024, r => UserRow(r + 1));
                    break;
                case "capped":   // 2500 rows > 2000-row cap
                    inner.AddFile("User/dt=2026-07-15/part-0000.jsonl", 2500, 1024, r => UserRow(r + 1));
                    break;
                default:         // oversize: readable file + skipped file
                    inner.AddFile("User/dt=2026-07-15/part-0000.jsonl", 40, 1024, r => UserRow(r + 1));
                    inner.AddFile("User/dt=2026-07-15/part-0001.jsonl", 40, 512L * 1024, r => UserRow(r + 5000));
                    break;
            }
            source.Inner = inner;
        }

        var alertHandler = new CapturingHandler();
        var previousUrl = Environment.GetEnvironmentVariable(Alerting.WebhookUrlEnvVar);
        var previousClient = Alerting.HttpClient;
        Environment.SetEnvironmentVariable(Alerting.WebhookUrlEnvVar, "https://hooks.example/alerts");
        Alerting.HttpClient = new HttpClient(alertHandler);
        try
        {
            var rng = new Random(4242);
            var schedule = new List<(string Kind, int Users)>
            {
                ("complete", completeUsers), ("capped", 0), ("complete", completeUsers),
                ("oversize", 0), ("complete", completeUsers),
            };
            for (var i = 0; i < 40; i++)
            {
                schedule.Add(rng.Next(3) switch
                {
                    0 => ("complete", 50 + rng.Next(completeUsers)),
                    1 => ("capped", 0),
                    _ => ("oversize", 0),
                });
            }

            int IdentityAlerts() =>
                alertHandler.Bodies.Count(b => b.Contains("\"identity_directory_incomplete\"", StringComparison.Ordinal));

            var applied = 0;
            var incompleteThrows = 0;
            var partialApplications = 0;
            var wrongAlertCounts = 0;
            var maxApplied = 0;
            long usersResolved = 0;
            var sw = Stopwatch.StartNew();

            foreach (var (kind, users) in schedule)
            {
                Install(kind, users);
                var countBefore = store.Count();
                var upsertsBefore = store.UpsertCalls;
                var alertsBefore = IdentityAlerts();
                try
                {
                    var directory = await sync.LoadDirectoryAsync();
                    var outcome = await sync.SyncAsync(directory, store, persist: true);
                    usersResolved += outcome.UsersResolved;
                    applied++;
                    maxApplied = Math.Max(maxApplied, users);
                    if (kind != "complete")
                        res.Failed.Add($"{kind} load did NOT throw — partial directory applied");
                    if (store.Count() != maxApplied)
                        res.Failed.Add($"store count {store.Count()} != expected {maxApplied}");
                }
                catch (InvalidDataException)
                {
                    incompleteThrows++;
                    if (kind == "complete")
                        res.Failed.Add("complete load threw unexpectedly");
                    if (store.Count() != countBefore || store.UpsertCalls != upsertsBefore)
                    {
                        partialApplications++;
                        res.Failed.Add($"{kind}: store touched by an incomplete load");
                    }
                    if (IdentityAlerts() != alertsBefore + 1)
                        wrongAlertCounts++;
                }
            }
            sw.Stop();
            res.WallSecs = sw.Elapsed.TotalSeconds;

            var expectedIncomplete = schedule.Count(s => s.Kind != "complete");
            res.Metrics.Add($"churn cycles           : {schedule.Count} ({schedule.Count(s => s.Kind == "complete")} complete, {expectedIncomplete} incomplete)");
            res.Metrics.Add($"complete loads applied : {applied}");
            res.Metrics.Add($"incomplete loads threw : {incompleteThrows}/{expectedIncomplete}");
            res.Metrics.Add($"identity alerts fired  : {IdentityAlerts()}");
            res.Metrics.Add($"partial applications   : {partialApplications}");
            res.Metrics.Add($"users resolved total   : {usersResolved:N0}");
            res.Metrics.Add($"final store size       : {store.Count():N0} (= largest complete load {maxApplied:N0})");
            res.Check(incompleteThrows == expectedIncomplete, "EVERY incomplete load threw");
            res.Check(IdentityAlerts() == expectedIncomplete, "EVERY incomplete load alerted (exactly once)");
            res.Check(wrongAlertCounts == 0, "alert count exact per incomplete load");
            res.Check(partialApplications == 0, "no partial directory ever applied");
            res.Check(store.Count() == maxApplied, "complete loads recover cleanly after failures");
            return res;
        }
        finally
        {
            Environment.SetEnvironmentVariable(Alerting.WebhookUrlEnvVar, previousUrl);
            Alerting.HttpClient = previousClient;
        }
    }

    // ── 12. Watermark/lag boundary storm ─────────────────────────────────────
    public async Task<ScenarioResult> WatermarkStormAsync()
    {
        var res = new ScenarioResult { Name = "watermark-storm" };
        var rowsPerFile = 300 * _scale;
        var baseDay = new DateTime(2026, 7, 17, 0, 0, 0, DateTimeKind.Utc);
        var offsets = new[]
        {
            TimeSpan.Zero, TimeSpan.FromMilliseconds(1), TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(12), new TimeSpan(0, 23, 59, 59, 999),
        };
        var lags = new[] { 0, 1, 23, 24, 25, 48 };

        var combos = 0;
        var partitionsEvaluated = 0;
        var wrongInclusions = 0;
        var wrongExclusions = 0;
        long rowsScanned = 0;
        var sw = Stopwatch.StartNew();

        foreach (var offset in offsets)
        foreach (var lag in lags)
        {
            combos++;
            var since = baseDay + offset;
            var bound = DateOnly.FromDateTime(since.AddHours(-lag).Date);   // independent model
            var dts = Enumerable.Range(-4, 9).Select(k => bound.AddDays(k)).ToList();

            var source = new ScatterBdhSource();
            foreach (var d in dts)
            {
                var dt = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                source.AddFile($"Contact/dt={dt}/p.jsonl", rowsPerFile, 1024,
                    r => $$"""{"Id":"C{{d.DayNumber:D7}}{{r:D6}}","Status":"Active"}""");
            }

            var cfg = HarnessConfig.Make(lagHours: lag, allowFullScan: true, maxRecordsPerObject: 0);
            var fetcher = new BdhFetcher(cfg, source, FilterSet.Empty);
            var result = await fetcher.FetchAsync(
                new ObjectConfig { ObjectName = "Contact", DisplayName = "Contact" },
                fullCrawl: false, sinceUtc: since);
            rowsScanned += result.Stats.RecordsScanned;
            partitionsEvaluated += dts.Count;

            var included = result.Records.Select(r => r.DataAsOf!).ToHashSet(StringComparer.Ordinal);
            foreach (var d in dts)
            {
                var dt = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var expect = d >= bound;
                if (included.Contains(dt) && !expect)
                {
                    wrongInclusions++;
                    res.Failed.Add($"lag={lag} since={since:o}: dt={dt} wrongly INCLUDED");
                }
                if (!included.Contains(dt) && expect)
                {
                    wrongExclusions++;
                    res.Failed.Add($"lag={lag} since={since:o}: dt={dt} wrongly EXCLUDED");
                }
            }
            if (result.Stats.PartitionsPruned != 4)
                res.Failed.Add($"lag={lag} since={since:o}: pruned {result.Stats.PartitionsPruned} != 4");
        }

        // Malformed/missing dt fail-safe block (fixed layout, counted once).
        var mal = new ScatterBdhSource();
        string MalRow(string tag, int r) => $$"""{"Id":"{{tag}}{{r:D6}}","Status":"Active"}""";
        mal.AddFile("Contact/dt=2026-07-10/p.jsonl", rowsPerFile, 1024, r => MalRow("OLD", r));
        mal.AddFile("Contact/dt=2026-07-15/p.jsonl", rowsPerFile, 1024, r => MalRow("ATB", r));
        mal.AddFile("Contact/dt=2026-07-16/p.jsonl", rowsPerFile, 1024, r => MalRow("NEW", r));
        mal.AddFile("Contact/dt=2026-02-30/p.jsonl", rowsPerFile, 1024, r => MalRow("BADDATE", r));
        mal.AddFile("Contact/dt=garbage/p.jsonl", rowsPerFile, 1024, r => MalRow("GARBAGE", r));
        mal.AddFile("Contact/extras/p.jsonl", rowsPerFile, 1024, r => MalRow("EXTRAS", r));
        mal.AddFile("Contact/root-file.jsonl", rowsPerFile, 1024, r => MalRow("ROOT", r));
        var malCfg = HarnessConfig.Make(lagHours: 24, allowFullScan: true, maxRecordsPerObject: 0);
        var malFetcher = new BdhFetcher(malCfg, mal, FilterSet.Empty);
        var malResult = await malFetcher.FetchAsync(
            new ObjectConfig { ObjectName = "Contact", DisplayName = "Contact" },
            fullCrawl: false, sinceUtc: new DateTime(2026, 7, 16, 0, 0, 0, DateTimeKind.Utc));
        rowsScanned += malResult.Stats.RecordsScanned;
        var malIds = malResult.Records.Select(r => r.ItemId[..3]).Distinct().OrderBy(s => s).ToList();
        var malOk = malResult.Records.Count == 6 * rowsPerFile
                    && malResult.Stats.PartitionsPruned == 1
                    && !malIds.Contains("OLD");
        sw.Stop();
        res.WallSecs = sw.Elapsed.TotalSeconds;

        res.Metrics.Add($"boundary combos        : {combos} ({offsets.Length} since offsets × {lags.Length} lags incl. DST-like 23/25h)");
        res.Metrics.Add($"partitions evaluated   : {partitionsEvaluated:N0} (+7 malformed/missing-dt cases)");
        res.Metrics.Add($"rows scanned           : {rowsScanned:N0}");
        res.Metrics.Add($"wrong inclusions       : {wrongInclusions}");
        res.Metrics.Add($"wrong exclusions       : {wrongExclusions}");
        res.Metrics.Add($"scan throughput        : {rowsScanned / Math.Max(0.001, res.WallSecs):N0} rows/s");
        res.Check(wrongInclusions == 0, "no partition wrongly included at any boundary");
        res.Check(wrongExclusions == 0, "no partition wrongly excluded at any boundary");
        res.Check(malOk, "malformed/missing dt dirs fail-safe included; only real out-of-window dt pruned");
        return res;
    }

    private static HttpResponseMessage WithRetryAfter(HttpResponseMessage resp, string value)
    {
        resp.Headers.TryAddWithoutValidation("Retry-After", value);
        return resp;
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>Thread-safe handler that records request bodies and returns 200.</summary>
internal sealed class CapturingHandler : HttpMessageHandler
{
    private readonly List<string> _bodies = new();
    private readonly object _lock = new();

    public IReadOnlyList<string> Bodies
    {
        get { lock (_lock) return _bodies.ToList(); }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(ct);
        lock (_lock) _bodies.Add(body);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
    }
}

/// <summary>Minimal delegate-backed HttpMessageHandler.</summary>
internal sealed class LambdaHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
    public LambdaHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(_fn(request));
}
