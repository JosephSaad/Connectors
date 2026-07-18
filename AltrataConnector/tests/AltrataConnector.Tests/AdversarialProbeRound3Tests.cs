// AdversarialProbeRound3Tests.cs
// ==============================
// INDEPENDENT adversarial verification of the round-3 dead-letter TOCTOU fix
// (IStateStore.MutateDeadLetters + its use in RetryFailedCoreAsync and the
// forget-subject scrub). These probes are written to DISPROVE the fix: each one
// FAILS if the read-modify-write is not truly atomic, or if a concurrently
// appended record (most dangerously a compensating erasure DELETE) is lost.
//
// Why these add coverage beyond StressRound3Tests.R3S4a: that test's concurrent
// scrubber filters on a subject that is NEVER produced, so its transform never
// actually removes a live row — it would still pass even if MutateDeadLetters
// read the queue OUTSIDE the lock. The probes below use transforms that drop
// REAL rows while appends race, and drive the actual retry finalize with a
// record appended mid-replay.
//
// Offline/in-process only.

using System.Collections.Concurrent;
using AltrataConnector.Altrata;
using AltrataConnector.Commands;
using AltrataConnector.Entitlement;
using AltrataConnector.Graph;
using AltrataConnector.Identity;
using AltrataConnector.Infrastructure;
using AltrataConnector.State;
using Xunit;

namespace AltrataConnector.Tests;

/// <summary>Decorator over FakeGraphClient that fires a one-shot hook the first
/// time DeleteItemAsync is called — used to simulate a producer appending a new
/// dead-letter DURING the retry replay window (between snapshot and finalize).</summary>
internal sealed class HookingGraphClient : IGraphClient
{
    private readonly FakeGraphClient _inner;
    private readonly Action _onFirstDelete;
    private int _fired;

    public HookingGraphClient(FakeGraphClient inner, Action onFirstDelete)
    {
        _inner = inner;
        _onFirstDelete = onFirstDelete;
    }

    public FakeGraphClient Inner => _inner;

    public Task DeleteItemAsync(string itemId, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _fired, 1) == 0)
            _onFirstDelete();
        return _inner.DeleteItemAsync(itemId, ct);
    }

    // straight delegation for the rest of the interface
    public Task EnsureConnectionAsync(CancellationToken ct = default) => _inner.EnsureConnectionAsync(ct);
    public Task<bool> ConnectionExistsAsync(CancellationToken ct = default) => _inner.ConnectionExistsAsync(ct);
    public Task RegisterSchemaAsync(GraphSchema schema, CancellationToken ct = default) => _inner.RegisterSchemaAsync(schema, ct);
    public Task PutItemAsync(ExternalItem item, CancellationToken ct = default) => _inner.PutItemAsync(item, ct);
    public Task<IReadOnlyList<BatchOpResult>> PutItemsBatchAsync(IReadOnlyList<ExternalItem> items, CancellationToken ct = default) => _inner.PutItemsBatchAsync(items, ct);
    public Task UpdateItemAclAsync(string itemId, IReadOnlyList<AclEntry> acl, CancellationToken ct = default) => _inner.UpdateItemAclAsync(itemId, acl, ct);
    public Task<IReadOnlyList<BatchOpResult>> UpdateItemAclsBatchAsync(IReadOnlyList<AclUpdate> updates, CancellationToken ct = default) => _inner.UpdateItemAclsBatchAsync(updates, ct);
    public Task<IReadOnlyList<BatchOpResult>> DeleteItemsBatchAsync(IReadOnlyList<string> itemIds, CancellationToken ct = default) => _inner.DeleteItemsBatchAsync(itemIds, ct);
}

public class AdversarialProbeRound3Tests : IDisposable
{
    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DeadLetterPolicy.EnvVar, null);
        ServiceStop.ResetForTests();
    }

    private static DeadLetterRecord DeleteRecord(string itemId, string subject) => new()
    {
        ItemId = itemId,
        Dataset = "PersonProfile",
        DeliveryId = "d1",
        Op = DeadLetterOps.Delete,
        Error = "queued erasure completion",
        SubjectHashes = new[] { DeadLetterPolicy.HashSubject(subject) },
    };

    // ---------------------------------------------------------------------------
    // PROBE 1 — retry-failed's atomic finalize must NOT drop a dead-letter that a
    // producer appends DURING the replay window. This is the exact failure the
    // round-3 fix claims to close ("a whole-queue overwrite would silently drop
    // it"). If RetryFailedCoreAsync used ReplaceDeadLetters(keep)/ClearDeadLetters
    // instead of the budget-based MutateDeadLetters, the appended record vanishes
    // and this test fails.
    // ---------------------------------------------------------------------------
    [Fact]
    public async Task Retry_finalize_preserves_a_deadletter_appended_during_replay()
    {
        ServiceStop.ResetForTests();
        var root = TestFixtures.NewTempDir("probe1");
        var config = TestFixtures.NewConfig();
        var inner = new FakeGraphClient();

        // Build the state store first so the concurrent-producer hook can append
        // to the SAME queue the runtime uses (Runtime is not a record, so we wire
        // the hooking Graph client at construction).
        var state = new FileStateStore(config.ConnectorId,
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));
        var identity = new SqliteIdentityStore(Path.Combine(root, "data", "identity.db"));

        // The concurrent producer: on the first replayed DELETE, append a BRAND
        // NEW compensating erasure DELETE that was NOT in retry's snapshot.
        var hook = new HookingGraphClient(inner, () =>
            state.AddDeadLetter(DeleteRecord("PersonProfile-CONCURRENT", "SUBJ-CONCURRENT")));

        var runtime = new Runtime
        {
            Config = config,
            State = state,
            Identity = identity,
            Graph = hook,
            Seats = new SeatService(config, identity, state),
            Alerts = new Alerting(config.ConnectorId),
            Audit = new AuditLog(config.ConnectorId, logsDir: Path.Combine(root, "logs")),
            Erasure = new ErasureLedger(config.ConnectorId, logsDir: Path.Combine(root, "logs")),
            GraphBreaker = new CircuitBreaker("graph", new CircuitBreakerOptions { Critical = true }),
            ApiBreaker = new CircuitBreaker("altrata-api", new CircuitBreakerOptions { Critical = false }),
        };

        // Seed two replayable DELETEs that will succeed.
        runtime.State.AddDeadLetter(DeleteRecord("PersonProfile-A", "SUBJ-A"));
        runtime.State.AddDeadLetter(DeleteRecord("PersonProfile-B", "SUBJ-B"));

        var result = await CommandRegistry.RetryFailedAsync(runtime, clearOnSuccess: true);

        var remaining = runtime.State.ReadDeadLetters();
        // The two snapshot DELETEs replayed and were removed; the concurrently
        // appended DELETE MUST survive (it was never processed and must not be
        // clobbered by the finalize).
        Assert.Single(remaining);
        Assert.Equal("PersonProfile-CONCURRENT", remaining[0].ItemId);
        // The concurrent record was NOT sent to Graph (it wasn't in the snapshot).
        Assert.Contains("PersonProfile-A", inner.DeletedItems);
        Assert.Contains("PersonProfile-B", inner.DeletedItems);
        Assert.DoesNotContain("PersonProfile-CONCURRENT", inner.DeletedItems);
        // retry-failed reports remaining==0 for what it processed (the survivor
        // arrived after its snapshot), so the run is a success.
        Assert.Equal(true, result);
    }

    // ---------------------------------------------------------------------------
    // PROBE 2 — forget-subject must scrub the erased subject's UPSERT (its
    // profile must not stay at rest / be replayed) but KEEP a queued compensating
    // erasure DELETE (dropping it would "leave a withdrawn subject live and
    // untracked" — the exact hazard named in IStateStore.MutateDeadLetters). This
    // pins the scrub transform's DELETE-exemption.
    // ---------------------------------------------------------------------------
    [Fact]
    public async Task Forget_subject_scrubs_upsert_but_keeps_compensating_delete()
    {
        ServiceStop.ResetForTests();
        var root = TestFixtures.NewTempDir("probe2");
        var config = TestFixtures.NewConfig();
        var graph = new FakeGraphClient();
        var runtime = TestFixtures.NewRuntime(config, graph, root);

        // Queue for subject P1: a redacted UPSERT (noise / profile ref) AND a
        // compensating erasure DELETE. Plus an unrelated subject's UPSERT.
        var p1Hash = DeadLetterPolicy.HashSubject("P1");
        runtime.State.AddDeadLetter(new DeadLetterRecord
        {
            ItemId = "PersonProfile-P1", Dataset = "PersonProfile", DeliveryId = "d1",
            Op = DeadLetterOps.Upsert, Redacted = true, Error = "HTTP 503",
            SubjectHashes = new[] { p1Hash },
        });
        runtime.State.AddDeadLetter(DeleteRecord("PersonProfile-P1", "P1"));  // compensating DELETE
        runtime.State.AddDeadLetter(new DeadLetterRecord
        {
            ItemId = "PersonProfile-P2", Dataset = "PersonProfile", DeliveryId = "d1",
            Op = DeadLetterOps.Upsert, Redacted = true, Error = "HTTP 503",
            SubjectHashes = new[] { DeadLetterPolicy.HashSubject("P2") },
        });

        var ok = await CommandRegistry.ForgetSubjectAsync(
            runtime, altrataId: "P1", email: null, actor: "joseph", confirm: true);
        Assert.Equal(true, ok);

        var after = runtime.State.ReadDeadLetters();
        // P1's UPSERT scrubbed; P1's DELETE kept; P2's UPSERT untouched.
        Assert.DoesNotContain(after, r => r.ItemId == "PersonProfile-P1" && r.Op == DeadLetterOps.Upsert);
        Assert.Contains(after, r => r.ItemId == "PersonProfile-P1" && r.Op == DeadLetterOps.Delete);
        Assert.Contains(after, r => r.ItemId == "PersonProfile-P2");
        Assert.True(runtime.State.IsSubjectSuppressed("P1"));
    }

    // ---------------------------------------------------------------------------
    // PROBE 3 — the MutateDeadLetters primitive under a REAL row-dropping
    // transform racing many appenders. Unlike R3S4a (whose scrubber targets a
    // never-produced subject and so removes nothing), this mutator repeatedly
    // deletes live pre-seeded "victim" rows while appenders add disjoint rows. If
    // the read and the write were not under one lock, an appended row landing
    // between a stale read and the overwrite would be lost — the final appended
    // count would fall short.
    // ---------------------------------------------------------------------------
    [Fact]
    public async Task MutateDeadLetters_drops_targeted_rows_without_losing_concurrent_appends()
    {
        var root = TestFixtures.NewTempDir("probe3");
        var state = new FileStateStore("Probe3",
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));

        var victimHash = DeadLetterPolicy.HashSubject("VICTIM");
        const int victims = 200;
        for (var i = 0; i < victims; i++)
            state.AddDeadLetter(DeleteRecord($"VICTIM-{i}", "VICTIM"));

        const int producers = 12;
        const int perProducer = 500;   // 6,000 disjoint appended rows
        var expected = new ConcurrentDictionary<string, byte>();

        using var stop = new CancellationTokenSource();
        var mutations = 0;
        // Mutator: repeatedly remove every VICTIM row (a live, row-dropping RMW)
        // while the appenders run.
        var mutator = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                state.MutateDeadLetters(cur => cur.Where(r => !r.SubjectHashes.Contains(victimHash)));
                Interlocked.Increment(ref mutations);
            }
        });

        Parallel.For(0, producers, new ParallelOptions { MaxDegreeOfParallelism = producers }, p =>
        {
            for (var i = 0; i < perProducer; i++)
            {
                var id = $"APP-{p:D2}-{i:D4}";
                expected[id] = 1;
                state.AddDeadLetter(DeleteRecord(id, $"S-{id}"));
            }
        });
        stop.Cancel();
        await mutator;
        // One final scrub after all appends land, so all victims are gone.
        state.MutateDeadLetters(cur => cur.Where(r => !r.SubjectHashes.Contains(victimHash)));

        var final = state.ReadDeadLetters();
        var ids = final.Select(r => r.ItemId).ToHashSet(StringComparer.Ordinal);
        // No victim survives.
        Assert.DoesNotContain(final, r => r.SubjectHashes.Contains(victimHash));
        // EVERY appended row survives — not one lost to the racing overwrite.
        Assert.Equal(producers * perProducer, final.Count);
        Assert.True(expected.Keys.All(ids.Contains), "a concurrently appended record was lost");
    }
}
