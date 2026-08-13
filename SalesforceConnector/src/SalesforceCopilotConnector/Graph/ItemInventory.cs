// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Graph/ItemInventory.cs
// ----------------------
// Ingested-item inventory: the connector's record of every external item id it
// has successfully PUT into a Graph connection, keyed per connection id (so
// connection sharding keeps a per-shard inventory automatically).
//
// This is the state surface behind the `reconcile` command. Salesforce exposes
// no deletion feed to the connector, so records removed in Salesforce are
// detected by an existence sweep — compare the inventory against the live source
// id set and DELETE what the source no longer contains.
//
// The Graph external item id equals the Salesforce record Id (Item/Models.cs
// sets ["id"] = Id), so the inventory stores the Salesforce record Id directly
// and reconcile compares against FetchSalesforceRecordsAsync(...) r["Id"] — the
// same id space, compared directly.
//
// Backends mirror the identity store: SQLite by default
// (data/{connectorId}_inventory.db, table `items`) and SQL Server
// (dbo.ItemInventory) when USE_SQL_SERVER=true — see docs/SQL_CONTRACT.md. The
// SQL Server table is shared, so HA nodes and reconcile see one inventory.

using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using SalesforceCopilotConnector.Config;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Graph;

/// <summary>The connector's record of external item ids ingested into one Graph connection.</summary>
public interface IItemInventory : IDisposable
{
    /// <summary>Upsert items as present in the index (stamps last-seen).</summary>
    void RecordSeen(IEnumerable<(string ItemId, string ObjectType)> items, DateTime seenUtc);

    /// <summary>Remove items (after a successful Graph DELETE).</summary>
    void Remove(IEnumerable<string> itemIds);

    /// <summary>All inventoried item ids for one object type (ordinal-sorted).</summary>
    List<string> IdsForObject(string objectType);

    /// <summary>Item ids grouped by object type.</summary>
    Dictionary<string, List<string>> AllByObject();

    /// <summary>Total inventoried item count.</summary>
    int Count();
}

/// <summary>SQLite-backed inventory (default backend). One file per connection id.</summary>
public sealed class ItemInventory : IItemInventory
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector.inventory");

    private readonly SqliteConnection _connection;

    /// <summary>Data directory for the SQLite files; settable so tests can redirect.</summary>
    public static string DataDir { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "data");

    /// <summary>Default DB path for <paramref name="connectorId"/>: <c>{DataDir}/{connectorId}_inventory.db</c>.</summary>
    public static string DbPath(string connectorId) =>
        Path.Combine(DataDir, $"{connectorId}_inventory.db");

    public ItemInventory(string connectorId, string? dbPath = null)
    {
        var path = dbPath ?? DbPath(connectorId);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            SecureDirectory.EnsureOwnerOnly(dir);  // #3: state dir owner-only

        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();
        // Contended writes must wait, not fail. Writes here are autocommit
        // statements, so under the default rollback journal each takes a SHARED
        // lock then upgrades to RESERVED; when another connection holds RESERVED,
        // SQLite returns SQLITE_BUSY on that upgrade immediately and deliberately
        // WITHOUT consulting the busy handler, because blocking mid-upgrade would
        // deadlock. busy_timeout alone therefore cannot fix it (measured on
        // Windows: it took a multi-node storm from 4 failing writers to 3). WAL
        // removes the upgrade - readers never block the writer - and the
        // write-write contention that remains does honour the busy handler.
        //
        // Windows enforces the locking that exposes this; POSIX hides it, so this
        // only ever failed on Windows Server, the deployment target. journal_mode
        // is persisted in the database file, so the pragma is a no-op after the
        // first connection and upgrades a file made by an older build.
        ApplySqlitePragmas(_connection);
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS items (
                item_id       TEXT PRIMARY KEY,
                object_type   TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_items_object ON items(object_type);
            """;
        command.ExecuteNonQuery();
        Logger.Debug($"Item inventory opened: {path}");
    }

    public void RecordSeen(IEnumerable<(string ItemId, string ObjectType)> items, DateTime seenUtc)
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO items (item_id, object_type, last_seen_utc)
            VALUES ($id, $type, $seen)
            ON CONFLICT(item_id) DO UPDATE SET
                object_type = excluded.object_type,
                last_seen_utc = excluded.last_seen_utc;
            """;
        var idParam = command.CreateParameter();
        idParam.ParameterName = "$id";
        var typeParam = command.CreateParameter();
        typeParam.ParameterName = "$type";
        var seenParam = command.CreateParameter();
        seenParam.ParameterName = "$seen";
        command.Parameters.AddRange(new[] { idParam, typeParam, seenParam });
        seenParam.Value = seenUtc.ToString("o", CultureInfo.InvariantCulture);

        foreach (var (itemId, objectType) in items)
        {
            idParam.Value = itemId;
            typeParam.Value = objectType;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void Remove(IEnumerable<string> itemIds)
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM items WHERE item_id = $id;";
        var idParam = command.CreateParameter();
        idParam.ParameterName = "$id";
        command.Parameters.Add(idParam);
        foreach (var itemId in itemIds)
        {
            idParam.Value = itemId;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public List<string> IdsForObject(string objectType)
    {
        var result = new List<string>();
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT item_id FROM items WHERE object_type = $type ORDER BY item_id;";
        command.Parameters.AddWithValue("$type", objectType);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    public Dictionary<string, List<string>> AllByObject()
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT item_id, object_type FROM items ORDER BY item_id;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var objectType = reader.GetString(1);
            if (!result.TryGetValue(objectType, out var ids))
                result[objectType] = ids = new List<string>();
            ids.Add(reader.GetString(0));
        }
        return result;
    }

    public int Count()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM items;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>
    /// Open the configured ingested-item inventory backend for <paramref name="connectorId"/>.
    ///
    /// <para>SQLite by default (one <c>data/{connectorId}_inventory.db</c> file per node); the
    /// shared SQL Server backend (<c>dbo.ItemInventory</c>, docs/SQL_CONTRACT.md) when
    /// <c>USE_SQL_SERVER=true</c>. Because the SQL Server table is shared, all HA nodes and
    /// <c>reconcile</c> see one cross-node inventory; the SQLite backend remains per-node/local.</para>
    /// </summary>
    public static IItemInventory Open(string connectorId) =>
        SyncState.UseSqlServer
            ? new SqlServerItemInventory(connectorId)
            : new ItemInventory(connectorId);


    /// <summary>
    /// Apply the busy timeout and switch the file to WAL, retrying while the
    /// journal-mode change is refused.
    /// </summary>
    /// <remarks>
    /// busy_timeout is a no-op to set and journal_mode is persisted in the file,
    /// so on every connection after the first this is pure overhead — but the
    /// FIRST one is a real race. Switching journal mode needs a brief exclusive
    /// lock, and busy_timeout does not cover that transition: several nodes
    /// opening the same state file at once (an HA pair starting together, or a
    /// stress test with six writers) collide on it and one gets SQLITE_BUSY —
    /// "database is locked" — thrown straight out of the constructor.
    ///
    /// Retrying is safe precisely because the change is idempotent and one-way:
    /// whoever wins leaves the file in WAL, and every later attempt is a no-op
    /// that just reports the mode. Failing here would abort a crawl at startup
    /// over a transition that has almost certainly already happened.
    /// </remarks>
    private static void ApplySqlitePragmas(SqliteConnection connection)
    {
        const int Attempts = 20;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var pragma = connection.CreateCommand();
                pragma.CommandText = "PRAGMA busy_timeout=10000; PRAGMA journal_mode=WAL;";
                pragma.ExecuteNonQuery();
                return;
            }
            catch (SqliteException exc) when (
                (exc.SqliteErrorCode is 5 or 6) && attempt < Attempts)  // BUSY / LOCKED
            {
                Thread.Sleep(25);
            }
        }
    }
}

/// <summary>
/// SQL Server-backed inventory (USE_SQL_SERVER=true): <c>dbo.ItemInventory</c>, keyed on
/// (ConnectorId, ItemId) and shared by every HA node and <c>reconcile</c>. All access goes
/// through the contract's stored procedures via <see cref="SqlExecutor"/> (hardened connection +
/// transient-fault retry) using <see cref="SqlStateStore.ConnectionString"/>. Stateless over the
/// executor — connections are per-operation (SqlClient pooling owns them) so Dispose is a no-op.
/// </summary>
public sealed class SqlServerItemInventory : IItemInventory
{
    private readonly string _connectorId;

    public SqlServerItemInventory(string connectorId) => _connectorId = connectorId;

    private static SqlCommand Proc(SqlConnection conn, string procName) =>
        new(procName, conn) { CommandType = CommandType.StoredProcedure };

    public void RecordSeen(IEnumerable<(string ItemId, string ObjectType)> items, DateTime seenUtc)
    {
        var records = new JsonArray();
        foreach (var (itemId, objectType) in items)
        {
            records.Add(new JsonObject
            {
                ["itemId"] = itemId,
                ["objectType"] = objectType,
            });
        }
        if (records.Count == 0)
            return;  // nothing to record

        var utc = seenUtc.Kind == DateTimeKind.Local ? seenUtc.ToUniversalTime() : seenUtc;
        SqlExecutor.Execute(SqlStateStore.ConnectionString, conn =>
        {
            using var cmd = Proc(conn, "dbo.usp_RecordInventoryItems");
            cmd.Parameters.AddWithValue("@ConnectorId", _connectorId);
            cmd.Parameters.AddWithValue("@ItemsJson", records.ToJsonString());
            cmd.Parameters.Add(new SqlParameter("@SeenUtc", SqlDbType.DateTime2) { Value = utc });
            cmd.ExecuteNonQuery();
        });
    }

    public void Remove(IEnumerable<string> itemIds)
    {
        var ids = new JsonArray();
        foreach (var itemId in itemIds)
            ids.Add(JsonValue.Create(itemId));
        if (ids.Count == 0)
            return;  // nothing to remove

        SqlExecutor.Execute(SqlStateStore.ConnectionString, conn =>
        {
            using var cmd = Proc(conn, "dbo.usp_RemoveInventoryItems");
            cmd.Parameters.AddWithValue("@ConnectorId", _connectorId);
            cmd.Parameters.AddWithValue("@ItemIdsJson", ids.ToJsonString());
            cmd.ExecuteNonQuery();
        });
    }

    public List<string> IdsForObject(string objectType)
    {
        return SqlExecutor.Execute(SqlStateStore.ConnectionString, conn =>
        {
            var result = new List<string>();
            using var cmd = Proc(conn, "dbo.usp_GetInventoryByObject");
            cmd.Parameters.AddWithValue("@ConnectorId", _connectorId);
            cmd.Parameters.AddWithValue("@ObjectType", objectType);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add(reader.GetString(0));
            return result;
        });
    }

    public Dictionary<string, List<string>> AllByObject()
    {
        return SqlExecutor.Execute(SqlStateStore.ConnectionString, conn =>
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            using var cmd = Proc(conn, "dbo.usp_GetInventoryAll");
            cmd.Parameters.AddWithValue("@ConnectorId", _connectorId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var itemId = reader.GetString(0);
                var objectType = reader.GetString(1);
                if (!result.TryGetValue(objectType, out var ids))
                    result[objectType] = ids = new List<string>();
                ids.Add(itemId);
            }
            return result;
        });
    }

    public int Count()
    {
        return SqlExecutor.Execute(SqlStateStore.ConnectionString, conn =>
        {
            using var cmd = Proc(conn, "dbo.usp_CountInventory");
            cmd.Parameters.AddWithValue("@ConnectorId", _connectorId);
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        });
    }

    public void Dispose()
    {
        // Stateless over SqlExecutor; SqlClient pooling owns the physical connections.
    }
}
