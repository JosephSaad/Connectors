// End-to-end: attachment content ingestion through the full pipeline —
// ACL inheritance, inventory recording, and the on/off toggle, against
// mocked Clarizen + Graph.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ClarizenConnector.AclEngine;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;
using ClarizenConnector.Item;

namespace ClarizenConnector.Tests;

public class AttachmentPipelineTests : IDisposable
{
    private const string Connector = "ClarizenAdaptiveWork";

    private readonly TempDir _dir = new();
    private readonly SyncStateScope _stateScope = new();
    private readonly IdentityStore _store;
    private readonly SchemaConfig _schema;
    private readonly FakeGraphClient _graph;
    private readonly ClarizenClient _clarizen;
    private readonly byte[] _fileBytes = Encoding.UTF8.GetBytes("Extracted attachment body text");

    public AttachmentPipelineTests()
    {
        _store = new IdentityStore("AttachPipe", Path.Combine(_dir.Path, "identity.db"));
        _store.Upsert(new PrincipalMapping("/User/1", "user", "o@example.com", "entra-owner", DateTime.UtcNow));

        _schema = new SchemaConfig
        {
            ObjectList = new List<ObjectConfig>
            {
                new()
                {
                    ObjectName = "Attachment",
                    DisplayName = "Attachment",
                    AclMode = "ownerOnly",
                    AttachmentUrlField = "DownloadUrl",
                    AttachmentNameField = "Name",
                    AttachmentContentTypeField = "FileType",
                    SelectedFields = new Dictionary<string, string>
                    {
                        ["Name"] = "Title",
                        ["DownloadUrl"] = "_cz_DownloadUrl",
                        ["CreatedBy"] = "CreatedBy",
                        ["LastUpdatedOn"] = "LastUpdatedOn",
                    },
                },
            },
        };

        var handler = new MockHttpHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/authentication/login"))
                return MockHttpHandler.Json(HttpStatusCode.OK, """{"sessionId": "s"}""");
            if (request.RequestUri.ToString().Contains("/download/"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_fileBytes),
                };
            var page = new JsonObject
            {
                ["entities"] = new JsonArray(new JsonObject
                {
                    ["id"] = "/Attachment/7",
                    ["Name"] = "spec.txt",
                    ["FileType"] = "text/plain",
                    ["DownloadUrl"] = "https://cz.example/download/7",
                    ["CreatedBy"] = new JsonObject { ["id"] = "/User/1" },
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

    private Func<string, IItemInventory> InventoryFactory => connectionId =>
        new ItemInventory(connectionId, Path.Combine(_dir.Path, $"inventory_{connectionId}.db"));

    private IngestPipeline Pipeline(bool attachmentOn)
    {
        var config = TestConfig.Make(attachmentIngestion: attachmentOn);
        var mapper = new PrincipalMapper(_store, "{}");
        var resolver = new AclResolver(
            mapper, new DirectorySnapshot(), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
        var enricher = new AttachmentEnricher(config, _clarizen, new Content.ContentExtractor());
        return new IngestPipeline(
            config, _schema, _clarizen, _graph, resolver, new ItemConverter(config),
            ha: null, inventoryFactory: InventoryFactory, attachmentEnricher: enricher);
    }

    [Fact]
    public async Task AttachmentOn_ExtractedTextInContent_AclInherited()
    {
        var summary = await Pipeline(attachmentOn: true).RunAsync(fullCrawl: true);
        Assert.Equal(1, summary.Ingested);

        var put = Assert.Single(_graph.Sent, s => s.Method == HttpMethod.Put);
        var payload = put.Body!.AsObject();

        // Extracted text is in the content body.
        Assert.Contains("Extracted attachment body text",
            payload["content"]!["value"]!.GetValue<string>());
        Assert.Contains("Attachment: spec.txt", payload["content"]!["value"]!.GetValue<string>());
        // Status property stamped.
        Assert.Equal("extracted", payload["properties"]!["AttachmentExtractionStatus"]!.GetValue<string>());
        // ACL inherited from the record's owner (no attachment-specific ACL path).
        var acl = payload["acl"]!.AsArray();
        Assert.Contains(acl, e => e!["value"]!.GetValue<string>() == "entra-owner");
    }

    [Fact]
    public async Task AttachmentOn_RecordsInventory()
    {
        await Pipeline(attachmentOn: true).RunAsync(fullCrawl: true);
        using var inventory = InventoryFactory(Connector);
        Assert.Equal(new[] { "Attachment_7" }, inventory.IdsForObject("Attachment"));
    }

    [Fact]
    public async Task AttachmentOff_NoExtraction_NoDownloadContent()
    {
        var summary = await Pipeline(attachmentOn: false).RunAsync(fullCrawl: true);
        Assert.Equal(1, summary.Ingested);

        var put = Assert.Single(_graph.Sent, s => s.Method == HttpMethod.Put);
        var payload = put.Body!.AsObject();
        Assert.DoesNotContain("Extracted attachment body text",
            payload["content"]!["value"]!.GetValue<string>());
        Assert.Null(payload["properties"]!["AttachmentExtractionStatus"]);
        // Still ingested as a metadata-only item (unchanged behaviour).
        Assert.Equal("spec.txt", payload["properties"]!["Title"]!.GetValue<string>());
    }
}
