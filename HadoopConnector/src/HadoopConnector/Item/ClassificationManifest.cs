// Item/ClassificationManifest.cs
// ------------------------------
// Optional advisory-classification export (CLASSIFICATION_MANIFEST=true).
// A per-crawl JSONL under logs/ mapping each item to its connector-applied
// sensitivity TAG + detected categories, plus a summary line with per-label
// counts — for catalog / DLP ingestion. File-based, like the reconciliation
// reports: NO live Microsoft Purview API call, and the tag is advisory (it is
// not a Purview-enforced label). Thread-safe (concurrent object/batch workers
// append).
//
// File: logs/classification_{connectorId}_{yyyyMMdd_HHmmss}.jsonl
//   per item:  {"item_id","object_type","sensitivity_label","categories":[...]}
//   summary:   {"kind":"summary","total":N,"counts":{"Restricted":..,...},
//               "connector","timestamp"} — written as the final line on Flush().

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Item;

public sealed class ClassificationManifest
{
    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector.classification");
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string _connectorId;
    private readonly string _path;
    private readonly object _lock = new();
    private readonly List<JsonObject> _entries = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    public ClassificationManifest(string connectorId, string logsDir, DateTime? nowUtc = null)
    {
        _connectorId = connectorId;
        var stamp = (nowUtc ?? DateTime.UtcNow).ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(logsDir);
        _path = System.IO.Path.Combine(logsDir, $"classification_{connectorId}_{stamp}.jsonl");
    }

    public string Path => _path;

    /// <summary>Record one classified item. Thread-safe.</summary>
    public void Record(string itemId, string objectType, SensitivityLabel label, IReadOnlyList<string> categories)
    {
        var entry = new JsonObject
        {
            ["item_id"] = itemId,
            ["object_type"] = objectType,
            ["sensitivity_label"] = label.ToString(),
            ["categories"] = new JsonArray(categories.Select(c => (JsonNode?)c).ToArray()),
        };
        lock (_lock)
        {
            _entries.Add(entry);
            _counts[label.ToString()] = _counts.GetValueOrDefault(label.ToString()) + 1;
        }
    }

    /// <summary>Write the manifest file (per-item lines + trailing summary). Never throws.</summary>
    public void Flush()
    {
        try
        {
            List<JsonObject> entries;
            JsonObject summary;
            lock (_lock)
            {
                entries = _entries.ToList();
                var counts = new JsonObject();
                foreach (var kv in _counts.OrderBy(k => k.Key, StringComparer.Ordinal))
                    counts[kv.Key] = kv.Value;
                summary = new JsonObject
                {
                    ["kind"] = "summary",
                    ["connector"] = _connectorId,
                    ["total"] = entries.Count,
                    ["counts"] = counts,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                };
            }

            using var writer = new StreamWriter(_path, append: false, Utf8NoBom);
            foreach (var entry in entries)
                writer.WriteLine(entry.ToJsonString(Compact));
            writer.WriteLine(summary.ToJsonString(Compact));
            Logger.Info($"Classification manifest written: {_path} ({entries.Count} item(s)).");
        }
        catch (Exception exc)
        {
            Logger.Warning($"Could not write classification manifest '{_path}': {exc.Message}");
        }
    }

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
}
