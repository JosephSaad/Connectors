// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// acl_engine/group_id_formats.py
// -------------------------------
// Canonical format strings for external group IDs used in both the Identity
// Crawl (group creation) and Content ACL (group references).
//
// **Critical**: The group IDs generated here MUST be used in both the identity
// crawl and the content ACL builder.  Any mismatch causes silent authorization
// failures at search time.
//
// **Constraint**: Microsoft Graph external group IDs must contain only ASCII
// alphanumeric characters (no hyphens, underscores, or special characters).
//
// Format placeholders:
//     {0} = object name  (e.g. "Account")
//     {1} = related ID   (e.g. role ID, group ID, user ID)

using System.Text.RegularExpressions;

namespace SalesforceCopilotConnector.AclEngine;

/// <summary>A format string whose <see cref="Format"/> strips non-alphanumeric characters.</summary>
public class SanitizedFormat
{
    private static readonly Regex NonAlnum = new("[^a-zA-Z0-9]", RegexOptions.Compiled);

    private readonly string _format;

    public SanitizedFormat(string format)
    {
        _format = format;
    }

    public string Format(params object?[] args)
    {
        var raw = string.Format(_format, args);
        return NonAlnum.Replace(raw, "");
    }

    public override string ToString() => _format;
}


/// <summary>
/// Format strings for external group IDs.
///
/// Every constant uses ``str.format()`` placeholders:
///     {0} = object name  (e.g. "Account")
///     {1} = related Salesforce ID  (e.g. role ID, group ID, user ID)
///
/// All IDs are alphanumeric only (Graph API requirement).
/// Non-alphanumeric characters in inputs are automatically stripped.
/// </summary>
public static class SfGroupIdFormats
{
    // Top-level group (one per object, used in PUBLIC OWD ACLs)
    public static readonly SanitizedFormat TopLevel = new("{0}TopLevel");
    // Example: "AccountTopLevel"

    // Global users with ViewAll/ModifyAll (PRIVATE OWD)
    public static readonly SanitizedFormat GlobalUsers = new("{0}GlobalUsers");
    // Example: "AccountGlobalUsers"

    // All internal users (Organization-type share)
    public static readonly SanitizedFormat AllInternalUsers = new("{0}AllInternalUsers");
    // Example: "AccountAllInternalUsers"

    // Role-based groups (with parent role nesting)
    public static readonly SanitizedFormat Role = new("{0}{1}Role");
    // Example: "Account00E5g000001ABCRole"

    public static readonly SanitizedFormat RoleAndSubordinates = new("{0}{1}RoleAndSubordinates");
    // Example: "Account00E5g000001ABCRoleAndSubordinates"

    // Role groups WITHOUT parent nesting (used as child of RoleAndSub)
    public static readonly SanitizedFormat RoleNoParents = new("{0}{1}RoleNoParents");
    public static readonly SanitizedFormat RoleAndSubordinatesNoParents = new("{0}{1}RoleAndSubordinatesNoParents");

    // Public/Queue groups
    public static readonly SanitizedFormat PublicGroup = new("{0}{1}PublicGroup");
    // Example: "Account00G5g000001XYZPublicGroup"

    // Manager groups
    public static readonly SanitizedFormat Manager = new("{0}{1}Manager");
    // Example: "Account0055g000001DEFManager"

    public static readonly SanitizedFormat ManagerAndSubordinates = new("{0}{1}ManagerAndSubordinates");
    // Example: "Account0055g000001DEFManagerAndSubordinates"

    // Territory groups
    public static readonly SanitizedFormat Territory = new("{0}{1}Territory");
    // Example: "Account0ML5g000000ABCTerritory"

    public static readonly SanitizedFormat TerritoryAndSubordinates = new("{0}{1}TerritoryAndSubordinates");
    // Example: "Account0ML5g000000ABCTerritoryAndSubordinates"
}
