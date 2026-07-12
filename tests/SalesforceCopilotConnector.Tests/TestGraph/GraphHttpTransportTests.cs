// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// HTTP transport tests for GraphClient. Every other Graph test (and the
// stress harness) swaps a fake at a seam ABOVE the transport, so the layer
// where production incidents actually live — real HttpClient sends, auth
// headers on the wire, Retry-After parsing, $batch request shape, nextLink
// pagination — previously never executed. These tests point the REAL client
// at a loopback HttpListener via GRAPH_BASE_URL (the sovereign-cloud
// override) and assert on what actually crossed the wire.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using SalesforceCopilotConnector.Graph;

namespace SalesforceCopilotConnector.Tests.TestGraph;

[Collection("EnvVars")]
public class GraphHttpTransportTests
{
    private sealed record RecordedRequest(string Method, string PathAndQuery, string? Authorization, string Body);

    /// <summary>
    /// Minimal scriptable Graph endpoint on a loopback port. The Nth request
    /// gets <c>Script(N)</c>'s response; every request is recorded verbatim.
    /// </summary>
    private sealed class FakeGraphServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly object _lock = new();
        private int _count;

        public List<RecordedRequest> Requests { get; } = new();
        public Func<int, (int Status, string Body, Dictionary<string, string>? Headers)> Script { get; set; }
            = _ => (200, "{}", null);
        public string BaseUrl { get; }

        public FakeGraphServer()
        {
            // HttpListener cannot bind port 0; reserve a free one first.
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            BaseUrl = $"http://127.0.0.1:{port}";
            _listener = new HttpListener();
            _listener.Prefixes.Add($"{BaseUrl}/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch { return; }  // listener disposed — test is over

                string body;
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    body = await reader.ReadToEndAsync();

                int n;
                lock (_lock)
                {
                    Requests.Add(new RecordedRequest(
                        context.Request.HttpMethod,
                        context.Request.Url!.PathAndQuery,
                        context.Request.Headers["Authorization"],
                        body));
                    n = _count++;
                }

                var (status, responseBody, headers) = Script(n);
                context.Response.StatusCode = status;
                if (headers != null)
                    foreach (var (key, value) in headers)
                        context.Response.Headers[key] = value;
                context.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(responseBody);
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); _listener.Close(); } catch { /* already down */ }
        }
    }

    /// <summary>
    /// Real GraphClient aimed at the fake server, with a preset far-future token
    /// (the documented seam — mirrors the Python tests patching get_token) so no
    /// AAD endpoint is contacted.
    /// </summary>
    private static GraphClient NewClient(int maxRetries = 2, int retryBackoffBase = 4)
    {
        var client = new GraphClient(maxRetries: maxRetries, retryBackoffBase: retryBackoffBase);
        client._token = new AccessToken("fake-transport-token", DateTimeOffset.UtcNow.AddHours(1));
        return client;
    }

    // ── endpoint override semantics (pure env) ─────────────────────────────────

    [Fact]
    public void GraphEndpointDefaultsMatchUpstreamAndOverridesCompose()
    {
        using (new EnvVarScope(("GRAPH_BASE_URL", null), ("GRAPH_SCOPE", null)))
        {
            Assert.Equal("https://graph.microsoft.com", GraphClient.GraphBaseUrl);
            Assert.Equal("https://graph.microsoft.com/.default", GraphClient.GraphScope);
        }
        using (new EnvVarScope(("GRAPH_BASE_URL", "https://graph.microsoft.us/"), ("GRAPH_SCOPE", null)))
        {
            // Trailing slash normalized; scope follows the base URL's audience.
            Assert.Equal("https://graph.microsoft.us", GraphClient.GraphBaseUrl);
            Assert.Equal("https://graph.microsoft.us/.default", GraphClient.GraphScope);
        }
        using (new EnvVarScope(("GRAPH_BASE_URL", "https://graph.microsoft.us"),
                   ("GRAPH_SCOPE", "https://custom.audience/.default")))
        {
            Assert.Equal("https://custom.audience/.default", GraphClient.GraphScope);
        }
    }

    // ── wire shape ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task PutSendsBearerTokenAndJsonBodyOverTheWire()
    {
        using var server = new FakeGraphServer();
        using var scope = new EnvVarScope(("GRAPH_BASE_URL", server.BaseUrl), ("GRAPH_RETRY_JITTER", null));
        var client = NewClient();

        var payload = new JsonObject { ["acl"] = new JsonArray(), ["content"] = "hello" };
        var result = await client.PutAsync("/external/connections/conn1/items/item-1", payload);

        Assert.NotNull(result);
        var request = Assert.Single(server.Requests);
        Assert.Equal("PUT", request.Method);
        Assert.Equal("/v1.0/external/connections/conn1/items/item-1", request.PathAndQuery);
        Assert.Equal("Bearer fake-transport-token", request.Authorization);
        Assert.Equal("hello", JsonNode.Parse(request.Body)!["content"]!.GetValue<string>());
    }

    // ── retry semantics on the wire ────────────────────────────────────────────

    [Fact]
    public async Task RetryAfterHeaderIsHonoredOverComputedBackoff()
    {
        using var server = new FakeGraphServer();
        using var scope = new EnvVarScope(("GRAPH_BASE_URL", server.BaseUrl), ("GRAPH_RETRY_JITTER", null));
        // Computed backoff would be 4s (base 4, attempt 0); Retry-After says 1s.
        var client = NewClient(maxRetries: 2, retryBackoffBase: 4);
        server.Script = n => n == 0
            ? (429, "{}", new Dictionary<string, string> { ["Retry-After"] = "1" })
            : (200, "{\"ok\":true}", null);

        var stopwatch = Stopwatch.StartNew();
        var result = await client.GetAsync("/external/connections/conn1");
        stopwatch.Stop();

        Assert.Equal(2, server.Requests.Count);
        Assert.True((result as JsonObject)?["ok"]?.GetValue<bool>());
        // Waited the server-provided 1s — not the 4s computed ladder.
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(0.9),
            $"retried after only {stopwatch.Elapsed.TotalSeconds:F2}s — Retry-After was not awaited");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3.5),
            $"took {stopwatch.Elapsed.TotalSeconds:F2}s — computed backoff used instead of Retry-After");
    }

    [Fact]
    public async Task BadRequestSurfacesGraphApiErrorWithoutRetrying()
    {
        using var server = new FakeGraphServer();
        using var scope = new EnvVarScope(("GRAPH_BASE_URL", server.BaseUrl), ("GRAPH_RETRY_JITTER", null));
        var client = NewClient();
        server.Script = _ => (400, "{\"error\":{\"code\":\"invalidRequest\",\"message\":\"bad item\"}}", null);

        await Assert.ThrowsAsync<GraphApiError>(
            () => client.PostAsync("/external/connections", new JsonObject()));
        Assert.Single(server.Requests);  // 400 is permanent — one attempt only
    }

    [Fact]
    public async Task TransientErrorsRetryUntilExhaustionThenThrow()
    {
        using var server = new FakeGraphServer();
        using var scope = new EnvVarScope(("GRAPH_BASE_URL", server.BaseUrl), ("GRAPH_RETRY_JITTER", null));
        var client = NewClient(maxRetries: 2, retryBackoffBase: 4);
        // Always 503, but with an instant Retry-After so the test doesn't sleep
        // through the computed ladder.
        server.Script = _ => (503, "{}", new Dictionary<string, string> { ["Retry-After"] = "0" });

        await Assert.ThrowsAsync<GraphApiError>(() => client.GetAsync("/external/connections/conn1"));
        Assert.Equal(3, server.Requests.Count);  // initial + maxRetries
    }

    // ── pagination over the wire ───────────────────────────────────────────────

    [Fact]
    public async Task PaginateFollowsAbsoluteNextLinks()
    {
        using var server = new FakeGraphServer();
        using var scope = new EnvVarScope(("GRAPH_BASE_URL", server.BaseUrl), ("GRAPH_RETRY_JITTER", null));
        var client = NewClient();
        server.Script = n => n == 0
            ? (200, $"{{\"value\":[{{\"id\":\"a\"}},{{\"id\":\"b\"}}],\"@odata.nextLink\":\"{server.BaseUrl}/v1.0/things?$skiptoken=page2\"}}", null)
            : (200, "{\"value\":[{\"id\":\"c\"}]}", null);

        var items = new List<string>();
        await foreach (var item in client.PaginateAsync("/things"))
            items.Add(item["id"]!.GetValue<string>());

        Assert.Equal(new[] { "a", "b", "c" }, items);
        Assert.Equal(2, server.Requests.Count);
        Assert.Contains("skiptoken=page2", server.Requests[1].PathAndQuery);
    }

    // ── $batch over the wire ───────────────────────────────────────────────────

    [Fact]
    public async Task BatchPostsSingleBatchEnvelopeAndMapsPerItemResponses()
    {
        using var server = new FakeGraphServer();
        using var scope = new EnvVarScope(("GRAPH_BASE_URL", server.BaseUrl), ("GRAPH_RETRY_JITTER", null));
        var client = NewClient();
        server.Script = _ => (200,
            "{\"responses\":[" +
            "{\"id\":\"1\",\"status\":200,\"body\":{}}," +
            "{\"id\":\"2\",\"status\":200,\"body\":{}}," +
            "{\"id\":\"3\",\"status\":429,\"headers\":{\"Retry-After\":\"0\"},\"body\":{}}]}",
            null);

        var requests = new List<JsonObject>();
        for (var i = 1; i <= 3; i++)
        {
            requests.Add(new JsonObject
            {
                ["id"] = i.ToString(),
                ["method"] = "PUT",
                ["url"] = $"/external/connections/conn1/items/item-{i}",
                ["headers"] = new JsonObject { ["Content-Type"] = "application/json" },
                ["body"] = new JsonObject { ["content"] = $"payload-{i}" },
            });
        }

        var responses = await client.BatchRequestsAsync(requests);

        var request = Assert.Single(server.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/v1.0/$batch", request.PathAndQuery);
        var envelope = (JsonObject)JsonNode.Parse(request.Body)!;
        var sent = (JsonArray)envelope["requests"]!;
        Assert.Equal(3, sent.Count);
        Assert.Equal("/external/connections/conn1/items/item-2", sent[1]!["url"]!.GetValue<string>());

        Assert.Equal(3, responses.Count);
        Assert.Equal(new[] { "1", "2", "3" }, responses.Select(r => r["id"]!.GetValue<string>()));
        Assert.Equal(429, responses[2]["status"]!.GetValue<int>());
    }
}
