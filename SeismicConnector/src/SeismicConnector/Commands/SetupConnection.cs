// Commands/SetupConnection.cs — create/verify the connection and register the schema.

using System.Diagnostics;
using SeismicConnector.Infrastructure;

namespace SeismicConnector.Commands;

public static class SetupConnection
{
    public static async Task<object?> CmdSetupConnection(ParsedArgs args)
    {
        var (logFile, summaryFile) = CommandRegistry.SetupLogging("setup-connection", args.Verbose);
        var progress = Logging.GetLogger("progress");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var runtime = Runtime.Create();
            await Deploy.EnsureConnectionsAsync(runtime);
            var state = await runtime.Connection.GetConnectionStateAsync(ServiceStop.Token);
            CommandRegistry.WriteSummary(
                summaryFile, logFile, new Graph.IngestionStats(), state,
                runtime.Config.Connector.Id, stopwatch.Elapsed.TotalSeconds, "setup-connection");
            return true;
        }
        catch (Exception ex)
        {
            progress.Error($"setup-connection failed: {ex.Message}");
            await Alerting.RaiseAsync("setup_failed", ex.Message);
            return false;
        }
    }
}
