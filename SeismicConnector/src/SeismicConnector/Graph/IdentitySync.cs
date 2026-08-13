// Graph/IdentitySync.cs
// ---------------------
// Identity crawl: enumerates Seismic users and groups and maps them to Entra
// ID principals, persisting the mapping in the identity store.
//
// * Users → Entra users matched by email/UPN (GET /users/{email}); the
//   mapping is stored under BOTH the Seismic user id and the lowercased email
//   so ACL resolution can match either.
// * Groups → Entra groups matched by display name
//   (GET /groups?$filter=displayName eq '...'). Ambiguous or missing matches
//   are stored unmapped and reported.
//
// identity-dry-run runs this end-to-end and prints the mapping table without
// (or with, --save) persisting it.

using System.Text.Json.Nodes;
using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector.Graph;

public sealed record IdentitySyncStats(
    int UsersScanned, int UsersMapped, int GroupsScanned, int GroupsMapped)
{
    public int TotalScanned => UsersScanned + GroupsScanned;
    public int TotalMapped => UsersMapped + GroupsMapped;
}

public sealed class IdentitySync
{
    private static readonly IAppLogger Logger = Logging.GetLogger("seismic_connector.identity");
    private static readonly IAppLogger Progress = Logging.GetLogger("progress");

    private readonly SeismicClient _seismic;
    private readonly GraphClient _graph;
    private readonly IIdentityStore _store;

    public IdentitySync(SeismicClient seismic, GraphClient graph, IIdentityStore store)
    {
        _seismic = seismic;
        _graph = graph;
        _store = store;
    }

    /// <summary>
    /// Crawl Seismic users/groups and resolve Entra mappings.
    /// <paramref name="persist"/> false → dry run (nothing written).
    /// <paramref name="onMapping"/> receives every resolved row (for dry-run display).
    /// </summary>
    public async Task<IdentitySyncStats> RunAsync(
        bool persist = true,
        Action<PrincipalMapping>? onMapping = null,
        CancellationToken ct = default)
    {
        var usersScanned = 0;
        var usersMapped = 0;
        var groupsScanned = 0;
        var groupsMapped = 0;

        Progress.Info("Identity crawl: enumerating Seismic users...");
        foreach (var user in await _seismic.GetUsersAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            usersScanned++;
            var email = user.Email ?? user.Username;
            string? entraId = null;
            if (!string.IsNullOrWhiteSpace(email))
                entraId = await LookupUserByEmailAsync(email!, ct).ConfigureAwait(false);
            if (entraId is not null)
                usersMapped++;

            var mapping = new PrincipalMapping(user.Id, "user", email, entraId, user.FullName);
            onMapping?.Invoke(mapping);
            if (persist)
            {
                _store.UpsertPrincipal(mapping);
                if (!string.IsNullOrWhiteSpace(email))
                {
                    // Secondary row keyed by email so ACLs can match without the Seismic id.
                    _store.UpsertPrincipal(mapping with { SeismicId = email!.ToLowerInvariant() });
                }
            }
        }

        Progress.Info("Identity crawl: enumerating Seismic groups...");
        foreach (var group in await _seismic.GetGroupsAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            groupsScanned++;
            var entraId = await LookupGroupByNameAsync(group.Name, ct).ConfigureAwait(false);
            if (entraId is not null)
                groupsMapped++;

            var mapping = new PrincipalMapping(group.Id, "group", null, entraId, group.Name);
            onMapping?.Invoke(mapping);
            if (persist)
                _store.UpsertPrincipal(mapping);
        }

        var stats = new IdentitySyncStats(usersScanned, usersMapped, groupsScanned, groupsMapped);
        Progress.Info(
            $"Identity crawl complete: {stats.UsersMapped}/{stats.UsersScanned} users, "
            + $"{stats.GroupsMapped}/{stats.GroupsScanned} groups mapped to Entra ID.");
        return stats;
    }

    /// <summary>Entra user object id by email/UPN; null when not found.</summary>
    internal async Task<string?> LookupUserByEmailAsync(string email, CancellationToken ct)
    {
        try
        {
            var user = await _graph.GetAsync(
                $"/users/{Uri.EscapeDataString(email)}?$select=id", ct).ConfigureAwait(false);
            return user?["id"]?.GetValue<string>();
        }
        catch (GraphApiError ex) when (ex.StatusCode == 404)
        {
            Logger.Info($"No Entra user for '{LogRedaction.MaskEmail(email)}'");
            return null;
        }
        catch (GraphApiError ex) when (ex.StatusCode == 400)
        {
            // One malformed address (e.g. "DOMAIN\jdoe" stored as a Seismic
            // username) is a per-principal data problem: leave THAT principal
            // unmapped rather than abort the whole identity crawl mid-way.
            // Auth/permission errors (401/403) still fail fast.
            Logger.Warning(
                $"Entra user lookup for '{email}' rejected as a bad request ({ex.Message}) — "
                + "left unmapped; fix the Seismic email/username to map this principal.");
            return null;
        }
    }

    /// <summary>Entra group object id by display name; null when missing or ambiguous.</summary>
    internal async Task<string?> LookupGroupByNameAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var filter = Uri.EscapeDataString($"displayName eq '{name.Replace("'", "''")}'");
        JsonNode? result;
        try
        {
            result = await _graph.GetAsync($"/groups?$filter={filter}&$select=id", ct).ConfigureAwait(false);
        }
        catch (GraphApiError ex) when (ex.StatusCode == 400)
        {
            // A group name Graph's $filter grammar rejects: per-principal data
            // problem — leave it unmapped and continue the crawl (401/403 and
            // 5xx still propagate: those are auth/outage, not this group).
            Logger.Warning(
                $"Entra group lookup for '{name}' rejected as a bad request ({ex.Message}) — "
                + "left unmapped.");
            return null;
        }
        if (result?["value"] is not JsonArray matches)
            return null;
        if (matches.Count != 1)
        {
            if (matches.Count > 1)
                Logger.Warning($"Ambiguous Entra group name '{name}' ({matches.Count} matches) — left unmapped.");
            return null;
        }
        return matches[0]?["id"]?.GetValue<string>();
    }
}
