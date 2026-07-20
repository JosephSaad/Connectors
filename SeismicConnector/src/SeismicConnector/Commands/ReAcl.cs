// Commands/ReAcl.cs
// -----------------
// `reacl [--dry-run]` — permission-change re-ACL audit/repair
// (Graph/ReAclSweep.cs). Walks the full inventory, re-resolves permissions for
// every ingested item, and detects those whose effective ACL drifted from the
// index. --dry-run reports counts only; without it, drifted items are re-ACLed
// via an ACL-only Graph PATCH (content is never re-sent). Works regardless of
// the PERMISSION_REACL flag (which only gates the automatic per-crawl path).
//
// Exit code: 0 when nothing needs repair (or everything was repaired), 1 when
// a dry run surfaced ACL drift that still needs applying.

using System.Diagnostics;
using SeismicConnector.Graph;
using SeismicConnector.Infrastructure;

namespace SeismicConnector.Commands;

public static class ReAcl
{
    public static async Task<object?> CmdReAcl(ParsedArgs args)
    {
        var dryRun = args.GetFlag("dry-run");
        var (logFile, summaryFile) = CommandRegistry.SetupLogging("reacl", args.Verbose);
        var progress = Logging.GetLogger("progress");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var runtime = Runtime.Create();
            // ReAclLockedAsync records an acl-restrict decision every time it
            // re-locks a classification-locked item — those are ledger entries.
            using var ledger = runtime.OpenLedger();

            var runDir = Path.GetDirectoryName(logFile) ?? CommandRegistry.LogsDir;
            var reportPath = Path.Combine(
                runDir,
                $"reacl_report_{runtime.Config.Connector.Id}_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");

            var sweep = new ReAclSweep(
                runtime.Config, runtime.Seismic, runtime.Store, runtime.Pipeline);
            var summary = await sweep.RunAsync(dryRun, reportPath, ServiceStop.Token);

            progress.Info($"Re-ACL report: {reportPath}");
            CommandRegistry.WriteSummary(
                summaryFile, logFile, runtime.Pipeline.Stats, null,
                runtime.Config.Connector.Id, stopwatch.Elapsed.TotalSeconds,
                dryRun ? "reacl --dry-run" : "reacl");

            // A large drift is worth an alert (both modes surface it).
            if (summary.DriftCount > 0)
                await Alerting.RaiseAsync(
                    "acl_drift",
                    $"reacl detected {summary.DriftCount} item(s) with changed effective permissions",
                    summary.CountsByKind);

            // Dry run with genuine drift → exit 1 (there is work to apply);
            // a repair run that applied the changes exits 0.
            return !(dryRun && summary.DriftCount > 0);
        }
        catch (OperationCanceledException)
        {
            progress.Warning("Stopped.");
            return true;
        }
        catch (Exception ex)
        {
            progress.Error($"reacl failed: {ex.Message}");
            Logging.GetLogger("seismic_connector").Error("reacl failed", ex);
            return false;
        }
    }
}
