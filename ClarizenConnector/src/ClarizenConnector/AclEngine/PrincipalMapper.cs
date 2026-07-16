// AclEngine/PrincipalMapper.cs
// ----------------------------
// Maps Clarizen principals (users/groups) to Entra ID identities using the
// persisted identity store.
//
//   Users  — matched by email/UPN during the identity sync; the resolved Entra
//            object id is read from the store.
//   Groups — matched via the optional CLARIZEN_GROUP_MAPPING JSON env var
//            ({"/Group/12": "<entra-group-guid>"}) or a store entry; groups
//            with no Entra mapping are EXPANDED to their member users so no
//            access is silently dropped.
//
// Unmappable principals are counted and logged, never silently ignored in
// stats (identity-dry-run surfaces them).

using System.Text.Json;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.AclEngine;

public sealed class PrincipalMapper
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector.acl");

    private readonly IIdentityStore _store;
    private readonly Dictionary<string, string> _groupOverrides;

    public int UnmappedUsers { get; private set; }
    public int UnmappedGroups { get; private set; }

    public PrincipalMapper(IIdentityStore store, string? groupMappingJson = null)
    {
        _store = store;
        _groupOverrides = ParseGroupMapping(
            groupMappingJson ?? Environment.GetEnvironmentVariable("CLARIZEN_GROUP_MAPPING"));
    }

    /// <summary>Parse the CLARIZEN_GROUP_MAPPING JSON object; invalid JSON → empty map (logged).</summary>
    internal static Dictionary<string, string> ParseGroupMapping(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return parsed is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            Logger.Warning("CLARIZEN_GROUP_MAPPING is not valid JSON; ignoring.");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Entra object id for a Clarizen user id, or null when unmapped.</summary>
    public string? MapUser(string clarizenUserId)
    {
        var mapping = _store.Find(clarizenUserId);
        if (mapping?.EntraId is { Length: > 0 } entraId)
            return entraId;
        UnmappedUsers++;
        return null;
    }

    /// <summary>Entra group id for a Clarizen group id (override wins over store), or null.</summary>
    public string? MapGroup(string clarizenGroupId)
    {
        if (_groupOverrides.TryGetValue(clarizenGroupId, out var overrideId))
            return overrideId;
        var mapping = _store.Find(clarizenGroupId);
        if (mapping?.EntraId is { Length: > 0 } entraId)
            return entraId;
        UnmappedGroups++;
        return null;
    }

    /// <summary>
    /// Map a mixed list of Clarizen principal ids to Entra grant targets:
    /// (userIds, groupIds). Groups with no Entra mapping are expanded to their
    /// member users via <paramref name="snapshot"/>.
    /// </summary>
    public (List<string> UserIds, List<string> GroupIds) MapPrincipals(
        IEnumerable<string> principalIds, DirectorySnapshot snapshot)
    {
        var users = new List<string>();
        var groups = new List<string>();
        foreach (var principalId in principalIds)
        {
            if (snapshot.Groups.TryGetValue(principalId, out var group))
            {
                var entraGroup = MapGroup(principalId);
                if (entraGroup is not null)
                {
                    AddUnique(groups, entraGroup);
                    continue;
                }
                // No Entra group — expand to member users so access is preserved.
                foreach (var memberId in group.MemberUserIds)
                {
                    var entraUser = MapUser(memberId);
                    if (entraUser is not null)
                        AddUnique(users, entraUser);
                }
                continue;
            }

            var mapped = MapUser(principalId);
            if (mapped is not null)
                AddUnique(users, mapped);
        }
        return (users, groups);
    }

    private static void AddUnique(List<string> list, string value)
    {
        if (!list.Contains(value, StringComparer.OrdinalIgnoreCase))
            list.Add(value);
    }
}
