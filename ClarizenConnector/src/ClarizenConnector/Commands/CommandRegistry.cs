// Commands/CommandRegistry.cs
// ---------------------------
// Hand-rolled CLI parser + command registry (same style as the Salesforce
// connector's argparse replica): subcommands with long options, --help at
// both levels, exit code 0 for help and 2 for usage errors.

using System.Globalization;
using System.Text;

namespace ClarizenConnector.Commands;

/// <summary>Thrown to unwind parsing with a process exit code (help → 0, usage error → 2).</summary>
public sealed class CliExit : Exception
{
    public CliExit(int code, string? message = null) : base(message ?? string.Empty) => Code = code;

    public int Code { get; }
}

public sealed class OptionSpec
{
    public required string Name { get; init; }          // e.g. "--type"
    public bool TakesValue { get; init; }
    public bool Required { get; init; }
    public string Description { get; init; } = string.Empty;
    public string? ValueName { get; init; }
}

public sealed class CommandSpec
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public List<OptionSpec> Options { get; init; } = new();
    public required Func<ParsedArgs, Task<object?>> Handler { get; init; }
}

public sealed class ParsedArgs
{
    public string Command { get; init; } = string.Empty;
    public bool Verbose { get; init; }
    public Dictionary<string, string?> Options { get; init; } = new(StringComparer.Ordinal);
    public Func<ParsedArgs, Task<object?>>? Func { get; init; }

    public bool HasFlag(string name) => Options.ContainsKey(name);

    public string? GetString(string name) => Options.GetValueOrDefault(name);

    public int GetInt(string name, int defaultValue)
    {
        var raw = GetString(name);
        if (raw is null)
            return defaultValue;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            throw new CliExit(2, $"error: argument {name}: invalid int value: '{raw}'");
        return parsed;
    }
}

public sealed class CliParser
{
    public const string Prog = "ClarizenConnector";

    private readonly List<CommandSpec> _commands;

    public CliParser(List<CommandSpec> commands) => _commands = commands;

    public IReadOnlyList<CommandSpec> Commands => _commands;

    public ParsedArgs ParseArgs(string[] args)
    {
        if (args.Length == 0)
            return new ParsedArgs();

        if (args[0] is "-h" or "--help")
        {
            PrintHelp(Console.Out);
            throw new CliExit(0);
        }

        var commandName = args[0];
        var spec = _commands.FirstOrDefault(c =>
            string.Equals(c.Name, commandName, StringComparison.Ordinal));
        if (spec is null)
        {
            Console.Error.WriteLine(
                $"usage: {Prog} <command> [options]\n{Prog}: error: unknown command '{commandName}' "
                + $"(choose from {string.Join(", ", _commands.Select(c => c.Name))})");
            throw new CliExit(2);
        }

        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        var verbose = false;
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
            {
                PrintCommandHelp(Console.Out, spec);
                throw new CliExit(0);
            }
            if (arg == "--verbose")
            {
                verbose = true;
                continue;
            }

            // --name=value form
            string name = arg;
            string? inlineValue = null;
            var eq = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && eq > 0)
            {
                name = arg[..eq];
                inlineValue = arg[(eq + 1)..];
            }

            var option = spec.Options.FirstOrDefault(o =>
                string.Equals(o.Name, name, StringComparison.Ordinal));
            if (option is null)
            {
                Console.Error.WriteLine(
                    $"usage: {Prog} {spec.Name} [options]\n{Prog} {spec.Name}: error: unrecognized argument: {arg}");
                throw new CliExit(2);
            }

            if (!option.TakesValue)
            {
                if (inlineValue is not null)
                {
                    Console.Error.WriteLine(
                        $"{Prog} {spec.Name}: error: argument {option.Name} does not take a value");
                    throw new CliExit(2);
                }
                options[option.Name] = null;
                continue;
            }

            string? value = inlineValue;
            if (value is null)
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine(
                        $"{Prog} {spec.Name}: error: argument {option.Name}: expected one argument");
                    throw new CliExit(2);
                }
                value = args[++i];
            }
            options[option.Name] = value;
        }

        foreach (var option in spec.Options.Where(o => o.Required))
        {
            if (!options.ContainsKey(option.Name))
            {
                Console.Error.WriteLine(
                    $"{Prog} {spec.Name}: error: the following arguments are required: {option.Name}");
                throw new CliExit(2);
            }
        }

        return new ParsedArgs
        {
            Command = spec.Name,
            Verbose = verbose,
            Options = options,
            Func = spec.Handler,
        };
    }

    public void PrintHelp(TextWriter writer)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"usage: {Prog} <command> [options] [--verbose]");
        sb.AppendLine();
        sb.AppendLine("Clarizen (Planview AdaptiveWork) → Microsoft 365 Copilot Graph connector.");
        sb.AppendLine();
        sb.AppendLine("commands:");
        foreach (var command in _commands)
            sb.AppendLine($"  {command.Name,-18} {command.Description}");
        sb.AppendLine();
        sb.AppendLine("global options:");
        sb.AppendLine("  --verbose          Print all log levels to the console (default: WARNING+).");
        sb.AppendLine();
        sb.AppendLine($"Run '{Prog} <command> --help' for command-specific options.");
        writer.Write(sb.ToString());
    }

    public static void PrintCommandHelp(TextWriter writer, CommandSpec spec)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"usage: {Prog} {spec.Name} [options]");
        sb.AppendLine();
        sb.AppendLine(spec.Description);
        if (spec.Options.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("options:");
            foreach (var option in spec.Options)
            {
                var left = option.TakesValue
                    ? $"{option.Name} <{option.ValueName ?? "value"}>"
                    : option.Name;
                sb.AppendLine($"  {left,-32} {option.Description}{(option.Required ? " (required)" : string.Empty)}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("  --verbose                        Print all log levels to the console.");
        writer.Write(sb.ToString());
    }
}

public static class CommandRegistry
{
    public static CliParser BuildParser() => new(new List<CommandSpec>
    {
        new()
        {
            Name = "guide",
            Description = "Show the complete setup and usage guide.",
            Handler = Guide.RunAsync,
        },
        new()
        {
            Name = "setup-connection",
            Description = "Create/verify the Graph external connection and register the schema (no ingestion).",
            Handler = SetupConnection.RunAsync,
        },
        new()
        {
            Name = "full-deployment",
            Description = "Deploy connection → schema → identity sync → ingest items with ACLs.",
            Options = ContinuousOptions(),
            Handler = Deploy.RunFullDeploymentAsync,
        },
        new()
        {
            Name = "ingest",
            Description = "Ingest items only (connection & schema must already exist).",
            Options = ContinuousOptions(),
            Handler = Deploy.RunIngestAsync,
        },
        new()
        {
            Name = "ingest-object",
            Description = "Ingest all records of a specific Clarizen object type.",
            Options = new List<OptionSpec>
            {
                new() { Name = "--type", TakesValue = true, Required = true, ValueName = "objectType",
                        Description = "Clarizen object type from config/schema.json (e.g. Project, Task)." },
            },
            Handler = IngestObject.RunAsync,
        },
        new()
        {
            Name = "ingest-item",
            Description = "Ingest a single Clarizen record by its id.",
            Options = new List<OptionSpec>
            {
                new() { Name = "--id", TakesValue = true, Required = true, ValueName = "id",
                        Description = "Clarizen entity id (e.g. /Task/1234567 or 1234567)." },
                new() { Name = "--object-type", TakesValue = true, ValueName = "objectType",
                        Description = "Object type; inferred from a /Type/id form id when omitted." },
            },
            Handler = IngestItem.RunAsync,
        },
        new()
        {
            Name = "retry-failed",
            Description = "Re-ingest records from the dead-letter queue.",
            Options = new List<OptionSpec>
            {
                new() { Name = "--file", TakesValue = true, ValueName = "path",
                        Description = "Dead-letter JSONL file (default: logs/failed_records_<CONNECTOR_ID>.jsonl)." },
                new() { Name = "--clear-on-success", TakesValue = false,
                        Description = "Clear the dead-letter queue when every retry succeeds." },
            },
            Handler = RetryFailed.RunAsync,
        },
        new()
        {
            Name = "reconcile",
            Description = "Report (and with --fix, repair) index-vs-source drift using the ingested-item inventory.",
            Options = new List<OptionSpec>
            {
                new() { Name = "--type", TakesValue = true, ValueName = "objectType",
                        Description = "Restrict reconciliation to one object type." },
                new() { Name = "--fix", TakesValue = false,
                        Description = "DELETE stale items (indexed but gone from Clarizen) from the connection." },
            },
            Handler = Reconcile.RunAsync,
        },
        new()
        {
            Name = "identity-dry-run",
            Description = "Resolve Clarizen users/groups to Entra identities and report, without ingesting.",
            Options = new List<OptionSpec>
            {
                new() { Name = "--save", TakesValue = false,
                        Description = "Persist the resolved mappings to the identity store." },
            },
            Handler = IdentityDryRun.RunAsync,
        },
        new()
        {
            Name = "validate-config",
            Description = "Preflight: env vars, config files, schema shape and (best-effort) connectivity.",
            Options = new List<OptionSpec>
            {
                new() { Name = "--strict", TakesValue = false,
                        Description = "Fail on warnings and require Clarizen + Graph connectivity." },
            },
            Handler = ValidateConfig.RunAsync,
        },
    });

    private static List<OptionSpec> ContinuousOptions() => new()
    {
        new OptionSpec
        {
            Name = "--continuous", TakesValue = false,
            Description = "Keep running with scheduled full and incremental crawls.",
        },
        new OptionSpec
        {
            Name = "--full-crawl-hours", TakesValue = true, ValueName = "hours",
            Description = "Full crawl interval in hours (min 12, max 168). Default: 24.",
        },
        new OptionSpec
        {
            Name = "--incremental-hours", TakesValue = true, ValueName = "hours",
            Description = "Incremental crawl interval in hours (min 1, max 168). Default: 4.",
        },
    };
}
