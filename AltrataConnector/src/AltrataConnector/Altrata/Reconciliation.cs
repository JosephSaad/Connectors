// Altrata/Reconciliation.cs
// -------------------------
// Per-delivery reconciliation: ingested + dead-lettered must equal the
// manifest record count for every file. Produces a JSONL report (one line per
// file plus a summary line) at logs/reconciliation_{CONNECTOR_ID}_{deliveryId}.jsonl
// and returns a summary object. See docs/FEEDS.md.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AltrataConnector.Altrata;

public sealed record FileReconciliation
{
    [JsonPropertyName("file")]
    public required string File { get; init; }

    [JsonPropertyName("dataset")]
    public required string Dataset { get; init; }

    [JsonPropertyName("manifestCount")]
    public required int ManifestCount { get; init; }

    [JsonPropertyName("parsedCount")]
    public required int ParsedCount { get; init; }

    [JsonPropertyName("ingested")]
    public required int Ingested { get; init; }

    [JsonPropertyName("deadLettered")]
    public required int DeadLettered { get; init; }

    /// <summary>Tombstones withdrawn from the index (delta deliveries).</summary>
    [JsonPropertyName("deleted")]
    public int Deleted { get; init; }

    /// <summary>Records skipped because the subject is on the erasure suppression list.</summary>
    [JsonPropertyName("suppressed")]
    public int Suppressed { get; init; }

    /// <summary>ingested + deleted + suppressed + deadLettered == manifestCount.</summary>
    [JsonPropertyName("reconciled")]
    public bool Reconciled => Ingested + Deleted + Suppressed + DeadLettered == ManifestCount;

    [JsonPropertyName("delta")]
    public int Delta => Ingested + Deleted + Suppressed + DeadLettered - ManifestCount;
}

public sealed record DeliveryReconciliation
{
    [JsonPropertyName("deliveryId")]
    public required string DeliveryId { get; init; }

    /// <summary>reconciled | mismatch | rejected (checksum failure).</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("completedUtc")]
    public DateTime CompletedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Correlation id of the crawl that produced this report.</summary>
    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("totalManifestRecords")]
    public int TotalManifestRecords { get; init; }

    [JsonPropertyName("totalIngested")]
    public int TotalIngested { get; init; }

    [JsonPropertyName("totalDeadLettered")]
    public int TotalDeadLettered { get; init; }

    [JsonPropertyName("totalDeleted")]
    public int TotalDeleted { get; init; }

    [JsonPropertyName("totalSuppressed")]
    public int TotalSuppressed { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("files")]
    public IReadOnlyList<FileReconciliation> Files { get; init; } = Array.Empty<FileReconciliation>();
}

public static class Reconciliation
{
    public const string StatusReconciled = "reconciled";
    public const string StatusMismatch = "mismatch";
    public const string StatusRejected = "rejected";

    private static readonly JsonSerializerOptions JsonlOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Build the delivery summary from per-file results.</summary>
    public static DeliveryReconciliation Summarize(
        string deliveryId, IReadOnlyList<FileReconciliation> files, string? error = null)
    {
        var status = error != null
            ? StatusRejected
            : files.All(f => f.Reconciled) ? StatusReconciled : StatusMismatch;
        return new DeliveryReconciliation
        {
            DeliveryId = deliveryId,
            Status = status,
            TotalManifestRecords = files.Sum(f => f.ManifestCount),
            TotalIngested = files.Sum(f => f.Ingested),
            TotalDeadLettered = files.Sum(f => f.DeadLettered),
            TotalDeleted = files.Sum(f => f.Deleted),
            TotalSuppressed = files.Sum(f => f.Suppressed),
            Error = error,
            CorrelationId = Infrastructure.CorrelationContext.Current,
            Files = files,
        };
    }

    /// <summary>Report path for a delivery.</summary>
    public static string ReportPath(string connectorId, string deliveryId, string? logsDir = null)
    {
        var dir = logsDir
            ?? Environment.GetEnvironmentVariable("LOGS_DIR")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "logs");
        var safe = string.Concat(deliveryId.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
        return Path.Combine(dir, $"reconciliation_{connectorId}_{safe}.jsonl");
    }

    /// <summary>Write the JSONL report: one line per file, then the summary line.</summary>
    public static string WriteReport(string connectorId, DeliveryReconciliation summary, string? logsDir = null)
    {
        var path = ReportPath(connectorId, summary.DeliveryId, logsDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Written through a shared handle, not `new StreamWriter(path, false)`.
        // That overload takes FileShare.Read, which refuses any concurrent handle
        // holding write access; Windows enforces share modes and POSIX does not,
        // so on Windows Server a report written while anything else has the file
        // open throws IOException and takes the delivery down with it. The report
        // IS read concurrently — reconciliation output is what the dead-letter
        // correlation check joins against — so this is a live path, not a
        // theoretical one. Same defect class as the dead-letter queue and the
        // checkpoint; this was the third file carrying it.
        using (var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        using (var writer = new StreamWriter(stream))
        {
            foreach (var file in summary.Files)
                writer.WriteLine(JsonSerializer.Serialize(
                    new { type = "file", deliveryId = summary.DeliveryId, detail = file }, JsonlOptions));
            writer.WriteLine(JsonSerializer.Serialize(
                new { type = "summary", detail = summary }, JsonlOptions));
        }

        return path;
    }
}
