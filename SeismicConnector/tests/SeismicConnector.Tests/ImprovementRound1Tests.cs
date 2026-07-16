// Improvement round 1: hardened PDF extraction (hex strings, ", octal,
// UTF-16BE), XLSX extraction, per-format extraction metrics, engagement/usage
// enrichment, and the full-inventory drift reconciliation sweep.

using System.IO.Compression;
using System.Text;
using SeismicConnector.Graph;
using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector.Tests;

// ── PDF extractor hardening ──────────────────────────────────────────────────

public class PdfExtractorHardeningTests
{
    private static string ExtractPdf(string body)
    {
        var pdf = "%PDF-1.4\n1 0 obj\n<< /Length 99 >>\nstream\n" + body + "\nendstream\nendobj\n%%EOF";
        return new PdfTextExtractor().Extract(Encoding.Latin1.GetBytes(pdf));
    }

    [Fact]
    public void HexString_Tj_IsDecoded()
    {
        // "Hi Joe" = 48 69 20 4A 6F 65
        Assert.Equal("Hi Joe", ExtractPdf("BT <4869204A6F65> Tj ET"));
    }

    [Fact]
    public void HexString_WithWhitespaceAndOddLength_IsDecoded()
    {
        // Whitespace inside hex strings is legal; odd digit count pads a 0.
        // 41 42 43 = "ABC"; trailing "4" pads to 0x40 = "@".
        var text = ExtractPdf("BT <41 42\n434> Tj ET");
        Assert.Equal("ABC@", text);
    }

    [Fact]
    public void DoubleQuoteOperator_IsCaptured()
    {
        // aw ac (text) "  — the move-set-show operator.
        Assert.Equal("quoted show", ExtractPdf("BT 2 1 (quoted show) \" ET"));
    }

    [Fact]
    public void OctalEscapes_AreDecoded()
    {
        // \101 = 'A', \102 = 'B'; \12 = LF (whitespace-normalized away).
        Assert.Equal("AB C", ExtractPdf(@"BT (\101\102\12C) Tj ET"));
    }

    [Fact]
    public void LineContinuation_EmitsNothing()
    {
        Assert.Equal("splitword", ExtractPdf("BT (split\\\nword) Tj ET"));
    }

    [Fact]
    public void Utf16BeLiteral_IsDecoded()
    {
        // BOM FE FF then UTF-16BE "Hi" (0048 0069 → \0feh...) — build via escapes.
        var body = "BT (\xFE\xFF\0H\0i) Tj ET";
        Assert.Equal("Hi", ExtractPdf(body));
    }

    [Fact]
    public void Utf16BeHexString_IsDecoded()
    {
        // FEFF 0053 0065 0069 0073 006D 0069 0063 = "Seismic"
        Assert.Equal("Seismic", ExtractPdf("BT <FEFF0053006500690073006D00690063> Tj ET"));
    }

    [Fact]
    public void TjArray_MixesLiteralAndHexElements()
    {
        var text = ExtractPdf("BT [ (Win) -20 <2052617465> (s) ] TJ ET");
        Assert.Equal("Win Rates", text);
    }

    [Fact]
    public void DecodeLiteralString_SymbolEscapes()
    {
        Assert.Equal("(a)\\b\tc", PdfTextExtractor.DecodeLiteralString(@"\(a\)\\b\tc"));
    }

    [Fact]
    public void LegacyLiteralPath_StillWorks()
    {
        Assert.Equal("Enablement playbook",
            ExtractPdf("BT (Enablement) Tj (playbook) Tj ET"));
    }
}

// ── XLSX extraction ──────────────────────────────────────────────────────────

public class XlsxExtractorTests
{
    private static byte[] BuildZip(params (string Path, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = zip.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    [Fact]
    public void Xlsx_SharedStrings_AreExtracted()
    {
        var xlsx = BuildZip(("xl/sharedStrings.xml", """
            <?xml version="1.0"?>
            <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="2" uniqueCount="2">
              <si><t>Pipeline coverage</t></si>
              <si><t>Q3 targets</t></si>
            </sst>
            """));
        var extractor = new OpenXmlTextExtractor();
        Assert.True(extractor.CanExtract("xlsx"));
        var text = extractor.Extract(xlsx);
        Assert.Contains("Pipeline coverage", text);
        Assert.Contains("Q3 targets", text);
    }

    [Fact]
    public void Xlsx_InlineStrings_AreExtracted()
    {
        var xlsx = BuildZip(("xl/worksheets/sheet1.xml", """
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>Discount ladder</t></is></c></row>
              </sheetData>
            </worksheet>
            """));
        Assert.Contains("Discount ladder", new OpenXmlTextExtractor().Extract(xlsx));
    }

    [Fact]
    public void Composite_RoutesXlsxFormats()
    {
        Assert.True(CompositeExtractor.Default.CanExtract("xlsx"));
        Assert.True(CompositeExtractor.Default.CanExtract("excel"));
    }
}

// ── per-format extraction metrics ────────────────────────────────────────────

public class ExtractionMetricsTests : IDisposable
{
    public ExtractionMetricsTests() => Metrics.ResetForTests();

    public void Dispose() => Metrics.ResetForTests();

    [Fact]
    public void SuccessAndFailure_AreCountedPerFormat()
    {
        var composite = CompositeExtractor.Default;
        Assert.Equal("hello", composite.ExtractFor("txt", Encoding.UTF8.GetBytes("hello")));
        Assert.Equal("", composite.ExtractFor("pdf", Encoding.UTF8.GetBytes("not a pdf")));
        Assert.Equal("", composite.ExtractFor("video", Encoding.UTF8.GetBytes("x")));  // unsupported → no attempt

        var attempts = Metrics.ExtractionAttemptsSnapshot;
        var successes = Metrics.ExtractionSuccessesSnapshot;
        Assert.Equal(1, attempts["txt"]);
        Assert.Equal(1, successes["txt"]);
        Assert.Equal(1, attempts["pdf"]);
        Assert.False(successes.ContainsKey("pdf"));
        Assert.False(attempts.ContainsKey("video"));
    }

    [Fact]
    public void Prometheus_RendersLabeledExtractionCounters()
    {
        Metrics.RecordExtraction("pdf", success: true);
        Metrics.RecordExtraction("pdf", success: false);
        Metrics.RecordExtraction("DOCX", success: true);  // normalized to lowercase

        var text = Metrics.RenderPrometheus();
        Assert.Contains("seismic_connector_extraction_attempts_total{format=\"pdf\"} 2", text);
        Assert.Contains("seismic_connector_extraction_success_total{format=\"pdf\"} 1", text);
        Assert.Contains("seismic_connector_extraction_attempts_total{format=\"docx\"} 1", text);
        Assert.Contains("# TYPE seismic_connector_extraction_attempts_total counter", text);
    }

    [Fact]
    public void WeirdFormatLabels_AreSanitized()
    {
        Metrics.RecordExtraction("../etc{passwd}", success: false);
        Metrics.RecordExtraction("", success: false);
        var attempts = Metrics.ExtractionAttemptsSnapshot;
        Assert.Equal(2, attempts["other"]);
    }

    [Fact]
    public void DriftGauge_Renders()
    {
        Metrics.SetLastDriftFindings(7);
        Assert.Contains("seismic_connector_last_drift_findings 7", Metrics.RenderPrometheus());
    }
}

// ── engagement/usage enrichment ──────────────────────────────────────────────

public class UsageEnrichmentTests
{
    [Fact]
    public void Transformer_AddsUsageProperties_WhenProvided()
    {
        var transformer = new ItemTransformer();
        var usage = new SeismicContentUsage
        {
            ContentId = "c1",
            ViewCount = 100,
            DownloadCount = 10,
            ShareCount = 5,
        };
        var acl = new AclResult(new[] { AclEntry.GrantUser("e1") }, 0, false);
        var item = transformer.Transform(TestContent.Make("c1"), null, null, acl, usage);

        var properties = item["properties"]!;
        Assert.Equal(100, properties["viewCount"]!.GetValue<long>());
        Assert.Equal(10, properties["downloadCount"]!.GetValue<long>());
        Assert.Equal(5, properties["shareCount"]!.GetValue<long>());
        Assert.Equal(100 + 20 + 15, properties["popularityScore"]!.GetValue<long>());
    }

    [Fact]
    public void Transformer_OmitsUsageProperties_WhenAbsent()
    {
        var transformer = new ItemTransformer();
        var acl = new AclResult(new[] { AclEntry.GrantUser("e1") }, 0, false);
        var item = transformer.Transform(TestContent.Make("c1"), null, null, acl);
        Assert.Null(item["properties"]!["viewCount"]);
        Assert.Null(item["properties"]!["popularityScore"]);
    }

    [Fact]
    public async Task Pipeline_EnrichesItems_WhenEnabled()
    {
        using var harness = new PipelineHarness(enrichUsage: true);
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1"));
        harness.Seismic.UsageByContentId["c1"] = new SeismicContentUsage
        {
            ContentId = "c1",
            ViewCount = 42,
            ShareCount = 1,
        };

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        var body = harness.LastPutBody("c1")!;
        Assert.Equal(42, body["properties"]!["viewCount"]!.GetValue<long>());
        Assert.Equal(45, body["properties"]!["popularityScore"]!.GetValue<long>());
    }

    [Fact]
    public async Task Pipeline_SkipsEnrichment_WhenDisabled()
    {
        using var harness = new PipelineHarness();  // enrichment off (default)
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1"));
        harness.Seismic.UsageByContentId["c1"] = new SeismicContentUsage { ContentId = "c1", ViewCount = 42 };

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        Assert.Equal(0, harness.Seismic.UsageFetches);  // analytics never called
        Assert.Null(harness.LastPutBody("c1")!["properties"]!["viewCount"]);
    }

    [Fact]
    public async Task UsageFetchFailure_DoesNotFailTheCrawl()
    {
        using var harness = new PipelineHarness(enrichUsage: true);
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1"));
        harness.Seismic.UsageFetchThrows = true;

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));
        Assert.Contains("c1", harness.PutItemIds());
        Assert.Null(harness.LastPutBody("c1")!["properties"]!["viewCount"]);
    }
}

// ── drift reconciliation sweep ───────────────────────────────────────────────

public class DriftSweepTests
{
    private static DriftSweep Sweep(PipelineHarness harness, ExclusionRules? rules = null) =>
        new(harness.Config, harness.Seismic, harness.Store, harness.Pipeline,
            rules is null ? null : new ExclusionFilter(rules));

    [Fact]
    public async Task InSync_ReportsNoDrift()
    {
        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1"));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));

        var summary = await Sweep(harness).RunAsync(repair: false);
        Assert.Equal(0, summary.Total);
        Assert.False(summary.HasUnrepairedDrift);
    }

    [Fact]
    public async Task OrphanedInIndex_IsDetected_AndRepairedByWithdrawal()
    {
        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("gone"));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));
        harness.Seismic.ContentsByTeamsite["ts1"].Clear();  // vanished from source

        // Report-only first: detected, not touched.
        var summary = await Sweep(harness).RunAsync(repair: false);
        var finding = Assert.Single(summary.Findings);
        Assert.Equal("orphaned-in-index", finding.Kind);
        Assert.False(finding.Repaired);
        Assert.True(summary.HasUnrepairedDrift);
        Assert.NotNull(harness.Store.GetTrackedItem("gone"));

        // Repair: withdrawn from Graph, dropped from the store.
        summary = await Sweep(harness).RunAsync(repair: true);
        Assert.True(summary.Findings.Single().Repaired);
        Assert.False(summary.HasUnrepairedDrift);
        Assert.Contains("gone", harness.DeletedItemIds);
        Assert.Null(harness.Store.GetTrackedItem("gone"));
    }

    [Fact]
    public async Task MissingFromIndex_IsDetected_AndRepairedByIngest()
    {
        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("new-item"));  // never crawled

        var summary = await Sweep(harness).RunAsync(repair: true);
        var finding = Assert.Single(summary.Findings);
        Assert.Equal("missing-from-index", finding.Kind);
        Assert.True(finding.Repaired);
        Assert.Contains("new-item", harness.PutItemIds());
        Assert.Equal("ingested", harness.Store.GetTrackedItem("new-item")!.Status);
    }

    [Fact]
    public async Task VersionDrift_IsDetected_AndRepairedInPlace()
    {
        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", versionId: "v1"));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));

        harness.Seismic.ContentsByTeamsite["ts1"].Clear();
        harness.AddContent(TestContent.Make("c1", versionId: "v2"));

        var summary = await Sweep(harness).RunAsync(repair: true);
        var finding = Assert.Single(summary.Findings);
        Assert.Equal("version-drift", finding.Kind);
        Assert.True(finding.Repaired);
        Assert.Equal("v2", harness.Store.GetTrackedItem("c1")!.VersionId);
        Assert.Empty(harness.DeletedItemIds);  // update in place, not delete+add
    }

    [Fact]
    public async Task ExcludedDrift_IsWithdrawn_AndMarkedExcluded()
    {
        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", properties: new List<SeismicProperty>
        {
            new() { Name = "classification", Value = "MNE" },
        }));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));  // open rules → ingested
        Assert.Equal("ingested", harness.Store.GetTrackedItem("c1")!.Status);

        // Rules tightened AFTER ingestion (config change, no crawl yet).
        var rules = new ExclusionRules
        {
            ExcludedFlags = new List<string> { "MNE" },
            FlagProperties = new List<string> { "classification" },
        };
        var summary = await Sweep(harness, rules).RunAsync(repair: true);
        var finding = Assert.Single(summary.Findings);
        Assert.Equal("excluded-drift", finding.Kind);
        Assert.Contains("c1", harness.DeletedItemIds);
        Assert.Equal("excluded", harness.Store.GetTrackedItem("c1")!.Status);
    }

    [Fact]
    public async Task ReinstatedContent_PreviouslyExcluded_IsReingested()
    {
        var rules = new ExclusionRules
        {
            ExcludedFlags = new List<string> { "MNE" },
            FlagProperties = new List<string> { "classification" },
        };
        using var harness = new PipelineHarness(rules);
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", properties: new List<SeismicProperty>
        {
            new() { Name = "classification", Value = "MNE" },
        }));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));
        Assert.Equal("excluded", harness.Store.GetTrackedItem("c1")!.Status);

        // Flag removed in Seismic → the item becomes eligible again.
        harness.Seismic.ContentsByTeamsite["ts1"].Clear();
        harness.AddContent(TestContent.Make("c1"));

        var summary = await Sweep(harness, rules).RunAsync(repair: true);
        var finding = Assert.Single(summary.Findings);
        Assert.Equal("missing-from-index", finding.Kind);
        Assert.Contains("previously excluded", finding.Detail);
        Assert.Equal("ingested", harness.Store.GetTrackedItem("c1")!.Status);
    }

    [Fact]
    public async Task DriftReportFile_HasFindingsAndSummary()
    {
        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("missing1"));
        var reportPath = Path.Combine(harness.State.Dir, "drift_report_test.jsonl");

        var summary = await Sweep(harness).RunAsync(repair: false, reportPath);
        Assert.Equal(1, summary.Total);

        var lines = File.ReadAllLines(reportPath).Where(l => l.Length > 0).ToList();
        Assert.Equal(2, lines.Count);  // 1 finding + summary
        var finding = System.Text.Json.Nodes.JsonNode.Parse(lines[0])!;
        Assert.Equal("missing-from-index", finding["kind"]?.GetValue<string>());
        Assert.Equal("missing1", finding["item_id"]?.GetValue<string>());
        Assert.False(finding["repaired"]!.GetValue<bool>());
        var trailer = System.Text.Json.Nodes.JsonNode.Parse(lines[1])!;
        Assert.Equal("summary", trailer["kind"]?.GetValue<string>());
        Assert.Equal(1, trailer["total"]?.GetValue<int>());
        Assert.Equal(1, trailer["by_kind"]?["missing-from-index"]?.GetValue<int>());
        Assert.Equal(1, Metrics.LastDriftFindings);
    }

    [Fact]
    public void CliParser_ReconcileCommand()
    {
        var parser = Commands.CommandRegistry.BuildParser();
        var parsed = parser.ParseArgs(new[] { "reconcile", "--repair" });
        Assert.Equal("reconcile", parsed.Command);
        Assert.True(parsed.GetFlag("repair"));
    }
}
