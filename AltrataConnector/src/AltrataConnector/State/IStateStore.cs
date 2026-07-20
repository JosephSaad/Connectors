// State/IStateStore.cs
// --------------------
// All non-identity connector state behind one interface:
//   * crawl checkpoint (crash resume)
//   * last-sync timestamps (full / incremental)
//   * dead-letter queue
//   * processed-delivery ledger
//   * generic key/value state (seat-list hash, billable lookup counter, ...)
//
// Default backend: FileStateStore (JSON + JSONL on disk).
// USE_SQL_SERVER=true: SqlStateStore (everything in SQL Server; required for HA).
//
// VALUE-DOMAIN DIVERGENCE BETWEEN THE BACKENDS — KNOWN AND OPEN.
//
// The two backends are interchangeable only over the values BOTH can store,
// return and compare identically. That set is smaller than "any .NET string":
// the file backend's JSON writer rewrites unpaired UTF-16 surrogates to U+FFFD,
// while NVARCHAR stores them verbatim; NVARCHAR is length-bounded and the file
// backend is not; and SQL's `=` blank-pads trailing spaces away while ordinal
// comparison does not. Values outside that set ARE accepted by whichever backend
// is configured and silently altered — which, on the DSAR suppression list,
// files an erasure under an id the subject does not have.
//
// NOTHING VALIDATES OR REJECTS. A previous round enforced the domain at the
// boundary of both implementations, throwing on an out-of-domain mutation. That
// rejection wedged every read-modify-write over state containing legacy
// out-of-domain values — including the forget-subject dead-letter scrub, which
// then left an erasure HALF-APPLIED (subject suppressed, payload still on disk)
// — so it was withdrawn rather than replaced. Implementations of this interface
// accept what they are given.
//
// The surviving normalisations never fail: NVARCHAR(MAX) free text (error,
// payload JSON, KV values) has unpaired surrogates replaced with U+FFFD so both
// backends persist the same replacement, and stored timestamps get Kind=Utc
// stamped. Neither can turn a recoverable failure into a lost failure record.
//
// The open divergences are enumerated in State/StateContract.cs and
// docs/SQL_CONTRACT.md. An operator choosing a backend, or migrating between
// them, needs to read them.

namespace AltrataConnector.State;

public interface IStateStore
{
    // ---- checkpoint ---------------------------------------------------------
    CrawlCheckpoint? GetCheckpoint();
    void SaveCheckpoint(CrawlCheckpoint checkpoint);
    void ClearCheckpoint();

    // ---- sync timestamps ------------------------------------------------------
    DateTime? GetLastSync(string kind);
    void SetLastSync(string kind, DateTime utc);

    // ---- dead letter ------------------------------------------------------------
    void AddDeadLetter(DeadLetterRecord record);

    /// <summary>Append a batch of records atomically with respect to other
    /// writers (one lock acquisition; a failed sub-batch lands contiguously).</summary>
    void AddDeadLetters(IReadOnlyCollection<DeadLetterRecord> records)
    {
        foreach (var record in records)
            AddDeadLetter(record);
    }
    IReadOnlyList<DeadLetterRecord> ReadDeadLetters();
    void ReplaceDeadLetters(IEnumerable<DeadLetterRecord> records);
    void ClearDeadLetters();

    /// <summary>
    /// Atomically read-modify-write the dead-letter queue under the SAME
    /// per-file / per-table lock that guards <see cref="AddDeadLetter"/>:
    /// <paramref name="transform"/> receives the CURRENT records and returns the
    /// records to persist. This closes the ReadDeadLetters()+ReplaceDeadLetters()
    /// TOCTOU — a concurrent AddDeadLetter append landing between a stale read
    /// and a whole-queue overwrite would otherwise be silently lost (e.g. a
    /// crawl's compensating erasure DELETE dropped by a racing forget-subject
    /// scrub, leaving a withdrawn subject live and untracked). Implementations
    /// MUST run the read and the write without releasing the queue lock.
    /// </summary>
    void MutateDeadLetters(
        Func<IReadOnlyList<DeadLetterRecord>, IEnumerable<DeadLetterRecord>> transform);

    // ---- delivery ledger ----------------------------------------------------------
    bool IsDeliveryProcessed(string deliveryId);
    void MarkDeliveryProcessed(string deliveryId, DateTime utc);
    IReadOnlyList<string> ListProcessedDeliveries();

    // ---- key/value ---------------------------------------------------------------
    string? GetValue(string key);
    void SetValue(string key, string? value);

    /// <summary>
    /// Atomic read-modify-write of ONE key/value entry: <paramref name="transform"/>
    /// receives the current value (null when absent) and returns the value to
    /// persist (null deletes). The read and the write MUST happen inside a single
    /// acquisition of the backend's lock, so a concurrent writer cannot slip
    /// between a stale read and the write-back.
    ///
    /// This is what makes the usage ceiling (Altrata/UsageBudget.cs) an actual
    /// ceiling: a GetValue+SetValue pair would let N concurrent lookups each read
    /// the same "used" figure and each conclude there was room, overshooting the
    /// limit by up to N-1 billable calls. Both shipped backends override this —
    /// FileStateStore under its process lock, SqlStateStore under
    /// UPDLOCK/HOLDLOCK in a transaction (so it holds ACROSS nodes too).
    ///
    /// The default implementation below is the naive non-atomic pair; it exists
    /// only so third-party/test IStateStore implementations keep compiling, and
    /// it is safe for single-threaded callers.
    /// </summary>
    string? MutateValue(string key, Func<string?, string?> transform)
    {
        var updated = transform(GetValue(key));
        SetValue(key, updated);
        return updated;
    }

    // ---- billable lookup counter ----------------------------------------------------
    long GetBillableLookupCount();
    long IncrementBillableLookups(long delta = 1);

    // ---- suppression list (per-subject erasure durability) ---------------------------
    /// <summary>Record an erased subject id so future crawls never re-ingest them.</summary>
    void AddSuppressedSubject(string subjectId);
    /// <summary>Un-suppress a subject (allow re-ingestion again).</summary>
    void RemoveSuppressedSubject(string subjectId);
    bool IsSubjectSuppressed(string subjectId);
    IReadOnlyList<string> ListSuppressedSubjects();

    /// <summary>Wipe everything (purge-all).</summary>
    void WipeAll();
}

/// <summary>Well-known key/value state keys.</summary>
public static class StateKeys
{
    public const string SeatListHash = "seat_list_hash";
    public const string BillableLookups = "billable_lookups";

    /// <summary>Usage-ceiling ledger (daily counter + rolling buckets) — one JSON
    /// document so both ceilings are charged in one atomic mutation.</summary>
    public const string UsageBudget = "usage_budget";
}
