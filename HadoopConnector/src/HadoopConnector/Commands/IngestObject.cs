// Commands/IngestObject.cs
// ------------------------
// `ingest-object --type X` — full ingestion of one object type.

using HadoopConnector.Infrastructure;

namespace HadoopConnector.Commands;

public static class IngestObject
{
    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector");

    public static async Task<object?> RunAsync(ParsedArgs args)
    {
        var objectType = args.GetString("--type")!;
        using var context = Runtime.Create(args, "ingest_object");
        Dashboard.Banner($"Ingest object: {objectType}");

        if (context.Schema.FindObject(objectType) is null)
        {
            Console.Error.WriteLine(
                $"error: object type '{objectType}' is not in config/schema.json "
                + $"(known: {string.Join(", ", context.Schema.ObjectList.Select(o => o.ObjectName))})");
            return false;
        }

        try
        {
            var pipeline = await context.BuildPipelineAsync(ServiceStop.Token);
            var summary = await pipeline.RunAsync(
                fullCrawl: true, onlyObjects: new[] { objectType }, ct: ServiceStop.Token);
            Dashboard.CrawlSummary($"Object '{objectType}'", summary);
            return summary.Failed == 0;
        }
        catch (Exception exc)
        {
            Logger.Error("ingest-object failed", exc);
            await Alerting.RaiseAsync("crawl_failed", exc.Message);
            return false;
        }
    }
}
