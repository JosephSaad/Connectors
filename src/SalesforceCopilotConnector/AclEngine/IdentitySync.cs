// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// AclEngine/IdentitySync.cs
// -------------------------
// Identity Crawl handler — creates and populates external groups in Microsoft
// Graph that mirror Salesforce's sharing model.
//
// The identity crawl runs as a separate scenario from content ingestion.  It
// creates the external groups that content item ACLs reference (via
// ``group_acl_builder.py``).
//
// Architecture
// ------------
// ``IdentitySyncHandler``
//     Top-level orchestrator.  Call ``RunFullCrawl()`` or
//     ``RunIncrementalCrawl()`` (both produce the same output since the
//     connector always emits complete membership).
//
// ``IdentityCrawlEnumerator``
//     List phase — queries OWD and emits one top-level group per SF object.
//
// ``IdentityGatherer``
//     Gather phase — populates each group with users and child groups based
//     on the object's OWD and share records.
//
// User Removal
// ------------
// Both full and incremental crawls emit the **complete current membership**
// every time.  The framework detects stale edges (members present in the
// previous crawl but absent in the current one) and removes them
// automatically.  This module **never** explicitly removes users or groups.

using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.AclEngine;

// ── Result containers ─────────────────────────────────────────────────────────

/// <summary>Represents an external group and its membership.</summary>
public class GroupMembership
{
    public string GroupId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public List<SfUser> Users { get; set; } = new();
    public List<ChildGroupRef> ChildGroups { get; set; } = new();
    public Dictionary<string, object?> Metadata { get; set; } = new();
}

/// <summary>Reference to a child group that should be nested inside a parent group.</summary>
public class ChildGroupRef
{
    public string GroupId { get; set; } = "";
    public GroupIdentityType GroupType { get; set; }
    public bool NeedsGather { get; set; } = true;
    public Dictionary<string, object?> Metadata { get; set; } = new();
}

/// <summary>Metadata for a top-level group emitted during the list phase.</summary>
public class TopLevelGroupInfo
{
    public string GroupId { get; set; } = "";
    public string ObjectName { get; set; } = "";
    public EntityVisibility Owd { get; set; }
    public string DisplayName { get; set; } = "";
}

/// <summary>Complete result of an identity crawl run.</summary>
public class IdentityCrawlResult
{
    public List<TopLevelGroupInfo> TopLevelGroups { get; set; } = new();
    public List<GroupMembership> GatheredGroups { get; set; } = new();
    public int TotalUsersEmitted { get; set; }
    public int TotalGroupsEmitted { get; set; }
}

// ── Main handler ──────────────────────────────────────────────────────────────

/// <summary>
/// Top-level handler for identity crawl operations.
///
/// Parameters
/// ----------
/// sfClient        : Authenticated <see cref="SalesforceClient"/>.
/// objectNames     : List of SF object names to crawl (e.g. ["Account", "Lead"]).
/// parentMap       : ``{object_name: (parent_field, parent_object)}`` from config.
/// owdOverrides    : Optional OWD overrides.
/// </summary>
public class IdentitySyncHandler
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector.acl_engine.identity");

    private readonly SalesforceClient _sf;
    private readonly List<string> _objectNames;
    private readonly Dictionary<string, (string ParentField, string ParentObject)> _parentMap;
    private readonly Dictionary<string, string> _owdOverrides;
    // Internal + mutable so tests can substitute the query client, mirroring the
    // Python tests' `handler._query_client = instance` after patching the class.
    internal IdentityQueryClient _queryClient;

    public IdentitySyncHandler(
        SalesforceClient sfClient,
        List<string> objectNames,
        Dictionary<string, (string ParentField, string ParentObject)>? parentMap = null,
        Dictionary<string, string>? owdOverrides = null,
        Dictionary<string, string>? owdFieldMap = null,
        bool useEntityDefinitionOwd = false)
    {
        _sf = sfClient;
        _objectNames = objectNames;
        _parentMap = parentMap ?? new Dictionary<string, (string, string)>();
        _owdOverrides = owdOverrides ?? new Dictionary<string, string>();
        _queryClient = new IdentityQueryClient(
            sfClient,
            owdFieldMap: owdFieldMap,
            useEntityDefinitionOwd: useEntityDefinitionOwd,
            objectNames: objectNames);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Execute a full identity crawl (synchronous wrapper).
    ///
    /// Returns an <see cref="IdentityCrawlResult"/> containing all top-level groups
    /// and their fully populated memberships.
    /// </summary>
    public IdentityCrawlResult RunFullCrawl()
    {
        return RunFullCrawlAsync().GetAwaiter().GetResult();
    }

    /// <summary>Async implementation of the full identity crawl.</summary>
    public async Task<IdentityCrawlResult> RunFullCrawlAsync()
    {
        var result = new IdentityCrawlResult();

        // Phase 1: List — emit top-level groups
        Logger.Info("[IdentitySync] Phase 1: Enumerating top-level groups");
        var enumerator = new IdentityCrawlEnumerator(
            _queryClient,
            _objectNames,
            _parentMap,
            _owdOverrides);
        result.TopLevelGroups = await enumerator.EnumerateAsync();

        Logger.Info(
            $"[IdentitySync] Emitted {result.TopLevelGroups.Count} top-level group(s)");

        // Phase 2: Gather — populate each group
        Logger.Info("[IdentitySync] Phase 2: Gathering group memberships");
        var gatherer = new IdentityGatherer(_queryClient);

        foreach (var topGroup in result.TopLevelGroups)
        {
            Logger.Info(
                $"[IdentitySync] Gathering {topGroup.GroupId} (OWD={topGroup.Owd.Value()})");
            var membership = await gatherer.BuildTopLevelGroupAsync(
                topGroup.ObjectName,
                topGroup.Owd);
            result.GatheredGroups.Add(membership);
            result.TotalUsersEmitted += membership.Users.Count;
            result.TotalGroupsEmitted += 1;

            // Gather child groups
            foreach (var childRef in membership.ChildGroups)
            {
                if (childRef.NeedsGather)
                {
                    var childMembership = await gatherer.GatherChildGroupAsync(
                        topGroup.ObjectName,
                        childRef);
                    result.GatheredGroups.Add(childMembership);
                    result.TotalUsersEmitted += childMembership.Users.Count;
                    result.TotalGroupsEmitted += 1;
                }
            }
        }

        Logger.Info(
            $"[IdentitySync] Crawl complete: {result.TotalGroupsEmitted} groups, {result.TotalUsersEmitted} user memberships");
        return result;
    }

    /// <summary>
    /// Execute an incremental identity crawl.
    ///
    /// Per the Salesforce connector pattern, incremental crawl emits
    /// the SAME output as a full crawl.  The framework handles stale
    /// edge detection automatically.
    /// </summary>
    public IdentityCrawlResult RunIncrementalCrawl()
    {
        return RunFullCrawl();
    }
}

// ── List phase ────────────────────────────────────────────────────────────────

/// <summary>
/// Emits one <see cref="TopLevelGroupInfo"/> per configured SF object.
///
/// Queries OWD from Salesforce and determines the effective visibility
/// for each object (handling ControlledByParent resolution).
/// </summary>
public class IdentityCrawlEnumerator
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector.acl_engine.identity");

    private readonly IdentityQueryClient _query;
    private readonly List<string> _objectNames;
    private readonly Dictionary<string, (string ParentField, string ParentObject)> _parentMap;
    private readonly Dictionary<string, string> _owdOverrides;

    public IdentityCrawlEnumerator(
        IdentityQueryClient queryClient,
        List<string> objectNames,
        Dictionary<string, (string ParentField, string ParentObject)> parentMap,
        Dictionary<string, string> owdOverrides)
    {
        _query = queryClient;
        _objectNames = objectNames;
        _parentMap = parentMap;
        _owdOverrides = owdOverrides;
    }

    /// <summary>Query OWD and emit top-level groups for all configured objects.</summary>
    public async Task<List<TopLevelGroupInfo>> EnumerateAsync()
    {
        var owdMap = await _query.GetOrgWideDefaultsAsync();

        // Apply overrides
        foreach (var (objName, rawOwd) in _owdOverrides)
            owdMap[objName] = IdentityModels.ParseVisibility(rawOwd);

        var groups = new List<TopLevelGroupInfo>();
        foreach (var objectName in _objectNames)
        {
            var visibility = owdMap.GetValueOrDefault(objectName, EntityVisibility.None);

            // Handle ControlledByParent: check if parent is public
            if (IdentityModels.IsControlledByParent(visibility))
            {
                if (_parentMap.TryGetValue(objectName, out var parentInfo))
                {
                    var parentObj = parentInfo.ParentObject;
                    var parentVis = owdMap.GetValueOrDefault(parentObj, EntityVisibility.None);
                    if (IdentityModels.IsPublicVisibility(parentVis))
                        visibility = parentVis;
                }
            }

            var groupId = SfGroupIdFormats.TopLevel.Format(objectName);
            groups.Add(new TopLevelGroupInfo
            {
                GroupId = groupId,
                ObjectName = objectName,
                Owd = visibility,
                DisplayName = objectName,
            });
            Logger.Info(
                $"[IdentityEnum] {objectName} → {groupId} (OWD={visibility.Value()})");
        }

        return groups;
    }
}

// ── Gather phase ──────────────────────────────────────────────────────────────

/// <summary>
/// Core logic for populating identity groups with users and child groups.
///
/// Handles both top-level groups (PUBLIC vs PRIVATE OWD) and child groups
/// (roles, public groups, managers, all-internal-users, global-access-users).
/// </summary>
public class IdentityGatherer
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector.acl_engine.identity");

    private readonly IdentityQueryClient _query;
    // Caches populated during crawl
    private Dictionary<string, string>? _roleHierarchy;
    private HashSet<string>? _rolesAssigned;

    public IdentityGatherer(IdentityQueryClient queryClient)
    {
        _query = queryClient;
    }

    /// <summary>
    /// Build the top-level group for an object.
    ///
    /// For PUBLIC OWD: populates with all authorized users.
    /// For PRIVATE OWD: creates child groups from share data.
    /// </summary>
    public async Task<GroupMembership> BuildTopLevelGroupAsync(
        string objectName,
        EntityVisibility visibility)
    {
        var groupId = SfGroupIdFormats.TopLevel.Format(objectName);
        var membership = new GroupMembership
        {
            GroupId = groupId,
            DisplayName = objectName,
            Metadata = new Dictionary<string, object?>
            {
                ["ObjectName"] = objectName,
                ["OrgWideDefault"] = visibility.Value(),
            },
        };

        if (IdentityModels.IsPublicVisibility(visibility))
        {
            // PUBLIC OWD: content items use grant-everyone ACL, so no
            // external group membership is needed.  Skip the expensive
            // authorized-users query entirely.
            Logger.Info(
                $"[IdentityGather] {objectName} PUBLIC: skipped (grant-everyone used for content ACLs)");
        }
        else
        {
            // PRIVATE OWD: build child group structure
            await BuildPrivateChildrenAsync(membership, objectName);
        }

        return membership;
    }

    /// <summary>Populate a child group with its members.</summary>
    public async Task<GroupMembership> GatherChildGroupAsync(
        string objectName,
        ChildGroupRef childRef)
    {
        var membership = new GroupMembership
        {
            GroupId = childRef.GroupId,
            Metadata = childRef.Metadata,
        };
        var groupType = childRef.GroupType;

        if (groupType == GroupIdentityType.GlobalAccessUsers)
        {
            if (childRef.Metadata.TryGetValue("use_read_permission", out var useRead) && useRead is true)
            {
                // Object has no share table — grant all users with Read permission
                membership.Users = await _query.GetAuthorizedUsersAsync(objectName);
            }
            else
            {
                membership.Users = await _query.GetGlobalAccessUsersAsync(objectName);
            }
        }
        else if (groupType is GroupIdentityType.RoleWithParent or GroupIdentityType.RoleWithoutParent)
        {
            var roleId = childRef.Metadata.GetValueOrDefault("RoleId") as string ?? "";
            var parentRoleId = childRef.Metadata.GetValueOrDefault("ParentRoleId") as string ?? "";

            membership.Users = await _query.GetUsersForRoleAsync(roleId);

            // Nest parent role group (edge only, not re-gathered)
            if (!string.IsNullOrEmpty(parentRoleId))
            {
                membership.ChildGroups.Add(new ChildGroupRef
                {
                    GroupId = SfGroupIdFormats.Role.Format(objectName, parentRoleId),
                    GroupType = GroupIdentityType.RoleWithParent,
                    NeedsGather = false,
                });
            }
        }
        else if (groupType is GroupIdentityType.RoleAndSubWithParent or GroupIdentityType.RoleAndSubWithoutParent)
        {
            var roleId = childRef.Metadata.GetValueOrDefault("RoleId") as string ?? "";
            var parentRoleId = childRef.Metadata.GetValueOrDefault("ParentRoleId") as string ?? "";
            var childRoles = childRef.Metadata.GetValueOrDefault("ChildRoles") as List<string> ?? new List<string>();

            membership.Users = await _query.GetUsersForRoleAsync(roleId);

            // Nest child role sub-groups
            foreach (var childRoleId in childRoles)
            {
                membership.ChildGroups.Add(new ChildGroupRef
                {
                    GroupId = SfGroupIdFormats.RoleAndSubordinatesNoParents.Format(objectName, childRoleId),
                    GroupType = GroupIdentityType.RoleAndSubWithoutParent,
                    NeedsGather = false,
                });
            }

            // Nest parent role
            if (!string.IsNullOrEmpty(parentRoleId))
            {
                membership.ChildGroups.Add(new ChildGroupRef
                {
                    GroupId = SfGroupIdFormats.Role.Format(objectName, parentRoleId),
                    GroupType = GroupIdentityType.RoleWithParent,
                    NeedsGather = false,
                });
            }
        }
        else if (groupType == GroupIdentityType.AllInternalUsers)
        {
            membership.Users = await _query.GetAuthorizedUsersAsync(objectName);
        }
        else if (groupType == GroupIdentityType.PublicGroup)
        {
            var pgId = childRef.Metadata.GetValueOrDefault("PublicGroupId") as string ?? "";
            membership.Users = await _query.GetGroupMemberUsersAsync(pgId);
        }
        else if (groupType == GroupIdentityType.Manager)
        {
            var relatedId = childRef.Metadata.GetValueOrDefault("RelatedId") as string ?? "";
            var user = await _query.GetUserByIdAsync(relatedId);
            if (user is not null)
                membership.Users = new List<SfUser> { user };
        }
        else if (groupType == GroupIdentityType.ManagerAndSubordinates)
        {
            var relatedId = childRef.Metadata.GetValueOrDefault("RelatedId") as string ?? "";
            membership.Users = await _query.GetManagerAndSubordinatesAsync(relatedId);
        }
        else if (groupType == GroupIdentityType.Territory)
        {
            var relatedId = childRef.Metadata.GetValueOrDefault("RelatedId") as string ?? "";
            membership.Users = await _query.GetTerritoryUsersAsync(relatedId);
        }
        else if (groupType == GroupIdentityType.TerritoryAndSubordinates)
        {
            var relatedId = childRef.Metadata.GetValueOrDefault("RelatedId") as string ?? "";
            // Users in this territory
            var users = await _query.GetTerritoryUsersAsync(relatedId);
            // Users in all descendant territories
            var descendantIds = await _query.GetAllDescendantTerritoryIdsAsync(relatedId);
            foreach (var descId in descendantIds)
                users.AddRange(await _query.GetTerritoryUsersAsync(descId));
            // Deduplicate by user ID
            var seen = new HashSet<string>();
            var deduped = new List<SfUser>();
            foreach (var u in users)
            {
                if (!seen.Contains(u.Id))
                {
                    seen.Add(u.Id);
                    deduped.Add(u);
                }
            }
            membership.Users = deduped;
        }

        Logger.Info(
            $"[IdentityGather] Child {childRef.GroupId}: {membership.Users.Count} user(s), {membership.ChildGroups.Count} nested group(s)");
        return membership;
    }

    // ── Private OWD child group construction ──────────────────────────────────

    /// <summary>Create all child groups for a PRIVATE OWD object.</summary>
    private async Task BuildPrivateChildrenAsync(
        GroupMembership membership,
        string objectName)
    {
        // 1. GlobalUsers child group
        membership.ChildGroups.Add(new ChildGroupRef
        {
            GroupId = SfGroupIdFormats.GlobalUsers.Format(objectName),
            GroupType = GroupIdentityType.GlobalAccessUsers,
            NeedsGather = true,
            Metadata = new Dictionary<string, object?>
            {
                ["ChildGroupType"] = GroupIdentityType.GlobalAccessUsers.Value(),
            },
        });

        // 2. Load role hierarchy
        _roleHierarchy ??= await _query.GetRoleHierarchyAsync();
        _rolesAssigned ??= await _query.GetRolesAssignedToUsersAsync();

        // 3. Query group shares for this object
        var groupShareIds = await _query.GetGroupShareIdsAsync(objectName);

        // If the share table doesn't exist (e.g. Product2, Pricebook2),
        // or the object has no valid OWD field on Organization (e.g. User),
        // flag the GlobalUsers group to include all Read-permission users
        // instead of only ViewAll/ModifyAll admins.
        if (!_query.HasShareTable(objectName) || !_query.HasOwdField(objectName))
            membership.ChildGroups[0].Metadata["use_read_permission"] = true;

        // 4. Load group details
        var sfGroups = new List<SfGroup>();
        if (groupShareIds.Count > 0)
            sfGroups = await _query.GetGroupsByIdsAsync(groupShareIds);

        var createdGroups = new HashSet<string>();

        // 5. Process group shares
        foreach (var sfGroup in sfGroups)
        {
            CreateChildGroupForSfGroup(membership, objectName, sfGroup, createdGroups);
        }

        Logger.Info(
            $"[IdentityGather] {objectName} PRIVATE: {membership.ChildGroups.Count} child group(s)");
    }

    /// <summary>Create the appropriate child group(s) for a Salesforce Group record.</summary>
    private void CreateChildGroupForSfGroup(
        GroupMembership membership,
        string objectName,
        SfGroup sfGroup,
        HashSet<string> created)
    {
        var gtype = sfGroup.Type;

        if (gtype is UserOrGroupType.Role or UserOrGroupType.RoleAndSubordinates or UserOrGroupType.RoleAndSubordinatesInternal)
        {
            CreateRoleGroupsWithHierarchy(membership, objectName, sfGroup.RelatedId, created);
        }
        else if (gtype == UserOrGroupType.Organization)
        {
            var gid = SfGroupIdFormats.AllInternalUsers.Format(objectName);
            if (!created.Contains(gid))
            {
                created.Add(gid);
                membership.ChildGroups.Add(new ChildGroupRef
                {
                    GroupId = gid,
                    GroupType = GroupIdentityType.AllInternalUsers,
                    NeedsGather = true,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["ChildGroupType"] = GroupIdentityType.AllInternalUsers.Value(),
                    },
                });
            }
        }
        else if (gtype == UserOrGroupType.Manager)
        {
            var gid = SfGroupIdFormats.Manager.Format(objectName, sfGroup.RelatedId);
            if (!created.Contains(gid))
            {
                created.Add(gid);
                membership.ChildGroups.Add(new ChildGroupRef
                {
                    GroupId = gid,
                    GroupType = GroupIdentityType.Manager,
                    NeedsGather = true,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["ChildGroupType"] = GroupIdentityType.Manager.Value(),
                        ["RelatedId"] = sfGroup.RelatedId,
                    },
                });
            }
        }
        else if (gtype == UserOrGroupType.ManagerAndSubordinatesInternal)
        {
            var gid = SfGroupIdFormats.ManagerAndSubordinates.Format(objectName, sfGroup.RelatedId);
            if (!created.Contains(gid))
            {
                created.Add(gid);
                membership.ChildGroups.Add(new ChildGroupRef
                {
                    GroupId = gid,
                    GroupType = GroupIdentityType.ManagerAndSubordinates,
                    NeedsGather = true,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["ChildGroupType"] = GroupIdentityType.ManagerAndSubordinates.Value(),
                        ["RelatedId"] = sfGroup.RelatedId,
                    },
                });
            }
        }
        else if (gtype == UserOrGroupType.Territory)
        {
            var gid = SfGroupIdFormats.Territory.Format(objectName, sfGroup.RelatedId);
            if (!created.Contains(gid))
            {
                created.Add(gid);
                membership.ChildGroups.Add(new ChildGroupRef
                {
                    GroupId = gid,
                    GroupType = GroupIdentityType.Territory,
                    NeedsGather = true,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["ChildGroupType"] = GroupIdentityType.Territory.Value(),
                        ["RelatedId"] = sfGroup.RelatedId,
                    },
                });
            }
        }
        else if (gtype is UserOrGroupType.TerritoryAndSubordinates or UserOrGroupType.TerritoryAndSubordinatesInternal)
        {
            var gid = SfGroupIdFormats.TerritoryAndSubordinates.Format(objectName, sfGroup.RelatedId);
            if (!created.Contains(gid))
            {
                created.Add(gid);
                membership.ChildGroups.Add(new ChildGroupRef
                {
                    GroupId = gid,
                    GroupType = GroupIdentityType.TerritoryAndSubordinates,
                    NeedsGather = true,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["ChildGroupType"] = GroupIdentityType.TerritoryAndSubordinates.Value(),
                        ["RelatedId"] = sfGroup.RelatedId,
                    },
                });
            }
        }
        else
        {
            // Regular / Queue → Public Group
            var gid = SfGroupIdFormats.PublicGroup.Format(objectName, sfGroup.Id);
            if (!created.Contains(gid) && sfGroup.GroupMembers.Count > 0)
            {
                created.Add(gid);
                membership.ChildGroups.Add(new ChildGroupRef
                {
                    GroupId = gid,
                    GroupType = GroupIdentityType.PublicGroup,
                    NeedsGather = true,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["ChildGroupType"] = GroupIdentityType.PublicGroup.Value(),
                        ["PublicGroupId"] = sfGroup.Id,
                    },
                });
            }
        }
    }

    /// <summary>Create Role group and walk up the hierarchy.</summary>
    private void CreateRoleGroupsWithHierarchy(
        GroupMembership membership,
        string objectName,
        string roleId,
        HashSet<string> created)
    {
        var hierarchy = _roleHierarchy ?? new Dictionary<string, string>();
        var currentRole = roleId;

        while (!string.IsNullOrEmpty(currentRole) && !created.Contains(currentRole))
        {
            created.Add(currentRole);
            var parentRole = hierarchy.GetValueOrDefault(currentRole, "");
            var childRoles = hierarchy
                .Where(kv => kv.Value == currentRole)
                .Select(kv => kv.Key)
                .ToList();

            var hasParent = !string.IsNullOrEmpty(parentRole);
            var groupType = hasParent
                ? GroupIdentityType.RoleWithParent
                : GroupIdentityType.RoleWithoutParent;

            var groupId = SfGroupIdFormats.Role.Format(objectName, currentRole);
            membership.ChildGroups.Add(new ChildGroupRef
            {
                GroupId = groupId,
                GroupType = groupType,
                NeedsGather = true,
                Metadata = new Dictionary<string, object?>
                {
                    ["ChildGroupType"] = groupType.Value(),
                    ["RoleId"] = currentRole,
                    ["ParentRoleId"] = parentRole,
                    ["ChildRoles"] = childRoles,
                },
            });

            currentRole = parentRole;
        }
    }
}
