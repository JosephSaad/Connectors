// Infrastructure/SalesforceTracing.cs
// -----------------------------------
// Distributed tracing for this connector, via the shared chassis.
//
// This connector was the only one of the five with no OpenTelemetry at all,
// while the fleet documentation described tracing as standard. The Aug-2026
// TOGAF assessment raised that as a concern in its own right — not the missing
// capability so much as the untrue claim, since nothing failed when a connector
// quietly lacked something the docs promised. The Conformance workflow now fails
// on exactly that, and this file is what lets its Salesforce exemption be
// deleted.
//
// The span vocabulary is the chassis's, deliberately: BeginCycle opens the same
// crawl-cycle span, with the same correlation id, that Seismic and Altrata
// already emit. A trace backend should not need per-connector knowledge to read
// a fleet it is told is uniform.
//
// Export is opt-in and off by default. With OTEL_EXPORTER_OTLP_ENDPOINT unset,
// Tracing.Initialize installs no provider and BeginCycle returns a scope that
// still carries a correlation id but opens no span — so the correlation
// threading through logs works with or without a collector, and a deployment
// that never sets the variable pays nothing.

using Connector.Chassis;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Infrastructure;

/// <summary>Builds <see cref="ChassisTracingOptions"/> from the OTEL_* env vars and <see cref="AppConfig"/>.</summary>
public static class SalesforceTracing
{
    /// <summary>
    /// Resolve the chassis tracing options. OTLP export is enabled only when
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set; protocol and headers ride the
    /// standard OTEL_* variables so an operator configures this connector the
    /// same way as any other OpenTelemetry process; the service name is
    /// <c>OTEL_SERVICE_NAME</c> when set, otherwise the connector name.
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

    /// <summary>Install the tracer. Idempotent — the chassis ignores a second call.</summary>
    public static void Initialize(AppConfig config) => Tracing.Initialize(OptionsFrom(config));

    private static string ResolveServiceName(AppConfig config)
    {
        var fromEnv = Environment.GetEnvironmentVariable(Tracing.ServiceNameEnvVar);
        return !string.IsNullOrWhiteSpace(fromEnv) ? fromEnv.Trim() : config.Connector.Name;
    }
}
