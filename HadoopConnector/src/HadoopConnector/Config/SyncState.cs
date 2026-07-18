// Config/SyncState.cs
// -------------------
// Persistent sync state for the ingestion pipeline. Three concerns:
//
//  1. Delta sync timestamp — last successful sync completion time per
//     connector, so subsequent incremental runs fetch only changed records
//     (only dt partitions newer than since − BDH_LAG_HOURS are read).
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
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Config;

public static class SyncState
{
    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector");
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
        catch (Exception exc) when (exc is FileNotFoundException or DirectoryNotFoundException)
        {
            // no state yet — a first run is normal and silent
        }
        catch (Exception exc) when (exc is JsonException or FormatException)
        {
            // The file EXISTS but cannot be read — that is corruption, not a
            // first run. The fallback (null → no watermark) is preserved, but
            // it silently widens the next incremental to a much larger read, so
            // the operator must be able to see why from the logs.
            Logger.Warning(
                $"Sync-state file '{SyncStateFile}' is corrupt ({exc.GetType().Name}: {exc.Message}); "
                + $"treating connector '{connectorId}' as never-synced — the next incremental crawl "
                + "reads without a watermark.");
        }
        return null;
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
        catch (Exception exc) when (exc is FileNotFoundException or DirectoryNotFoundException)
        {
            // start fresh — no file yet is normal and silent
        }
        catch (JsonException exc)
        {
            // Corrupt existing file: it is about to be REPLACED, which discards
            // every other connector's timestamp stored in it — warn so the loss
            // is attributable from the logs.
            Logger.Warning(
                $"Sync-state file '{SyncStateFile}' is corrupt ({exc.Message}); rewriting it — "
                + "timestamps previously stored for other connector ids are lost.");
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
        catch (Exception exc) when (exc is FileNotFoundException or DirectoryNotFoundException)
        {
            // no checkpoint — nothing to resume is normal and silent
        }
        catch (JsonException exc)
        {
            // A corrupt checkpoint is SAFE to ignore (PUTs are idempotent, the
            // run just re-processes from chunk 0) but never silent: the operator
            // should know why a resume re-did work.
            Logger.Warning(
                $"Checkpoint file '{CheckpointPath(connectorId)}' is corrupt ({exc.Message}); "
                + "ignoring it — the crawl restarts from chunk 0 (idempotent re-processing).");
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
            catch (Exception exc) when (exc is FileNotFoundException or DirectoryNotFoundException)
            {
                // start fresh — first checkpoint of a run is normal and silent
            }
            catch (JsonException exc)
            {
                Logger.Warning(
                    $"Checkpoint file '{CheckpointPath(connectorId)}' is corrupt ({exc.Message}); "
                    + "starting a fresh completed-chunk map.");
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
        // DEADLETTER_PAYLOAD_MODE=redacted strips record values BEFORE dispatch,
        // so neither backend (JSONL file / dbo.DeadLetter) ever stores them.
        requestBodies = DeadLetterRedaction.Apply(requestBodies, DeadLetterRedaction.RedactRequestBody);
        responseBodies = DeadLetterRedaction.Apply(responseBodies, DeadLetterRedaction.RedactResponseBody);
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
                // Seal a torn/unterminated final line (a crash left a partial
                // record with no trailing newline) before the first append of this
                // call, so the new record lands on its own line instead of being
                // glued onto the fragment — a glued line is unparseable JSON, and
                // ReadFailedRecords would then silently skip it, LOSING a failure
                // from the retry safety net. Mirrors DecisionLedger's
                // EnsureCleanAppendBoundary. Best-effort and idempotent: a normal
                // newline-terminated file is a no-op.
                SealDeadLetterBoundary(path);
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
                try
                {
                    entries.Add(JsonNode.Parse(line)!.AsObject());
                }
                catch (Exception exc) when (exc is JsonException or InvalidOperationException)
                {
                    // Per-LINE isolation: a torn line (process killed mid-append)
                    // must corrupt ONE queue entry, not crash every reader of the
                    // queue — the crawl-end depth gauge, /metrics and retry-failed
                    // all read this file. The bad line is named so the operator
                    // can inspect/repair it; all intact entries still load.
                    Logger.Warning(
                        $"Dead-letter file '{path}' line {lineNumber} is not valid JSON "
                        + $"({exc.Message}); skipping that entry — inspect the file to recover it.");
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

    /// <summary>Seal a dead-letter file whose final line does not end with a
    /// newline (a crash left a torn/unterminated partial record), so the next
    /// append starts on a clean line instead of being glued onto the fragment.
    /// Best-effort — a sealing failure must not fail the append (the write below
    /// falls back to append-as-is); idempotent — a newline-terminated (or
    /// missing/empty) file is a no-op. Callers hold DeadLetterLock.</summary>
    private static void SealDeadLetterBoundary(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0)
                return;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
            fs.Seek(-1, SeekOrigin.End);
            if (fs.ReadByte() != '\n')
                fs.WriteByte((byte)'\n');
        }
        catch (Exception exc)
        {
            Logger.Warning(
                $"Dead-letter file '{path}' append-boundary check failed "
                + $"({exc.GetType().Name}: {exc.Message}); appending as-is.");
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
