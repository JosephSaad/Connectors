// Commands/Cli.cs
// ---------------
// Hand-rolled argparse-style CLI (same style as the reference connector):
// subcommands with typed options, --help at both levels, usage errors exit 2.

namespace AltrataConnector.Commands;

/// <summary>Thrown to unwind to Program with a specific exit code (help → 0, usage error → 2).</summary>
public sealed class ArgumentParserExit : Exception
{
    public int Code { get; }
    public ArgumentParserExit(int code, string? message = null) : base(message ?? "") => Code = code;
}

public sealed class OptionSpec
{
    public required string Name { get; init; }          // "--type"
    public bool HasValue { get; init; }                 // flag vs option
    public bool Required { get; init; }
    public string? Help { get; init; }
    public int? MinInt { get; init; }
    public int? MaxInt { get; init; }
    public bool IsInt => MinInt.HasValue || MaxInt.HasValue;
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
    public string Command { get; init; } = "";
    public bool Verbose { get; init; }
    public Dictionary<string, string?> Values { get; init; } = new(StringComparer.Ordinal);
    public Func<ParsedArgs, Task<object?>>? Func { get; init; }

    public bool GetFlag(string name) => Values.ContainsKey(name);

    public string? GetString(string name) =>
        Values.TryGetValue(name, out var value) ? value : null;

    public int GetInt(string name, int fallback) =>
        Values.TryGetValue(name, out var value) && int.TryParse(value, out var n) ? n : fallback;
}

public sealed class CliParser
{
    private readonly List<CommandSpec> _commands = new();
    private readonly string _programName;

    public CliParser(string programName) => _programName = programName;

    public void AddCommand(CommandSpec command) => _commands.Add(command);

    public IReadOnlyList<CommandSpec> Commands => _commands;

    public ParsedArgs ParseArgs(string[] args)
    {
        var list = args.ToList();
        var verbose = list.Remove("--verbose");

        if (list.Count == 0)
            return new ParsedArgs { Command = "", Verbose = verbose };

        if (list[0] is "-h" or "--help")
        {
            PrintHelp(Console.Out);
            throw new ArgumentParserExit(0);
        }

        var commandName = list[0];
        var command = _commands.FirstOrDefault(c => c.Name == commandName);
        if (command == null)
        {
            Console.Error.WriteLine($"{_programName}: error: unknown command '{commandName}'");
            PrintHelp(Console.Error);
            throw new ArgumentParserExit(2);
        }

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 1; i < list.Count; i++)
        {
            var token = list[i];
            if (token is "-h" or "--help")
            {
                PrintCommandHelp(Console.Out, command);
                throw new ArgumentParserExit(0);
            }

            var option = command.Options.FirstOrDefault(o => o.Name == token);
            if (option == null)
            {
                Console.Error.WriteLine($"{_programName} {command.Name}: error: unknown option '{token}'");
                throw new ArgumentParserExit(2);
            }

            if (!option.HasValue)
            {
                values[option.Name] = null;
                continue;
            }

            if (i + 1 >= list.Count || list[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"{_programName} {command.Name}: error: option '{token}' requires a value");
                throw new ArgumentParserExit(2);
            }
            var raw = list[++i];
            if (option.IsInt)
            {
                if (!int.TryParse(raw, out var n))
                {
                    Console.Error.WriteLine($"{_programName} {command.Name}: error: option '{token}' expects an integer");
                    throw new ArgumentParserExit(2);
                }
                if ((option.MinInt.HasValue && n < option.MinInt) ||
                    (option.MaxInt.HasValue && n > option.MaxInt))
                {
                    Console.Error.WriteLine(
                        $"{_programName} {command.Name}: error: option '{token}' must be between " +
                        $"{option.MinInt ?? int.MinValue} and {option.MaxInt ?? int.MaxValue}");
                    throw new ArgumentParserExit(2);
                }
            }
            values[option.Name] = raw;
        }

        foreach (var option in command.Options.Where(o => o.Required))
        {
            if (!values.ContainsKey(option.Name))
            {
                Console.Error.WriteLine($"{_programName} {command.Name}: error: option '{option.Name}' is required");
                throw new ArgumentParserExit(2);
            }
        }

        return new ParsedArgs
        {
            Command = command.Name,
            Verbose = verbose,
            Values = values,
            Func = command.Handler,
        };
    }

    public void PrintHelp(TextWriter writer)
    {
        writer.WriteLine($"usage: {_programName} <command> [options] [--verbose]");
        writer.WriteLine();
        writer.WriteLine("commands:");
        foreach (var command in _commands)
            writer.WriteLine($"  {command.Name,-18} {command.Description}");
        writer.WriteLine();
        writer.WriteLine($"Run '{_programName} <command> --help' for command options.");
    }

    private void PrintCommandHelp(TextWriter writer, CommandSpec command)
    {
        writer.WriteLine($"usage: {_programName} {command.Name} " +
                         string.Join(" ", command.Options.Select(o =>
                             o.Required
                                 ? $"{o.Name}{(o.HasValue ? " <value>" : "")}"
                                 : $"[{o.Name}{(o.HasValue ? " <value>" : "")}]")));
        writer.WriteLine();
        writer.WriteLine(command.Description);
        if (command.Options.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("options:");
            foreach (var option in command.Options)
                writer.WriteLine($"  {option.Name,-24} {option.Help}");
        }
    }
}
