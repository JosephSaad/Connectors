using System.Net;
using AltrataConnector.Graph;

namespace AltrataConnector.Tests;

public class RetryDelayTests : IDisposable
{
    public RetryDelayTests() => Environment.SetEnvironmentVariable("GRAPH_RETRY_JITTER", null);
    public void Dispose() => Environment.SetEnvironmentVariable("GRAPH_RETRY_JITTER", null);

    [Fact]
    public void JitterDisabledIsIdentity()
    {
        Assert.Equal(10.0, RetryDelay.ApplyJitter(10.0, jitterEnabled: false, uniformSample: 0.0));
        Assert.Equal(10.0, RetryDelay.ApplyJitter(10.0, jitterEnabled: false, uniformSample: 0.999));
    }

    [Fact]
    public void JitterBoundsArePlusMinusTwentyPercent()
    {
        // sample 0 → 0.8x, sample→1 → →1.2x
        Assert.Equal(8.0, RetryDelay.ApplyJitter(10.0, true, 0.0), 10);
        Assert.Equal(12.0, RetryDelay.ApplyJitter(10.0, true, 1.0), 10);
        Assert.Equal(10.0, RetryDelay.ApplyJitter(10.0, true, 0.5), 10);
    }

    [Fact]
    public void JitterRespectsEnvironmentFlag()
    {
        Environment.SetEnvironmentVariable("GRAPH_RETRY_JITTER", "true");
        Assert.True(RetryDelay.JitterEnabled);
        for (var i = 0; i < 200; i++)
        {
            var jittered = RetryDelay.Jitter(10.0);
            Assert.InRange(jittered, 8.0, 12.0);
        }

        Environment.SetEnvironmentVariable("GRAPH_RETRY_JITTER", "false");
        Assert.Equal(10.0, RetryDelay.Jitter(10.0));
    }

    [Theory]
    [InlineData(2, 0, 2)]
    [InlineData(2, 1, 4)]
    [InlineData(2, 2, 8)]
    [InlineData(2, 4, 32)]
    [InlineData(2, 5, 60)]   // capped
    [InlineData(2, 10, 60)]  // capped
    public void BackoffIsExponentialWithCap(double backoffBase, int attempt, double expected)
    {
        Assert.Equal(expected, RetryDelay.ComputeBackoff(backoffBase, attempt));
    }

    [Fact]
    public void RetryAfterDeltaSecondsIsParsed()
    {
        var response = new HttpResponseMessage((HttpStatusCode)429);
        response.Headers.Add("Retry-After", "17");
        Assert.Equal(17.0, GraphClient.ReadRetryAfterSeconds(response));
    }

    [Fact]
    public void RetryAfterAbsentIsNull()
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        Assert.Null(GraphClient.ReadRetryAfterSeconds(response));
    }

    [Fact]
    public void RetryableStatusesAre429And5xx()
    {
        Assert.True(GraphClient.IsRetryable((HttpStatusCode)429));
        Assert.True(GraphClient.IsRetryable(HttpStatusCode.InternalServerError));
        Assert.True(GraphClient.IsRetryable(HttpStatusCode.ServiceUnavailable));
        Assert.False(GraphClient.IsRetryable(HttpStatusCode.BadRequest));
        Assert.False(GraphClient.IsRetryable(HttpStatusCode.NotFound));
        Assert.False(GraphClient.IsRetryable(HttpStatusCode.OK));
    }
}

public class GraphTransportRetryTests
{
    private static GraphClient NewClient(ScriptedHandler handler, List<double> delays)
    {
        var config = TestFixtures.NewConfig();
        return new GraphClient(config, handler, (seconds, _) =>
        {
            delays.Add(seconds);
            return Task.CompletedTask;
        });
    }

    private static void EnqueueToken(ScriptedHandler handler) =>
        handler.EnqueueJson(200, """{"access_token":"tok","expires_in":3600}""");

    [Fact]
    public async Task RetryAfterHeaderIsHonouredExactly()
    {
        Environment.SetEnvironmentVariable("GRAPH_RETRY_JITTER", "true");  // must NOT affect Retry-After
        try
        {
            var delays = new List<double>();
            var handler = new ScriptedHandler();
            EnqueueToken(handler);
            handler.EnqueueJson(429, "{}", r => r.Headers.Add("Retry-After", "7"));
            handler.EnqueueJson(200, "{}");

            var client = NewClient(handler, delays);
            var item = new ExternalItem
            {
                Id = "x1",
                Acl = new[] { new AclEntry { Type = "user", Value = "u" } },
                Properties = new Dictionary<string, object?>(),
            };
            await client.PutItemAsync(item);

            Assert.Single(delays);
            Assert.Equal(7.0, delays[0]);  // exact — jitter never applied to Retry-After
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRAPH_RETRY_JITTER", null);
        }
    }

    [Fact]
    public async Task ComputedBackoffUsedWhenNoRetryAfter()
    {
        var delays = new List<double>();
        var handler = new ScriptedHandler();
        EnqueueToken(handler);
        handler.EnqueueJson(503, "{}");
        handler.EnqueueJson(200, "{}");

        var client = NewClient(handler, delays);
        await client.DeleteItemAsync("x1");

        Assert.Single(delays);
        // config backoff base 0.01 → attempt 1 → 0.01 * 2^1 = 0.02
        Assert.Equal(0.02, delays[0], 5);
    }

    [Fact]
    public async Task ExhaustedRetriesSurfaceTheFailure()
    {
        var delays = new List<double>();
        var handler = new ScriptedHandler();
        EnqueueToken(handler);
        handler.EnqueueJson(429, "{}");
        handler.EnqueueJson(429, "{}");
        handler.EnqueueJson(429, "{}");  // GraphMaxRetries=2 → 3 attempts total

        var client = NewClient(handler, delays);
        var exc = await Assert.ThrowsAsync<GraphClientException>(() => client.DeleteItemAsync("x1"));
        Assert.Equal(429, exc.StatusCode);
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public async Task NonRetryableStatusFailsImmediately()
    {
        var delays = new List<double>();
        var handler = new ScriptedHandler();
        EnqueueToken(handler);
        handler.EnqueueJson(400, """{"error":"bad"}""");

        var client = NewClient(handler, delays);
        var exc = await Assert.ThrowsAsync<GraphClientException>(() => client.DeleteItemAsync("x1"));
        Assert.Equal(400, exc.StatusCode);
        Assert.Empty(delays);
    }
}
