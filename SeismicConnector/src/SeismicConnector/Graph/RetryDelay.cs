// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Graph/RetryDelay.cs
// -------------------
// Optional retry-backoff jitter, enabled with GRAPH_RETRY_JITTER=true
// (default: false — computed delays are then bit-identical to the original
// behaviour). When enabled, COMPUTED exponential-backoff delays get a uniform
// ±20% jitter; server-provided Retry-After values are always honoured exactly
// and must never pass through this helper.
//
// Why: in HA (multi-node) deployments all nodes share one per-connection Graph
// 429 quota. Without jitter, nodes that were throttled together retry in
// lockstep and collide again. See docs/RETRY.md and docs/HA.md.

namespace SeismicConnector.Graph;

internal static class RetryDelay
{
    /// <summary>Jitter amplitude: ±20% of the computed delay.</summary>
    internal const double JitterFraction = 0.20;

    private static readonly Random Rng = new();

    /// <summary>True when GRAPH_RETRY_JITTER=true (case-insensitive). Read per call so tests can flip it.</summary>
    internal static bool JitterEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("GRAPH_RETRY_JITTER"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Pure delay computation (unit-testable seam): map a computed backoff delay
    /// and a uniform sample in [0, 1) to the jittered delay in
    /// [delay·0.8, delay·1.2). Identity when <paramref name="jitterEnabled"/> is false.
    /// </summary>
    internal static double ApplyJitter(double delaySeconds, bool jitterEnabled, double uniformSample)
    {
        if (!jitterEnabled)
            return delaySeconds;
        return delaySeconds * (1.0 - JitterFraction + 2.0 * JitterFraction * uniformSample);
    }

    /// <summary>
    /// Jitter a COMPUTED backoff delay when GRAPH_RETRY_JITTER=true; returns the
    /// input unchanged otherwise. Never call with a server-provided Retry-After.
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
}
