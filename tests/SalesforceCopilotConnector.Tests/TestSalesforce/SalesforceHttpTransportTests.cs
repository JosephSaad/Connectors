// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// HTTP transport tests for Salesforce/ApiClient — the counterpart of
// TestGraph/GraphHttpTransportTests. All other Salesforce tests stub
// ApiClient.SfSession with a fake handler, so the OAuth form post, SOQL query
// URLs, nextRecordsUrl pagination, the INVALID_FIELD prune-and-retry loop,
// per-org field caching, 401 token refresh, and the 502/503/504 retry ladder
// had never executed over a real socket. These tests point an AppConfig's
// InstanceUrl at a loopback HttpListener and assert on what crossed the wire.

using System.Text.Json.Nodes;
using System.Web;
using SalesforceCopilotConnector.Salesforce;
using SalesforceCopilotConnector.Tests.TestInfrastructure;

namespace SalesforceCopilotConnector.Tests.TestSalesforce;

[Collection("EnvVars")]
public class SalesforceHttpTransportTests
{
    /// <summary>
    /// Snapshot/restore the process-global <see cref="ApiClient.SfSession"/> and
    /// install a fresh REAL HttpClient — another suite may have left a stub in it.
    /// </summary>
    private sealed class RealSfSessionScope : IDisposable
    {
        private readonly HttpClient _saved = ApiClient.SfSession;
        public RealSfSessionScope() => ApiClient.SfSession = new HttpClient();
        public void Dispose() => ApiClient.SfSession = _saved;
    }

    /// <summary>TestConfig with the Salesforce instance retargeted at the fake server.</summary>
    private static AppConfig ConfigFor(string instanceUrl)
    {
        var b = TestFixtures.TestConfig();
        return new AppConfig
        {
            ClientId = b.ClientId,
            TenantId = b.TenantId,
            RepoRoot = b.RepoRoot,
            SchemaConfig = b.SchemaConfig,
            OwdFieldMap = b.OwdFieldMap,
            ParentMap = b.ParentMap,
            OwdOverrides = b.OwdOverrides,
            ObjectNames = b.ObjectNames,
            UseNewAclEngine = b.UseNewAclEngine,
            UseGroupAcl = b.UseGroupAcl,
            UseEntityDefinitionOwd = b.UseEntityDefinitionOwd,
            DebugObjectType = b.DebugObjectType,
            DebugItemId = b.DebugItemId,
            Tuning = b.Tuning,
            Connector = new ConnectorSettings
            {
                // Unique per test: the field cache is keyed by connection id +
                // instance URL, so no state leaks between tests or runs.
                Id = $"sf-wire-{Guid.NewGuid():N}",
                Name = b.Connector.Name,
                Description = b.Connector.Description,
                Schema = b.Connector.Schema,
                Template = b.Connector.Template,
                Salesforce = new SalesforceSettings
                {
                    InstanceUrl = instanceUrl,
                    ApiVersion = b.Connector.Salesforce.ApiVersion,
                    ClientId = "wire-client-id",
                    ClientSecret = "wire-client-secret",
                },
            },
        };
    }

    private static async Task<List<JsonObject>> FetchAllAsync(
        AppConfig config, string token, SalesforceObjectConfig objectConfig)
    {
        var records = new List<JsonObject>();
        await foreach (var record in ApiClient.FetchSalesforceRecordsAsync(config, token, objectConfig))
            records.Add(record);
        return records;
    }

    private static string SoqlOf(RecordedRequest request) =>
        HttpUtility.UrlDecode(request.PathAndQuery);

    private static string RecordsPage(bool done, string nextUrl = "", params string[] ids)
    {
        var records = string.Join(",", ids.Select(id =>
            $"{{\"attributes\":{{\"type\":\"Account\"}},\"Id\":\"{id}\",\"Name\":\"n-{id}\"}}"));
        var next = done ? "" : $",\"nextRecordsUrl\":\"{nextUrl}\"";
        return $"{{\"totalSize\":{ids.Length},\"done\":{done.ToString().ToLowerInvariant()},\"records\":[{records}]{next}}}";
    }

    // ── OAuth token endpoint ───────────────────────────────────────────────────

    [Fact]
    public async Task TokenRequestPostsClientCredentialsFormAndParsesToken()
    {
        using var server = new LoopbackJsonServer();
        using var session = new RealSfSessionScope();
        server.Script = (_, _) => (200, "{\"access_token\":\"wire-token-123\",\"token_type\":\"Bearer\"}", null);

        var token = await ApiClient.GetSalesforceAccessTokenAsync(ConfigFor(server.BaseUrl));

        Assert.Equal("wire-token-123", token);
        var request = Assert.Single(server.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/services/oauth2/token", request.PathAndQuery);
        Assert.Contains("grant_type=client_credentials", request.Body);
        Assert.Contains("client_id=wire-client-id", request.Body);
        Assert.Contains("client_secret=wire-client-secret", request.Body);
    }

    [Fact]
    public async Task TokenFailureSurfacesStatusAndResponseBody()
    {
        using var server = new LoopbackJsonServer();
        using var session = new RealSfSessionScope();
        server.Script = (_, _) => (400, "{\"error\":\"invalid_client\"}", null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ApiClient.GetSalesforceAccessTokenAsync(ConfigFor(server.BaseUrl)));
        Assert.Contains("400", ex.Message);
        Assert.Contains("invalid_client", ex.Message);
    }

    // ── query pagination ───────────────────────────────────────────────────────

    [Fact]
    public async Task QueryFollowsNextRecordsUrlUntilDone()
    {
        using var server = new LoopbackJsonServer();
        using var session = new RealSfSessionScope();
        var config = ConfigFor(server.BaseUrl);
        var nextPath = $"/services/data/{config.Connector.Salesforce.ApiVersion}/query/01g-next-2";
        server.Script = (n, _) => n == 0
            ? (200, RecordsPage(done: false, nextPath, "001A", "001B"), null)
            : (200, RecordsPage(done: true, "", "001C"), null);

        var records = await FetchAllAsync(config, "fetch-token",
            new SalesforceObjectConfig("Account", new[] { "Id", "Name" }));

        Assert.Equal(new[] { "001A", "001B", "001C" }, records.Select(r => r["Id"]!.GetValue<string>()));
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal("Bearer fetch-token", server.Requests[0].Authorization);
        Assert.Equal(nextPath, server.Requests[1].PathAndQuery);  // nextRecordsUrl replayed verbatim
    }

    // ── INVALID_FIELD prune-and-retry + per-org field cache ────────────────────

    [Fact]
    public async Task InvalidFieldIsPrunedRetriedAndCachedForTheNextRun()
    {
        using var server = new LoopbackJsonServer();
        using var session = new RealSfSessionScope();
        var config = ConfigFor(server.BaseUrl);
        var objectConfig = new SalesforceObjectConfig("Account", new[] { "Id", "Name", "BadField" });
        server.Script = (_, request) =>
            SoqlOf(request).Contains("BadField")
                ? (400, "[{\"errorCode\":\"INVALID_FIELD\",\"message\":\"No such column 'BadField' on entity 'Account'\"}]", null)
                : (200, RecordsPage(done: true, "", "001A"), null);

        var records = await FetchAllAsync(config, "fetch-token", objectConfig);

        Assert.Single(records);
        Assert.Equal(2, server.Requests.Count);
        Assert.Contains("BadField", SoqlOf(server.Requests[0]));      // first attempt includes it
        Assert.DoesNotContain("BadField", SoqlOf(server.Requests[1])); // pruned on the retry
        Assert.Contains("Name", SoqlOf(server.Requests[1]));           // healthy fields survive

        // The working field list was persisted: a second run queries WITHOUT the
        // bad field from the very first request — no 400 round-trip.
        server.Requests.Clear();
        var again = await FetchAllAsync(config, "fetch-token", objectConfig);
        Assert.Single(again);
        var request = Assert.Single(server.Requests);
        Assert.DoesNotContain("BadField", SoqlOf(request));
    }

    // ── 401 refresh-once and 5xx retry ladder ──────────────────────────────────

    [Fact]
    public async Task UnauthorizedRefreshesTokenViaOauthEndpointAndRetries()
    {
        using var server = new LoopbackJsonServer();
        using var session = new RealSfSessionScope();
        var config = ConfigFor(server.BaseUrl);
        server.Script = (_, request) => request.PathAndQuery switch
        {
            "/services/oauth2/token" => (200, "{\"access_token\":\"refreshed-token\"}", null),
            _ when request.Authorization == "Bearer refreshed-token"
                => (200, RecordsPage(done: true, "", "001A"), null),
            _ => (401, "[{\"errorCode\":\"INVALID_SESSION_ID\",\"message\":\"Session expired or invalid\"}]", null),
        };

        var records = await FetchAllAsync(config, "stale-token",
            new SalesforceObjectConfig("Account", new[] { "Id", "Name" }));

        Assert.Single(records);
        Assert.Equal(3, server.Requests.Count);
        Assert.Equal("Bearer stale-token", server.Requests[0].Authorization);          // original attempt
        Assert.Equal("/services/oauth2/token", server.Requests[1].PathAndQuery);       // refresh
        Assert.Equal("Bearer refreshed-token", server.Requests[2].Authorization);      // retried with new token
    }

    [Fact]
    public async Task ServiceUnavailableIsRetriedTransparently()
    {
        using var server = new LoopbackJsonServer();
        using var session = new RealSfSessionScope();
        var config = ConfigFor(server.BaseUrl);
        // Python-parity session policy: urllib3 Retry(status_forcelist=[502,503,504]).
        server.Script = (n, _) => n == 0
            ? (503, "{}", null)
            : (200, RecordsPage(done: true, "", "001A"), null);

        var records = await FetchAllAsync(config, "fetch-token",
            new SalesforceObjectConfig("Account", new[] { "Id", "Name" }));

        Assert.Single(records);
        Assert.Equal(2, server.Requests.Count);  // 503 then transparent retry
    }
}
