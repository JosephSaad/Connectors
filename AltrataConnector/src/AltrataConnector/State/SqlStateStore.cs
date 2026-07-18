// State/SqlStateStore.cs
// ----------------------
// SQL Server state backend (USE_SQL_SERVER=true + SQL_CONNECTION_STRING).
// Moves ALL non-identity state — checkpoint, sync timestamps, dead-letter,
// delivery ledger, KV/billable counter — into SQL Server so multiple nodes
// can share one source of truth (required for HA_MODE). See docs/SQL_CONTRACT.md.
//
// SQL_USE_MANAGED_IDENTITY=true appends "Authentication=Active Directory Default"
// when the connection string does not already carry an auth mode.
// SQL_MAX_RETRIES wraps every command in a transient-fault retry loop.

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using AltrataConnector.Infrastructure;

namespace AltrataConnector.State;

public sealed class SqlStateStore : IStateStore
{
    private static readonly IAppLogger Logger = Logging.GetLogger("altrata_connector.sql");

    private readonly string _connectionString;
    private readonly string _connectorId;
    private readonly int _maxRetries;
    private bool _schemaEnsured;
    private readonly object _sync = new();

    public SqlStateStore(string connectionString, string connectorId,
        bool useManagedIdentity = false, int maxRetries = 3)
    {
        _connectionString = BuildConnectionString(connectionString, useManagedIdentity);
        _connectorId = connectorId;
        _maxRetries = Math.Max(0, maxRetries);
    }

    internal static string BuildConnectionString(string raw, bool useManagedIdentity)
    {
        if (!useManagedIdentity)
            return raw;
        var builder = new SqlConnectionStringBuilder(raw);
        if (string.IsNullOrEmpty(builder["Authentication"] as string))
            builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault;
        return builder.ConnectionString;
    }

    // ---- schema -----------------------------------------------------------

    internal const string SchemaScript = """
        IF OBJECT_ID(N'dbo.altrata_checkpoint', N'U') IS NULL
        CREATE TABLE dbo.altrata_checkpoint (
            connector_id  NVARCHAR(64)  NOT NULL PRIMARY KEY,
            delivery_id   NVARCHAR(256) NOT NULL,
            dataset       NVARCHAR(64)  NOT NULL,
            file_name     NVARCHAR(512) NOT NULL,
            record_index  INT           NOT NULL,
            updated_utc   DATETIME2     NOT NULL
        );
        IF OBJECT_ID(N'dbo.altrata_deadletter', N'U') IS NULL
        CREATE TABLE dbo.altrata_deadletter (
            id            BIGINT IDENTITY(1,1) PRIMARY KEY,
            connector_id  NVARCHAR(64)   NOT NULL,
            item_id       NVARCHAR(256)  NOT NULL,
            dataset       NVARCHAR(64)   NOT NULL,
            delivery_id   NVARCHAR(256)  NOT NULL,
            error         NVARCHAR(MAX)  NOT NULL,
            op            NVARCHAR(16)   NOT NULL CONSTRAINT df_altrata_dl_op DEFAULT N'upsert',
            payload_json  NVARCHAR(MAX)  NOT NULL,
            failed_utc    DATETIME2      NOT NULL,
            attempts      INT            NOT NULL,
            redacted        BIT           NOT NULL CONSTRAINT df_altrata_dl_redacted DEFAULT 0,
            subject_ids     NVARCHAR(MAX) NOT NULL CONSTRAINT df_altrata_dl_subject_ids DEFAULT N'[]',
            subject_hashes  NVARCHAR(MAX) NOT NULL CONSTRAINT df_altrata_dl_subject_hashes DEFAULT N'[]'
        );
        IF COL_LENGTH(N'dbo.altrata_deadletter', N'op') IS NULL
            ALTER TABLE dbo.altrata_deadletter
                ADD op NVARCHAR(16) NOT NULL CONSTRAINT df_altrata_dl_op_mig DEFAULT N'upsert';
        IF COL_LENGTH(N'dbo.altrata_deadletter', N'redacted') IS NULL
            ALTER TABLE dbo.altrata_deadletter
                ADD redacted       BIT           NOT NULL CONSTRAINT df_altrata_dl_redacted_mig DEFAULT 0,
                    subject_ids    NVARCHAR(MAX) NOT NULL CONSTRAINT df_altrata_dl_subject_ids_mig DEFAULT N'[]',
                    subject_hashes NVARCHAR(MAX) NOT NULL CONSTRAINT df_altrata_dl_subject_hashes_mig DEFAULT N'[]';
        IF OBJECT_ID(N'dbo.altrata_kv', N'U') IS NULL
        CREATE TABLE dbo.altrata_kv (
            connector_id  NVARCHAR(64)  NOT NULL,
            [key]         NVARCHAR(128) NOT NULL,
            [value]       NVARCHAR(MAX) NULL,
            CONSTRAINT pk_altrata_kv PRIMARY KEY (connector_id, [key])
        );
        IF OBJECT_ID(N'dbo.altrata_deliveries', N'U') IS NULL
        CREATE TABLE dbo.altrata_deliveries (
            connector_id  NVARCHAR(64)  NOT NULL,
            delivery_id   NVARCHAR(256) NOT NULL,
            processed_utc DATETIME2     NOT NULL,
            CONSTRAINT pk_altrata_deliveries PRIMARY KEY (connector_id, delivery_id)
        );
        IF OBJECT_ID(N'dbo.altrata_leases', N'U') IS NULL
        CREATE TABLE dbo.altrata_leases (
            lease_name    NVARCHAR(128) NOT NULL PRIMARY KEY,
            owner         NVARCHAR(128) NOT NULL,
            expires_utc   DATETIME2     NOT NULL
        );
        IF OBJECT_ID(N'dbo.altrata_suppressed', N'U') IS NULL
        CREATE TABLE dbo.altrata_suppressed (
            connector_id NVARCHAR(64)  NOT NULL,
            subject_id   NVARCHAR(256) NOT NULL,
            CONSTRAINT pk_altrata_suppressed PRIMARY KEY (connector_id, subject_id)
        );
        """;

    private void EnsureSchema(SqlConnection connection)
    {
        if (_schemaEnsured)
            return;
        using var command = new SqlCommand(SchemaScript, connection);
        command.ExecuteNonQuery();
        _schemaEnsured = true;
    }

    // ---- executor with transient retry ----------------------------------------

    /// <summary>Transient SQL error numbers retried by the executor.</summary>
    internal static readonly int[] TransientErrorNumbers = { -2, 4060, 40197, 40501, 40613, 49918, 49919, 49920, 11001 };

    internal T Execute<T>(Func<SqlConnection, T> operation)
    {
        lock (_sync)
        {
            var attempt = 0;
            while (true)
            {
                try
                {
                    using var connection = new SqlConnection(_connectionString);
                    connection.Open();
                    EnsureSchema(connection);
                    return operation(connection);
                }
                catch (SqlException exc) when (attempt < _maxRetries && IsTransient(exc))
                {
                    attempt++;
                    var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
                    Logger.Warning($"SQL transient error {exc.Number} (attempt {attempt}/{_maxRetries}), retrying in {delay.TotalSeconds:0}s");
                    Thread.Sleep(delay);
                }
                catch (SqlException exc)
                {
                    // Non-transient, or transient retries exhausted: still fail
                    // fast (unchanged — shared state must not be guessed at),
                    // but record WHICH connector's state store died and the SQL
                    // error number before the exception unwinds the command.
                    Logger.Error($"SQL state operation FAILED for connector '{_connectorId}' " +
                                 $"(SQL error {exc.Number}, after {attempt} retry(ies)): {exc.Message}");
                    throw;
                }
            }
        }
    }

    internal static bool IsTransient(SqlException exc) =>
        TransientErrorNumbers.Contains(exc.Number);

    // ---- IStateStore --------------------------------------------------------------

    public CrawlCheckpoint? GetCheckpoint() => Execute(conn =>
    {
        using var cmd = new SqlCommand(
            "SELECT delivery_id, dataset, file_name, record_index, updated_utc " +
            "FROM dbo.altrata_checkpoint WHERE connector_id = @cid", conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return new CrawlCheckpoint
        {
            DeliveryId = reader.GetString(0),
            Dataset = reader.GetString(1),
            FileName = reader.GetString(2),
            RecordIndex = reader.GetInt32(3),
            UpdatedUtc = reader.GetDateTime(4),
        };
    });

    public void SaveCheckpoint(CrawlCheckpoint checkpoint) => Execute<object?>(conn =>
    {
        using var cmd = new SqlCommand("""
            MERGE dbo.altrata_checkpoint AS target
            USING (SELECT @cid AS connector_id) AS source
            ON target.connector_id = source.connector_id
            WHEN MATCHED THEN UPDATE SET delivery_id=@d, dataset=@ds, file_name=@f, record_index=@r, updated_utc=@u
            WHEN NOT MATCHED THEN INSERT (connector_id, delivery_id, dataset, file_name, record_index, updated_utc)
            VALUES (@cid, @d, @ds, @f, @r, @u);
            """, conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        cmd.Parameters.AddWithValue("@d", checkpoint.DeliveryId);
        cmd.Parameters.AddWithValue("@ds", checkpoint.Dataset);
        cmd.Parameters.AddWithValue("@f", checkpoint.FileName);
        cmd.Parameters.AddWithValue("@r", checkpoint.RecordIndex);
        cmd.Parameters.AddWithValue("@u", checkpoint.UpdatedUtc);
        cmd.ExecuteNonQuery();
        return null;
    });

    public void ClearCheckpoint() => Execute<object?>(conn =>
    {
        using var cmd = new SqlCommand(
            "DELETE FROM dbo.altrata_checkpoint WHERE connector_id = @cid", conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        cmd.ExecuteNonQuery();
        return null;
    });

    public DateTime? GetLastSync(string kind)
    {
        var raw = GetValue($"last_sync_{kind}");
        return raw != null && DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when)
            ? when
            : null;
    }

    public void SetLastSync(string kind, DateTime utc) =>
        SetValue($"last_sync_{kind}", utc.ToString("o", CultureInfo.InvariantCulture));

    public void AddDeadLetter(DeadLetterRecord record) => Execute<object?>(conn =>
    {
        InsertDeadLetter(conn, null, record);
        return null;
    });

    private void InsertDeadLetter(SqlConnection conn, SqlTransaction? txn, DeadLetterRecord record)
    {
        using var cmd = new SqlCommand("""
            INSERT INTO dbo.altrata_deadletter
                (connector_id, item_id, dataset, delivery_id, error, op, payload_json, failed_utc, attempts,
                 redacted, subject_ids, subject_hashes)
            VALUES (@cid, @i, @ds, @d, @e, @o, @p, @f, @a, @r, @sids, @shashes);
            """, conn, txn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        cmd.Parameters.AddWithValue("@i", record.ItemId);
        cmd.Parameters.AddWithValue("@ds", record.Dataset);
        cmd.Parameters.AddWithValue("@d", record.DeliveryId);
        cmd.Parameters.AddWithValue("@e", record.Error);
        cmd.Parameters.AddWithValue("@o", record.Op);
        cmd.Parameters.AddWithValue("@p", record.PayloadJson);
        cmd.Parameters.AddWithValue("@f", record.FailedUtc);
        cmd.Parameters.AddWithValue("@a", record.Attempts);
        cmd.Parameters.AddWithValue("@r", record.Redacted);
        cmd.Parameters.AddWithValue("@sids", JsonSerializer.Serialize(record.SubjectIds));
        cmd.Parameters.AddWithValue("@shashes", JsonSerializer.Serialize(record.SubjectHashes));
        cmd.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> ParseStringList(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();   // tolerate manual edits, like the file store
        }
    }

    public IReadOnlyList<DeadLetterRecord> ReadDeadLetters() =>
        Execute(conn => ReadDeadLettersCore(conn, null));

    private IReadOnlyList<DeadLetterRecord> ReadDeadLettersCore(SqlConnection conn, SqlTransaction? txn)
    {
        var records = new List<DeadLetterRecord>();
        using var cmd = new SqlCommand(
            "SELECT item_id, dataset, delivery_id, error, op, payload_json, failed_utc, attempts, " +
            "redacted, subject_ids, subject_hashes " +
            "FROM dbo.altrata_deadletter WHERE connector_id = @cid ORDER BY id", conn, txn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            records.Add(new DeadLetterRecord
            {
                ItemId = reader.GetString(0),
                Dataset = reader.GetString(1),
                DeliveryId = reader.GetString(2),
                Error = reader.GetString(3),
                Op = reader.GetString(4),
                PayloadJson = reader.GetString(5),
                FailedUtc = reader.GetDateTime(6),
                Attempts = reader.GetInt32(7),
                Redacted = reader.GetBoolean(8),
                SubjectIds = ParseStringList(reader.GetString(9)),
                SubjectHashes = ParseStringList(reader.GetString(10)),
            });
        }
        return records;
    }

    public void ReplaceDeadLetters(IEnumerable<DeadLetterRecord> records)
    {
        ClearDeadLetters();
        foreach (var record in records)
            AddDeadLetter(record);
    }

    public void ClearDeadLetters() => Execute<object?>(conn =>
    {
        DeleteAllDeadLetters(conn, null);
        return null;
    });

    private void DeleteAllDeadLetters(SqlConnection conn, SqlTransaction? txn)
    {
        using var cmd = new SqlCommand(
            "DELETE FROM dbo.altrata_deadletter WHERE connector_id = @cid", conn, txn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Atomic read-modify-write inside a single serializable transaction
    /// so a concurrent AddDeadLetter cannot slip between the snapshot read and
    /// the whole-queue rewrite (the file backend's TOCTOU fix, in SQL terms).</summary>
    public void MutateDeadLetters(
        Func<IReadOnlyList<DeadLetterRecord>, IEnumerable<DeadLetterRecord>> transform) =>
        Execute<object?>(conn =>
        {
            using var txn = conn.BeginTransaction(System.Data.IsolationLevel.Serializable);
            try
            {
                var updated = transform(ReadDeadLettersCore(conn, txn)).ToList();
                DeleteAllDeadLetters(conn, txn);
                foreach (var record in updated)
                    InsertDeadLetter(conn, txn, record);
                txn.Commit();
            }
            catch
            {
                txn.Rollback();
                throw;
            }
            return null;
        });

    public bool IsDeliveryProcessed(string deliveryId) => Execute(conn =>
    {
        using var cmd = new SqlCommand(
            "SELECT COUNT(1) FROM dbo.altrata_deliveries WHERE connector_id = @cid AND delivery_id = @d", conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        cmd.Parameters.AddWithValue("@d", deliveryId);
        return (int)cmd.ExecuteScalar()! > 0;
    });

    public void MarkDeliveryProcessed(string deliveryId, DateTime utc) => Execute<object?>(conn =>
    {
        using var cmd = new SqlCommand("""
            MERGE dbo.altrata_deliveries AS target
            USING (SELECT @cid AS connector_id, @d AS delivery_id) AS source
            ON target.connector_id = source.connector_id AND target.delivery_id = source.delivery_id
            WHEN MATCHED THEN UPDATE SET processed_utc = @u
            WHEN NOT MATCHED THEN INSERT (connector_id, delivery_id, processed_utc) VALUES (@cid, @d, @u);
            """, conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        cmd.Parameters.AddWithValue("@d", deliveryId);
        cmd.Parameters.AddWithValue("@u", utc);
        cmd.ExecuteNonQuery();
        return null;
    });

    public IReadOnlyList<string> ListProcessedDeliveries() => Execute(conn =>
    {
        var ids = new List<string>();
        using var cmd = new SqlCommand(
            "SELECT delivery_id FROM dbo.altrata_deliveries WHERE connector_id = @cid ORDER BY delivery_id", conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return (IReadOnlyList<string>)ids;
    });

    public string? GetValue(string key) => Execute(conn =>
    {
        using var cmd = new SqlCommand(
            "SELECT [value] FROM dbo.altrata_kv WHERE connector_id = @cid AND [key] = @k", conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        cmd.Parameters.AddWithValue("@k", key);
        var result = cmd.ExecuteScalar();
        return result == null || result is DBNull ? null : (string)result;
    });

    public void SetValue(string key, string? value) => Execute<object?>(conn =>
    {
        using var cmd = new SqlCommand("""
            MERGE dbo.altrata_kv AS target
            USING (SELECT @cid AS connector_id, @k AS [key]) AS source
            ON target.connector_id = source.connector_id AND target.[key] = source.[key]
            WHEN MATCHED THEN UPDATE SET [value] = @v
            WHEN NOT MATCHED THEN INSERT (connector_id, [key], [value]) VALUES (@cid, @k, @v);
            """, conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", (object?)value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return null;
    });

    public long GetBillableLookupCount()
    {
        var raw = GetValue(StateKeys.BillableLookups);
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    public long IncrementBillableLookups(long delta = 1) => Execute(conn =>
    {
        using var cmd = new SqlCommand("""
            MERGE dbo.altrata_kv AS target
            USING (SELECT @cid AS connector_id, @k AS [key]) AS source
            ON target.connector_id = source.connector_id AND target.[key] = source.[key]
            WHEN MATCHED THEN UPDATE SET [value] = CAST(TRY_CAST(target.[value] AS BIGINT) + @delta AS NVARCHAR(32))
            WHEN NOT MATCHED THEN INSERT (connector_id, [key], [value]) VALUES (@cid, @k, CAST(@delta AS NVARCHAR(32)));
            SELECT [value] FROM dbo.altrata_kv WHERE connector_id = @cid AND [key] = @k;
            """, conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        cmd.Parameters.AddWithValue("@k", StateKeys.BillableLookups);
        cmd.Parameters.AddWithValue("@delta", delta);
        var result = cmd.ExecuteScalar();
        return long.TryParse(result as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    });

    public void AddSuppressedSubject(string subjectId) => Execute<object?>(conn =>
    {
        using var cmd = new SqlCommand("""
            IF NOT EXISTS (SELECT 1 FROM dbo.altrata_suppressed WHERE connector_id = @cid AND subject_id = @s)
                INSERT INTO dbo.altrata_suppressed (connector_id, subject_id) VALUES (@cid, @s);
            """, conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        cmd.Parameters.AddWithValue("@s", subjectId);
        cmd.ExecuteNonQuery();
        return null;
    });

    public void RemoveSuppressedSubject(string subjectId) => Execute<object?>(conn =>
    {
        using var cmd = new SqlCommand(
            "DELETE FROM dbo.altrata_suppressed WHERE connector_id = @cid AND subject_id = @s", conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        cmd.Parameters.AddWithValue("@s", subjectId);
        cmd.ExecuteNonQuery();
        return null;
    });

    public bool IsSubjectSuppressed(string subjectId) => Execute(conn =>
    {
        using var cmd = new SqlCommand(
            "SELECT COUNT(1) FROM dbo.altrata_suppressed WHERE connector_id = @cid AND subject_id = @s", conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        cmd.Parameters.AddWithValue("@s", subjectId);
        return (int)cmd.ExecuteScalar()! > 0;
    });

    public IReadOnlyList<string> ListSuppressedSubjects() => Execute(conn =>
    {
        var ids = new List<string>();
        using var cmd = new SqlCommand(
            "SELECT subject_id FROM dbo.altrata_suppressed WHERE connector_id = @cid ORDER BY subject_id", conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return (IReadOnlyList<string>)ids;
    });

    public void WipeAll() => Execute<object?>(conn =>
    {
        foreach (var table in new[]
                 { "altrata_checkpoint", "altrata_deadletter", "altrata_kv", "altrata_deliveries", "altrata_suppressed" })
        {
            using var cmd = new SqlCommand($"DELETE FROM dbo.{table} WHERE connector_id = @cid", conn);
            cmd.Parameters.AddWithValue("@cid", _connectorId);
            cmd.ExecuteNonQuery();
        }
        return null;
    });
}
