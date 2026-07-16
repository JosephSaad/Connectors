// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for graph.connection functions — port of tests/test_graph/test_graph_connection.py.

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests.TestGraph;

public class GraphConnectionTests
{
    private readonly AppConfig _testConfig = TestFixtures.TestConfig();
    private readonly FakeGraphClient _mockClient = new();

    /// <summary>Equivalent of Python ``time.monotonic()`` for EnsureConnectionAsync.</summary>
    private static double Monotonic() => Environment.TickCount64 / 1000.0;

    // ── create_connection ────────────────────────────────────────────────────

    [Fact]
    public async Task CreateConnectionPostsPayload()
    {
        await Connection.CreateConnectionAsync(_testConfig, _mockClient);
        Assert.Single(_mockClient.PostCalls);
        var body = _mockClient.PostCalls[0].Body as JsonObject;
        Assert.NotNull(body);
        Assert.Equal(_testConfig.Connector.Id, body!["id"]!.GetValue<string>());
        Assert.Equal(_testConfig.Connector.Name, body["name"]!.GetValue<string>());
    }

    // ── get_connection ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetConnectionCallsGet()
    {
        _mockClient.OnGet = _ => new JsonObject
        {
            ["id"] = _testConfig.Connector.Id,
            ["state"] = "ready",
        };
        var result = await Connection.GetConnectionAsync(_testConfig, _mockClient);
        Assert.Equal("ready", result["state"]!.GetValue<string>());
        Assert.Single(_mockClient.GetCalls);
        Assert.Equal(
            $"{GraphClient.ExternalConnectionsPath}/{_testConfig.Connector.Id}",
            _mockClient.GetCalls[0].Url);
    }

    // ── connection_exists ────────────────────────────────────────────────────

    [Fact]
    public async Task ConnectionExistsTrue()
    {
        _mockClient.OnGet = _ => new JsonObject { ["id"] = "x" };
        Assert.True(await Connection.ConnectionExistsAsync(_testConfig, _mockClient));
    }

    [Fact]
    public async Task ConnectionExistsFalseOnError()
    {
        _mockClient.OnGet = _ => throw new GraphApiError(404, "not found");
        Assert.False(await Connection.ConnectionExistsAsync(_testConfig, _mockClient));
    }

    // ── ensure_connection ────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureConnectionExisting()
    {
        _mockClient.OnGet = _ => new JsonObject { ["id"] = _testConfig.Connector.Id };
        var result = await Connection.EnsureConnectionAsync(_testConfig, _mockClient, Monotonic());
        Assert.Equal("existing", result);
    }

    [Fact]
    public async Task EnsureConnectionCreatedOn404()
    {
        _mockClient.OnGet = _ => throw new GraphApiError(404, "not found");
        _mockClient.OnPost = (_, _) => new JsonObject();
        var result = await Connection.EnsureConnectionAsync(_testConfig, _mockClient, Monotonic());
        Assert.Equal("created", result);
    }

    [Fact]
    public async Task EnsureConnectionReturnsNoneOnOtherError()
    {
        _mockClient.OnGet = _ => throw new InvalidOperationException("unexpected");
        var result = await Connection.EnsureConnectionAsync(_testConfig, _mockClient, Monotonic());
        Assert.Null(result);
    }

    // ── is_connection_ready ──────────────────────────────────────────────────

    [Fact]
    public async Task IsConnectionReadyTrue()
    {
        // schema_exists → true (schema GET succeeds); connection GET → ready
        _mockClient.OnGet = url => url.EndsWith("/schema")
            ? new JsonObject { ["properties"] = new JsonArray() }
            : new JsonObject { ["state"] = "ready" };
        Assert.True(await Connection.IsConnectionReadyAsync(_testConfig, _mockClient));
    }

    [Fact]
    public async Task IsConnectionReadyFalseWhenNotReady()
    {
        // schema_exists → true, but connection state is draft
        _mockClient.OnGet = url => url.EndsWith("/schema")
            ? new JsonObject { ["properties"] = new JsonArray() }
            : new JsonObject { ["state"] = "draft" };
        Assert.False(await Connection.IsConnectionReadyAsync(_testConfig, _mockClient));
    }

    [Fact]
    public async Task IsConnectionReadyFalseWhenNoSchema()
    {
        // schema_exists → false; connection state ready
        _mockClient.OnGet = url => url.EndsWith("/schema")
            ? throw new GraphApiError(404, "not found")
            : new JsonObject { ["state"] = "ready" };
        Assert.False(await Connection.IsConnectionReadyAsync(_testConfig, _mockClient));
    }

    // ── set_search_settings ──────────────────────────────────────────────────

    [Fact]
    public async Task SetSearchSettingsSkipsWhenPresent()
    {
        _mockClient.OnGet = _ => new JsonObject
        {
            ["searchSettings"] = new JsonObject { ["searchResultTemplates"] = new JsonArray() },
        };
        await Connection.SetSearchSettingsAsync(_testConfig, _mockClient);
        Assert.Empty(_mockClient.PatchCalls);
    }

    [Fact]
    public async Task SetSearchSettingsPatchesWhenAbsent()
    {
        _mockClient.OnGet = _ => new JsonObject();
        await Connection.SetSearchSettingsAsync(_testConfig, _mockClient);
        Assert.Single(_mockClient.PatchCalls);
    }

    // ── clear_connection_items ───────────────────────────────────────────────

    [Fact]
    public async Task ClearConnectionItemsDeletesAll()
    {
        _mockClient.PaginateItems = new List<JsonObject>
        {
            new() { ["id"] = "item1" },
            new() { ["id"] = "item2" },
        };
        var count = await Connection.ClearConnectionItemsAsync(_testConfig, _mockClient);
        Assert.Equal(2, count);
        Assert.Equal(2, _mockClient.DeleteCalls.Count);
    }

    // ── delete_connection ────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteConnectionSuccess()
    {
        _mockClient.OnGet = _ => new JsonObject { ["id"] = _testConfig.Connector.Id };
        var result = await Connection.DeleteConnectionAsync(_testConfig, _mockClient, Monotonic());
        Assert.True(result);
        Assert.Single(_mockClient.DeleteCalls);
    }
}
