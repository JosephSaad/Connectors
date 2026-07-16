using SeismicConnector.Commands;

namespace SeismicConnector.Tests;

public class CliParserTests
{
    private static ArgumentParser Parser() => CommandRegistry.BuildParser();

    [Fact]
    public void EmptyArgs_ReturnsNoCommand()
    {
        var parsed = Parser().ParseArgs(Array.Empty<string>());
        Assert.Equal("", parsed.Command);
        Assert.Null(parsed.Func);
    }

    [Theory]
    [InlineData("guide")]
    [InlineData("setup-connection")]
    [InlineData("full-deployment")]
    [InlineData("ingest")]
    [InlineData("retry-failed")]
    [InlineData("identity-dry-run")]
    [InlineData("validate-config")]
    public void KnownCommands_Parse(string command)
    {
        var parsed = Parser().ParseArgs(new[] { command });
        Assert.Equal(command, parsed.Command);
        Assert.NotNull(parsed.Func);
        Assert.False(parsed.Verbose);
    }

    [Fact]
    public void UnknownCommand_ExitsWithCode2()
    {
        var exit = Assert.Throws<ArgumentParserExit>(() => Parser().ParseArgs(new[] { "bogus" }));
        Assert.Equal(2, exit.Code);
    }

    [Fact]
    public void Help_ExitsWithCode0()
    {
        var exit = Assert.Throws<ArgumentParserExit>(() => Parser().ParseArgs(new[] { "--help" }));
        Assert.Equal(0, exit.Code);
    }

    [Fact]
    public void CommandHelp_ExitsWithCode0()
    {
        var exit = Assert.Throws<ArgumentParserExit>(
            () => Parser().ParseArgs(new[] { "ingest-item", "--help" }));
        Assert.Equal(0, exit.Code);
    }

    [Fact]
    public void Verbose_IsGlobal()
    {
        var parsed = Parser().ParseArgs(new[] { "ingest", "--verbose" });
        Assert.True(parsed.Verbose);
    }

    [Fact]
    public void FullDeployment_ContinuousOptions()
    {
        var parsed = Parser().ParseArgs(new[]
        {
            "full-deployment", "--continuous", "--full-crawl-hours", "24", "--incremental-hours", "4",
        });
        Assert.True(parsed.GetFlag("continuous"));
        Assert.Equal(24, parsed.GetInt("full-crawl-hours"));
        Assert.Equal(4, parsed.GetInt("incremental-hours"));
        var intervals = CommandRegistry.ReadContinuousIntervals(parsed);
        Assert.Equal((24, 4), intervals);
    }

    [Theory]
    [InlineData("11")]   // below minimum 12
    [InlineData("169")]  // above maximum 168
    public void FullCrawlHours_OutOfRange_Exits2(string hours)
    {
        var parsed = Parser().ParseArgs(new[] { "full-deployment", "--full-crawl-hours", hours });
        var exit = Assert.Throws<ArgumentParserExit>(() => CommandRegistry.ReadContinuousIntervals(parsed));
        Assert.Equal(2, exit.Code);
    }

    [Fact]
    public void IncrementalHours_OutOfRange_Exits2()
    {
        var parsed = Parser().ParseArgs(new[] { "ingest", "--incremental-hours", "0" });
        var exit = Assert.Throws<ArgumentParserExit>(() => CommandRegistry.ReadContinuousIntervals(parsed));
        Assert.Equal(2, exit.Code);
    }

    [Fact]
    public void IngestItem_RequiresId()
    {
        var exit = Assert.Throws<ArgumentParserExit>(() => Parser().ParseArgs(new[] { "ingest-item" }));
        Assert.Equal(2, exit.Code);
    }

    [Fact]
    public void IngestItem_ParsesIdAndTeamsite()
    {
        var parsed = Parser().ParseArgs(new[] { "ingest-item", "--id", "c-42", "--teamsite", "ts-9" });
        Assert.Equal("c-42", parsed.GetString("id"));
        Assert.Equal("ts-9", parsed.GetString("teamsite"));
    }

    [Fact]
    public void IngestObject_RequiresType()
    {
        var exit = Assert.Throws<ArgumentParserExit>(() => Parser().ParseArgs(new[] { "ingest-object" }));
        Assert.Equal(2, exit.Code);
    }

    [Fact]
    public void ValuedOption_MissingValue_Exits2()
    {
        var exit = Assert.Throws<ArgumentParserExit>(
            () => Parser().ParseArgs(new[] { "ingest-object", "--type" }));
        Assert.Equal(2, exit.Code);
    }

    [Fact]
    public void UnknownOption_Exits2()
    {
        var exit = Assert.Throws<ArgumentParserExit>(
            () => Parser().ParseArgs(new[] { "ingest", "--bogus" }));
        Assert.Equal(2, exit.Code);
    }

    [Fact]
    public void RetryFailed_Flags()
    {
        var parsed = Parser().ParseArgs(new[]
        {
            "retry-failed", "--file", "logs/failed_records_X.jsonl", "--clear-on-success",
        });
        Assert.Equal("logs/failed_records_X.jsonl", parsed.GetString("file"));
        Assert.True(parsed.GetFlag("clear-on-success"));
    }

    [Fact]
    public void IdentityDryRun_SaveFlag()
    {
        var parsed = Parser().ParseArgs(new[] { "identity-dry-run", "--save" });
        Assert.True(parsed.GetFlag("save"));
    }

    [Fact]
    public void ValidateConfig_StrictFlag()
    {
        var parsed = Parser().ParseArgs(new[] { "validate-config", "--strict" });
        Assert.True(parsed.GetFlag("strict"));
    }
}
