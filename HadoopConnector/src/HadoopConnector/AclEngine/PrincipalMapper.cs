// AclEngine/PrincipalMapper.cs
// ----------------------------
// Maps BDH owner principals (Salesforce user ids or owner emails) to Entra ID
// object ids using the persisted identity store (populated by IdentitySync
// from the BDH User export + Graph email resolution).
//
// Unmappable principals are counted, never silently ignored — identity-dry-run
// surfaces them, and the pipeline's zero-ACL skip keeps such records out of
// the index (never world-readable by accident).

using HadoopConnector.Graph;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.AclEngine;

public sealed class PrincipalMapper
{
    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector.acl");

    private readonly IIdentityStore _store;
    private readonly Dictionary<string, string> _emailIndex;

    public int UnmappedOwners { get; private set; }

    public PrincipalMapper(IIdentityStore store)
    {
        _store = store;
        // Email → Entra id index so records carrying only OwnerEmail resolve
        // without a per-record store scan.
        _emailIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in store.All())
        {
            if (!string.IsNullOrWhiteSpace(mapping.Email) && !string.IsNullOrWhiteSpace(mapping.EntraId))
                _emailIndex[mapping.Email!] = mapping.EntraId!;
        }
    }

    /// <summary>Entra object id for a Salesforce user id, or null when unmapped.</summary>
    public string? MapOwnerId(string salesforceUserId)
    {
        var mapping = _store.Find(salesforceUserId);
        if (mapping?.EntraId is { Length: > 0 } entraId)
            return entraId;
        UnmappedOwners++;
        return null;
    }

    /// <summary>Entra object id for an owner email, or null when unmapped.</summary>
    public string? MapOwnerEmail(string email)
    {
        if (_emailIndex.TryGetValue(email, out var entraId))
            return entraId;
        UnmappedOwners++;
        return null;
    }

    /// <summary>
    /// Resolve an owner to an Entra id: the id mapping wins; the email index is
    /// the fallback. Null when neither resolves (counted once).
    /// </summary>
    public string? ResolveOwner(string? ownerId, string? ownerEmail)
    {
        if (!string.IsNullOrWhiteSpace(ownerId))
        {
            var mapping = _store.Find(ownerId!);
            if (mapping?.EntraId is { Length: > 0 } entraId)
                return entraId;
        }
        if (!string.IsNullOrWhiteSpace(ownerEmail)
            && _emailIndex.TryGetValue(ownerEmail!, out var byEmail))
        {
            return byEmail;
        }
        if (!string.IsNullOrWhiteSpace(ownerId) || !string.IsNullOrWhiteSpace(ownerEmail))
        {
            UnmappedOwners++;
            Logger.Debug($"Owner '{ownerId ?? ownerEmail}' has no Entra mapping.");
        }
        return null;
    }
}
