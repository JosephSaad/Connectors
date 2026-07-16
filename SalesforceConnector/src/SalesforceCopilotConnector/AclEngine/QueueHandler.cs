// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// AclEngine/QueueHandler.cs
// -------------------------
// Step 3.3.3: Queue, Public Group, and Manager-based group resolution.
//
// This handler covers the "everything else" branch in group dispatch:
//
// Group.Type = "Organization"
//     The share was granted to the entire org.
//     Returns the PUBLIC_SENTINEL constant so the caller can emit a
//     tenant-wide grant without enumerating users.
//
// Group.Type = "Queue" | "Group" (Public Group) | anything unrecognised
//     Static membership stored in GroupMember.
//     We expand the membership recursively – a group can nest other groups.
//     User IDs are collected directly; nested group IDs are pushed onto the
//     traversal stack and expanded in the same loop.
//     Cycle detection prevents infinite loops.
//
// Group.Type = "Manager"
//     The share was granted to a specific manager identified by Group.RelatedId
//     (a User Id).  We grant access to that manager AND their *direct reports*
//     (one level) via User.ManagerId.
//
// Group.Type = "ManagerAndSubordinatesInternal"
//     Same as "Manager" but includes ALL transitive reports at every depth.
//
// Why User.ManagerId and not UserRole?
// --------------------------------------
// The Manager / ManagerAndSubordinatesInternal group types mirror the org chart
// (HR reporting line) stored in User.ManagerId, which is independent of the role
// hierarchy stored in UserRole.ParentRoleId.  Both are valid sharing mechanisms
// in Salesforce but they model different org structures.
//
// Queries used
// ------------
//   Group members       : SELECT UserOrGroupId FROM GroupMember WHERE GroupId = '<id>'
//   Manager chain       : SELECT Id, ManagerId FROM User WHERE IsActive = true

using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.AclEngine;

/// <summary>
/// Resolves user sets for Queue, Public Group, and Manager-based groups.
///
/// Parameters
/// ----------
/// sfClient : SalesforceClient instance.
/// </summary>
public class QueueHandler
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector.acl_engine");

    private readonly SalesforceClient _sf;
    // Pre-warm cache: group_id → [member_ids] (null = not yet fetched)
    private Dictionary<string, List<string>>? _groupMembers;
    private readonly object _prewarmLock = new();

    public QueueHandler(SalesforceClient sfClient)
    {
        _sf = sfClient;
    }

    // ── Bulk pre-warm (once per run) ─────────────────────────────────────

    /// <summary>
    /// Fetch ALL GroupMember rows in one SOQL call.
    /// After this, ResolveStaticGroupAsync() is a pure in-memory DFS.
    /// </summary>
    public async Task PrewarmAsync()
    {
        if (_groupMembers is not null)
            return;

        var members = new Dictionary<string, List<string>>();
        try
        {
            var rows = await _sf.QueryAllAsync(
                "SELECT GroupId, UserOrGroupId FROM GroupMember");
            foreach (var r in rows)
            {
                var gid = (string?)r["GroupId"];
                var mid = (string?)r["UserOrGroupId"];
                if (!string.IsNullOrEmpty(gid) && !string.IsNullOrEmpty(mid))
                {
                    if (!members.TryGetValue(gid, out var memberList))
                    {
                        memberList = new List<string>();
                        members[gid] = memberList;
                    }
                    memberList.Add(mid);
                }
            }
            Logger.Info($"[QueueHandler] Pre-warmed group members for {members.Count} group(s)");
        }
        catch (InvalidOperationException exc)
        {
            Logger.Warning(
                $"[QueueHandler] GroupMember prewarm failed: {exc.Message}; will fall back to per-group SOQL");
        }

        lock (_prewarmLock)
        {
            if (_groupMembers is null)
                _groupMembers = members;
        }
    }

    // ── Organization ──────────────────────────────────────────────────────────

    /// <summary>
    /// Return {PUBLIC_SENTINEL} to signal org-wide (everyone) access.
    ///
    /// The caller (AclResolver) should emit a tenant-wide grant ACL entry
    /// instead of enumerating individual users.
    /// </summary>
    public Task<HashSet<string>> ResolveOrganizationAsync()
    {
        Logger.Info("[QueueHandler] Organization group → public sentinel");
        return Task.FromResult(new HashSet<string> { Models.PublicSentinel });
    }

    // ── Queue / Public Group (static membership) ──────────────────────────────

    /// <summary>
    /// Expand a Queue or Public Group by iterating GroupMember records.
    ///
    /// Algorithm (iterative DFS, cycle-safe)
    /// --------------------------------------
    /// 1. Pop a group_id from the stack.
    /// 2. Fetch its GroupMember rows.
    /// 3. For each member:
    ///    - If it looks like a User (prefix "005") → add to result set.
    ///    - Otherwise → push onto the stack for recursive expansion.
    /// 4. Repeat until the stack is empty.
    ///
    /// Note: we rely on UserHandler.USER_ID_PREFIX ("005") to identify user
    /// IDs without an extra network call.  Any member ID that is *not* a user
    /// will be treated as a nested group and expanded in the next iteration.
    ///
    /// Parameters
    /// ----------
    /// groupId : The Group Id to expand.
    ///
    /// Returns
    /// -------
    /// HashSet&lt;string&gt; – Salesforce User Ids reachable from this group.
    /// </summary>
    public async Task<HashSet<string>> ResolveStaticGroupAsync(string groupId)
    {
        var allUsers = new HashSet<string>();
        var stack = new List<string> { groupId };
        var visited = new HashSet<string>();

        while (stack.Count > 0)
        {
            var current = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (visited.Contains(current))
                continue;
            visited.Add(current);

            foreach (var memberId in await GetGroupMembersAsync(current))
            {
                if (memberId.StartsWith(UserHandler.UserIdPrefix))
                {
                    allUsers.Add(memberId);
                }
                else
                {
                    // Nested group / queue / role group – expand on next iteration
                    stack.Add(memberId);
                }
            }
        }

        Logger.Debug(
            $"[QueueHandler] Static group {groupId} → {allUsers.Count} user(s) after full expansion " +
            $"({visited.Count} group node(s) visited)");
        return allUsers;
    }

    // ── Manager hierarchy (User.ManagerId) ────────────────────────────────────

    /// <summary>
    /// Return the manager + their *direct reports* (one level only).
    ///
    /// Uses User.ManagerId (HR org chart), not the role hierarchy.
    /// Group.Type = "Manager".
    /// </summary>
    public async Task<HashSet<string>> ResolveManagerAsync(string managerId)
    {
        return await WalkManagerChainAsync(managerId, transitive: false);
    }

    /// <summary>
    /// Return the manager + ALL transitive reports at every depth.
    ///
    /// Uses User.ManagerId (HR org chart), not the role hierarchy.
    /// Group.Type = "ManagerAndSubordinatesInternal".
    /// </summary>
    public async Task<HashSet<string>> ResolveManagerAndSubordinatesAsync(string managerId)
    {
        return await WalkManagerChainAsync(managerId, transitive: true);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// BFS from <paramref name="managerId"/> through User.ManagerId relationships.
    ///
    /// Strategy
    /// --------
    /// 1. Fetch all active users + their ManagerId in a single query.
    /// 2. Build a reports_by_manager dict in memory.
    /// 3. BFS/DFS from managerId:
    ///    - transitive=false : only one level of direct reports.
    ///    - transitive=true  : all levels until no more reports are found.
    ///
    /// Pulling all users in one query is O(total_users) but avoids issuing
    /// one query per manager level, which is much worse on large orgs.
    /// </summary>
    private async Task<HashSet<string>> WalkManagerChainAsync(string managerId, bool transitive)
    {
        var allUsers = await _sf.QueryAllAsync(
            "SELECT Id, ManagerId FROM User WHERE IsActive = true");

        // Build: manager_id → set of direct report user IDs
        var reportsByManager = new Dictionary<string, HashSet<string>>();
        foreach (var user in allUsers)
        {
            var uid = (string?)user["Id"];
            var mid = (string?)user["ManagerId"];
            if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(mid))
            {
                if (!reportsByManager.TryGetValue(mid, out var reports))
                {
                    reports = new HashSet<string>();
                    reportsByManager[mid] = reports;
                }
                reports.Add(uid);
            }
        }

        // Start with the manager themselves; BFS through direct reports
        var resolved = new HashSet<string> { managerId };
        var frontier = reportsByManager.TryGetValue(managerId, out var direct)
            ? direct.ToList()
            : new List<string>();

        while (frontier.Count > 0)
        {
            var current = frontier[^1];
            frontier.RemoveAt(frontier.Count - 1);
            if (resolved.Contains(current))
                continue;
            resolved.Add(current);
            if (transitive && reportsByManager.TryGetValue(current, out var reports))
                frontier.AddRange(reports);
        }

        Logger.Debug(
            $"[QueueHandler] Manager{(transitive ? "+Subordinates" : "")} {managerId} → {resolved.Count} user(s)");
        return resolved;
    }

    /// <summary>Return all UserOrGroupId values from GroupMember for <paramref name="groupId"/>.</summary>
    private async Task<List<string>> GetGroupMembersAsync(string groupId)
    {
        var soql = $"SELECT UserOrGroupId FROM GroupMember WHERE GroupId = '{groupId}'";
        List<System.Text.Json.Nodes.JsonObject> records;
        try
        {
            records = await _sf.QueryAllAsync(soql);
        }
        catch (InvalidOperationException exc)
        {
            Logger.Warning($"[QueueHandler] GroupMember query failed for {groupId}: {exc.Message}");
            return new List<string>();
        }
        return records
            .Select(r => (string?)r["UserOrGroupId"])
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToList();
    }
}
