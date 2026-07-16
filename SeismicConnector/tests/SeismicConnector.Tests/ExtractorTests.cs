// Content extraction: plain text/html, DOCX/PPTX (built in-memory as real
// OOXML zips), PDF text layer, and the composite router.

using System.IO.Compression;
using System.Text;
using SeismicConnector.Seismic;

namespace SeismicConnector.Tests;

public class ExtractorTests
{
    // ── plain text ───────────────────────────────────────────────────────────

    [Fact]
    public void PlainText_Extracts()
    {
        var extractor = new PlainTextExtractor();
        Assert.True(extractor.CanExtract("txt"));
        Assert.Equal("hello world", extractor.Extract(Encoding.UTF8.GetBytes("hello   world\n")));
    }

    [Fact]
    public void Html_TagsAreStripped()
    {
        var extractor = new PlainTextExtractor();
        var html = "<html><head><style>p{color:red}</style></head><body><p>Sales <b>deck</b></p>"
                   + "<script>alert(1)</script></body></html>";
        Assert.Equal("Sales deck", extractor.Extract(Encoding.UTF8.GetBytes(html)));
    }

    // ── OOXML ────────────────────────────────────────────────────────────────

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
    public void Docx_TextNodesAreExtracted()
    {
        var docx = BuildZip(("word/document.xml", """
            <?xml version="1.0"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>
                <w:p><w:r><w:t>Quarterly</w:t></w:r><w:r><w:t>pitch</w:t></w:r></w:p>
              </w:body>
            </w:document>
            """));
        var extractor = new OpenXmlTextExtractor();
        Assert.True(extractor.CanExtract("docx"));
        Assert.Equal("Quarterly pitch", extractor.Extract(docx));
    }

    [Fact]
    public void Pptx_SlideAndNotesTextIsExtracted()
    {
        var pptx = BuildZip(
            ("ppt/slides/slide1.xml", """
                <p:sld xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                       xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main">
                  <a:t>Win rates</a:t><a:t>up 20%</a:t>
                </p:sld>
                """),
            ("ppt/notesSlides/notesSlide1.xml", """
                <p:notes xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                         xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main">
                  <a:t>Speaker note</a:t>
                </p:notes>
                """));
        var text = new OpenXmlTextExtractor().Extract(pptx);
        Assert.Contains("Win rates", text);
        Assert.Contains("up 20%", text);
        Assert.Contains("Speaker note", text);
    }

    [Fact]
    public void CorruptZip_YieldsEmpty_NeverThrows()
    {
        var extractor = new OpenXmlTextExtractor();
        Assert.Equal("", extractor.Extract(Encoding.UTF8.GetBytes("this is not a zip")));
    }

    // ── PDF ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Pdf_UncompressedTextLayer_IsExtracted()
    {
        var pdf = "%PDF-1.4\n1 0 obj\n<< /Length 60 >>\nstream\n"
                  + "BT /F1 12 Tf (Enablement) Tj (playbook) Tj [ (v) -20 (2) ] TJ ET\n"
                  + "endstream\nendobj\ntrailer\n%%EOF";
        var extractor = new PdfTextExtractor();
        Assert.True(extractor.CanExtract("pdf"));
        var text = extractor.Extract(Encoding.ASCII.GetBytes(pdf));
        Assert.Contains("Enablement", text);
        Assert.Contains("playbook", text);
        Assert.Contains("v2", text.Replace(" ", ""));
    }

    [Fact]
    public void Pdf_EscapedParentheses_AreUnescaped()
    {
        var builder = new StringBuilder();
        PdfTextExtractor.ExtractShowTextOperators(@"(margin \(net\)) Tj", builder);
        Assert.Equal("margin (net)", builder.ToString().Trim());
    }

    // ── composite ────────────────────────────────────────────────────────────

    [Fact]
    public void Composite_RoutesByFormat()
    {
        var composite = CompositeExtractor.Default;
        Assert.True(composite.CanExtract("txt"));
        Assert.True(composite.CanExtract("docx"));
        Assert.True(composite.CanExtract("pdf"));
        Assert.False(composite.CanExtract("video"));

        Assert.Equal("plain", composite.ExtractFor("txt", Encoding.UTF8.GetBytes("plain")));
        Assert.Equal("", composite.ExtractFor("video", Encoding.UTF8.GetBytes("bytes")));
    }

    [Fact]
    public void Composite_ExtractionFailure_FallsThroughToEmpty()
    {
        Assert.Equal("", CompositeExtractor.Default.ExtractFor("pdf", Array.Empty<byte>()));
    }

    [Fact]
    public void Transformer_FallsBackToDescription_WhenNoText()
    {
        var transformer = new ItemTransformer();
        var content = TestContent.Make("c1", format: "video");
        var acl = new AclResult(new[] { AclEntry.GrantUser("e1") }, 0, false);
        var item = transformer.Transform(content, null, payload: null, acl);
        Assert.Equal("Description of c1", item["content"]?["value"]?.GetValue<string>());
        Assert.Equal("c1", item["id"]?.GetValue<string>());
        Assert.Equal("grant", item["acl"]?[0]?["accessType"]?.GetValue<string>());
    }

    [Fact]
    public void Transformer_CapsContentLength()
    {
        var transformer = new ItemTransformer();
        var content = TestContent.Make("c1", format: "txt");
        var huge = Encoding.UTF8.GetBytes(new string('x', ItemTransformer.MaxContentChars + 5000));
        var acl = new AclResult(new[] { AclEntry.GrantUser("e1") }, 0, false);
        var item = transformer.Transform(content, null, huge, acl);
        Assert.Equal(ItemTransformer.MaxContentChars, item["content"]!["value"]!.GetValue<string>().Length);
    }
}
