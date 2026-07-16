// Graph/SqlServerIdentityStore.cs
// -------------------------------
// SQL Server implementation of IIdentityStore (USE_SQL_SERVER=true). All
// nodes in an HA deployment share these tables; rows are scoped by
// ConnectorId. Transient-fault retry is inherited from SqlExecutor.

using Microsoft.Data.SqlClient;
using SeismicConnector.Infrastructure;

namespace SeismicConnector.Graph;

public sealed class SqlServerIdentityStore : IIdentityStore
{
    private readonly string _connectorId;

    public SqlServerIdentityStore(string connectorId)
    {
        _connectorId = connectorId;
    }

    public void UpsertPrincipal(PrincipalMapping mapping) =>
        SqlExecutor.Execute(connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                MERGE dbo.Principals AS target
                USING (SELECT @ConnectorId AS ConnectorId, @SeismicId AS SeismicId) AS source
                    ON target.ConnectorId = source.ConnectorId AND target.SeismicId = source.SeismicId
                WHEN MATCHED THEN UPDATE SET
                    PrincipalType = @Type, Email = @Email, EntraId = @EntraId,
                    DisplayName = @Name, SyncedUtc = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN INSERT
                    (ConnectorId, SeismicId, PrincipalType, Email, EntraId, DisplayName, SyncedUtc)
                    VALUES (@ConnectorId, @SeismicId, @Type, @Email, @EntraId, @Name, SYSUTCDATETIME());
                """;
            cmd.Parameters.AddWithValue("@ConnectorId", _connectorId);
            cmd.Parameters.AddWithValue("@SeismicId", mapping.SeismicId);
            cmd.Parameters.AddWithValue("@Type", mapping.PrincipalType);
            cmd.Parameters.AddWithValue("@Email", (object?)mapping.Email ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@EntraId", (object?)mapping.EntraObjectId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Name", (object?)mapping.DisplayName ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        });

    public PrincipalMapping? GetPrincipal(string seismicId) =>
        SqlExecutor.Execute(connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT SeismicId, PrincipalType, Email, EntraId, DisplayName
                FROM dbo.Principals WHERE ConnectorId = @ConnectorId AND SeismicId = @SeismicId
                """;
            cmd.Parameters.AddWithValue("@ConnectorId", _connectorId);
            cmd.Parameters.AddWithValue("@SeismicId", seismicId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadPrincipal(reader) : null;
        });

    public string? GetEntraObjectId(string seismicId) => GetPrincipal(seismicId)?.EntraObjectId;

    public IReadOnlyList<PrincipalMapping> GetAllPrincipals() =>
        SqlExecutor.Execute(connection =>
        {
            var results = new List<PrincipalMapping>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT SeismicId, PrincipalType, Email, EntraId, DisplayName
                FROM dbo.Principals WHERE ConnectorId = @ConnectorId ORDER BY SeismicId
                """;
            cmd.Parameters.AddWithValue("@ConnectorId", _connectorId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(ReadPrincipal(reader));
            return (IReadOnlyList<PrincipalMapping>)results;
        });

    public int CountMappedPrincipals() =>
        SqlExecutor.Execute(connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM dbo.Principals WHERE ConnectorId = @ConnectorId AND EntraId IS NOT NULL";
            cmd.Parameters.AddWithValue("@ConnectorId", _connectorId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        });

    private static PrincipalMapping ReadPrincipal(SqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4));

    public void UpsertTrackedItem(TrackedItem item) =>
        SqlExecutor.Execute(connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                MERGE dbo.TrackedItems AS target
                USING (SELECT @ConnectorId AS ConnectorId, @ItemId AS ItemId) AS source
                    ON target.ConnectorId = source.ConnectorId AND target.ItemId = source.ItemId
                WHEN MATCHED THEN UPDATE SET
                    VersionId = @VersionId, TeamsiteId = @TeamsiteId, ExpiresUtc = @ExpiresUtc,
                    LastSeenUtc = @LastSeenUtc, Status = @Status, AclFingerprint = @AclFingerprint
                WHEN NOT MATCHED THEN INSERT
                    (ConnectorId, ItemId, VersionId, TeamsiteId, ExpiresUtc, LastSeenUtc, Status, AclFingerprint)
                    VALUES (@ConnectorId, @ItemId, @VersionId, @TeamsiteId, @ExpiresUtc, @LastSeenUtc, @Status, @AclFingerprint);
                """;
            cmd.Parameters.AddWithValue("@ConnectorId", _connectorId);
            cmd.Parameters.AddWithValue("@ItemId", item.ItemId);
            cmd.Parameters.AddWithValue("@VersionId", item.VersionId);
            cmd.Parameters.AddWithValue("@TeamsiteId", item.TeamsiteId);
            cmd.Parameters.AddWithValue("@ExpiresUtc", (object?)item.ExpiresAtUtc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@LastSeenUtc", item.LastSeenUtc);
            cmd.Parameters.AddWithValue("@Status", item.Status);
            cmd.Parameters.AddWithValue("@AclFingerprint", (object?)item.AclFingerprint ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        });

    public TrackedItem? GetTrackedItem(string itemId) =>
        SqlExecutor.Execute(connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = SelectTracked + " AND ItemId = @ItemId";
            cmd.Parameters.AddWithValue("@ConnectorId", _connectorId);
            cmd.Parameters.AddWithValue("@ItemId", itemId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadTracked(reader) : null;
        });

    public IReadOnlyList<TrackedItem> GetAllTrackedItems() =>
        QueryTracked(SelectTracked, cmd => { });

    public IReadOnlyList<TrackedItem> GetExpiredItems(DateTime nowUtc) =>
        QueryTracked(
            SelectTracked + " AND ExpiresUtc IS NOT NULL AND ExpiresUtc <= @Now AND Status = 'ingested'",
            cmd => cmd.Parameters.AddWithValue("@Now", nowUtc));

    public IReadOnlyList<TrackedItem> GetItemsNotSeenSince(DateTime crawlStartUtc) =>
        QueryTracked(
            SelectTracked + " AND LastSeenUtc < @Start AND Status = 'ingested'",
            cmd => cmd.Parameters.AddWithValue("@Start", crawlStartUtc));

    public void RemoveTrackedItem(string itemId) =>
        SqlExecutor.Execute(connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "DELETE FROM dbo.TrackedItems WHERE ConnectorId = @ConnectorId AND ItemId = @ItemId";
            cmd.Parameters.AddWithValue("@ConnectorId", _connectorId);
            cmd.Parameters.AddWithValue("@ItemId", itemId);
            cmd.ExecuteNonQuery();
        });

    private const string SelectTracked = """
        SELECT ItemId, VersionId, TeamsiteId, ExpiresUtc, LastSeenUtc, Status, AclFingerprint
        FROM dbo.TrackedItems WHERE ConnectorId = @ConnectorId
        """;

    private IReadOnlyList<TrackedItem> QueryTracked(string sql, Action<SqlCommand> bind) =>
        SqlExecutor.Execute(connection =>
        {
            var results = new List<TrackedItem>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@ConnectorId", _connectorId);
            bind(cmd);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(ReadTracked(reader));
            return (IReadOnlyList<TrackedItem>)results;
        });

    private static TrackedItem ReadTracked(SqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
        DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
        reader.GetString(5))
    {
        AclFingerprint = reader.IsDBNull(6) ? null : reader.GetString(6),
    };

    public void Dispose()
    {
    }
}
