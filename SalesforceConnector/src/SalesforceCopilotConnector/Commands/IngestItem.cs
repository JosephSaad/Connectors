// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// ingest-item command — ingest a single Salesforce record by its ID.
//
// Useful for inspecting a specific record's ingestion, ACL resolution, or
// adaptive-card rendering without re-ingesting the entire dataset.
//
// Usage::
//
//     run.py ingest-item --id 500f6000008iCNYAA2
//     run.py ingest-item --id 500f6000008iCNYAA2 --verbose

using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Commands;

public static class IngestItem
{
    // ── Test hooks ───────────────────────────────────────────────────────────
    // Mirror Python monkeypatching of the module-level imports in commands/ingest_item.py
    // (e.g. patch("commands.ingest_item.ingest_content")). Default to the real
    // implementations; behavior is identical when unhooked.
    internal static Func<AppConfig> LoadConfigHook = Settings.LoadConfig;
    internal static Func<AppConfig, GraphClient, Task<bool>> IsConnectionReadyHook = Connection.IsConnectionReadyAsync;
    internal static Func<AppConfig, GraphClient, DateTime?, Task<IngestionStats>> IngestContentHook =
        (config, client, since) => Ingest.IngestContentAsync(config, client, since: since);

    /// <summary>Ingest a single Salesforce record by its ID.</summary>
    public static async Task<bool?> CmdIngestItemAsync(ParsedArgs args)
    {
        var itemId = args.GetString("id")!;
        var label = $"INGEST ITEM ({itemId})";

        var (logFile, summaryFile) = CommandRegistry.SetupLogging("ingest_item", verbose: args.GetBool("verbose"));
        var logger = Logging.GetLogger("ingest_item");
        // Enable DEBUG on the converter logger so the field-mapping table is captured in the log file
        var connectorLogger = Logging.GetLoggerObject("salesforce_connector");
        connectorLogger.Level = LogLevels.Debug;
        foreach (var h in Logging.Root.Handlers)
        {
            if (h is LineRotatingFileHandler)  // file handler
                h.Level = LogLevels.Debug;
        }
        var progress = Logging.GetLogger("progress");
        var startTime = CommandRegistry.MonotonicSeconds();
        IngestionStats? stats = null;
        AppConfig? config = null;

        try
        {
            logger.Info($"📄 Logging to: {logFile}");
            logger.Info(new string('=', 70));
            logger.Info(label);
            logger.Info(new string('=', 70));

            config = LoadConfigHook();
            progress.Info($"Starting ingestion for item '{itemId}'...");
            logger.Info($"  Connector ID: {config.Connector.Id}");
            logger.Info($"  Salesforce Instance: {config.Connector.Salesforce.InstanceUrl}");

            var objectType = args.GetString("object_type");
            config = CommandRegistry.ReplaceConfig(config, debugItemId: itemId);
            if (!string.IsNullOrEmpty(objectType))
            {
                config = CommandRegistry.ReplaceConfig(config, debugObjectType: objectType);
                logger.Info($"  Object Type: {objectType} (user-supplied)");
            }
            logger.Info($"  Item ID: {itemId}");

            logger.Info("\n" + new string('=', 70));
            logger.Info("STEP 1: Initialize Graph API Client");
            logger.Info(new string('=', 70));
            var client = new GraphClient(
                apiVersion: config.Tuning.GraphApiVersion,
                maxRetries: config.Tuning.GraphMaxRetries,
                retryBackoffBase: config.Tuning.GraphRetryBackoffBase);
            logger.Info("✓ Graph client initialized");
            progress.Info("  Graph client initialized");

            logger.Info("\n" + new string('=', 70));
            logger.Info("STEP 2: Verify Connection Ready");
            logger.Info(new string('=', 70));
            if (!await IsConnectionReadyHook(config, client))
            {
                logger.Error("❌ Connection is not ready. Please run 'full-deployment' first.");
                return null;
            }
            logger.Info($"✓ Connection is ready: {config.Connector.Id}");
            progress.Info($"  Connection '{config.Connector.Id}' verified (existing)");

            logger.Info("\n" + new string('=', 70));
            logger.Info("STEP 3: Ingest Item with ACL");
            logger.Info(new string('=', 70));
            logger.Info($"  Instance: {config.Connector.Salesforce.InstanceUrl}");
            logger.Info($"  API Version: {config.Connector.Salesforce.ApiVersion}");
            progress.Info("  Starting ingestion...");
            stats = await IngestContentHook(config, client, null);
            logger.Info("✓ Ingestion completed");

            var elapsed = CommandRegistry.MonotonicSeconds() - startTime;
            CommandRegistry.WriteSummary(summaryFile, logFile, stats, "existing (verified)", config.Connector.Id, elapsed, label);
            return null;
        }
        catch (Exception error)
        {
            var elapsed = CommandRegistry.MonotonicSeconds() - startTime;
            stats ??= new IngestionStats();
            CommandRegistry.WriteSummary(summaryFile, logFile, stats, "existing (verified)",
                config?.Connector?.Id ?? "unknown",
                elapsed, $"{label} (CRASHED)");
            Logging.GetLogger("ingest_item").Error($"❌ Fatal error during ingestion: {error.Message}", error);
            throw;
        }
    }
}
