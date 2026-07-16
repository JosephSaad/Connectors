// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Graph/IdentityPublisher.cs
// ----------------------------
// Publishes Identity Crawl results to Microsoft Graph, using a SQLite-backed
// store to track state and minimize API calls.
//
// Architecture
// ------------
//
//     IdentityCrawlResult (from IdentitySync.cs)
//             │
//             ▼
//     IdentityPublisher.PublishAsync()
//             │
//             ├── 1. Convert crawl result → flat {group_id: (name, members)} dict
//             │
//             ├── 2. IdentityStore.ComputeDiff()
//             │      Compare new state vs stored state → List<GroupDiff>
//             │
//             ├── 3. Apply diffs (selective Graph API calls)
//             │      ├── create  → PUT group + POST each member
//             │      ├── update  → POST new members + DELETE removed members
//             │      ├── delete  → DELETE group
//             │      └── unchanged → skip (no API call)
//             │
//             ├── 4. Update SQLite store on success
//             │
//             └── 5. Record sync session stats
//
// Graph API endpoints used
// ------------------------
// ``PUT  /external/connections/{id}/groups/{groupId}``
//     Create or replace an external group.
//
// ``DELETE /external/connections/{id}/groups/{groupId}``
//     Delete an external group and all its members.
//
// ``POST /external/connections/{id}/groups/{groupId}/members``
//     Add a member (user or nested group) to an external group.
//
// ``DELETE /external/connections/{id}/groups/{groupId}/members/{memberId}``
//     Remove a single member from an external group.

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.AclEngine;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Graph;

// ── Publisher ─────────────────────────────────────────────────────────────────

/// <summary>
/// Publishes identity crawl results to Microsoft Graph with change-aware
/// optimization via SQLite state tracking.
///
/// Parameters
/// ----------
/// graphClient    : Authenticated <c>GraphClient</c> instance.
/// connectionId   : The external connection ID in Microsoft Graph.
/// store          : <c>IdentityStore</c> for state tracking.  If null, one is
///                  created automatically using <c>connectionId</c>.
/// principalMapper : <c>PrincipalMapper</c> for resolving Salesforce User IDs
///                   to AAD GUIDs / UPNs.  Required for user members to be
///                   accepted by the Graph external groups API.
/// </summary>
public class IdentityPublisher
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector.identity_publisher");

    private readonly GraphClient _client;
    private readonly string _connectionId;
    private readonly IIdentityStore _store;
    private readonly string _basePath;
    private readonly PrincipalMapper? _principalMapper;

    public IdentityPublisher(
        GraphClient graphClient,
        string connectionId,
        IIdentityStore? store = null,
        PrincipalMapper? principalMapper = null)
    {
        _client = graphClient;
        _connectionId = connectionId;
        _store = store ?? IdentityStore.CreateStore(connectionId);
        // External groups API endpoint
        _basePath = $"{GraphClient.ExternalConnectionsPath}/{Uri.EscapeDataString(connectionId)}";
        _principalMapper = principalMapper;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Publish an identity crawl result to Microsoft Graph.
    ///
    /// Compares the crawl result against the SQLite store, computes a minimal
    /// diff, and makes only the necessary Graph API calls.
    ///
    /// Returns <see cref="SyncSessionStats"/> with counts of what was done.
    /// </summary>
    public async Task<SyncSessionStats> PublishAsync(IdentityCrawlResult crawlResult)
    {
        var sessionId = _store.StartSession();
        var stats = new SyncSessionStats { SessionId = sessionId };

        try
        {
            // Step 1: Convert crawl result to flat map, resolving SF user IDs → AAD
            var newGroups = await FlattenCrawlResultAsync(crawlResult);

            Logger.Info(
                $"[IdentityPublisher] Crawl produced {newGroups.Count} group(s) with members");

            // Step 2: Compute diff against stored state
            var diffs = _store.ComputeDiff(newGroups);

            var changed = diffs.Where(d => d.HasChanges).ToList();
            var unchanged = diffs.Where(d => !d.HasChanges).ToList();
            stats.GroupsUnchanged = unchanged.Count;

            var totalApiCalls = changed.Sum(d => d.ApiCallsNeeded);
            Logger.Info(
                $"[IdentityPublisher] Diff: {diffs.Count(d => d.Action == "create")} create, "
                + $"{diffs.Count(d => d.Action == "update")} update, "
                + $"{diffs.Count(d => d.Action == "delete")} delete, "
                + $"{stats.GroupsUnchanged} unchanged "
                + $"(~{totalApiCalls} API calls needed)");

            // Step 3: Apply diffs in two passes
            // Pass 1: Create all new groups and delete stale ones (no members yet)
            // This ensures child groups exist before parents try to reference them.
            foreach (var diff in diffs)
            {
                if (diff.Action == "create")
                {
                    if (await CreateGroupOnlyAsync(diff.GroupId, diff.DisplayName))
                    {
                        stats.GroupsCreated += 1;
                        stats.ApiCallsMade += 1;
                    }
                    else
                    {
                        stats.Errors += 1;
                    }
                }
                else if (diff.Action == "delete")
                {
                    await ApplyDeleteAsync(diff, stats);
                }
            }

            // Pass 2: Add/remove members for created and updated groups
            foreach (var diff in diffs)
            {
                if (diff.Action == "create")
                {
                    var allMembers = newGroups[diff.GroupId].Members;
                    var added = await AddMembersAsync(diff.GroupId, diff.MembersToAdd);
                    stats.MembersAdded += added;
                    stats.ApiCallsMade += diff.MembersToAdd.Count;
                    if (added < diff.MembersToAdd.Count)
                        stats.Errors += diff.MembersToAdd.Count - added;
                    _store.UpsertGroup(diff.GroupId, diff.DisplayName);
                    _store.ReplaceMembers(diff.GroupId, allMembers);
                }
                else if (diff.Action == "update")
                {
                    await ApplyUpdateAsync(diff, stats, newGroups);
                }
                // "unchanged" and "delete" → skip in pass 2
            }

            // Step 4: Complete session
            _store.CompleteSession(sessionId, stats, status: "completed");

            Logger.Info(
                "[IdentityPublisher] Sync complete: "
                + $"created={stats.GroupsCreated} updated={stats.GroupsUpdated} "
                + $"deleted={stats.GroupsDeleted} unchanged={stats.GroupsUnchanged} "
                + $"members_added={stats.MembersAdded} members_removed={stats.MembersRemoved} "
                + $"api_calls={stats.ApiCallsMade} errors={stats.Errors}");
        }
        catch (Exception)
        {
            _store.CompleteSession(sessionId, stats, status: "failed");
            throw;
        }

        return stats;
    }

    // ── Crawl result conversion ───────────────────────────────────────────────

    /// <summary>
    /// Convert an <see cref="IdentityCrawlResult"/> into a flat dict suitable for
    /// diff computation, resolving Salesforce User IDs to AAD identifiers.
    ///
    /// Uses <c>PrincipalMapper</c> (when available) to resolve each SF user ID
    /// to an AAD GUID, UPN, or email.  The Graph external groups API requires
    /// <c>identitySource: azureActiveDirectory</c> for user members — raw
    /// Salesforce IDs (<c>005...</c>) are not accepted.
    ///
    /// Returns <c>{group_id: (display_name, {MemberEntry, ...})}</c>
    /// </summary>
    // internal (not private): the identity-dry-run command calls this directly,
    // mirroring Python's ``publisher._flatten_crawl_result_async(...)``.
    internal async Task<Dictionary<string, (string DisplayName, HashSet<MemberEntry> Members)>> FlattenCrawlResultAsync(
        IdentityCrawlResult crawlResult)
    {
        // If no PrincipalMapper, fall back to static method (dry-run / tests)
        if (_principalMapper is null)
            return FlattenCrawlResult(crawlResult);

        // Collect all unique SF user IDs across all groups
        var allUserIds = new HashSet<string>();
        foreach (var membership in crawlResult.GatheredGroups)
        {
            foreach (var user in membership.Users)
                allUserIds.Add(user.Id);
        }

        // Bulk-resolve SF user IDs → AAD identifiers via PrincipalMapper
        var resolvedMap = new Dictionary<string, string>();
        if (_principalMapper is not null && allUserIds.Count > 0)
        {
            // Pre-warm the mapper's cache with all user IDs at once
            await _principalMapper.PrewarmUsersAsync(allUserIds);

            // Resolve each user to an AAD identifier
            foreach (var uid in allUserIds)
            {
                if (_principalMapper.UserDetailsCache.TryGetValue(uid, out var details) && details is not null)
                {
                    var aadId = await _principalMapper.ResolvePrincipalAsync(details);
                    if (!string.IsNullOrEmpty(aadId))
                        resolvedMap[uid] = aadId;
                }
            }

            Logger.Info(
                $"[IdentityPublisher] Resolved {resolvedMap.Count}/{allUserIds.Count} SF user IDs to AAD identifiers");
        }

        // Build the flat group map
        var groups = new Dictionary<string, (string DisplayName, HashSet<MemberEntry> Members)>();

        foreach (var membership in crawlResult.GatheredGroups)
        {
            var members = new HashSet<MemberEntry>();

            foreach (var user in membership.Users)
            {
                if (resolvedMap.TryGetValue(user.Id, out var aadId)
                    && PrincipalMapper.LooksLikeGuid(aadId))
                {
                    // Resolved to AAD GUID — the only format Graph accepts for users
                    members.Add(new MemberEntry(
                        memberId: aadId,
                        memberType: "user",
                        identitySource: "azureActiveDirectory"));
                }
                else
                {
                    // Could not resolve to GUID — skip this user
                    // Graph rejects emails/UPNs with "InvalidGuidForAadMemberType"
                    Logger.Debug(
                        $"[IdentityPublisher] Skipping user {user.Id} — no AAD GUID resolved "
                        + $"(got: {(resolvedMap.TryGetValue(user.Id, out var got) ? got : "None")})");
                }
            }

            // Add child group members (always external identity source)
            foreach (var child in membership.ChildGroups)
            {
                members.Add(new MemberEntry(
                    memberId: child.GroupId,
                    memberType: "externalGroup",
                    identitySource: "external"));
            }

            groups[membership.GroupId] = (membership.DisplayName, members);
        }

        return groups;
    }

    /// <summary>
    /// Synchronous fallback for flattening without AAD resolution.
    /// Used by identity-dry-run (no Graph client available).
    ///
    /// Only emits user members whose identifier looks like a valid AAD GUID.
    /// Non-GUID identifiers (emails, UPNs, SF IDs) are skipped because
    /// the Graph external groups API rejects them.
    ///
    /// Returns <c>{group_id: (display_name, {MemberEntry, ...})}</c>
    /// </summary>
    public static Dictionary<string, (string DisplayName, HashSet<MemberEntry> Members)> FlattenCrawlResult(
        IdentityCrawlResult crawlResult)
    {
        var groups = new Dictionary<string, (string DisplayName, HashSet<MemberEntry> Members)>();

        foreach (var membership in crawlResult.GatheredGroups)
        {
            var members = new HashSet<MemberEntry>();

            // Add user members — only GUIDs are valid for Graph
            foreach (var user in membership.Users)
            {
                // Try federation_identifier, then email, then user_name
                var candidate = FirstNonEmpty(user.FederationIdentifier, user.Email, user.UserName);
                if (!string.IsNullOrEmpty(candidate) && PrincipalMapper.LooksLikeGuid(candidate))
                {
                    members.Add(new MemberEntry(
                        memberId: candidate,
                        memberType: "user",
                        identitySource: "azureActiveDirectory"));
                }
                // else: skip — non-GUID identifiers are rejected by Graph
            }

            // Add child group members
            foreach (var child in membership.ChildGroups)
            {
                members.Add(new MemberEntry(
                    memberId: child.GroupId,
                    memberType: "externalGroup",
                    identitySource: "external"));
            }

            groups[membership.GroupId] = (membership.DisplayName, members);
        }

        return groups;
    }

    // ── Diff application ──────────────────────────────────────────────────────

    /// <summary>Create a new group in Graph and add all its members.</summary>
    private async Task ApplyCreateAsync(
        GroupDiff diff,
        SyncSessionStats stats,
        Dictionary<string, (string DisplayName, HashSet<MemberEntry> Members)> newGroups)
    {
        var groupId = diff.GroupId;

        // PUT the group
        if (!await PutGroupAsync(groupId, diff.DisplayName))
        {
            stats.Errors += 1;
            return;
        }

        stats.ApiCallsMade += 1;
        stats.GroupsCreated += 1;

        // POST each member
        var allMembers = newGroups[groupId].Members;
        var added = await AddMembersAsync(groupId, diff.MembersToAdd);
        stats.MembersAdded += added;
        stats.ApiCallsMade += diff.MembersToAdd.Count;
        if (added < diff.MembersToAdd.Count)
            stats.Errors += diff.MembersToAdd.Count - added;

        // Update store: record group and all successfully-derived members
        _store.UpsertGroup(groupId, diff.DisplayName);
        _store.ReplaceMembers(groupId, allMembers);
    }

    /// <summary>Apply member additions and removals to an existing group.</summary>
    private async Task ApplyUpdateAsync(
        GroupDiff diff,
        SyncSessionStats stats,
        Dictionary<string, (string DisplayName, HashSet<MemberEntry> Members)> newGroups)
    {
        var groupId = diff.GroupId;

        // Add new members
        var added = await AddMembersAsync(groupId, diff.MembersToAdd);
        stats.MembersAdded += added;
        stats.ApiCallsMade += diff.MembersToAdd.Count;
        if (added < diff.MembersToAdd.Count)
            stats.Errors += diff.MembersToAdd.Count - added;

        // Remove stale members
        var removed = await RemoveMembersAsync(groupId, diff.MembersToRemove);
        stats.MembersRemoved += removed;
        stats.ApiCallsMade += diff.MembersToRemove.Count;
        if (removed < diff.MembersToRemove.Count)
            stats.Errors += diff.MembersToRemove.Count - removed;

        stats.GroupsUpdated += 1;

        // Update store with new complete membership
        var allMembers = newGroups[groupId].Members;
        _store.UpsertGroup(groupId, diff.DisplayName);
        _store.ReplaceMembers(groupId, allMembers);
    }

    /// <summary>Delete a group from Graph and remove it from the store.</summary>
    private async Task ApplyDeleteAsync(GroupDiff diff, SyncSessionStats stats)
    {
        var groupId = diff.GroupId;

        if (await DeleteGroupAsync(groupId))
        {
            stats.GroupsDeleted += 1;
            _store.DeleteGroup(groupId);
        }
        else
        {
            stats.Errors += 1;
        }

        stats.ApiCallsMade += 1;
    }

    // ── Graph API wrappers ────────────────────────────────────────────────────

    /// <summary>
    /// Create an external group (without adding members).
    ///
    /// ``POST /external/connections/{connId}/groups``
    ///
    /// Called in Pass 1 to ensure all groups exist before Pass 2 adds
    /// members (child group references require the target group to exist).
    /// </summary>
    private Task<bool> CreateGroupOnlyAsync(string groupId, string displayName)
    {
        return PutGroupAsync(groupId, displayName);
    }

    /// <summary>
    /// Create an external group.
    ///
    /// ``POST /external/connections/{connId}/groups``
    /// </summary>
    private async Task<bool> PutGroupAsync(string groupId, string displayName)
    {
        var url = $"{_basePath}/groups";
        var payload = new JsonObject
        {
            ["id"] = groupId,
            ["displayName"] = string.IsNullOrEmpty(displayName) ? groupId : displayName,
        };
        try
        {
            await _client.PostAsync(url, jsonBody: payload);
            Logger.Info($"[IdentityPublisher] POST group {groupId}");
            return true;
        }
        catch (GraphApiError e)
        {
            if (e.StatusCode == 409)
            {
                // Group already exists — treat as success
                Logger.Info($"[IdentityPublisher] Group {groupId} already exists (409)");
                return true;
            }
            Logger.Error($"[IdentityPublisher] POST group {groupId} failed [{e.StatusCode}]: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Delete an external group.
    ///
    /// ``DELETE /external/connections/{connId}/groups/{groupId}``
    /// </summary>
    private async Task<bool> DeleteGroupAsync(string groupId)
    {
        var url = $"{_basePath}/groups/{Uri.EscapeDataString(groupId)}";
        try
        {
            await _client.DeleteAsync(url);
            Logger.Info($"[IdentityPublisher] DELETE group {groupId}");
            return true;
        }
        catch (GraphApiError e)
        {
            if (e.StatusCode == 404)
            {
                Logger.Warning($"[IdentityPublisher] Group {groupId} already gone (404)");
                return true;  // Already deleted — treat as success
            }
            Logger.Error($"[IdentityPublisher] DELETE group {groupId} failed: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Add members to a group one at a time.
    ///
    /// ``POST /external/connections/{connId}/groups/{groupId}/members``
    ///
    /// Returns the count of successfully added members.
    /// </summary>
    private async Task<int> AddMembersAsync(string groupId, List<MemberEntry> members)
    {
        var url = $"{_basePath}/groups/{Uri.EscapeDataString(groupId)}/members";
        var success = 0;

        foreach (var member in members)
        {
            var payload = BuildMemberPayload(member);
            try
            {
                await _client.PostAsync(url, jsonBody: payload);
                success += 1;
            }
            catch (GraphApiError e)
            {
                if (e.StatusCode == 409)
                {
                    // Member already exists — treat as success
                    Logger.Debug(
                        $"[IdentityPublisher] Member {member.MemberId} already in {groupId} (409)");
                    success += 1;
                }
                else
                {
                    Logger.Error(
                        $"[IdentityPublisher] Add member {member.MemberId} to {groupId} failed: {e.Message}");
                }
            }
        }

        if (members.Count > 0)
        {
            Logger.Info(
                $"[IdentityPublisher] Added {success}/{members.Count} member(s) to {groupId}");
        }
        return success;
    }

    /// <summary>
    /// Remove members from a group one at a time.
    ///
    /// ``DELETE /external/connections/{connId}/groups/{groupId}/members/{memberId}``
    ///
    /// Returns the count of successfully removed members.
    /// </summary>
    private async Task<int> RemoveMembersAsync(string groupId, List<MemberEntry> members)
    {
        var success = 0;

        foreach (var member in members)
        {
            var url = $"{_basePath}/groups/{Uri.EscapeDataString(groupId)}"
                + $"/members/{Uri.EscapeDataString(member.MemberId)}";
            try
            {
                await _client.DeleteAsync(url);
                success += 1;
            }
            catch (GraphApiError e)
            {
                if (e.StatusCode == 404)
                {
                    Logger.Debug(
                        $"[IdentityPublisher] Member {member.MemberId} already removed from {groupId} (404)");
                    success += 1;
                }
                else
                {
                    Logger.Error(
                        $"[IdentityPublisher] Remove member {member.MemberId} from {groupId} failed: {e.Message}");
                }
            }
        }

        if (members.Count > 0)
        {
            Logger.Info(
                $"[IdentityPublisher] Removed {success}/{members.Count} member(s) from {groupId}");
        }
        return success;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Build the JSON payload for adding a member to an external group.
    ///
    /// The Microsoft Graph external groups API accepts these formats:
    ///
    /// AAD users (id MUST be an AAD Object GUID)::
    ///
    ///     {
    ///         "id": "&lt;aad-object-guid&gt;",
    ///         "type": "user",
    ///         "identitySource": "azureActiveDirectory"
    ///     }
    ///
    /// External groups (nested)::
    ///
    ///     {
    ///         "id": "&lt;group_id&gt;",
    ///         "type": "externalGroup",
    ///         "identitySource": "external"
    ///     }
    ///
    /// Note: ``@odata.type`` is NOT needed.  AAD users require the GUID — emails
    /// and UPNs are rejected with ``InvalidGuidForAadMemberType``.
    /// </summary>
    // internal (not private): the Python tests import module-level ``_build_member_payload``.
    internal static JsonObject BuildMemberPayload(MemberEntry member)
    {
        if (member.MemberType == "externalGroup")
        {
            return new JsonObject
            {
                ["id"] = member.MemberId,
                ["type"] = "externalGroup",
                ["identitySource"] = "external",
            };
        }
        return new JsonObject
        {
            ["id"] = member.MemberId,
            ["type"] = member.MemberType,
            ["identitySource"] = member.IdentitySource,
        };
    }

    /// <summary>Python-style <c>a or b or c</c> for strings: first non-empty value.</summary>
    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrEmpty(v))
                return v;
        }
        return "";
    }
}
