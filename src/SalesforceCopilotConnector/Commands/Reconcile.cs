// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// reconcile command — index-vs-source drift report over the ingested-item inventory.
//
// For every object type (or just --type X) it compares what the connector has
// ingested (the inventory, data/{connector_id}_inventory.db) against the live
// Salesforce record set, and reports the two drift classes:
//
//   MISSING (in Salesforce, not indexed) — reported; the next crawl or
//       `retry-failed` ingests these.
//   STALE (indexed, gone from Salesforce) — reported; with --fix the items are
//       DELETEd from the Graph connection and dropped from the inventory.
//
// Returns true only when no drift remains (after any --fix pass), so the command
// slots straight into a monitoring cron.
//
// Usage::
//
//     run.py reconcile
//     run.py reconcile --type Case
//     run.py reconcile --fix
//     run.py reconcile --verbose

using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Commands;

public static class Reconcile
{
    private static readonly IAppLogger Logger = Logging.GetLogger("reconcile");

    /// <summary>Report (and optionally fix) index-vs-source drift using the ingested-item inventory.</summary>
    public static async Task<bool?> CmdReconcileAsync(ParsedArgs args)
    {
        var onlyType = args.GetString("type");
        var fix = args.GetBool("fix");
        var (logFile, _) = CommandRegistry.SetupLogging("reconcile", verbose: args.GetBool("verbose"));
        var progress = Logging.GetLogger("progress");

        Logger.Info($"📄 Logging to: {logFile}");
        progress.Info(new string('=', 70));
        progress.Info("RECONCILE: index-vs-source drift" + (fix ? " (--fix)" : string.Empty));
        progress.Info(new string('=', 70));

        try
        {
            var config = Settings.LoadConfig();

            // ── Validate --type against the configured object list ────────────────
            if (onlyType is not null
                && !ApiClient.ObjectConfigs.Any(
                    c => string.Equals(c.ObjectType, onlyType, StringComparison.OrdinalIgnoreCase)))
            {
                Console.Error.WriteLine(
                    $"error: object type '{onlyType}' is not configured "
                    + $"(known: {string.Join(", ", ApiClient.ObjectConfigs.Select(c => c.ObjectType))})");
                return false;
            }

            // ── Build the Graph client the same way the other commands do ─────────
            var client = new GraphClient(
                apiVersion: config.Tuning.GraphApiVersion,
                maxRetries: config.Tuning.GraphMaxRetries,
                retryBackoffBase: config.Tuning.GraphRetryBackoffBase);

            var reconciler = new Reconciler(config, client);
            var report = await reconciler.ReconcileAsync(onlyType, fix, ServiceStop.Token);

            foreach (var drift in report.Objects)
            {
                progress.Info(
                    $"{drift.ObjectName} [{drift.ConnectionId}]: source={drift.SourceCount} "
                    + $"indexed={drift.IndexedCount} missing={drift.Missing.Count} stale={drift.Stale.Count}"
                    + (fix ? $" fixed={drift.FixedCount}" : string.Empty));
                foreach (var itemId in drift.Missing.Take(10))
                    progress.Info($"    missing: {itemId}");
                if (drift.Missing.Count > 10)
                    progress.Info($"    ... and {drift.Missing.Count - 10} more missing");
                foreach (var itemId in drift.Stale.Take(10))
                    progress.Info($"    stale:   {itemId}");
                if (drift.Stale.Count > 10)
                    progress.Info($"    ... and {drift.Stale.Count - 10} more stale");
                foreach (var failure in drift.FixFailures)
                    progress.Info($"    fix failed: {failure}");
            }

            progress.Info(
                $"Totals: missing={report.TotalMissing} stale={report.TotalStale}"
                + (fix ? $" fixed={report.TotalFixed}" : string.Empty));

            if (!report.HasDrift)
            {
                progress.Info("Index and source are in agreement — no drift.");
                return true;
            }
            if (!report.HasRemainingDrift)
            {
                progress.Info("All drift repaired.");
                return true;
            }
            progress.Info(fix
                ? "Drift remains (missing items are ingested by the next crawl; see fix failures above)."
                : "Drift detected — run a full crawl for missing items, or `reconcile --fix` to remove stale ones.");
            return false;
        }
        catch (Exception exc)
        {
            Logger.Error($"❌ reconcile failed: {exc.Message}", exc);
            return false;
        }
    }
}
