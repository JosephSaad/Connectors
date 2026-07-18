using HadoopConnector.Commands;

namespace HadoopConnector.Tests;

public class CliParserTests
{
    private static CliParser Parser() => CommandRegistry.BuildParser();

    [Fact]
    public void EmptyArgs_ReturnsNoCommand()
    {
        var parsed = Parser().ParseArgs(Array.Empty<string>());
        Assert.Equal(string.Empty, parsed.Command);
        Assert.Null(parsed.Func);
    }

    [Fact]
    public void KnownCommands_AllRegistered()
    {
        var names = Parser().Commands.Select(c => c.Name).ToList();
        Assert.Equal(
            new[]
            {
                "guide", "setup-connection", "full-deployment", "ingest", "ingest-object",
                "ingest-item", "retry-failed", "reconcile", "identity-dry-run", "validate-config",
            },
            names);
    }

    [Fact]
    public void Reconcile_Options()
    {
        var parsed = Parser().ParseArgs(new[] { "reconcile", "--type", "Project", "--fix" });
        Assert.Equal("Project", parsed.GetString("--type"));
        Assert.True(parsed.HasFlag("--fix"));

        var bare = Parser().ParseArgs(new[] { "reconcile" });
        Assert.Null(bare.GetString("--type"));
        Assert.False(bare.HasFlag("--fix"));
    }

    [Fact]
    public void UnknownCommand_ExitsWithCode2()
    {
        var exit = Assert.Throws<CliExit>(() => Parser().ParseArgs(new[] { "bogus" }));
        Assert.Equal(2, exit.Code);
    }

    [Fact]
    public void Help_ExitsWithCode0()
    {
        var exit = Assert.Throws<CliExit>(() => Parser().ParseArgs(new[] { "--help" }));
        Assert.Equal(0, exit.Code);
    }

    [Fact]
    public void CommandHelp_ExitsWithCode0()
    {
        var exit = Assert.Throws<CliExit>(() => Parser().ParseArgs(new[] { "ingest", "--help" }));
        Assert.Equal(0, exit.Code);
    }

    [Fact]
    public void Verbose_IsGlobalFlag()
    {
        var parsed = Parser().ParseArgs(new[] { "guide", "--verbose" });
        Assert.True(parsed.Verbose);
        Assert.Equal("guide", parsed.Command);
    }

    [Fact]
    public void FullDeployment_ContinuousOptionsParsed()
    {
        var parsed = Parser().ParseArgs(new[]
        {
            "full-deployment", "--continuous", "--full-crawl-hours", "24", "--incremental-hours", "4",
        });
        Assert.True(parsed.HasFlag("--continuous"));
        Assert.Equal(24, parsed.GetInt("--full-crawl-hours", 0));
        Assert.Equal(4, parsed.GetInt("--incremental-hours", 0));
    }

    [Fact]
    public void OptionValue_EqualsFormSupported()
    {
        var parsed = Parser().ParseArgs(new[] { "ingest-object", "--type=Project" });
        Assert.Equal("Project", parsed.GetString("--type"));
    }

    [Fact]
    public void IngestObject_MissingRequiredType_ExitsWithCode2()
    {
        var exit = Assert.Throws<CliExit>(() => Parser().ParseArgs(new[] { "ingest-object" }));
        Assert.Equal(2, exit.Code);
    }

    [Fact]
    public void IngestItem_IdAndObjectType()
    {
        var parsed = Parser().ParseArgs(new[]
        {
            "ingest-item", "--id", "0035e00000abcde", "--object-type", "Contact",
        });
        Assert.Equal("0035e00000abcde", parsed.GetString("--id"));
        Assert.Equal("Contact", parsed.GetString("--object-type"));
    }

    [Fact]
    public void ValueMissing_ExitsWithCode2()
    {
        var exit = Assert.Throws<CliExit>(() => Parser().ParseArgs(new[] { "ingest-item", "--id" }));
        Assert.Equal(2, exit.Code);
    }

    [Fact]
    public void UnknownOption_ExitsWithCode2()
    {
        var exit = Assert.Throws<CliExit>(() => Parser().ParseArgs(new[] { "guide", "--wat" }));
        Assert.Equal(2, exit.Code);
    }

    [Fact]
    public void FlagOptionWithValue_ExitsWithCode2()
    {
        var exit = Assert.Throws<CliExit>(
            () => Parser().ParseArgs(new[] { "retry-failed", "--clear-on-success=yes" }));
        Assert.Equal(2, exit.Code);
    }

    [Fact]
    public void GetInt_InvalidValue_ExitsWithCode2()
    {
        var parsed = Parser().ParseArgs(new[] { "ingest", "--full-crawl-hours", "abc" });
        var exit = Assert.Throws<CliExit>(() => parsed.GetInt("--full-crawl-hours", 24));
        Assert.Equal(2, exit.Code);
    }

    [Fact]
    public void RetryFailed_Flags()
    {
        var parsed = Parser().ParseArgs(new[]
        {
            "retry-failed", "--file", "logs/failed.jsonl", "--clear-on-success",
        });
        Assert.Equal("logs/failed.jsonl", parsed.GetString("--file"));
        Assert.True(parsed.HasFlag("--clear-on-success"));
    }

    [Theory]
    [InlineData(12, 1)]
    [InlineData(168, 168)]
    [InlineData(24, 4)]
    public void ValidateSchedule_AcceptsValidBounds(int full, int incremental)
    {
        var (f, i) = Deploy.ValidateSchedule(full, incremental);
        Assert.Equal(full, f);
        Assert.Equal(incremental, i);
    }

    [Theory]
    [InlineData(11, 4)]
    [InlineData(169, 4)]
    [InlineData(24, 0)]
    [InlineData(24, 169)]
    public void ValidateSchedule_RejectsOutOfRange(int full, int incremental)
    {
        var exit = Assert.Throws<CliExit>(() => Deploy.ValidateSchedule(full, incremental));
        Assert.Equal(2, exit.Code);
    }

    [Fact]
    public void IngestItem_RequiresObjectType()
    {
        // ingest-item ids are Salesforce record ids; the object type can no
        // longer be inferred from the id, so --object-type is required.
        var parser = CommandRegistry.BuildParser();
        var spec = parser.Commands.First(c => c.Name == "ingest-item");
        var objectType = spec.Options.First(o => o.Name == "--object-type");
        Assert.True(objectType.Required);
        Assert.True(spec.Options.First(o => o.Name == "--id").Required);
    }

    [Fact]
    public void IngestItem_MissingObjectType_IsUsageError()
    {
        var parser = CommandRegistry.BuildParser();
        var exit = Assert.Throws<CliExit>(
            () => parser.ParseArgs(new[] { "ingest-item", "--id", "003A0000001" }));
        Assert.Equal(2, exit.Code);
    }
}
