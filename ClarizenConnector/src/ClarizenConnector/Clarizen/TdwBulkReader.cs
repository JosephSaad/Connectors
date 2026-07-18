// Clarizen/TdwBulkReader.cs
// -------------------------
// Bulk "tdw" (Clarizen data-warehouse export) reader for full crawls.
//
// Clarizen's TDW ships nightly CSV/JSON exports of every entity table. When
// TDW_EXPORT_PATH points at a directory containing {ObjectName}.csv or
// {ObjectName}.json, full crawls read records from those files instead of
// spending live API budget; incremental crawls always use the REST API.
// The API path is the automatic fallback for object types with no export file.
//
//   {ObjectName}.json — a JSON array of objects, or {"entities": [...]}.
//   {ObjectName}.csv  — RFC-4180 style: header row of field names; quoted
//                       fields with "" escaping; commas/newlines inside quotes.

using System.Text;
using System.Text.Json.Nodes;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Clarizen;

public sealed class TdwBulkReader
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector.tdw");

    private readonly string _exportPath;

    public TdwBulkReader(string exportPath)
    {
        _exportPath = exportPath;
    }

    /// <summary>True when a bulk export file exists for <paramref name="objectName"/>.</summary>
    public bool HasExport(string objectName) => FindFile(objectName) is not null;

    internal string? FindFile(string objectName)
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(_exportPath, objectName + ".json"),
                     Path.Combine(_exportPath, objectName + ".csv"),
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>Read all records for <paramref name="objectName"/> from the export file.</summary>
    public List<JsonObject> ReadObject(string objectName)
    {
        var path = FindFile(objectName)
            ?? throw new FileNotFoundException(
                $"No TDW export file for '{objectName}' under '{_exportPath}' "
                + $"(expected {objectName}.json or {objectName}.csv).");
        Logger.Info($"TDW bulk read: {path}");
        try
        {
            return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? ParseJson(File.ReadAllText(path))
                : ParseCsv(File.ReadAllText(path));
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
        {
            // Rethrow with the FILE named: a bare JsonException ("invalid char at
            // position N") gives an operator nothing to act on. The whole file is
            // unusable, so this stays fatal for the object (worker-crash path,
            // dead-lettered + logged upstream) — but now it says which export.
            Logger.Error($"TDW export '{path}' for object '{objectName}' failed to parse", exc);
            throw new InvalidDataException(
                $"TDW export '{path}' for object '{objectName}' failed to parse: "
                + $"{exc.GetType().Name}: {exc.Message}",
                exc);
        }
    }

    /// <summary>Parse a JSON export: bare array or {"entities": [...]}.</summary>
    internal static List<JsonObject> ParseJson(string json)
    {
        var node = JsonNode.Parse(json);
        var array = node switch
        {
            JsonArray direct => direct,
            JsonObject wrapper when wrapper["entities"] is JsonArray entities => entities,
            _ => throw new InvalidDataException(
                "TDW JSON export must be an array of objects or {\"entities\": [...]}."),
        };
        var records = new List<JsonObject>(array.Count);
        foreach (var element in array)
        {
            if (element is JsonObject obj)
                records.Add((JsonObject)obj.DeepClone());
        }
        return records;
    }

    /// <summary>Parse a CSV export (header row; quoted fields; "" escaping; embedded commas/newlines).</summary>
    internal static List<JsonObject> ParseCsv(string csv)
    {
        var rows = TokenizeCsv(csv);
        if (rows.Count == 0)
            return new List<JsonObject>();

        var header = rows[0];
        var records = new List<JsonObject>(rows.Count - 1);
        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.Count == 1 && row[0].Length == 0)
                continue;  // trailing blank line
            var record = new JsonObject();
            for (var c = 0; c < header.Count; c++)
            {
                var value = c < row.Count ? row[c] : string.Empty;
                record[header[c]] = value.Length == 0 ? null : JsonValue.Create(value);
            }
            records.Add(record);
        }
        return records;
    }

    private static List<List<string>> TokenizeCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
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
                    // swallow; \n terminates the row
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
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
            rows.Add(row);
        }
        return rows;
    }
}
