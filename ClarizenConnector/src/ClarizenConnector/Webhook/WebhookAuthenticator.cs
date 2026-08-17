// Webhook/WebhookAuthenticator.cs
// -------------------------------
// The webhook security decision, in one testable place: HMAC signature +
// anti-replay. Called by the receiver over the RAW request bytes BEFORE any
// JSON parse (validate-before-parse is preserved).
//
// Anti-replay (docs/WEBHOOKS.md):
//   • The sender sends a timestamp header and signs "{timestamp}.{body}", so
//     the timestamp is bound INTO the HMAC and cannot be altered.
//   • Freshness: |now - timestamp| must be within the tolerance window
//     (CLARIZEN_WEBHOOK_TIMESTAMP_TOLERANCE_SECONDS, default 300). A stale (or
//     future-dated) request is rejected fail-closed.
//   • Duplicate suppression: a bounded, short-lived cache of recently-seen
//     signatures rejects a byte-for-byte replay within the window. The cache is
//     capped and prunes expired entries, so it cannot grow unbounded.
//
// Migration flag (CLARIZEN_WEBHOOK_REQUIRE_TIMESTAMP, default TRUE): with it on,
// a request WITHOUT a timestamp header is rejected (anti-replay is mandatory).
// Turn it off only to migrate senders that cannot yet send a timestamp — those
// fall back to legacy body-only HMAC validation (no replay protection). When on,
// anti-replay is real.

using System.Globalization;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Webhook;

public enum WebhookAuthResult
{
    Ok,
    MissingSignature,
    MissingTimestamp,
    StaleTimestamp,
    BadSignature,
    Replay,
}

public sealed class WebhookAuthenticator
{
    public const string RequireTimestampEnvVar = "CLARIZEN_WEBHOOK_REQUIRE_TIMESTAMP";
    public const string ToleranceEnvVar = "CLARIZEN_WEBHOOK_TIMESTAMP_TOLERANCE_SECONDS";
    public const string TimestampHeaderEnvVar = "CLARIZEN_WEBHOOK_TIMESTAMP_HEADER";
    public const string DefaultTimestampHeader = "X-Clarizen-Timestamp";

    private const int DefaultToleranceSeconds = 300;   // 5 minutes
    private const int DefaultSeenCacheCap = 8192;

    private readonly SignatureValidator _validator;
    private readonly bool _requireTimestamp;
    private readonly TimeSpan _tolerance;
    private readonly SeenSignatureCache _seen;

    public WebhookAuthenticator(
        string secret, bool requireTimestamp, TimeSpan tolerance, int seenCacheCap = DefaultSeenCacheCap)
    {
        _validator = new SignatureValidator(secret);
        _requireTimestamp = requireTimestamp;
        _tolerance = tolerance <= TimeSpan.Zero ? TimeSpan.FromSeconds(DefaultToleranceSeconds) : tolerance;
        _seen = new SeenSignatureCache(seenCacheCap);
    }

    /// <summary>The header this authenticator reads the signed timestamp from.</summary>
    public string TimestampHeader { get; init; } = DefaultTimestampHeader;

    /// <summary>True when a timestamp is mandatory (strict anti-replay).</summary>
    public bool RequireTimestamp => _requireTimestamp;

    /// <summary>Construct from the environment (defaults: require=true, tolerance=300s).</summary>
    public static WebhookAuthenticator FromEnv(string secret)
    {
        var require = !EnvFlags.IsFalse(RequireTimestampEnvVar);
        var toleranceSeconds = Math.Max(1, EnvFlags.GetInt(ToleranceEnvVar, DefaultToleranceSeconds));
        var header = EnvFlags.GetString(TimestampHeaderEnvVar, DefaultTimestampHeader);
        return new WebhookAuthenticator(secret, require, TimeSpan.FromSeconds(toleranceSeconds))
        {
            TimestampHeader = header,
        };
    }

    /// <summary>
    /// Validate a request. Constant-time signature comparison; fail-closed on a
    /// stale/forged timestamp; duplicate signatures within the window rejected.
    /// </summary>
    public WebhookAuthResult Verify(
        byte[] body, string? signature, string? timestampHeader, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return WebhookAuthResult.MissingSignature;

        if (string.IsNullOrWhiteSpace(timestampHeader))
        {
            if (_requireTimestamp)
                return WebhookAuthResult.MissingTimestamp;
            // Migration mode: legacy body-only HMAC, no replay protection.
            return _validator.IsValid(body, signature)
                ? WebhookAuthResult.Ok
                : WebhookAuthResult.BadSignature;
        }

        var timestamp = timestampHeader.Trim();
        if (!TryParseTimestamp(timestamp, out var sentUtc))
            return WebhookAuthResult.StaleTimestamp;   // unparseable → fail-closed

        // Absolute skew guards both stale AND future-dated requests.
        if ((nowUtc - sentUtc).Duration() > _tolerance)
            return WebhookAuthResult.StaleTimestamp;

        // Signature must match the timestamp-bound canonical message.
        if (!_validator.IsValid(body, signature, timestamp))
            return WebhookAuthResult.BadSignature;

        // Replay: the signature covers timestamp+body, so an identical replay
        // reproduces the same signature. Reject a repeat within the window. Only
        // record AFTER the signature is proven valid (never cache garbage).
        var key = NormalizeSignature(signature);
        if (!_seen.TryAdd(key, sentUtc + _tolerance, nowUtc))
            return WebhookAuthResult.Replay;

        return WebhookAuthResult.Ok;
    }

    /// <summary>Parse the timestamp header: Unix epoch seconds or an ISO-8601 datetime.</summary>
    internal static bool TryParseTimestamp(string raw, out DateTime utc)
    {
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochSeconds))
        {
            try
            {
                utc = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).UtcDateTime;
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                utc = default;
                return false;
            }
        }
        if (DateTimeOffset.TryParse(
                raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            utc = parsed.UtcDateTime;
            return true;
        }
        utc = default;
        return false;
    }

    /// <summary>Canonicalize a signature for dedupe (strip "sha256=" prefix, lower-case).</summary>
    internal static string NormalizeSignature(string signature)
    {
        var candidate = signature.Trim();
        var eq = candidate.IndexOf('=');
        if (eq >= 0 && candidate[..eq].StartsWith("sha256", StringComparison.OrdinalIgnoreCase))
            candidate = candidate[(eq + 1)..];
        return candidate.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Bounded, short-lived set of recently-seen signatures. Entries expire at
    /// their timestamp + tolerance (after which the freshness check rejects the
    /// request anyway). Capped: expired entries are pruned first, and if still at
    /// the cap the soonest-to-expire entry is evicted, so memory is bounded.
    /// </summary>
    private sealed class SeenSignatureCache
    {
        private readonly int _cap;
        private readonly Dictionary<string, DateTime> _expiry = new(StringComparer.Ordinal);
        private readonly object _lock = new();

        public SeenSignatureCache(int cap) => _cap = Math.Max(1, cap);

        /// <summary>Add a signature; false when it is already present (a replay).</summary>
        public bool TryAdd(string signature, DateTime expiresUtc, DateTime nowUtc)
        {
            lock (_lock)
            {
                Prune(nowUtc);
                if (_expiry.ContainsKey(signature))
                    return false;
                if (_expiry.Count >= _cap)
                    EvictSoonestToExpire();
                _expiry[signature] = expiresUtc;
                return true;
            }
        }

        private void Prune(DateTime nowUtc)
        {
            if (_expiry.Count == 0)
                return;
            var stale = _expiry.Where(kv => kv.Value <= nowUtc).Select(kv => kv.Key).ToList();
            foreach (var key in stale)
                _expiry.Remove(key);
        }

        private void EvictSoonestToExpire()
        {
            var soonest = _expiry.OrderBy(kv => kv.Value).First().Key;
            _expiry.Remove(soonest);
        }
    }
}
