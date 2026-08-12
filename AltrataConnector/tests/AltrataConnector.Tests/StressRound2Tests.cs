// StressRound2Tests.cs
// ====================
// Round-2 stress harness. Round 1 (StressHarnessTests.cs) covered the
// invariants under raw scale; this round attacks NEW dimensions:
//
//   R2S1  BuildItemId under unicode/normalization adversaries — combining
//         marks, NFC/NFD pairs, homoglyphs, case-folding traps (U+212A),
//         zero-width/RTL controls, non-ASCII digits. The id space must stay
//         ASCII, disjoint and byte-stable across runs.
//   R2S2  DSAR forget-subject racing a mid-flight crawl over the same subject
//         — the subject must never survive via an in-flight batch (no
//         resurrection window); suppression wins in EVERY interleaving.
//         Includes the re-ACL-pass variant (no ghost inventory rows).
//   R2S3  Hash-chained erasure ledger at 50k+ entries under concurrency,
//         then a single-byte tamper storm — Verify must detect 100% of
//         tampers WITHOUT throwing.
//   R2S4  Entity resolution over 100k contacts with near-duplicate
//         names/employers — throughput, bounded (deduped) review queue,
//         zero raw PII in any persisted queue/log output.
//   R2S5  Seat churn landing while shard crawls run concurrently — the
//         ACL-hash bookkeeping must exactly mirror Graph reality, and one
//         re-ACL pass must converge every item to the FINAL seat state.
//   R2S6  HA failover storm over one shared SQLite DB — no double-crawl,
//         no lost writes, no unhandled 'database is locked'.
//
// Offline/in-process only: temp dirs, SQLite files, ordered fakes. Measured
// numbers go through StressLog (STRESS_LOG + test output), same as round 1.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using AltrataConnector.Altrata;
using AltrataConnector.Commands;
using AltrataConnector.Config;
using AltrataConnector.Entitlement;
using AltrataConnector.Graph;
using AltrataConnector.Identity;
using AltrataConnector.Infrastructure;
using AltrataConnector.Ingestion;
using AltrataConnector.State;
using Xunit.Abstractions;

namespace AltrataConnector.Tests;

/// <summary>
/// Thread-safe IGraphClient fake that records EVERY operation with a global
/// sequence number, so a test can replay arrival order and compute the final
/// (last-op-wins) state of the index — exactly what the race scenarios need.
/// Optional async hooks gate a batch mid-flight for deterministic interleaving.
/// </summary>
internal sealed class OrderedGraph : IGraphClient
{
    public const string OpPut = "put";
    public const string OpDelete = "delete";
    public const string OpAcl = "acl";

    public sealed record GraphOp(long Seq, string Kind, string ItemId, IReadOnlyList<AclEntry>? Acl);

    private long _seq;
    private readonly ConcurrentQueue<GraphOp> _ops = new();

    /// <summary>Awaited before a PUT batch lands (deterministic race gates).</summary>
    public Func<IReadOnlyList<ExternalItem>, Task>? BeforePutBatch { get; set; }

    /// <summary>Awaited before an ACL-update batch lands.</summary>
    public Func<IReadOnlyList<AclUpdate>, Task>? BeforeAclBatch { get; set; }

    public IReadOnlyList<GraphOp> Ops => _ops.ToArray();

    public IReadOnlyList<GraphOp> OpsFor(string itemId) =>
        _ops.Where(o => o.ItemId == itemId).OrderBy(o => o.Seq).ToList();

    /// <summary>Replay all ops in arrival order → items that exist NOW, each with
    /// its last-applied ACL (a DELETE removes; a later PUT resurrects).</summary>
    public Dictionary<string, IReadOnlyList<AclEntry>> LiveItems()
    {
        var live = new Dictionary<string, IReadOnlyList<AclEntry>>(StringComparer.Ordinal);
        foreach (var op in _ops.OrderBy(o => o.Seq))
        {
            switch (op.Kind)
            {
                case OpPut:
                    live[op.ItemId] = op.Acl!;
                    break;
                case OpDelete:
                    live.Remove(op.ItemId);
                    break;
                case OpAcl:
                    if (live.ContainsKey(op.ItemId))
                        live[op.ItemId] = op.Acl!;
                    break;
            }
        }
        return live;
    }

    private void Record(string kind, string itemId, IReadOnlyList<AclEntry>? acl)
    {
        if (acl != null)
        {
            SeatAclBuilder.AssertNeverEveryone(acl);   // structural guard, as in prod
            Assert.NotEmpty(acl);                      // an empty ACL must never ship
        }
        _ops.Enqueue(new GraphOp(Interlocked.Increment(ref _seq), kind, itemId, acl));
    }

    public Task EnsureConnectionAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> ConnectionExistsAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task RegisterSchemaAsync(GraphSchema schema, CancellationToken ct = default) => Task.CompletedTask;

    public Task PutItemAsync(ExternalItem item, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Record(OpPut, item.Id, item.Acl);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<BatchOpResult>> PutItemsBatchAsync(
        IReadOnlyList<ExternalItem> items, CancellationToken ct = default)
    {
        if (BeforePutBatch != null)
            await BeforePutBatch(items);
        var results = new List<BatchOpResult>(items.Count);
        foreach (var item in items)
        {
            Record(OpPut, item.Id, item.Acl);
            results.Add(new BatchOpResult(item.Id, true, 200, null));
        }
        return results;
    }

    public Task UpdateItemAclAsync(string itemId, IReadOnlyList<AclEntry> acl, CancellationToken ct = default)
    {
        Record(OpAcl, itemId, acl);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<BatchOpResult>> UpdateItemAclsBatchAsync(
        IReadOnlyList<AclUpdate> updates, CancellationToken ct = default)
    {
        if (BeforeAclBatch != null)
            await BeforeAclBatch(updates);
        var results = new List<BatchOpResult>(updates.Count);
        foreach (var update in updates)
        {
            Record(OpAcl, update.ItemId, update.Acl);
            results.Add(new BatchOpResult(update.ItemId, true, 200, null));
        }
        return results;
    }

    public Task DeleteItemAsync(string itemId, CancellationToken ct = default)
    {
        Record(OpDelete, itemId, null);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BatchOpResult>> DeleteItemsBatchAsync(
        IReadOnlyList<string> itemIds, CancellationToken ct = default)
    {
        var results = new List<BatchOpResult>(itemIds.Count);
        foreach (var itemId in itemIds)
        {
            Record(OpDelete, itemId, null);
            results.Add(new BatchOpResult(itemId, true, 204, null));
        }
        return Task.FromResult<IReadOnlyList<BatchOpResult>>(results);
    }
}

/// <summary>Per-scenario wiring over temp dirs: crawl engine + full Runtime
/// sharing the SAME stores/graph, so commands can race the engine.</summary>
internal sealed class R2Rig : IDisposable
{
    public string Root { get; }
    public string FeedPath { get; }
    public string SeatPath { get; }
    public AppConfig Config { get; }
    public FileStateStore State { get; }
    public SqliteIdentityStore Identity { get; }
    public OrderedGraph Graph { get; } = new();
    public SeatService Seats { get; }
    public CrawlEngine Engine { get; }
    public Runtime Runtime { get; }

    private readonly string? _previousLogsDir;

    public R2Rig(string prefix, string connectorId, params string[] seatUsers)
    {
        Root = TestFixtures.NewTempDir(prefix);
        FeedPath = Path.Combine(Root, "feed");
        Directory.CreateDirectory(FeedPath);
        SeatPath = Path.Combine(Root, "seats.json");
        TestFixtures.WriteSeatFile(SeatPath,
            seatUsers.Length > 0 ? seatUsers : new[] { "alice@contoso.com" });

        _previousLogsDir = Environment.GetEnvironmentVariable("LOGS_DIR");
        Environment.SetEnvironmentVariable("LOGS_DIR", Path.Combine(Root, "logs"));
        Environment.SetEnvironmentVariable("GRAPH_CONNECTION_SHARDS", null);

        Config = TestFixtures.NewConfig(feedPath: FeedPath, seatListPath: SeatPath,
            dataDir: Path.Combine(Root, "data")) with
        { ConnectorId = connectorId };
        State = new FileStateStore(connectorId,
            logsDir: Path.Combine(Root, "logs"), dataDir: Path.Combine(Root, "data"));
        Identity = new SqliteIdentityStore(Path.Combine(Root, "data", "identity.db"));
        Seats = new SeatService(Config, Identity, State);
        Engine = new CrawlEngine(Config, Graph, State, Identity, Seats, new FakeAlertSink());
        Runtime = new Runtime
        {
            Config = Config,
            State = State,
            Identity = Identity,
            Graph = Graph,
            Seats = Seats,
            Alerts = new Alerting(connectorId),
            Audit = new AuditLog(connectorId, logsDir: Path.Combine(Root, "logs")),
            Erasure = new ErasureLedger(connectorId, logsDir: Path.Combine(Root, "logs")),
            Decisions = new DecisionLedger(connectorId, logsDir: Path.Combine(Root, "logs")),
            GraphBreaker = new CircuitBreaker("graph", new CircuitBreakerOptions { Enabled = false }),
            ApiBreaker = new CircuitBreaker("api", new CircuitBreakerOptions { Enabled = false }),
        };
        ServiceStop.Reset();
    }

    public void Dispose()
    {
        Identity.Dispose();
        Environment.SetEnvironmentVariable("LOGS_DIR", _previousLogsDir);
        ServiceStop.Reset();
    }
}

// =====================================================================================
// R2S1 — BuildItemId under unicode / normalization adversaries
// =====================================================================================

public class ItemIdUnicodeStressTests
{
    private readonly ITestOutputHelper _out;
    public ItemIdUnicodeStressTests(ITestOutputHelper output) => _out = output;

    private static bool IsGraphSafeAscii(string id)
    {
        // ^[A-Za-z0-9-]+(_[0-9a-f]{12})?$ — pass-through body, optional hash tail.
        var underscore = id.IndexOf('_');
        var body = underscore < 0 ? id : id[..underscore];
        if (body.Length == 0 || !body.All(ch =>
                ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-'))
            return false;
        if (underscore < 0)
            return true;
        var tail = id[(underscore + 1)..];
        return tail.Length == 12
               && !tail.Contains('_')
               && tail.All(ch => ch is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
    }

    [Fact]
    public void Regression_unicode_never_passes_through_and_ids_are_pinned()
    {
        // Known-answer ids: SHA-256 tails precomputed independently (python
        // hashlib over the exact UTF-8 bytes), so any future normalization,
        // culture or encoding drift in BuildItemId breaks these literals.
        var cases = new (string Raw, string Expected, string Why)[]
        {
            ("Zo\u00eb", "PersonProfile-Zo-_c6a12698582f", "NFC precomposed letter"),
            ("Zoe\u0308", "PersonProfile-Zoe-_c106d15587c5", "NFD combining mark"),
            ("\u041f123", "PersonProfile--123_a5cdabd29a22", "Cyrillic homoglyph \u041f"),
            ("K\u212aelvin", "PersonProfile-K-elvin_c6d984261624", "U+212A case-folds to k"),
            ("\u0661\u0662\u0663", "PersonProfile----_7afc29c798e4", "Arabic-Indic digits"),
            ("P\u200b1", "PersonProfile-P-1_67f47adc4ed2", "zero-width space"),
            ("P\u202e321", "PersonProfile-P-321_8dc8d9a79000", "RTL override"),
        };
        foreach (var (raw, expected, why) in cases)
        {
            var id = ItemTransformer.BuildItemId(Datasets.PersonProfile, raw);
            Assert.True(id.All(ch => ch < 128), $"{why}: id '{id}' contains non-ASCII");
            Assert.True(IsGraphSafeAscii(id), $"{why}: id '{id}' outside the Graph-safe shape");
            Assert.Equal(expected, id);   // byte-stable across runs
        }

        // NFC and NFD spellings of the same visual name are DIFFERENT raw ids
        // and must stay disjoint (no normalization anywhere in the id path).
        Assert.NotEqual(
            ItemTransformer.BuildItemId(Datasets.PersonProfile, "Zo\u00eb"),
            ItemTransformer.BuildItemId(Datasets.PersonProfile, "Zoe\u0308"));
        // A Kelvin-sign id can never collide with its ASCII case-fold.
        Assert.NotEqual(
            ItemTransformer.BuildItemId(Datasets.PersonProfile, "K\u212aelvin"),
            ItemTransformer.BuildItemId(Datasets.PersonProfile, "KKelvin"));
        StressLog.Line(_out,
            $"[R2S1a] {cases.Length} unicode adversary ids pinned to known answers, all ASCII Graph-safe");
    }

    [Fact]
    public void Adversarial_unicode_sweep_stays_disjoint_ascii_and_stable()
    {
        var datasets = new[] { Datasets.PersonProfile, Datasets.Organization, Datasets.RelationshipPath };
        var seen = new Dictionary<string, (string Ds, string Rid)>(StringComparer.Ordinal);
        var produced = new List<(string Ds, string Rid, string Id)>();
        var collisions = 0;
        var nonAscii = 0;
        var badShape = 0;
        long total = 0;
        long hashed = 0;

        void Add(string ds, string rid)
        {
            total++;
            var id = ItemTransformer.BuildItemId(ds, rid);
            if (!id.All(ch => ch < 128))
                nonAscii++;
            if (!IsGraphSafeAscii(id))
                badShape++;
            if (id.Contains('_'))
                hashed++;
            if (seen.TryGetValue(id, out var prev))
            {
                if (prev.Ds != ds || prev.Rid != rid)
                    collisions++;
            }
            else
            {
                seen[id] = (ds, rid);
            }
            produced.Add((ds, rid, id));
        }

        const int k = 5_000;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < k; i++)
        {
            foreach (var ds in datasets)
            {
                Add(ds, $"acct{i}");                          // ASCII pass-through
                Add(ds, $"ACCT{i}");                          // ASCII case pair (distinct id)
                Add(ds, $"acct\u00e9{i}");                    // NFC precomposed e-acute
                Add(ds, $"accte\u0301{i}");                   // NFD e + combining acute
                Add(ds, $"acct\u200b{i}");                    // zero-width space
                Add(ds, $"acct\u200d{i}");                    // zero-width joiner
                Add(ds, $"\u0430cct{i}");                     // Cyrillic homoglyph a
                Add(ds, $"acct\u212a{i}");                    // Kelvin sign (folds to k)
                Add(ds, $"acct\u0661{i}");                    // Arabic-Indic digit one
                Add(ds, $"acct\U0001F600{i}");                // surrogate pair (emoji)
                Add(ds, $"acct{i}\u202e");                    // trailing RTL override
                Add(ds, $"acct-{i}-\u0301");                  // combining mark after hyphen
            }
        }
        sw.Stop();

        Assert.Equal(0, nonAscii);      // every id is pure ASCII
        Assert.Equal(0, badShape);      // every id matches [A-Za-z0-9-]+(_hex12)?
        Assert.Equal(0, collisions);    // id space disjoint over the whole sweep

        // Stability: recomputing every id from the same raw input yields the
        // identical string (deterministic across runs — no ambient state).
        var unstable = produced.Count(p => ItemTransformer.BuildItemId(p.Ds, p.Rid) != p.Id);
        Assert.Equal(0, unstable);

        StressLog.Line(_out,
            $"[R2S1b] unicode sweep: {total:N0} adversarial ids ({seen.Count:N0} distinct) → 0 collisions, " +
            $"0 non-ASCII, 0 bad-shape, {hashed:N0} hashed / {total - hashed:N0} pass-through, " +
            $"0 unstable on recompute, {total / sw.Elapsed.TotalSeconds:N0} id/s");
    }
}

// =====================================================================================
// R2S2 — DSAR forget-subject racing a mid-flight crawl (no resurrection window)
// =====================================================================================

public class DsarCrawlRaceStressTests
{
    private readonly ITestOutputHelper _out;
    public DsarCrawlRaceStressTests(ITestOutputHelper output) => _out = output;

    private static string PersonItemId(string subjectId) =>
        ItemTransformer.BuildItemId(Datasets.PersonProfile, subjectId);

    [Fact]
    public async Task Forget_completing_during_inflight_PUT_cannot_resurrect_subject()
    {
        using var rig = new R2Rig("dsargate", "R2DsarGate", "alice@contoso.com", "bob@contoso.com");
        const string victim = "VRACE";
        TestFixtures.WriteDelivery(rig.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(
                (victim, "Victim Person", null, null),
                ("F1", "Filler One", null, null),
                ("F2", "Filler Two", null, null),
                ("F3", "Filler Three", null, null),
                ("F4", "Filler Four", null, null),
                ("F5", "Filler Five", null, null)), 6));

        var victimItem = PersonItemId(victim);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new SemaphoreSlim(0);
        var gated = 0;
        rig.Graph.BeforePutBatch = async items =>
        {
            // Park the FIRST batch carrying the victim mid-flight: the crawl has
            // already passed its per-record suppression pre-check for it.
            if (items.Any(i => i.Id == victimItem) && Interlocked.Exchange(ref gated, 1) == 0)
            {
                reached.TrySetResult();
                Assert.True(await release.WaitAsync(TimeSpan.FromSeconds(30)), "PUT gate never released");
            }
        };

        var crawl = Task.Run(() => rig.Engine.RunAsync(CrawlKind.Full));
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The whole DSAR cycle runs TO COMPLETION while the victim's PUT batch
        // is in flight: withdrawal (nothing inventoried yet), suppression, ledger.
        var forgotten = await CommandRegistry.ForgetSubjectAsync(
            rig.Runtime, altrataId: victim, email: null, actor: "joseph", confirm: true);
        Assert.Equal(true, forgotten);
        Assert.True(rig.State.IsSubjectSuppressed(victim));

        release.Release();
        var result = await crawl;

        // THE invariant: the in-flight batch must not leave the subject alive.
        var live = rig.Graph.LiveItems();
        Assert.False(live.ContainsKey(victimItem),
            "erased subject resurrected by the in-flight crawl batch");
        Assert.DoesNotContain(rig.Identity.ListIngestedItems(), i => i.ItemId == victimItem);
        Assert.Empty(rig.Identity.ListItemsForSubject(victim));

        // Accounting stays honest: the victim's record counts as suppressed, the
        // delivery still reconciles, and the ledger chain is intact.
        Assert.Equal(1, result.ItemsSuppressed);
        Assert.Equal(5, result.ItemsIngested);
        Assert.Equal(Reconciliation.StatusReconciled, result.Reconciliations.Single().Status);
        Assert.True(rig.Runtime.Erasure.Verify(out _));

        var ops = rig.Graph.OpsFor(victimItem);
        Assert.Equal(OrderedGraph.OpPut, ops[0].Kind);        // the racing PUT landed...
        Assert.Equal(OrderedGraph.OpDelete, ops[^1].Kind);    // ...and was withdrawn after it
        StressLog.Line(_out,
            $"[R2S2a] forget-vs-inflight-PUT gate: victim PUT@seq{ops[0].Seq} compensated by DELETE@seq{ops[^1].Seq}, " +
            "suppressed=1, ingested=5, delivery reconciled, 0 live items for the subject");
    }

    [Fact]
    public async Task Interleaving_fuzz_suppression_wins_in_every_schedule()
    {
        using var rig = new R2Rig("dsarfuzz", "R2DsarFuzz", "alice@contoso.com");
        const int iterations = 140;
        var rng = new Random(20260717);
        long noPut = 0, putWithdrawn = 0;

        var sw = Stopwatch.StartNew();
        for (var k = 0; k < iterations; k++)
        {
            var victim = $"V{k:D3}";
            var victimItem = PersonItemId(victim);
            TestFixtures.WriteDelivery(rig.FeedPath, $"dz{k:D3}",
                ($"p{k}.json", Datasets.PersonProfile, TestFixtures.PersonJson(
                    (victim, $"Victim {k}", null, null),
                    ($"F{k}a", "Filler", null, null),
                    ($"F{k}b", "Filler", null, null),
                    ($"F{k}c", "Filler", null, null)), 4));

            Task<CrawlResult> Crawl() => rig.Engine.RunAsync(CrawlKind.Incremental);
            Task<object?> Forget() => CommandRegistry.ForgetSubjectAsync(
                rig.Runtime, altrataId: victim, email: null, actor: "joseph", confirm: true);

            object? forgotten;
            switch (k % 4)
            {
                case 0:   // forget fully BEFORE the crawl — suppression filters ingest
                    forgotten = await Forget();
                    await Crawl();
                    break;
                case 2:   // crawl fully BEFORE the forget — normal withdrawal
                    await Crawl();
                    forgotten = await Forget();
                    break;
                default:  // true race with random jitter on both sides
                    var crawlDelay = rng.Next(0, 3);
                    var forgetDelay = rng.Next(0, 10);
                    var crawlTask = Task.Run(async () =>
                    {
                        await Task.Delay(crawlDelay);
                        return await Crawl();
                    });
                    var forgetTask = Task.Run(async () =>
                    {
                        await Task.Delay(forgetDelay);
                        return await Forget();
                    });
                    await crawlTask;
                    forgotten = await forgetTask;
                    break;
            }
            Assert.Equal(true, forgotten);

            // Post-conditions that must hold under EVERY schedule.
            Assert.True(rig.State.IsSubjectSuppressed(victim), $"iter {k}: not suppressed");
            Assert.False(rig.Graph.LiveItems().ContainsKey(victimItem),
                $"iter {k}: erased subject still live in the index (resurrection window)");
            Assert.Empty(rig.Identity.ListItemsForSubject(victim));
            Assert.DoesNotContain(rig.Identity.ListIngestedItems(), i => i.ItemId == victimItem);

            var ops = rig.Graph.OpsFor(victimItem);
            if (ops.Count == 0)
            {
                noPut++;               // suppression filtered the record before any PUT
            }
            else
            {
                Assert.Equal(OrderedGraph.OpDelete, ops[^1].Kind);   // last op always DELETE
                putWithdrawn++;
            }
        }
        sw.Stop();

        Assert.Equal(iterations, noPut + putWithdrawn);
        Assert.True(noPut > 0, "no forget-before-ingest schedule was exercised");
        Assert.True(putWithdrawn > 0, "no PUT-then-withdraw schedule was exercised");
        Assert.True(rig.Runtime.Erasure.Verify(out _));
        Assert.Equal(iterations,
            rig.Runtime.Erasure.ReadAll().Count(e => e.Action == ErasureActions.Erase));
        StressLog.Line(_out,
            $"[R2S2b] DSAR×crawl fuzz: {iterations} interleavings (suppressed-before-PUT={noPut}, " +
            $"PUT-then-withdrawn={putWithdrawn}), 0 resurrections, ledger Verify=OK, " +
            $"{iterations / sw.Elapsed.TotalSeconds:N1} interleavings/s");
    }

    [Fact]
    public async Task ReAcl_pass_racing_forget_never_reinserts_erased_inventory_rows()
    {
        using var rig = new R2Rig("dsarreacl", "R2DsarReAcl", "alice@contoso.com");
        var itemA = PersonItemId("A");
        var itemB = PersonItemId("B");
        foreach (var (item, subject) in new[] { (itemA, "A"), (itemB, "B") })
        {
            rig.Identity.RecordIngestedItem(new IngestedItem(item, Datasets.PersonProfile, "stale-hash", DateTime.UtcNow));
            rig.Identity.RecordItemSubjects(item, new[] { subject });
            await rig.Graph.PutItemAsync(new ExternalItem
            {
                Id = item,
                Acl = new[] { new AclEntry { Type = "user", Value = "alice@contoso.com" } },
                Properties = new Dictionary<string, object?> { ["title"] = subject },
            });
        }

        rig.Seats.CommitSeatHash("previous-seat-hash");   // force RequiresReAcl
        var sync = rig.Seats.SyncSeats();
        Assert.True(sync.RequiresReAcl);

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new SemaphoreSlim(0);
        var gated = 0;
        rig.Graph.BeforeAclBatch = async _ =>
        {
            if (Interlocked.Exchange(ref gated, 1) == 0)
            {
                reached.TrySetResult();
                Assert.True(await release.WaitAsync(TimeSpan.FromSeconds(30)), "ACL gate never released");
            }
        };

        var pass = Task.Run(() => rig.Engine.ReAclPassAsync(sync, CancellationToken.None));
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Erase subject A while the re-ACL batch (computed from a snapshot that
        // still contains A's item) is parked in flight.
        var forgotten = await CommandRegistry.ForgetSubjectAsync(
            rig.Runtime, altrataId: "A", email: null, actor: "joseph", confirm: true);
        Assert.Equal(true, forgotten);
        Assert.DoesNotContain(rig.Identity.ListIngestedItems(), i => i.ItemId == itemA);

        release.Release();
        await pass;

        // The pass must NOT re-insert the erased subject's inventory row (a ghost
        // row would make the store claim ownership of an item the DSAR withdrew).
        var remaining = Assert.Single(rig.Identity.ListIngestedItems());
        Assert.Equal(itemB, remaining.ItemId);
        Assert.Equal(sync.SeatHash, remaining.AclHash);
        Assert.False(rig.Graph.LiveItems().ContainsKey(itemA));
        StressLog.Line(_out,
            "[R2S2c] re-ACL×forget gate: erased row NOT re-inserted by the in-flight re-ACL pass; " +
            $"survivor stamped with the new seat hash {sync.SeatHash[..12]}…");
    }
}

// =====================================================================================
// R2S3 — erasure ledger at 50k+ under concurrency + single-byte tamper storm
// =====================================================================================

public class LedgerScaleTamperStressTests
{
    private readonly ITestOutputHelper _out;
    public LedgerScaleTamperStressTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Regression_verify_reports_tamper_without_throwing()
    {
        var root = TestFixtures.NewTempDir("ledgerreg");
        var ledger = new ErasureLedger("R2LedgerReg", logsDir: root);
        for (var i = 0; i < 5; i++)
            ledger.Append("joseph", ErasureActions.Erase, $"s{i}", null, new[] { $"i{i}" });
        Assert.True(ledger.Verify(out _));

        var pristine = File.ReadAllBytes(ledger.Path);
        var lines = File.ReadAllLines(ledger.Path);
        int LineStart(int index) => lines.Take(index).Sum(l => l.Length + 1);

        // Structural tampers previously CRASHED Verify (JsonException) instead of
        // returning false — the detector must never throw on what it detects.
        var cases = new (string Label, int Pos, byte NewByte, int ExpectBroken)[]
        {
            ("line 3 '{' destroyed", LineStart(2), (byte)'X', 3),
            ("newline joining lines 1+2", LineStart(0) + lines[0].Length, (byte)' ', 1),
            ("trailing byte of final line", pristine.Length - 1, (byte)'}', 5),
            ("value byte inside line 2", LineStart(1) + lines[1].IndexOf("\"s1\"", StringComparison.Ordinal) + 2, (byte)'Z', 2),
        };
        foreach (var (label, pos, newByte, expectBroken) in cases)
        {
            var tampered = (byte[])pristine.Clone();
            Assert.NotEqual(newByte, tampered[pos]);
            tampered[pos] = newByte;
            File.WriteAllBytes(ledger.Path, tampered);
            var ok = ledger.Verify(out var broken);   // must not throw
            Assert.False(ok, $"{label}: tamper undetected");
            Assert.Equal(expectBroken, broken);
        }

        File.WriteAllBytes(ledger.Path, pristine);
        Assert.True(ledger.Verify(out _));
        StressLog.Line(_out,
            $"[R2S3a] {cases.Length} structural/value tampers each detected via Verify=false (no exception), pristine restores OK");
    }

    [Fact]
    public void Concurrent_50k_appends_chain_valid_and_fast()
    {
        var root = TestFixtures.NewTempDir("ledger50k");
        var ledger = new ErasureLedger("R2Ledger50k", logsDir: root);
        const int n = 50_000;

        var sw = Stopwatch.StartNew();
        Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
            ledger.Append("joseph", ErasureActions.Erase, $"subject-{i}", null, new[] { $"item-{i}" }));
        sw.Stop();
        // O(1) appends: the O(N²) re-read-per-append implementation needs hours here.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(120),
            $"50k appends took {sw.Elapsed.TotalSeconds:F0}s — append is not O(1)");

        var vsw = Stopwatch.StartNew();
        Assert.True(ledger.Verify(out var broken), $"chain broken at {broken}");
        vsw.Stop();

        var all = ledger.ReadAll();
        Assert.Equal(n, all.Count);
        var seqs = all.Select(e => e.Seq).OrderBy(s => s).ToList();
        for (var i = 0; i < n; i++)
            Assert.Equal(i + 1, seqs[i]);   // contiguous 1..N despite 16-way contention

        StressLog.Line(_out,
            $"[R2S3b] ledger scale: {n:N0} concurrent appends (16 threads) in {sw.Elapsed.TotalSeconds:F1}s " +
            $"({n / sw.Elapsed.TotalSeconds:N0} append/s), Verify=OK in {vsw.ElapsedMilliseconds:N0} ms, seq 1..{n:N0} contiguous");
    }

    [Fact]
    public void Single_byte_tamper_storm_detected_100_percent()
    {
        var root = TestFixtures.NewTempDir("ledgertamper");
        var ledger = new ErasureLedger("R2LedgerTamper", logsDir: root);
        const int entries = 1_200;
        for (var i = 0; i < entries; i++)
            ledger.Append("joseph", ErasureActions.Erase, $"subject-{i}", null,
                new[] { $"item-{i}-a", $"item-{i}-b" });
        Assert.True(ledger.Verify(out _));
        var pristine = File.ReadAllBytes(ledger.Path);

        // 500 tampers: forced structural positions + random positions/bytes.
        var rng = new Random(20260717);
        var positions = new List<int> { 0, pristine.Length - 1, Array.IndexOf(pristine, (byte)'\n') };
        var chosen = new HashSet<int>(positions);
        while (positions.Count < 500)
        {
            var pos = rng.Next(pristine.Length);
            if (chosen.Add(pos))
                positions.Add(pos);
        }

        var detected = 0;
        var undetected = new List<string>();
        var vsw = Stopwatch.StartNew();
        foreach (var pos in positions)
        {
            var tampered = (byte[])pristine.Clone();
            byte newByte;
            do
            {
                newByte = (byte)rng.Next(256);
            }
            while (newByte == pristine[pos]);
            tampered[pos] = newByte;
            File.WriteAllBytes(ledger.Path, tampered);
            if (!ledger.Verify(out _))
                detected++;
            else
                undetected.Add($"pos={pos} 0x{pristine[pos]:X2}→0x{newByte:X2}");
        }
        vsw.Stop();

        Assert.True(undetected.Count == 0,
            $"undetected tampers: {string.Join(", ", undetected.Take(5))}");
        Assert.Equal(positions.Count, detected);

        File.WriteAllBytes(ledger.Path, pristine);
        Assert.True(ledger.Verify(out _));   // and the pristine chain still verifies
        StressLog.Line(_out,
            $"[R2S3c] tamper storm: {detected}/{positions.Count} single-byte tampers detected (100%) over a " +
            $"{entries:N0}-entry chain ({pristine.Length / 1024:N0} KB), avg Verify {vsw.Elapsed.TotalMilliseconds / positions.Count:F1} ms");
    }
}

// =====================================================================================
// R2S4 — entity resolution at 100k scale: throughput, bounded queue, zero raw PII
// =====================================================================================

public class EntityResolutionScaleStressTests
{
    private readonly ITestOutputHelper _out;
    public EntityResolutionScaleStressTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Regression_review_queue_dedups_repeat_candidates()
    {
        var root = TestFixtures.NewTempDir("reviewdedup");
        using var store = new SqliteIdentityStore(Path.Combine(root, "id.db"));
        store.ReplaceCrmContacts(new[]
        {
            new CrmContact { Id = "C1", Name = "Ada PIINAME1", Employer = "PIIEMP1 Acme Engines", Role = "Director" },
        });
        var queue = new MatchReviewQueue("R2Dedup", logsDir: root);
        var resolver = new EntityResolver(store, new FuzzyOptions
        {
            Enabled = true, Threshold = 0.85, ReviewFloor = 0.6, Review = queue,
        });

        // The same candidate resolved twice (as every full crawl does) must land
        // in the review queue ONCE — the documented dedup keys must actually dedup.
        resolver.Match(email: null, name: "Ada PIINAME1", employer: "PIIEMP1 Acme Systems", role: null, altrataId: "A1");
        resolver.Match(email: null, name: "Ada PIINAME1", employer: "PIIEMP1 Acme Systems", role: null, altrataId: "A1");
        Assert.Single(queue.ReadAll());

        // A different employer spelling is a NEW candidate (different dedup key).
        resolver.Match(email: null, name: "Ada PIINAME1", employer: "PIIEMP1 Acme Widgets", role: null, altrataId: "A1");
        Assert.Equal(2, queue.ReadAll().Count);

        // A fresh queue instance over the same file (next crawl) still dedups.
        var queue2 = new MatchReviewQueue("R2Dedup", logsDir: root);
        var resolver2 = new EntityResolver(store, new FuzzyOptions
        {
            Enabled = true, Threshold = 0.85, ReviewFloor = 0.6, Review = queue2,
        });
        resolver2.Match(email: null, name: "Ada PIINAME1", employer: "PIIEMP1 Acme Systems", role: null, altrataId: "A1");
        Assert.Equal(2, queue2.ReadAll().Count);
        StressLog.Line(_out, "[R2S4a] review queue dedup: 4 resolves of 2 distinct candidates → 2 entries (incl. across instances)");
    }

    [Fact]
    public void Hundred_k_contacts_throughput_bounded_queue_zero_pii()
    {
        var root = TestFixtures.NewTempDir("entityscale");
        using var store = new SqliteIdentityStore(Path.Combine(root, "id.db"));

        const int n = 100_000;
        var suffixes = new[] { "Inc", "Incorporated", "Ltd", "Limited" };
        var contacts = new List<CrmContact>(n);
        for (var i = 0; i < n; i++)
        {
            contacts.Add(new CrmContact
            {
                Id = $"C{i}",
                Email = $"pii.user{i}@ex{i % 977}.example",
                Name = $"Alex PIINAME{i} Carter",
                // Near-duplicate employers: 911 buckets × 4 suffix spellings that
                // all normalize to the same employer string.
                Employer = $"PIIEMP{i % 911} Global Advisory {suffixes[i % 4]}",
                Role = "Director",
            });
        }
        var loadSw = Stopwatch.StartNew();
        store.ReplaceCrmContacts(contacts);
        loadSw.Stop();
        Assert.Equal(n, store.CountCrmContacts());

        // ---- deterministic tier at scale (indexed email / name+employer) ----
        var resolver = new EntityResolver(store);   // fuzzy off
        var mismatches = 0;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < n; i++)
        {
            var match = (i % 2 == 0)
                ? resolver.Match(email: $"pii.user{i}@ex{i % 977}.example", name: null, employer: null)
                : resolver.Match(email: null, name: $"Alex PIINAME{i} Carter",
                    // different suffix spelling than stored — must fold to the same employer
                    employer: $"PIIEMP{i % 911} Global Advisory {suffixes[(i + 2) % 4]}");
            if (match == null || match.CrmContactId != $"C{i}")
                mismatches++;
        }
        sw.Stop();
        Assert.Equal(0, mismatches);

        // ---- fuzzy tier over the full 100k candidate pool ----
        var queue = new MatchReviewQueue("R2EntityScale", logsDir: root);
        var fuzzyResolver = new EntityResolver(store, new FuzzyOptions
        {
            Enabled = true, Threshold = 0.85, ReviewFloor = 0.6, Review = queue,
        });
        const int probes = 40;
        var fuzzySw = Stopwatch.StartNew();
        for (var j = 0; j < probes; j++)
        {
            var i = (j * 997) % n;
            // exact name (score 1.0 → 0.6) + half-overlapping employer (→ +0.15)
            // = 0.75 ∈ [floor, threshold) ⇒ review-queue routing, never auto-link.
            var match = fuzzyResolver.Match(email: null, name: $"Alex PIINAME{i} Carter",
                employer: $"PIIEMP{i % 911} Global Consulting", role: null, altrataId: $"A{i}");
            Assert.Null(match);
        }
        fuzzySw.Stop();
        var entries = queue.ReadAll();
        Assert.Equal(probes, entries.Count);

        // Second crawl over the SAME probes (fresh queue + resolver instances):
        // the queue must stay bounded — no growth on re-resolve.
        var queueB = new MatchReviewQueue("R2EntityScale", logsDir: root);
        var fuzzyResolverB = new EntityResolver(store, new FuzzyOptions
        {
            Enabled = true, Threshold = 0.85, ReviewFloor = 0.6, Review = queueB,
        });
        for (var j = 0; j < probes; j++)
        {
            var i = (j * 997) % n;
            fuzzyResolverB.Match(email: null, name: $"Alex PIINAME{i} Carter",
                employer: $"PIIEMP{i % 911} Global Consulting", role: null, altrataId: $"A{i}");
        }
        Assert.Equal(probes, queueB.ReadAll().Count);   // bounded across crawls

        // ---- zero raw PII in ANY persisted queue/log output ----
        Assert.All(entries, e =>
        {
            Assert.Matches("^[0-9a-f]{16}$", e.NameHash!);
            Assert.Matches("^[0-9a-f]{16}$", e.EmployerHash!);
        });
        var piiTokens = new[] { "PIINAME", "PIIEMP", "pii.user", "Alex", "Carter", "Global", "Advisory", "Consulting", "Director" };
        foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
        {
            var raw = File.ReadAllText(file);
            foreach (var token in piiTokens)
                Assert.DoesNotContain(token, raw);
        }

        var candidateScores = (long)probes * n * 2;   // both fuzzy passes scan the pool
        StressLog.Line(_out,
            $"[R2S4b] entity scale: {n:N0} contacts loaded in {loadSw.Elapsed.TotalSeconds:F1}s; deterministic " +
            $"{n:N0} resolves, 0 wrong links, {n / sw.Elapsed.TotalSeconds:N0} resolve/s; fuzzy {probes}+{probes} probes " +
            $"scored {candidateScores:N0} candidates ({probes * (long)n / fuzzySw.Elapsed.TotalSeconds:N0} score/s), " +
            $"queue bounded at {entries.Count} entries across 2 crawls, 0 raw-PII tokens in persisted output");
    }
}

// =====================================================================================
// R2S5 — seat churn landing while shard crawls run concurrently
// =====================================================================================

public class SeatShardChurnStressTests
{
    private readonly ITestOutputHelper _out;
    public SeatShardChurnStressTests(ITestOutputHelper output) => _out = output;

    private sealed record ShardRig(string Id, AppConfig Config, FileStateStore State,
        SqliteIdentityStore Identity, SeatService Seats, CrawlEngine Engine);

    private static void WriteSeatsAtomic(string path, IReadOnlyList<string> users)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(
            new { users, groups = Array.Empty<string>() }));
        File.Move(tmp, path, overwrite: true);
    }

    private static string AclKey(IEnumerable<AclEntry> acl) =>
        string.Join("|", acl.Select(e => $"{e.Type}:{e.Value}").OrderBy(s => s, StringComparer.Ordinal));

    [Fact]
    public async Task Concurrent_shard_crawls_with_seat_churn_converge_to_final_acl()
    {
        var root = TestFixtures.NewTempDir("seatshard");
        var previousLogs = Environment.GetEnvironmentVariable("LOGS_DIR");
        Environment.SetEnvironmentVariable("LOGS_DIR", Path.Combine(root, "logs"));
        Environment.SetEnvironmentVariable("GRAPH_CONNECTION_SHARDS", null);
        ServiceStop.Reset();
        var shards = new List<ShardRig>();
        try
        {
            var seatPath = Path.Combine(root, "seats.json");
            WriteSeatsAtomic(seatPath, new[] { "u0@contoso.com", "u1@contoso.com" });

            const int shardCount = 4;
            const int itemsPerShard = 240;
            var graph = new OrderedGraph();
            for (var s = 0; s < shardCount; s++)
            {
                var shardId = $"R2Shard{(char)('A' + s)}";
                var feed = Path.Combine(root, $"feed{s}");
                Directory.CreateDirectory(feed);
                var persons = Enumerable.Range(0, itemsPerShard)
                    .Select(i => ($"{(char)('A' + s)}{i}", $"Person {s}-{i}", (string?)null, (string?)null))
                    .ToArray();
                TestFixtures.WriteDelivery(feed, $"d{s}",
                    ($"persons{s}.json", Datasets.PersonProfile, TestFixtures.PersonJson(persons), itemsPerShard));

                var config = TestFixtures.NewConfig(feedPath: feed, seatListPath: seatPath,
                    dataDir: Path.Combine(root, $"data{s}")) with
                { ConnectorId = shardId };
                var state = new FileStateStore(shardId,
                    logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, $"data{s}"));
                var identity = new SqliteIdentityStore(Path.Combine(root, $"data{s}", "identity.db"));
                var seats = new SeatService(config, identity, state);
                shards.Add(new ShardRig(shardId, config, state, identity, seats,
                    new CrawlEngine(config, graph, state, identity, seats, new FakeAlertSink())));
            }

            // Shard crawls run concurrently while the seat file churns beneath
            // them. Starts are staggered so later shards seat-sync MID-churn and
            // capture different snapshots than earlier ones.
            var crawls = shards.Select((sh, index) => Task.Run(async () =>
            {
                await Task.Delay(index * 25);
                return await sh.Engine.RunAsync(CrawlKind.Full);
            })).ToArray();
            var rng = new Random(20260717);
            var pool = Enumerable.Range(0, 10).Select(i => $"u{i}@contoso.com").ToArray();
            var flips = 0;
            var flipsDuringCrawl = 0;
            while (crawls.Any(c => !c.IsCompleted) && flips < 200)
            {
                var users = pool.OrderBy(_ => rng.Next()).Take(rng.Next(2, 5)).ToArray();
                WriteSeatsAtomic(seatPath, users);
                flips++;
                if (crawls.Any(c => !c.IsCompleted))
                    flipsDuringCrawl++;
                await Task.Delay(5);
            }
            var finalUsers = new[] { "alice@contoso.com", "bob@contoso.com", "carol@contoso.com" };
            WriteSeatsAtomic(seatPath, finalUsers);   // FINAL seat state (never in the churn pool)
            flips++;

            var results = await Task.WhenAll(crawls);
            Assert.All(results, r =>
            {
                Assert.Equal(1, r.DeliveriesProcessed);
                Assert.Equal(itemsPerShard, r.ItemsIngested);
                Assert.Equal(0, r.ItemsDeadLettered);
            });

            var finalSeats = SeatService.ParseSeatFile(File.ReadAllText(seatPath));
            var finalHash = SeatAclBuilder.ComputeSeatHash(finalSeats);
            var finalAclKey = AclKey(SeatAclBuilder.BuildAcl(finalSeats));

            // Invariant A — the bookkeeping never lies: per shard, the items whose
            // STORED acl-hash differs from the final hash are EXACTLY the items
            // whose last-applied Graph ACL differs from the final ACL.
            var live = graph.LiveItems();
            long staleBefore = 0;
            var snapshotHashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var shard in shards)
            {
                var items = shard.Identity.ListIngestedItems();
                Assert.Equal(itemsPerShard, items.Count);
                snapshotHashes.UnionWith(items.Select(i => i.AclHash));
                var staleByHash = items.Where(i => i.AclHash != finalHash).Select(i => i.ItemId)
                    .ToHashSet(StringComparer.Ordinal);
                var staleByGraph = items.Where(i => AclKey(live[i.ItemId]) != finalAclKey).Select(i => i.ItemId)
                    .ToHashSet(StringComparer.Ordinal);
                Assert.True(staleByHash.SetEquals(staleByGraph),
                    $"{shard.Id}: hash-tracking disagrees with Graph reality " +
                    $"(byHash={staleByHash.Count}, byGraph={staleByGraph.Count})");
                staleBefore += staleByHash.Count;
            }

            // Invariant B — one re-ACL pass per shard converges EVERY item to the
            // final seat state (stale snapshots never survive a sync).
            long reAcled = 0;
            foreach (var shard in shards)
            {
                var sync = shard.Seats.SyncSeats();
                Assert.Equal(finalHash, sync.SeatHash);
                if (sync.RequiresReAcl)
                    reAcled += await shard.Engine.ReAclPassAsync(sync, CancellationToken.None);
                else if (sync.Changed)
                    shard.Seats.CommitSeatHash(sync.SeatHash);
                Assert.Empty(shard.Identity.ListItemsWithAclHashOtherThan(finalHash));
            }

            var liveAfter = graph.LiveItems();
            Assert.Equal(shardCount * itemsPerShard, liveAfter.Count);
            Assert.All(liveAfter.Values, acl => Assert.Equal(finalAclKey, AclKey(acl)));

            StressLog.Line(_out,
                $"[R2S5] seat churn × shards: {shardCount} shards × {itemsPerShard} items crawled concurrently, " +
                $"{flips} seat flips ({flipsDuringCrawl} while crawling), {snapshotHashes.Count} distinct seat-hash " +
                $"snapshot(s) applied, stale-vs-final before sync {staleBefore}/{shardCount * itemsPerShard} " +
                $"(hash-tracking == Graph reality on every shard), re-ACL updated {reAcled}, stale after 0");
        }
        finally
        {
            foreach (var shard in shards)
                shard.Identity.Dispose();
            Environment.SetEnvironmentVariable("LOGS_DIR", previousLogs);
            ServiceStop.Reset();
        }
    }
}

// =====================================================================================
// R2S6 — HA failover storm over one shared SQLite DB
// =====================================================================================

public class HaSqliteStormStressTests
{
    private readonly ITestOutputHelper _out;
    public HaSqliteStormStressTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Failover_storm_on_one_sqlite_db_no_double_crawl_no_lost_writes()
    {
        var root = TestFixtures.NewTempDir("hastorm");
        var db = Path.Combine(root, "shared_identity.db");
        var leases = new InMemoryLeaseStore();
        const int nodeCount = 6;
        const int units = 24;
        const int itemsPerUnit = 60;
        var ttl = TimeSpan.FromMinutes(2);
        var t0 = new DateTime(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);

        var nodes = Enumerable.Range(0, nodeCount)
            .Select(n => new HaCoordinator(leases, $"node{n}"))
            .ToArray();
        var stores = new SqliteIdentityStore[nodeCount];

        var unitWorkers = new int[units];
        var workerViolations = 0;
        var sqliteErrors = new ConcurrentQueue<string>();
        var winners = new ConcurrentDictionary<int, string>();
        var doubleWins = 0;
        long acquireWins = 0, acquireRejects = 0, itemWrites = 0;
        var crashedUnits = Enumerable.Range(0, units).Where(u => u % 4 == 0).ToHashSet();

        void CrawlUnit(int node, int u, bool crashMidway)
        {
            var w = Interlocked.Increment(ref unitWorkers[u]);
            if (w > 1)
                Interlocked.Increment(ref workerViolations);   // double-crawl!
            try
            {
                var upTo = crashMidway ? itemsPerUnit / 3 : itemsPerUnit;
                for (var j = 0; j < upTo; j++)
                {
                    try
                    {
                        stores[node].RecordIngestedItem(new IngestedItem(
                            $"u{u:D2}-item{j:D2}", Datasets.PersonProfile, $"h-final-{u}", DateTime.UtcNow));
                        stores[node].RecordItemSubjects($"u{u:D2}-item{j:D2}", new[] { $"u{u:D2}-subj{j:D2}" });
                        Interlocked.Increment(ref itemWrites);
                        if (j % 20 == 0)
                        {
                            _ = stores[node].GetSeats();
                            _ = stores[node].CountIngestedItems();
                        }
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException exc)
                    {
                        sqliteErrors.Enqueue($"node{node} unit{u} item{j}: {exc.SqliteErrorCode} {exc.Message}");
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref unitWorkers[u]);
            }
        }

        // ---- phase 1: all nodes race every unit at T0; ~25% of held units "crash"
        // (their node abandons WITHOUT releasing the lease). Store instances open
        // concurrently against the same DB file (schema-ensure race included).
        // Like the crawl engine's IsDeliveryProcessed check, a node skips units
        // already completed — a lease released after completion is legitimately
        // re-acquirable, and must not be double-CRAWLED.
        var completed = new ConcurrentDictionary<int, bool>();
        long completedSkips = 0;
        var sw = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, nodeCount).Select(n => Task.Run(() =>
        {
            stores[n] = new SqliteIdentityStore(db);
            stores[n].ReplaceSeats(new[] { new SeatPrincipal(SeatPrincipalKind.UserUpn, "alice@contoso.com") });
            var order = Enumerable.Range(0, units).OrderBy(_ => new Random(n * 7919).Next()).ToList();
            foreach (var u in order)
            {
                if (completed.ContainsKey(u))
                {
                    Interlocked.Increment(ref completedSkips);
                    continue;
                }
                if (nodes[n].TryAcquire($"unit:{u}", ttl, t0))
                {
                    Interlocked.Increment(ref acquireWins);
                    if (completed.ContainsKey(u))
                    {
                        // Completed between our check and the acquire — hand back.
                        Interlocked.Increment(ref completedSkips);
                        nodes[n].Release($"unit:{u}");
                        continue;
                    }
                    if (!winners.TryAdd(u, $"node{n}"))
                        Interlocked.Increment(ref doubleWins);
                    var crash = crashedUnits.Contains(u);
                    CrawlUnit(n, u, crash);
                    if (!crash)
                    {
                        completed[u] = true;             // mark BEFORE releasing
                        nodes[n].Release($"unit:{u}");   // crashed nodes never release
                    }
                }
                else
                {
                    Interlocked.Increment(ref acquireRejects);
                }
            }
        })));
        Assert.Equal(units, winners.Count);
        Assert.Equal(0, doubleWins);   // one crawler per unit, ever, in phase 1

        // ---- probes at T0+30s: an abandoned (crashed) lease has NOT expired —
        // no other node may take the unit over early.
        var earlyProbeRejects = 0;
        foreach (var u in crashedUnits)
        {
            for (var n = 0; n < nodeCount; n++)
            {
                if ($"node{n}" == winners[u])
                    continue;   // the owner re-acquires re-entrantly by design
                if (!nodes[n].TryAcquire($"unit:{u}", ttl, t0 + TimeSpan.FromSeconds(30)))
                    earlyProbeRejects++;
            }
        }
        Assert.Equal(crashedUnits.Count * (nodeCount - 1), earlyProbeRejects);

        // ---- renewal keeps a live long-running holder safe past the original TTL.
        Assert.True(nodes[0].TryAcquire("unit:long", ttl, t0));
        Assert.True(nodes[0].Renew("unit:long", ttl, t0 + TimeSpan.FromSeconds(90)));
        Assert.False(nodes[1].TryAcquire("unit:long", ttl, t0 + ttl + TimeSpan.FromSeconds(30)),
            "a renewed lease was stolen past its ORIGINAL expiry");
        nodes[0].Release("unit:long");

        // ---- phase 2: T1 > TTL — crashed units fail over; exactly one node wins
        // each expired lease and re-crawls the unit fully (idempotent rewrite).
        var t1 = t0 + ttl + TimeSpan.FromSeconds(1);
        var failoverWinners = new ConcurrentDictionary<int, string>();
        var failoverDoubleWins = 0;
        await Task.WhenAll(Enumerable.Range(0, nodeCount).Select(n => Task.Run(() =>
        {
            foreach (var u in crashedUnits.OrderBy(_ => new Random(n * 104729).Next()))
            {
                if (completed.ContainsKey(u))
                {
                    Interlocked.Increment(ref completedSkips);
                    continue;
                }
                if (nodes[n].TryAcquire($"unit:{u}", ttl, t1))
                {
                    Interlocked.Increment(ref acquireWins);
                    if (completed.ContainsKey(u))
                    {
                        Interlocked.Increment(ref completedSkips);
                        nodes[n].Release($"unit:{u}");
                        continue;
                    }
                    if (!failoverWinners.TryAdd(u, $"node{n}"))
                    {
                        Interlocked.Increment(ref failoverDoubleWins);
                        continue;
                    }
                    CrawlUnit(n, u, crashMidway: false);
                    completed[u] = true;
                    nodes[n].Release($"unit:{u}");
                }
                else
                {
                    Interlocked.Increment(ref acquireRejects);
                }
            }
        })));
        sw.Stop();
        Assert.Equal(crashedUnits.Count, failoverWinners.Count);
        Assert.Equal(0, failoverDoubleWins);
        Assert.Equal(units, completed.Count);   // every unit completed exactly once

        // ---- close-with-failed-claims: exactly ONE node closes the crawl, and a
        // retry by the winner stays a win (idempotent for the owner).
        var closeState = new FileStateStore("R2HaStorm",
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));
        var closeWins = nodes.Count(nd => nd.TryCloseCrawl("full:storm", anyFailedClaims: false, closeState, t1));
        Assert.Equal(1, closeWins);
        var winner = nodes.Single(nd => nd.NodeId == closeState.GetValue("crawl_closed_by_full:storm"));
        Assert.True(winner.TryCloseCrawl("full:storm", false, closeState, t1));   // retry stays a win
        Assert.Equal("closed", closeState.GetValue("crawl_status_full:storm"));

        // ---- invariants over the SHARED database: nothing lost, nothing doubled,
        // no unhandled lock errors anywhere in the storm.
        Assert.Equal(0, workerViolations);          // never two live crawlers on one unit
        Assert.True(sqliteErrors.IsEmpty,
            $"SQLite errors under storm: {string.Join(" | ", sqliteErrors.Take(3))}");
        using (var check = new SqliteIdentityStore(db))
        {
            Assert.Equal(units * itemsPerUnit, check.CountIngestedItems());   // no lost writes
            var byUnit = check.ListIngestedItems().GroupBy(i => i.ItemId[..3]).ToList();
            Assert.Equal(units, byUnit.Count);
            foreach (var g in byUnit)
                Assert.Equal(itemsPerUnit, g.Count());
            Assert.DoesNotContain(check.ListItemsWithAclHashOtherThan("zzz-none"),
                i => !i.AclHash.StartsWith("h-final-", StringComparison.Ordinal));
            // reverse index survived the storm too
            Assert.Single(check.ListItemsForSubject("u00-subj00"));
        }
        for (var n = 0; n < nodeCount; n++)
            stores[n].Dispose();

        StressLog.Line(_out,
            $"[R2S6] HA storm: {nodeCount} nodes × {units} units on ONE SQLite DB — {acquireWins} lease wins / " +
            $"{acquireRejects} contended rejects / {completedSkips} completed-unit skips, {crashedUnits.Count} " +
            $"failovers (all after TTL expiry, {earlyProbeRejects} early-takeover attempts blocked), " +
            $"0 double-crawls, {itemWrites:N0} writes ({itemWrites / sw.Elapsed.TotalSeconds:N0}/s), " +
            $"rows {units * itemsPerUnit:N0}/{units * itemsPerUnit:N0} present, 0 'database is locked' errors, " +
            "close won exactly once (idempotent retry OK)");
    }
}
