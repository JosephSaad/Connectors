// Infrastructure/SeismicTracing.cs
// --------------------------------
// Seismic-side bridge to the connector-neutral chassis tracing layer.
//
// The OTLP endpoint/protocol/headers and OTEL_SERVICE_NAME resolution that used
// to live inside Tracing.Initialize live here now: this reads the standard
// OTEL_* environment variables plus the connector's AppConfig and builds the
// connector-neutral ChassisTracingOptions the chassis Tracing consumes. Keeping
// this on the Seismic side is what lets the chassis reference no AppConfig while
// the resulting OTLP behaviour stays byte-identical to the pre-extraction code.

using SeismicConnector.Config;

namespace SeismicConnector.Infrastructure;

/// <summary>Builds <see cref="ChassisTracingOptions"/> from OTEL_* env vars + AppConfig.</summary>
public static class SeismicTracing
{
    /// <summary>
    /// Resolve the chassis tracing options: OTLP export is enabled when
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set; the protocol/headers ride the
    /// standard OTEL_* vars; the service name is <c>OTEL_SERVICE_NAME</c> when
    /// set, otherwise the connector name.
    /// </summary>
    public static ChassisTracingOptions OptionsFrom(AppConfig config)
    {
        var endpoint = Environment.GetEnvironmentVariable(Tracing.EndpointEnvVar);
        return new ChassisTracingOptions(
            Enabled: !string.IsNullOrWhiteSpace(endpoint),
            OtlpEndpoint: endpoint,
            OtlpProtocol: Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL"),
            OtlpHeaders: Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS"),
            ServiceName: ResolveServiceName(config));
    }

    private static string ResolveServiceName(AppConfig config)
    {
        var fromEnv = Environment.GetEnvironmentVariable(Tracing.ServiceNameEnvVar);
        return !string.IsNullOrWhiteSpace(fromEnv) ? fromEnv.Trim() : config.Connector.Name;
    }
}
