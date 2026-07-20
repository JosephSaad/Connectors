// Commands/Reconcile.cs
// ---------------------
// `reconcile [--repair]` — full-inventory drift sweep between Seismic, the
// tracked-item store and the Graph index (Graph/DriftSweep.cs). Report-only
// by default; --repair withdraws orphaned/excluded/expired items and
// (re-)ingests missing or version-drifted ones. Exit code: 0 when the index
// is in sync (or fully repaired), 1 when unrepaired drift remains.

using System.Diagnostics;
using SeismicConnector.Graph;
using SeismicConnector.Infrastructure;

namespace SeismicConnector.Commands;

public static class Reconcile
{
    public static async Task<object?> CmdReconcile(ParsedArgs args)
    {
        var repair = args.GetFlag("repair");
        var (logFile, summaryFile) = CommandRegistry.SetupLogging("reconcile", args.Verbose);
        var progress = Logging.GetLogger("progress");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var runtime = Runtime.Create();
            using var report = runtime.OpenReport(logFile);
            // --repair re-ingests and withdraws, so this command makes exclusion
            // and ACL-restriction decisions like any crawl: it needs the ledger.
            using var ledger = runtime.OpenLedger();

            var runDir = Path.GetDirectoryName(logFile) ?? CommandRegistry.LogsDir;
            var driftReportPath = Path.Combine(
                runDir,
                $"drift_report_{runtime.Config.Connector.Id}_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");

            var sweep = new DriftSweep(
                runtime.Config, runtime.Seismic, runtime.Store, runtime.Pipeline);
            var summary = await sweep.RunAsync(repair, driftReportPath, ServiceStop.Token);

            report.Finish();
            progress.Info($"Drift report: {driftReportPath}");
            CommandRegistry.WriteSummary(
                summaryFile, logFile, runtime.Pipeline.Stats, null,
                runtime.Config.Connector.Id, stopwatch.Elapsed.TotalSeconds,
                repair ? "reconcile --repair" : "reconcile", report.FilePath);

            if (summary.HasUnrepairedDrift)
                await Alerting.RaiseAsync(
                    "index_drift",
                    $"reconcile found {summary.UnrepairedCount} unrepaired drift finding(s)",
                    summary.CountsByKind);
            return !summary.HasUnrepairedDrift;
        }
        catch (OperationCanceledException)
        {
            progress.Warning("Stopped.");
            return true;
        }
        catch (Exception ex)
        {
            progress.Error($"reconcile failed: {ex.Message}");
            Logging.GetLogger("seismic_connector").Error("reconcile failed", ex);
            return false;
        }
    }
}
