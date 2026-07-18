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

        var sw = Stopwatch.StartNew();
        var result = await fetcher.FetchAsync(obj, fullCrawl: true, sinceUtc: null);
        sw.Stop();
        res.WallSecs = sw.Elapsed.TotalSeconds;

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

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>Minimal delegate-backed HttpMessageHandler.</summary>
internal sealed class LambdaHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
    public LambdaHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(_fn(request));
}
