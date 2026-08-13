// Connector.Chassis/LogRedaction.cs
// ---------------------------------
// Pseudonymisation for identifiers that would otherwise reach a log file.
//
// Run logs are the blind spot in this fleet's privacy story. Dead-letter
// payloads are redacted by default and indexed items are erasable through the
// DSAR path, but connector.log is neither: it is retained on the node, mirrored
// to the Windows Event Log and shipped to SIEM, and a ForgetSubject request
// never reaches it. An email written there at INFO or WARNING outlives the
// erasure it was supposed to honour.
//
// The shape here matches the fleet's existing convention — Altrata's
// DeadLetterPolicy.HashSubject uses the same short SHA-256 — so a hash in a log
// line joins against a hash in a dead-letter record.

using System.Security.Cryptography;
using System.Text;

namespace Connector.Chassis;

/// <summary>Redaction helpers for values that must not appear in logs verbatim.</summary>
public static class LogRedaction
{
    /// <summary>Placeholder for an absent value, so a null reads distinctly from a redaction.</summary>
    public const string None = "<none>";

    /// <summary>
    /// Render an email address as <c>&lt;16-hex&gt;@domain</c> — for example
    /// <c>4f2a91c8b30de751@contoso.com</c>.
    /// </summary>
    /// <remarks>
    /// The domain stays in clear because it is what operational triage actually
    /// needs: "every unresolved user is @contoso.com" identifies a tenant or
    /// federation problem, while the individual mailbox almost never changes the
    /// diagnosis. The local part is replaced by a stable hash of the WHOLE
    /// address, so repeat failures for one person still correlate across lines
    /// and across runs.
    ///
    /// This is pseudonymisation, not anonymisation. A SHA-256 of an address is
    /// reversible by anyone holding a candidate list, so the output is still
    /// personal data under GDPR and still belongs inside the log's normal
    /// retention and access controls. What it buys is that a log leak no longer
    /// hands over a directory of addresses, and that a casual reader of a run log
    /// cannot identify a subject. A keyed HMAC would close the brute-force gap,
    /// at the cost of a per-deployment secret to manage and rotate; that trade
    /// was not worth making for a correlation key.
    /// </remarks>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return None;
        }

        var value = email.Trim();
        var hash = ShortHash(value);
        var at = value.LastIndexOf('@');

        // Only split on a plausible address: an '@' with something either side.
        // Anything else is not an email and gets hashed whole rather than having
        // an arbitrary tail published in clear.
        return at > 0 && at < value.Length - 1
            ? $"{hash}@{value[(at + 1)..]}"
            : hash;
    }

    /// <summary>
    /// Render an arbitrary identifier (user id, owner id, subject id) as a stable
    /// short hash, with nothing in clear.
    /// </summary>
    public static string MaskId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? None : ShortHash(id.Trim());

    /// <summary>
    /// Short stable SHA-256 (16 lowercase hex). Deliberately the same shape as
    /// Altrata's <c>DeadLetterPolicy.HashSubject</c>, so the two are joinable.
    /// </summary>
    private static string ShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..16];
}
