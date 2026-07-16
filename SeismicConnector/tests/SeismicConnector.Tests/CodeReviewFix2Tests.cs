// Regression tests for the second code-review round (2026-07):
//
//   MEDIUM — extractor bounds each unit but not the AGGREGATE; the PDF operator
//            regex had no match timeout (a hostile doc stalled the serial crawl
//            worker for minutes).
//   LOW-1  — a brand-NEW or LIBRARY item that is Unresolved (source has
//            principals, none map) must NOT be granted everyone under
//            SEISMIC_FALLBACK_ACL=tenant.
//   LOW-2  — under fallback=skip an already-ingested item that becomes
//            Unresolved must be left in place (no withdraw/re-ingest churn).
//   LOW-3  — the webhook event queue is depth-capped (drop-oldest).

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using SeismicConnector.Config;
using SeismicConnector.Seismic;

namespace SeismicConnector.Tests;

// ── MEDIUM: aggregate inflation ceiling + PDF operator match timeout ──────────

public class AggregateInflationTests
{
    private static byte[] ZlibCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    private static byte[] PdfWithStreams(IEnumerable<byte[]> streamBodies)
    {
        using var payload = new MemoryStream();
        void Ascii(string s) => payload.Write(Encoding.ASCII.GetBytes(s));
        Ascii("%PDF-1.4\n");
        foreach (var body in streamBodies)
        {
            Ascii("stream\n");
            payload.Write(body);
            Ascii("\nendstream\n");
        }
        Ascii("%%EOF");
        return payload.ToArray();
    }

    [Fact]
    public void Pdf_ManyEmptyHighRatioStreams_StopAtAggregateCeiling()
    {
        // Each empty bomb inflates to the per-stream cap (32 MB) but emits NO
        // text, so the text-buffer cap never trips. Two of them reach the 64 MB
        // aggregate ceiling; a THIRD stream carrying real text is never reached.
        var bomb = ZlibCompress(new byte[ExtractionLimits.MaxInflatedStreamBytes]);
        var realText = Encoding.ASCII.GetBytes("(AGGREGATE_MARKER) Tj");
        var payload = PdfWithStreams(new[] { bomb, bomb, realText });

        var text = new PdfTextExtractor().Extract(payload);

        // The aggregate cap fired before the text stream was inflated/scanned.
        Assert.DoesNotContain("AGGREGATE_MARKER", text);

        // Control: the SAME text stream, placed first, IS extracted — proving the
        // marker is extractable and only the aggregate cap suppressed it above.
        var controlText = new PdfTextExtractor().Extract(PdfWithStreams(new[] { realText }));
        Assert.Contains("AGGREGATE_MARKER", controlText);
    }

    [Fact]
    public void OpenXml_ManyNoTextParts_StopAtAggregateCeiling()
    {
        // Recognised text parts that contain NO text nodes (a giant comment):
        // each inflates ~24 MB, so a handful cross the 64 MB aggregate ceiling
        // before a final slide carrying real text is opened.
        const int bigChars = 24_000_000;
        var bigParts = (int)(ExtractionLimits.MaxAggregateInflatedBytes / bigChars) + 1;

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < bigParts; i++)
            {
                // Zero-padded name so the big parts sort BEFORE the text slide.
                WriteEntry(zip, $"ppt/slides/slide{i:0000}.xml",
                    "<sld xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><!--"
                    + new string('x', bigChars) + "--></sld>");
            }
            WriteEntry(zip, "ppt/slides/slide9999.xml",
                "<sld xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">"
                + "<a:t>AGGREGATE_MARKER</a:t></sld>");
        }

        var text = new OpenXmlTextExtractor().Extract(buffer.ToArray());
        Assert.DoesNotContain("AGGREGATE_MARKER", text);

        // Control: the text slide alone IS extracted.
        var control = MakeZip("ppt/slides/slide1.xml",
            "<sld xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">"
            + "<a:t>AGGREGATE_MARKER</a:t></sld>");
        Assert.Contains("AGGREGATE_MARKER", new OpenXmlTextExtractor().Extract(control));
    }

    [Fact]
    public async Task Pdf_PathologicalTjStream_DoesNotHang_AndKeepsPriorText()
    {
        // A clean text stream, then a hostile stream: thousands of unterminated
        // '[' drive the TJ-array scan quadratic (minutes without a timeout). The
        // per-scan match timeout aborts it; the earlier text survives and the
        // call returns promptly.
        var clean = Encoding.ASCII.GetBytes("(hello timeout world) Tj");
        var hostile = Encoding.ASCII.GetBytes(new string('[', 500_000));
        var payload = PdfWithStreams(new[] { clean, hostile });

        var extractor = new PdfTextExtractor(TimeSpan.FromMilliseconds(50));
        var task = Task.Run(() => extractor.Extract(payload));
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15)));

        Assert.Same(task, finished);  // completed → the match timeout prevented the hang
        Assert.Contains("hello timeout world", await task);
    }

    [Fact]
    public void Pdf_HealthyMultiStreamDoc_IsNotTruncatedByAggregateCeiling()
    {
        // A normal document with several small streams must extract every one —
        // the aggregate ceiling sits far above ordinary payload sizes.
        var streams = Enumerable.Range(0, 20)
            .Select(i => Encoding.ASCII.GetBytes($"(word{i}) Tj"))
            .ToArray();

        var text = new PdfTextExtractor().Extract(PdfWithStreams(streams));

        Assert.Contains("word0", text);
        Assert.Contains("word19", text);
    }

    private static byte[] MakeZip(string entryName, string xml)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, entryName, xml);
        }
        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }
}

// ── LOW-1 / LOW-2: Unresolved must never widen (new/library) nor churn (skip) ─

public class UnresolvedNeverWidensOrChurnsTests
{
    private static List<SeismicPermission> Perms(params string[] ids) =>
        ids.Select(id => new SeismicPermission { PrincipalId = id, PrincipalType = "user" }).ToList();

    [Fact]
    public async Task NewItem_TenantFallback_Unresolved_IsNotGrantedEveryone()
    {
        // LOW-1: a brand-NEW item whose principals exist but don't map must not
        // fall through to the everyone-fallback during a transient identity gap.
        using var harness = new PipelineHarness(fallbackAcl: "tenant");
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", permissions: Perms("ghost-only")));

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        Assert.DoesNotContain("c1", harness.PutItemIds());
        Assert.True(harness.Pipeline.Stats.AclSkipped >= 1);
        // Never recorded as an ingested/everyone item — left for a later crawl.
        var tracked = harness.Store.GetTrackedItem("c1");
        Assert.True(tracked is null || tracked.Status != "ingested");
    }

    [Fact]
    public async Task LibraryItem_TenantFallback_Unresolved_IsNotGrantedEveryone()
    {
        // LOW-1: an unresolved LIBRARY item was re-PUT with the everyone-ACL every
        // crawl (IngestLibrariesAsync only checked acl.Skipped).
        using var harness = new PipelineHarness(fallbackAcl: "tenant");
        harness.AddTeamsite("ts1", permissions: Perms("ghost-only"));

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "Library"));

        Assert.DoesNotContain("lib-ts1", harness.PutItemIds());
        Assert.True(harness.Pipeline.Stats.AclSkipped >= 1);
    }

    [Fact]
    public async Task LibraryItem_GenuinelyNoPrincipals_TenantFallback_StillFollowsPolicy()
    {
        // Control: a teamsite with genuinely no principals is NOT Unresolved and
        // still indexes the library item under the documented tenant policy.
        using var harness = new PipelineHarness(fallbackAcl: "tenant");
        harness.AddTeamsite("ts1", permissions: new List<SeismicPermission>());

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "Library"));

        Assert.Contains("lib-ts1", harness.PutItemIds());
        var acl = harness.LastPutBody("lib-ts1")!["acl"]!.AsArray();
        Assert.Equal("everyone", acl[0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task IngestedItem_NewVersionDuringIdentityGap_Skip_IsLeftInPlace()
    {
        // LOW-2: fallback=skip. An ingested item gets a new version WHILE its
        // principals stop mapping. The old order withdrew it (acl-unmappable) then
        // re-ingested on recovery. It must instead be left exactly as-is.
        using var harness = new PipelineHarness();  // fallback skip
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", versionId: "v1"));  // seismic-user-1 maps
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));
        Assert.Equal("ingested", harness.Store.GetTrackedItem("c1")!.Status);

        harness.Seismic.ContentsByTeamsite["ts1"].Clear();
        harness.AddContent(TestContent.Make("c1", versionId: "v2", permissions: Perms("ghost-only")));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        // No withdraw/re-ingest churn: still tracked at v1, never deleted.
        Assert.DoesNotContain("c1", harness.DeletedItemIds);
        var tracked = harness.Store.GetTrackedItem("c1")!;
        Assert.Equal("ingested", tracked.Status);
        Assert.Equal("v1", tracked.VersionId);
    }

    [Fact]
    public async Task IngestedItem_GenuinelyEmptiedPrincipals_Skip_StillWithdraws()
    {
        // Guard: the reorder must NOT break the genuine-skip branch — an item
        // whose principals are truly emptied (not an identity gap) under
        // fallback=skip is no longer indexable and IS withdrawn.
        using var harness = new PipelineHarness();  // fallback skip
        harness.AddTeamsite("ts1");  // teamsite has no principals either
        harness.AddContent(TestContent.Make("c1", versionId: "v1"));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));
        Assert.Equal("ingested", harness.Store.GetTrackedItem("c1")!.Status);

        harness.Seismic.ContentsByTeamsite["ts1"].Clear();
        harness.AddContent(TestContent.Make("c1", versionId: "v2",
            permissions: new List<SeismicPermission>()));  // genuinely no principals
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        Assert.Contains("c1", harness.DeletedItemIds);
        Assert.Null(harness.Store.GetTrackedItem("c1"));
    }
}

// ── LOW-3: webhook event queue is depth-capped (drop-oldest) ─────────────────

public class WebhookQueueCapTests
{
    [Fact]
    public void EnqueueCapped_BoundsDepth_DropsOldest()
    {
        var queue = new ConcurrentQueue<ContentEvent>();
        const int cap = 5;
        const int extra = 20;
        var totalDropped = 0;

        for (var i = 0; i < cap + extra; i++)
        {
            totalDropped += WebhookReceiver.EnqueueCapped(
                queue, new ContentEvent { Type = "contentPublished", ContentId = $"c{i}" }, cap);
        }

        Assert.Equal(cap, queue.Count);
        Assert.Equal(extra, totalDropped);

        // Drop-oldest: the survivors are the NEWEST `cap` events.
        var survivors = queue.Select(e => e.ContentId).ToList();
        Assert.Equal(Enumerable.Range(extra, cap).Select(i => $"c{i}"), survivors);
        Assert.DoesNotContain("c0", survivors);
    }

    [Fact]
    public void EnqueueCapped_UnderCap_DropsNothing()
    {
        var queue = new ConcurrentQueue<ContentEvent>();
        for (var i = 0; i < WebhookReceiver.MaxQueuedEvents; i++)
        {
            Assert.Equal(0, WebhookReceiver.EnqueueCapped(
                queue, new ContentEvent { Type = "contentPublished", ContentId = $"c{i}" },
                WebhookReceiver.MaxQueuedEvents));
        }
        Assert.Equal(WebhookReceiver.MaxQueuedEvents, queue.Count);
    }
}
