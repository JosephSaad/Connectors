// Seismic/ContentExtractor.cs
// ---------------------------
// Pluggable text-extraction pipeline used to index document payloads.
//
// Deliberate scope (documented in README): no external parsing library is
// pulled in. Instead:
//
//   * plain text / markdown / csv / json / html — decoded directly (html tags
//     stripped);
//   * DOCX / PPTX / XLSX — Office Open XML packages are ZIP archives; the
//     extractor reads word/document.xml, ppt/slides/slide*.xml (+ notes),
//     xl/sharedStrings.xml and xl/worksheets/sheet*.xml with XmlReader and
//     collects the w:t / a:t / s:t text nodes. This covers the text layer of
//     real Office documents without a dependency;
//   * PDF — best-effort text-layer extraction: FlateDecode streams are
//     inflated (System.IO.Compression) and the show-text operators are
//     parsed — Tj, ', ", and TJ arrays, with literal strings (octal + symbol
//     escapes, line continuations) AND hex strings, including UTF-16BE
//     (BOM-prefixed) payloads. Scanned or exotically-encoded PDFs yield
//     little or nothing; the pipeline then falls back to metadata-only
//     indexing.
//
// Every extraction attempt is recorded per format in the metrics registry
// (seismic_connector_extraction_*_total{format="..."} — docs/OBSERVABILITY.md).
//
// Extraction failures NEVER fail an item: the transformer falls back to
// name + description. Swap in a richer implementation by registering another
// IContentExtractor in CompositeExtractor.

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace SeismicConnector.Seismic;

public interface IContentExtractor
{
    /// <summary>True when this extractor understands the Seismic format label / file extension.</summary>
    bool CanExtract(string format);

    /// <summary>Extract plain text; return empty string when nothing could be extracted.</summary>
    string Extract(byte[] payload);
}

/// <summary>
/// Hard ceilings applied DURING extraction, so a decompression bomb (a small
/// compressed payload that inflates to gigabytes) cannot exhaust memory.
/// SEISMIC_MAX_EXTRACT_MB caps only the COMPRESSED download and the
/// transformer's MaxContentChars trims only the FINAL text — both after
/// inflation — so the inflation itself must be bounded here: extraction stops
/// (keeping the text accumulated so far) once a ceiling is hit.
/// </summary>
internal static class ExtractionLimits
{
    /// <summary>Max bytes inflated from any single compressed stream (PDF FlateDecode).</summary>
    internal const int MaxInflatedStreamBytes = 32 * 1024 * 1024;

    /// <summary>Max XML characters parsed per OOXML package part (bounds the inflated zip entry).</summary>
    internal const long MaxXmlCharsPerPart = 32_000_000;

    /// <summary>Max characters accumulated into an extractor's text buffer (final cap is MaxContentChars).</summary>
    internal const int MaxAccumulatedChars = 1_000_000;

    /// <summary>
    /// AGGREGATE ceiling on bytes inflated across ALL streams/parts of a single
    /// document. The per-stream / per-part caps above bound one unit, but a
    /// crafted payload can pack many high-ratio units that each emit NO text
    /// (so the text-buffer cap never trips) — inflating and scanning every one
    /// stalls the serial crawl worker. Once the cumulative inflated size crosses
    /// this ceiling the extractor stops opening further units (keeping the text
    /// gathered so far). Sits above the per-unit caps so a normal multi-part
    /// document is never truncated.
    /// </summary>
    internal const long MaxAggregateInflatedBytes = 64L * 1024 * 1024;
}

/// <summary>
/// Read-only stream wrapper that counts the bytes pulled from the inner stream.
/// Wraps a zip entry's decompression stream so the OOXML extractor can bound the
/// CUMULATIVE inflated size across all parts (a per-part XmlReader cap alone lets
/// many no-text parts each inflate to the per-part ceiling).
/// </summary>
internal sealed class CountingReadStream : Stream
{
    private readonly Stream _inner;

    public CountingReadStream(Stream inner) => _inner = inner;

    public long BytesRead { get; private set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        BytesRead += n;
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Plain text and text-adjacent formats (txt, md, csv, json, html).</summary>
public sealed class PlainTextExtractor : IContentExtractor
{
    private static readonly HashSet<string> Formats = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "text", "md", "markdown", "csv", "json", "html", "htm", "xml",
    };

    public bool CanExtract(string format) => Formats.Contains(Normalize(format));

    public string Extract(byte[] payload)
    {
        var text = DecodeUtf8(payload);
        // Strip tags for markup formats; harmless for the rest.
        text = Regex.Replace(text, "<script[^>]*>.*?</script>", " ",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<style[^>]*>.*?</style>", " ",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", " ");
        return NormalizeWhitespace(text);
    }

    internal static string Normalize(string format) => format.Trim().TrimStart('.').ToLowerInvariant();

    internal static string DecodeUtf8(byte[] payload)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(payload);
        }
        catch
        {
            // Defensive only (a non-throwing decoder should never throw):
            // extraction is best-effort by contract — empty text falls back to
            // metadata-only indexing, never a failed item.
            return string.Empty;
        }
    }

    internal static string NormalizeWhitespace(string text) =>
        Regex.Replace(text, @"\s+", " ").Trim();
}

/// <summary>DOCX / PPTX / XLSX text via ZipArchive + XmlReader (w:t, a:t and s:t nodes).</summary>
public sealed class OpenXmlTextExtractor : IContentExtractor
{
    private static readonly HashSet<string> Formats = new(StringComparer.OrdinalIgnoreCase)
    {
        "doc", "docx", "word", "ppt", "pptx", "presentation", "livedoc",
        "xls", "xlsx", "excel", "spreadsheet",
    };

    public bool CanExtract(string format) => Formats.Contains(PlainTextExtractor.Normalize(format));

    public string Extract(byte[] payload)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(payload), ZipArchiveMode.Read);
            var sb = new StringBuilder();
            var aggregateInflated = 0L;
            foreach (var entry in zip.Entries
                .Where(IsTextPart)
                .OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                // Decompression-bomb guard: stop once enough text is in hand.
                if (sb.Length >= ExtractionLimits.MaxAccumulatedChars)
                    break;
                // Aggregate-bomb guard: stop once cumulative inflation across all
                // parts crosses the ceiling — bounds a payload packed with many
                // no-text parts that never trip the text-buffer cap.
                if (aggregateInflated >= ExtractionLimits.MaxAggregateInflatedBytes)
                    break;
                // Count decompressed bytes even if the part aborts mid-read, so
                // the aggregate reflects the work actually done.
                using var counting = new CountingReadStream(entry.Open());
                try
                {
                    AppendTextNodes(counting, sb, aggregateInflated);
                }
                catch (XmlException)
                {
                    // Oversized (MaxXmlCharsPerPart tripped) or malformed part —
                    // keep whatever text was accumulated before the abort.
                }
                aggregateInflated += counting.BytesRead;
            }
            return PlainTextExtractor.NormalizeWhitespace(sb.ToString());
        }
        catch
        {
            return string.Empty;  // not a zip / corrupted — metadata-only fallback
        }
    }

    private static bool IsTextPart(ZipArchiveEntry entry) =>
        entry.FullName is "word/document.xml" or "xl/sharedStrings.xml"
        || (entry.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal)
            && entry.FullName.EndsWith(".xml", StringComparison.Ordinal))
        || (entry.FullName.StartsWith("ppt/notesSlides/notesSlide", StringComparison.Ordinal)
            && entry.FullName.EndsWith(".xml", StringComparison.Ordinal))
        // XLSX inline strings (cells not routed through sharedStrings).
        || (entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)
            && entry.FullName.EndsWith(".xml", StringComparison.Ordinal));

    private static void AppendTextNodes(CountingReadStream stream, StringBuilder sb, long aggregateBefore)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            // Bomb guard: a tiny DEFLATE part can inflate to gigabytes of XML.
            // The reader aborts (XmlException) once the part exceeds this many
            // characters, bounding memory regardless of the compression ratio.
            MaxCharactersInDocument = ExtractionLimits.MaxXmlCharsPerPart,
        };
        using var reader = XmlReader.Create(stream, settings);
        var inTextNode = false;
        while (reader.Read())
        {
            if (sb.Length >= ExtractionLimits.MaxAccumulatedChars)
                return;  // enough text extracted — stop inflating this part
            // Aggregate-bomb guard mid-part: stop inflating this no-text part once
            // cumulative inflation crosses the ceiling, so one giant part can't
            // blow the aggregate on its own.
            if (aggregateBefore + stream.BytesRead >= ExtractionLimits.MaxAggregateInflatedBytes)
                return;
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
            {
                inTextNode = true;
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "t")
            {
                inTextNode = false;
                sb.Append(' ');
            }
            else if (inTextNode && reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
            {
                sb.Append(reader.Value);
            }
        }
    }
}

/// <summary>Best-effort PDF text-layer extraction (FlateDecode + Tj/TJ operators).</summary>
public sealed class PdfTextExtractor : IContentExtractor
{
    /// <summary>Default per-operator-scan regex timeout (test seam mirrors ContentClassifier).</summary>
    private static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromSeconds(2);

    private readonly TimeSpan _matchTimeout;

    public PdfTextExtractor() : this(null) { }

    /// <param name="matchTimeout">Per-scan regex match timeout (test seam; default 2s). Bounds a
    /// hostile show-text operator stream whose regex scan would otherwise run for minutes.</param>
    public PdfTextExtractor(TimeSpan? matchTimeout) =>
        _matchTimeout = matchTimeout ?? DefaultMatchTimeout;

    public bool CanExtract(string format) => PlainTextExtractor.Normalize(format) == "pdf";

    public string Extract(byte[] payload)
    {
        try
        {
            var sb = new StringBuilder();
            var aggregateInflated = 0L;
            foreach (var stream in EnumerateStreams(payload))
            {
                // Decompression-bomb guard: stop once enough text is in hand.
                if (sb.Length >= ExtractionLimits.MaxAccumulatedChars)
                    break;
                // Aggregate-bomb guard: stop once cumulative inflation across all
                // streams crosses the ceiling — bounds a payload packed with many
                // high-ratio streams that emit no text (so the buffer cap never
                // trips) yet each inflate + regex-scan up to the per-stream cap.
                if (aggregateInflated >= ExtractionLimits.MaxAggregateInflatedBytes)
                    break;
                var inflated = TryInflate(stream) ?? stream;
                aggregateInflated += inflated.Length;
                var text = Encoding.Latin1.GetString(inflated);
                ExtractShowTextOperators(text, sb, _matchTimeout);
            }
            return PlainTextExtractor.NormalizeWhitespace(sb.ToString());
        }
        catch
        {
            // Corrupt/exotic PDF structure — extraction is best-effort by
            // contract: empty text → metadata-only indexing, never a failed
            // item. CompositeExtractor.ExtractFor records the miss per format.
            return string.Empty;
        }
    }

    /// <summary>Slice out every <c>stream ... endstream</c> body in the file.</summary>
    private static IEnumerable<byte[]> EnumerateStreams(byte[] payload)
    {
        var haystack = payload;
        var start = 0;
        while (true)
        {
            var streamIdx = IndexOf(haystack, "stream", start);
            if (streamIdx < 0)
                yield break;
            var bodyStart = streamIdx + "stream".Length;
            // Skip the EOL after the keyword.
            if (bodyStart < haystack.Length && haystack[bodyStart] == (byte)'\r')
                bodyStart++;
            if (bodyStart < haystack.Length && haystack[bodyStart] == (byte)'\n')
                bodyStart++;
            var endIdx = IndexOf(haystack, "endstream", bodyStart);
            if (endIdx < 0)
                yield break;
            yield return haystack[bodyStart..endIdx];
            start = endIdx + "endstream".Length;
        }
    }

    private static int IndexOf(byte[] haystack, string needle, int from)
    {
        var bytes = Encoding.ASCII.GetBytes(needle);
        for (var i = from; i <= haystack.Length - bytes.Length; i++)
        {
            var match = true;
            for (var j = 0; j < bytes.Length; j++)
            {
                if (haystack[i + j] != bytes[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Inflate a FlateDecode stream, bounded at
    /// <see cref="ExtractionLimits.MaxInflatedStreamBytes"/>: a high-ratio
    /// decompression bomb is TRUNCATED at the ceiling instead of inflating
    /// unboundedly into memory (extraction is best-effort anyway).
    /// </summary>
    internal static byte[]? TryInflate(byte[] data)
    {
        try
        {
            using var input = new MemoryStream(data);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
            {
                var room = ExtractionLimits.MaxInflatedStreamBytes - (int)output.Length;
                if (read >= room)
                {
                    output.Write(buffer, 0, Math.Max(0, room));
                    break;  // bomb guard: ceiling reached — keep what we have
                }
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch
        {
            return null;  // not FlateDecode (or already plain)
        }
    }

    /// <summary>
    /// Pull strings out of every show-text operator: <c>Tj</c>, <c>'</c>,
    /// <c>"</c> and <c>TJ</c> arrays — literal <c>(...)</c> strings (with
    /// octal/symbol escapes and line continuations) and hex <c>&lt;...&gt;</c>
    /// strings, including UTF-16BE (BOM FE FF) payloads.
    /// </summary>
    internal static void ExtractShowTextOperators(string content, StringBuilder sb, TimeSpan? matchTimeout = null)
    {
        // A hostile stream (e.g. thousands of unterminated '[' before a huge body)
        // drives these scans quadratic — minutes on the SERIAL crawl worker. Bound
        // every scan with a match timeout; on timeout keep the text gathered so far
        // and move on (extraction is best-effort). InfiniteMatchTimeout preserves the
        // old unbounded behaviour when a caller passes null (the static test seam).
        var timeout = matchTimeout ?? Regex.InfiniteMatchTimeout;
        const RegexOptions opts = RegexOptions.Singleline;
        try
        {
            // (text) Tj | (text) ' | aw ac (text) "  — string sits right before the operator.
            foreach (Match match in Regex.Matches(
                         content, @"\(((?:[^()\\]|\\.)*)\)\s*(?:Tj|'|"")", opts, timeout))
            {
                sb.Append(DecodeLiteralString(match.Groups[1].Value)).Append(' ');
            }
            // <48656C6C6F> Tj — hex-string form of the same operators.
            foreach (Match match in Regex.Matches(
                         content, @"<([0-9A-Fa-f\s]*)>\s*(?:Tj|'|"")", opts, timeout))
            {
                sb.Append(DecodeHexString(match.Groups[1].Value)).Append(' ');
            }
            // [ (a) -120 (b) <686921> ] TJ — mixed literal/hex elements.
            foreach (Match match in Regex.Matches(content, @"\[(.*?)\]\s*TJ", opts, timeout))
            {
                foreach (Match inner in Regex.Matches(
                             match.Groups[1].Value,
                             @"\(((?:[^()\\]|\\.)*)\)|<([0-9A-Fa-f\s]*)>",
                             opts, timeout))
                {
                    sb.Append(inner.Groups[1].Success
                        ? DecodeLiteralString(inner.Groups[1].Value)
                        : DecodeHexString(inner.Groups[2].Value));
                }
                sb.Append(' ');
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological stream — abandon operator scanning for it (memory stays
            // bounded, the worker is not stalled); text already appended is kept.
        }
    }

    /// <summary>
    /// Decode a PDF literal string body: symbol escapes (\n \r \t \b \f \( \) \\),
    /// 1-3 digit octal escapes (\101 → 'A'), backslash-newline line
    /// continuations, then UTF-16BE when the bytes carry a FE FF BOM.
    /// </summary>
    internal static string DecodeLiteralString(string s)
    {
        var bytes = new List<byte>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch != '\\')
            {
                bytes.Add((byte)ch);  // content was read as Latin1: char == byte
                continue;
            }
            if (++i >= s.Length)
                break;
            var esc = s[i];
            switch (esc)
            {
                case 'n':
                    bytes.Add((byte)'\n');
                    break;
                case 'r':
                    bytes.Add((byte)'\r');
                    break;
                case 't':
                    bytes.Add((byte)'\t');
                    break;
                case 'b':
                    bytes.Add((byte)'\b');
                    break;
                case 'f':
                    bytes.Add((byte)'\f');
                    break;
                case '\n':
                    break;  // line continuation — nothing emitted
                case '\r':
                    if (i + 1 < s.Length && s[i + 1] == '\n')
                        i++;  // \<CRLF> continuation
                    break;
                case >= '0' and <= '7':
                {
                    // 1-3 octal digits.
                    var value = esc - '0';
                    for (var d = 0; d < 2 && i + 1 < s.Length && s[i + 1] is >= '0' and <= '7'; d++)
                        value = value * 8 + (s[++i] - '0');
                    bytes.Add((byte)(value & 0xFF));
                    break;
                }
                default:
                    bytes.Add((byte)esc);  // \( \) \\ and any unknown escape → literal
                    break;
            }
        }
        return DecodeTextBytes(bytes.ToArray());
    }

    /// <summary>Decode a PDF hex string body (whitespace ignored; odd length padded with 0).</summary>
    internal static string DecodeHexString(string hex)
    {
        var digits = new StringBuilder(hex.Length);
        foreach (var ch in hex)
        {
            if (Uri.IsHexDigit(ch))
                digits.Append(ch);
        }
        if (digits.Length % 2 == 1)
            digits.Append('0');
        var bytes = new byte[digits.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(digits.ToString(i * 2, 2), 16);
        return DecodeTextBytes(bytes);
    }

    /// <summary>UTF-16BE when BOM-prefixed (the PDF text-string convention), else Latin1 ≈ PDFDocEncoding.</summary>
    private static string DecodeTextBytes(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        return Encoding.Latin1.GetString(bytes);
    }
}

/// <summary>Routes to the first extractor that understands the format.</summary>
public sealed class CompositeExtractor : IContentExtractor
{
    private static readonly Infrastructure.IAppLogger Logger =
        Infrastructure.Logging.GetLogger("seismic_connector.extract");

    private readonly IReadOnlyList<IContentExtractor> _extractors;

    public CompositeExtractor(params IContentExtractor[] extractors)
    {
        _extractors = extractors.Length > 0
            ? extractors
            : new IContentExtractor[]
            {
                new PlainTextExtractor(),
                new OpenXmlTextExtractor(),
                new PdfTextExtractor(),
            };
    }

    public static CompositeExtractor Default { get; } = new();

    public bool CanExtract(string format) => _extractors.Any(e => e.CanExtract(format));

    public string Extract(byte[] payload) => throw new NotSupportedException(
        "Use ExtractFor(format, payload) on the composite.");

    /// <summary>
    /// Extract text for <paramref name="format"/>; empty string when
    /// unsupported or failed. Every attempt is recorded in the per-format
    /// metrics (success = non-empty text), so /metrics exposes extraction
    /// health per payload type.
    /// </summary>
    public string ExtractFor(string format, byte[] payload)
    {
        var attempted = false;
        foreach (var extractor in _extractors)
        {
            if (!extractor.CanExtract(format))
                continue;
            attempted = true;
            try
            {
                var text = extractor.Extract(payload);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    Infrastructure.Metrics.RecordExtraction(format, success: true);
                    return text;
                }
            }
            catch (Exception ex)
            {
                // extraction must never fail an item — fall through. Debug-gated
                // breadcrumb so "why is this doc metadata-only?" is answerable
                // (the per-format failure metric records the miss either way).
                if (Logger.IsEnabledFor(Infrastructure.LogLevels.Debug))
                    Logger.Debug(
                        $"Extractor {extractor.GetType().Name} threw on a '{format}' payload "
                        + $"({ex.GetType().Name}: {ex.Message}) — falling through to metadata-only.");
            }
        }
        if (attempted)
            Infrastructure.Metrics.RecordExtraction(format, success: false);
        return string.Empty;
    }
}
