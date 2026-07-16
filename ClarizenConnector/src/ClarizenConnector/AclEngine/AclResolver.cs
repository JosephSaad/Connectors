// AclEngine/AclResolver.cs
// ------------------------
// Per-record ACL resolution for Clarizen work items.
//
// Modes (config/schema.json → aclMode per object):
//   public         — grant everyone (org-public data like Discussions).
//   ownerOnly      — grant only the record's owner/manager/creator users.
//   projectMembers — (default) grant the owner fields PLUS everyone with
//                    membership in the record's parent project (users and
//                    groups from RegularResourceLink / project team), the
//                    Clarizen model for work-item visibility.
//
// Owner-ish reference fields recognized on any record: CreatedBy, Owner,
// Manager, AssignedTo, ReportedBy. Principals are mapped to Entra identities
// via PrincipalMapper; super-user grants (CLARIZEN_ADMIN_GROUP_ID) and deny
// entries can be appended by the caller. Records that resolve to zero
// principals fall back to the FALLBACK_ACL_GROUP_ID grant when configured,
// otherwise remain empty (and the item is skipped by the pipeline, never
// ingested world-readable).

using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.AclEngine;

public sealed class AclResolver
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector.acl");

    /// <summary>Reference fields treated as record-level owner grants when present.</summary>
    internal static readonly string[] OwnerFields =
    {
        "CreatedBy", "Owner", "Manager", "AssignedTo", "ReportedBy",
    };

    private readonly PrincipalMapper _mapper;
    private readonly DirectorySnapshot _snapshot;
    private readonly string? _adminGroupId;
    private readonly string? _fallbackGroupId;

    public AclResolver(
        PrincipalMapper mapper,
        DirectorySnapshot snapshot,
        string? adminGroupId = null,
        string? fallbackGroupId = null)
    {
        _mapper = mapper;
        _snapshot = snapshot;
        _adminGroupId = adminGroupId
            ?? Environment.GetEnvironmentVariable("CLARIZEN_ADMIN_GROUP_ID");
        _fallbackGroupId = fallbackGroupId
            ?? Environment.GetEnvironmentVariable("FALLBACK_ACL_GROUP_ID");
    }

    /// <summary>Resolve the ACL entries for one record.</summary>
    public List<AclEntry> Resolve(ClarizenRecord record, ObjectConfig objectConfig)
    {
        var acl = new List<AclEntry>();

        if (string.Equals(objectConfig.AclMode, "public", StringComparison.OrdinalIgnoreCase))
        {
            acl.Add(new AclEntry(AclEntryType.EveryoneExceptGuests, "everyoneExceptGuests", AclAccessType.Grant));
            return acl;
        }

        var principals = CollectPrincipals(record, objectConfig);
        var (userIds, groupIds) = _mapper.MapPrincipals(principals, _snapshot);

        foreach (var userId in userIds)
            acl.Add(new AclEntry(AclEntryType.User, userId, AclAccessType.Grant));
        foreach (var groupId in groupIds)
            acl.Add(new AclEntry(AclEntryType.Group, groupId, AclAccessType.Grant));

        if (!string.IsNullOrEmpty(_adminGroupId)
            && !acl.Any(e => e.Type == AclEntryType.Group
                             && string.Equals(e.Value, _adminGroupId, StringComparison.OrdinalIgnoreCase)))
        {
            acl.Add(new AclEntry(AclEntryType.Group, _adminGroupId, AclAccessType.Grant));
        }

        if (acl.Count == 0 && !string.IsNullOrEmpty(_fallbackGroupId))
        {
            Logger.Debug(
                $"Record {record.ItemId}: no mappable principals; using FALLBACK_ACL_GROUP_ID grant.");
            acl.Add(new AclEntry(AclEntryType.Group, _fallbackGroupId, AclAccessType.Grant));
        }
        return acl;
    }

    /// <summary>Collect raw Clarizen principal ids for a record per its ACL mode. Testable.</summary>
    internal List<string> CollectPrincipals(ClarizenRecord record, ObjectConfig objectConfig)
    {
        var principals = new List<string>();

        void Add(string? principalId)
        {
            if (!string.IsNullOrEmpty(principalId)
                && !principals.Contains(principalId, StringComparer.OrdinalIgnoreCase))
            {
                principals.Add(principalId);
            }
        }

        // Owner-ish reference fields on the record itself.
        foreach (var field in OwnerFields)
            Add(record.GetReferenceId(field));

        if (string.Equals(objectConfig.AclMode, "ownerOnly", StringComparison.OrdinalIgnoreCase))
            return principals;

        // projectMembers: pull the parent project's membership.
        var projectId = string.IsNullOrEmpty(objectConfig.ProjectField)
            ? record.RawId  // the record IS the project
            : record.GetReferenceId(objectConfig.ProjectField);
        if (!string.IsNullOrEmpty(projectId))
        {
            foreach (var member in _snapshot.MembersOf(projectId))
                Add(member);
        }
        return principals;
    }
}
