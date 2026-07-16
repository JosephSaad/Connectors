// The clients' breaker classification: 5xx and transport failures trip the
// breaker; 4xx and 429 (flow control) do not; and an open breaker fails fast
// (CircuitOpen) without hitting the network.

using System.Net;
using System.Text.Json.Nodes;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Tests;

public class GraphClientBreakerTests
{
    private static GraphClient Make(MockHttpHandler handler, CircuitBreaker breaker)
    {
        var client = new GraphClient(
            TestConfig.Make(graphMaxRetries: 0), handler, breaker) { OverrideToken = "t" };
        client.DelayAsync = (_, _) => Task.CompletedTask;
        return client;
    }

    private static CircuitBreaker Breaker(int threshold = 3) =>
        new("graph", new CircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = threshold,
            OpenDuration = TimeSpan.FromSeconds(30),
            Window = TimeSpan.FromSeconds(60),
            HalfOpenTrials = 1,
        });

    [Fact]
    public async Task ServerErrors_TripTheBreaker()
    {
        var breaker = Breaker(threshold: 3);
        var handler = new MockHttpHandler((_, _) =>
            MockHttpHandler.Json(HttpStatusCode.InternalServerError, "boom"));
        var client = Make(handler, breaker);

        await client.GetAsync("x");
        await client.GetAsync("x");
        Assert.Equal(CircuitState.Closed, breaker.State);
        await client.GetAsync("x");
        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public async Task ClientErrorsAndThrottle_DoNotTrip()
    {
        var breaker = Breaker(threshold: 2);
        var handler = new MockHttpHandler((request, _) =>
        {
            // Alternate 400 and 429 — neither is an outage.
            var count = request.RequestUri!.Query.Length;
            return MockHttpHandler.Json(
                count % 2 == 0 ? HttpStatusCode.BadRequest : (HttpStatusCode)429, "{}");
        });
        var client = Make(handler, breaker);

        for (var i = 0; i < 10; i++)
            await client.GetAsync("x");
        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal(0, breaker.Trips);
    }

    [Fact]
    public async Task TransportFailure_TripsTheBreaker()
    {
        var breaker = Breaker(threshold: 2);
        var handler = new MockHttpHandler((_, _) => throw new HttpRequestException("dns down"));
        var client = Make(handler, breaker);

        // maxRetries=0 → the transport failure propagates; the breaker records it.
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("x"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("x"));
        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public async Task OpenBreaker_FailsFast_WithoutNetwork()
    {
        var breaker = Breaker(threshold: 1);
        breaker.TripForTests();
        var handler = new MockHttpHandler((_, _) =>
            MockHttpHandler.Json(HttpStatusCode.OK, "{}"));
        var client = Make(handler, breaker);

        var response = await client.PutAsync("items/1", new JsonObject());
        Assert.True(response.CircuitOpen);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(handler.Requests);  // never hit the network
    }

    [Fact]
    public async Task Success_KeepsBreakerClosed()
    {
        var breaker = Breaker(threshold: 2);
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Json(HttpStatusCode.OK, "{}"));
        var client = Make(handler, breaker);
        for (var i = 0; i < 5; i++)
            await client.GetAsync("x");
        Assert.Equal(CircuitState.Closed, breaker.State);
    }
}

public class ClarizenClientBreakerTests
{
    private static ClarizenClient Make(MockHttpHandler handler, CircuitBreaker breaker)
    {
        var client = new ClarizenClient(
            TestConfig.Make(), new ApiBudget(1_000_000, callsPerMinute: 6_000_000), handler, breaker)
        {
            OverrideSessionId = "s",
        };
        client.DelayAsync = (_, _) => Task.CompletedTask;
        return client;
    }

    private static CircuitBreaker Breaker(int threshold = 3) =>
        new("clarizen", new CircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = threshold,
            OpenDuration = TimeSpan.FromSeconds(30),
            Window = TimeSpan.FromSeconds(60),
            HalfOpenTrials = 1,
        });

    private static MockHttpHandler Status(HttpStatusCode code) =>
        new((_, _) => MockHttpHandler.Json(code, "{}"));

    [Fact]
    public async Task ServerErrors_TripTheBreaker()
    {
        var breaker = Breaker(threshold: 2);
        var client = Make(Status(HttpStatusCode.BadGateway), breaker);
        // Each query exhausts retries on 502; the terminal outcome is a failure.
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.QueryAllAsync("SELECT x FROM Y"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.QueryAllAsync("SELECT x FROM Y"));
        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public async Task ClientError_DoesNotTrip()
    {
        var breaker = Breaker(threshold: 2);
        var client = Make(Status(HttpStatusCode.BadRequest), breaker);
        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.QueryAllAsync("SELECT x FROM Y"));
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public async Task OpenBreaker_QueryFailsFast_AsCircuitOpen()
    {
        var breaker = Breaker(threshold: 1);
        breaker.TripForTests();
        var handler = Status(HttpStatusCode.OK);
        var client = Make(handler, breaker);

        await Assert.ThrowsAsync<CircuitOpenException>(() => client.QueryAllAsync("SELECT x FROM Y"));
        Assert.Empty(handler.Requests);  // fail fast, no network
    }
}
