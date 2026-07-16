// Altrata/FeedReader.cs
// ---------------------
// Discovers deliveries under FEED_PATH, validates manifest SHA-256 checksums,
// and parses per-dataset data files (JSON array of objects, or CSV with a
// header row) into FeedRecords.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AltrataConnector.Infrastructure;

namespace AltrataConnector.Altrata;

public sealed class ChecksumMismatchException : Exception
{
    public string FileName { get; }
    public ChecksumMismatchException(string fileName, string expected, string actual)
        : base($"Checksum mismatch for '{fileName}': manifest {expected}, actual {actual}") =>
        FileName = fileName;
}

/// <summary>A .json array feed file over the FEED_JSON_MAX_MB cap (the whole
/// array must be parsed in memory; large datasets belong in streamed .jsonl).</summary>
public sealed class FeedFileTooLargeException : Exception
{
    public FeedFileTooLargeException(string message) : base(message) { }
}

public static class FeedReader
{
    private static readonly IAppLogger Logger = Logging.GetLogger("altrata_connector.feeds");

    public const string ManifestFileName = "manifest.json";
    public const string ArchiveDirectoryName = "archive";

    // ---- discovery ----------------------------------------------------------

    /// <summary>All deliveries under the feed path (directories containing
    /// manifest.json), ordered by delivery id. The archive directory is skipped.</summary>
    public static IReadOnlyList<Delivery> DiscoverDeliveries(string feedPath)
    {
        if (!Directory.Exists(feedPath))
            return Array.Empty<Delivery>();

        var deliveries = new List<Delivery>();
        foreach (var dir in Directory.EnumerateDirectories(feedPath))
        {
            if (string.Equals(Path.GetFileName(dir), ArchiveDirectoryName, StringComparison.OrdinalIgnoreCase))
                continue;
            var manifestPath = Path.Combine(dir, ManifestFileName);
            if (!File.Exists(manifestPath))
                continue;
            try
            {
                var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (manifest != null)
                    deliveries.Add(new Delivery(dir, manifest));
            }
            catch (Exception exc)
            {
                Logger.Warning($"Skipping delivery '{dir}': manifest unreadable ({exc.Message})");
            }
        }
        return deliveries.OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
    }

    // ---- checksum validation ---------------------------------------------------

    public static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Validate every manifest entry's SHA-256. Throws ChecksumMismatchException
    /// on the first mismatch; FileNotFoundException when a listed file is absent.
    /// </summary>
    public static void ValidateChecksums(Delivery delivery)
    {
        foreach (var file in delivery.Manifest.Files)
        {
            var path = Path.Combine(delivery.Directory, file.Name);
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Delivery '{delivery.Id}' is missing file '{file.Name}' listed in the manifest", path);
            var actual = ComputeSha256(path);
            var expected = file.Sha256.Trim().ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new ChecksumMismatchException(file.Name, expected, actual);
        }
    }

    // ---- parsing -------------------------------------------------------------------

    /// <summary>FEED_JSON_MAX_MB — size cap for .json ARRAY feed files, which
    /// must be fully parsed in memory (default 256 MB). .jsonl/.csv stream
    /// line-by-line and are not capped.</summary>
    public const string JsonMaxMbEnvVar = "FEED_JSON_MAX_MB";
    public const long DefaultJsonMaxMb = 256;

    /// <summary>Test seam: overrides the env-derived .json size cap (bytes).</summary>
    internal static long? JsonMaxBytesOverride;

    internal static long JsonMaxBytes()
    {
        if (JsonMaxBytesOverride is { } overridden)
            return overridden;
        var raw = Environment.GetEnvironmentVariable(JsonMaxMbEnvVar);
        return long.TryParse(raw, out var mb) && mb > 0
            ? mb * 1024L * 1024L
            : DefaultJsonMaxMb * 1024L * 1024L;
    }

    /// <summary>
    /// Parse a data file into records. JSON (.json/.jsonl) or CSV (.csv).
    /// The file is opened ONCE: when <paramref name="expectedSha256"/> is
    /// given, the manifest checksum is verified and the records parsed from
    /// the SAME open handle, so a file swapped between the upfront checksum
    /// gate and the read (TOCTOU) can never be ingested under the manifest's
    /// hash. Oversized .json arrays are refused up front (FEED_JSON_MAX_MB)
    /// instead of being materialized unbounded.
    /// </summary>
    public static IReadOnlyList<FeedRecord> ReadRecords(string filePath, string dataset,
        string? expectedSha256 = null)
    {
        var canonical = Datasets.Canonical(dataset);
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (extension is not (".json" or ".jsonl" or ".csv"))
            throw new NotSupportedException($"Unsupported feed file type '{extension}' ({filePath})");

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (extension == ".json" && stream.Length > JsonMaxBytes())
        {
            throw new FeedFileTooLargeException(
                $"'{filePath}' is {stream.Length} bytes — .json array feeds are parsed in memory and " +
                $"capped at {JsonMaxBytes()} bytes ({JsonMaxMbEnvVar}, default {DefaultJsonMaxMb} MB). " +
                "Deliver large datasets as .jsonl (streamed line-by-line, uncapped).");
        }
        if (expectedSha256 != null)
        {
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            var expected = expectedSha256.Trim().ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new ChecksumMismatchException(Path.GetFileName(filePath), expected, actual);
            stream.Position = 0;
        }
        return extension switch
        {
            ".json" => ParseJson(stream, canonical),
            ".jsonl" => ParseJsonLines(ReadLinesFrom(stream), canonical),
            _ => ParseCsv(ReadLinesFrom(stream), canonical),
        };
    }

    private static IEnumerable<string> ReadLinesFrom(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    /// <summary>Parse a JSON array of objects directly from a stream (no
    /// intermediate string materialization).</summary>
    internal static IReadOnlyList<FeedRecord> ParseJson(Stream stream, string dataset)
    {
        using var doc = JsonDocument.Parse(stream);
        return ParseJsonDocument(doc, dataset);
    }

    internal static IReadOnlyList<FeedRecord> ParseJson(string json, string dataset)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseJsonDocument(doc, dataset);
    }

    private static IReadOnlyList<FeedRecord> ParseJsonDocument(JsonDocument doc, string dataset)
    {
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new FormatException("Feed JSON must be an array of objects");
        var records = new List<FeedRecord>(doc.RootElement.GetArrayLength());
        foreach (var element in doc.RootElement.EnumerateArray())
            records.Add(ToRecord(element, dataset));
        return records;
    }

    internal static IReadOnlyList<FeedRecord> ParseJsonLines(IEnumerable<string> lines, string dataset)
    {
        var records = new List<FeedRecord>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using var doc = JsonDocument.Parse(line);
            records.Add(ToRecord(doc.RootElement, dataset));
        }
        return records;
    }

    private static FeedRecord ToRecord(JsonElement element, string dataset)
    {
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            fields[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => property.Value.GetString(),
                _ => property.Value.GetRawText(),
            };
        }
        return new FeedRecord { Dataset = dataset, Fields = fields };
    }

    // Minimal RFC-4180 CSV: quoted fields, embedded commas/quotes/newlines inside
    // quotes are NOT split across physical lines (feed files are line-per-record).
    internal static IReadOnlyList<FeedRecord> ParseCsv(IEnumerable<string> lines, string dataset)
    {
        var records = new List<FeedRecord>();
        string[]? header = null;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var cells = SplitCsvLine(line);
            if (header == null)
            {
                header = cells;
                continue;
            }
            var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Length; i++)
            {
                var value = i < cells.Length ? cells[i] : null;
                fields[header[i]] = string.IsNullOrEmpty(value) ? null : value;
            }
            records.Add(new FeedRecord { Dataset = dataset, Fields = fields });
        }
        return records;
    }

    internal static string[] SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(ch);
                }
            }
            else if (ch == '"')
            {
                inQuotes = true;
            }
            else if (ch == ',')
            {
                cells.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(ch);
            }
        }
        cells.Add(sb.ToString());
        return cells.ToArray();
    }
}
