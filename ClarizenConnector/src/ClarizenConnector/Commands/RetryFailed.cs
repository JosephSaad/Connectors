// Commands/RetryFailed.cs
// -----------------------
// `retry-failed [--file path] [--clear-on-success]` — re-ingest dead-lettered
// records. Each entry's record is re-fetched from Clarizen (fresh fields +
// fresh ACL) and re-PUT. With --clear-on-success the queue is cleared when
// EVERY retry succeeded; otherwise the queue is rewritten with only the
// still-failing entries.

using System.Text.Json;
using System.Text.Json.Nodes;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Commands;

public static class RetryFailed
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector");

    /// <summary>
    /// Parse "{ObjectType}_{LocalId}" (see <see cref="ClarizenRecord.ItemId"/>)
    /// back into its parts. Testable.
    /// <para>
    /// Splitting on the FIRST underscore was wrong: Clarizen entity type names
    /// may contain underscores (custom entities routinely do), so
    /// "Custom_Entity_1234567" parsed as ObjectType "Custom" / LocalId
    /// "Entity_1234567" and the retry re-fetched a record that does not exist.
    /// </para>
    /// <para>
    /// When the caller knows the object type — the dead-letter entry carries
    /// <c>object_type</c>, and it is resolved against the schema before we get
    /// here — the split is BOUNDED by that known name, which is exact even for a
    /// LocalId containing an underscore. Otherwise it falls back to the LAST
    /// underscore, correct for every id this connector emits (LocalId is the
    /// segment after the final '/' of a Clarizen entity id).
    /// </para>
    /// </summary>
    internal static (string ObjectType, string LocalId)? ParseItemId(
        string itemId, string? knownObjectType = null)
    {
        if (!string.IsNullOrEmpty(knownObjectType)
            && itemId.Length > knownObjectType.Length + 1
            && itemId.StartsWith(knownObjectType, StringComparison.OrdinalIgnoreCase)
            && itemId[knownObjectType.Length] == '_')
        {
            return (itemId[..knownObjectType.Length], itemId[(knownObjectType.Length + 1)..]);
        }

        var underscore = itemId.LastIndexOf('_');
        if (underscore <= 0 || underscore >= itemId.Length - 1)
            return null;
        return (itemId[..underscore], itemId[(underscore + 1)..]);
    }

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
            var objectTypeName = entry["object_type"]?.GetValue<string>()
                ?? ParseItemId(itemId)?.ObjectType
                ?? string.Empty;
            var objectConfig = context.Schema.FindObject(objectTypeName);
            // Re-parse bound by the RESOLVED object name: the schema is the
            // authority on where the type ends and the local id begins.
            if (objectConfig is null
                || ParseItemId(itemId, objectConfig.ObjectName) is not { } parsed)
            {
                stillFailing.Add((itemId, $"Unknown object type '{objectTypeName}'", objectTypeName));
                continue;
            }

            try
            {
                var row = await context.Clarizen.RetrieveAsync(
                    objectConfig, parsed.LocalId, ServiceStop.Token);
                if (row is null)
                {
                    // Record deleted upstream — drop it from the queue.
                    Logger.Info($"{itemId}: no longer exists in Clarizen; dropping from dead-letter.");
                    succeeded++;
                    continue;
                }
                var record = new ClarizenRecord(objectConfig.ObjectName, row);
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
                // Per-entry isolation: this record stays dead-lettered (with the
                // exception type preserved) and the loop continues. Log the full
                // exception so an operator can tell a transient fetch error from
                // a converter bug without re-running.
                Logger.Error(
                    $"retry-failed: {itemId} ({objectConfig.ObjectName}) retry attempt crashed — "
                    + "keeping it in the dead-letter queue",
                    exc);
                stillFailing.Add(
                    (itemId, $"[retry] {exc.GetType().Name}: {exc.Message}", objectConfig.ObjectName));
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

        // Same per-line isolation as SyncState.ReadFailedRecords: one torn or
        // corrupt line (crash mid-append) must not make every OTHER entry
        // unretryable — log it with the file + line number and keep going.
        var entries = new List<JsonObject>();
        var lineNumber = 0;
        foreach (var rawLine in File.ReadLines(file))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            try
            {
                if (JsonNode.Parse(line) is JsonObject entry)
                {
                    entries.Add(entry);
                }
                else
                {
                    Logger.Error(
                        $"retry-failed: '{file}' line {lineNumber} is valid JSON but not an "
                        + $"object — skipping it. Raw line: {line}");
                }
            }
            catch (JsonException exc)
            {
                Logger.Error(
                    $"retry-failed: '{file}' line {lineNumber} is not valid JSON "
                    + $"({exc.Message}) — skipping it. Raw line: {line}");
            }
        }
        return entries;
    }
}
