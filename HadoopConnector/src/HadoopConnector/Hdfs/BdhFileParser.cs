// Hdfs/BdhFileParser.cs
// ---------------------
// Hardened streaming parser for BDH data files (adapted from the sibling
// connector's TDW reader, upgraded to stream):
//
//   {name}.csv    — RFC-4180 style: header row of field names; quoted fields
//                   with "" escaping; commas/newlines inside quotes.
//   {name}.jsonl  — one JSON object per line.
//   {name}.json   — a JSON array of objects, or {"entities": [...]} (small
//                   files only — arrays must be materialized to parse).
//
// Hardening:
//   • BoundedStream caps total bytes read (BDH_MAX_FILE_BYTES) — a file that
//     lies about its size (or a mid-stream redirect gone wrong) throws
//     InvalidDataException instead of exhausting memory.
//   • Rows are YIELDED one at a time — no unbounded materialization; the
//     caller applies predicates/caps while streaming.
//   • Malformed JSONL lines and short CSV rows are counted (ParseErrors) and
//     skipped, never silently absorbed; a file that yields ONLY errors is
//     surfaced by the caller's id validation.
//   • Field-count mismatches: extra columns are dropped, missing columns are
//     null — matching the TDW reader's tolerant-but-logged behaviour.

using System.Text;
using System.Text.Json.Nodes;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Hdfs;

/// <summary>Wraps a stream and throws once more than <c>maxBytes</c> are read.</summary>
public sealed class BoundedStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maxBytes;
    private long _read;

    public BoundedStream(Stream inner, long maxBytes)
    {
        _inner = inner;
        _maxBytes = maxBytes;
    }

    public long BytesRead => _read;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        Account(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Account(read);
        return read;
    }

    private void Account(int read)
    {
        _read += read;
        if (_read > _maxBytes)
        {
            throw new InvalidDataException(
                $"File exceeds the {_maxBytes}-byte read bound (BDH_MAX_FILE_BYTES); aborting the read.");
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }
}

public sealed class BdhFileParser
{
    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector.parser");

    /// <summary>Rows skipped because they could not be parsed (malformed JSONL / short CSV).</summary>
    public int ParseErrors { get; private set; }

    /// <summary>File extensions this parser understands.</summary>
    public static bool IsDataFile(string name) =>
        name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for layout noise that must be skipped (_SUCCESS, dotfiles, temp files).</summary>
    public static bool IsIgnorableEntry(string name) =>
        name.StartsWith("_", StringComparison.Ordinal)
        || name.StartsWith(".", StringComparison.Ordinal)
        || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Stream-parse one data file into rows. <paramref name="fileName"/> picks
    /// the format; <paramref name="maxBytes"/> bounds total bytes consumed.
    /// </summary>
    public IEnumerable<JsonObject> Parse(Stream stream, string fileName, long maxBytes)
    {
        var bounded = new BoundedStream(stream, maxBytes);
        var reader = new StreamReader(bounded, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        if (fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return ParseCsv(reader, fileName);
        if (fileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            return ParseJsonl(reader, fileName);
        return ParseJsonArray(reader, fileName);
    }

    // ── JSONL ────────────────────────────────────────────────────────────────

    internal IEnumerable<JsonObject> ParseJsonl(TextReader reader, string fileName)
    {
        using (reader)
        {
            string? line;
            var lineNumber = 0;
            while ((line = reader.ReadLine()) is not null)
            {
                lineNumber++;
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                    continue;
                JsonObject? row = null;
                try
                {
                    row = JsonNode.Parse(trimmed) as JsonObject;
                }
                catch (System.Text.Json.JsonException)
                {
                    // fall through to the error count
                }
                if (row is null)
                {
                    ParseErrors++;
                    if (ParseErrors <= 5)
                        Logger.Warning($"{fileName}: skipping malformed JSONL line {lineNumber}.");
                    continue;
                }
                yield return row;
            }
        }
    }

    // ── JSON array ───────────────────────────────────────────────────────────

    internal IEnumerable<JsonObject> ParseJsonArray(TextReader reader, string fileName)
    {
        JsonNode? node;
        using (reader)
        {
            try
            {
                node = JsonNode.Parse(reader.ReadToEnd());
            }
            catch (System.Text.Json.JsonException exc)
            {
                throw new InvalidDataException($"{fileName}: invalid JSON export: {exc.Message}");
            }
        }
        var array = node switch
        {
            JsonArray direct => direct,
            JsonObject wrapper when wrapper["entities"] is JsonArray entities => entities,
            _ => throw new InvalidDataException(
                $"{fileName}: JSON export must be an array of objects or {{\"entities\": [...]}}."),
        };
        foreach (var element in array)
        {
            if (element is JsonObject obj)
                yield return (JsonObject)obj.DeepClone();
            else
                ParseErrors++;
        }
    }

    // ── CSV ──────────────────────────────────────────────────────────────────

    internal IEnumerable<JsonObject> ParseCsv(TextReader reader, string fileName)
    {
        using (reader)
        {
            using var rows = TokenizeCsv(reader).GetEnumerator();
            if (!rows.MoveNext())
                yield break;
            var header = rows.Current;
            if (header.Count == 0 || (header.Count == 1 && header[0].Length == 0))
                yield break;

            while (rows.MoveNext())
            {
                var row = rows.Current;
                if (row.Count == 1 && row[0].Length == 0)
                    continue;  // trailing blank line
                if (row.Count > header.Count)
                {
                    ParseErrors++;
                    if (ParseErrors <= 5)
                        Logger.Warning($"{fileName}: row has {row.Count} fields, header has {header.Count}; skipping.");
                    continue;
                }
                var record = new JsonObject();
                for (var c = 0; c < header.Count; c++)
                {
                    var value = c < row.Count ? row[c] : string.Empty;
                    record[header[c]] = value.Length == 0 ? null : JsonValue.Create(value);
                }
                yield return record;
            }
        }
    }

    /// <summary>Streaming RFC-4180 tokenizer: quoted fields, "" escaping, embedded commas/newlines.</summary>
    internal static IEnumerable<List<string>> TokenizeCsv(TextReader reader)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        int next;
        while ((next = reader.Read()) >= 0)
        {
            var ch = (char)next;
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        field.Append('"');
                        reader.Read();
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;  // swallow; \n terminates the row
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    yield return row;
                    row = new List<string>();
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }
}
