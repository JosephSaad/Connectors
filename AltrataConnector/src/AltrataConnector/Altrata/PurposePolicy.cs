// Altrata/PurposePolicy.cs
// ------------------------
// Purpose-based authorization / per-action veto. The connector records a
// purpose-of-use string on every billable enrichment lookup (AuditLog), but a
// recorded purpose was never EVALUATED — any purpose proceeded. This policy
// closes that gap: when a purpose allowlist is configured it is enforced BEFORE
// the billable/sensitive action, fail-closed.
//
//   PURPOSE_ALLOWLIST (default unset) — comma-separated list of permitted
//       purpose values (matched case-insensitively, trimmed). When SET,
//       enforcement is ON: a lookup whose --purpose is not on the list (or is
//       empty) is DENIED and audited; the billable call never happens.
//       When UNSET, enforcement is OFF — behaviour is unchanged (the purpose is
//       recorded only), and that fact is logged so the posture is never silent.
//
// The purpose and actor are operator-supplied identifiers, not subject PII, so
// they stay loggable exactly as the audit trail already treats them.

namespace AltrataConnector.Altrata;

public sealed class PurposeDeniedException : Exception
{
    public PurposeDeniedException(string message) : base(message) { }
}

public sealed record PurposeDecision(bool Allowed, string Reason);

public interface IPurposePolicy
{
    /// <summary>True when a purpose allowlist is configured (enforcing).</summary>
    bool Enforcing { get; }
    PurposeDecision Evaluate(string actor, string purpose);
}

public sealed class PurposePolicy : IPurposePolicy
{
    public const string AllowlistEnvVar = "PURPOSE_ALLOWLIST";

    private readonly HashSet<string> _allowed;

    public PurposePolicy(IEnumerable<string>? allowedPurposes)
    {
        _allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (allowedPurposes != null)
        {
            foreach (var purpose in allowedPurposes)
            {
                if (!string.IsNullOrWhiteSpace(purpose))
                    _allowed.Add(purpose.Trim());
            }
        }
    }

    public bool Enforcing => _allowed.Count > 0;

    /// <summary>Read PURPOSE_ALLOWLIST from the environment (live, like the other knobs).</summary>
    public static PurposePolicy FromEnv()
    {
        var raw = Environment.GetEnvironmentVariable(AllowlistEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
            return new PurposePolicy(null);
        return new PurposePolicy(raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>
    /// Decide whether <paramref name="actor"/> may act under
    /// <paramref name="purpose"/>. When enforcement is off every purpose is
    /// allowed (record-only); when on, the purpose must be a non-empty value on
    /// the allowlist (fail-closed).
    /// </summary>
    public PurposeDecision Evaluate(string actor, string purpose)
    {
        if (!Enforcing)
            return new PurposeDecision(true, "enforcement-off");
        if (string.IsNullOrWhiteSpace(purpose))
            return new PurposeDecision(false, "empty-purpose");
        return _allowed.Contains(purpose.Trim())
            ? new PurposeDecision(true, "allowed")
            : new PurposeDecision(false, "not-in-allowlist");
    }
}
