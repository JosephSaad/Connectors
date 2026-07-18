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
}
