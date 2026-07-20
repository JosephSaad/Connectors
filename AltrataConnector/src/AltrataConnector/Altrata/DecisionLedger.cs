// Altrata/DecisionLedger.cs
// -------------------------
// Tamper-evident, append-only DECISION ledger: the immutable audit of the two
// governance decisions that narrow what reaches (or stays in) the seat-gated
// index —
//
//   * EXCLUSION       — an item/delivery deliberately kept OUT of the index
//                       (e.g. a delivery rejected for a checksum mismatch).
//   * ACL-RESTRICTION — an item whose ACL was tightened below the seat default
//                       by classification enforcement (CLASSIFICATION_ENFORCE_ACL:
//                       a top-tier "Restricted" item locked to the reviewer group).
//
// Scoped to these LOW-VOLUME decisions on purpose — it is NOT written on the
// normal per-item ingest path. Shares the SHA-256 hash-chain substrate with the
// erasure ledger (HashChainedLedger<T>): Verify() detects any edit/reorder/
// delete without a trusted external store.
//
// PII CAUTION (enforced by tests): entries carry the OPAQUE item id, the
// decision, a PII-safe reason string, the actor and the correlation id ONLY —
// never a personal value (name/email/wealth).

using System.Globalization;
using System.Text.Json;

using AltrataConnector.Infrastructure;

namespace AltrataConnector.Altrata;

public static class DecisionActions
{
    public const string Exclude = "exclude";
    public const string RestrictAcl = "acl-restrict";

    /// <summary>QUARANTINE — an item the ContentGate stage refused to index and
    /// routed to the dead-letter queue for human review (CS-1). Its OWN kind on
    /// purpose: a quarantine is reversible (retry-failed re-drives it) and is
    /// driven by a heuristic, whereas an exclusion is a permanent, deterministic
    /// refusal. Overloading 'exclude' would make the two indistinguishable in
    /// the audit.</summary>
    public const string Quarantine = "quarantine";
}

public sealed record DecisionLedgerEntry
{
    /// <summary>1-based position in the chain.</summary>
    public required long Seq { get; init; }
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    /// <summary>Opaque item id (or delivery id) the decision concerns.</summary>
    public required string ItemId { get; init; }
    /// <summary>exclude | acl-restrict.</summary>
    public required string Decision { get; init; }
    /// <summary>PII-safe rationale (no personal values).</summary>
    public required string Reason { get; init; }
    public string? Actor { get; init; }
    public string? CorrelationId { get; init; }
    public required string PrevHash { get; init; }
    public required string Hash { get; init; }
}

public interface IDecisionLedger
{
    DecisionLedgerEntry Append(string itemId, string decision, string reason, string? actor = null);
    DecisionLedgerEntry RecordExclusion(string itemId, string reason, string? actor = null);
    DecisionLedgerEntry RecordAclRestriction(string itemId, string reason, string? actor = null);

    /// <summary>ContentGate quarantine (CS-1). <paramref name="reason"/> must be
    /// the PII-safe "content-gate:&lt;category&gt;" string — never matched text.</summary>
    DecisionLedgerEntry RecordQuarantine(string itemId, string reason, string? actor = null);
    IReadOnlyList<DecisionLedgerEntry> ReadAll();
    bool Verify(out int brokenAtSeq);
}

public sealed class DecisionLedger : HashChainedLedger<DecisionLedgerEntry>, IDecisionLedger
{
    private static readonly IAppLogger LedgerLogger =
        Logging.GetLogger("altrata_connector.decision_ledger");

    protected override IAppLogger Logger => LedgerLogger;
    protected override string LedgerName => "Decision ledger";
    protected override string BrokenMetricName => "altrata_decision_ledger_broken";

    public DecisionLedger(string connectorId, string? logsDir = null)
        : base(BuildPath(connectorId, logsDir)) { }

    private static string BuildPath(string connectorId, string? logsDir)
    {
        var dir = logsDir
            ?? Environment.GetEnvironmentVariable("LOGS_DIR")
            ?? System.IO.Path.Combine(Directory.GetCurrentDirectory(), "logs");
        return System.IO.Path.Combine(dir, $"decision_ledger_{connectorId}.jsonl");
    }

    /// <summary>Canonical hash: fixed field order over the previous hash + this
    /// entry's content. Deterministic, so Verify recomputes it exactly.</summary>
    public static string ComputeHash(long seq, DateTime timestampUtc, string itemId, string decision,
        string reason, string? actor, string? correlationId, string prevHash)
    {
        var canonical = string.Join("", new[]
        {
            prevHash,
            seq.ToString(CultureInfo.InvariantCulture),
            timestampUtc.ToString("O"),
            itemId,
            decision,
            reason,
            actor ?? "",
            correlationId ?? "",
        });
        return Sha256Hex(canonical);
    }

    public DecisionLedgerEntry Append(string itemId, string decision, string reason, string? actor = null) =>
        AppendCore($"'{decision}' for item '{itemId}'",
            (seq, timestamp, correlationId, prevHash) =>
            {
                var hash = ComputeHash(seq, timestamp, itemId, decision, reason, actor, correlationId, prevHash);
                return new DecisionLedgerEntry
                {
                    Seq = seq,
                    TimestampUtc = timestamp,
                    ItemId = itemId,
                    Decision = decision,
                    Reason = reason,
                    Actor = actor,
                    CorrelationId = correlationId,
                    PrevHash = prevHash,
                    Hash = hash,
                };
            });

    public DecisionLedgerEntry RecordExclusion(string itemId, string reason, string? actor = null) =>
        Append(itemId, DecisionActions.Exclude, reason, actor);

    public DecisionLedgerEntry RecordAclRestriction(string itemId, string reason, string? actor = null) =>
        Append(itemId, DecisionActions.RestrictAcl, reason, actor);

    public DecisionLedgerEntry RecordQuarantine(string itemId, string reason, string? actor = null) =>
        Append(itemId, DecisionActions.Quarantine, reason, actor);

    protected override long SeqOf(DecisionLedgerEntry e) => e.Seq;
    protected override string PrevHashOf(DecisionLedgerEntry e) => e.PrevHash;
    protected override string HashOf(DecisionLedgerEntry e) => e.Hash;

    protected override string RecomputeHash(DecisionLedgerEntry e) =>
        ComputeHash(e.Seq, e.TimestampUtc, e.ItemId, e.Decision, e.Reason, e.Actor,
            e.CorrelationId, e.PrevHash);

    protected override DecisionLedgerEntry? Deserialize(string line) =>
        JsonSerializer.Deserialize<DecisionLedgerEntry>(line);
}
