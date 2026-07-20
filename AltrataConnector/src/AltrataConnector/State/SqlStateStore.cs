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

    /// <summary>Collation for the DSAR suppression list's subject_id.
    ///
    /// The file backend compares suppressed subject ids with
    /// StringComparer.Ordinal — deliberately, and documented at length in
    /// FileStateStore.StateDoc.SuppressedSubjectsRaw. The SQL backend inherited
    /// the DATABASE's default collation, which on a stock SQL Server install
    /// (SQL_Latin1_General_CP1_CI_AS) is CASE- AND ACCENT-INSENSITIVE. The two
    /// backends therefore disagreed about what "already erased" means:
    ///
    ///   * "ALT-9001" and "alt-9001" are two distinct subjects on the file
    ///     backend and ONE subject on SQL. Erasing both on SQL silently kept
    ///     only the first — the IF NOT EXISTS guard in AddSuppressedSubject
    ///     matched case-insensitively, so the second erasure was dropped with
    ///     no error, and that subject stayed ingestible.
    ///   * Conversely a subject nobody erased ("alt-9001", erased as "ALT-9001")
    ///     answered "suppressed" on SQL and "not suppressed" on file, so a
    ///     fleet running both backends produced different ingest sets from one
    ///     erasure ledger.
    ///
    /// A BINARY collation is the fix rather than normalised key storage
    /// (upper-casing, or storing a hash): the suppression list is DSAR
    /// evidence and ListSuppressedSubjects has to hand back the subject id
    /// exactly as the erasure was filed. Normalising would either destroy those
    /// bytes or require a second shadow column carrying them. BIN2 compares
    /// code point by code point, which is equality-identical to
    /// StringComparer.Ordinal, while leaving the stored value byte-exact.
    ///
    /// _BIN2 rather than the legacy _BIN: _BIN compares only the first
    /// character by code point and the remainder byte-wise, which is not
    /// ordinal for the tail of a string.
    ///
    /// CORRECTION — an earlier revision of this comment justified BIN2 partly
    /// on the file backend "keeping every byte exactly as written". That was
    /// FALSE, and the falsehood hid a live defect. The file backend keeps every
    /// byte JSON CAN ENCODE: System.Text.Json cannot emit an unpaired UTF-16
    /// surrogate and substitutes U+FFFD, so `AddSuppressedSubject("ALT-\uD83D")`
    /// followed by `IsSubjectSuppressed` with the SAME string returns FALSE on
    /// file — the erasure is filed under a different id than the one the caller
    /// asked for, and the subject stays ingestible — while NVARCHAR (UCS-2)
    /// stores the lone surrogate verbatim and SQL answers TRUE. Collation cannot
    /// reach that: the two backends store DIFFERENT VALUES, they do not compare
    /// the same value differently.
    ///
    /// THAT DEFECT IS NOW CLOSED — at the erase-subject entry point, not on
    /// state writes. A round that rejected out-of-domain ids at the door of
    /// both backends wedged DSAR erasure over legacy data and was withdrawn;
    /// the replacement (Commands/SubjectIdPolicy.cs) validates the
    /// operator-supplied `forget-subject --id` — ill-formed UTF-16 and length
    /// against the DDL's declared subject_id width — at the very start of the
    /// command, before any mutation. Subject ids reaching THIS STORE are still
    /// deliberately NOT validated: ids resolved from stored state (the
    /// crosswalk via --email, legacy suppression entries being removed) must
    /// remain writable and removable, or read-modify-write over legacy state
    /// wedges again. Whitespace is trimmed from operator input by the
    /// commands; blank padding of legacy state remains an open divergence
    /// (docs/SQL_CONTRACT.md (c)). BIN2 remains correct and necessary here; it
    /// is simply about comparison, and the entry-point validation is about
    /// value domain.</summary>
    internal const string SubjectIdCollation = "Latin1_General_100_BIN2";

    /// <summary>The same binary collation, applied to the OTHER columns this
    /// store compares by equality.
    ///
    /// docs/SQL_CONTRACT.md previously recorded these as a known-but-unfixed
    /// residual: `altrata_kv.[key]` and `altrata_deliveries.delivery_id`
    /// inherited the database default (case-insensitive on a stock install)
    /// while their file-backend counterparts are ordinal
    /// `Dictionary&lt;string,…&gt;`. That is the SAME divergence as the
    /// suppression list, one table over — `last_sync_Full` and `last_sync_full`
    /// are one key on SQL and two on file, and two connector ids differing only
    /// by case SHARE state on SQL and not on file. The reason given for leaving
    /// it ("the affected keys are connector-generated") is a statement about
    /// today's callers, not about the interface: GetValue/SetValue and
    /// MarkDeliveryProcessed are public IStateStore members.
    ///
    /// Not applied to `altrata_checkpoint.connector_id` or
    /// `altrata_leases.lease_name`: both are inline unnamed PRIMARY KEYs, so
    /// migrating a deployed table needs dynamic SQL to discover the
    /// auto-generated constraint name, and that is not something this round can
    /// test against a real server. New deployments get the collation from the
    /// CREATE TABLE below; existing ones stay on the database default there.
    /// Recorded in docs/SQL_CONTRACT.md rather than left silent.</summary>
    internal const string IdentifierCollation = SubjectIdCollation;

    internal const string SchemaScript = """
        IF OBJECT_ID(N'dbo.altrata_checkpoint', N'U') IS NULL
        CREATE TABLE dbo.altrata_checkpoint (
            connector_id  NVARCHAR(64)  COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
            delivery_id   NVARCHAR(256) NOT NULL,
            dataset       NVARCHAR(64)  NOT NULL,
            file_name     NVARCHAR(512) NOT NULL,
            record_index  INT           NOT NULL,
            updated_utc   DATETIME2     NOT NULL
        );
        IF OBJECT_ID(N'dbo.altrata_deadletter', N'U') IS NULL
        CREATE TABLE dbo.altrata_deadletter (
            id            BIGINT IDENTITY(1,1) PRIMARY KEY,
            connector_id  NVARCHAR(64)   COLLATE Latin1_General_100_BIN2 NOT NULL,
            item_id       NVARCHAR(256)  NOT NULL,
            dataset       NVARCHAR(64)   NOT NULL,
            delivery_id   NVARCHAR(256)  NOT NULL,
            error         NVARCHAR(MAX)  NOT NULL,
            op            NVARCHAR(16)   NOT NULL CONSTRAINT df_altrata_dl_op DEFAULT N'upsert',
            correlation_id NVARCHAR(128) NULL,
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
        IF COL_LENGTH(N'dbo.altrata_deadletter', N'correlation_id') IS NULL
            ALTER TABLE dbo.altrata_deadletter ADD correlation_id NVARCHAR(128) NULL;
        IF OBJECT_ID(N'dbo.altrata_kv', N'U') IS NULL
        CREATE TABLE dbo.altrata_kv (
            connector_id  NVARCHAR(64)  COLLATE Latin1_General_100_BIN2 NOT NULL,
            [key]         NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [value]       NVARCHAR(MAX) NULL,
            CONSTRAINT pk_altrata_kv PRIMARY KEY (connector_id, [key])
        );
        IF EXISTS (SELECT 1 FROM sys.columns
                   WHERE object_id = OBJECT_ID(N'dbo.altrata_kv')
                     AND name IN (N'connector_id', N'key')
                     AND collation_name <> N'Latin1_General_100_BIN2')
        BEGIN
            ALTER TABLE dbo.altrata_kv DROP CONSTRAINT pk_altrata_kv;
            ALTER TABLE dbo.altrata_kv
                ALTER COLUMN connector_id NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL;
            ALTER TABLE dbo.altrata_kv
                ALTER COLUMN [key] NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL;
            ALTER TABLE dbo.altrata_kv
                ADD CONSTRAINT pk_altrata_kv PRIMARY KEY (connector_id, [key]);
        END
        IF OBJECT_ID(N'dbo.altrata_deliveries', N'U') IS NULL
        CREATE TABLE dbo.altrata_deliveries (
            connector_id  NVARCHAR(64)  COLLATE Latin1_General_100_BIN2 NOT NULL,
            delivery_id   NVARCHAR(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            processed_utc DATETIME2     NOT NULL,
            CONSTRAINT pk_altrata_deliveries PRIMARY KEY (connector_id, delivery_id)
        );
        IF EXISTS (SELECT 1 FROM sys.columns
                   WHERE object_id = OBJECT_ID(N'dbo.altrata_deliveries')
                     AND name IN (N'connector_id', N'delivery_id')
                     AND collation_name <> N'Latin1_General_100_BIN2')
        BEGIN
            ALTER TABLE dbo.altrata_deliveries DROP CONSTRAINT pk_altrata_deliveries;
            ALTER TABLE dbo.altrata_deliveries
                ALTER COLUMN connector_id NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL;
            ALTER TABLE dbo.altrata_deliveries
                ALTER COLUMN delivery_id NVARCHAR(256) COLLATE Latin1_General_100_BIN2 NOT NULL;
            ALTER TABLE dbo.altrata_deliveries
                ADD CONSTRAINT pk_altrata_deliveries PRIMARY KEY (connector_id, delivery_id);
        END
        IF EXISTS (SELECT 1 FROM sys.columns
                   WHERE object_id = OBJECT_ID(N'dbo.altrata_deadletter')
                     AND name = N'connector_id'
                     AND collation_name <> N'Latin1_General_100_BIN2')
            ALTER TABLE dbo.altrata_deadletter
                ALTER COLUMN connector_id NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL;
        IF OBJECT_ID(N'dbo.altrata_leases', N'U') IS NULL
        CREATE TABLE dbo.altrata_leases (
            lease_name    NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
            owner         NVARCHAR(128) NOT NULL,
            expires_utc   DATETIME2     NOT NULL
        );
        IF OBJECT_ID(N'dbo.altrata_suppressed', N'U') IS NULL
        CREATE TABLE dbo.altrata_suppressed (
            connector_id NVARCHAR(64)  COLLATE Latin1_General_100_BIN2 NOT NULL,
            subject_id   NVARCHAR(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            CONSTRAINT pk_altrata_suppressed PRIMARY KEY (connector_id, subject_id)
        );
        IF EXISTS (SELECT 1 FROM sys.columns
                   WHERE object_id = OBJECT_ID(N'dbo.altrata_suppressed')
                     AND name IN (N'subject_id', N'connector_id')
                     AND collation_name <> N'Latin1_General_100_BIN2')
        BEGIN
            ALTER TABLE dbo.altrata_suppressed DROP CONSTRAINT pk_altrata_suppressed;
            ALTER TABLE dbo.altrata_suppressed
                ALTER COLUMN subject_id NVARCHAR(256) COLLATE Latin1_General_100_BIN2 NOT NULL;
            ALTER TABLE dbo.altrata_suppressed
                ALTER COLUMN connector_id NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL;
            ALTER TABLE dbo.altrata_suppressed
                ADD CONSTRAINT pk_altrata_suppressed PRIMARY KEY (connector_id, subject_id);
        END
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

    /// <summary>Latching "this attempt has already durably committed" flag.
    ///
    /// The executor retries the WHOLE operation on a transient fault, which is
    /// correct only while the operation has changed nothing. Once a charging
    /// operation has committed, a transient fault raised afterwards — a
    /// connection reset surfacing during teardown, for instance — must NOT
    /// re-run it: MutateValue's transform would read the already-incremented
    /// value and increment it again, charging the usage ceiling twice for one
    /// lookup. An operation marks itself the moment its change is durable, and
    /// from that point the attempt fails fast instead of replaying.
    ///
    /// AUDIT of every write path in this class, by replay safety. A path needs
    /// the guard exactly when replaying it is not a no-op:
    ///
    ///   NEEDS THE GUARD — relative or caller-supplied writes
    ///     MutateValue               read-modify-write, transform not idempotent
    ///     IncrementBillableLookups  MERGE with a relative (+@delta) update
    ///     MutateDeadLetters         caller transform; increments Attempts
    ///     AddDeadLetter             appends a row; replay duplicates it
    ///     AddDeadLetters            appends a batch; replay duplicates it
    ///     ReplaceDeadLetters        delegates to MutateDeadLetters
    ///
    ///   SAFE UNGUARDED — absolute writes, replaying reproduces the same state
    ///     SaveCheckpoint            MERGE to absolute column values
    ///     ClearCheckpoint           DELETE by key
    ///     SetValue                  MERGE to an absolute value
    ///     MarkDeliveryProcessed     MERGE to an absolute timestamp
    ///     ClearDeadLetters          DELETE by connector
    ///     AddSuppressedSubject      IF NOT EXISTS + INSERT; converges
    ///     RemoveSuppressedSubject   DELETE by key
    ///     WipeAll                   DELETEs by connector
    ///
    /// Keep this audit current when adding a write path.
    ///
    /// THE FENCE MUST NOT MASK THE FAULT IT FENCES. A fenced transactional path
    /// is written `using var txn = conn.BeginTransaction();` with NO catch. Two
    /// paths once wrapped the body in `catch { txn.Rollback(); throw; }`; when
    /// the fault came from Commit() — the exact case this guard reasons about —
    /// Rollback threw InvalidOperationException on the completed transaction and
    /// REPLACED the SqlException, which matches neither handler in
    /// <see cref="Execute"/>, so the retry decision was never made and the
    /// failure was never logged. Dispose already rolls back an uncommitted
    /// transaction and is a no-op on a completed one.
    ///
    /// Tests: RollbackMaskingIlGuardTests is the guard — it asserts on the
    /// COMPILED IL of this class (exhaustive scan of every IL byte offset for a
    /// call to Rollback; zero catch clauses and zero exception filters in each
    /// dead-letter write path's compiled body), so no source formatting of the
    /// defect can evade it. TransactionRollbackMaskingTests demonstrates the
    /// masking MECHANISM against a real ADO.NET provider (Microsoft.Data.Sqlite).
    /// Neither executes this class's write paths: Execute opens a real
    /// SqlConnection and there is no SQL Server on the build host. See
    /// docs/SQL_CONTRACT.md for the exact bound on what is guaranteed.</summary>
    internal sealed class CommitGuard
    {
        public bool Committed { get; private set; }
        public void MarkCommitted() => Committed = true;
    }

    /// <summary>The retry decision, extracted as a pure function so it is
    /// testable without a live SQL Server (SqlException cannot be constructed
    /// by a test).</summary>
    internal static bool ShouldRetry(int attempt, int maxRetries, bool isTransient, bool committed) =>
        !committed && isTransient && attempt < maxRetries;

    internal T Execute<T>(Func<SqlConnection, T> operation) =>
        Execute((conn, _) => operation(conn));

    internal T Execute<T>(Func<SqlConnection, CommitGuard, T> operation)
    {
        lock (_sync)
        {
            var attempt = 0;
            while (true)
            {
                var guard = new CommitGuard();
                try
                {
                    using var connection = new SqlConnection(_connectionString);
                    connection.Open();
                    EnsureSchema(connection);
                    return operation(connection, guard);
                }
                catch (SqlException exc) when (ShouldRetry(attempt, _maxRetries, IsTransient(exc), guard.Committed))
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
            // DATETIME2 has no Kind; GetDateTime returns Unspecified while the
            // file backend returns Utc. Stamp it so the backends agree.
            UpdatedUtc = StateContract.Utc(reader.GetDateTime(4)),
        };
    });

    public void SaveCheckpoint(CrawlCheckpoint raw)
    {
        var checkpoint = StateContract.Storable(raw);
        Execute<object?>(conn =>
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
    }

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
        var raw = GetValue(StateContract.LastSyncKeyPrefix + kind);
        return raw != null && DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when)
            ? StateContract.Utc(when)
            : null;
    }

    public void SetLastSync(string kind, DateTime utc) =>
        SetValue(StateContract.LastSyncKey(kind),
            StateContract.Utc(utc).ToString("o", CultureInfo.InvariantCulture));

    /// <summary>Appends one dead-letter row. Commit-fenced for exactly the
    /// reason MutateValue is: this is a RELATIVE write (it appends; it does not
    /// set a value to an absolute state), it runs in autocommit so it is
    /// durable the instant ExecuteNonQuery returns, and the executor retries
    /// the WHOLE operation on a transient fault. A connection reset surfacing
    /// after that point re-ran the INSERT and put a SECOND copy of the same
    /// failed item in the queue — which inflates the dead-letter count an
    /// operator triages, and, because DeadLetterPolicy retires a record once
    /// its attempts pass the ceiling, lets one failed item consume two of the
    /// queue's bounded slots.
    ///
    /// Residual, identical to the one recorded on MutateValue: if
    /// ExecuteNonQuery ITSELF throws, the outcome is genuinely ambiguous — the
    /// server may have committed the row before the connection dropped — and
    /// no client-side flag can resolve that. Closing it needs a transactional
    /// idempotency key on the row (a caller-supplied dedupe token, unique per
    /// connector, that the INSERT is conditioned on), which is deferred work:
    /// the table's key is a bare IDENTITY today, so adding one is a schema
    /// change with its own migration.</summary>
    public void AddDeadLetter(DeadLetterRecord record)
    {
        var storable = StateContract.Storable(record);   // normalised before any connection is opened
        Execute<object?>((conn, guard) =>
        {
            InsertDeadLetter(conn, null, storable);
            guard.MarkCommitted();   // autocommit: durable now — a retry would duplicate the row
            return null;
        });
    }

    /// <summary>Batch append. The interface's default implementation loops
    /// <see cref="AddDeadLetter"/>, which on this backend is N INDEPENDENT
    /// Execute calls — N lock acquisitions, N autocommit transactions — so a
    /// concurrent writer could interleave rows into the middle of the batch and
    /// a fault partway through left the batch half-applied. IStateStore
    /// documents this API as landing contiguously under one lock acquisition,
    /// so the SQL backend has to override rather than inherit.
    ///
    /// One transaction, one commit, fenced once: the whole batch is a single
    /// relative append, so a post-commit transient fault must not replay it.
    ///
    /// NO explicit Rollback — see <see cref="MutateDeadLetters"/> for why an
    /// explicit one was strictly LESS safe than the `using` it replaced.</summary>
    public void AddDeadLetters(IReadOnlyCollection<DeadLetterRecord> records)
    {
        if (records.Count == 0)
            return;
        var storable = new List<DeadLetterRecord>(records.Count);
        foreach (var record in records)
            storable.Add(StateContract.Storable(record));   // normalised before any I/O
        Execute<object?>((conn, guard) =>
        {
            using var txn = conn.BeginTransaction();
            foreach (var record in storable)
                InsertDeadLetter(conn, txn, record);
            txn.Commit();
            guard.MarkCommitted();   // past this point a retry would duplicate the batch
            return null;
        });
    }

    /// <summary>The dead-letter INSERT, as a constant so a test can PARSE it
    /// under the real SQL Server grammar and check its column list against the
    /// table's — the shape of defect where a record member is persisted by one
    /// backend and silently dropped by the other is only visible by comparing
    /// those two lists.</summary>
    internal const string InsertDeadLetterSql = """
        INSERT INTO dbo.altrata_deadletter
            (connector_id, item_id, dataset, delivery_id, error, op, correlation_id, payload_json,
             failed_utc, attempts, redacted, subject_ids, subject_hashes)
        VALUES (@cid, @i, @ds, @d, @e, @o, @corr, @p, @f, @a, @r, @sids, @shashes);
        """;

    /// <summary>The dead-letter SELECT, likewise parseable. A column written
    /// but never read back is the same defect with the halves swapped.</summary>
    internal const string SelectDeadLetterSql =
        "SELECT item_id, dataset, delivery_id, error, op, correlation_id, payload_json, failed_utc, " +
        "attempts, redacted, subject_ids, subject_hashes " +
        "FROM dbo.altrata_deadletter WHERE connector_id = @cid ORDER BY id";

    private void InsertDeadLetter(SqlConnection conn, SqlTransaction? txn, DeadLetterRecord record)
    {
        using var cmd = new SqlCommand(InsertDeadLetterSql, conn, txn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        cmd.Parameters.AddWithValue("@i", record.ItemId);
        cmd.Parameters.AddWithValue("@ds", record.Dataset);
        cmd.Parameters.AddWithValue("@d", record.DeliveryId);
        cmd.Parameters.AddWithValue("@e", record.Error);
        cmd.Parameters.AddWithValue("@o", record.Op);
        // CorrelationId was persisted by the file backend and SILENTLY DROPPED
        // here — neither bound nor read, and the table had no column for it.
        // CrawlEngine.StampCorrelation stamps every dead letter with the id of
        // the cycle that produced it, and CommandRegistry.DeadLetterIdentityKey
        // feeds it into the identity key retry-failed uses to finalise its
        // snapshot. On SQL that key component collapsed to the empty string for
        // every record, and switching backends lost the trace link outright.
        cmd.Parameters.AddWithValue("@corr", (object?)record.CorrelationId ?? DBNull.Value);
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
        using var cmd = new SqlCommand(SelectDeadLetterSql, conn, txn);
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
                CorrelationId = reader.IsDBNull(5) ? null : reader.GetString(5),
                PayloadJson = reader.GetString(6),
                // DATETIME2 carries no Kind and GetDateTime returns Unspecified;
                // the file backend returns Utc. Stamp it so the two backends
                // return EQUAL DateTimes, not merely equal ticks.
                FailedUtc = StateContract.Utc(reader.GetDateTime(7)),
                Attempts = reader.GetInt32(8),
                Redacted = reader.GetBoolean(9),
                SubjectIds = ParseStringList(reader.GetString(10)),
                SubjectHashes = ParseStringList(reader.GetString(11)),
            });
        }
        return records;
    }

    /// <summary>Replaces the whole queue. Routed through
    /// <see cref="MutateDeadLetters"/> — which does the delete and the reinserts
    /// inside ONE serializable, commit-fenced transaction — rather than the
    /// delete-then-append-in-a-loop it used to be.
    ///
    /// That old shape was the same retry-after-commit defect compounded: it
    /// spanned N+1 INDEPENDENT Execute calls, so it was not atomic at all. A
    /// transient fault partway through left the queue holding a PREFIX of the
    /// replacement — every record after the failure point silently dropped,
    /// having already been deleted — and a fault after any single append
    /// duplicated that record. Since ReplaceDeadLetters is how a drain/requeue
    /// writes the surviving records back, losing the tail loses failed items
    /// outright.
    ///
    /// The sequence is materialised BEFORE the transaction opens: the transform
    /// can be re-run by the executor on a pre-commit transient fault, and a
    /// lazily-evaluated argument would be enumerated a second time (or, if it
    /// is a one-shot sequence, come back empty).</summary>
    public void ReplaceDeadLetters(IEnumerable<DeadLetterRecord> records)
    {
        // Normalised HERE rather than inside the transaction so the rows to be
        // written are decided before a connection is opened — the same point
        // the file backend normalises. Normalisation cannot fail.
        var replacement = new List<DeadLetterRecord>();
        foreach (var record in records)
            replacement.Add(StateContract.Storable(record));
        MutateDeadLetters(_ => replacement);
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
    /// the whole-queue rewrite (the file backend's TOCTOU fix, in SQL terms).
    ///
    /// Commit-fenced like the charging operations: the transform is
    /// caller-supplied and not assumed idempotent — the retry-failed path
    /// increments each record's Attempts — so replaying it after a successful
    /// commit would over-count attempts and retire a record from the queue
    /// earlier than its policy allows.
    ///
    /// NO EXPLICIT ROLLBACK, and that is the point. This method and
    /// <see cref="AddDeadLetters"/> both used to wrap the body in
    /// `catch { txn.Rollback(); throw; }`, citing <see cref="MutateValue"/> as
    /// the model — but MutateValue has no such catch, and the catch made these
    /// paths STRICTLY LESS SAFE than the model they cited:
    ///
    ///   * SqlTransaction.Rollback is documented to throw
    ///     InvalidOperationException when the transaction has already been
    ///     committed, or when the connection is broken.
    ///   * The fault this fence exists to reason about is one raised BY
    ///     txn.Commit(). In exactly that case the catch ran Rollback() on a
    ///     completed-or-broken transaction, Rollback threw, and its
    ///     InvalidOperationException REPLACED the SqlException on the way out.
    ///   * <see cref="Execute"/>'s two handlers both filter on
    ///     `catch (SqlException)`. The masked exception matched NEITHER, so
    ///     ShouldRetry was never consulted — a genuinely UNCOMMITTED batch was
    ///     not retried and the dead letters were LOST rather than duplicated —
    ///     and the "SQL state operation FAILED" diagnostic never fired, so the
    ///     loss was not even attributable to a SQL error number.
    ///
    /// `using var txn` is the correct and sufficient shape: SqlTransaction
    /// .Dispose rolls back an uncommitted transaction and is a documented no-op
    /// on a completed one, so the original exception reaches the executor
    /// intact whether the fault came from the body or from the commit. This is
    /// what MutateValue has always done.</summary>
    public void MutateDeadLetters(
        Func<IReadOnlyList<DeadLetterRecord>, IEnumerable<DeadLetterRecord>> transform) =>
        Execute<object?>((conn, guard) =>
        {
            using var txn = conn.BeginTransaction(System.Data.IsolationLevel.Serializable);
            var updated = transform(ReadDeadLettersCore(conn, txn)).ToList();
            // Normalised inside the transaction because the records come from
            // a caller-supplied transform, not from the snapshot.
            for (var i = 0; i < updated.Count; i++)
                updated[i] = StateContract.Storable(updated[i]);
            DeleteAllDeadLetters(conn, txn);
            foreach (var record in updated)
                InsertDeadLetter(conn, txn, record);
            txn.Commit();
            guard.MarkCommitted();   // past this point a retry would re-apply the transform
            return null;
        });

    public bool IsDeliveryProcessed(string deliveryId) =>
        Execute(conn =>
    {
        using var cmd = new SqlCommand(
            "SELECT COUNT(1) FROM dbo.altrata_deliveries WHERE connector_id = @cid AND delivery_id = @d", conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        cmd.Parameters.AddWithValue("@d", deliveryId);
        return (int)cmd.ExecuteScalar()! > 0;
    });

    public void MarkDeliveryProcessed(string rawDeliveryId, DateTime rawUtc)
    {
        var deliveryId = rawDeliveryId;
        var utc = StateContract.Utc(rawUtc);
        Execute<object?>(conn =>
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
    }

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

    public string? GetValue(string key)
    {
        return Execute(conn =>
    {
        using var cmd = new SqlCommand(
            "SELECT [value] FROM dbo.altrata_kv WHERE connector_id = @cid AND [key] = @k", conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        cmd.Parameters.AddWithValue("@k", key);
        var result = cmd.ExecuteScalar();
        return result == null || result is DBNull ? null : (string)result;
    });
    }

    public void SetValue(string rawKey, string? rawValue)
    {
        var key = rawKey;
        var value = rawValue == null ? null : StateContract.Text(rawValue);
        Execute<object?>(conn =>
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
    }

    /// <summary>Atomic read-modify-write of one KV row, ACROSS NODES: the SELECT
    /// takes UPDLOCK+HOLDLOCK inside a transaction, so a second node's
    /// reservation blocks until this one commits. HOLDLOCK also range-locks the
    /// not-yet-existing key, so the first-ever write cannot race either. This is
    /// what makes the usage ceiling (Altrata/UsageBudget.cs) genuinely fleet-wide
    /// on the SQL backend rather than per-host as it is on the file backend.
    ///
    /// The transform is NOT idempotent (the usage meter's is a read-increment),
    /// so the commit is fenced with <see cref="CommitGuard"/>: once tx.Commit()
    /// returns, a later transient fault fails fast rather than replaying the
    /// transform against the value it just wrote. Note the residual limit —
    /// if Commit() ITSELF throws, the outcome is genuinely ambiguous (the
    /// server may have committed before the connection dropped) and no
    /// client-side flag can resolve it; that needs a transactional idempotency
    /// key, recorded as deferred work.</summary>
    public string? MutateValue(string rawKey, Func<string?, string?> transform)
    {
        var key = rawKey;
        return Execute((conn, guard) =>
    {
        using var tx = conn.BeginTransaction();
        string? current;
        using (var read = new SqlCommand(
            "SELECT [value] FROM dbo.altrata_kv WITH (UPDLOCK, HOLDLOCK) " +
            "WHERE connector_id = @cid AND [key] = @k", conn, tx))
        {
            read.Parameters.AddWithValue("@cid", _connectorId);
            read.Parameters.AddWithValue("@k", key);
            var result = read.ExecuteScalar();
            current = result == null || result is DBNull ? null : (string)result;
        }

        var updated = transform(current);
        if (updated != null)
            updated = StateContract.Text(updated);   // same normalisation the file backend applies

        if (updated == null)
        {
            using var delete = new SqlCommand(
                "DELETE FROM dbo.altrata_kv WHERE connector_id = @cid AND [key] = @k", conn, tx);
            delete.Parameters.AddWithValue("@cid", _connectorId);
            delete.Parameters.AddWithValue("@k", key);
            delete.ExecuteNonQuery();
        }
        else
        {
            using var write = new SqlCommand("""
                MERGE dbo.altrata_kv WITH (HOLDLOCK) AS target
                USING (SELECT @cid AS connector_id, @k AS [key]) AS source
                ON target.connector_id = source.connector_id AND target.[key] = source.[key]
                WHEN MATCHED THEN UPDATE SET [value] = @v
                WHEN NOT MATCHED THEN INSERT (connector_id, [key], [value]) VALUES (@cid, @k, @v);
                """, conn, tx);
            write.Parameters.AddWithValue("@cid", _connectorId);
            write.Parameters.AddWithValue("@k", key);
            write.Parameters.AddWithValue("@v", updated);
            write.ExecuteNonQuery();
        }

        tx.Commit();
        guard.MarkCommitted();   // past this point a retry would double-charge
        return updated;
    });
    }

    public long GetBillableLookupCount()
    {
        var raw = GetValue(StateKeys.BillableLookups);
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    /// <summary>Charges the billable counter. The MERGE is a relative
    /// (+@delta) update, so replaying it double-counts; it runs in autocommit,
    /// so it is durable the moment ExecuteScalar returns. Fenced with
    /// <see cref="CommitGuard"/> for the same reason as MutateValue.</summary>
    public long IncrementBillableLookups(long delta = 1) => Execute((conn, guard) =>
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
        guard.MarkCommitted();   // past this point a retry would double-charge
        return long.TryParse(result as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    });

    // ---- suppression list (erasure durability) --------------------------------
    //
    // Every subject_id comparison below pins the comparison collation
    // EXPLICITLY on the parameter side (`= @s COLLATE ...`) rather than relying
    // on the column picking it up from the schema. Two reasons:
    //
    //   * it is the comparison, not the storage, that decides whether an erased
    //     subject counts as erased — so the ordinal semantics belong where the
    //     comparison is written, where a reviewer can see them;
    //   * it is correct even against a table provisioned by an older script (or
    //     by hand) whose column is still database-default. Such a table then
    //     costs an index scan instead of a seek, which is the right trade for a
    //     list of this size and is transient anyway: EnsureSchema runs the
    //     collation migration before any of these commands can execute.
    //
    // Pinning on the PARAMETER keeps the column reference sargable, so once the
    // column is BIN2 — comparison collation and index collation agreeing — the
    // primary key is still seekable.

    public void AddSuppressedSubject(string rawSubjectId)
    {
        var subjectId = rawSubjectId;
        Execute<object?>(conn =>
    {
        using var cmd = new SqlCommand($"""
            IF NOT EXISTS (SELECT 1 FROM dbo.altrata_suppressed
                           WHERE connector_id = @cid AND subject_id = @s COLLATE {SubjectIdCollation})
                INSERT INTO dbo.altrata_suppressed (connector_id, subject_id) VALUES (@cid, @s);
            """, conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        cmd.Parameters.AddWithValue("@s", subjectId);
        cmd.ExecuteNonQuery();
        return null;
    });
    }

    public void RemoveSuppressedSubject(string rawSubjectId)
    {
        var subjectId = rawSubjectId;
        Execute<object?>(conn =>
    {
        using var cmd = new SqlCommand(
            "DELETE FROM dbo.altrata_suppressed " +
            $"WHERE connector_id = @cid AND subject_id = @s COLLATE {SubjectIdCollation}", conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        cmd.Parameters.AddWithValue("@s", subjectId);
        cmd.ExecuteNonQuery();
        return null;
    });
    }

    public bool IsSubjectSuppressed(string subjectId) =>
        Execute(conn =>
    {
        using var cmd = new SqlCommand(
            "SELECT COUNT(1) FROM dbo.altrata_suppressed " +
            $"WHERE connector_id = @cid AND subject_id = @s COLLATE {SubjectIdCollation}", conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        cmd.Parameters.AddWithValue("@s", subjectId);
        return (int)cmd.ExecuteScalar()! > 0;
    });

    /// <summary>Ordered ORDINALLY, to match the file backend exactly.
    ///
    /// The server-side ORDER BY under BIN2 sorts by CODE POINT; the file
    /// backend's SortedSet(StringComparer.Ordinal) sorts by UTF-16 CODE UNIT.
    /// Those agree for the whole BMP and disagree only where a supplementary
    /// character (surrogate pair, U+10000 and above) sorts against a BMP
    /// character in U+E000..U+FFFF. Equality is identical either way — this is
    /// purely about ORDER — but ListSuppressedSubjects output is what an
    /// operator diffs between two nodes during a DR comparison, so a re-sort in
    /// the client makes the two backends byte-for-byte comparable rather than
    /// nearly so. The SQL ORDER BY is kept so the wire order is deterministic
    /// before the re-sort.</summary>
    public IReadOnlyList<string> ListSuppressedSubjects() => Execute(conn =>
    {
        var ids = new List<string>();
        using var cmd = new SqlCommand(
            "SELECT subject_id FROM dbo.altrata_suppressed WHERE connector_id = @cid ORDER BY subject_id", conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        ids.Sort(StringComparer.Ordinal);
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
