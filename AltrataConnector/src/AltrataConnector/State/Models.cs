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
    /// Empty for delete ops (the item id is the whole payload) and for records
    /// written under DEADLETTER_PAYLOAD_MODE=redacted (see <see cref="Redacted"/>).</summary>
    public string PayloadJson { get; init; } = "";
    public DateTime FailedUtc { get; init; } = DateTime.UtcNow;
    public int Attempts { get; init; } = 1;

    /// <summary>True when the item payload was WITHHELD by
    /// DEADLETTER_PAYLOAD_MODE=redacted (this connector's default — the queue
    /// must not hold wealth/relationship profiles at rest; SECURITY.md).
    /// retry-failed re-fetches the record from the feed delivery
    /// (checksum-verified) and re-transforms it instead of re-PUTting a
    /// captured payload.</summary>
    public bool Redacted { get; init; }

    /// <summary>Raw subject person id(s) of the record — stamped in FULL
    /// payload mode only (the payload already carries the whole profile, so
    /// the ids add no exposure). Drives the DSAR suppression guard and the
    /// item↔subject reverse index on replay.</summary>
    public IReadOnlyList<string> SubjectIds { get; init; } = Array.Empty<string>();

    /// <summary>Short SHA-256 of each subject id — stamped in BOTH modes; the
    /// redacted queue carries hashes ONLY. Lets forget-subject scrub queued
    /// records and retry-failed refuse erased subjects without the queue ever
    /// naming a person (DeadLetterPolicy.HashSubject).</summary>
    public IReadOnlyList<string> SubjectHashes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// True when retry-failed can act on this record: DELETEs carry their whole
    /// payload in the item id; upserts need the captured item JSON OR the
    /// redacted marker (replay then re-fetches from the feed). Transform
    /// failures (op 'transform', or legacy upserts with an empty payload from
    /// queues written before the op existed) are NOT replayable — they are
    /// excluded from the dead-letter alert depth and retired explicitly.
    /// </summary>
    [JsonIgnore]
    public bool IsReplayable =>
        Op == DeadLetterOps.Delete
        || (Op == DeadLetterOps.Upsert && (PayloadJson.Length > 0 || Redacted));
}

/// <summary>Crawl kinds tracked by the sync timestamps.</summary>
public static class CrawlKind
{
    public const string Full = "full";
    public const string Incremental = "incremental";
}
