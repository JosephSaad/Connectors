// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// setup-connection command — create/verify connection and register schema.
//
// Performs the following steps in order:
//
// 1. Load configuration from environment variables and config files.
// 2. Initialise the Microsoft Graph API client.
// 3. Create or verify the external connection.
// 4. Register the Graph connector schema.
// 5. Configure search display settings (result type / adaptive card).
// 6. Wait for the connection to reach the ``ready`` state.
//
// Stops after the connection is ready — does **not** ingest any content.
// Useful for initial setup or re-registering an updated schema.
//
// Usage::
//
//     run.py setup-connection           # quiet console
//     run.py setup-connection --verbose  # detailed console output
//
// Returns ``true`` on success, ``false`` on failure (exit code 1).

using System.Globalization;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Commands;

public static class SetupConnection
{
    /// <summary>Create/verify the external connection and register the schema.</summary>
    public static async Task<bool?> CmdSetupConnectionAsync(ParsedArgs args)
    {
        var inv = CultureInfo.InvariantCulture;
        var verbose = args.GetBool("verbose");
        var (logFile, _) = CommandRegistry.SetupLogging("setup_connection", verbose: verbose);
        var logger = Logging.GetLogger("setup_connection");
        var progress = Logging.GetLogger("progress");
        var startTime = CommandRegistry.MonotonicSeconds();

        try
        {
            logger.Info($"📄 Logging to: {logFile}");
            logger.Info(new string('=', 70));
            logger.Info("SETUP CONNECTION: Connection → Schema → Search Settings");
            logger.Info(new string('=', 70));

            var config = Settings.LoadConfig();
            progress.Info($"Starting setup for connector '{config.Connector.Id}'...");
            logger.Info("Configuration loaded:");
            logger.Info($"  Connector ID: {config.Connector.Id}");
            logger.Info($"  Connector Name: {config.Connector.Name}");

            // ── Step 1: Graph client ──────────────────────────────────────────
            logger.Info("\n" + new string('=', 70));
            logger.Info("STEP 1: Initialize Graph API Client");
            logger.Info(new string('=', 70));
            var client = new GraphClient(
                apiVersion: config.Tuning.GraphApiVersion,
                maxRetries: config.Tuning.GraphMaxRetries,
                retryBackoffBase: config.Tuning.GraphRetryBackoffBase);
            logger.Info("✓ Graph client initialized");
            progress.Info("  Graph client initialized");

            // ── Step 2: Connection ────────────────────────────────────────────
            logger.Info("\n" + new string('=', 70));
            logger.Info("STEP 2: Create/Ensure Connection");
            logger.Info(new string('=', 70));
            var initialTimestamp = CommandRegistry.MonotonicSeconds();
            var connectionStatus = await Connection.EnsureConnectionAsync(config, client, initialTimestamp);
            if (connectionStatus == null)
            {
                logger.Error("❌ Failed to create/ensure connection");
                progress.Info("❌ Connection creation failed");
                return false;
            }
            logger.Info($"✓ Connection ready: {config.Connector.Id}");
            progress.Info("  Connection ensured");

            // ── Step 3: Schema ────────────────────────────────────────────────
            logger.Info("\n" + new string('=', 70));
            logger.Info("STEP 3: Register Schema");
            logger.Info(new string('=', 70));
            await Schema.EnsureSchemaAsync(config, client);
            logger.Info("✓ Schema registered");
            progress.Info("  Schema registered");

            // ── Step 4: Search settings ───────────────────────────────────────
            logger.Info("\n" + new string('=', 70));
            logger.Info("STEP 4: Configure Search Settings");
            logger.Info(new string('=', 70));
            await Connection.SetSearchSettingsAsync(config, client);
            logger.Info("✓ Search settings configured");
            progress.Info("  Search settings configured");

            // ── Step 5: Verify ready ──────────────────────────────────────────
            logger.Info("\n" + new string('=', 70));
            logger.Info("STEP 5: Verify Connection Ready");
            logger.Info(new string('=', 70));
            if (!await Connection.IsConnectionReadyAsync(config, client))
            {
                logger.Warning("⚠ Connection not ready yet, waiting...");
                await Task.Delay(TimeSpan.FromSeconds(5));
                if (!await Connection.IsConnectionReadyAsync(config, client))
                {
                    logger.Error("❌ Connection still not ready");
                    progress.Info("❌ Connection not ready after wait");
                    return false;
                }
            }
            logger.Info("✓ Connection is ready");
            progress.Info("  Connection ready");

            var elapsed = CommandRegistry.MonotonicSeconds() - startTime;
            logger.Info(new string('=', 70));
            logger.Info($"SETUP COMPLETE in {elapsed.ToString("F1", inv)}s");
            logger.Info(new string('=', 70));
            progress.Info($"✅ Setup complete in {elapsed.ToString("F1", inv)}s — connection & schema are ready.");
            progress.Info("   Run 'python run.py full-deployment' or 'python run.py ingest' to ingest content.");
            return true;
        }
        catch (Exception ex)
        {
            logger.Error("❌ Setup failed with an unexpected error", ex);
            progress.Info($"❌ Setup failed — check {logFile} for details.");
            return false;
        }
    }
}
