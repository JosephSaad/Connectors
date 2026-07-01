// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for GraphClient (graph.client) — port of tests/test_graph/test_graph_client.py.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using SalesforceCopilotConnector.Graph;

namespace SalesforceCopilotConnector.Tests.TestGraph;

public class GraphClientTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Recording HttpMessageHandler — replaces Python's mocked ``client._session``.</summary>
    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        internal sealed record Recorded(string Method, string Url, string? Body);

        public List<Recorded> Requests { get; } = new();
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();

        /// <summary>Fallback factory used when the queue is empty (like MagicMock return_value).</summary>
        public Func<HttpResponseMessage>? Default { get; set; }

        public void Enqueue(Func<HttpResponseMessage> responseFactory) => _responses.Enqueue(responseFactory);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Recorded(request.Method.Method, request.RequestUri!.ToString(), body));
            var factory = _responses.Count > 0
                ? _responses.Dequeue()
                : Default ?? throw new InvalidOperationException("No response configured");
            var response = factory();
            response.RequestMessage = request;
            return response;
        }
    }

    private static HttpResponseMessage MakeResponse(
        int statusCode = 200,
        JsonNode? jsonBody = null,
        string text = "",
        Dictionary<string, string>? headers = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)statusCode);
        var content = !string.IsNullOrEmpty(text) ? text : (jsonBody ?? new JsonObject()).ToJsonString();
        var mediaType = !string.IsNullOrEmpty(text) ? "text/plain" : "application/json";
        response.Content = new StringContent(content, Encoding.UTF8, mediaType);
        if (headers != null)
        {
            foreach (var (key, value) in headers)
                response.Headers.TryAddWithoutValidation(key, value);
        }
        return response;
    }

    /// <summary>Port of the ``client`` fixture — fake token + fake HTTP session.</summary>
    private static (GraphClient Client, FakeHttpHandler Handler) MakeClient()
    {
        var handler = new FakeHttpHandler();
        var client = new GraphClient(maxRetries: 2, delaySeconds: 0, retryBackoffBase: 0);
        client._http = new HttpClient(handler);
        // Token valid for 1 hour — GetHeadersAsync never calls DefaultAzureCredential.
        client._token = new AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1));
        return (client, handler);
    }

    // ── Basic HTTP methods ───────────────────────────────────────────────────

    [Fact]
    public async Task GetMakesGetRequest()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(() => MakeResponse(jsonBody: new JsonObject { ["value"] = 1 }));
        var result = await client.GetAsync("/test");
        Assert.Single(handler.Requests);
        Assert.Equal("GET", handler.Requests[0].Method);
        Assert.True(JsonNode.DeepEquals(result, new JsonObject { ["value"] = 1 }));
    }

    [Fact]
    public async Task PostSendsJsonBody()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(() => MakeResponse(jsonBody: new JsonObject { ["id"] = "abc" }));
        var body = new JsonObject { ["name"] = "test" };
        await client.PostAsync("/test", jsonBody: body);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(handler.Requests[0].Body!), body));
    }

    [Fact]
    public async Task PutSendsJsonBody()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(() => MakeResponse(jsonBody: new JsonObject()));
        var body = new JsonObject { ["data"] = 42 };
        await client.PutAsync("/items/1", jsonBody: body);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(handler.Requests[0].Body!), body));
    }

    [Fact]
    public async Task DeleteSendsDeleteRequest()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(() => MakeResponse(jsonBody: new JsonObject()));
        await client.DeleteAsync("/items/1");
        Assert.Equal("DELETE", handler.Requests[0].Method);
    }

    // ── Error handling ───────────────────────────────────────────────────────

    [Fact]
    public async Task RaisesGraphApiErrorOn404()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(() => MakeResponse(
            statusCode: 404,
            jsonBody: new JsonObject
            {
                ["error"] = new JsonObject { ["code"] = "NotFound", ["message"] = "Not found" },
            }));
        var error = await Assert.ThrowsAsync<GraphApiError>(() => client.GetAsync("/missing"));
        Assert.Equal(404, error.StatusCode);
        Assert.Equal("NotFound", error.Code);
    }

    /// <summary>Verify retry on 429/500/502/503/504, then success.</summary>
    [Fact]
    public async Task RetriesOnRetryableStatusCodes()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(() => MakeResponse(
            statusCode: 429,
            jsonBody: new JsonObject { ["error"] = new JsonObject { ["message"] = "throttled" } }));
        handler.Enqueue(() => MakeResponse(jsonBody: new JsonObject { ["ok"] = true }));
        var result = await client.GetAsync("/throttled");
        Assert.True(JsonNode.DeepEquals(result, new JsonObject { ["ok"] = true }));
        Assert.Equal(2, handler.Requests.Count);
    }

    /// <summary>After max_retries, should raise GraphApiError.</summary>
    [Fact]
    public async Task RetriesExhaustedRaises()
    {
        var (client, handler) = MakeClient();
        handler.Default = () => MakeResponse(
            statusCode: 500,
            jsonBody: new JsonObject { ["error"] = new JsonObject { ["message"] = "server error" } });
        var error = await Assert.ThrowsAsync<GraphApiError>(() => client.GetAsync("/fail"));
        Assert.Equal(500, error.StatusCode);
        // 1 initial + 2 retries = 3
        Assert.Equal(3, handler.Requests.Count);
    }

    /// <summary>Long-running operation: follows Location header.</summary>
    [Fact]
    public async Task FollowsLocationHeader()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(() => MakeResponse(
            statusCode: 202,
            jsonBody: new JsonObject(),
            headers: new Dictionary<string, string>
            {
                ["Location"] = "https://graph.microsoft.com/v1.0/operations/123",
            }));
        handler.Enqueue(() => MakeResponse(jsonBody: new JsonObject { ["status"] = "completed" }));
        await client.RequestAsync("PATCH", "/schema");
        Assert.Equal(2, handler.Requests.Count);
    }

    // ── Pagination ───────────────────────────────────────────────────────────

    [Fact]
    public async Task PaginateFollowsNextLink()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(() => MakeResponse(jsonBody: new JsonObject
        {
            ["value"] = new JsonArray(
                new JsonObject { ["id"] = "1" },
                new JsonObject { ["id"] = "2" }),
            ["@odata.nextLink"] = "https://graph.microsoft.com/v1.0/next",
        }));
        handler.Enqueue(() => MakeResponse(jsonBody: new JsonObject
        {
            ["value"] = new JsonArray(new JsonObject { ["id"] = "3" }),
        }));
        var items = new List<JsonObject>();
        await foreach (var item in client.PaginateAsync("/items"))
            items.Add(item);
        Assert.Equal(new[] { "1", "2", "3" }, items.Select(i => i["id"]!.GetValue<string>()).ToArray());
    }

    // ── URL normalization ────────────────────────────────────────────────────

    [Fact]
    public void NormalizeUrlPrependsBaseForRelative()
    {
        var (client, _) = MakeClient();
        Assert.Equal($"{GraphClient.GraphBaseUrl}/v1.0/test", client.NormalizeUrl("/test"));
    }

    [Fact]
    public void NormalizeUrlPassesThroughAbsolute()
    {
        var (client, _) = MakeClient();
        var url = "https://example.com/path";
        Assert.Equal(url, client.NormalizeUrl(url));
    }

    // ── GraphApiError.FromResponseAsync ──────────────────────────────────────

    [Fact]
    public async Task FromResponseParsesErrorJson()
    {
        var response = MakeResponse(
            statusCode: 403,
            jsonBody: new JsonObject
            {
                ["error"] = new JsonObject { ["code"] = "AccessDenied", ["message"] = "No access" },
            });
        var error = await GraphApiError.FromResponseAsync(response);
        Assert.Equal(403, error.StatusCode);
        Assert.Equal("AccessDenied", error.Code);
        Assert.Contains("No access", error.Message);
    }

    [Fact]
    public async Task FromResponseHandlesNonJson()
    {
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Internal Server Error", Encoding.UTF8, "text/plain"),
            ReasonPhrase = "ISE",
        };
        var error = await GraphApiError.FromResponseAsync(response);
        Assert.Equal(500, error.StatusCode);
        Assert.Equal("Internal Server Error", error.Body);
    }
}
