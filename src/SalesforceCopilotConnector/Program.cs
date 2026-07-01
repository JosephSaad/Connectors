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
using SalesforceCopilotConnector.Commands;

namespace SalesforceCopilotConnector;

public static class Program
{
    public static async Task<int> Main(string[] args)
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
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
