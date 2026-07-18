// State/DeadLetterPolicy.cs
// -------------------------
// Dead-letter payload protection. The dead-letter queue is the one place a
// FULL wealth/relationship profile would otherwise sit at rest OUTSIDE the
// seat-gated index (logs/failed_records_*.jsonl or the SQL queue table) —
// operators read it, copy it into tickets, and back it up with the logs.
//
//   DEADLETTER_PAYLOAD_MODE=redacted   (DEFAULT for this connector — the data
//       is the most sensitive of the connector family; decision + rationale in
//       SECURITY.md) — queue records carry ids, subject HASHES, the error and
//       the attempt count only. retry-failed re-fetches the record from the
//       still-on-disk feed delivery (checksum-verified) and re-transforms it.
//
//   DEADLETTER_PAYLOAD_MODE=full       — sibling-default behaviour: the
//       transformed item JSON is captured verbatim for standalone replay
//       (works after the feed delivery is archived/deleted). Raw subject ids
//       ride along for the DSAR suppression guard at replay time.
//
// Any other value fails fast naming the setting. In BOTH modes every queue
// record carries subject hashes, and retry-failed refuses to replay an upsert
// whose subject has been erased since (suppression wins; DELETE ops are exempt
// because they COMPLETE erasures).

using System.Security.Cryptography;
using System.Text;
using AltrataConnector.Config;

namespace AltrataConnector.State;

public static class DeadLetterPolicy
{
    public const string EnvVar = "DEADLETTER_PAYLOAD_MODE";
    public const string Full = "full";
    public const string Redacted = "redacted";

    /// <summary>Current mode (live env read, like the chassis' other knobs).
    /// Default REDACTED — see SECURITY.md for the decision record.</summary>
    public static string Mode
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(EnvVar);
            if (string.IsNullOrWhiteSpace(raw))
                return Redacted;
            var value = raw.Trim().ToLowerInvariant();
            if (value is Full or Redacted)
                return value;
            throw new ConfigurationError(
                $"{EnvVar} must be '{Full}' or '{Redacted}', got '{raw.Trim()}'");
        }
    }

    public static bool IsRedacted => Mode == Redacted;

    /// <summary>Short stable SHA-256 (16 hex) of a subject id — the PII-safe
    /// join key between queue records and the erasure suppression list. Same
    /// shape as the match-review dedup hashes.</summary>
    public static string HashSubject(string subjectId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subjectId)))
            .ToLowerInvariant()[..16];

    public static IReadOnlyList<string> HashSubjects(IReadOnlyCollection<string> subjectIds)
    {
        if (subjectIds.Count == 0)
            return Array.Empty<string>();
        var hashes = new List<string>(subjectIds.Count);
        foreach (var id in subjectIds)
            hashes.Add(HashSubject(id));
        return hashes;
    }
}
