// State/FileStateStore.cs
// -----------------------
// Default on-disk state backend:
//
//   logs/checkpoint_{CONNECTOR_ID}.json       crawl checkpoint
//   logs/failed_records_{CONNECTOR_ID}.jsonl  dead-letter queue (append-only JSONL)
//   data/{CONNECTOR_ID}_state.json            sync timestamps, delivery ledger, KV
//
// Writes are atomic (a UNIQUE temp file + File.Move with overwrite) and guarded
// by a genuinely process-wide lock keyed by the resolved file path, so two
// FileStateStore instances over one file mutually exclude. Multi-node writers
// still require the SQL backend instead — this lock is process-scoped only.

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AltrataConnector.Infrastructure;

namespace AltrataConnector.State;

public sealed class FileStateStore : IStateStore
{
    private static readonly IAppLogger Logger = Logging.GetLogger("altrata_connector.state");

    /// <summary>Once-per-process-per-file corruption warnings: these read paths
    /// run on hot/polled loops (every state op re-loads the doc; /metrics polls
    /// the dead-letter queue), so a corrupt file must be LOUD exactly once, not
    /// a firehose. Keyed by full path — each connector/shard file warns itself.</summary>
    private static readonly ConcurrentDictionary<string, bool> CorruptFileWarned =
        new(StringComparer.Ordinal);

    private static void WarnCorruptOnce(string path, string message)
    {
        if (CorruptFileWarned.TryAdd(Path.GetFullPath(path), true))
            Logger.Error(message);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions JsonlOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Process-wide state-file locks, keyed by RESOLVED path — the same
    /// pattern as <see cref="DeadLetterLocks"/> below.
    ///
    /// This used to be a per-INSTANCE object while the file header and
    /// MutateValue's contract both described it as process-wide. That claim was
    /// simply false: two FileStateStore instances over one file (Runtime.Create
    /// builds one per command, shard runtimes build their own, the health
    /// endpoint holds another) excluded nothing. The guarantee is made REAL
    /// rather than documented away because MutateValue's atomicity is
    /// load-bearing — it is the whole reason the usage ceiling is a ceiling, and
    /// a false claim there silently overshoots a billable limit.</summary>
    private static readonly ConcurrentDictionary<string, object> StateLocks =
        new(StringComparer.Ordinal);

    private readonly string _connectorId;
    private readonly string _logsDir;
    private readonly string _dataDir;

    public FileStateStore(string connectorId, string? logsDir = null, string? dataDir = null)
    {
        _connectorId = connectorId;
        _logsDir = logsDir
            ?? Environment.GetEnvironmentVariable("LOGS_DIR")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "logs");
        _dataDir = dataDir
            ?? Environment.GetEnvironmentVariable("DATA_DIR")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "data");
    }

    public string CheckpointPath => Path.Combine(_logsDir, $"checkpoint_{_connectorId}.json");
    public string DeadLetterPath => Path.Combine(_logsDir, $"failed_records_{_connectorId}.jsonl");
    public string StatePath => Path.Combine(_dataDir, $"{_connectorId}_state.json");

    // ---- persistent doc ------------------------------------------------------

    internal sealed class StateDoc
    {
        public Dictionary<string, DateTime> LastSync { get; set; } = new();
        public Dictionary<string, DateTime> ProcessedDeliveries { get; set; } = new();
        public Dictionary<string, string?> Values { get; set; } = new();
        public long BillableLookups { get; set; }

        /// <summary>WIRE shape of the erasure suppression list. Deliberately a
        /// plain List, NOT the SortedSet the rest of the class works with.
        ///
        /// System.Text.Json discards a collection property's initializer: it
        /// constructs a fresh instance with that collection type's DEFAULT
        /// comparer and assigns it. For SortedSet&lt;string&gt; the default is
        /// the CULTURE-sensitive Comparer&lt;string&gt;.Default, so a
        /// `= new(StringComparer.Ordinal)` initializer survived only until the
        /// first reload. Two things then went wrong, and the second is why the
        /// wire type had to change rather than merely re-imposing the comparer
        /// after deserialization:
        ///
        ///   1. EQUALITY changed. Culture collation ignores characters ordinal
        ///      comparison does not — U+00AD, U+200B, U+200D, U+FEFF and so on.
        ///      "ALT-9001" and "ALT-9001­" are distinct subjects ordinally
        ///      and the SAME key culturally. An id nobody erased could answer
        ///      "suppressed"; and because CrawlEngine rebuilds an Ordinal
        ///      HashSet from ListSuppressedSubjects, an id that WAS erased could
        ///      go missing from that set and be re-ingested.
        ///   2. Ids were LOST AT PARSE TIME. Deserialization Adds each array
        ///      element into the default-comparer set, so two ordinally-distinct
        ///      erasures collapsed into one before any repair code could run.
        ///      Un-suppressing the survivor then silently un-suppressed the
        ///      other — an erased subject becoming ingestible again. On the DSAR
        ///      suppression list, which the DR plan classes RPO-0, that is data
        ///      loss with a regulatory edge.
        ///
        /// Reading into a List keeps every byte exactly as written;
        /// <see cref="SuppressedSubjects"/> re-imposes ordinal semantics on
        /// load and <see cref="FlushSuppressed"/> writes them back. The JSON on
        /// disk is unchanged — still an array of strings under this name — so
        /// existing state files and the SQL backend stay compatible.</summary>
        [JsonPropertyName("SuppressedSubjects")]
        public List<string> SuppressedSubjectsRaw { get; set; } = new();

        /// <summary>Erased subject altrata ids — durable across re-delivery.
        /// ORDINAL by construction, on every load, forever.</summary>
        [JsonIgnore]
        public SortedSet<string> SuppressedSubjects { get; private set; } =
            new(StringComparer.Ordinal);

        /// <summary>Rebuild the ordinal working set from the wire list (load).</summary>
        public void RebuildSuppressed() =>
            SuppressedSubjects = new SortedSet<string>(SuppressedSubjectsRaw, StringComparer.Ordinal);

        /// <summary>Project the ordinal working set back onto the wire list
        /// (save). Enumeration is ordinal-ordered, so the persisted array is
        /// reproducible across hosts and locales — which is what makes two
        /// nodes' suppression lists diffable during a DR comparison.</summary>
        public void FlushSuppressed() =>
            SuppressedSubjectsRaw = SuppressedSubjects.ToList();
    }

    private StateDoc LoadDoc()
    {
        if (!File.Exists(StatePath))
            return new StateDoc();
        try
        {
            var doc = JsonSerializer.Deserialize<StateDoc>(File.ReadAllText(StatePath)) ?? new StateDoc();
            // Re-impose the semantics the TYPE declares but the wire cannot
            // carry (see StateDoc.SuppressedSubjectsRaw). Every load goes
            // through here, so there is no path that yields a doc whose
            // suppression set is not ordinal.
            doc.RebuildSuppressed();
            return doc;
        }
        catch (Exception exc)
        {
            // Corrupt state file — start fresh rather than crash (unchanged),
            // but never silently: an empty doc drops the sync timestamps, the
            // processed-delivery ledger, the billable counter AND the erasure
            // suppression list, and an operator must know why they reset.
            WarnCorruptOnce(StatePath,
                $"State file '{StatePath}' is unreadable ({exc.GetType().Name}: {exc.Message}) — " +
                "continuing with an EMPTY state document: sync timestamps, processed-delivery ledger, " +
                "billable counter and the erasure suppression list are not readable until the file is restored.");
            return new StateDoc();
        }
    }

    private void SaveDoc(StateDoc doc)
    {
        doc.FlushSuppressed();
        AtomicWrite(StatePath, JsonSerializer.Serialize(doc, JsonOptions));
    }

    private static void AtomicWrite(string path, string content, bool failBeforeMove = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // UNIQUE per write. A shared "<path>.tmp" made every writer over a given
        // file collide: an operator running `ingest-item` while a crawl runs
        // under the same CONNECTOR_ID would have one writer's WriteAllText
        // truncate the other's half-finished temp, so File.Move could publish a
        // TRUNCATED state document — or, as the probe showed, the second Move
        // would throw FileNotFoundException on a background thread because the
        // first had already renamed the shared temp away. The process id keeps
        // the name diagnosable; the guid makes it unique within the process.
        var tmp = $"{path}.{Environment.ProcessId:x}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (failBeforeMove)
                throw new IOException("injected mid-write failure (test seam)");
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Never leave litter beside the state file. A stray temp is
            // confusing during an incident and, when the write failed because
            // the volume filled, is part of what is keeping it full.
            try
            {
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
            catch
            {
                // Best effort only — the original failure is the one that matters.
            }
            throw;
        }
    }

    /// <summary>Test seam: drive AtomicWrite directly, optionally failing after
    /// the temp file is written but before it is published, so the crash-safety
    /// contract (no litter, previous state intact) is assertable.</summary>
    internal static void AtomicWriteForTests(string path, string content, bool failBeforeMove = false) =>
        AtomicWrite(path, content, failBeforeMove);

    /// <summary>Test seam: the loaded state document, so the round-trip tests can
    /// assert the suppression set's COMPARER and not merely a symptom of it.</summary>
    internal StateDoc LoadStateDocument()
    {
        lock (StateLock)
            return LoadDoc();
    }

    /// <summary>Test seam: the object that serialises state-file mutations, so
    /// the "process-wide" claim can be asserted as lock identity.</summary>
    internal object StateLockForTests => StateLock;

    private object StateLock =>
        StateLocks.GetOrAdd(Path.GetFullPath(StatePath), _ => new object());

    // ---- checkpoint ------------------------------------------------------------

    public CrawlCheckpoint? GetCheckpoint()
    {
        lock (StateLock)
        {
            if (!File.Exists(CheckpointPath))
                return null;
            try
            {
                var checkpoint = JsonSerializer.Deserialize<CrawlCheckpoint>(File.ReadAllText(CheckpointPath));
                // Kind, not value: DATETIME2 carries no Kind, so the SQL
                // backend stamps Utc on read. Do the same here so the two
                // return EQUAL DateTimes and not merely equal ticks.
                return checkpoint == null
                    ? null
                    : checkpoint with { UpdatedUtc = StateContract.Utc(checkpoint.UpdatedUtc) };
            }
            catch (Exception exc)
            {
                // Unreadable checkpoint = resume position lost. Treating it as
                // "no checkpoint" is safe (PUTs are idempotent; the delivery
                // re-ingests from record 0) but must be visible in the log.
                WarnCorruptOnce(CheckpointPath,
                    $"Checkpoint file '{CheckpointPath}' is unreadable ({exc.GetType().Name}: {exc.Message}) — " +
                    "treating as NO checkpoint; the interrupted delivery restarts from record 0 (PUTs are idempotent).");
                return null;
            }
        }
    }

    public void SaveCheckpoint(CrawlCheckpoint checkpoint)
    {
        var storable = StateContract.Storable(checkpoint);
        lock (StateLock)
            AtomicWrite(CheckpointPath, JsonSerializer.Serialize(storable, JsonOptions));
    }

    public void ClearCheckpoint()
    {
        lock (StateLock)
        {
            if (File.Exists(CheckpointPath))
                File.Delete(CheckpointPath);
        }
    }

    // ---- sync timestamps ----------------------------------------------------------

    public DateTime? GetLastSync(string kind)
    {
        lock (StateLock)
            return LoadDoc().LastSync.TryGetValue(kind, out var when)
                ? StateContract.Utc(when)
                : null;
    }

    public void SetLastSync(string kind, DateTime utc)
    {
        var when = StateContract.Utc(utc);
        lock (StateLock)
        {
            var doc = LoadDoc();
            doc.LastSync[kind] = when;
            SaveDoc(doc);
        }
    }

    // ---- dead letter -----------------------------------------------------------------
    //
    // Dead-letter corruption fix (ported from the reference connector's stress
    // harness finding): concurrent $batch workers — potentially through
    // DIFFERENT store instances over the same file — must never interleave
    // partial lines. All appends serialize on a process-wide per-path lock and
    // each record is written as one complete line through a single
    // FileMode.Append stream (never truncating, readable while written).

    private static readonly ConcurrentDictionary<string, object> DeadLetterLocks =
        new(StringComparer.Ordinal);

    private object DeadLetterLock =>
        DeadLetterLocks.GetOrAdd(Path.GetFullPath(DeadLetterPath), _ => new object());

    public void AddDeadLetter(DeadLetterRecord record) =>
        AddDeadLetters(new[] { record });

    /// <summary>Append a batch of records under one lock acquisition (a whole
    /// failed sub-batch lands contiguously, mirroring AppendFailedRecords).</summary>
    public void AddDeadLetters(IReadOnlyCollection<DeadLetterRecord> records)
    {
        if (records.Count == 0)
            return;
        // Normalise the WHOLE batch before opening the file, so the bytes
        // written are decided before any I/O starts. Normalisation cannot fail,
        // so this is not a rejection point.
        var storable = Storable(records);
        lock (DeadLetterLock)
        {
            Directory.CreateDirectory(_logsDir);
            using var stream = new FileStream(
                DeadLetterPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            foreach (var record in storable)
                writer.WriteLine(JsonSerializer.Serialize(record, JsonlOptions));
        }
    }

    /// <summary>Normalise a whole batch up front (see
    /// <see cref="StateContract.Storable(DeadLetterRecord)"/>). Normalisation
    /// only — no record is ever refused, so a batch read from a legacy queue
    /// file can always be written back.</summary>
    private static List<DeadLetterRecord> Storable(IEnumerable<DeadLetterRecord> records)
    {
        var result = new List<DeadLetterRecord>();
        foreach (var record in records)
            result.Add(StateContract.Storable(record));
        return result;
    }

    public IReadOnlyList<DeadLetterRecord> ReadDeadLetters()
    {
        lock (DeadLetterLock)
        {
            if (!File.Exists(DeadLetterPath))
                return Array.Empty<DeadLetterRecord>();
            var records = new List<DeadLetterRecord>();
            var malformed = 0;
            var firstMalformedLine = 0;
            var lineNumber = 0;
            foreach (var line in File.ReadLines(DeadLetterPath))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    var record = JsonSerializer.Deserialize<DeadLetterRecord>(line);
                    if (record != null)
                        // Kind only — the value is NOT re-validated on read.
                        // A queue file written before this contract existed may
                        // hold an out-of-domain record, and refusing to read it
                        // would turn a legacy value into a LOST failure record.
                        records.Add(record with { FailedUtc = StateContract.Utc(record.FailedUtc) });
                }
                catch
                {
                    // Skip malformed lines; retry-failed reports what it could
                    // parse — but a skipped line is a LOST failure record, so
                    // it is counted and warned about (once per file) below.
                    malformed++;
                    if (firstMalformedLine == 0)
                        firstMalformedLine = lineNumber;
                }
            }
            if (malformed > 0)
                WarnCorruptOnce(DeadLetterPath,
                    $"Dead-letter queue '{DeadLetterPath}' has {malformed} malformed line(s) " +
                    $"(first at line {firstMalformedLine}) — those failure records cannot be replayed and were skipped; " +
                    $"{records.Count} record(s) parsed.");
            return records;
        }
    }

    public void ReplaceDeadLetters(IEnumerable<DeadLetterRecord> records)
    {
        var storable = Storable(records);
        lock (DeadLetterLock)
        {
            var sb = new StringBuilder();
            foreach (var record in storable)
                sb.AppendLine(JsonSerializer.Serialize(record, JsonlOptions));
            AtomicWrite(DeadLetterPath, sb.ToString());
        }
    }

    public void ClearDeadLetters()
    {
        lock (DeadLetterLock)
        {
            if (File.Exists(DeadLetterPath))
                File.Delete(DeadLetterPath);
        }
    }

    /// <summary>Atomic read-modify-write: the read AND the write happen inside a
    /// single acquisition of the per-file lock, so a concurrent AddDeadLetter
    /// append cannot slip between a stale snapshot and a whole-queue overwrite
    /// (Monitor is re-entrant, so the nested Read/Replace/Clear calls share this
    /// lock). An empty result deletes the file (matching ClearDeadLetters).</summary>
    public void MutateDeadLetters(
        Func<IReadOnlyList<DeadLetterRecord>, IEnumerable<DeadLetterRecord>> transform)
    {
        lock (DeadLetterLock)
        {
            var updated = transform(ReadDeadLetters()).ToList();
            if (updated.Count == 0)
                ClearDeadLetters();
            else
                ReplaceDeadLetters(updated);
        }
    }

    // ---- delivery ledger ------------------------------------------------------------------

    public bool IsDeliveryProcessed(string deliveryId)
    {
        lock (StateLock)
            return LoadDoc().ProcessedDeliveries.ContainsKey(deliveryId);
    }

    public void MarkDeliveryProcessed(string deliveryId, DateTime utc)
    {
        var key = deliveryId;
        var when = StateContract.Utc(utc);
        lock (StateLock)
        {
            var doc = LoadDoc();
            doc.ProcessedDeliveries[key] = when;
            SaveDoc(doc);
        }
    }

    public IReadOnlyList<string> ListProcessedDeliveries()
    {
        lock (StateLock)
            return LoadDoc().ProcessedDeliveries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    // ---- key/value -----------------------------------------------------------------------------

    public string? GetValue(string key)
    {
        lock (StateLock)
            return LoadDoc().Values.TryGetValue(key, out var value) ? value : null;
    }

    public void SetValue(string key, string? value)
    {
        var validKey = key;
        // null still DELETES; a non-null value is normalized (not rejected) —
        // it lands in NVARCHAR(MAX) and is not an identity.
        var storable = value == null ? null : StateContract.Text(value);
        lock (StateLock)
        {
            var doc = LoadDoc();
            if (storable == null)
                doc.Values.Remove(validKey);
            else
                doc.Values[validKey] = storable;
            SaveDoc(doc);
        }
    }

    /// <summary>Atomic read-modify-write: the load, the transform and the save
    /// all happen inside ONE acquisition of the process-wide, per-file lock
    /// (<see cref="StateLocks"/>), so two concurrent usage-ceiling reservations
    /// cannot both observe the same pre-increment count and both conclude there
    /// was room — and that now holds however many FileStateStore INSTANCES the
    /// process has over the file, which is what the claim previously asserted
    /// without delivering. (Cross-PROCESS atomicity still needs the SQL
    /// backend — see the scope note in Altrata/UsageBudget.cs.)</summary>
    public string? MutateValue(string key, Func<string?, string?> transform)
    {
        var validKey = key;
        lock (StateLock)
        {
            var doc = LoadDoc();
            doc.Values.TryGetValue(validKey, out var current);
            var updated = transform(current);
            if (updated != null)
                updated = StateContract.Text(updated);
            if (updated == null)
                doc.Values.Remove(validKey);
            else
                doc.Values[validKey] = updated;
            SaveDoc(doc);
            return updated;
        }
    }

    // ---- billable lookups --------------------------------------------------------------------------

    public long GetBillableLookupCount()
    {
        lock (StateLock)
            return LoadDoc().BillableLookups;
    }

    public long IncrementBillableLookups(long delta = 1)
    {
        lock (StateLock)
        {
            var doc = LoadDoc();
            doc.BillableLookups += delta;
            SaveDoc(doc);
            return doc.BillableLookups;
        }
    }

    // ---- suppression list (erasure durability) ----------------------------------------------------------

    public void AddSuppressedSubject(string subjectId)
    {
        // The DSAR filing point. An id containing an unpaired UTF-16 surrogate
        // is still silently rewritten to U+FFFD by System.Text.Json on save —
        // an inherent limit of the JSON backend — but since the operator-entry
        // validation (Commands/SubjectIdPolicy.cs, enforced at the
        // forget-subject command before any mutation) no such id can arrive
        // here from an operator; only a replay of legacy state can, and
        // replays must never be refused. Deliberately NO validation here — see
        // StateContract.cs history and docs/SQL_CONTRACT.md.
        var id = subjectId;
        lock (StateLock)
        {
            var doc = LoadDoc();
            if (doc.SuppressedSubjects.Add(id))
                SaveDoc(doc);
        }
    }

    public void RemoveSuppressedSubject(string subjectId)
    {
        var id = subjectId;
        lock (StateLock)
        {
            var doc = LoadDoc();
            if (doc.SuppressedSubjects.Remove(id))
                SaveDoc(doc);
        }
    }

    public bool IsSubjectSuppressed(string subjectId)
    {
        lock (StateLock)
            return LoadDoc().SuppressedSubjects.Contains(subjectId);
    }

    public IReadOnlyList<string> ListSuppressedSubjects()
    {
        lock (StateLock)
            return LoadDoc().SuppressedSubjects.ToList();
    }

    // ---- purge ------------------------------------------------------------------------------------------

    public void WipeAll()
    {
        // Each file is deleted under the lock that guards IT. The dead-letter
        // queue has its own lock, so deleting it under the state lock (as this
        // did) raced a concurrent AddDeadLetters append.
        //
        // The two locks are taken SEQUENTIALLY, never nested: MutateDeadLetters
        // hands control to a caller-supplied transform while holding the
        // dead-letter lock, and that transform may legitimately touch state —
        // dead-letter-then-state is therefore a live acquisition order, and
        // nesting the reverse order here would be a lock-order inversion.
        // WipeAll is not atomic across the three files either way.
        lock (StateLock)
        {
            foreach (var path in new[] { CheckpointPath, StatePath })
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
        lock (DeadLetterLock)
        {
            if (File.Exists(DeadLetterPath))
                File.Delete(DeadLetterPath);
        }
    }
}
