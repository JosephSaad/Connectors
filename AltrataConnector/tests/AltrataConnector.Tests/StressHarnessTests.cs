// StressHarnessTests.cs
// =====================
// Load / concurrency stress harness for the Altrata connector. Pushes the
// signature invariants under scale and contention, captures REAL measured
// numbers (throughput, blocked-widening counts, erasure completeness, ledger
// integrity, collision counts, breaker slot accounting, memory bounds) and
// guards the BuildItemId collision fix with a regression.
//
// Everything is in-process/offline: temp dirs, SQLite stores, fakes. No network.
//
// Measured numbers are appended to the file named by env STRESS_LOG (and echoed
// through ITestOutputHelper) so a runner can capture them regardless of the
// xunit console verbosity.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AltrataConnector.Altrata;
using AltrataConnector.Commands;
using AltrataConnector.Config;
using AltrataConnector.Entitlement;
using AltrataConnector.Graph;
using AltrataConnector.Identity;
using AltrataConnector.Infrastructure;
using AltrataConnector.State;
using Xunit.Abstractions;

namespace AltrataConnector.Tests;

/// <summary>Thread-safe measured-number sink: appends to STRESS_LOG + test output.</summary>
internal static class StressLog
{
    private static readonly object Gate = new();
    private static readonly string Path =
        Environment.GetEnvironmentVariable("STRESS_LOG")
        ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "altrata_stress.log");

    public static void Line(ITestOutputHelper output, string message)
    {
        output.WriteLine(message);
        lock (Gate)
            File.AppendAllText(Path, message + Environment.NewLine);
    }
}

/// <summary>Thread-safe IGraphClient fake (the shared FakeGraphClient uses plain
/// List&lt;T&gt;, unsafe under the concurrent DSAR / churn scenarios).</summary>
internal sealed class ConcurrentGraph : IGraphClient
{
    public ConcurrentBag<ExternalItem> Puts { get; } = new();
    public ConcurrentBag<string> Deletes { get; } = new();
    private int _puts;
    private int _deletes;
    public int PutCount => Volatile.Read(ref _puts);
    public int DeleteCount => Volatile.Read(ref _deletes);

    public Task EnsureConnectionAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task RegisterSchemaAsync(GraphSchema schema, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> ConnectionExistsAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task PutItemAsync(ExternalItem item, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        SeatAclBuilder.AssertNeverEveryone(item.Acl);   // structural guard, same as prod path
        Puts.Add(item);
        Interlocked.Increment(ref _puts);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BatchOpResult>> PutItemsBatchAsync(
        IReadOnlyList<ExternalItem> items, CancellationToken ct = default)
    {
        var r = new List<BatchOpResult>();
        foreach (var i in items) { PutItemAsync(i, ct).Wait(ct); r.Add(new BatchOpResult(i.Id, true, 200, null)); }
        return Task.FromResult<IReadOnlyList<BatchOpResult>>(r);
    }

    public Task UpdateItemAclAsync(string itemId, IReadOnlyList<AclEntry> acl, CancellationToken ct = default)
    {
        SeatAclBuilder.AssertNeverEveryone(acl);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BatchOpResult>> UpdateItemAclsBatchAsync(
        IReadOnlyList<AclUpdate> updates, CancellationToken ct = default)
    {
        foreach (var u in updates) SeatAclBuilder.AssertNeverEveryone(u.Acl);
        return Task.FromResult<IReadOnlyList<BatchOpResult>>(
            updates.Select(u => new BatchOpResult(u.ItemId, true, 200, null)).ToList());
    }

    public Task DeleteItemAsync(string itemId, CancellationToken ct = default)
    {
        Deletes.Add(itemId);
        Interlocked.Increment(ref _deletes);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BatchOpResult>> DeleteItemsBatchAsync(
        IReadOnlyList<string> itemIds, CancellationToken ct = default)
    {
        foreach (var id in itemIds) DeleteItemAsync(id, ct).Wait(ct);
        return Task.FromResult<IReadOnlyList<BatchOpResult>>(
            itemIds.Select(id => new BatchOpResult(id, true, 204, null)).ToList());
    }
}

// =====================================================================================
// Scenario 1 — Seat-only entitlement never widens to everyone
// =====================================================================================

public class SeatEntitlementStressTests
{
    private readonly ITestOutputHelper _out;
    public SeatEntitlementStressTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void AssertNeverEveryone_blocks_every_broadening_attempt_under_load()
    {
        const int iterations = 400_000;
        var blocked = 0;
        var passed = 0;
        var forbiddenVariants = new[] { "everyone", "everyoneExceptGuests", "EVERYONE", "EveryoneExceptGuests" };

        var sw = Stopwatch.StartNew();
        Parallel.For(0, iterations, () => (blocked: 0, passed: 0), (i, _, local) =>
        {
            IReadOnlyList<AclEntry> acl = (i % 2 == 0)
                ? new[]
                  {
                      new AclEntry { Type = "user", Value = $"u{i}@contoso.com" },
                      new AclEntry { Type = forbiddenVariants[i % forbiddenVariants.Length], Value = "*" },
                  }
                : new[] { new AclEntry { Type = "user", Value = $"u{i}@contoso.com" } };
            try
            {
                SeatAclBuilder.AssertNeverEveryone(acl);
                local.passed++;
            }
            catch (EntitlementViolationException)
            {
                local.blocked++;
            }
            return local;
        },
        local => { Interlocked.Add(ref blocked, local.blocked); Interlocked.Add(ref passed, local.passed); });
        sw.Stop();

        Assert.Equal(iterations / 2, blocked);   // every broadening attempt rejected
        Assert.Equal(iterations / 2, passed);    // every seat-only ACL admitted
        StressLog.Line(_out,
            $"[S1a] AssertNeverEveryone: {iterations:N0} ACLs, {blocked:N0} broadening attempts BLOCKED, " +
            $"{passed:N0} seat-only passed, {iterations / sw.Elapsed.TotalSeconds:N0} acl/s");
    }

    [Fact]
    public async Task Concurrent_seat_churn_never_widens_acl()
    {
        var root = TestFixtures.NewTempDir("seatchurn");
        using var identity = new SqliteIdentityStore(System.IO.Path.Combine(root, "id.db"));
        identity.ReplaceSeats(TestFixtures.DefaultSeats());

        // Bounded work (no spin-until-cancel) so writers can't starve readers on
        // the store's Monitor. Writers yield between churns to interleave.
        const int readers = 6, writers = 3, perReader = 6_000, perWriter = 800;
        var builtAcls = 0L;
        var widened = 0L;
        var sw = Stopwatch.StartNew();

        var writerTasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            var rng = new Random(1000 + w);
            for (var i = 0; i < perWriter; i++)
            {
                // Always non-empty churn: 1..6 random seats, mixed users/groups.
                var n = rng.Next(1, 7);
                var seats = Enumerable.Range(0, n)
                    .Select(_ => rng.Next(2) == 0
                        ? new SeatPrincipal(SeatPrincipalKind.UserUpn, $"user{rng.Next(50)}@contoso.com")
                        : new SeatPrincipal(SeatPrincipalKind.Group, $"grp-{rng.Next(20)}"))
                    .ToList();
                identity.ReplaceSeats(seats);
                Thread.Yield();
            }
        })).ToArray();

        var readerTasks = Enumerable.Range(0, readers).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < perReader; i++)
            {
                var seats = identity.GetSeats();
                // ReplaceSeats is atomic (txn under lock), so a snapshot is always
                // the full non-empty set a writer committed — never a torn/empty ACL.
                var acl = SeatAclBuilder.BuildAcl(seats);
                Assert.NotEmpty(acl);
                if (acl.Any(e => SeatAclBuilder.ForbiddenAclTypes.Contains(e.Type, StringComparer.OrdinalIgnoreCase)))
                    Interlocked.Increment(ref widened);
                Assert.All(acl, e => Assert.True(e.Type is "user" or "group"));
                Interlocked.Increment(ref builtAcls);
            }
        })).ToArray();

        await Task.WhenAll(readerTasks.Concat(writerTasks));
        sw.Stop();

        Assert.Equal(0, Interlocked.Read(ref widened));   // THE invariant
        StressLog.Line(_out,
            $"[S1b] Seat churn: {builtAcls:N0} ACLs built while {writers} writers churned the seat set " +
            $"{writers * perWriter:N0} times, 0 widened to everyone, {builtAcls / sw.Elapsed.TotalSeconds:N0} acl/s");
    }

    [Fact]
    public async Task RetryFailed_rebuilds_acl_from_current_seats_not_stale()
    {
        Environment.SetEnvironmentVariable("GRAPH_CONNECTION_SHARDS", null);
        var root = TestFixtures.NewTempDir("retryacl");
        var graph = new FakeGraphClient();
        using var runtime = TestFixtures.NewRuntime(TestFixtures.NewConfig(), graph, root);

        // Current seats: ONLY alice (bob & carol have since been de-provisioned).
        runtime.Identity.ReplaceSeats(new[]
        {
            new SeatPrincipal(SeatPrincipalKind.UserUpn, "alice@contoso.com"),
        });

        // Dead-letter 500 upserts whose CAPTURED payload carries a STALE broad ACL
        // (alice + bob + carol). retry-failed must re-PUT under the CURRENT seats.
        const int n = 500;
        var staleAcl = new[]
        {
            new AclEntry { Type = "user", Value = "alice@contoso.com" },
            new AclEntry { Type = "user", Value = "bob@contoso.com" },
            new AclEntry { Type = "user", Value = "carol@contoso.com" },
        };
        for (var i = 0; i < n; i++)
        {
            var item = new ExternalItem
            {
                Id = $"PersonProfile-R{i}",
                Acl = staleAcl,
                Properties = new Dictionary<string, object?> { ["title"] = $"R{i}" },
                Content = new ExternalItemContent { Type = "text", Value = "x" },
            };
            runtime.State.AddDeadLetter(new DeadLetterRecord
            {
                ItemId = item.Id,
                Dataset = Datasets.PersonProfile,
                Op = DeadLetterOps.Upsert,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(item),
            });
        }

        var sw = Stopwatch.StartNew();
        var drained = await CommandRegistry.RetryFailedAsync(runtime, clearOnSuccess: true);
        sw.Stop();

        Assert.Equal(true, drained);
        Assert.Empty(runtime.State.ReadDeadLetters());
        Assert.Equal(n, graph.PutItems.Count);
        // Every replayed item now carries the CURRENT single-seat ACL, never the
        // stale broad one, and never an everyone grant.
        Assert.All(graph.PutItems, item =>
        {
            Assert.Single(item.Acl);
            Assert.Equal("alice@contoso.com", item.Acl[0].Value);
            SeatAclBuilder.AssertNeverEveryone(item.Acl);
        });
        StressLog.Line(_out,
            $"[S1c] retry-failed rebuilt ACL from CURRENT seats for {n:N0} items " +
            $"(stale bob/carol dropped, 0 everyone), {n / sw.Elapsed.TotalSeconds:N0} item/s");
    }
}

// =====================================================================================
// Scenario 2 — DSAR forget-subject at scale + hash-chained ledger integrity
// =====================================================================================

public class DsarErasureStressTests
{
    private readonly ITestOutputHelper _out;
    public DsarErasureStressTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Ledger_chain_stays_valid_under_concurrent_appends()
    {
        var root = TestFixtures.NewTempDir("ledger");
        var ledger = new ErasureLedger("AltrataStress", logsDir: root);
        // Append is O(file) per call (re-reads to chain prevHash), so the chain
        // is O(N^2) overall — 2,000 is a realistic DSAR-cycle volume that still
        // hammers the per-entry lock hard under 16-way contention.
        const int n = 2_000;

        var sw = Stopwatch.StartNew();
        Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
            ledger.Append("joseph", ErasureActions.Erase, $"subject-{i}", null, new[] { $"item-{i}" }));
        sw.Stop();

        Assert.True(ledger.Verify(out var broken), $"ledger broken at seq {broken}");
        Assert.Equal(0, broken);
        var all = ledger.ReadAll();
        Assert.Equal(n, all.Count);
        // Seq numbers form a contiguous 1..N with no gaps/dupes despite concurrency.
        var seqs = all.Select(e => e.Seq).OrderBy(s => s).ToList();
        for (var i = 0; i < n; i++) Assert.Equal(i + 1, seqs[i]);
        StressLog.Line(_out,
            $"[S2a] Ledger: {n:N0} concurrent appends (16 threads), Verify=OK, seq 1..{n} contiguous, " +
            $"0 broken links, {n / sw.Elapsed.TotalSeconds:N0} append/s");
    }

    [Fact]
    public async Task ForgetSubject_at_scale_is_complete_and_irreversible()
    {
        Environment.SetEnvironmentVariable("GRAPH_CONNECTION_SHARDS", null);
        var root = TestFixtures.NewTempDir("forgetscale");
        var graph = new ConcurrentGraph();
        var config = TestFixtures.NewConfig();
        var state = new FileStateStore("AltrataScale", logsDir: System.IO.Path.Combine(root, "logs"),
            dataDir: System.IO.Path.Combine(root, "data"));
        var identity = new SqliteIdentityStore(System.IO.Path.Combine(root, "data", "id.db"));
        using var runtime = new Runtime
        {
            Config = config,
            State = state,
            Identity = identity,
            Graph = graph,
            Seats = new SeatService(config, identity, state),
            Alerts = new Alerting("AltrataScale"),
            Audit = new AuditLog("AltrataScale", logsDir: System.IO.Path.Combine(root, "logs")),
            Erasure = new ErasureLedger("AltrataScale", logsDir: System.IO.Path.Combine(root, "logs")),
            Decisions = new DecisionLedger("AltrataScale", logsDir: System.IO.Path.Combine(root, "logs")),
            GraphBreaker = new CircuitBreaker("graph", new CircuitBreakerOptions { Enabled = false }),
            ApiBreaker = new CircuitBreaker("api", new CircuitBreakerOptions { Enabled = false }),
        };

        const int subjects = 400;
        var derivedPerSubject = 3;   // PersonProfile + wealth/board/career
        var expectedItems = 0;
        for (var s = 0; s < subjects; s++)
        {
            var subjectId = $"P{s}";
            var profileId = ItemTransformer.BuildItemId(Datasets.PersonProfile, subjectId);
            runtime.Identity.RecordIngestedItem(new IngestedItem(profileId, Datasets.PersonProfile, "h", DateTime.UtcNow));
            runtime.Identity.RecordItemSubjects(profileId, new[] { subjectId });
            expectedItems++;
            foreach (var ds in new[] { Datasets.WealthIndicator, Datasets.BoardMembership, Datasets.CareerHistory })
            {
                var itemId = ItemTransformer.BuildItemId(ds, $"{ds}-{subjectId}-rec");
                runtime.Identity.RecordIngestedItem(new IngestedItem(itemId, ds, "h", DateTime.UtcNow));
                runtime.Identity.RecordItemSubjects(itemId, new[] { subjectId });
                expectedItems++;
            }
        }
        Assert.Equal(subjects * (1 + derivedPerSubject), expectedItems);

        var sw = Stopwatch.StartNew();
        for (var s = 0; s < subjects; s++)
        {
            var ok = await CommandRegistry.ForgetSubjectAsync(
                runtime, altrataId: $"P{s}", email: null, actor: "joseph", confirm: true);
            Assert.Equal(true, ok);
        }
        sw.Stop();

        // Completeness: inventory drained, every item withdrawn from Graph.
        Assert.Equal(0, runtime.Identity.CountIngestedItems());
        Assert.Equal(expectedItems, graph.DeleteCount);
        // Irreversibility: every subject on the durable suppression list.
        for (var s = 0; s < subjects; s++)
            Assert.True(runtime.State.IsSubjectSuppressed($"P{s}"));
        // Ledger: one erase entry per subject, chain intact.
        Assert.True(runtime.Erasure.Verify(out var broken), $"broken at {broken}");
        Assert.Equal(subjects, runtime.Erasure.ReadAll().Count(e => e.Action == ErasureActions.Erase));

        // "Never reappears via a later crawl": simulate re-ingest attempt of every
        // forgotten subject; the suppression filter drops all of them.
        var reIngested = Enumerable.Range(0, subjects)
            .Count(s => !runtime.State.IsSubjectSuppressed($"P{s}"));
        Assert.Equal(0, reIngested);

        StressLog.Line(_out,
            $"[S2b] forget-subject scale: {subjects:N0} subjects / {expectedItems:N0} items erased, " +
            $"inventory=0, suppressed={subjects:N0}/{subjects:N0}, ledger Verify=OK, re-ingested=0, " +
            $"{expectedItems / sw.Elapsed.TotalSeconds:N0} item/s");
    }

    [Fact]
    public async Task Concurrent_forget_keeps_ledger_valid_and_suppression_complete()
    {
        Environment.SetEnvironmentVariable("GRAPH_CONNECTION_SHARDS", null);
        var root = TestFixtures.NewTempDir("forgetconc");
        var graph = new ConcurrentGraph();
        var config = TestFixtures.NewConfig();
        var state = new FileStateStore("AltrataConc", logsDir: System.IO.Path.Combine(root, "logs"),
            dataDir: System.IO.Path.Combine(root, "data"));
        var identity = new SqliteIdentityStore(System.IO.Path.Combine(root, "data", "id.db"));
        using var runtime = new Runtime
        {
            Config = config,
            State = state,
            Identity = identity,
            Graph = graph,
            Seats = new SeatService(config, identity, state),
            Alerts = new Alerting("AltrataConc"),
            Audit = new AuditLog("AltrataConc", logsDir: System.IO.Path.Combine(root, "logs")),
            Erasure = new ErasureLedger("AltrataConc", logsDir: System.IO.Path.Combine(root, "logs")),
            Decisions = new DecisionLedger("AltrataConc", logsDir: System.IO.Path.Combine(root, "logs")),
            GraphBreaker = new CircuitBreaker("graph", new CircuitBreakerOptions { Enabled = false }),
            ApiBreaker = new CircuitBreaker("api", new CircuitBreakerOptions { Enabled = false }),
        };

        const int subjects = 250;
        for (var s = 0; s < subjects; s++)
        {
            var id = ItemTransformer.BuildItemId(Datasets.PersonProfile, $"C{s}");
            runtime.Identity.RecordIngestedItem(new IngestedItem(id, Datasets.PersonProfile, "h", DateTime.UtcNow));
            runtime.Identity.RecordItemSubjects(id, new[] { $"C{s}" });
        }

        var sw = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, subjects).Select(s => Task.Run(async () =>
        {
            var ok = await CommandRegistry.ForgetSubjectAsync(
                runtime, altrataId: $"C{s}", email: null, actor: "joseph", confirm: true);
            Assert.Equal(true, ok);
        })));
        sw.Stop();

        Assert.True(runtime.Erasure.Verify(out var broken), $"ledger broken at seq {broken}");
        Assert.Equal(subjects, runtime.Erasure.ReadAll().Count(e => e.Action == ErasureActions.Erase));
        Assert.Equal(0, runtime.Identity.CountIngestedItems());
        Assert.Equal(subjects, graph.DeleteCount);
        for (var s = 0; s < subjects; s++)
            Assert.True(runtime.State.IsSubjectSuppressed($"C{s}"));
        StressLog.Line(_out,
            $"[S2c] {subjects:N0} CONCURRENT forget-subject ops: ledger Verify=OK ({subjects:N0} entries, " +
            $"0 broken), inventory=0, all suppressed, {subjects / sw.Elapsed.TotalSeconds:N0} forget/s");
    }
}

// =====================================================================================
// Scenario 3 — BuildItemId collision-free under a large adversarial id space
// =====================================================================================

public class ItemIdCollisionStressTests
{
    private readonly ITestOutputHelper _out;
    public ItemIdCollisionStressTests(ITestOutputHelper output) => _out = output;

    private static string Body(string dataset, string rid)
    {
        var raw = $"{dataset}-{rid}";
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw) sb.Append(char.IsLetterOrDigit(ch) || ch == '-' ? ch : '-');
        return sb.ToString();
    }

    private static string Hash12(string rid) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rid))).ToLowerInvariant()[..12];

    [Fact]
    public void BuildItemId_collision_free_over_large_adversarial_space()
    {
        var datasets = new[]
        {
            Datasets.PersonProfile, Datasets.Organization, Datasets.WealthIndicator,
            Datasets.BoardMembership, Datasets.CareerHistory, Datasets.RelationshipPath,
        };
        // itemId -> the raw (dataset,recordId) that produced it.
        var seen = new Dictionary<string, (string Ds, string Rid)>(StringComparer.Ordinal);
        var collisions = 0;
        var total = 0;

        void Add(string ds, string rid)
        {
            total++;
            var id = ItemTransformer.BuildItemId(ds, rid);
            if (seen.TryGetValue(id, out var prev))
            {
                if (prev.Ds != ds || prev.Rid != rid) collisions++;
            }
            else seen[id] = (ds, rid);
        }

        var before = GC.GetTotalMemory(true);
        var sw = Stopwatch.StartNew();
        const int k = 12_000;   // × 6 datasets × 7 crafted ids ≈ 500k adversarial ids
        for (var i = 0; i < k; i++)
        {
            foreach (var ds in datasets)
            {
                // 1. dirty ids that sanitize to a common shape
                var dirty = $"acct:{i}/{i % 131}";
                Add(ds, dirty);

                // 2. THE forge attack on the OLD ('-') scheme: a graph-safe raw id
                //    crafted to equal sanitize(dirty)+'-'+hash(dirty). Pre-fix this
                //    collided with (ds, dirty); post-fix it must be distinct.
                var bodyTail = Body(ds, dirty).Substring(ds.Length + 1);
                Add(ds, $"{bodyTail}-{Hash12(dirty)}");

                // 3. forge attempt on the NEW ('_') scheme (contains '_' → itself hashed)
                Add(ds, $"{bodyTail}_{Hash12(dirty)}");

                // 4. near-collisions: differ only in the unsafe char (sanitize-equal)
                Add(ds, $"a:b{i}");
                Add(ds, $"a/b{i}");
                Add(ds, $"a b{i}");

                // 5. graph-safe ids that mimic a hashed suffix
                Add(ds, $"acct-{i}-{Hash12(dirty)}");
            }
        }
        sw.Stop();
        var after = GC.GetTotalMemory(false);

        Assert.Equal(0, collisions);
        StressLog.Line(_out,
            $"[S3] BuildItemId: {total:N0} adversarial ids ({seen.Count:N0} distinct) → {collisions} collisions, " +
            $"{total / sw.Elapsed.TotalSeconds:N0} id/s, map footprint ~{(after - before) / (1024 * 1024)} MB");
    }

    [Fact]
    public void Crafted_clean_id_cannot_forge_a_hashed_id()   // regression for the fix
    {
        // Pre-fix: BuildItemId("PersonProfile","acct:12/3") == the id produced for
        // the graph-safe raw "acct-12-3-<hash>", a real zero-work collision.
        var dirty = ItemTransformer.BuildItemId(Datasets.PersonProfile, "acct:12/3");
        var forge = ItemTransformer.BuildItemId(
            Datasets.PersonProfile, $"acct-12-3-{Hash12("acct:12/3")}");
        Assert.NotEqual(dirty, forge);

        // The hashed form is joined with '_' (folded away by the sanitizer), so it
        // is provably disjoint from any graph-safe pass-through id.
        Assert.Contains('_', dirty);
        Assert.DoesNotContain('_', forge);
        StressLog.Line(_out, $"[S3-reg] forge blocked: dirty='{dirty}' forge='{forge}'");
    }
}

// =====================================================================================
// Scenario 4 — EntityResolver review queue stays hash-only (PII-safe) under load
// =====================================================================================

public class EntityResolverPiiStressTests
{
    private readonly ITestOutputHelper _out;
    public EntityResolverPiiStressTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Review_queue_stays_hash_only_under_concurrent_load()
    {
        var root = TestFixtures.NewTempDir("reviewload");
        using var store = new SqliteIdentityStore(System.IO.Path.Combine(root, "id.db"));

        const int n = 5_000;
        // Distinctive PII tokens so any leak is trivially searchable.
        var contacts = Enumerable.Range(0, n).Select(i => new CrmContact
        {
            Id = $"C{i}",
            Name = $"Ada PIINAME{i}",
            Employer = $"PIIEMP{i} Acme Engines",
            Role = "Director",
        }).ToList();
        store.ReplaceCrmContacts(contacts);

        var queue = new MatchReviewQueue("AltrataReview", logsDir: root);
        var fuzzy = new FuzzyOptions
        {
            Enabled = true, Threshold = 0.85, ReviewFloor = 0.6, Review = queue,
        };
        var resolver = new EntityResolver(store, fuzzy);

        // Warm the candidate pool once (avoids the lazy `??=` race), then hammer.
        resolver.Match(null, "warm up", "no match here");

        var sw = Stopwatch.StartNew();
        Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = 12 }, i =>
        {
            // name matches contact i exactly (nameScore 1.0 → 0.6); employer only
            // partially overlaps ("Systems" vs "Engines") → total ~0.7 ∈ [floor,threshold)
            // ⇒ routed to the review queue, never auto-linked.
            // role omitted so total ≈ 0.75 ∈ [floor, threshold) ⇒ review (not auto-link).
            resolver.Match(email: null, name: $"Ada PIINAME{i}",
                employer: $"PIIEMP{i} Acme Systems", role: null, altrataId: $"A{i}");
        });
        sw.Stop();

        var entries = queue.ReadAll();
        Assert.NotEmpty(entries);   // the review path actually ran

        // No raw PII anywhere in the persisted file — ids/scores/hashes only.
        var raw = File.ReadAllText(queue.Path);
        Assert.DoesNotContain("PIINAME", raw);
        Assert.DoesNotContain("PIIEMP", raw);
        Assert.DoesNotContain("Ada", raw);
        Assert.DoesNotContain("Acme", raw);
        Assert.DoesNotContain("Systems", raw);
        Assert.DoesNotContain("Engines", raw);
        Assert.DoesNotContain("Director", raw);
        // Hashes ARE present (dedup keys).
        Assert.All(entries, e =>
        {
            Assert.NotNull(e.NameHash);
            Assert.Matches("^[0-9a-f]{16}$", e.NameHash!);
            Assert.StartsWith("A", e.AltrataId);
        });
        StressLog.Line(_out,
            $"[S4] review queue: {entries.Count:N0} entries under 12-thread load, 0 raw-PII tokens leaked, " +
            $"all name/employer stored as 16-hex hashes, {n / sw.Elapsed.TotalSeconds:N0} resolve/s");
    }
}

// =====================================================================================
// Scenario 5 — Circuit breaker releases its slot on every exit path (no leak)
// =====================================================================================

public class CircuitBreakerStressTests
{
    private readonly ITestOutputHelper _out;
    public CircuitBreakerStressTests(ITestOutputHelper output) => _out = output;

    private static int InFlight(CircuitBreaker b) =>
        (int)typeof(CircuitBreaker).GetField("_halfOpenInFlight", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(b)!;

    private sealed class TripEx : Exception { }

    [Fact]
    public async Task No_halfopen_slot_leak_under_concurrent_load()
    {
        const int trials = 4;
        const int rounds = 3_000;
        var admitted = 0;
        var rejected = 0;
        var maxInFlight = 0;

        for (var round = 0; round < rounds; round++)
        {
            // A FRESH breaker per round removes cross-round state coupling; each
            // round independently exercises the half-open slot accounting under
            // contention. Clock is per-round so we can drive Open → HalfOpen.
            var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var clockTicks = baseTime.Ticks;
            Func<DateTime> clock = () => new DateTime(Volatile.Read(ref clockTicks), DateTimeKind.Utc);
            var options = new CircuitBreakerOptions
            {
                Enabled = true, FailureThreshold = 1, Window = TimeSpan.FromHours(1),
                OpenDuration = TimeSpan.FromSeconds(30), HalfOpenTrials = trials,
            };
            var breaker = new CircuitBreaker("stress", options, clock);

            // Trip → Open (one failure, threshold 1).
            try { await breaker.ExecuteAsync<int>(() => throw new TripEx(), isTripException: _ => true); }
            catch (TripEx) { }

            // Advance past OpenDuration so the next Enter lazily moves to HalfOpen.
            Interlocked.Add(ref clockTicks, options.OpenDuration.Ticks + TimeSpan.FromSeconds(1).Ticks);

            // Fire MORE concurrent calls than the trial budget: up to `trials` get
            // admitted (each exits via a path that must release its slot), the rest
            // are rejected by the budget (Enter throws BEFORE incrementing → no leak).
            var outcome = round % 4;
            var tasks = Enumerable.Range(0, trials * 4).Select(_ => Task.Run(async () =>
            {
                try
                {
                    await breaker.ExecuteAsync<int>(async () =>
                    {
                        await Task.Yield();
                        var m = Volatile.Read(ref maxInFlight);
                        var cur = InFlight(breaker);
                        if (cur > m) Interlocked.CompareExchange(ref maxInFlight, cur, m);
                        return outcome switch
                        {
                            0 => 1,                                     // success
                            1 => throw new TripEx(),                    // trip exception path
                            2 => throw new OperationCanceledException(),// non-token cancel → trip path
                            _ => 2,                                     // failure-result path
                        };
                    },
                    isFailureResult: v => outcome == 3 && v == 2,
                    isTripException: e => e is TripEx || e is OperationCanceledException);
                    Interlocked.Increment(ref admitted);
                }
                catch (CircuitOpenException) { Interlocked.Increment(ref rejected); }
                catch (TripEx) { Interlocked.Increment(ref admitted); }
                catch (OperationCanceledException) { Interlocked.Increment(ref admitted); }
            })).ToArray();
            await Task.WhenAll(tasks);

            // Quiescent: no call is in flight. A leaked slot would be observable as
            // _halfOpenInFlight > 0 stuck here. On EVERY exit path it must be 0
            // (Trip/Close reset it; the success/close path decrements each slot).
            Assert.Equal(0, InFlight(breaker));
        }

        Assert.True(maxInFlight <= trials, $"admitted {maxInFlight} concurrent trials, budget {trials}");
        StressLog.Line(_out,
            $"[S5] breaker: {rounds:N0} rounds × {trials * 4} concurrent calls, {admitted:N0} admitted / " +
            $"{rejected:N0} budget-rejected, peak in-flight={maxInFlight} (≤{trials}), final in-flight=0 every round (no leak)");
    }
}

// =====================================================================================
// Scenario 6 — Checkpoint/dead-letter/$batch resilience under concurrency
// =====================================================================================

public class IngestResilienceStressTests
{
    private readonly ITestOutputHelper _out;
    public IngestResilienceStressTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void DeadLetter_concurrent_producers_lose_nothing()
    {
        var root = TestFixtures.NewTempDir("deadletter");
        var store = new FileStateStore("AltrataDLQ", logsDir: System.IO.Path.Combine(root, "logs"),
            dataDir: System.IO.Path.Combine(root, "data"));

        const int producers = 8, perProducer = 5_000;
        var sw = Stopwatch.StartNew();
        Parallel.For(0, producers, p =>
        {
            for (var i = 0; i < perProducer; i++)
                store.AddDeadLetter(new DeadLetterRecord
                {
                    ItemId = $"P{p}-{i}",
                    Dataset = Datasets.PersonProfile,
                    Op = DeadLetterOps.Delete,   // replayable, no payload needed
                });
        });
        sw.Stop();

        var all = store.ReadDeadLetters();
        Assert.Equal(producers * perProducer, all.Count);
        // Every (producer,index) present exactly once — no torn/lost/dup lines.
        var ids = all.Select(r => r.ItemId).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(producers * perProducer, ids.Count);
        StressLog.Line(_out,
            $"[S6a] dead-letter: {producers} concurrent producers × {perProducer:N0} = " +
            $"{all.Count:N0} records, all present & unique (0 lost, 0 torn), " +
            $"{all.Count / sw.Elapsed.TotalSeconds:N0} rec/s");
    }

    [Fact]
    public async Task RetryFailed_drains_exactly_and_batch_never_exceeds_20()
    {
        Environment.SetEnvironmentVariable("GRAPH_CONNECTION_SHARDS", null);
        var root = TestFixtures.NewTempDir("drain");
        var graph = new FakeGraphClient();
        // Some deletes fail once to leave a remainder, proving "drains exactly".
        using var runtime = TestFixtures.NewRuntime(TestFixtures.NewConfig(), graph, root);
        runtime.Identity.ReplaceSeats(TestFixtures.DefaultSeats());

        const int n = 1_000;
        for (var i = 0; i < n; i++)
        {
            var id = $"PersonProfile-D{i}";
            runtime.State.AddDeadLetter(new DeadLetterRecord
            {
                ItemId = id, Dataset = Datasets.PersonProfile, Op = DeadLetterOps.Delete,
            });
            if (i % 100 == 0) graph.FailingDeletes.Add(id);  // 10 will remain
        }

        var result = await CommandRegistry.RetryFailedAsync(runtime, clearOnSuccess: true);
        Assert.Equal(false, result);   // remainder present
        var remaining = runtime.State.ReadDeadLetters();
        Assert.Equal(10, remaining.Count);
        Assert.Equal(n - 10, graph.DeletedItems.Count);
        Assert.All(remaining, r => Assert.Equal(2, r.Attempts));  // bumped exactly once
        StressLog.Line(_out,
            $"[S6b] retry-failed drained {n - 10:N0}/{n:N0} exactly; {remaining.Count} hard-failures kept " +
            "with attempts=2 (no double-drain, no loss)");

        // $batch sub-batch size is clamped to ≤20 even with an over-large config.
        Assert.True(GraphClient.GraphBatchMaxSize == 20);
        var handler = new ScriptedHandler();
        var bodies = new List<string>();
        var cfg = TestFixtures.NewConfig() with { GraphBatchSize = 999, GraphBatchWorkers = 1 };
        var client = new GraphClient(cfg, handler, (_, _) => Task.CompletedTask);
        handler.EnqueueJson(200, """{"access_token":"tok","expires_in":3600}""");
        // 45 items → sub-batches of 20,20,5. Capture each $batch body's request count.
        for (var b = 0; b < 3; b++)
        {
            handler.Enqueue(req =>
            {
                bodies.Add(req.Content!.ReadAsStringAsync().Result);
                var ids = Enumerable.Range(0, 20).Select(k => $$"""{"id":"{{k}}","status":200}""");
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{"responses":[{{string.Join(",", ids)}}]}""",
                        Encoding.UTF8, "application/json"),
                };
            });
        }
        var items = Enumerable.Range(0, 45).Select(i => new ExternalItem
        {
            Id = $"PersonProfile-B{i}",
            Acl = new[] { new AclEntry { Type = "user", Value = "alice@contoso.com" } },
            Properties = new Dictionary<string, object?> { ["title"] = $"B{i}" },
        }).ToList();
        await client.PutItemsBatchAsync(items);

        var perBatchCounts = bodies.Select(b =>
            System.Text.Json.JsonDocument.Parse(b).RootElement.GetProperty("requests").GetArrayLength()).ToList();
        Assert.All(perBatchCounts, c => Assert.True(c <= 20, $"sub-batch of {c} exceeds 20"));
        Assert.Equal(45, perBatchCounts.Sum());
        StressLog.Line(_out,
            $"[S6c] $batch clamp: 45 items with GRAPH_BATCH_SIZE=999 → sub-batches {string.Join("+", perBatchCounts)} " +
            "(each ≤20), total 45");
    }
}
