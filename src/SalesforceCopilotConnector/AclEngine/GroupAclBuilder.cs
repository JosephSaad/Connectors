// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// AclEngine/GroupAclBuilder.cs
// ----------------------------
// Group-based ACL builder for content items.
//
// Replaces the user-only ACL resolution with group-reference ACLs that point
// to external groups created by the Identity Crawl.  This approach is more
// efficient (fewer ACEs per item) and automatically stays in sync when group
// membership changes.
//
// Enabled by setting ``USE_GROUP_ACL=true`` in the environment.  When disabled,
// the legacy user-only engines remain active.
//
// ACL Construction Rules:
//     - PUBLIC OWD   → single group ACE: "{Object}-TopLevel"
//     - PRIVATE OWD  → "{Object}-GlobalUsers" + per-share user/group ACEs
//     - ControlledByParent → inherits parent record's private ACLs
//       (parent shares fetched and cached lazily across chunks)

using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.AclEngine;

/// <summary>
/// Builds group-based ACLs for content items.
///
/// Each item's ACL references external groups (created by the identity crawl)
/// instead of listing individual users.  The Microsoft Graph framework resolves
/// group membership at search time.
///
/// Parameters
/// ----------
/// sfClient     : Authenticated <see cref="SalesforceClient"/>.
/// owdOverrides : Optional dict ``{object_name: owd_value}`` to override
///                Salesforce OWD settings.
/// parentMap    : ``{object_name: (parent_field, parent_object)}`` from config.
/// </summary>
public class GroupAclBuilder
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector.acl_engine.group_acl");

    // NOTE: several members below are `internal` (rather than `private`) so the
    // test project (InternalsVisibleTo) can inject fixture state the way the
    // Python tests assign `builder._owd_map`, `builder._users_by_id`, etc.
    internal readonly SalesforceClient _sf;
    internal readonly IdentityQueryClient _queryClient;
    private readonly Dictionary<string, string> _owdOverrides;
    private readonly Dictionary<string, (string ParentField, string ParentObject)> _parentMap;
    internal PrincipalMapper? _principalMapper;
    // Caches populated on first use
    internal Dictionary<string, EntityVisibility>? _owdMap;
    internal Dictionary<string, SfUser>? _usersById;
    internal Dictionary<string, SfGroup>? _groupsById;
    internal HashSet<string>? _frozenUsers;
    private readonly HashSet<string> _noShareTableTypes = new();
    // Lazy cache: {parent_record_id: [acl_entry, ...]}
    // Accumulates across chunks so repeated parent lookups are free.
    private readonly Dictionary<string, List<Dictionary<string, string>>> _parentAclCache = new();

    public GroupAclBuilder(
        SalesforceClient sfClient,
        Dictionary<string, string>? owdOverrides = null,
        Dictionary<string, (string ParentField, string ParentObject)>? parentMap = null,
        Dictionary<string, string>? owdFieldMap = null,
        PrincipalMapper? principalMapper = null,
        bool useEntityDefinitionOwd = false,
        List<string>? objectNames = null)
    {
        _sf = sfClient;
        _queryClient = new IdentityQueryClient(
            sfClient,
            owdFieldMap: owdFieldMap,
            useEntityDefinitionOwd: useEntityDefinitionOwd,
            objectNames: objectNames);
        _owdOverrides = owdOverrides ?? new Dictionary<string, string>();
        _parentMap = parentMap ?? new Dictionary<string, (string, string)>();
        _principalMapper = principalMapper;
    }

    /// <summary>Derive the share table API name for a given sObject type.</summary>
    internal static string ShareTableName(string objectType)
    {
        if (objectType.EndsWith("__c"))
            return objectType[..^3] + "__Share";
        return objectType + "Share";
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Pre-fetch the OWD map and return human-readable labels.
    ///
    /// Returns ``{object_type: "Private" | "Public Read" | ...}``.
    /// </summary>
    public async Task<Dictionary<string, string>> PrewarmOwdAsync()
    {
        var labels = new Dictionary<string, string>
        {
            ["None"] = "Private",
            ["Read"] = "Public Read",
            ["Edit"] = "Public Read/Write",
            ["ReadEditTransfer"] = "Public Read/Write/Transfer",
            ["ControlledByParent"] = "ControlledByParent",
            ["ControlledByLeadOrContact"] = "ControlledByParent",
            ["ControlledByCampaign"] = "ControlledByParent",
        };
        if (_owdMap is null)
        {
            _owdMap = await _queryClient.GetOrgWideDefaultsAsync();
            foreach (var (objName, rawOwd) in _owdOverrides)
                _owdMap[objName] = IdentityModels.ParseVisibility(rawOwd);
        }
        var result = new Dictionary<string, string>();
        foreach (var (obj, vis) in _owdMap)
            result[obj] = labels.GetValueOrDefault(vis.ToString(), vis.ToString());
        return result;
    }

    /// <summary>
    /// Resolve group-based ACLs for all records.
    ///
    /// Returns ``{object_type: {record_id: [acl_entry]}}``.
    ///
    /// Same shape as the legacy and new user-only engines so it is a
    /// drop-in replacement in ``graph/ingest.py``.
    /// </summary>
    public Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>> Resolve(
        Dictionary<string, List<JsonObject>> recordsByObjectType)
    {
        return ResolveAsync(recordsByObjectType).GetAwaiter().GetResult();
    }

    /// <summary>Async implementation of the resolve pipeline.</summary>
    public async Task<Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>>> ResolveAsync(
        Dictionary<string, List<JsonObject>> recordsByObjectType)
    {
        Logger.Info("[GroupACL] Starting group-based ACL resolution");

        // Load OWD map once
        if (_owdMap is null)
        {
            var t0 = Stopwatch.StartNew();
            _owdMap = await _queryClient.GetOrgWideDefaultsAsync();
            Logger.Info($"[GroupACL][TIMING] OWD query: {Secs1(t0)}s");
            // Apply overrides
            foreach (var (objName, rawOwd) in _owdOverrides)
                _owdMap[objName] = IdentityModels.ParseVisibility(rawOwd);
        }

        var aclMaps = new Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>>();

        foreach (var (objectType, records) in recordsByObjectType)
        {
            if (records is null || records.Count == 0)
                continue;
            Logger.Info($"[GroupACL] Resolving {records.Count} {objectType} record(s)");
            aclMaps[objectType] = await BuildAclMapAsync(objectType, records, aclMaps);
        }

        Logger.Info($"[GroupACL] Resolution complete: {aclMaps.Count} object type(s)");
        return aclMaps;
    }

    // ── Per-object-type resolution ────────────────────────────────────────────

    /// <summary>Build ACL map for one object type based on its OWD.</summary>
    internal async Task<Dictionary<string, List<Dictionary<string, string>>>> BuildAclMapAsync(
        string objectType,
        List<JsonObject> records,
        Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>> aclMaps)
    {
        var visibility = GetVisibility(objectType);

        if (IdentityModels.IsPublicVisibility(visibility))
        {
            Logger.Info($"[GroupACL] {objectType}: PUBLIC → TopLevel group");
            return BuildPublicAcls(objectType, records);
        }

        if (IdentityModels.IsControlledByParent(visibility))
        {
            if (_parentMap.TryGetValue(objectType, out var parentInfo))
            {
                var parentObj = parentInfo.ParentObject;
                var parentVis = GetVisibility(parentObj);
                if (IdentityModels.IsPublicVisibility(parentVis))
                {
                    Logger.Info($"[GroupACL] {objectType}: ControlledByParent, parent {parentObj} is PUBLIC");
                    return BuildPublicAcls(objectType, records);
                }
            }

            Logger.Info($"[GroupACL] {objectType}: ControlledByParent → inheriting parent share ACLs");
            return await BuildControlledByParentAclsAsync(objectType, records);
        }

        // PRIVATE
        Logger.Info($"[GroupACL] {objectType}: PRIVATE → per-record share-based ACLs");
        return await BuildPrivateAclsAsync(objectType, records);
    }

    // ── PUBLIC OWD ────────────────────────────────────────────────────────────

    /// <summary>All records get a grant-everyone ACE — no external group needed.</summary>
    private Dictionary<string, List<Dictionary<string, string>>> BuildPublicAcls(
        string objectType,
        List<JsonObject> records)
    {
        var acl = new List<Dictionary<string, string>>
        {
            new() { ["accessType"] = "grant", ["type"] = "everyone", ["value"] = "everyone" },
        };
        var map = new Dictionary<string, List<Dictionary<string, string>>>();
        foreach (var r in records)
        {
            var id = (string?)r["Id"];
            if (!string.IsNullOrEmpty(id))
                map[id] = acl;
        }
        return map;
    }

    // ── PRIVATE OWD ───────────────────────────────────────────────────────────

    /// <summary>
    /// Build per-record ACLs using share data.
    ///
    /// Each record gets:
    /// 1. The GlobalUsers group (ViewAll/ModifyAll admins)
    /// 2. Per-share ACEs: user ACEs for direct user shares, group ACEs for
    ///    role/queue/manager/organization shares
    /// </summary>
    private async Task<Dictionary<string, List<Dictionary<string, string>>>> BuildPrivateAclsAsync(
        string objectType,
        List<JsonObject> records)
    {
        // Ensure we have user and group data loaded
        if (_usersById is null)
        {
            var t0 = Stopwatch.StartNew();
            var users = await _queryClient.GetUsersWithRolesAsync(objectType);
            _usersById = new Dictionary<string, SfUser>();
            foreach (var u in users)
                _usersById[u.Id] = u;
            Logger.Info($"[GroupACL][TIMING] Load users for {objectType}: {Secs1(t0)}s ({_usersById.Count} users)");
        }

        if (_frozenUsers is null)
        {
            var t0 = Stopwatch.StartNew();
            _frozenUsers = await _queryClient.GetFrozenUserIdsAsync();
            Logger.Info($"[GroupACL][TIMING] Load frozen users: {Secs1(t0)}s ({_frozenUsers.Count} frozen)");
        }

        // Bulk-fetch shares so the per-record loop has data to process
        var shareT0 = Stopwatch.StartNew();
        await FetchAndInjectSharesAsync(objectType, records);
        Logger.Info($"[GroupACL][TIMING] Share fetch for {objectType}: {Secs1(shareT0)}s");

        var aclMap = new Dictionary<string, List<Dictionary<string, string>>>();
        var groupQueryCount = 0;
        var groupQueryTime = 0.0;

        foreach (var record in records)
        {
            var recordId = (string?)record["Id"] ?? "";
            if (string.IsNullOrEmpty(recordId))
                continue;

            var acls = new List<Dictionary<string, string>>
            {
                GroupAce(SfGroupIdFormats.GlobalUsers.Format(objectType)),
            };

            var shareRecords = (record["Shares"] as JsonObject)?["records"] as JsonArray ?? new JsonArray();
            var processedRoles = new HashSet<string>();
            var aclFailed = false;

            foreach (var shareNode in shareRecords)
            {
                var share = shareNode as JsonObject;
                var uogId = (string?)share?["UserOrGroupId"] ?? "";
                var uogType = (string?)(share?["UserOrGroup"] as JsonObject)?["Type"] ?? "";

                if (uogType == "User")
                {
                    var ok = await ProcessUserShareAsync(acls, uogId, objectType, processedRoles);
                    if (!ok)
                    {
                        aclFailed = true;
                        break;
                    }
                }
                else if (!string.IsNullOrEmpty(uogId))
                {
                    // Any non-User share (Queue, Role, Organization, etc.)
                    var gqT0 = Stopwatch.StartNew();
                    await ProcessGroupShareAsync(acls, uogId, objectType, processedRoles);
                    var gqDur = gqT0.Elapsed.TotalSeconds;
                    if (gqDur > 0.001)  // only count actual SOQL calls
                    {
                        groupQueryCount += 1;
                        groupQueryTime += gqDur;
                    }
                }
            }

            if (aclFailed)
                aclMap[recordId] = DenyEveryoneAcl();
            else
                aclMap[recordId] = acls;
        }

        if (groupQueryCount > 0)
        {
            Logger.Info(string.Create(
                CultureInfo.InvariantCulture,
                $"[GroupACL][TIMING] Group lookups for {objectType}: {groupQueryCount} SOQL queries in {groupQueryTime:F1}s ({groupQueryTime / groupQueryCount * 1000:F0} ms/query avg)"));
        }

        return aclMap;
    }

    /// <summary>
    /// Bulk-fetch share records from Salesforce and inject into record dicts.
    ///
    /// Queries ``{Object}Share`` in batches of <paramref name="batchSize"/> so
    /// <see cref="BuildPrivateAclsAsync"/> can read ``record["Shares"]["records"]``
    /// without per-record SOQL.
    ///
    /// Skips records that already have ``Shares`` populated (e.g. tests).
    ///
    /// ``internal virtual`` so tests can substitute it, mirroring the Python
    /// tests' monkeypatching of ``builder._fetch_and_inject_shares``.
    /// </summary>
    internal virtual async Task FetchAndInjectSharesAsync(
        string objectType,
        List<JsonObject> records,
        int batchSize = 200)
    {
        // Skip if all records already have shares injected
        var needsFetch = records
            .Where(r => !string.IsNullOrEmpty((string?)r["Id"]) && !r.ContainsKey("Shares"))
            .ToList();
        if (needsFetch.Count == 0)
            return;

        // Skip objects whose share table doesn't exist (e.g. Product2, Pricebook2)
        if (_noShareTableTypes.Contains(objectType))
            return;

        var shareTable = ShareTableName(objectType);
        var parentField = objectType.EndsWith("__c") ? "ParentId" : $"{objectType}Id";

        var recordIds = needsFetch.Select(r => (string)r["Id"]!).ToList();
        var sharesByRecord = recordIds.ToDictionary(rid => rid, _ => new List<JsonObject>());
        var totalShares = 0;

        for (var i = 0; i < recordIds.Count; i += batchSize)
        {
            var batch = recordIds.Skip(i).Take(batchSize).ToList();
            var idsIn = string.Join(", ", batch.Select(rid => $"'{rid}'"));
            var soql =
                $"SELECT {parentField}, UserOrGroupId, UserOrGroup.Type " +
                $"FROM {shareTable} " +
                $"WHERE {parentField} IN ({idsIn})";
            try
            {
                var rows = await _sf.QueryAllAsync(soql);
                foreach (var r in rows)
                {
                    var parentId = (string?)r[parentField];
                    if (!string.IsNullOrEmpty(parentId) && sharesByRecord.TryGetValue(parentId, out var shareList))
                    {
                        shareList.Add(new JsonObject
                        {
                            ["UserOrGroupId"] = (string?)r["UserOrGroupId"] ?? "",
                            ["UserOrGroup"] = r["UserOrGroup"]?.DeepClone() ?? new JsonObject(),
                        });
                        totalShares += 1;
                    }
                }
            }
            catch (Exception exc)
            {
                var excStr = exc.Message;
                if (excStr.Contains("INVALID_TYPE") || excStr.Contains("is not supported"))
                {
                    Logger.Info(
                        $"[GroupACL] Share table {shareTable} does not exist — {objectType} uses non-standard sharing; skipping");
                    _noShareTableTypes.Add(objectType);
                    return;
                }
                Logger.Warning(
                    $"[GroupACL] Bulk share fetch from {shareTable} failed (batch {i / batchSize + 1}): {exc.Message}");
            }
        }

        // Inject into records
        foreach (var record in needsFetch)
        {
            var rid = (string?)record["Id"] ?? "";
            if (sharesByRecord.TryGetValue(rid, out var shares))
            {
                var arr = new JsonArray();
                foreach (var s in shares)
                    arr.Add(s);
                record["Shares"] = new JsonObject { ["records"] = arr };
            }
        }

        Logger.Info(
            $"[GroupACL] Fetched {totalShares} share(s) for {recordIds.Count} {objectType} record(s) from {shareTable}");
    }

    /// <summary>
    /// Add ACEs for a direct user share.
    ///
    /// Returns True on success, False if user ACE resolution failed
    /// (caller should set the entire item ACL to deny-everyone).
    /// </summary>
    private async Task<bool> ProcessUserShareAsync(
        List<Dictionary<string, string>> acls,
        string userId,
        string objectType,
        HashSet<string> processedRoles)
    {
        var user = (_usersById ?? new Dictionary<string, SfUser>()).GetValueOrDefault(userId);
        var frozen = _frozenUsers ?? new HashSet<string>();

        if (user is null || user.PermissionSets.Count == 0 || frozen.Contains(user.Id))
            return true;  // Skipped user, not a failure

        // Resolve user to AAD GUID — try FederationIdentifier → stripped UserName → Email → employeeId
        // in order, stopping at the first candidate that resolves successfully.
        // _resolve_identifier chains to _lookup_graph_user_id which covers both
        // Attempt 1 (direct path) and Attempt 2 (filter, including employeeId for alphanumeric identifiers).
        Dictionary<string, string>? ace = null;
        var candidates = BestUserIdentifiers(user);
        foreach (var identifier in candidates)
        {
            if (_principalMapper is not null)
            {
                var resolved = await _principalMapper.ResolveIdentifierAsync(identifier);
                if (!string.IsNullOrEmpty(resolved))
                {
                    ace = UserAceAad(resolved);
                    if (ace is not null)
                        break;  // resolved and valid GUID
                }
            }
            else
            {
                ace = UserAceAad(identifier);
                if (ace is not null)
                    break;  // identifier was already a GUID
            }
        }

        if (candidates.Count == 0)
        {
            Logger.Warning(
                $"[GroupACL] User {userId} ({user.Name}) — no valid identifier (FederationIdentifier/UserName/Email/employeeId all missing or invalid)");
        }
        else if (ace is null)
        {
            Logger.Warning(
                $"[GroupACL] User {userId} ({user.Name}) — tried {candidates.Count} candidate(s) {FormatList(candidates)}, none resolved to AAD GUID → ACL resolution failed");
        }

        if (ace is not null)
        {
            acls.Add(ace);
        }
        else
        {
            // Cannot resolve user to GUID — ACL resolution failed
            return false;
        }

        // Parent role group ACE (hierarchy-based implicit sharing)
        if (!string.IsNullOrEmpty(user.ParentRoleId) && !processedRoles.Contains(user.ParentRoleId))
        {
            processedRoles.Add(user.ParentRoleId);
            acls.Add(GroupAce(SfGroupIdFormats.Role.Format(objectType, user.ParentRoleId)));
        }

        return true;
    }

    /// <summary>Add ACE for a group-type share.</summary>
    private async Task ProcessGroupShareAsync(
        List<Dictionary<string, string>> acls,
        string groupId,
        string objectType,
        HashSet<string> processedRoles)
    {
        // Lazy-load group details
        _groupsById ??= new Dictionary<string, SfGroup>();

        if (!_groupsById.ContainsKey(groupId))
        {
            var groups = await _queryClient.GetGroupsByIdsAsync(new List<string> { groupId });
            foreach (var g in groups)
                _groupsById[g.Id] = g;
        }

        var group = _groupsById.GetValueOrDefault(groupId);
        if (group is null)
            return;

        string? aceId = null;
        var gt = group.Type;

        if (gt == UserOrGroupType.Role)
        {
            if (!processedRoles.Contains(group.RelatedId))
            {
                processedRoles.Add(group.RelatedId);
                aceId = SfGroupIdFormats.Role.Format(objectType, group.RelatedId);
            }
        }
        else if (gt is UserOrGroupType.RoleAndSubordinates or UserOrGroupType.RoleAndSubordinatesInternal)
        {
            aceId = SfGroupIdFormats.RoleAndSubordinates.Format(objectType, group.RelatedId);
        }
        else if (gt == UserOrGroupType.Organization)
        {
            aceId = SfGroupIdFormats.AllInternalUsers.Format(objectType);
        }
        else if (gt == UserOrGroupType.Manager)
        {
            aceId = SfGroupIdFormats.Manager.Format(objectType, group.RelatedId);
        }
        else if (gt == UserOrGroupType.ManagerAndSubordinatesInternal)
        {
            aceId = SfGroupIdFormats.ManagerAndSubordinates.Format(objectType, group.RelatedId);
        }
        else if (gt == UserOrGroupType.Territory)
        {
            aceId = SfGroupIdFormats.Territory.Format(objectType, group.RelatedId);
        }
        else if (gt is UserOrGroupType.TerritoryAndSubordinates or UserOrGroupType.TerritoryAndSubordinatesInternal)
        {
            aceId = SfGroupIdFormats.TerritoryAndSubordinates.Format(objectType, group.RelatedId);
        }
        else
        {
            // Regular / Queue → Public Group
            aceId = SfGroupIdFormats.PublicGroup.Format(objectType, groupId);
        }

        if (!string.IsNullOrEmpty(aceId))
            acls.Add(GroupAce(aceId));
    }

    // ── CONTROLLED BY PARENT ─────────────────────────────────────────────────

    /// <summary>
    /// Build ACLs for ControlledByParent objects by inheriting parent record ACLs.
    ///
    /// For each child record:
    /// 1. Look up the parent record ID (e.g. Contact.AccountId).
    /// 2. If the parent's ACL is already cached, reuse it.
    /// 3. Otherwise, fetch the parent record + its share entries and build
    ///    private ACLs using the same logic as <see cref="BuildPrivateAclsAsync"/>.
    /// 4. The child inherits the parent's ACL entries plus its own
    ///    ``{child_object}-GlobalUsers`` group (ViewAll/ModifyAll admins
    ///    for the child object type).
    ///
    /// The ``_parentAclCache`` accumulates across chunks so repeated
    /// parent lookups are free.
    /// </summary>
    private async Task<Dictionary<string, List<Dictionary<string, string>>>> BuildControlledByParentAclsAsync(
        string objectType,
        List<JsonObject> records)
    {
        if (!_parentMap.TryGetValue(objectType, out var parentInfo))
        {
            Logger.Warning(
                $"[GroupACL] {objectType}: ControlledByParent but no parent_map entry — " +
                "falling back to GlobalUsers + deny-everyone");
            var fallback = new Dictionary<string, List<Dictionary<string, string>>>();
            foreach (var r in records)
            {
                var id = (string?)r["Id"];
                if (!string.IsNullOrEmpty(id))
                    fallback[id] = DenyEveryoneAcl();
            }
            return fallback;
        }

        var (parentField, parentObj) = parentInfo;

        // Ensure user data is loaded (needed for owner resolution on parent records)
        if (_usersById is null)
        {
            var users = await _queryClient.GetUsersWithRolesAsync(objectType);
            _usersById = new Dictionary<string, SfUser>();
            foreach (var u in users)
                _usersById[u.Id] = u;
        }

        if (_frozenUsers is null)
            _frozenUsers = await _queryClient.GetFrozenUserIdsAsync();

        // 1. Collect unique parent IDs that are NOT already cached
        var parentIdsNeeded = new HashSet<string>();
        foreach (var record in records)
        {
            var pid = (string?)record[parentField];
            if (!string.IsNullOrEmpty(pid) && !_parentAclCache.ContainsKey(pid))
                parentIdsNeeded.Add(pid);
        }

        // 2. Fetch and resolve uncached parent records
        if (parentIdsNeeded.Count > 0)
        {
            var t0 = Stopwatch.StartNew();
            var parentRecords = await FetchParentRecordsAsync(parentObj, parentIdsNeeded);
            Logger.Info(
                $"[GroupACL][TIMING] Fetch {parentRecords.Count} parent {parentObj} record(s): {Secs1(t0)}s");

            // Build private ACLs for the parent records (reuse existing logic).
            // _build_private_acls internally calls _fetch_and_inject_shares,
            // so we don't need to call it separately here.
            var parentAclMap = await BuildPrivateAclsAsync(parentObj, parentRecords);

            // Store in cache
            foreach (var (pid, acls) in parentAclMap)
                _parentAclCache[pid] = acls;

            Logger.Info(
                $"[GroupACL] Cached {parentAclMap.Count} new parent {parentObj} ACL(s) (total cache: {_parentAclCache.Count})");
        }

        // 3. Map each child record to its parent's ACL
        var cacheHits = 0;
        var cacheMisses = 0;
        var aclMap = new Dictionary<string, List<Dictionary<string, string>>>();

        // Child's own GlobalUsers group — admins with ViewAll/ModifyAll on the
        // child object type can see all children regardless of parent ACL.
        var childGlobalAce = GroupAce(SfGroupIdFormats.GlobalUsers.Format(objectType));

        foreach (var record in records)
        {
            var recordId = (string?)record["Id"] ?? "";
            if (string.IsNullOrEmpty(recordId))
                continue;

            var pid = (string?)record[parentField];
            if (!string.IsNullOrEmpty(pid) && _parentAclCache.TryGetValue(pid, out var parentAcls))
            {
                cacheHits += 1;
                // Inherit parent ACL + child's own GlobalUsers
                // Prepend child GlobalUsers if not already present
                // (parent ACLs have parent's GlobalUsers; child needs its own too)
                var combined = new List<Dictionary<string, string>> { childGlobalAce };
                combined.AddRange(parentAcls);
                aclMap[recordId] = combined;
            }
            else
            {
                cacheMisses += 1;
                if (string.IsNullOrEmpty(pid))
                {
                    // No parent reference — fall back to owner-based ACL
                    var ownerAcl = await ResolveOwnerAclAsync(record, objectType, childGlobalAce);
                    if (ownerAcl is not null)
                        aclMap[recordId] = ownerAcl;
                    else
                        aclMap[recordId] = DenyEveryoneAcl();
                    Logger.Debug(
                        $"[GroupACL] {objectType}/{recordId}: no {parentField} value — owner-based ACL");
                }
                else
                {
                    Logger.Warning(
                        $"[GroupACL] {objectType}/{recordId}: parent {parentObj}/{pid} not resolved — deny-everyone");
                    aclMap[recordId] = DenyEveryoneAcl();
                }
            }
        }

        Logger.Info(
            $"[GroupACL] {objectType} CBP: {aclMap.Count} records, {cacheHits} cache hits, {cacheMisses} misses (no parent)");
        return aclMap;
    }

    /// <summary>
    /// Build a GlobalUsers + owner ACL for a record.
    ///
    /// Returns the ACL list, or ``null`` if the owner cannot be resolved
    /// (caller should fall back to deny-everyone).
    /// </summary>
    private async Task<List<Dictionary<string, string>>?> ResolveOwnerAclAsync(
        JsonObject record,
        string objectType,
        Dictionary<string, string> globalAce)
    {
        var acls = new List<Dictionary<string, string>> { globalAce };
        var ownerId = (string?)record["OwnerId"] ?? "";
        if (string.IsNullOrEmpty(ownerId))
            return acls;  // GlobalUsers only — no owner to resolve

        var owner = (_usersById ?? new Dictionary<string, SfUser>()).GetValueOrDefault(ownerId);
        if (owner is null)
        {
            Logger.Debug(
                $"[GroupACL] Owner {ownerId} not in user cache for {objectType}/{(string?)record["Id"] ?? ""} — GlobalUsers only");
            return acls;
        }

        var candidates = BestUserIdentifiers(owner);
        foreach (var identifier in candidates)
        {
            if (_principalMapper is not null)
            {
                var resolved = await _principalMapper.ResolveIdentifierAsync(identifier);
                if (!string.IsNullOrEmpty(resolved))
                {
                    var ace = UserAceAad(resolved);
                    if (ace is not null)
                    {
                        acls.Add(ace);
                        return acls;
                    }
                }
            }
            else
            {
                var ace = UserAceAad(identifier);
                if (ace is not null)
                {
                    acls.Add(ace);
                    return acls;
                }
            }
        }

        // Couldn't resolve owner — still return GlobalUsers-only ACL
        Logger.Debug(
            $"[GroupACL] Owner {ownerId} ({owner.Name}) unresolvable — GlobalUsers only for {objectType}/{(string?)record["Id"] ?? ""}");
        return acls;
    }

    /// <summary>
    /// Fetch minimal parent records (Id + OwnerId) for ACL resolution.
    ///
    /// Returns synthetic record dicts suitable for <see cref="BuildPrivateAclsAsync"/>.
    /// </summary>
    private async Task<List<JsonObject>> FetchParentRecordsAsync(
        string parentObj,
        HashSet<string> parentIds,
        int batchSize = 200)
    {
        var records = new List<JsonObject>();
        var parentIdList = parentIds.OrderBy(x => x, StringComparer.Ordinal).ToList();

        for (var i = 0; i < parentIdList.Count; i += batchSize)
        {
            var batch = parentIdList.Skip(i).Take(batchSize).ToList();
            var idsIn = string.Join(", ", batch.Select(pid => $"'{pid}'"));
            var soql = $"SELECT Id, OwnerId FROM {parentObj} WHERE Id IN ({idsIn})";
            try
            {
                var rows = await _sf.QueryAllAsync(soql);
                foreach (var row in rows)
                {
                    records.Add(new JsonObject
                    {
                        ["Id"] = (string?)row["Id"],
                        ["OwnerId"] = (string?)row["OwnerId"] ?? "",
                    });
                }
            }
            catch (Exception exc)
            {
                Logger.Warning(
                    $"[GroupACL] Failed to fetch parent {parentObj} records (batch {i / batchSize + 1}): {exc.Message}");
            }
        }

        Logger.Info(
            $"[GroupACL] Fetched {records.Count}/{parentIds.Count} parent {parentObj} record(s)");
        return records;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Get the effective OWD visibility for an object type.</summary>
    private EntityVisibility GetVisibility(string objectType)
    {
        if (_owdMap is not null && _owdMap.TryGetValue(objectType, out var vis))
            return vis;
        return EntityVisibility.None;  // Safe default
    }

    // ── Identifier resolution helper ────────────────────────────────────────

    /// <summary>
    /// Return an ordered list of all valid identifiers for AAD ACE resolution.
    ///
    /// Priority: FederationIdentifier → stripped UserName → Email
    /// Each candidate is de-duplicated.  Bare values without '@' that are not
    /// GUIDs (e.g. numeric Salesforce IDs like '61273255') are rejected.
    ///
    /// Returning a list (instead of just the first match) lets callers try every
    /// candidate through Graph until one resolves — mirroring the behaviour of
    /// PrincipalMapper._resolve_principal.
    /// </summary>
    internal static List<string> BestUserIdentifiers(SfUser user)
    {
        // Strip the single Salesforce-appended org label from a username.
        //
        // Rules (mirrors principal_mapper._strip_sf_username_suffix):
        // - Must contain exactly one '@'.
        // - Domain must have MORE than 2 labels — 'acme.com' is never stripped.
        // - Only the LAST label is removed: 'acme.com.sandbox' → 'acme.com'.
        static string? StripSfSuffix(string username)
        {
            if (!username.Contains('@'))
                return null;
            var atIdx = username.LastIndexOf('@');
            var local = username[..atIdx];
            var domain = username[(atIdx + 1)..];
            var labels = domain.Split('.');
            if (labels.Length <= 2)
                return null;
            return $"{local}@{string.Join(".", labels[..^1])}";
        }

        var seen = new HashSet<string>();
        var candidates = new List<string>();

        void Add(string? val)
        {
            if (string.IsNullOrEmpty(val))
                return;
            val = val.Trim();
            if (string.IsNullOrEmpty(val) || seen.Contains(val))
                return;
            // Reject bare values that have no '@' and are not GUIDs and are not
            // purely alphanumeric (which may be an employeeId, e.g. 'E12345').
            // e.g. mixed garbage like '61273255abc!!' is still rejected.
            if (!val.Contains('@') && !LooksLikeGuid(val) && !IsAlnum(val))
            {
                Logger.Debug(
                    $"[GroupACL] Skipping identifier '{val}' for user {user.Id} — not a UPN, email, GUID, or alphanumeric employeeId");
                return;
            }
            seen.Add(val);
            candidates.Add(val);
        }

        // FederationIdentifier (raw, then stripped)
        Add(user.FederationIdentifier);
        if (!string.IsNullOrEmpty(user.FederationIdentifier))
            Add(StripSfSuffix(user.FederationIdentifier));

        // UserName (stripped first — the raw SF username is rarely a valid UPN)
        if (!string.IsNullOrEmpty(user.UserName))
        {
            Add(StripSfSuffix(user.UserName));
            Add(user.UserName);  // raw as last resort
        }

        // Email
        Add(user.Email);

        return candidates;
    }

    /// <summary>Return the single highest-priority identifier (first from BestUserIdentifiers).</summary>
    internal static string? BestUserIdentifier(SfUser user)
    {
        var candidates = BestUserIdentifiers(user);
        return candidates.Count > 0 ? candidates[0] : null;
    }

    // ── Module-level ACE factories ────────────────────────────────────────────

    /// <summary>
    /// Return a deny-everyone ACL.
    ///
    /// Used when ACL resolution fails for an item (e.g. user cannot be resolved
    /// to an AAD GUID).  The item will be ingested but hidden from all users.
    /// </summary>
    internal static List<Dictionary<string, string>> DenyEveryoneAcl()
    {
        return new List<Dictionary<string, string>>
        {
            new() { ["accessType"] = "deny", ["type"] = "everyone", ["value"] = "everyone" },
        };
    }

    /// <summary>Create a Group ACE referencing an external group.</summary>
    internal static Dictionary<string, string> GroupAce(string groupId)
    {
        return new Dictionary<string, string>
        {
            ["accessType"] = "grant",
            ["type"] = "externalGroup",
            ["value"] = groupId,
        };
    }

    /// <summary>
    /// Create a User ACE using AAD identity.
    ///
    /// Returns null if the identifier is not a valid GUID — Graph API rejects
    /// non-GUID values with ``InvalidGuid`` error.
    /// </summary>
    internal static Dictionary<string, string>? UserAceAad(string federationIdentifier)
    {
        if (!LooksLikeGuid(federationIdentifier))
            return null;
        return new Dictionary<string, string>
        {
            ["accessType"] = "grant",
            ["type"] = "user",
            ["value"] = federationIdentifier,
        };
    }

    /// <summary>
    /// Create a User ACE with external identity.
    ///
    /// Returns null — raw Salesforce IDs (005...) are never valid GUIDs and
    /// Graph API rejects them.  Users without AAD GUIDs should be resolved
    /// via the identity crawl group membership instead.
    /// </summary>
    internal static Dictionary<string, string>? UserAceExternal(SfUser user)
    {
        return null;
    }

    /// <summary>Return True if <paramref name="value"/> looks like an AAD Object GUID.</summary>
    internal static bool LooksLikeGuid(string value)
    {
        var parts = value.Trim().Split('-');
        if (parts.Length != 5)
            return false;
        int[] expected = { 8, 4, 4, 4, 12 };
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length != expected[i] || !parts[i].All(c => "0123456789abcdefABCDEF".Contains(c)))
                return false;
        }
        return true;
    }

    // ── Formatting helpers (mirror Python logging repr) ───────────────────────

    /// <summary>Mirror Python's ``str.isalnum()``: non-empty and all alphanumeric.</summary>
    private static bool IsAlnum(string value)
    {
        return value.Length > 0 && value.All(char.IsLetterOrDigit);
    }

    /// <summary>Format a list of strings the way Python repr() renders it: ['a', 'b'].</summary>
    private static string FormatList(IEnumerable<string> items)
    {
        return "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";
    }

    /// <summary>Format elapsed seconds with one decimal (invariant), mirroring Python "%.1f".</summary>
    private static string Secs1(Stopwatch sw)
    {
        return sw.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
    }
}
