// Commands/CommandRegistry.cs
// ---------------------------
// Builds the CLI parser, wires shared logging, and hosts the run-summary
// writer used by every command.

using System.Globalization;
using SeismicConnector.Graph;
using SeismicConnector.Infrastructure;

namespace SeismicConnector.Commands;

public static class CommandRegistry
{
    public static string LogsDir { get; internal set; } = Path.GetFullPath("logs");

    /// <summary>
    /// Configure logging: everything (INFO+) to a line-rotating file under
    /// logs/{prefix}_{timestamp}/; console shows INFO+ with --verbose, else
    /// WARNING+. A 'progress' logger always prints milestones to console.
    /// Returns (logFile, summaryFile).
    /// </summary>
    public static (string LogFile, string SummaryFile) SetupLogging(string prefix, bool verbose = false)
    {
        Directory.CreateDirectory(LogsDir);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var runDir = Path.Combine(LogsDir, $"{prefix}_{timestamp}");
        Directory.CreateDirectory(runDir);
        var logFile = Path.Combine(runDir, $"{prefix}_{timestamp}.log");
        var summaryFile = Path.Combine(runDir, $"summary_{prefix}_{timestamp}.log");

        var fileHandler = new LineRotatingFileHandler(logFile) { Level = LogLevels.Info };
        var consoleHandler = new StreamHandler(Console.Out)
        {
            Level = verbose ? LogLevels.Info : LogLevels.Warning,
        };

        var root = Logging.Root;
        foreach (var handler in root.Handlers)
        {
            handler.Close();
            root.RemoveHandler(handler);
        }
        root.Level = LogLevels.Info;
        root.AddHandler(fileHandler);
        root.AddHandler(consoleHandler);

        var progress = Logging.GetLoggerObject("progress");
        progress.Propagate = false;
        foreach (var handler in progress.Handlers)
            progress.RemoveHandler(handler);
        progress.AddHandler(new StreamHandler(Console.Out)
        {
            Level = LogLevels.Info,
            MessageOnly = true,
        });
        progress.AddHandler(fileHandler);

        // Retention pruning runs at the start of every command (docs/OBSERVABILITY.md).
        LogPruner.Prune(LogsDir);

        return (logFile, summaryFile);
    }

    /// <summary>Write and print the end-of-run summary.</summary>
    public static void WriteSummary(
        string summaryFile,
        string logFile,
        IngestionStats stats,
        string? connectionStatus,
        string connectorId,
        double elapsedSeconds,
        string commandName,
        string? reconciliationFile = null)
    {
        var lines = new List<string>
        {
            "============================================================",
            $"  {commandName} — run summary",
            "============================================================",
            $"  Connector:        {connectorId}",
            $"  Connection:       {connectionStatus ?? "n/a"}",
            $"  Elapsed:          {elapsedSeconds.ToString("F1", CultureInfo.InvariantCulture)}s",
            $"  Items ingested:   {stats.Ingested}",
            $"  Items failed:     {stats.Failed}",
            $"  Items skipped:    {stats.Skipped}",
            $"  Items excluded:   {stats.Excluded}   (No-MNE filter)",
            $"  Items withdrawn:  {stats.Withdrawn}",
            $"  ACL-skipped:      {stats.AclSkipped}",
            $"  Re-ACLed:         {stats.ReAcled}   (ACL drift: {stats.AclDrift})",
        };
        if (reconciliationFile is not null)
            lines.Add($"  Reconciliation:   {reconciliationFile}");
        lines.Add($"  Log file:         {logFile}");
        lines.Add("============================================================");

        var text = string.Join(Environment.NewLine, lines);
        try
        {
            File.WriteAllText(summaryFile, text + Environment.NewLine);
        }
        catch (Exception exc)
        {
            Logging.GetLogger("seismic_connector").Warning($"Could not write summary file: {exc.Message}");
        }
        Logging.GetLogger("progress").Info(text);
    }

    public static ArgumentParser BuildParser()
    {
        var parser = new ArgumentParser(
            "seismic-connector",
            "Seismic sales-enablement content → Microsoft 365 Copilot Graph connector.");

        parser.AddCommand(new CommandSpec
        {
            Name = "guide",
            Help = "Show the complete setup and usage guide",
            Handler = args => Guide.CmdGuide(args),
        });

        parser.AddCommand(new CommandSpec
        {
            Name = "setup-connection",
            Help = "Create/verify the Graph connection and register the schema (no ingestion)",
            Handler = args => SetupConnection.CmdSetupConnection(args),
        });

        parser.AddCommand(new CommandSpec
        {
            Name = "full-deployment",
            Help = "Deploy connection → schema → identity crawl → ingest with ACLs",
            Options = ContinuousOptions(),
            Handler = args => Deploy.CmdFullDeployment(args),
        });

        parser.AddCommand(new CommandSpec
        {
            Name = "ingest",
            Help = "Ingest items only (connection & schema must already exist)",
            Options = ContinuousOptions(),
            Handler = args => IngestCommands.CmdIngest(args),
        });

        parser.AddCommand(new CommandSpec
        {
            Name = "ingest-object",
            Help = "Ingest all items of one schema object type (ContentItem or Library)",
            Options = new List<OptionSpec>
            {
                new() { Name = "--type", TakesValue = true, Required = true, Help = "object type from config/schema.json" },
            },
            Handler = args => IngestCommands.CmdIngestObject(args),
        });

        parser.AddCommand(new CommandSpec
        {
            Name = "ingest-item",
            Help = "Ingest a single Seismic content item by its id",
            Options = new List<OptionSpec>
            {
                new() { Name = "--id", TakesValue = true, Required = true, Help = "Seismic content id" },
                new() { Name = "--teamsite", TakesValue = true, Help = "teamsite id (skips the lookup scan)" },
            },
            Handler = args => IngestCommands.CmdIngestItem(args),
        });

        parser.AddCommand(new CommandSpec
        {
            Name = "retry-failed",
            Help = "Re-ingest items from the dead-letter queue",
            Options = new List<OptionSpec>
            {
                new() { Name = "--file", TakesValue = true, Help = "explicit dead-letter JSONL path" },
                new() { Name = "--clear-on-success", Help = "clear the dead-letter queue when every retry succeeds" },
            },
            Handler = args => RetryFailed.CmdRetryFailed(args),
        });

        parser.AddCommand(new CommandSpec
        {
            Name = "reconcile",
            Help = "Full-inventory drift sweep: diff Seismic vs the index; --repair converges them",
            Options = new List<OptionSpec>
            {
                new() { Name = "--repair", Help = "withdraw orphaned/excluded items and (re-)ingest missing/superseded ones" },
            },
            Handler = args => Reconcile.CmdReconcile(args),
        });

        parser.AddCommand(new CommandSpec
        {
            Name = "reacl",
            Help = "Permission-change re-ACL: refresh ACLs where effective permissions drifted; --dry-run audits only",
            Options = new List<OptionSpec>
            {
                new() { Name = "--dry-run", Help = "report drift counts without changing any ACL" },
            },
            Handler = args => ReAcl.CmdReAcl(args),
        });

        parser.AddCommand(new CommandSpec
        {
            Name = "identity-dry-run",
            Help = "Run the Seismic → Entra identity crawl without persisting (add --save to persist)",
            Options = new List<OptionSpec>
            {
                new() { Name = "--save", Help = "persist the resolved mappings to the identity store" },
            },
            Handler = args => IdentityDryRun.CmdIdentityDryRun(args),
        });

        parser.AddCommand(new CommandSpec
        {
            Name = "validate-config",
            Help = "Preflight: env vars, config files, and best-effort Seismic + Graph connectivity",
            Options = new List<OptionSpec>
            {
                new() { Name = "--strict", Help = "treat connectivity warnings as failures" },
            },
            Handler = args => ValidateConfig.CmdValidateConfig(args),
        });

        return parser;
    }

    private static List<OptionSpec> ContinuousOptions() => new()
    {
        new OptionSpec { Name = "--continuous", Help = "keep running with scheduled full + incremental crawls" },
        new OptionSpec { Name = "--full-crawl-hours", TakesValue = true, Help = "full crawl interval in hours (12-168, default 24)" },
        new OptionSpec { Name = "--incremental-hours", TakesValue = true, Help = "incremental crawl interval in hours (1-168, default 4)" },
    };

    /// <summary>Validate the continuous-mode intervals (shared by full-deployment / ingest).</summary>
    internal static (int FullCrawlHours, int IncrementalHours) ReadContinuousIntervals(ParsedArgs args)
    {
        var full = args.GetInt("full-crawl-hours") ?? 24;
        var incremental = args.GetInt("incremental-hours") ?? 4;
        if (full is < 12 or > 168)
            throw new ArgumentParserExit(2, "--full-crawl-hours must be between 12 and 168");
        if (incremental is < 1 or > 168)
            throw new ArgumentParserExit(2, "--incremental-hours must be between 1 and 168");
        return (full, incremental);
    }
}
