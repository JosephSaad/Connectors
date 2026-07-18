// Commands/RetryFailed.cs
// -----------------------
// Re-ingests every item in the dead-letter queue (file JSONL or SQL,
// whichever backend is active). Items that succeed are removed from the
// queue; --clear-on-success clears the whole queue when every retry
// succeeded. Withdrawal failures are retried as withdrawals.

using System.Diagnostics;
using SeismicConnector.Config;
using SeismicConnector.Infrastructure;

namespace SeismicConnector.Commands;

public static class RetryFailed
{
    public static async Task<object?> CmdRetryFailed(ParsedArgs args)
    {
        var (logFile, summaryFile) = CommandRegistry.SetupLogging("retry-failed", args.Verbose);
        var progress = Logging.GetLogger("progress");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var runtime = Runtime.Create();
            var connectorId = runtime.Config.Connector.Id;

            var explicitFile = args.GetString("file");
            var records = explicitFile is not null
                ? ReadRecordsFromFile(explicitFile)
                : SyncState.ReadFailedRecords(connectorId);

            if (records.Count == 0)
            {
                progress.Info("Dead-letter queue is empty — nothing to retry.");
                return true;
            }

            progress.Info($"Retrying {records.Count} dead-lettered item(s)...");
            using var report = runtime.OpenReport(logFile);

            var allOk = true;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in records)
            {
                if (ServiceStop.Requested)
                    break;
                var itemId = record["item_id"]?.GetValue<string>();
                var objectType = record["object_type"]?.GetValue<string>() ?? "ContentItem";
                if (string.IsNullOrEmpty(itemId) || !seen.Add(itemId))
                    continue;

                bool ok;
                try
                {
                    if (objectType == "Withdrawal")
                    {
                        await runtime.Pipeline.WithdrawItemAsync(
                            itemId, "retry", "dead-letter retry of failed withdrawal", ServiceStop.Token);
                        ok = true;
                    }
                    else if (objectType == "Teamsite")
                    {
                        // A whole-teamsite listing failure: re-crawl handled by next run;
                        // here we just probe that the teamsite lists now.
                        ok = (await runtime.Seismic.GetContentsAsync(itemId, null, ServiceStop.Token)).Count >= 0;
                    }
                    else
                    {
                        ok = await runtime.Pipeline.IngestSingleAsync(itemId, null, ServiceStop.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;  // graceful stop — handled by the command-level catch
                }
                catch (Exception ex)
                {
                    // Per-record boundary: one record whose retry throws (Seismic
                    // 5xx, breaker open, ...) must not abort the retries queued
                    // BEHIND it. It stays in the dead-letter queue (nothing is
                    // removed on failure) and the run reports FAIL + exit 1.
                    Logging.GetLogger("seismic_connector").Error(
                        $"retry-failed: retry of {objectType} {itemId} threw — the record stays in "
                        + "the dead-letter queue; continuing with the remaining records.", ex);
                    ok = false;
                }
                allOk &= ok;
                progress.Info($"  {(ok ? "OK " : "FAIL")} {objectType} {itemId}");
            }

            report.Finish();
            if (allOk && args.GetFlag("clear-on-success") && explicitFile is null)
            {
                SyncState.ClearFailedRecords(connectorId);
                progress.Info("All retries succeeded — dead-letter queue cleared.");
            }
            Metrics.SetDeadLetterDepth(SyncState.ReadFailedRecords(connectorId).Count);
            CommandRegistry.WriteSummary(
                summaryFile, logFile, runtime.Pipeline.Stats, null,
                connectorId, stopwatch.Elapsed.TotalSeconds, "retry-failed", report.FilePath);
            return allOk;
        }
        catch (Exception ex)
        {
            progress.Error($"retry-failed failed: {ex.Message}");
            Logging.GetLogger("seismic_connector").Error("retry-failed failed", ex);
            return false;
        }
    }

    private static List<System.Text.Json.Nodes.JsonObject> ReadRecordsFromFile(string path)
    {
        var entries = new List<System.Text.Json.Nodes.JsonObject>();
        if (!File.Exists(path))
            return entries;
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(trimmed) is System.Text.Json.Nodes.JsonObject obj)
                    entries.Add(obj);
            }
            catch (System.Text.Json.JsonException ex)
            {
                // A torn/corrupt line (e.g. crash mid-append) must not hide the
                // parseable records around it — skip it, but say which line.
                Logging.GetLogger("seismic_connector").Warning(
                    $"retry-failed: {path} line {lineNumber} is not valid JSON ({ex.Message}) — skipped.");
            }
        }
        return entries;
    }
}
