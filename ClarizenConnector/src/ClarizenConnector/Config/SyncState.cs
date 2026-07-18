// Config/SyncState.cs
// -------------------
// Persistent sync state for the ingestion pipeline. Three concerns:
//
//  1. Delta sync timestamp — last successful sync completion time per
//     connector, so subsequent incremental runs fetch only changed records
//     (Clarizen modifiedDate > since).
//  2. Checkpointing — completed chunk index per object type within an
//     in-progress run, so a crash/service-stop resumes without re-processing.
//  3. Dead-letter queue — failed item IDs (with error + payload detail)
//     appended to a JSONL file for inspection and `retry-failed`.
//
// File layout (default backend), all under logs/:
//     sync_state.json                 {"<connectorId>": "<ISO-8601>"}
//     checkpoint_<connectorId>.json   {"since": "...", "completed": {"Project": 4}}
//     failed_records_<connectorId>.jsonl
//
// With USE_SQL_SERVER=true + SQL_CONNECTION_STRING every method routes to
// SqlStateStore instead (shared database — required for HA).

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Config;

public static class SyncState
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector");
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>State directory. Settable so tests can redirect state files.</summary>
    public static string LogsDir { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "logs");

    private static bool? _useSqlServer;

    internal static bool UseSqlServer
    {
        get
        {
            _useSqlServer ??= EnvFlags.UseSqlServer;
            return _useSqlServer.Value;
        }
    }

    /// <summary>Test seam: re-read USE_SQL_SERVER / SQL_CONNECTION_STRING on next use.</summary>
    internal static void ResetProviderCache() => _useSqlServer = null;

    private static readonly object CheckpointLock = new();
    private static readonly object DeadLetterLock = new();

    // ── Delta sync timestamp ─────────────────────────────────────────────────

    private static string SyncStateFile => Path.Combine(LogsDir, "sync_state.json");

    /// <summary>Last successful sync timestamp (UTC) for <paramref name="connectorId"/>, or null.</summary>
    public static DateTime? ReadLastSync(string connectorId)
    {
        if (UseSqlServer)
            return SqlStateStore.ReadLastSync(connectorId);
        try
        {
            var data = JsonNode.Parse(File.ReadAllText(SyncStateFile, Utf8NoBom))!.AsObject();
            if (data.TryGetPropertyValue(connectorId, out var node)
                && node is not null
                && node.GetValueKind() == JsonValueKind.String)
            {
                return ParseIso(node.GetValue<string>());
            }
        }
        catch (Exception exc) when (
            exc is FileNotFoundException or DirectoryNotFoundException or JsonException or FormatException)
        {
            // A missing file is the normal "no state yet" case and stays silent;
            // an EXISTING file that fails to parse means the delta cursor was
            // lost (the next incremental behaves like a first run) — say so.
            WarnIfCorrupt(exc, SyncStateFile,
                $"last-sync state for '{connectorId}' unavailable; treating as no previous sync");
        }
        return null;
    }

    /// <summary>Log a warning for a state file that exists but cannot be parsed
    /// (missing-file conditions stay silent — they are the normal first-run path).</summary>
    private static void WarnIfCorrupt(Exception exc, string path, string consequence)
    {
        if (exc is FileNotFoundException or DirectoryNotFoundException)
            return;
        Logger.Warning(
            $"State file '{path}' exists but could not be parsed "
            + $"({exc.GetType().Name}: {exc.Message}) — {consequence}.");
    }

    public static void WriteLastSync(string connectorId, DateTime timestampUtc)
    {
        if (UseSqlServer)
        {
            SqlStateStore.WriteLastSync(connectorId, timestampUtc);
            Logger.Info($"Saved last sync timestamp: {IsoFormat(timestampUtc)}");
            return;
        }
        var data = new JsonObject();
        try
        {
            data = JsonNode.Parse(File.ReadAllText(SyncStateFile, Utf8NoBom))!.AsObject();
        }
        catch (Exception exc) when (
            exc is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            // start fresh (warn when an existing file was unreadable — other
            // connectors' cursors in it are lost by the rewrite below)
            WarnIfCorrupt(exc, SyncStateFile, "rewriting it from scratch");
        }
        data[connectorId] = IsoFormat(timestampUtc);
        Directory.CreateDirectory(LogsDir);
        File.WriteAllText(SyncStateFile, data.ToJsonString(Indented), Utf8NoBom);
        Logger.Info($"Saved last sync timestamp: {IsoFormat(timestampUtc)}");
    }

    // ── Checkpointing ────────────────────────────────────────────────────────

    private static string CheckpointPath(string connectorId) =>
        Path.Combine(LogsDir, $"checkpoint_{connectorId}.json");

    /// <summary>Checkpoint object with keys "since" and "completed" ({objectType: lastCompletedChunk}), or null.</summary>
    public static JsonObject? ReadCheckpoint(string connectorId)
    {
        if (UseSqlServer)
            return SqlStateStore.ReadCheckpoint(connectorId);
        try
        {
            var data = JsonNode.Parse(File.ReadAllText(CheckpointPath(connectorId), Utf8NoBom));
            if (data is JsonObject obj && obj.ContainsKey("completed"))
                return obj;
        }
        catch (Exception exc) when (
            exc is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            // No checkpoint file is normal; a corrupt one means the resume
            // point was lost and completed chunks will be re-sent (safe —
            // PUT is idempotent — but worth an explanation in the log).
            WarnIfCorrupt(exc, CheckpointPath(connectorId),
                "resuming without a checkpoint (completed chunks will be re-ingested)");
        }
        return null;
    }

    /// <summary>
    /// Mark <paramref name="objectType"/> chunk <paramref name="chunkIndex"/> completed.
    /// The stored "since" pins the incremental boundary of the original run; a
    /// checkpoint written with a different since starts a fresh completed map.
    /// Chunk indexes only ever advance (Math.Max).
    /// </summary>
    public static void WriteCheckpoint(string connectorId, string? sinceIso, string objectType, int chunkIndex)
    {
        if (UseSqlServer)
        {
            SqlStateStore.WriteCheckpoint(connectorId, sinceIso, objectType, chunkIndex);
            return;
        }
        lock (CheckpointLock)
        {
            var data = new JsonObject
            {
                ["since"] = sinceIso,
                ["completed"] = new JsonObject(),
            };
            try
            {
                if (JsonNode.Parse(File.ReadAllText(CheckpointPath(connectorId), Utf8NoBom))
                    is JsonObject existing)
                {
                    var existingSince =
                        existing.TryGetPropertyValue("since", out var sinceNode)
                        && sinceNode is not null
                        && sinceNode.GetValueKind() == JsonValueKind.String
                            ? sinceNode.GetValue<string>()
                            : null;
                    if (existingSince == sinceIso && existing.ContainsKey("completed"))
                        data = existing;
                }
            }
            catch (Exception exc) when (
                exc is FileNotFoundException or DirectoryNotFoundException or JsonException)
            {
                // start fresh (warn when an existing checkpoint was unreadable)
                WarnIfCorrupt(exc, CheckpointPath(connectorId), "starting a fresh completed-chunk map");
            }
            var completed = data["completed"]!.AsObject();
            var current = completed.TryGetPropertyValue(objectType, out var node) && node is not null
                ? node.GetValue<int>()
                : 0;
            completed[objectType] = Math.Max(current, chunkIndex);
            Directory.CreateDirectory(LogsDir);
            File.WriteAllText(CheckpointPath(connectorId), data.ToJsonString(Indented), Utf8NoBom);
        }
    }

    public static void ClearCheckpoint(string connectorId)
    {
        if (UseSqlServer)
        {
            SqlStateStore.ClearCheckpoint(connectorId);
            return;
        }
        try
        {
            File.Delete(CheckpointPath(connectorId));
        }
        catch (Exception exc) when (exc is FileNotFoundException or DirectoryNotFoundException)
        {
            // already gone
        }
    }

    // ── Dead-letter queue ────────────────────────────────────────────────────

    public static string FailedRecordsPath(string connectorId)
    {
        Directory.CreateDirectory(LogsDir);
        return Path.Combine(LogsDir, $"failed_records_{connectorId}.jsonl");
    }

    /// <summary>Append failed items (id + error) to the dead-letter JSONL file.</summary>
    public static void AppendFailedRecords(
        string connectorId,
        IReadOnlyList<(string ItemId, string Error)> failures,
        string objectType,
        Dictionary<string, JsonNode?>? requestBodies = null,
        Dictionary<string, JsonNode?>? responseBodies = null)
    {
        if (failures.Count == 0)
            return;
        // DEADLETTER_PAYLOAD_MODE=redacted: strip payloads at this choke point
        // so the file AND SQL backends — and every dead-letter path, including
        // the financial-classification ones — are covered identically. The
        // response body is dropped entirely (Graph validation errors can echo
        // request values); retry-failed re-fetches from source, so redaction
        // never reduces retryability.
        if (Infrastructure.DeadLetterRedactor.RedactionEnabled)
        {
            requestBodies = Infrastructure.DeadLetterRedactor.RedactRequestBodies(requestBodies);
            responseBodies = null;
        }
        if (UseSqlServer)
        {
            SqlStateStore.AppendDeadLetter(connectorId, failures, objectType, requestBodies, responseBodies);
            return;
        }
        requestBodies ??= new Dictionary<string, JsonNode?>();
        responseBodies ??= new Dictionary<string, JsonNode?>();
        var timestamp = IsoFormat(DateTime.UtcNow);
        var path = FailedRecordsPath(connectorId);
        try
        {
            lock (DeadLetterLock)
            {
                using var writer = new StreamWriter(path, append: true, Utf8NoBom);
                var correlationId = Infrastructure.CorrelationContext.Current;
                foreach (var (itemId, error) in failures)
                {
                    var record = new JsonObject
                    {
                        ["item_id"] = itemId,
                        ["object_type"] = objectType,
                        ["error"] = error,
                        ["timestamp"] = timestamp,
                    };
                    // Tie each failure to the crawl cycle that produced it.
                    if (correlationId is not null)
                        record["correlation_id"] = correlationId;
                    if (requestBodies.TryGetValue(itemId, out var request))
                        record["request_body"] = request?.DeepClone();
                    if (responseBodies.TryGetValue(itemId, out var response))
                        record["response_body"] = response?.DeepClone();
                    writer.WriteLine(record.ToJsonString(Compact));
                }
            }
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            Logger.Error(
                $"Failed to write {failures.Count} failed record(s) to dead-letter file {path}: {exc.Message}");
            foreach (var (itemId, error) in failures)
                Logger.Error($"  UNRECORDED FAILURE — object={objectType} id={itemId} error={error}");
        }
    }

    /// <summary>All entries from the dead-letter queue for <paramref name="connectorId"/>.</summary>
    public static List<JsonObject> ReadFailedRecords(string connectorId)
    {
        if (UseSqlServer)
            return SqlStateStore.ReadDeadLetter(connectorId);
        var entries = new List<JsonObject>();
        var path = FailedRecordsPath(connectorId);
        try
        {
            var lineNumber = 0;
            foreach (var rawLine in File.ReadLines(path, Utf8NoBom))
            {
                lineNumber++;
                var line = rawLine.Trim();
                if (line.Length == 0)
                    continue;
                // Per-line isolation: a torn/corrupt line (typically a crash
                // mid-append) must not make the WHOLE queue unreadable — that
                // would break retry-failed and the dead-letter depth gauge.
                // Log the offending line verbatim so no failure detail is lost,
                // then keep reading the parseable entries.
                try
                {
                    if (JsonNode.Parse(line) is JsonObject entry)
                    {
                        entries.Add(entry);
                    }
                    else
                    {
                        Logger.Error(
                            $"Dead-letter file '{path}' line {lineNumber} is valid JSON but not "
                            + $"an object — skipping it. Raw line: {line}");
                    }
                }
                catch (JsonException exc)
                {
                    Logger.Error(
                        $"Dead-letter file '{path}' line {lineNumber} is not valid JSON "
                        + $"({exc.Message}) — skipping it. Raw line: {line}");
                }
            }
        }
        catch (Exception exc) when (exc is FileNotFoundException or DirectoryNotFoundException)
        {
            // empty queue
        }
        return entries;
    }

    public static void ClearFailedRecords(string connectorId)
    {
        if (UseSqlServer)
        {
            SqlStateStore.ClearDeadLetter(connectorId);
            return;
        }
        try
        {
            File.Delete(FailedRecordsPath(connectorId));
        }
        catch (Exception exc) when (exc is FileNotFoundException or DirectoryNotFoundException)
        {
            // already gone
        }
    }

    // ── Serialization helpers ────────────────────────────────────────────────

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>ISO-8601 UTC with offset, e.g. 2026-07-12T09:30:00.1234567+00:00.</summary>
    internal static string IsoFormat(DateTime timestampUtc) =>
        new DateTimeOffset(DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc))
            .ToString("o", CultureInfo.InvariantCulture)
            .Replace("Z", "+00:00");

    /// <summary>Parse an ISO-8601 string back to a UTC DateTime; FormatException on invalid input.</summary>
    internal static DateTime ParseIso(string value)
    {
        if (!DateTimeOffset.TryParse(
                value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            throw new FormatException($"Invalid ISO-8601 timestamp: '{value}'");
        }
        return parsed.UtcDateTime;
    }
}
