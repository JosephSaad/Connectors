// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Config/SqlStateStore.cs
// -----------------------
// SQL Server-backed implementation of the SyncState surface, per
// docs/SQL_CONTRACT.md (state stored procedures). Activated when
// USE_SQL_SERVER=true and SQL_CONNECTION_STRING is set — SyncState routes
// each public method here in that case so the rest of the pipeline is
// unchanged.
//
// All access goes through the contract's stored procedures via
// Microsoft.Data.SqlClient (no inline SQL):
//
//   usp_ReadLastSync / usp_WriteLastSync
//   usp_ReadCheckpoints / usp_WriteCheckpoint / usp_ClearCheckpoints
//   usp_AppendDeadLetter / usp_ReadDeadLetter /
//   usp_MarkDeadLetterRetried / usp_ClearDeadLetter
//
// Shapes returned to callers are identical to the file implementation:
// ReadCheckpoint reassembles ``{"since": ..., "completed": {obj: chunk}}``
// from Checkpoints rows and ReadDeadLetter reassembles the JSONL record
// shape (``item_id``/``object_type``/``error``/``timestamp`` +
// ``request_body``/``response_body`` when captured).

using System.Data;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Config;

internal static class SqlStateStore
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector");

    // Mirrors SyncState.CheckpointLock: serialises the read-modify-write of the
    // per-object chunk index within this process (cross-process atomicity is
    // provided by the MERGE inside usp_WriteCheckpoint).
    private static readonly object CheckpointLock = new();

    /// <summary>Connection string (contract: point at the AG listener for HA).</summary>
    internal static string ConnectionString =>
        Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING")
        ?? throw new InvalidOperationException(
            "SQL_CONNECTION_STRING must be set when USE_SQL_SERVER=true");

    private static SqlConnection OpenConnection()
    {
        var connection = new SqlConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    private static SqlCommand Proc(SqlConnection connection, string name)
    {
        var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = name;
        return command;
    }

    // ── Delta sync timestamp ─────────────────────────────────────────────────

    /// <summary>SQL equivalent of the sync_state.json lookup.</summary>
    public static DateTime? ReadLastSync(string connectorId)
    {
        using var connection = OpenConnection();
        using var command = Proc(connection, "dbo.usp_ReadLastSync");
        command.Parameters.AddWithValue("@ConnectorId", connectorId);
        var result = command.ExecuteScalar();
        return result is DateTime lastSync
            ? DateTime.SpecifyKind(lastSync, DateTimeKind.Utc)
            : null;
    }

    /// <summary>SQL equivalent of the sync_state.json upsert.</summary>
    public static void WriteLastSync(string connectorId, DateTime timestamp)
    {
        var utc = timestamp.Kind == DateTimeKind.Local ? timestamp.ToUniversalTime() : timestamp;
        using var connection = OpenConnection();
        using var command = Proc(connection, "dbo.usp_WriteLastSync");
        command.Parameters.AddWithValue("@ConnectorId", connectorId);
        command.Parameters.Add(new SqlParameter("@LastSyncUtc", SqlDbType.DateTime2) { Value = utc });
        command.ExecuteNonQuery();
    }

    // ── Checkpointing ────────────────────────────────────────────────────────

    /// <summary>
    /// Reassemble the checkpoint into the exact file shape:
    /// ``{"since": ..., "completed": {object_type: last_completed_chunk_index}}``,
    /// or <c>null</c> when no checkpoint rows exist.
    /// </summary>
    public static JsonObject? ReadCheckpoint(string connectorId)
    {
        using var connection = OpenConnection();
        using var command = Proc(connection, "dbo.usp_ReadCheckpoints");
        command.Parameters.AddWithValue("@ConnectorId", connectorId);
        using var reader = command.ExecuteReader();
        string? since = null;
        var haveSince = false;
        var completed = new JsonObject();
        var anyRows = false;
        while (reader.Read())
        {
            anyRows = true;
            var objectType = Convert.ToString(reader["ObjectType"])!;
            completed[objectType] = Convert.ToInt32(reader["ChunkIndex"]);
            if (!haveSince)
            {
                var sinceValue = reader["SinceIso"];
                since = sinceValue is DBNull or null ? null : Convert.ToString(sinceValue);
                haveSince = true;
            }
        }
        if (!anyRows)
            return null;
        return new JsonObject
        {
            ["since"] = since,
            ["completed"] = completed,
        };
    }

    /// <summary>
    /// Mark <paramref name="objectType"/> chunk <paramref name="chunkIndex"/> as completed.
    /// Mirrors the file semantics exactly: a different ``since`` resets the whole
    /// checkpoint, and the stored chunk index is monotonic (max of old and new).
    /// </summary>
    public static void WriteCheckpoint(string connectorId, string? sinceIso, string objectType, int chunkIndex)
    {
        lock (CheckpointLock)
        {
            var existing = ReadCheckpoint(connectorId);
            var current = 0;
            if (existing != null)
            {
                var existingSince = existing["since"]?.GetValue<string>();
                if (existingSince == sinceIso)
                {
                    current = (existing["completed"] as JsonObject)?[objectType]?.GetValue<int>() ?? 0;
                }
                else
                {
                    // Python file semantics: a since change rewrites the file from scratch.
                    ClearCheckpoint(connectorId);
                }
            }
            using var connection = OpenConnection();
            using var command = Proc(connection, "dbo.usp_WriteCheckpoint");
            command.Parameters.AddWithValue("@ConnectorId", connectorId);
            command.Parameters.AddWithValue("@ObjectType", objectType);
            command.Parameters.AddWithValue("@ChunkIndex", Math.Max(current, chunkIndex));
            command.Parameters.AddWithValue("@SinceIso", (object?)sinceIso ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Remove all checkpoint rows for <paramref name="connectorId"/>.</summary>
    public static void ClearCheckpoint(string connectorId)
    {
        using var connection = OpenConnection();
        using var command = Proc(connection, "dbo.usp_ClearCheckpoints");
        command.Parameters.AddWithValue("@ConnectorId", connectorId);
        command.ExecuteNonQuery();
    }

    // ── Dead-letter queue ────────────────────────────────────────────────────

    /// <summary>
    /// Append failed items via usp_AppendDeadLetter (bulk, atomic — replaces the
    /// file-lock approach). ``@RecordsJson`` uses the contract's camelCase JSON
    /// convention: ``[{"itemId", "objectType", "error", "requestBody", "responseBody"}]``
    /// where requestBody/responseBody are serialized JSON text (or SQL NULL when
    /// not captured for the item).
    /// </summary>
    public static void AppendDeadLetter(
        string connectorId,
        IReadOnlyList<(string ItemId, string Error)> failures,
        string objectType,
        Dictionary<string, JsonNode?>? requestBodies = null,
        Dictionary<string, JsonNode?>? responseBodies = null)
    {
        if (failures.Count == 0)
            return;
        requestBodies ??= new Dictionary<string, JsonNode?>();
        responseBodies ??= new Dictionary<string, JsonNode?>();
        var records = new JsonArray();
        foreach (var (itemId, itemError) in failures)
        {
            var record = new JsonObject
            {
                ["itemId"] = itemId,
                ["objectType"] = objectType,
                ["error"] = itemError,
                // "null" (JSON text) means captured-but-null; absent key → SQL NULL.
                ["requestBody"] = requestBodies.TryGetValue(itemId, out var requestBody)
                    ? PyJson.Dumps(requestBody)
                    : null,
                ["responseBody"] = responseBodies.TryGetValue(itemId, out var responseBody)
                    ? PyJson.Dumps(responseBody)
                    : null,
            };
            records.Add(record);
        }
        try
        {
            using var connection = OpenConnection();
            using var command = Proc(connection, "dbo.usp_AppendDeadLetter");
            command.Parameters.AddWithValue("@ConnectorId", connectorId);
            command.Parameters.AddWithValue("@RecordsJson", PyJson.Dumps(records));
            command.ExecuteNonQuery();
        }
        catch (Exception exc) when (exc is SqlException or InvalidOperationException)
        {
            // Mirror the file implementation: never lose track of failures silently.
            Logger.Error(
                $"Failed to write {failures.Count} failed record(s) to SQL dead-letter table: {exc.Message}");
            foreach (var (itemId, itemError) in failures)
            {
                Logger.Error(
                    $"  UNRECORDED FAILURE — object={objectType} id={itemId} error={itemError}");
            }
        }
    }

    /// <summary>
    /// Read all unretried dead-letter rows, returning ``(Id, record)`` pairs where
    /// ``record`` matches the JSONL file shape byte-for-byte in structure.
    /// </summary>
    public static List<(long Id, JsonObject Record)> ReadDeadLetterRows(string connectorId)
    {
        var rows = new List<(long, JsonObject)>();
        using var connection = OpenConnection();
        using var command = Proc(connection, "dbo.usp_ReadDeadLetter");
        command.Parameters.AddWithValue("@ConnectorId", connectorId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = Convert.ToInt64(reader["Id"]);
            var failedUtc = DateTime.SpecifyKind(
                Convert.ToDateTime(reader["FailedUtc"]), DateTimeKind.Utc);
            var record = new JsonObject
            {
                ["item_id"] = Convert.ToString(reader["ItemId"]),
                ["object_type"] = Convert.ToString(reader["ObjectType"]),
                ["error"] = Convert.ToString(reader["Error"]),
                ["timestamp"] = SyncState.IsoFormat(failedUtc),
            };
            var requestBody = reader["RequestBody"];
            if (requestBody is not (DBNull or null))
                record["request_body"] = ParseBodyText(Convert.ToString(requestBody));
            var responseBody = reader["ResponseBody"];
            if (responseBody is not (DBNull or null))
                record["response_body"] = ParseBodyText(Convert.ToString(responseBody));
            rows.Add((id, record));
        }
        return rows;
    }

    /// <summary>Unretried dead-letter records in the exact file shape.</summary>
    public static List<JsonObject> ReadDeadLetter(string connectorId)
        => ReadDeadLetterRows(connectorId).Select(row => row.Record).ToList();

    /// <summary>Mark rows retried. ``@Ids`` is a JSON array of bigints (``[1,2,3]``).</summary>
    public static void MarkDeadLetterRetried(IEnumerable<long> ids)
    {
        var idList = ids.ToList();
        if (idList.Count == 0)
            return;
        using var connection = OpenConnection();
        using var command = Proc(connection, "dbo.usp_MarkDeadLetterRetried");
        command.Parameters.AddWithValue("@Ids", "[" + string.Join(",", idList) + "]");
        command.ExecuteNonQuery();
    }

    /// <summary>Delete all dead-letter rows for <paramref name="connectorId"/>.</summary>
    public static void ClearDeadLetter(string connectorId)
    {
        using var connection = OpenConnection();
        using var command = Proc(connection, "dbo.usp_ClearDeadLetter");
        command.Parameters.AddWithValue("@ConnectorId", connectorId);
        command.ExecuteNonQuery();
    }

    private static JsonNode? ParseBodyText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        try
        {
            return JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return JsonValue.Create(text);
        }
    }
}
