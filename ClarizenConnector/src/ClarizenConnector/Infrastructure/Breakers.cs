// Infrastructure/Breakers.cs
// --------------------------
// Process-wide circuit-breaker registry: one breaker per external dependency
// (the Clarizen source client and the Microsoft Graph client). Both are
// treated as CRITICAL — a sustained outage of either degrades the crawl.
//
// Default state is DISABLED passthrough breakers, so until Initialize() is
// called (production: Runtime.Create) the registry is inert — IsAnyCriticalOpen
// is false, State is Closed, and the metrics read zero. Tests that don't touch
// breakers therefore see byte-identical behaviour; breaker/degraded tests call
// Initialize() with their own options + clock and ResetForTests() afterwards.

using System.Globalization;
using System.Text;

namespace ClarizenConnector.Infrastructure;

public static class Breakers
{
    /// <summary>Breaker guarding the Clarizen REST/TDW source client.</summary>
    public static CircuitBreaker Clarizen { get; private set; } = CircuitBreaker.Disabled;

    /// <summary>Breaker guarding the Microsoft Graph client (token + ingest/delete).</summary>
    public static CircuitBreaker Graph { get; private set; } = CircuitBreaker.Disabled;

    /// <summary>Install configured breakers for the dependencies. Idempotent per call.</summary>
    public static void Initialize(CircuitBreakerOptions options, Func<DateTime>? now = null)
    {
        Clarizen = new CircuitBreaker("clarizen", options, now);
        Graph = new CircuitBreaker("graph", options, now);
    }

    /// <summary>True when any critical dependency's breaker is Open (degraded mode).</summary>
    public static bool IsAnyCriticalOpen => Clarizen.IsOpen || Graph.IsOpen;

    /// <summary>The critical dependencies whose breakers are currently Open (for logs).</summary>
    public static IReadOnlyList<string> OpenCritical()
    {
        var open = new List<string>(2);
        if (Clarizen.IsOpen)
            open.Add(Clarizen.Name);
        if (Graph.IsOpen)
            open.Add(Graph.Name);
        return open;
    }

    /// <summary>Restore the inert disabled defaults (test isolation).</summary>
    internal static void ResetForTests()
    {
        Clarizen = CircuitBreaker.Disabled;
        Graph = CircuitBreaker.Disabled;
    }

    // ── /metrics rendering ───────────────────────────────────────────────────

    /// <summary>Append per-dependency breaker metrics to <paramref name="sb"/>:
    /// a state gauge (0 closed / 1 half-open / 2 open) and trip/reset counters.</summary>
    public static void AppendMetrics(StringBuilder sb)
    {
        AppendBreaker(sb, Clarizen);
        AppendBreaker(sb, Graph);
    }

    private static void AppendBreaker(StringBuilder sb, CircuitBreaker breaker)
    {
        var label = $"{{dependency=\"{breaker.Name}\"}}";
        Line(sb, "circuit_breaker_state", "gauge",
            "Circuit-breaker state per dependency: 0 closed, 1 half-open, 2 open.",
            label, ((int)breaker.State).ToString(CultureInfo.InvariantCulture));
        Line(sb, "circuit_breaker_trips_total", "counter",
            "Total times a dependency's breaker tripped to open.",
            label, breaker.Trips.ToString(CultureInfo.InvariantCulture));
        Line(sb, "circuit_breaker_resets_total", "counter",
            "Total times a dependency's breaker recovered to closed.",
            label, breaker.Resets.ToString(CultureInfo.InvariantCulture));
    }

    private const string Prefix = "clarizen_connector_";

    private static void Line(
        StringBuilder sb, string name, string type, string help, string label, string value)
    {
        var full = Prefix + name;
        // HELP/TYPE are per metric name; emit once by keying on the first
        // dependency (clarizen) so the exposition stays valid (no duplicates).
        if (label.Contains("\"clarizen\""))
        {
            sb.Append("# HELP ").Append(full).Append(' ').Append(help).Append('\n');
            sb.Append("# TYPE ").Append(full).Append(' ').Append(type).Append('\n');
        }
        sb.Append(full).Append(label).Append(' ').Append(value).Append('\n');
    }
}
