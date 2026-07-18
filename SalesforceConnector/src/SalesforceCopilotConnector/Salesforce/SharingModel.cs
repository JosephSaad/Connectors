// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Item;

namespace SalesforceCopilotConnector.Salesforce;

public enum EntityVisibility
{
    None,                       // "None"
    PublicReadOnly,             // "Read"
    PublicReadWrite,            // "Edit"
    PublicReadWriteTransfer,    // "ReadEditTransfer"
    All,                        // "All"
    ControlledByParent,         // "ControlledByParent"
    ControlledByCampaign,       // "ControlledByCampaign"
    ControlledByLeadOrContact,  // "ControlledByLeadOrContact"
}

public static class EntityVisibilityExtensions
{
    /// <summary>Wire value of the enum member, mirroring Python ``EntityVisibility.value``.</summary>
    public static string Value(this EntityVisibility visibility) => visibility switch
    {
        EntityVisibility.None => "None",
        EntityVisibility.PublicReadOnly => "Read",
        EntityVisibility.PublicReadWrite => "Edit",
        EntityVisibility.PublicReadWriteTransfer => "ReadEditTransfer",
        EntityVisibility.All => "All",
        EntityVisibility.ControlledByParent => "ControlledByParent",
        EntityVisibility.ControlledByCampaign => "ControlledByCampaign",
        EntityVisibility.ControlledByLeadOrContact => "ControlledByLeadOrContact",
        _ => throw new ArgumentException($"'{visibility}' is not a valid EntityVisibility"),
    };

    /// <summary>Value-based construction, mirroring Python ``EntityVisibility(raw)``.</summary>
    public static EntityVisibility Parse(string raw) => raw switch
    {
        "None" => EntityVisibility.None,
        "Read" => EntityVisibility.PublicReadOnly,
        "Edit" => EntityVisibility.PublicReadWrite,
        "ReadEditTransfer" => EntityVisibility.PublicReadWriteTransfer,
        "All" => EntityVisibility.All,
        "ControlledByParent" => EntityVisibility.ControlledByParent,
        "ControlledByCampaign" => EntityVisibility.ControlledByCampaign,
        "ControlledByLeadOrContact" => EntityVisibility.ControlledByLeadOrContact,
        _ => throw new ArgumentException($"'{raw}' is not a valid EntityVisibility"),
    };
}

public enum UserOrGroupType
{
    User,                              // "User"
    Queue,                             // "Queue"
    Role,                              // "Role"
    RoleAndSubordinates,               // "RoleAndSubordinates"
    RoleAndSubordinatesInternal,       // "RoleAndSubordinatesInternal"
    Organization,                      // "Organization"
    Manager,                           // "Manager"
    ManagerAndSubordinatesInternal,    // "ManagerAndSubordinatesInternal"
    PublicGroup,                       // "Group"
}

public static class UserOrGroupTypeExtensions
{
    /// <summary>Wire value of the enum member, mirroring Python ``UserOrGroupType.value``.</summary>
    public static string Value(this UserOrGroupType type) => type switch
    {
        UserOrGroupType.User => "User",
        UserOrGroupType.Queue => "Queue",
        UserOrGroupType.Role => "Role",
        UserOrGroupType.RoleAndSubordinates => "RoleAndSubordinates",
        UserOrGroupType.RoleAndSubordinatesInternal => "RoleAndSubordinatesInternal",
        UserOrGroupType.Organization => "Organization",
        UserOrGroupType.Manager => "Manager",
        UserOrGroupType.ManagerAndSubordinatesInternal => "ManagerAndSubordinatesInternal",
        UserOrGroupType.PublicGroup => "Group",
        _ => throw new ArgumentException($"'{type}' is not a valid UserOrGroupType"),
    };

    /// <summary>Value-based construction, mirroring Python ``UserOrGroupType(raw)``.</summary>
    public static UserOrGroupType Parse(string raw) => raw switch
    {
        "User" => UserOrGroupType.User,
        "Queue" => UserOrGroupType.Queue,
        "Role" => UserOrGroupType.Role,
        "RoleAndSubordinates" => UserOrGroupType.RoleAndSubordinates,
        "RoleAndSubordinatesInternal" => UserOrGroupType.RoleAndSubordinatesInternal,
        "Organization" => UserOrGroupType.Organization,
        "Manager" => UserOrGroupType.Manager,
        "ManagerAndSubordinatesInternal" => UserOrGroupType.ManagerAndSubordinatesInternal,
        "Group" => UserOrGroupType.PublicGroup,
        _ => throw new ArgumentException($"'{raw}' is not a valid UserOrGroupType"),
    };
}

public enum RecordEnumerationDirection
{
    Ascending,   // "Ascending"
    Descending,  // "Descending"
}

public class IdentityResponseBase
{
    public string Id { get; set; } = "";

    [JsonPropertyName("attributes")]
    public JsonObject? Attributes { get; set; }
}

public class UserOrGroup
{
    public string Type { get; set; } = "";

    [JsonPropertyName("attributes")]
    public JsonObject? Attributes { get; set; }
}

public class PermissionSetAssignment : IdentityResponseBase
{
    public string? AssigneeId { get; set; }
    public string? PermissionSetId { get; set; }
    public bool? IsActive { get; set; }
}

public class UserRole : IdentityResponseBase
{
    public string? Name { get; set; }
    public string? ParentRoleId { get; set; }
    public string? DeveloperName { get; set; }
    public string? ContactAccessForAccountOwner { get; set; }
    public string? OpportunityAccessForAccountOwner { get; set; }
}

public class User : IdentityResponseBase
{
    public string? Name { get; set; }
    public string? Alias { get; set; }
    public string? Email { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? FederationIdentifier { get; set; }
    public string? UserName { get; set; }
    public bool? IsActive { get; set; }
    public string? UserType { get; set; }
    public bool IsFrozen { get; set; }
    public string? UserRoleId { get; set; }
    public string? ManagerId { get; set; }
    public UserRole? UserRole { get; set; }
    public List<PermissionSetAssignment>? PermissionSets { get; set; }
    public JsonObject? PermissionSetAssignments { get; set; }
}

public class EntityShareBase : IdentityResponseBase
{
    public string? UserOrGroupId { get; set; }
    public string? RowCause { get; set; }
    public UserOrGroup? UserOrGroup { get; set; }
}

public class Group : IdentityResponseBase
{
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? RelatedId { get; set; }
    public string? DeveloperName { get; set; }
    public bool? DoesIncludeBosses { get; set; }
    public JsonObject? GroupMembers { get; set; }
}

public class GroupMember : IdentityResponseBase
{
    public string? GroupId { get; set; }
    public string? UserOrGroupId { get; set; }
}

public class ObjectRecord : IdentityResponseBase
{
    public bool IsDeleted { get; set; }
    public JsonObject? Shares { get; set; }
}

public class UserLogin : IdentityResponseBase
{
    public string? UserId { get; set; }
    public bool IsFrozen { get; set; }
}

/// <summary>Maps Salesforce records (Account / Opportunity) to Territory2 assignments.</summary>
public class ObjectTerritory2Association : IdentityResponseBase
{
    public string? ObjectId { get; set; }
    public string? Territory2Id { get; set; }
    public string? AssociationCause { get; set; }
    public string? SobjectType { get; set; }
}

/// <summary>Maps users to Territory2 assignments.</summary>
public class UserTerritory2Association : IdentityResponseBase
{
    public string? UserId { get; set; }
    public string? Territory2Id { get; set; }
    public string? RoleInTerritory2 { get; set; }
}

/// <summary>Territory hierarchy node – holds the parent territory reference.</summary>
public class Territory2 : IdentityResponseBase
{
    public string? ParentTerritory2Id { get; set; }
}

public class Organization : IdentityResponseBase
{
    public EntityVisibility DefaultAccountAccess { get; set; } = EntityVisibility.None;
    public EntityVisibility DefaultContactAccess { get; set; } = EntityVisibility.None;
    public EntityVisibility DefaultOpportunityAccess { get; set; } = EntityVisibility.None;
    public EntityVisibility DefaultLeadAccess { get; set; } = EntityVisibility.None;
    public EntityVisibility DefaultCaseAccess { get; set; } = EntityVisibility.None;
    public EntityVisibility DefaultCampaignAccess { get; set; } = EntityVisibility.None;

    /// <summary>Dynamic field access, mirroring Python ``getattr(organization, name, EntityVisibility.NONE)``.</summary>
    public EntityVisibility GetVisibilityByFieldName(string fieldName) => fieldName switch
    {
        "DefaultAccountAccess" => DefaultAccountAccess,
        "DefaultContactAccess" => DefaultContactAccess,
        "DefaultOpportunityAccess" => DefaultOpportunityAccess,
        "DefaultLeadAccess" => DefaultLeadAccess,
        "DefaultCaseAccess" => DefaultCaseAccess,
        "DefaultCampaignAccess" => DefaultCampaignAccess,
        _ => EntityVisibility.None,
    };

    /// <summary>Dynamic field assignment, mirroring Python ``setattr(organization, name, value)``.</summary>
    public void SetVisibilityByFieldName(string fieldName, EntityVisibility value)
    {
        switch (fieldName)
        {
            case "DefaultAccountAccess": DefaultAccountAccess = value; break;
            case "DefaultContactAccess": DefaultContactAccess = value; break;
            case "DefaultOpportunityAccess": DefaultOpportunityAccess = value; break;
            case "DefaultLeadAccess": DefaultLeadAccess = value; break;
            case "DefaultCaseAccess": DefaultCaseAccess = value; break;
            case "DefaultCampaignAccess": DefaultCampaignAccess = value; break;
        }
    }
}

public class SfIdentityCheckpointState
{
    public string LastRecordId { get; set; } = "";
    public string NextUrl { get; set; } = "";
    public bool Exhausted { get; set; }
}

public class SalesforceIdentitySOQLResponseProcessor
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector");

    private static readonly JsonSerializerOptions SerializerOptions = new();

    private static readonly string[] OrganizationVisibilityKeys =
    {
        "DefaultAccountAccess",
        "DefaultContactAccess",
        "DefaultOpportunityAccess",
        "DefaultLeadAccess",
        "DefaultCaseAccess",
        "DefaultCampaignAccess",
    };

    /// <summary>Parse a Salesforce SOQL response into a list of <typeparamref name="T"/> model instances.</summary>
    public List<T> Get<T>(JsonObject? response) where T : IdentityResponseBase
    {
        var records = response?["records"] as JsonArray;
        if (records is null || records.Count == 0)
            return new List<T>();

        var results = new List<T>();
        foreach (var record in records)
        {
            try
            {
                var parsed = ParseRecord<T>(record as JsonObject);
                if (parsed is not null)
                    results.Add(parsed);
            }
            catch (Exception error)
            {
                var recordId = (record as JsonObject)?["Id"]?.ToString() ?? "(no Id)";
                Logger.Warning(
                    $"Failed to parse {typeof(T).Name} record {recordId}: {error.GetType().Name}: {error.Message}");
            }
        }
        return results;
    }

    /// <summary>Dispatch *record* to the appropriate model-specific parser.</summary>
    private T? ParseRecord<T>(JsonObject? record) where T : IdentityResponseBase
    {
        if (record is null || record.Count == 0)
            return null;

        var cleanRecord = new JsonObject();
        foreach (var (key, value) in record)
        {
            if (key != "attributes")
                cleanRecord[key] = value?.DeepClone();
        }

        if (typeof(T) == typeof(User))
            return (T)(object)ParseUser(cleanRecord);
        if (typeof(T) == typeof(EntityShareBase))
            return (T)(object)ParseEntityShare(cleanRecord);
        if (typeof(T) == typeof(ObjectRecord))
            return (T)(object)ParseObjectRecord(cleanRecord);
        if (typeof(T) == typeof(Organization))
            return (T)(object)ParseOrganization(cleanRecord);

        return cleanRecord.Deserialize<T>(SerializerOptions);
    }

    /// <summary>Parse a raw Salesforce record into a <see cref="User"/> model.</summary>
    private User ParseUser(JsonObject record)
    {
        var user = record.Deserialize<User>(SerializerOptions) ?? new User();
        if (record.ContainsKey("Username") && !record.ContainsKey("UserName"))
            user.UserName = record["Username"]?.GetValue<string>();
        return user;
    }

    /// <summary>Parse a raw Salesforce record into an <see cref="EntityShareBase"/> model.</summary>
    private EntityShareBase ParseEntityShare(JsonObject record)
    {
        var share = record.Deserialize<EntityShareBase>(SerializerOptions) ?? new EntityShareBase();
        if (record["UserOrGroup"] is JsonObject userOrGroup)
        {
            share.UserOrGroup = new UserOrGroup
            {
                Type = userOrGroup["Type"]?.GetValue<string>() ?? "",
                Attributes = userOrGroup["attributes"] as JsonObject,
            };
        }
        return share;
    }

    /// <summary>Parse a raw Salesforce record into an <see cref="ObjectRecord"/> model.</summary>
    private ObjectRecord ParseObjectRecord(JsonObject record)
    {
        return record.Deserialize<ObjectRecord>(SerializerOptions) ?? new ObjectRecord();
    }

    /// <summary>Parse a raw Salesforce record into an <see cref="Organization"/> model with visibility enums.</summary>
    private Organization ParseOrganization(JsonObject record)
    {
        var organization = new Organization { Id = record["Id"]?.GetValue<string>() ?? "" };
        foreach (var key in OrganizationVisibilityKeys)
        {
            var value = record[key]?.GetValue<string>();
            if (string.IsNullOrEmpty(value))
                continue;
            try
            {
                organization.SetVisibilityByFieldName(key, EntityVisibilityExtensions.Parse(value));
            }
            catch (ArgumentException)
            {
                Logger.Warning($"Unknown entity visibility {value} for {key}");
            }
        }
        return organization;
    }
}

public static class IdentitySyncQueries
{
    private static readonly List<string> OwdFields = BuildOwdFields();

    private static List<string> BuildOwdFields()
    {
        var fields = new List<string>(Settings.BuildOwdFieldMap().Values) { "DefaultCampaignAccess" };
        var seen = new HashSet<string>();
        var ordered = new List<string>();
        foreach (var field in fields)
        {
            if (seen.Add(field))
                ordered.Add(field);
        }
        return ordered;
    }

    public static readonly string OrgWideDefaultQuery = $"SELECT {string.Join(", ", OwdFields)} from Organization";

    public const string AllSharesFromRecords =
        "SELECT Id, IsDeleted, (SELECT Id, UserOrGroupId, UserOrGroup.Type from Shares) " +
        "from {0}{1} ORDER BY Id {2}";

    public const string GroupMembersQueryFormat = "SELECT Id, GroupId, UserOrGroupId from GroupMember{0} ORDER BY Id asc";

    public const string GroupTypeAndRelatedIdQuery =
        "SELECT Id, Name, Type, RelatedId, DoesIncludeBosses, " +
        "(SELECT UserOrGroupId from GroupMembers Limit 1) from Group{0} ORDER BY Id asc";

    public const string UsersQueryForContentIngestionFormat =
        "SELECT Id, Name, Alias, Email, FederationIdentifier, FirstName, LastName, " +
        "UserName, UserRoleId, UserRole.ParentRoleId, " +
        "(SELECT PermissionSet.Id, PermissionSet.IsOwnedByProfile, PermissionSet.Profile.Name, PermissionSet.Label " +
        "FROM PermissionSetAssignments " +
        "WHERE PermissionSetId IN (Select ParentId FROM ObjectPermissions WHERE SObjectType = '{0}' AND PermissionsRead = true)) " +
        "from User WHERE IsActive = True AND (NOT Name Like '%User%'){1} ORDER BY Id asc";

    public const string UsersQueryFormat =
        "SELECT Id, Name, Alias, Email, FederationIdentifier, FirstName, LastName, " +
        "UserName, UserRoleId, UserRole.ParentRoleId, IsActive, ManagerId " +
        "FROM User WHERE (NOT Name Like '%User%'){0} ORDER BY Id asc{1}";

    public const string UserLoginQuery = "SELECT Id, UserId FROM UserLogin Where IsFrozen = True{0} ORDER BY Id asc";

    public const string UserRoleQuery =
        "SELECT Id, ParentRoleId, ContactAccessForAccountOwner, OpportunityAccessForAccountOwner " +
        "FROM UserRole{0} ORDER BY Id asc";

    public const string UserAndMangerQuery = "SELECT Id, ManagerId from User{0} ORDER BY Id asc";

    // ── Territory-based ACL queries ──────────────────────────────────────────
    // Step 1 – fetch Territory2Ids for a given Account / Opportunity record
    public const string ObjectTerritory2AssociationQueryFormat =
        "SELECT Id, ObjectId, Territory2Id, AssociationCause, SobjectType " +
        "FROM ObjectTerritory2Association WHERE {0}";

    // Step 2 / 4 – fetch UserIds for a set of Territory2Ids
    public const string UserTerritory2AssociationQueryFormat =
        "SELECT Id, UserId, Territory2Id, RoleInTerritory2 " +
        "FROM UserTerritory2Association WHERE Territory2Id IN ({0})";

    // Step 3 – fetch the parent territory for a given Territory2 node
    public const string Territory2ParentQueryFormat =
        "SELECT Id, ParentTerritory2Id " +
        "FROM Territory2 WHERE Id = '{0}'";

    // Bulk queries for pre-warming caches ─────────────────────────────────
    // Fetch ALL territory→parent relationships in one SOQL call
    public const string AllTerritory2Query = "SELECT Id, ParentTerritory2Id FROM Territory2{0} ORDER BY Id asc";

    // Fetch ALL ObjectTerritory2Association records in one SOQL call
    public const string AllObjectTerritory2AssociationQuery =
        "SELECT Id, ObjectId, Territory2Id FROM ObjectTerritory2Association{0} ORDER BY Id asc";

    // Fetch ALL UserTerritory2Association records in one SOQL call
    public const string AllUserTerritory2AssociationQuery =
        "SELECT Id, UserId, Territory2Id FROM UserTerritory2Association{0} ORDER BY Id asc";

    // Fetch ALL groups with type/related info in one SOQL call
    public const string AllGroupsQuery =
        "SELECT Id, Name, Type, RelatedId, DoesIncludeBosses " +
        "FROM Group{0} ORDER BY Id asc";

    // Fetch ALL group members in one SOQL call
    public const string AllGroupMembersQuery =
        "SELECT Id, GroupId, UserOrGroupId FROM GroupMember{0} ORDER BY Id asc";

    // Fetch ALL users with role info for principal resolution
    public const string AllUsersWithRolesQuery =
        "SELECT Id, Name, Alias, Email, FederationIdentifier, FirstName, LastName, " +
        "UserName, UserRoleId, IsActive, ManagerId " +
        "FROM User WHERE IsActive = True AND (NOT Name Like '%User%'){0} ORDER BY Id asc";
}

public static class SalesforceConstants
{
    private static readonly JsonObject _schema = Settings.LoadSchemaConfig();

    // Per-object config keyed by objectName, loaded from schema.json
    public static readonly Dictionary<string, JsonObject> Objects = BuildObjects();

    // Object names in the order defined in schema.json
    public static readonly List<string> OrderedObjectNames = BuildOrderedObjectNames();

    private static Dictionary<string, JsonObject> BuildObjects()
    {
        var result = new Dictionary<string, JsonObject>();
        foreach (var node in _schema["objectList"] as JsonArray ?? new JsonArray())
        {
            if (node is JsonObject obj)
                result[obj["objectName"]!.GetValue<string>()] = obj;
        }
        return result;
    }

    private static List<string> BuildOrderedObjectNames()
    {
        var result = new List<string>();
        foreach (var node in _schema["objectList"] as JsonArray ?? new JsonArray())
        {
            if (node is JsonObject obj)
                result.Add(obj["objectName"]!.GetValue<string>());
        }
        return result;
    }
}

public class AsyncSalesforceClient
{
    public string InstanceUrl { get; }
    public string ApiVersion { get; }

    // Pooled client — reused across all SOQL queries to avoid repeated
    // TCP + TLS handshakes.  Thread-safe (HttpClient pools are locked internally).
    private readonly HttpClient _session;

    /// <summary>Initialize with a Salesforce *instanceUrl* and *apiVersion*.</summary>
    public AsyncSalesforceClient(string instanceUrl, string apiVersion)
    {
        InstanceUrl = instanceUrl.TrimEnd('/');
        ApiVersion = apiVersion;
        _session = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 20 })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>Execute a SOQL query against the ``/query`` endpoint.</summary>
    public Task<JsonObject> QueryAsync(string soql, string accessToken) =>
        ExecuteQueryAsync(soql, accessToken, useQueryAll: false);

    /// <summary>Execute a SOQL query against the ``/queryAll`` endpoint (includes deleted/archived).</summary>
    public Task<JsonObject> QueryAllAsync(string soql, string accessToken) =>
        ExecuteQueryAsync(soql, accessToken, useQueryAll: true);

    /// <summary>Send a SOQL request and return the JSON response.</summary>
    private async Task<JsonObject> ExecuteQueryAsync(string soql, string accessToken, bool useQueryAll)
    {
        var endpoint = useQueryAll ? "queryAll" : "query";
        var queryUrl =
            $"{InstanceUrl}/services/data/{ApiVersion}/{endpoint}?" +
            $"q={WebUtility.UrlEncode(soql)}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var request = new HttpRequestMessage(HttpMethod.Get, queryUrl);
        request.Headers.TryAddWithoutValidation("accept", "application/json");
        request.Headers.TryAddWithoutValidation("authorization", $"Bearer {accessToken}");
        using var response = await _session.SendAsync(request, cts.Token);
        var text = await response.Content.ReadAsStringAsync(CancellationToken.None);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
    }
}

public class ClientHelperForIdentitySync
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector");

    public AsyncSalesforceClient SalesforceClient { get; }
    public string InstanceUrl { get; }
    public string AccessToken { get; }
    public int BatchSize { get; }
    public SalesforceIdentitySOQLResponseProcessor ResponseProcessor { get; }

    /// <summary>Initialize with a Salesforce async client, instance URL, and access token.</summary>
    public ClientHelperForIdentitySync(
        AsyncSalesforceClient salesforceClient,
        string instanceUrl,
        string accessToken,
        int batchSize = 100)
    {
        SalesforceClient = salesforceClient;
        InstanceUrl = instanceUrl;
        AccessToken = accessToken;
        BatchSize = batchSize;
        ResponseProcessor = new SalesforceIdentitySOQLResponseProcessor();
    }

    /// <summary>Fetch the Organization record and return it with normalized visibility values.</summary>
    public async Task<Organization> GetOrgWideDefaultsFromSalesforceAsync()
    {
        var response = await ExecuteQueryAsync(IdentitySyncQueries.OrgWideDefaultQuery);
        var organizations = ResponseProcessor.Get<Organization>(response);
        if (organizations.Count == 0)
            throw new ArgumentException("No organization record found");

        var organization = organizations[0];
        foreach (var key in new[] { "DefaultAccountAccess", "DefaultOpportunityAccess" })
        {
            var value = organization.GetVisibilityByFieldName(key);
            if (value is EntityVisibility.ControlledByCampaign or EntityVisibility.ControlledByLeadOrContact)
                organization.SetVisibilityByFieldName(key, EntityVisibility.None);
        }
        return organization;
    }

    /// <summary>
    /// Return a mapping of object names to their org-wide default visibility.
    ///
    /// Strategy
    /// --------
    /// 1. Try a single bulk ``SELECT &lt;all_owd_fields&gt; FROM Organization``.
    /// 2. If that fails (e.g. a field is missing from this org), retry with
    ///    one ``SELECT &lt;field&gt; FROM Organization`` per owdField.
    /// 3. Any field/object that still fails defaults to ``NONE`` (Private) —
    ///    never to a permissive value — to preserve least privilege.
    /// </summary>
    public async Task<Dictionary<string, EntityVisibility>> GetOrgWideDefaultsMapAsync()
    {
        var owdFieldMap = Settings.BuildOwdFieldMap();  // {objectName: owdField}

        // ── 1. Bulk attempt ───────────────────────────────────────────────────
        try
        {
            var organization = await GetOrgWideDefaultsFromSalesforceAsync();
            var bulkResult = new Dictionary<string, EntityVisibility>();
            foreach (var (objName, objCfg) in SalesforceConstants.Objects)
            {
                if (!objCfg.ContainsKey("owdField"))
                    continue;
                // Default to NONE (Private) — not PUBLIC_READ_WRITE — on missing attribute
                bulkResult[objName] = organization.GetVisibilityByFieldName(objCfg["owdField"]!.GetValue<string>());
            }
            return bulkResult;
        }
        catch (Exception exc)
        {
            Logger.Warning(
                $"[OWD] Bulk Organization query failed ({exc.Message}); retrying per-field (defaulting to Private on failure)");
        }

        // ── 2. Per-field fallback ─────────────────────────────────────────────
        // Invert: owdField → [objectName, ...]
        var fieldToObjects = new Dictionary<string, List<string>>();
        foreach (var (objName, owdField) in owdFieldMap)
        {
            if (!fieldToObjects.TryGetValue(owdField, out var list))
            {
                list = new List<string>();
                fieldToObjects[owdField] = list;
            }
            list.Add(objName);
        }

        var result = new Dictionary<string, EntityVisibility>();
        foreach (var (owdField, objNames) in fieldToObjects)
        {
            var soql = $"SELECT {owdField} FROM Organization";
            EntityVisibility visibility;
            try
            {
                var response = await ExecuteQueryAsync(soql);
                var rows = ResponseProcessor.Get<Organization>(response);
                if (rows.Count > 0)
                {
                    visibility = rows[0].GetVisibilityByFieldName(owdField);
                }
                else
                {
                    Logger.Warning($"[OWD] Per-field query for {owdField} returned no rows; defaulting to Private");
                    visibility = EntityVisibility.None;
                }
            }
            catch (Exception fieldExc)
            {
                Logger.Warning(
                    $"[OWD] Per-field query for {owdField} failed ({fieldExc.Message}); defaulting to Private");
                visibility = EntityVisibility.None;
            }

            foreach (var objName in objNames)
            {
                result[objName] = visibility;
                Logger.Info($"[OWD] {objName} (Organization.{owdField}) → {visibility.Value()} (per-field fallback)");
            }
        }

        return result;
    }

    /// <summary>Fetch records of *objectName* that have associated share entries.</summary>
    public async Task<List<ObjectRecord>> GetRecordsWithSharesAsync(
        string objectName,
        SfIdentityCheckpointState? checkpoint = null,
        bool fetchAll = false,
        string filterCondition = "",
        RecordEnumerationDirection direction = RecordEnumerationDirection.Ascending)
    {
        var order = direction == RecordEnumerationDirection.Ascending ? "asc" : "desc";
        var soql = !string.IsNullOrEmpty(filterCondition)
            ? string.Format(
                IdentitySyncQueries.AllSharesFromRecords,
                objectName,
                $" WHERE {filterCondition}{{0}}",
                order)
            : string.Format(IdentitySyncQueries.AllSharesFromRecords, objectName, "{0}", order);

        var records = await GetRecordsUsingLastIdAsync<ObjectRecord>(
            soql,
            fetchAll,
            !string.IsNullOrEmpty(filterCondition),
            checkpoint,
            useQueryAll: true,
            direction: direction);
        return records
            .Where(record =>
                record.Shares?["records"] is JsonArray shareRecords &&
                shareRecords.Count > 0 &&
                !record.IsDeleted)
            .ToList();
    }

    /// <summary>Fetch Salesforce GroupMember records with optional filtering and pagination.</summary>
    public async Task<List<GroupMember>> GetGroupMembersAsync(
        string filterCondition = "",
        SfIdentityCheckpointState? checkpoint = null,
        bool fetchAll = true)
    {
        var soql = !string.IsNullOrEmpty(filterCondition)
            ? string.Format(IdentitySyncQueries.GroupMembersQueryFormat, $" WHERE {filterCondition}{{0}}")
            : string.Format(IdentitySyncQueries.GroupMembersQueryFormat, "{0}");

        return await GetRecordsUsingLastIdAsync<GroupMember>(
            soql,
            fetchAll,
            !string.IsNullOrEmpty(filterCondition),
            checkpoint);
    }

    /// <summary>Fetch Group records with their Type and RelatedId fields.</summary>
    public async Task<List<Group>> GetGroupTypeAndRelatedIdAsync(
        string filterCondition = "",
        SfIdentityCheckpointState? checkpoint = null,
        bool fetchAll = true)
    {
        var soql = !string.IsNullOrEmpty(filterCondition)
            ? string.Format(IdentitySyncQueries.GroupTypeAndRelatedIdQuery, $" WHERE {filterCondition}{{0}}")
            : string.Format(IdentitySyncQueries.GroupTypeAndRelatedIdQuery, "{0}");

        return await GetRecordsUsingLastIdAsync<Group>(
            soql,
            fetchAll,
            !string.IsNullOrEmpty(filterCondition),
            checkpoint);
    }

    /// <summary>Fetch active users with read permissions on *objectName*, including permission set details.</summary>
    public async Task<List<User>> GetUsersForContentIngestionAsync(
        string objectName,
        string filterConditions = "",
        SfIdentityCheckpointState? checkpoint = null,
        bool fetchAll = true)
    {
        var soql = string.Format(IdentitySyncQueries.UsersQueryForContentIngestionFormat, objectName, "{0}");
        if (!string.IsNullOrEmpty(filterConditions))
        {
            soql = string.Format(
                IdentitySyncQueries.UsersQueryForContentIngestionFormat,
                objectName,
                $" AND {filterConditions}{{0}}");
        }

        var users = await GetRecordsUsingLastIdAsync<User>(
            soql,
            fetchAll,
            true,
            checkpoint);

        foreach (var user in users)
        {
            if (user.PermissionSetAssignments is not null &&
                user.PermissionSetAssignments["records"] is JsonArray psaRecords &&
                psaRecords.Count > 0)
            {
                user.PermissionSets = ResponseProcessor.Get<PermissionSetAssignment>(user.PermissionSetAssignments);
            }
        }
        return users;
    }

    /// <summary>
    /// Fetch authorized users (and groups when visibility is ``NONE``) for ACL resolution.
    ///
    /// User batches are fetched **concurrently** to reduce wall-clock time when
    /// there are many batches.
    ///
    /// Returns:
    ///     A tuple of (per-object user dicts, group dict).
    /// </summary>
    public async Task<(Dictionary<string, Dictionary<string, User>> AuthorizedUsersForSfObjects, Dictionary<string, Group> SfGroups)>
        GetAuthorizedUsersAndGroupsFromSalesforceAsync(
            List<string> userIds,
            List<string> groupIds,
            SalesforceObjectHandler salesforceObjectHandler,
            EntityVisibility entityVisibility,
            HashSet<string> frozenUsers)
    {
        var authorizedUsersForSfObjects = new Dictionary<string, Dictionary<string, User>>
        {
            [salesforceObjectHandler.ObjectName] = new Dictionary<string, User>(),
        };
        var childHandlers = salesforceObjectHandler.ChildHandlers ?? new List<SalesforceObjectHandler>();
        foreach (var child in childHandlers)
            authorizedUsersForSfObjects[child.ObjectName] = new Dictionary<string, User>();

        var distinctUsers = userIds.Distinct().ToList();
        var batchSize = BatchSize;
        var userBatches = new List<List<string>>();
        for (var index = 0; index < distinctUsers.Count; index += batchSize)
            userBatches.Add(distinctUsers.GetRange(index, Math.Min(batchSize, distinctUsers.Count - index)));

        // ── Fetch user batches concurrently ───────────────────────────────
        async Task<List<(string ObjectName, List<User> Users)>> FetchBatchAsync(List<string> userBatch)
        {
            // Fetch users for the parent + children in one batch, return (objectName, users) pairs.
            var quotedUserIds = string.Join(", ", userBatch.Select(uid => "'" + uid + "'"));
            var filterStr = $"Id in ({quotedUserIds})";

            // Launch parent + child queries concurrently
            var fetchTasks = new List<Task<List<User>>>
            {
                GetUsersForContentIngestionAsync(salesforceObjectHandler.ObjectName, filterStr),
            };
            foreach (var child in childHandlers)
                fetchTasks.Add(GetUsersForContentIngestionAsync(child.ObjectName, filterStr));

            var fetchResults = await Task.WhenAll(fetchTasks);

            var pairs = new List<(string ObjectName, List<User> Users)>
            {
                (salesforceObjectHandler.ObjectName, fetchResults[0]),
            };
            for (var i = 0; i < childHandlers.Count; i++)
                pairs.Add((childHandlers[i].ObjectName, fetchResults[i + 1]));
            return pairs;
        }

        var nonEmptyBatches = userBatches.Where(batch => batch.Count > 0).ToList();
        if (nonEmptyBatches.Count > 0)
        {
            var batchResults = await Task.WhenAll(nonEmptyBatches.Select(FetchBatchAsync));
            foreach (var pairs in batchResults)
            {
                foreach (var (objName, users) in pairs)
                {
                    var target = authorizedUsersForSfObjects[objName];
                    foreach (var user in users)
                    {
                        user.IsFrozen = frozenUsers.Contains(user.Id);
                        target[user.Id] = user;
                    }
                }
            }
        }

        var sfGroups = new Dictionary<string, Group>();
        if (entityVisibility == EntityVisibility.None)
        {
            var distinctGroups = groupIds.Distinct().ToList();
            var groupBatches = new List<List<string>>();
            for (var index = 0; index < distinctGroups.Count; index += batchSize)
                groupBatches.Add(distinctGroups.GetRange(index, Math.Min(batchSize, distinctGroups.Count - index)));

            var allGroups = new List<Group>();
            foreach (var groupBatch in groupBatches)
            {
                if (groupBatch.Count == 0)
                    continue;
                var quotedGroupIds = string.Join(", ", groupBatch.Select(groupId => "'" + groupId + "'"));
                var filterStr = $"Id in ({quotedGroupIds})";
                allGroups.AddRange(await GetGroupTypeAndRelatedIdAsync(filterStr));
            }
            foreach (var group in allGroups)
                sfGroups[group.Id] = group;
        }

        return (authorizedUsersForSfObjects, sfGroups);
    }

    /// <summary>Fetch User records from Salesforce with optional filtering and limit.</summary>
    public async Task<List<User>> GetUsersFromSalesforceAsync(
        string filterConditions = "",
        int limit = 0,
        SfIdentityCheckpointState? checkpoint = null,
        bool fetchAll = true)
    {
        var limitClause = limit > 0 ? $" Limit {limit}" : "";
        var filterClause = !string.IsNullOrEmpty(filterConditions) ? $" AND {filterConditions}{{0}}" : "{0}";
        var soql = string.Format(IdentitySyncQueries.UsersQueryFormat, filterClause, limitClause);
        return await GetRecordsUsingLastIdAsync<User>(
            soql,
            fetchAll,
            true,
            checkpoint);
    }

    /// <summary>Fetch all frozen user login records from Salesforce.</summary>
    public async Task<List<UserLogin>> GetFrozenUsersAsync()
    {
        return await GetRecordsUsingLastIdAsync<UserLogin>(
            IdentitySyncQueries.UserLoginQuery,
            true,
            true,
            null);
    }

    /// <summary>Fetch the UserRole hierarchy from Salesforce.</summary>
    public async Task<List<UserRole>> GetUserRoleHierarchyFromSalesforceAsync(
        SfIdentityCheckpointState? checkpoint = null,
        bool fetchAll = true)
    {
        return await GetRecordsUsingLastIdAsync<UserRole>(
            IdentitySyncQueries.UserRoleQuery,
            fetchAll,
            false,
            checkpoint);
    }

    /// <summary>Fetch all users with their ManagerId fields.</summary>
    public async Task<List<User>> GetUsersAndManagersAsync()
    {
        return await GetRecordsUsingLastIdAsync<User>(
            IdentitySyncQueries.UserAndMangerQuery,
            true,
            false,
            null);
    }

    // ── Territory-based ACL helpers ──────────────────────────────────────────

    /// <summary>
    /// Step 1: Fetch Territory2Ids assigned to a given Account / Opportunity record.
    ///
    /// Queries: ObjectTerritory2Association WHERE ObjectId = '&lt;objectId&gt;'
    /// </summary>
    public async Task<List<string>> GetTerritoryIdsForRecordAsync(string objectId)
    {
        var soql = string.Format(
            IdentitySyncQueries.ObjectTerritory2AssociationQueryFormat,
            $"ObjectId = '{objectId}'");
        var response = await ExecuteQueryAsync(soql);
        var associations = ResponseProcessor.Get<ObjectTerritory2Association>(response);
        return associations
            .Where(a => !string.IsNullOrEmpty(a.Territory2Id))
            .Select(a => a.Territory2Id!)
            .ToList();
    }

    /// <summary>
    /// Steps 2 / 4: Fetch UserIds for a list of Territory2Ids.
    ///
    /// Queries: UserTerritory2Association WHERE Territory2Id IN (&lt;territoryIds&gt;)
    /// </summary>
    public async Task<List<string>> GetUsersForTerritoriesAsync(List<string> territoryIds)
    {
        if (territoryIds.Count == 0)
            return new List<string>();
        var quoted = string.Join(", ", territoryIds.Distinct().OrderBy(t => t, StringComparer.Ordinal).Select(t => $"'{t}'"));
        var soql = string.Format(IdentitySyncQueries.UserTerritory2AssociationQueryFormat, quoted);
        var response = await ExecuteQueryAsync(soql);
        var associations = ResponseProcessor.Get<UserTerritory2Association>(response);
        return associations
            .Where(a => !string.IsNullOrEmpty(a.UserId))
            .Select(a => a.UserId!)
            .ToList();
    }

    /// <summary>
    /// Step 3: Fetch the ParentTerritory2Id for a given Territory2 node.
    ///
    /// Returns null when there is no parent (i.e. the territory is a root node).
    /// Queries: Territory2 WHERE Id = '&lt;territoryId&gt;'
    /// </summary>
    public async Task<string?> GetParentTerritoryIdAsync(string territoryId)
    {
        var soql = string.Format(IdentitySyncQueries.Territory2ParentQueryFormat, territoryId);
        var response = await ExecuteQueryAsync(soql);
        var territories = ResponseProcessor.Get<Territory2>(response);
        if (territories.Count > 0 && !string.IsNullOrEmpty(territories[0].ParentTerritory2Id))
            return territories[0].ParentTerritory2Id;
        return null;
    }

    // ── Bulk pre-warm helpers ────────────────────────────────────────────────
    // These methods fetch entire tables in a few paginated SOQL calls rather
    // than making per-record queries.  They return pre-built lookup dicts so
    // the ACL resolver can operate entirely in-memory.

    /// <summary>Fetch ALL Territory2 records and return ``{territoryId: parentId}``.</summary>
    public async Task<Dictionary<string, string?>> BulkFetchAllTerritoriesAsync()
    {
        var territories = await GetRecordsUsingLastIdAsync<Territory2>(
            IdentitySyncQueries.AllTerritory2Query, true, false, null);
        var result = new Dictionary<string, string?>();
        foreach (var territory in territories)
        {
            if (!string.IsNullOrEmpty(territory.Id))
                result[territory.Id] = territory.ParentTerritory2Id;
        }
        return result;
    }

    /// <summary>
    /// Fetch ALL ObjectTerritory2Association records.
    ///
    /// Returns ``{objectId: [territoryId, ...]}``.
    /// </summary>
    public async Task<Dictionary<string, List<string>>> BulkFetchObjectTerritoryAssociationsAsync()
    {
        var associations = await GetRecordsUsingLastIdAsync<ObjectTerritory2Association>(
            IdentitySyncQueries.AllObjectTerritory2AssociationQuery, true, false, null);
        var result = new Dictionary<string, List<string>>();
        foreach (var a in associations)
        {
            if (!string.IsNullOrEmpty(a.ObjectId) && !string.IsNullOrEmpty(a.Territory2Id))
            {
                if (!result.TryGetValue(a.ObjectId!, out var list))
                {
                    list = new List<string>();
                    result[a.ObjectId!] = list;
                }
                list.Add(a.Territory2Id!);
            }
        }
        return result;
    }

    /// <summary>
    /// Fetch ALL UserTerritory2Association records.
    ///
    /// Returns ``{territoryId: {userId, ...}}``.
    /// </summary>
    public async Task<Dictionary<string, HashSet<string>>> BulkFetchUserTerritoryAssociationsAsync()
    {
        var associations = await GetRecordsUsingLastIdAsync<UserTerritory2Association>(
            IdentitySyncQueries.AllUserTerritory2AssociationQuery, true, false, null);
        var result = new Dictionary<string, HashSet<string>>();
        foreach (var a in associations)
        {
            if (!string.IsNullOrEmpty(a.Territory2Id) && !string.IsNullOrEmpty(a.UserId))
            {
                if (!result.TryGetValue(a.Territory2Id!, out var set))
                {
                    set = new HashSet<string>();
                    result[a.Territory2Id!] = set;
                }
                set.Add(a.UserId!);
            }
        }
        return result;
    }

    /// <summary>Fetch ALL Group records in one paginated sweep.</summary>
    public async Task<List<Group>> BulkFetchAllGroupsAsync()
    {
        return await GetRecordsUsingLastIdAsync<Group>(
            IdentitySyncQueries.AllGroupsQuery, true, false, null);
    }

    /// <summary>
    /// Fetch ALL GroupMember records.
    ///
    /// Returns ``{groupId: [userOrGroupId, ...]}``.
    /// </summary>
    public async Task<Dictionary<string, List<string>>> BulkFetchAllGroupMembersAsync()
    {
        var members = await GetRecordsUsingLastIdAsync<GroupMember>(
            IdentitySyncQueries.AllGroupMembersQuery, true, false, null);
        var result = new Dictionary<string, List<string>>();
        foreach (var m in members)
        {
            if (!string.IsNullOrEmpty(m.GroupId) && !string.IsNullOrEmpty(m.UserOrGroupId))
            {
                if (!result.TryGetValue(m.GroupId!, out var list))
                {
                    list = new List<string>();
                    result[m.GroupId!] = list;
                }
                list.Add(m.UserOrGroupId!);
            }
        }
        return result;
    }

    /// <summary>Paginate through Salesforce records using ``Id``-based keyset pagination.</summary>
    private async Task<List<T>> GetRecordsUsingLastIdAsync<T>(
        string soqlFormat,
        bool fetchAll,
        bool containsFilterConditions,
        SfIdentityCheckpointState? checkpoint,
        bool useQueryAll = false,
        RecordEnumerationDirection direction = RecordEnumerationDirection.Ascending)
        where T : IdentityResponseBase
    {
        // Process a page of results and return the next pagination cursor.
        static string Processor(List<T> currentSet, JsonObject records, string lastId, List<T> results)
        {
            if (!string.IsNullOrEmpty(lastId) && currentSet.Count > 0 && lastId == currentSet[0].Id)
                currentSet.RemoveAt(0);
            results.AddRange(currentSet);
            var done = records["done"]?.GetValue<bool>() ?? true;
            var nextRecordsUrl = records["nextRecordsUrl"]?.GetValue<string>();
            if ((!done || !string.IsNullOrEmpty(nextRecordsUrl)) && currentSet.Count > 0)
                return currentSet[^1].Id;
            return "";
        }

        return await GetRecordsUsingCustomLastIdAsync<T>(
            soqlFormat,
            fetchAll,
            containsFilterConditions,
            Processor,
            "Id",
            checkpoint,
            useQueryAll,
            direction);
    }

    /// <summary>Paginate through Salesforce records using a custom field for keyset pagination.</summary>
    private async Task<List<T>> GetRecordsUsingCustomLastIdAsync<T>(
        string soqlFormat,
        bool fetchAll,
        bool containsFilterConditions,
        Func<List<T>, JsonObject, string, List<T>, string> currentSetProcessor,
        string lastIdFieldName,
        SfIdentityCheckpointState? checkpoint,
        bool useQueryAll = false,
        RecordEnumerationDirection direction = RecordEnumerationDirection.Ascending)
        where T : IdentityResponseBase
    {
        var lastId = checkpoint?.LastRecordId ?? "";
        if (checkpoint is not null)
            checkpoint.NextUrl = "";

        var results = new List<T>();
        var comparison = direction == RecordEnumerationDirection.Ascending ? ">" : "<";

        while (true)
        {
            var whereClause = !string.IsNullOrEmpty(lastId)
                ? $"{(containsFilterConditions ? " AND " : " WHERE ")}{lastIdFieldName} {comparison} '{lastId}'"
                : "";

            var soql = string.Format(soqlFormat, whereClause);
            var response = useQueryAll ? await ExecuteQueryAllAsync(soql) : await ExecuteQueryAsync(soql);
            if (response is null || response["records"] is not JsonArray records || records.Count == 0)
                break;

            var currentSet = ResponseProcessor.Get<T>(response);
            if (currentSet.Count > 0)
            {
                var newLastId = currentSetProcessor(currentSet, response, lastId, results);
                lastId = newLastId == lastId ? "" : newLastId;
            }
            else
            {
                lastId = "";
            }

            if (!fetchAll || string.IsNullOrEmpty(lastId))
                break;
        }

        if (checkpoint is not null)
        {
            checkpoint.LastRecordId = lastId;
            checkpoint.Exhausted = string.IsNullOrEmpty(lastId);
        }

        return results;
    }

    /// <summary>Execute a SOQL query via the Salesforce client.</summary>
    private Task<JsonObject> ExecuteQueryAsync(string soql) =>
        SalesforceClient.QueryAsync(soql, AccessToken);

    /// <summary>Execute a SOQL queryAll via the Salesforce client.</summary>
    private Task<JsonObject> ExecuteQueryAllAsync(string soql) =>
        SalesforceClient.QueryAllAsync(soql, AccessToken);
}
