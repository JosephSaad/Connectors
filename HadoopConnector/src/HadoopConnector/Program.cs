// BDH (Hadoop data mart, Salesforce-shaped) → Microsoft 365 Copilot Graph connector.
//
// Unified CLI — all operations are subcommands of this single entry point.
//
// Usage:
//     HadoopConnector <command> [options] [--verbose]
//
// Commands:
//     guide               Show the complete setup and usage guide
//     setup-connection    Create/verify connection and register schema (no ingestion)
//     full-deployment     Deploy connection → schema → identity → ingest with ACLs
//     ingest              Ingest items only (connection & schema must already exist)
//     ingest-object       Ingest all records of one BDH object type
//     ingest-item         Ingest a single BDH record by Salesforce id
//     retry-failed        Re-ingest dead-lettered records
//     identity-dry-run    Resolve BDH→Entra identities and report
//     validate-config     Preflight config/connectivity checks
//
// Continuous mode (full-deployment and ingest):
//     --continuous --full-crawl-hours 24 --incremental-hours 4
//
// When started by the Windows Service Control Manager the same CLI runs under
// an SCM-aware host with graceful stop (see Infrastructure/ServiceHost.cs).

using System.Text;
using HadoopConnector.Commands;
using HadoopConnector.Infrastructure;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace HadoopConnector;

public static class Program
{
    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector");

    public static async Task<int> Main(string[] args)
    {
        WireChassisLogging();

        if (WindowsServiceHelpers.IsWindowsService())
            return await ServiceHost.RunAsync(args, ExecuteAsync);

        return await ExecuteAsync(args);
    }

    /// <summary>Wire the shared-chassis logging identity and hooks. Logging lives
    /// in Connector.Chassis and cannot reference connector types, so the host
    /// supplies its run-dir hardening, Event Log mirror and correlation source
    /// and selects the standard (run-directory) text/json format. Mirrored by the
    /// test bootstrap so tests and production behave identically.</summary>
    private static void WireChassisLogging()
    {
        Connector.Chassis.Chassis.Init(
            new Connector.Chassis.ChassisIdentity(
                "hadoop_connector", EventLogSink.SourceName, "hadoop_connector"));
        Connector.Chassis.Chassis.CorrelationIdProvider = () => CorrelationContext.Current;
        Logging.HardenDirectoryHook = SecureDirectories.CreateHardened;
        Logging.EventLogMirrorHook = EventLogSink.Mirror;
        // This connector keeps its own transport for alert webhooks: four
        // connectors have four CreateHandler implementations, all reading
        // PROXY_URL and CA_BUNDLE_PATH, and picking a winner would change which
        // TLS and proxy behaviour the others get. The chassis owns the alerting
        // logic; the host keeps the transport it was hardened with.
        Connector.Chassis.Alerting.HandlerFactory = HttpTransport.CreateHandler;

        Logging.UseStandardMode();
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
            // Final backstop for anything a command let escape (typically a
            // config/auth error thrown before the command's own try/catch, e.g.
            // from Runtime.Create). Log a structured record — command, args,
            // exception WITH stack — so the failure is diagnosable from the run
            // log alone, then mirror it to stderr and exit nonzero.
            Logger.Error(
                $"Command '{parsedArgs.Command}' failed with an unhandled exception "
                + $"(args: {SummarizeArgs(args)})",
                ex);
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    /// <summary>Space-joined argv for the final error log. Argument VALUES are
    /// included — the CLI takes no secrets on the command line (all secrets are
    /// SECRET_* env vars / Key Vault), so this cannot leak credentials.</summary>
    internal static string SummarizeArgs(IReadOnlyList<string> args) =>
        args.Count == 0 ? "(none)" : string.Join(' ', args);
}
