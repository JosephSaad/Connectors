// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// MetricsRenderer.cs
// ------------------
// Generic, series-free Prometheus text-exposition building blocks shared by
// every connector's metrics facade. This type holds NO connector-specific
// series and references NO connector type: each connector keeps its own
// registry of counters/gauges (a small `Metrics` facade) and calls these
// helpers to render them.
//
// The metric-name prefix is parameterised off the chassis identity
// (`Chassis.Identity.ConnectorId`), so a connector emits its own
// "<connector_id>_*" series without the chassis knowing which connector it is.
//
// No external dependencies — the simplest thing that produces valid Prometheus
// exposition (v0.0.4) so scraping works without a client library.

using System.Globalization;
using System.Text;

namespace Connector.Chassis;

/// <summary>
/// Reusable Prometheus text-exposition helpers (counters, gauges, labelled
/// counters, label escaping, and a registry-based circuit-breaker renderer),
/// prefix-parameterised by <see cref="Chassis.Identity"/>. Connector metrics
/// facades own the series and drive these helpers; the chassis stays free of
/// any connector-specific state.
/// </summary>
public static class MetricsRenderer
{
    /// <summary>
    /// Metric-name prefix, parameterised off the chassis identity so each
    /// connector emits its own "<c>&lt;connector_id&gt;_*</c>" series (e.g.
    /// "<c>seismic_connector_</c>").
    /// </summary>
    public static string Prefix => $"{Chassis.Identity.ConnectorId}_";

    /// <summary>Render a monotonic counter (HELP/TYPE + one sample line).</summary>
    public static void Counter(StringBuilder sb, string name, string help, long value) =>
        Metric(sb, name, help, "counter", value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Render an integer gauge (HELP/TYPE + one sample line).</summary>
    public static void Gauge(StringBuilder sb, string name, string help, long value) =>
        Metric(sb, name, help, "gauge", value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Render a floating-point gauge (HELP/TYPE + one sample line).</summary>
    public static void GaugeDouble(StringBuilder sb, string name, string help, double value) =>
        Metric(sb, name, help, "gauge", value.ToString("0.###", CultureInfo.InvariantCulture));

    /// <summary>
    /// Render a single metric family: a <c># HELP</c> line, a <c># TYPE</c>
    /// line, then the sample line. '\n' line endings; help strings contain
    /// neither backslash nor newline so no HELP-text escaping is needed.
    /// </summary>
    public static void Metric(StringBuilder sb, string name, string help, string type, string value)
    {
        var full = Prefix + name;
        sb.Append("# HELP ").Append(full).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(full).Append(' ').Append(type).Append('\n');
        sb.Append(full).Append(' ').Append(value).Append('\n');
    }

    /// <summary>
    /// Render a labelled counter family: always emits the <c># HELP</c>/<c># TYPE</c>
    /// header (even when <paramref name="values"/> is empty), then one sample
    /// line per label value ordered by key (ordinal). <paramref name="escape"/>
    /// selects whether label values are Prometheus-escaped — connectors that
    /// escape (Seismic) pass <c>true</c>; connectors that render label values
    /// verbatim (Clarizen/Hadoop) pass <c>false</c> (the default) and guard the
    /// call themselves when they want empty families omitted.
    /// </summary>
    public static void LabeledCounter(
        StringBuilder sb, string name, string help, string labelKey,
        IReadOnlyDictionary<string, long> values, bool escape = false)
    {
        var full = Prefix + name;
        sb.Append("# HELP ").Append(full).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(full).Append(" counter\n");
        foreach (var kv in values.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var label = escape ? EscapeLabel(kv.Key) : kv.Key;
            sb.Append(full).Append('{').Append(labelKey).Append("=\"").Append(label).Append("\"} ")
              .Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
    }

    /// <summary>Escape a Prometheus label value (backslash, quote, newline).</summary>
    public static string EscapeLabel(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    /// <summary>
    /// Render the per-dependency circuit-breaker state gauge + trip/reset
    /// counters over the chassis <see cref="CircuitBreakerRegistry"/>, for
    /// connectors whose breakers register there (Seismic). Label values are
    /// escaped.
    /// </summary>
    public static void RenderCircuitBreakers(StringBuilder sb)
    {
        var breakers = CircuitBreakerRegistry.All;

        sb.Append("# HELP ").Append(Prefix)
            .Append("circuit_breaker_state Circuit-breaker state per dependency (0=closed, 1=open, 2=half-open).\n");
        sb.Append("# TYPE ").Append(Prefix).Append("circuit_breaker_state gauge\n");
        foreach (var breaker in breakers)
        {
            sb.Append(Prefix).Append("circuit_breaker_state{dependency=\"")
                .Append(EscapeLabel(breaker.Name)).Append("\"} ")
                .Append((int)breaker.State).Append('\n');
        }

        sb.Append("# HELP ").Append(Prefix)
            .Append("circuit_breaker_trips_total Times a dependency breaker opened (closed→open).\n");
        sb.Append("# TYPE ").Append(Prefix).Append("circuit_breaker_trips_total counter\n");
        foreach (var breaker in breakers)
        {
            sb.Append(Prefix).Append("circuit_breaker_trips_total{dependency=\"")
                .Append(EscapeLabel(breaker.Name)).Append("\"} ")
                .Append(breaker.TripCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        sb.Append("# HELP ").Append(Prefix)
            .Append("circuit_breaker_resets_total Times a dependency breaker recovered (half-open→closed).\n");
        sb.Append("# TYPE ").Append(Prefix).Append("circuit_breaker_resets_total counter\n");
        foreach (var breaker in breakers)
        {
            sb.Append(Prefix).Append("circuit_breaker_resets_total{dependency=\"")
                .Append(EscapeLabel(breaker.Name)).Append("\"} ")
                .Append(breaker.ResetCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
    }
}
