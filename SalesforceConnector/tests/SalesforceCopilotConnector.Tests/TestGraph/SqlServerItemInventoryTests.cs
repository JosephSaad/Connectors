// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Integration tests for graph.SqlServerItemInventory — the SQL Server ingested-
// item inventory backend (dbo.ItemInventory, docs/SQL_CONTRACT.md).  Mirrors the
// SQLite ItemInventoryTests coverage (record/upsert, object filtering + order,
// grouping, bulk remove incl. no-op, count) plus per-connector isolation.
//
// These tests require a provisioned database (scripts/sql/create-database.sql)
// and SKIP cleanly (early return) unless SQLSERVER_TEST_CONNECTION_STRING is set,
// e.g.:
//
//     SQLSERVER_TEST_CONNECTION_STRING="Server=localhost;Database=SalesforceConnector;User Id=sa;Password=...;TrustServerCertificate=true" dotnet test
//
// The store reads its connection string from SQL_CONNECTION_STRING (via
// SqlStateStore.ConnectionString), so each fixture sets that env var for its
// lifetime — but deliberately NOT USE_SQL_SERVER, so a parallel ItemInventory.Open
// in another collection still routes to SQLite.  Runs in CI's sql-integration job.

using Microsoft.Data.SqlClient;
using SalesforceCopilotConnector.Graph;

namespace SalesforceCopilotConnector.Tests.TestGraph;

[Collection("IngestGlobalState")]
public sealed class SqlServerItemInventoryTests : IDisposable
{
    private readonly EnvVarScope? _env;
    private readonly string _connectorId = "sqltest-inv-" + Guid.NewGuid().ToString("N")[..12];
    private string SecondaryConnectorId => _connectorId + "-b";

    public SqlServerItemInventoryTests()
    {
        // Only SQL_CONNECTION_STRING — SqlServerItemInventory reads it directly and
        // is constructed by hand here (USE_SQL_SERVER routing is exercised elsewhere).
        if (SqlServerTestEnv.Available)
            _env = new EnvVarScope(("SQL_CONNECTION_STRING", SqlServerTestEnv.ConnectionString));
    }

    /// <summary>True → the test must return without asserting (no server configured).</summary>
    private bool Skip => _env is null;

    private SqlServerItemInventory Open(string? connectorId = null) => new(connectorId ?? _connectorId);

    /// <summary>Direct read of a row's LastSeenUtc (no proc exposes it) for upsert assertions.</summary>
    private static DateTime LastSeen(string connectorId, string itemId)
    {
        using var conn = new SqlConnection(SqlServerTestEnv.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT LastSeenUtc FROM dbo.ItemInventory WHERE ConnectorId = @c AND ItemId = @i;";
        cmd.Parameters.AddWithValue("@c", connectorId);
        cmd.Parameters.AddWithValue("@i", itemId);
        return (DateTime)cmd.ExecuteScalar()!;
    }

    public void Dispose()
    {
        if (_env is not null)
        {
            try
            {
                using var conn = new SqlConnection(SqlServerTestEnv.ConnectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM dbo.ItemInventory WHERE ConnectorId IN (@a, @b);";
                cmd.Parameters.AddWithValue("@a", _connectorId);
                cmd.Parameters.AddWithValue("@b", SecondaryConnectorId);
                cmd.ExecuteNonQuery();
            }
            catch (SqlException)
            {
                // best-effort cleanup
            }
            _env.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void RecordSeenInsertsAndReadsByObject()
    {
        if (Skip) return;
        var inv = Open();
        inv.RecordSeen(
            new[] { ("A1", "Account"), ("A2", "Account"), ("C1", "Case") },
            new DateTime(2026, 7, 12, 9, 0, 0, DateTimeKind.Utc));

        Assert.Equal(3, inv.Count());
        Assert.Equal(new[] { "A1", "A2" }, inv.IdsForObject("Account"));
        Assert.Equal(new[] { "C1" }, inv.IdsForObject("Case"));
        Assert.Empty(inv.IdsForObject("Ghost"));
    }

    [Fact]
    public void RecordSeenIsUpsertUpdatingObjectTypeAndLastSeen()
    {
        if (Skip) return;
        var inv = Open();
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddHours(5);

        inv.RecordSeen(new[] { ("X", "Account") }, t1);
        Assert.Equal(1, inv.Count());
        Assert.Equal(t1, LastSeen(_connectorId, "X"));

        // Re-recording the same id upserts: new ObjectType, later LastSeenUtc, no
        // duplicate row (mirrors the SQLite ON CONFLICT upsert).
        inv.RecordSeen(new[] { ("X", "Case") }, t2);
        Assert.Equal(1, inv.Count());
        Assert.Empty(inv.IdsForObject("Account"));
        Assert.Equal(new[] { "X" }, inv.IdsForObject("Case"));
        Assert.Equal(t2, LastSeen(_connectorId, "X"));
    }

    [Fact]
    public void IdsForObjectFiltersAndOrdersById()
    {
        if (Skip) return;
        var inv = Open();
        // Inserted out of order to prove the proc's ORDER BY ItemId.
        inv.RecordSeen(
            new[] { ("A3", "Account"), ("A1", "Account"), ("A2", "Account"), ("C9", "Case") },
            DateTime.UtcNow);

        Assert.Equal(new[] { "A1", "A2", "A3" }, inv.IdsForObject("Account"));
        Assert.Equal(new[] { "C9" }, inv.IdsForObject("Case"));
    }

    [Fact]
    public void AllByObjectGroupsIdsByObjectType()
    {
        if (Skip) return;
        var inv = Open();
        inv.RecordSeen(
            new[] { ("A1", "Account"), ("C1", "Case"), ("A2", "Account") }, DateTime.UtcNow);

        var all = inv.AllByObject();
        Assert.Equal(2, all.Count);
        Assert.Equal(new[] { "A1", "A2" }, all["Account"]);
        Assert.Equal(new[] { "C1" }, all["Case"]);
    }

    [Fact]
    public void RemoveBulkAndMissingIdIsNoOp()
    {
        if (Skip) return;
        var inv = Open();
        inv.RecordSeen(
            new[] { ("A1", "Account"), ("A2", "Account"), ("A3", "Account") }, DateTime.UtcNow);

        // Bulk remove including a non-existent id — the ghost is silently ignored.
        inv.Remove(new[] { "A1", "A3", "GhostZZ" });
        Assert.Equal(new[] { "A2" }, inv.IdsForObject("Account"));
        Assert.Equal(1, inv.Count());

        // Removing only a non-existent id is a no-op.
        inv.Remove(new[] { "GhostZZ" });
        Assert.Equal(1, inv.Count());

        // Empty removal is a no-op.
        inv.Remove(Array.Empty<string>());
        Assert.Equal(1, inv.Count());
    }

    [Fact]
    public void CountReflectsRowsAndEmptyRecordIsNoOp()
    {
        if (Skip) return;
        var inv = Open();
        Assert.Equal(0, inv.Count());

        inv.RecordSeen(new[] { ("A1", "Account"), ("A2", "Account") }, DateTime.UtcNow);
        Assert.Equal(2, inv.Count());

        // Empty RecordSeen is a no-op (no proc call, no rows).
        inv.RecordSeen(Array.Empty<(string, string)>(), DateTime.UtcNow);
        Assert.Equal(2, inv.Count());
    }

    [Fact]
    public void PerConnectorIsolation()
    {
        if (Skip) return;
        var a = Open(_connectorId);
        var b = Open(SecondaryConnectorId);

        a.RecordSeen(new[] { ("A1", "Account") }, DateTime.UtcNow);
        b.RecordSeen(new[] { ("B1", "Account"), ("B2", "Case") }, DateTime.UtcNow);

        // Each connector sees only its own rows.
        Assert.Equal(new[] { "A1" }, a.IdsForObject("Account"));
        Assert.Equal(new[] { "B1" }, b.IdsForObject("Account"));
        Assert.Equal(1, a.Count());
        Assert.Equal(2, b.Count());
        Assert.Empty(a.IdsForObject("Case"));
    }
}
