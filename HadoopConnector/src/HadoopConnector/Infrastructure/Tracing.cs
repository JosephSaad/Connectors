// Infrastructure/Tracing.cs
// -------------------------
// OpenTelemetry distributed tracing for the connector.
//
// The connector owns one ActivitySource ("HadoopConnector"). Pipeline stages
// (crawl cycle, per-object crawl, source fetch, transform, Graph batch ingest,
// deletion sweep) open Activities on it, parented by the
// ambient Activity.Current so the tree reflects the call graph.
//
// Export is OPT-IN and gated on OTEL_EXPORTER_OTLP_ENDPOINT:
//   • unset → Initialize() registers NOTHING. With no listener on the source,
//     ActivitySource.StartActivity(...) returns null in O(1) (an internal
//     HasListeners() check), so the instrumentation is a cheap no-op and the
//     default behaviour/overhead is unchanged.
//   • set   → a TracerProvider with a BATCH processor + OTLP exporter is built.
//     The standard OTEL env vars are honoured (OTEL_EXPORTER_OTLP_ENDPOINT /
//     HEADERS / PROTOCOL by the exporter; OTEL_SERVICE_NAME for the resource,
//     defaulting to the connector name). The batch processor drains on a
//     background thread with a bounded queue and drops on overflow, so a slow
//     or unreachable collector can never fail or stall a crawl.
//
// StartActivity helpers null-check internally, so callers write
// `using var span = Tracing.StartObjectCrawl(...);` unconditionally.

using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace HadoopConnector.Infrastructure;

public static class Tracing
{
    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector.tracing");

    /// <summary>The single ActivitySource all connector spans are emitted on.</summary>
    public const string SourceName = "HadoopConnector";

    public const string EndpointEnvVar = "OTEL_EXPORTER_OTLP_ENDPOINT";
    public const string ServiceNameEnvVar = "OTEL_SERVICE_NAME";
    public const string ProtocolEnvVar = "OTEL_EXPORTER_OTLP_PROTOCOL";
    public const string HeadersEnvVar = "OTEL_EXPORTER_OTLP_HEADERS";

    public static readonly ActivitySource Source = new(SourceName, "1.0.0");

    private static TracerProvider? _provider;

    /// <summary>True when an OTLP exporter is registered (endpoint configured).</summary>
    public static bool Enabled { get; private set; }

    /// <summary>The configured OTLP endpoint when tracing is on, else null.</summary>
    public static string? Endpoint { get; private set; }

    /// <summary>The resolved service name (OTEL_SERVICE_NAME or the connector name).</summary>
    public static string? ServiceName { get; private set; }

    /// <summary>
    /// Initialise tracing. Returns an <see cref="IDisposable"/> that flushes and
    /// disposes the provider when tracing is on, or null when it is off (nothing
    /// registered). Never throws — a bad exporter config falls back to off.
    /// </summary>
    public static IDisposable? Initialize(string connectorName)
    {
        var endpoint = Environment.GetEnvironmentVariable(EndpointEnvVar);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            Enabled = false;
            Endpoint = null;
            return null;  // no listener registered → StartActivity is a no-op
        }

        try
        {
            var serviceName = ResolveServiceName(connectorName);
            _provider = Sdk.CreateTracerProviderBuilder()
                .AddSource(SourceName)
                .SetResourceBuilder(ResourceBuilder.CreateDefault()
                    .AddService(serviceName, serviceVersion: "1.0.0"))
                // AddOtlpExporter reads OTEL_EXPORTER_OTLP_ENDPOINT/HEADERS/
                // PROTOCOL from the environment; the batch processor is the
                // default (bounded queue, background drain, drop-on-overflow).
                .AddOtlpExporter()
                .Build();

            Enabled = true;
            Endpoint = endpoint;
            ServiceName = serviceName;
            Logger.Info($"OpenTelemetry tracing enabled: exporting to '{endpoint}' as service '{serviceName}'.");
            return new ProviderHandle(_provider);
        }
        catch (Exception exc)
        {
            // Never let a broken exporter config take the connector down.
            Enabled = false;
            Endpoint = null;
            _provider = null;
            Logger.Error($"OpenTelemetry tracing failed to initialise; continuing without export: {exc.Message}");
            return null;
        }
    }

    /// <summary>OTEL_SERVICE_NAME when set, else the connector name.</summary>
    public static string ResolveServiceName(string connectorName)
    {
        var configured = Environment.GetEnvironmentVariable(ServiceNameEnvVar);
        return string.IsNullOrWhiteSpace(configured) ? connectorName : configured;
    }

    // ── Span helpers ─────────────────────────────────────────────────────────
    // Each returns null when no listener is active (tracing off) — callers use
    // `using var span = ...;` and the null is a harmless no-op.

    /// <summary>Root span for one crawl cycle. The caller opens a correlation
    /// scope right after (adopting this span's TraceId) and stamps it back.</summary>
    public static Activity? StartCrawlCycle(string kind, string connectorId)
    {
        var span = Source.StartActivity("crawl.cycle", ActivityKind.Internal);
        span?.SetTag("bdh.crawl.kind", kind);
        span?.SetTag("bdh.connector.id", connectorId);
        return span;
    }

    /// <summary>Child span for one object type's crawl.</summary>
    public static Activity? StartObjectCrawl(string objectType, string connectorId)
    {
        var span = Source.StartActivity("crawl.object", ActivityKind.Internal);
        span?.SetTag("bdh.object_type", objectType);
        span?.SetTag("bdh.connector.id", connectorId);
        Stamp(span);
        return span;
    }

    /// <summary>Span for a BDH source fetch (WebHDFS or localpath).</summary>
    public static Activity? StartSourceFetch(string objectType, string source)
    {
        var span = Source.StartActivity("source.fetch", ActivityKind.Client);
        span?.SetTag("bdh.object_type", objectType);
        span?.SetTag("bdh.source", source);  // "rest" | "tdw"
        Stamp(span);
        return span;
    }

    /// <summary>Span for converting a chunk of records to externalItems.</summary>
    public static Activity? StartTransform(string objectType, int recordCount)
    {
        var span = Source.StartActivity("transform.chunk", ActivityKind.Internal);
        span?.SetTag("bdh.object_type", objectType);
        span?.SetTag("bdh.record_count", recordCount);
        Stamp(span);
        return span;
    }

    /// <summary>Span for a Graph $batch ingest (upsert).</summary>
    public static Activity? StartGraphIngest(string objectType, int itemCount)
    {
        var span = Source.StartActivity("graph.ingest", ActivityKind.Client);
        span?.SetTag("bdh.object_type", objectType);
        span?.SetTag("bdh.item_count", itemCount);
        Stamp(span);
        return span;
    }

    /// <summary>Span for the deletion/tombstone sweep of one object type.</summary>
    public static Activity? StartDeletionSweep(string objectType)
    {
        var span = Source.StartActivity("deletion.sweep", ActivityKind.Internal);
        span?.SetTag("bdh.object_type", objectType);
        Stamp(span);
        return span;
    }

    /// <summary>Tag the ambient correlation id onto a span (no-op when null).</summary>
    private static void Stamp(Activity? span)
    {
        if (span is not null && CorrelationContext.Current is { } id)
            span.SetTag("bdh.correlation_id", id);
    }

    /// <summary>Test seam: reset static state (does not dispose an external provider).</summary>
    internal static void ResetForTests()
    {
        _provider = null;
        Enabled = false;
        Endpoint = null;
        ServiceName = null;
    }

    private sealed class ProviderHandle : IDisposable
    {
        private readonly TracerProvider _tracerProvider;

        public ProviderHandle(TracerProvider tracerProvider) => _tracerProvider = tracerProvider;

        public void Dispose()
        {
            try
            {
                // Best-effort flush so a graceful stop ships buffered spans;
                // bounded so a dead collector can't hang shutdown.
                _tracerProvider.ForceFlush(2000);
            }
            catch
            {
                // never throw on shutdown
            }
            try
            {
                _tracerProvider.Dispose();
            }
            catch
            {
                // never throw on shutdown
            }
            Enabled = false;
            Endpoint = null;
            _provider = null;
        }
    }
}
