// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// AclEngine/TerritoryHandler.cs
// -----------------------------
// Step 3.3.2: Territory2-based group resolution.
//
// Salesforce Territory Management 2.0 introduces a hierarchy of Territory2 nodes
// stored in the Territory2 object:
//   Territory2.Id  ──parent──►  Territory2.ParentTerritory2Id  ──► ... (root)
//
// Users are linked to territories via UserTerritory2Association.
// Records (Account, Opportunity, …) are linked via ObjectTerritory2Association.
//
// Resolution modes
// ----------------
// ResolveTerritoryAsync(territory2Id)
//     Users assigned to exactly *this* territory.
//     Used for Group.Type = "Territory".
//
// ResolveTerritoryAndSubordinatesAsync(territory2Id)
//     Users in this territory PLUS all descendant territories (downward DFS).
//     Used for Group.Type = "TerritoryAndSubordinates" /
//     "TerritoryAndSubordinatesInternal".
//
// ResolveParentTerritoriesAsync(territory2Id)
//     Users in all *ancestor* territories (upward walk to root).
//     Mirrors the role hierarchy "implicit sharing" concept – users who manage a
//     territory can see records in all child territories.
//
// GetTerritoryIdsForRecordAsync(recordId)
//     Entry point for Account / Opportunity records: fetches the Territory2Ids
//     directly assigned to the record via ObjectTerritory2Association.
//     The caller then passes each territory ID into one of the resolution methods.
//
// Full flow for a record (called by the resolver)
// -----------------------------------------------
//   1. GetTerritoryIdsForRecordAsync(recordId)     → direct Territory2Id(s)
//   2. For each territory ID:
//        a. ResolveTerritoryAsync(tId)              → users in that territory
//        b. CollectAncestorTerritoryIdsAsync(tId)   → walk upward
//        c. Fetch users for each ancestor territory
//   3. Union all user sets
//
// Queries used
// ------------
//   Direct territory assignments  : SELECT Territory2Id FROM ObjectTerritory2Association WHERE ObjectId = '<id>'
//   Users in a territory          : SELECT UserId FROM UserTerritory2Association WHERE Territory2Id = '<id>'
//   Child territories             : SELECT Id FROM Territory2 WHERE ParentTerritory2Id = '<id>'
//   Parent of a territory         : SELECT Id, ParentTerritory2Id FROM Territory2 WHERE Id = '<id>' LIMIT 1

using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.AclEngine;

/// <summary>
/// Resolves user sets for Territory2-based Salesforce groups.
/// </summary>
public class TerritoryHandler
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector.acl_engine");

    private readonly SalesforceClient _sf;
    // Pre-warm caches (null = not yet fetched)
    // territory_id → parent_territory_id
    private Dictionary<string, string?>? _territoryParentMap;
    // parent_territory_id → [child_territory_ids]
    private Dictionary<string, List<string>>? _territoriesByParent;
    // territory_id → {user_ids}
    private Dictionary<string, HashSet<string>>? _usersByTerritory;
    private readonly object _prewarmLock = new();

    public TerritoryHandler(SalesforceClient sfClient)
    {
        _sf = sfClient;
    }

    // ── Bulk pre-warm (once per run) ─────────────────────────────────────

    /// <summary>
    /// Fetch ALL Territory2 nodes and ALL UserTerritory2Association rows
    /// in 2 SOQL calls. After this, all territory resolution is in-memory.
    /// </summary>
    public async Task PrewarmAsync()
    {
        if (_territoryParentMap is not null)
            return;

        var parentMap = new Dictionary<string, string?>();
        var byParent = new Dictionary<string, List<string>>();
        try
        {
            var rows = await _sf.QueryAllAsync(
                "SELECT Id, ParentTerritory2Id FROM Territory2");
            foreach (var r in rows)
            {
                var tid = (string?)r["Id"];
                var pid = (string?)r["ParentTerritory2Id"];
                if (!string.IsNullOrEmpty(tid))
                {
                    parentMap[tid] = pid;
                    if (!string.IsNullOrEmpty(pid))
                    {
                        if (!byParent.TryGetValue(pid, out var children))
                        {
                            children = new List<string>();
                            byParent[pid] = children;
                        }
                        children.Add(tid);
                    }
                }
            }
            Logger.Info($"[TerritoryHandler] Pre-warmed {parentMap.Count} territory node(s)");
        }
        catch (InvalidOperationException exc)
        {
            Logger.Warning($"[TerritoryHandler] Territory2 prewarm failed: {exc.Message}");
        }

        var usersByTerritory = new Dictionary<string, HashSet<string>>();
        try
        {
            var rows = await _sf.QueryAllAsync(
                "SELECT UserId, Territory2Id FROM UserTerritory2Association");
            foreach (var r in rows)
            {
                var uid = (string?)r["UserId"];
                var tid = (string?)r["Territory2Id"];
                if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(tid))
                {
                    if (!usersByTerritory.TryGetValue(tid, out var users))
                    {
                        users = new HashSet<string>();
                        usersByTerritory[tid] = users;
                    }
                    users.Add(uid);
                }
            }
            Logger.Info($"[TerritoryHandler] Pre-warmed users for {usersByTerritory.Count} territory/territories");
        }
        catch (InvalidOperationException exc)
        {
            Logger.Warning($"[TerritoryHandler] UserTerritory2Association prewarm failed: {exc.Message}");
        }

        lock (_prewarmLock)
        {
            if (_territoryParentMap is null)
            {
                _territoriesByParent = byParent;
                _usersByTerritory = usersByTerritory;
                _territoryParentMap = parentMap;  // set last — signals ready
            }
        }
    }

    // ── Record-level territory look-up ────────────────────────────────────────

    /// <summary>
    /// Return Territory2Ids directly assigned to <paramref name="recordId"/> via
    /// ObjectTerritory2Association.
    ///
    /// This is the starting point for Account / Opportunity records.
    /// Returns an empty list if the object has no territory assignments.
    /// </summary>
    public async Task<List<string>> GetTerritoryIdsForRecordAsync(string recordId)
    {
        var soql =
            $"SELECT Territory2Id " +
            $"FROM ObjectTerritory2Association " +
            $"WHERE ObjectId = '{recordId}'";
        List<System.Text.Json.Nodes.JsonObject> records;
        try
        {
            records = await _sf.QueryAllAsync(soql);
        }
        catch (InvalidOperationException exc)
        {
            Logger.Warning(
                $"[TerritoryHandler] Could not fetch territory assignments for {recordId}: {exc.Message}");
            return new List<string>();
        }

        var territoryIds = records
            .Select(r => (string?)r["Territory2Id"])
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToList();
        Logger.Info(
            $"[TerritoryHandler] Record {recordId} → {territoryIds.Count} direct territory assignment(s): {FormatList(territoryIds)}");
        return territoryIds;
    }

    // ── Group-type resolution methods ─────────────────────────────────────────

    /// <summary>
    /// Return users assigned to exactly <paramref name="territory2Id"/>.
    /// No traversal – single territory only.
    /// Used for Group.Type = "Territory".
    /// </summary>
    public async Task<HashSet<string>> ResolveTerritoryAsync(string territory2Id)
    {
        var users = await UsersInTerritoryAsync(territory2Id);
        Logger.Info(
            $"[TerritoryHandler] Territory {territory2Id} → {users.Count} user(s)");
        return users;
    }

    /// <summary>
    /// Return users in <paramref name="territory2Id"/> PLUS every descendant territory.
    ///
    /// Traverses downward through Territory2.ParentTerritory2Id relationships
    /// using an iterative DFS with cycle detection.
    /// Used for Group.Type = "TerritoryAndSubordinates" /
    /// "TerritoryAndSubordinatesInternal".
    /// </summary>
    public async Task<HashSet<string>> ResolveTerritoryAndSubordinatesAsync(string territory2Id)
    {
        var allUsers = new HashSet<string>();
        var stack = new List<string> { territory2Id };
        var visited = new HashSet<string>();

        while (stack.Count > 0)
        {
            var current = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (visited.Contains(current))
                continue;
            visited.Add(current);

            var users = await UsersInTerritoryAsync(current);
            allUsers.UnionWith(users);

            var children = await ChildTerritoryIdsAsync(current);
            stack.AddRange(children);
        }

        Logger.Info(
            $"[TerritoryHandler] Territory+Subordinates {territory2Id} → {allUsers.Count} user(s) across {visited.Count} territory/territories");
        return allUsers;
    }

    /// <summary>
    /// Return users in all *ancestor* territories above <paramref name="territory2Id"/>.
    ///
    /// Walks upward via Territory2.ParentTerritory2Id until the root
    /// (ParentTerritory2Id is null) or a cycle is detected.
    /// Mirrors implicit sharing upward in the territory tree.
    /// </summary>
    public async Task<HashSet<string>> ResolveParentTerritoriesAsync(string territory2Id)
    {
        var ancestorIds = await CollectAncestorTerritoryIdsAsync(territory2Id);
        if (ancestorIds.Count == 0)
        {
            Logger.Debug(
                $"[TerritoryHandler] Territory {territory2Id} has no parent territories");
            return new HashSet<string>();
        }

        var allUsers = new HashSet<string>();
        foreach (var ancestorId in ancestorIds)
        {
            var users = await UsersInTerritoryAsync(ancestorId);
            allUsers.UnionWith(users);
        }

        Logger.Info(
            $"[TerritoryHandler] Parent territories of {territory2Id} → {ancestorIds.Count} ancestor(s), {allUsers.Count} user(s)");
        return allUsers;
    }

    // ── Private query helpers ─────────────────────────────────────────────────

    /// <summary>Return users assigned to <paramref name="territory2Id"/>.</summary>
    private async Task<HashSet<string>> UsersInTerritoryAsync(string territory2Id)
    {
        if (_usersByTerritory is not null)
        {
            return _usersByTerritory.TryGetValue(territory2Id, out var cached)
                ? new HashSet<string>(cached)
                : new HashSet<string>();
        }
        var soql =
            $"SELECT UserId FROM UserTerritory2Association " +
            $"WHERE Territory2Id = '{territory2Id}'";
        var records = await _sf.QueryAllAsync(soql);
        return records
            .Select(r => (string?)r["UserId"])
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToHashSet();
    }

    /// <summary>Return direct child territories of <paramref name="territory2Id"/>.</summary>
    private async Task<List<string>> ChildTerritoryIdsAsync(string territory2Id)
    {
        if (_territoriesByParent is not null)
        {
            return _territoriesByParent.TryGetValue(territory2Id, out var cached)
                ? new List<string>(cached)
                : new List<string>();
        }
        var soql = $"SELECT Id FROM Territory2 WHERE ParentTerritory2Id = '{territory2Id}'";
        var records = await _sf.QueryAllAsync(soql);
        return records
            .Select(r => (string?)r["Id"])
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToList();
    }

    /// <summary>Return the ParentTerritory2Id for <paramref name="territory2Id"/>, or null at root.</summary>
    private async Task<string?> GetParentTerritoryIdAsync(string territory2Id)
    {
        if (_territoryParentMap is not null)
            return _territoryParentMap.GetValueOrDefault(territory2Id);
        var soql =
            $"SELECT Id, ParentTerritory2Id FROM Territory2 " +
            $"WHERE Id = '{territory2Id}' LIMIT 1";
        var records = await _sf.QueryAllAsync(soql);
        if (records.Count == 0)
            return null;
        return (string?)records[0]["ParentTerritory2Id"];
    }

    /// <summary>
    /// Walk upward from <paramref name="territory2Id"/> collecting every ancestor territory ID.
    /// Stops when ParentTerritory2Id is null or a cycle is detected.
    /// </summary>
    private async Task<HashSet<string>> CollectAncestorTerritoryIdsAsync(string territory2Id)
    {
        var ancestors = new HashSet<string>();
        var visited = new HashSet<string> { territory2Id };
        var current = territory2Id;

        while (!string.IsNullOrEmpty(current))
        {
            var parentId = await GetParentTerritoryIdAsync(current);
            if (string.IsNullOrEmpty(parentId) || visited.Contains(parentId))
                break;
            ancestors.Add(parentId);
            visited.Add(parentId);
            current = parentId;
        }

        return ancestors;
    }

    /// <summary>Format a list of strings the way Python repr() renders it: ['a', 'b'].</summary>
    private static string FormatList(IEnumerable<string> items)
    {
        return "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";
    }
}
