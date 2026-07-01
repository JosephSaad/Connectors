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
}
