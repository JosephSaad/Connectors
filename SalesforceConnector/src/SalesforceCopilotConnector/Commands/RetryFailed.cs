// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// retry-failed command — re-ingest every item recorded in the dead-letter file.
//
// Reads ``logs/failed_records_<connector_id>.jsonl``, groups the entries by
// Salesforce object type (using the ``object_type`` field written when the
// failure was first recorded), then re-ingests each item via the normal
// ``IngestContentAsync`` path.  Successfully re-ingested items are NOT removed
// from the JSONL automatically — use ``--clear-on-success`` to wipe the file
// once all retries pass.
//
// Usage::
//
//     run.py retry-failed
//     run.py retry-failed --file logs/failed_records_MyConnector.jsonl
//     run.py retry-failed --verbose
//     run.py retry-failed --clear-on-success

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Config;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Commands;

public static class RetryFailed
{
    /// <summary>Re-ingest every item recorded in the dead-letter JSONL file.</summary>
    public static async Task<bool?> CmdRetryFailedAsync(ParsedArgs args)
    {
        const string label = "RETRY FAILED RECORDS";
        var (logFile, summaryFile) = CommandRegistry.SetupLogging("retry_failed", verbose: args.GetBool("verbose"));
        var logger = Logging.GetLogger("retry_failed");
        var progress = Logging.GetLogger("progress");
        var startTime = CommandRegistry.MonotonicSeconds();
        var stats = new IngestionStats();
        AppConfig? config = null;

        try
        {
            logger.Info($"📄 Logging to: {logFile}");
            logger.Info(new string('=', 70));
            logger.Info(label);
            logger.Info(new string('=', 70));

            config = Settings.LoadConfig();
            var connectorId = config.Connector.Id;

            // ── Resolve dead-letter file path ────────────────────────────────────
            var explicitFile = args.GetString("file");
            string dlPath;
            if (!string.IsNullOrEmpty(explicitFile))
            {
                dlPath = explicitFile;
                if (!Path.IsPathRooted(dlPath))
                    dlPath = Path.Combine(Directory.GetCurrentDirectory(), dlPath);
            }
            else
            {
                dlPath = SyncState.FailedRecordsPath(connectorId);
            }

            logger.Info($"  Dead-letter file: {dlPath}");

            // ── Read dead-letter file ────────────────────────────────────────────
            if (!File.Exists(dlPath))
            {
                logger.Info($"✓ No dead-letter file found at {dlPath} — nothing to retry.");
                progress.Info("No failed records to retry.");
                var elapsedEarly = CommandRegistry.MonotonicSeconds() - startTime;
                CommandRegistry.WriteSummary(summaryFile, logFile, stats, "N/A", connectorId, elapsedEarly, label);
                return null;
            }

            var failedEntries = new List<JsonObject>();
            foreach (var rawLine in SyncState.ReadLinesShared(dlPath))
            {
                var line = rawLine.Trim();
                if (line.Length > 0)
                    failedEntries.Add(JsonNode.Parse(line)!.AsObject());
            }
            if (failedEntries.Count == 0)
            {
                logger.Info("✓ Dead-letter file is empty — nothing to retry.");
                progress.Info("No failed records to retry.");
                var elapsedEarly = CommandRegistry.MonotonicSeconds() - startTime;
                CommandRegistry.WriteSummary(summaryFile, logFile, stats, "N/A", connectorId, elapsedEarly, label);
                return null;
            }

            // Deduplicate by item_id (keep last occurrence — most recent failure).
            var seen = new Dictionary<string, JsonObject>();
            var order = new List<string>();  // preserve Python dict insertion order
            foreach (var entry in failedEntries)
            {
                var id = entry["item_id"]?.GetValue<string>() ?? "";
                if (id.Length > 0)
                {
                    if (!seen.ContainsKey(id))
                        order.Add(id);
                    seen[id] = entry;
                }
            }
            var uniqueEntries = order.Select(id => seen[id]).ToList();

            logger.Info($"  Connector ID: {connectorId}");
            logger.Info($"  Items to retry: {uniqueEntries.Count} ({failedEntries.Count} total entries, " +
                        $"{failedEntries.Count - uniqueEntries.Count} duplicates removed)");
            progress.Info($"Found {uniqueEntries.Count} unique item(s) to retry.");

            // ── Initialise Graph client ──────────────────────────────────────────
            logger.Info("\n" + new string('=', 70));
            logger.Info("STEP 1: Initialize Graph API Client");
            logger.Info(new string('=', 70));
            var client = new GraphClient(
                apiVersion: config.Tuning.GraphApiVersion,
                maxRetries: config.Tuning.GraphMaxRetries,
                retryBackoffBase: config.Tuning.GraphRetryBackoffBase);
            logger.Info("✓ Graph client initialized");

            // ── Verify connection ────────────────────────────────────────────────
            logger.Info("\n" + new string('=', 70));
            logger.Info("STEP 2: Verify Connection Ready");
            logger.Info(new string('=', 70));
            if (!await Connection.IsConnectionReadyAsync(config, client))
            {
                logger.Error("❌ Connection is not ready. Please run 'full-deployment' first.");
                return null;
            }
            logger.Info($"✓ Connection is ready: {connectorId}");

            // ── Re-ingest each item ──────────────────────────────────────────────
            logger.Info("\n" + new string('=', 70));
            logger.Info("STEP 3: Re-ingest Failed Items");
            logger.Info(new string('=', 70));

            var allSuccess = true;
            var stillFailing = new List<JsonObject>();  // entries that failed again — written to a new file at the end
            for (var idx = 1; idx <= uniqueEntries.Count; idx++)
            {
                var entry = uniqueEntries[idx - 1];
                var itemId = entry["item_id"]!.GetValue<string>();
                var objectType = entry["object_type"]?.GetValue<string>();
                if (string.IsNullOrEmpty(objectType))
                    objectType = null;
                var originalError = entry["error"]?.GetValue<string>() ?? "";

                logger.Info($"[{idx}/{uniqueEntries.Count}] Retrying {itemId} (object_type={objectType ?? "auto-detect"}, " +
                            $"original_error={originalError[..Math.Min(120, originalError.Length)]})");
                progress.Info($"  [{idx}/{uniqueEntries.Count}] Retrying {itemId}…");

                var itemConfig = CommandRegistry.ReplaceConfig(config, debugItemId: itemId);
                if (objectType != null)
                    itemConfig = CommandRegistry.ReplaceConfig(itemConfig, debugObjectType: objectType);

                try
                {
                    var itemStats = await Ingest.IngestContentAsync(itemConfig, client, since: null);
                    stats.SuccessCount += itemStats.SuccessCount;
                    stats.FailedCount += itemStats.FailedCount;
                    stats.DeletedCount += itemStats.DeletedCount;
                    stats.TotalFetched += itemStats.TotalFetched;
                    // Reflect the actual ACL engine used (set from the first item that runs).
                    if (stats.AclEngine == "LEGACY" && itemStats.AclEngine != "LEGACY")
                        stats.AclEngine = itemStats.AclEngine;
                    if (itemStats.FailedCount > 0)
                    {
                        allSuccess = false;
                        stillFailing.Add(entry);
                        logger.Warning($"  ✗ {itemId} still failed after retry");
                    }
                    else
                    {
                        logger.Info($"  ✓ {itemId} ingested successfully");
                    }
                }
                catch (Exception exc)
                {
                    allSuccess = false;
                    stillFailing.Add(entry);
                    stats.FailedCount += 1;
                    logger.Error($"  ✗ Exception while retrying {itemId}: {exc.Message}", exc);
                }
            }

            // ── Write still-failing items to a new file ────────────────────────
            // These can be targeted directly on the next retry-failed run via --file.
            if (stillFailing.Count > 0)
            {
                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                var pendingPath = Path.Combine(Path.GetDirectoryName(dlPath) ?? "", $"retry_pending_{connectorId}_{ts}.jsonl");
                using (var fh = new StreamWriter(pendingPath, append: false, new UTF8Encoding(false)))
                {
                    foreach (var rec in stillFailing)
                        fh.Write(rec.ToJsonString() + "\n");
                }
                logger.Warning($"  {stillFailing.Count} item(s) still failing — written to: {pendingPath}");
                logger.Warning($"  To retry only these items run: python run.py retry-failed --file {pendingPath}");
            }

            // ── Optionally clear the dead-letter file ────────────────────────────
            if (allSuccess && args.GetBool("clear_on_success"))
            {
                try
                {
                    File.Delete(dlPath);
                }
                catch (DirectoryNotFoundException)
                {
                    // Parent directory already gone — the dead-letter file is
                    // by definition cleared; nothing to report.
                }
                logger.Info("✓ Dead-letter file cleared (all retries succeeded).");
            }
            else if (args.GetBool("clear_on_success") && !allSuccess)
            {
                logger.Warning("Some items still failed — dead-letter file NOT cleared.");
            }

            var elapsed = CommandRegistry.MonotonicSeconds() - startTime;
            CommandRegistry.WriteSummary(summaryFile, logFile, stats, "existing (verified)", connectorId, elapsed, label);
            logger.Info("✓ Retry run complete");
            return null;
        }
        catch (Exception error)
        {
            var elapsed = CommandRegistry.MonotonicSeconds() - startTime;
            CommandRegistry.WriteSummary(summaryFile, logFile, stats, "existing (verified)",
                config?.Connector?.Id ?? "unknown",
                elapsed, $"{label} (CRASHED)");
            Logging.GetLogger("retry_failed").Error($"❌ Fatal error during retry: {error.Message}", error);
            return null;
        }
    }
}
