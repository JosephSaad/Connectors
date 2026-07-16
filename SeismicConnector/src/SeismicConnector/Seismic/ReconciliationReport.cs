// Seismic/ReconciliationReport.cs
// -------------------------------
// Auditable record of every item the No-MNE filter skipped or withdrew during
// a crawl. One JSONL line per event plus a summary object written when the
// crawl finishes; the summary counts are also folded into the run summary and
// surfaced on the dashboard.

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SeismicConnector.Seismic;

public sealed class ReconciliationReport : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private readonly object _lock = new();
    private readonly StreamWriter? _writer;
    private readonly Dictionary<string, int> _countsByRule = new(StringComparer.Ordinal);
    private int _excluded;
    private int _withdrawn;
    private bool _finished;

    public string? FilePath { get; }

    /// <summary>Open a report writing to <paramref name="filePath"/> (parent dirs created).</summary>
    public ReconciliationReport(string filePath)
    {
        FilePath = filePath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _writer = new StreamWriter(filePath, append: false, Utf8NoBom);
    }

    /// <summary>In-memory report (counts only) — used by targeted single-item paths and tests.</summary>
    public ReconciliationReport()
    {
        FilePath = null;
    }

    public int ExcludedCount
    {
        get { lock (_lock) return _excluded; }
    }

    public int WithdrawnCount
    {
        get { lock (_lock) return _withdrawn; }
    }

    public IReadOnlyDictionary<string, int> CountsByRule
    {
        get { lock (_lock) return new Dictionary<string, int>(_countsByRule); }
    }

    /// <summary>Record an item skipped at ingest time.</summary>
    public void RecordExcluded(string itemId, string? teamsiteId, string rule, string reason) =>
        Record("excluded", itemId, teamsiteId, rule, reason);

    /// <summary>Record an already-ingested item withdrawn from the Graph connection.</summary>
    public void RecordWithdrawn(string itemId, string? teamsiteId, string rule, string reason) =>
        Record("withdrawn", itemId, teamsiteId, rule, reason);

    private void Record(string action, string itemId, string? teamsiteId, string rule, string reason)
    {
        lock (_lock)
        {
            if (action == "excluded")
                _excluded++;
            else
                _withdrawn++;
            _countsByRule.TryGetValue(rule, out var count);
            _countsByRule[rule] = count + 1;

            if (_writer is null)
                return;
            var entry = new Dictionary<string, object?>
            {
                ["action"] = action,
                ["item_id"] = itemId,
                ["teamsite_id"] = teamsiteId,
                ["rule"] = rule,
                ["reason"] = reason,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            };
            var correlationId = Infrastructure.Tracing.CurrentCorrelationId;
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
                ["action"] = "summary",
                ["excluded_total"] = _excluded,
                ["withdrawn_total"] = _withdrawn,
                ["by_rule"] = _countsByRule,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            }, JsonOptions);
            _writer.WriteLine(summary);
            _writer.Flush();
            _writer.Dispose();
        }
    }

    public void Dispose() => Finish();
}
