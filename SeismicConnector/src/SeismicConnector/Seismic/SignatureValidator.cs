// Seismic/SignatureValidator.cs
// -----------------------------
// Shared-secret HMAC validation for inbound webhook posts. This is the security
// boundary: NO payload is parsed or enqueued until its signature is validated
// against SEISMIC_WEBHOOK_SECRET.
//
// Contract:
//   • The sender computes HMAC-SHA256(secret, raw-request-body) and sends it in
//     the signature header (default X-Seismic-Signature). Hex (optionally
//     "sha256=" prefixed) and base64 encodings are both accepted.
//   • Validation is over the EXACT raw body bytes, before any JSON parse.
//   • Comparison is constant-time (CryptographicOperations.FixedTimeEquals) to
//     avoid timing oracles.
//   • Fail-closed: a missing/empty secret means the receiver must not run at all
//     (enforced by the receiver); a missing/malformed/mismatched signature on a
//     request is rejected.

using System.Security.Cryptography;
using System.Text;

namespace SeismicConnector.Seismic;

public sealed class SignatureValidator
{
    /// <summary>Default header carrying the request signature.</summary>
    public const string DefaultHeader = "X-Seismic-Signature";

    private readonly byte[] _secret;

    public SignatureValidator(string secret)
    {
        if (string.IsNullOrEmpty(secret))
            throw new ArgumentException("Webhook secret must not be empty.", nameof(secret));
        _secret = Encoding.UTF8.GetBytes(secret);
    }

    /// <summary>Compute the hex HMAC-SHA256 of <paramref name="body"/> — the value
    /// a sender puts in the signature header. Used by tests and tooling.</summary>
    public string ComputeHex(byte[] body)
    {
        using var hmac = new HMACSHA256(_secret);
        return Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    /// <summary>
    /// Compute the hex HMAC over the anti-replay signed payload
    /// <c>timestamp + "." + body</c> — the value a timestamp-aware sender puts in
    /// the signature header. Binding the timestamp INTO the HMAC means a captured
    /// request cannot be replayed with a fresh timestamp (that would change the
    /// signed bytes and break the signature).
    /// </summary>
    public string ComputeHex(string timestamp, byte[] body)
    {
        using var hmac = new HMACSHA256(_secret);
        return Convert.ToHexString(hmac.ComputeHash(SignedPayload(timestamp, body))).ToLowerInvariant();
    }

    /// <summary>Validate <paramref name="providedSignature"/> over the timestamped
    /// signed payload (<c>timestamp + "." + body</c>). Same encodings / constant-time
    /// compare as the body-only overload.</summary>
    public bool IsValid(string timestamp, byte[] body, string? providedSignature) =>
        IsValid(SignedPayload(timestamp, body), providedSignature);

    /// <summary>The bytes the HMAC covers when a timestamp is bound in: utf8(timestamp + ".") ++ body.</summary>
    private static byte[] SignedPayload(string timestamp, byte[] body)
    {
        var prefix = Encoding.UTF8.GetBytes((timestamp ?? "") + ".");
        var payload = new byte[prefix.Length + body.Length];
        Buffer.BlockCopy(prefix, 0, payload, 0, prefix.Length);
        Buffer.BlockCopy(body, 0, payload, prefix.Length, body.Length);
        return payload;
    }

    /// <summary>
    /// True when <paramref name="providedSignature"/> matches the HMAC of
    /// <paramref name="body"/>. Accepts hex (with or without a "sha256=" prefix)
    /// or base64. A null/empty/garbage signature returns false. Constant-time in
    /// the length-matched path.
    /// </summary>
    public bool IsValid(byte[] body, string? providedSignature)
    {
        if (string.IsNullOrWhiteSpace(providedSignature))
            return false;

        using var hmac = new HMACSHA256(_secret);
        var expected = hmac.ComputeHash(body);

        var candidate = providedSignature.Trim();
        var eq = candidate.IndexOf('=');
        if (eq >= 0 && candidate[..eq].StartsWith("sha256", StringComparison.OrdinalIgnoreCase))
            candidate = candidate[(eq + 1)..];

        var provided = TryDecode(candidate);
        if (provided is null)
            return false;

        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    /// <summary>Decode a signature as hex first, then base64; null when neither parses.</summary>
    internal static byte[]? TryDecode(string signature)
    {
        signature = signature.Trim();
        try
        {
            if (signature.Length % 2 == 0 && signature.All(Uri.IsHexDigit))
                return Convert.FromHexString(signature);
        }
        catch (FormatException)
        {
            // fall through to base64
        }
        try
        {
            return Convert.FromBase64String(signature);
        }
        catch (FormatException)
        {
            // Neither hex nor base64 — by contract the caller treats null as an
            // invalid signature (fail-closed 401); nothing to log HERE because
            // the receiver logs every rejection with the remote endpoint.
            return null;
        }
    }
}
