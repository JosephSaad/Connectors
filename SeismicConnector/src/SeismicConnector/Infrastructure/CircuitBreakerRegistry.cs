// Infrastructure/CircuitBreakerRegistry.cs
// ----------------------------------------
// Process-wide registry of named circuit breakers so the observability surface
// (/metrics, /health readiness) and the crawl pipeline (degraded-mode pause)
// can read dependency health without threading breaker references everywhere.
//
// Each external client registers its breaker here on construction, keyed by a
// stable dependency name ("seismic", "graph"). Registration replaces by name,
// so the single production Seismic/Graph clients own the live breakers; tests
// reset between cases.

using System.Collections.Concurrent;

namespace SeismicConnector.Infrastructure;

public static class CircuitBreakerRegistry
{
    private static readonly ConcurrentDictionary<string, CircuitBreaker> Breakers =
        new(StringComparer.Ordinal);

    public const string SeismicName = "seismic";
    public const string GraphName = "graph";

    /// <summary>Register (or replace) the breaker for a dependency name.</summary>
    public static CircuitBreaker Register(CircuitBreaker breaker)
    {
        Breakers[breaker.Name] = breaker;
        return breaker;
    }

    public static CircuitBreaker? Get(string name) =>
        Breakers.TryGetValue(name, out var breaker) ? breaker : null;

    /// <summary>All registered breakers, ordered by name for stable rendering.</summary>
    public static IReadOnlyList<CircuitBreaker> All =>
        Breakers.Values.OrderBy(b => b.Name, StringComparer.Ordinal).ToList();

    /// <summary>True when any CRITICAL breaker is currently open (readiness / degraded-mode signal).</summary>
    public static bool AnyCriticalOpen =>
        Breakers.Values.Any(b => b.Critical && b.State == CircuitState.Open);

    /// <summary>The names of critical breakers that are currently open.</summary>
    public static IReadOnlyList<string> OpenCriticalNames =>
        Breakers.Values
            .Where(b => b.Critical && b.State == CircuitState.Open)
            .Select(b => b.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>Test seam: clear all registered breakers.</summary>
    internal static void ResetForTests() => Breakers.Clear();
}
