// Graph/SqlServerIdentityStore.cs
// -------------------------------
// SQL Server identity store (USE_SQL_SERVER=true): dbo.Principals, keyed on
// (ConnectorId, SourceId). See scripts/sql/create-database.sql and
// docs/SQL_CONTRACT.md. Gateway injectable for tests.

using System.Globalization;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Graph;

public sealed class SqlServerIdentityStore : IIdentityStore
{
    private readonly string _connectorId;
    private readonly ISqlGateway _sql;

    public SqlServerIdentityStore(string connectorId, ISqlGateway? gateway = null)
    {
        _connectorId = connectorId;
        _sql = gateway ?? new SqlExecutor();
    }

    public void Upsert(PrincipalMapping mapping)
    {
        _sql.Execute(
            """
            MERGE dbo.Principals AS target
            USING (SELECT @connector AS ConnectorId, @id AS SourceId) AS source
               ON target.ConnectorId = source.ConnectorId AND target.SourceId = source.SourceId
             WHEN MATCHED THEN UPDATE SET
                  PrincipalType = @type, Email = @email, EntraId = @entra, UpdatedUtc = @updated
             WHEN NOT MATCHED THEN
                  INSERT (ConnectorId, SourceId, PrincipalType, Email, EntraId, UpdatedUtc)
                  VALUES (@connector, @id, @type, @email, @entra, @updated);
            """,
            ("@connector", _connectorId),
            ("@id", mapping.SourceId),
            ("@type", mapping.PrincipalType),
            ("@email", mapping.Email),
            ("@entra", mapping.EntraId),
            ("@updated", mapping.UpdatedUtc));
    }

    public PrincipalMapping? Find(string sourceId)
    {
        var rows = _sql.Query(
            """
            SELECT SourceId, PrincipalType, Email, EntraId, UpdatedUtc
              FROM dbo.Principals WHERE ConnectorId = @connector AND SourceId = @id;
            """,
            ("@connector", _connectorId), ("@id", sourceId));
        return rows.Count == 0 ? null : Map(rows[0]);
    }

    public List<PrincipalMapping> All()
    {
        var rows = _sql.Query(
            """
            SELECT SourceId, PrincipalType, Email, EntraId, UpdatedUtc
              FROM dbo.Principals WHERE ConnectorId = @connector ORDER BY SourceId;
            """,
            ("@connector", _connectorId));
        return rows.Select(Map).ToList();
    }

    public int ResolvedCount() => Convert.ToInt32(
        _sql.Scalar(
            """
            SELECT COUNT(*) FROM dbo.Principals
             WHERE ConnectorId = @connector AND EntraId IS NOT NULL AND EntraId <> '';
            """,
            ("@connector", _connectorId)),
        CultureInfo.InvariantCulture);

    public int Count() => Convert.ToInt32(
        _sql.Scalar(
            "SELECT COUNT(*) FROM dbo.Principals WHERE ConnectorId = @connector;",
            ("@connector", _connectorId)),
        CultureInfo.InvariantCulture);

    public void Clear() =>
        _sql.Execute(
            "DELETE FROM dbo.Principals WHERE ConnectorId = @connector;",
            ("@connector", _connectorId));

    private static PrincipalMapping Map(Dictionary<string, object?> row) => new(
        (string)row["SourceId"]!,
        (string)row["PrincipalType"]!,
        row["Email"] as string,
        row["EntraId"] as string,
        row["UpdatedUtc"] is DateTime dt ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : DateTime.MinValue);

    public void Dispose()
    {
        // Stateless over the gateway; nothing to dispose.
    }
}
