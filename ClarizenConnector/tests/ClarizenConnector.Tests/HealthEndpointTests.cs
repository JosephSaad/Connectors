using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Tests;

public class HealthEndpointTests
{
    [Fact]
    public void Disabled_WhenPortUnsetOrInvalid()
    {
        using (new EnvScope((HealthEndpoint.PortEnvVar, null)))
            Assert.Null(HealthEndpoint.StartIfConfigured());
        using (new EnvScope((HealthEndpoint.PortEnvVar, "0")))
            Assert.Null(HealthEndpoint.StartIfConfigured());
        using (new EnvScope((HealthEndpoint.PortEnvVar, "-5")))
            Assert.Null(HealthEndpoint.StartIfConfigured());
        using (new EnvScope((HealthEndpoint.PortEnvVar, "notaport")))
            Assert.Null(HealthEndpoint.StartIfConfigured());
    }

    [Fact]
    public async Task ServesHealthReadyMetricsAnd404()
    {
        var port = FreePort();
        using var scope = new EnvScope((HealthEndpoint.PortEnvVar, port.ToString()));
        Metrics.ResetForTests();
        Metrics.IncItemsIngested(11);

        using var endpoint = HealthEndpoint.StartIfConfigured(deadLetterDepth: () => 42);
        Assert.NotNull(endpoint);
        using var http = new HttpClient();

        Assert.Equal("OK", await http.GetStringAsync($"http://localhost:{port}/health"));
        Assert.Equal("READY", await http.GetStringAsync($"http://localhost:{port}/ready"));

        var metrics = await http.GetStringAsync($"http://localhost:{port}/metrics");
        Assert.Contains("clarizen_connector_items_ingested_total 11", metrics);
        Assert.Contains("clarizen_connector_dead_letter_depth 42", metrics);

        var notFound = await http.GetAsync($"http://localhost:{port}/nope");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, notFound.StatusCode);
        Metrics.ResetForTests();
    }

    [Fact]
    public void Dispose_StopsListener()
    {
        var port = FreePort();
        using var scope = new EnvScope((HealthEndpoint.PortEnvVar, port.ToString()));
        var endpoint = HealthEndpoint.StartIfConfigured();
        Assert.NotNull(endpoint);
        endpoint!.Dispose();
        endpoint.Dispose();  // double-dispose is safe

        // The port is released — a second endpoint can bind it.
        using var second = HealthEndpoint.StartIfConfigured();
        Assert.NotNull(second);
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
