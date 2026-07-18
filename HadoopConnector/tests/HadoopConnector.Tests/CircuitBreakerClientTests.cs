// The clients' breaker classification: 5xx and transport failures trip the
// breaker; 4xx and 429 (flow control) do not; and an open breaker fails fast
// (CircuitOpen) without hitting the network.

using System.Net;
using System.Text.Json.Nodes;
using HadoopConnector.Hdfs;
using HadoopConnector.Graph;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Tests;

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

public class WebHdfsClientBreakerTests
{
    private const string ListStatusJson = """
        {"FileStatuses": {"FileStatus": [
            {"pathSuffix": "dt=2026-01-01", "type": "DIRECTORY", "length": 0, "modificationTime": 0}
        ]}}
        """;

    private static WebHdfsClient Make(MockHttpHandler handler, CircuitBreaker breaker)
    {
        var client = new WebHdfsClient(
            "http://namenode.example:9870/webhdfs/v1", "/data/bdh", "svc-bdh",
            delegationToken: null, handler: handler, breaker: breaker);
        client.DelayAsync = (_, _) => Task.CompletedTask;
        return client;
    }

    private static CircuitBreaker Breaker(int threshold = 3) =>
        new("hdfs", new CircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = threshold,
            OpenDuration = TimeSpan.FromSeconds(30),
            Window = TimeSpan.FromSeconds(60),
            HalfOpenTrials = 1,
        });

    private static MockHttpHandler Status(HttpStatusCode code, string body = "{}") =>
        new((_, _) => MockHttpHandler.Json(code, body));

    [Fact]
    public async Task ServerErrors_AfterRetries_TripTheBreaker()
    {
        var breaker = Breaker(threshold: 2);
        var client = Make(Status(HttpStatusCode.BadGateway), breaker);
        // Each LISTSTATUS exhausts retries on 502; the terminal outcome is ONE failure.
        await Assert.ThrowsAsync<HdfsException>(() => client.ListAsync("Contact"));
        Assert.Equal(CircuitState.Closed, breaker.State);  // 1 < 2
        await Assert.ThrowsAsync<HdfsException>(() => client.ListAsync("Contact"));
        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public async Task ClientError_DoesNotTrip()
    {
        var breaker = Breaker(threshold: 2);
        var client = Make(Status(HttpStatusCode.NotFound,
            """{"RemoteException": {"message": "File does not exist"}}"""), breaker);
        for (var i = 0; i < 5; i++)
        {
            var exc = await Assert.ThrowsAsync<HdfsException>(() => client.ListAsync("Ghost"));
            Assert.Equal(404, exc.StatusCode);
        }
        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal(0, breaker.Trips);
    }

    [Fact]
    public async Task Success_ParsesListing_AndClearsTheWindow()
    {
        var breaker = Breaker(threshold: 2);
        var client = Make(Status(HttpStatusCode.OK, ListStatusJson), breaker);

        breaker.OnFailure();  // one pre-existing failure in the window
        var entries = await client.ListAsync("Contact");
        var entry = Assert.Single(entries);
        Assert.Equal("dt=2026-01-01", entry.PathSuffix);
        Assert.True(entry.IsDirectory);

        breaker.OnFailure();  // the earlier failure was cleared by the success
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public async Task OpenBreaker_ListFailsFast_AsCircuitOpen()
    {
        var breaker = Breaker(threshold: 1);
        breaker.TripForTests();
        var handler = Status(HttpStatusCode.OK, ListStatusJson);
        var client = Make(handler, breaker);

        await Assert.ThrowsAsync<CircuitOpenException>(() => client.ListAsync("Contact"));
        Assert.Empty(handler.Requests);  // fail fast, no network
    }

    [Fact]
    public async Task OpenBreaker_OpenFailsFast_AsCircuitOpen()
    {
        var breaker = Breaker(threshold: 1);
        breaker.TripForTests();
        var handler = Status(HttpStatusCode.OK, "data");
        var client = Make(handler, breaker);

        await Assert.ThrowsAsync<CircuitOpenException>(
            () => client.OpenAsync("Contact/dt=2026-01-01/part-000.jsonl"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Open_ServerError_TripsAndClientErrorDoesNot()
    {
        var trip = Breaker(threshold: 1);
        var failing = Make(Status(HttpStatusCode.InternalServerError, "boom"), trip);
        await Assert.ThrowsAsync<HdfsException>(() => failing.OpenAsync("f"));
        Assert.Equal(CircuitState.Open, trip.State);

        var ignore = Breaker(threshold: 1);
        var notFound = Make(Status(HttpStatusCode.NotFound, "gone"), ignore);
        await Assert.ThrowsAsync<HdfsException>(() => notFound.OpenAsync("f"));
        Assert.Equal(CircuitState.Closed, ignore.State);
        Assert.Equal(0, ignore.Trips);
    }

    [Fact]
    public void ParseListStatus_Malformed_ThrowsInsteadOfEmptyList()
    {
        // A malformed envelope must never look like an empty directory (that
        // would feed the deletion sweep a phantom-empty source).
        Assert.Throws<HdfsException>(() => WebHdfsClient.ParseListStatus(JsonNode.Parse("{}")));
        Assert.Throws<HdfsException>(() => WebHdfsClient.ParseListStatus(null));
    }

    [Fact]
    public void BuildUri_CarriesAuthQueryAndRootPath()
    {
        using var client = new WebHdfsClient(
            "http://namenode.example:9870/webhdfs/v1", "/data/bdh", "svc-bdh", "tok-123");
        var uri = client.BuildUri("Contact/dt=2026-01-01", "LISTSTATUS");
        Assert.StartsWith("http://namenode.example:9870/webhdfs/v1/data/bdh/Contact/", uri.ToString());
        Assert.Contains("op=LISTSTATUS", uri.Query);
        Assert.Contains("user.name=svc-bdh", uri.Query);
        Assert.Contains("delegation=tok-123", uri.Query);
    }

    /// <summary>Build a breaker on an injectable clock, trip it, then advance past
    /// OpenDuration so it is in HalfOpen and ready to admit probes.</summary>
    private static CircuitBreaker HalfOpenBreaker(int halfOpenTrials)
    {
        var clock = DateTime.UtcNow;
        var breaker = new CircuitBreaker("hdfs", new CircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = 2,
            OpenDuration = TimeSpan.FromSeconds(30),
            Window = TimeSpan.FromSeconds(60),
            HalfOpenTrials = halfOpenTrials,
        }, () => clock);
        breaker.TripForTests();                 // Open at 'clock'
        clock = clock.AddSeconds(31);           // past OpenDuration → HalfOpen on next Refresh
        return breaker;
    }

    // Wedge regression (settle-once): a HalfOpen probe ending in a 4xx (ignored)
    // outcome must release its probe slot. If slots leaked, HalfOpenTrials
    // ignored probes would wedge the breaker open forever.
    [Fact]
    public async Task HalfOpenProbes_IgnoredOutcomes_DoNotWedgeTheBreaker()
    {
        var breaker = HalfOpenBreaker(halfOpenTrials: 1);
        Assert.Equal(CircuitState.HalfOpen, breaker.State);

        var client = Make(Status(HttpStatusCode.NotFound, "gone"), breaker);
        for (var i = 0; i < 5; i++)
        {
            var exc = await Assert.ThrowsAsync<HdfsException>(() => client.ListAsync("Ghost"));
            Assert.Equal(404, exc.StatusCode);
            Assert.Equal(CircuitState.HalfOpen, breaker.State);  // still probing, not wedged
        }

        // A fresh probe is still admitted and a real success closes the breaker.
        var healthy = Make(Status(HttpStatusCode.OK, ListStatusJson), breaker);
        await healthy.ListAsync("Contact");
        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal(1, breaker.Resets);
    }

    [Fact]
    public async Task HalfOpen_SuccessfulProbes_RecoverTheBreaker_AndCallsSucceedAfter()
    {
        var breaker = HalfOpenBreaker(halfOpenTrials: 2);
        Assert.Equal(CircuitState.HalfOpen, breaker.State);

        var client = Make(Status(HttpStatusCode.OK, ListStatusJson), breaker);
        await client.ListAsync("Contact");   // probe 1
        Assert.Equal(CircuitState.HalfOpen, breaker.State);
        await client.ListAsync("Contact");   // probe 2 → closes
        Assert.Equal(CircuitState.Closed, breaker.State);

        // No HalfOpen wedge: after recovery, calls flow normally.
        var entries = await client.ListAsync("Contact");
        Assert.Single(entries);
    }

    [Fact]
    public async Task HalfOpen_OpenAsyncProbe_SettlesItsSlotExactlyOnce()
    {
        var breaker = HalfOpenBreaker(halfOpenTrials: 2);
        Assert.Equal(CircuitState.HalfOpen, breaker.State);

        // Simulate one concurrent probe already in flight (slot 1 of 2 taken).
        Assert.True(breaker.TryAcquire());

        var client = Make(Status(HttpStatusCode.NotFound, "gone"), breaker);
        await Assert.ThrowsAsync<HdfsException>(() => client.OpenAsync("Ghost/file"));

        // Exactly one slot (the probe's own) was released, so one slot remains
        // held by the simulated concurrent probe: only ONE more acquire fits.
        Assert.True(breaker.TryAcquire());   // now 2 of 2
        Assert.False(breaker.TryAcquire());  // cap reached → no double-release happened
    }
}
