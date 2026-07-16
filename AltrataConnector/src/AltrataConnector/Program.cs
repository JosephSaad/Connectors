// Altrata Copilot Connector — unified CLI
//
// Usage:
//     altrata-connector <command> [options] [--verbose]
//
// Commands:
//     guide              Show the complete setup and usage guide
//     setup-connection   Create/verify connection and register schema
//     full-deployment    Connection → schema → full crawl (supports --continuous)
//     ingest             Crawl the feed directory (--incremental for new deliveries)
//     ingest-object      Ingest one dataset:  --type WealthIndicator
//     ingest-item        Billable API lookup: --id P1 --purpose "..." [--requested-by u]
//     retry-failed       Replay the dead-letter queue [--clear-on-success]
//     identity-dry-run   Preview seats/contacts/crosswalk [--save]
//     seat-sync          Refresh seats; changed list → re-ACL pass
//     validate-config    Preflight checks [--strict]
//     purge-all          License-end purge (dry run without --confirm)

using System.Text;
using Microsoft.Extensions.Hosting.WindowsServices;
using AltrataConnector.Commands;
using AltrataConnector.Infrastructure;

namespace AltrataConnector;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Started by the Windows Service Control Manager → run under the
        // SCM-aware host (Infrastructure/ServiceHost.cs). Always false when
        // launched from a console or on non-Windows platforms.
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
            return exit.Code;  // help → 0, usage error → 2
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
            return exit.Code;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
