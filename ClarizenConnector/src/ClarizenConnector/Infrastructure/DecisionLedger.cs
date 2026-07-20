// Infrastructure/DecisionLedger.cs
// --------------------------------
// Immutable, append-only, SHA-256 hash-chained audit ledger for the two
// governance DECISIONS an operator/auditor must be able to reconstruct after
// the fact:
//
//   exclusion        — an item was NOT ingested (e.g. no resolvable ACL
//                      principal, so it was skipped rather than made visible).
//   acl_restriction  — an item's ACL was tightened away from its resolved
//                      principals (FINANCIAL_DATA_MODE=acl, or
//                      CLASSIFICATION_ENFORCE_ACL on a Restricted item).
//   quarantine       — the ContentGate stage (CONTENT_GATE) refused to index an
//                      item because its content was scored malicious (prompt
//                      injection) or its attachment binary could not be proven
//                      clean. The item is in the dead-letter queue, not gone.
//
// Scope is deliberately LOW-VOLUME: only these decisions, never every ingest.
// Each record carries {seq, item_id, decision, reason, timestamp, prev_hash}
// and a `hash` = SHA-256(prev_hash + "\n" + canonical-record). Tampering with
// any line (or reordering/removing one) breaks the chain, which Verify detects.
//
// File-based per connection id (decisions_<connectorId>.jsonl under the shared
// state dir, so SqlStateScope/SyncStateScope redirect it in tests). Gated by
// DECISION_LEDGER (default ON); DECISION_LEDGER=false disables recording.
// Never throws into the caller — an audit-write failure is logged, not fatal.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClarizenConnector.Infrastructure;

public static class DecisionLedger
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector.audit");
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
    private static readonly object WriteLock = new();

    public const string EnvVar = "DECISION_LEDGER";
    public const string DecisionExclusion = "exclusion";
    public const string DecisionAclRestriction = "acl_restriction";

    /// <summary>ContentGate refused to index an item (its own decision kind — a
    /// quarantine is NOT an exclusion: the item is retained and re-drivable).</summary>
    public const string DecisionQuarantine = "quarantine";

    /// <summary>Genesis link for the first record in a chain.</summary>
    internal const string GenesisHash = "genesis";

    /// <summary>True unless DECISION_LEDGER is explicitly falsey. Default ON.</summary>
    public static bool Enabled
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(EnvVar);
            return string.IsNullOrWhiteSpace(raw) || EnvFlags.IsTrue(EnvVar);
        }
    }

    internal static string LedgerPath(string connectorId) =>
        Path.Combine(Config.SyncState.LogsDir, $"decisions_{connectorId}.jsonl");

    /// <summary>Record an exclusion decision (item not ingested).</summary>
    public static void RecordExclusion(string connectorId, string itemId, string reason) =>
        Record(connectorId, itemId, DecisionExclusion, reason);

    /// <summary>Record an ACL-restriction decision (item locked down).</summary>
    public static void RecordRestriction(string connectorId, string itemId, string reason) =>
        Record(connectorId, itemId, DecisionAclRestriction, reason);

    /// <summary>Record a content-gate quarantine (item withheld from the index
    /// and dead-lettered for re-drive).</summary>
    public static void RecordQuarantine(string connectorId, string itemId, string reason) =>
        Record(connectorId, itemId, DecisionQuarantine, reason);

    /// <summary>
    /// Append one decision to the chain. Reads the tail to link the previous
    /// hash and advance the sequence, then writes the hashed record. Never
    /// throws — an audit I/O failure is logged so it is diagnosable but never
    /// aborts the crawl.
    /// </summary>
    public static void Record(string connectorId, string itemId, string decision, string reason)
    {
        if (!Enabled)
            return;
        try
        {
            lock (WriteLock)
            {
                var (lastSeq, lastHash) = ReadTail(connectorId);
                var seq = lastSeq + 1;
                var prevHash = lastHash ?? GenesisHash;
                var record = BuildRecord(seq, itemId, decision, reason, prevHash);
                record["hash"] = ComputeHash(prevHash, record);

                var path = LedgerPath(connectorId);
                Directory.CreateDirectory(Config.SyncState.LogsDir);
                // A crash mid-append can leave a torn final line with no trailing
                // newline. Isolate it onto its own line before appending so the
                // new record is never glued onto the fragment (which would lose
                // it and corrupt the physical line). The tolerant reader below
                // skips the fragment; the chain continues cleanly from here.
                EnsureTrailingNewline(connectorId, path);
                using var writer = new StreamWriter(path, append: true, Utf8NoBom);
                writer.WriteLine(record.ToJsonString(Compact));
            }
        }
        catch (Exception exc)
        {
            Logger.Error(
                $"Failed to append a '{decision}' decision for {itemId} to the audit ledger "
                + $"({exc.GetType().Name}: {exc.Message}). Decision still applied; the ledger is incomplete.");
        }
    }

    /// <summary>All ledger records for <paramref name="connectorId"/>, in order.
    /// Tolerant reader: an unparseable line (a torn final write from a crash, or
    /// interior corruption) is skipped rather than throwing, so one torn line can
    /// never make the whole ledger unreadable or wedge every later append. A torn
    /// tail then simply drops off; interior corruption leaves a seq/hash-chain
    /// gap that <see cref="Verify"/> still reports as tamper.</summary>
    public static List<JsonObject> Read(string connectorId)
    {
        var entries = new List<JsonObject>();
        try
        {
            foreach (var rawLine in File.ReadLines(LedgerPath(connectorId), Utf8NoBom))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                    continue;
                JsonObject? entry;
                try
                {
                    entry = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    // Per-line isolation (same contract as SyncState's dead-letter
                    // reader): skip the fragment and keep reading the good lines.
                    continue;
                }
                if (entry is not null)
                    entries.Add(entry);
            }
        }
        catch (Exception exc) when (exc is FileNotFoundException or DirectoryNotFoundException)
        {
            // no ledger yet
        }
        return entries;
    }

    /// <summary>
    /// If the ledger file exists and does not already end in a newline, append
    /// one so a torn final line (a crash mid-append) is isolated and the next
    /// record lands cleanly on its own line. Logged once, at the moment of
    /// repair, rather than on every read. Best-effort — the caller's try/catch
    /// still guards the actual write.
    /// </summary>
    private static void EnsureTrailingNewline(string connectorId, string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
            return;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Seek(-1, SeekOrigin.End);
        if (stream.ReadByte() == '\n')
            return;
        stream.Seek(0, SeekOrigin.End);
        stream.WriteByte((byte)'\n');
        Logger.Warning(
            $"Decision ledger for '{connectorId}' had a torn final line (a prior crash "
            + "mid-append); isolated it so the chain continues from the last intact record.");
    }

    /// <summary>
    /// Verify the whole chain: every record's stored hash must recompute from
    /// its prev_hash + canonical content, each prev_hash must equal the prior
    /// record's hash (genesis for the first), and seq must increase by one from
    /// 1. Returns false on any tamper/gap/reorder. Never throws.
    /// </summary>
    public static bool Verify(string connectorId)
    {
        try
        {
            var expectedPrev = GenesisHash;
            var expectedSeq = 1L;
            foreach (var entry in Read(connectorId))
            {
                var storedHash = entry["hash"]?.GetValue<string>();
                var prevHash = entry["prev_hash"]?.GetValue<string>();
                var seq = entry["seq"]?.GetValue<long>();
                if (storedHash is null || prevHash is null || seq is null)
                    return false;
                if (prevHash != expectedPrev || seq != expectedSeq)
                    return false;

                // Recompute the hash over the record minus its own hash field.
                var canonical = BuildRecord(
                    seq.Value,
                    entry["item_id"]?.GetValue<string>() ?? string.Empty,
                    entry["decision"]?.GetValue<string>() ?? string.Empty,
                    entry["reason"]?.GetValue<string>() ?? string.Empty,
                    prevHash);
                canonical["timestamp"] = entry["timestamp"]?.GetValue<string>();
                if (ComputeHash(prevHash, canonical) != storedHash)
                    return false;

                expectedPrev = storedHash;
                expectedSeq++;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Read the last record's (seq, hash); (0, null) when the chain is empty.</summary>
    private static (long Seq, string? Hash) ReadTail(string connectorId)
    {
        var entries = Read(connectorId);
        if (entries.Count == 0)
            return (0, null);
        var last = entries[^1];
        return (last["seq"]?.GetValue<long>() ?? 0, last["hash"]?.GetValue<string>());
    }

    private static JsonObject BuildRecord(
        long seq, string itemId, string decision, string reason, string prevHash) => new()
    {
        // Fixed key order is part of the canonical form the hash covers.
        ["seq"] = seq,
        ["item_id"] = itemId,
        ["decision"] = decision,
        ["reason"] = reason,
        ["timestamp"] = Config.SyncState.IsoFormat(DateTime.UtcNow),
        ["prev_hash"] = prevHash,
    };

    /// <summary>SHA-256 (FIPS-approved) hex over prev_hash + "\n" + canonical record.</summary>
    private static string ComputeHash(string prevHash, JsonObject recordWithoutHash)
    {
        var canonical = prevHash + "\n" + recordWithoutHash.ToJsonString(Compact);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
