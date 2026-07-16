// Improvement round 2: LiveDoc field/variable metadata indexing.
//
// LiveDoc (generated-document template) content exposes personalization
// inputs — fields/variables like "client name", "product", "region". With
// LIVEDOC_FIELD_INDEXING=true the connector fetches those inputs for LiveDoc
// items and indexes their names/labels as refinable properties AND into the
// searchable content text, so a template is findable by its inputs. The extra
// API call is gated: LiveDoc items only, feature enabled only. Best-effort:
// any fetch failure indexes the item without the field metadata.

using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector.Tests;

// ── LiveDoc detection ────────────────────────────────────────────────────────

public class LiveDocDetectionTests
{
    [Theory]
    [InlineData("livedoc")]
    [InlineData("LiveDoc")]
    [InlineData("LIVEDOC")]
    [InlineData(".livedoc")]
    [InlineData("live-doc")]
    [InlineData("livedocument")]
    public void LiveDocFormats_AreDetected(string format)
    {
        Assert.True(TestContent.Make("c1", format: format).IsLiveDoc);
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("docx")]
    [InlineData("pptx")]
    [InlineData("video")]
    [InlineData("")]
    public void NonLiveDocFormats_AreNotDetected(string format)
    {
        Assert.False(TestContent.Make("c1", format: format).IsLiveDoc);
    }

    [Theory]
    [InlineData("region", null, "region")]
    [InlineData("region", "Sales Region", "Sales Region")]
    [InlineData("", "  Client Name  ", "Client Name")]
    [InlineData("", "", "")]
    public void DisplayName_PrefersLabelThenNameTrimmed(string name, string? label, string expected)
    {
        var field = new SeismicLiveDocField { Name = name, Label = label };
        Assert.Equal(expected, field.DisplayName);
    }
}

// ── field metadata → properties + content mapping ────────────────────────────

public class LiveDocTransformTests
{
    private static readonly AclResult Grant =
        new(new[] { AclEntry.GrantUser("e1") }, 0, false);

    private static List<SeismicLiveDocField> Fields(params (string Name, string? Label)[] specs) =>
        specs.Select(s => new SeismicLiveDocField { Name = s.Name, Label = s.Label }).ToList();

    [Fact]
    public void Fields_BecomeRefinableProperties()
    {
        var item = new ItemTransformer().Transform(
            TestContent.Make("c1", format: "livedoc"), null, null, Grant,
            liveDocFields: Fields(("clientName", "Client Name"), ("product", "Product"), ("region", null)));

        var props = item["properties"]!;
        Assert.True(props["isLiveDoc"]!.GetValue<bool>());
        Assert.Equal(3, props["liveDocFieldCount"]!.GetValue<int>());
        var names = props["liveDocFieldNames"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "Client Name", "Product", "region" }, names);  // label wins, order preserved
    }

    [Fact]
    public void FieldLabels_AreWovenIntoContentText()
    {
        var item = new ItemTransformer().Transform(
            TestContent.Make("c1", format: "livedoc"), null, null, Grant,
            liveDocFields: Fields(("region", "Sales Region"), ("q", "Quarter")));

        var text = item["content"]!["value"]!.GetValue<string>();
        Assert.Contains("LiveDoc fields:", text);
        Assert.Contains("Sales Region", text);
        Assert.Contains("Quarter", text);
    }

    [Fact]
    public void DuplicateFieldTokens_AreDeduplicated_CaseInsensitive()
    {
        var item = new ItemTransformer().Transform(
            TestContent.Make("c1", format: "livedoc"), null, null, Grant,
            liveDocFields: Fields(("region", "Region"), ("region2", "region"), ("product", "Product")));

        var names = item["properties"]!["liveDocFieldNames"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "Region", "Product" }, names);
        Assert.Equal(2, item["properties"]!["liveDocFieldCount"]!.GetValue<int>());
    }

    [Fact]
    public void EmptyOrWhitespaceFields_AreIgnored()
    {
        var item = new ItemTransformer().Transform(
            TestContent.Make("c1", format: "livedoc"), null, null, Grant,
            liveDocFields: Fields(("", "   "), ("region", "Region")));

        var names = item["properties"]!["liveDocFieldNames"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "Region" }, names);
    }

    [Fact]
    public void NoFields_OmitsFieldPropertiesButKeepsIsLiveDoc()
    {
        var item = new ItemTransformer().Transform(
            TestContent.Make("c1", format: "livedoc"), null, null, Grant,
            liveDocFields: new List<SeismicLiveDocField>());

        Assert.True(item["properties"]!["isLiveDoc"]!.GetValue<bool>());
        Assert.Null(item["properties"]!["liveDocFieldNames"]);
        Assert.Null(item["properties"]!["liveDocFieldCount"]);
    }

    [Fact]
    public void NonLiveDoc_IsLiveDocFalse_NoFieldProperties()
    {
        var item = new ItemTransformer().Transform(
            TestContent.Make("c1", format: "pdf"), null, null, Grant);
        Assert.False(item["properties"]!["isLiveDoc"]!.GetValue<bool>());
        Assert.Null(item["properties"]!["liveDocFieldNames"]);
    }

    [Fact]
    public void WovenFields_AppendToExtractedText_NotReplaceIt()
    {
        var content = TestContent.Make("c1", format: "livedoc");  // description = "Description of c1"
        var item = new ItemTransformer().Transform(
            content, null, null, Grant, liveDocFields: Fields(("region", "Region")));
        var text = item["content"]!["value"]!.GetValue<string>();
        Assert.Contains("Description of c1", text);
        Assert.Contains("LiveDoc fields: Region", text);
    }
}

// ── pipeline gating, toggle, and resilience ──────────────────────────────────

public class LiveDocPipelineTests
{
    [Fact]
    public async Task Enabled_LiveDocItem_IsEnrichedAndFieldsFetched()
    {
        using var harness = new PipelineHarness(liveDocFieldIndexing: true);
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("ld1", format: "livedoc"));
        harness.Seismic.LiveDocFieldsByContentId["ld1"] = new List<SeismicLiveDocField>
        {
            new() { Name = "clientName", Label = "Client Name" },
            new() { Name = "region", Label = "Region" },
        };

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        Assert.Equal(new[] { "ld1" }, harness.Seismic.LiveDocFieldFetches);
        var props = harness.LastPutBody("ld1")!["properties"]!;
        Assert.True(props["isLiveDoc"]!.GetValue<bool>());
        Assert.Equal(2, props["liveDocFieldCount"]!.GetValue<int>());
        Assert.Contains("Client Name",
            harness.LastPutBody("ld1")!["content"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task Enabled_NonLiveDocItem_DoesNotCallTheFieldEndpoint()
    {
        using var harness = new PipelineHarness(liveDocFieldIndexing: true);
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("doc1", format: "pdf"));
        harness.AddContent(TestContent.Make("doc2", format: "pptx"));

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        Assert.Empty(harness.Seismic.LiveDocFieldFetches);  // gated: only LiveDoc items
        Assert.False(harness.LastPutBody("doc1")!["properties"]!["isLiveDoc"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Disabled_LiveDocItem_DoesNotCallTheFieldEndpoint()
    {
        using var harness = new PipelineHarness();  // LIVEDOC_FIELD_INDEXING off (default)
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("ld1", format: "livedoc"));
        harness.Seismic.LiveDocFieldsByContentId["ld1"] = new List<SeismicLiveDocField>
        {
            new() { Name = "region", Label = "Region" },
        };

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        Assert.Empty(harness.Seismic.LiveDocFieldFetches);  // zero new calls when disabled
        // isLiveDoc is still stamped (cheap, no API call), but no field metadata.
        Assert.True(harness.LastPutBody("ld1")!["properties"]!["isLiveDoc"]!.GetValue<bool>());
        Assert.Null(harness.LastPutBody("ld1")!["properties"]!["liveDocFieldNames"]);
    }

    [Fact]
    public async Task FetchFailure_DoesNotFailTheCrawl_ItemIndexedWithoutFields()
    {
        using var harness = new PipelineHarness(liveDocFieldIndexing: true);
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("ld1", format: "livedoc"));
        harness.Seismic.LiveDocFetchThrows = true;

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        Assert.Contains("ld1", harness.PutItemIds());
        var props = harness.LastPutBody("ld1")!["properties"]!;
        Assert.True(props["isLiveDoc"]!.GetValue<bool>());
        Assert.Null(props["liveDocFieldNames"]);  // no fields, but item still indexed
    }

    [Fact]
    public async Task Enabled_RecordsExtractionMetric()
    {
        Metrics.ResetForTests();
        try
        {
            using var harness = new PipelineHarness(liveDocFieldIndexing: true);
            harness.AddTeamsite("ts1");
            harness.AddContent(TestContent.Make("hit", format: "livedoc"));
            harness.AddContent(TestContent.Make("miss", format: "livedoc"));  // no fields registered
            harness.Seismic.LiveDocFieldsByContentId["hit"] = new List<SeismicLiveDocField>
            {
                new() { Name = "region", Label = "Region" },
            };

            Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

            var attempts = Metrics.ExtractionAttemptsSnapshot;
            var successes = Metrics.ExtractionSuccessesSnapshot;
            Assert.Equal(2, attempts["livedoc-fields"]);
            Assert.Equal(1, successes["livedoc-fields"]);  // only "hit" had fields
        }
        finally
        {
            Metrics.ResetForTests();
        }
    }

    [Fact]
    public async Task SingleItemPath_EnrichesLiveDocFields()
    {
        using var harness = new PipelineHarness(liveDocFieldIndexing: true);
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("ld1", format: "livedoc"));
        harness.Seismic.LiveDocFieldsByContentId["ld1"] = new List<SeismicLiveDocField>
        {
            new() { Name = "product", Label = "Product" },
        };

        Assert.True(await harness.Pipeline.IngestSingleAsync("ld1", "ts1"));
        Assert.Equal(new[] { "ld1" }, harness.Seismic.LiveDocFieldFetches);
        Assert.Equal(1, harness.LastPutBody("ld1")!["properties"]!["liveDocFieldCount"]!.GetValue<int>());
    }
}

// ── config knob ──────────────────────────────────────────────────────────────

public class LiveDocConfigTests : IDisposable
{
    private readonly string _configDir =
        Path.Combine(Path.GetTempPath(), "seismic-ld-" + Guid.NewGuid().ToString("N"));

    private static readonly (string, string)[] RequiredEnv =
    {
        ("CONNECTOR_ID", "SeismicLive"),
        ("CONNECTOR_NAME", "n"),
        ("CONNECTOR_DESCRIPTION", "d"),
        ("SEISMIC_TENANT", "contoso"),
        ("SEISMIC_CLIENT_ID", "c"),
        ("SECRET_SEISMIC_CLIENT_SECRET", "s"),
        ("AAD_APP_TENANT_ID", "t"),
        ("AAD_APP_CLIENT_ID", "c"),
        ("SECRET_AAD_APP_CLIENT_SECRET", "s"),
    };

    public LiveDocConfigTests()
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(Path.Combine(_configDir, "schema.json"),
            """{"objects":[{"name":"ContentItem","enabled":true},{"name":"Library","enabled":true}]}""");
        foreach (var (key, value) in RequiredEnv)
            Environment.SetEnvironmentVariable(key, value);
    }

    public void Dispose()
    {
        foreach (var (key, _) in RequiredEnv)
            Environment.SetEnvironmentVariable(key, null);
        Environment.SetEnvironmentVariable("LIVEDOC_FIELD_INDEXING", null);
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void DefaultsOff()
    {
        Assert.False(SeismicConnector.Config.AppConfig.Load(_configDir).Seismic.LiveDocFieldIndexing);
    }

    [Fact]
    public void EnabledByEnv()
    {
        Environment.SetEnvironmentVariable("LIVEDOC_FIELD_INDEXING", "true");
        Assert.True(SeismicConnector.Config.AppConfig.Load(_configDir).Seismic.LiveDocFieldIndexing);
    }
}
