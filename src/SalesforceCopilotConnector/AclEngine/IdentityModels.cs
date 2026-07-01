// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// acl_engine/identity_models.py
// -----------------------------
// Data models and enumerations for the Identity Crawl and Group-based ACL
// resolution pipeline.
//
// These models represent Salesforce sharing concepts (users, groups, roles,
// share entries) and the group identity types used when creating external
// groups in Microsoft Graph.

namespace SalesforceCopilotConnector.AclEngine;

// ── Org-Wide Default visibility ──────────────────────────────────────────────

/// <summary>Org-Wide Default visibility of a Salesforce object.</summary>
public enum EntityVisibility
{
    None,                       // Private
    Read,                       // Public Read
    Edit,                       // Public Read/Write
    ReadEditTransfer,           // Public Read/Write/Transfer
    ControlledByParent,
    ControlledByLeadOrContact,
    ControlledByCampaign,
}

public static class EntityVisibilityExtensions
{
    /// <summary>The Salesforce string value for this enum member (Python ``.value``).</summary>
    public static string Value(this EntityVisibility visibility) => visibility.ToString();

    /// <summary>
    /// Value-based construction, mirroring Python ``EntityVisibility(raw)``.
    /// Throws <see cref="ArgumentException"/> for unknown values (Python ValueError).
    /// </summary>
    public static EntityVisibility Parse(string raw) => raw switch
    {
        "None" => EntityVisibility.None,
        "Read" => EntityVisibility.Read,
        "Edit" => EntityVisibility.Edit,
        "ReadEditTransfer" => EntityVisibility.ReadEditTransfer,
        "ControlledByParent" => EntityVisibility.ControlledByParent,
        "ControlledByLeadOrContact" => EntityVisibility.ControlledByLeadOrContact,
        "ControlledByCampaign" => EntityVisibility.ControlledByCampaign,
        _ => throw new ArgumentException($"'{raw}' is not a valid EntityVisibility"),
    };
}


// ── User/Group type on share records ────────────────────────────────────────

/// <summary>Type from the UserOrGroup relationship on share records.</summary>
public enum UserOrGroupType
{
    User,
    Queue,
    Role,
    RoleAndSubordinates,
    RoleAndSubordinatesInternal,
    Organization,
    Manager,
    ManagerAndSubordinatesInternal,
    Territory,
    TerritoryAndSubordinates,
    TerritoryAndSubordinatesInternal,
    Regular,
    Personal,
}

public static class UserOrGroupTypeExtensions
{
    /// <summary>The Salesforce string value for this enum member (Python ``.value``).</summary>
    public static string Value(this UserOrGroupType type) => type.ToString();

    /// <summary>
    /// Value-based construction, mirroring Python ``UserOrGroupType(raw)``.
    /// Throws <see cref="ArgumentException"/> for unknown values (Python ValueError).
    /// </summary>
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
        "Territory" => UserOrGroupType.Territory,
        "TerritoryAndSubordinates" => UserOrGroupType.TerritoryAndSubordinates,
        "TerritoryAndSubordinatesInternal" => UserOrGroupType.TerritoryAndSubordinatesInternal,
        "Regular" => UserOrGroupType.Regular,
        "Personal" => UserOrGroupType.Personal,
        _ => throw new ArgumentException($"'{raw}' is not a valid UserOrGroupType"),
    };
}


// ── Child group identity type ────────────────────────────────────────────────

/// <summary>Type of child group emitted during identity sync.</summary>
public enum GroupIdentityType
{
    RoleWithParent,
    RoleWithoutParent,
    RoleAndSubWithParent,
    RoleAndSubWithoutParent,
    PublicGroup,
    GlobalAccessUsers,
    AllInternalUsers,
    Manager,
    ManagerAndSubordinates,
    Territory,
    TerritoryAndSubordinates,
}

public static class GroupIdentityTypeExtensions
{
    /// <summary>The string value for this enum member (Python ``.value``).</summary>
    public static string Value(this GroupIdentityType type) => type switch
    {
        GroupIdentityType.RoleWithParent => "RoleWithParentRole",
        GroupIdentityType.RoleWithoutParent => "RoleWithoutParentRole",
        GroupIdentityType.RoleAndSubWithParent => "RoleAndSubWithParentRole",
        GroupIdentityType.RoleAndSubWithoutParent => "RoleAndSubWithoutParentRole",
        GroupIdentityType.PublicGroup => "PublicGroup",
        GroupIdentityType.GlobalAccessUsers => "GlobalAccessUsers",
        GroupIdentityType.AllInternalUsers => "AllInternalUsers",
        GroupIdentityType.Manager => "Manager",
        GroupIdentityType.ManagerAndSubordinates => "ManagerAndSubordinates",
        GroupIdentityType.Territory => "Territory",
        GroupIdentityType.TerritoryAndSubordinates => "TerritoryAndSubordinates",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}


// ── Data classes ─────────────────────────────────────────────────────────────

/// <summary>Represents a Salesforce user relevant to ACL resolution.</summary>
public class SfUser
{
    public string Id { get; set; }
    public string Name { get; set; } = "";
    public string Alias { get; set; } = "";
    public string Email { get; set; } = "";
    public string FederationIdentifier { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string UserName { get; set; } = "";
    public string UserRoleId { get; set; } = "";
    public string ParentRoleId { get; set; } = "";
    public string ManagerId { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public bool IsFrozen { get; set; } = false;
    public List<Dictionary<string, object?>> PermissionSets { get; set; } = new();

    public SfUser(string id)
    {
        Id = id;
    }

    /// <summary>Returns identity source properties for external identity mapping.</summary>
    public Dictionary<string, string> GetSourceProperties()
    {
        return new Dictionary<string, string>
        {
            ["Name"] = Name ?? "",
            ["Alias"] = Alias ?? "",
            ["Email"] = Email ?? "",
            ["FirstName"] = FirstName ?? "",
            ["LastName"] = LastName ?? "",
            ["FederationIdentifier"] = FederationIdentifier ?? "",
            ["UserName"] = UserName ?? "",
        };
    }
}


/// <summary>Represents a Salesforce Group record used during ACL resolution.</summary>
public class SfGroup
{
    public string Id { get; set; }
    public UserOrGroupType Type { get; set; }
    public string RelatedId { get; set; } = "";
    public bool DoesIncludeBosses { get; set; } = false;
    public List<string> GroupMembers { get; set; } = new();

    public SfGroup(string id, UserOrGroupType type)
    {
        Id = id;
        Type = type;
    }
}


/// <summary>A single share entry from an ``{ObjectName}Share`` table.</summary>
public class EntityShare
{
    public string UserOrGroupId { get; set; }
    public UserOrGroupType UserOrGroupType { get; set; }

    public EntityShare(string userOrGroupId, UserOrGroupType userOrGroupType)
    {
        UserOrGroupId = userOrGroupId;
        UserOrGroupType = userOrGroupType;
    }
}


/// <summary>Minimal representation of a Salesforce UserRole record.</summary>
public class UserRole
{
    public string Id { get; set; }
    public string ParentRoleId { get; set; } = "";

    public UserRole(string id, string parentRoleId = "")
    {
        Id = id;
        ParentRoleId = parentRoleId;
    }
}


// ── Visibility helpers ───────────────────────────────────────────────────────

public static class IdentityModels
{
    private static readonly HashSet<EntityVisibility> PublicVisibilities = new()
    {
        EntityVisibility.Read,
        EntityVisibility.Edit,
        EntityVisibility.ReadEditTransfer,
    };

    private static readonly HashSet<EntityVisibility> ControlledByParentVisibilities = new()
    {
        EntityVisibility.ControlledByParent,
        EntityVisibility.ControlledByLeadOrContact,
        EntityVisibility.ControlledByCampaign,
    };

    /// <summary>Return True when the OWD means the object is publicly readable.</summary>
    public static bool IsPublicVisibility(EntityVisibility visibility)
        => PublicVisibilities.Contains(visibility);

    /// <summary>Return True when the OWD means per-record ACLs are required.</summary>
    public static bool IsPrivateVisibility(EntityVisibility visibility)
        => visibility == EntityVisibility.None;

    /// <summary>Return True when the OWD means sharing is inherited from a parent.</summary>
    public static bool IsControlledByParent(EntityVisibility visibility)
        => ControlledByParentVisibilities.Contains(visibility);

    /// <summary>Parse a raw Salesforce OWD string into an ``EntityVisibility`` enum value.</summary>
    public static EntityVisibility ParseVisibility(string raw)
    {
        return raw switch
        {
            "Private" => EntityVisibility.None,
            "None" => EntityVisibility.None,
            "Read" => EntityVisibility.Read,
            "Edit" => EntityVisibility.Edit,
            "ReadEditTransfer" => EntityVisibility.ReadEditTransfer,
            "ControlledByParent" => EntityVisibility.ControlledByParent,
            "ControlledByCampaign" => EntityVisibility.ControlledByCampaign,
            "ControlledByLeadOrContact" => EntityVisibility.ControlledByLeadOrContact,
            _ => EntityVisibility.None,
        };
    }
}
