// Seismic/ClassificationManifest.cs
// ---------------------------------
// Optional classification export (CLASSIFICATION_MANIFEST=true).
//
// Writes a per-crawl JSONL manifest of every classified item — item id →
// sensitivity TAG + detected categories — plus a trailing summary object with
// per-label and per-category counts. File-based, exactly like the
// reconciliation/drift/reacl reports; a downstream catalog/DLP job can pick it
// up. The tag is an ADVISORY connector-applied classification (Purview-aligned
// in NAMING only) — NOT a Purview-enforced sensitivity label. There is NO live
// Purview API call.

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SeismicConnector.Seismic;

public sealed class ClassificationManifest : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private readonly object _lock = new();
    private readonly StreamWriter? _writer;
    private readonly Dictionary<string, int> _byLabel = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _byCategory = new(StringComparer.Ordinal);
    private int _total;
    private bool _finished;

    public string? FilePath { get; }

    /// <summary>Open a file-backed manifest at <paramref name="filePath"/> (parent dirs created).</summary>
    public ClassificationManifest(string filePath)
    {
        FilePath = filePath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _writer = new StreamWriter(filePath, append: false, Utf8NoBom);
    }

    /// <summary>In-memory manifest (counts only) — used when export is off, and by tests.</summary>
    public ClassificationManifest()
    {
        FilePath = null;
    }

    public int Total
    {
        get { lock (_lock) return _total; }
    }

    public IReadOnlyDictionary<string, int> CountsByLabel
    {
        get { lock (_lock) return new Dictionary<string, int>(_byLabel); }
    }

    public IReadOnlyDictionary<string, int> CountsByCategory
    {
        get { lock (_lock) return new Dictionary<string, int>(_byCategory); }
    }

    /// <summary>Record one classified item (label + detected categories).</summary>
    public void RecordItem(
        string itemId, string? teamsiteId, SensitivityLabel label, IReadOnlyList<string> categories)
    {
        lock (_lock)
        {
            _total++;
            var labelName = label.ToString();
            _byLabel.TryGetValue(labelName, out var labelCount);
            _byLabel[labelName] = labelCount + 1;
            foreach (var category in categories)
            {
                _byCategory.TryGetValue(category, out var catCount);
                _byCategory[category] = catCount + 1;
            }

            if (_writer is null)
                return;
            var entry = new Dictionary<string, object?>
            {
                ["item_id"] = itemId,
                ["teamsite_id"] = teamsiteId,
                ["sensitivity_label"] = labelName,
                ["detected_categories"] = categories,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            };
            var correlationId = Tracing.CurrentCorrelationId;
            if (correlationId is not null)
                entry["correlation_id"] = correlationId;
            _writer.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
            _writer.Flush();
        }
    }

    /// <summary>Write the trailing summary object and close the file. Idempotent.</summary>
    public void Finish()
    {
        lock (_lock)
        {
            if (_finished)
                return;
            _finished = true;
            if (_writer is null)
                return;
            var summary = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["record"] = "summary",
                ["classified_total"] = _total,
                ["by_label"] = _byLabel,
                ["by_category"] = _byCategory,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            }, JsonOptions);
            _writer.WriteLine(summary);
            _writer.Flush();
            _writer.Dispose();
        }
    }

    public void Dispose() => Finish();
}
