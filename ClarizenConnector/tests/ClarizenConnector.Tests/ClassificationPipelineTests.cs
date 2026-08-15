// End-to-end: classification properties appear on ingested items only when
// CLASSIFICATION is on, financial folds into the label, and the manifest is
// written when CLASSIFICATION_MANIFEST is on. Mocked Clarizen + Graph.

using System.Net;
using System.Text.Json.Nodes;
using ClarizenConnector.AclEngine;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Content;
using ClarizenConnector.Graph;
using ClarizenConnector.Item;

namespace ClarizenConnector.Tests;

public class ClassificationPipelineTests : IDisposable
{
    private const string Connector = "ClarizenAdaptiveWork";

    private readonly TempDir _dir = new();
    private readonly SyncStateScope _stateScope = new();
    private readonly IdentityStore _store;
    private readonly SchemaConfig _schema;
    private readonly FakeGraphClient _graph;
    private readonly ClarizenClient _clarizen;

    public ClassificationPipelineTests()
    {
        _store = new IdentityStore("Classify", Path.Combine(_dir.Path, "identity.db"));
        _store.Upsert(new PrincipalMapping("/User/1", "user", "o@example.com", "entra-owner", DateTime.UtcNow));
        _schema = new SchemaConfig
        {
            ObjectList = new List<ObjectConfig>
            {
                new()
                {
                    ObjectName = "Task",
                    DisplayName = "Task",
                    AclMode = "ownerOnly",
                    SensitivityDefault = "Internal",
                    SelectedFields = new Dictionary<string, string>
                    {
                        ["Name"] = "Title",
                        ["Description"] = "_cz_Description",
                        ["Owner"] = "Owner",
                        ["LastUpdatedOn"] = "LastUpdatedOn",
                    },
                },
            },
        };
        var handler = new MockHttpHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/authentication/login"))
                return MockHttpHandler.Json(HttpStatusCode.OK, """{"sessionId": "s"}""");
            var page = new JsonObject
            {
                ["entities"] = new JsonArray(new JsonObject
                {
                    ["id"] = "/Task/1",
                    ["Name"] = "Onboard vendor",
                    // A PII email lives in the description content.
                    ["Description"] = "Primary contact is jane.doe@vendor.com for signoff.",
                    ["Owner"] = new JsonObject { ["id"] = "/User/1" },
                }),
                ["paging"] = new JsonObject { ["hasMore"] = false },
            };
            return MockHttpHandler.Json(HttpStatusCode.OK, page.ToJsonString());
        });
        _clarizen = new ClarizenClient(
            TestConfig.Make(), new ApiBudget(1_000_000, callsPerMinute: 6_000_000), handler);
        _clarizen.DelayAsync = (_, _) => Task.CompletedTask;
        _graph = new FakeGraphClient(TestConfig.Make());
    }

    public void Dispose()
    {
        _store.Dispose();
        _stateScope.Dispose();
        _dir.Dispose();
    }

    private Func<string, IItemInventory> InventoryFactory => id =>
        new ItemInventory(id, Path.Combine(_dir.Path, $"inv_{id}.db"));

    private IngestPipeline Pipeline(AppConfig config)
    {
        var mapper = new PrincipalMapper(_store, "{}");
        var resolver = new AclResolver(
            mapper, new DirectorySnapshot(), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
        SensitivityClassifier? classifier = config.Classification
            ? new SensitivityClassifier(ContentClassifier.Load(
                Path.Combine(AppContext.BaseDirectory, "config", "classification.json")))
            : null;
        return new IngestPipeline(
            config, _schema, _clarizen, _graph, resolver, new ItemConverter(config),
            ha: null, inventoryFactory: InventoryFactory, attachmentEnricher: null,
            sensitivityClassifier: classifier);
    }

    private JsonObject IngestedProperties()
    {
        var put = Assert.Single(_graph.Sent, s => s.Method == HttpMethod.Put);
        return put.Body!.AsObject()["properties"]!.AsObject();
    }

    [Fact]
    public async Task ClassificationOn_StampsLabelAndCategories()
    {
        var summary = await Pipeline(TestConfig.Make(classification: true)).RunAsync(fullCrawl: true);
        Assert.Equal(1, summary.Ingested);

        var props = IngestedProperties();
        Assert.Equal("Restricted", props["advisorySensitivity"]!.GetValue<string>());  // PII in description
        Assert.Contains("PII", props["DetectedCategories"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task ClassificationOff_NoTaxonomyProperties()
    {
        var summary = await Pipeline(TestConfig.Make(classification: false)).RunAsync(fullCrawl: true);
        Assert.Equal(1, summary.Ingested);

        var props = IngestedProperties();
        Assert.Null(props["advisorySensitivity"]);
        Assert.Null(props["DetectedCategories"]);
    }

    [Fact]
    public async Task Manifest_WrittenWhenEnabled_WithSummary()
    {
        var config = TestConfig.Make(classification: true, classificationManifest: true);
        await Pipeline(config).RunAsync(fullCrawl: true);

        var manifests = Directory.GetFiles(SyncState.LogsDir, "classification_*.jsonl");
        var path = Assert.Single(manifests);
        var lines = File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToList();
        Assert.Equal(2, lines.Count);  // 1 item + summary

        var item = JsonNode.Parse(lines[0])!;
        Assert.Equal("Task_1", item["item_id"]!.GetValue<string>());
        Assert.Equal("Restricted", item["sensitivity_label"]!.GetValue<string>());

        var summary = JsonNode.Parse(lines[^1])!;
        Assert.Equal("summary", summary["kind"]!.GetValue<string>());
        Assert.Equal(1, summary["counts"]!["Restricted"]!.GetValue<int>());
    }

    [Fact]
    public async Task Manifest_NotWritten_WhenDisabled()
    {
        await Pipeline(TestConfig.Make(classification: true, classificationManifest: false))
            .RunAsync(fullCrawl: true);
        Assert.Empty(Directory.GetFiles(SyncState.LogsDir, "classification_*.jsonl"));
    }
}
