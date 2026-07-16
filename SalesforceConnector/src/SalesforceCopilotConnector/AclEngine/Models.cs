// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// acl_engine/models.py
// --------------------
// Shared data classes and enumerations used across the ACL engine.
// No Salesforce or HTTP logic lives here – pure data containers only.

namespace SalesforceCopilotConnector.AclEngine;

// ── Sentinel ──────────────────────────────────────────────────────────────────
// Returned in the user_ids set when the record is visible to the entire org.
// Callers should emit a tenant-wide "grant everyone" ACL entry instead of
// enumerating individual users.
public static class Models
{
    public const string PublicSentinel = "__PUBLIC__";
}


// ── Org-Wide Default visibility values ───────────────────────────────────────

/// <summary>Mirrors Salesforce InternalSharingModel / OWD string values.</summary>
public enum OWDVisibility
{
    Private,
    PublicRead,
    PublicReadWrite,
    PublicReadWriteTransfer,
    All,
    ControlledByParent,
    ControlledByCampaign,
    ControlledByLeadOrContact,
}

public static class OWDVisibilityExtensions
{
    /// <summary>The Salesforce string value for this enum member (Python ``.value``).</summary>
    public static string Value(this OWDVisibility visibility) => visibility switch
    {
        OWDVisibility.Private => "Private",
        OWDVisibility.PublicRead => "Read",
        OWDVisibility.PublicReadWrite => "Edit",
        OWDVisibility.PublicReadWriteTransfer => "ReadEditTransfer",
        OWDVisibility.All => "All",
        OWDVisibility.ControlledByParent => "ControlledByParent",
        OWDVisibility.ControlledByCampaign => "ControlledByCampaign",
        OWDVisibility.ControlledByLeadOrContact => "ControlledByLeadOrContact",
        _ => throw new ArgumentOutOfRangeException(nameof(visibility), visibility, null),
    };
}


// ── Group type values returned by the Salesforce Group object ─────────────────

/// <summary>Values that can appear in Group.Type (and as UserOrGroup.Type on share rows).</summary>
public enum GroupType
{
    User,
    Queue,
    Role,
    RoleAndSubordinates,
    RoleAndSubordinatesInternal,
    Organization,
    Manager,
    ManagerAndSubordinatesInternal,
    PublicGroup,
    Territory,
    TerritoryAndSubordinates,
    TerritoryAndSubordinatesInternal,
}

public static class GroupTypeExtensions
{
    /// <summary>The Salesforce string value for this enum member (Python ``.value``).</summary>
    public static string Value(this GroupType type) => type switch
    {
        GroupType.User => "User",
        GroupType.Queue => "Queue",
        GroupType.Role => "Role",
        GroupType.RoleAndSubordinates => "RoleAndSubordinates",
        GroupType.RoleAndSubordinatesInternal => "RoleAndSubordinatesInternal",
        GroupType.Organization => "Organization",
        GroupType.Manager => "Manager",
        GroupType.ManagerAndSubordinatesInternal => "ManagerAndSubordinatesInternal",
        GroupType.PublicGroup => "Group",
        GroupType.Territory => "Territory",
        GroupType.TerritoryAndSubordinates => "TerritoryAndSubordinates",
        GroupType.TerritoryAndSubordinatesInternal => "TerritoryAndSubordinatesInternal",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}


// ── Data containers ───────────────────────────────────────────────────────────

/// <summary>
/// One row from an &lt;ObjectType&gt;Share table.
///
/// Fields
/// ------
/// UserOrGroupId : Salesforce User or Group Id.
/// RowCause      : Why the share was created (e.g. "Manual", "Rule",
///                 "Territory", "Owner").
/// AccessLevel   : The level of access granted (e.g. "Read", "Edit",
///                 "All").  Field name varies by object; discovered
///                 dynamically by ShareFetcher.
/// </summary>
public class ShareEntry
{
    public string UserOrGroupId { get; set; }
    public string? RowCause { get; set; }
    public string? AccessLevel { get; set; }

    public ShareEntry(string userOrGroupId, string? rowCause = null, string? accessLevel = null)
    {
        UserOrGroupId = userOrGroupId;
        RowCause = rowCause;
        AccessLevel = accessLevel;
    }
}


/// <summary>
/// Minimal representation of a Salesforce Group record.
///
/// <c>Type</c>      – GroupType value (e.g. "Role", "Queue", "Manager", …)
/// <c>RelatedId</c> – For dynamic groups (Role, Territory, Manager) this points
///                    to the underlying RoleId / Territory2Id / UserId.
/// </summary>
public class GroupRecord
{
    public string Id { get; set; }
    public string? Type { get; set; }
    public string? RelatedId { get; set; }
    public bool? DoesIncludeBosses { get; set; }

    public GroupRecord(string id, string? type = null, string? relatedId = null, bool? doesIncludeBosses = null)
    {
        Id = id;
        Type = type;
        RelatedId = relatedId;
        DoesIncludeBosses = doesIncludeBosses;
    }
}


/// <summary>
/// Final output produced by AclResolver for a single record.
///
/// Fields
/// ------
/// ObjectType : Salesforce object API name (e.g. "Account").
/// RecordId   : The 18-char Salesforce record Id.
/// Owd        : The raw OWD string fetched for this object type.
/// IsPublic   : True when the record is visible to every user in the org
///              (OWD is public-readable).  When True, user_ids is empty.
/// UserIds    : Set of Salesforce User Ids that may see this record.
///              Contains PUBLIC_SENTINEL when is_public is True.
/// </summary>
public class AclResult
{
    public string ObjectType { get; set; }
    public string RecordId { get; set; }
    public string Owd { get; set; }
    public bool IsPublic { get; set; }
    public HashSet<string> UserIds { get; set; }

    public AclResult(
        string objectType,
        string recordId,
        string owd = "",
        bool isPublic = false,
        HashSet<string>? userIds = null)
    {
        ObjectType = objectType;
        RecordId = recordId;
        Owd = owd;
        IsPublic = isPublic;
        UserIds = userIds ?? new HashSet<string>();
    }
}
