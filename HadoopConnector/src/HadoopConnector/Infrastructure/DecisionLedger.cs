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
            var raw = Environment.GetEnvironmentVariable("DECISION_LEDGER");
            return string.IsNullOrWhiteSpace(raw) || EnvFlags.IsTrue("DECISION_LEDGER");
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
            lines = File.ReadLines(path, Utf8NoBom).Where(l => l.Trim().Length > 0).ToList();
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

    /// <summary>Read the last valid entry to continue the chain across restarts;
    /// (0, GENESIS) for a missing/empty/corrupt file.</summary>
    private static (long Seq, string Hash) ReadChainHead(string path)
    {
        try
        {
            string? last = null;
            foreach (var raw in File.ReadLines(path, Utf8NoBom))
            {
                if (raw.Trim().Length > 0)
                    last = raw;
            }
            if (last is null)
                return (0, Genesis);
            var obj = JsonNode.Parse(last)!.AsObject();
            return (obj["seq"]!.GetValue<long>(), obj["hash"]!.GetValue<string>());
        }
        catch
        {
            // Missing/empty/corrupt tail — start (or continue) from genesis.
            return (0, Genesis);
        }
    }
}
