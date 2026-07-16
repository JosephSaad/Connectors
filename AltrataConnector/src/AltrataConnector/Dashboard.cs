// Dashboard.cs
// ------------
// Spectre.Console live dashboard shown during crawls on an interactive
// console (skipped when output is redirected, LOG_FORMAT=json, or --verbose
// floods the console anyway). Ctrl+C requests the same graceful
// chunk-boundary stop as the Windows-service stop.

using AltrataConnector.Infrastructure;
using AltrataConnector.Ingestion;
using Spectre.Console;

namespace AltrataConnector;

public static class Dashboard
{
    public static bool Enabled =>
        !Console.IsOutputRedirected && !Logging.JsonFormat && !Logging.Verbose;

    /// <summary>Run a crawl with a live status table; falls back to plain awaiting.</summary>
    public static async Task<CrawlResult> RunAsync(CrawlEngine engine, Func<Task<CrawlResult>> crawl)
    {
        if (!Enabled)
            return await crawl();

        var started = DateTime.UtcNow;
        var crawlTask = crawl();

        await AnsiConsole.Live(Render(engine, started))
            .AutoClear(false)
            .StartAsync(async ctx =>
            {
                while (!crawlTask.IsCompleted)
                {
                    ctx.UpdateTarget(Render(engine, started));
                    await Task.WhenAny(crawlTask, Task.Delay(500));
                }
                ctx.UpdateTarget(Render(engine, started));
            });

        return await crawlTask;
    }

    private static Table Render(CrawlEngine engine, DateTime startedUtc)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Altrata Connector[/]");
        table.AddColumn("Metric");
        table.AddColumn("Value");
        table.AddRow("Delivery", Sanitize(engine.Progress.CurrentDelivery));
        table.AddRow("Dataset", Sanitize(engine.Progress.CurrentDataset));
        table.AddRow("Ingested", engine.Progress.Ingested.ToString());
        table.AddRow("Dead-lettered", engine.Progress.Failed.ToString());
        table.AddRow("Elapsed", (DateTime.UtcNow - startedUtc).ToString(@"hh\:mm\:ss"));
        table.AddRow("Stop", ServiceStop.Requested ? "[yellow]finishing chunk...[/]" : "Ctrl+C for graceful stop");
        return table;
    }

    private static string Sanitize(string text) =>
        string.IsNullOrEmpty(text) ? "-" : Markup.Escape(text);

    /// <summary>Wire Ctrl+C to the graceful chunk-boundary stop (idempotent).</summary>
    public static void HookCancelKey()
    {
        Console.CancelKeyPress += (_, e) =>
        {
            if (!ServiceStop.Requested)
            {
                e.Cancel = true;  // first Ctrl+C: graceful
                Logging.GetLogger("altrata_connector").Warning(
                    "Ctrl+C — finishing the current chunk and saving the checkpoint (press again to force quit)");
                ServiceStop.Request();
            }
            // second Ctrl+C: default behaviour (terminate)
        };
    }
}
