// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for graph.schema functions — port of tests/test_graph/test_graph_schema.py.

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests.TestGraph;

public class GraphSchemaTests : IDisposable
{
    private readonly AppConfig _testConfig = TestFixtures.TestConfig();
    private readonly FakeGraphClient _mockClient = new();
    private readonly Func<double, Task> _originalSleeper;

    public GraphSchemaTests()
    {
        // Port of ``@patch("graph.schema.delay")`` — no real sleeping in tests.
        _originalSleeper = Utils.Sleeper;
        Utils.Sleeper = _ => Task.CompletedTask;
    }

    public void Dispose()
    {
        Utils.Sleeper = _originalSleeper;
    }

    [Fact]
    public async Task CreateSchemaPatchesWithPayload()
    {
        await Schema.CreateSchemaAsync(_testConfig, _mockClient);
        Assert.Single(_mockClient.PatchCalls);
        var body = _mockClient.PatchCalls[0].Body as JsonObject;
        Assert.NotNull(body);
        Assert.Equal("microsoft.graph.externalItem", body!["baseType"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(body["properties"], _testConfig.Connector.Schema));
    }

    [Fact]
    public async Task SchemaExistsTrue()
    {
        _mockClient.OnGet = _ => new JsonObject { ["properties"] = new JsonArray() };
        Assert.True(await Schema.SchemaExistsAsync(_testConfig, _mockClient));
    }

    [Fact]
    public async Task SchemaExistsFalseOnError()
    {
        _mockClient.OnGet = _ => throw new GraphApiError(404, "not found");
        Assert.False(await Schema.SchemaExistsAsync(_testConfig, _mockClient));
    }

    [Fact]
    public async Task EnsureSchemaReturnsIfExists()
    {
        _mockClient.OnGet = _ => new JsonObject { ["properties"] = new JsonArray() };
        await Schema.EnsureSchemaAsync(_testConfig, _mockClient);
        Assert.Empty(_mockClient.PatchCalls);
    }

    [Fact]
    public async Task EnsureSchemaCreatesWhen404()
    {
        _mockClient.OnGet = _ => throw new GraphApiError(404, "not found");
        await Schema.EnsureSchemaAsync(_testConfig, _mockClient);
        Assert.Single(_mockClient.PatchCalls);
    }
}
