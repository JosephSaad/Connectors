// TracingTests.cs
// ---------------
// Proof that this connector actually emits spans, not merely that it references
// OpenTelemetry.
//
// The distinction matters here more than usual. The Conformance workflow detects
// the capability by grep, so adding the package and an Initialize call would
// turn that job green while producing no telemetry at all — satisfying the gate
// instead of the requirement it stands for. These tests close that loophole by
// asserting on spans captured from the ActivitySource in memory.

using System.Diagnostics;
using Connector.Chassis;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Tests;

/// <summary>
/// Captures every Activity from the chassis ActivitySource for the lifetime of
/// the scope. In-memory, no exporter, no network — and samples AllData so spans
/// are visible without an OTLP endpoint configured.
/// </summary>
public sealed class ChassisSpanCapture : IDisposable
{
    private readonly ActivityListener _listener;

    public List<Activity> Spans { get; } = new();

    public ChassisSpanCapture()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == Tracing.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => { lock (Spans) Spans.Add(a); },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();
}

public class TracingTests
{
    /// <summary>
    /// A crawl cycle opens a real span on the chassis ActivitySource — the same
    /// source Seismic and Altrata publish to, so a trace backend needs no
    /// per-connector knowledge to read a fleet it is told is uniform.
    /// </summary>
    [Fact]
    public void BeginCycle_EmitsASpanOnTheSharedChassisSource()
    {
        using var capture = new ChassisSpanCapture();

        using (var cycle = Tracing.BeginCycle("salesforce_connector", "full"))
        {
            Assert.False(string.IsNullOrWhiteSpace(cycle.CorrelationId));
        }

        Assert.NotEmpty(capture.Spans);
    }

    /// <summary>
    /// Export stays opt-in. With no OTLP endpoint the chassis installs no
    /// provider, but the cycle scope must still hand back a correlation id — the
    /// id threads through logs and dead-letter records whether or not a collector
    /// exists, so a deployment that never sets the variable loses nothing but the
    /// export.
    /// </summary>
    [Fact]
    public void WithNoOtlpEndpoint_TracingIsInertButStillCorrelates()
    {
        var previous = Environment.GetEnvironmentVariable(Tracing.EndpointEnvVar);
        Environment.SetEnvironmentVariable(Tracing.EndpointEnvVar, null);
        try
        {
            using var cycle = Tracing.BeginCycle("salesforce_connector", "incremental");
            Assert.False(string.IsNullOrWhiteSpace(cycle.CorrelationId));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Tracing.EndpointEnvVar, previous);
        }
    }

    /// <summary>
    /// The OTEL_* variables are read the standard way, so this connector is
    /// configured like any other OpenTelemetry process rather than through a
    /// bespoke knob.
    /// </summary>
    [Fact]
    public void OptionsFrom_EnablesExportOnlyWhenAnEndpointIsSet()
    {
        var previousEndpoint = Environment.GetEnvironmentVariable(Tracing.EndpointEnvVar);
        var previousName = Environment.GetEnvironmentVariable(Tracing.ServiceNameEnvVar);
        try
        {
            var config = new Salesforce.AppConfig();

            Environment.SetEnvironmentVariable(Tracing.EndpointEnvVar, null);
            Assert.False(SalesforceTracing.OptionsFrom(config).Enabled);

            Environment.SetEnvironmentVariable(Tracing.EndpointEnvVar, "http://localhost:4317");
            var on = SalesforceTracing.OptionsFrom(config);
            Assert.True(on.Enabled);
            Assert.Equal("http://localhost:4317", on.OtlpEndpoint);

            Environment.SetEnvironmentVariable(Tracing.ServiceNameEnvVar, "sf-under-test");
            Assert.Equal("sf-under-test", SalesforceTracing.OptionsFrom(config).ServiceName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Tracing.EndpointEnvVar, previousEndpoint);
            Environment.SetEnvironmentVariable(Tracing.ServiceNameEnvVar, previousName);
        }
    }
}
