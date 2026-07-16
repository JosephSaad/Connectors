// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Integration tests for the SQL Server state backend (Config/SqlStateStore
// routed through SyncState). These tests need a real SQL Server with the
// contract schema deployed (scripts/sql) and SKIP (early return) unless the
// SQLSERVER_TEST_CONNECTION_STRING environment variable is set, so the suite
// stays green on machines without SQL Server.

using System.Data;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Config;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Tests;

/// <summary>
/// Swaps process env vars for the duration of a test and resets the cached
/// provider checks in SyncState / HaCoordinator (restored on Dispose).
/// </summary>
internal sealed class EnvVarScope : IDisposable
{
    private readonly Dictionary<string, string?> _saved = new();

    public EnvVarScope(params (string Name, string? Value)[] variables)
    {
        foreach (var (name, value) in variables)
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
        SyncState.ResetProviderCache();
        HaCoordinator.ResetCache();
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
        SyncState.ResetProviderCache();
        HaCoordinator.ResetCache();
    }
}

internal static class SqlTestSupport
{
    /// <summary>Set to run the SQL Server integration tests; unset → they skip.</summary>
    public static string? TestConnectionString =>
        Environment.GetEnvironmentVariable("SQLSERVER_TEST_CONNECTION_STRING");

    public static EnvVarScope SqlScope(params (string Name, string? Value)[] extra)
    {
        var variables = new List<(string, string?)>
        {
            ("USE_SQL_SERVER", "true"),
            ("SQL_CONNECTION_STRING", TestConnectionString),
        };
        variables.AddRange(extra);
        return new EnvVarScope(variables.ToArray());
    }

    public static string UniqueConnectorId(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(40, prefix.Length + 33)];
}

[Collection("IngestGlobalState")]
public class SqlStateStoreTests
{
    [Fact]
    public void LastSyncRoundTrip()
    {
        if (string.IsNullOrEmpty(SqlTestSupport.TestConnectionString))
            return;  // SKIP: no SQL Server available
        using var scope = SqlTestSupport.SqlScope();
        var connectorId = SqlTestSupport.UniqueConnectorId("sqltest-sync");

        Assert.Null(SyncState.ReadLastSync(connectorId));

        var timestamp = new DateTime(2026, 7, 1, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234560);
        SyncState.WriteLastSync(connectorId, timestamp);

        var back = SyncState.ReadLastSync(connectorId);
        Assert.NotNull(back);
        Assert.Equal(timestamp, back!.Value);
        Assert.Equal(DateTimeKind.Utc, back.Value.Kind);
    }

    [Fact]
    public void CheckpointRoundTripAndClear()
    {
        if (string.IsNullOrEmpty(SqlTestSupport.TestConnectionString))
            return;  // SKIP: no SQL Server available
        using var scope = SqlTestSupport.SqlScope();
        var connectorId = SqlTestSupport.UniqueConnectorId("sqltest-cp");

        Assert.Null(SyncState.ReadCheckpoint(connectorId));

        const string sinceIso = "2026-01-01T00:00:00";
        SyncState.WriteCheckpoint(connectorId, sinceIso, "Account", 2);
        SyncState.WriteCheckpoint(connectorId, sinceIso, "Contact", 5);
        // Chunk index is monotonic — a lower value must not regress it.
        SyncState.WriteCheckpoint(connectorId, sinceIso, "Account", 1);

        var checkpoint = SyncState.ReadCheckpoint(connectorId);
        Assert.NotNull(checkpoint);
        Assert.Equal(sinceIso, checkpoint!["since"]?.ToString());
        var completed = checkpoint["completed"]!.AsObject();
        Assert.Equal(2, completed["Account"]!.GetValue<int>());
        Assert.Equal(5, completed["Contact"]!.GetValue<int>());

        SyncState.ClearCheckpoint(connectorId);
        Assert.Null(SyncState.ReadCheckpoint(connectorId));
    }

    [Fact]
    public void CheckpointResetsWhenSinceChanges()
    {
        if (string.IsNullOrEmpty(SqlTestSupport.TestConnectionString))
            return;  // SKIP: no SQL Server available
        using var scope = SqlTestSupport.SqlScope();
        var connectorId = SqlTestSupport.UniqueConnectorId("sqltest-cpsince");

        SyncState.WriteCheckpoint(connectorId, "2026-01-01T00:00:00", "Account", 3);
        // Same file semantics as the JSON checkpoint: a different since rewrites
        // the checkpoint from scratch.
        SyncState.WriteCheckpoint(connectorId, "2026-02-02T00:00:00", "Contact", 1);

        var checkpoint = SyncState.ReadCheckpoint(connectorId);
        Assert.NotNull(checkpoint);
        Assert.Equal("2026-02-02T00:00:00", checkpoint!["since"]?.ToString());
        var completed = checkpoint["completed"]!.AsObject();
        Assert.False(completed.ContainsKey("Account"));
        Assert.Equal(1, completed["Contact"]!.GetValue<int>());

        SyncState.ClearCheckpoint(connectorId);
    }

    [Fact]
    public void DeadLetterRoundTrip()
    {
        if (string.IsNullOrEmpty(SqlTestSupport.TestConnectionString))
            return;  // SKIP: no SQL Server available
        using var scope = SqlTestSupport.SqlScope();
        var connectorId = SqlTestSupport.UniqueConnectorId("sqltest-dl");
        var dlPath = SyncState.FailedRecordsPath(connectorId);

        Assert.Empty(SyncState.ReadFailedRecords(connectorId));

        var requestBodies = new Dictionary<string, JsonNode?>
        {
            ["it-1"] = new JsonObject { ["acl"] = new JsonArray(), ["名前"] = "ünïcode" },
        };
        var responseBodies = new Dictionary<string, JsonNode?>
        {
            ["it-1"] = new JsonObject { ["status"] = 400 },
        };
        SyncState.AppendFailedRecords(
            dlPath,
            new List<(string, string)>
            {
                ("it-1", "[Graph] HTTP 400: bad request"),
                ("it-2", "[Transform] boom"),
            },
            "Account",
            requestBodies: requestBodies,
            responseBodies: responseBodies);

        var entries = SyncState.ReadFailedRecords(connectorId);
        Assert.Equal(2, entries.Count);

        var first = entries.Single(e => e["item_id"]!.ToString() == "it-1");
        Assert.Equal("Account", first["object_type"]!.ToString());
        Assert.Equal("[Graph] HTTP 400: bad request", first["error"]!.ToString());
        Assert.False(string.IsNullOrEmpty(first["timestamp"]?.ToString()));
        Assert.Equal("ünïcode", first["request_body"]!["名前"]!.ToString());
        Assert.Equal(400, first["response_body"]!["status"]!.GetValue<int>());

        var second = entries.Single(e => e["item_id"]!.ToString() == "it-2");
        Assert.False(second.ContainsKey("request_body"));

        // Retried rows disappear from the unretried read.
        var rows = SqlStateStore.ReadDeadLetterRows(connectorId);
        SqlStateStore.MarkDeadLetterRetried(rows.Select(row => row.Id));
        Assert.Empty(SyncState.ReadFailedRecords(connectorId));

        SyncState.ClearFailedRecords(connectorId);
        Assert.Empty(SyncState.ReadFailedRecords(connectorId));
    }

    // ── Commit-ack-loss idempotency (retry of a committed-but-unacked unit) ──

    /// <summary>
    /// usp_AppendDeadLetter with an explicit @BatchId — proc-level, mirroring a
    /// transient retry of ONE SqlStateStore.AppendDeadLetter unit (which
    /// generates its batch id outside the retry lambda and therefore
    /// re-presents the same one).
    /// </summary>
    private static void AppendDeadLetterBatch(string connectorId, Guid batchId, string recordsJson)
    {
        SqlExecutor.Execute(SqlStateStore.ConnectionString, connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandType = CommandType.StoredProcedure;
            command.CommandText = "dbo.usp_AppendDeadLetter";
            command.Parameters.AddWithValue("@ConnectorId", connectorId);
            command.Parameters.AddWithValue("@RecordsJson", recordsJson);
            command.Parameters.AddWithValue("@BatchId", batchId);
            command.ExecuteNonQuery();
        });
    }

    /// <summary>Start a session via usp_StartSession with an explicit id (proc-level).</summary>
    private static void ExecStartSession(Guid sessionId, string connectionId)
    {
        SqlExecutor.Execute(SqlStateStore.ConnectionString, connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandType = CommandType.StoredProcedure;
            command.CommandText = "dbo.usp_StartSession";
            command.Parameters.AddWithValue("@SessionId", sessionId);
            command.Parameters.AddWithValue("@ConnectionId", connectionId);
            command.Parameters.AddWithValue("@CrawlType", "content");
            command.Parameters.AddWithValue("@SyncType", "full");
            command.ExecuteNonQuery();
        });
    }

    /// <summary>Back-date a session's StartedUtc so it falls past a retention cutoff.</summary>
    private static void AgeSessionBack(Guid sessionId, int days)
    {
        SqlExecutor.Execute(SqlStateStore.ConnectionString, connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE dbo.SyncSessions SET StartedUtc = DATEADD(DAY, -@Days, SYSUTCDATETIME()) "
                + "WHERE SessionId = @SessionId";
            command.Parameters.AddWithValue("@Days", days);
            command.Parameters.AddWithValue("@SessionId", sessionId);
            command.ExecuteNonQuery();
        });
    }

    private static int CountSessions(Guid sessionId)
    {
        return SqlExecutor.Execute(SqlStateStore.ConnectionString, connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM dbo.SyncSessions WHERE SessionId = @SessionId";
            command.Parameters.AddWithValue("@SessionId", sessionId);
            return Convert.ToInt32(command.ExecuteScalar());
        });
    }

    private static void DeleteSession(Guid sessionId)
    {
        SqlExecutor.Execute(SqlStateStore.ConnectionString, connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM dbo.SyncSessions WHERE SessionId = @SessionId";
            command.Parameters.AddWithValue("@SessionId", sessionId);
            command.ExecuteNonQuery();
        });
    }

    [Fact]
    public void AppendDeadLetterSameBatchIdTwiceDoesNotDuplicate()
    {
        if (string.IsNullOrEmpty(SqlTestSupport.TestConnectionString))
            return;  // SKIP: no SQL Server available
        using var scope = SqlTestSupport.SqlScope();
        var connectorId = SqlTestSupport.UniqueConnectorId("sqltest-dlbatch");
        const string recordsJson =
            "[{\"itemId\": \"it-1\", \"objectType\": \"Account\", \"error\": \"boom\"}, "
            + "{\"itemId\": \"it-2\", \"objectType\": \"Account\", \"error\": \"bang\"}]";

        // A commit-ack-loss retry re-runs the SAME append with the SAME batch
        // id — the batch must not be duplicated.
        var batchId = Guid.NewGuid();
        AppendDeadLetterBatch(connectorId, batchId, recordsJson);
        AppendDeadLetterBatch(connectorId, batchId, recordsJson);
        Assert.Equal(2, SyncState.ReadFailedRecords(connectorId).Count);

        // A different batch id (a genuinely new append) still lands.
        AppendDeadLetterBatch(connectorId, Guid.NewGuid(), recordsJson);
        Assert.Equal(4, SyncState.ReadFailedRecords(connectorId).Count);

        SyncState.ClearFailedRecords(connectorId);
    }

    [Fact]
    public void AppendDeadLetterSeparateCallsBothAppend()
    {
        if (string.IsNullOrEmpty(SqlTestSupport.TestConnectionString))
            return;  // SKIP: no SQL Server available
        using var scope = SqlTestSupport.SqlScope();
        var connectorId = SqlTestSupport.UniqueConnectorId("sqltest-dlfresh");
        var dlPath = SyncState.FailedRecordsPath(connectorId);

        // Two separate AppendDeadLetter calls carry FRESH batch ids, so even
        // identical payloads (the same item failing twice) both land — the
        // batch guard only dedupes retries of one unit, never real failures.
        var failures = new List<(string, string)> { ("it-1", "[Graph] HTTP 400: bad request") };
        SyncState.AppendFailedRecords(dlPath, failures, "Account");
        SyncState.AppendFailedRecords(dlPath, failures, "Account");
        Assert.Equal(2, SyncState.ReadFailedRecords(connectorId).Count);

        SyncState.ClearFailedRecords(connectorId);
    }

    [Fact]
    public void PruneHistoryDeletesZombieRunningSessions()
    {
        if (string.IsNullOrEmpty(SqlTestSupport.TestConnectionString))
            return;  // SKIP: no SQL Server available
        using var scope = SqlTestSupport.SqlScope();
        var connectionId = SqlTestSupport.UniqueConnectorId("sqltest-zombie");
        var zombie = Guid.NewGuid();
        var live = Guid.NewGuid();
        try
        {
            ExecStartSession(zombie, connectionId);
            ExecStartSession(live, connectionId);
            AgeSessionBack(zombie, days: 40);

            // 30-day retention: a 'running' row started 40 days ago is a zombie
            // from a crashed run and is pruned; the fresh running row survives.
            SqlExecutor.Execute(SqlStateStore.ConnectionString, connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandType = CommandType.StoredProcedure;
                command.CommandText = "dbo.usp_PruneHistory";
                command.Parameters.AddWithValue("@RetentionDays", 30);
                command.ExecuteNonQuery();
            });

            Assert.Equal(0, CountSessions(zombie));
            Assert.Equal(1, CountSessions(live));
        }
        finally
        {
            DeleteSession(zombie);
            DeleteSession(live);
        }
    }

    [Fact]
    public void LogPrunerSqlModePrunesServerHistoryThroughSqlExecutor()
    {
        if (string.IsNullOrEmpty(SqlTestSupport.TestConnectionString))
            return;  // SKIP: no SQL Server available
        // LogPruner no longer builds a raw SqlConnection (which skipped the
        // Encrypt-forcing / managed-identity / retry hardening and failed login
        // every run under SQL_USE_MANAGED_IDENTITY=true) — usp_PruneHistory now
        // runs through SqlExecutor. End-to-end: SQL-mode Prune() must delete an
        // aged zombie 'running' session.
        using var scope = SqlTestSupport.SqlScope(("LOG_RETENTION_DAYS", "30"));
        var connectionId = SqlTestSupport.UniqueConnectorId("sqltest-logprune");
        var tmpLogs = Directory.CreateTempSubdirectory("log_pruner_sql_").FullName;
        LogPruner.LogsDirOverride = tmpLogs;
        var zombie = Guid.NewGuid();
        try
        {
            ExecStartSession(zombie, connectionId);
            AgeSessionBack(zombie, days: 40);

            LogPruner.Prune();

            Assert.Equal(0, CountSessions(zombie));
        }
        finally
        {
            LogPruner.LogsDirOverride = null;
            DeleteSession(zombie);
            try
            {
                Directory.Delete(tmpLogs, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void FileProviderStillUsedWhenEnvUnset()
    {
        // No SQL required: with USE_SQL_SERVER unset the file provider runs and
        // the cached env check is resettable.
        using var scope = new EnvVarScope(
            ("USE_SQL_SERVER", null),
            ("SQL_CONNECTION_STRING", null));
        Assert.False(SyncState.UseSqlServer);

        using var flipped = new EnvVarScope(
            ("USE_SQL_SERVER", "true"),
            ("SQL_CONNECTION_STRING", "Server=example;Database=x;"));
        Assert.True(SyncState.UseSqlServer);
    }
}
