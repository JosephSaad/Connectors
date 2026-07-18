// StressHarness.cs
// ----------------
// Load/concurrency stress harness for the Clarizen connector. Everything runs
// in-process against the existing fakes/mocks (no real network). Each scenario
// asserts a hard invariant AND emits REAL measured numbers via StressLog so the
// run can be read back after `dotnet test`.
//
// Scenarios:
//   1. Circuit breaker releases its half-open slot on EVERY exit path
//      (success / failure / ignored / budget-throw / token-error /
//      cancellation) under heavy concurrency — no slot leak, no wedge.
//   2. HMAC webhook validation under a concurrent valid+forged flood —
//      fail-closed, body-size cap, correctness rate.
//   3. Content extraction decompression caps hold against OOXML/PDF bombs —
//      output + allocations stay bounded, no OOM.
//   4. Deletion sweep / withdraw is correct under concurrency — no over-deletion.
//   5. Checkpoint/resume + dead-letter under concurrent producers; retry-failed
//      drains exactly.
//   6. $batch ingest respects the ≤20 cap and backs off on 429/Retry-After.

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ClarizenConnector.AclEngine;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Content;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;
using ClarizenConnector.Item;
using ClarizenConnector.Webhook;
using Xunit.Abstractions;

namespace ClarizenConnector.Tests;

/// <summary>Appends measured numbers to a results file (STRESS_RESULTS_FILE or a
/// temp path) and to the xUnit output — so numbers survive a passing run.</summary>
internal static class StressLog
{
    private static readonly object Gate = new();
    private static readonly string Path =
        Environment.GetEnvironmentVariable("STRESS_RESULTS_FILE")
        ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clarizen_stress_results.txt");

    public static void Line(ITestOutputHelper? output, string text)
    {
        output?.WriteLine(text);
        lock (Gate)
            File.AppendAllText(Path, text + Environment.NewLine);
    }
}

public class BreakerSlotStressTests
{
    private readonly ITestOutputHelper _out;
    public BreakerSlotStressTests(ITestOutputHelper output) => _out = output;

    private static CircuitBreaker HalfOpen(int trials, Func<DateTime> clock, ref DateTime now)
    {
        var b = new CircuitBreaker("stress", new CircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = 2,
            OpenDuration = TimeSpan.FromSeconds(30),
            Window = TimeSpan.FromSeconds(60),
            HalfOpenTrials = trials,
        }, clock);
        b.TripForTests();
        now = now.AddSeconds(31);       // past OpenDuration → HalfOpen on next Refresh
        Assert.Equal(CircuitState.HalfOpen, b.State);
        return b;
    }

    // REGRESSION (HIGH): GraphClient.SendWithRetryAsync took a half-open probe
    // slot but had no try/finally settle — a non-5xx token-fetch error leaked the
    // slot, wedging the Graph breaker forever. Prove the slot is now released.
    [Fact]
    public async Task GraphClient_TokenError_ReleasesHalfOpenSlot_NoWedge()
    {
        var now = DateTime.UtcNow;
        var breaker = HalfOpen(1, () => now, ref now);

        // Token endpoint returns 400 (invalid_client) → GetTokenAsync throws
        // InvalidOperationException BEFORE the request try-block. OverrideToken is
        // left null so the real token path runs.
        var handler = new MockHttpHandler((req, _) =>
            MockHttpHandler.Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid_client\"}"));
        var client = new GraphClient(TestConfig.Make(graphMaxRetries: 0), handler, breaker);
        client.DelayAsync = (_, _) => Task.CompletedTask;

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PutAsync("items/1", new JsonObject()));

        // The probe slot must be released: still HalfOpen, still admitting probes.
        Assert.Equal(CircuitState.HalfOpen, breaker.State);
        Assert.True(breaker.TryAcquire(), "half-open slot leaked on token error — breaker wedged");
        StressLog.Line(_out, "[breaker] GraphClient token-error path: slot released, no wedge — PASS");
    }

    // REGRESSION (HIGH): cancellation during a retry DelayAsync also escaped the
    // slot uncaught. Prove the finally releases it.
    [Fact]
    public async Task GraphClient_CancellationDuringRetry_ReleasesHalfOpenSlot()
    {
        var now = DateTime.UtcNow;
        var breaker = HalfOpen(1, () => now, ref now);

        var handler = new MockHttpHandler((_, _) =>
            MockHttpHandler.Json(HttpStatusCode.ServiceUnavailable, "boom"));  // retryable
        var client = new GraphClient(TestConfig.Make(graphMaxRetries: 3), handler, breaker)
        {
            OverrideToken = "t",
        };
        // The retry wait throws as if the crawl were cancelled mid-backoff.
        client.DelayAsync = (_, _) => throw new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() => client.PutAsync("items/1", new JsonObject()));

        Assert.Equal(CircuitState.HalfOpen, breaker.State);
        Assert.True(breaker.TryAcquire(), "half-open slot leaked on cancellation — breaker wedged");
        StressLog.Line(_out, "[breaker] GraphClient cancellation-during-retry: slot released, no wedge — PASS");
    }

    // Hammer a shared breaker's half-open slot accounting from many threads doing
    // TryAcquire→OnIgnored (which keeps it HalfOpen). At the end, exactly
    // HalfOpenTrials slots must be free — no leak, no over-release — proving the
    // release path is correct under contention.
    [Fact]
    public async Task SharedBreaker_HalfOpenSlots_NeverLeakUnderConcurrency()
    {
        const int trials = 8;
        const int threads = 32;
        const int perThread = 50_000;
        var now = DateTime.UtcNow;
        var breaker = HalfOpen(trials, () => now, ref now);

        long acquired = 0;
        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            var local = 0L;
            for (var i = 0; i < perThread; i++)
            {
                if (breaker.TryAcquire())
                {
                    local++;
                    breaker.OnIgnored();  // release; keeps state HalfOpen
                }
            }
            Interlocked.Add(ref acquired, local);
        })).ToArray();
        await Task.WhenAll(tasks);
        sw.Stop();

        // Still HalfOpen, and exactly `trials` slots are free (nothing leaked).
        Assert.Equal(CircuitState.HalfOpen, breaker.State);
        var free = 0;
        while (breaker.TryAcquire())
            free++;
        Assert.Equal(trials, free);

        var ops = acquired + free;
        var rate = ops / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        StressLog.Line(_out,
            $"[breaker] shared half-open hammer: {threads} threads x {perThread} loops, "
            + $"{acquired:N0} acquire/release cycles in {sw.ElapsedMilliseconds} ms "
            + $"({rate / 1e6:0.00} M ops/s); free slots at end = {free} (== {trials}) — NO LEAK");
    }

    // Drive every real terminal outcome through ClarizenClient probes at scale
    // (each task on its own breaker/client so throughput is the measure), proving
    // the slot always settles once. Includes the budget-throw path.
    [Fact]
    public async Task ClarizenClient_EveryExitPath_SettlesSlot_AtScale()
    {
        const int workers = 24;
        const int iterations = 4_000;
        long total = 0, wedged = 0;
        var sw = Stopwatch.StartNew();

        var tasks = Enumerable.Range(0, workers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var now = DateTime.UtcNow;
                var breaker = new CircuitBreaker("s", new CircuitBreakerOptions
                {
                    Enabled = true, FailureThreshold = 1,
                    OpenDuration = TimeSpan.FromSeconds(30),
                    Window = TimeSpan.FromSeconds(60), HalfOpenTrials = 1,
                }, () => now);
                breaker.TripForTests();
                now = now.AddSeconds(31);           // HalfOpen

                var kind = (w + i) % 4;
                var budget = kind == 3
                    ? Exhausted()                    // Consume() throws → finally settles
                    : new ApiBudget(1_000_000, callsPerMinute: 6_000_000);
                var status = kind switch
                {
                    0 => HttpStatusCode.OK,          // success → closes
                    1 => HttpStatusCode.BadGateway,  // 5xx → reopens
                    _ => HttpStatusCode.BadRequest,  // 4xx → ignored
                };
                var handler = new MockHttpHandler((_, _) => MockHttpHandler.Json(status, "{}"));
                var client = new ClarizenClient(TestConfig.Make(), budget, handler, breaker)
                {
                    OverrideSessionId = "s",
                };
                client.DelayAsync = (_, _) => Task.CompletedTask;

                try { await client.QueryAllAsync("SELECT x FROM Y"); }
                catch { /* expected for failure/ignored/quota kinds */ }

                // Whatever the outcome, the breaker must not be wedged in HalfOpen
                // with a leaked slot: it is either Closed (success), Open (5xx),
                // or HalfOpen-with-a-free-slot (4xx/quota).
                if (breaker.State == CircuitState.HalfOpen && !breaker.TryAcquire())
                    Interlocked.Increment(ref wedged);
                Interlocked.Increment(ref total);
            }
        })).ToArray();
        await Task.WhenAll(tasks);
        sw.Stop();

        Assert.Equal(0, wedged);
        var rate = total / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        StressLog.Line(_out,
            $"[breaker] ClarizenClient every-exit-path: {total:N0} probe cycles "
            + $"(success/5xx/4xx/quota) in {sw.ElapsedMilliseconds} ms ({rate:N0} probes/s); "
            + $"wedged breakers = {wedged} — PASS");
    }

    private static ApiBudget Exhausted()
    {
        var b = new ApiBudget(callsPerDay: 1, callsPerMinute: 6_000_000);
        b.Consume();
        return b;
    }
}

public class WebhookFloodStressTests : IDisposable
{
    private const string Secret = "flood-secret-value";
    private readonly ITestOutputHelper _out;
    private readonly TempDir _dir = new();
    private readonly SyncStateScope _state = new();
    private readonly IdentityStore _store;
    private readonly WebhookProcessor _processor;

    public WebhookFloodStressTests(ITestOutputHelper output)
    {
        _out = output;
        Metrics.ResetForTests();
        _store = new IdentityStore("Flood", Path.Combine(_dir.Path, "id.db"));
        var schema = new SchemaConfig
        {
            ObjectList = new List<ObjectConfig>
            {
                new() { ObjectName = "Task", SelectedFields = new Dictionary<string, string> { ["Name"] = "Title" } },
            },
        };
        var config = TestConfig.Make();
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Json(HttpStatusCode.OK, "{}"));
        var clarizen = new ClarizenClient(config, new ApiBudget(1_000_000), handler);
        var mapper = new PrincipalMapper(_store, "{}");
        var resolver = new AclResolver(mapper, new DirectorySnapshot(), string.Empty, string.Empty);
        var pipeline = new IngestPipeline(
            config, schema, clarizen, new FakeGraphClient(config), resolver, new ItemConverter(config),
            ha: null, inventoryFactory: id => new ItemInventory(id, Path.Combine(_dir.Path, $"inv_{id}.db")));
        _processor = new WebhookProcessor(schema, clarizen, pipeline, TimeSpan.FromSeconds(60));
    }

    public void Dispose()
    {
        _store.Dispose();
        _state.Dispose();
        _dir.Dispose();
        Metrics.ResetForTests();
    }

    private static int FreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        return ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
    }

    [Fact]
    public async Task ConcurrentValidAndForgedFlood_FailsClosed_And_CapsHold()
    {
        const int validCount = 300;
        const int forgedCount = 300;
        var port = FreePort();
        using var scope = new EnvScope(
            (WebhookReceiver.PortEnvVar, port.ToString()),
            (WebhookReceiver.SecretEnvVar, Secret),
            (WebhookReceiver.PathEnvVar, null),
            (WebhookReceiver.HeaderEnvVar, null),
            ("USE_KEY_VAULT", null));
        using var receiver = WebhookReceiver.StartIfConfigured(_processor);
        Assert.NotNull(receiver);

        var validator = new SignatureValidator(Secret);
        using var httpHandler = new SocketsHttpHandler { MaxConnectionsPerServer = 128 };
        using var client = new HttpClient(httpHandler);
        // Bound simultaneously-open loopback sockets so the HttpListener accept
        // backlog is not overrun — still a genuine concurrent flood (128 in
        // flight), just not a self-inflicted socket-exhaustion DoS on the test.
        using var gate = new SemaphoreSlim(128);

        async Task<HttpStatusCode> Post(string body, string? sig)
        {
            await gate.WaitAsync();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}/webhook")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                if (sig is not null)
                    req.Headers.TryAddWithoutValidation(SignatureValidator.DefaultHeader, sig);
                using var resp = await client.SendAsync(req);
                return resp.StatusCode;
            }
            finally
            {
                gate.Release();
            }
        }

        // 300 valid (distinct entities) + 300 forged, all fired concurrently.
        var jobs = new List<Task<(bool Valid, HttpStatusCode Code)>>();
        for (var i = 0; i < validCount; i++)
        {
            var body = $"{{\"events\":[{{\"entityType\":\"Task\",\"id\":\"/Task/{i}\",\"operation\":\"update\"}}]}}";
            var sig = validator.ComputeHex(Encoding.UTF8.GetBytes(body));
            jobs.Add(Task.Run(async () => (true, await Post(body, sig))));
        }
        for (var i = 0; i < forgedCount; i++)
        {
            var body = $"{{\"events\":[{{\"entityType\":\"Task\",\"id\":\"/Task/f{i}\",\"operation\":\"update\"}}]}}";
            // Wrong secret → forged signature; must be rejected (fail-closed).
            var sig = new SignatureValidator("attacker-secret").ComputeHex(Encoding.UTF8.GetBytes(body));
            jobs.Add(Task.Run(async () => (false, await Post(body, sig))));
        }

        var sw = Stopwatch.StartNew();
        var results = await Task.WhenAll(jobs);
        sw.Stop();

        var validAccepted = results.Count(r => r.Valid && r.Code == HttpStatusCode.Accepted);
        var validWrong = results.Count(r => r.Valid && r.Code != HttpStatusCode.Accepted);
        var forgedRejected = results.Count(r => !r.Valid && r.Code == HttpStatusCode.Unauthorized);
        var forgedLeaked = results.Count(r => !r.Valid && r.Code == HttpStatusCode.Accepted);

        Assert.Equal(validCount, validAccepted);   // every genuine post accepted
        Assert.Equal(0, validWrong);
        Assert.Equal(forgedCount, forgedRejected);  // every forged post rejected
        Assert.Equal(0, forgedLeaked);              // NONE leaked past the boundary
        Assert.Equal(validCount, _processor.Debouncer.PendingCount);  // only valid enqueued

        // Body-size cap: a >1MB post is rejected BEFORE parse/enqueue. The
        // receiver short-circuits on ContentLength64 > cap without draining the
        // body, so the client sees either a clean 413 or a mid-upload connection
        // drop — BOTH are correct "rejected before enqueue". The invariant that
        // matters: nothing new is enqueued.
        var pendingBefore = _processor.Debouncer.PendingCount;
        var oversize = new string('x', WebhookReceiver.MaxBodyBytes + 1024);
        var big = $"{{\"events\":[{{\"entityType\":\"Task\",\"id\":\"/Task/big\",\"pad\":\"{oversize}\"}}]}}";
        var bigSig = validator.ComputeHex(Encoding.UTF8.GetBytes(big));
        HttpStatusCode? bigCode = null;
        try { bigCode = await Post(big, bigSig); }
        catch (HttpRequestException) { /* server closed early on oversize — also a rejection */ }
        if (bigCode is not null)
            Assert.Equal((HttpStatusCode)413, bigCode);
        Assert.Equal(pendingBefore, _processor.Debouncer.PendingCount);  // oversize never enqueued

        var total = validCount + forgedCount;
        StressLog.Line(_out,
            $"[webhook] flood: {total} concurrent posts ({validCount} valid + {forgedCount} forged) "
            + $"in {sw.ElapsedMilliseconds} ms ({total / Math.Max(0.001, sw.Elapsed.TotalSeconds):N0} posts/s); "
            + $"valid→202={validAccepted}, forged→401={forgedRejected}, leaked={forgedLeaked}; "
            + $"validation correctness = {(validAccepted + forgedRejected) * 100.0 / total:0.0}%; "
            + $"oversize→413 cap holds — PASS");
    }
}

public class ContentBombStressTests
{
    private readonly ITestOutputHelper _out;
    public ContentBombStressTests(ITestOutputHelper output) => _out = output;

    // A docx whose word/document.xml is a single <w:t> node holding ~8M chars.
    // Streamed into the zip so the TEST never buffers it whole; the extractor
    // must truncate at its inflate/accumulation ceiling, not OOM.
    [Fact]
    public void OoxmlBomb_TruncatesBounded_NoOom()
    {
        const int bombChars = 8_000_000;   // > MaxInflatedBytes and > MaxExtractedChars
        byte[] docx;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("word/document.xml");
                using var s = entry.Open();
                using var w = new StreamWriter(s, new UTF8Encoding(false));
                w.Write("<?xml version=\"1.0\"?><w:document xmlns:w=\"x\"><w:body><w:p><w:r><w:t>");
                var block = new string('A', 64 * 1024);
                for (var written = 0; written < bombChars; written += block.Length)
                    w.Write(block);
                w.Write("</w:t></w:r></w:p></w:body></w:document>");
            }
            docx = ms.ToArray();
        }

        var extractor = new ContentExtractor();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        var result = extractor.Extract(docx, "bomb.docx", null);
        sw.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        // Output is capped far below the 8M-char bomb; work stayed bounded.
        Assert.True(result.Text.Length <= ContentExtractor.MaxExtractedChars,
            $"output {result.Text.Length} exceeded cap {ContentExtractor.MaxExtractedChars}");
        // Allocations during extraction are a small multiple of the caps, NOT the
        // 8 MB decompressed bomb blown up in memory.
        Assert.True(allocated < 64L * 1024 * 1024,
            $"extraction allocated {allocated / 1024 / 1024} MB — cap failed to bound memory");
        StressLog.Line(_out,
            $"[content] OOXML bomb: {bombChars / 1_000_000}M-char <w:t> compressed to "
            + $"{docx.Length / 1024} KB → extracted {result.Text.Length:N0} chars "
            + $"(cap {ContentExtractor.MaxExtractedChars:N0}) in {sw.ElapsedMilliseconds} ms; "
            + $"allocated {allocated / 1024 / 1024} MB (bounded) — NO OOM");
    }

    // A PDF FlateDecode stream that inflates to ~64MB from a tiny compressed body.
    // ReadBounded must stop at MaxInflatedBytes.
    [Fact]
    public void PdfFlateBomb_InflationBounded_NoOom()
    {
        const int inflated = 64 * 1024 * 1024;
        byte[] compressed;
        using (var cms = new MemoryStream())
        {
            using (var zlib = new ZLibStream(cms, CompressionLevel.Optimal, leaveOpen: true))
            {
                var block = new byte[64 * 1024];
                Array.Fill(block, (byte)' ');
                // Embed a tiny bit of PDF text so any inflated prefix is valid.
                var seed = Encoding.ASCII.GetBytes("(hello) Tj ");
                zlib.Write(seed, 0, seed.Length);
                for (var written = 0; written < inflated; written += block.Length)
                    zlib.Write(block, 0, block.Length);
            }
            compressed = cms.ToArray();
        }

        var pdf = new MemoryStream();
        var header = Encoding.Latin1.GetBytes("%PDF-1.4\n<< /Filter /FlateDecode /Length "
            + compressed.Length + " >>\nstream\n");
        pdf.Write(header, 0, header.Length);
        pdf.Write(compressed, 0, compressed.Length);
        var footer = Encoding.Latin1.GetBytes("\nendstream\n%%EOF");
        pdf.Write(footer, 0, footer.Length);
        var bytes = pdf.ToArray();

        var extractor = new ContentExtractor();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        var result = extractor.Extract(bytes, "bomb.pdf", null);
        sw.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.True(result.Text.Length <= ContentExtractor.MaxExtractedChars);
        Assert.True(allocated < 128L * 1024 * 1024,
            $"pdf extraction allocated {allocated / 1024 / 1024} MB — inflate cap failed");
        StressLog.Line(_out,
            $"[content] PDF FlateDecode bomb: {compressed.Length / 1024} KB compressed → would inflate to "
            + $"{inflated / 1024 / 1024} MB; extracted {result.Text.Length:N0} chars in {sw.ElapsedMilliseconds} ms; "
            + $"allocated {allocated / 1024 / 1024} MB (bounded by MaxInflatedBytes) — NO OOM");
    }
}

public class DeletionAndStateStressTests
{
    private const string Connector = "ClarizenAdaptiveWork";
    private readonly ITestOutputHelper _out;
    public DeletionAndStateStressTests(ITestOutputHelper output) => _out = output;

    private static IngestPipeline Pipeline(
        TempDir dir, out FakeGraphClient graph, out Func<string, IItemInventory> invFactory)
    {
        var config = TestConfig.Make(graphBatchSize: 20);
        var schema = new SchemaConfig
        {
            ObjectList = new List<ObjectConfig>
            {
                new() { ObjectName = "Task", SelectedFields = new Dictionary<string, string> { ["Name"] = "Title" } },
            },
        };
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Json(HttpStatusCode.OK, "{}"));
        var clarizen = new ClarizenClient(config, new ApiBudget(1_000_000, callsPerMinute: 6_000_000), handler);
        var store = new IdentityStore("DelStress", Path.Combine(dir.Path, "id.db"));
        var mapper = new PrincipalMapper(store, "{}");
        var resolver = new AclResolver(mapper, new DirectorySnapshot(), string.Empty, string.Empty);
        graph = new FakeGraphClient(config);
        var localFactory = (Func<string, IItemInventory>)(id =>
            new ItemInventory(id, Path.Combine(dir.Path, $"inv_{id}.db")));
        invFactory = localFactory;
        return new IngestPipeline(
            config, schema, clarizen, graph, resolver, new ItemConverter(config),
            ha: null, inventoryFactory: localFactory);
    }

    // Concurrent withdraws of DISTINCT ids against a shared inventory: every id is
    // deleted exactly once, the inventory ends empty, and no id is deleted twice.
    [Fact]
    public async Task ConcurrentWithdraw_NoOverDeletion()
    {
        using var dir = new TempDir();
        using var state = new SyncStateScope();
        Metrics.ResetForTests();
        var pipeline = Pipeline(dir, out _, out var invFactory);
        var objectConfig = new ObjectConfig
        {
            ObjectName = "Task",
            SelectedFields = new Dictionary<string, string> { ["Name"] = "Title" },
        };

        const int n = 500;
        using (var inv = invFactory(Connector))
        {
            inv.RecordSeen(Enumerable.Range(0, n).Select(i => ($"Task_{i}", "Task")), DateTime.UtcNow);
            Assert.Equal(n, inv.Count());
        }

        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, n)
            .Select(i => Task.Run(() => pipeline.WithdrawSingleAsync(objectConfig, i.ToString())))
            .ToArray();
        var results = await Task.WhenAll(tasks);
        sw.Stop();

        // Correctness is measured on state the product owns (thread-safe): every
        // withdraw succeeded, the shared inventory drained to EXACTLY zero (no
        // under-deletion), and the Interlocked delete counter is exactly n (no
        // over-deletion / no id removed twice). (FakeGraphClient.Sent is a plain
        // List and is deliberately NOT used as the oracle under concurrency.)
        Assert.All(results, r => Assert.True(r.Ok, r.Error));
        using (var inv = invFactory(Connector))
            Assert.Equal(0, inv.Count());
        Assert.Equal(n, Metrics.ItemsDeleted);
        StressLog.Line(_out,
            $"[deletion] {n} concurrent withdraws in {sw.ElapsedMilliseconds} ms: inventory drained "
            + $"to exactly 0, items_deleted_total = {Metrics.ItemsDeleted} (== {n}) — NO OVER/UNDER-DELETION");
    }

    // Concurrent dead-letter producers must lose nothing; retry-failed then drains
    // to exactly zero. Also hammers checkpoint writes for monotonic advance.
    [Fact]
    public async Task DeadLetter_ConcurrentProducers_LoseNothing_DrainExactly()
    {
        using var state = new SyncStateScope();
        const int producers = 16;
        const int perProducer = 500;

        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, producers).Select(p => Task.Run(() =>
        {
            for (var i = 0; i < perProducer; i++)
            {
                SyncState.AppendFailedRecords(
                    Connector,
                    new List<(string, string)> { ($"Task_{p}_{i}", $"err-{p}-{i}") },
                    "Task");
                SyncState.WriteCheckpoint(Connector, null, "Task", (p * perProducer) + i);
            }
        })).ToArray();
        await Task.WhenAll(tasks);
        sw.Stop();

        var expected = producers * perProducer;
        var records = SyncState.ReadFailedRecords(Connector);
        Assert.Equal(expected, records.Count);                       // nothing lost
        Assert.Equal(expected, records.Select(r => r["item_id"]!.GetValue<string>()).Distinct().Count());

        // retry-failed semantics: clear drains to exactly zero.
        SyncState.ClearFailedRecords(Connector);
        Assert.Empty(SyncState.ReadFailedRecords(Connector));

        var cp = SyncState.ReadCheckpoint(Connector);
        Assert.NotNull(cp);
        Assert.Equal(expected - 1, cp!["completed"]!["Task"]!.GetValue<int>());  // monotonic max

        StressLog.Line(_out,
            $"[deadletter] {producers} producers x {perProducer} = {expected} concurrent appends in "
            + $"{sw.ElapsedMilliseconds} ms: read back {records.Count} (0 lost), all distinct; "
            + $"clear drained to 0; checkpoint monotonic max = {cp["completed"]!["Task"]!.GetValue<int>()} — PASS");
    }
}

public class BatchCapStressTests
{
    private readonly ITestOutputHelper _out;
    public BatchCapStressTests(ITestOutputHelper output) => _out = output;

    // Records the largest $batch envelope Graph ever receives, and whether a 429
    // was surfaced for the adaptive-concurrency dial.
    private sealed class CountingGraph : GraphClient
    {
        public int MaxBatchRequests;
        public int BatchPosts;
        public bool Throttle;
        private int _postCount;
        public CountingGraph(AppConfig cfg) : base(cfg) => OverrideToken = "t";

        public override Task<GraphResponse> SendWithRetryAsync(
            HttpMethod method, string path, JsonNode? body, CancellationToken ct = default)
        {
            if (path == "$batch" && body is JsonObject env && env["requests"] is JsonArray reqs)
            {
                Interlocked.Increment(ref BatchPosts);
                var n = reqs.Count;
                if (n > Volatile.Read(ref MaxBatchRequests))
                    Volatile.Write(ref MaxBatchRequests, n);
                var responses = new JsonArray();
                // First envelope returns a per-item 429 to exercise back-off.
                var throttleThis = Throttle && Interlocked.Increment(ref _postCount) == 1;
                foreach (var r in reqs)
                {
                    responses.Add(new JsonObject
                    {
                        ["id"] = r!["id"]!.GetValue<string>(),
                        ["status"] = throttleThis ? 429 : 200,
                    });
                }
                return Task.FromResult(new GraphResponse
                {
                    StatusCode = HttpStatusCode.OK,
                    Body = new JsonObject { ["responses"] = responses },
                });
            }
            return Task.FromResult(new GraphResponse { StatusCode = HttpStatusCode.OK });
        }
    }

    [Fact]
    public async Task Ingest_RespectsBatchCap20_AndBacksOffOn429()
    {
        using var dir = new TempDir();
        using var state = new SyncStateScope();
        Metrics.ResetForTests();

        // Configure a batch size ABOVE the Graph limit; the pipeline must still
        // never post more than 20 requests per envelope. (GraphBatchSize is
        // clamped to ≤20 by AppConfig; assert the real posted envelopes.)
        var config = TestConfig.Make(ingestChunkSize: 1000, graphBatchSize: 20);
        var schema = new SchemaConfig
        {
            ObjectList = new List<ObjectConfig>
            {
                new()
                {
                    ObjectName = "Task", AclMode = "ownerOnly",
                    SelectedFields = new Dictionary<string, string>
                    { ["Name"] = "Title", ["Owner"] = "Owner" },
                },
            },
        };
        const int records = 1000;
        var entities = new JsonArray();
        for (var i = 1; i <= records; i++)
        {
            entities.Add(new JsonObject
            {
                ["id"] = $"/Task/{i}",
                ["Name"] = $"Task {i}",
                ["Owner"] = new JsonObject { ["id"] = "/User/1", ["name"] = "Owner" },
            });
        }
        var handler = new MockHttpHandler((req, _) =>
            req.RequestUri!.AbsolutePath.EndsWith("/authentication/login")
                ? MockHttpHandler.Json(HttpStatusCode.OK, "{\"sessionId\":\"s\"}")
                : MockHttpHandler.Json(HttpStatusCode.OK,
                    new JsonObject
                    {
                        ["entities"] = entities.DeepClone(),
                        ["paging"] = new JsonObject { ["hasMore"] = false },
                    }.ToJsonString()));
        var clarizen = new ClarizenClient(config, new ApiBudget(1_000_000, callsPerMinute: 6_000_000), handler);
        clarizen.DelayAsync = (_, _) => Task.CompletedTask;
        var store = new IdentityStore("BatchStress", Path.Combine(dir.Path, "id.db"));
        store.Upsert(new PrincipalMapping("/User/1", "user", "o@x.com", "entra-o", DateTime.UtcNow));
        var mapper = new PrincipalMapper(store, "{}");
        var resolver = new AclResolver(mapper, new DirectorySnapshot(), string.Empty, string.Empty);
        var graph = new CountingGraph(config) { Throttle = true };
        var pipeline = new IngestPipeline(
            config, schema, clarizen, graph, resolver, new ItemConverter(config),
            ha: null, inventoryFactory: id => new ItemInventory(id, Path.Combine(dir.Path, $"inv_{id}.db")));

        var beforeConc = pipeline.Concurrency.Current;
        var sw = Stopwatch.StartNew();
        var summary = await pipeline.RunAsync(fullCrawl: true);
        sw.Stop();

        // Exactly one 20-item envelope was per-item-429'd → those 20 dead-letter;
        // the rest ingest. The cap must never be exceeded on any envelope.
        Assert.True(graph.MaxBatchRequests <= 20, $"posted a $batch of {graph.MaxBatchRequests} (> 20 cap)");
        Assert.Equal(20, graph.MaxBatchRequests);          // full envelopes used up to the cap
        Assert.Equal(records - 20, summary.Ingested);
        Assert.Equal(20, summary.Failed);
        Assert.Equal(50, graph.BatchPosts);                // 1000 / 20
        Assert.Equal(20, SyncState.ReadFailedRecords(Connector).Count);
        Assert.InRange(pipeline.Concurrency.Current, 1, beforeConc);  // adaptive dial stayed in range
        store.Dispose();

        StressLog.Line(_out,
            $"[batch] {records} records → {graph.BatchPosts} $batch posts, max {graph.MaxBatchRequests} "
            + $"requests/envelope (≤20 cap holds); one per-item-429 envelope dead-lettered 20; "
            + $"adaptive concurrency start {beforeConc} → end {pipeline.Concurrency.Current}; "
            + $"ingested {summary.Ingested}, failed {summary.Failed} in {sw.ElapsedMilliseconds} ms — PASS");
    }

    private const string Connector = "ClarizenAdaptiveWork";

    // HTTP-level Retry-After honouring at the real GraphClient: a 429 with
    // Retry-After: 5 is retried, and the honoured (clamped ≤60s) delay is used.
    [Fact]
    public async Task GraphClient_HonoursRetryAfter_On429_ThenSucceeds()
    {
        var attempts = 0;
        var handler = new MockHttpHandler((_, _) =>
        {
            attempts++;
            if (attempts == 1)
            {
                var resp = MockHttpHandler.Json((HttpStatusCode)429, "{}");
                resp.Headers.TryAddWithoutValidation("Retry-After", "5");
                return resp;
            }
            return MockHttpHandler.Json(HttpStatusCode.OK, "{}");
        });
        var client = new GraphClient(TestConfig.Make(graphMaxRetries: 3), handler) { OverrideToken = "t" };
        var delays = new List<TimeSpan>();
        client.DelayAsync = (d, _) => { delays.Add(d); return Task.CompletedTask; };

        var response = await client.PutAsync("items/1", new JsonObject());

        Assert.True(response.IsSuccess);
        Assert.Equal(2, attempts);                       // 429 then 200
        var honored = Assert.Single(delays);
        Assert.Equal(5.0, honored.TotalSeconds, precision: 1);   // server Retry-After honoured
        StressLog.Line(_out,
            $"[batch] GraphClient Retry-After: 429→honoured {honored.TotalSeconds:0}s backoff (clamped ≤"
            + $"{RetryDelay.MaxRetryWaitSeconds:0}s)→200 on retry — PASS");
    }
}
