// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// acl_engine/role_handler.py
// --------------------------
// Step 3.3.1: Role-based group resolution.
//
// Salesforce role hierarchy is a tree stored in the UserRole object:
//   UserRole.Id  ──parent──►  UserRole.ParentRoleId  ──parent──► ...  (root)
//
// Three resolution modes are supported
// -------------------------------------
// ResolveRoleAsync(roleId)
//     Users assigned to *exactly* this role.
//     Used for Group.Type = "Role".
//
// ResolveRoleAndSubordinatesAsync(roleId)
//     Users in this role PLUS all descendant roles (downward BFS/DFS).
//     Used for Group.Type = "RoleAndSubordinates" / "RoleAndSubordinatesInternal".
//
// ResolveParentRolesAsync(roleId)
//     Users in all *ancestor* roles (upward walk to root).
//     Used for implicit "Grant Access Using Hierarchies" – the record owner's
//     managers in the role tree automatically see the record too.
//     Called by the resolver after it resolves the owner's role.
//
// Queries used
// ------------
//   Users in a role  : SELECT Id FROM User WHERE UserRoleId = '<id>' AND IsActive = true
//   Child roles      : SELECT Id FROM UserRole WHERE ParentRoleId = '<id>'
//   Parent of a role : SELECT Id, ParentRoleId FROM UserRole WHERE Id = '<id>' LIMIT 1

using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.AclEngine;

/// <summary>
/// Resolves user sets for role-based Salesforce groups.
///
/// Parameters
/// ----------
/// sfClient : SalesforceClient instance.
/// </summary>
public class RoleHandler
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector.acl_engine");

    private readonly SalesforceClient _sf;
    // ── Bulk pre-warm caches (null = not yet fetched) ──────────────────────
    // role_id → parent_role_id (or null at root)
    private Dictionary<string, string?>? _roleParentMap;
    // parent_role_id → [child_role_ids]
    private Dictionary<string, List<string>>? _rolesByParent;
    // role_id → {active user_ids}
    private Dictionary<string, HashSet<string>>? _usersByRole;
    // Guard: at most one thread does the bulk fetch
    private readonly SemaphoreSlim _prewarmLock = new(1, 1);

    public RoleHandler(SalesforceClient sfClient)
    {
        _sf = sfClient;
        _roleParentMap = null;
        _rolesByParent = null;
        _usersByRole = null;
    }

    // ── Bulk pre-warm ───────────────────────────────────────────────────

    /// <summary>
    /// Fetch ALL UserRole records and ALL active User-role assignments in
    /// exactly 2 SOQL calls.  Subsequent role/user lookups are pure
    /// in-memory dict lookups — no SOQL fired per-record.
    ///
    /// Thread-safe: only the first caller does the work; subsequent callers
    /// return immediately once the caches are populated.
    /// </summary>
    public async Task PrewarmAsync()
    {
        if (_roleParentMap is not null)
            return;  // Already done

        await _prewarmLock.WaitAsync();
        try
        {
            if (_roleParentMap is not null)
                return;  // Another thread beat us to it

            var roleParent = new Dictionary<string, string?>();
            var rolesByParent = new Dictionary<string, List<string>>();
            try
            {
                var rows = await _sf.QueryAllAsync(
                    "SELECT Id, ParentRoleId FROM UserRole");
                foreach (var r in rows)
                {
                    var rid = r["Id"]?.GetValue<string>();
                    var pid = r["ParentRoleId"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(rid))
                    {
                        roleParent[rid] = pid;
                        if (!string.IsNullOrEmpty(pid))
                        {
                            if (!rolesByParent.TryGetValue(pid, out var children))
                            {
                                children = new List<string>();
                                rolesByParent[pid] = children;
                            }
                            children.Add(rid);
                        }
                    }
                }
                Logger.Info($"[RoleHandler] Pre-warmed {roleParent.Count} role(s)");
            }
            catch (InvalidOperationException exc)
            {
                Logger.Warning($"[RoleHandler] Bulk role prewarm failed: {exc.Message}; will fall back to per-role SOQL");
            }

            var usersByRole = new Dictionary<string, HashSet<string>>();
            try
            {
                var rows = await _sf.QueryAllAsync(
                    "SELECT Id, UserRoleId FROM User WHERE IsActive = true AND UserRoleId != null");
                foreach (var r in rows)
                {
                    var uid = r["Id"]?.GetValue<string>();
                    var roleId = r["UserRoleId"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(roleId))
                    {
                        if (!usersByRole.TryGetValue(roleId, out var users))
                        {
                            users = new HashSet<string>();
                            usersByRole[roleId] = users;
                        }
                        users.Add(uid);
                    }
                }
                Logger.Info($"[RoleHandler] Pre-warmed users for {usersByRole.Count} role(s)");
            }
            catch (InvalidOperationException exc)
            {
                Logger.Warning($"[RoleHandler] Bulk user-role prewarm failed: {exc.Message}; will fall back to per-role SOQL");
            }

            // Publish atomically
            _rolesByParent = rolesByParent;
            _usersByRole = usersByRole;
            _roleParentMap = roleParent;  // set last — signals "ready"
        }
        finally
        {
            _prewarmLock.Release();
        }
    }

    // ── Public resolution methods ─────────────────────────────────────────────

    /// <summary>
    /// Return the set of active users assigned to exactly *roleId*.
    /// No traversal – single role only.
    /// </summary>
    public async Task<HashSet<string>> ResolveRoleAsync(string roleId)
    {
        var users = await UsersInRoleAsync(roleId);
        Logger.Info($"[RoleHandler] Role {roleId} → {users.Count} user(s)");
        return users;
    }

    /// <summary>
    /// Return users in *roleId* PLUS every descendant role.
    ///
    /// Traverses downward through UserRole.ParentRoleId relationships using an
    /// iterative DFS to avoid recursion limits on deep hierarchies.
    /// Cycle detection is included as a safety guard.
    /// </summary>
    public async Task<HashSet<string>> ResolveRoleAndSubordinatesAsync(string roleId)
    {
        var allUsers = new HashSet<string>();
        var stack = new List<string> { roleId };
        var visited = new HashSet<string>();

        while (stack.Count > 0)
        {
            var current = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (visited.Contains(current))
                continue;
            visited.Add(current);

            var users = await UsersInRoleAsync(current);
            allUsers.UnionWith(users);

            var children = await ChildRoleIdsAsync(current);
            stack.AddRange(children);
        }

        Logger.Info(
            $"[RoleHandler] Role+Subordinates {roleId} → {allUsers.Count} user(s) across {visited.Count} role(s)");
        return allUsers;
    }

    /// <summary>
    /// Return users in all *ancestor* roles above *roleId*.
    ///
    /// Implements Salesforce's "Grant Access Using Hierarchies" rule:
    /// anyone higher up in the role tree than the record owner automatically
    /// inherits read access to the owner's records.
    ///
    /// Walks upward via UserRole.ParentRoleId until the root (ParentRoleId is
    /// null) or a cycle is detected.
    /// </summary>
    public async Task<HashSet<string>> ResolveParentRolesAsync(string roleId)
    {
        var parentRoleIds = await CollectAncestorRoleIdsAsync(roleId);
        if (parentRoleIds.Count == 0)
        {
            Logger.Debug($"[RoleHandler] Role {roleId} has no parent roles");
            return new HashSet<string>();
        }

        var allUsers = new HashSet<string>();
        foreach (var parentId in parentRoleIds)
        {
            var users = await UsersInRoleAsync(parentId);
            allUsers.UnionWith(users);
        }

        Logger.Info(
            $"[RoleHandler] Parent roles of {roleId} → {parentRoleIds.Count} ancestor role(s), {allUsers.Count} user(s)");
        return allUsers;
    }

    // ── Private query helpers ─────────────────────────────────────────────────

    /// <summary>Fetch active users whose UserRoleId matches *roleId*.</summary>
    private async Task<HashSet<string>> UsersInRoleAsync(string roleId)
    {
        // Fast path — bulk cache hit
        if (_usersByRole is not null)
        {
            return _usersByRole.TryGetValue(roleId, out var cached)
                ? new HashSet<string>(cached)
                : new HashSet<string>();
        }
        // Slow path — per-role SOQL fallback
        var soql =
            $"SELECT Id FROM User "
            + $"WHERE UserRoleId = '{roleId}' AND IsActive = true";
        var records = await _sf.QueryAllAsync(soql);
        return records
            .Select(r => r["Id"]?.GetValue<string>())
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToHashSet();
    }

    /// <summary>Fetch direct child roles (one level down) of *roleId*.</summary>
    private async Task<List<string>> ChildRoleIdsAsync(string roleId)
    {
        // Fast path — bulk cache hit
        if (_rolesByParent is not null)
        {
            return _rolesByParent.TryGetValue(roleId, out var cached)
                ? new List<string>(cached)
                : new List<string>();
        }
        // Slow path
        var soql = $"SELECT Id FROM UserRole WHERE ParentRoleId = '{roleId}'";
        var records = await _sf.QueryAllAsync(soql);
        return records
            .Select(r => r["Id"]?.GetValue<string>())
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToList();
    }

    /// <summary>Return the ParentRoleId for *roleId*, or null if it is the root.</summary>
    private async Task<string?> GetParentRoleIdAsync(string roleId)
    {
        // Fast path — bulk cache hit
        if (_roleParentMap is not null)
            return _roleParentMap.TryGetValue(roleId, out var cached) ? cached : null;
        // Slow path
        var soql =
            $"SELECT Id, ParentRoleId FROM UserRole "
            + $"WHERE Id = '{roleId}' LIMIT 1";
        var records = await _sf.QueryAllAsync(soql);
        if (records.Count == 0)
            return null;
        return records[0]["ParentRoleId"]?.GetValue<string>();  // null at root
    }

    /// <summary>
    /// Walk upward from *roleId* collecting every ancestor role ID.
    /// Stops when ParentRoleId is null or a cycle is detected.
    /// </summary>
    private async Task<HashSet<string>> CollectAncestorRoleIdsAsync(string roleId)
    {
        var ancestors = new HashSet<string>();
        var visited = new HashSet<string> { roleId };
        var current = roleId;

        while (true)
        {
            var parentId = await GetParentRoleIdAsync(current);
            if (string.IsNullOrEmpty(parentId) || visited.Contains(parentId))
                break;
            ancestors.Add(parentId);
            visited.Add(parentId);
            current = parentId;
        }

        return ancestors;
    }
}
