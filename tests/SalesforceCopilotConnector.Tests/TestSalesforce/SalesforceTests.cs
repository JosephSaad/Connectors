// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests.TestSalesforce;

/// <summary>Port of tests/test_salesforce/test_salesforce.py.</summary>
[Collection("IngestGlobalState")]  // ApiClient.SfSession is process-global
public class SalesforceTests
{
    /// <summary>
    /// Fake HttpMessageHandler that returns queued responses and records request URLs
    /// (Python monkeypatches ``_sf_session.get``).
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public List<string> Requests { get; } = new();

        public StubHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private static HttpResponseMessage StubResponse(int statusCode, string reason, JsonNode payload)
    {
        return new HttpResponseMessage((HttpStatusCode)statusCode)
        {
            ReasonPhrase = reason,
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }

    private static string ExtractSoql(string url)
    {
        var query = new Uri(url).Query.TrimStart('?');
        foreach (var pair in query.Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts[0] == "q")
                return WebUtility.UrlDecode(parts[1]);
        }
        throw new InvalidOperationException($"No 'q' parameter in {url}");
    }

    /// <summary>
    /// Copy of the shared test config with an empty connector ID so the SQLite
    /// field cache is a no-op (Python monkeypatches ``_load_field_cache`` /
    /// ``_save_field_cache``; the cache helpers short-circuit on an empty
    /// connection ID).
    /// </summary>
    private static AppConfig TestConfigWithoutFieldCache()
    {
        var config = TestFixtures.TestConfig();
        return new AppConfig
        {
            ClientId = config.ClientId,
            TenantId = config.TenantId,
            RepoRoot = config.RepoRoot,
            SchemaConfig = config.SchemaConfig,
            OwdFieldMap = config.OwdFieldMap,
            ParentMap = config.ParentMap,
            OwdOverrides = config.OwdOverrides,
            ObjectNames = config.ObjectNames,
            UseNewAclEngine = config.UseNewAclEngine,
            UseGroupAcl = config.UseGroupAcl,
            UseEntityDefinitionOwd = config.UseEntityDefinitionOwd,
            DebugObjectType = config.DebugObjectType,
            DebugItemId = config.DebugItemId,
            Tuning = config.Tuning,
            Connector = new ConnectorSettings
            {
                Id = "",
                Name = config.Connector.Name,
                Description = config.Connector.Description,
                Schema = config.Connector.Schema,
                Template = config.Connector.Template,
                Salesforce = config.Connector.Salesforce,
            },
        };
    }

    [Fact]
    public async Task FetchSalesforceRecordsRetriesWithoutInvalidField()
    {
        var objectConfig = new SalesforceObjectConfig(
            ObjectType: "Account",
            Fields: new[] { "Id", "Name", "AccountNumber" });

        var handler = new StubHandler(
            StubResponse(
                400,
                "Bad Request",
                new JsonArray(
                    new JsonObject
                    {
                        ["message"] = "No such column 'AccountNumber' on entity 'Account'.",
                        ["errorCode"] = "INVALID_FIELD",
                    })),
            StubResponse(
                200,
                "OK",
                new JsonObject
                {
                    ["records"] = new JsonArray(
                        new JsonObject
                        {
                            ["Id"] = "001000000000001AAA",
                            ["Name"] = "Contoso",
                        }),
                }));

        var testConfig = TestConfigWithoutFieldCache();

        var originalSession = ApiClient.SfSession;
        ApiClient.SfSession = new HttpClient(handler);
        try
        {
            var records = new List<JsonObject>();
            await foreach (var record in ApiClient.FetchSalesforceRecordsAsync(testConfig, "token", objectConfig))
                records.Add(record);

            Assert.Equal(2, handler.Requests.Count);
            Assert.Contains("AccountNumber", ExtractSoql(handler.Requests[0]));
            Assert.DoesNotContain("AccountNumber", ExtractSoql(handler.Requests[1]));

            var record0 = Assert.Single(records);
            Assert.Equal(3, record0.Count);
            Assert.Equal("001000000000001AAA", record0["Id"]!.GetValue<string>());
            Assert.Equal("Contoso", record0["Name"]!.GetValue<string>());
            Assert.Equal("Account", record0["objectType"]!.GetValue<string>());
        }
        finally
        {
            ApiClient.SfSession = originalSession;
        }
    }

    [Fact]
    public async Task FetchRecordIdsUsesIdOnlySoqlWithFilterConditionAndPaginates()
    {
        // The deletion sweep's source of truth. It must (a) project ONLY Id (no bodies, no
        // field-retry loop), (b) apply the SAME filterCondition the content crawl uses — so
        // filtered-out records are never mistaken for deletions — and (c) paginate to completion.
        var objectConfig = new SalesforceObjectConfig(
            ObjectType: "Account",
            Fields: new[] { "Id", "Name", "Industry" },  // ignored — id-only projection
            FilterCondition: "IsDeleted = false");

        var handler = new StubHandler(
            StubResponse(
                200,
                "OK",
                new JsonObject
                {
                    ["records"] = new JsonArray(
                        new JsonObject { ["Id"] = "001A" },
                        new JsonObject { ["Id"] = "001B" }),
                    ["nextRecordsUrl"] = "/services/data/v60.0/query/01g-2000",
                }),
            StubResponse(
                200,
                "OK",
                new JsonObject
                {
                    ["records"] = new JsonArray(new JsonObject { ["Id"] = "001C" }),
                }));

        var originalSession = ApiClient.SfSession;
        ApiClient.SfSession = new HttpClient(handler);
        try
        {
            var ids = new List<string>();
            await foreach (var id in ApiClient.FetchRecordIdsAsync(
                               TestConfigWithoutFieldCache(), "token", objectConfig))
            {
                ids.Add(id);
            }

            Assert.Equal("SELECT Id FROM Account WHERE IsDeleted = false", ExtractSoql(handler.Requests[0]));
            Assert.Equal(new[] { "001A", "001B", "001C" }, ids);  // paginated via nextRecordsUrl
            Assert.Equal(2, handler.Requests.Count);
        }
        finally
        {
            ApiClient.SfSession = originalSession;
        }
    }

    [Fact]
    public void ExtractUnsupportedFieldsForRelationshipPath()
    {
        var errorInfo = new SalesforceErrorInfo(
            ErrorCode: "INVALID_FIELD",
            Message:
                "Didn't understand relationship 'ConvertedAccount' in field path. " +
                "If you are attempting to use a custom relationship, be sure to append the '__r' " +
                "after the custom relationship name.",
            RawText: "");

        var unsupportedFields = ApiClient.ExtractUnsupportedFields(
            new[] { "Id", "Name", "ConvertedAccount.Name", "ConvertedAccount.Owner.Name" },
            errorInfo);

        Assert.Equal(new[] { "ConvertedAccount.Name", "ConvertedAccount.Owner.Name" }, unsupportedFields);
    }
}
