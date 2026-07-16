// Dependency-free content extractor: one test per supported format, built from
// fixtures synthesized in-test (real OOXML zips, a hand-built PDF) so nothing
// external is needed and the round-trip is genuine.

using System.IO.Compression;
using System.Text;
using ClarizenConnector.Content;

namespace ClarizenConnector.Tests;

public class ContentExtractorTests
{
    private readonly ContentExtractor _extractor = new();

    // ── Plain text family ────────────────────────────────────────────────────

    [Fact]
    public void PlainText_Utf8_Extracted()
    {
        var bytes = Encoding.UTF8.GetBytes("Hello, Clarizen привет");
        var result = _extractor.Extract(bytes, "notes.txt", "text/plain");
        Assert.True(result.Extracted);
        Assert.Contains("Hello, Clarizen", result.Text);
        Assert.Contains("привет", result.Text);
    }

    [Fact]
    public void PlainText_Utf8Bom_Stripped()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("body")).ToArray();
        var result = _extractor.Extract(bytes, "x.txt", null);
        Assert.Equal("body", result.Text);
    }

    [Fact]
    public void InvalidUtf8_FallsBackToLatin1()
    {
        var bytes = new byte[] { 0x48, 0x69, 0xE9 };  // "Hi" + é in Latin-1 (invalid UTF-8)
        var result = _extractor.Extract(bytes, "x.txt", null);
        Assert.True(result.Extracted);
        Assert.StartsWith("Hi", result.Text);
    }

    [Fact]
    public void Csv_Extracted()
    {
        var result = _extractor.Extract(
            Encoding.UTF8.GetBytes("name,role\nGopi,PM\nSarah,Dev"), "team.csv", "text/csv");
        Assert.Contains("Gopi", result.Text);
        Assert.Contains("Dev", result.Text);
    }

    [Fact]
    public void Html_TagsStrippedEntitiesDecoded()
    {
        var html = "<html><body><h1>Title</h1><p>a &amp; b &lt; c</p></body></html>";
        var result = _extractor.Extract(Encoding.UTF8.GetBytes(html), "page.html", "text/html");
        Assert.Contains("Title", result.Text);
        Assert.Contains("a & b < c", result.Text);
        Assert.DoesNotContain("<p>", result.Text);
    }

    // ── OOXML ────────────────────────────────────────────────────────────────

    private static byte[] BuildOoxml(params (string Path, string Xml)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, xml) in entries)
            {
                var entry = zip.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(xml);
            }
        }
        return stream.ToArray();
    }

    [Fact]
    public void Docx_ExtractsWordText()
    {
        var docx = BuildOoxml(("word/document.xml",
            "<?xml version=\"1.0\"?><w:document xmlns:w=\"http://x\"><w:body>"
            + "<w:p><w:r><w:t>Migration</w:t></w:r><w:r><w:t xml:space=\"preserve\"> plan</w:t></w:r></w:p>"
            + "<w:p><w:r><w:t>Phase one</w:t></w:r></w:p></w:body></w:document>"));
        var result = _extractor.Extract(docx, "doc.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        Assert.True(result.Extracted);
        Assert.Contains("Migration", result.Text);
        Assert.Contains("plan", result.Text);
        Assert.Contains("Phase one", result.Text);
    }

    [Fact]
    public void Pptx_ExtractsSlideTextInOrder()
    {
        var pptx = BuildOoxml(
            ("ppt/slides/slide2.xml",
                "<p:sld xmlns:p=\"http://p\" xmlns:a=\"http://a\"><a:t>Second</a:t></p:sld>"),
            ("ppt/slides/slide1.xml",
                "<p:sld xmlns:p=\"http://p\" xmlns:a=\"http://a\"><a:t>First</a:t></p:sld>"));
        var result = _extractor.Extract(pptx, "deck.pptx",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation");
        Assert.True(result.Extracted);
        var first = result.Text.IndexOf("First", StringComparison.Ordinal);
        var second = result.Text.IndexOf("Second", StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first, "slides should extract in slide-number order");
    }

    [Fact]
    public void Xlsx_ExtractsSharedStrings()
    {
        var xlsx = BuildOoxml(
            ("xl/sharedStrings.xml",
                "<sst xmlns=\"http://s\"><si><t>Revenue</t></si><si><t>Q4</t></si></sst>"),
            ("xl/worksheets/sheet1.xml",
                "<worksheet><sheetData><row><c t=\"inlineStr\"><is><t>Inline cell</t></is></c></row></sheetData></worksheet>"));
        var result = _extractor.Extract(xlsx, "book.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        Assert.True(result.Extracted);
        Assert.Contains("Revenue", result.Text);
        Assert.Contains("Q4", result.Text);
        Assert.Contains("Inline cell", result.Text);
    }

    [Fact]
    public void Ooxml_MissingEntry_YieldsNoText()
    {
        var bogus = BuildOoxml(("docProps/core.xml", "<x/>"));  // no word/document.xml
        var result = _extractor.Extract(bogus, "doc.docx", null);
        Assert.False(result.Extracted);
        Assert.Equal("no-text", result.Reason);
    }

    // Regression (MEDIUM-1): a crafted docx with ONE gigantic <w:t> text node
    // must stay bounded. The zip compresses the repeated char to a few KB (a
    // decompression-bomb shape), but the inflated node is far larger than the
    // accumulation ceiling; the chunked reader must truncate instead of
    // materializing the whole node (which ReadElementContentAsString would).
    [Fact]
    public void Docx_SingleGiantTextNode_StaysBounded()
    {
        // > MaxInflatedBytes (accumulation cap) but < the MaxCharactersInDocument
        // backstop, so parsing proceeds and the chunked read is what bounds it.
        var giant = new string('A', 3_000_000);
        var docx = BuildOoxml(("word/document.xml",
            "<?xml version=\"1.0\"?><w:document xmlns:w=\"http://x\"><w:body>"
            + $"<w:p><w:r><w:t>{giant}</w:t></w:r></w:p></w:body></w:document>"));

        var result = _extractor.Extract(docx, "bomb.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

        Assert.True(result.Extracted);
        // Final text is capped at MaxExtractedChars — proof the walk never ran
        // unbounded on the single node.
        Assert.True(result.Text.Length <= ContentExtractor.MaxExtractedChars,
            $"expected <= {ContentExtractor.MaxExtractedChars}, got {result.Text.Length}");
        Assert.StartsWith("AAAA", result.Text);
    }

    // A document whose TOTAL character count blows past the hard backstop is
    // rejected (caught → skipped) rather than parsed — defence in depth over the
    // chunked read.
    [Fact]
    public void Docx_OverMaxCharactersBackstop_SkippedNotThrown()
    {
        var huge = new string('B', 8 * ContentExtractor.MaxInflatedBytes + 1_000_000);
        var docx = BuildOoxml(("word/document.xml",
            "<?xml version=\"1.0\"?><w:document xmlns:w=\"http://x\"><w:body>"
            + $"<w:p><w:r><w:t>{huge}</w:t></w:r></w:p></w:body></w:document>"));

        var result = _extractor.Extract(docx, "huge.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

        Assert.False(result.Extracted);
        Assert.StartsWith("extract-error", result.Reason);
    }

    // The chunked/incremental read must not drop content from legitimate docs
    // with many small runs — every run is still captured.
    [Fact]
    public void Docx_ManySmallRuns_AllExtracted()
    {
        var runs = new StringBuilder();
        for (var i = 0; i < 60; i++)
            runs.Append($"<w:p><w:r><w:t>run{i}=value{i}</w:t></w:r></w:p>");
        var docx = BuildOoxml(("word/document.xml",
            "<?xml version=\"1.0\"?><w:document xmlns:w=\"http://x\"><w:body>"
            + runs + "</w:body></w:document>"));

        var result = _extractor.Extract(docx, "multi.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

        Assert.True(result.Extracted);
        Assert.Contains("run0=value0", result.Text);
        Assert.Contains("run30=value30", result.Text);
        Assert.Contains("run59=value59", result.Text);
    }

    // ── PDF (uncompressed + FlateDecode) ─────────────────────────────────────

    private static byte[] BuildPdf(string content, bool flate)
    {
        byte[] streamBytes;
        string filter;
        if (flate)
        {
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                var raw = Encoding.Latin1.GetBytes(content);
                zlib.Write(raw, 0, raw.Length);
            }
            streamBytes = output.ToArray();
            filter = "/Filter /FlateDecode ";
        }
        else
        {
            streamBytes = Encoding.Latin1.GetBytes(content);
            filter = string.Empty;
        }

        using var pdf = new MemoryStream();
        void Write(string s) { var b = Encoding.Latin1.GetBytes(s); pdf.Write(b, 0, b.Length); }
        Write("%PDF-1.4\n");
        Write($"5 0 obj\n<< {filter}/Length {streamBytes.Length} >>\nstream\n");
        pdf.Write(streamBytes, 0, streamBytes.Length);
        Write("\nendstream\nendobj\n%%EOF");
        return pdf.ToArray();
    }

    [Fact]
    public void Pdf_Uncompressed_ExtractsTextOperators()
    {
        var stream = "BT /F1 12 Tf (Hello) Tj (World) Tj ET";
        var result = _extractor.Extract(BuildPdf(stream, flate: false), "doc.pdf", "application/pdf");
        Assert.True(result.Extracted);
        Assert.Contains("Hello", result.Text);
        Assert.Contains("World", result.Text);
    }

    [Fact]
    public void Pdf_FlateDecode_Inflated_AndExtracted()
    {
        var stream = "BT (Compressed text layer) Tj ET";
        var result = _extractor.Extract(BuildPdf(stream, flate: true), "doc.pdf", "application/pdf");
        Assert.True(result.Extracted);
        Assert.Contains("Compressed text layer", result.Text);
    }

    [Fact]
    public void Pdf_ImageOnly_NoText_Skipped()
    {
        // A content stream with no text-showing operators (simulated scan).
        var result = _extractor.Extract(
            BuildPdf("q 100 0 0 100 0 0 cm /Im0 Do Q", flate: false), "scan.pdf", "application/pdf");
        Assert.False(result.Extracted);
        Assert.Equal("no-text", result.Reason);
    }

    [Fact]
    public void Pdf_LiteralEscapesAndNestedParens()
    {
        var sb = new StringBuilder();
        ContentExtractor.ExtractPdfTextOperators(@"(a\(b\) c\\d) Tj", sb);
        Assert.Contains("a(b) c\\d", sb.ToString());
    }

    // ── Dispatch / guards ────────────────────────────────────────────────────

    [Fact]
    public void CanExtract_ByExtensionAndMime()
    {
        Assert.True(_extractor.CanExtract("a.docx", null));
        Assert.True(_extractor.CanExtract("noext", "application/pdf"));
        Assert.False(_extractor.CanExtract("a.png", "image/png"));
        Assert.False(_extractor.CanExtract("a.zip", null));
    }

    [Fact]
    public void Extract_EmptyOrUnknown_Skipped()
    {
        Assert.Equal("empty", _extractor.Extract(Array.Empty<byte>(), "a.txt", null).Reason);
        // No extension and an unknown mime → cannot pick an extractor at all.
        var unknown = _extractor.Extract(new byte[] { 1, 2, 3 }, "noext", "application/octet-stream");
        Assert.False(unknown.Extracted);
        Assert.Equal("unknown-type", unknown.Reason);
        // A recognised-extension binary that decodes to nothing → no-text.
        var binary = _extractor.Extract(new byte[] { 1, 2, 3 }, "a.txt", null);
        Assert.False(binary.Extracted);
        Assert.Equal("no-text", binary.Reason);
    }

    [Fact]
    public void Extract_CorruptOoxml_SkippedNotThrown()
    {
        var result = _extractor.Extract(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0xFF }, "x.docx", null);
        Assert.False(result.Extracted);
        Assert.StartsWith("extract-error", result.Reason);
    }

    // ── Decompression bombs (bounded inflation) ──────────────────────────────

    [Fact]
    public void Pdf_FlateDecodeBomb_TruncatedAtCeiling_NotInflatedUnbounded()
    {
        // ~11MB of text-showing operators (well above the 2MB inflation
        // ceiling) compresses to a tiny FlateDecode stream (high-ratio bomb).
        // Extraction must stop at the ceiling instead of buffering the whole
        // inflated payload.
        var run = string.Concat(Enumerable.Repeat("(AAAAAAAAAAAAAAAAAAAAAAAAAAAAAA) Tj ", 300_000));
        var bomb = BuildPdf("BT " + run + " ET", flate: true);
        Assert.True(bomb.Length < 1024 * 1024, "the compressed fixture itself must be small");

        var result = _extractor.Extract(bomb, "bomb.pdf", "application/pdf");

        Assert.True(result.Extracted);
        Assert.True(result.Text.Length <= ContentExtractor.MaxExtractedChars);
    }

    [Fact]
    public void ReadBounded_StopsAtCeiling_ForHighRatioStream()
    {
        // 64MB of zeros deflates to ~64KB; the bounded reader must return at
        // most MaxInflatedBytes rather than materializing all 64MB.
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var chunk = new byte[64 * 1024];
            for (var i = 0; i < 1024; i++)
                zlib.Write(chunk, 0, chunk.Length);
        }
        compressed.Position = 0;

        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        var inflated = ContentExtractor.ReadBounded(inflater, ContentExtractor.MaxInflatedBytes);

        Assert.Equal(ContentExtractor.MaxInflatedBytes, inflated.Length);
    }

    [Fact]
    public void Docx_HugeTextBody_TruncatedAtCeiling()
    {
        // A docx whose document.xml carries far more text than the ceiling
        // (zip-compressed to a small upload). The OOXML walk must stop
        // accumulating at the ceiling; the final cap still applies.
        var text = new string('z', 1024);
        var runs = string.Concat(Enumerable.Repeat($"<w:t>{text}</w:t>", 6_000));  // ~6M chars
        var docx = BuildOoxml(("word/document.xml",
            $"<?xml version=\"1.0\"?><w:document xmlns:w=\"http://x\"><w:body>{runs}</w:body></w:document>"));
        Assert.True(docx.Length < 1024 * 1024, "the compressed fixture itself must be small");

        var result = _extractor.Extract(docx, "bomb.docx", null);

        Assert.True(result.Extracted);
        Assert.InRange(
            result.Text.Length,
            ContentExtractor.MaxExtractedChars - 1,  // final Trim may drop a boundary space
            ContentExtractor.MaxExtractedChars);
    }

    [Fact]
    public void Normalize_CollapsesWhitespace_AndCaps()
    {
        Assert.Equal("a b c", ContentExtractor.Normalize("  a\n\n  b\t\tc  "));
        var huge = new string('x', ContentExtractor.MaxExtractedChars + 1000);
        Assert.True(ContentExtractor.Normalize(huge).Length <= ContentExtractor.MaxExtractedChars);
    }

    [Fact]
    public void ExtensionOf_And_ContentTypeMap()
    {
        Assert.Equal("pdf", ContentExtractor.ExtensionOf("Report.Final.PDF"));
        Assert.Null(ContentExtractor.ExtensionOf("noext"));
        Assert.Equal("docx", ContentExtractor.ContentTypeToExtension(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document"));
        Assert.Null(ContentExtractor.ContentTypeToExtension("image/png"));
    }
}
