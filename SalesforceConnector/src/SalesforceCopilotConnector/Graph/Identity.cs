// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Graph/Identity.cs
// -----------------
// Top-level orchestrator for the Identity Crawl + Publish pipeline.
//
// Provides a single RunIdentitySyncAsync() function that:
//
// 1. Queries Salesforce for group membership (via IdentitySyncHandler).
// 2. Diffs against the SQLite store (via IdentityStore).
// 3. Publishes only the changes to Microsoft Graph (via IdentityPublisher).
//
// Called by Commands/Deploy.cs and Commands/Ingest.cs when
// USE_GROUP_ACL=true.

using System.Globalization;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.AclEngine;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Graph;

public static class Identity
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector");

    /// <summary>
    /// Execute a full identity crawl and publish changes to Microsoft Graph.
    ///
    /// Steps
    /// -----
    /// 1. Create a SalesforceClient for identity queries.
    /// 2. Run IdentitySyncHandler.RunFullCrawlAsync() to query all group
    ///    memberships from Salesforce.
    /// 3. Compare against the SQLite store and publish only the diff to Graph.
    ///
    /// Parameters
    /// ----------
    /// config      : Fully loaded AppConfig.
    /// graphClient : Authenticated GraphClient.
    ///
    /// Returns
    /// -------
    /// SyncSessionStats with counts of groups created/updated/deleted and
    /// API calls made.
    /// </summary>
    public static async Task<SyncSessionStats> RunIdentitySyncAsync(AppConfig config, GraphClient graphClient)
    {
        var progress = Logging.GetLogger("progress");

        // 1. Build Salesforce client
        var sfClient = new SalesforceClient(
            instanceUrl: config.Connector.Salesforce.InstanceUrl,
            apiVersion: config.Connector.Salesforce.ApiVersion,
            accessToken: await ApiClient.GetSalesforceAccessTokenAsync(config),
            tokenRefresher: () => ApiClient.GetSalesforceAccessTokenAsync(config).GetAwaiter().GetResult());

        // 2. Determine object names from config
        var objectNames = new List<string>();
        if (config.SchemaConfig["objectList"] is JsonArray objectList)
        {
            foreach (var obj in objectList)
            {
                var objectName = obj?["objectName"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(objectName))
                {
                    objectNames.Add(objectName);
                }
            }
        }
        Logger.Info($"[IdentitySync] Objects to crawl: [{string.Join(", ", objectNames.Select(n => $"'{n}'"))}]");

        // 3. Run identity crawl
        progress.Info($"  Running identity crawl for {objectNames.Count} object type(s)...");
        var handler = new IdentitySyncHandler(
            sfClient: sfClient,
            objectNames: objectNames,
            parentMap: config.ParentMap,
            owdOverrides: config.OwdOverrides,
            owdFieldMap: config.OwdFieldMap,
            useEntityDefinitionOwd: config.UseEntityDefinitionOwd);
        var crawlResult = await handler.RunFullCrawlAsync();

        Logger.Info(
            $"[IdentitySync] Crawl complete: {crawlResult.TotalGroupsEmitted} group(s), {crawlResult.TotalUsersEmitted} user membership(s)");
        progress.Info(
            $"  Identity crawl: {crawlResult.TotalGroupsEmitted} groups, {crawlResult.TotalUsersEmitted} memberships");

        // 4. Publish to Graph (diff-based, with AAD resolution)
        progress.Info("  Publishing identity changes to Graph...");
        var mapper = new PrincipalMapper(
            sfClient: sfClient,
            graphClient: graphClient,
            tenantId: config.TenantId,
            batchSize: config.Tuning.SalesforceBatchSize);
        SyncSessionStats stats;
        using (var store = IdentityStore.CreateStore(config.Connector.Id))
        {
            var publisher = new IdentityPublisher(
                graphClient: graphClient,
                connectionId: config.Connector.Id,
                store: store,
                principalMapper: mapper);
            stats = await publisher.PublishAsync(crawlResult);
        }

        progress.Info(
            $"  Identity sync: created={stats.GroupsCreated} updated={stats.GroupsUpdated} deleted={stats.GroupsDeleted} unchanged={stats.GroupsUnchanged} (API calls={stats.ApiCallsMade})");

        return stats;
    }

    /// <summary>
    /// Execute an identity crawl and publish changes to Microsoft Graph, in
    /// either full or incremental mode (#12).
    ///
    /// When <paramref name="incremental"/> is <c>false</c> (the default) this is
    /// exactly the full crawl above — it delegates to the two-argument overload,
    /// so the full path stays byte-identical to today.
    ///
    /// When <paramref name="incremental"/> is <c>true</c> it reads the identity
    /// store's last successful identity-session watermark and gathers only the
    /// groups/memberships whose Salesforce data changed since then, publishing
    /// just that diff.  If no prior successful identity session exists there is
    /// no watermark to crawl from, so it logs that and falls back to a full
    /// crawl.  On success the session/watermark is recorded exactly as the full
    /// path does (via <c>IdentityPublisher.PublishAsync</c>).
    ///
    /// Gating this on incremental crawls is the orchestrator's job (it passes
    /// <c>incremental: true</c> when <c>IDENTITY_SYNC_ON_INCREMENTAL</c> is set);
    /// this method never reads that env var.
    /// </summary>
    public static async Task<SyncSessionStats> RunIdentitySyncAsync(
        AppConfig config, GraphClient graphClient, bool incremental)
    {
        if (!incremental)
        {
            // Default path — byte-identical to the existing full crawl.
            return await RunIdentitySyncAsync(config, graphClient);
        }

        var progress = Logging.GetLogger("progress");

        // Read the watermark: the start time of the last completed identity
        // session. No prior session → nothing to crawl incrementally from → full.
        DateTime? since;
        using (var wmStore = IdentityStore.CreateStore(config.Connector.Id))
        {
            since = TryGetIdentityWatermark(wmStore, out var wm) ? wm : null;
        }

        if (since is null)
        {
            Logger.Info(
                "[IdentitySync] Incremental requested but no prior successful identity session found — falling back to full crawl");
            progress.Info("  No prior identity session — running full identity crawl");
            return await RunIdentitySyncAsync(config, graphClient);
        }

        Logger.Info($"[IdentitySync] Incremental identity crawl since {ToIsoFormat(since.Value)}");
        progress.Info($"  Running incremental identity crawl (since {ToIsoFormat(since.Value)})...");

        // 1. Build Salesforce client (identical to the full path).
        var sfClient = new SalesforceClient(
            instanceUrl: config.Connector.Salesforce.InstanceUrl,
            apiVersion: config.Connector.Salesforce.ApiVersion,
            accessToken: await ApiClient.GetSalesforceAccessTokenAsync(config),
            tokenRefresher: () => ApiClient.GetSalesforceAccessTokenAsync(config).GetAwaiter().GetResult());

        // 2. Determine object names from config (identical to the full path).
        var objectNames = new List<string>();
        if (config.SchemaConfig["objectList"] is JsonArray objectList)
        {
            foreach (var obj in objectList)
            {
                var objectName = obj?["objectName"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(objectName))
                {
                    objectNames.Add(objectName);
                }
            }
        }
        Logger.Info($"[IdentitySync] Objects to crawl: [{string.Join(", ", objectNames.Select(n => $"'{n}'"))}]");

        // 3. Run the incremental identity crawl (only changed objects gathered).
        var handler = new IdentitySyncHandler(
            sfClient: sfClient,
            objectNames: objectNames,
            parentMap: config.ParentMap,
            owdOverrides: config.OwdOverrides,
            owdFieldMap: config.OwdFieldMap,
            useEntityDefinitionOwd: config.UseEntityDefinitionOwd);
        var crawlResult = await handler.RunIncrementalCrawlAsync(since.Value);

        Logger.Info(
            $"[IdentitySync] Incremental crawl complete: {crawlResult.TotalGroupsEmitted} group(s), {crawlResult.TotalUsersEmitted} user membership(s)");
        progress.Info(
            $"  Identity crawl: {crawlResult.TotalGroupsEmitted} groups, {crawlResult.TotalUsersEmitted} memberships (changed objects only)");

        // 4. Publish to Graph (diff-based, with AAD resolution).
        //    The diff is scoped to the objects that were actually re-gathered so
        //    that groups belonging to unchanged objects are left untouched rather
        //    than deleted (ComputeDiff would otherwise delete any stored group not
        //    present in the crawl result).
        progress.Info("  Publishing identity changes to Graph...");
        var mapper = new PrincipalMapper(
            sfClient: sfClient,
            graphClient: graphClient,
            tenantId: config.TenantId,
            batchSize: config.Tuning.SalesforceBatchSize);
        var changedObjects = crawlResult.TopLevelGroups.Select(t => t.ObjectName).ToHashSet();
        SyncSessionStats stats;
        using (var store = IdentityStore.CreateStore(config.Connector.Id))
        {
            var scopedStore = new ObjectScopedIdentityStore(store, changedObjects);
            var publisher = new IdentityPublisher(
                graphClient: graphClient,
                connectionId: config.Connector.Id,
                store: scopedStore,
                principalMapper: mapper);
            stats = await publisher.PublishAsync(crawlResult);
        }
        stats.SyncType = "incremental";

        progress.Info(
            $"  Identity sync: created={stats.GroupsCreated} updated={stats.GroupsUpdated} deleted={stats.GroupsDeleted} unchanged={stats.GroupsUnchanged} (API calls={stats.ApiCallsMade})");

        return stats;
    }

    /// <summary>
    /// Record content crawl stats in the SQLite sync_sessions table.
    ///
    /// Called after IngestContentAsync() completes so that content crawl history
    /// is tracked alongside identity crawl history in one DB.
    ///
    /// Parameters
    /// ----------
    /// config         : Fully loaded AppConfig.
    /// ingestionStats : IngestionStats from Graph.Ingest.IngestContentAsync().
    /// syncType       : "full" or "incremental".
    /// </summary>
    public static void RecordContentCrawl(AppConfig config, IngestionStats ingestionStats, string syncType = "full")
    {
        SyncSessionStats stats;
        using (var store = IdentityStore.CreateStore(config.Connector.Id))
        {
            var sessionId = store.StartSession(crawlType: "content", syncType: syncType);
            stats = new SyncSessionStats
            {
                SessionId = sessionId,
                SyncType = syncType,
                ContentTotalFetched = ingestionStats.TotalFetched,
                ContentSuccess = ingestionStats.SuccessCount,
                ContentFailed = ingestionStats.FailedCount,
                ContentDeleted = ingestionStats.DeletedCount,
                ContentAclEngine = ingestionStats.AclEngine,
                Errors = ingestionStats.FailedCount,
            };
            store.CompleteSession(sessionId, stats);
        }
        Logger.Info(
            $"[ContentCrawl] Session recorded (sync_type={syncType}): fetched={stats.ContentTotalFetched} success={stats.ContentSuccess} failed={stats.ContentFailed} deleted={stats.ContentDeleted} acl={stats.ContentAclEngine}");
    }

    /// <summary>
    /// Return the start timestamp of the last successful content crawl.
    ///
    /// Used by the incremental content crawl to determine the "since"
    /// parameter for IngestContentAsync() — only Salesforce records modified
    /// after this timestamp are fetched.
    ///
    /// Returns
    /// -------
    /// DateTime (UTC) or null if no previous content crawl exists.
    /// </summary>
    public static DateTime? GetLastContentCrawlTime(AppConfig config)
    {
        DateTime? result;
        using (var store = IdentityStore.CreateStore(config.Connector.Id))
        {
            result = store.GetLastSuccessfulContentCrawlTime();
        }
        if (result != null)
        {
            Logger.Info($"[ContentCrawl] Last successful crawl: {ToIsoFormat(result.Value)}");
        }
        else
        {
            Logger.Info("[ContentCrawl] No previous content crawl found — will do full sync");
        }
        return result;
    }

    /// <summary>Mirror of Python datetime.isoformat() for a UTC-aware datetime.</summary>
    private static string ToIsoFormat(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        var formatted = utc.Millisecond == 0 && utc.Ticks % TimeSpan.TicksPerSecond == 0
            ? utc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
            : utc.ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        return formatted + "+00:00";
    }

    /// <summary>
    /// Read the incremental-identity watermark from <paramref name="store"/>: the
    /// <c>started_at</c> of the last completed identity session.  Returns
    /// <c>false</c> when there is no prior successful identity session (the caller
    /// then falls back to a full crawl).
    ///
    /// Internal so the fallback decision is unit-testable without Salesforce auth.
    /// </summary>
    internal static bool TryGetIdentityWatermark(IIdentityStore store, out DateTime since)
    {
        var lastSession = store.GetLastSession(crawlType: "identity");
        if (lastSession is not null
            && lastSession.TryGetValue("started_at", out var startedAtObj)
            && startedAtObj is string startedAt
            && !string.IsNullOrEmpty(startedAt)
            && TryParseIsoUtc(startedAt, out var parsed))
        {
            since = parsed;
            return true;
        }
        since = default;
        return false;
    }

    /// <summary>
    /// Parse a stored session <c>started_at</c> timestamp (Python-isoformat, e.g.
    /// <c>2026-07-02T10:15:00+00:00</c>) into a UTC <see cref="DateTime"/>.
    /// </summary>
    private static bool TryParseIsoUtc(string value, out DateTime result)
    {
        if (DateTimeOffset.TryParse(
                value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed))
        {
            result = parsed.UtcDateTime;
            return true;
        }
        result = default;
        return false;
    }
}

/// <summary>
/// An <see cref="IIdentityStore"/> decorator that restricts the *diff surface*
/// to a fixed set of Salesforce objects (#12 incremental identity).
///
/// The incremental crawl re-gathers only the objects that changed since the
/// watermark, so the crawl result contains just those objects' groups.  The
/// publisher's <see cref="IIdentityStore.ComputeDiff"/> deletes any stored group
/// absent from the crawl result — which, against the real store, would delete
/// every group belonging to an *unchanged* object.  This wrapper makes
/// <see cref="GetAllGroupIds"/> / <see cref="ComputeDiff"/> consider only the
/// changed objects' groups, so unchanged-object groups are invisible to the diff
/// and therefore left untouched.  Every other operation (reads, writes, session
/// tracking) passes straight through to the inner store.
///
/// Object membership is decided by group-ID prefix: every group emitted for an
/// object begins with <see cref="IdentitySyncHandler.SanitizedObjectPrefix"/>.
/// A group is in scope when it starts with the sanitised prefix of one of the
/// changed objects; ties are broken by the longest matching prefix so that,
/// e.g., an <c>AccountPlan…</c> group is never mistaken for an <c>Account</c>
/// group.
/// </summary>
internal sealed class ObjectScopedIdentityStore : IIdentityStore
{
    private readonly IIdentityStore _inner;
    // (objectName, sanitizedPrefix) for the changed objects, longest prefix first.
    private readonly List<(string ObjectName, string Prefix)> _scope;

    public ObjectScopedIdentityStore(IIdentityStore inner, IEnumerable<string> changedObjectNames)
    {
        _inner = inner;
        _scope = changedObjectNames
            .Select(name => (ObjectName: name, Prefix: IdentitySyncHandler.SanitizedObjectPrefix(name)))
            .Where(t => t.Prefix.Length > 0)
            .OrderByDescending(t => t.Prefix.Length)
            .ToList();
    }

    /// <summary>True when <paramref name="groupId"/> belongs to a changed object.</summary>
    private bool InScope(string groupId)
    {
        foreach (var (_, prefix) in _scope)
        {
            if (groupId.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // ── Scoped surface (only these two differ from the inner store) ────────────

    /// <summary>Only the changed objects' tracked group IDs.</summary>
    public HashSet<string> GetAllGroupIds()
        => _inner.GetAllGroupIds().Where(InScope).ToHashSet();

    /// <summary>
    /// Diff the desired state against the stored state, but only over groups
    /// belonging to the changed objects.  Mirrors
    /// <see cref="IdentityStore.ComputeDiff"/> exactly, substituting the scoped
    /// <see cref="GetAllGroupIds"/> so unchanged-object groups never surface as
    /// deletions.
    /// </summary>
    public List<GroupDiff> ComputeDiff(Dictionary<string, (string DisplayName, HashSet<MemberEntry> Members)> newGroups)
    {
        var diffs = new List<GroupDiff>();
        var existingIds = GetAllGroupIds();
        var newIds = new HashSet<string>(newGroups.Keys);

        foreach (var (groupId, (displayName, desiredMembers)) in newGroups)
        {
            if (!existingIds.Contains(groupId))
            {
                diffs.Add(new GroupDiff
                {
                    GroupId = groupId,
                    Action = "create",
                    DisplayName = displayName,
                    MembersToAdd = desiredMembers.ToList(),
                    MembersToRemove = new List<MemberEntry>(),
                });
            }
            else
            {
                var currentMembers = _inner.GetMembers(groupId);
                var toAdd = new HashSet<MemberEntry>(desiredMembers);
                toAdd.ExceptWith(currentMembers);
                var toRemove = new HashSet<MemberEntry>(currentMembers);
                toRemove.ExceptWith(desiredMembers);

                if (toAdd.Count > 0 || toRemove.Count > 0)
                {
                    diffs.Add(new GroupDiff
                    {
                        GroupId = groupId,
                        Action = "update",
                        DisplayName = displayName,
                        MembersToAdd = toAdd.ToList(),
                        MembersToRemove = toRemove.ToList(),
                    });
                }
                else
                {
                    diffs.Add(new GroupDiff
                    {
                        GroupId = groupId,
                        Action = "unchanged",
                        DisplayName = displayName,
                    });
                }
            }
        }

        foreach (var staleId in existingIds.Where(id => !newIds.Contains(id)))
        {
            diffs.Add(new GroupDiff
            {
                GroupId = staleId,
                Action = "delete",
            });
        }

        return diffs;
    }

    // ── Straight pass-through ──────────────────────────────────────────────────

    public bool GroupExists(string groupId) => _inner.GroupExists(groupId);
    public void UpsertGroup(string groupId, string displayName = "", string description = "")
        => _inner.UpsertGroup(groupId, displayName, description);
    public void DeleteGroup(string groupId) => _inner.DeleteGroup(groupId);
    public HashSet<MemberEntry> GetMembers(string groupId) => _inner.GetMembers(groupId);
    public void AddMember(string groupId, MemberEntry member) => _inner.AddMember(groupId, member);
    public void RemoveMember(string groupId, MemberEntry member) => _inner.RemoveMember(groupId, member);
    public void ReplaceMembers(string groupId, HashSet<MemberEntry> members) => _inner.ReplaceMembers(groupId, members);
    public string StartSession(string crawlType = "identity", string syncType = "full")
        => _inner.StartSession(crawlType, syncType);
    public void CompleteSession(string sessionId, SyncSessionStats stats, string status = "completed")
        => _inner.CompleteSession(sessionId, stats, status);
    public Dictionary<string, object?>? GetLastSession(string? crawlType = null) => _inner.GetLastSession(crawlType);
    public DateTime? GetLastSuccessfulContentCrawlTime() => _inner.GetLastSuccessfulContentCrawlTime();
    public string[]? GetCachedFields(string instanceUrl, string objectType) => _inner.GetCachedFields(instanceUrl, objectType);
    public void SaveCachedFields(string instanceUrl, string objectType, string[] fields)
        => _inner.SaveCachedFields(instanceUrl, objectType, fields);
    public int ClearFieldCache(string? instanceUrl = null, string? objectType = null)
        => _inner.ClearFieldCache(instanceUrl, objectType);
    public string? GetCachedFls(string instanceUrl, string objectType) => _inner.GetCachedFls(instanceUrl, objectType);
    public void SaveCachedFls(string instanceUrl, string objectType, string permissionsJson)
        => _inner.SaveCachedFls(instanceUrl, objectType, permissionsJson);
    public int ClearFlsCache(string? instanceUrl = null, string? objectType = null)
        => _inner.ClearFlsCache(instanceUrl, objectType);
    public string ConnectionId => _inner.ConnectionId;
    public string DbPath => _inner.DbPath;
    public Dictionary<string, int> GetStats() => _inner.GetStats();
    public void Close() => _inner.Close();
    public void Dispose() => _inner.Dispose();
}
