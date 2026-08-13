// Commands/RetryFailed.cs
// -----------------------
// `retry-failed [--file path] [--clear-on-success]` — re-ingest dead-lettered
// records. Each entry's record is re-located in BDH (fresh fields + fresh
// ACL) via the targeted newest-first partition scan and re-PUT. With
// --clear-on-success the queue is cleared when EVERY retry succeeded;
// otherwise the queue is rewritten with only the still-failing entries.

using System.Text.Json.Nodes;
using HadoopConnector.Hdfs;
using HadoopConnector.Config;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Commands;

public static class RetryFailed
{
    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector");

    /// <summary>Error recorded when a dead-letter entry is KEPT because its
    /// lookup skipped an oversize file (the miss is unproven).</summary>
    internal const string IncompleteLookupError =
        "lookup incomplete: an oversize file (> BDH_MAX_FILE_BYTES) was skipped while "
        + "searching, so the record cannot be confirmed gone from BDH";

    /// <summary>A missing record may be dropped from the dead-letter queue only
    /// when the search was COMPLETE — a lookup that skipped an oversize file
    /// must never be treated as "gone from BDH".</summary>
    internal static bool ShouldDropMissing(FindByIdResult find) =>
        find.Record is null && !find.SkippedOversize;

    public static async Task<object?> RunAsync(ParsedArgs args)
    {
        using var context = Runtime.Create(args, "retry_failed");
        Dashboard.Banner("Retry failed records");

        var entries = LoadEntries(args, context.Config.ConnectorId);
        if (entries.Count == 0)
        {
            Dashboard.Line("Dead-letter queue is empty — nothing to retry.");
            return true;
        }
        Dashboard.Line($"{entries.Count} dead-lettered record(s) to retry.");

        var pipeline = await context.BuildPipelineAsync(ServiceStop.Token);
        var stillFailing = new List<(string ItemId, string Error, string ObjectType)>();
        var succeeded = 0;

        foreach (var entry in entries)
        {
            if (ServiceStop.Requested)
                break;
            var itemId = entry["item_id"]?.GetValue<string>() ?? string.Empty;
            var objectTypeName = entry["object_type"]?.GetValue<string>() ?? string.Empty;
            var objectConfig = context.Schema.FindObject(objectTypeName);
            if (objectConfig is null)
            {
                stillFailing.Add((itemId, $"Unknown object type '{objectTypeName}'", objectTypeName));
                continue;
            }

            try
            {
                var find = await context.Fetcher.FindByIdDetailedAsync(
                    objectConfig, itemId, ServiceStop.Token);
                if (find.Record is null)
                {
                    if (!ShouldDropMissing(find))
                    {
                        // The search skipped an oversize file, so the miss is NOT
                        // proof the record is gone — keep the entry for a later
                        // retry instead of silently dropping it.
                        Logger.Warning(
                            $"{itemId}: not found, but the lookup skipped an oversize file "
                            + "(> BDH_MAX_FILE_BYTES) — keeping the dead-letter entry.");
                        stillFailing.Add((itemId, IncompleteLookupError, objectConfig.ObjectName));
                        continue;
                    }
                    // Record gone from BDH (deleted upstream, or pruned out of the
                    // filtered partitions) — drop it from the queue.
                    Logger.Info($"{itemId}: no longer present in BDH; dropping from dead-letter.");
                    succeeded++;
                    continue;
                }
                var record = find.Record;
                var (ok, error) = await pipeline.IngestSingleAsync(
                    record, objectConfig, ServiceStop.Token);
                if (ok)
                {
                    succeeded++;
                }
                else
                {
                    stillFailing.Add((itemId, error ?? "unknown error", objectConfig.ObjectName));
                }
            }
            catch (Exception exc)
            {
                // Keep the entry in the queue (re-queued below with exc.Message),
                // but ALSO log the full exception — the queue entry only stores
                // the message, and for an unexpected type the stack is the only
                // way to tell a BDH read failure from a converter/Graph bug.
                Logger.Error(
                    $"retry-failed: retry crashed for item {itemId} "
                    + $"(object '{objectConfig.ObjectName}') — entry kept in the dead-letter queue",
                    exc);
                stillFailing.Add((itemId, exc.Message, objectConfig.ObjectName));
            }
        }

        Dashboard.Line($"Retry finished: {succeeded} succeeded, {stillFailing.Count} still failing.");

        if (stillFailing.Count == 0)
        {
            if (args.HasFlag("--clear-on-success"))
            {
                SyncState.ClearFailedRecords(context.Config.ConnectorId);
                Dashboard.Line("Dead-letter queue cleared.");
            }
            return true;
        }

        // Rewrite the queue with only the still-failing entries.
        SyncState.ClearFailedRecords(context.Config.ConnectorId);
        foreach (var group in stillFailing.GroupBy(f => f.ObjectType))
        {
            SyncState.AppendFailedRecords(
                context.Config.ConnectorId,
                group.Select(f => (f.ItemId, f.Error)).ToList(),
                group.Key);
        }
        return false;
    }

    private static List<JsonObject> LoadEntries(ParsedArgs args, string connectorId)
    {
        var file = args.GetString("--file");
        if (file is null)
            return SyncState.ReadFailedRecords(connectorId);

        var entries = new List<JsonObject>();
        foreach (var rawLine in SyncState.ReadLinesShared(file))
        {
            var line = rawLine.Trim();
            if (line.Length > 0)
                entries.Add(JsonNode.Parse(line)!.AsObject());
        }
        return entries;
    }
}
