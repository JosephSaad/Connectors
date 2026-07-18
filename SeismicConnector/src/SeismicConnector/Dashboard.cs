// Dashboard.cs
// ------------
// Spectre.Console live dashboard for --continuous mode. Renders crawl phase,
// counters and webhook queue depth once a second. Suppressed automatically
// when stdout is redirected (service mode / CI) so log output stays clean.

using Spectre.Console;
using SeismicConnector.Graph;
using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector;

public sealed class Dashboard : IDisposable
{
    private readonly IngestPipeline _pipeline;
    private readonly WebhookReceiver? _webhook;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;

    private Dashboard(IngestPipeline pipeline, WebhookReceiver? webhook)
    {
        _pipeline = pipeline;
        _webhook = webhook;
        _thread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "dashboard",
        };
        _thread.Start();
    }

    /// <summary>Start the live dashboard when running interactively; null otherwise.</summary>
    public static Dashboard? StartIfInteractive(IngestPipeline pipeline, WebhookReceiver? webhook)
    {
        if (Console.IsOutputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
            return null;
        return new Dashboard(pipeline, webhook);
    }

    private void RenderLoop()
    {
        try
        {
            AnsiConsole.Live(BuildTable())
                .AutoClear(false)
                .Start(ctx =>
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        ctx.UpdateTarget(BuildTable());
                        if (_cts.Token.WaitHandle.WaitOne(1000))
                            break;
                    }
                });
        }
        catch
        {
            // The dashboard is cosmetic — never let it break a crawl.
        }
    }

    private Table BuildTable()
    {
        var stats = _pipeline.Stats;
        var table = new Table()
            .Title("[bold]Seismic Copilot Connector[/]")
            .Border(TableBorder.Rounded)
            .AddColumn("Metric")
            .AddColumn(new TableColumn("Value").RightAligned());

        table.AddRow("Phase", Markup.Escape(_pipeline.Phase));
        table.AddRow("Ingested", stats.Ingested.ToString());
        table.AddRow("Failed", stats.Failed > 0 ? $"[red]{stats.Failed}[/]" : "0");
        table.AddRow("Skipped (unchanged)", stats.Skipped.ToString());
        table.AddRow("Excluded (No-MNE)", stats.Excluded > 0 ? $"[yellow]{stats.Excluded}[/]" : "0");
        table.AddRow("Withdrawn", stats.Withdrawn.ToString());
        table.AddRow("ACL-skipped", stats.AclSkipped.ToString());
        table.AddRow("Throttled (429)", Metrics.Throttle429Total.ToString());
        table.AddRow("Dead-letter depth", Metrics.DeadLetterDepth.ToString());
        if (_webhook is not null)
            table.AddRow("Webhook queue", _webhook.PendingCount.ToString());
        table.AddRow("Uptime", TimeSpan.FromSeconds((long)Metrics.UptimeSeconds).ToString());
        table.Caption("[grey]Ctrl+C: graceful stop (chunk finishes, checkpoint saved)[/]");
        return table;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _thread.Join(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Cosmetic teardown: a wedged render thread must not block or fail
            // process shutdown (it is a background thread either way).
        }
        _cts.Dispose();
    }
}
