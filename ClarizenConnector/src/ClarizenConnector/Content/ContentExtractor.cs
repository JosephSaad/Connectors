// Content/ContentExtractor.cs
// ---------------------------
// Dependency-free text extraction from attachment binaries. No third-party
// parsers — only the BCL (System.IO.Compression for zip/deflate, System.Xml
// for OOXML, System.Text for decoding). Supported families:
//
//   • Plain text: txt / csv / tsv / log / md / json / yaml / xml → decoded as
//     UTF-8 (BOM-aware) with a Latin-1 fallback.
//   • HTML: htm / html → tags stripped, entities decoded.
//   • OOXML: docx (word/document.xml <w:t>), xlsx (shared strings + inline
//     <t>), pptx (slides <a:t>) — read from the zip with XmlReader, no
//     full-document DOM.
//   • PDF: best-effort text layer — inflate FlateDecode streams and pull the
//     strings from Tj / TJ / ' / " text-showing operators. Image-only
//     (scanned) PDFs yield no text and are reported as skipped, never OCR'd.
//
// Anything else (images, archives, unknown binary) returns Skipped so the
// item stays metadata-only. Extraction NEVER throws: any parse failure is a
// Skipped result with a reason.

using System.IO.Compression;
using System.Text;
using System.Xml;

namespace ClarizenConnector.Content;

public sealed class ContentExtractor : IContentExtractor
{
    /// <summary>Extensions this extractor will attempt (lower-case, no dot).</summary>
    internal static readonly HashSet<string> KnownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "text", "csv", "tsv", "log", "md", "markdown", "json", "yaml", "yml", "xml",
        "htm", "html",
        "docx", "xlsx", "pptx",
        "pdf",
    };

    /// <summary>Max characters kept from any one attachment (keeps the Graph
    /// content payload bounded regardless of file size).</summary>
    public const int MaxExtractedChars = 512 * 1024;

    public bool CanExtract(string fileName, string? contentType)
    {
        var ext = ExtensionOf(fileName);
        if (ext is not null && KnownExtensions.Contains(ext))
            return true;
        return ContentTypeToExtension(contentType) is not null;
    }

    public ExtractionResult Extract(byte[] content, string fileName, string? contentType)
    {
        if (content.Length == 0)
            return ExtractionResult.Skipped("empty");

        var ext = ExtensionOf(fileName) ?? ContentTypeToExtension(contentType);
        if (ext is null)
            return ExtractionResult.Skipped("unknown-type");

        try
        {
            var text = ext switch
            {
                "docx" => ExtractOoxml(content, "word/document.xml", "t"),
                "pptx" => ExtractPptx(content),
                "xlsx" => ExtractXlsx(content),
                "pdf" => ExtractPdf(content),
                "htm" or "html" => StripHtml(DecodeText(content)),
                _ => DecodeText(content),  // txt/csv/json/xml/... family
            };
            text = Normalize(text);
            return text.Length == 0
                ? ExtractionResult.Skipped("no-text")
                : ExtractionResult.Ok(text);
        }
        catch (Exception exc)
        {
            return ExtractionResult.Skipped($"extract-error:{exc.GetType().Name}");
        }
    }

    // ── Helpers: type detection ──────────────────────────────────────────────

    internal static string? ExtensionOf(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1)
            return null;
        return fileName[(dot + 1)..].Trim().ToLowerInvariant();
    }

    internal static string? ContentTypeToExtension(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return null;
        var mime = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return mime switch
        {
            "text/plain" => "txt",
            "text/csv" => "csv",
            "text/tab-separated-values" => "tsv",
            "text/markdown" => "md",
            "application/json" => "json",
            "application/xml" or "text/xml" => "xml",
            "text/html" => "html",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => "docx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => "xlsx",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation" => "pptx",
            "application/pdf" => "pdf",
            _ => null,
        };
    }

    // ── Plain text / HTML ────────────────────────────────────────────────────

    /// <summary>Decode bytes as UTF-8 (BOM-aware); on invalid UTF-8 fall back to Latin-1.</summary>
    internal static string DecodeText(byte[] content)
    {
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            // Skip a UTF-8 BOM if present.
            if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
                return strict.GetString(content, 3, content.Length - 3);
            return strict.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(content);
        }
    }

    internal static string StripHtml(string html)
    {
        if (!html.Contains('<'))
            return System.Net.WebUtility.HtmlDecode(html);
        var sb = new StringBuilder(html.Length);
        var inTag = false;
        foreach (var ch in html)
        {
            switch (ch)
            {
                case '<':
                    inTag = true;
                    break;
                case '>':
                    inTag = false;
                    sb.Append(' ');
                    break;
                default:
                    if (!inTag)
                        sb.Append(ch);
                    break;
            }
        }
        return System.Net.WebUtility.HtmlDecode(sb.ToString());
    }

    // ── OOXML (docx / pptx / xlsx) ───────────────────────────────────────────

    /// <summary>Concatenate the text of every <paramref name="localName"/> element
    /// in one zip entry (docx: word/document.xml, elem "t").</summary>
    private static string ExtractOoxml(byte[] content, string entryName, string localName)
    {
        using var stream = new MemoryStream(content);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = zip.GetEntry(entryName);
        if (entry is null)
            return string.Empty;
        using var entryStream = entry.Open();
        return ReadElementText(entryStream, localName);
    }

    /// <summary>pptx: every slide's <a:t> runs, in slide-number order.</summary>
    private static string ExtractPptx(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var slides = zip.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => SlideNumber(e.FullName))
            .ToList();
        var sb = new StringBuilder();
        foreach (var slide in slides)
        {
            using var entryStream = slide.Open();
            var text = ReadElementText(entryStream, "t");
            if (text.Length > 0)
                sb.Append(text).Append('\n');
        }
        return sb.ToString();
    }

    private static int SlideNumber(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }

    /// <summary>xlsx: shared strings + inline cell strings (every <t>). Cheap
    /// but complete for search — cell text is what a reader would see.</summary>
    private static string ExtractXlsx(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var sb = new StringBuilder();

        var shared = zip.GetEntry("xl/sharedStrings.xml");
        if (shared is not null)
        {
            using var entryStream = shared.Open();
            sb.Append(ReadElementText(entryStream, "t"));
        }
        foreach (var sheet in zip.Entries.Where(e =>
                     e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                     && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            using var entryStream = sheet.Open();
            // Inline strings (t="inlineStr") also serialize as <t>.
            var inline = ReadElementText(entryStream, "t");
            if (inline.Length > 0)
                sb.Append('\n').Append(inline);
        }
        return sb.ToString();
    }

    /// <summary>Stream the text content of every element named
    /// <paramref name="localName"/> (namespace-agnostic) via XmlReader.</summary>
    private static string ReadElementText(Stream xml, string localName)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            XmlResolver = null,
        };
        var sb = new StringBuilder();
        using var reader = XmlReader.Create(xml, settings);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element
                && string.Equals(reader.LocalName, localName, StringComparison.Ordinal))
            {
                var value = reader.ReadElementContentAsString();
                if (value.Length > 0)
                {
                    sb.Append(value);
                    sb.Append(' ');
                }
            }
        }
        return sb.ToString();
    }

    // ── PDF (best-effort text layer) ─────────────────────────────────────────

    /// <summary>
    /// Best-effort PDF text extraction: inflate every FlateDecode stream and
    /// pull the literal strings from text-showing operators (Tj, TJ, ', ").
    /// Uncompressed content streams are scanned directly. Image-only PDFs
    /// yield nothing (reported as no-text — never OCR'd).
    /// </summary>
    internal static string ExtractPdf(byte[] content)
    {
        var sb = new StringBuilder();
        foreach (var streamBytes in EnumeratePdfStreams(content))
        {
            var text = DecodeText(streamBytes);
            ExtractPdfTextOperators(text, sb);
        }
        return sb.ToString();
    }

    /// <summary>Yield the decoded bytes of every `stream ... endstream` body,
    /// inflating FlateDecode where the preceding dict declares it.</summary>
    private static IEnumerable<byte[]> EnumeratePdfStreams(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var index = 0;
        while (true)
        {
            var streamPos = text.IndexOf("stream", index, StringComparison.Ordinal);
            if (streamPos < 0)
                yield break;
            var dictStart = text.LastIndexOf("<<", streamPos, StringComparison.Ordinal);
            var isFlate = dictStart >= 0
                && text.AsSpan(dictStart, streamPos - dictStart).IndexOf("FlateDecode") >= 0;

            // Body starts after the CRLF/LF that follows the "stream" keyword.
            var bodyStart = streamPos + "stream".Length;
            if (bodyStart < text.Length && text[bodyStart] == '\r')
                bodyStart++;
            if (bodyStart < text.Length && text[bodyStart] == '\n')
                bodyStart++;

            var endPos = text.IndexOf("endstream", bodyStart, StringComparison.Ordinal);
            if (endPos < 0)
                yield break;
            var bodyLength = endPos - bodyStart;
            index = endPos + "endstream".Length;
            if (bodyLength <= 0)
                continue;

            var raw = new byte[bodyLength];
            Array.Copy(pdf, bodyStart, raw, 0, bodyLength);

            if (isFlate)
            {
                byte[]? inflated = TryInflate(raw);
                if (inflated is not null)
                    yield return inflated;
            }
            else
            {
                yield return raw;
            }
        }
    }

    private static byte[]? TryInflate(byte[] raw)
    {
        // zlib (RFC 1950) framing has a 2-byte header; ZLibStream handles it.
        try
        {
            using var input = new MemoryStream(raw);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            return output.ToArray();
        }
        catch
        {
            // Fall back to raw deflate (no zlib header).
            try
            {
                using var input = new MemoryStream(raw);
                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                deflate.CopyTo(output);
                return output.ToArray();
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Pull literal strings from PDF text-showing operators in a
    /// content stream: (...)Tj, [(..)..(..)]TJ, (..)' and (..)".</summary>
    internal static void ExtractPdfTextOperators(string contentStream, StringBuilder sb)
    {
        var i = 0;
        while (i < contentStream.Length)
        {
            var ch = contentStream[i];
            if (ch == '(')
            {
                var literal = ReadPdfLiteral(contentStream, ref i);  // advances past ')'
                sb.Append(literal);
            }
            else if (ch == 'T' && i + 1 < contentStream.Length
                     && (contentStream[i + 1] == 'j' || contentStream[i + 1] == 'J'
                         || contentStream[i + 1] == 'd' || contentStream[i + 1] == 'D'
                         || contentStream[i + 1] == '*'))
            {
                // End of a text operator — separate runs with whitespace.
                sb.Append(' ');
                i += 2;
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>Read a PDF string literal `(...)` starting at the '(' under
    /// <paramref name="i"/>, honouring backslash escapes and nested parens.
    /// Leaves <paramref name="i"/> just past the closing ')'.</summary>
    private static string ReadPdfLiteral(string s, ref int i)
    {
        var sb = new StringBuilder();
        i++;  // skip '('
        var depth = 1;
        while (i < s.Length && depth > 0)
        {
            var ch = s[i];
            if (ch == '\\' && i + 1 < s.Length)
            {
                var next = s[i + 1];
                sb.Append(next switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'b' => '\b',
                    'f' => '\f',
                    _ => next,
                });
                i += 2;
                continue;
            }
            if (ch == '(')
            {
                depth++;
                sb.Append(ch);
            }
            else if (ch == ')')
            {
                depth--;
                if (depth > 0)
                    sb.Append(ch);
            }
            else
            {
                sb.Append(ch);
            }
            i++;
        }
        return sb.ToString();
    }

    // ── Normalisation ────────────────────────────────────────────────────────

    /// <summary>Collapse runs of whitespace, trim, and cap the length.</summary>
    internal static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        var sb = new StringBuilder(Math.Min(text.Length, MaxExtractedChars));
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            var isSpace = char.IsWhiteSpace(ch);
            if (isSpace)
            {
                if (!lastWasSpace)
                    sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                // Drop control chars that survive from binary streams.
                if (!char.IsControl(ch))
                    sb.Append(ch);
                lastWasSpace = false;
            }
            if (sb.Length >= MaxExtractedChars)
                break;
        }
        return sb.ToString().Trim();
    }
}
