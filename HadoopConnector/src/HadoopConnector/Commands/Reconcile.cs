// Commands/Reconcile.cs
// ---------------------
// `reconcile [--type X] [--fix]` — index-vs-source drift report built on the
// ingested-item inventory:
//
//   MISSING (in BDH, not indexed) — reported; the next crawl or
//       `retry-failed` ingests these.
//   STALE (indexed, gone from BDH) — reported; with --fix the items are
//       DELETEd from the Graph connection and dropped from the inventory.
//
// Exit code 0 only when no drift remains (after any --fix pass), so the
// command slots straight into a monitoring cron.

using HadoopConnector.Graph;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Commands;

public static class Reconcile
{
    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector");

    public static async Task<object?> RunAsync(ParsedArgs args)
    {
        var onlyType = args.GetString("--type");
        var fix = args.HasFlag("--fix");

        using var context = Runtime.Create(args, "reconcile");
        Dashboard.Banner("Reconcile" + (fix ? " (--fix)" : string.Empty));

        if (onlyType is not null && context.Schema.FindObject(onlyType) is null)
        {
            Console.Error.WriteLine(
                $"error: object type '{onlyType}' is not in config/schema.json "
                + $"(known: {string.Join(", ", context.Schema.ObjectList.Select(o => o.ObjectName))})");
            return false;
        }

        try
        {
            var reconciler = new Reconciler(
                context.Config, context.Schema, context.Fetcher, context.Graph);
            var report = await reconciler.ReconcileAsync(onlyType, fix, ServiceStop.Token);

            foreach (var drift in report.Objects)
            {
                Dashboard.Line(
                    $"{drift.ObjectName} [{drift.ConnectionId}]: source={drift.SourceCount} "
                    + $"indexed={drift.IndexedCount} missing={drift.Missing.Count} stale={drift.Stale.Count}"
                    + (fix ? $" fixed={drift.FixedCount}" : string.Empty));
                foreach (var itemId in drift.Missing.Take(10))
                    Dashboard.Line($"    missing: {itemId}");
                if (drift.Missing.Count > 10)
                    Dashboard.Line($"    ... and {drift.Missing.Count - 10} more missing");
                foreach (var itemId in drift.Stale.Take(10))
                    Dashboard.Line($"    stale:   {itemId}");
                if (drift.Stale.Count > 10)
                    Dashboard.Line($"    ... and {drift.Stale.Count - 10} more stale");
                foreach (var failure in drift.FixFailures)
                    Dashboard.Line($"    fix failed: {failure}");
            }

            Dashboard.Line(
                $"Totals: missing={report.TotalMissing} stale={report.TotalStale}"
                + (fix ? $" fixed={report.TotalFixed}" : string.Empty));

            if (!report.HasDrift)
            {
                Dashboard.Line("Index and source are in agreement — no drift.");
                return true;
            }
            if (!report.HasRemainingDrift)
            {
                Dashboard.Line("All drift repaired.");
                return true;
            }
            Dashboard.Line(fix
                ? "Drift remains (missing items are ingested by the next crawl; see fix failures above)."
                : "Drift detected — run a full crawl for missing items, or `reconcile --fix` to remove stale ones.");
            return false;
        }
        catch (Exception exc)
        {
            Logger.Error("reconcile failed", exc);
            return false;
        }
    }
}
