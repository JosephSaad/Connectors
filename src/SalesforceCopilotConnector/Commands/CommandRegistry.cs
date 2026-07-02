// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// CLI command modules for the Salesforce CRM Custom Connector.
//
// This namespace contains all subcommand implementations for ``Program.cs``.
// Each class exposes a single ``Cmd*`` method that the parser dispatches to.
//
// Modules
// -------
// Guide          Print the interactive setup and usage guide.
// Deploy         Full end-to-end deployment (connection → schema → ingestion).
// IngestCommand  Re-ingest items into an existing connection.
// IngestItem     Ingest a single Salesforce record by its ID.
// IngestObject   Ingest all records of a specific Salesforce object type.
//
// Shared utilities
// ----------------
// SetupLogging(prefix, verbose)
//     Configures the root logger with a timestamped file handler (always INFO+),
//     a console handler (INFO+ when *verbose* is true, WARNING+ otherwise),
//     and a 'progress' logger that always prints to console for key milestones.
//     Returns ``(logFile, summaryFile)``.
//
// WriteSummary(summaryFile, logFile, stats, connectionStatus, connectorId, elapsed, commandName)
//     Writes a run summary to *summaryFile* and prints it to the console via
//     the progress logger.
//
// BuildParser()
//     Constructs the ``ArgumentParser`` with all subcommands and the global
//     ``--verbose`` flag.  Called from ``Program.cs``.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Commands;

// ---------------------------------------------------------------------------
// Logging setup (shared by all commands) + CLI parser builder
// ---------------------------------------------------------------------------

public static class CommandRegistry
{
    public static readonly string LogsDir;

    static CommandRegistry()
    {
        LogsDir = Path.GetFullPath("logs");
        Directory.CreateDirectory(LogsDir);
    }

    private static readonly List<(LogHandler Handler, int Level)> ConsoleHandlers = new();

    /// <summary>
    /// Test hook — replaces <see cref="SetupLogging"/> when set
    /// (Python tests: <c>patch("commands.setup_logging")</c>). Null in production.
    /// </summary>
    internal static Func<string, bool, bool, (string LogFile, string SummaryFile)>? SetupLoggingOverride;

    /// <summary>
    /// Test hook — replaces <see cref="WriteSummary"/> when set
    /// (Python tests: <c>patch("commands.write_summary")</c>). Null in production.
    /// </summary>
    internal static Action<string, string, IngestionStats, string?, string, double, string>? WriteSummaryOverride;

    /// <summary>
    /// Configure logging: full INFO detail always goes to file.
    /// Console shows INFO+ when verbose=true, WARNING+ otherwise.
    /// A 'progress' logger always prints to console for key milestones.
    /// When <paramref name="dashboardMode"/> is true, console handlers are suppressed so
    /// that the live dashboard can render without interference.
    /// Returns (logFile, summaryFile).
    /// </summary>
    public static (string LogFile, string SummaryFile) SetupLogging(string prefix, bool verbose = false, bool dashboardMode = false)
    {
        if (SetupLoggingOverride is not null)
            return SetupLoggingOverride(prefix, verbose, dashboardMode);

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var runDir = Path.Combine(LogsDir, $"{prefix}_{timestamp}");
        Directory.CreateDirectory(runDir);
        var logFile = Path.Combine(runDir, $"{prefix}_{timestamp}.log");
        var summaryFile = Path.Combine(runDir, $"summary_{prefix}_{timestamp}.log");

        var fileHandler = new LineRotatingFileHandler(logFile)
        {
            Level = LogLevels.Info,
        };

        var consoleLevel = verbose ? LogLevels.Info : LogLevels.Warning;
        var consoleHandler = new StreamHandler(Console.Out)
        {
            Level = dashboardMode ? LogLevels.Critical + 1 : consoleLevel,
        };

        var root = Logging.Root;
        root.Level = LogLevels.Info;
        root.AddHandler(fileHandler);
        root.AddHandler(consoleHandler);

        // Progress logger — always visible on console for key milestones
        var progress = Logging.GetLoggerObject("progress");
        progress.Propagate = false;  // don't duplicate to root
        var progressConsole = new StreamHandler(Console.Out)
        {
            Level = dashboardMode ? LogLevels.Critical + 1 : LogLevels.Info,
            MessageOnly = true,
        };
        progress.AddHandler(progressConsole);
        progress.AddHandler(fileHandler);  // also goes to main log file

        // Store handlers so they can be restored after dashboard stops
        ConsoleHandlers.Clear();
        ConsoleHandlers.Add((consoleHandler, consoleLevel));
        ConsoleHandlers.Add((progressConsole, LogLevels.Info));

        // Retention pruning (LOG_RETENTION_DAYS, docs/SQL_CONTRACT.md): every
        // command — and every --continuous cycle, which re-enters SetupLogging —
        // prunes aged run dirs once at start. Runs after the handlers are wired
        // so its one-line summary lands in this run's log file. No-op by default.
        LogPruner.Prune();

        return (logFile, summaryFile);
    }

    /// <summary>Re-enable console handlers after dashboard mode ends.</summary>
    public static void RestoreConsoleLogging()
    {
        foreach (var (handler, level) in ConsoleHandlers)
            handler.Level = level;
    }

    /// <summary>Write summary to file and print to console.</summary>
    /// <summary>
    /// Fold a completed crawl's <see cref="IngestionStats"/> into the observability
    /// <see cref="Metrics"/> registry (surfaced on the <c>/metrics</c> endpoint). Counters
    /// are additive, so this accumulates correctly across shards and continuous cycles.
    /// A no-op in effect when no one scrapes the endpoint (env <c>HEALTH_PORT</c> unset).
    /// </summary>
    /// <summary>
    /// Start the observability HTTP endpoint (/health, /ready, /metrics) when
    /// <c>HEALTH_PORT</c> is set, and register the connector id for alert payloads.
    /// Returns an <see cref="IDisposable"/> the caller keeps for the command's lifetime,
    /// or <c>null</c> when the endpoint is disabled. Config-load failures are swallowed —
    /// the real command surfaces them. Never invoked in tests (HEALTH_PORT unset).
    /// </summary>
    public static IDisposable? MaybeStartHealthEndpoint()
    {
        var port = Environment.GetEnvironmentVariable("HEALTH_PORT");
        if (string.IsNullOrEmpty(port) || port.Trim() == "0")
            return null;
        try
        {
            var config = Settings.LoadConfig();
            Alerting.ConnectorId = config.Connector.Id;
            return HealthEndpoint.StartIfConfigured(config);
        }
        catch
        {
            return null;
        }
    }

    public static void RecordCrawlMetrics(IngestionStats stats)
    {
        Metrics.IncItemsIngested(stats.SuccessCount);
        Metrics.IncItemsFailed(stats.FailedCount);
        Metrics.IncItemsDeleted(stats.DeletedCount);
        Metrics.IncItemsSkipped(stats.SkippedCount);
        Metrics.IncCrawlsCompleted();
        Metrics.MarkCrawlCompletedNow();
        Metrics.SetDeadLetterDepth(stats.FailedCount);
    }

    public static void WriteSummary(
        string summaryFile,
        string logFile,
        IngestionStats stats,
        string? connectionStatus,
        string connectorId,
        double elapsed,
        string commandName)
    {
        if (WriteSummaryOverride is not null)
        {
            WriteSummaryOverride(summaryFile, logFile, stats, connectionStatus, connectorId, elapsed, commandName);
            return;
        }

        var progress = Logging.GetLogger("progress");
        var inv = CultureInfo.InvariantCulture;

        var lines = new List<string>();
        lines.Add("");
        lines.Add(new string('=', 60));
        lines.Add($"  RUN SUMMARY — {commandName}");
        lines.Add(new string('=', 60));
        lines.Add($"  Connector ID:     {connectorId}");
        if (!string.IsNullOrEmpty(connectionStatus))
            lines.Add($"  Connection:       {connectionStatus}");
        lines.Add($"  Records fetched:  {stats.TotalFetched}");
        if (stats.ObjectTypeCounts is { Count: > 0 })
        {
            foreach (var kv in stats.ObjectTypeCounts.OrderBy(k => k.Key, StringComparer.Ordinal))
                lines.Add($"    - {kv.Key}: {kv.Value}");
        }
        lines.Add($"  ACL engine:       {stats.AclEngine}");
        if (stats.AclFallbackUsed)
            lines.Add("  ACL fallback:     YES (public ACLs used due to error)");
        lines.Add($"  Ingested OK:      {stats.SuccessCount}");
        lines.Add($"  Deleted:          {stats.DeletedCount}");
        lines.Add($"  Failed:           {stats.FailedCount}");
        if (stats.FailedIds is { Count: > 0 })
        {
            lines.Add($"  Failed item IDs (first {stats.FailedIds.Count}):");
            foreach (var fid in stats.FailedIds)
                lines.Add($"    - {fid}");
            if (stats.FailedCount > stats.FailedIds.Count)
                lines.Add($"    ... and {stats.FailedCount - stats.FailedIds.Count} more");
            lines.Add("");
            lines.Add($"  >> Failed records: logs/failed_records_{connectorId}.jsonl");
        }
        lines.Add($"  Time elapsed:     {elapsed.ToString("F1", inv)}s");

        // Phase timing breakdown
        string[] phases = { "SF Fetch", "ACL", "Transform", "Graph Push" };
        if (stats.PhaseTimings is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("  Phase Timing (cumulative)");
            lines.Add("  " + new string('-', 56));
            var header = string.Format(inv, "  {0,-20}", "Object");
            foreach (var phase in phases)
                header += string.Format(inv, " {0,12}", phase);
            header += string.Format(inv, " {0,10}", "Total");
            lines.Add(header);
            lines.Add("  " + new string('-', 56));

            var grandPhase = phases.ToDictionary(p => p, _ => 0.0);
            foreach (var objType in stats.PhaseTimings.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var objTimings = stats.PhaseTimings[objType];
                var row = string.Format(inv, "  {0,-20}", objType);
                var rowTotal = 0.0;
                foreach (var phase in phases)
                {
                    if (objTimings.TryGetValue(phase, out var entry) && entry.Item1 > 0)
                    {
                        var (secs, count) = entry;
                        row += string.Format(inv, " {0,8:F1}s({1})", secs, count);
                        grandPhase[phase] += secs;
                        rowTotal += secs;
                    }
                    else
                    {
                        row += string.Format(inv, " {0,12}", "—");
                    }
                }
                row += string.Format(inv, " {0,8:F1}s", rowTotal);
                lines.Add(row);
            }

            lines.Add("  " + new string('-', 56));
            var totalsRow = string.Format(inv, "  {0,-20}", "TOTAL");
            var grandTotal = 0.0;
            foreach (var phase in phases)
            {
                var val = grandPhase[phase];
                totalsRow += string.Format(inv, " {0,9:F1}s   ", val);
                grandTotal += val;
            }
            totalsRow += string.Format(inv, " {0,8:F1}s", grandTotal);
            lines.Add(totalsRow);
            lines.Add("  " + new string('-', 56));
        }

        lines.Add($"  Full log:         {logFile}");
        lines.Add($"  Summary log:      {summaryFile}");
        lines.Add(new string('=', 60));

        var summaryText = string.Join("\n", lines);

        // Write to summary file
        File.WriteAllText(summaryFile, summaryText, new UTF8Encoding(false));

        // Print to console (always visible)
        progress.Info(summaryText);
    }

    /// <summary>
    /// Remove all handlers from the root and progress loggers.
    ///
    /// Must be called before <see cref="SetupLogging"/> when running multiple ingestion
    /// iterations in the same process (e.g. ``--continuous`` mode) so that each
    /// iteration writes to a fresh log file without duplicating output.
    /// </summary>
    public static void ResetLogging()
    {
        foreach (var loggerName in new[] { "", "progress" })
        {
            var lgr = Logging.GetLoggerObject(loggerName);
            foreach (var handler in lgr.Handlers)
            {
                handler.Close();
                lgr.RemoveHandler(handler);
            }
        }
    }

    /// <summary>Monotonic clock in seconds (Python <c>time.monotonic()</c>).</summary>
    public static double MonotonicSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>
    /// Equivalent of Python <c>dataclasses.replace(config, debug_item_id=..., debug_object_type=...)</c>:
    /// returns a copy of <paramref name="config"/> with the debug fields overridden.
    /// </summary>
    public static AppConfig ReplaceConfig(AppConfig config, string? debugItemId = null, string? debugObjectType = null) =>
        new AppConfig
        {
            ClientId = config.ClientId,
            TenantId = config.TenantId,
            Connector = config.Connector,
            RepoRoot = config.RepoRoot,
            Tuning = config.Tuning,
            SchemaConfig = config.SchemaConfig,
            OwdFieldMap = config.OwdFieldMap,
            ParentMap = config.ParentMap,
            OwdOverrides = config.OwdOverrides,
            ObjectNames = config.ObjectNames,
            UseNewAclEngine = config.UseNewAclEngine,
            UseGroupAcl = config.UseGroupAcl,
            UseEntityDefinitionOwd = config.UseEntityDefinitionOwd,
            DebugObjectType = debugObjectType ?? config.DebugObjectType,
            DebugItemId = debugItemId ?? config.DebugItemId,
        };

    /// <summary>
    /// Format a datetime like Python's <c>datetime.isoformat()</c>:
    /// microsecond precision only when non-zero, and a <c>+00:00</c> offset for UTC values.
    /// </summary>
    public static string PyIsoFormat(DateTime dt)
    {
        var result = dt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var fractionTicks = dt.Ticks % TimeSpan.TicksPerSecond;
        if (fractionTicks != 0)
        {
            var micros = fractionTicks / 10;
            result += "." + micros.ToString("D6", CultureInfo.InvariantCulture);
        }
        if (dt.Kind == DateTimeKind.Utc)
            result += "+00:00";
        return result;
    }

    // ---------------------------------------------------------------------------
    // CLI parser
    // ---------------------------------------------------------------------------

    /// <summary>Build and return the CLI argument parser for Program.cs.</summary>
    public static ArgumentParser BuildParser()
    {
        var parser = new ArgumentParser(
            prog: "run.py",
            description: "Salesforce CRM Custom Connector - Unified CLI",
            epilog:
                "Examples:\n" +
                "  python run.py guide\n" +
                "  python run.py full-deployment\n" +
                "  python run.py full-deployment --verbose\n" +
                "  python run.py full-deployment --continuous --full-crawl-hours 24 --incremental-hours 4\n" +
                "  python run.py ingest\n" +
                "  python run.py ingest --continuous --full-crawl-hours 48 --incremental-hours 6\n" +
                "  python run.py ingest-item --id 500f6000008iCNYAA2\n" +
                "  python run.py ingest-object --type Case\n");

        // guide
        parser.AddParser(
            "guide",
            "Show the complete setup and usage guide"
        ).SetDefaults(Guide.CmdGuide);

        // full-deployment
        var pDeploy = parser.AddParser(
            "full-deployment",
            "Deploy connection → schema → ingest items with ACLs");
        pDeploy.AddArgument(
            "--incremental",
            isFlag: true,
            help: "Start with an incremental crawl (since last successful full crawl) instead of a full crawl.");
        pDeploy.AddArgument(
            "--continuous",
            isFlag: true,
            help: "Keep running with scheduled full and incremental crawls.");
        pDeploy.AddArgument(
            "--full-crawl-hours",
            isInt: true,
            defaultValue: 24,
            help: "Full crawl interval in hours when --continuous is set (min 12, max 168). Default: 24.");
        pDeploy.AddArgument(
            "--incremental-hours",
            isInt: true,
            defaultValue: 4,
            help: "Incremental crawl interval in hours when --continuous is set (min 1, max 168). Default: 4.");
        pDeploy.AddArgument("--verbose", isFlag: true, help: "Print all INFO+ logs to console.");
        pDeploy.SetDefaults(Deploy.CmdFullDeploymentAsync);

        // ingest
        var pIngest = parser.AddParser(
            "ingest",
            "Ingest items only (connection & schema must already exist)");
        pIngest.AddArgument(
            "--incremental",
            isFlag: true,
            help: "Start with an incremental crawl (since last successful full crawl) instead of a full crawl.");
        pIngest.AddArgument(
            "--continuous",
            isFlag: true,
            help: "Keep running with scheduled full and incremental crawls.");
        pIngest.AddArgument(
            "--full-crawl-hours",
            isInt: true,
            defaultValue: 24,
            help: "Full crawl interval in hours when --continuous is set (min 12, max 168). Default: 24.");
        pIngest.AddArgument(
            "--incremental-hours",
            isInt: true,
            defaultValue: 4,
            help: "Incremental crawl interval in hours when --continuous is set (min 1, max 168). Default: 4.");
        pIngest.AddArgument("--verbose", isFlag: true, help: "Print all INFO+ logs to console.");
        pIngest.SetDefaults(IngestCommand.CmdIngestAsync);

        // ingest-item
        var pItem = parser.AddParser(
            "ingest-item",
            "Ingest a single Salesforce record by its ID");
        pItem.AddArgument(
            "--id",
            required: true,
            help: "Salesforce record ID (e.g. 500f6000008iCNYAA2)");
        pItem.AddArgument(
            "--object-type",
            help: "Salesforce object type (e.g. Account, Case). When provided, only this object is queried — dramatically faster.");
        pItem.AddArgument("--verbose", isFlag: true, help: "Print all INFO+ logs to console.");
        pItem.SetDefaults(IngestItem.CmdIngestItemAsync);

        // ingest-object
        var pObj = parser.AddParser(
            "ingest-object",
            "Ingest all records of a specific Salesforce object type");
        pObj.AddArgument(
            "--type",
            required: true,
            help: "Salesforce object type (e.g. Case, Account, Opportunity)");
        pObj.AddArgument("--verbose", isFlag: true, help: "Print all INFO+ logs to console.");
        pObj.SetDefaults(IngestObject.CmdIngestObjectAsync);

        // setup-connection
        var pSetup = parser.AddParser(
            "setup-connection",
            "Create/verify connection and register schema (no ingestion)");
        pSetup.AddArgument("--verbose", isFlag: true, help: "Print all INFO+ logs to console.");
        pSetup.SetDefaults(SetupConnection.CmdSetupConnectionAsync);

        // identity-dry-run
        var pIdentity = parser.AddParser(
            "identity-dry-run",
            "Preview identity crawl changes without calling Graph APIs");
        pIdentity.AddArgument(
            "--save",
            isFlag: true,
            help: "Write crawl results to the SQLite store (without calling Graph APIs).");
        pIdentity.AddArgument("--verbose", isFlag: true, help: "Print all INFO+ logs to console.");
        pIdentity.SetDefaults(IdentityDryRun.CmdIdentityDryRunAsync);

        // retry-failed
        var pRetry = parser.AddParser(
            "retry-failed",
            "Re-ingest every item recorded in the dead-letter failed_records file");
        pRetry.AddArgument(
            "--file",
            metavar: "PATH",
            help: "Path to the dead-letter JSONL file to retry (default: logs/failed_records_<connector_id>.jsonl).");
        pRetry.AddArgument(
            "--clear-on-success",
            isFlag: true,
            help: "Delete the dead-letter file after all retries succeed.");
        pRetry.AddArgument("--verbose", isFlag: true, help: "Print all INFO+ logs to console.");
        pRetry.SetDefaults(RetryFailed.CmdRetryFailedAsync);

        // validate-config (C#-side addition — preflight; not in the Python original)
        var pValidate = parser.AddParser(
            "validate-config",
            "Validate env, config files, schema shape, and connectivity (preflight)");
        pValidate.AddArgument("--strict", isFlag: true, help: "Treat connectivity/schema warnings as failures.");
        pValidate.AddArgument("--verbose", isFlag: true, help: "Print all INFO+ logs to console.");
        pValidate.SetDefaults(async a => await ValidateConfig.RunAsync(a));

        return parser;
    }
}

// ---------------------------------------------------------------------------
// argparse replication (hand-rolled, no external packages)
// ---------------------------------------------------------------------------

/// <summary>Thrown in place of argparse's <c>sys.exit()</c> (help → 0, usage error → 2).</summary>
public sealed class ArgumentParserExit : Exception
{
    public int Code { get; }

    public ArgumentParserExit(int code) : base($"exit {code}")
    {
        Code = code;
    }
}

/// <summary>One optional argument of a subcommand (argparse <c>add_argument</c>).</summary>
public sealed class ArgumentDef
{
    public required string OptionName { get; init; }
    public bool IsFlag { get; init; }
    public bool Required { get; init; }
    public bool IsInt { get; init; }
    public object? Default { get; init; }
    public string Help { get; init; } = "";
    public string? Metavar { get; init; }

    /// <summary>argparse dest: option name without leading dashes, dashes → underscores.</summary>
    public string Dest => OptionName.TrimStart('-').Replace('-', '_');

    public string EffectiveMetavar => Metavar ?? Dest.ToUpperInvariant();

    public string Invocation => IsFlag ? OptionName : $"{OptionName} {EffectiveMetavar}";

    public string UsagePart =>
        Required ? $"{OptionName} {EffectiveMetavar}"
        : IsFlag ? $"[{OptionName}]"
        : $"[{OptionName} {EffectiveMetavar}]";
}

/// <summary>Parsed argument values (the argparse <c>Namespace</c>).</summary>
public sealed class ParsedArgs
{
    private readonly Dictionary<string, object?> _values = new();

    public string? Command { get; internal set; }

    public Func<ParsedArgs, Task<bool?>>? Func { get; internal set; }

    internal void Set(string dest, object? value) => _values[dest] = value;

    public bool GetBool(string dest) => _values.TryGetValue(dest, out var v) && v is true;

    public string? GetString(string dest) => _values.TryGetValue(dest, out var v) ? v as string : null;

    public int GetInt(string dest, int fallback = 0) =>
        _values.TryGetValue(dest, out var v) && v is int i ? i : fallback;
}

/// <summary>One subcommand (argparse subparser).</summary>
public sealed class SubCommand
{
    public required string Name { get; init; }
    public required string HelpText { get; init; }
    public List<ArgumentDef> Arguments { get; } = new();
    public Func<ParsedArgs, Task<bool?>>? Handler { get; private set; }

    public SubCommand AddArgument(
        string optionName,
        bool isFlag = false,
        bool required = false,
        bool isInt = false,
        object? defaultValue = null,
        string help = "",
        string? metavar = null)
    {
        Arguments.Add(new ArgumentDef
        {
            OptionName = optionName,
            IsFlag = isFlag,
            Required = required,
            IsInt = isInt,
            Default = isFlag ? false : defaultValue,
            Help = help,
            Metavar = metavar,
        });
        return this;
    }

    public void SetDefaults(Func<ParsedArgs, Task<bool?>> handler) => Handler = handler;
}

/// <summary>
/// Hand-rolled replica of the <c>argparse.ArgumentParser</c> behaviour used by run.py:
/// subcommands, ``store_true`` flags, typed/required options with defaults, ``--opt=value``
/// syntax, unique prefix abbreviation, help output, and exit codes (0 for help, 2 for errors).
/// </summary>
public sealed class ArgumentParser
{
    public string Prog { get; }
    public string Description { get; }
    public string Epilog { get; }

    private readonly List<SubCommand> _subCommands = new();

    public ArgumentParser(string prog, string description, string epilog)
    {
        Prog = prog;
        Description = description;
        Epilog = epilog;
    }

    public SubCommand AddParser(string name, string help)
    {
        var sub = new SubCommand { Name = name, HelpText = help };
        _subCommands.Add(sub);
        return sub;
    }

    public ParsedArgs ParseArgs(string[] args)
    {
        var extras = new List<string>();
        SubCommand? sub = null;
        var i = 0;

        // Top level: only -h/--help is defined before the command positional.
        for (; i < args.Length; i++)
        {
            var tok = args[i];
            if (IsHelpToken(tok))
            {
                PrintHelp(Console.Out);
                throw new ArgumentParserExit(0);
            }
            if (tok.Length > 1 && tok[0] == '-' && !LooksLikeNegativeNumber(tok))
            {
                extras.Add(tok);
                continue;
            }
            sub = _subCommands.FirstOrDefault(s => s.Name == tok);
            if (sub == null)
            {
                var choices = string.Join(", ", _subCommands.Select(s => $"'{s.Name}'"));
                Error($"argument command: invalid choice: '{tok}' (choose from {choices})");
            }
            i++;
            break;
        }

        var parsed = new ParsedArgs();
        if (sub == null)
        {
            if (extras.Count > 0)
                Error($"unrecognized arguments: {string.Join(" ", extras)}");
            return parsed;  // no command given — Program prints help and exits 0
        }

        parsed.Command = sub.Name;
        parsed.Func = sub.Handler;
        foreach (var a in sub.Arguments)
            parsed.Set(a.Dest, a.Default);

        var seen = new HashSet<string>();
        for (; i < args.Length; i++)
        {
            var tok = args[i];
            string? inlineValue = null;
            var name = tok;
            if (tok.StartsWith("--", StringComparison.Ordinal) && tok.Contains('='))
            {
                var eq = tok.IndexOf('=');
                name = tok[..eq];
                inlineValue = tok[(eq + 1)..];
            }

            if (name == "-h")
            {
                Console.Out.Write(FormatSubHelp(sub));
                throw new ArgumentParserExit(0);
            }

            if (name.StartsWith("--", StringComparison.Ordinal) && name.Length > 2)
            {
                // Exact match wins, else unique prefix (argparse allow_abbrev=True).
                var optionNames = sub.Arguments.Select(a => a.OptionName).Append("--help").ToList();
                List<string> matches = optionNames.Contains(name)
                    ? new List<string> { name }
                    : optionNames.Where(n => n.StartsWith(name, StringComparison.Ordinal)).ToList();
                if (matches.Count == 0)
                {
                    extras.Add(tok);
                    continue;
                }
                if (matches.Count > 1)
                    SubError(sub, $"ambiguous option: {name} could match {string.Join(", ", matches)}");
                var matchName = matches[0];
                if (matchName == "--help")
                {
                    Console.Out.Write(FormatSubHelp(sub));
                    throw new ArgumentParserExit(0);
                }
                var arg = sub.Arguments.First(a => a.OptionName == matchName);
                if (arg.IsFlag)
                {
                    if (inlineValue != null)
                        SubError(sub, $"argument {arg.OptionName}: ignored explicit argument '{inlineValue}'");
                    parsed.Set(arg.Dest, true);
                }
                else
                {
                    var value = inlineValue;
                    if (value == null)
                    {
                        var hasNext = i + 1 < args.Length && LooksLikeValue(args[i + 1]);
                        if (!hasNext)
                            SubError(sub, $"argument {arg.OptionName}: expected one argument");
                        value = args[++i];
                    }
                    if (arg.IsInt)
                    {
                        if (!TryParsePyInt(value, out var intValue))
                            SubError(sub, $"argument {arg.OptionName}: invalid int value: '{value}'");
                        parsed.Set(arg.Dest, intValue);
                    }
                    else
                    {
                        parsed.Set(arg.Dest, value);
                    }
                }
                seen.Add(arg.Dest);
            }
            else
            {
                extras.Add(tok);
            }
        }

        var missing = sub.Arguments
            .Where(a => a.Required && !seen.Contains(a.Dest))
            .Select(a => a.OptionName)
            .ToList();
        if (missing.Count > 0)
            SubError(sub, $"the following arguments are required: {string.Join(", ", missing)}");

        // argparse reports leftover args with the top-level parser's usage.
        if (extras.Count > 0)
            Error($"unrecognized arguments: {string.Join(" ", extras)}");

        return parsed;
    }

    private static bool IsHelpToken(string tok) =>
        tok == "-h" || (tok.StartsWith("--", StringComparison.Ordinal) && tok.Length > 2 && "--help".StartsWith(tok, StringComparison.Ordinal));

    private static bool LooksLikeNegativeNumber(string tok)
    {
        if (tok.Length < 2 || tok[0] != '-')
            return false;
        var body = tok[1..];
        return body.All(char.IsDigit) ||
               (body.Contains('.') && body.Replace(".", "").Length > 0 && body.Replace(".", "").All(char.IsDigit) && body.Count(c => c == '.') == 1);
    }

    private static bool LooksLikeValue(string tok) =>
        !tok.StartsWith("-", StringComparison.Ordinal) || tok == "-" || LooksLikeNegativeNumber(tok);

    private static bool TryParsePyInt(string value, out int result) =>
        int.TryParse(value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);

    [DoesNotReturn]
    private void Error(string message)
    {
        Console.Error.Write(FormatUsage(GetWidth()));
        Console.Error.WriteLine($"{Prog}: error: {message}");
        throw new ArgumentParserExit(2);
    }

    [DoesNotReturn]
    private void SubError(SubCommand sub, string message)
    {
        Console.Error.Write(FormatSubUsage(sub, GetWidth()));
        Console.Error.WriteLine($"{Prog} {sub.Name}: error: {message}");
        throw new ArgumentParserExit(2);
    }

    // -- Help / usage formatting (argparse HelpFormatter approximation) --------

    private static int GetWidth()
    {
        var cols = Environment.GetEnvironmentVariable("COLUMNS");
        if (int.TryParse(cols, out var c) && c > 0)
            return c - 2;
        try
        {
            if (!Console.IsOutputRedirected && Console.WindowWidth > 0)
                return Console.WindowWidth - 2;
        }
        catch
        {
        }
        return 78;
    }

    private string FormatUsage(int width) =>
        WrapUsage($"usage: {Prog} ", new[] { "[-h]", "command", "..." }, width);

    private string FormatSubUsage(SubCommand sub, int width)
    {
        var parts = new List<string> { "[-h]" };
        parts.AddRange(sub.Arguments.Select(a => a.UsagePart));
        return WrapUsage($"usage: {Prog} {sub.Name} ", parts, width);
    }

    private static string WrapUsage(string prefix, IEnumerable<string> parts, int width)
    {
        var indent = new string(' ', prefix.Length);
        var lines = new List<string>();
        var current = prefix;
        var empty = true;
        foreach (var part in parts)
        {
            var candidate = empty ? current + part : current + " " + part;
            if (!empty && candidate.Length > width)
            {
                lines.Add(current);
                current = indent + part;
            }
            else
            {
                current = candidate;
            }
            empty = false;
        }
        lines.Add(current);
        return string.Join("\n", lines) + "\n";
    }

    public void PrintHelp(TextWriter writer) => writer.Write(FormatHelp());

    public string FormatHelp()
    {
        var width = GetWidth();
        var sb = new StringBuilder();
        sb.Append(FormatUsage(width));
        sb.Append('\n');
        sb.Append(Description).Append('\n');

        // argparse: help_position = min(max(len(invocation)) + current_indent(2) + 2, 24);
        // sub-action invocations are counted at their raw length.
        var invocationLengths = new List<int> { "-h, --help".Length, "command".Length };
        invocationLengths.AddRange(_subCommands.Select(s => s.Name.Length));
        var helpPosition = Math.Min(invocationLengths.Max() + 4, 24);

        sb.Append('\n').Append("positional arguments:\n");
        sb.Append(FormatItem(2, "command", "", helpPosition, width));
        foreach (var s in _subCommands)
            sb.Append(FormatItem(4, s.Name, s.HelpText, helpPosition, width));

        sb.Append('\n').Append("options:\n");
        sb.Append(FormatItem(2, "-h, --help", "show this help message and exit", helpPosition, width));

        if (!string.IsNullOrEmpty(Epilog))
        {
            sb.Append('\n');
            sb.Append(Epilog);
            if (!Epilog.EndsWith("\n", StringComparison.Ordinal))
                sb.Append('\n');
        }
        return sb.ToString();
    }

    public string FormatSubHelp(SubCommand sub)
    {
        var width = GetWidth();
        var sb = new StringBuilder();
        sb.Append(FormatSubUsage(sub, width));

        // argparse: help_position = min(max(len(invocation)) + current_indent(2) + 2, 24)
        var invocationLengths = new List<int> { "-h, --help".Length };
        invocationLengths.AddRange(sub.Arguments.Select(a => a.Invocation.Length));
        var helpPosition = Math.Min(invocationLengths.Max() + 4, 24);

        sb.Append('\n').Append("options:\n");
        sb.Append(FormatItem(2, "-h, --help", "show this help message and exit", helpPosition, width));
        foreach (var a in sub.Arguments)
            sb.Append(FormatItem(2, a.Invocation, a.Help, helpPosition, width));
        return sb.ToString();
    }

    private static string FormatItem(int indent, string header, string help, int helpPosition, int width)
    {
        if (string.IsNullOrEmpty(help))
            return new string(' ', indent) + header + "\n";

        var sb = new StringBuilder();
        var actionWidth = helpPosition - indent - 2;
        string firstPrefix;
        if (header.Length <= actionWidth)
        {
            firstPrefix = new string(' ', indent) + header.PadRight(actionWidth) + "  ";
        }
        else
        {
            sb.Append(new string(' ', indent)).Append(header).Append('\n');
            firstPrefix = new string(' ', helpPosition);
        }
        var helpWidth = Math.Max(width - helpPosition, 11);
        var wrapped = WrapText(help, helpWidth);
        sb.Append(firstPrefix).Append(wrapped[0]).Append('\n');
        var continuationIndent = new string(' ', helpPosition);
        for (var k = 1; k < wrapped.Count; k++)
            sb.Append(continuationIndent).Append(wrapped[k]).Append('\n');
        return sb.ToString();
    }

    private static List<string> WrapText(string text, int width)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = "";
        foreach (var word in words)
        {
            if (current.Length == 0)
                current = word;
            else if (current.Length + 1 + word.Length <= width)
                current += " " + word;
            else
            {
                lines.Add(current);
                current = word;
            }
        }
        if (current.Length > 0)
            lines.Add(current);
        if (lines.Count == 0)
            lines.Add("");
        return lines;
    }
}
