// AttachmentEnricher + downloader tests: size cap, type allowlist, download
// failure, extraction status stamping, content append, metrics, on/off toggle.

using System.Text;
using System.Text.Json.Nodes;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Content;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;
using ClarizenConnector.Item;

namespace ClarizenConnector.Tests;

/// <summary>Scriptable downloader: maps url → result; records requested caps.</summary>
public sealed class FakeAttachmentDownloader : IAttachmentDownloader
{
    private readonly Func<string, long, DownloadResult> _responder;
    public List<(string Url, long MaxBytes)> Requests { get; } = new();

    public FakeAttachmentDownloader(Func<string, long, DownloadResult> responder) =>
        _responder = responder;

    public Task<DownloadResult> DownloadAttachmentAsync(
        string url, long maxBytes, CancellationToken ct = default)
    {
        Requests.Add((url, maxBytes));
        return Task.FromResult(_responder(url, maxBytes));
    }
}

public class AttachmentEnricherTests
{
    private static ObjectConfig AttachmentConfig() => new()
    {
        ObjectName = "Attachment",
        DisplayName = "Attachment",
        AclMode = "projectMembers",
        ProjectField = "AttachedTo",
        AttachmentUrlField = "DownloadUrl",
        AttachmentNameField = "Name",
        AttachmentContentTypeField = "FileType",
        AttachmentSizeField = "FileSize",
        SelectedFields = new Dictionary<string, string>
        {
            ["Name"] = "Title",
            ["DownloadUrl"] = "_cz_DownloadUrl",
        },
    };

    private static ClarizenRecord Record(
        string name, string? url = "https://cz/file", string? fileType = null, long? size = null)
    {
        var fields = new JsonObject { ["id"] = "/Attachment/7", ["Name"] = name };
        if (url is not null)
            fields["DownloadUrl"] = url;
        if (fileType is not null)
            fields["FileType"] = fileType;
        if (size is not null)
            fields["FileSize"] = size.Value.ToString();
        return new ClarizenRecord("Attachment", fields);
    }

    private static ExternalItem BaseItem() => new()
    {
        Id = "Attachment_7",
        Acl = { new AclEntry(AclEntryType.User, "u1", AclAccessType.Grant) },
        Content = "Attachment: report.txt",
    };

    private static AppConfig Config(
        bool on = true, long maxBytes = 10 * 1024 * 1024, string? allowed = null) =>
        TestConfig.Make(
            attachmentIngestion: on,
            attachmentMaxBytes: maxBytes,
            attachmentAllowedTypes: allowed is null ? null : AppConfig.ParseAllowedTypes(allowed));

    private static AttachmentEnricher Enricher(
        AppConfig config, FakeAttachmentDownloader downloader, IContentExtractor? extractor = null) =>
        new(config, downloader, extractor ?? new ContentExtractor());

    [Fact]
    public void ShouldEnrich_GatedByFlagAndObjectType()
    {
        var downloader = new FakeAttachmentDownloader((_, _) => DownloadResult.Failed("x"));
        Assert.True(Enricher(Config(on: true), downloader).ShouldEnrich(AttachmentConfig()));
        Assert.False(Enricher(Config(on: false), downloader).ShouldEnrich(AttachmentConfig()));

        var nonAttachment = new ObjectConfig { ObjectName = "Task" };
        Assert.False(Enricher(Config(on: true), downloader).ShouldEnrich(nonAttachment));
    }

    [Fact]
    public async Task Disabled_IsNoOp()
    {
        Metrics.ResetForTests();
        var downloader = new FakeAttachmentDownloader((_, _) =>
            DownloadResult.Success(Encoding.UTF8.GetBytes("text")));
        var item = BaseItem();
        var status = await Enricher(Config(on: false), downloader)
            .EnrichAsync(item, Record("a.txt"), AttachmentConfig());
        Assert.Equal("disabled", status);
        Assert.Empty(downloader.Requests);
        Assert.False(item.Properties.ContainsKey(AttachmentEnricher.StatusProperty));
        Metrics.ResetForTests();
    }

    [Fact]
    public async Task Success_ExtractsAppendsContent_StampsStatus_Metric()
    {
        Metrics.ResetForTests();
        var downloader = new FakeAttachmentDownloader((_, _) =>
            DownloadResult.Success(Encoding.UTF8.GetBytes("The quarterly figures")));
        var item = BaseItem();

        var status = await Enricher(Config(), downloader)
            .EnrichAsync(item, Record("q4.txt", fileType: "text/plain"), AttachmentConfig());

        Assert.Equal("extracted", status);
        Assert.Equal("extracted", item.Properties[AttachmentEnricher.StatusProperty]);
        Assert.Contains("The quarterly figures", item.Content);
        Assert.Contains("Attachment: q4.txt", item.Content);
        // Original content preserved (append, not replace).
        Assert.Contains("Attachment: report.txt", item.Content);
        Assert.Equal(1, Metrics.AttachmentsExtracted);
        Assert.Equal(0, Metrics.AttachmentsSkipped);
        Metrics.ResetForTests();
    }

    [Fact]
    public async Task DisallowedType_Skipped_NoDownload()
    {
        Metrics.ResetForTests();
        var downloader = new FakeAttachmentDownloader((_, _) =>
            DownloadResult.Success(new byte[] { 1 }));
        var item = BaseItem();

        var status = await Enricher(Config(), downloader)
            .EnrichAsync(item, Record("photo.png", fileType: "image/png"), AttachmentConfig());

        Assert.StartsWith("skipped:type", status);
        Assert.Empty(downloader.Requests);  // never downloaded
        Assert.Equal(1, Metrics.AttachmentsSkipped);
        Assert.Equal(0, Metrics.AttachmentsExtracted);
        Metrics.ResetForTests();
    }

    [Fact]
    public async Task TypeAllowlist_Narrowed_ExcludesOtherwiseSupported()
    {
        var downloader = new FakeAttachmentDownloader((_, _) =>
            DownloadResult.Success(Encoding.UTF8.GetBytes("x")));
        // Only pdf allowed → a txt is skipped even though the extractor supports it.
        var status = await Enricher(Config(allowed: "pdf"), downloader)
            .EnrichAsync(BaseItem(), Record("a.txt"), AttachmentConfig());
        Assert.StartsWith("skipped:type", status);
        Assert.Empty(downloader.Requests);
    }

    [Fact]
    public async Task DeclaredOversize_Skipped_BeforeDownload()
    {
        Metrics.ResetForTests();
        var downloader = new FakeAttachmentDownloader((_, _) =>
            DownloadResult.Success(new byte[] { 1 }));
        var status = await Enricher(Config(maxBytes: 1000), downloader)
            .EnrichAsync(BaseItem(), Record("big.txt", size: 5000), AttachmentConfig());
        Assert.StartsWith("skipped:oversize", status);
        Assert.Empty(downloader.Requests);
        Assert.Equal(1, Metrics.AttachmentsSkipped);
        Metrics.ResetForTests();
    }

    [Fact]
    public async Task StreamedOversize_Skipped_CapPassedToDownloader()
    {
        var downloader = new FakeAttachmentDownloader((_, maxBytes) => DownloadResult.TooLarge());
        var status = await Enricher(Config(maxBytes: 2048), downloader)
            .EnrichAsync(BaseItem(), Record("big.txt"), AttachmentConfig());
        Assert.Equal("skipped:oversize", status);
        Assert.Equal(2048, downloader.Requests.Single().MaxBytes);
    }

    [Fact]
    public async Task DownloadFailure_Skipped_WithReason()
    {
        var downloader = new FakeAttachmentDownloader((_, _) => DownloadResult.Failed("http-403"));
        var status = await Enricher(Config(), downloader)
            .EnrichAsync(BaseItem(), Record("a.txt"), AttachmentConfig());
        Assert.Equal("skipped:download:http-403", status);
    }

    [Fact]
    public async Task NoUrl_Skipped()
    {
        var downloader = new FakeAttachmentDownloader((_, _) =>
            DownloadResult.Success(new byte[] { 1 }));
        var status = await Enricher(Config(), downloader)
            .EnrichAsync(BaseItem(), Record("a.txt", url: null), AttachmentConfig());
        Assert.Equal("skipped:no-url", status);
        Assert.Empty(downloader.Requests);
    }

    [Fact]
    public async Task NoExtractableText_Skipped()
    {
        var downloader = new FakeAttachmentDownloader((_, _) =>
            DownloadResult.Success(Encoding.UTF8.GetBytes("   \n\t  ")));  // whitespace only
        var status = await Enricher(Config(), downloader)
            .EnrichAsync(BaseItem(), Record("blank.txt"), AttachmentConfig());
        Assert.Equal("skipped:no-text", status);
    }

    [Fact]
    public void ParseAllowedTypes_ExtensionsAndMimes()
    {
        var set = AppConfig.ParseAllowedTypes("pdf, .DOCX ; application/json");
        Assert.Contains("pdf", set);
        Assert.Contains("docx", set);
        Assert.Contains("json", set);
        Assert.DoesNotContain("txt", set);

        Assert.Same(AppConfig.DefaultAttachmentTypes, AppConfig.ParseAllowedTypes(null));
    }

    [Fact]
    public async Task ReadCapped_DetectsOversizeMidStream()
    {
        using var stream = new MemoryStream(new byte[5000]);
        var result = await ClarizenClient.ReadCappedAsync(stream, 1000, CancellationToken.None);
        Assert.True(result.Oversize);

        using var small = new MemoryStream(Encoding.UTF8.GetBytes("hi"));
        var ok = await ClarizenClient.ReadCappedAsync(small, 1000, CancellationToken.None);
        Assert.True(ok.Ok);
        Assert.Equal(2, ok.Content.Length);
    }
}
