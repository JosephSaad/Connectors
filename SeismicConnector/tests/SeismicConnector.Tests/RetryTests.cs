// Retry/backoff math: jitter helper + the GraphClient delay policy
// (Retry-After honoured exactly, computed backoff jittered, 60s cap) and the
// end-to-end retry loop against a mock HTTP handler.

using System.Net;
using SeismicConnector.Graph;

namespace SeismicConnector.Tests;

public class RetryDelayTests : IDisposable
{
    public RetryDelayTests() => Environment.SetEnvironmentVariable("GRAPH_RETRY_JITTER", null);

    public void Dispose() => Environment.SetEnvironmentVariable("GRAPH_RETRY_JITTER", null);

    [Fact]
    public void JitterDisabled_IsIdentity()
    {
        Assert.Equal(8.0, RetryDelay.ApplyJitter(8.0, jitterEnabled: false, uniformSample: 0.0));
        Assert.Equal(8.0, RetryDelay.ApplyJitter(8.0, jitterEnabled: false, uniformSample: 0.999));
    }

    [Theory]
    [InlineData(10.0, 0.0, 8.0)]    // lower bound: delay * 0.8
    [InlineData(10.0, 0.5, 10.0)]   // midpoint: unchanged
    [InlineData(10.0, 1.0, 12.0)]   // upper bound: delay * 1.2
    public void JitterEnabled_MapsSampleToPlusMinus20Percent(double delay, double sample, double expected)
    {
        Assert.Equal(expected, RetryDelay.ApplyJitter(delay, jitterEnabled: true, uniformSample: sample), 10);
    }

    [Fact]
    public void Jitter_ReadsEnvVar()
    {
        Environment.SetEnvironmentVariable("GRAPH_RETRY_JITTER", "false");
        Assert.Equal(5.0, RetryDelay.Jitter(5.0));

        Environment.SetEnvironmentVariable("GRAPH_RETRY_JITTER", "true");
        for (var i = 0; i < 50; i++)
        {
            var jittered = RetryDelay.Jitter(10.0);
            Assert.InRange(jittered, 8.0, 12.0);
        }
    }
}

public class GraphDelayPolicyTests : IDisposable
{
    public GraphDelayPolicyTests() => Environment.SetEnvironmentVariable("GRAPH_RETRY_JITTER", null);

    public void Dispose() => Environment.SetEnvironmentVariable("GRAPH_RETRY_JITTER", null);

    private static GraphClient Client() => new(TestConfig.Build().Graph, new FakeHttpHandler())
    {
        OverrideAccessToken = "token",
    };

    [Fact]
    public void RetryAfter_IsHonouredExactly_EvenWithJitterEnabled()
    {
        Environment.SetEnvironmentVariable("GRAPH_RETRY_JITTER", "true");
        var client = Client();
        // Server said 17s → exactly 17s, never jittered.
        for (var i = 0; i < 20; i++)
            Assert.Equal(17.0, client.NextDelaySeconds(attempt: 0, retryAfterHeader: "17"));
    }

    [Fact]
    public void RetryAfter_IsCappedAt60Seconds()
    {
        var client = Client();
        Assert.Equal(60.0, client.NextDelaySeconds(attempt: 0, retryAfterHeader: "3600"));
    }

    [Fact]
    public void ComputedBackoff_IsExponential()
    {
        var client = Client();  // base 2
        Assert.Equal(2.0, client.NextDelaySeconds(0, null));
        Assert.Equal(4.0, client.NextDelaySeconds(1, null));
        Assert.Equal(8.0, client.NextDelaySeconds(2, null));
        Assert.Equal(16.0, client.NextDelaySeconds(3, null));
    }

    [Fact]
    public void ComputedBackoff_IsCappedAt60Seconds()
    {
        var client = Client();
        Assert.Equal(60.0, client.NextDelaySeconds(10, null));
    }

    [Fact]
    public void ComputedBackoff_JitterAppliesWhenEnabled()
    {
        Environment.SetEnvironmentVariable("GRAPH_RETRY_JITTER", "true");
        var client = Client();
        for (var i = 0; i < 50; i++)
        {
            var delay = client.NextDelaySeconds(2, null);  // computed 8s → [6.4, 9.6]
            Assert.InRange(delay, 6.4, 9.6);
        }
    }

    [Fact]
    public void UnparseableRetryAfter_FallsBackToComputedBackoff()
    {
        var client = Client();
        // HTTP-date style Retry-After is not a number → computed backoff.
        Assert.Equal(4.0, client.NextDelaySeconds(1, "Wed, 21 Oct 2026 07:28:00 GMT"));
    }
}

public class GraphRetryLoopTests
{
    [Fact]
    public async Task Throttled429_RetriesAndUsesServerRetryAfter()
    {
        var handler = new FakeHttpHandler();
        var calls = 0;
        handler.When(HttpMethod.Get, "/external/connections", (request, _) =>
        {
            calls++;
            if (calls < 3)
            {
                var response = FakeHttpHandler.Json((HttpStatusCode)429, """{"error":{"message":"throttled"}}""");
                response.Headers.Add("Retry-After", "7");
                return response;
            }
            return FakeHttpHandler.Json(HttpStatusCode.OK, """{"state":"ready"}""");
        });

        var client = new GraphClient(TestConfig.Build().Graph, handler)
        {
            OverrideAccessToken = "token",
            DelayAsync = (_, _) => Task.CompletedTask,  // don't really sleep
        };

        var result = await client.GetAsync("/external/connections/X");
        Assert.Equal("ready", result?["state"]?.GetValue<string>());
        Assert.Equal(3, calls);
        Assert.Equal(new[] { 7.0, 7.0 }, client.ObservedDelaysSeconds);  // exact Retry-After, twice
    }

    [Fact]
    public async Task ServerError_ExhaustsRetries_ThrowsGraphApiError()
    {
        var handler = new FakeHttpHandler();
        var calls = 0;
        handler.When(HttpMethod.Get, "/external", (_, _) =>
        {
            calls++;
            return FakeHttpHandler.Json(HttpStatusCode.InternalServerError, """{"error":{"message":"boom"}}""");
        });

        var client = new GraphClient(TestConfig.Build().Graph, handler)
        {
            OverrideAccessToken = "token",
            DelayAsync = (_, _) => Task.CompletedTask,
        };

        var error = await Assert.ThrowsAsync<GraphApiError>(() => client.GetAsync("/external/x"));
        Assert.Equal(500, error.StatusCode);
        Assert.Equal(4, calls);  // 1 initial + MaxRetries(3)
        // Without a Retry-After header the waits follow the computed ladder 2,4,8.
        Assert.Equal(new[] { 2.0, 4.0, 8.0 }, client.ObservedDelaysSeconds);
    }

    [Fact]
    public async Task ClientError_IsNotRetried()
    {
        var handler = new FakeHttpHandler();
        var calls = 0;
        handler.When(HttpMethod.Get, "/external", (_, _) =>
        {
            calls++;
            return FakeHttpHandler.Json(HttpStatusCode.BadRequest, """{"error":{"message":"bad"}}""");
        });

        var client = new GraphClient(TestConfig.Build().Graph, handler)
        {
            OverrideAccessToken = "token",
            DelayAsync = (_, _) => Task.CompletedTask,
        };

        var error = await Assert.ThrowsAsync<GraphApiError>(() => client.GetAsync("/external/x"));
        Assert.Equal(400, error.StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Batch_PerItemFailures_AreSurfacedIndividually()
    {
        var handler = new FakeHttpHandler();
        handler.When(HttpMethod.Post, "/$batch", (_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, """
            {"responses":[
                {"id":"a","status":200},
                {"id":"b","status":429,"body":{"error":{"message":"throttled item"}}}
            ]}
            """));

        var client = new GraphClient(TestConfig.Build().Graph, handler)
        {
            OverrideAccessToken = "token",
        };
        var items = new List<(string, System.Text.Json.Nodes.JsonNode)>
        {
            ("a", new System.Text.Json.Nodes.JsonObject { ["id"] = "a" }),
            ("b", new System.Text.Json.Nodes.JsonObject { ["id"] = "b" }),
        };

        var results = await client.PutExternalItemsBatchAsync("Conn", items);
        Assert.Equal(2, results.Count);
        Assert.True(results.Single(r => r.ItemId == "a").Success);
        var failed = results.Single(r => r.ItemId == "b");
        Assert.False(failed.Success);
        Assert.Equal(429, failed.Status);
        Assert.Equal("throttled item", failed.Error);
    }

    [Fact]
    public async Task Batch_MissingResponses_AreFailures()
    {
        var handler = new FakeHttpHandler();
        handler.When(HttpMethod.Post, "/$batch", (_, _) => FakeHttpHandler.Json(
            HttpStatusCode.OK, """{"responses":[{"id":"a","status":200}]}"""));

        var client = new GraphClient(TestConfig.Build().Graph, handler) { OverrideAccessToken = "token" };
        var items = new List<(string, System.Text.Json.Nodes.JsonNode)>
        {
            ("a", new System.Text.Json.Nodes.JsonObject()),
            ("ghost", new System.Text.Json.Nodes.JsonObject()),
        };

        var results = await client.PutExternalItemsBatchAsync("Conn", items);
        Assert.True(results.Single(r => r.ItemId == "a").Success);
        Assert.False(results.Single(r => r.ItemId == "ghost").Success);
    }

    [Fact]
    public async Task Delete_Treats404AsSuccess()
    {
        var handler = new FakeHttpHandler();
        handler.When(HttpMethod.Delete, "/items/", (_, _) =>
            FakeHttpHandler.Json(HttpStatusCode.NotFound, """{"error":{"message":"gone"}}"""));

        var client = new GraphClient(TestConfig.Build().Graph, handler) { OverrideAccessToken = "token" };
        Assert.True(await client.DeleteExternalItemAsync("Conn", "item-1"));
    }
}
