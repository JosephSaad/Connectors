// Graph/Inventory.cs
// ------------------
// Ingested-item inventory: the connector's record of every external item id
// it has successfully PUT into a Graph connection, keyed per connection id
// (so sharding keeps per-shard inventories automatically).
//
// This is the state surface behind deletion/tombstone sync and the
// `reconcile` command: Clarizen's REST API has no deletion feed, so removals
// are detected by an existence sweep — compare the inventory against the
// full-crawl source id set and DELETE what the source no longer contains.
//
// Backends mirror the identity store: SQLite by default
// (data/{CONNECTOR_ID}_inventory.db, table `items`) and SQL Server
// (dbo.ItemInventory) when USE_SQL_SERVER=true — see docs/SQL_CONTRACT.md.

using System.Globalization;
using ClarizenConnector.Infrastructure;
using Microsoft.Data.Sqlite;

namespace ClarizenConnector.Graph;

public interface IItemInventory : IDisposable
{
    /// <summary>Upsert items as present in the index (stamps last-seen).</summary>
    void RecordSeen(IEnumerable<(string ItemId, string ObjectType)> items, DateTime seenUtc);

    /// <summary>Remove items (after a successful Graph DELETE).</summary>
    void Remove(IEnumerable<string> itemIds);

    /// <summary>All inventoried item ids for one object type.</summary>
    List<string> IdsForObject(string objectType);

    /// <summary>Item ids grouped by object type.</summary>
    Dictionary<string, List<string>> AllByObject();

    int Count();
}

/// <summary>SQLite-backed inventory (default backend). One file per connection id.</summary>
public sealed class ItemInventory : IItemInventory
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector.inventory");

    private readonly SqliteConnection _connection;

    /// <summary>Data directory; settable so tests can redirect.</summary>
    public static string DataDir { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "data");

    public static string DbPath(string connectorId) =>
        Path.Combine(DataDir, $"{connectorId}_inventory.db");

    public ItemInventory(string connectorId, string? dbPath = null)
    {
        var path = dbPath ?? DbPath(connectorId);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();
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

    /// <summary>Open the configured inventory backend (SQLite or SQL Server).</summary>
    public static IItemInventory Open(string connectorId) =>
        EnvFlags.UseSqlServer
            ? new SqlServerItemInventory(connectorId)
            : new ItemInventory(connectorId);
}

/// <summary>SQL Server inventory (USE_SQL_SERVER=true): dbo.ItemInventory, keyed
/// on (ConnectorId, ItemId). Gateway injectable for tests.</summary>
public sealed class SqlServerItemInventory : IItemInventory
{
    private readonly string _connectorId;
    private readonly ISqlGateway _sql;

    public SqlServerItemInventory(string connectorId, ISqlGateway? gateway = null)
    {
        _connectorId = connectorId;
        _sql = gateway ?? new SqlExecutor();
    }

    public void RecordSeen(IEnumerable<(string ItemId, string ObjectType)> items, DateTime seenUtc)
    {
        foreach (var (itemId, objectType) in items)
        {
            _sql.Execute(
                """
                MERGE dbo.ItemInventory AS target
                USING (SELECT @connector AS ConnectorId, @item AS ItemId) AS source
                   ON target.ConnectorId = source.ConnectorId AND target.ItemId = source.ItemId
                 WHEN MATCHED THEN UPDATE SET ObjectType = @type, LastSeenUtc = @seen
                 WHEN NOT MATCHED THEN
                      INSERT (ConnectorId, ItemId, ObjectType, LastSeenUtc)
                      VALUES (@connector, @item, @type, @seen);
                """,
                ("@connector", _connectorId), ("@item", itemId), ("@type", objectType), ("@seen", seenUtc));
        }
    }

    public void Remove(IEnumerable<string> itemIds)
    {
        foreach (var itemId in itemIds)
        {
            _sql.Execute(
                "DELETE FROM dbo.ItemInventory WHERE ConnectorId = @connector AND ItemId = @item;",
                ("@connector", _connectorId), ("@item", itemId));
        }
    }

    public List<string> IdsForObject(string objectType)
    {
        var rows = _sql.Query(
            """
            SELECT ItemId FROM dbo.ItemInventory
             WHERE ConnectorId = @connector AND ObjectType = @type ORDER BY ItemId;
            """,
            ("@connector", _connectorId), ("@type", objectType));
        return rows.Select(r => (string)r["ItemId"]!).ToList();
    }

    public Dictionary<string, List<string>> AllByObject()
    {
        var rows = _sql.Query(
            """
            SELECT ItemId, ObjectType FROM dbo.ItemInventory
             WHERE ConnectorId = @connector ORDER BY ItemId;
            """,
            ("@connector", _connectorId));
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var objectType = (string)row["ObjectType"]!;
            if (!result.TryGetValue(objectType, out var ids))
                result[objectType] = ids = new List<string>();
            ids.Add((string)row["ItemId"]!);
        }
        return result;
    }

    public int Count() => Convert.ToInt32(
        _sql.Scalar(
            "SELECT COUNT(*) FROM dbo.ItemInventory WHERE ConnectorId = @connector;",
            ("@connector", _connectorId)),
        CultureInfo.InvariantCulture);

    public void Dispose()
    {
        // Stateless over the gateway; nothing to dispose.
    }
}
