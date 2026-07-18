// Config/SqlStateStore.cs
// -----------------------
// SQL Server backend for SyncState (USE_SQL_SERVER=true). Sync timestamps,
// checkpoints and the dead-letter queue live in three tables (see
// scripts/sql/create-database.sql and docs/SQL_CONTRACT.md):
//
//   dbo.SyncTimestamps (ConnectorId PK, LastSyncUtc)
//   dbo.Checkpoints    (ConnectorId, ObjectType) PK, SinceIso, ChunkIndex
//   dbo.DeadLetter     (Id identity, ConnectorId, ItemId, ObjectType, Error,
//                       RequestBody, ResponseBody, CreatedUtc)
//
// The gateway is injectable for tests (ISqlGateway); production lazily builds
// a SqlExecutor from SQL_CONNECTION_STRING.

using System.Globalization;
using System.Text.Json.Nodes;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Config;

public static class SqlStateStore
{
    private static ISqlGateway? _gateway;

    /// <summary>Gateway seam; tests assign a fake. Lazily a SqlExecutor in production.</summary>
    internal static ISqlGateway Gateway
    {
        get => _gateway ??= new SqlExecutor();
        set => _gateway = value;
    }

    internal static void ResetForTests() => _gateway = null;

    // ── Sync timestamps ──────────────────────────────────────────────────────

    public static DateTime? ReadLastSync(string connectorId)
    {
        var value = Gateway.Scalar(
            "SELECT LastSyncUtc FROM dbo.SyncTimestamps WHERE ConnectorId = @connector;",
            ("@connector", connectorId));
        return value is DateTime dt ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : null;
    }

    public static void WriteLastSync(string connectorId, DateTime timestampUtc)
    {
        Gateway.Execute(
            """
            MERGE dbo.SyncTimestamps AS target
            USING (SELECT @connector AS ConnectorId) AS source
               ON target.ConnectorId = source.ConnectorId
             WHEN MATCHED THEN UPDATE SET LastSyncUtc = @ts
             WHEN NOT MATCHED THEN INSERT (ConnectorId, LastSyncUtc) VALUES (@connector, @ts);
            """,
            ("@connector", connectorId), ("@ts", timestampUtc));
    }

    // ── Checkpoints ──────────────────────────────────────────────────────────

    public static JsonObject? ReadCheckpoint(string connectorId)
    {
        var rows = Gateway.Query(
            "SELECT ObjectType, SinceIso, ChunkIndex FROM dbo.Checkpoints WHERE ConnectorId = @connector;",
            ("@connector", connectorId));
        if (rows.Count == 0)
            return null;

        var completed = new JsonObject();
        string? since = null;
        foreach (var row in rows)
        {
            since = row["SinceIso"] as string;
            completed[(string)row["ObjectType"]!] =
                Convert.ToInt32(row["ChunkIndex"], CultureInfo.InvariantCulture);
        }
        return new JsonObject { ["since"] = since, ["completed"] = completed };
    }

    public static void WriteCheckpoint(string connectorId, string? sinceIso, string objectType, int chunkIndex)
    {
        // A different "since" means a new run boundary — reset the map first.
        Gateway.Execute(
            """
            DELETE FROM dbo.Checkpoints
             WHERE ConnectorId = @connector
               AND ISNULL(SinceIso, '') <> ISNULL(@since, '');
            """,
            ("@connector", connectorId), ("@since", sinceIso));
        Gateway.Execute(
            """
            MERGE dbo.Checkpoints AS target
            USING (SELECT @connector AS ConnectorId, @type AS ObjectType) AS source
               ON target.ConnectorId = source.ConnectorId AND target.ObjectType = source.ObjectType
             WHEN MATCHED THEN UPDATE
                  SET ChunkIndex = IIF(@chunk > target.ChunkIndex, @chunk, target.ChunkIndex),
                      SinceIso = @since
             WHEN NOT MATCHED THEN
                  INSERT (ConnectorId, ObjectType, SinceIso, ChunkIndex)
                  VALUES (@connector, @type, @since, @chunk);
            """,
            ("@connector", connectorId), ("@type", objectType), ("@since", sinceIso), ("@chunk", chunkIndex));
    }

    public static void ClearCheckpoint(string connectorId)
    {
        Gateway.Execute(
            "DELETE FROM dbo.Checkpoints WHERE ConnectorId = @connector;",
            ("@connector", connectorId));
    }

    // ── Dead-letter queue ────────────────────────────────────────────────────

    public static void AppendDeadLetter(
        string connectorId,
        IReadOnlyList<(string ItemId, string Error)> failures,
        string objectType,
        Dictionary<string, JsonNode?>? requestBodies,
        Dictionary<string, JsonNode?>? responseBodies)
    {
        var correlationId = Infrastructure.CorrelationContext.Current;
        foreach (var (itemId, error) in failures)
        {
            JsonNode? request = null;
            JsonNode? response = null;
            requestBodies?.TryGetValue(itemId, out request);
            responseBodies?.TryGetValue(itemId, out response);
            Gateway.Execute(
                """
                INSERT INTO dbo.DeadLetter
                    (ConnectorId, ItemId, ObjectType, Error, RequestBody, ResponseBody, CorrelationId, CreatedUtc)
                VALUES (@connector, @item, @type, @error, @request, @response, @correlation, SYSUTCDATETIME());
                """,
                ("@connector", connectorId),
                ("@item", itemId),
                ("@type", objectType),
                ("@error", error),
                ("@request", request?.ToJsonString()),
                ("@response", response?.ToJsonString()),
                ("@correlation", (object?)correlationId));
        }
    }

    public static List<JsonObject> ReadDeadLetter(string connectorId)
    {
        var rows = Gateway.Query(
            """
            SELECT ItemId, ObjectType, Error, RequestBody, ResponseBody, CorrelationId, CreatedUtc
              FROM dbo.DeadLetter WHERE ConnectorId = @connector ORDER BY Id;
            """,
            ("@connector", connectorId));
        var entries = new List<JsonObject>(rows.Count);
        foreach (var row in rows)
        {
            var entry = new JsonObject
            {
                ["item_id"] = row["ItemId"] as string,
                ["object_type"] = row["ObjectType"] as string,
                ["error"] = row["Error"] as string,
                ["timestamp"] = row["CreatedUtc"] is DateTime dt
                    ? SyncState.IsoFormat(dt)
                    : null,
            };
            if (row.TryGetValue("CorrelationId", out var corr) && corr is string cid && cid.Length > 0)
                entry["correlation_id"] = cid;
            if (row["RequestBody"] is string request && request.Length > 0)
                entry["request_body"] = JsonNode.Parse(request);
            if (row["ResponseBody"] is string response && response.Length > 0)
                entry["response_body"] = JsonNode.Parse(response);
            entries.Add(entry);
        }
        return entries;
    }

    public static void ClearDeadLetter(string connectorId)
    {
        Gateway.Execute(
            "DELETE FROM dbo.DeadLetter WHERE ConnectorId = @connector;",
            ("@connector", connectorId));
    }
}
