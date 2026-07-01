// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// AclEngine/IdentityQueries.cs
// ----------------------------
// SOQL query methods used by the Identity Crawl and Group-based ACL builder.
//
// Provides a ``IdentityQueryClient`` that wraps an existing ``SalesforceClient``
// and adds high-level async methods for all SOQL queries defined in the
// Identity Crawl specification (temp.md Sections 3.1–3.12).
//
// Each method corresponds to one SOQL query pattern and returns parsed
// ``identity_models`` dataclass instances.

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.AclEngine;

/// <summary>
/// High-level query methods for identity crawl and group-based ACL.
///
/// Parameters
/// ----------
/// sfClient     : An authenticated <see cref="SalesforceClient"/> instance.
/// owdFieldMap  : ``{object_name: owd_field}`` from config (e.g.
///                ``{"Account": "DefaultAccountAccess"}``).  When provided,
///                the OWD query is built dynamically from this map.  When
///                omitted, falls back to loading from ``config/schema.json``.
/// batchSize    : Max IDs per SOQL IN clause (default 50, Salesforce limit).
/// useEntityDefinitionOwd : When True, query ``EntityDefinition`` for OWD
///                values before falling back to the Organization table.
/// objectNames  : All object names from schema.json (used for the
///                EntityDefinition query).  When omitted, loaded from config.
/// </summary>
public class IdentityQueryClient
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector.acl_engine.identity");

    // Salesforce limits the number of IDs in an IN clause
    private const int MaxInClauseIds = 50;

    // ── EntityDefinition.InternalSharingModel → EntityVisibility mapping ─────
    // EntityDefinition returns different string literals than the Organization table.
    // This dict normalises them to the EntityVisibility values the rest of the
    // group-ACL engine already understands.
    // Internal so tests can verify mapping completeness (Python `_ENTITY_DEF_TO_VISIBILITY`).
    internal static readonly Dictionary<string, EntityVisibility> EntityDefToVisibility = new()
    {
        ["Private"] = EntityVisibility.None,
        ["Read"] = EntityVisibility.Read,
        ["ReadSelect"] = EntityVisibility.Read,
        ["ReadWrite"] = EntityVisibility.Edit,
        ["ReadWriteTransfer"] = EntityVisibility.ReadEditTransfer,
        ["FullAccess"] = EntityVisibility.ReadEditTransfer,
        ["ControlledByParent"] = EntityVisibility.ControlledByParent,
        ["ControlledByCampaign"] = EntityVisibility.ControlledByCampaign,
        ["ControlledByLeadOrContact"] = EntityVisibility.ControlledByLeadOrContact,
    };

    private readonly SalesforceClient _sf;
    private readonly int _batchSize;
    private readonly Dictionary<string, string> _owdFieldMap;
    private readonly HashSet<string> _noShareTableTypes = new();
    private HashSet<string> _invalidOwdFields = new();
    // ── EntityDefinition flight ──────────────────────────────────────────
    // Internal so wiring tests can inspect them (Python `qc._use_entity_definition_owd`).
    internal readonly bool _useEntityDefinitionOwd;
    internal readonly List<string> _objectNames;
    // ── End EntityDefinition flight ──────────────────────────────────────

    public IdentityQueryClient(
        SalesforceClient sfClient,
        Dictionary<string, string>? owdFieldMap = null,
        int batchSize = MaxInClauseIds,
        bool useEntityDefinitionOwd = false,
        List<string>? objectNames = null)
    {
        _sf = sfClient;
        _batchSize = batchSize;
        _owdFieldMap = owdFieldMap ?? LoadOwdFieldMap();
        _useEntityDefinitionOwd = useEntityDefinitionOwd;
        _objectNames = useEntityDefinitionOwd
            ? (objectNames is not null ? new List<string>(objectNames) : LoadObjectNames())
            : new List<string>();
    }

    /// <summary>
    /// Derive the share table API name for a given sObject type.
    ///
    /// Standard objects : Account   → AccountShare
    /// Custom objects   : Work_Order__c → Work_Order__Share
    /// </summary>
    internal static string ShareTableName(string objectType)
    {
        if (objectType.EndsWith("__c"))
            return objectType[..^3] + "__Share";
        return objectType + "Share";
    }

    /// <summary>Split <paramref name="items"/> into chunks of at most <paramref name="size"/>.</summary>
    internal static List<List<string>> Chunked(List<string> items, int size)
    {
        var chunks = new List<List<string>>();
        for (var i = 0; i < items.Count; i += size)
            chunks.Add(items.Skip(i).Take(size).ToList());
        return chunks;
    }

    /// <summary>Format a list of IDs for a SOQL IN clause.</summary>
    internal static string QuoteIds(IEnumerable<string> ids)
    {
        return string.Join(", ", ids.Select(i => $"'{i}'"));
    }

    /// <summary>Fallback: load owd_field_map from config/schema.json.</summary>
    private static Dictionary<string, string> LoadOwdFieldMap()
    {
        try
        {
            return Settings.BuildOwdFieldMap();
        }
        catch (Exception)
        {
            Logger.Warning("[IdentityQuery] Could not load owd_field_map from config");
            return new Dictionary<string, string>();
        }
    }

    /// <summary>Fallback: load all object names from config/schema.json.</summary>
    private static List<string> LoadObjectNames()
    {
        try
        {
            return Settings.BuildObjectNameList();
        }
        catch (Exception)
        {
            Logger.Warning("[IdentityQuery] Could not load object names from config");
            return new List<string>();
        }
    }

    // ── 3.1  Org-Wide Defaults ───────────────────────────────────────────────

    /// <summary>
    /// Query OWD settings for all configured objects.
    ///
    /// When ``useEntityDefinitionOwd`` is enabled:
    ///   1. Queries ``EntityDefinition`` for all objects in schema.json.
    ///   2. Maps ``InternalSharingModel`` to ``EntityVisibility``.
    ///   3. Falls back to the Organization-table approach for objects not
    ///      found in the EntityDefinition result set.
    ///
    /// When disabled: behaves exactly as before (Organization table query).
    ///
    /// Returns ``{object_name: EntityVisibility}``.
    /// </summary>
    public virtual async Task<Dictionary<string, EntityVisibility>> GetOrgWideDefaultsAsync()
    {
        var owdMap = new Dictionary<string, EntityVisibility>();

        // ── NEW PATH: EntityDefinition ────────────────────────────────────────
        var entityDefResolved = new HashSet<string>();
        if (_useEntityDefinitionOwd && _objectNames.Count > 0)
        {
            var entityDefMap = await FetchEntityDefOwdAsync();
            foreach (var (k, v) in entityDefMap)
                owdMap[k] = v;
            entityDefResolved = new HashSet<string>(entityDefMap.Keys);
            Logger.Info(
                $"[IdentityQuery] EntityDefinition resolved {entityDefResolved.Count} object(s): " +
                FormatDict(entityDefMap.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value.ToString()))));
        }
        // ── END NEW PATH ──────────────────────────────────────────────────────

        // ── OLD PATH (fallback): Organization table ───────────────────────────
        // Only query for objects NOT already resolved by EntityDefinition
        var remainingMap = _owdFieldMap
            .Where(kv => !entityDefResolved.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        if (remainingMap.Count > 0)
        {
            var orgOwd = await FetchOrgTableOwdAsync(remainingMap);
            foreach (var (k, v) in orgOwd)
                owdMap[k] = v;
        }
        // ── END OLD PATH ──────────────────────────────────────────────────────

        Logger.Info(
            "[IdentityQuery] OWD map: " +
            FormatDict(owdMap.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value.ToString()))));
        return owdMap;
    }

    /// <summary>
    /// Query ``EntityDefinition`` for all objects in schema.json and return
    /// ``{objectName: EntityVisibility}``.
    ///
    /// Fires:
    ///     SELECT QualifiedApiName, InternalSharingModel
    ///     FROM EntityDefinition
    ///     WHERE QualifiedApiName IN ('Account', 'Contact', …)
    ///
    /// Unknown ``InternalSharingModel`` values default to ``NONE`` (Private).
    /// </summary>
    private async Task<Dictionary<string, EntityVisibility>> FetchEntityDefOwdAsync()
    {
        var quoted = string.Join(", ", _objectNames.Select(name => $"'{name}'"));
        var soql =
            "SELECT QualifiedApiName, InternalSharingModel " +
            "FROM EntityDefinition " +
            $"WHERE QualifiedApiName IN ({quoted})";
        Logger.Info($"[IdentityQuery] Fetching OWD via EntityDefinition: {soql}");

        JsonArray records;
        try
        {
            var result = await _sf.QueryAsync(soql, tooling: true);
            records = result["records"] as JsonArray ?? new JsonArray();
        }
        catch (Exception exc)
        {
            Logger.Warning(
                $"[IdentityQuery] EntityDefinition query failed ({exc.Message}); " +
                "will fall back to Organization query");
            return new Dictionary<string, EntityVisibility>();
        }

        var owdMap = new Dictionary<string, EntityVisibility>();
        foreach (var rowNode in records)
        {
            var row = rowNode as JsonObject;
            var apiName = (string?)row?["QualifiedApiName"];
            var rawModel = (string?)row?["InternalSharingModel"];
            if (string.IsNullOrEmpty(apiName))
                continue;
            var vis = EntityDefToVisibility.GetValueOrDefault(rawModel ?? "", EntityVisibility.None);
            owdMap[apiName] = vis;
            Logger.Info(
                $"[IdentityQuery] EntityDefinition: {apiName} → InternalSharingModel='{rawModel}' → OWD='{vis}'");
        }

        // Log objects that were queried but not returned by EntityDefinition
        var missing = new HashSet<string>(_objectNames);
        missing.ExceptWith(owdMap.Keys);
        if (missing.Count > 0)
            Logger.Warning($"[IdentityQuery] EntityDefinition returned no rows for: {FormatSet(missing)}");

        Logger.Info(
            $"[IdentityQuery] EntityDefinition cache: {owdMap.Count}/{_objectNames.Count} object(s)");
        return owdMap;
    }

    /// <summary>
    /// Query OWD from the Organization table for the given field map.
    ///
    /// This is the original Organization-table implementation, extracted into
    /// its own method so it can serve as a fallback when the EntityDefinition
    /// path is enabled.
    /// </summary>
    private async Task<Dictionary<string, EntityVisibility>> FetchOrgTableOwdAsync(
        Dictionary<string, string> fieldMap)
    {
        if (fieldMap.Count == 0)
            return new Dictionary<string, EntityVisibility>();

        // Validate fields against Organization describe to avoid INVALID_FIELD
        var validFields = await GetOrgFieldsAsync();
        var filteredMap = fieldMap
            .Where(kv => validFields.Contains(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        var skipped = new HashSet<string>(fieldMap.Keys);
        skipped.ExceptWith(filteredMap.Keys);
        if (skipped.Count > 0)
        {
            _invalidOwdFields = new HashSet<string>(skipped.Select(obj => fieldMap[obj]));
            Logger.Warning(
                "[IdentityQuery] Skipping objects with invalid OWD fields on Organization: " +
                FormatDict(skipped.Select(obj => new KeyValuePair<string, string>(obj, fieldMap[obj]))));
        }

        if (filteredMap.Count == 0)
        {
            Logger.Warning("[IdentityQuery] No valid OWD fields found; returning empty OWD");
            return new Dictionary<string, EntityVisibility>();
        }

        // Build SELECT from validated fields: deduplicate field names
        var owdFields = new List<string>();
        var seenFields = new HashSet<string>();
        foreach (var fld in filteredMap.Values)
        {
            if (seenFields.Add(fld))
                owdFields.Add(fld);
        }
        var soql = $"SELECT {string.Join(", ", owdFields)} FROM Organization";

        Logger.Info($"[IdentityQuery] Fetching OWD with: {soql}");

        var result = await _sf.QueryAsync(soql);
        var records = result["records"] as JsonArray ?? new JsonArray();
        if (records.Count == 0)
        {
            Logger.Warning("[IdentityQuery] No Organization record returned");
            return new Dictionary<string, EntityVisibility>();
        }

        var record = records[0] as JsonObject ?? new JsonObject();
        var owdMap = new Dictionary<string, EntityVisibility>();
        foreach (var (objName, owdField) in filteredMap)
        {
            var raw = (string?)record[owdField] ?? "None";
            owdMap[objName] = IdentityModels.ParseVisibility(raw);
        }

        return owdMap;
    }

    /// <summary>Return the set of field names available on the Organization entity.</summary>
    private async Task<HashSet<string>> GetOrgFieldsAsync()
    {
        try
        {
            var desc = await _sf.DescribeSObjectAsync("Organization");
            var fields = desc["fields"] as JsonArray ?? new JsonArray();
            return fields
                .OfType<JsonObject>()
                .Select(f => (string?)f["name"])
                .Where(n => n is not null)
                .Select(n => n!)
                .ToHashSet();
        }
        catch (Exception exc)
        {
            Logger.Warning($"[IdentityQuery] Organization describe failed: {exc.Message}");
            // Fall back to all configured fields (query may fail for bad ones)
            return new HashSet<string>(_owdFieldMap.Values);
        }
    }

    // ── 3.2  Authorized Users (all with Read permission) ─────────────────────

    /// <summary>
    /// Query all active users who have Read permission on <paramref name="objectName"/>
    /// via PermissionSetAssignment.  Used for PUBLIC OWD objects.
    /// </summary>
    public virtual async Task<List<SfUser>> GetAuthorizedUsersAsync(string objectName)
    {
        var soql =
            "SELECT Id, Assignee.Name, Assignee.Id, Assignee.Alias, " +
            "Assignee.Email, Assignee.FirstName, Assignee.LastName, " +
            "Assignee.FederationIdentifier, Assignee.UserName, " +
            "Assignee.IsActive, Assignee.UserRoleId, " +
            "PermissionSet.Id, PermissionSet.IsOwnedByProfile, " +
            "PermissionSet.Profile.Name, PermissionSet.Label " +
            "FROM PermissionSetAssignment " +
            "WHERE PermissionSetId IN (" +
            "  SELECT ParentId FROM ObjectPermissions " +
            $"  WHERE SObjectType = '{objectName}' AND PermissionsRead = true" +
            ") " +
            "AND Assignee.IsActive = True " +
            "AND Assignee.UserType = 'Standard' " +
            "AND (NOT Assignee.Name LIKE '%User%') " +
            "ORDER BY Id ASC";
        Logger.Info($"[IdentityQuery] get_users_with_roles — hitting Salesforce User endpoint for '{objectName}'");
        Logger.Info($"[IdentityQuery] SOQL: {soql}");
        var records = await _sf.QueryAllAsync(soql);
        Logger.Info($"[IdentityQuery] Fetched {records.Count} user record(s) for '{objectName}'");
        var users = ParsePsaUsers(records);
        var missing = users.Where(u => string.IsNullOrEmpty(u.FederationIdentifier)).ToList();
        if (missing.Count > 0)
        {
            Logger.Warning(
                $"[IdentityQuery] {missing.Count} of {users.Count} user(s) for '{objectName}' have no FederationIdentifier");
            foreach (var u in missing)
            {
                Logger.Debug(
                    $"[IdentityQuery] User {u.Id} ({u.Name}) — FederationIdentifier: MISSING");
            }
        }
        return users;
    }

    // ── 3.3  Global Access Users (ViewAll/ModifyAll) ─────────────────────────

    /// <summary>
    /// Query users with ViewAll or ModifyAll on <paramref name="objectName"/>.
    /// Used for the GlobalUsers group in PRIVATE OWD.
    /// </summary>
    public virtual async Task<List<SfUser>> GetGlobalAccessUsersAsync(string objectName)
    {
        var soql =
            "SELECT Id, Assignee.Name, Assignee.Id, Assignee.Alias, " +
            "Assignee.Email, Assignee.FirstName, Assignee.LastName, " +
            "Assignee.FederationIdentifier, Assignee.UserName, " +
            "Assignee.IsActive, Assignee.UserRoleId, " +
            "PermissionSet.Id, PermissionSet.IsOwnedByProfile, " +
            "PermissionSet.Profile.Name, PermissionSet.Label " +
            "FROM PermissionSetAssignment " +
            "WHERE PermissionSetId IN (" +
            "  SELECT ParentId FROM ObjectPermissions " +
            $"  WHERE SObjectType = '{objectName}' AND PermissionsViewAllRecords = true" +
            ") " +
            "AND Assignee.IsActive = True " +
            "AND (NOT Assignee.Name LIKE '%User%') " +
            "ORDER BY Id ASC";
        var records = await _sf.QueryAllAsync(soql);
        return ParsePsaUsers(records);
    }

    // ── 3.6  Group Shares Only (identity sync optimisation) ──────────────────

    /// <summary>
    /// Query distinct UserOrGroupId values from the share table
    /// where the share is to a Group (Queue type).
    /// </summary>
    public virtual async Task<List<string>> GetGroupShareIdsAsync(string objectName)
    {
        var shareTable = ShareTableName(objectName);
        var soql =
            $"SELECT UserOrGroupId " +
            $"FROM {shareTable} " +
            $"GROUP BY UserOrGroupId " +
            $"ORDER BY UserOrGroupId ASC";
        List<JsonObject> records;
        try
        {
            records = await _sf.QueryAllAsync(soql);
        }
        catch (InvalidOperationException exc)
        {
            var excStr = exc.Message;
            if (excStr.Contains("INVALID_TYPE") || excStr.Contains("is not supported"))
            {
                Logger.Info(
                    $"[IdentityQuery] Share table {shareTable} does not exist — {objectName} uses non-standard sharing");
                _noShareTableTypes.Add(objectName);
            }
            else
            {
                Logger.Warning($"[IdentityQuery] Could not query {shareTable} group shares: {exc.Message}");
            }
            return new List<string>();
        }
        return records
            .Select(r => (string?)r["UserOrGroupId"])
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToList();
    }

    /// <summary>Return False if a prior query proved the share table doesn't exist.</summary>
    public virtual bool HasShareTable(string objectName)
    {
        return !_noShareTableTypes.Contains(objectName);
    }

    /// <summary>
    /// Return True if the object has a valid OWD field on Organization.
    ///
    /// Objects like User, Product2, Pricebook2 have no OWD field — their
    /// access is controlled by Profile/Permission Set, not per-record sharing.
    /// </summary>
    public virtual bool HasOwdField(string objectName)
    {
        return _owdFieldMap.ContainsKey(objectName) && !_invalidOwdFields.Contains(_owdFieldMap[objectName]);
    }

    // ── 3.7  Group Details ───────────────────────────────────────────────────

    /// <summary>
    /// Fetch Group records (type, RelatedId, members) for a list of group IDs.
    /// Batches queries to respect the IN clause limit.
    /// </summary>
    public virtual async Task<List<SfGroup>> GetGroupsByIdsAsync(List<string> groupIds)
    {
        if (groupIds.Count == 0)
            return new List<SfGroup>();

        var allGroups = new List<SfGroup>();
        foreach (var chunk in Chunked(groupIds, _batchSize))
        {
            var quoted = QuoteIds(chunk);
            var soql =
                "SELECT Id, Type, RelatedId, DoesIncludeBosses, " +
                "(SELECT UserOrGroupId FROM GroupMembers) " +
                $"FROM Group WHERE Id IN ({quoted}) " +
                "ORDER BY Id ASC";
            var records = await _sf.QueryAllAsync(soql);
            foreach (var r in records)
            {
                var memberRecords = (r["GroupMembers"] as JsonObject)?["records"] as JsonArray ?? new JsonArray();
                var memberIds = memberRecords
                    .OfType<JsonObject>()
                    .Select(m => (string?)m["UserOrGroupId"])
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Select(id => id!)
                    .ToList();

                var rawType = (string?)r["Type"] ?? "";
                UserOrGroupType groupType;
                try
                {
                    groupType = UserOrGroupTypeExtensions.Parse(rawType);
                }
                catch (ArgumentException)
                {
                    groupType = UserOrGroupType.Regular;
                }

                allGroups.Add(new SfGroup((string?)r["Id"] ?? "", groupType)
                {
                    RelatedId = (string?)r["RelatedId"] ?? "",
                    DoesIncludeBosses = (bool?)r["DoesIncludeBosses"] ?? false,
                    GroupMembers = memberIds,
                });
            }
        }
        return allGroups;
    }

    // ── 3.8  Role Hierarchy ─────────────────────────────────────────────────

    /// <summary>
    /// Query the full UserRole hierarchy.
    ///
    /// Returns ``{role_id: parent_role_id}`` for all roles.
    /// Roles with no parent have an empty string value.
    /// </summary>
    public virtual async Task<Dictionary<string, string>> GetRoleHierarchyAsync()
    {
        var soql = "SELECT Id, ParentRoleId FROM UserRole ORDER BY Id ASC";
        var records = await _sf.QueryAllAsync(soql);
        var map = new Dictionary<string, string>();
        foreach (var r in records)
        {
            var id = (string?)r["Id"];
            if (!string.IsNullOrEmpty(id))
                map[id] = (string?)r["ParentRoleId"] ?? "";
        }
        return map;
    }

    // ── 3.9  Roles Assigned to Active Users ─────────────────────────────────

    /// <summary>
    /// Query distinct UserRoleId values for active standard users.
    /// </summary>
    public virtual async Task<HashSet<string>> GetRolesAssignedToUsersAsync()
    {
        var soql =
            "SELECT UserRoleId FROM User " +
            "WHERE UserRoleId != null AND IsActive = True " +
            "AND UserType = 'Standard' " +
            "AND (NOT Name LIKE '%User%') " +
            "GROUP BY UserRoleId " +
            "ORDER BY UserRoleId ASC";
        var records = await _sf.QueryAllAsync(soql);
        return records
            .Select(r => (string?)r["UserRoleId"])
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToHashSet();
    }

    // ── 3.10  Users with roles and permission sets ──────────────────────────

    /// <summary>
    /// Query all active users with their role and parent role info,
    /// plus permission set assignments for <paramref name="objectName"/>.
    /// </summary>
    public async Task<List<SfUser>> GetUsersWithRolesAsync(string objectName)
    {
        var soql =
            "SELECT Id, Name, Alias, Email, FederationIdentifier, " +
            "FirstName, LastName, UserName, UserRoleId, " +
            "UserRole.ParentRoleId, ManagerId, " +
            "(SELECT PermissionSet.Id, PermissionSet.IsOwnedByProfile, " +
            "PermissionSet.Profile.Name, PermissionSet.Label " +
            "FROM PermissionSetAssignments " +
            "WHERE PermissionSetId IN (" +
            "  SELECT ParentId FROM ObjectPermissions " +
            $"  WHERE SObjectType = '{objectName}' AND PermissionsRead = true" +
            ")) " +
            "FROM User " +
            "WHERE IsActive = True AND (NOT Name LIKE '%User%') " +
            "ORDER BY Id ASC";
        Logger.Info($"[IdentityQuery] get_users_with_roles — querying Salesforce users for '{objectName}'");
        var records = await _sf.QueryAllAsync(soql);
        var users = ParseFullUsers(records);
        var missing = users.Where(u => string.IsNullOrEmpty(u.FederationIdentifier)).ToList();
        if (missing.Count > 0)
        {
            Logger.Warning(
                $"[IdentityQuery] {missing.Count}/{users.Count} user(s) missing FederationIdentifier for '{objectName}': " +
                string.Join(", ", missing.Select(u => $"{u.Name} ({u.Id})")));
        }
        return users;
    }

    // ── 3.11  Frozen Users ──────────────────────────────────────────────────

    /// <summary>Query IDs of users who are currently frozen.</summary>
    public async Task<HashSet<string>> GetFrozenUserIdsAsync()
    {
        var soql = "SELECT Id, UserId FROM UserLogin WHERE IsFrozen = True ORDER BY Id ASC";
        var records = await _sf.QueryAllAsync(soql);
        return records
            .Select(r => (string?)r["UserId"])
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToHashSet();
    }

    // ── 3.12  Manager Relationships ─────────────────────────────────────────

    /// <summary>
    /// Query all user → manager relationships.
    ///
    /// Returns ``{user_id: manager_id}``.
    /// </summary>
    public async Task<Dictionary<string, string>> GetManagerMapAsync()
    {
        var soql = "SELECT Id, ManagerId FROM User ORDER BY Id ASC";
        var records = await _sf.QueryAllAsync(soql);
        var map = new Dictionary<string, string>();
        foreach (var r in records)
        {
            var id = (string?)r["Id"];
            var managerId = (string?)r["ManagerId"];
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(managerId))
                map[id] = managerId;
        }
        return map;
    }

    // ── Territory queries ───────────────────────────────────────────────────

    /// <summary>
    /// Query users assigned to a specific Territory2 via
    /// UserTerritory2Association.
    /// </summary>
    public async Task<List<string>> GetTerritoryUserIdsAsync(string territoryId)
    {
        var soql =
            "SELECT UserId FROM UserTerritory2Association " +
            $"WHERE Territory2Id = '{territoryId}'";
        List<JsonObject> records;
        try
        {
            records = await _sf.QueryAllAsync(soql);
        }
        catch (InvalidOperationException)
        {
            Logger.Warning($"[IdentityQuery] Could not query territory users for {territoryId}");
            return new List<string>();
        }
        return records
            .Select(r => (string?)r["UserId"])
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToList();
    }

    /// <summary>Fetch users assigned to a territory as SfUser instances.</summary>
    public virtual async Task<List<SfUser>> GetTerritoryUsersAsync(string territoryId)
    {
        var userIds = await GetTerritoryUserIdsAsync(territoryId);
        if (userIds.Count == 0)
            return new List<SfUser>();
        return await GetUsersByIdsAsync(userIds);
    }

    /// <summary>Query direct child Territory2 IDs under a parent territory.</summary>
    public async Task<List<string>> GetChildTerritoryIdsAsync(string territoryId)
    {
        var soql =
            "SELECT Id FROM Territory2 " +
            $"WHERE ParentTerritory2Id = '{territoryId}' " +
            "ORDER BY Id ASC";
        List<JsonObject> records;
        try
        {
            records = await _sf.QueryAllAsync(soql);
        }
        catch (InvalidOperationException)
        {
            Logger.Warning($"[IdentityQuery] Could not query child territories for {territoryId}");
            return new List<string>();
        }
        return records
            .Select(r => (string?)r["Id"])
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToList();
    }

    /// <summary>
    /// Collect all descendant Territory2 IDs via iterative BFS.
    /// </summary>
    public virtual async Task<HashSet<string>> GetAllDescendantTerritoryIdsAsync(string territoryId)
    {
        var descendants = new HashSet<string>();
        var frontier = new List<string> { territoryId };
        while (frontier.Count > 0)
        {
            var current = frontier[0];
            frontier.RemoveAt(0);
            var children = await GetChildTerritoryIdsAsync(current);
            foreach (var childId in children)
            {
                if (!descendants.Contains(childId))
                {
                    descendants.Add(childId);
                    frontier.Add(childId);
                }
            }
        }
        return descendants;
    }

    // ── Helper: get users by role ────────────────────────────────────────────

    /// <summary>Query active users assigned to a specific role.</summary>
    public virtual async Task<List<SfUser>> GetUsersForRoleAsync(string roleId)
    {
        var soql =
            "SELECT Id, Name, Email, FederationIdentifier, UserName " +
            "FROM User " +
            $"WHERE UserRoleId = '{roleId}' AND IsActive = True " +
            "AND (NOT Name LIKE '%User%') " +
            "ORDER BY Id ASC";
        var records = await _sf.QueryAllAsync(soql);
        return records
            .Where(r => !string.IsNullOrEmpty((string?)r["Id"]))
            .Select(r => new SfUser((string?)r["Id"] ?? "")
            {
                Name = (string?)r["Name"] ?? "",
                Email = (string?)r["Email"] ?? "",
                FederationIdentifier = (string?)r["FederationIdentifier"] ?? "",
                UserName = (string?)r["UserName"] ?? "",
            })
            .ToList();
    }

    // ── Helper: get group members as users ───────────────────────────────────

    /// <summary>Fetch user members of a specific group.</summary>
    public virtual async Task<List<SfUser>> GetGroupMemberUsersAsync(string groupId)
    {
        var soql =
            "SELECT UserOrGroupId FROM GroupMember " +
            $"WHERE GroupId = '{groupId}' " +
            "ORDER BY Id ASC";
        var records = await _sf.QueryAllAsync(soql);
        var userIds = records
            .Select(r => (string?)r["UserOrGroupId"])
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToList();
        if (userIds.Count == 0)
            return new List<SfUser>();
        return await GetUsersByIdsAsync(userIds);
    }

    // ── Helper: get user by ID ──────────────────────────────────────────────

    /// <summary>Fetch a single user by Salesforce ID.</summary>
    public virtual async Task<SfUser?> GetUserByIdAsync(string userId)
    {
        var soql =
            "SELECT Id, Name, Email, FederationIdentifier, UserName, ManagerId " +
            $"FROM User WHERE Id = '{userId}' AND IsActive = True";
        var records = await _sf.QueryAllAsync(soql);
        if (records.Count == 0)
            return null;
        var r = records[0];
        return new SfUser((string?)r["Id"] ?? "")
        {
            Name = (string?)r["Name"] ?? "",
            Email = (string?)r["Email"] ?? "",
            FederationIdentifier = (string?)r["FederationIdentifier"] ?? "",
            UserName = (string?)r["UserName"] ?? "",
            ManagerId = (string?)r["ManagerId"] ?? "",
        };
    }

    // ── Helper: get users by IDs ────────────────────────────────────────────

    /// <summary>Fetch users by a list of Salesforce IDs.</summary>
    public async Task<List<SfUser>> GetUsersByIdsAsync(List<string> userIds)
    {
        if (userIds.Count == 0)
            return new List<SfUser>();

        var allUsers = new List<SfUser>();
        foreach (var chunk in Chunked(userIds, _batchSize))
        {
            var quoted = QuoteIds(chunk);
            var soql =
                "SELECT Id, Name, Email, FederationIdentifier, UserName " +
                $"FROM User WHERE Id IN ({quoted}) AND IsActive = True " +
                "ORDER BY Id ASC";
            var records = await _sf.QueryAllAsync(soql);
            foreach (var r in records)
            {
                var id = (string?)r["Id"];
                if (!string.IsNullOrEmpty(id))
                {
                    allUsers.Add(new SfUser(id)
                    {
                        Name = (string?)r["Name"] ?? "",
                        Email = (string?)r["Email"] ?? "",
                        FederationIdentifier = (string?)r["FederationIdentifier"] ?? "",
                        UserName = (string?)r["UserName"] ?? "",
                    });
                }
            }
        }
        return allUsers;
    }

    // ── Helper: get manager + subordinates ──────────────────────────────────

    /// <summary>Fetch a manager and all direct reports.</summary>
    public virtual async Task<List<SfUser>> GetManagerAndSubordinatesAsync(string managerId)
    {
        var soql =
            "SELECT Id, Name, Email, FederationIdentifier, UserName " +
            $"FROM User WHERE (Id = '{managerId}' OR ManagerId = '{managerId}') " +
            "AND IsActive = True " +
            "ORDER BY Id ASC";
        var records = await _sf.QueryAllAsync(soql);
        return records
            .Where(r => !string.IsNullOrEmpty((string?)r["Id"]))
            .Select(r => new SfUser((string?)r["Id"] ?? "")
            {
                Name = (string?)r["Name"] ?? "",
                Email = (string?)r["Email"] ?? "",
                FederationIdentifier = (string?)r["FederationIdentifier"] ?? "",
                UserName = (string?)r["UserName"] ?? "",
            })
            .ToList();
    }

    // ── Parsing helpers ─────────────────────────────────────────────────────

    /// <summary>Parse PermissionSetAssignment query results into SfUser instances (deduped).</summary>
    private List<SfUser> ParsePsaUsers(List<JsonObject> psaRecords)
    {
        var seen = new Dictionary<string, SfUser>();
        foreach (var r in psaRecords)
        {
            var assignee = r["Assignee"] as JsonObject ?? new JsonObject();
            var userId = (string?)assignee["Id"];
            if (string.IsNullOrEmpty(userId) || seen.ContainsKey(userId))
                continue;

            var permSet = r["PermissionSet"] as JsonObject ?? new JsonObject();
            seen[userId] = new SfUser(userId)
            {
                Name = (string?)assignee["Name"] ?? "",
                Alias = (string?)assignee["Alias"] ?? "",
                Email = (string?)assignee["Email"] ?? "",
                FirstName = (string?)assignee["FirstName"] ?? "",
                LastName = (string?)assignee["LastName"] ?? "",
                FederationIdentifier = (string?)assignee["FederationIdentifier"] ?? "",
                UserName = (string?)assignee["UserName"] ?? "",
                UserRoleId = (string?)assignee["UserRoleId"] ?? "",
                IsActive = (bool?)assignee["IsActive"] ?? true,
                PermissionSets = new List<Dictionary<string, object?>>
                {
                    new Dictionary<string, object?>
                    {
                        ["Id"] = (string?)permSet["Id"] ?? "",
                        ["Label"] = (string?)permSet["Label"] ?? "",
                        ["IsOwnedByProfile"] = (bool?)permSet["IsOwnedByProfile"] ?? false,
                    },
                },
            };
        }
        return seen.Values.ToList();
    }

    /// <summary>Parse User query results (with nested role and permission set data).</summary>
    private List<SfUser> ParseFullUsers(List<JsonObject> userRecords)
    {
        var users = new List<SfUser>();
        foreach (var r in userRecords)
        {
            var userRole = r["UserRole"] as JsonObject ?? new JsonObject();
            var psaRecords = (r["PermissionSetAssignments"] as JsonObject)?["records"] as JsonArray ?? new JsonArray();
            var permissionSets = new List<Dictionary<string, object?>>();
            foreach (var pNode in psaRecords)
            {
                var permSet = (pNode as JsonObject)?["PermissionSet"] as JsonObject ?? new JsonObject();
                permissionSets.Add(new Dictionary<string, object?>
                {
                    ["Id"] = (string?)permSet["Id"] ?? "",
                    ["Label"] = (string?)permSet["Label"] ?? "",
                    ["IsOwnedByProfile"] = (bool?)permSet["IsOwnedByProfile"] ?? false,
                });
            }

            users.Add(new SfUser((string?)r["Id"] ?? "")
            {
                Name = (string?)r["Name"] ?? "",
                Alias = (string?)r["Alias"] ?? "",
                Email = (string?)r["Email"] ?? "",
                FirstName = (string?)r["FirstName"] ?? "",
                LastName = (string?)r["LastName"] ?? "",
                FederationIdentifier = (string?)r["FederationIdentifier"] ?? "",
                UserName = (string?)r["UserName"] ?? "",
                UserRoleId = (string?)r["UserRoleId"] ?? "",
                ParentRoleId = (string?)userRole["ParentRoleId"] ?? "",
                ManagerId = (string?)r["ManagerId"] ?? "",
                PermissionSets = permissionSets,
            });
        }
        return users;
    }

    // ── Formatting helpers (mirror Python logging repr) ───────────────────────

    /// <summary>Format key/value pairs the way Python repr() renders a dict: {'k': 'v', ...}.</summary>
    private static string FormatDict(IEnumerable<KeyValuePair<string, string>> items)
    {
        return "{" + string.Join(", ", items.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";
    }

    /// <summary>Format strings the way Python repr() renders a set: {'a', 'b'}.</summary>
    private static string FormatSet(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count == 0 ? "set()" : "{" + string.Join(", ", list.Select(i => $"'{i}'")) + "}";
    }
}
