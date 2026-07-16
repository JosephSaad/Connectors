// Content/IContentExtractor.cs
// ----------------------------
// Pluggable text extraction from attachment binaries. The pipeline holds an
// IContentExtractor (default: ContentExtractor) so tests can substitute a
// fake, and future formats can be added without touching the ingest code.
//
// Extraction is deliberately dependency-free (see ContentExtractor): OOXML
// via System.IO.Compression + XmlReader, PDF text-layer best-effort, and
// plain text/csv/html — scanned images and unknown binaries return a
// "skipped" result (metadata-only), never garbage text.

namespace ClarizenConnector.Content;

/// <summary>Outcome of a text-extraction attempt.</summary>
public sealed record ExtractionResult(bool Extracted, string Text, string Reason)
{
    /// <summary>Text was extracted (may be empty for a genuinely empty document).</summary>
    public static ExtractionResult Ok(string text) => new(true, text, "extracted");

    /// <summary>No text extracted — <paramref name="reason"/> is a short machine-ish tag.</summary>
    public static ExtractionResult Skipped(string reason) => new(false, string.Empty, reason);
}

public interface IContentExtractor
{
    /// <summary>True when this extractor can attempt <paramref name="fileName"/> /
    /// <paramref name="contentType"/> (used for the allowlist and dispatch).</summary>
    bool CanExtract(string fileName, string? contentType);

    /// <summary>Extract text from <paramref name="content"/>. Never throws — a
    /// parse failure returns <see cref="ExtractionResult.Skipped"/>.</summary>
    ExtractionResult Extract(byte[] content, string fileName, string? contentType);
}
