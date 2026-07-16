// Commands/IngestItem.cs
// ----------------------
// `ingest-item --id X [--object-type Y]` — ingest one Clarizen record.
// Ids of the form "/Task/1234567" carry their own object type; a bare id
// requires --object-type.

using ClarizenConnector.Clarizen;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Commands;

public static class IngestItem
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector");

    /// <summary>Infer the object type from a "/Type/NNN" id; null for bare ids. Testable.</summary>
    internal static string? InferObjectType(string id)
    {
        var trimmed = id.Trim().TrimStart('/');
        var slash = trimmed.IndexOf('/');
        return slash > 0 ? trimmed[..slash] : null;
    }

    public static async Task<object?> RunAsync(ParsedArgs args)
    {
        var id = args.GetString("--id")!;
        var objectType = args.GetString("--object-type") ?? InferObjectType(id);
        if (objectType is null)
        {
            Console.Error.WriteLine(
                "error: --object-type is required when --id is not of the form /Type/id");
            return false;
        }

        using var context = Runtime.Create(args, "ingest_item");
        Dashboard.Banner($"Ingest item: {id} ({objectType})");

        var objectConfig = context.Schema.FindObject(objectType);
        if (objectConfig is null)
        {
            Console.Error.WriteLine(
                $"error: object type '{objectType}' is not in config/schema.json");
            return false;
        }

        try
        {
            var row = await context.Clarizen.RetrieveAsync(objectConfig, id, ServiceStop.Token);
            if (row is null)
            {
                Console.Error.WriteLine($"error: record '{id}' not found in Clarizen");
                return false;
            }
            var record = new ClarizenRecord(objectConfig.ObjectName, row);
            var pipeline = await context.BuildPipelineAsync(ServiceStop.Token);
            var (ok, error) = await pipeline.IngestSingleAsync(record, objectConfig, ServiceStop.Token);
            if (ok)
            {
                Dashboard.Line($"Ingested {record.ItemId}.");
                return true;
            }
            Console.Error.WriteLine($"error: failed to ingest {record.ItemId}: {error}");
            return false;
        }
        catch (Exception exc)
        {
            Logger.Error("ingest-item failed", exc);
            return false;
        }
    }
}
