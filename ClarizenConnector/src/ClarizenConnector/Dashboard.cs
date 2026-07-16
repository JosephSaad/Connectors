// Dashboard.cs
// ------------
// Spectre.Console live status surface. Renders a banner per command, a live
// per-object progress line during ingestion, and a summary table at the end
// of a crawl. Falls back to plain lines when the console is redirected
// (service mode, CI) so log files stay clean.

using ClarizenConnector.Graph;
using Spectre.Console;

namespace ClarizenConnector;

public static class Dashboard
{
    private static readonly object Sync = new();

    /// <summary>Plain-text mode (no ANSI): service mode / redirected output / tests.</summary>
    internal static bool PlainMode { get; set; } = Console.IsOutputRedirected;

    public static void Banner(string title)
    {
        lock (Sync)
        {
            if (PlainMode)
            {
                Console.WriteLine($"=== {title} ===");
                return;
            }
            AnsiConsole.Write(new Rule($"[bold deepskyblue1]{Markup.Escape(title)}[/]").LeftJustified());
        }
    }

    public static void Line(string message)
    {
        lock (Sync)
        {
            if (PlainMode)
            {
                Console.WriteLine(message);
                return;
            }
            AnsiConsole.MarkupLine(Markup.Escape(message));
        }
    }

    /// <summary>IngestPipeline.OnProgress hook.</summary>
    public static void ReportProgress(string objectType, int done, int total)
    {
        lock (Sync)
        {
            if (PlainMode)
            {
                Console.WriteLine($"  {objectType}: {done}/{total}");
                return;
            }
            AnsiConsole.MarkupLine(
                $"  [grey]{Markup.Escape(objectType)}[/]: [green]{done}[/]/{total}");
        }
    }

    public static void CrawlSummary(string kind, IngestSummary summary)
    {
        lock (Sync)
        {
            if (PlainMode)
            {
                Console.WriteLine(
                    $"{kind} crawl summary: ingested={summary.Ingested} failed={summary.Failed} "
                    + $"deleted={summary.Deleted} skippedChunks={summary.SkippedChunks} "
                    + $"noAclSkipped={summary.NoAclSkipped}"
                    + (summary.Stopped ? " (stopped)" : string.Empty)
                    + (summary.QuotaExhausted ? " (API budget exhausted)" : string.Empty)
                    + (summary.Degraded ? " (DEGRADED — circuit open, checkpoint retained)" : string.Empty));
                foreach (var (objectType, count) in summary.PerObject)
                    Console.WriteLine($"  {objectType}: {count}");
                foreach (var objectType in summary.SweepSkipped)
                    Console.WriteLine($"  DELETION SWEEP SKIPPED (safety guard): {objectType}");
                return;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Object type");
            table.AddColumn(new TableColumn("Ingested").RightAligned());
            foreach (var (objectType, count) in summary.PerObject)
                table.AddRow(Markup.Escape(objectType), count.ToString());
            table.AddRow("[bold]Total[/]", $"[bold]{summary.Ingested}[/]");
            AnsiConsole.Write(table);

            var status = summary.Degraded
                ? "[yellow]paused — DEGRADED (circuit open, checkpoint retained)[/]"
                : summary.Stopped
                    ? "[yellow]stopped gracefully (checkpoint saved)[/]"
                    : summary.QuotaExhausted
                        ? "[yellow]paused — Clarizen API budget exhausted (checkpoint saved)[/]"
                        : "[green]completed[/]";
            AnsiConsole.MarkupLine(
                $"{Markup.Escape(kind)} crawl {status} — failed: {summary.Failed}, "
                + $"deleted: {summary.Deleted}, skipped chunks: {summary.SkippedChunks}, "
                + $"no-ACL skips: {summary.NoAclSkipped}");
            foreach (var objectType in summary.SweepSkipped)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Deletion sweep skipped (safety guard): {Markup.Escape(objectType)}[/]");
            }
        }
    }
}
