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
// Backend: SQLite only (data/{connectorId}_inventory.db, table `items`).

using System.Globalization;
using Microsoft.Data.Sqlite;
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

/// <summary>SQLite-backed inventory (the only backend this round). One file per connection id.</summary>
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

    /// <summary>
    /// Open the ingested-item inventory for <paramref name="connectorId"/> (the SQLite backend).
    ///
    /// <para><b>Deferred:</b> a shared SQL Server inventory backend (mirroring
    /// <see cref="IdentityStore.CreateStore"/>'s <c>USE_SQL_SERVER</c> switch and a
    /// <c>dbo.ItemInventory</c> table in docs/SQL_CONTRACT.md) is a planned follow-up. Until it
    /// lands the inventory is <b>per-node/local</b>: in HA multi-node mode each node keeps its own
    /// SQLite inventory, so <c>reconcile</c> should be run from a single node (or per node) rather
    /// than assuming one shared cross-node view.</para>
    /// </summary>
    public static IItemInventory Open(string connectorId) => new ItemInventory(connectorId);
}
