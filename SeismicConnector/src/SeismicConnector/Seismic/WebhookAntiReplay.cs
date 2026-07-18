// Seismic/WebhookAntiReplay.cs
// ----------------------------
// Anti-replay hardening for the webhook receiver, layered ON TOP of the HMAC
// signature check without weakening validate-before-parse. A captured, validly-
// signed request must not be replayable:
//
//   1. FRESHNESS — the sender sends a signed timestamp header (default
//      X-Seismic-Timestamp) and computes the HMAC over `timestamp + "." + body`
//      (the timestamp is BOUND INTO the signature, so it cannot be swapped). A
//      request whose timestamp is outside the freshness window (default 5 min)
//      is rejected fail-closed.
//   2. DEDUP — a bounded, short-lived cache of recently-seen signatures rejects
//      a duplicate of an already-accepted request WITHIN the window. Entries
//      self-expire after the window; the cache is size-capped so a flood cannot
//      grow it without bound.
//
// Migration flag (SEISMIC_WEBHOOK_REQUIRE_TIMESTAMP, DEFAULT TRUE): with it on,
// a request WITHOUT a valid fresh timestamp is rejected. Set it false while
// migrating senders that cannot yet send a timestamp — those fall back to the
// legacy body-only HMAC (no replay protection, documented). Anti-replay is REAL
// whenever a timestamp is present, regardless of the flag.
//
// This class owns only the freshness/dedup policy and the clock; the HMAC itself
// stays in SignatureValidator. It is unit-testable without binding a listener
// (inject the clock).

using SeismicConnector.Infrastructure;

namespace SeismicConnector.Seismic;

/// <summary>The outcome of the anti-replay + signature gate for one request.</summary>
public enum WebhookVerdict
{
    Accepted,
    InvalidSignature,
    MissingTimestamp,
    StaleTimestamp,
    ReplayDetected,
}

public sealed class WebhookAntiReplay
{
    public const string TimestampHeaderEnvVar = "SEISMIC_WEBHOOK_TIMESTAMP_HEADER";
    public const string DefaultTimestampHeader = "X-Seismic-Timestamp";

    public const string WindowEnvVar = "SEISMIC_WEBHOOK_REPLAY_WINDOW_SECONDS";
    public const int DefaultWindowSeconds = 300;

    public const string RequireTimestampEnvVar = "SEISMIC_WEBHOOK_REQUIRE_TIMESTAMP";

    /// <summary>Hard cap on the dedup cache (bounds memory under a signed flood).</summary>
    internal const int MaxCacheEntries = 200_000;

    private readonly SignatureValidator _validator;
    private readonly TimeSpan _window;
    private readonly bool _requireTimestamp;
    private readonly Func<DateTimeOffset> _clock;

    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;

    public string TimestampHeader { get; }

    public bool RequireTimestamp => _requireTimestamp;

    public TimeSpan Window => _window;

    public WebhookAntiReplay(
        SignatureValidator validator,
        TimeSpan window,
        bool requireTimestamp,
        string? timestampHeader = null,
        Func<DateTimeOffset>? clock = null)
    {
        _validator = validator;
        _window = window > TimeSpan.Zero ? window : TimeSpan.FromSeconds(DefaultWindowSeconds);
        _requireTimestamp = requireTimestamp;
        TimestampHeader = string.IsNullOrWhiteSpace(timestampHeader) ? DefaultTimestampHeader : timestampHeader!;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Build from the environment (production defaults: require timestamp ON, 5-min window).</summary>
    public static WebhookAntiReplay FromEnv(SignatureValidator validator, Func<DateTimeOffset>? clock = null)
    {
        var windowSeconds = Settings.IntEnv(WindowEnvVar, DefaultWindowSeconds);
        var window = TimeSpan.FromSeconds(windowSeconds > 0 ? windowSeconds : DefaultWindowSeconds);
        var require = Settings.BoolEnv(RequireTimestampEnvVar, fallback: true);
        var header = Settings.EnvOr(TimestampHeaderEnvVar, DefaultTimestampHeader);
        return new WebhookAntiReplay(validator, window, require, header, clock);
    }

    /// <summary>
    /// Validate one request's signature and anti-replay posture over the EXACT
    /// raw body (call before any parse). <paramref name="timestampHeader"/> is
    /// the raw value of the configured timestamp header (may be null/absent).
    /// </summary>
    public WebhookVerdict Check(byte[] rawBody, string? timestampHeader, string? signature)
    {
        // No timestamp: either fail-closed (require on) or fall back to the
        // legacy body-only HMAC (migration — no replay protection possible).
        if (string.IsNullOrWhiteSpace(timestampHeader))
        {
            if (_requireTimestamp)
                return WebhookVerdict.MissingTimestamp;
            return _validator.IsValid(rawBody, signature)
                ? WebhookVerdict.Accepted
                : WebhookVerdict.InvalidSignature;
        }

        var ts = timestampHeader.Trim();

        // Verify the signature FIRST (over timestamp + "." + body). This
        // authenticates the timestamp itself before we make any freshness
        // decision on it, and gates cache insertion so bogus signatures can
        // never poison the dedup cache.
        if (!_validator.IsValid(ts, rawBody, signature))
            return WebhookVerdict.InvalidSignature;

        // Authenticated but unparseable timestamp → cannot establish freshness → fail-closed.
        if (!TryParseTimestamp(ts, out var stamp))
            return WebhookVerdict.StaleTimestamp;

        var now = _clock();
        if ((now - stamp).Duration() > _window)
            return WebhookVerdict.StaleTimestamp;

        // Replay dedup, keyed by the (authenticated) signature value.
        var key = signature!.Trim();
        lock (_lock)
        {
            Evict(now);
            if (_seen.ContainsKey(key))
                return WebhookVerdict.ReplayDetected;
            if (_seen.Count < MaxCacheEntries)
                _seen[key] = now + _window;
        }
        return WebhookVerdict.Accepted;
    }

    /// <summary>Drop expired dedup entries. Rate-limited to at most once per second under load.</summary>
    private void Evict(DateTimeOffset now)
    {
        if (now < _nextSweep && _seen.Count < MaxCacheEntries)
            return;
        _nextSweep = now.AddSeconds(1);
        if (_seen.Count == 0)
            return;
        var expired = _seen.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList();
        foreach (var key in expired)
            _seen.Remove(key);
    }

    /// <summary>Parse a timestamp as Unix seconds (integer) OR ISO-8601.</summary>
    internal static bool TryParseTimestamp(string value, out DateTimeOffset stamp)
    {
        if (long.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var unix))
        {
            try
            {
                stamp = DateTimeOffset.FromUnixTimeSeconds(unix);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                stamp = default;
                return false;
            }
        }

        if (DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out stamp))
        {
            return true;
        }

        stamp = default;
        return false;
    }
}
