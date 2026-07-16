// Infrastructure/Tracing.cs
// -------------------------
// OpenTelemetry distributed tracing + correlation IDs (the platform
// "Observability & Audit Backbone").
//
// Two layers, deliberately separated so the audit trail is always on while the
// export machinery is strictly opt-in:
//
//  1. Correlation IDs + spans (always available, ~zero cost when nobody
//     listens). A single ActivitySource ("SeismicConnector") is used to open
//     spans for the meaningful stages (crawl cycle, teamsite crawl, Seismic
//     fetch, extraction, transform, Graph batch, reconcile/reacl sweeps,
//     webhook handling). ActivitySource.StartActivity returns null when no
//     listener is registered, so with the exporter off the spans compile away
//     to a cheap null check. A correlation id is minted per crawl cycle and
//     flows via AsyncLocal to every child task, log line, dead-letter record
//     and report entry — so a crawl is followable end-to-end by one id.
//
//  2. OTLP export (opt-in, gated on OTEL_EXPORTER_OTLP_ENDPOINT). Only when
//     that standard env var is set is a TracerProvider built and a listener
//     registered; the OTLP exporter uses a batched, bounded background queue,
//     so a broken/unreachable collector can never fail or stall a crawl
//     (export is fire-and-forget on a background thread; drops are silent).
//     With no endpoint, nothing is registered and overhead is unchanged.
//
// Standard OTEL_* env vars are honoured by the exporter (endpoint, protocol,
// headers, timeout). OTEL_SERVICE_NAME defaults to the connector name.

using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SeismicConnector.Config;

namespace SeismicConnector.Infrastructure;

public static class Tracing
{
    private static readonly IAppLogger Logger = Logging.GetLogger("seismic_connector.tracing");

    /// <summary>The single ActivitySource all connector spans are opened from.</summary>
    public const string ActivitySourceName = "SeismicConnector";

    public const string EndpointEnvVar = "OTEL_EXPORTER_OTLP_ENDPOINT";
    public const string ServiceNameEnvVar = "OTEL_SERVICE_NAME";

    private static readonly ActivitySource Source = new(ActivitySourceName, "1.0.0");

    private static readonly AsyncLocal<string?> CorrelationSlot = new();

    private static TracerProvider? _provider;

    static Tracing()
    {
        // W3C ids so the correlation id aligns with the trace id and the
        // traceparent header we inject on outbound HTTP is standards-compliant.
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;
    }

    /// <summary>True once an OTLP exporter has been registered (endpoint configured).</summary>
    public static bool Enabled => _provider is not null;

    /// <summary>The resolved OTLP endpoint when enabled; null otherwise.</summary>
    public static string? ExporterEndpoint { get; private set; }

    /// <summary>The OTEL service.name in effect (OTEL_SERVICE_NAME or the connector name).</summary>
    public static string ServiceName { get; private set; } = ActivitySourceName;

    /// <summary>The correlation id for the current async flow (crawl cycle), if any.</summary>
    public static string? CurrentCorrelationId => CorrelationSlot.Value;

    /// <summary>
    /// Build the OTLP TracerProvider IF <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is
    /// set; otherwise leave tracing inert (no listener, unchanged overhead).
    /// Idempotent and never throws — a misconfigured exporter must not stop the
    /// connector from running.
    /// </summary>
    public static void Initialize(AppConfig config)
    {
        if (_provider is not null)
            return;

        ServiceName = ResolveServiceName(config);

        var endpoint = Environment.GetEnvironmentVariable(EndpointEnvVar);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            // Inert: no exporter, no listener — StartActivity returns null.
            return;
        }

        try
        {
            _provider = Sdk.CreateTracerProviderBuilder()
                .AddSource(ActivitySourceName)
                .SetResourceBuilder(ResourceBuilder.CreateDefault()
                    .AddService(serviceName: ServiceName, serviceVersion: "1.0.0"))
                // AddOtlpExporter defaults to a BatchActivityExportProcessor: a
                // bounded queue drained on a background thread. Export failures
                // (unreachable collector) are swallowed by the SDK, so a crawl
                // is never blocked or failed by the exporter.
                .AddOtlpExporter(options =>
                {
                    options.Endpoint = new Uri(endpoint);
                    options.Protocol = ResolveProtocol();
                })
                .Build();
            ExporterEndpoint = endpoint;
            Logger.Info($"OpenTelemetry tracing enabled — OTLP export to {endpoint} (service '{ServiceName}').");
        }
        catch (Exception ex)
        {
            // Never let a tracing misconfiguration stop the connector.
            _provider = null;
            ExporterEndpoint = null;
            Logger.Warning($"OpenTelemetry initialization failed ({ex.Message}); tracing disabled.");
        }
    }

    private static string ResolveServiceName(AppConfig config)
    {
        var fromEnv = Environment.GetEnvironmentVariable(ServiceNameEnvVar);
        return !string.IsNullOrWhiteSpace(fromEnv) ? fromEnv.Trim() : config.Connector.Name;
    }

    private static OtlpExportProtocol ResolveProtocol()
    {
        var raw = (Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL") ?? "").Trim().ToLowerInvariant();
        return raw is "grpc" ? OtlpExportProtocol.Grpc : OtlpExportProtocol.HttpProtobuf;
    }

    /// <summary>Flush and dispose the exporter (best-effort; safe to call when disabled).</summary>
    public static void Shutdown()
    {
        try
        {
            _provider?.Dispose();
        }
        catch
        {
            // best-effort
        }
        finally
        {
            _provider = null;
            ExporterEndpoint = null;
        }
    }

    /// <summary>
    /// Start a span. Returns <c>null</c> when no listener is registered
    /// (exporter off) — callers null-check, so the disabled path is a cheap
    /// no-op. When a span opens it inherits <see cref="Activity.Current"/> as
    /// its parent, so nesting these gives correct parent/child structure.
    /// </summary>
    public static Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal) =>
        Source.StartActivity(name, kind);

    /// <summary>
    /// Begin a crawl-cycle scope: opens the root <c>crawl.cycle</c> span (when a
    /// listener is present), mints/propagates a correlation id, and stamps it on
    /// the span. The correlation id is the W3C trace id when a span exists (so it
    /// matches the exported trace), else a fresh id — either way it flows via
    /// AsyncLocal to every child task/log/record until the scope is disposed.
    /// </summary>
    public static CycleScope BeginCycle(string connectorId, string crawlKind, string? objectFilter = null)
    {
        var activity = StartActivity("crawl.cycle", ActivityKind.Internal);
        var correlationId = activity?.TraceId.ToHexString() ?? Guid.NewGuid().ToString("N");

        activity?.SetTag("connector.id", connectorId);
        activity?.SetTag("seismic.crawl_kind", crawlKind);
        activity?.SetTag("correlation_id", correlationId);
        if (objectFilter is not null)
            activity?.SetTag("seismic.object_filter", objectFilter);

        var previous = CorrelationSlot.Value;
        CorrelationSlot.Value = correlationId;
        return new CycleScope(activity, previous, correlationId);
    }

    /// <summary>
    /// Attach the current correlation id to an already-open span/scope (used by
    /// the sweeps and webhook handling, which open their own root span but share
    /// the ambient correlation id when one exists, or mint one otherwise).
    /// </summary>
    public static CycleScope BeginNamedScope(string spanName, string connectorId, string kind)
    {
        var activity = StartActivity(spanName, ActivityKind.Internal);
        var correlationId = CorrelationSlot.Value
            ?? activity?.TraceId.ToHexString()
            ?? Guid.NewGuid().ToString("N");
        activity?.SetTag("connector.id", connectorId);
        activity?.SetTag("seismic.kind", kind);
        activity?.SetTag("correlation_id", correlationId);
        var previous = CorrelationSlot.Value;
        CorrelationSlot.Value = correlationId;
        return new CycleScope(activity, previous, correlationId);
    }

    /// <summary>
    /// Inject the current W3C <c>traceparent</c> (and <c>tracestate</c>) onto an
    /// outbound HTTP request so downstream services join the same trace. No-op
    /// when no span is active.
    /// </summary>
    public static void InjectTraceContext(HttpRequestMessage request)
    {
        var activity = Activity.Current;
        if (activity is null || activity.IdFormat != ActivityIdFormat.W3C || activity.Id is null)
            return;
        if (!request.Headers.Contains("traceparent"))
            request.Headers.TryAddWithoutValidation("traceparent", activity.Id);
        var state = activity.TraceStateString;
        if (!string.IsNullOrEmpty(state) && !request.Headers.Contains("tracestate"))
            request.Headers.TryAddWithoutValidation("tracestate", state);
    }

    /// <summary>Test seam: reset the ambient correlation id (does not touch the provider).</summary>
    internal static void ResetCorrelationForTests() => CorrelationSlot.Value = null;

    /// <summary>Test seam: set the ambient correlation id directly.</summary>
    internal static void SetCorrelationForTests(string? id) => CorrelationSlot.Value = id;

    /// <summary>A crawl-cycle (or named) tracing scope; disposing ends the span and restores the correlation id.</summary>
    public sealed class CycleScope : IDisposable
    {
        private readonly Activity? _activity;
        private readonly string? _previousCorrelationId;
        private bool _disposed;

        public string CorrelationId { get; }

        internal CycleScope(Activity? activity, string? previousCorrelationId, string correlationId)
        {
            _activity = activity;
            _previousCorrelationId = previousCorrelationId;
            CorrelationId = correlationId;
        }

        /// <summary>Set a tag on the scope's span (no-op when the span is inert).</summary>
        public void SetTag(string key, object? value) => _activity?.SetTag(key, value);

        /// <summary>Mark the span failed with an error status.</summary>
        public void SetError(string description) => _activity?.SetStatus(ActivityStatusCode.Error, description);

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            CorrelationSlot.Value = _previousCorrelationId;
            _activity?.Dispose();
        }
    }
}
