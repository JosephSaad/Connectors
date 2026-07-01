// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// acl_engine/sf_client.py
// -----------------------
// Thin, async-friendly Salesforce REST client.
//
// Responsibilities
// ----------------
// * Execute SOQL queries against the standard (/query) or Tooling (/tooling/query)
//   API endpoint.
// * Transparently page through multi-page result sets via nextRecordsUrl.
// * Fetch sObject field metadata via the describe endpoint (used by ShareFetcher
//   and the resolver's parent-field discovery).
//
// No caching, no retry logic – kept intentionally simple so that the callers
// (OWDFetcher, ShareFetcher, the various handlers) stay in full control.

using System.Net;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.AclEngine;

/// <summary>
/// All HTTP calls to Salesforce go through this class.
///
/// Parameters
/// ----------
/// instanceUrl    : Full URL of the org  (e.g. "https://myorg.my.salesforce.com")
/// apiVersion     : API version string   (e.g. "60.0")
/// accessToken    : OAuth Bearer token
/// tokenRefresher : Optional callable that returns a fresh access token.
///                  When provided, 401 responses trigger a single retry
///                  with the new token.
/// </summary>
public class SalesforceClient
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector.acl_engine");

    private const int TimeoutSecs = 60;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(TimeoutSecs),
    };

    private readonly string _baseUrl;
    private readonly string _apiVersion;
    private string _accessToken;
    private readonly Func<string>? _tokenRefresher;

    public SalesforceClient(
        string instanceUrl,
        string apiVersion,
        string accessToken,
        Func<string>? tokenRefresher = null)
    {
        _baseUrl = instanceUrl.TrimEnd('/');
        // Normalise "v60.0" → "60.0" so URL construction (which prepends 'v') is correct
        _apiVersion = apiVersion.TrimStart('v');
        _accessToken = accessToken;
        _tokenRefresher = tokenRefresher;
    }

    /// <summary>Refresh the access token via the callback, if available.</summary>
    private void RefreshToken()
    {
        if (_tokenRefresher is null)
            return;
        var newToken = _tokenRefresher();
        _accessToken = newToken;
        Logger.Info("[SalesforceClient] Token refreshed successfully");
    }

    // ── Public async API ──────────────────────────────────────────────────────

    /// <summary>
    /// Execute a SOQL query and return the raw Salesforce response dict.
    ///
    /// ``virtual`` so tests can substitute it, mirroring the Python tests'
    /// mocking of ``sf_client.query``.
    /// </summary>
    public virtual async Task<JsonObject> QueryAsync(string soql, bool tooling = false)
    {
        var endpoint = tooling ? "tooling/query" : "query";
        var url = $"{_baseUrl}/services/data/v{_apiVersion}/{endpoint}?q={Uri.EscapeDataString(soql)}";
        var response = await GetAsync(url);
        if (response.StatusCode == HttpStatusCode.Unauthorized && _tokenRefresher is not null)
        {
            Logger.Warning("[SalesforceClient] 401 — refreshing token and retrying");
            RefreshToken();
            response = await GetAsync(url);
        }
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Salesforce {(tooling ? "tooling " : "")}query failed "
                + $"[{(int)response.StatusCode}]: {text}");
        }
        return await ReadJsonObjectAsync(response);
    }

    /// <summary>
    /// Execute a SOQL query and collect *all* records, following nextRecordsUrl
    /// pagination automatically.
    ///
    /// Returns a flat list of record dicts.
    ///
    /// ``virtual`` so tests can substitute it, mirroring the Python tests'
    /// mocking of ``sf_client.query_all``.
    /// </summary>
    public virtual async Task<List<JsonObject>> QueryAllAsync(string soql, bool tooling = false)
    {
        var records = new List<JsonObject>();
        var result = await QueryAsync(soql, tooling);
        AddRecords(records, result);

        while (!(result["done"]?.GetValue<bool>() ?? true)
               && !string.IsNullOrEmpty(result["nextRecordsUrl"]?.GetValue<string>()))
        {
            // nextRecordsUrl is a relative path – prepend the base URL
            var nextPath = result["nextRecordsUrl"]!.GetValue<string>();
            if (!nextPath.StartsWith("http"))
                nextPath = _baseUrl + nextPath;

            var response = await GetAsync(nextPath);
            if (response.StatusCode == HttpStatusCode.Unauthorized && _tokenRefresher is not null)
            {
                Logger.Warning("[SalesforceClient] 401 during pagination — refreshing token");
                RefreshToken();
                response = await GetAsync(nextPath);
            }
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    $"Salesforce pagination failed [{(int)response.StatusCode}]: {text}");
            }
            result = await ReadJsonObjectAsync(response);
            AddRecords(records, result);
        }

        return records;
    }

    /// <summary>
    /// Fetch the full describe metadata for an sObject.
    ///
    /// Used by ShareFetcher to discover the parent reference field name on
    /// &lt;ObjectType&gt;Share objects, and to discover the access-level field.
    ///
    /// ``virtual`` so tests can substitute it, mirroring the Python tests'
    /// mocking of ``sf_client.describe_sobject``.
    /// </summary>
    public virtual async Task<JsonObject> DescribeSObjectAsync(string sobjectName)
    {
        var url = $"{_baseUrl}/services/data/v{_apiVersion}/sobjects/{sobjectName}/describe";
        var response = await GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Salesforce describe({sobjectName}) failed "
                + $"[{(int)response.StatusCode}]: {text}");
        }
        return await ReadJsonObjectAsync(response);
    }

    /// <summary>
    /// Fetch a single record via the sObject REST endpoint.
    ///
    /// Equivalent to:
    ///     GET /services/data/vXX.X/sobjects/&lt;sobject_name&gt;/&lt;record_id&gt;
    ///
    /// Returns the full record payload as a dict.  Throws InvalidOperationException
    /// on any non-2xx response.
    ///
    /// curl equivalent:
    ///     GET /services/data/v60.0/sobjects/User/&lt;record_id&gt;
    ///     Authorization: Bearer &lt;access_token&gt;
    /// </summary>
    public async Task<JsonObject> GetSObjectAsync(string sobjectName, string recordId)
    {
        var url = $"{_baseUrl}/services/data/v{_apiVersion}"
                  + $"/sobjects/{sobjectName}/{recordId}";
        var response = await GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Salesforce GET {sobjectName}/{recordId} failed "
                + $"[{(int)response.StatusCode}]: {text}");
        }
        return await ReadJsonObjectAsync(response);
    }

    // ── HTTP helpers ──────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> GetAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_accessToken}");
        request.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        return await Http.SendAsync(request);
    }

    private static async Task<JsonObject> ReadJsonObjectAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(text)!.AsObject();
    }

    private static void AddRecords(List<JsonObject> records, JsonObject result)
    {
        if (result["records"] is JsonArray arr)
        {
            foreach (var node in arr)
            {
                if (node is JsonObject obj)
                    records.Add(obj);
            }
        }
    }
}
