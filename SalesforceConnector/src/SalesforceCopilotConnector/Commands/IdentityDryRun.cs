// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// identity-dry-run command — preview identity crawl changes without calling Graph.
//
// Crawls Salesforce for group membership, diffs against the local SQLite store,
// and prints a report of what would be created, updated, deleted, or left
// unchanged.  No Microsoft Graph API calls are made.
//
// Usage::
//
//     run.py identity-dry-run
//     run.py identity-dry-run --verbose
//     run.py identity-dry-run --save          # also writes to SQLite DB
//     run.py identity-dry-run --save --verbose

using System.Globalization;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.AclEngine;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Commands;

public static class IdentityDryRun
{
    /// <summary>Run identity crawl against Salesforce and show what Graph calls would be made.</summary>
    public static async Task<bool?> CmdIdentityDryRunAsync(ParsedArgs args)
    {
        var inv = CultureInfo.InvariantCulture;
        var (logFile, _) = CommandRegistry.SetupLogging("identity_dry_run", verbose: args.GetBool("verbose"));
        var logger = Logging.GetLogger("identity_dry_run");
        var progress = Logging.GetLogger("progress");
        var startTime = CommandRegistry.MonotonicSeconds();
        IIdentityStore? store = null;

        try
        {
            var config = Settings.LoadConfig();
            progress.Info($"Identity Dry Run for connector '{config.Connector.Id}'");
            progress.Info(new string('=', 60));

            // ── Step 1: Crawl Salesforce ──────────────────────────────────────────
            var sfClient = new SalesforceClient(
                instanceUrl: config.Connector.Salesforce.InstanceUrl,
                apiVersion: config.Connector.Salesforce.ApiVersion,
                accessToken: await ApiClient.GetSalesforceAccessTokenAsync(config),
                tokenRefresher: () => ApiClient.GetSalesforceAccessTokenAsync(config).GetAwaiter().GetResult());

            var objectNames = new List<string>();
            if (config.SchemaConfig["objectList"] is JsonArray objectList)
            {
                foreach (var obj in objectList)
                {
                    var name = obj?["objectName"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(name))
                        objectNames.Add(name);
                }
            }
            progress.Info($"  Objects: {string.Join(", ", objectNames)}");

            var handler = new IdentitySyncHandler(
                sfClient: sfClient,
                objectNames: objectNames,
                parentMap: config.ParentMap,
                owdOverrides: config.OwdOverrides,
                owdFieldMap: config.OwdFieldMap);

            progress.Info("  Crawling Salesforce...");
            var crawlResult = await handler.RunFullCrawlAsync();
            progress.Info(
                $"  Crawl complete: {crawlResult.TotalGroupsEmitted} group(s), " +
                $"{crawlResult.TotalUsersEmitted} user membership(s)");

            // ── Step 2: Flatten to member sets (resolve SF users → AAD GUIDs) ─────
            var graphClient = new GraphClient();
            var principalMapper = new PrincipalMapper(
                sfClient: sfClient,
                graphClient: graphClient,
                tenantId: config.TenantId,
                batchSize: config.Tuning.SalesforceBatchSize);
            var publisher = new IdentityPublisher(
                graphClient: graphClient,
                connectionId: config.Connector.Id,
                principalMapper: principalMapper);
            var flat = await publisher.FlattenCrawlResultAsync(crawlResult);

            // ── Step 3: Diff against SQLite store ─────────────────────────────────
            store = IdentityStore.CreateStore(config.Connector.Id);
            var storedStats = store.GetStats();
            progress.Info(
                $"  SQLite store: {storedStats["groups"]} group(s), " +
                $"{storedStats["members"]} member(s) from previous run");

            var diffs = store.ComputeDiff(flat);

            // ── Step 4: Print report ──────────────────────────────────────────────
            var creates = diffs.Where(d => d.Action == "create").ToList();
            var updates = diffs.Where(d => d.Action == "update").ToList();
            var deletes = diffs.Where(d => d.Action == "delete").ToList();
            var unchanged = diffs.Where(d => d.Action == "unchanged").ToList();
            var totalApiCalls = diffs.Sum(d => d.ApiCallsNeeded);

            progress.Info("");
            progress.Info(new string('=', 60));
            progress.Info("  IDENTITY DRY RUN REPORT");
            progress.Info(new string('=', 60));
            progress.Info($"  Groups to CREATE:    {creates.Count}");
            progress.Info($"  Groups to UPDATE:    {updates.Count}");
            progress.Info($"  Groups to DELETE:    {deletes.Count}");
            progress.Info($"  Groups UNCHANGED:    {unchanged.Count}");
            progress.Info($"  Est. API calls:      {totalApiCalls}");
            progress.Info(new string('-', 60));

            if (creates.Count > 0)
            {
                progress.Info("");
                progress.Info("  NEW GROUPS:");
                foreach (var d in creates)
                    progress.Info($"    + {d.GroupId}  ({d.MembersToAdd.Count} members)");
            }

            if (updates.Count > 0)
            {
                progress.Info("");
                progress.Info("  UPDATED GROUPS:");
                foreach (var d in updates)
                {
                    progress.Info(
                        $"    ~ {d.GroupId}  (+{d.MembersToAdd.Count} members, -{d.MembersToRemove.Count} members)");
                    foreach (var m in d.MembersToAdd.Take(5))
                        progress.Info($"        + {m.MemberId} ({m.MemberType})");
                    if (d.MembersToAdd.Count > 5)
                        progress.Info($"        ... and {d.MembersToAdd.Count - 5} more");
                    foreach (var m in d.MembersToRemove.Take(5))
                        progress.Info($"        - {m.MemberId} ({m.MemberType})");
                    if (d.MembersToRemove.Count > 5)
                        progress.Info($"        ... and {d.MembersToRemove.Count - 5} more");
                }
            }

            if (deletes.Count > 0)
            {
                progress.Info("");
                progress.Info("  STALE GROUPS (to delete):");
                foreach (var d in deletes)
                    progress.Info($"    x {d.GroupId}");
            }

            if (unchanged.Count > 0)
            {
                progress.Info("");
                progress.Info($"  UNCHANGED GROUPS: {unchanged.Count} (no API calls needed)");
            }

            var elapsed = CommandRegistry.MonotonicSeconds() - startTime;
            progress.Info("");
            progress.Info($"  Time: {elapsed.ToString("F1", inv)}s");
            progress.Info($"  Log:  {logFile}");
            progress.Info(new string('=', 60));
            progress.Info("");
            progress.Info("  This was a DRY RUN. No Graph API calls were made.");

            // ── Step 5: Optionally save to SQLite ─────────────────────────────────
            var saveToDb = args.GetBool("save");
            if (saveToDb)
            {
                progress.Info("  --save flag set: writing crawl data to SQLite...");

                var sessionId = store.StartSession(crawlType: "identity-dry-run");
                var dryStats = new SyncSessionStats
                {
                    SessionId = sessionId,
                    GroupsCreated = creates.Count,
                    GroupsUpdated = updates.Count,
                    GroupsDeleted = deletes.Count,
                    GroupsUnchanged = unchanged.Count,
                    MembersAdded = creates.Concat(updates).Sum(d => d.MembersToAdd.Count),
                    MembersRemoved = updates.Sum(d => d.MembersToRemove.Count),
                };

                foreach (var (groupId, value) in flat)
                {
                    store.UpsertGroup(groupId, value.Item1);
                    store.ReplaceMembers(groupId, value.Item2);
                }

                // Delete stale groups from store
                foreach (var d in deletes)
                    store.DeleteGroup(d.GroupId);

                store.CompleteSession(sessionId, dryStats, status: "completed");

                var finalStats = store.GetStats();
                progress.Info(
                    $"  SQLite updated: {finalStats["groups"]} group(s), {finalStats["members"]} member(s)");
                progress.Info($"  DB path: {store.DbPath}");
            }
            else
            {
                progress.Info("  Tip: add --save to write crawl data to SQLite without calling Graph.");
            }

            progress.Info("  To execute for real: set USE_GROUP_ACL=true and run full-deployment or ingest.");
            progress.Info("");

            store.Close();
            return true;
        }
        catch (Exception e)
        {
            Logging.GetLogger("identity_dry_run").Error($"Fatal error: {e.Message}", e);
            return false;
        }
        finally
        {
            // Ensure the SQLite connection is always released
            try
            {
                store?.Close();
            }
            catch
            {
                // Best-effort close on the way out — a dispose failure must not
                // mask the command's real result (or its real exception).
            }
        }
    }
}
