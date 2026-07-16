// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Graph;

namespace SalesforceCopilotConnector.Tests.TestGraph;

/// <summary>
/// Test double for <see cref="GraphClient"/> — the C# equivalent of the Python
/// tests' <c>MagicMock()</c> graph client.  Records every call and lets each
/// test configure canned responses (or thrown exceptions) per HTTP verb.
/// </summary>
public sealed class FakeGraphClient : GraphClient
{
    public sealed record RecordedCall(string Method, string Url, JsonNode? Body, Dictionary<string, string>? Headers);

    public List<RecordedCall> Calls { get; } = new();

    public List<RecordedCall> GetCalls => Calls.Where(c => c.Method == "GET").ToList();
    public List<RecordedCall> PostCalls => Calls.Where(c => c.Method == "POST").ToList();
    public List<RecordedCall> PatchCalls => Calls.Where(c => c.Method == "PATCH").ToList();
    public List<RecordedCall> PutCalls => Calls.Where(c => c.Method == "PUT").ToList();
    public List<RecordedCall> DeleteCalls => Calls.Where(c => c.Method == "DELETE").ToList();
    public List<RecordedCall> BatchCalls => Calls.Where(c => c.Method == "BATCH").ToList();

    // Per-verb behaviours — override in tests (like MagicMock return_value / side_effect).
    public Func<string, JsonNode?> OnGet { get; set; } = _ => new JsonObject();
    public Func<string, JsonNode?, JsonNode?> OnPost { get; set; } = (_, _) => new JsonObject();
    public Func<string, JsonNode?, JsonNode?> OnPatch { get; set; } = (_, _) => new JsonObject();
    public Func<string, JsonNode?, JsonNode?> OnPut { get; set; } = (_, _) => new JsonObject();
    public Func<string, JsonNode?> OnDelete { get; set; } = _ => new JsonObject();
    public Func<List<JsonObject>, List<JsonObject>> OnBatch { get; set; } = _ => new List<JsonObject>();
    public List<JsonObject> PaginateItems { get; set; } = new();

    public override Task<JsonNode?> GetAsync(string pathOrUrl, Dictionary<string, string>? headers = null)
    {
        Calls.Add(new RecordedCall("GET", pathOrUrl, null, headers));
        return Task.FromResult(OnGet(pathOrUrl));
    }

    public override Task<JsonNode?> PostAsync(
        string pathOrUrl, JsonNode? jsonBody = null, Dictionary<string, string>? headers = null)
    {
        Calls.Add(new RecordedCall("POST", pathOrUrl, jsonBody, headers));
        return Task.FromResult(OnPost(pathOrUrl, jsonBody));
    }

    public override Task<JsonNode?> PatchAsync(
        string pathOrUrl, JsonNode? jsonBody = null, Dictionary<string, string>? headers = null)
    {
        Calls.Add(new RecordedCall("PATCH", pathOrUrl, jsonBody, headers));
        return Task.FromResult(OnPatch(pathOrUrl, jsonBody));
    }

    public override Task<JsonNode?> PutAsync(
        string pathOrUrl, JsonNode? jsonBody = null, Dictionary<string, string>? headers = null)
    {
        Calls.Add(new RecordedCall("PUT", pathOrUrl, jsonBody, headers));
        return Task.FromResult(OnPut(pathOrUrl, jsonBody));
    }

    public override Task<JsonNode?> DeleteAsync(string pathOrUrl, Dictionary<string, string>? headers = null)
    {
        Calls.Add(new RecordedCall("DELETE", pathOrUrl, null, headers));
        return Task.FromResult(OnDelete(pathOrUrl));
    }

    public override Task<List<JsonObject>> BatchRequestsAsync(List<JsonObject> requestsPayload)
    {
        Calls.Add(new RecordedCall("BATCH", "/$batch", null, null));
        return Task.FromResult(OnBatch(requestsPayload));
    }

    public override async IAsyncEnumerable<JsonObject> PaginateAsync(string pathOrUrl)
    {
        Calls.Add(new RecordedCall("PAGINATE", pathOrUrl, null, null));
        await Task.CompletedTask;
        foreach (var item in PaginateItems)
            yield return item;
    }

    /// <summary>Equivalent of Python's <c>graph_client.reset_mock()</c>.</summary>
    public void ResetMock() => Calls.Clear();
}
