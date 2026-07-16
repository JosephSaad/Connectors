// Graph/RetryDelay.cs
// -------------------
// Retry-backoff computation for Graph API calls (429-hardened).
//
// Server-provided Retry-After values are honoured, but every wait — server or
// computed — is hard-capped at MaxRetryWaitSeconds (60 s, the $batch retry
// ladder cap): a poisoned or absurd Retry-After must not park the crawl for
// minutes. Computed exponential-backoff delays (base * 2^attempt, capped at
// 60 s) optionally get a uniform ±20% jitter when GRAPH_RETRY_JITTER=true —
// recommended in HA so nodes throttled together don't retry in lockstep.
// Jitter never applies to a server Retry-After. See docs/RETRY.md.

namespace ClarizenConnector.Graph;

internal static class RetryDelay
{
    /// <summary>Jitter amplitude: ±20% of the computed delay.</summary>
    internal const double JitterFraction = 0.20;

    /// <summary>Hard cap on ANY retry wait — computed backoff or server Retry-After.</summary>
    internal const double MaxRetryWaitSeconds = 60.0;

    private static readonly Random Rng = new();

    /// <summary>True when GRAPH_RETRY_JITTER=true (case-insensitive). Read per call so tests can flip it.</summary>
    internal static bool JitterEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("GRAPH_RETRY_JITTER"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Exponential backoff: base * 2^attempt, capped at 60 seconds. Pure.</summary>
    internal static double ComputeBackoff(double backoffBase, int attempt) =>
        Math.Min(MaxRetryWaitSeconds, backoffBase * Math.Pow(2, attempt));

    /// <summary>
    /// Pure jitter seam: map a computed delay and a uniform sample in [0,1) to
    /// the jittered delay in [delay·0.8, delay·1.2). Identity when jitter is off.
    /// </summary>
    internal static double ApplyJitter(double delaySeconds, bool jitterEnabled, double uniformSample)
    {
        if (!jitterEnabled)
            return delaySeconds;
        return delaySeconds * (1.0 - JitterFraction + 2.0 * JitterFraction * uniformSample);
    }

    /// <summary>
    /// Jitter a COMPUTED backoff delay when GRAPH_RETRY_JITTER=true; returns
    /// the input unchanged otherwise. Never call with a server Retry-After.
    /// </summary>
    internal static double Jitter(double delaySeconds)
    {
        if (!JitterEnabled)
            return delaySeconds;
        double sample;
        lock (Rng)
            sample = Rng.NextDouble();
        return ApplyJitter(delaySeconds, jitterEnabled: true, uniformSample: sample);
    }

    /// <summary>
    /// Delay for retry <paramref name="attempt"/>: the server's Retry-After
    /// when present (never jittered), otherwise jittered exponential backoff.
    /// Either way the wait is clamped to <see cref="MaxRetryWaitSeconds"/>;
    /// <paramref name="clamped"/> reports whether the clamp fired so the
    /// caller can log the hardening decision.
    /// </summary>
    internal static double ForAttempt(
        double backoffBase, int attempt, double? retryAfterSeconds, out bool clamped)
    {
        double wait;
        if (retryAfterSeconds is { } ra && ra >= 0)
            wait = ra;                                   // server value — no jitter
        else
            wait = Jitter(ComputeBackoff(backoffBase, attempt));

        clamped = wait > MaxRetryWaitSeconds;
        return clamped ? MaxRetryWaitSeconds : wait;
    }

    /// <summary>Convenience overload without the clamp report.</summary>
    internal static double ForAttempt(double backoffBase, int attempt, double? retryAfterSeconds) =>
        ForAttempt(backoffBase, attempt, retryAfterSeconds, out _);
}
