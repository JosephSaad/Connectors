// State/Models.cs
// ---------------
// Serializable state records shared by the file and SQL Server state backends.

using System.Text.Json.Serialization;

namespace AltrataConnector.State;

/// <summary>Crash-resume checkpoint: position inside a feed delivery.</summary>
public sealed record CrawlCheckpoint
{
    public required string DeliveryId { get; init; }
    public required string Dataset { get; init; }
    public required string FileName { get; init; }
    public int RecordIndex { get; init; }
    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>Dead-letter operations (what retry-failed should replay).</summary>
public static class DeadLetterOps
{
    public const string Upsert = "upsert";
    public const string Delete = "delete";

    /// <summary>A record that failed TRANSFORM (before an item existed to PUT).
    /// Deterministic — replaying cannot fix it; fix the source feed and re-run
    /// ingest, or retire it with `retry-failed --retire-unreplayable`.</summary>
    public const string Transform = "transform";
}

/// <summary>One dead-lettered record (JSONL line in file mode, row in SQL mode).</summary>
public sealed record DeadLetterRecord
{
    public required string ItemId { get; init; }
    public string Dataset { get; init; } = "";
    public string DeliveryId { get; init; } = "";
    public string Error { get; init; } = "";
    /// <summary>upsert (replay = PUT payload) or delete (replay = DELETE item).
    /// Defaults to upsert so pre-existing queue files stay replayable.</summary>
    public string Op { get; init; } = DeadLetterOps.Upsert;
    /// <summary>Correlation id of the cycle that produced this failure (nullable).</summary>
    public string? CorrelationId { get; init; }
    /// <summary>The transformed external item as JSON, replayable by retry-failed.
    /// Empty for delete ops (the item id is the whole payload).</summary>
    public string PayloadJson { get; init; } = "";
    public DateTime FailedUtc { get; init; } = DateTime.UtcNow;
    public int Attempts { get; init; } = 1;

    /// <summary>
    /// True when retry-failed can act on this record: DELETEs carry their whole
    /// payload in the item id, upserts need the captured item JSON. Transform
    /// failures (op 'transform', or legacy upserts with an empty payload from
    /// queues written before the op existed) are NOT replayable — they are
    /// excluded from the dead-letter alert depth and retired explicitly.
    /// </summary>
    [JsonIgnore]
    public bool IsReplayable =>
        Op == DeadLetterOps.Delete
        || (Op == DeadLetterOps.Upsert && PayloadJson.Length > 0);
}

/// <summary>Crawl kinds tracked by the sync timestamps.</summary>
public static class CrawlKind
{
    public const string Full = "full";
    public const string Incremental = "incremental";
}
