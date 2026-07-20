// VerifierProbeRound4Tests.cs
// ---------------------------
// Regression tests derived directly from the adversarial verifier's probes.
// Each test below FAILED against the pre-fix tree; the comment on each names
// the exact defect it pins so a future refactor that reintroduces it is caught.
//
//   R4-1  StateDoc.SuppressedSubjects lost its StringComparer.Ordinal on every
//         JSON round-trip (System.Text.Json discards the property initializer
//         and constructs a default SortedSet). DSAR erasure suppression — the
//         RPO-0 asset — silently switched to CULTURE-sensitive equality.
//   R4-2  AtomicWrite used a shared "<path>.tmp" for every writer.
//   R4-3  FileStateStore._sync was per-instance while the file header and the
//         MutateValue contract promised process-wide exclusion.
//   R4-4  AltrataApiClient set 'billed' only AFTER IncrementBillableLookups
//         returned, so a failed counter write released a reservation for a call
//         that had really been billed.
//   R4-5  SqlStateStore.Execute retried the WHOLE operation on a transient
//         fault, including one raised after a charge had already committed.

using System.Collections.Concurrent;
using System.Text.Json;
using AltrataConnector.Altrata;
using AltrataConnector.Config;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

// ============================================================================
// R4-1 — the erasure suppression list must keep ORDINAL semantics across a
//        reload from disk.
// ============================================================================

public class SuppressionComparerRoundTripTests
{
    /// <summary>Two subject ids that are DISTINCT under ordinal comparison but
    /// EQUAL under the culture-sensitive default comparer that System.Text.Json
    /// silently substituted. U+00AD (soft hyphen) is collation-ignorable, so
    /// Comparer&lt;string&gt;.Default.Compare(Plain, Confusable) == 0 while
    /// StringComparer.Ordinal.Compare(...) != 0. Verified empirically on this
    /// runtime before the fix was written.</summary>
    private const string Plain = "ALT-9001";
    private const string Confusable = "ALT-9001­";

    private static FileStateStore NewStore(string root) =>
        new("AltrataTest", logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));

    [Fact]
    public void TheTwoProbeIdsAreOrdinallyDistinctButCultureEqual()
    {
        // Guards the premise of every test below. If a future runtime changes
        // ICU collation such that these no longer collide, this fails loudly
        // and tells the next reader to pick a new pair rather than leaving the
        // regression tests silently vacuous.
        Assert.NotEqual(0, StringComparer.Ordinal.Compare(Plain, Confusable));
        Assert.Equal(0, Comparer<string>.Default.Compare(Plain, Confusable));
    }

    [Fact]
    public void SuppressedSetKeepsOrdinalComparerAfterReloadFromDisk()
    {
        var root = TestFixtures.NewTempDir("suppress_cmp");
        var store = NewStore(root);
        store.AddSuppressedSubject(Plain);

        // The state file now exists, so every subsequent op goes through
        // LoadDoc — i.e. through the JSON round-trip that used to drop the
        // comparer. Assert the comparer identity itself, not just a symptom.
        Assert.True(File.Exists(store.StatePath));
        Assert.Same(StringComparer.Ordinal, store.LoadStateDocument().SuppressedSubjects.Comparer);
    }

    [Fact]
    public void SuppressedSetKeepsOrdinalComparerWhenLoadedByAFreshInstance()
    {
        // The DR case: the process that wrote the file is gone and a new one
        // reads it back. Nothing but the on-disk bytes carries the semantics.
        var root = TestFixtures.NewTempDir("suppress_cmp_dr");
        NewStore(root).AddSuppressedSubject(Plain);

        var reloaded = NewStore(root);
        Assert.Same(StringComparer.Ordinal, reloaded.LoadStateDocument().SuppressedSubjects.Comparer);
    }

    [Fact]
    public void AnUnerasedSubjectIsNotReportedSuppressedByCultureCollision()
    {
        // FALSE POSITIVE direction: only Confusable was ever erased, but under
        // the default comparer the ordinally-distinct Plain also answered
        // "suppressed" — blocking ingestion of a subject nobody erased.
        var root = TestFixtures.NewTempDir("suppress_falsepos");
        var store = NewStore(root);
        store.AddSuppressedSubject(Confusable);

        Assert.True(store.IsSubjectSuppressed(Confusable));
        Assert.False(store.IsSubjectSuppressed(Plain));
    }

    [Fact]
    public void TwoOrdinallyDistinctErasuresBothPersistAndBothSurviveReload()
    {
        // FALSE NEGATIVE direction, and the severe one. Pre-fix, the reloaded
        // set compared culture-sensitively, so SortedSet.Add(Confusable)
        // returned false against the already-present Plain: the second erasure
        // was NEVER WRITTEN TO DISK and never appeared in the DSAR listing.
        var root = TestFixtures.NewTempDir("suppress_collapse");
        var store = NewStore(root);
        store.AddSuppressedSubject(Plain);
        store.AddSuppressedSubject(Confusable);

        var listed = NewStore(root).ListSuppressedSubjects();
        Assert.Equal(2, listed.Count);
        Assert.Contains(Plain, listed);
        Assert.Contains(Confusable, listed);
    }

    [Fact]
    public void UnsuppressingOneSubjectNeverUnsuppressesAnOrdinallyDistinctOne()
    {
        // The end-to-end erasure failure the collapse caused: with only one
        // entry physically on disk, removing it left the OTHER erased subject
        // unsuppressed — i.e. free to be re-ingested by the next crawl.
        var root = TestFixtures.NewTempDir("suppress_remove");
        var store = NewStore(root);
        store.AddSuppressedSubject(Plain);
        store.AddSuppressedSubject(Confusable);

        store.RemoveSuppressedSubject(Plain);

        var reloaded = NewStore(root);
        Assert.False(reloaded.IsSubjectSuppressed(Plain));
        Assert.True(reloaded.IsSubjectSuppressed(Confusable));   // still erased
    }

    [Fact]
    public void CrawlSuppressionCheckStillMatchesAfterReload()
    {
        // CrawlEngine rebuilds an Ordinal HashSet from ListSuppressedSubjects()
        // (Ingestion/CrawlEngine.cs) and tests item subjects against it. This
        // pins the contract that consumer depends on: everything that was
        // erased is in the list, byte-for-byte.
        var root = TestFixtures.NewTempDir("suppress_crawl");
        var store = NewStore(root);
        foreach (var id in new[] { Plain, Confusable, "ALT-1", "alt-1", "ALT-‍1" })
            store.AddSuppressedSubject(id);

        var asCrawlSeesIt = new HashSet<string>(
            NewStore(root).ListSuppressedSubjects(), StringComparer.Ordinal);

        foreach (var id in new[] { Plain, Confusable, "ALT-1", "alt-1", "ALT-‍1" })
            Assert.Contains(id, asCrawlSeesIt);
    }

    [Fact]
    public void ListingOrderIsOrdinalAndStableAcrossReload()
    {
        // Ordering is part of the contract too: a culture-collated listing is
        // not reproducible across hosts/locales, which breaks DR comparison of
        // two nodes' suppression lists.
        var root = TestFixtures.NewTempDir("suppress_order");
        var store = NewStore(root);
        foreach (var id in new[] { "co-op", "coop", "COOP", "ALT-2", "ALT-10" })
            store.AddSuppressedSubject(id);

        var listed = NewStore(root).ListSuppressedSubjects();
        Assert.Equal(listed.OrderBy(x => x, StringComparer.Ordinal).ToList(), listed);
    }

    [Fact]
    public void HandWrittenStateFileIsNormalisedToOrdinalOnLoad()
    {
        // An operator-restored / hand-edited file is the DR path. Even when the
        // array arrives out of order and holds culture-colliding ids, the load
        // must reconstruct ordinal semantics and keep every distinct id.
        var root = TestFixtures.NewTempDir("suppress_handwritten");
        var dataDir = Path.Combine(root, "data");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "AltrataTest_state.json"), $$"""
            {
              "LastSync": {},
              "ProcessedDeliveries": {},
              "Values": {},
              "BillableLookups": 0,
              "SuppressedSubjects": ["{{Confusable}}", "{{Plain}}", "zzz", "aaa"]
            }
            """);

        var store = NewStore(root);
        Assert.Same(StringComparer.Ordinal, store.LoadStateDocument().SuppressedSubjects.Comparer);
        Assert.Equal(4, store.ListSuppressedSubjects().Count);
        Assert.True(store.IsSubjectSuppressed(Plain));
        Assert.True(store.IsSubjectSuppressed(Confusable));
    }

    [Fact]
    public void SuppressionSurvivesRoundTripAlongsideEveryOtherStateField()
    {
        // The comparer fix must not have changed the persisted JSON shape:
        // everything else in the document still round-trips.
        var root = TestFixtures.NewTempDir("suppress_shape");
        var store = NewStore(root);
        store.SetLastSync("full", new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc));
        store.MarkDeliveryProcessed("D1", DateTime.UtcNow);
        store.SetValue("k", "v");
        store.IncrementBillableLookups(5);
        store.AddSuppressedSubject(Plain);

        var reloaded = NewStore(root);
        Assert.NotNull(reloaded.GetLastSync("full"));
        Assert.True(reloaded.IsDeliveryProcessed("D1"));
        Assert.Equal("v", reloaded.GetValue("k"));
        Assert.Equal(5, reloaded.GetBillableLookupCount());
        Assert.True(reloaded.IsSubjectSuppressed(Plain));

        // The on-disk array is still a plain JSON array of strings.
        using var doc = JsonDocument.Parse(File.ReadAllText(reloaded.StatePath));
        var array = doc.RootElement.GetProperty("SuppressedSubjects");
        Assert.Equal(JsonValueKind.Array, array.ValueKind);
        Assert.Equal(Plain, array[0].GetString());
    }
}

// ============================================================================
// R4-2 — AtomicWrite must not share one temp path between writers.
// ============================================================================

public class AtomicWriteTempPathTests
{
    private static FileStateStore NewStore(string root, string id = "AltrataTest") =>
        new(id, logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));

    [Fact]
    public void ConcurrentWritersOverOneStateFileNeverCorruptItOrThrow()
    {
        // Two FileStateStore instances = the operator running ingest-item while
        // a crawl runs, same CONNECTOR_ID, same file. Pre-fix both raced on
        // "<path>.tmp": one writer's File.Move could publish the OTHER's
        // half-written bytes, or fail outright because the shared temp had
        // already been moved away.
        var root = TestFixtures.NewTempDir("atomic_write");
        var a = NewStore(root);
        var b = NewStore(root);
        a.SetValue("seed", "seed");

        var failures = new ConcurrentBag<Exception>();
        var barrier = new Barrier(2);

        void Hammer(FileStateStore store, string key)
        {
            barrier.SignalAndWait();
            for (var i = 0; i < 150; i++)
            {
                try { store.SetValue(key, new string('x', 500 + i)); }
                catch (Exception exc) { failures.Add(exc); }
            }
        }

        var t1 = new Thread(() => Hammer(a, "a"));
        var t2 = new Thread(() => Hammer(b, "b"));
        t1.Start(); t2.Start();
        t1.Join(); t2.Join();

        Assert.Empty(failures);

        // The file must still be valid JSON with both writers' keys present.
        var reloaded = NewStore(root);
        Assert.Equal("seed", reloaded.GetValue("seed"));
        Assert.NotNull(reloaded.GetValue("a"));
        Assert.NotNull(reloaded.GetValue("b"));
    }

    [Fact]
    public void NoTempFilesAreLeftBehindAfterWrites()
    {
        var root = TestFixtures.NewTempDir("atomic_write_litter");
        var store = NewStore(root);
        for (var i = 0; i < 20; i++)
            store.SetValue($"k{i}", $"v{i}");
        store.SaveCheckpoint(new CrawlCheckpoint
        {
            DeliveryId = "D1", Dataset = "person_profile", FileName = "f.json",
            RecordIndex = 3, UpdatedUtc = DateTime.UtcNow,
        });

        var litter = Directory.GetFiles(Path.Combine(root, "data"), "*.tmp*")
            .Concat(Directory.GetFiles(Path.Combine(root, "logs"), "*.tmp*"))
            .ToList();
        Assert.Empty(litter);
    }

    [Fact]
    public void AFailedWriteDoesNotLeaveATempFileOrDamageThePreviousState()
    {
        // Serializing an unwritable payload is not reachable through the public
        // API, so drive AtomicWrite directly: a mid-write failure must clean up
        // its own temp and leave the last good state untouched.
        var root = TestFixtures.NewTempDir("atomic_write_fail");
        var store = NewStore(root);
        store.SetValue("k", "good");

        var target = Path.Combine(root, "data", "sub", "deep", "file.json");
        Assert.Throws<IOException>(() => FileStateStore.AtomicWriteForTests(target, "x", failBeforeMove: true));

        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!, "*.tmp*"));
        Assert.Equal("good", NewStore(root).GetValue("k"));
    }
}

// ============================================================================
// R4-3 — the "process-wide lock" the header and MutateValue promise must be real.
// ============================================================================

public class ProcessWideStateLockTests
{
    private static FileStateStore NewStore(string root, string id = "AltrataTest") =>
        new(id, logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));

    [Fact]
    public void TwoInstancesOverOneFileShareTheSameStateLock()
    {
        var root = TestFixtures.NewTempDir("state_lock_identity");
        Assert.Same(NewStore(root).StateLockForTests, NewStore(root).StateLockForTests);
    }

    [Fact]
    public void DifferentStateFilesDoNotShareALock()
    {
        // The lock must be keyed by the resolved file, not global — two
        // connectors must not serialise against each other.
        var root = TestFixtures.NewTempDir("state_lock_distinct");
        Assert.NotSame(NewStore(root, "AltrataA").StateLockForTests,
                       NewStore(root, "AltrataB").StateLockForTests);
    }

    [Fact]
    public void MutateValueIsAtomicAcrossSeparateInstancesOverOneFile()
    {
        // The claim MutateValue's doc-comment makes, stated as a test: two
        // usage-ceiling reservations running through DIFFERENT store instances
        // must not both observe the same pre-increment count. Pre-fix each
        // instance held its own object and lost increments.
        var root = TestFixtures.NewTempDir("state_lock_mutate");
        const int perThread = 200;
        var stores = new[] { NewStore(root), NewStore(root), NewStore(root), NewStore(root) };
        stores[0].SetValue("counter", "0");

        var threads = stores.Select(store => new Thread(() =>
        {
            for (var i = 0; i < perThread; i++)
                store.MutateValue("counter", raw => (long.Parse(raw ?? "0") + 1).ToString());
        })).ToList();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        Assert.Equal((stores.Length * perThread).ToString(), NewStore(root).GetValue("counter"));
    }

    [Fact]
    public void IncrementBillableLookupsIsAtomicAcrossSeparateInstances()
    {
        var root = TestFixtures.NewTempDir("state_lock_billable");
        const int perThread = 150;
        var stores = new[] { NewStore(root), NewStore(root), NewStore(root) };

        var threads = stores.Select(store => new Thread(() =>
        {
            for (var i = 0; i < perThread; i++)
                store.IncrementBillableLookups();
        })).ToList();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        Assert.Equal(stores.Length * perThread, NewStore(root).GetBillableLookupCount());
    }

    [Fact]
    public void ConcurrentErasureSuppressionAcrossInstancesLosesNothing()
    {
        // The RPO-0 asset under the same race: every erasure recorded by any
        // instance must be on disk afterwards.
        var root = TestFixtures.NewTempDir("state_lock_suppress");
        const int perThread = 60;
        var stores = new[] { NewStore(root), NewStore(root), NewStore(root) };

        var threads = stores.Select((store, s) => new Thread(() =>
        {
            for (var i = 0; i < perThread; i++)
                store.AddSuppressedSubject($"ALT-{s}-{i:D3}");
        })).ToList();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        var listed = NewStore(root).ListSuppressedSubjects();
        Assert.Equal(stores.Length * perThread, listed.Count);
        for (var s = 0; s < stores.Length; s++)
            for (var i = 0; i < perThread; i++)
                Assert.Contains($"ALT-{s}-{i:D3}", listed);
    }
}

// ============================================================================
// R4-4 — a lookup that was really billed must never hand its reservation back.
// ============================================================================

/// <summary>Wraps a real store but fails the billable-counter write, modelling
/// a full disk / permission loss on the state file at exactly the wrong moment.</summary>
internal sealed class IncrementFailingStateStore : IStateStore
{
    private readonly IStateStore _inner;
    public IncrementFailingStateStore(IStateStore inner) => _inner = inner;

    public long IncrementCalls { get; private set; }

    public long IncrementBillableLookups(long delta = 1)
    {
        IncrementCalls++;
        throw new IOException("state file is not writable");
    }

    public CrawlCheckpoint? GetCheckpoint() => _inner.GetCheckpoint();
    public void SaveCheckpoint(CrawlCheckpoint checkpoint) => _inner.SaveCheckpoint(checkpoint);
    public void ClearCheckpoint() => _inner.ClearCheckpoint();
    public DateTime? GetLastSync(string kind) => _inner.GetLastSync(kind);
    public void SetLastSync(string kind, DateTime utc) => _inner.SetLastSync(kind, utc);
    public void AddDeadLetter(DeadLetterRecord record) => _inner.AddDeadLetter(record);
    public void AddDeadLetters(IReadOnlyCollection<DeadLetterRecord> records) => _inner.AddDeadLetters(records);
    public IReadOnlyList<DeadLetterRecord> ReadDeadLetters() => _inner.ReadDeadLetters();
    public void ReplaceDeadLetters(IEnumerable<DeadLetterRecord> records) => _inner.ReplaceDeadLetters(records);
    public void ClearDeadLetters() => _inner.ClearDeadLetters();
    public void MutateDeadLetters(Func<IReadOnlyList<DeadLetterRecord>, IEnumerable<DeadLetterRecord>> transform) =>
        _inner.MutateDeadLetters(transform);
    public bool IsDeliveryProcessed(string deliveryId) => _inner.IsDeliveryProcessed(deliveryId);
    public void MarkDeliveryProcessed(string deliveryId, DateTime utc) => _inner.MarkDeliveryProcessed(deliveryId, utc);
    public IReadOnlyList<string> ListProcessedDeliveries() => _inner.ListProcessedDeliveries();
    public string? GetValue(string key) => _inner.GetValue(key);
    public void SetValue(string key, string? value) => _inner.SetValue(key, value);
    public string? MutateValue(string key, Func<string?, string?> transform) => _inner.MutateValue(key, transform);
    public long GetBillableLookupCount() => _inner.GetBillableLookupCount();
    public void AddSuppressedSubject(string subjectId) => _inner.AddSuppressedSubject(subjectId);
    public void RemoveSuppressedSubject(string subjectId) => _inner.RemoveSuppressedSubject(subjectId);
    public bool IsSubjectSuppressed(string subjectId) => _inner.IsSubjectSuppressed(subjectId);
    public IReadOnlyList<string> ListSuppressedSubjects() => _inner.ListSuppressedSubjects();
    public void WipeAll() => _inner.WipeAll();
}

public class BilledReservationAccountingTests
{
    private static AppConfig ApiConfig(int maxPerDay) => new()
    {
        ConnectorId = "AltrataTest", ConnectorName = "t", ConnectorDescription = "t",
        AadClientId = "c", AadTenantId = "t", AadClientSecret = "s",
        AltrataApiBaseUrl = "https://api.altrata.test/v1",
        AltrataTokenUrl = "https://auth.altrata.test/oauth/token",
        AltrataClientId = "api-client", AltrataClientSecret = "api-secret",
        AltrataMaxLookupsPerDay = maxPerDay,
    };

    private static void ScriptOneLookup(ScriptedHandler handler, string id)
    {
        handler.EnqueueJson(200, """{"access_token":"tok","expires_in":3600}""");
        handler.EnqueueJson(200, $$"""{"id":"{{id}}","person_name":"Ada Lovelace"}""");
    }

    [Fact]
    public async Task ACallThatReached200KeepsItsReservationEvenIfTheCounterWriteFails()
    {
        // The vendor served (and will invoice) the lookup. Pre-fix, 'billed'
        // was still false when IncrementBillableLookups threw, so the exception
        // filter handed the reservation back and the ceiling under-counted a
        // call that was really billed — the ceiling could then be walked past
        // indefinitely by a host whose state file had gone read-only.
        var root = TestFixtures.NewTempDir("billed_accounting");
        var real = new FileStateStore("AltrataTest",
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));
        var state = new IncrementFailingStateStore(real);
        var audit = new AuditLog("AltrataTest", logsDir: Path.Combine(root, "logs"));
        var handler = new ScriptedHandler();
        var config = ApiConfig(maxPerDay: 5);
        var meter = new UsageMeter(real, UsageBudgetOptions.FromConfig(config));
        var client = new AltrataApiClient(config, state, audit, handler,
            (_, _) => Task.CompletedTask, purpose: new PurposePolicy(null), usage: meter);

        var before = meter.Peek(DateTime.UtcNow).DayUsed;
        ScriptOneLookup(handler, "P1");

        await Assert.ThrowsAsync<IOException>(() => client.LookupPersonAsync("P1", "joseph", "RFP"));

        Assert.Equal(1, state.IncrementCalls);
        // Conservative accounting: the reservation stays CHARGED.
        Assert.Equal(before + 1, meter.Peek(DateTime.UtcNow).DayUsed);
    }

    [Fact]
    public async Task RepeatedCounterWriteFailuresStillWalkTheCeilingDownToARefusal()
    {
        // The consequence stated as behaviour: N billed-but-unrecorded lookups
        // must exhaust an N-per-day ceiling, not run forever.
        var root = TestFixtures.NewTempDir("billed_accounting_walk");
        var real = new FileStateStore("AltrataTest",
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));
        var state = new IncrementFailingStateStore(real);
        var audit = new AuditLog("AltrataTest", logsDir: Path.Combine(root, "logs"));
        var handler = new ScriptedHandler();
        var config = ApiConfig(maxPerDay: 3);
        var meter = new UsageMeter(real, UsageBudgetOptions.FromConfig(config));
        var client = new AltrataApiClient(config, state, audit, handler,
            (_, _) => Task.CompletedTask, purpose: new PurposePolicy(null), usage: meter);

        for (var i = 0; i < 3; i++)
        {
            ScriptOneLookup(handler, $"P{i}");
            await Assert.ThrowsAsync<IOException>(() => client.LookupPersonAsync($"P{i}", "joseph", "RFP"));
        }

        // Ceiling reached. Nothing is scripted for the 4th, so a leaked HTTP
        // call would surface as "Unexpected request" instead.
        await Assert.ThrowsAsync<UsageBudgetExceededException>(
            () => client.LookupPersonAsync("P4", "joseph", "RFP"));
    }

    [Fact]
    public async Task AFailureBeforeTheResponseStillReleasesTheReservation()
    {
        // The other side of the contract, unchanged: a lookup that never became
        // billable must still hand its reservation back, or a flapping upstream
        // would burn the allowance without producing a usable result.
        var root = TestFixtures.NewTempDir("billed_accounting_release");
        var state = new FileStateStore("AltrataTest",
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));
        var audit = new AuditLog("AltrataTest", logsDir: Path.Combine(root, "logs"));
        var handler = new ScriptedHandler();
        var config = ApiConfig(maxPerDay: 5);
        var meter = new UsageMeter(state, UsageBudgetOptions.FromConfig(config));
        var client = new AltrataApiClient(config, state, audit, handler,
            (_, _) => Task.CompletedTask, purpose: new PurposePolicy(null), usage: meter);

        handler.EnqueueJson(200, """{"access_token":"tok","expires_in":3600}""");
        handler.EnqueueJson(503, """{"error":"upstream down"}""");

        await Assert.ThrowsAnyAsync<Exception>(() => client.LookupPersonAsync("P1", "joseph", "RFP"));

        Assert.Equal(0, meter.Peek(DateTime.UtcNow).DayUsed);
        Assert.Equal(0, state.GetBillableLookupCount());
    }
}

// ============================================================================
// R4-5 — SqlStateStore.Execute must not re-run an operation that already
//         committed its charge.
// ============================================================================

public class SqlExecuteCommitBarrierTests
{
    [Fact]
    public void AnUncommittedTransientFaultIsRetried()
    {
        // The behaviour that must NOT regress: transient faults raised before
        // anything committed are still retried up to the configured budget.
        Assert.True(SqlStateStore.ShouldRetry(attempt: 0, maxRetries: 3, isTransient: true, committed: false));
        Assert.True(SqlStateStore.ShouldRetry(attempt: 2, maxRetries: 3, isTransient: true, committed: false));
    }

    [Fact]
    public void ACommittedOperationIsNeverRetried()
    {
        // The defect: a transient fault raised AFTER the charge committed (a
        // connection drop during teardown, for instance) re-ran the whole
        // operation — re-running MutateValue's transform and charging the
        // usage ceiling a second time for one lookup.
        Assert.False(SqlStateStore.ShouldRetry(attempt: 0, maxRetries: 3, isTransient: true, committed: true));
        Assert.False(SqlStateStore.ShouldRetry(attempt: 2, maxRetries: 3, isTransient: true, committed: true));
    }

    [Fact]
    public void NonTransientAndExhaustedFaultsAreNotRetried()
    {
        Assert.False(SqlStateStore.ShouldRetry(attempt: 0, maxRetries: 3, isTransient: false, committed: false));
        Assert.False(SqlStateStore.ShouldRetry(attempt: 3, maxRetries: 3, isTransient: true, committed: false));
    }

    [Fact]
    public void TheCommitGuardLatchesAndCannotBeUnset()
    {
        var guard = new SqlStateStore.CommitGuard();
        Assert.False(guard.Committed);
        guard.MarkCommitted();
        Assert.True(guard.Committed);
        guard.MarkCommitted();
        Assert.True(guard.Committed);
    }

    [Fact]
    public void EveryChargingOperationRunsThroughACommitGuardedExecute()
    {
        // Structural pin: the two operations that can charge (MutateValue —
        // which backs the usage ceiling — and IncrementBillableLookups) must
        // use the guard-aware Execute overload. Without this, someone could
        // add the barrier and later have it silently dropped in a refactor.
        var source = File.ReadAllText(Path.Combine(
            TestFixtures.RepoRoot(), "src", "AltrataConnector", "State", "SqlStateStore.cs"));

        foreach (var marker in new[] { "MutateValue", "IncrementBillableLookups" })
        {
            var start = source.IndexOf($"public {(marker == "MutateValue" ? "string?" : "long")} {marker}",
                StringComparison.Ordinal);
            Assert.True(start >= 0, $"{marker} not found in SqlStateStore.cs");
            var body = source.Substring(start, Math.Min(2200, source.Length - start));
            Assert.Contains("guard.MarkCommitted()", body);
        }
    }
}
