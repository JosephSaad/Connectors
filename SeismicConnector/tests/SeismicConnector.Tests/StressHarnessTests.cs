// StressHarnessTests.cs
// ---------------------
// Load / concurrency stress harness for the Seismic connector's signature
// safety paths. Everything is in-process/offline (fakes, loopback HTTP, temp
// dirs) — no network. Each scenario captures REAL measured numbers into a
// results file (StressLog) that the run summary prints, and asserts the
// invariant it is stressing.
//
// Invariants under test (see the class-per-scenario below):
//   1. HMAC validate-before-act under a concurrent flood (valid+forged+malformed).
//   2. Webhook queue cap holds (drop-oldest back-pressure, bounded memory).
//   3. AclResult.Unresolved never widens under concurrent identity churn.
//   4. IdentityStore _gate: concurrent resolution has no races / lost updates.
//   5. No-MNE exclusion holds at scale — excluded items are never emitted.
//   6. Circuit breaker releases its half-open slot on every exit path.
//   7. Dead-letter is correct under concurrent producers; retry dedups exactly.
//   8. $batch respects ≤20 and backs off honoring 429 Retry-After.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using SeismicConnector.Config;
using SeismicConnector.Graph;
using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector.Tests;

/// <summary>Appends measured stress numbers to a results file for the run summary.</summary>
internal static class StressLog
{
    private static readonly object Gate = new();
    public static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "seismic_stress_results.txt");

    public static void Record(string scenario, string detail)
    {
        lock (Gate)
            File.AppendAllText(Path, $"[{scenario}] {detail}\n");
    }
}

// ── 1. HMAC validate-before-act under a concurrent flood ─────────────────────

// xUnit runs test CLASSES in parallel. Both this flood test and the existing
// WebhookReceiverTests bind HttpListeners on loopback ephemeral ports; running
// them concurrently races for ports/listener capacity (observed as a flaky
// 401/connection failure in the webhook tests under the flood). Sharing ONE
// collection serializes the two loopback-binding classes relative to each
// other while everything else still runs in parallel.
[CollectionDefinition("LoopbackWebhook", DisableParallelization = true)]
public sealed class LoopbackWebhookCollection { }

[Collection("LoopbackWebhook")]
public class Stress_HmacFlood
{
    private const string Secret = "s3cr3t-shared-key";

    [Fact]
    public async Task ConcurrentMixedFlood_OnlyValidSignedEventsEverEnqueue()
    {
        var port = FreePort();
        using var receiver = WebhookReceiver.StartIfConfigured(port, Secret);
        Assert.NotNull(receiver);

        const int total = 1200;
        var validIds = new ConcurrentBag<string>();
        var status = new ConcurrentDictionary<int, int>();  // http status → count
        var forgedAccepted = 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // 48-way concurrency over a shared work queue.
        var work = new ConcurrentQueue<int>(Enumerable.Range(0, total));
        var workers = Enumerable.Range(0, 48).Select(_ => Task.Run(async () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            while (work.TryDequeue(out var i))
            {
                var kind = i % 6;
                var contentId = kind == 0 ? $"valid-{i}" : $"forged-{i}";
                var body = $$"""{"type":"contentPublished","contentId":"{{contentId}}"}""";
                string? sig = kind switch
                {
                    0 => Sign(body),                                   // valid
                    1 => null,                                         // missing
                    2 => "sha256=deadbeef",                            // wrong/short hex
                    3 => Sign(body + " "),                             // tampered body → mismatch
                    4 => "%%%not-base64-or-hex%%%",                    // malformed encoding
                    _ => Sign(body).Replace('a', 'b').Replace('0', '1'), // corrupted valid hex
                };
                var code = await PostAsync(http, port, body, sig);
                status.AddOrUpdate((int)code, 1, (_, c) => c + 1);
                if (kind == 0 && code == HttpStatusCode.Accepted)
                    validIds.Add(contentId);
                if (kind != 0 && code == HttpStatusCode.Accepted)
                    Interlocked.Increment(ref forgedAccepted);
            }
        }));
        await Task.WhenAll(workers);
        sw.Stop();

        var drained = receiver!.DrainEvents();
        var drainedIds = drained.Select(e => e.ContentId).ToHashSet(StringComparer.Ordinal);

        // THE security invariant: no forged/malformed/unsigned payload was ever
        // accepted or parsed into the queue.
        Assert.Equal(0, forgedAccepted);
        Assert.All(drainedIds, id => Assert.StartsWith("valid-", id));
        // Every accepted valid event is present (deduped by content id — all unique here).
        var expectedValid = validIds.Distinct().ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expectedValid, drainedIds);

        var accepted = status.GetValueOrDefault(202);
        var rejected = status.GetValueOrDefault(401);
        var throughput = total / sw.Elapsed.TotalSeconds;
        StressLog.Record("HMAC-FLOOD",
            $"posts={total} accepted(202)={accepted} rejected(401)={rejected} " +
            $"forged_accepted={forgedAccepted} drained_valid={drainedIds.Count} " +
            $"throughput={throughput:F0} posts/s elapsed={sw.Elapsed.TotalSeconds:F2}s");

        Assert.Equal(total / 6, accepted);          // exactly the valid sixth
        Assert.Equal(total - total / 6, rejected);  // everything else 401
    }

    private static string Sign(string body) =>
        new SignatureValidator(Secret).ComputeHex(Encoding.UTF8.GetBytes(body));

    private static async Task<HttpStatusCode> PostAsync(HttpClient http, int port, string body, string? sig)
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        if (sig is not null)
            content.Headers.Add(SignatureValidator.DefaultHeader, sig);
        using var resp = await http.PostAsync($"http://localhost:{port}/webhook", content);
        return resp.StatusCode;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

// ── 2. Webhook queue cap holds (bounded memory) ──────────────────────────────

public class Stress_QueueCap
{
    [Fact]
    public void QueueNeverExceedsCap_UnderSustainedProduction()
    {
        var queue = new ConcurrentQueue<ContentEvent>();
        const int cap = 500;
        const int produced = 50_000;
        var peak = 0;
        var totalDropped = 0;
        for (var i = 0; i < produced; i++)
        {
            totalDropped += WebhookReceiver.EnqueueCapped(
                queue, new ContentEvent { Type = "contentPublished", ContentId = $"c{i}" }, cap);
            if (queue.Count > peak)
                peak = queue.Count;
        }
        // Bounded memory: depth never exceeds the cap; the newest cap survive.
        Assert.True(peak <= cap, $"peak depth {peak} exceeded cap {cap}");
        Assert.Equal(cap, queue.Count);
        Assert.Equal(produced - cap, totalDropped);
        var survivors = queue.Select(e => e.ContentId).ToList();
        Assert.Equal("c49500", survivors[0]);  // oldest survivor = produced-cap
        StressLog.Record("QUEUE-CAP",
            $"produced={produced} cap={cap} peak_depth={peak} final_depth={queue.Count} dropped_oldest={totalDropped}");
    }
}

// ── 3. AclResult.Unresolved never widens under concurrent identity churn ─────

public class Stress_AclNeverWidens : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteIdentityStore _store;

    public Stress_AclNeverWidens()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "seismic-acl-" + Guid.NewGuid().ToString("N") + ".db");
        _store = new SqliteIdentityStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task ConcurrentFingerprintChurn_UnresolvedNeverProducesRealEveryoneGrant()
    {
        // fallback=tenant is the DANGEROUS policy: an unresolved principal set
        // would otherwise widen to "everyone in tenant". The mapper must flag
        // any fallback-everyone result Unresolved so the pipeline refuses it.
        var mapper = new AclMapper(_store, fallbackAcl: "tenant", tenantId: "tenant-guid");
        // A small principal set the item carries. The churn driver flips the
        // WHOLE set between fully-mapped and fully-unmapped so the dangerous
        // "nothing maps → would widen to everyone" state is hit constantly.
        var principals = new[] { "u0", "u1", "u2" };
        foreach (var p in principals)
            _store.UpsertPrincipal(new PrincipalMapping(p, "user", $"{p}@c.com", $"entra-{p}", null));

        var itemPerms = principals
            .Select(p => new SeismicPermission { PrincipalId = p, PrincipalType = "user", Email = $"{p}@c.com" })
            .ToList();

        long resolves = 0, unresolved = 0, everyoneGrants = 0, blockedWidenings = 0, realUserGrants = 0;
        var invariantViolations = new ConcurrentBag<string>();
        var stop = false;

        // Churn driver: alternately map-all / unmap-all the principal set for the
        // whole duration of the readers (paced so SQLite fsync stays sane).
        var writer = Task.Run(() =>
        {
            var mapped = false;
            while (!Volatile.Read(ref stop))
            {
                mapped = !mapped;
                foreach (var p in principals)
                    _store.UpsertPrincipal(new PrincipalMapping(
                        p, "user", $"{p}@c.com", mapped ? $"entra-{p}" : null, null));
                Thread.Sleep(1);
            }
        });

        // Readers: resolve the SAME principal-carrying item repeatedly and check
        // the never-widen invariant on every single result.
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var r = 0; r < 5_000; r++)
            {
                var acl = mapper.Resolve(itemPerms, teamsitePermissions: null);
                Interlocked.Increment(ref resolves);
                var hasEveryone = acl.Entries.Any(e => e.Type == "everyone");
                if (hasEveryone)
                {
                    Interlocked.Increment(ref everyoneGrants);
                    // INVARIANT: a fallback everyone-grant over a principal-carrying
                    // source MUST be flagged Unresolved (never a real resolution).
                    if (!acl.Unresolved)
                        invariantViolations.Add($"everyone-grant NOT flagged Unresolved (entries={acl.Entries.Count})");
                }
                if (acl.Unresolved)
                {
                    Interlocked.Increment(ref unresolved);
                    // Simulate the pipeline decision: an Unresolved result is refused,
                    // so the existing ACL is never widened.
                    Interlocked.Increment(ref blockedWidenings);
                }
                else if (acl.Entries.Count > 0 && acl.Entries.All(e => e.Type == "user"))
                {
                    Interlocked.Increment(ref realUserGrants);
                }
                // A non-Unresolved result must NEVER contain an everyone entry.
                if (!acl.Unresolved && hasEveryone)
                    invariantViolations.Add("non-unresolved everyone leak");
            }
        })).ToList();

        await Task.WhenAll(readers);
        Volatile.Write(ref stop, true);
        await writer;

        Assert.Empty(invariantViolations);
        // Prove the dangerous path was actually exercised, not vacuously passed.
        Assert.True(unresolved > 0, "churn never produced an unresolved state — test is vacuous");
        Assert.True(everyoneGrants > 0, "fallback everyone-grant path never exercised");
        Assert.Equal(everyoneGrants, blockedWidenings);  // every everyone-grant was refused
        StressLog.Record("ACL-NO-WIDEN",
            $"resolves={resolves} unresolved={unresolved} everyone_grants={everyoneGrants} " +
            $"widenings_blocked={blockedWidenings} real_user_grants={realUserGrants} violations={invariantViolations.Count}");
    }
}

// ── 4. IdentityStore _gate: no races / lost updates ──────────────────────────

public class Stress_IdentityStoreGate : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteIdentityStore _store;

    public Stress_IdentityStoreGate()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "seismic-gate-" + Guid.NewGuid().ToString("N") + ".db");
        _store = new SqliteIdentityStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task ConcurrentReadWrite_NoCorruption_NoLostUpdates()
    {
        const int keys = 120;
        const int versions = 25;
        const int writersPerKey = 1;  // one dedicated writer per key → deterministic final value
        var errors = new ConcurrentBag<string>();
        long ops = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Dedicated writers: each owns a disjoint key range, writing monotonically
        // increasing versions — the final read MUST equal the last version written.
        var writeTasks = Enumerable.Range(0, keys).Select(k => Task.Run(() =>
        {
            try
            {
                for (var v = 1; v <= versions; v++)
                {
                    _store.UpsertPrincipal(new PrincipalMapping($"k{k}", "user", $"k{k}@c.com", $"entra-{k}-{v}", $"v{v}"));
                    _store.UpsertTrackedItem(new TrackedItem($"item-{k}", $"ver{v}", $"ts{k % 8}", null, DateTime.UtcNow, "ingested"));
                    Interlocked.Add(ref ops, 2);
                }
            }
            catch (Exception ex) { errors.Add(ex.ToString()); }
        }));

        // Concurrent readers hammering the same store (reentrant lock path).
        var readTasks = Enumerable.Range(0, 16).Select(readerId => Task.Run(() =>
        {
            var rng = new Random(readerId + 1);
            try
            {
                for (var i = 0; i < 4_000; i++)
                {
                    var k = rng.Next(keys);
                    _ = _store.GetEntraObjectId($"k{k}");
                    _ = _store.GetTrackedItem($"item-{k}");
                    _ = _store.CountMappedPrincipals();
                    Interlocked.Add(ref ops, 3);
                }
            }
            catch (Exception ex) { errors.Add(ex.ToString()); }
        }));

        await Task.WhenAll(writeTasks.Concat(readTasks));
        sw.Stop();

        Assert.Empty(errors);
        // No lost updates: every key holds its final (v=50) write.
        for (var k = 0; k < keys; k++)
        {
            Assert.Equal($"entra-{k}-{versions}", _store.GetEntraObjectId($"k{k}"));
            Assert.Equal($"ver{versions}", _store.GetTrackedItem($"item-{k}")!.VersionId);
        }
        Assert.Equal(keys, _store.CountMappedPrincipals());
        StressLog.Record("ID-STORE-GATE",
            $"keys={keys} writers={keys * writersPerKey} readers=16 total_ops={ops} " +
            $"ops/s={ops / sw.Elapsed.TotalSeconds:F0} errors={errors.Count} lost_updates=0");
    }
}

// ── 5. No-MNE exclusion holds at scale ───────────────────────────────────────

public class Stress_ExclusionAtScale
{
    [Fact]
    public async Task LargeCrawl_ExcludedItemsAreNeverEmitted()
    {
        var rules = new ExclusionRules
        {
            ExcludedFlags = { "MNE" },
            FlagProperties = { "complianceFlag" },
            RestrictedTeamsiteIds = { "ts-restricted" },
        };
        using var harness = new PipelineHarness(exclusions: rules, fallbackAcl: "tenant");
        harness.AddTeamsite("ts-open", "Open Library");
        harness.AddTeamsite("ts-restricted", "Deal Room", restricted: false);  // excluded by id

        const int perTeamsite = 1000;
        var excludedIds = new HashSet<string>(StringComparer.Ordinal);
        // Open teamsite: every 3rd item carries the MNE flag.
        for (var i = 0; i < perTeamsite; i++)
        {
            var mne = i % 3 == 0;
            var id = $"open-{i}";
            if (mne)
                excludedIds.Add(id);
            harness.AddContent(TestContent.Make(id, teamsiteId: "ts-open",
                properties: mne
                    ? new List<SeismicProperty> { new() { Name = "complianceFlag", Value = "MNE" } }
                    : new List<SeismicProperty>()));
        }
        // Restricted teamsite: EVERY item must be excluded.
        for (var i = 0; i < perTeamsite; i++)
        {
            var id = $"rstr-{i}";
            excludedIds.Add(id);
            harness.AddContent(TestContent.Make(id, teamsiteId: "ts-restricted"));
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ok = await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem");
        sw.Stop();
        Assert.True(ok);

        var put = harness.PutItemIds().ToHashSet(StringComparer.Ordinal);
        // THE compliance invariant: not one excluded id ever reached Graph.
        var leaked = put.Intersect(excludedIds).ToList();
        Assert.Empty(leaked);
        // And every NON-excluded open item WAS emitted.
        var expectedEmitted = Enumerable.Range(0, perTeamsite)
            .Where(i => i % 3 != 0).Select(i => $"open-{i}").ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expectedEmitted, put);

        // Concurrent stateless-filter stress on the same rules.
        var filter = new ExclusionFilter(rules);
        var mneContent = TestContent.Make("x", teamsiteId: "ts-open",
            properties: new List<SeismicProperty> { new() { Name = "complianceFlag", Value = "MNE" } });
        long evals = 0; var filterViolations = 0;
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100_000; i++)
            {
                if (!filter.Evaluate(mneContent).Excluded)
                    Interlocked.Increment(ref filterViolations);
                Interlocked.Increment(ref evals);
            }
        })));
        Assert.Equal(0, filterViolations);

        StressLog.Record("NO-MNE-SCALE",
            $"items={perTeamsite * 2} excluded_expected={excludedIds.Count} emitted={put.Count} " +
            $"excluded_leaked={leaked.Count} crawl={sw.Elapsed.TotalSeconds:F2}s " +
            $"concurrent_filter_evals={evals} filter_violations={filterViolations}");
    }
}

// ── 6. Circuit breaker releases its half-open slot on every exit path ────────

public class Stress_BreakerNoSlotLeak
{
    [Fact]
    public async Task ConcurrentProbeStorm_NeverLeaksAHalfOpenSlot()
    {
        var clock = new FakeClock();
        const int trials = 3;
        var b = new CircuitBreaker("stress", new CircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = 1,
            OpenDuration = TimeSpan.FromSeconds(10),
            Window = TimeSpan.FromSeconds(60),
            HalfOpenTrials = trials,
        }, critical: true, clock.Func);

        long probes = 0, admitted = 0, shortCircuited = 0;
        var maxInFlight = 0;

        // Many cycles: trip → half-open → burst of concurrent probes (mixed
        // success / tripping-fail / caller-cancel) → observe the slot count.
        for (var cycle = 0; cycle < 60; cycle++)
        {
            // Force open, then advance into the half-open probe window.
            b.ForceOpenForTests();
            clock.Advance(TimeSpan.FromSeconds(11));
            Assert.Equal(CircuitState.HalfOpen, b.State);

            var gate = new TaskCompletionSource();
            var burst = Enumerable.Range(0, 24).Select(i => Task.Run(async () =>
            {
                Interlocked.Increment(ref probes);
                var mode = i % 3;
                try
                {
                    await b.ExecuteAsync<int>(async ct =>
                    {
                        Interlocked.Increment(ref admitted);
                        var snap = b.HalfOpenInFlight;
                        InterlockedMax(ref maxInFlight, snap);
                        await gate.Task;  // hold the slot until released
                        return mode switch
                        {
                            0 => 1,                                            // success
                            1 => throw new GraphApiError(503, "outage"),        // tripping
                            _ => throw new OperationCanceledException(ct),      // caller cancel path
                        };
                    }, GraphClient.IsTrippingFailure);
                }
                catch (CircuitOpenException) { Interlocked.Increment(ref shortCircuited); }
                catch (GraphApiError) { }
                catch (OperationCanceledException) { }
            })).ToList();

            // Let the admitted probes park on the gate, then release them together.
            await Task.Delay(5);
            gate.SetResult();
            await Task.WhenAll(burst);

            // INVARIANT: after every probe exits, no half-open permit is leaked.
            Assert.Equal(0, b.HalfOpenInFlight);
            // And never more than `trials` probes were admitted concurrently.
            Assert.True(maxInFlight <= trials, $"admitted {maxInFlight} > trials {trials}");
        }

        // The breaker still works: force open, half-open, one success closes it.
        b.ForceOpenForTests();
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(CircuitState.HalfOpen, b.State);
        await b.ExecuteAsync(_ => Task.FromResult(0), GraphClient.IsTrippingFailure);
        Assert.Equal(CircuitState.Closed, b.State);
        Assert.Equal(0, b.HalfOpenInFlight);

        StressLog.Record("BREAKER-SLOT",
            $"cycles=60 probes={probes} admitted={admitted} short_circuited={shortCircuited} " +
            $"trials={trials} max_concurrent_in_flight={maxInFlight} final_in_flight=0 trips={b.TripCount} resets={b.ResetCount}");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int snap;
        while (value > (snap = Volatile.Read(ref target)))
            Interlocked.CompareExchange(ref target, value, snap);
    }
}

// ── 7. Dead-letter correct under concurrent producers; retry dedups exactly ──

public class Stress_DeadLetterConcurrency
{
    [Fact]
    public async Task ConcurrentProducers_EveryRecordIsIntactJsonl_NoTornLines()
    {
        using var state = new TempStateDir();
        const string connectorId = "StressDL";
        var path = SyncState.FailedRecordsPath(connectorId);
        const int producers = 24;
        const int perProducer = 500;

        await Task.WhenAll(Enumerable.Range(0, producers).Select(p => Task.Run(() =>
        {
            for (var i = 0; i < perProducer; i++)
            {
                var id = $"p{p}-i{i}";
                SyncState.AppendFailedRecords(
                    path,
                    new List<(string, string)> { (id, $"boom {p}/{i} with, commas \"quotes\" and \n newlines") },
                    "ContentItem",
                    requestBodies: new Dictionary<string, JsonNode?> { [id] = new JsonObject { ["id"] = id } });
            }
        })));

        // ReadFailedRecords parses every line — a torn/interleaved line throws.
        var records = SyncState.ReadFailedRecords(connectorId);
        Assert.Equal(producers * perProducer, records.Count);
        var ids = records.Select(r => r["item_id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(producers * perProducer, ids.Count);  // all distinct, none lost

        // retry-failed dedup contract: duplicate item_ids drain exactly once.
        var withDupes = records.Concat(records.Take(1000)).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var drained = withDupes.Count(r => seen.Add(r["item_id"]!.GetValue<string>()));
        Assert.Equal(producers * perProducer, drained);

        StressLog.Record("DEAD-LETTER",
            $"producers={producers} records_written={records.Count} distinct_ids={ids.Count} " +
            $"torn_lines=0 retry_dedup_input={withDupes.Count} retry_drained_exactly={drained}");
    }
}

// ── 8. $batch respects ≤20 and backs off honoring 429 Retry-After ────────────

public class Stress_BatchCapAndBackoff
{
    private static GraphClient Client(FakeHttpHandler handler) =>
        new(TestConfig.Build().Graph, handler)
        {
            OverrideAccessToken = "token",
            DelayAsync = (_, _) => Task.CompletedTask,  // record the wait, don't sleep
        };

    [Fact]
    public async Task Batch_NeverExceeds20_AndHonorsRetryAfterOn429()
    {
        var maxEnvelopeSize = 0;
        var round = 0;
        var handler = new FakeHttpHandler();
        handler.When(HttpMethod.Post, "/$batch", (_, body) =>
        {
            var reqs = JsonNode.Parse(body!)!["requests"]!.AsArray();
            lock (handler) maxEnvelopeSize = Math.Max(maxEnvelopeSize, reqs.Count);
            var thisRound = Interlocked.Increment(ref round);
            var responses = new JsonArray();
            foreach (var r in reqs)
            {
                var id = r!["id"]!.GetValue<string>();
                // First round: throttle everything with Retry-After: 7. Then succeed.
                if (thisRound == 1)
                    responses.Add(new JsonObject
                    {
                        ["id"] = id,
                        ["status"] = 429,
                        ["headers"] = new JsonObject { ["Retry-After"] = "7" },
                    });
                else
                    responses.Add(new JsonObject { ["id"] = id, ["status"] = 200 });
            }
            return FakeHttpHandler.Json(HttpStatusCode.OK, new JsonObject { ["responses"] = responses }.ToJsonString());
        });
        var client = Client(handler);

        // Exactly 20 items — the hard cap.
        var items = Enumerable.Range(0, 20)
            .Select(i => ($"c{i}", (JsonNode)new JsonObject { ["id"] = $"c{i}" }))
            .ToList();
        var outcome = await client.PutExternalItemsBatchWithRetryAsync("conn", items);

        Assert.True(outcome.SawThrottle);
        Assert.All(outcome.Results, r => Assert.True(r.Success));  // all recovered after backoff
        Assert.True(maxEnvelopeSize <= 20, $"envelope had {maxEnvelopeSize} > 20");
        Assert.Equal(20, maxEnvelopeSize);
        // The 429 Retry-After (7s) was honored as a floor on the inter-round wait.
        Assert.Contains(client.ObservedDelaysSeconds, w => w >= 7.0);

        // Over-cap input is rejected outright (never sends a >20 envelope).
        var overCap = Enumerable.Range(0, 21)
            .Select(i => ($"o{i}", (JsonNode)new JsonObject { ["id"] = $"o{i}" }))
            .ToList();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.PutExternalItemsBatchWithRetryAsync("conn", overCap));

        StressLog.Record("BATCH-CAP",
            $"items=20 max_envelope={maxEnvelopeSize} saw_throttle={outcome.SawThrottle} " +
            $"honored_retry_after={client.ObservedDelaysSeconds.Max():F1}s over_cap_rejected=true");
    }
}
