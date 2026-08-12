// Infrastructure/SqlExecutor.cs
// -----------------------------
// Shared SQL Server access layer for the optional SQL state backend
// (docs/SQL_CONTRACT.md).
//
// * Connection string from SQL_CONNECTION_STRING; Encrypt=True is forced
//   unless the string already sets Encrypt.
// * SQL_USE_MANAGED_IDENTITY=true → an Entra access token
//   (https://database.windows.net/.default via DefaultAzureCredential) is
//   attached instead of connection-string credentials.
// * Transient faults (deadlock 1205, throttling 49918/49919/49920, AG failover
//   40613/40197/40501/4060, timeout -2, broken connection 10054/10053/233/64)
//   are retried up to SQL_MAX_RETRIES with exponential backoff.
// * First use provisions the schema (tables created IF NOT EXISTS-style).

using Azure.Core;
using Azure.Identity;
using Microsoft.Data.SqlClient;

namespace Connector.Chassis;

public static class SqlExecutor
{
    private static readonly IAppLogger Logger = Chassis.GetLogger($"{Chassis.Identity.BaseLoggerName}.sql");

    private static readonly int[] TransientErrorNumbers =
    {
        1205, 49918, 49919, 49920, 40613, 40197, 40501, 4060, 10928, 10929, -2, 10054, 10053, 233, 64,
    };

    private static readonly object SchemaLock = new();
    private static bool _schemaEnsured;

    /// <summary>Test seam: replaces the schema-provisioning guard between tests.</summary>
    internal static void ResetForTests()
    {
        lock (SchemaLock)
        {
            _schemaEnsured = false;
        }
    }

    public static string ConnectionString
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING")
                ?? throw new InvalidOperationException("Invalid configuration: Missing SQL_CONNECTION_STRING");
            return EnsureEncrypt(raw);
        }
    }

    /// <summary>Force Encrypt=True unless the connection string already sets Encrypt.</summary>
    internal static string EnsureEncrypt(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!connectionString.Contains("Encrypt", StringComparison.OrdinalIgnoreCase))
            builder.Encrypt = true;
        return builder.ConnectionString;
    }

    private static int MaxRetries =>
        int.TryParse(Environment.GetEnvironmentVariable("SQL_MAX_RETRIES"), out var parsed) && parsed >= 0
            ? parsed
            : 5;

    private static bool UseManagedIdentity =>
        EnvFlags.IsTrue("SQL_USE_MANAGED_IDENTITY");

    public static SqlConnection OpenConnection()
    {
        var connection = new SqlConnection(ConnectionString);
        if (UseManagedIdentity)
        {
            var token = new DefaultAzureCredential().GetToken(
                new TokenRequestContext(new[] { "https://database.windows.net/.default" }));
            connection.AccessToken = token.Token;
        }
        connection.Open();
        return connection;
    }

    /// <summary>Run <paramref name="operation"/> with transient-fault retry.</summary>
    public static T Execute<T>(Func<SqlConnection, T> operation)
    {
        EnsureSchema();
        return ExecuteCore(operation);
    }

    public static void Execute(Action<SqlConnection> operation) =>
        Execute<object?>(connection =>
        {
            operation(connection);
            return null;
        });

    private static T ExecuteCore<T>(Func<SqlConnection, T> operation)
    {
        var maxRetries = MaxRetries;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var connection = OpenConnection();
                return operation(connection);
            }
            catch (SqlException ex) when (attempt < maxRetries && IsTransient(ex))
            {
                var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 30));
                Logger.Warning(
                    $"Transient SQL error {ex.Number} — retrying in {delay.TotalSeconds:F0}s "
                    + $"(attempt {attempt + 1}/{maxRetries})");
                Thread.Sleep(delay);
            }
        }
    }

    internal static bool IsTransient(SqlException ex) =>
        ex.Errors.Cast<SqlError>().Any(e => TransientErrorNumbers.Contains(e.Number));

    // ── schema provisioning ──────────────────────────────────────────────────

    internal const string SchemaDdl = """
        IF OBJECT_ID('dbo.SyncTimestamps', 'U') IS NULL
            CREATE TABLE dbo.SyncTimestamps (
                ConnectorId NVARCHAR(64) NOT NULL PRIMARY KEY,
                LastSyncUtc DATETIME2 NOT NULL);
        IF OBJECT_ID('dbo.Checkpoints', 'U') IS NULL
            CREATE TABLE dbo.Checkpoints (
                ConnectorId NVARCHAR(64) NOT NULL,
                ObjectType  NVARCHAR(128) NOT NULL,
                SinceIso    NVARCHAR(64) NULL,
                ChunkIndex  INT NOT NULL,
                UpdatedUtc  DATETIME2 NOT NULL,
                CONSTRAINT PK_Checkpoints PRIMARY KEY (ConnectorId, ObjectType));
        IF OBJECT_ID('dbo.DeadLetter', 'U') IS NULL
            CREATE TABLE dbo.DeadLetter (
                Id            BIGINT IDENTITY PRIMARY KEY,
                ConnectorId   NVARCHAR(64) NOT NULL,
                ItemId        NVARCHAR(256) NOT NULL,
                ObjectType    NVARCHAR(128) NOT NULL,
                Error         NVARCHAR(MAX) NULL,
                RequestBody   NVARCHAR(MAX) NULL,
                ResponseBody  NVARCHAR(MAX) NULL,
                CorrelationId NVARCHAR(64) NULL,
                CreatedUtc    DATETIME2 NOT NULL);
        IF OBJECT_ID('dbo.DeadLetter', 'U') IS NOT NULL
            AND COL_LENGTH('dbo.DeadLetter', 'CorrelationId') IS NULL
            ALTER TABLE dbo.DeadLetter ADD CorrelationId NVARCHAR(64) NULL;
        IF OBJECT_ID('dbo.Principals', 'U') IS NULL
            CREATE TABLE dbo.Principals (
                ConnectorId   NVARCHAR(64) NOT NULL,
                SeismicId     NVARCHAR(128) NOT NULL,
                PrincipalType NVARCHAR(16) NOT NULL,
                Email         NVARCHAR(320) NULL,
                EntraId       NVARCHAR(64) NULL,
                DisplayName   NVARCHAR(256) NULL,
                SyncedUtc     DATETIME2 NOT NULL,
                CONSTRAINT PK_Principals PRIMARY KEY (ConnectorId, SeismicId));
        IF OBJECT_ID('dbo.TrackedItems', 'U') IS NULL
            CREATE TABLE dbo.TrackedItems (
                ConnectorId    NVARCHAR(64) NOT NULL,
                ItemId         NVARCHAR(256) NOT NULL,
                VersionId      NVARCHAR(128) NOT NULL,
                TeamsiteId     NVARCHAR(128) NOT NULL,
                ExpiresUtc     DATETIME2 NULL,
                LastSeenUtc    DATETIME2 NOT NULL,
                Status         NVARCHAR(16) NOT NULL,
                AclFingerprint NVARCHAR(128) NULL,
                ClassificationLocked BIT NOT NULL DEFAULT 0,
                CONSTRAINT PK_TrackedItems PRIMARY KEY (ConnectorId, ItemId));
        IF OBJECT_ID('dbo.TrackedItems', 'U') IS NOT NULL
            AND COL_LENGTH('dbo.TrackedItems', 'AclFingerprint') IS NULL
            ALTER TABLE dbo.TrackedItems ADD AclFingerprint NVARCHAR(128) NULL;
        IF OBJECT_ID('dbo.TrackedItems', 'U') IS NOT NULL
            AND COL_LENGTH('dbo.TrackedItems', 'ClassificationLocked') IS NULL
            ALTER TABLE dbo.TrackedItems ADD ClassificationLocked BIT NOT NULL DEFAULT 0;
        IF OBJECT_ID('dbo.CrawlSessions', 'U') IS NULL
            CREATE TABLE dbo.CrawlSessions (
                CrawlId     UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                ConnectorId NVARCHAR(64) NOT NULL,
                CrawlKind   NVARCHAR(16) NOT NULL,
                SinceIso    NVARCHAR(64) NULL,
                Status      NVARCHAR(16) NOT NULL,
                OpenedUtc   DATETIME2 NOT NULL,
                OpenedBy    NVARCHAR(128) NOT NULL,
                ClosedUtc   DATETIME2 NULL,
                ClosedBy    NVARCHAR(128) NULL);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CrawlSessions_Open')
            CREATE UNIQUE INDEX UX_CrawlSessions_Open
                ON dbo.CrawlSessions(ConnectorId)
                WHERE Status = 'open';
        IF OBJECT_ID('dbo.CrawlClaims', 'U') IS NULL
            CREATE TABLE dbo.CrawlClaims (
                CrawlId      UNIQUEIDENTIFIER NOT NULL,
                ResourceKey  NVARCHAR(256) NOT NULL,
                NodeId       NVARCHAR(128) NOT NULL,
                Status       NVARCHAR(16) NOT NULL,
                ClaimedUtc   DATETIME2 NOT NULL,
                HeartbeatUtc DATETIME2 NOT NULL,
                CONSTRAINT PK_CrawlClaims PRIMARY KEY (CrawlId, ResourceKey));
        """;

    private static void EnsureSchema()
    {
        lock (SchemaLock)
        {
            if (_schemaEnsured)
                return;
            ExecuteCore<object?>(connection =>
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = SchemaDdl;
                cmd.ExecuteNonQuery();
                return null;
            });
            _schemaEnsured = true;
        }
    }
}
