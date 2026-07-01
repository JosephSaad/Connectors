// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Microsoft Graph API client with retry and pagination support.
//
// This module provides GraphClient, a thin wrapper around the Microsoft
// Graph REST API that handles:
//
// * Authentication — acquires tokens via DefaultAzureCredential (supports
//   Azure CLI, managed identity, environment variables, etc.).
// * Retry with exponential back-off — automatically retries on transient HTTP
//   errors (429, 500, 502, 503, 504) up to a configurable number of attempts.
// * Long-running operations — follows "Location" headers returned by
//   asynchronous Graph operations (e.g. schema provisioning) and polls until the
//   operation completes.
// * Pagination — the GraphClient.PaginateAsync iterator transparently
//   follows "@odata.nextLink" across paged result sets.
//
// Constants
// ---------
// GraphBaseUrl : "https://graph.microsoft.com"
// ExternalConnectionsPath : "/external/connections" — base path for the External Connectors API.
//
// Classes
// -------
// GraphApiError
//     Raised when the Graph API returns a non-success status that is not retryable
//     (or retries have been exhausted).  Carries StatusCode, Code, and
//     the raw Body.
// GraphClient
//     Stateful HTTP client.  Instantiate once per command and reuse for all Graph
//     calls within that run.

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Graph;

public class GraphApiError : Exception
{
    public int StatusCode { get; }
    public string? Code { get; }
    public object? Body { get; }

    /// <summary>Initialize with HTTP status code, message, optional error code and response body.</summary>
    public GraphApiError(int statusCode, string message, string? code = null, object? body = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        Body = body;
    }

    /// <summary>Create a GraphApiError from an HTTP response, extracting error details from JSON body.</summary>
    public static async Task<GraphApiError> FromResponseAsync(HttpResponseMessage response)
    {
        string? code = null;
        var text = await response.Content.ReadAsStringAsync();
        object? body = text;
        var message = !string.IsNullOrEmpty(text) ? text : (response.ReasonPhrase ?? "");

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return new GraphApiError((int)response.StatusCode, message, body: body);
        }
        body = parsed;

        if (parsed is JsonObject dict)
        {
            var error = dict["error"] as JsonObject ?? new JsonObject();
            code = error["code"]?.GetValue<string>();
            var errorMessage = error["message"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(errorMessage))
            {
                message = errorMessage;
            }
        }

        return new GraphApiError((int)response.StatusCode, message, code: code, body: body);
    }
}

public class GraphClient
{
    public const string GraphScope = "https://graph.microsoft.com/.default";
    public const string GraphBaseUrl = "https://graph.microsoft.com";
    public const string GraphDefaultApiVersion = "v1.0";
    public const string ExternalConnectionsPath = "/external/connections";

    // Hard limit imposed by the Graph API — do not exceed.
    // See: https://learn.microsoft.com/en-us/graph/json-batching#batch-size-limitations
    public const int GraphBatchMaxSize = 20;

    // Transient HTTP status codes that should be retried
    private static readonly HashSet<int> RetryableStatusCodes = new() { 429, 500, 502, 503, 504 };

    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector");

    // Note: the Python version suppresses noisy Azure Identity token-acquisition
    // logs via logging.getLogger("azure.identity").setLevel(logging.WARNING);
    // Azure.Identity for .NET does not emit through our logger, so no equivalent is needed.

    private readonly DefaultAzureCredential _credential;
    // internal (not private): tests preset a token the way the Python tests
    // patch DefaultAzureCredential.get_token (fixture ``client``).
    internal AccessToken? _token;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    // HttpClient is thread-safe; a single shared instance replaces Python's
    // thread-local requests.Session (which is not thread-safe).
    // Settable so tests can substitute a fake HttpMessageHandler
    // (Python tests replace ``client._session`` with a MagicMock).
    internal HttpClient _http;
    private readonly string _baseUrl;
    private readonly int _delaySeconds;
    private readonly int _maxRetries;
    private readonly int _retryBackoffBase;

    /// <summary>Initialize the Graph client with authentication, retry, and polling settings.</summary>
    public GraphClient(
        string apiVersion = GraphDefaultApiVersion,
        int delaySeconds = 60,
        int maxRetries = 4,
        int retryBackoffBase = 2)
    {
        _credential = new DefaultAzureCredential();
        _token = null;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        _baseUrl = $"{GraphBaseUrl}/{apiVersion}";
        _delaySeconds = delaySeconds;
        _maxRetries = maxRetries;
        _retryBackoffBase = retryBackoffBase;
    }

    // Note: the HTTP verb methods, BatchRequestsAsync, and PaginateAsync are
    // virtual so tests can substitute a fake client (Python tests use MagicMock).

    /// <summary>Send a GET request to the Graph API.</summary>
    public virtual Task<JsonNode?> GetAsync(string pathOrUrl, Dictionary<string, string>? headers = null)
    {
        return RequestAsync("GET", pathOrUrl, headers: headers);
    }

    /// <summary>Send a POST request to the Graph API.</summary>
    public virtual Task<JsonNode?> PostAsync(
        string pathOrUrl,
        JsonNode? jsonBody = null,
        Dictionary<string, string>? headers = null)
    {
        return RequestAsync("POST", pathOrUrl, jsonBody: jsonBody, headers: headers);
    }

    /// <summary>Send a PATCH request to the Graph API.</summary>
    public virtual Task<JsonNode?> PatchAsync(
        string pathOrUrl,
        JsonNode? jsonBody = null,
        Dictionary<string, string>? headers = null)
    {
        return RequestAsync("PATCH", pathOrUrl, jsonBody: jsonBody, headers: headers);
    }

    /// <summary>Send a PUT request to the Graph API.</summary>
    public virtual Task<JsonNode?> PutAsync(
        string pathOrUrl,
        JsonNode? jsonBody = null,
        Dictionary<string, string>? headers = null)
    {
        return RequestAsync("PUT", pathOrUrl, jsonBody: jsonBody, headers: headers);
    }

    /// <summary>Send a DELETE request to the Graph API.</summary>
    public virtual Task<JsonNode?> DeleteAsync(string pathOrUrl, Dictionary<string, string>? headers = null)
    {
        return RequestAsync("DELETE", pathOrUrl, headers: headers);
    }

    /// <summary>
    /// Send up to <see cref="GraphBatchMaxSize"/> (20) requests in a single <c>POST /$batch</c> call.
    ///
    /// Each element in <paramref name="requestsPayload"/> must be a JsonObject with:
    ///     <c>id</c>      — unique string identifier within this batch
    ///     <c>method</c>  — HTTP verb (e.g. "PUT", "DELETE")
    ///     <c>url</c>     — relative path without the API version prefix
    ///                      (e.g. /external/connections/{id}/items/{itemId})
    ///     <c>headers</c> — (optional) dict of extra headers; required when <c>body</c> is set
    ///     <c>body</c>    — (optional) JSON-serialisable request body
    ///
    /// Returns a list of response objects, each with <c>id</c>, <c>status</c>, <c>headers</c>,
    /// and <c>body</c> fields.  The outer <c>POST /$batch</c> is retried on transient
    /// errors using the standard retry logic; individual per-item failures (non-2xx
    /// <c>status</c>) are returned as-is for the caller to handle.
    ///
    /// Throws <see cref="ArgumentException"/> if more than <see cref="GraphBatchMaxSize"/> requests are passed.
    /// </summary>
    public virtual async Task<List<JsonObject>> BatchRequestsAsync(List<JsonObject> requestsPayload)
    {
        if (requestsPayload.Count > GraphBatchMaxSize)
        {
            throw new ArgumentException(
                $"Batch size {requestsPayload.Count} exceeds the Graph API maximum of {GraphBatchMaxSize}. " +
                "Split the payload into smaller chunks before calling batch_requests().");
        }

        var requestsArray = new JsonArray();
        foreach (var request in requestsPayload)
        {
            requestsArray.Add(request);
        }

        var response = await PostAsync("/$batch", jsonBody: new JsonObject { ["requests"] = requestsArray });
        if (response is JsonObject dict && dict["responses"] is JsonArray responses)
        {
            return responses.OfType<JsonObject>().ToList();
        }
        return new List<JsonObject>();
    }

    /// <summary>Iterate over all pages of a Graph API collection, yielding each item object.</summary>
    public virtual async IAsyncEnumerable<JsonObject> PaginateAsync(string pathOrUrl)
    {
        string? nextUrl = pathOrUrl;
        while (nextUrl != null)
        {
            var payload = await GetAsync(nextUrl);
            if (payload is not JsonObject page)
            {
                yield break;
            }
            if (page["value"] is JsonArray values)
            {
                foreach (var value in values)
                {
                    if (value is JsonObject item)
                    {
                        yield return item;
                    }
                }
            }
            nextUrl = page["@odata.nextLink"]?.GetValue<string>();
        }
    }

    /// <summary>
    /// Execute an HTTP request with retry, long-running operation polling, and error handling.
    ///
    /// Args:
    ///     method: HTTP method (GET, POST, PATCH, PUT, DELETE).
    ///     pathOrUrl: Relative path or absolute URL.
    ///     jsonBody: Optional JSON-serializable request body.
    ///     headers: Optional extra headers merged with auth headers.
    ///
    /// Returns:
    ///     Parsed response body (JsonObject, string JsonValue, or empty JsonObject for no-content responses).
    ///
    /// Throws:
    ///     GraphApiError: On non-retryable errors or after retries are exhausted.
    /// </summary>
    public async Task<JsonNode?> RequestAsync(
        string method,
        string pathOrUrl,
        JsonNode? jsonBody = null,
        Dictionary<string, string>? headers = null)
    {
        var url = NormalizeUrl(pathOrUrl);
        var requestHeaders = await GetHeadersAsync(headers);
        var attempt = 0;

        while (true)
        {
            using var requestMessage = new HttpRequestMessage(new HttpMethod(method), url);
            if (jsonBody is not null)
            {
                requestMessage.Content = new StringContent(jsonBody.ToJsonString(), Encoding.UTF8, "application/json");
            }
            foreach (var (key, value) in requestHeaders)
            {
                if (key.Equals("content-type", StringComparison.OrdinalIgnoreCase))
                {
                    if (requestMessage.Content is not null)
                    {
                        requestMessage.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
                    }
                    continue;
                }
                requestMessage.Headers.TryAddWithoutValidation(key, value);
            }

            using var response = await _http.SendAsync(requestMessage);

            var location = response.Headers.TryGetValues("Location", out var locationValues)
                ? locationValues.FirstOrDefault()
                : null;
            if (location != null && location.Contains("/operations/"))
            {
                await Task.Delay(TimeSpan.FromSeconds(_delaySeconds));
                method = "GET";
                url = NormalizeUrl(location);
                jsonBody = null;
                requestHeaders = await GetHeadersAsync(headers);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                if (RetryableStatusCodes.Contains(statusCode) && attempt < _maxRetries)
                {
                    double wait;
                    var retryAfter = response.Headers.TryGetValues("Retry-After", out var retryAfterValues)
                        ? retryAfterValues.FirstOrDefault()
                        : null;
                    if (retryAfter != null)
                    {
                        if (!double.TryParse(retryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out wait))
                        {
                            wait = _retryBackoffBase * Math.Pow(2, attempt);
                        }
                    }
                    else
                    {
                        wait = _retryBackoffBase * Math.Pow(2, attempt);
                    }
                    attempt++;
                    Logger.Warning(
                        $"Graph API transient error {statusCode} for {method} {url} — retrying in {wait.ToString("F0", CultureInfo.InvariantCulture)}s (attempt {attempt}/{_maxRetries})");
                    await Task.Delay(TimeSpan.FromSeconds(wait));
                    requestHeaders = await GetHeadersAsync(headers);  // refresh token
                    continue;
                }
                throw await GraphApiError.FromResponseAsync(response);
            }

            var payload = await ParseResponseAsync(response);

            var responseUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
            if (responseUrl.Contains("/operations/"))
            {
                var status = (payload as JsonObject)?["status"]?.GetValue<string>();
                if (status == "inprogress")
                {
                    await Task.Delay(TimeSpan.FromSeconds(_delaySeconds));
                    method = "GET";
                    url = NormalizeUrl(responseUrl);
                    jsonBody = null;
                    requestHeaders = await GetHeadersAsync(headers);
                    continue;
                }
            }

            return payload;
        }
    }

    /// <summary>Build request headers with a cached Bearer token, refreshed 5 min before expiry.</summary>
    private async Task<Dictionary<string, string>> GetHeadersAsync(Dictionary<string, string>? headers)
    {
        const int RefreshBuffer = 300;  // refresh 5 minutes before expiry
        await _tokenLock.WaitAsync();
        try
        {
            if (_token is null || (_token.Value.ExpiresOn - DateTimeOffset.UtcNow).TotalSeconds < RefreshBuffer)
            {
                _token = await _credential.GetTokenAsync(new TokenRequestContext(new[] { GraphScope }));
            }
        }
        finally
        {
            _tokenLock.Release();
        }
        var baseHeaders = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {_token.Value.Token}",
            ["Accept"] = "application/json",
        };
        if (headers != null)
        {
            foreach (var (key, value) in headers)
            {
                baseHeaders[key] = value;
            }
        }
        return baseHeaders;
    }

    /// <summary>Convert a relative path to a full Graph API URL, passing absolute URLs through.</summary>
    // internal (not private): the Python tests exercise ``client._normalize_url``.
    internal string NormalizeUrl(string pathOrUrl)
    {
        if (pathOrUrl.StartsWith("http://") || pathOrUrl.StartsWith("https://"))
        {
            return pathOrUrl;
        }
        return $"{_baseUrl}{pathOrUrl}";
    }

    /// <summary>Parse the HTTP response body as JSON, falling back to plain text.</summary>
    private static async Task<JsonNode?> ParseResponseAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsByteArrayAsync();
        if (content.Length == 0)
        {
            return new JsonObject();
        }

        var text = Encoding.UTF8.GetString(content);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "";
        if (contentType.Contains("application/json"))
        {
            return JsonNode.Parse(text);
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return JsonValue.Create(text);
        }
    }
}
