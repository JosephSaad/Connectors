// Graph/IdentityStore.cs
// ----------------------
// SQLite-backed identity store (the default backend). One file per connector:
// data/{CONNECTOR_ID}_identity.db, table `principals`.

using ClarizenConnector.Infrastructure;
using Microsoft.Data.Sqlite;

namespace ClarizenConnector.Graph;

public sealed class IdentityStore : IIdentityStore
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector.identity");

    private readonly SqliteConnection _connection;

    /// <summary>Data directory; settable so tests can redirect.</summary>
    public static string DataDir { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "data");

    public static string DbPath(string connectorId) =>
        Path.Combine(DataDir, $"{connectorId}_identity.db");

    /// <summary>Open (creating the file/table when needed) the store for <paramref name="connectorId"/>.</summary>
    public IdentityStore(string connectorId, string? dbPath = null)
    {
        var path = dbPath ?? DbPath(connectorId);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connection = new SqliteConnection($"Data Source={path}");
        try
        {
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS principals (
                    clarizen_id    TEXT PRIMARY KEY,
                    principal_type TEXT NOT NULL,
                    email          TEXT,
                    entra_id       TEXT,
                    updated_utc    TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_principals_email ON principals(email);
                """;
            command.ExecuteNonQuery();
        }
        catch (Exception exc)
        {
            // Log-and-rethrow: name the db file (SqliteException does not).
            Logger.Error($"Failed to open identity store database '{path}'", exc);
            _connection.Dispose();
            throw;
        }
        Logger.Debug($"Identity store opened: {path}");
    }

    public void Upsert(PrincipalMapping mapping)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO principals (clarizen_id, principal_type, email, entra_id, updated_utc)
            VALUES ($id, $type, $email, $entra, $updated)
            ON CONFLICT(clarizen_id) DO UPDATE SET
                principal_type = excluded.principal_type,
                email          = excluded.email,
                entra_id       = excluded.entra_id,
                updated_utc    = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$id", mapping.ClarizenId);
        command.Parameters.AddWithValue("$type", mapping.PrincipalType);
        command.Parameters.AddWithValue("$email", (object?)mapping.Email ?? DBNull.Value);
        command.Parameters.AddWithValue("$entra", (object?)mapping.EntraId ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", mapping.UpdatedUtc.ToString("o"));
        command.ExecuteNonQuery();
    }

    public PrincipalMapping? Find(string clarizenId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT clarizen_id, principal_type, email, entra_id, updated_utc "
            + "FROM principals WHERE clarizen_id = $id;";
        command.Parameters.AddWithValue("$id", clarizenId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public List<PrincipalMapping> All()
    {
        var result = new List<PrincipalMapping>();
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT clarizen_id, principal_type, email, entra_id, updated_utc "
            + "FROM principals ORDER BY clarizen_id;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(Map(reader));
        return result;
    }

    public int ResolvedCount()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM principals WHERE entra_id IS NOT NULL AND entra_id <> '';";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public int Count()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM principals;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void Clear()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM principals;";
        command.ExecuteNonQuery();
    }

    private static PrincipalMapping Map(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind));

    public void Dispose() => _connection.Dispose();

    /// <summary>Open the configured identity store backend (SQLite or SQL Server).</summary>
    public static IIdentityStore Open(string connectorId) =>
        EnvFlags.UseSqlServer
            ? new SqlServerIdentityStore(connectorId)
            : new IdentityStore(connectorId);
}
