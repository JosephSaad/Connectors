// State/FileStateStore.cs
// -----------------------
// Default on-disk state backend:
//
//   logs/checkpoint_{CONNECTOR_ID}.json       crawl checkpoint
//   logs/failed_records_{CONNECTOR_ID}.jsonl  dead-letter queue (append-only JSONL)
//   data/{CONNECTOR_ID}_state.json            sync timestamps, delivery ledger, KV
//
// Writes are atomic (temp file + File.Move with overwrite) and guarded by a
// process-wide lock; multi-node writers require the SQL backend instead.

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

    private readonly object _sync = new();
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

    private sealed class StateDoc
    {
        public Dictionary<string, DateTime> LastSync { get; set; } = new();
        public Dictionary<string, DateTime> ProcessedDeliveries { get; set; } = new();
        public Dictionary<string, string?> Values { get; set; } = new();
        public long BillableLookups { get; set; }
        /// <summary>Erased subject altrata ids — durable across re-delivery.</summary>
        public SortedSet<string> SuppressedSubjects { get; set; } = new(StringComparer.Ordinal);
    }

    private StateDoc LoadDoc()
    {
        if (!File.Exists(StatePath))
            return new StateDoc();
        try
        {
            return JsonSerializer.Deserialize<StateDoc>(File.ReadAllText(StatePath)) ?? new StateDoc();
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

    private void SaveDoc(StateDoc doc) =>
        AtomicWrite(StatePath, JsonSerializer.Serialize(doc, JsonOptions));

    private static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    // ---- checkpoint ------------------------------------------------------------

    public CrawlCheckpoint? GetCheckpoint()
    {
        lock (_sync)
        {
            if (!File.Exists(CheckpointPath))
                return null;
            try
            {
                return JsonSerializer.Deserialize<CrawlCheckpoint>(File.ReadAllText(CheckpointPath));
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
        lock (_sync)
            AtomicWrite(CheckpointPath, JsonSerializer.Serialize(checkpoint, JsonOptions));
    }

    public void ClearCheckpoint()
    {
        lock (_sync)
        {
            if (File.Exists(CheckpointPath))
                File.Delete(CheckpointPath);
        }
    }

    // ---- sync timestamps ----------------------------------------------------------

    public DateTime? GetLastSync(string kind)
    {
        lock (_sync)
            return LoadDoc().LastSync.TryGetValue(kind, out var when) ? when : null;
    }

    public void SetLastSync(string kind, DateTime utc)
    {
        lock (_sync)
        {
            var doc = LoadDoc();
            doc.LastSync[kind] = utc;
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
        lock (DeadLetterLock)
        {
            Directory.CreateDirectory(_logsDir);
            using var stream = new FileStream(
                DeadLetterPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            foreach (var record in records)
                writer.WriteLine(JsonSerializer.Serialize(record, JsonlOptions));
        }
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
                        records.Add(record);
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
        lock (DeadLetterLock)
        {
            var sb = new StringBuilder();
            foreach (var record in records)
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

    // ---- delivery ledger ------------------------------------------------------------------

    public bool IsDeliveryProcessed(string deliveryId)
    {
        lock (_sync)
            return LoadDoc().ProcessedDeliveries.ContainsKey(deliveryId);
    }

    public void MarkDeliveryProcessed(string deliveryId, DateTime utc)
    {
        lock (_sync)
        {
            var doc = LoadDoc();
            doc.ProcessedDeliveries[deliveryId] = utc;
            SaveDoc(doc);
        }
    }

    public IReadOnlyList<string> ListProcessedDeliveries()
    {
        lock (_sync)
            return LoadDoc().ProcessedDeliveries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    // ---- key/value -----------------------------------------------------------------------------

    public string? GetValue(string key)
    {
        lock (_sync)
            return LoadDoc().Values.TryGetValue(key, out var value) ? value : null;
    }

    public void SetValue(string key, string? value)
    {
        lock (_sync)
        {
            var doc = LoadDoc();
            if (value == null)
                doc.Values.Remove(key);
            else
                doc.Values[key] = value;
            SaveDoc(doc);
        }
    }

    // ---- billable lookups --------------------------------------------------------------------------

    public long GetBillableLookupCount()
    {
        lock (_sync)
            return LoadDoc().BillableLookups;
    }

    public long IncrementBillableLookups(long delta = 1)
    {
        lock (_sync)
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
        lock (_sync)
        {
            var doc = LoadDoc();
            if (doc.SuppressedSubjects.Add(subjectId))
                SaveDoc(doc);
        }
    }

    public void RemoveSuppressedSubject(string subjectId)
    {
        lock (_sync)
        {
            var doc = LoadDoc();
            if (doc.SuppressedSubjects.Remove(subjectId))
                SaveDoc(doc);
        }
    }

    public bool IsSubjectSuppressed(string subjectId)
    {
        lock (_sync)
            return LoadDoc().SuppressedSubjects.Contains(subjectId);
    }

    public IReadOnlyList<string> ListSuppressedSubjects()
    {
        lock (_sync)
            return LoadDoc().SuppressedSubjects.ToList();
    }

    // ---- purge ------------------------------------------------------------------------------------------

    public void WipeAll()
    {
        lock (_sync)
        {
            foreach (var path in new[] { CheckpointPath, DeadLetterPath, StatePath })
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
    }
}
