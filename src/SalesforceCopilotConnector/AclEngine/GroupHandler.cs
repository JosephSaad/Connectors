// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// AclEngine/GroupHandler.cs
// -------------------------
// Step 3.3.0: General group dispatcher.
//
// This class is the single entry point for every UserOrGroupId that is NOT a
// plain User (i.e. does not start with "005").
//
// It fetches the Group record to discover its Type and RelatedId, then routes
// to the appropriate specialist handler:
//
//   ┌─────────────────────────────────────────────┬───────────────────────────┐
//   │ Group.Type                                  │ Handler / method          │
//   ├─────────────────────────────────────────────┼───────────────────────────┤
//   │ Role                                        │ RoleHandler.resolve_role  │
//   │ RoleAndSubordinates                         │ RoleHandler               │
//   │ RoleAndSubordinatesInternal                 │   .resolve_role_and_subs  │
//   ├─────────────────────────────────────────────┼───────────────────────────┤
//   │ Territory                                   │ TerritoryHandler          │
//   │ TerritoryAndSubordinates                    │   .resolve_territory      │
//   │ TerritoryAndSubordinatesInternal            │   .resolve_territory_and  │
//   │                                             │   _subordinates           │
//   ├─────────────────────────────────────────────┼───────────────────────────┤
//   │ Organization                                │ QueueHandler              │
//   │                                             │   .resolve_organization   │
//   ├─────────────────────────────────────────────┼───────────────────────────┤
//   │ Manager                                     │ QueueHandler              │
//   │ ManagerAndSubordinatesInternal              │   .resolve_manager[_subs] │
//   ├─────────────────────────────────────────────┼───────────────────────────┤
//   │ Queue / Group (Public Group) / unrecognised │ QueueHandler              │
//   │                                             │   .resolve_static_group   │
//   └─────────────────────────────────────────────┴───────────────────────────┘
//
// Why query the Group table instead of relying solely on the UserOrGroup.Type
// embedded in share rows?
//     Share rows include a UserOrGroup sub-object with Type, but that type is
//     "User" or "Group" – not the specific group sub-type.  The specific sub-type
//     (Role, Queue, Territory, …) lives in the Group table itself.

using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.AclEngine;

/// <summary>
/// Identifies the type of a Salesforce Group and delegates to the correct
/// specialist handler.
///
/// Parameters
/// ----------
/// sfClient : SalesforceClient instance.
/// </summary>
public class GroupHandler
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector.acl_engine");

    private readonly SalesforceClient _sf;
    private readonly RoleHandler _roleHandler;
    private readonly TerritoryHandler _territoryHandler;
    private readonly QueueHandler _queueHandler;
    // Bulk pre-warm cache: group_id → GroupRecord
    private Dictionary<string, GroupRecord>? _groupCache;
    private readonly SemaphoreSlim _prewarmLock = new(1, 1);
    // UserHandler is instantiated inside GroupHandler to share prewarm.
    // Settable so AclResolver can inject its own instance (Python: group_handler._uh = user_handler).
    internal UserHandler? Uh { get; set; }

    public GroupHandler(SalesforceClient sfClient)
    {
        _sf = sfClient;
        _roleHandler = new RoleHandler(sfClient);
        _territoryHandler = new TerritoryHandler(sfClient);
        _queueHandler = new QueueHandler(sfClient);
    }

    // ── Expose sub-handler prewarm ──────────────────────────────────────────

    /// <summary>
    /// Fetch ALL Salesforce Group records in 1 SOQL call, and pre-warm the
    /// role handler (2 SOQL calls).  After this, every group and role lookup
    /// during ACL resolution is a pure in-memory dict lookup.
    /// </summary>
    public async Task PrewarmAsync()
    {
        if (_groupCache is not null)
            return;

        await _prewarmLock.WaitAsync();
        try
        {
            if (_groupCache is not null)
                return;

            var cache = new Dictionary<string, GroupRecord>();
            try
            {
                var rows = await _sf.QueryAllAsync(
                    "SELECT Id, Type, RelatedId, DoesIncludeBosses FROM Group");
                foreach (var r in rows)
                {
                    var gid = (string?)r["Id"];
                    if (!string.IsNullOrEmpty(gid))
                    {
                        cache[gid] = new GroupRecord(
                            id: gid,
                            type: (string?)r["Type"],
                            relatedId: (string?)r["RelatedId"],
                            doesIncludeBosses: (bool?)r["DoesIncludeBosses"]);
                    }
                }
                Logger.Info($"[GroupHandler] Pre-warmed {cache.Count} group(s)");
            }
            catch (InvalidOperationException exc)
            {
                Logger.Warning($"[GroupHandler] Bulk group prewarm failed: {exc.Message}; will fall back to per-group SOQL");
            }

            _groupCache = cache;
        }
        finally
        {
            _prewarmLock.Release();
        }

        // Pre-warm role handler (independent of group cache)
        await _roleHandler.PrewarmAsync();
        // Pre-warm queue/group members, territory data, and active user set
        await _queueHandler.PrewarmAsync();
        await _territoryHandler.PrewarmAsync();
        await UserHandlerInstance.PrewarmAsync();
    }

    /// <summary>Access the UserHandler from the RoleHandler's sf_client (shared).</summary>
    private UserHandler UserHandlerInstance => Uh ??= new UserHandler(_sf);

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolve <paramref name="groupId"/> to a set of Salesforce User Ids.
    ///
    /// Returns {PUBLIC_SENTINEL} when the group represents the entire
    /// organisation (Group.Type = "Organization").
    ///
    /// Parameters
    /// ----------
    /// groupId : Any non-user UserOrGroupId from a share row or owner field.
    ///
    /// Returns
    /// -------
    /// HashSet&lt;string&gt; – Salesforce User Ids, or {PUBLIC_SENTINEL} for org-wide access.
    /// </summary>
    public async Task<HashSet<string>> ResolveAsync(string groupId)
    {
        var group = await FetchGroupAsync(groupId);

        if (group is null)
        {
            Logger.Warning($"[GroupHandler] Group record not found for Id={groupId}; skipping");
            return new HashSet<string>();
        }

        var gtype = (group.Type ?? "").Trim();
        var related = group.RelatedId;

        Logger.Debug(
            $"[GroupHandler] Dispatching group {groupId}  Type={gtype}  RelatedId={related}");

        // ── 3.3.1  Role-based ─────────────────────────────────────────────────
        if (gtype == "Role")
        {
            if (string.IsNullOrEmpty(related))
            {
                Logger.Warning($"[GroupHandler] Role group {groupId} has no RelatedId");
                return new HashSet<string>();
            }
            return await _roleHandler.ResolveRoleAsync(related);
        }

        if (gtype is "RoleAndSubordinates" or "RoleAndSubordinatesInternal")
        {
            if (string.IsNullOrEmpty(related))
            {
                Logger.Warning($"[GroupHandler] Role+Subordinates group {groupId} has no RelatedId");
                return new HashSet<string>();
            }
            return await _roleHandler.ResolveRoleAndSubordinatesAsync(related);
        }

        // ── 3.3.2  Territory-based ────────────────────────────────────────────
        if (gtype == "Territory")
        {
            if (string.IsNullOrEmpty(related))
            {
                Logger.Warning($"[GroupHandler] Territory group {groupId} has no RelatedId");
                return new HashSet<string>();
            }
            return await _territoryHandler.ResolveTerritoryAsync(related);
        }

        if (gtype is "TerritoryAndSubordinates" or "TerritoryAndSubordinatesInternal")
        {
            if (string.IsNullOrEmpty(related))
            {
                Logger.Warning(
                    $"[GroupHandler] Territory+Subordinates group {groupId} has no RelatedId");
                return new HashSet<string>();
            }
            return await _territoryHandler.ResolveTerritoryAndSubordinatesAsync(related);
        }

        // ── 3.3.3  Organization (everyone) ────────────────────────────────────
        if (gtype == "Organization")
        {
            return await _queueHandler.ResolveOrganizationAsync();
        }

        // ── 3.3.3  Manager-based ──────────────────────────────────────────────
        if (gtype == "Manager")
        {
            if (string.IsNullOrEmpty(related))
            {
                Logger.Warning($"[GroupHandler] Manager group {groupId} has no RelatedId");
                return new HashSet<string>();
            }
            return await _queueHandler.ResolveManagerAsync(related);
        }

        if (gtype == "ManagerAndSubordinatesInternal")
        {
            if (string.IsNullOrEmpty(related))
            {
                Logger.Warning(
                    $"[GroupHandler] Manager+Subordinates group {groupId} has no RelatedId");
                return new HashSet<string>();
            }
            return await _queueHandler.ResolveManagerAndSubordinatesAsync(related);
        }

        // ── 3.3.3  Queue / Public Group / unrecognised ────────────────────────
        // Covers Group.Type = "Queue", "Group" (Public Group), and anything
        // we do not explicitly recognise – safe fallback is static expansion.
        return await _queueHandler.ResolveStaticGroupAsync(groupId);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Return the GroupRecord for <paramref name="groupId"/>.
    ///
    /// Serves from the bulk pre-warm cache when available; falls back to a
    /// per-group SOQL otherwise.
    /// </summary>
    private async Task<GroupRecord?> FetchGroupAsync(string groupId)
    {
        // Fast path — bulk cache hit
        if (_groupCache is not null)
            return _groupCache.GetValueOrDefault(groupId);

        // Slow path — per-group SOQL fallback
        var soql =
            $"SELECT Id, Type, RelatedId, DoesIncludeBosses " +
            $"FROM Group " +
            $"WHERE Id = '{groupId}' LIMIT 1";
        List<System.Text.Json.Nodes.JsonObject> records;
        try
        {
            records = await _sf.QueryAllAsync(soql);
        }
        catch (InvalidOperationException exc)
        {
            Logger.Warning($"[GroupHandler] Could not fetch Group {groupId}: {exc.Message}");
            return null;
        }

        if (records.Count == 0)
            return null;

        var r = records[0];
        return new GroupRecord(
            id: (string?)r["Id"] ?? groupId,
            type: (string?)r["Type"],
            relatedId: (string?)r["RelatedId"],
            doesIncludeBosses: (bool?)r["DoesIncludeBosses"]);
    }
}
