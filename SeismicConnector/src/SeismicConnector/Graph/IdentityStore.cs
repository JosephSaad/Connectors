// Graph/IdentityStore.cs
// ----------------------
// Persistent connector state that is NOT crawl-progress: principal mappings
// (Seismic user/group → Entra ID object id) and the tracked-item table that
// powers version-supersede, unpublish-withdraw, expiry-withdraw and late-flag
// withdrawal.
//
// Default backend: SQLite at data/{CONNECTOR_ID}_identity.db.
// USE_SQL_SERVER=true routes the same interface to SQL Server
// (Graph/SqlServerIdentityStore.cs) so every node in an HA deployment shares
// one store — see docs/SQL_CONTRACT.md.

using Microsoft.Data.Sqlite;
using SeismicConnector.Seismic;

namespace SeismicConnector.Graph;

/// <summary>A Seismic principal and its (optional) Entra mapping.</summary>
public sealed record PrincipalMapping(
    string SeismicId,
    string PrincipalType,       // "user" | "group"
    string? Email,
    string? EntraObjectId,
    string? DisplayName);

/// <summary>One externalItem the connector has ingested (or excluded).</summary>
public sealed record TrackedItem(
    string ItemId,
    string VersionId,
    string TeamsiteId,
    DateTime? ExpiresAtUtc,
    DateTime LastSeenUtc,
    string Status)              // "ingested" | "excluded"
{
    /// <summary>
    /// Fingerprint of the resolved ACL principal set at last (re-)ACL — powers
    /// permission-change detection (PERMISSION_REACL / the reacl command).
    /// Null for items tracked before re-ACL support existed; treated as
    /// "unknown", which forces one re-resolve on the next pass.
    /// </summary>
    public string? AclFingerprint { get; init; }
}

public interface IIdentityStore : IDisposable
{
    // Principals
    void UpsertPrincipal(PrincipalMapping mapping);
    PrincipalMapping? GetPrincipal(string seismicId);
    string? GetEntraObjectId(string seismicId);
    IReadOnlyList<PrincipalMapping> GetAllPrincipals();
    int CountMappedPrincipals();

    // Tracked items
    void UpsertTrackedItem(TrackedItem item);
    TrackedItem? GetTrackedItem(string itemId);
    IReadOnlyList<TrackedItem> GetAllTrackedItems();
    IReadOnlyList<TrackedItem> GetExpiredItems(DateTime nowUtc);
    IReadOnlyList<TrackedItem> GetItemsNotSeenSince(DateTime crawlStartUtc);
    void RemoveTrackedItem(string itemId);
}

public static class IdentityStoreFactory
{
    /// <summary>Data directory for SQLite stores. Settable so tests can redirect.</summary>
    public static string DataDir { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "data");

    /// <summary>SQLite by default; SQL Server when USE_SQL_SERVER=true + SQL_CONNECTION_STRING.</summary>
    public static IIdentityStore Open(string connectorId)
    {
        if (Settings.BoolEnv("USE_SQL_SERVER")
            && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING")))
        {
            return new SqlServerIdentityStore(connectorId);
        }
        Directory.CreateDirectory(DataDir);
        return new SqliteIdentityStore(Path.Combine(DataDir, $"{connectorId}_identity.db"));
    }
}

public sealed class SqliteIdentityStore : IIdentityStore
{
    private readonly SqliteConnection _connection;

    public SqliteIdentityStore(string dbPath)
    {
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS principals (
                seismic_id     TEXT PRIMARY KEY,
                principal_type TEXT NOT NULL,
                email          TEXT,
                entra_id       TEXT,
                display_name   TEXT,
                synced_at      TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS tracked_items (
                item_id         TEXT PRIMARY KEY,
                version_id      TEXT NOT NULL,
                teamsite_id     TEXT NOT NULL,
                expires_at      TEXT,
                last_seen       TEXT NOT NULL,
                status          TEXT NOT NULL,
                acl_fingerprint TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_tracked_items_teamsite ON tracked_items(teamsite_id);
            """;
        cmd.ExecuteNonQuery();
        MigrateAclFingerprintColumn();
    }

    /// <summary>Add the acl_fingerprint column to tracked_items opened from an older DB.</summary>
    private void MigrateAclFingerprintColumn()
    {
        bool hasColumn;
        using (var probe = _connection.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('tracked_items') WHERE name = 'acl_fingerprint'";
            hasColumn = Convert.ToInt64(probe.ExecuteScalar()) > 0;
        }
        if (hasColumn)
            return;
        using var alter = _connection.CreateCommand();
        alter.CommandText = "ALTER TABLE tracked_items ADD COLUMN acl_fingerprint TEXT";
        alter.ExecuteNonQuery();
    }

    // ── principals ───────────────────────────────────────────────────────────

    public void UpsertPrincipal(PrincipalMapping mapping)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO principals (seismic_id, principal_type, email, entra_id, display_name, synced_at)
            VALUES ($id, $type, $email, $entra, $name, $ts)
            ON CONFLICT(seismic_id) DO UPDATE SET
                principal_type = excluded.principal_type,
                email          = excluded.email,
                entra_id       = excluded.entra_id,
                display_name   = excluded.display_name,
                synced_at      = excluded.synced_at;
            """;
        cmd.Parameters.AddWithValue("$id", mapping.SeismicId);
        cmd.Parameters.AddWithValue("$type", mapping.PrincipalType);
        cmd.Parameters.AddWithValue("$email", (object?)mapping.Email ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$entra", (object?)mapping.EntraObjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", (object?)mapping.DisplayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public PrincipalMapping? GetPrincipal(string seismicId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT seismic_id, principal_type, email, entra_id, display_name FROM principals WHERE seismic_id = $id";
        cmd.Parameters.AddWithValue("$id", seismicId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadPrincipal(reader) : null;
    }

    public string? GetEntraObjectId(string seismicId) => GetPrincipal(seismicId)?.EntraObjectId;

    public IReadOnlyList<PrincipalMapping> GetAllPrincipals()
    {
        var results = new List<PrincipalMapping>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT seismic_id, principal_type, email, entra_id, display_name FROM principals ORDER BY seismic_id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadPrincipal(reader));
        return results;
    }

    public int CountMappedPrincipals()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM principals WHERE entra_id IS NOT NULL";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static PrincipalMapping ReadPrincipal(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4));

    // ── tracked items ────────────────────────────────────────────────────────

    public void UpsertTrackedItem(TrackedItem item)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tracked_items (item_id, version_id, teamsite_id, expires_at, last_seen, status, acl_fingerprint)
            VALUES ($id, $version, $teamsite, $expires, $seen, $status, $acl)
            ON CONFLICT(item_id) DO UPDATE SET
                version_id      = excluded.version_id,
                teamsite_id     = excluded.teamsite_id,
                expires_at      = excluded.expires_at,
                last_seen       = excluded.last_seen,
                status          = excluded.status,
                acl_fingerprint = excluded.acl_fingerprint;
            """;
        cmd.Parameters.AddWithValue("$id", item.ItemId);
        cmd.Parameters.AddWithValue("$version", item.VersionId);
        cmd.Parameters.AddWithValue("$teamsite", item.TeamsiteId);
        cmd.Parameters.AddWithValue("$expires", (object?)item.ExpiresAtUtc?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$seen", item.LastSeenUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$status", item.Status);
        cmd.Parameters.AddWithValue("$acl", (object?)item.AclFingerprint ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public TrackedItem? GetTrackedItem(string itemId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = SelectTracked + " WHERE item_id = $id";
        cmd.Parameters.AddWithValue("$id", itemId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadTracked(reader) : null;
    }

    public IReadOnlyList<TrackedItem> GetAllTrackedItems() => QueryTracked(SelectTracked, null);

    public IReadOnlyList<TrackedItem> GetExpiredItems(DateTime nowUtc) =>
        QueryTracked(
            SelectTracked + " WHERE expires_at IS NOT NULL AND expires_at <= $now AND status = 'ingested'",
            cmd => cmd.Parameters.AddWithValue("$now", nowUtc.ToString("o")));

    public IReadOnlyList<TrackedItem> GetItemsNotSeenSince(DateTime crawlStartUtc) =>
        QueryTracked(
            SelectTracked + " WHERE last_seen < $start AND status = 'ingested'",
            cmd => cmd.Parameters.AddWithValue("$start", crawlStartUtc.ToString("o")));

    public void RemoveTrackedItem(string itemId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM tracked_items WHERE item_id = $id";
        cmd.Parameters.AddWithValue("$id", itemId);
        cmd.ExecuteNonQuery();
    }

    private const string SelectTracked =
        "SELECT item_id, version_id, teamsite_id, expires_at, last_seen, status, acl_fingerprint FROM tracked_items";

    private IReadOnlyList<TrackedItem> QueryTracked(string sql, Action<SqliteCommand>? bind)
    {
        var results = new List<TrackedItem>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadTracked(reader));
        return results;
    }

    private static TrackedItem ReadTracked(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3), null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
        DateTime.Parse(reader.GetString(4), null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
        reader.GetString(5))
    {
        AclFingerprint = reader.IsDBNull(6) ? null : reader.GetString(6),
    };

    public void Dispose() => _connection.Dispose();
}
