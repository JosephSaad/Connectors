// Infrastructure/DecisionLedger.cs
// --------------------------------
// Immutable, append-only, SHA-256 hash-chained audit of the connector's
// low-volume ACCESS decisions:
//
//   EXCLUSION      — an item was deliberately kept OUT of the index (e.g. no
//                    resolvable ACL principals — nobody could be granted it).
//   ACL_RESTRICTION — a top-tier (Restricted) item's ACL was narrowed to the
//                    classification-enforcement group (CLASSIFICATION_ENFORCE_ACL).
//
// Scope is deliberately narrow — decisions that change WHO can see WHAT, not
// every ingest. Each entry links to the previous one by hash, so any later
// edit, deletion or reordering of the file is detectable by Verify().
//
// File: {logsDir}/decisions_{connectorId}.jsonl, one JSON object per line:
//   {"seq":1,"timestamp":"...","item_id":"...","object_type":"...",
//    "decision":"EXCLUSION","reason":"...","prev_hash":"GENESIS","hash":"<sha256>"}
//
// hash = SHA-256 over the canonical, newline-joined tuple
//   seq | timestamp | item_id | object_type | decision | reason | prev_hash
// and becomes the next entry's prev_hash. The chain is continued across process
// restarts (the constructor reads the last valid line). Node-local and file-based
// like the classification manifest and dead-letter queue; thread-safe for the
// in-process object/batch workers that append to it.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HadoopConnector.Infrastructure;

public sealed class DecisionLedger
{
    /// <summary>Decision: item deliberately excluded from the index.</summary>
    public const string Exclusion = "EXCLUSION";

    /// <summary>Decision: item ACL narrowed by classification enforcement.</summary>
    public const string AclRestriction = "ACL_RESTRICTION";

    /// <summary>Genesis link for the first entry.</summary>
    internal const string Genesis = "GENESIS";

    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector.audit");
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    private readonly string _path;
    private readonly object _lock = new();
    private string _lastHash;
    private long _seq;
    private bool _appendBoundaryChecked;

    public DecisionLedger(string connectorId, string logsDir)
    {
        Directory.CreateDirectory(logsDir);
        _path = System.IO.Path.Combine(logsDir, $"decisions_{connectorId}.jsonl");
        (_seq, _lastHash) = ReadChainHead(_path);
    }

    public string Path => _path;

    /// <summary>DECISION_LEDGER (default ON) — set false to disable the audit.</summary>
    public static bool Enabled
    {
        get
        {
            return !EnvFlags.IsFalse("DECISION_LEDGER");
        }
    }

    /// <summary>Append one decision, linking it to the chain head. Thread-safe;
    /// never throws (an audit-write failure must not fail the crawl, but it IS
    /// logged so the gap is attributable).</summary>
    public void Record(string itemId, string objectType, string decision, string reason)
    {
        try
        {
            lock (_lock)
            {
                // Seal a torn/unterminated final line (a crash mid-append) before
                // the first write of this instance, so the new entry lands on its
                // own line instead of being glued onto the partial and corrupting
                // BOTH lines. One-time per instance, under the lock.
                EnsureCleanAppendBoundary();
                var seq = _seq + 1;
                var timestamp = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                var hash = ComputeHash(seq, timestamp, itemId, objectType, decision, reason, _lastHash);
                var entry = new JsonObject
                {
                    ["seq"] = seq,
                    ["timestamp"] = timestamp,
                    ["item_id"] = itemId,
                    ["object_type"] = objectType,
                    ["decision"] = decision,
                    ["reason"] = reason,
                    ["prev_hash"] = _lastHash,
                    ["hash"] = hash,
                };
                using (var writer = new StreamWriter(_path, append: true, Utf8NoBom))
                    writer.WriteLine(entry.ToJsonString(Compact));
                _seq = seq;
                _lastHash = hash;
            }
        }
        catch (Exception exc)
        {
            Logger.Error(
                $"Decision-ledger write failed for item '{itemId}' ({decision}): "
                + $"{exc.GetType().Name}: {exc.Message}");
        }
    }

    /// <summary>Canonical SHA-256 (lowercase hex) over the entry tuple.</summary>
    internal static string ComputeHash(
        long seq, string timestamp, string itemId, string objectType,
        string decision, string reason, string prevHash)
    {
        var canonical = string.Join(
            '\n',
            seq.ToString(CultureInfo.InvariantCulture),
            timestamp, itemId, objectType, decision, reason, prevHash);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>Outcome of a ledger verification.</summary>
    public readonly record struct VerifyResult(bool Ok, int Entries, string? Error)
    {
        public static VerifyResult Valid(int entries) => new(true, entries, null);

        public static VerifyResult Broken(int atEntry, string reason) =>
            new(false, atEntry, reason);
    }

    /// <summary>Re-derive every hash and confirm each entry links to the previous
    /// one and to its own recomputed hash — detects edits, deletions and
    /// reordering. A missing file is a valid (empty) chain.</summary>
    public static VerifyResult Verify(string path)
    {
        List<string> lines;
        try
        {
            lines = ReadLinesShared(path).Where(l => l.Trim().Length > 0).ToList();
        }
        catch (Exception exc) when (exc is FileNotFoundException or DirectoryNotFoundException)
        {
            return VerifyResult.Valid(0);
        }

        var prevHash = Genesis;
        long expectedSeq = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            JsonObject entry;
            try
            {
                entry = JsonNode.Parse(lines[i])!.AsObject();
            }
            catch (Exception exc) when (exc is JsonException or InvalidOperationException)
            {
                return VerifyResult.Broken(i + 1, $"line {i + 1} is not valid JSON: {exc.Message}");
            }

            expectedSeq++;
            var seq = entry["seq"]?.GetValue<long>() ?? -1;
            if (seq != expectedSeq)
                return VerifyResult.Broken(i + 1, $"seq gap/reorder at line {i + 1} (expected {expectedSeq}, got {seq}).");

            var storedPrev = entry["prev_hash"]?.GetValue<string>() ?? string.Empty;
            if (!string.Equals(storedPrev, prevHash, StringComparison.Ordinal))
                return VerifyResult.Broken(i + 1, $"broken link at seq {seq}: prev_hash does not match the previous entry.");

            var recomputed = ComputeHash(
                seq,
                entry["timestamp"]?.GetValue<string>() ?? string.Empty,
                entry["item_id"]?.GetValue<string>() ?? string.Empty,
                entry["object_type"]?.GetValue<string>() ?? string.Empty,
                entry["decision"]?.GetValue<string>() ?? string.Empty,
                entry["reason"]?.GetValue<string>() ?? string.Empty,
                storedPrev);
            var storedHash = entry["hash"]?.GetValue<string>() ?? string.Empty;
            if (!string.Equals(recomputed, storedHash, StringComparison.Ordinal))
                return VerifyResult.Broken(i + 1, $"tamper detected at seq {seq}: recomputed hash does not match stored hash.");

            prevHash = storedHash;
        }
        return VerifyResult.Valid(lines.Count);
    }

    /// <summary>Read the last VALID entry to continue the chain across restarts.
    /// A torn/unterminated final line (a crash mid-append) is tolerated: the head
    /// is the last well-formed entry, NOT genesis — resetting to genesis would
    /// restart the sequence at 1 and orphan the existing audit history. Only a
    /// missing/empty file (or one with no parseable entry at all) is (0, GENESIS).
    /// The tail-truncation caveat stands: the torn final entry itself is lost.</summary>
    private static (long Seq, string Hash) ReadChainHead(string path)
    {
        long seq = 0;
        string hash = Genesis;
        var skippedTornLine = false;
        try
        {
            foreach (var raw in ReadLinesShared(path))
            {
                if (raw.Trim().Length == 0)
                    continue;
                long? entrySeq;
                string? entryHash;
                try
                {
                    var obj = JsonNode.Parse(raw)!.AsObject();
                    entrySeq = obj["seq"]?.GetValue<long>();
                    entryHash = obj["hash"]?.GetValue<string>();
                }
                catch (Exception exc) when (exc is JsonException or InvalidOperationException or FormatException)
                {
                    // Torn/garbled line (e.g. a crash mid-append). Skip it and keep
                    // the last valid entry as the head rather than discarding the
                    // whole chain; usually only the final line is affected.
                    skippedTornLine = true;
                    continue;
                }
                if (entrySeq is null || entryHash is null)
                {
                    skippedTornLine = true;
                    continue;
                }
                seq = entrySeq.Value;
                hash = entryHash;
            }
        }
        catch (Exception exc) when (exc is FileNotFoundException or DirectoryNotFoundException)
        {
            // No file yet — a first run starts from genesis.
            return (0, Genesis);
        }
        catch (Exception exc)
        {
            // Any other read failure (e.g. IO/permission): do not reset an existing
            // chain to genesis silently — surface it and continue from genesis only
            // because nothing could be read.
            Logger.Warning(
                $"Decision-ledger '{path}' could not be read to continue the chain "
                + $"({exc.GetType().Name}: {exc.Message}); starting from genesis.");
            return (0, Genesis);
        }
        if (skippedTornLine)
        {
            Logger.Warning(
                $"Decision-ledger '{path}' has a torn/garbled line (a crash mid-append); "
                + $"continuing the chain from the last intact entry (seq {seq}). Verify() will "
                + "flag the torn line — inspect the file to confirm it is tail-truncation, not tamper.");
        }
        return (seq, hash);
    }

    /// <summary>Seal a final line that does not end with a newline (a crash left a
    /// torn/unterminated partial), so the next append starts on a clean line.
    /// One-time per instance; best-effort — a sealing failure must not fail the
    /// append (the write below falls back to append-as-is).</summary>
    private void EnsureCleanAppendBoundary()
    {
        if (_appendBoundaryChecked)
            return;
        _appendBoundaryChecked = true;
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length == 0)
                return;
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            fs.Seek(-1, SeekOrigin.End);
            if (fs.ReadByte() != '\n')
                fs.WriteByte((byte)'\n');
        }
        catch (Exception exc)
        {
            Logger.Warning(
                $"Decision-ledger '{_path}' append-boundary check failed "
                + $"({exc.GetType().Name}: {exc.Message}); appending as-is.");
        }
    }

    /// <summary>
    /// Read the ledger with a share mode that tolerates the concurrent appender.
    /// </summary>
    /// <remarks>
    /// <c>File.ReadLines</c> opens with <see cref="FileShare.Read"/>, which does
    /// not permit the appender's Write access. Windows enforces share modes and
    /// POSIX does not, so verifying or exporting the ledger while a crawl is
    /// writing to it throws on Windows Server — the deployment target — and never
    /// on the machines this is developed on. The audit trail is the worst place
    /// for a read that only works when nothing else is happening.
    ///
    /// Only the READER is widened. The appender deliberately keeps
    /// <see cref="FileShare.Read"/>: refusing a second writer is what enforces
    /// the single-active-writer assumption the hash chain rests on, since two
    /// appenders would independently resume the seq/prev_hash counters and fork
    /// the chain. That asymmetry is the whole point — a mechanical "add
    /// ReadWrite everywhere" sweep would break the ledger it was meant to protect.
    /// </remarks>
    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Utf8NoBom);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

}
