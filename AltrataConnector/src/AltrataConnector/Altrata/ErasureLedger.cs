// Altrata/ErasureLedger.cs
// ------------------------
// Tamper-evident, append-only erasure ledger for per-subject erasure (DSAR /
// right-to-erasure). Distinct from the purpose-of-use audit log (AuditLog):
// the audit log records lawful USE of the data, this ledger records its
// REMOVAL — who erased which subject, when, and exactly what was withdrawn.
//
// Tamper evidence: entries form a SHA-256 hash chain. Each entry's Hash covers
// its own fields plus the previous entry's Hash, so editing, reordering or
// deleting any entry breaks every subsequent link — Verify() detects it without
// a trusted external store. The file is only ever opened in append mode; no
// code path rewrites earlier lines. Retained across purge-all (the license-end
// obligation is to remove DATA; the erasure ledger is the compliance record).

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using AltrataConnector.Infrastructure;

namespace AltrataConnector.Altrata;

public static class ErasureActions
{
    public const string Erase = "erase";
    public const string Unsuppress = "unsuppress";
}

public sealed record ErasureLedgerEntry
{
    /// <summary>1-based position in the chain.</summary>
    public required long Seq { get; init; }
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public required string Actor { get; init; }
    /// <summary>erase | unsuppress.</summary>
    public required string Action { get; init; }
    public required string SubjectId { get; init; }
    public string? SubjectEmail { get; init; }
    /// <summary>Correlation id of the erasure cycle (links the ledger to the
    /// structured logs and spans for this operation).</summary>
    public string? CorrelationId { get; init; }
    public IReadOnlyList<string> ItemsRemoved { get; init; } = Array.Empty<string>();
    /// <summary>Previous entry's Hash (all-zero for the genesis entry).</summary>
    public required string PrevHash { get; init; }
    /// <summary>SHA-256 over the canonical form of this entry (see ComputeHash).</summary>
    public required string Hash { get; init; }
}

public interface IErasureLedger
{
    ErasureLedgerEntry Append(string actor, string action, string subjectId,
        string? subjectEmail, IReadOnlyList<string> itemsRemoved);
    IReadOnlyList<ErasureLedgerEntry> ReadAll();
    bool Verify(out int brokenAtSeq);
}

public sealed class ErasureLedger : IErasureLedger
{
    // PII CAUTION: ledger ENTRIES may carry a subject email (the compliance
    // record needs it), but no LOG line here ever may — log only the file path,
    // seq / line numbers, the action and the opaque subject id.
    private static readonly IAppLogger Logger = Logging.GetLogger("altrata_connector.erasure_ledger");

    /// <summary>Genesis PrevHash — 64 hex zeros.</summary>
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly JsonSerializerOptions JsonlOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _sync = new();

    /// <summary>
    /// Chain-tail cache: (entry count, last hash, file length at cache time).
    /// Append re-read and re-parsed the WHOLE file per call, making an N-entry
    /// ledger O(N²) overall and capping realistic DSAR volumes at a few
    /// thousand entries; with the cache each append is O(1). The cache is
    /// validated against the current file length before every use, so another
    /// instance appending to the same file (sequential cross-instance use, as
    /// commands in separate processes do) or an external truncation triggers a
    /// full strict reload — byte-identical chaining semantics to the uncached
    /// implementation. Guarded by _sync.
    /// </summary>
    private (int Count, string LastHash, long FileLength)? _tail;

    public string Path { get; }

    public ErasureLedger(string connectorId, string? logsDir = null)
    {
        var dir = logsDir
            ?? Environment.GetEnvironmentVariable("LOGS_DIR")
            ?? System.IO.Path.Combine(Directory.GetCurrentDirectory(), "logs");
        Path = System.IO.Path.Combine(dir, $"erasure_ledger_{connectorId}.jsonl");
    }

    /// <summary>
    /// Canonical hash of an entry: a fixed field order joined with a separator
    /// that cannot occur in the values, over the previous hash + this entry's
    /// content. Deterministic, so Verify recomputes it exactly.
    /// </summary>
    public static string ComputeHash(long seq, DateTime timestampUtc, string actor, string action,
        string subjectId, string? subjectEmail, string? correlationId,
        IReadOnlyList<string> itemsRemoved, string prevHash)
    {
        var canonical = string.Join("", new[]
        {
            prevHash,
            seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
            timestampUtc.ToString("O"),
            actor,
            action,
            subjectId,
            subjectEmail ?? "",
            correlationId ?? "",
            string.Join(",", itemsRemoved),
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public ErasureLedgerEntry Append(string actor, string action, string subjectId,
        string? subjectEmail, IReadOnlyList<string> itemsRemoved)
    {
        lock (_sync)
        {
            var currentLength = File.Exists(Path) ? new FileInfo(Path).Length : 0L;
            if (_tail == null || _tail.Value.FileLength != currentLength)
            {
                // Cold cache, or the file changed under us (another instance
                // appended / external truncation): strict full reload. A chain
                // that cannot be parsed must never be appended to — that would
                // bury the tamper under fresh entries.
                var existing = ReadEntries(out var firstBadLine);
                if (firstBadLine != 0)
                {
                    // LOUD refusal: the append is the erasure's compliance
                    // record, so a corrupt chain must be impossible to miss.
                    // Ids only — never the subject email.
                    Logger.Error(
                        $"Erasure ledger '{Path}' line {firstBadLine} is unreadable — REFUSING to append " +
                        $"'{action}' for subject '{subjectId}' to a corrupt chain. Restore the ledger, then re-run.");
                    throw new InvalidDataException(
                        $"Erasure ledger '{Path}' line {firstBadLine} is unreadable — refusing to " +
                        "append to a corrupt chain. Run verify and restore the ledger first.");
                }
                _tail = (existing.Count, existing.Count > 0 ? existing[^1].Hash : GenesisHash, currentLength);
            }

            var seq = _tail.Value.Count + 1;
            var prevHash = _tail.Value.LastHash;
            var timestamp = DateTime.UtcNow;
            var correlationId = CorrelationContext.Current;
            var hash = ComputeHash(seq, timestamp, actor, action, subjectId, subjectEmail,
                correlationId, itemsRemoved, prevHash);

            var entry = new ErasureLedgerEntry
            {
                Seq = seq,
                TimestampUtc = timestamp,
                Actor = actor,
                Action = action,
                SubjectId = subjectId,
                SubjectEmail = subjectEmail,
                CorrelationId = correlationId,
                ItemsRemoved = itemsRemoved,
                PrevHash = prevHash,
                Hash = hash,
            };

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            using (var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.WriteLine(JsonSerializer.Serialize(entry, JsonlOptions));
            }
            _tail = (seq, hash, new FileInfo(Path).Length);
            return entry;
        }
    }

    public IReadOnlyList<ErasureLedgerEntry> ReadAll()
    {
        lock (_sync)
        {
            var entries = ReadEntries(out var firstBadLine);
            if (firstBadLine != 0)
            {
                Logger.Error(
                    $"Erasure ledger '{Path}' line {firstBadLine} is unreadable/tampered — ReadAll refused.");
                throw new InvalidDataException(
                    $"Erasure ledger '{Path}' line {firstBadLine} is unreadable/tampered — " +
                    "use Verify() to check integrity without throwing.");
            }
            return entries;
        }
    }

    /// <summary>
    /// Tolerant line reader: parses entries up to the first unreadable line;
    /// firstBadLine is that line's 1-based number (0 = whole file parsed).
    /// Blank lines are skipped; an unparseable line stops the scan so Verify
    /// can pinpoint the break instead of throwing on tampered bytes.
    /// </summary>
    private List<ErasureLedgerEntry> ReadEntries(out int firstBadLine)
    {
        firstBadLine = 0;
        var entries = new List<ErasureLedgerEntry>();
        if (!File.Exists(Path))
            return entries;
        var lineNumber = 0;
        foreach (var line in File.ReadLines(Path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            ErasureLedgerEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<ErasureLedgerEntry>(line);
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
    /// matches and links to its predecessor; on failure, brokenAtSeq is the
    /// first entry whose integrity check failed (0 when the file is empty/absent).
    /// NEVER throws on tampered content — a line that no longer parses as an
    /// entry (structural corruption) reports the chain as broken at that line,
    /// exactly like a hash mismatch does.
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
                var recomputed = ComputeHash(entry.Seq, entry.TimestampUtc, entry.Actor, entry.Action,
                    entry.SubjectId, entry.SubjectEmail, entry.CorrelationId, entry.ItemsRemoved, entry.PrevHash);
                if (entry.Seq != expectedSeq || entry.PrevHash != prevHash || entry.Hash != recomputed)
                {
                    brokenAtSeq = (int)entry.Seq;
                    Logger.Error(
                        $"Erasure ledger '{Path}' FAILED verification: chain broken at seq {brokenAtSeq} " +
                        "(sequence/link/hash mismatch — the entry or an earlier one was edited, reordered or deleted).");
                    return false;
                }
                prevHash = entry.Hash;
            }
            if (firstBadLine != 0)
            {
                brokenAtSeq = firstBadLine;   // unreadable line = broken chain link
                Logger.Error(
                    $"Erasure ledger '{Path}' FAILED verification: chain broken at seq {brokenAtSeq} " +
                    "(line does not parse as a ledger entry).");
                return false;
            }
            brokenAtSeq = 0;
            return true;
        }
    }
}
