// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Config/SyncState.cs
// -------------------
// Persistent sync state for the ingestion pipeline.
//
// Manages three concerns:
//
// 1. **Delta sync timestamp** — records the last successful sync completion
//    time per connector so subsequent runs fetch only changed records.
// 2. **Checkpointing** — tracks completed chunks within an in-progress run so
//    the process can resume after a crash without re-processing everything.
// 3. **Dead-letter queue** — persists failed item IDs to a JSONL file for
//    inspection and retry in a subsequent pass.
//
// All state files live in the ``logs/`` directory alongside the run logs.
// File formats (JSON / JSONL) are byte-compatible with the Python version:
// ``json.dumps`` separators, ``indent=2``, and ``ensure_ascii`` escaping are
// reproduced exactly.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Config;

public static class SyncState
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector");

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Read a state file with a share mode that tolerates a concurrent writer.
    /// </summary>
    /// <remarks>
    /// <see cref="File.ReadAllText(string, System.Text.Encoding)"/> opens with
    /// <see cref="FileShare.Read"/>, which does not permit another handle to hold
    /// write access. Windows enforces share modes and POSIX does not, so on
    /// Windows a read that overlaps another node's checkpoint write throws
    /// <see cref="IOException"/> and takes the crawl down with it — while the
    /// same code is silent on the macOS and Linux machines it is developed on.
    ///
    /// <see cref="FileShare.Delete"/> is part of the contract, not incidental:
    /// <see cref="WriteAllTextAtomic"/> publishes by renaming over this path, and
    /// Windows refuses to replace a file that an open handle has not shared
    /// delete access to.
    /// </remarks>
    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Utf8NoBom);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Write a state file so a concurrent reader sees either the whole previous
    /// version or the whole new one, never a partial write.
    /// </summary>
    /// <remarks>
    /// <see cref="File.WriteAllText(string, string, System.Text.Encoding)"/>
    /// truncates in place, so a reader that opens mid-write gets a torn file. For
    /// a checkpoint that is worse than the sharing violation it replaces: torn
    /// JSON is caught as <see cref="System.Text.Json.JsonException"/> and read as
    /// "no checkpoint", so instead of failing loudly the crawl silently restarts
    /// from the beginning and re-ingests everything. Staging to a sibling file and
    /// renaming over the target makes the swap atomic.
    ///
    /// The temp name carries a GUID because two nodes may publish at once; a
    /// shared staging name would let them corrupt each other, which is the very
    /// failure this is here to prevent.
    /// </remarks>
    private static void WriteAllTextAtomic(string path, string contents)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, contents, Utf8NoBom);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort: never mask the real error */ }
            throw;
        }
    }

    /// <summary>State directory (Python: ``LOGS_DIR``). Settable so tests can redirect state files.</summary>
    public static string LogsDir { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "logs");

    // ── SQL Server provider routing (docs/SQL_CONTRACT.md) ───────────────────
    // When USE_SQL_SERVER=true and SQL_CONNECTION_STRING is set, every public
    // method below routes to SqlStateStore (stored procedures); otherwise the
    // original file implementation runs unchanged. The env check is cached but
    // resettable so tests can flip providers.

    private static bool? _useSqlServer;

    /// <summary>True when the SQL Server state backend is active.</summary>
    internal static bool UseSqlServer
    {
        get
        {
            _useSqlServer ??=
                string.Equals(
                    Environment.GetEnvironmentVariable("USE_SQL_SERVER"),
                    "true",
                    StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING"));
            return _useSqlServer.Value;
        }
    }

    /// <summary>Test seam: re-read USE_SQL_SERVER / SQL_CONNECTION_STRING on next use.</summary>
    internal static void ResetProviderCache() => _useSqlServer = null;

    /// <summary>Recover the connector id from a ``failed_records_&lt;id&gt;.jsonl`` path.</summary>
    private static string ConnectorIdFromDeadLetterPath(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        const string prefix = "failed_records_";
        return name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
    }

    // Protects read-modify-write of checkpoint files from concurrent workers
    private static readonly object CheckpointLock = new();
    // Serializes dead-letter appends. Up to GraphConcurrentBatches × workers
    // tasks append to the same failed_records_<connector>.jsonl; unsynchronized
    // StreamWriter appends interleave partial lines (CPython's open(path, "a")
    // got O_APPEND atomicity per flush; .NET does not).
    private static readonly object DeadLetterLock = new();

    // ── Delta sync timestamp ─────────────────────────────────────────────────

    private static string SyncStateFile => Path.Combine(LogsDir, "sync_state.json");

    /// <summary>Return the last successful sync timestamp for <paramref name="connectorId"/>, or <c>null</c>.</summary>
    public static DateTime? ReadLastSync(string connectorId)
    {
        if (UseSqlServer)
        {
            return SqlStateStore.ReadLastSync(connectorId);
        }
        try
        {
            var data = JsonNode.Parse(ReadAllTextShared(SyncStateFile))!.AsObject();
            var ts = data.TryGetPropertyValue(connectorId, out var tsNode)
                && tsNode is not null
                && tsNode.GetValueKind() == JsonValueKind.String
                    ? tsNode.GetValue<string>()
                    : null;
            if (!string.IsNullOrEmpty(ts))
            {
                return ParseIsoFormat(ts);
            }
        }
        catch (Exception exc) when (
            exc is FileNotFoundException or DirectoryNotFoundException or JsonException or FormatException)
        {
            // Python: except (FileNotFoundError, json.JSONDecodeError, ValueError): pass
        }
        return null;
    }

    /// <summary>Persist <paramref name="timestamp"/> as the last successful sync time for <paramref name="connectorId"/>.</summary>
    public static void WriteLastSync(string connectorId, DateTime timestamp)
    {
        if (UseSqlServer)
        {
            SqlStateStore.WriteLastSync(connectorId, timestamp);
            Logger.Info($"Saved last sync timestamp: {IsoFormat(timestamp)}");
            return;
        }
        var data = new JsonObject();
        try
        {
            data = JsonNode.Parse(ReadAllTextShared(SyncStateFile))!.AsObject();
        }
        catch (Exception exc) when (
            exc is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            // Python: except (FileNotFoundError, json.JSONDecodeError): pass
        }
        data[connectorId] = IsoFormat(timestamp);
        SecureDirectory.EnsureOwnerOnly(LogsDir);
        WriteAllTextAtomic(SyncStateFile, PyJson.Dumps(data, indent: 2));
        Logger.Info($"Saved last sync timestamp: {IsoFormat(timestamp)}");
    }

    // ── Checkpointing ────────────────────────────────────────────────────────

    private static string CheckpointPath(string connectorId)
    {
        return Path.Combine(LogsDir, $"checkpoint_{connectorId}.json");
    }

    /// <summary>
    /// Read the checkpoint for <paramref name="connectorId"/>.
    ///
    /// Returns a dict with keys ``"since"`` and ``"completed"``
    /// (``{object_type: last_completed_chunk_index}``), or <c>null</c> if no
    /// checkpoint exists.
    /// </summary>
    public static JsonObject? ReadCheckpoint(string connectorId)
    {
        if (UseSqlServer)
        {
            return SqlStateStore.ReadCheckpoint(connectorId);
        }
        var path = CheckpointPath(connectorId);
        try
        {
            var data = JsonNode.Parse(ReadAllTextShared(path));
            if (data is JsonObject dataObject && dataObject.ContainsKey("completed"))
            {
                return dataObject;
            }
        }
        catch (Exception exc) when (
            exc is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            // Python: except (FileNotFoundError, json.JSONDecodeError): pass
        }
        return null;
    }

    /// <summary>
    /// Mark <paramref name="objectType"/> chunk <paramref name="chunkIndex"/> as completed.
    ///
    /// The checkpoint stores the ``since`` ISO string so that a resume uses the
    /// same incremental boundary as the original run.
    /// </summary>
    public static void WriteCheckpoint(
        string connectorId,
        string? sinceIso,
        string objectType,
        int chunkIndex)
    {
        if (UseSqlServer)
        {
            SqlStateStore.WriteCheckpoint(connectorId, sinceIso, objectType, chunkIndex);
            return;
        }
        var path = CheckpointPath(connectorId);
        lock (CheckpointLock)
        {
            var data = new JsonObject
            {
                ["since"] = sinceIso,
                ["completed"] = new JsonObject(),
            };
            try
            {
                var existing = JsonNode.Parse(ReadAllTextShared(path));
                if (existing is JsonObject existingObject)
                {
                    var existingSince = existingObject.TryGetPropertyValue("since", out var sinceNode)
                        && sinceNode is not null
                        && sinceNode.GetValueKind() == JsonValueKind.String
                            ? sinceNode.GetValue<string>()
                            : null;
                    if (existingSince == sinceIso)
                    {
                        data = existingObject;
                    }
                }
            }
            catch (Exception exc) when (
                exc is FileNotFoundException or DirectoryNotFoundException or JsonException)
            {
                // Python: except (FileNotFoundError, json.JSONDecodeError): pass
            }
            var completed = data["completed"]!.AsObject();
            var current = completed.TryGetPropertyValue(objectType, out var currentNode) && currentNode is not null
                ? currentNode.GetValue<int>()
                : 0;
            completed[objectType] = Math.Max(current, chunkIndex);
            SecureDirectory.EnsureOwnerOnly(LogsDir);
            WriteAllTextAtomic(path, PyJson.Dumps(data, indent: 2));
        }
    }

    /// <summary>Remove the checkpoint file for <paramref name="connectorId"/>.</summary>
    public static void ClearCheckpoint(string connectorId)
    {
        if (UseSqlServer)
        {
            SqlStateStore.ClearCheckpoint(connectorId);
            return;
        }
        var path = CheckpointPath(connectorId);
        try
        {
            File.Delete(path);
        }
        catch (Exception exc) when (exc is FileNotFoundException or DirectoryNotFoundException)
        {
            // Python: except FileNotFoundError: pass
        }
    }

    // ── Dead-letter queue ────────────────────────────────────────────────────

    /// <summary>Return the path to the dead-letter JSONL file for <paramref name="connectorId"/>.</summary>
    public static string FailedRecordsPath(string connectorId)
    {
        SecureDirectory.EnsureOwnerOnly(LogsDir);
        return Path.Combine(LogsDir, $"failed_records_{connectorId}.jsonl");
    }

    /// <summary>
    /// Append failed items to the dead-letter JSONL file.
    ///
    /// <paramref name="failures"/> is a list of ``(item_id, error_detail)``
    /// tuples (per-item errors).
    ///
    /// <paramref name="requestBodies"/> : optional ``{item_id: request_payload}`` dict.
    /// <paramref name="responseBodies"/> : optional ``{item_id: response_payload}`` dict.
    ///
    /// When provided, the full request and response are included in the JSONL
    /// entry for debugging failed Graph API calls.
    /// </summary>
    public static void AppendFailedRecords(
        string filePath,
        IReadOnlyList<(string ItemId, string Error)> failures,
        string objectType,
        string error = "",
        Dictionary<string, JsonNode?>? requestBodies = null,
        Dictionary<string, JsonNode?>? responseBodies = null)
    {
        if (failures.Count == 0)
        {
            return;
        }
        // DEADLETTER_PAYLOAD_MODE=redacted: strip item property values/content
        // before the record is persisted (either backend). Default full.
        if (DeadLetterRedaction.RedactedMode)
        {
            requestBodies = DeadLetterRedaction.RedactBodies(requestBodies);
            responseBodies = DeadLetterRedaction.RedactBodies(responseBodies);
        }
        if (UseSqlServer)
        {
            SqlStateStore.AppendDeadLetter(
                ConnectorIdFromDeadLetterPath(filePath),
                failures,
                objectType,
                requestBodies,
                responseBodies);
            return;
        }
        requestBodies ??= new Dictionary<string, JsonNode?>();
        responseBodies ??= new Dictionary<string, JsonNode?>();
        var timestamp = IsoFormat(DateTime.UtcNow);
        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (!string.IsNullOrEmpty(parent))
            {
                SecureDirectory.EnsureOwnerOnly(parent);
            }
            lock (DeadLetterLock)
            {
                EnsureCleanAppendBoundary(filePath);
                // FileShare.ReadWrite, explicitly: on Windows the share mode is
                // enforced, and StreamWriter(path, append:) opens the file as
                // FileShare.Read. A concurrent reader (end-of-run dead-letter
                // accounting, retry-failed) opens for read with its own share mode,
                // which does not permit this handle's Write access — so the reader
                // fails with a sharing violation while a crawl is appending. POSIX
                // does not enforce share modes, which is why this only bites on
                // Windows Server, the deployment target.
                using var deadLetterStream = new FileStream(
                    filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var fh = new StreamWriter(deadLetterStream, Utf8NoBom);
                foreach (var (itemId, itemError) in failures)
                {
                    var record = new JsonObject
                    {
                        ["item_id"] = itemId,
                        ["object_type"] = objectType,
                        ["error"] = itemError,
                        ["timestamp"] = timestamp,
                    };
                    if (requestBodies.TryGetValue(itemId, out var requestBody))
                    {
                        record["request_body"] = requestBody?.DeepClone();
                    }
                    if (responseBodies.TryGetValue(itemId, out var responseBody))
                    {
                        record["response_body"] = responseBody?.DeepClone();
                    }
                    var line = PyJson.Dumps(record, indent: null);
                    fh.Write(line + "\n");
                }
            }
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            Logger.Error(
                $"Failed to write {failures.Count} failed record(s) to dead-letter file {filePath}: {exc.Message}",
                exc);
            foreach (var (itemId, itemError) in failures)
            {
                Logger.Error(
                    $"  UNRECORDED FAILURE — object={objectType} id={itemId} error={itemError}");
            }
        }
    }

    /// <summary>
    /// Seal a torn dead-letter tail before the first append of a batch.
    ///
    /// A prior crash can leave the JSONL file with an unterminated final line —
    /// the record bytes flushed but the process died before the trailing
    /// <c>'\n'</c>. Appending straight away would glue the next record onto that
    /// fragment, producing one line that no longer parses; <see cref="ReadFailedRecords"/>
    /// then chokes on it and the freshly appended failure is lost from the retry
    /// safety net. If the file exists, is non-empty, and its last byte is not
    /// <c>'\n'</c>, write a single newline so the new record starts on its own
    /// line. A normal newline-terminated file is a no-op, so the seal is cheap and
    /// idempotent. Best-effort: any I/O problem is logged and swallowed so the
    /// append (itself a failure path) still proceeds and never throws here. This
    /// mirrors the append-boundary discipline the DecisionLedger uses to keep its
    /// JSONL chain parseable. Callers hold <see cref="DeadLetterLock"/>.
    /// </summary>
    private static void EnsureCleanAppendBoundary(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }
            using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (stream.Length == 0)
            {
                return;
            }
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n')
            {
                stream.WriteByte((byte)'\n');
            }
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(
                $"Could not seal dead-letter append boundary for {filePath} " +
                $"({exc.GetType().Name}); appending without it.");
        }
    }

    /// <summary>
    /// Append failed items to the dead-letter JSONL file.
    ///
    /// <paramref name="failures"/> is a plain list of item-ID strings (all
    /// share the same <paramref name="error"/>).
    /// </summary>
    public static void AppendFailedRecords(
        string filePath,
        IReadOnlyList<string> failures,
        string objectType,
        string error = "",
        Dictionary<string, JsonNode?>? requestBodies = null,
        Dictionary<string, JsonNode?>? responseBodies = null)
    {
        AppendFailedRecords(
            filePath,
            failures.Select(itemId => (itemId, error)).ToList(),
            objectType,
            error,
            requestBodies,
            responseBodies);
    }

    /// <summary>Read all entries from the dead-letter file for <paramref name="connectorId"/>.</summary>
    public static List<JsonObject> ReadFailedRecords(string connectorId)
    {
        if (UseSqlServer)
        {
            return SqlStateStore.ReadDeadLetter(connectorId);
        }
        var path = FailedRecordsPath(connectorId);
        var entries = new List<JsonObject>();
        var lineNumber = 0;
        try
        {
            foreach (var rawLine in ReadLinesShared(path))
            {
                lineNumber++;
                var line = rawLine.Trim();
                if (line.Length > 0)
                {
                    entries.Add(JsonNode.Parse(line)!.AsObject());
                }
            }
        }
        catch (Exception exc) when (exc is FileNotFoundException or DirectoryNotFoundException)
        {
            // Python: except FileNotFoundError: pass
        }
        catch (Exception exc) when (exc is JsonException or InvalidOperationException)
        {
            // Catch-log-rethrow only (InvalidOperationException = line parsed but
            // is not a JSON object): without this the caller (e.g. retry-failed)
            // reports a bare JSON parse error with no hint of WHICH file or line
            // is corrupt. Behavior is unchanged — the exception still propagates.
            Logger.Error(
                $"Dead-letter file {path} contains a corrupt entry at line {lineNumber}: {exc.Message}");
            throw;
        }
        return entries;
    }


    /// <summary>
    /// Enumerate the lines of <paramref name="path"/> tolerating a concurrent
    /// appender.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="File.ReadLines(string, System.Text.Encoding)"/>,
    /// which opens the file as <see cref="FileShare.Read"/>. On Windows the share
    /// mode is enforced: it does not permit the Write access an appending writer
    /// holds, so reading the dead-letter queue while a crawl appends to it throws a
    /// sharing violation. POSIX does not enforce share modes, so the defect is
    /// invisible off Windows — while Windows Server is the primary deployment
    /// target.
    ///
    /// A record still being written is simply not yet newline-terminated, so it is
    /// either absent or skipped as malformed by the caller and picked up on the next
    /// read — the queue is append-only, so no committed record is missed.
    /// </remarks>
    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Utf8NoBom);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    /// <summary>Remove the dead-letter file for <paramref name="connectorId"/>.</summary>
    public static void ClearFailedRecords(string connectorId)
    {
        if (UseSqlServer)
        {
            SqlStateStore.ClearDeadLetter(connectorId);
            return;
        }
        var path = FailedRecordsPath(connectorId);
        try
        {
            File.Delete(path);
        }
        catch (Exception exc) when (exc is FileNotFoundException or DirectoryNotFoundException)
        {
            // Python: except FileNotFoundError: pass
        }
    }

    // ── Python datetime interop ──────────────────────────────────────────────

    /// <summary>
    /// Format a timestamp exactly like Python ``datetime.isoformat()``:
    /// microseconds only when non-zero, ``+00:00`` offset for UTC-aware values,
    /// no offset for naive (<see cref="DateTimeKind.Unspecified"/>) values.
    /// </summary>
    internal static string IsoFormat(DateTime timestamp)
    {
        var microseconds = timestamp.Ticks % TimeSpan.TicksPerSecond / 10;
        var s = timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        if (microseconds != 0)
        {
            s += "." + microseconds.ToString("D6", CultureInfo.InvariantCulture);
        }
        if (timestamp.Kind == DateTimeKind.Utc)
        {
            s += "+00:00";
        }
        else if (timestamp.Kind == DateTimeKind.Local)
        {
            var offset = TimeZoneInfo.Local.GetUtcOffset(timestamp);
            s += (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString("hh\\:mm");
        }
        return s;
    }

    /// <summary>
    /// Parse an ISO string like Python ``datetime.fromisoformat``: values with a
    /// UTC offset come back as <see cref="DateTimeKind.Utc"/>, naive values as
    /// <see cref="DateTimeKind.Unspecified"/>.  Throws <see cref="FormatException"/>
    /// on invalid input (Python: ``ValueError``).
    /// </summary>
    internal static DateTime ParseIsoFormat(string value)
    {
        var hasOffset = value.EndsWith("Z", StringComparison.Ordinal)
            || Regex.IsMatch(value, @"[+-]\d{2}:?\d{2}$");
        var candidate = value.Replace("Z", "+00:00");
        candidate = Regex.Replace(candidate, @"([+-])(\d{2})(\d{2})$", "$1$2:$3");
        if (!DateTimeOffset.TryParse(
                candidate,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            throw new FormatException($"Invalid isoformat string: '{value}'");
        }
        return hasOffset
            ? parsed.UtcDateTime
            : DateTime.SpecifyKind(parsed.DateTime, DateTimeKind.Unspecified);
    }
}

/// <summary>
/// Serialises <see cref="JsonNode"/> trees byte-compatibly with Python
/// ``json.dumps``: item separator ``", "`` / key separator ``": "`` (or
/// ``indent=N`` block style), ``ensure_ascii`` escaping of non-ASCII
/// characters, and Python float ``repr`` for numbers.
/// </summary>
internal static class PyJson
{
    public static string Dumps(JsonNode? node, int? indent = null)
    {
        var sb = new StringBuilder();
        WriteNode(node, sb, indent, 0);
        return sb.ToString();
    }

    private static void WriteNode(JsonNode? node, StringBuilder sb, int? indent, int level)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                return;
            case JsonObject obj:
                WriteObject(obj, sb, indent, level);
                return;
            case JsonArray arr:
                WriteArray(arr, sb, indent, level);
                return;
            case JsonValue value:
                WriteValue(value, sb);
                return;
        }
    }

    private static void WriteObject(JsonObject obj, StringBuilder sb, int? indent, int level)
    {
        if (obj.Count == 0)
        {
            sb.Append("{}");
            return;
        }
        sb.Append('{');
        var first = true;
        foreach (var pair in obj)
        {
            if (!first)
            {
                sb.Append(indent is null ? ", " : ",");
            }
            first = false;
            if (indent is not null)
            {
                sb.Append('\n').Append(' ', indent.Value * (level + 1));
            }
            WriteString(pair.Key, sb);
            sb.Append(": ");
            WriteNode(pair.Value, sb, indent, level + 1);
        }
        if (indent is not null)
        {
            sb.Append('\n').Append(' ', indent.Value * level);
        }
        sb.Append('}');
    }

    private static void WriteArray(JsonArray arr, StringBuilder sb, int? indent, int level)
    {
        if (arr.Count == 0)
        {
            sb.Append("[]");
            return;
        }
        sb.Append('[');
        var first = true;
        foreach (var element in arr)
        {
            if (!first)
            {
                sb.Append(indent is null ? ", " : ",");
            }
            first = false;
            if (indent is not null)
            {
                sb.Append('\n').Append(' ', indent.Value * (level + 1));
            }
            WriteNode(element, sb, indent, level + 1);
        }
        if (indent is not null)
        {
            sb.Append('\n').Append(' ', indent.Value * level);
        }
        sb.Append(']');
    }

    private static void WriteValue(JsonValue value, StringBuilder sb)
    {
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                WriteString(value.GetValue<string>(), sb);
                return;
            case JsonValueKind.True:
                sb.Append("true");
                return;
            case JsonValueKind.False:
                sb.Append("false");
                return;
            case JsonValueKind.Null:
                sb.Append("null");
                return;
            case JsonValueKind.Number:
                WriteNumber(value, sb);
                return;
            default:
                sb.Append(value.ToJsonString());
                return;
        }
    }

    private static void WriteNumber(JsonValue value, StringBuilder sb)
    {
        if (value.TryGetValue<long>(out var l))
        {
            sb.Append(l.ToString(CultureInfo.InvariantCulture));
            return;
        }
        if (value.TryGetValue<int>(out var i))
        {
            sb.Append(i.ToString(CultureInfo.InvariantCulture));
            return;
        }
        if (value.TryGetValue<ulong>(out var ul))
        {
            sb.Append(ul.ToString(CultureInfo.InvariantCulture));
            return;
        }
        if (value.TryGetValue<double>(out var d))
        {
            sb.Append(FloatRepr(d));
            return;
        }
        if (value.TryGetValue<float>(out var f))
        {
            sb.Append(FloatRepr(f));
            return;
        }
        if (value.TryGetValue<decimal>(out var m))
        {
            sb.Append(m.ToString(CultureInfo.InvariantCulture));
            return;
        }
        sb.Append(value.ToJsonString());
    }

    /// <summary>Python ``repr(float)`` as used by ``json.dumps`` (Infinity / -Infinity / NaN).</summary>
    private static string FloatRepr(double d)
    {
        if (double.IsNaN(d))
        {
            return "NaN";
        }
        if (double.IsPositiveInfinity(d))
        {
            return "Infinity";
        }
        if (double.IsNegativeInfinity(d))
        {
            return "-Infinity";
        }
        var s = d.ToString(CultureInfo.InvariantCulture);
        if (!s.Contains('.') && !s.Contains('e') && !s.Contains('E'))
        {
            s += ".0";
        }
        return s.Replace("E", "e");
    }

    /// <summary>Python ``json.dumps`` string escaping with ``ensure_ascii=True``.</summary>
    private static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                default:
                    if (ch < 0x20 || ch > 0x7e)
                    {
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
        sb.Append('"');
    }
}
