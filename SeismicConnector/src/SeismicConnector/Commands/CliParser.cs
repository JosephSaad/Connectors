// Commands/CliParser.cs
// ---------------------
// Hand-rolled argument parser in the style of Python argparse (mirroring the
// Salesforce connector): subcommands, --flags, valued options, --help at both
// levels. Help → exit 0; usage errors → exit 2 (via ArgumentParserExit).

namespace SeismicConnector.Commands;

/// <summary>Thrown to unwind to Program.Main with a specific exit code.</summary>
public sealed class ArgumentParserExit : Exception
{
    public int Code { get; }

    public ArgumentParserExit(int code, string? message = null)
        : base(message ?? $"exit {code}")
    {
        Code = code;
    }
}

/// <summary>Result of parsing: the chosen command, its options, and its handler.</summary>
public sealed class ParsedArgs
{
    public string Command { get; init; } = "";
    public bool Verbose { get; init; }
    public Dictionary<string, string?> Options { get; } = new(StringComparer.Ordinal);
    public Func<ParsedArgs, Task<object?>>? Func { get; init; }

    public string? GetString(string name) =>
        Options.TryGetValue(name, out var value) ? value : null;

    public bool GetFlag(string name) => Options.ContainsKey(name);

    public int? GetInt(string name)
    {
        var raw = GetString(name);
        return raw is not null && int.TryParse(raw, out var parsed) ? parsed : null;
    }
}

public sealed class OptionSpec
{
    public required string Name { get; init; }          // e.g. "--id"
    public bool TakesValue { get; init; }
    public bool Required { get; init; }
    public string Help { get; init; } = "";

    public string Key => Name.TrimStart('-');
}

public sealed class CommandSpec
{
    public required string Name { get; init; }
    public string Help { get; init; } = "";
    public List<OptionSpec> Options { get; init; } = new();
    public required Func<ParsedArgs, Task<object?>> Handler { get; init; }
}

public sealed class ArgumentParser
{
    private readonly string _program;
    private readonly string _description;
    private readonly List<CommandSpec> _commands = new();

    public ArgumentParser(string program, string description)
    {
        _program = program;
        _description = description;
    }

    public void AddCommand(CommandSpec command) => _commands.Add(command);

    public IReadOnlyList<CommandSpec> Commands => _commands;

    public ParsedArgs ParseArgs(string[] args)
    {
        if (args.Length == 0)
            return new ParsedArgs();

        if (args[0] is "-h" or "--help")
        {
            PrintHelp(Console.Out);
            throw new ArgumentParserExit(0);
        }

        var commandName = args[0];
        var command = _commands.FirstOrDefault(c => c.Name == commandName);
        if (command is null)
        {
            Console.Error.WriteLine($"{_program}: error: unknown command '{commandName}'");
            PrintHelp(Console.Error);
            throw new ArgumentParserExit(2);
        }

        var verbose = false;
        var parsed = new ParsedArgs
        {
            Command = command.Name,
            Func = command.Handler,
            Verbose = args.Skip(1).Contains("--verbose"),
        };
        _ = verbose;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--verbose")
                continue;
            if (arg is "-h" or "--help")
            {
                PrintCommandHelp(Console.Out, command);
                throw new ArgumentParserExit(0);
            }
            var option = command.Options.FirstOrDefault(o => o.Name == arg);
            if (option is null)
            {
                Console.Error.WriteLine($"{_program} {command.Name}: error: unrecognized argument '{arg}'");
                throw new ArgumentParserExit(2);
            }
            if (option.TakesValue)
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine(
                        $"{_program} {command.Name}: error: argument {option.Name}: expected a value");
                    throw new ArgumentParserExit(2);
                }
                parsed.Options[option.Key] = args[++i];
            }
            else
            {
                parsed.Options[option.Key] = null;
            }
        }

        foreach (var option in command.Options.Where(o => o.Required))
        {
            if (!parsed.Options.ContainsKey(option.Key))
            {
                Console.Error.WriteLine(
                    $"{_program} {command.Name}: error: the following argument is required: {option.Name}");
                throw new ArgumentParserExit(2);
            }
        }

        return parsed;
    }

    public void PrintHelp(TextWriter writer)
    {
        writer.WriteLine($"usage: {_program} <command> [options] [--verbose]");
        writer.WriteLine();
        writer.WriteLine(_description);
        writer.WriteLine();
        writer.WriteLine("commands:");
        foreach (var command in _commands)
            writer.WriteLine($"  {command.Name,-20} {command.Help}");
        writer.WriteLine();
        writer.WriteLine("global options:");
        writer.WriteLine("  --verbose            show INFO logs on the console (file always captures everything)");
        writer.WriteLine($"\nRun '{_program} <command> --help' for command options.");
    }

    private void PrintCommandHelp(TextWriter writer, CommandSpec command)
    {
        writer.WriteLine($"usage: {_program} {command.Name} [options]");
        writer.WriteLine();
        writer.WriteLine(command.Help);
        if (command.Options.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("options:");
            foreach (var option in command.Options)
            {
                var value = option.TakesValue ? " <value>" : "";
                writer.WriteLine($"  {option.Name + value,-28} {option.Help}{(option.Required ? " (required)" : "")}");
            }
        }
    }
}
