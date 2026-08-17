// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Salesforce CRM Custom Connector - Unified CLI
//
// All operations are available as subcommands of this single entry point.
//
// Usage:
//     run.py <command> [--verbose]
//
// Commands:
//     guide                    Show the complete setup and usage guide
//     setup-connection         Create/verify connection and register schema (no ingestion)
//     full-deployment          Deploy connection → schema → ingest items with ACLs
//     ingest                   Ingest items only (connection & schema must already exist)
//     ingest-item              Ingest a single Salesforce record by its ID
//     ingest-object            Ingest all records of a specific Salesforce object type
//
// Global options:
//     --verbose                Print all log levels (INFO, WARNING, ERROR) to console.
//                              Without this flag only WARNING and ERROR are shown on
//                              console; the log file always captures everything.
//
// Continuous mode (full-deployment and ingest only):
//     --continuous             Keep running with scheduled full and incremental crawls.
//     --full-crawl-hours <int> Full crawl interval in hours (min 12, max 168). Default: 24.
//     --incremental-hours <int> Incremental crawl interval in hours (min 1, max 168). Default: 4.
//
// Examples:
//     run.py guide
//     run.py setup-connection
//     run.py setup-connection --verbose
//     run.py full-deployment
//     run.py full-deployment --verbose
//     run.py full-deployment --continuous --full-crawl-hours 24 --incremental-hours 4
//     run.py ingest --verbose
//     run.py ingest --continuous
//     run.py ingest-item --id 001dN00000sh4neQAA
//     run.py ingest-item --id 001dN00000sh4neQAA --object-type Account
//     run.py ingest-object --type Case
//     run.py retry-failed
//     run.py retry-failed --file logs/failed_records_MyConnector.jsonl
//     run.py retry-failed --clear-on-success
//     run.py identity-dry-run --verbose
//     run.py identity-dry-run --save --verbose

using System.Text;
using Microsoft.Extensions.Hosting.WindowsServices;
using SalesforceCopilotConnector.Commands;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        WireChassis();

        // Started by the Windows Service Control Manager → run the requested
        // command under the shared SCM-aware host (Connector.Chassis.ServiceHost).
        // Always false when launched from a console or on non-Windows platforms.
        if (WindowsServiceHelpers.IsWindowsService())
        {
            // These two lines ran inside this connector's own ServiceHost before
            // it adopted the chassis host, and they must still run only in
            // service mode. Windows Event Log mirroring (EVENTLOG_ENABLED=true)
            // is attached before the host starts so service lifecycle records
            // reach the event log even though file/console logging is wired
            // later by SetupLogging; the lifecycle logger is raised to INFO so
            // those records pass the root level gate, which still defaults to
            // WARNING at this point.
            EventLogSink.AttachIfEnabled();
            Logging.GetLoggerObject("salesforce_connector.service").Level = LogLevels.Info;
            return await Connector.Chassis.ServiceHost.RunAsync(args, ExecuteAsync);
        }

        return await ExecuteAsync(args);
    }

    /// <summary>
    /// Install this connector's identity and logging into the shared chassis.
    /// </summary>
    /// <remarks>
    /// Must run before the first chassis type is touched: chassis components name
    /// their loggers from <c>Chassis.Identity</c> at type-load, so a later call
    /// would be ignored.
    ///
    /// The logger factory is the important part. Unlike the other connectors, this
    /// one keeps its own handler-based logging (a port of CPython's logging
    /// module), so chassis components are pointed back at it through
    /// ChassisLoggerAdapter. Without that, their log lines would go to the
    /// chassis's own sinks, which nothing here reads.
    /// </remarks>
    internal static void WireChassis()
    {
        // No ServiceHost lifecycle hooks: this connector's local ServiceHost
        // emitted no Windows Event Log entries for service start/stop, and the
        // shared host must not start emitting them on its behalf.
        Connector.Chassis.Chassis.Init(
            new Connector.Chassis.ChassisIdentity(
                "salesforce_connector", EventLogSink.SourceName, "salesforce_connector")
            {
                ServiceName = "SalesforceCopilotConnector",
                HomeEnvVar = "SFCONNECTOR_HOME",
            });
        // Alert webhooks keep this connector's own transport — its handler is the
        // richest on TLS in the fleet, and the chassis must not silently swap it.
        Connector.Chassis.Alerting.HandlerFactory = () => HttpClientFactory.CreateHandler();

        Connector.Chassis.Chassis.LoggerFactory =
            name => new ChassisLoggerAdapter(name, Logging.GetLogger(name));
    }

    /// <summary>Parse and run one CLI command; returns the process exit code.</summary>
    internal static async Task<int> ExecuteAsync(string[] args)
    {
        try
        {
            // Python writes the guide (and logs) through a UTF-8 stdout wrapper.
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
            return exit.Code;  // argparse: help → 0, usage error → 2
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
            return 0;  // commands returning None exit 0
        }
        catch (ArgumentParserExit exit)
        {
            return exit.Code;
        }
        catch (Exception ex)
        {
            // Mirror CPython: an unhandled exception prints a traceback and exits 1.
            // Additionally land the failure in the structured log (with the command
            // name and full stack) so operators troubleshooting from the log file
            // see the final error even when stderr was not captured.
            Logging.GetLogger("salesforce_connector").Error(
                $"Command '{parsedArgs.Command}' crashed with an unhandled exception", ex);
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
