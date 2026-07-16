// Altrata/ClassificationManifest.cs
// ---------------------------------
// Optional per-delivery classification summary (CLASSIFICATION_MANIFEST=true),
// file-based like the reconciliation reports. Records counts per sensitivity
// label / detected category and an item-id → label map.
//
// PII CAUTION (asserted by tests): the manifest contains NO personal data —
// only opaque item ids, sensitivity-label names, category labels and counts.
// Never a name, email, national id or wealth figure.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using AltrataConnector.Infrastructure;

namespace AltrataConnector.Altrata;

/// <summary>One classified item — id + label + category labels (no values).</summary>
public sealed record ClassificationEntry(
    string ItemId, string Label, IReadOnlyList<string> Categories);

public static class ClassificationManifest
{
    private static readonly JsonSerializerOptions JsonlOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ManifestPath(string connectorId, string deliveryId, string? logsDir = null)
    {
        var dir = logsDir
            ?? Environment.GetEnvironmentVariable("LOGS_DIR")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "logs");
        var safe = string.Concat(deliveryId.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
        return Path.Combine(dir, $"classification_{connectorId}_{safe}.jsonl");
    }

    /// <summary>
    /// Write the JSONL manifest: one line per item (id → label + categories),
    /// then a summary line with counts per label and per category. Returns the
    /// path. No personal values are ever written.
    /// </summary>
    public static string Write(string connectorId, string deliveryId,
        IReadOnlyList<ClassificationEntry> entries, string? logsDir = null)
    {
        var path = ManifestPath(connectorId, deliveryId, logsDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var byLabel = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var byCategory = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            byLabel[entry.Label] = byLabel.GetValueOrDefault(entry.Label) + 1;
            foreach (var category in entry.Categories)
                byCategory[category] = byCategory.GetValueOrDefault(category) + 1;
        }

        using var writer = new StreamWriter(path, append: false);
        foreach (var entry in entries)
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                type = "item",
                deliveryId,
                itemId = entry.ItemId,
                label = entry.Label,
                categories = entry.Categories,
            }, JsonlOptions));
        writer.WriteLine(JsonSerializer.Serialize(new
        {
            type = "summary",
            deliveryId,
            correlationId = CorrelationContext.Current,
            totalItems = entries.Count,
            countsByLabel = byLabel,
            countsByCategory = byCategory,
        }, JsonlOptions));
        return path;
    }
}

/// <summary>
/// Process-global labeled classification counters, rendered as Prometheus lines
/// by the health endpoint (the Metrics core is label-less). Values are label /
/// category names and counts only.
/// </summary>
public static class ClassificationStats
{
    private static readonly ConcurrentDictionary<string, long> Labels = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> Categories = new(StringComparer.Ordinal);

    public static void RecordItem(string label, IEnumerable<string> categories)
    {
        Labels.AddOrUpdate(label, 1, (_, n) => n + 1);
        foreach (var category in categories)
            Categories.AddOrUpdate(category, 1, (_, n) => n + 1);
    }

    public static long ItemsForLabel(string label) => Labels.TryGetValue(label, out var n) ? n : 0;
    public static long DetectionsForCategory(string category) => Categories.TryGetValue(category, out var n) ? n : 0;

    internal static void ResetForTests()
    {
        Labels.Clear();
        Categories.Clear();
    }

    /// <summary>Labeled Prometheus lines: items_classified_total{label},
    /// sensitive_detections_total{category}.</summary>
    public static string RenderPrometheus()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("# HELP altrata_items_classified_total Items stamped with a sensitivity label.\n");
        sb.Append("# TYPE altrata_items_classified_total counter\n");
        foreach (var (label, count) in Labels.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append($"altrata_items_classified_total{{label=\"{Escape(label)}\"}} {count}\n");
        sb.Append("# HELP altrata_sensitive_detections_total Field-classifier category detections.\n");
        sb.Append("# TYPE altrata_sensitive_detections_total counter\n");
        foreach (var (category, count) in Categories.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append($"altrata_sensitive_detections_total{{category=\"{Escape(category)}\"}} {count}\n");
        return sb.ToString();
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "");
}
