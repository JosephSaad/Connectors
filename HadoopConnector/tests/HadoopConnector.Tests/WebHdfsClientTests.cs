// WebHdfsClientTests.cs
// ---------------------
// WebHDFS protocol client: URI construction (op + auth params + path
// escaping), LISTSTATUS parsing, RemoteException surfacing, GETFILESTATUS
// existence, OPEN streaming (redirect follow is HttpClient's; here we assert
// content + failure classification), and the 429/5xx retry ladder with exact
// Retry-After honoured and the 60s clamp. No network — MockHttpHandler only.

using System.Net;
using System.Net.Http.Headers;
using HadoopConnector.Hdfs;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Tests;

public class WebHdfsClientTests
{
    private const string Namenode = "http://nn.example:9870/webhdfs/v1";

    private static WebHdfsClient Make(
        MockHttpHandler handler,
        string? user = "svc-bdh",
        string? token = null,
        CircuitBreaker? breaker = null)
    {
        var client = new WebHdfsClient(
            Namenode, "/data/bdh", user, token, handler, breaker);
        client.DelayAsync = (_, _) => Task.CompletedTask;
        return client;
    }

    private const string ListBody = """
        {"FileStatuses":{"FileStatus":[
            {"pathSuffix":"dt=2026-07-15","type":"DIRECTORY","length":0,"modificationTime":0},
            {"pathSuffix":"part-0000.jsonl","type":"FILE","length":123,"modificationTime":1752537600000}
        ]}}
        """;

    // ── URI construction ─────────────────────────────────────────────────────

    [Fact]
    public void BuildUri_IncludesOpUserAndRoot()
    {
        var client = Make(new MockHttpHandler((_, _) => MockHttpHandler.Json(HttpStatusCode.OK, "{}")));
        var uri = client.BuildUri("Contact/dt=2026-07-15", "LISTSTATUS");
        Assert.Equal("http://nn.example:9870/webhdfs/v1/data/bdh/Contact/dt%3D2026-07-15"
                     + "?op=LISTSTATUS&user.name=svc-bdh", uri.ToString());
    }

    [Fact]
    public void BuildUri_EmptyRelativePath_TargetsRoot()
    {
        var client = Make(new MockHttpHandler((_, _) => MockHttpHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.StartsWith(
            "http://nn.example:9870/webhdfs/v1/data/bdh?op=GETFILESTATUS",
            client.BuildUri(string.Empty, "GETFILESTATUS").ToString());
    }

    [Fact]
    public void BuildUri_DelegationToken_Appended()
    {
        var client = Make(
            new MockHttpHandler((_, _) => MockHttpHandler.Json(HttpStatusCode.OK, "{}")),
            user: null, token: "TOK==123");
        var uri = client.BuildUri("Contact", "OPEN").ToString();
        Assert.Contains("delegation=TOK%3D%3D123", uri);
        Assert.DoesNotContain("user.name", uri);
    }

    [Fact]
    public void Ctor_MissingNamenodeUrl_Throws() =>
        Assert.Throws<ArgumentException>(() => new WebHdfsClient(" ", "/data/bdh"));

    // ── LISTSTATUS ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_ParsesFileStatuses()
    {
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Json(HttpStatusCode.OK, ListBody));
        var entries = await Make(handler).ListAsync("Contact");

        Assert.Equal(2, entries.Count);
        Assert.True(entries[0].IsDirectory);
        Assert.Equal("dt=2026-07-15", entries[0].PathSuffix);
        Assert.False(entries[1].IsDirectory);
        Assert.Equal(123, entries[1].Length);
        Assert.Equal(1752537600000, entries[1].ModificationTimeMs);
    }

    [Fact]
    public void ParseListStatus_MalformedEnvelope_Throws()
    {
        Assert.Throws<HdfsException>(() =>
            WebHdfsClient.ParseListStatus(System.Text.Json.Nodes.JsonNode.Parse("{}")));
        Assert.Throws<HdfsException>(() => WebHdfsClient.ParseListStatus(null));
    }

    [Fact]
    public async Task ListAsync_RemoteException_SurfacedInError()
    {
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Json(
            HttpStatusCode.NotFound,
            """{"RemoteException":{"exception":"FileNotFoundException","message":"File /data/bdh/Nope does not exist."}}"""));
        var exc = await Assert.ThrowsAsync<HdfsException>(() => Make(handler).ListAsync("Nope"));
        Assert.Equal(404, exc.StatusCode);
        Assert.Contains("does not exist", exc.Message);
    }

    // ── GETFILESTATUS / Ping ─────────────────────────────────────────────────

    [Fact]
    public async Task ExistsAsync_404IsFalse_200IsTrue()
    {
        var status = HttpStatusCode.NotFound;
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Json(status, "{}"));
        var client = Make(handler);
        Assert.False(await client.ExistsAsync("Contact"));
        status = HttpStatusCode.OK;
        Assert.True(await client.ExistsAsync("Contact"));
    }

    [Fact]
    public async Task PingAsync_NeverThrows()
    {
        var handler = new MockHttpHandler((_, _) => throw new HttpRequestException("net down"));
        Assert.False(await Make(handler).PingAsync());
    }

    // ── OPEN ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenAsync_StreamsContent()
    {
        var handler = new MockHttpHandler((request, _) =>
            request.RequestUri!.Query.Contains("op=OPEN")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("Id\n001\n"),
                }
                : MockHttpHandler.Json(HttpStatusCode.OK, "{}"));
        await using var stream = await Make(handler).OpenAsync("Contact/dt=2026-07-15/part-0000.csv");
        using var reader = new StreamReader(stream);
        Assert.Equal("Id\n001\n", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task OpenAsync_HttpError_ThrowsHdfsException()
    {
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Json(
            HttpStatusCode.Forbidden, """{"RemoteException":{"message":"denied"}}"""));
        var exc = await Assert.ThrowsAsync<HdfsException>(
            () => Make(handler).OpenAsync("Contact/x.csv"));
        Assert.Equal(403, exc.StatusCode);
    }

    // Regression (LOW-6): OPEN honours the same pre-body 429/5xx + Retry-After
    // ladder as SendAsync. Before the fix OpenAsync threw on the first 503.
    [Fact]
    public async Task OpenAsync_RetriesOn503_HonoursRetryAfter_ThenSucceeds()
    {
        var delays = new List<TimeSpan>();
        var calls = 0;
        var handler = new MockHttpHandler((_, _) =>
        {
            if (++calls == 1)
            {
                var response = MockHttpHandler.Json(HttpStatusCode.ServiceUnavailable, "maint");
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(9));
                return response;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Id\n001\n") };
        });
        var client = Make(handler);
        client.DelayAsync = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };

        await using var stream = await client.OpenAsync("Contact/dt=2026-07-15/part-0000.csv");
        using var reader = new StreamReader(stream);
        Assert.Equal("Id\n001\n", await reader.ReadToEndAsync());
        Assert.Equal(2, calls);                              // 503 then 200
        Assert.Equal(TimeSpan.FromSeconds(9), delays[0]);    // Retry-After honoured
    }

    // ── Retry ladder ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_RetriesOn500_ThenSucceeds()
    {
        var calls = 0;
        var handler = new MockHttpHandler((_, _) =>
            ++calls <= 2
                ? MockHttpHandler.Json(HttpStatusCode.InternalServerError, "boom")
                : MockHttpHandler.Json(HttpStatusCode.OK, ListBody));
        var entries = await Make(handler).ListAsync("Contact");
        Assert.Equal(3, calls);
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task ListAsync_HonoursRetryAfterExactly_AndClampsTo60s()
    {
        var delays = new List<TimeSpan>();
        var calls = 0;
        var handler = new MockHttpHandler((_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                var response = MockHttpHandler.Json((HttpStatusCode)429, "slow down");
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return response;
            }
            if (calls == 2)
            {
                var response = MockHttpHandler.Json(HttpStatusCode.ServiceUnavailable, "maint");
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(600));
                return response;
            }
            return MockHttpHandler.Json(HttpStatusCode.OK, ListBody);
        });
        var client = Make(handler);
        client.DelayAsync = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };

        await client.ListAsync("Contact");
        Assert.Equal(TimeSpan.FromSeconds(7), delays[0]);    // exact Retry-After
        Assert.Equal(TimeSpan.FromSeconds(60), delays[1]);   // clamped to 60s
    }

    [Fact]
    public async Task ListAsync_RetriesExhausted_Throws()
    {
        var calls = 0;
        var handler = new MockHttpHandler((_, _) =>
        {
            calls++;
            return MockHttpHandler.Json(HttpStatusCode.BadGateway, "down");
        });
        await Assert.ThrowsAsync<HdfsException>(() => Make(handler).ListAsync("Contact"));
        Assert.Equal(5, calls);   // initial + 4 retries
    }

    [Fact]
    public async Task ListAsync_TransportErrors_RetriedThenThrown()
    {
        var calls = 0;
        var handler = new MockHttpHandler((_, _) =>
        {
            calls++;
            throw new HttpRequestException("connection reset");
        });
        await Assert.ThrowsAsync<HttpRequestException>(() => Make(handler).ListAsync("Contact"));
        Assert.Equal(5, calls);
    }

    // ── Delegation-token leak canary ─────────────────────────────────────────
    // The token lives in the query string (?delegation=...). It must NEVER appear
    // in an exception message surfaced from ANY failure path — those messages are
    // logged and shipped to SIEM. Logging uses uri.AbsolutePath (no query); this
    // pins that no error path regresses to the full URI.
    private const string SecretToken = "SUPERSECRETTOK==xyz";

    [Fact]
    public async Task ListAsync_HttpError_ExceptionMessageOmitsDelegationToken()
    {
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Json(
            HttpStatusCode.Forbidden, """{"RemoteException":{"message":"denied"}}"""));
        var exc = await Assert.ThrowsAsync<HdfsException>(
            () => Make(handler, token: SecretToken).ListAsync("Contact"));
        Assert.DoesNotContain(SecretToken, exc.ToString());
        Assert.DoesNotContain("delegation=", exc.ToString());
    }

    [Fact]
    public async Task OpenAsync_HttpError_ExceptionMessageOmitsDelegationToken()
    {
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Json(
            HttpStatusCode.Forbidden, """{"RemoteException":{"message":"denied"}}"""));
        var exc = await Assert.ThrowsAsync<HdfsException>(
            () => Make(handler, token: SecretToken).OpenAsync("Contact/x.csv"));
        Assert.DoesNotContain(SecretToken, exc.ToString());
        Assert.DoesNotContain("delegation=", exc.ToString());
    }

    [Fact]
    public async Task OpenAsync_TransportError_ExceptionMessageOmitsDelegationToken()
    {
        // A transport failure after retries exhausted is wrapped in an
        // HdfsException by OpenAsync ("OPEN '<relativePath>' failed: <inner>").
        var handler = new MockHttpHandler((_, _) => throw new HttpRequestException("connection reset"));
        var exc = await Assert.ThrowsAsync<HdfsException>(
            () => Make(handler, token: SecretToken).OpenAsync("Contact/x.csv"));
        Assert.DoesNotContain(SecretToken, exc.ToString());
        Assert.DoesNotContain("delegation=", exc.ToString());
    }

    // ── Circuit breaker interplay ────────────────────────────────────────────

    [Fact]
    public async Task OpenBreaker_FailsFastWithCircuitOpenException()
    {
        var options = new CircuitBreakerOptions
        {
            FailureThreshold = 1,
            OpenDuration = TimeSpan.FromMinutes(5),
        };
        var breaker = new CircuitBreaker("hdfs", options, () => DateTime.UtcNow);
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Json(HttpStatusCode.OK, ListBody));
        var client = Make(handler, breaker: breaker);

        breaker.TripForTests();
        await Assert.ThrowsAsync<CircuitOpenException>(() => client.ListAsync("Contact"));
        await Assert.ThrowsAsync<CircuitOpenException>(() => client.ExistsAsync("Contact"));
        await Assert.ThrowsAsync<CircuitOpenException>(() => client.OpenAsync("Contact/x.csv"));
        Assert.Empty(handler.Requests);   // nothing was sent
    }
}
