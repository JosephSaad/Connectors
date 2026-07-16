using System.Net;
using System.Text.Json.Nodes;
using ClarizenConnector.Graph;

namespace ClarizenConnector.Tests;

public class GraphClientTests
{
    private static GraphClient Make(
        MockHttpHandler handler, List<TimeSpan> delays, int maxRetries = 4)
    {
        var client = new GraphClient(TestConfig.Make(graphMaxRetries: maxRetries), handler)
        {
            OverrideToken = "test-token",
        };
        client.DelayAsync = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };
        return client;
    }

    [Fact]
    public async Task Success_NoRetries()
    {
        var handler = new MockHttpHandler((_, _) =>
            MockHttpHandler.Json(HttpStatusCode.OK, """{"value": 1}"""));
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var response = await client.GetAsync("external/connections/X");
        Assert.True(response.IsSuccess);
        Assert.Equal(1, response.Body!["value"]!.GetValue<int>());
        Assert.Empty(delays);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Throttled429_HonorsRetryAfterExactly()
    {
        var calls = 0;
        var handler = new MockHttpHandler((_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                var throttled = MockHttpHandler.Json(HttpStatusCode.TooManyRequests, "{}");
                throttled.Headers.RetryAfter =
                    new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
                return throttled;
            }
            return MockHttpHandler.Json(HttpStatusCode.OK, "{}");
        });
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var response = await client.GetAsync("items/1");
        Assert.True(response.IsSuccess);
        Assert.Equal(2, calls);
        Assert.Equal(17.0, Assert.Single(delays).TotalSeconds, 3);
    }

    [Fact]
    public async Task ServerError_UsesComputedBackoff()
    {
        using var scope = new EnvScope(("GRAPH_RETRY_JITTER", null));
        var calls = 0;
        var handler = new MockHttpHandler((_, _) =>
        {
            calls++;
            return calls <= 2
                ? MockHttpHandler.Json(HttpStatusCode.InternalServerError, "boom")
                : MockHttpHandler.Json(HttpStatusCode.OK, "{}");
        });
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var response = await client.GetAsync("items/1");
        Assert.True(response.IsSuccess);
        Assert.Equal(3, calls);
        // base 2: attempt 0 → 2s, attempt 1 → 4s
        Assert.Equal(new[] { 2.0, 4.0 }, delays.Select(d => d.TotalSeconds).ToArray());
    }

    [Fact]
    public async Task ExhaustedRetries_ReturnsLastResponse()
    {
        var handler = new MockHttpHandler((_, _) =>
            MockHttpHandler.Json(HttpStatusCode.ServiceUnavailable, "down"));
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays, maxRetries: 2);

        var response = await client.GetAsync("items/1");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, handler.Requests.Count);  // initial + 2 retries
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public async Task ClientError400_IsNotRetried()
    {
        var handler = new MockHttpHandler((_, _) =>
            MockHttpHandler.Json(HttpStatusCode.BadRequest, """{"error": "bad"}"""));
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var response = await client.PutAsync("items/1", new JsonObject());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(handler.Requests);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task BearerToken_IsAttached()
    {
        string? auth = null;
        var handler = new MockHttpHandler((request, _) =>
        {
            auth = request.Headers.Authorization?.ToString();
            return MockHttpHandler.Json(HttpStatusCode.OK, "{}");
        });
        var client = Make(handler, new List<TimeSpan>());
        await client.GetAsync("me");
        Assert.Equal("Bearer test-token", auth);
    }

    [Fact]
    public async Task TokenEndpoint_UsedOnceAndCached()
    {
        var tokenCalls = 0;
        var handler = new MockHttpHandler((request, _) =>
        {
            if (request.RequestUri!.ToString().Contains("login.microsoftonline.com"))
            {
                tokenCalls++;
                return MockHttpHandler.Json(
                    HttpStatusCode.OK, """{"access_token": "tok", "expires_in": 3600}""");
            }
            return MockHttpHandler.Json(HttpStatusCode.OK, "{}");
        });
        var client = new GraphClient(TestConfig.Make(), handler);
        client.DelayAsync = (_, _) => Task.CompletedTask;

        await client.GetAsync("a");
        await client.GetAsync("b");
        Assert.Equal(1, tokenCalls);
    }

    [Fact]
    public void ParseRetryAfter_DeltaSeconds()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter =
            new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
        Assert.Equal(42.0, GraphClient.ParseRetryAfter(response.Headers)!.Value, 3);
    }

    [Fact]
    public void ParseRetryAfter_HttpDate_FallsBackToComputedBackoff()
    {
        // 429 hardening (SF parity): only NUMERIC delta-seconds are trusted;
        // an HTTP-date (or garbage) yields null → computed backoff instead.
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
            DateTimeOffset.UtcNow.AddSeconds(30));
        Assert.Null(GraphClient.ParseRetryAfter(response.Headers));
    }

    [Fact]
    public async Task RetryAfterBeyondCap_IsClampedTo60s()
    {
        var calls = 0;
        var handler = new MockHttpHandler((_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                var throttled = MockHttpHandler.Json(HttpStatusCode.TooManyRequests, "{}");
                throttled.Headers.TryAddWithoutValidation("Retry-After", "600");
                return throttled;
            }
            return MockHttpHandler.Json(HttpStatusCode.OK, "{}");
        });
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var response = await client.GetAsync("items/1");
        Assert.True(response.IsSuccess);
        Assert.Equal(60.0, Assert.Single(delays).TotalSeconds, 3);
    }

    [Fact]
    public async Task NotImplemented501_IsNotRetried_ButBadGateway502Is()
    {
        // The retryable set is exactly {429, 500, 502, 503, 504} (SF parity).
        Assert.Equal(new[] { 429, 500, 502, 503, 504 },
            GraphClient.RetryableStatusCodes.OrderBy(c => c).ToArray());

        var calls = 0;
        var handler = new MockHttpHandler((_, _) =>
        {
            calls++;
            return calls == 1
                ? MockHttpHandler.Json(HttpStatusCode.BadGateway, "bad gateway")
                : MockHttpHandler.Json(HttpStatusCode.OK, "{}");
        });
        var client = Make(handler, new List<TimeSpan>());
        var response = await client.GetAsync("items/1");
        Assert.True(response.IsSuccess);
        Assert.Equal(2, calls);

        var notImplemented = new MockHttpHandler((_, _) =>
            MockHttpHandler.Json(HttpStatusCode.NotImplemented, "nope"));
        var client501 = Make(notImplemented, new List<TimeSpan>());
        var result = await client501.GetAsync("items/1");
        Assert.Equal(HttpStatusCode.NotImplemented, result.StatusCode);
        Assert.Single(notImplemented.Requests);
    }

    [Fact]
    public void ParseRetryAfter_Absent_ReturnsNull()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        Assert.Null(GraphClient.ParseRetryAfter(response.Headers));
    }
}
