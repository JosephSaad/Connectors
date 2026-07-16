// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// ingest-object command — ingest all records of one Salesforce object type.
//
// Useful for selectively syncing a single object (e.g. ``Case``, ``Account``,
// ``Opportunity``) without running a full ingestion.
//
// Usage::
//
//     run.py ingest-object --type Case
//     run.py ingest-object --type Account --verbose

using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Commands;

public static class IngestObject
{
    // ── Test hooks ───────────────────────────────────────────────────────────
    // Mirror Python monkeypatching of the module-level imports in commands/ingest_object.py
    // (e.g. patch("commands.ingest_object.ingest_content")). Default to the real
    // implementations; behavior is identical when unhooked.
    internal static Func<AppConfig> LoadConfigHook = Settings.LoadConfig;
    internal static Func<AppConfig, GraphClient, Task<bool>> IsConnectionReadyHook = Connection.IsConnectionReadyAsync;
    internal static Func<AppConfig, GraphClient, DateTime?, Task<IngestionStats>> IngestContentHook =
        (config, client, since) => Ingest.IngestContentAsync(config, client, since: since);

    /// <summary>Ingest all records of a specific Salesforce object type.</summary>
    public static async Task<bool?> CmdIngestObjectAsync(ParsedArgs args)
    {
        var objectType = args.GetString("type")!;
        var label = $"INGEST OBJECT ({objectType})";

        var (logFile, summaryFile) = CommandRegistry.SetupLogging($"ingest_object_{objectType}", verbose: args.GetBool("verbose"));
        var logger = Logging.GetLogger("ingest_object");
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
            progress.Info($"Starting ingestion for object type '{objectType}'...");
            logger.Info($"  Connector ID: {config.Connector.Id}");
            logger.Info($"  Salesforce Instance: {config.Connector.Salesforce.InstanceUrl}");

            config = CommandRegistry.ReplaceConfig(config, debugObjectType: objectType);
            logger.Info($"  Object Type: {objectType}");

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
            logger.Info("STEP 3: Ingest Object Type with ACL");
            logger.Info(new string('=', 70));
            logger.Info($"  Object Type: {objectType}");
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
            Logging.GetLogger("ingest_object").Error($"❌ Fatal error during ingestion: {error.Message}", error);
            throw;
        }
    }
}
