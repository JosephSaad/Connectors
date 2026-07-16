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
    /// <summary>Genesis PrevHash — 64 hex zeros.</summary>
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly JsonSerializerOptions JsonlOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _sync = new();

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
            var existing = ReadAllInternal();
            var seq = existing.Count + 1;
            var prevHash = existing.Count > 0 ? existing[^1].Hash : GenesisHash;
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
            using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine(JsonSerializer.Serialize(entry, JsonlOptions));
            return entry;
        }
    }

    public IReadOnlyList<ErasureLedgerEntry> ReadAll()
    {
        lock (_sync)
            return ReadAllInternal();
    }

    private List<ErasureLedgerEntry> ReadAllInternal()
    {
        var entries = new List<ErasureLedgerEntry>();
        if (!File.Exists(Path))
            return entries;
        foreach (var line in File.ReadLines(Path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var entry = JsonSerializer.Deserialize<ErasureLedgerEntry>(line);
            if (entry != null)
                entries.Add(entry);
        }
        return entries;
    }

    /// <summary>
    /// Verify the whole chain. Returns true when every entry's recomputed hash
    /// matches and links to its predecessor; on failure, brokenAtSeq is the
    /// first entry whose integrity check failed (0 when the file is empty/absent).
    /// </summary>
    public bool Verify(out int brokenAtSeq)
    {
        lock (_sync)
        {
            var entries = ReadAllInternal();
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
                    return false;
                }
                prevHash = entry.Hash;
            }
            brokenAtSeq = 0;
            return true;
        }
    }
}
