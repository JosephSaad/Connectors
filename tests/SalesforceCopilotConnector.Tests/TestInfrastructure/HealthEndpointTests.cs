// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for Infrastructure/HealthEndpoint.cs (#9).
//
//   * StartIfConfigured returns null when HEALTH_PORT is unset / 0 / negative /
//     non-numeric (the off-by-default guarantee).
//   * When HEALTH_ENDPOINT_TEST is set, a live bind on a high ephemeral port
//     serves /health, /ready and /metrics, then disposes cleanly. This test is
//     gated because HttpListener binding can be unreliable in some CI sandboxes
//     (mirrors the SQLSERVER_TEST_CONNECTION_STRING skip pattern).

using System.Net;
using System.Net.Sockets;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;
using Xunit.Abstractions;

namespace SalesforceCopilotConnector.Tests.TestInfrastructure;

/// <summary>Gate for the live-bind health-endpoint test.</summary>
public static class HealthEndpointTestEnv
{
    public const string EnvVar = "HEALTH_ENDPOINT_TEST";

    public static bool Enabled =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVar));
}

/// <summary>Reports whether the live-bind health test ran or was skipped.</summary>
public sealed class HealthEndpointSkipReport
{
    private readonly ITestOutputHelper _output;

    public HealthEndpointSkipReport(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ReportHealthEndpointLiveTestAvailability()
    {
        _output.WriteLine(HealthEndpointTestEnv.Enabled
            ? "Health endpoint live-bind test ENABLED (HEALTH_ENDPOINT_TEST is set)."
            : "Health endpoint live-bind test SKIPPED: set HEALTH_ENDPOINT_TEST=1 to run it.");
    }
}

/// <summary>
/// Mutates the process-global HEALTH_PORT env var; joins the "EnvVars"
/// collection and restores it.
/// </summary>
[Collection("EnvVars")]
public sealed class HealthEndpointTests : IDisposable
{
    private readonly string? _savedPort;

    public HealthEndpointTests()
    {
        _savedPort = Environment.GetEnvironmentVariable("HEALTH_PORT");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HEALTH_PORT", _savedPort);
    }

    private static AppConfig ConfigWithConnector(string id) => new()
    {
        Connector = new ConnectorSettings { Id = id },
    };

    // ── Off by default ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("notanumber")]
    public void StartIfConfiguredReturnsNullWhenDisabled(string? portValue)
    {
        Environment.SetEnvironmentVariable("HEALTH_PORT", portValue);
        var handle = HealthEndpoint.StartIfConfigured(ConfigWithConnector("c1"));
        Assert.Null(handle);
    }

    // ── Live bind (gated) ────────────────────────────────────────────────────

    [Fact]
    public async Task ServesRoutesAndDisposesCleanly()
    {
        if (!HealthEndpointTestEnv.Enabled)
        {
            return;  // skip: see HealthEndpointSkipReport
        }

        var port = FreeTcpPort();
        Environment.SetEnvironmentVariable("HEALTH_PORT", port.ToString());

        var handle = HealthEndpoint.StartIfConfigured(ConfigWithConnector("healthtest"));
        Assert.NotNull(handle);

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var baseUrl = $"http://localhost:{port}";

            var health = await client.GetAsync($"{baseUrl}/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Equal("OK", (await health.Content.ReadAsStringAsync()).Trim());

            var ready = await client.GetAsync($"{baseUrl}/ready");
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            Assert.Equal("READY", (await ready.Content.ReadAsStringAsync()).Trim());

            var metrics = await client.GetAsync($"{baseUrl}/metrics");
            Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
            var body = await metrics.Content.ReadAsStringAsync();
            Assert.Contains("salesforce_connector_items_ingested_total", body);
            Assert.Contains("# TYPE salesforce_connector_dead_letter_depth gauge", body);

            var missing = await client.GetAsync($"{baseUrl}/nope");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            handle!.Dispose();
        }

        // After dispose, the port should no longer accept connections.
        using var client2 = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        await Assert.ThrowsAnyAsync<Exception>(
            () => client2.GetAsync($"http://localhost:{port}/health"));
    }

    /// <summary>Grab a currently-free TCP port by binding to port 0.</summary>
    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
