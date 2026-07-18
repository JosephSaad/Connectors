// Commands/IngestItem.cs
// ----------------------
// `ingest-item --id X --object-type Y` — ingest one BDH record by its
// Salesforce id. The record is located by scanning the object's partitions
// newest-first (partition filters apply; record predicates are bypassed —
// this is a targeted operator action).

using HadoopConnector.Hdfs;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Commands;

public static class IngestItem
{
    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector");

    public static async Task<object?> RunAsync(ParsedArgs args)
    {
        var id = args.GetString("--id")!;
        var objectType = args.GetString("--object-type");
        if (objectType is null)
        {
            Console.Error.WriteLine("error: --object-type is required (e.g. --object-type Contact)");
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
            var record = await context.Fetcher.FindByIdAsync(objectConfig, id, ServiceStop.Token);
            if (record is null)
            {
                Console.Error.WriteLine($"error: record '{id}' not found in BDH");
                return false;
            }
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
