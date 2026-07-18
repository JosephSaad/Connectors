// Altrata/ErasureLedger.cs
// ------------------------
// Tamper-evident, append-only erasure ledger for per-subject erasure (DSAR /
// right-to-erasure). Distinct from the purpose-of-use audit log (AuditLog):
// the audit log records lawful USE of the data, this ledger records its
// REMOVAL — who erased which subject, when, and exactly what was withdrawn.
//
// The hash-chain mechanics (append, corrupt-chain refusal, tolerant reader,
// Verify) live in the shared HashChainedLedger<T> base; this type supplies the
// erasure entry, its canonical hash and the erasure-specific naming/metrics.
// Retained across purge-all (the license-end obligation is to remove DATA; the
// erasure ledger is the compliance record).

using System.Globalization;
using System.Text.Json;

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

public sealed class ErasureLedger : HashChainedLedger<ErasureLedgerEntry>, IErasureLedger
{
    // PII CAUTION: ledger ENTRIES may carry a subject email (the compliance
    // record needs it), but no LOG line here ever may — log only the file path,
    // seq / line numbers, the action and the opaque subject id.
    private static readonly IAppLogger LedgerLogger =
        Logging.GetLogger("altrata_connector.erasure_ledger");

    protected override IAppLogger Logger => LedgerLogger;
    protected override string LedgerName => "Erasure ledger";

    /// <summary>Gauge drives the SIEM ledger-tamper alert (severity: security) —
    /// docs/SIEM.md, ops/prometheus-alerts.yml.</summary>
    protected override string BrokenMetricName => "altrata_erasure_ledger_broken";

    public ErasureLedger(string connectorId, string? logsDir = null)
        : base(BuildPath(connectorId, logsDir)) { }

    private static string BuildPath(string connectorId, string? logsDir)
    {
        var dir = logsDir
            ?? Environment.GetEnvironmentVariable("LOGS_DIR")
            ?? System.IO.Path.Combine(Directory.GetCurrentDirectory(), "logs");
        return System.IO.Path.Combine(dir, $"erasure_ledger_{connectorId}.jsonl");
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
        var canonical = string.Join("", new[]
        {
            prevHash,
            seq.ToString(CultureInfo.InvariantCulture),
            timestampUtc.ToString("O"),
            actor,
            action,
            subjectId,
            subjectEmail ?? "",
            correlationId ?? "",
            string.Join(",", itemsRemoved),
        });
        return Sha256Hex(canonical);
    }

    public ErasureLedgerEntry Append(string actor, string action, string subjectId,
        string? subjectEmail, IReadOnlyList<string> itemsRemoved) =>
        AppendCore($"'{action}' for subject '{subjectId}'",
            (seq, timestamp, correlationId, prevHash) =>
            {
                var hash = ComputeHash(seq, timestamp, actor, action, subjectId, subjectEmail,
                    correlationId, itemsRemoved, prevHash);
                return new ErasureLedgerEntry
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
            });

    protected override long SeqOf(ErasureLedgerEntry e) => e.Seq;
    protected override string PrevHashOf(ErasureLedgerEntry e) => e.PrevHash;
    protected override string HashOf(ErasureLedgerEntry e) => e.Hash;

    protected override string RecomputeHash(ErasureLedgerEntry e) =>
        ComputeHash(e.Seq, e.TimestampUtc, e.Actor, e.Action, e.SubjectId, e.SubjectEmail,
            e.CorrelationId, e.ItemsRemoved, e.PrevHash);

    protected override ErasureLedgerEntry? Deserialize(string line) =>
        JsonSerializer.Deserialize<ErasureLedgerEntry>(line);
}
