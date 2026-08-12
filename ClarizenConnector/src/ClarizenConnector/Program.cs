// Clarizen (Planview AdaptiveWork) → Microsoft 365 Copilot Graph connector.
//
// Unified CLI — all operations are subcommands of this single entry point.
//
// Usage:
//     ClarizenConnector <command> [options] [--verbose]
//
// Commands:
//     guide               Show the complete setup and usage guide
//     setup-connection    Create/verify connection and register schema (no ingestion)
//     full-deployment     Deploy connection → schema → identity → ingest with ACLs
//     ingest              Ingest items only (connection & schema must already exist)
//     ingest-object       Ingest all records of one Clarizen object type
//     ingest-item         Ingest a single Clarizen record by id
//     retry-failed        Re-ingest dead-lettered records
//     identity-dry-run    Resolve Clarizen→Entra identities and report
//     validate-config     Preflight config/connectivity checks
//
// Continuous mode (full-deployment and ingest):
//     --continuous --full-crawl-hours 24 --incremental-hours 4
//
// When started by the Windows Service Control Manager the same CLI runs under
// an SCM-aware host with graceful stop (see Infrastructure/ServiceHost.cs).

using System.Text;
using ClarizenConnector.Commands;
using ClarizenConnector.Infrastructure;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace ClarizenConnector;

public static class Program
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector.cli");

    /// <summary>
    /// Point the shared chassis logging at Clarizen's identity, format and
    /// side-effects. R2: Logging lives in Connector.Chassis; the chassis cannot
    /// reference ClarizenConnector types, so the Clarizen host wires them here as
    /// hooks before the first log line — owner-only directory hardening, the
    /// Windows Event Log mirror, and the ambient correlation id. (Metrics is now
    /// a connector-side facade in ClarizenConnector.Infrastructure that drives the
    /// chassis MetricsRenderer directly, so no metrics wiring is needed here.)
    /// Idempotent: repeated calls re-assign the same delegates.
    /// </summary>
    private static void WireChassisLogging()
    {
        Connector.Chassis.Chassis.Init(
            new Connector.Chassis.ChassisIdentity(
                "clarizen_connector", EventLogSink.Source, "clarizen_connector"));
        Connector.Chassis.Chassis.CorrelationIdProvider = () => CorrelationContext.Current;
        Logging.HardenDirectoryHook = DirectoryHardening.EnsureOwnerOnly;
        Logging.EventLogMirrorHook = EventLogSink.Mirror;
        Logging.UseStandardMode();
    }

    public static async Task<int> Main(string[] args)
    {
        WireChassisLogging();

        if (WindowsServiceHelpers.IsWindowsService())
            return await ServiceHost.RunAsync(args, ExecuteAsync);

        return await ExecuteAsync(args);
    }

    /// <summary>Parse and run one CLI command; returns the process exit code.</summary>
    internal static async Task<int> ExecuteAsync(string[] args)
    {
        WireChassisLogging();

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
        catch (CliExit exit)
        {
            if (!string.IsNullOrEmpty(exit.Message))
                Console.Error.WriteLine(exit.Message);
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
        catch (CliExit exit)
        {
            if (!string.IsNullOrEmpty(exit.Message))
                Console.Error.WriteLine(exit.Message);
            return exit.Code;
        }
        catch (Exception ex)
        {
            // Final backstop: nothing above handled this. Emit one structured
            // ERROR naming the command with the full exception (type + stack) —
            // Logger.Error reaches stderr AND the run's connector.log/JSON sink,
            // so the failure is diagnosable from logs alone — and exit nonzero.
            Logger.Error($"Command '{parsedArgs.Command}' failed with an unhandled exception", ex);
            return 1;
        }
    }
}
