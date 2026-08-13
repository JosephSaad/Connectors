// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Graph/DecisionLedger.cs
// -----------------------
// #11 — immutable, tamper-evident audit of access-affecting decisions.
//
// Records the LOW-VOLUME decisions the connector makes that change who can see
// an item beyond the record's own Salesforce sharing:
//
//   * EXCLUSION       — the item was dropped to a safe deny-everyone fallback
//                       (e.g. the ACL scale guard's fallback action).
//   * ACL_RESTRICTION — the item's ACL was narrowed to a specific group
//                       (classification enforcement #6b, or the scale guard's
//                       group action #9).
//
// It is NOT an ingest log: ordinary items are never recorded, only these
// deliberate access decisions, so a real deployment writes at most a handful of
// entries per crawl.
//
// Tamper evidence: entries form a SHA-256 hash chain (each entry's Hash covers
// its own fields plus the previous entry's Hash), so editing, reordering, or
// deleting any entry breaks every later link — Verify() detects it without a
// trusted external store. The file is only ever opened in append mode. This
// generalizes the Altrata connector's erasure-ledger pattern (that connector
// chains erasure events; this one chains access decisions).
//
// Gated by DECISION_LEDGER (default off): CreateIfEnabled returns null when
// unset, so the pipeline is byte-identical to today unless an operator opts in.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Graph;

/// <summary>Canonical decision kinds recorded in the ledger.</summary>
public static class DecisionKinds
{
    public const string Exclusion = "exclusion";
    public const string AclRestriction = "acl-restriction";
}

public sealed record DecisionLedgerEntry
{
    /// <summary>1-based position in the chain.</summary>
    public required long Seq { get; init; }
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    /// <summary>exclusion | acl-restriction (see <see cref="DecisionKinds"/>).</summary>
    public required string Decision { get; init; }
    public required string ItemId { get; init; }
    public string? ObjectType { get; init; }
    /// <summary>Human-readable rationale (PII-safe: reason text, never record content).</summary>
    public required string Reason { get; init; }
    /// <summary>Previous entry's Hash (all-zero for the genesis entry).</summary>
    public required string PrevHash { get; init; }
    /// <summary>SHA-256 over the canonical form of this entry (see ComputeHash).</summary>
    public required string Hash { get; init; }
}

public interface IDecisionLedger
{
    DecisionLedgerEntry Append(string decision, string itemId, string? objectType, string reason);
    IReadOnlyList<DecisionLedgerEntry> ReadAll();
    bool Verify(out int brokenAtSeq);
}

public sealed class DecisionLedger : IDecisionLedger
{
    public const string EnvVar = "DECISION_LEDGER";

    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector.decision_ledger");

    /// <summary>Genesis PrevHash — 64 hex zeros.</summary>
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly JsonSerializerOptions JsonlOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _sync = new();

    // Chain-tail cache: (entry count, last hash, file length at cache time). Makes
    // each append O(1); validated against the current file length before use so a
    // truncation or a separate process appending triggers a strict full reload.
    private (int Count, string LastHash, long FileLength)? _tail;

    public string Path { get; }

    public DecisionLedger(string connectorId, string? logsDir = null)
    {
        var dir = logsDir
            ?? Environment.GetEnvironmentVariable("LOGS_DIR")
            ?? System.IO.Path.Combine(Directory.GetCurrentDirectory(), "logs");
        Path = System.IO.Path.Combine(dir, $"decision_ledger_{connectorId}.jsonl");
    }

    /// <summary>
    /// Construct a ledger only when <c>DECISION_LEDGER</c> is truthy; otherwise
    /// return null so the pipeline skips all decision recording (byte-identical to
    /// today). Uses <see cref="SyncState"/>-style logs dir resolution.
    /// </summary>
    public static DecisionLedger? CreateIfEnabled(string connectorId, string? logsDir = null)
    {
        if (!EnvFlags.IsTrue(EnvVar))
            return null;
        return new DecisionLedger(connectorId, logsDir ?? Config.SyncState.LogsDir);
    }

    /// <summary>
    /// Canonical hash of an entry: a fixed field order over the previous hash + this
    /// entry's content. Deterministic, so Verify recomputes it exactly.
    /// </summary>
    public static string ComputeHash(long seq, DateTime timestampUtc, string decision,
        string itemId, string? objectType, string reason, string prevHash)
    {
        var canonical = string.Join("", new[]
        {
            prevHash,
            seq.ToString(CultureInfo.InvariantCulture),
            timestampUtc.ToString("O"),
            decision,
            itemId,
            objectType ?? "",
            reason,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public DecisionLedgerEntry Append(string decision, string itemId, string? objectType, string reason)
    {
        lock (_sync)
        {
            var currentLength = File.Exists(Path) ? new FileInfo(Path).Length : 0L;
            if (_tail == null || _tail.Value.FileLength != currentLength)
            {
                var existing = ReadEntries(out var firstBadLine);
                if (firstBadLine != 0)
                {
                    // A chain that cannot be parsed must never be appended to —
                    // that would bury the tamper under fresh entries.
                    Logger.Error(
                        $"Decision ledger '{Path}' line {firstBadLine} is unreadable — REFUSING to append " +
                        $"'{decision}' for item '{itemId}' to a corrupt chain. Restore the ledger, then re-run.");
                    throw new InvalidDataException(
                        $"Decision ledger '{Path}' line {firstBadLine} is unreadable — refusing to " +
                        "append to a corrupt chain. Run Verify and restore the ledger first.");
                }
                _tail = (existing.Count, existing.Count > 0 ? existing[^1].Hash : GenesisHash, currentLength);
            }

            var seq = _tail.Value.Count + 1;
            var prevHash = _tail.Value.LastHash;
            var timestamp = DateTime.UtcNow;
            var hash = ComputeHash(seq, timestamp, decision, itemId, objectType, reason, prevHash);

            var entry = new DecisionLedgerEntry
            {
                Seq = seq,
                TimestampUtc = timestamp,
                Decision = decision,
                ItemId = itemId,
                ObjectType = objectType,
                Reason = reason,
                PrevHash = prevHash,
                Hash = hash,
            };

            SecureDirectory.EnsureOwnerOnly(System.IO.Path.GetDirectoryName(Path)!);
            using (var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.WriteLine(JsonSerializer.Serialize(entry, JsonlOptions));
            }
            _tail = (seq, hash, new FileInfo(Path).Length);
            return entry;
        }
    }

    public IReadOnlyList<DecisionLedgerEntry> ReadAll()
    {
        lock (_sync)
        {
            var entries = ReadEntries(out var firstBadLine);
            if (firstBadLine != 0)
            {
                throw new InvalidDataException(
                    $"Decision ledger '{Path}' line {firstBadLine} is unreadable/tampered — " +
                    "use Verify() to check integrity without throwing.");
            }
            return entries;
        }
    }

    /// <summary>
    /// Tolerant line reader: parses entries up to the first unreadable line;
    /// firstBadLine is that line's 1-based number (0 = whole file parsed).
    /// </summary>
    private List<DecisionLedgerEntry> ReadEntries(out int firstBadLine)
    {
        firstBadLine = 0;
        var entries = new List<DecisionLedgerEntry>();
        if (!File.Exists(Path))
            return entries;
        var lineNumber = 0;
        foreach (var line in ReadLinesShared(Path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            DecisionLedgerEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<DecisionLedgerEntry>(line);
            }
            catch (JsonException)
            {
                entry = null;
            }
            if (entry == null)
            {
                firstBadLine = lineNumber;
                break;
            }
            entries.Add(entry);
        }
        return entries;
    }

    /// <summary>
    /// Verify the whole chain. Returns true when every entry's recomputed hash
    /// matches and links to its predecessor; on failure, brokenAtSeq is the first
    /// entry whose integrity check failed (0 when the file is empty/absent). NEVER
    /// throws on tampered content — a line that no longer parses reports the chain
    /// as broken at that line, exactly like a hash mismatch.
    /// </summary>
    public bool Verify(out int brokenAtSeq)
    {
        lock (_sync)
        {
            var entries = ReadEntries(out var firstBadLine);
            var prevHash = GenesisHash;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var expectedSeq = i + 1;
                var recomputed = ComputeHash(entry.Seq, entry.TimestampUtc, entry.Decision,
                    entry.ItemId, entry.ObjectType, entry.Reason, entry.PrevHash);
                if (entry.Seq != expectedSeq || entry.PrevHash != prevHash || entry.Hash != recomputed)
                {
                    brokenAtSeq = (int)entry.Seq;
                    Logger.Error(
                        $"Decision ledger '{Path}' FAILED verification: chain broken at seq {brokenAtSeq} " +
                        "(sequence/link/hash mismatch — the entry or an earlier one was edited, reordered or deleted).");
                    return false;
                }
                prevHash = entry.Hash;
            }
            if (firstBadLine != 0)
            {
                brokenAtSeq = firstBadLine;   // unreadable line = broken chain link
                Logger.Error(
                    $"Decision ledger '{Path}' FAILED verification: chain broken at seq {brokenAtSeq} " +
                    "(line does not parse as a ledger entry).");
                return false;
            }
            brokenAtSeq = 0;
            return true;
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
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

}
