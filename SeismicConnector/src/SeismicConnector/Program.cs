// Seismic → Microsoft 365 Copilot Graph connector — unified CLI.
//
// Commands:
//     guide                Show the complete setup and usage guide
//     setup-connection     Create/verify connection and register schema (no ingestion)
//     full-deployment      Deploy connection → schema → identity → ingest with ACLs
//     ingest               Ingest items only (connection & schema must already exist)
//     ingest-object        Ingest all items of one schema object type
//     ingest-item          Ingest a single Seismic content item by id
//     retry-failed         Re-ingest items from the dead-letter queue
//     identity-dry-run     Preview (or --save) the Seismic → Entra identity mapping
//     validate-config      Preflight env/config/connectivity checks
//
// Global options:
//     --verbose            Print INFO logs to console (file always captures everything)
//
// Continuous mode (full-deployment and ingest only):
//     --continuous --full-crawl-hours N --incremental-hours N
//
// When started by the Windows Service Control Manager the requested command
// runs under the SCM-aware host (Infrastructure/ServiceHost.cs); a service
// stop finishes the in-flight chunk, flushes the pending Graph batch and
// saves the checkpoint before exiting.

using System.Text;
using Microsoft.Extensions.Hosting.WindowsServices;
using SeismicConnector.Commands;
using SeismicConnector.Infrastructure;

namespace SeismicConnector;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Install this connector's identity into the shared chassis before any
        // logging / event-log / ledger use, and give the chassis the connector's
        // correlation-id source. The three strings reproduce exactly what the
        // pre-extraction code hardcoded, so log/metric/event output is unchanged.
        Chassis.Init(new ChassisIdentity("seismic_connector", "SeismicConnector", "seismic_connector")
        {
            HomeEnvVar = "SEISMIC_CONNECTOR_HOME",
        });
        Chassis.CorrelationIdProvider = () => Tracing.CurrentCorrelationId;

        // Windows-service lifecycle → Event Log. These three lines were the
        // chassis ServiceHost's hardcoded defaults; they are this connector's
        // wording, so they moved out to the host when the other four connectors
        // adopted the shared host. Note "Service stopped" is OnStopped, not
        // OnFinished: it is emitted on every path, including an unhandled
        // exception, exactly as before.
        ServiceHost.OnStopRequested = () =>
            EventLogSink.Lifecycle("Service stop requested — finishing the current chunk.");
        ServiceHost.OnStarting = (args, workingDirectory) => EventLogSink.Lifecycle(
            $"Service started: {string.Join(" ", args)} (working directory: {workingDirectory})");
        ServiceHost.OnStopped = exitCode =>
            EventLogSink.Lifecycle($"Service stopped (exit code {exitCode}).");

        // The chassis HA coordinator records claim metrics through host hooks so
        // the chassis references no connector type; wire them to this connector's
        // metrics facade (byte-identical to the pre-facade in-registry behaviour).
        HaCoordinator.ClaimAcquiredHook = () =>
        {
            Metrics.IncHaClaimsAcquired();
            Metrics.AddHaClaimsHeld(1);
        };
        HaCoordinator.ClaimReleasedHook = () => Metrics.AddHaClaimsHeld(-1);

        if (WindowsServiceHelpers.IsWindowsService())
            return await ServiceHost.RunAsync(args, ExecuteAsync);

        return await ExecuteAsync(args);
    }

    /// <summary>Parse and run one CLI command; returns the process exit code.</summary>
    internal static async Task<int> ExecuteAsync(string[] args)
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch
        {
            // Redirected/limited consoles — best effort.
        }

        var parser = CommandRegistry.BuildParser();
        ParsedArgs parsedArgs;
        try
        {
            parsedArgs = parser.ParseArgs(args);
        }
        catch (ArgumentParserExit exit)
        {
            if (exit.Code != 0 && exit.Message.StartsWith("--", StringComparison.Ordinal))
                Console.Error.WriteLine($"seismic-connector: error: {exit.Message}");
            return exit.Code;
        }

        if (string.IsNullOrEmpty(parsedArgs.Command))
        {
            parser.PrintHelp(Console.Out);
            return 0;
        }

        try
        {
            var result = await parsedArgs.Func!(parsedArgs);
            if (result is bool success)
                return success ? 0 : 1;
            return 0;
        }
        catch (ArgumentParserExit exit)
        {
            if (exit.Code != 0)
                Console.Error.WriteLine($"seismic-connector: error: {exit.Message}");
            return exit.Code;
        }
        catch (Exception ex)
        {
            // Last-resort handler for exceptions that escape a command handler
            // (each command logs its own failure to its run log first). Emit the
            // FULL exception — type + message + stack — with a greppable prefix,
            // and exit nonzero. Console.Error because logging may not be
            // configured yet (e.g. a failure before SetupLogging).
            Console.Error.WriteLine($"seismic-connector: fatal: {ex}");
            return 1;
        }
    }
}
