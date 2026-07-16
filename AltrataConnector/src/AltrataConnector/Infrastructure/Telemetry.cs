// Infrastructure/Telemetry.cs
// ---------------------------
// OpenTelemetry distributed tracing + correlation IDs (Observability & Audit
// Backbone). Off by default: with no OTEL_EXPORTER_OTLP_ENDPOINT set, no
// TracerProvider is registered, ActivitySource.StartActivity returns null, and
// every Span(...) call is a near-free no-op — default behaviour and overhead
// are unchanged.
//
// PII CAUTION (enforced): spans/tags must NEVER carry personal data. This is a
// licensed-PII connector; trace exhaust is a leakage surface. Tags go through
// SetTag, which only accepts keys on the AllowedTagKeys allowlist — all of
// which are opaque ids, counts, hashes or enums. Names, emails, wealth figures
// and profile content can never reach a span. See docs/OBSERVABILITY.md and the
// no-PII-in-traces tests.
//
// Correlation id: a stable 32-hex id per crawl / erasure cycle (the root span's
// trace id when tracing is on, else a generated id). Stamped on structured JSON
// logs, dead-letter records, reconciliation reports, erasure-ledger entries and
// span tags — so one cycle is followable end-to-end by correlation id even when
// OTLP export is off.

using System.Diagnostics;
using System.Reflection;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AltrataConnector.Infrastructure;

public static class Telemetry
{
    public const string ActivitySourceName = "AltrataConnector";

    private static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

    /// <summary>The single ActivitySource all spans come from.</summary>
    public static readonly ActivitySource Source = new(ActivitySourceName, Version);

    // ---- env (standard OTEL_* vars) ----------------------------------------

    /// <summary>OTLP endpoint; when null/empty tracing is disabled (inert).</summary>
    public static string? OtlpEndpoint
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
            return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        }
    }

    public static bool TracingEnabled => OtlpEndpoint != null;

    /// <summary>OTEL_SERVICE_NAME, defaulting to the connector display name.</summary>
    public static string ServiceName(string connectorName)
    {
        var raw = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME");
        return string.IsNullOrWhiteSpace(raw) ? connectorName : raw.Trim();
    }

    // ---- PII-safe tag allowlist --------------------------------------------
    // Every value tagged under these keys is an opaque id, a count, a hash or a
    // fixed enum — never a name, email, wealth figure or profile field.

    public static readonly IReadOnlySet<string> AllowedTagKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "altrata.correlation_id",
        "altrata.crawl.kind",
        "altrata.crawl.deliveries",
        "altrata.delivery.id",
        "altrata.delivery.files",
        "altrata.delivery.status",
        "altrata.manifest.files",
        "altrata.dataset",
        "altrata.records.count",
        "altrata.records.ingested",
        "altrata.records.deleted",
        "altrata.records.suppressed",
        "altrata.records.deadlettered",
        "altrata.subject.id",           // Altrata person id — opaque, not a name/email
        "altrata.subject.count",
        "altrata.subject.resolved_by",  // "id" | "email" (mode only, never the email)
        "altrata.match.rule",           // email | name+employer | fuzzy
        "altrata.match.confidence",     // number
        "altrata.crm.contacts",
        "altrata.seat.count",
        "altrata.seat.hash",
        "altrata.seat.changed",
        "altrata.reacl.updated",
        "altrata.reacl.failed",
        "altrata.path.edges",
        "altrata.path.memberships",
        "altrata.graph.op",             // put | patch-acl | delete
        "altrata.graph.batch.size",
        "altrata.graph.batch.ok",
        "altrata.graph.batch.failed",
        "altrata.api.billable_total",
        "altrata.items.count",
        "altrata.items.withdrawn",
        "altrata.items.failed",
    };

    /// <summary>
    /// The ONLY way spans get tags in this codebase. A key outside the
    /// allowlist is dropped (and warned once) so a future edit can never leak
    /// PII onto a span by using the wrong key.
    /// </summary>
    public static void SetTag(Activity? activity, string key, object? value)
    {
        if (activity == null || value == null)
            return;
        if (!AllowedTagKeys.Contains(key))
        {
            WarnRejectedKey(key);
            return;
        }
        activity.SetTag(key, value);
    }

    private static readonly HashSet<string> WarnedKeys = new(StringComparer.Ordinal);

    private static void WarnRejectedKey(string key)
    {
        lock (WarnedKeys)
        {
            if (!WarnedKeys.Add(key))
                return;
        }
        Logging.GetLogger("altrata_connector.telemetry").Warning(
            $"Span tag key '{key}' is not on the PII-safe allowlist and was dropped.");
    }

    // ---- span helpers ------------------------------------------------------

    /// <summary>
    /// Start a span (child of Activity.Current) and stamp the current
    /// correlation id. Returns null when no listener/exporter is registered —
    /// so callers pay nothing when tracing is off. Always stamps the
    /// correlation id tag when a span is produced.
    /// </summary>
    public static Activity? Span(string name, ActivityKind kind = ActivityKind.Internal)
    {
        var activity = Source.StartActivity(name, kind);
        if (activity != null)
        {
            var correlationId = CorrelationContext.Current;
            if (correlationId != null)
                activity.SetTag("altrata.correlation_id", correlationId);
        }
        return activity;
    }

    /// <summary>Inject the current W3C trace context onto an outbound request
    /// so spans continue across the wire (no-op when no Activity is current).</summary>
    public static void InjectTraceContext(HttpRequestMessage request)
    {
        var activity = Activity.Current;
        if (activity == null || activity.Id == null)
            return;
        if (!request.Headers.Contains("traceparent"))
            request.Headers.TryAddWithoutValidation("traceparent", activity.Id);
        var state = activity.TraceStateString;
        if (!string.IsNullOrEmpty(state) && !request.Headers.Contains("tracestate"))
            request.Headers.TryAddWithoutValidation("tracestate", state);
    }

    // ---- provider registration ---------------------------------------------

    /// <summary>
    /// Register the OTLP TracerProvider when OTEL_EXPORTER_OTLP_ENDPOINT is set;
    /// otherwise return null (nothing registered, fully inert). The OTLP
    /// exporter uses a batched, bounded background queue — an unreachable
    /// collector drops spans silently and NEVER blocks or fails a crawl. The
    /// standard OTEL_EXPORTER_OTLP_* env vars configure endpoint/protocol/headers.
    /// </summary>
    public static IDisposable? StartIfConfigured(string connectorName)
    {
        var endpoint = OtlpEndpoint;
        if (endpoint == null)
            return null;

        try
        {
            var provider = Sdk.CreateTracerProviderBuilder()
                .AddSource(ActivitySourceName)
                .SetResourceBuilder(ResourceBuilder.CreateDefault()
                    .AddService(serviceName: ServiceName(connectorName), serviceVersion: Version))
                .AddOtlpExporter()  // reads OTEL_EXPORTER_OTLP_* ; batched by default
                .Build();
            Logging.GetLogger("altrata_connector.telemetry").Info(
                $"OpenTelemetry tracing enabled → {endpoint} (service '{ServiceName(connectorName)}')");
            Metrics.Set("altrata_tracing_enabled", 1);
            return provider;
        }
        catch (Exception exc)
        {
            // Tracing must never take the process down.
            Logging.GetLogger("altrata_connector.telemetry").Warning(
                $"OpenTelemetry tracing failed to start ({exc.Message}); continuing without traces.");
            return null;
        }
    }

    /// <summary>One-line status for validate-config / /metrics info.</summary>
    public static string StatusLine(string connectorName) =>
        TracingEnabled
            ? $"tracing=on endpoint={OtlpEndpoint} service={ServiceName(connectorName)}"
            : "tracing=off (set OTEL_EXPORTER_OTLP_ENDPOINT to enable)";
}

/// <summary>
/// Ambient correlation id for the current crawl / erasure cycle. Flows across
/// async awaits (AsyncLocal); read cheaply by logging, dead-letter, reports and
/// spans. Present whenever a cycle is active, regardless of OTLP export.
/// </summary>
public static class CorrelationContext
{
    private static readonly AsyncLocal<string?> Value = new();

    public static string? Current => Value.Value;

    /// <summary>Begin a correlation scope; disposes back to the prior id.</summary>
    public static IDisposable Begin(string? correlationId = null)
    {
        var previous = Value.Value;
        Value.Value = correlationId ?? NewId();
        return new Scope(previous);
    }

    /// <summary>A fresh 32-hex correlation id (trace-id shaped).</summary>
    public static string NewId() => ActivityTraceId.CreateRandom().ToHexString();

    private sealed class Scope : IDisposable
    {
        private readonly string? _previous;
        public Scope(string? previous) => _previous = previous;
        public void Dispose() => Value.Value = _previous;
    }
}
