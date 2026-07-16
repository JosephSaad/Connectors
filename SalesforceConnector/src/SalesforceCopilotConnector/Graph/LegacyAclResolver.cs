// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Item;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Graph;

/// <summary>
/// Legacy ACL resolver for Salesforce → Microsoft Graph external items.
///
/// This module implements the *legacy* access-control-list resolution pipeline.
/// For each Salesforce record it determines which Azure AD principals (users)
/// should be granted access in the Microsoft Graph external connection, based on
/// Salesforce's sharing model:
///
/// <list type="bullet">
/// <item><b>Organisation-Wide Defaults (OWD)</b> — when an object's OWD is <c>Public</c>,
///   all tenant users are granted access.  When it is <c>Private</c> or
///   <c>ControlledByParent</c>, the resolver drills into record-level sharing.</item>
/// <item><b>Record ownership</b> — the record owner always receives access.</item>
/// <item><b>Role hierarchy</b> — users in roles above the owner's role inherit access.</item>
/// <item><b>Sharing rules &amp; manual shares</b> — <c>EntityShare</c> records are queried to
///   discover additional grantees (users and groups).</item>
/// <item><b>Group expansion</b> — public groups and queues are recursively expanded to
///   their member users.</item>
/// <item><b>Territory management</b> — if territories are in use, territory membership
///   is resolved and merged into the ACL.</item>
/// <item><b>Parent-chain inheritance</b> — objects with <c>ControlledByParent</c> OWD
///   inherit their parent record's ACL (up to <c>ACL_MAX_PARENT_DEPTH</c>).</item>
/// </list>
///
/// The newer <c>AclEngine</c> package is a modular rewrite of this logic.  Set
/// <c>USE_NEW_ACL_ENGINE=true</c> in the environment to use it instead.
///
/// <para>
/// AclResolver is instantiated per ingestion run.  Call <see cref="ResolveAsync"/> with a
/// dict of <c>{object_type: [records]}</c> to receive a nested dict of
/// <c>{object_type: {record_id: [acl_entry]}}</c> ready for the Graph PUT payload.
/// </para>
/// </summary>
public class AclResolver
{
    private const string UserIdPrefix = "005";
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector");

    private readonly AppConfig _config;
    private readonly Dictionary<string, SalesforceObjectHandler> _handlers;
    private readonly GraphClient? _graphClient;
    private readonly string _tenantId;
    private readonly ClientHelperForIdentitySync _helper;
    private readonly Dictionary<string, (HashSet<string> UserIds, bool IncludesEveryone)> _groupCache = new();
    private readonly Dictionary<string, string?> _principalIdCache = new();
    private Dictionary<string, HashSet<string>>? _roleChildrenCache;
    private List<User>? _usersAndManagers;
    private HashSet<string>? _frozenUsers;
    // territory ACL caches
    private Dictionary<string, string?> _territoryParentCache = new();
    private Dictionary<string, HashSet<string>> _territoryUsersCache = new();
    // bulk pre-warm caches (populated by PrewarmCachesAsync)
    private Dictionary<string, List<string>>? _objectTerritoryAssoc;
    private Dictionary<string, string?>? _allTerritoryParents;
    private Dictionary<string, HashSet<string>>? _allTerritoryUsers;
    private Dictionary<string, Group>? _allGroupsById;
    private Dictionary<string, List<string>>? _allGroupMembersByGroup;
    private Dictionary<string, string>? _roleParentMap;
    private Dictionary<string, HashSet<string>>? _usersByRole;
    private bool _prewarmed;
    // Guards mutable caches shared across parallel object workers (Python relies on the GIL).
    private readonly object _cacheLock = new();

    /// <summary>Initialize the ACL resolver with config, object handlers, and optional Graph client for GUID lookups.</summary>
    public AclResolver(
        AppConfig config,
        Dictionary<string, SalesforceObjectHandler> handlers,
        GraphClient? graphClient = null)
    {
        _config = config;
        _handlers = handlers;
        _graphClient = graphClient;
        _tenantId = config.TenantId;
        _helper = new ClientHelperForIdentitySync(
            new AsyncSalesforceClient(
                config.Connector.Salesforce.InstanceUrl,
                config.Connector.Salesforce.ApiVersion),
            config.Connector.Salesforce.InstanceUrl,
            // Python calls get_salesforce_access_token synchronously in the constructor.
            ApiClient.GetSalesforceAccessTokenAsync(config).GetAwaiter().GetResult(),
            batchSize: config.Tuning.SalesforceBatchSize);
    }

    /// <summary>
    /// Test-double constructor — skips the Salesforce access-token fetch performed by
    /// the public constructor.  Used by test fakes that override <see cref="ResolveAsync"/>
    /// and <see cref="PrewarmCachesAsync"/> (Python tests replace the whole class with a MagicMock).
    /// </summary>
    protected AclResolver(AppConfig config)
    {
        _config = config;
        _handlers = new Dictionary<string, SalesforceObjectHandler>();
        _graphClient = null;
        _tenantId = config.TenantId;
        _helper = null!;
    }

    /// <summary>
    /// Resolve ACLs for all records. Returns <c>{object_type: {record_id: [acl_entry]}}</c>.
    /// Async implementation that resolves ACLs per object type using OWD visibility.
    /// </summary>
    public virtual async Task<Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>>> ResolveAsync(
        Dictionary<string, List<JsonObject>> recordsByObjectType)
    {
        if (recordsByObjectType.Count == 0)
            return new Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>>();

        Logger.Info("ACL RESOLUTION START");

        // Bulk pre-warm all reference data on first invocation
        await PrewarmCachesAsync();

        var aclMaps = new Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>>();
        var visibilityMap = await _helper.GetOrgWideDefaultsMapAsync();

        Logger.Info("\nOrg-Wide Defaults (OWD) Visibility Map:");
        foreach (var (objName, visibility) in visibilityMap)
            Logger.Info($"  {objName}: {VisibilityRepr(visibility)}");

        var objectOrder = SortObjectNames(recordsByObjectType.Keys);
        Logger.Info($"\nObject processing order: {ReprList(objectOrder)}");

        foreach (var objectName in objectOrder)
        {
            var records = recordsByObjectType.GetValueOrDefault(objectName) ?? new List<JsonObject>();
            if (records.Count == 0)
                continue;

            Logger.Info($"Processing object: {objectName} ({records.Count} records)");

            aclMaps[objectName] = await BuildAclMapForObjectAsync(
                objectName,
                records,
                visibilityMap,
                aclMaps);
        }

        Logger.Info("ACL RESOLUTION COMPLETE");

        return aclMaps;
    }

    /// <summary>
    /// Bulk-fetch all reference data from Salesforce in a few SOQL calls.
    ///
    /// After this method completes the ACL resolver can resolve territories,
    /// groups, role hierarchies, and user-to-role mappings entirely in-memory
    /// without any per-record SOQL queries.
    ///
    /// Typical Salesforce orgs:
    /// - Territory2:                      &lt; 500 rows
    /// - ObjectTerritory2Association:     &lt; 50 000 rows
    /// - UserTerritory2Association:       &lt; 10 000 rows
    /// - Group + GroupMember:             &lt; 5 000 rows
    /// - UserRole:                        &lt; 1 000 rows
    /// - Users (active, with roles):      &lt; 50 000 rows
    ///
    /// All of these fit comfortably in RAM and are fetched in seconds.
    /// </summary>
    public virtual async Task PrewarmCachesAsync()
    {
        if (_prewarmed)
            return;

        Logger.Info(new string('=', 70));
        Logger.Info("BULK PRE-WARM: Fetching all ACL reference data from Salesforce");
        Logger.Info(new string('=', 70));

        var sw = Stopwatch.StartNew();

        // 1. Role hierarchy  ────────────────────────────────────────────────
        var roles = await _helper.GetUserRoleHierarchyFromSalesforceAsync(fetchAll: true);
        var childrenByParent = new Dictionary<string, HashSet<string>>();
        var parentMap = new Dictionary<string, string>();
        foreach (var role in roles)
        {
            if (!string.IsNullOrEmpty(role.ParentRoleId) && !string.IsNullOrEmpty(role.Id))
            {
                if (!childrenByParent.TryGetValue(role.ParentRoleId!, out var children))
                    childrenByParent[role.ParentRoleId!] = children = new HashSet<string>();
                children.Add(role.Id);
                parentMap[role.Id] = role.ParentRoleId!;
            }
        }
        _roleChildrenCache = childrenByParent;
        _roleParentMap = parentMap;
        Logger.Info($"  Roles: {roles.Count} records, {parentMap.Count} parent links");

        // 2. Users with role info (for role→user mapping) ──────────────────
        var allUsers = await _helper.GetUsersFromSalesforceAsync(fetchAll: true);
        var usersByRole = new Dictionary<string, HashSet<string>>();
        foreach (var user in allUsers)
        {
            if (!string.IsNullOrEmpty(user.UserRoleId) && !string.IsNullOrEmpty(user.Id))
            {
                if (!usersByRole.TryGetValue(user.UserRoleId!, out var roleUsers))
                    usersByRole[user.UserRoleId!] = roleUsers = new HashSet<string>();
                roleUsers.Add(user.Id);
            }
        }
        _usersByRole = usersByRole;
        _usersAndManagers = allUsers;
        Logger.Info($"  Users: {allUsers.Count} active, {usersByRole.Values.Sum(v => v.Count)} with roles");

        // 3. Frozen users ──────────────────────────────────────────────────
        var frozenLogins = await _helper.GetFrozenUsersAsync();
        _frozenUsers = frozenLogins
            .Where(ul => !string.IsNullOrEmpty(ul.UserId))
            .Select(ul => ul.UserId!)
            .ToHashSet();
        Logger.Info($"  Frozen users: {_frozenUsers.Count}");

        // 4. Territory data (all 3 tables in parallel) ─────────────────────
        //    Territory Management 2.0 objects may not exist in every org —
        //    treat any error as "no territory data" rather than crashing.
        try
        {
            var terrParentsTask = _helper.BulkFetchAllTerritoriesAsync();
            var objTerrTask = _helper.BulkFetchObjectTerritoryAssociationsAsync();
            var userTerrTask = _helper.BulkFetchUserTerritoryAssociationsAsync();
            await Task.WhenAll(terrParentsTask, objTerrTask, userTerrTask);
            _allTerritoryParents = terrParentsTask.Result;
            _objectTerritoryAssoc = objTerrTask.Result;
            _allTerritoryUsers = userTerrTask.Result;
        }
        catch (Exception exc)
        {
            Logger.Warning($"  Territory bulk fetch failed (Territory Management may not be enabled): {exc.Message}");
            _allTerritoryParents = new Dictionary<string, string?>();
            _objectTerritoryAssoc = new Dictionary<string, List<string>>();
            _allTerritoryUsers = new Dictionary<string, HashSet<string>>();
        }

        // Also fill the per-territory caches used by the fallback code paths
        _territoryParentCache = new Dictionary<string, string?>(_allTerritoryParents);
        _territoryUsersCache = new Dictionary<string, HashSet<string>>(_allTerritoryUsers);
        Logger.Info(
            $"  Territories: {_allTerritoryParents.Count} nodes, " +
            $"{_objectTerritoryAssoc.Values.Sum(v => v.Count)} object associations, " +
            $"{_allTerritoryUsers.Values.Sum(v => v.Count)} user associations");

        // 5. Groups + group members (in parallel) ─────────────────────────
        var groupsTask = _helper.BulkFetchAllGroupsAsync();
        var membersTask = _helper.BulkFetchAllGroupMembersAsync();
        await Task.WhenAll(groupsTask, membersTask);
        var allGroups = groupsTask.Result;
        _allGroupMembersByGroup = membersTask.Result;
        _allGroupsById = new Dictionary<string, Group>();
        foreach (var g in allGroups)
        {
            if (!string.IsNullOrEmpty(g.Id))
                _allGroupsById[g.Id] = g;
        }
        Logger.Info(
            $"  Groups: {_allGroupsById.Count}, " +
            $"GroupMembers: {_allGroupMembersByGroup.Values.Sum(v => v.Count)}");

        var elapsed = sw.Elapsed.TotalSeconds;
        _prewarmed = true;
        Logger.Info($"BULK PRE-WARM COMPLETE in {elapsed.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}s");
        Logger.Info(new string('=', 70));
    }

    /// <summary>Build the ACL map for one object type based on its OWD visibility setting.</summary>
    private async Task<Dictionary<string, List<Dictionary<string, string>>>> BuildAclMapForObjectAsync(
        string objectName,
        List<JsonObject> records,
        Dictionary<string, EntityVisibility> visibilityMap,
        Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>> aclMaps)
    {
        var visibility = visibilityMap.TryGetValue(objectName, out var v)
            ? v
            : EntityVisibility.PublicReadOnly;

        Logger.Info($"Visibility for {objectName}: {VisibilityRepr(visibility)}");

        if (IsPublicVisibility(visibility))
        {
            Logger.Info("  → Public visibility - granting everyone access");
            var publicMap = new Dictionary<string, List<Dictionary<string, string>>>();
            foreach (var record in records)
            {
                var recordId = record["Id"]?.ToString();
                if (!string.IsNullOrEmpty(recordId))
                    publicMap[recordId!] = PublicAcl();
            }
            return publicMap;
        }

        if (IsControlledByParent(visibility))
        {
            Logger.Info("  → Controlled by parent - using parent ACLs");
            return await BuildParentControlledAclMapAsync(objectName, records, aclMaps);
        }

        Logger.Info("  → Private visibility - building custom ACLs");
        return await BuildPrivateAclMapAsync(objectName, records);
    }

    /// <summary>Build ACLs for objects with ControlledByParent visibility by inheriting from parent records.</summary>
    internal async Task<Dictionary<string, List<Dictionary<string, string>>>> BuildParentControlledAclMapAsync(
        string objectName,
        List<JsonObject> records,
        Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>> aclMaps)
    {
        var handler = _handlers.GetValueOrDefault(objectName);
        var parentObjectName = handler?.ParentObjectName;
        if (string.IsNullOrEmpty(parentObjectName) || handler == null)
            return await BuildPrivateAclMapAsync(objectName, records);

        var parentAclMap = aclMaps.GetValueOrDefault(parentObjectName!)
                           ?? new Dictionary<string, List<Dictionary<string, string>>>();
        var resolved = new Dictionary<string, List<Dictionary<string, string>>>();
        var unresolvedRecords = new List<JsonObject>();

        foreach (var record in records)
        {
            var recordId = record["Id"]?.ToString();
            var parentRecordId = handler.GetParentRecordId(record);
            if (!string.IsNullOrEmpty(recordId)
                && !string.IsNullOrEmpty(parentRecordId)
                && parentAclMap.TryGetValue(parentRecordId!, out var parentAcl))
            {
                resolved[recordId!] = parentAcl;
            }
            else
            {
                unresolvedRecords.Add(record);
            }
        }

        if (unresolvedRecords.Count > 0)
        {
            foreach (var (key, value) in await BuildPrivateAclMapAsync(objectName, unresolvedRecords))
                resolved[key] = value;
        }

        return resolved;
    }

    /// <summary>
    /// Build ACLs for private-visibility objects using ownership, shares, roles, and territories.
    /// ``internal virtual`` so tests can substitute it, mirroring the Python tests'
    /// monkeypatching of ``resolver._build_private_acl_map``.
    /// </summary>
    internal virtual async Task<Dictionary<string, List<Dictionary<string, string>>>> BuildPrivateAclMapAsync(
        string objectName,
        List<JsonObject> records)
    {
        if (!_handlers.ContainsKey(objectName))
        {
            Logger.Info($"    No handler for {objectName} - defaulting to public ACL");
            var publicMap = new Dictionary<string, List<Dictionary<string, string>>>();
            foreach (var record in records)
            {
                var rid = record["Id"]?.ToString();
                if (!string.IsNullOrEmpty(rid))
                    publicMap[rid!] = PublicAcl();
            }
            return publicMap;
        }

        var recordIds = records
            .Where(record => !string.IsNullOrEmpty(record["Id"]?.ToString()))
            .Select(record => record["Id"]!.ToString())
            .ToList();
        if (recordIds.Count == 0)
            return new Dictionary<string, List<Dictionary<string, string>>>();

        Logger.Info($"    Building private ACLs for {recordIds.Count} {objectName} records");

        var sharesByRecord = await GetSharesByRecordAsync(objectName, recordIds);

        var recordUserIds = new Dictionary<string, HashSet<string>>();
        var recordGroupIds = new Dictionary<string, HashSet<string>>();
        var publicRecords = new HashSet<string>();
        var unionUserIds = new HashSet<string>();
        var unionGroupIds = new HashSet<string>();

        foreach (var record in records)
        {
            var recordId = record["Id"]!.ToString();
            var userIds = new HashSet<string>();
            var groupIds = new HashSet<string>();

            var ownerId = record["OwnerId"]?.ToString();
            if (!string.IsNullOrEmpty(ownerId))
            {
                userIds.Add(ownerId!);

                // Add role hierarchy access: grant access to users in parent roles of the owner
                // This implements "Grant Access Using Hierarchies" for implicit upward sharing
                var ownerRoleId = GetOwnerRoleId(record);
                if (!string.IsNullOrEmpty(ownerRoleId))
                {
                    var parentRoleUsers = await GetParentRoleUsersAsync(ownerRoleId!);
                    userIds.UnionWith(parentRoleUsers);
                }
            }

            foreach (var share in sharesByRecord.GetValueOrDefault(recordId) ?? new List<EntityShareBase>())
            {
                var shareId = share.UserOrGroupId;
                if (string.IsNullOrEmpty(shareId))
                    continue;
                if (share.UserOrGroup != null && share.UserOrGroup.Type == nameof(UserOrGroupType.User))
                    userIds.Add(shareId!);
                else
                    groupIds.Add(shareId!);
            }

            var (expandedUsers, includesEveryone) = await ExpandGroupsAsync(groupIds);
            userIds.UnionWith(expandedUsers);

            if (includesEveryone)
                publicRecords.Add(recordId);

            // Territory-based ACL: Account and Opportunity can be governed by
            // Territory2 assignments.  Include all users from directly assigned
            // territories as well as every ancestor territory in the hierarchy.
            if (objectName is "Account" or "Opportunity")
            {
                var territoryUserIds = await GetTerritoryUserIdsAsync(recordId);
                userIds.UnionWith(territoryUserIds);
            }

            recordUserIds[recordId] = userIds;
            recordGroupIds[recordId] = groupIds;
            unionUserIds.UnionWith(userIds);
            unionGroupIds.UnionWith(groupIds);
        }

        Logger.Info($"    Authorizing {unionUserIds.Count} users and {unionGroupIds.Count} groups");

        Dictionary<string, Dictionary<string, User>> authorizedUsersByObject;
        try
        {
            (authorizedUsersByObject, _) = await _helper.GetAuthorizedUsersAndGroupsFromSalesforceAsync(
                unionUserIds.ToList(),
                unionGroupIds.ToList(),
                _handlers[objectName],
                EntityVisibility.None,
                await GetFrozenUsersAsync());
        }
        catch (Exception authError)
        {
            Logger.Error($"    ❌ Failed to authorize users: {authError.Message}");
            throw;
        }

        var usersById = authorizedUsersByObject.GetValueOrDefault(objectName)
                        ?? new Dictionary<string, User>();
        Logger.Info($"    Retrieved {usersById.Count} authorized users from Salesforce");

        // Bulk-resolve Salesforce users to AAD GUIDs via Graph $batch (20 per call).
        // This replaces hundreds of sequential GET /users/{id} calls with a few batch calls.
        await BulkWarmPrincipalCacheAsync(usersById);

        var aclMap = new Dictionary<string, List<Dictionary<string, string>>>();
        foreach (var recordId in recordIds)
        {
            if (publicRecords.Contains(recordId))
            {
                aclMap[recordId] = PublicAcl();
                continue;
            }

            var aclEntries = await BuildUserAclsAsync(
                usersById,
                recordUserIds.GetValueOrDefault(recordId) ?? new HashSet<string>());
            aclMap[recordId] = aclEntries.Count > 0 ? aclEntries : DenyAllAcl();
        }

        return aclMap;
    }

    /// <summary>Fetch sharing records from Salesforce, grouped by record ID.</summary>
    private async Task<Dictionary<string, List<EntityShareBase>>> GetSharesByRecordAsync(
        string objectName,
        List<string> recordIds)
    {
        var filterCondition = BuildInFilter("Id", recordIds);
        var recordsWithShares = await _helper.GetRecordsWithSharesAsync(
            objectName,
            fetchAll: true,
            filterCondition: filterCondition,
            direction: RecordEnumerationDirection.Ascending);

        var sharesByRecord = new Dictionary<string, List<EntityShareBase>>();
        foreach (var record in recordsWithShares)
        {
            var shareRecords = record.Shares ?? new JsonObject();
            sharesByRecord[record.Id] = _helper.ResponseProcessor.Get<EntityShareBase>(shareRecords);
        }
        return sharesByRecord;
    }

    /// <summary>Expand a set of group IDs into individual user IDs, detecting 'everyone' groups.</summary>
    private async Task<(HashSet<string> UserIds, bool IncludesEveryone)> ExpandGroupsAsync(HashSet<string> groupIds)
    {
        var userIds = new HashSet<string>();
        var includesEveryone = false;

        foreach (var groupId in groupIds)
        {
            var (groupUsers, groupEveryone) = await ResolveGroupAsync(groupId);
            userIds.UnionWith(groupUsers);
            includesEveryone = includesEveryone || groupEveryone;
        }
        return (userIds, includesEveryone);
    }

    /// <summary>
    /// Recursively resolve a Salesforce group to its member user IDs with caching.
    ///
    /// When pre-warmed data is available, uses in-memory lookups instead of
    /// per-group SOQL queries.
    ///
    /// Internal so tests can drive cyclic-membership scenarios directly.
    /// </summary>
    internal async Task<(HashSet<string> UserIds, bool IncludesEveryone)> ResolveGroupAsync(string groupId)
    {
        var (userIds, includesEveryone, _) = await ResolveGroupAsync(groupId, new HashSet<string>());
        return (userIds, includesEveryone);
    }

    /// <summary>
    /// Core recursive resolution with cycle detection.
    ///
    /// <paramref name="visited"/> holds the ancestor chain of the current DFS
    /// (a group already on the chain is a cycle — the newer AclEngine
    /// QueueHandler uses the same visited-set approach).  When a cycle is
    /// detected the nested reference contributes nothing, and every group on
    /// the cycle path is left uncached so a later top-level resolve of those
    /// groups computes their full membership.
    /// </summary>
    private async Task<(HashSet<string> UserIds, bool IncludesEveryone, bool CycleDetected)> ResolveGroupAsync(
        string groupId,
        HashSet<string> visited)
    {
        lock (_cacheLock)
        {
            if (_groupCache.TryGetValue(groupId, out var cached))
                return (cached.UserIds, cached.IncludesEveryone, false);
        }

        // Cycle guard: this group is already an ancestor in the current DFS.
        if (!visited.Add(groupId))
        {
            Logger.Warning(
                $"[LegacyACL] Cyclic group membership detected at {groupId}; skipping nested reference");
            return (new HashSet<string>(), false, true);
        }

        try
        {
            // ── Fast path: use pre-warmed data ────────────────────────────────
            if (_allGroupsById != null)
            {
                var group = _allGroupsById.GetValueOrDefault(groupId);
                if (group == null)
                    return CacheGroup(groupId, (new HashSet<string>(), false));

                var groupType = (group.Type ?? "").Trim();

                if (groupType == nameof(UserOrGroupType.Organization))
                    return CacheGroup(groupId, (new HashSet<string>(), true));

                if (groupType == nameof(UserOrGroupType.Role) && !string.IsNullOrEmpty(group.RelatedId))
                    return CacheGroup(groupId, (GetRoleUsersFromCache(group.RelatedId!, includeDescendants: false), false));

                if (groupType is nameof(UserOrGroupType.RoleAndSubordinates)
                        or nameof(UserOrGroupType.RoleAndSubordinatesInternal)
                    && !string.IsNullOrEmpty(group.RelatedId))
                {
                    return CacheGroup(groupId, (GetRoleUsersFromCache(group.RelatedId!, includeDescendants: true), false));
                }

                if (groupType == nameof(UserOrGroupType.Manager) && !string.IsNullOrEmpty(group.RelatedId))
                    return CacheGroup(groupId, (await ResolveManagerUsersAsync(group.RelatedId!, includeDescendants: false), false));

                if (groupType == nameof(UserOrGroupType.ManagerAndSubordinatesInternal) && !string.IsNullOrEmpty(group.RelatedId))
                    return CacheGroup(groupId, (await ResolveManagerUsersAsync(group.RelatedId!, includeDescendants: true), false));

                // Regular group: expand members from pre-warmed data
                var memberIds = _allGroupMembersByGroup?.GetValueOrDefault(groupId) ?? new List<string>();
                var userIds = new HashSet<string>();
                var includesEveryone = false;
                var cycleDetected = false;
                foreach (var memberId in memberIds)
                {
                    if (string.IsNullOrEmpty(memberId) || memberId == groupId)
                        continue;
                    if (memberId.StartsWith(UserIdPrefix, StringComparison.Ordinal))
                    {
                        userIds.Add(memberId);
                        continue;
                    }
                    var (nestedUsers, nestedEveryone, nestedCycle) = await ResolveGroupAsync(memberId, visited);
                    userIds.UnionWith(nestedUsers);
                    includesEveryone = includesEveryone || nestedEveryone;
                    cycleDetected = cycleDetected || nestedCycle;
                }

                return CacheGroup(groupId, (userIds, includesEveryone), cycleDetected);
            }

            // ── Fallback: per-group SOQL queries ──────────────────────────────
            var groups = await _helper.GetGroupTypeAndRelatedIdAsync(BuildInFilter("Id", new List<string> { groupId }));
            if (groups.Count == 0)
                return CacheGroup(groupId, (new HashSet<string>(), false));

            var fallbackGroup = groups[0];
            var fallbackGroupType = (fallbackGroup.Type ?? "").Trim();

            if (fallbackGroupType == nameof(UserOrGroupType.Organization))
                return CacheGroup(groupId, (new HashSet<string>(), true));

            if (fallbackGroupType == nameof(UserOrGroupType.Role) && !string.IsNullOrEmpty(fallbackGroup.RelatedId))
                return CacheGroup(groupId, (await ResolveRoleUsersAsync(fallbackGroup.RelatedId!, includeDescendants: false), false));

            if (fallbackGroupType is nameof(UserOrGroupType.RoleAndSubordinates)
                    or nameof(UserOrGroupType.RoleAndSubordinatesInternal)
                && !string.IsNullOrEmpty(fallbackGroup.RelatedId))
            {
                return CacheGroup(groupId, (await ResolveRoleUsersAsync(fallbackGroup.RelatedId!, includeDescendants: true), false));
            }

            if (fallbackGroupType == nameof(UserOrGroupType.Manager) && !string.IsNullOrEmpty(fallbackGroup.RelatedId))
                return CacheGroup(groupId, (await ResolveManagerUsersAsync(fallbackGroup.RelatedId!, includeDescendants: false), false));

            if (fallbackGroupType == nameof(UserOrGroupType.ManagerAndSubordinatesInternal) && !string.IsNullOrEmpty(fallbackGroup.RelatedId))
                return CacheGroup(groupId, (await ResolveManagerUsersAsync(fallbackGroup.RelatedId!, includeDescendants: true), false));

            var members = await _helper.GetGroupMembersAsync(BuildInFilter("GroupId", new List<string> { groupId }));
            var fallbackUserIds = new HashSet<string>();
            var fallbackIncludesEveryone = false;
            var fallbackCycleDetected = false;

            foreach (var member in members)
            {
                var memberId = member.UserOrGroupId;
                if (string.IsNullOrEmpty(memberId) || memberId == groupId)
                    continue;
                if (memberId!.StartsWith(UserIdPrefix, StringComparison.Ordinal))
                {
                    fallbackUserIds.Add(memberId);
                    continue;
                }
                var (nestedUsers, nestedEveryone, nestedCycle) = await ResolveGroupAsync(memberId, visited);
                fallbackUserIds.UnionWith(nestedUsers);
                fallbackIncludesEveryone = fallbackIncludesEveryone || nestedEveryone;
                fallbackCycleDetected = fallbackCycleDetected || nestedCycle;
            }

            return CacheGroup(groupId, (fallbackUserIds, fallbackIncludesEveryone), fallbackCycleDetected);
        }
        finally
        {
            // Restore ancestor-chain semantics so shared (diamond) references
            // reached via a different path are not misreported as cycles.
            visited.Remove(groupId);
        }
    }

    /// <summary>
    /// Store a fully resolved group in the cache and return it.
    ///
    /// When <paramref name="cycleDetected"/> is true the result is *partial*
    /// (a nested reference back to an ancestor was skipped), so it is returned
    /// without being cached — caching it could permanently under-grant records
    /// shared directly with that group.
    /// </summary>
    private (HashSet<string> UserIds, bool IncludesEveryone, bool CycleDetected) CacheGroup(
        string groupId,
        (HashSet<string> UserIds, bool IncludesEveryone) result,
        bool cycleDetected = false)
    {
        if (!cycleDetected)
        {
            lock (_cacheLock)
            {
                _groupCache[groupId] = result;
            }
        }
        return (result.UserIds, result.IncludesEveryone, cycleDetected);
    }

    /// <summary>
    /// Return user IDs assigned to a role, optionally including descendant roles.
    ///
    /// Uses pre-warmed <c>_usersByRole</c> cache when available (zero SOQL).
    /// </summary>
    private async Task<HashSet<string>> ResolveRoleUsersAsync(string roleId, bool includeDescendants)
    {
        if (_usersByRole != null)
            return GetRoleUsersFromCache(roleId, includeDescendants);

        var roleIds = new HashSet<string> { roleId };
        if (includeDescendants)
            roleIds.UnionWith(await GetDescendantRoleIdsAsync(roleId));

        var users = await _helper.GetUsersFromSalesforceAsync(
            BuildInFilter("UserRoleId", roleIds.OrderBy(x => x, StringComparer.Ordinal).ToList()),
            fetchAll: true);
        return users.Where(user => !string.IsNullOrEmpty(user.Id)).Select(user => user.Id).ToHashSet();
    }

    /// <summary>Pure in-memory role→users lookup using pre-warmed caches.</summary>
    private HashSet<string> GetRoleUsersFromCache(string roleId, bool includeDescendants)
    {
        var roleIds = new HashSet<string> { roleId };
        if (includeDescendants && _roleChildrenCache != null && _roleChildrenCache.Count > 0)
        {
            var frontier = new List<string>(_roleChildrenCache.GetValueOrDefault(roleId) ?? new HashSet<string>());
            while (frontier.Count > 0)
            {
                var current = frontier[^1];
                frontier.RemoveAt(frontier.Count - 1);
                if (roleIds.Contains(current))
                    continue;
                roleIds.Add(current);
                frontier.AddRange(_roleChildrenCache.GetValueOrDefault(current) ?? new HashSet<string>());
            }
        }

        var result = new HashSet<string>();
        if (_usersByRole != null && _usersByRole.Count > 0)
        {
            foreach (var rid in roleIds)
                result.UnionWith(_usersByRole.GetValueOrDefault(rid) ?? new HashSet<string>());
        }
        return result;
    }

    /// <summary>Return user IDs in a manager's reporting chain, optionally including all subordinates.</summary>
    private async Task<HashSet<string>> ResolveManagerUsersAsync(string managerId, bool includeDescendants)
    {
        var users = await GetUsersAndManagersAsync();
        var reportsByManager = new Dictionary<string, HashSet<string>>();
        foreach (var user in users)
        {
            if (!string.IsNullOrEmpty(user.ManagerId) && !string.IsNullOrEmpty(user.Id))
            {
                if (!reportsByManager.TryGetValue(user.ManagerId!, out var reports))
                    reportsByManager[user.ManagerId!] = reports = new HashSet<string>();
                reports.Add(user.Id);
            }
        }

        var resolved = new HashSet<string> { managerId };
        var frontier = new List<string>(reportsByManager.GetValueOrDefault(managerId) ?? new HashSet<string>());
        while (frontier.Count > 0)
        {
            var current = frontier[^1];
            frontier.RemoveAt(frontier.Count - 1);
            if (resolved.Contains(current))
                continue;
            resolved.Add(current);
            if (includeDescendants)
                frontier.AddRange(reportsByManager.GetValueOrDefault(current) ?? new HashSet<string>());
        }
        return resolved;
    }

    /// <summary>Return all descendant role IDs below the given role in the hierarchy.</summary>
    private async Task<HashSet<string>> GetDescendantRoleIdsAsync(string roleId)
    {
        if (_roleChildrenCache == null)
        {
            var roles = await _helper.GetUserRoleHierarchyFromSalesforceAsync(fetchAll: true);
            var childrenByParent = new Dictionary<string, HashSet<string>>();
            foreach (var role in roles)
            {
                if (!string.IsNullOrEmpty(role.ParentRoleId) && !string.IsNullOrEmpty(role.Id))
                {
                    if (!childrenByParent.TryGetValue(role.ParentRoleId!, out var children))
                        childrenByParent[role.ParentRoleId!] = children = new HashSet<string>();
                    children.Add(role.Id);
                }
            }
            _roleChildrenCache = childrenByParent;
        }

        var descendants = new HashSet<string>();
        var frontier = new List<string>(_roleChildrenCache.GetValueOrDefault(roleId) ?? new HashSet<string>());
        while (frontier.Count > 0)
        {
            var current = frontier[^1];
            frontier.RemoveAt(frontier.Count - 1);
            if (descendants.Contains(current))
                continue;
            descendants.Add(current);
            frontier.AddRange(_roleChildrenCache.GetValueOrDefault(current) ?? new HashSet<string>());
        }
        return descendants;
    }

    /// <summary>
    /// Get all parent role IDs (upward in hierarchy) for implicit sharing.
    ///
    /// Uses pre-warmed <c>_roleParentMap</c> when available (zero SOQL).
    /// </summary>
    private async Task<HashSet<string>> GetParentRoleIdsAsync(string roleId)
    {
        var parentMap = _roleParentMap;
        if (parentMap == null)
        {
            // Fallback: build from SOQL
            if (_roleChildrenCache == null)
                await GetDescendantRoleIdsAsync("");  // Initialize cache
            var roles = await _helper.GetUserRoleHierarchyFromSalesforceAsync(fetchAll: true);
            parentMap = new Dictionary<string, string>();
            foreach (var role in roles)
            {
                if (!string.IsNullOrEmpty(role.Id) && !string.IsNullOrEmpty(role.ParentRoleId))
                    parentMap[role.Id] = role.ParentRoleId!;
            }
            _roleParentMap = parentMap;
        }

        // Traverse upward to collect all parent roles
        var parents = new HashSet<string>();
        var current = roleId;
        while (parentMap.ContainsKey(current))
        {
            var parent = parentMap[current];
            if (parents.Contains(parent))
                break;  // cycle guard
            parents.Add(parent);
            current = parent;
        }

        return parents;
    }

    /// <summary>
    /// Get all users in parent roles for implicit upward sharing.
    ///
    /// Uses pre-warmed data when available (zero SOQL).
    /// </summary>
    private async Task<HashSet<string>> GetParentRoleUsersAsync(string roleId)
    {
        var parentRoleIds = await GetParentRoleIdsAsync(roleId);

        if (parentRoleIds.Count == 0)
            return new HashSet<string>();

        // Fast path: in-memory lookup
        if (_usersByRole != null)
        {
            var result = new HashSet<string>();
            foreach (var rid in parentRoleIds)
                result.UnionWith(_usersByRole.GetValueOrDefault(rid) ?? new HashSet<string>());
            return result;
        }

        // Fallback: SOQL
        var users = await _helper.GetUsersFromSalesforceAsync(
            BuildInFilter("UserRoleId", parentRoleIds.OrderBy(x => x, StringComparer.Ordinal).ToList()),
            fetchAll: true);
        return users.Where(user => !string.IsNullOrEmpty(user.Id)).Select(user => user.Id).ToHashSet();
    }

    // ── Territory-based ACL helpers ───────────────────────────────────────────

    /// <summary>
    /// Walk the Territory2 hierarchy upward, collecting every ancestor ID.
    ///
    /// The loop terminates when there is no ParentTerritory2Id (root territory)
    /// or when a cycle is detected (safety guard).
    /// Results are cached in _territoryParentCache to avoid redundant queries.
    /// </summary>
    private async Task<HashSet<string>> GetAllParentTerritoryIdsAsync(string territoryId)
    {
        var parentIds = new HashSet<string>();
        var visited = new HashSet<string> { territoryId };
        var currentId = (string?)territoryId;

        while (!string.IsNullOrEmpty(currentId))
        {
            bool cached;
            lock (_cacheLock)
            {
                cached = _territoryParentCache.ContainsKey(currentId!);
            }
            if (!cached)
            {
                var fetched = await _helper.GetParentTerritoryIdAsync(currentId!);
                lock (_cacheLock)
                {
                    _territoryParentCache[currentId!] = fetched;
                }
            }
            string? parentId;
            lock (_cacheLock)
            {
                parentId = _territoryParentCache[currentId!];
            }
            var cacheNote = cached ? "(cached)" : "(fetched)";
            Logger.Debug(
                $"      Territory hierarchy: {currentId} → parent: " +
                $"{(string.IsNullOrEmpty(parentId) ? "(none – root)" : parentId)} {cacheNote}");
            if (string.IsNullOrEmpty(parentId) || visited.Contains(parentId!))
                break;
            parentIds.Add(parentId!);
            visited.Add(parentId!);
            currentId = parentId;
        }

        return parentIds;
    }

    /// <summary>
    /// Return the set of Salesforce UserIds assigned to *one* territory.
    ///
    /// Results are cached in _territoryUsersCache so repeated lookups for the
    /// same territory (across multiple records) hit memory instead of Salesforce.
    /// </summary>
    private async Task<HashSet<string>> GetTerritoryUsersCachedAsync(string territoryId)
    {
        bool present;
        lock (_cacheLock)
        {
            present = _territoryUsersCache.ContainsKey(territoryId);
        }
        if (!present)
        {
            Logger.Debug($"      Querying UserTerritory2Association for territory {territoryId}");
            var userIds = await _helper.GetUsersForTerritoriesAsync(new List<string> { territoryId });
            lock (_cacheLock)
            {
                _territoryUsersCache[territoryId] = userIds.ToHashSet();
            }
            Logger.Debug($"      → {userIds.Count} user(s) found for territory {territoryId}");
        }
        else
        {
            lock (_cacheLock)
            {
                Logger.Debug(
                    $"      Territory {territoryId} users served from cache " +
                    $"({_territoryUsersCache[territoryId].Count} user(s))");
            }
        }
        lock (_cacheLock)
        {
            return _territoryUsersCache[territoryId];
        }
    }

    /// <summary>
    /// Resolve the full set of territory-based Salesforce UserIds for one record.
    ///
    /// When bulk pre-warmed data is available (the common path for full syncs),
    /// this method performs <b>zero SOQL queries</b> — all lookups are pure
    /// in-memory dict operations.
    /// </summary>
    private async Task<HashSet<string>> GetTerritoryUserIdsAsync(string recordId)
    {
        // ── Fast path: use pre-warmed data ────────────────────────────────
        if (_objectTerritoryAssoc != null)
        {
            var directTerritories = _objectTerritoryAssoc.GetValueOrDefault(recordId) ?? new List<string>();
            if (directTerritories.Count == 0)
                return new HashSet<string>();

            // Collect direct + all ancestor territories via in-memory parent walk
            var allTerritoryIds = new HashSet<string>(directTerritories);
            foreach (var tId in directTerritories)
            {
                var current = (string?)tId;
                while (!string.IsNullOrEmpty(current) && _allTerritoryParents != null)
                {
                    var parent = _allTerritoryParents.GetValueOrDefault(current!);
                    if (string.IsNullOrEmpty(parent) || allTerritoryIds.Contains(parent!))
                        break;
                    allTerritoryIds.Add(parent!);
                    current = parent;
                }
            }

            // Collect users from all territories
            var allUserIds = new HashSet<string>();
            if (_allTerritoryUsers != null)
            {
                foreach (var tId in allTerritoryIds)
                    allUserIds.UnionWith(_allTerritoryUsers.GetValueOrDefault(tId) ?? new HashSet<string>());
            }

            Logger.Debug(
                $"  [Territory ACL] Record {recordId}: {allTerritoryIds.Count} territories → " +
                $"{allUserIds.Count} users (pre-warmed)");
            return allUserIds;
        }

        // ── Fallback: per-record SOQL queries (only if prewarm failed) ────
        Logger.Info("    ── Territory ACL: Step 1 – ObjectTerritory2Association ──────────");
        var territoryIds = await _helper.GetTerritoryIdsForRecordAsync(recordId);
        if (territoryIds.Count == 0)
        {
            Logger.Info($"    Step 1: No Territory2 assignments found for record {recordId}");
            return new HashSet<string>();
        }

        Logger.Info(
            $"    Step 1: Record {recordId} → {territoryIds.Count} direct territory ID(s): {ReprList(territoryIds)}");

        // Step 2+3: Walk the Territory2 parent hierarchy upward for each direct territory
        Logger.Info("    ── Territory ACL: Step 2 – Territory2 parent hierarchy walk ─────");
        var fallbackTerritoryIds = new HashSet<string>(territoryIds);
        foreach (var tId in territoryIds)
        {
            var ancestorIds = await GetAllParentTerritoryIdsAsync(tId);
            if (ancestorIds.Count > 0)
            {
                Logger.Info(
                    $"    Step 2: Territory {tId} → {ancestorIds.Count} ancestor(s): " +
                    $"{ReprList(ancestorIds.OrderBy(x => x, StringComparer.Ordinal))}");
            }
            else
            {
                Logger.Info($"    Step 2: Territory {tId} has no parent (root territory)");
            }
            fallbackTerritoryIds.UnionWith(ancestorIds);
        }

        Logger.Info(
            $"    Step 2: Total territories to check (direct + ancestors): {fallbackTerritoryIds.Count} → " +
            $"{ReprList(fallbackTerritoryIds.OrderBy(x => x, StringComparer.Ordinal))}");

        // Steps 4+5: Fetch users per territory (with per-territory caching)
        Logger.Debug("    ── Territory ACL: Steps 3-4 – UserTerritory2Association ──────────");
        var fallbackUserIds = new HashSet<string>();
        foreach (var tId in fallbackTerritoryIds.OrderBy(x => x, StringComparer.Ordinal))
        {
            var users = await GetTerritoryUsersCachedAsync(tId);
            Logger.Debug($"    Step 3/4: Territory {tId} → {users.Count} user(s)");
            fallbackUserIds.UnionWith(users);
        }

        Logger.Debug(
            $"    [Territory ACL] Record {recordId} → {fallbackUserIds.Count} unique territory user(s)");
        return fallbackUserIds;
    }

    /// <summary>Extract the owner's UserRole ID from a Salesforce record, if present.</summary>
    private static string? GetOwnerRoleId(JsonObject record)
    {
        if (record["Owner"] is JsonObject owner)
        {
            if (owner["UserRole"] is JsonObject userRole)
                return userRole["Id"]?.ToString();
        }
        return null;
    }

    /// <summary>Fetch and cache all Salesforce users with their manager relationships.</summary>
    private async Task<List<User>> GetUsersAndManagersAsync()
    {
        if (_usersAndManagers == null)
            _usersAndManagers = await _helper.GetUsersAndManagersAsync();
        return _usersAndManagers;
    }

    /// <summary>Fetch and cache the set of frozen Salesforce user IDs.</summary>
    private async Task<HashSet<string>> GetFrozenUsersAsync()
    {
        if (_frozenUsers == null)
        {
            _frozenUsers = (await _helper.GetFrozenUsersAsync())
                .Where(userLogin => !string.IsNullOrEmpty(userLogin.UserId))
                .Select(userLogin => userLogin.UserId!)
                .ToHashSet();
        }
        return _frozenUsers;
    }

    /// <summary>
    /// Pre-resolve user identifiers to AAD GUIDs via Graph $batch.
    ///
    /// Collects every un-cached identifier (FederationIdentifier, UserName,
    /// Email) across <paramref name="usersById"/>, then resolves them in batches of 20 using
    /// <c>POST /$batch</c> with <c>GET /users/{id}?$select=id</c> requests.
    ///
    /// Only <b>successes</b> are cached so that individual fallback (filter query)
    /// still runs for 404s — some identifiers only resolve via the filter path.
    /// </summary>
    private async Task BulkWarmPrincipalCacheAsync(Dictionary<string, User> usersById)
    {
        if (_graphClient == null)
            return;

        var toResolve = new List<string>();
        foreach (var user in usersById.Values)
        {
            if (user == null || user.IsFrozen)
                continue;
            foreach (var raw in new[] { user.FederationIdentifier, user.UserName, user.Email })
            {
                if (string.IsNullOrEmpty(raw))
                    continue;
                var ident = raw!.Trim();
                bool alreadyCached;
                lock (_cacheLock)
                {
                    alreadyCached = _principalIdCache.ContainsKey(ident);
                }
                if (ident.Length > 0 && !alreadyCached && !LooksLikeGuid(ident))
                    toResolve.Add(ident);
            }
        }

        // Deduplicate while preserving order
        var seen = new HashSet<string>();
        var unique = new List<string>();
        foreach (var ident in toResolve)
        {
            if (!seen.Contains(ident))
            {
                seen.Add(ident);
                unique.Add(ident);
            }
        }

        if (unique.Count == 0)
            return;

        Logger.Info($"    Bulk Graph lookup: {unique.Count} unique identifiers");
        var resolved = 0;

        for (var i = 0; i < unique.Count; i += GraphClient.GraphBatchMaxSize)
        {
            var batch = unique.Skip(i).Take(GraphClient.GraphBatchMaxSize).ToList();
            var requestsPayload = new List<JsonObject>();
            for (var idx = 0; idx < batch.Count; idx++)
            {
                requestsPayload.Add(new JsonObject
                {
                    ["id"] = idx.ToString(),
                    ["method"] = "GET",
                    ["url"] = $"/users/{Uri.EscapeDataString(batch[idx])}?$select=id",
                });
            }
            try
            {
                var responses = await _graphClient.BatchRequestsAsync(requestsPayload);
                foreach (var resp in responses)
                {
                    if (!int.TryParse(resp["id"]?.ToString(), out var idx))
                        idx = -1;
                    if (!(0 <= idx && idx < batch.Count))
                        continue;
                    var status = resp["status"]?.GetValue<int>() ?? 0;
                    var body = resp["body"];
                    if (200 <= status && status < 300 && body is JsonObject bodyObj
                        && !string.IsNullOrEmpty(bodyObj["id"]?.ToString()))
                    {
                        lock (_cacheLock)
                        {
                            _principalIdCache[batch[idx]] = bodyObj["id"]!.ToString();
                        }
                        resolved += 1;
                    }
                    // Don't cache failures — let individual lookup try the filter path
                }
            }
            catch (Exception exc)
            {
                Logger.Debug($"    Bulk Graph batch failed: {exc.Message} — falling back to individual lookups");
            }
        }

        Logger.Info($"    Bulk Graph lookup: resolved {resolved} / {unique.Count} identifiers");
    }

    /// <summary>Convert a set of Salesforce user IDs into deduplicated Graph ACL grant entries.</summary>
    private async Task<List<Dictionary<string, string>>> BuildUserAclsAsync(
        Dictionary<string, User> usersById,
        HashSet<string> userIds)
    {
        var aclEntries = new List<Dictionary<string, string>>();
        var seenValues = new HashSet<string>();

        foreach (var userId in userIds.OrderBy(x => x, StringComparer.Ordinal))
        {
            var user = usersById.GetValueOrDefault(userId);

            if (user == null || user.IsFrozen)
                continue;

            var principal = await ResolveUserGuidAsync(user);

            if (string.IsNullOrEmpty(principal))
            {
                Logger.Warning(
                    $"    ✗ User {userId} ({user.Email ?? user.UserName ?? "no-identifier"}): No M365 account found");
                continue;
            }

            var normalized = principal!.ToLowerInvariant();
            if (seenValues.Contains(normalized))
                continue;
            seenValues.Add(normalized);
            aclEntries.Add(new Dictionary<string, string>
            {
                ["accessType"] = "grant",
                ["type"] = "user",
                ["value"] = principal!,
            });
        }

        return aclEntries;
    }

    /// <summary>Return the first non-empty identifier from FederationIdentifier, UserName, or Email.</summary>
    private static string? GetUserPrincipal(User user)
    {
        foreach (var candidate in new[] { user.FederationIdentifier, user.UserName, user.Email })
        {
            if (!string.IsNullOrEmpty(candidate))
                return candidate!.Trim();
        }
        return null;
    }

    /// <summary>
    /// Resolve a Salesforce user to a Microsoft Graph user GUID, trying all identity fields.
    ///
    /// For FederationIdentifier and UserName we also try stripping the extra org-unique
    /// suffix Salesforce appends (e.g. <c>john@nokia.com.cape2104</c> → <c>john@nokia.com</c>)
    /// because AAD UPNs / mails never carry that suffix.
    /// </summary>
    private async Task<string?> ResolveUserGuidAsync(User user)
    {
        var seen = new HashSet<string>();

        async Task<string?> TryAsync(string? identifier)
        {
            if (string.IsNullOrEmpty(identifier))
                return null;
            var val = identifier!.Trim();
            if (val.Length == 0 || seen.Contains(val))
                return null;
            seen.Add(val);
            var result = await ResolvePrincipalGuidAsync(val);
            if (!string.IsNullOrEmpty(result))
                return result;
            // Also try the suffix-stripped version
            var stripped = StripSfUsernameSuffix(val);
            if (!string.IsNullOrEmpty(stripped) && !seen.Contains(stripped!))
            {
                seen.Add(stripped!);
                return await ResolvePrincipalGuidAsync(stripped!);
            }
            return null;
        }

        foreach (var fieldValue in new[] { user.FederationIdentifier, user.UserName, user.Email })
        {
            var resolved = await TryAsync(fieldValue);
            if (!string.IsNullOrEmpty(resolved))
                return resolved;
        }

        return null;
    }

    /// <summary>Resolve an identifier (UPN, email, or GUID) to a Microsoft Graph user ID with caching.</summary>
    private async Task<string?> ResolvePrincipalGuidAsync(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier))
            return null;

        var normalized = identifier!.Trim();
        if (normalized.Length == 0)
            return null;

        // Check cache
        lock (_cacheLock)
        {
            if (_principalIdCache.TryGetValue(normalized, out var cached))
                return cached;
        }

        // Check if already a GUID
        if (LooksLikeGuid(normalized))
        {
            lock (_cacheLock)
            {
                _principalIdCache[normalized] = normalized;
            }
            return normalized;
        }

        // Need Graph client to look up
        if (_graphClient == null)
        {
            lock (_cacheLock)
            {
                _principalIdCache[normalized] = null;
            }
            return null;
        }

        // Lookup via Graph API
        var graphId = await LookupGraphUserIdAsync(normalized);
        lock (_cacheLock)
        {
            _principalIdCache[normalized] = graphId;
        }
        return graphId;
    }

    /// <summary>Look up a user's Graph ID by direct path or filter query on UPN/mail.</summary>
    private async Task<string?> LookupGraphUserIdAsync(string identifier)
    {
        var directPath = $"/users/{Uri.EscapeDataString(identifier)}?$select=id";
        try
        {
            var payload = await _graphClient!.GetAsync(directPath);
            if (payload is JsonObject payloadObj && !string.IsNullOrEmpty(payloadObj["id"]?.ToString()))
            {
                Logger.Info($"[LegacyACL] Graph direct lookup ✓ {identifier} → {payloadObj["id"]}");
                return payloadObj["id"]!.ToString();
            }
        }
        catch (GraphApiError error)
        {
            Logger.Debug($"[LegacyACL] Graph direct lookup {error.StatusCode} for {identifier}");
            if (error.StatusCode is not (400 or 403 or 404))
                throw;
        }
        catch (Exception exc)
        {
            Logger.Debug($"[LegacyACL] Graph direct lookup exception for {identifier}: {exc.Message}");
            return null;
        }

        // Advanced filter query — requires ConsistencyLevel: eventual + $count=true
        // or the Graph API silently returns empty results for multi-property 'or' filters.
        var escapedIdentifier = identifier.Replace("'", "''");
        var filterPath =
            "/users?$select=id&$top=1&$count=true&$filter=" +
            $"userPrincipalName eq '{escapedIdentifier}'" +
            $" or mail eq '{escapedIdentifier}'" +
            $" or onPremisesUserPrincipalName eq '{escapedIdentifier}'";
        var eventualHeaders = new Dictionary<string, string> { ["ConsistencyLevel"] = "eventual" };
        try
        {
            var payload = await _graphClient!.GetAsync(filterPath, headers: eventualHeaders);
            var values = (payload as JsonObject)?["value"] as JsonArray;
            if (values is { Count: > 0 } && values[0] is JsonObject first
                && !string.IsNullOrEmpty(first["id"]?.ToString()))
            {
                var guid = first["id"]!.ToString();
                Logger.Info($"[LegacyACL] Graph filter lookup ✓ {identifier} → {guid}");
                return guid;
            }
            else
            {
                Logger.Warning(
                    $"[LegacyACL] No AAD user found for '{identifier}' (tried direct + filter on UPN/mail/onPremisesUPN)");
                return null;
            }
        }
        catch (GraphApiError error)
        {
            Logger.Debug(
                $"[LegacyACL] Graph filter error for {identifier} (status={error.StatusCode}): {error.Message}");
            if (error.StatusCode is not (400 or 403 or 404))
                throw;
        }
        catch (Exception exc)
        {
            Logger.Debug($"[LegacyACL] Graph filter exception for {identifier}: {exc.Message}");
            return null;
        }

        return null;
    }

    /// <summary>Return True if the value matches the 8-4-4-4-12 hex GUID format.</summary>
    private static bool LooksLikeGuid(string value)
    {
        var cleaned = value.Trim();
        var parts = cleaned.Split('-');
        if (parts.Length != 5)
            return false;
        var expectedLengths = new[] { 8, 4, 4, 4, 12 };
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length != expectedLengths[i])
                return false;
            if (!parts[i].All(ch => "0123456789abcdefABCDEF".Contains(ch)))
                return false;
        }
        return true;
    }

    /// <summary>Return an ACL list granting access to everyone in the tenant.</summary>
    private List<Dictionary<string, string>> PublicAcl()
    {
        return new List<Dictionary<string, string>>
        {
            new Dictionary<string, string>
            {
                ["accessType"] = "grant",
                ["type"] = "everyone",
                ["value"] = _tenantId,
            },
        };
    }

    /// <summary>Return an ACL list denying access to everyone (used when no users are resolved).</summary>
    private List<Dictionary<string, string>> DenyAllAcl()
    {
        return new List<Dictionary<string, string>>
        {
            new Dictionary<string, string>
            {
                ["accessType"] = "deny",
                ["type"] = "everyone",
                ["value"] = _tenantId,
            },
        };
    }

    /// <summary>Sort object names so parent objects are processed before their dependents.</summary>
    internal List<string> SortObjectNames(IEnumerable<string> objectNames)
    {
        var orderedNames = objectNames.Select(name => name).ToList();
        var objectNameSet = orderedNames.ToHashSet();
        var dependencyDepths = new Dictionary<string, int>();
        var visiting = new HashSet<string>();

        // Compute the parent-chain depth for topological sorting.
        int DependencyDepth(string name)
        {
            if (dependencyDepths.TryGetValue(name, out var known))
                return known;

            if (visiting.Contains(name))
                return 0;

            visiting.Add(name);
            var handler = _handlers.GetValueOrDefault(name);
            var parentObjectName = handler?.ParentObjectName;
            var depth = 0;
            if (!string.IsNullOrEmpty(parentObjectName) && objectNameSet.Contains(parentObjectName!))
                depth = DependencyDepth(parentObjectName!) + 1;
            visiting.Remove(name);
            dependencyDepths[name] = depth;
            return depth;
        }

        return orderedNames
            .OrderBy(name => DependencyDepth(name))
            .ThenBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Build a SOQL IN filter clause from a list of values.</summary>
    private static string BuildInFilter(string fieldName, List<string> values)
    {
        var quotedValues = string.Join(
            ", ",
            values
                .ToHashSet()
                .OrderBy(value => value, StringComparer.Ordinal)
                .Where(value => !string.IsNullOrEmpty(value))
                .Select(value => $"'{value}'"));
        return quotedValues.Length > 0 ? $"{fieldName} in ({quotedValues})" : "";
    }

    /// <summary>Return True if the OWD visibility grants public read access.</summary>
    private static bool IsPublicVisibility(EntityVisibility visibility)
    {
        return visibility is EntityVisibility.PublicReadOnly
            or EntityVisibility.PublicReadWrite
            or EntityVisibility.PublicReadWriteTransfer
            or EntityVisibility.All;
    }

    /// <summary>Return True if the OWD visibility is ControlledByParent.</summary>
    private static bool IsControlledByParent(EntityVisibility visibility)
    {
        return visibility == EntityVisibility.ControlledByParent;
    }

    /// <summary>Format visibility the way Python's str(EntityVisibility.X) renders it in log output.</summary>
    private static string VisibilityRepr(EntityVisibility visibility)
    {
        return visibility switch
        {
            EntityVisibility.None => "EntityVisibility.NONE",
            EntityVisibility.PublicReadOnly => "EntityVisibility.PUBLIC_READ_ONLY",
            EntityVisibility.PublicReadWrite => "EntityVisibility.PUBLIC_READ_WRITE",
            EntityVisibility.PublicReadWriteTransfer => "EntityVisibility.PUBLIC_READ_WRITE_TRANSFER",
            EntityVisibility.All => "EntityVisibility.ALL",
            EntityVisibility.ControlledByParent => "EntityVisibility.CONTROLLED_BY_PARENT",
            EntityVisibility.ControlledByCampaign => "EntityVisibility.CONTROLLED_BY_CAMPAIGN",
            EntityVisibility.ControlledByLeadOrContact => "EntityVisibility.CONTROLLED_BY_LEAD_OR_CONTACT",
            _ => visibility.ToString(),
        };
    }

    /// <summary>Format a list of strings the way Python's repr of a list of str renders in log output.</summary>
    private static string ReprList(IEnumerable<string> values)
    {
        return "[" + string.Join(", ", values.Select(v => $"'{v}'")) + "]";
    }

    /// <summary>
    /// Salesforce appends an extra label to the domain to make usernames globally
    /// unique (e.g. <c>rohith.kakumani.ext@nokia.com.cape2104</c>).  Strip the trailing
    /// label so the result can be matched against a normal AAD UPN / mail address.
    ///
    /// Returns <c>null</c> when stripping is not applicable (domain has ≤ 2 labels).
    /// </summary>
    private static string? StripSfUsernameSuffix(string username)
    {
        if (!username.Contains('@'))
            return null;
        var atIndex = username.LastIndexOf('@');
        var local = username[..atIndex];
        var domain = username[(atIndex + 1)..];
        var labels = domain.Split('.');
        if (labels.Length <= 2)
            return null;
        return $"{local}@{string.Join(".", labels[..^1])}";
    }
}
