// Altrata/AuditLog.cs
// -------------------
// Purpose-of-use audit trail. Every Altrata enrichment API lookup (a billable
// event touching personal data) is appended to logs/audit_{CONNECTOR_ID}.jsonl:
//
//   {"timestampUtc": "...", "actor": "...", "action": "api_lookup",
//    "altrataId": "...", "purpose": "...", "billable": true}
//
// The file is append-only by construction: the writer only ever opens it in
// append mode and no code path rewrites or truncates it (purge-all keeps the
// audit trail — the license-end obligation is to remove Altrata DATA, while
// the audit trail documents lawful use).

using System.Text;
using System.Text.Json;

namespace AltrataConnector.Altrata;

public sealed record AuditEntry
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public required string Actor { get; init; }
    public required string Action { get; init; }
    public string? AltrataId { get; init; }
    public required string Purpose { get; init; }
    public bool Billable { get; init; }
}

public interface IAuditLog
{
    void Append(AuditEntry entry);
}

public sealed class AuditLog : IAuditLog
{
    private readonly object _sync = new();

    public string Path { get; }

    public AuditLog(string connectorId, string? logsDir = null)
    {
        var dir = logsDir
            ?? Environment.GetEnvironmentVariable("LOGS_DIR")
            ?? System.IO.Path.Combine(Directory.GetCurrentDirectory(), "logs");
        Path = System.IO.Path.Combine(dir, $"audit_{connectorId}.jsonl");
    }

    public void Append(AuditEntry entry)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            // Append-only: FileMode.Append can never truncate existing entries.
            using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine(JsonSerializer.Serialize(entry));
        }
    }

    public IReadOnlyList<AuditEntry> ReadAll()
    {
        lock (_sync)
        {
            if (!File.Exists(Path))
                return Array.Empty<AuditEntry>();
            var entries = new List<AuditEntry>();
            foreach (var line in File.ReadLines(Path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<AuditEntry>(line);
                    if (entry != null)
                        entries.Add(entry);
                }
                catch
                {
                    // tolerate manual edits; the write path never produces bad lines
                }
            }
            return entries;
        }
    }
}
