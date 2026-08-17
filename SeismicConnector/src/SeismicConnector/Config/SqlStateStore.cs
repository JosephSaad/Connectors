// Config/SqlStateStore.cs
// -----------------------
// SQL Server backend for SyncState (USE_SQL_SERVER=true).
//
// This lived in Connector.Chassis until it was moved here, because it was never
// shared: this connector was its only consumer, it had no chassis tests, and the
// other four connectors will not adopt it -- each has a recorded, permanent
// reason to keep its own (Clarizen and Hadoop reach SQL through an injectable
// ISqlGateway seam their tests mock; Altrata's implements IStateStore with
// DSAR-suppression and billable-lookup operations; Salesforce's is bound to its
// own stored-procedure contract). A reference implementation with no reachable
// migration target is not a shared component -- it is one connector's code in a
// shared project, validated against one consumer while its location implies the
// fleet. See Connector.Chassis/CONSOLIDATION.md.
//
// It still reads SQL through the chassis SqlExecutor, which IS shared.

using System.Text.Json.Nodes;

namespace SeismicConnector.Config;

public static class SqlStateStore
{
    public static DateTime? ReadLastSync(string connectorId) =>
        SqlExecutor.Execute(connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT LastSyncUtc FROM dbo.SyncTimestamps WHERE ConnectorId = @ConnectorId";
            cmd.Parameters.AddWithValue("@ConnectorId", connectorId);
            var result = cmd.ExecuteScalar();
            return result is DateTime dt ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : (DateTime?)null;
        });

    public static void WriteLastSync(string connectorId, DateTime timestamp) =>
        SqlExecutor.Execute(connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                MERGE dbo.SyncTimestamps AS target
                USING (SELECT @ConnectorId AS ConnectorId) AS source
                    ON target.ConnectorId = source.ConnectorId
                WHEN MATCHED THEN UPDATE SET LastSyncUtc = @LastSyncUtc
                WHEN NOT MATCHED THEN INSERT (ConnectorId, LastSyncUtc) VALUES (@ConnectorId, @LastSyncUtc);
                """;
            cmd.Parameters.AddWithValue("@ConnectorId", connectorId);
            cmd.Parameters.AddWithValue("@LastSyncUtc", timestamp.ToUniversalTime());
            cmd.ExecuteNonQuery();
        });

    public static JsonObject? ReadCheckpoint(string connectorId) =>
        SqlExecutor.Execute(connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT ObjectType, SinceIso, ChunkIndex FROM dbo.Checkpoints
                WHERE ConnectorId = @ConnectorId
                """;
            cmd.Parameters.AddWithValue("@ConnectorId", connectorId);
            using var reader = cmd.ExecuteReader();
            string? since = null;
            var completed = new JsonObject();
            var any = false;
            while (reader.Read())
            {
                any = true;
                completed[reader.GetString(0)] = reader.GetInt32(2);
                if (!reader.IsDBNull(1))
                    since = reader.GetString(1);
            }
            return any ? new JsonObject { ["since"] = since, ["completed"] = completed } : null;
        });

    public static void WriteCheckpoint(string connectorId, string? sinceIso, string objectType, int chunkIndex) =>
        SqlExecutor.Execute(connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                MERGE dbo.Checkpoints AS target
                USING (SELECT @ConnectorId AS ConnectorId, @ObjectType AS ObjectType) AS source
                    ON target.ConnectorId = source.ConnectorId AND target.ObjectType = source.ObjectType
                WHEN MATCHED THEN UPDATE SET
                    SinceIso = @SinceIso,
                    ChunkIndex = CASE WHEN target.SinceIso = @SinceIso OR (target.SinceIso IS NULL AND @SinceIso IS NULL)
                                      THEN IIF(target.ChunkIndex > @ChunkIndex, target.ChunkIndex, @ChunkIndex)
                                      ELSE @ChunkIndex END,
                    UpdatedUtc = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN INSERT (ConnectorId, ObjectType, SinceIso, ChunkIndex, UpdatedUtc)
                    VALUES (@ConnectorId, @ObjectType, @SinceIso, @ChunkIndex, SYSUTCDATETIME());
                """;
            cmd.Parameters.AddWithValue("@ConnectorId", connectorId);
            cmd.Parameters.AddWithValue("@ObjectType", objectType);
            cmd.Parameters.AddWithValue("@SinceIso", (object?)sinceIso ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ChunkIndex", chunkIndex);
            cmd.ExecuteNonQuery();
        });

    public static void ClearCheckpoint(string connectorId) =>
        SqlExecutor.Execute(connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM dbo.Checkpoints WHERE ConnectorId = @ConnectorId";
            cmd.Parameters.AddWithValue("@ConnectorId", connectorId);
            cmd.ExecuteNonQuery();
        });

    public static void AppendDeadLetter(
        string connectorId,
        IReadOnlyList<(string ItemId, string Error)> failures,
        string objectType,
        Dictionary<string, JsonNode?>? requestBodies,
        Dictionary<string, JsonNode?>? responseBodies) =>
        SqlExecutor.Execute(connection =>
        {
            foreach (var (itemId, error) in failures)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO dbo.DeadLetter
                        (ConnectorId, ItemId, ObjectType, Error, RequestBody, ResponseBody, CorrelationId, CreatedUtc)
                    VALUES (@ConnectorId, @ItemId, @ObjectType, @Error, @RequestBody, @ResponseBody, @CorrelationId, SYSUTCDATETIME());
                    """;
                cmd.Parameters.AddWithValue("@ConnectorId", connectorId);
                cmd.Parameters.AddWithValue("@ItemId", itemId);
                cmd.Parameters.AddWithValue("@ObjectType", objectType);
                cmd.Parameters.AddWithValue("@Error", (object?)error ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CorrelationId",
                    (object?)Tracing.CurrentCorrelationId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@RequestBody",
                    requestBodies is not null && requestBodies.TryGetValue(itemId, out var request) && request is not null
                        ? request.ToJsonString()
                        : DBNull.Value);
                cmd.Parameters.AddWithValue("@ResponseBody",
                    responseBodies is not null && responseBodies.TryGetValue(itemId, out var response) && response is not null
                        ? response.ToJsonString()
                        : DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        });

    public static List<JsonObject> ReadDeadLetter(string connectorId) =>
        SqlExecutor.Execute(connection =>
        {
            var entries = new List<JsonObject>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT ItemId, ObjectType, Error, RequestBody, ResponseBody, CreatedUtc, CorrelationId
                FROM dbo.DeadLetter WHERE ConnectorId = @ConnectorId ORDER BY Id
                """;
            cmd.Parameters.AddWithValue("@ConnectorId", connectorId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var record = new JsonObject
                {
                    ["item_id"] = reader.GetString(0),
                    ["object_type"] = reader.GetString(1),
                    ["error"] = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    ["timestamp"] = reader.GetDateTime(5).ToString("o"),
                };
                if (!reader.IsDBNull(3))
                    record["request_body"] = JsonNode.Parse(reader.GetString(3));
                if (!reader.IsDBNull(4))
                    record["response_body"] = JsonNode.Parse(reader.GetString(4));
                if (!reader.IsDBNull(6))
                    record["correlation_id"] = reader.GetString(6);
                entries.Add(record);
            }
            return entries;
        });

    public static void ClearDeadLetter(string connectorId) =>
        SqlExecutor.Execute(connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM dbo.DeadLetter WHERE ConnectorId = @ConnectorId";
            cmd.Parameters.AddWithValue("@ConnectorId", connectorId);
            cmd.ExecuteNonQuery();
        });
}
