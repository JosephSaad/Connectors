// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Infrastructure/SalesforceFlags.cs
// ---------------------------------
// Salesforce-specific policy gates. This is what remained of the connector's
// own EnvFlags after the boolean vocabulary moved to the chassis: the deletion
// sweep is a Salesforce judgement (see DeletionSync below), not fleet
// infrastructure, so it stays here — under a connector-specific name, because a
// second type called EnvFlags is exactly the drift the chassis exists to stop.
//
// Everything generic — the truthy/falsy vocabulary, trimming, the warning on an
// unrecognised value — now comes from Connector.Chassis.EnvFlags, aliased to
// `EnvFlags` in the csproj. Both gates below are opt-OUT, so both are spelled
// with IsFalse: a protective default must be switched off deliberately, never
// by a value nobody meant.

namespace SalesforceCopilotConnector.Infrastructure;

/// <summary>Feature gates specific to this connector's Salesforce behaviour.</summary>
public static class SalesforceFlags
{
    /// <summary>
    /// <c>DELETION_SYNC</c> — run the automatic inventory-backed existence sweep after a
    /// full crawl (withdraw items deleted in Salesforce from the Graph connection).
    /// <b>Default TRUE</b>: unlike every other operational knob this is opt-OUT, because the
    /// sweep is the connector's only safety net for hard-deleted / long-offline records
    /// (Salesforce's native <c>IsDeleted</c> only covers the ~15-day Recycle Bin). Set
    /// <c>false</c>/<c>0</c>/<c>no</c> to disable the sweep; the inventory is still maintained
    /// so <c>reconcile</c> keeps working.
    /// </summary>
    public static bool DeletionSync => !EnvFlags.IsFalse("DELETION_SYNC");

    /// <summary>
    /// <c>DELETION_SYNC_MAX_PERCENT</c> — mass-deletion safety guard for the automatic sweep.
    /// When more than this percentage of an object type's inventory would be deleted in one
    /// sweep (and the inventory holds at least
    /// <see cref="Graph.Reconciler.MinInventoryForSafetyGuard"/> items), that object is skipped
    /// with a warning and a <c>deletion_sweep_skipped</c> alert. <c>0</c> or <c>&gt;= 100</c>
    /// disables the guard. Default 25.
    /// </summary>
    public static int DeletionSyncMaxPercent => EnvFlags.GetInt("DELETION_SYNC_MAX_PERCENT", 25);
}
