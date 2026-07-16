// Commands/SetupConnection.cs
// ---------------------------
// `setup-connection` — create/verify the Graph external connection and
// register the schema. No ingestion.

using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Commands;

public static class SetupConnection
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector");

    public static async Task<object?> RunAsync(ParsedArgs args)
    {
        using var context = Runtime.Create(args, "setup_connection");
        Dashboard.Banner("Setup connection");
        try
        {
            await context.ProvisionConnectionsAsync(ServiceStop.Token);
            Dashboard.Line(
                $"Connection '{context.Config.ConnectorId}' is provisioned and the schema is registered.");
            return true;
        }
        catch (Exception exc)
        {
            Logger.Error("setup-connection failed", exc);
            await Alerting.RaiseAsync("setup_failed", exc.Message);
            return false;
        }
    }
}
