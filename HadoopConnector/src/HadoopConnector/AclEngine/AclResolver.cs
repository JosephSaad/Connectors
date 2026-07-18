// AclEngine/AclResolver.cs
// ------------------------
// Per-record ACL resolution for BDH records.
//
// BDH mirrors Salesforce DATA but not the Salesforce sharing tables, so full
// sharing-model resolution (role hierarchy, sharing rules, teams) is
// IMPOSSIBLE here — that's part of the cheap-path trade-off (README). Modes
// (config/schema.json → aclMode per object):
//
//   ownerOnly — (default) grant only the record's owner: OwnerId (ownerField)
//               resolved through the identity store, with the owner-email
//               field (ownerEmailField, default OwnerEmail) as fallback.
//   group     — grant a fixed Entra group (aclGroupId per object), e.g.
//               "all-sales" for org-visible objects.
//   public    — grant everyoneExceptGuests. Explicit operator opt-in.
//
// Zero resolved principals → the pipeline SKIPS the item (never ingested
// world-readable); FALLBACK_ACL_GROUP_ID supplies a default grant when set.
// BDH_ADMIN_GROUP_ID appends an operator/admin grant to every non-public ACL.
// The fallback never WIDENS access: it applies only when nothing resolved,
// not on top of resolved grants.

using HadoopConnector.Config;
using HadoopConnector.Graph;
using HadoopConnector.Hdfs;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.AclEngine;

public sealed class AclResolver
{
    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector.acl");

    private readonly PrincipalMapper _mapper;
    private readonly string? _adminGroupId;
    private readonly string? _fallbackGroupId;

    public AclResolver(
        PrincipalMapper mapper,
        string? adminGroupId = null,
        string? fallbackGroupId = null)
    {
        _mapper = mapper;
        _adminGroupId = adminGroupId
            ?? Environment.GetEnvironmentVariable("BDH_ADMIN_GROUP_ID");
        _fallbackGroupId = fallbackGroupId
            ?? Environment.GetEnvironmentVariable("FALLBACK_ACL_GROUP_ID");
    }

    /// <summary>Resolve the ACL entries for one record. Empty ⇒ caller skips the item.</summary>
    public List<AclEntry> Resolve(BdhRecord record, ObjectConfig objectConfig)
    {
        var acl = new List<AclEntry>();

        switch (objectConfig.AclMode.ToLowerInvariant())
        {
            case "public":
                acl.Add(new AclEntry(
                    AclEntryType.EveryoneExceptGuests, "everyoneExceptGuests", AclAccessType.Grant));
                return acl;

            case "group":
                if (string.IsNullOrWhiteSpace(objectConfig.AclGroupId))
                {
                    // Schema validation rejects this up front; belt-and-braces:
                    // never fall open to a wider grant at runtime.
                    Logger.Error(
                        $"{objectConfig.ObjectName}: aclMode=group but no aclGroupId configured — "
                        + "record skipped (no ACL).");
                    return acl;
                }
                acl.Add(new AclEntry(AclEntryType.Group, objectConfig.AclGroupId, AclAccessType.Grant));
                break;

            default:  // ownerOnly
                var ownerId = record.GetString(objectConfig.EffectiveOwnerField);
                var ownerEmail = record.GetString(objectConfig.EffectiveOwnerEmailField);
                var entraId = _mapper.ResolveOwner(ownerId, ownerEmail);
                if (entraId is not null)
                    acl.Add(new AclEntry(AclEntryType.User, entraId, AclAccessType.Grant));
                break;
        }

        if (!string.IsNullOrEmpty(_adminGroupId)
            && !acl.Any(e => e.Type == AclEntryType.Group
                             && string.Equals(e.Value, _adminGroupId, StringComparison.OrdinalIgnoreCase)))
        {
            // Admin grant only ever accompanies a resolved ACL (or fallback below);
            // it is appended after the zero-principal check for ownerOnly so an
            // unmapped owner still results in a skip unless a fallback exists.
            if (acl.Count > 0)
                acl.Add(new AclEntry(AclEntryType.Group, _adminGroupId, AclAccessType.Grant));
        }

        if (acl.Count == 0 && !string.IsNullOrEmpty(_fallbackGroupId))
        {
            Logger.Debug(
                $"Record {record.ItemId}: no mappable principals; using FALLBACK_ACL_GROUP_ID grant.");
            acl.Add(new AclEntry(AclEntryType.Group, _fallbackGroupId, AclAccessType.Grant));
            if (!string.IsNullOrEmpty(_adminGroupId)
                && !string.Equals(_adminGroupId, _fallbackGroupId, StringComparison.OrdinalIgnoreCase))
            {
                acl.Add(new AclEntry(AclEntryType.Group, _adminGroupId, AclAccessType.Grant));
            }
        }
        return acl;
    }
}
