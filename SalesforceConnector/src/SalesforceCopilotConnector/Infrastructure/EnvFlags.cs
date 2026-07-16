// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Infrastructure/EnvFlags.cs
// --------------------------
// Command-layer feature gates read from environment variables. Uses the same
// truthy semantics as Salesforce.Settings.BoolEnv (true / 1 / yes, case-insensitive)
// so behavior is consistent across the codebase. Every flag defaults to OFF, so an
// unset environment reproduces the original single-node, file-backed behavior exactly.
//
// Exception: DELETION_SYNC defaults ON (see below) — the inventory-backed existence
// sweep is the connector's only line of defense for hard-deleted Salesforce records,
// so it is opt-OUT rather than opt-in.

using System.Globalization;

namespace SalesforceCopilotConnector.Infrastructure;

/// <summary>Boolean feature flags read from the environment for the command layer.</summary>
public static class EnvFlags
{
    /// <summary>True when the named env var is <c>true</c>/<c>1</c>/<c>yes</c> (case-insensitive).</summary>
    public static bool IsTrue(string name)
    {
        var value = (Environment.GetEnvironmentVariable(name) ?? "false").ToLowerInvariant();
        return value is "true" or "1" or "yes";
    }

    /// <summary>
    /// <c>IDENTITY_SYNC_ON_INCREMENTAL</c> — run the (incremental) identity crawl on
    /// incremental content crawls too, not just full crawls. Default false.
    /// </summary>
    public static bool IdentitySyncOnIncremental => IsTrue("IDENTITY_SYNC_ON_INCREMENTAL");

    /// <summary>
    /// <c>DELETION_SYNC</c> — run the automatic inventory-backed existence sweep after a
    /// full crawl (withdraw items deleted in Salesforce from the Graph connection).
    /// <b>Default TRUE</b>: unlike every other operational knob this is opt-OUT, because the
    /// sweep is the connector's only safety net for hard-deleted / long-offline records
    /// (Salesforce's native <c>IsDeleted</c> only covers the ~15-day Recycle Bin). Set
    /// <c>false</c>/<c>0</c>/<c>no</c> to disable the sweep; the inventory is still maintained
    /// so <c>reconcile</c> keeps working.
    /// </summary>
    public static bool DeletionSync
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("DELETION_SYNC");
            // Default ON: unset/blank ⇒ true. Only an explicit false/0/no disables it.
            if (string.IsNullOrWhiteSpace(raw))
                return true;
            return raw.Trim().ToLowerInvariant() is not ("false" or "0" or "no");
        }
    }

    /// <summary>
    /// <c>DELETION_SYNC_MAX_PERCENT</c> — mass-deletion safety guard for the automatic sweep.
    /// When more than this percentage of an object type's inventory would be deleted in one
    /// sweep (and the inventory holds at least
    /// <see cref="Graph.Reconciler.MinInventoryForSafetyGuard"/> items), that object is skipped
    /// with a warning and a <c>deletion_sweep_skipped</c> alert. <c>0</c> or <c>&gt;= 100</c>
    /// disables the guard. Default 25.
    /// </summary>
    public static int DeletionSyncMaxPercent
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("DELETION_SYNC_MAX_PERCENT");
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : 25;
        }
    }
}
