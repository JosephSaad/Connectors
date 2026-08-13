// AclEngine/IdentitySync.cs
// -------------------------
// Identity crawl: loads the Clarizen directory (users, groups + membership,
// project membership) and resolves Clarizen users to Entra ID identities by
// email/UPN through Microsoft Graph, persisting the mapping in the identity
// store. Runs before every full content crawl (and on incrementals when
// IDENTITY_SYNC_ON_INCREMENTAL=true). identity-dry-run executes the same
// resolution without persisting (unless --save).

using System.Text.Json.Nodes;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.AclEngine;

public sealed class IdentitySyncResult
{
    public int UsersTotal { get; set; }
    public int UsersResolved { get; set; }
    public int GroupsTotal { get; set; }
    public int ProjectsWithMembers { get; set; }
    public List<string> UnresolvedEmails { get; } = new();
}

public sealed class IdentitySync
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector.identity");

    private readonly ClarizenClient _clarizen;
    private readonly GraphClient _graph;

    public IdentitySync(ClarizenClient clarizen, GraphClient graph)
    {
        _clarizen = clarizen;
        _graph = graph;
    }

    // ── Directory snapshot ───────────────────────────────────────────────────

    /// <summary>Load users, groups (with membership) and project membership from Clarizen.</summary>
    public async Task<DirectorySnapshot> LoadDirectoryAsync(CancellationToken ct = default)
    {
        var snapshot = new DirectorySnapshot();

        foreach (var row in await _clarizen.QueryAllAsync(
                     "SELECT DisplayName, Email, SuperUser FROM User", ct: ct).ConfigureAwait(false))
        {
            var user = ParseUser(row);
            if (user is not null)
                snapshot.Users[user.Id] = user;
        }

        foreach (var row in await _clarizen.QueryAllAsync(
                     "SELECT Name FROM UserGroup", ct: ct).ConfigureAwait(false))
        {
            var id = GetId(row);
            if (id is null)
                continue;
            snapshot.Groups[id] = new ClarizenGroup(id, GetString(row, "Name"), new List<string>());
        }

        foreach (var row in await _clarizen.QueryAllAsync(
                     "SELECT UserGroup, Member FROM GroupMembership", ct: ct).ConfigureAwait(false))
        {
            var groupId = GetRefId(row, "UserGroup");
            var memberId = GetRefId(row, "Member");
            if (groupId is null || memberId is null)
                continue;
            if (snapshot.Groups.TryGetValue(groupId, out var group))
                group.MemberUserIds.Add(memberId);
        }

        // Project membership: resource links (team) — users or groups per project.
        foreach (var row in await _clarizen.QueryAllAsync(
                     "SELECT WorkItem, Resource FROM RegularResourceLink", ct: ct).ConfigureAwait(false))
        {
            var projectId = GetRefId(row, "WorkItem");
            var resourceId = GetRefId(row, "Resource");
            if (projectId is null || resourceId is null)
                continue;
            snapshot.AddProjectMember(projectId, resourceId);
        }

        Logger.Info(
            $"Directory snapshot: {snapshot.Users.Count} users, {snapshot.Groups.Count} groups, "
            + $"{snapshot.ProjectMembers.Count} projects with members.");
        return snapshot;
    }

    internal static ClarizenUser? ParseUser(JsonObject row)
    {
        var id = GetId(row);
        if (id is null)
            return null;
        var isSuper = row.TryGetPropertyValue("SuperUser", out var super)
            && super is not null
            && super.GetValueKind() == System.Text.Json.JsonValueKind.True;
        return new ClarizenUser(id, GetString(row, "Email"), GetString(row, "DisplayName"), isSuper);
    }

    // ── Entra resolution ─────────────────────────────────────────────────────

    /// <summary>
    /// Resolve every snapshot user to an Entra object id by email/UPN and
    /// persist mappings into <paramref name="store"/> (unless dry-run).
    /// Groups are persisted unresolved unless CLARIZEN_GROUP_MAPPING provides
    /// an Entra group id.
    /// </summary>
    public async Task<IdentitySyncResult> SyncAsync(
        DirectorySnapshot snapshot,
        IIdentityStore store,
        bool persist = true,
        CancellationToken ct = default)
    {
        var result = new IdentitySyncResult
        {
            UsersTotal = snapshot.Users.Count,
            GroupsTotal = snapshot.Groups.Count,
            ProjectsWithMembers = snapshot.ProjectMembers.Count,
        };

        foreach (var user in snapshot.Users.Values)
        {
            ct.ThrowIfCancellationRequested();
            string? entraId = null;
            if (!string.IsNullOrWhiteSpace(user.Email))
            {
                entraId = await ResolveUserByEmailAsync(user.Email, ct).ConfigureAwait(false);
            }
            if (entraId is null)
            {
                if (!string.IsNullOrWhiteSpace(user.Email))
                    result.UnresolvedEmails.Add(user.Email!);
            }
            else
            {
                result.UsersResolved++;
            }
            if (persist)
            {
                store.Upsert(new PrincipalMapping(
                    user.Id, "user", user.Email, entraId, DateTime.UtcNow));
            }
        }

        var groupOverrides = PrincipalMapper.ParseGroupMapping(
            Environment.GetEnvironmentVariable("CLARIZEN_GROUP_MAPPING"));
        foreach (var group in snapshot.Groups.Values)
        {
            groupOverrides.TryGetValue(group.Id, out var entraGroupId);
            if (persist)
            {
                store.Upsert(new PrincipalMapping(
                    group.Id, "group", null, entraGroupId, DateTime.UtcNow));
            }
        }

        Logger.Info(
            $"Identity sync: {result.UsersResolved}/{result.UsersTotal} users resolved to Entra; "
            + $"{result.UnresolvedEmails.Count} unresolved.");
        return result;
    }

    /// <summary>Graph lookup: GET /users/{email}?$select=id → object id, null on 404.</summary>
    internal async Task<string?> ResolveUserByEmailAsync(string email, CancellationToken ct)
    {
        try
        {
            var response = await _graph.GetAsync(
                $"users/{Uri.EscapeDataString(email)}?$select=id", ct).ConfigureAwait(false);
            if (response.IsSuccess)
                return response.Body?["id"]?.GetValue<string>();
            return null;
        }
        catch (Exception exc)
        {
            // Best-effort per-user lookup: the user is counted as unresolved and
            // the sync continues. Keep the exception type — a config/auth error
            // (token failure) looks very different from a transient socket error.
            Logger.Warning(
                $"Entra lookup failed for '{LogRedaction.MaskEmail(email)}' (user counted as unresolved): "
                + $"{exc.GetType().Name}: {exc.Message}");
            return null;
        }
    }

    // ── JSON row helpers ─────────────────────────────────────────────────────

    private static string? GetId(JsonObject row) =>
        row.TryGetPropertyValue("id", out var id) && id is not null ? id.GetValue<string>() : null;

    private static string? GetString(JsonObject row, string field) =>
        row.TryGetPropertyValue(field, out var node) && node is JsonValue value
        && value.TryGetValue<string>(out var s)
            ? s
            : null;

    private static string? GetRefId(JsonObject row, string field)
    {
        if (!row.TryGetPropertyValue(field, out var node) || node is null)
            return null;
        return node switch
        {
            JsonObject obj when obj.TryGetPropertyValue("id", out var id) && id is not null =>
                id.GetValue<string>(),
            JsonValue value when value.TryGetValue<string>(out var s) => s,
            _ => null,
        };
    }
}
