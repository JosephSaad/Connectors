using AltrataConnector.Commands;

namespace AltrataConnector.Tests;

public class CliParserTests
{
    private static CliParser Parser() => CommandRegistry.BuildParser();

    [Fact]
    public void AllExpectedCommandsAreRegistered()
    {
        var names = Parser().Commands.Select(c => c.Name).ToArray();
        foreach (var expected in new[]
                 {
                     "guide", "setup-connection", "full-deployment", "ingest", "ingest-object",
                     "ingest-item", "retry-failed", "identity-dry-run", "seat-sync",
                     "validate-config", "purge-all",
                 })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public void ParsesCommandWithNoOptions()
    {
        var parsed = Parser().ParseArgs(new[] { "guide" });
        Assert.Equal("guide", parsed.Command);
        Assert.False(parsed.Verbose);
        Assert.NotNull(parsed.Func);
    }

    [Fact]
    public void VerboseIsGlobalAndPositionIndependent()
    {
        Assert.True(Parser().ParseArgs(new[] { "--verbose", "guide" }).Verbose);
        Assert.True(Parser().ParseArgs(new[] { "guide", "--verbose" }).Verbose);
    }

    [Fact]
    public void ParsesContinuousModeOptions()
    {
        var parsed = Parser().ParseArgs(new[]
            { "full-deployment", "--continuous", "--full-crawl-hours", "24", "--incremental-hours", "4" });
        Assert.True(parsed.GetFlag("--continuous"));
        Assert.Equal(24, parsed.GetInt("--full-crawl-hours", 0));
        Assert.Equal(4, parsed.GetInt("--incremental-hours", 0));
    }

    [Fact]
    public void FullCrawlHoursBelowMinimumIsUsageError()
    {
        var exit = Assert.Throws<ArgumentParserExit>(() =>
            Parser().ParseArgs(new[] { "full-deployment", "--full-crawl-hours", "6" }));
        Assert.Equal(2, exit.Code);
    }

    [Fact]
    public void IncrementalHoursAboveMaximumIsUsageError()
    {
        var exit = Assert.Throws<ArgumentParserExit>(() =>
            Parser().ParseArgs(new[] { "ingest", "--incremental-hours", "500" }));
        Assert.Equal(2, exit.Code);
    }

    [Fact]
    public void IngestObjectRequiresType()
    {
        var exit = Assert.Throws<ArgumentParserExit>(() =>
            Parser().ParseArgs(new[] { "ingest-object" }));
        Assert.Equal(2, exit.Code);
    }

    [Fact]
    public void IngestObjectParsesType()
    {
        var parsed = Parser().ParseArgs(new[] { "ingest-object", "--type", "WealthIndicator" });
        Assert.Equal("WealthIndicator", parsed.GetString("--type"));
    }

    [Fact]
    public void IngestItemRequiresIdAndPurpose()
    {
        Assert.Equal(2, Assert.Throws<ArgumentParserExit>(() =>
            Parser().ParseArgs(new[] { "ingest-item", "--id", "P1" })).Code);
        Assert.Equal(2, Assert.Throws<ArgumentParserExit>(() =>
            Parser().ParseArgs(new[] { "ingest-item", "--purpose", "why" })).Code);

        var parsed = Parser().ParseArgs(new[]
            { "ingest-item", "--id", "P1", "--purpose", "client research", "--requested-by", "joe" });
        Assert.Equal("P1", parsed.GetString("--id"));
        Assert.Equal("client research", parsed.GetString("--purpose"));
        Assert.Equal("joe", parsed.GetString("--requested-by"));
    }

    [Fact]
    public void OptionMissingValueIsUsageError()
    {
        var exit = Assert.Throws<ArgumentParserExit>(() =>
            Parser().ParseArgs(new[] { "ingest-object", "--type" }));
        Assert.Equal(2, exit.Code);
    }

    [Fact]
    public void UnknownCommandIsUsageError()
    {
        var exit = Assert.Throws<ArgumentParserExit>(() =>
            Parser().ParseArgs(new[] { "frobnicate" }));
        Assert.Equal(2, exit.Code);
    }

    [Fact]
    public void UnknownOptionIsUsageError()
    {
        var exit = Assert.Throws<ArgumentParserExit>(() =>
            Parser().ParseArgs(new[] { "ingest", "--bogus" }));
        Assert.Equal(2, exit.Code);
    }

    [Fact]
    public void HelpExitsZero()
    {
        Assert.Equal(0, Assert.Throws<ArgumentParserExit>(() =>
            Parser().ParseArgs(new[] { "--help" })).Code);
        Assert.Equal(0, Assert.Throws<ArgumentParserExit>(() =>
            Parser().ParseArgs(new[] { "ingest", "--help" })).Code);
    }

    [Fact]
    public void PurgeAllFlagsParse()
    {
        var dry = Parser().ParseArgs(new[] { "purge-all" });
        Assert.False(dry.GetFlag("--confirm"));

        var confirmed = Parser().ParseArgs(new[] { "purge-all", "--confirm" });
        Assert.True(confirmed.GetFlag("--confirm"));

        var forcedDry = Parser().ParseArgs(new[] { "purge-all", "--confirm", "--dry-run" });
        Assert.True(forcedDry.GetFlag("--dry-run"));
    }

    [Fact]
    public void RetryFailedClearOnSuccessParses()
    {
        var parsed = Parser().ParseArgs(new[] { "retry-failed", "--clear-on-success" });
        Assert.True(parsed.GetFlag("--clear-on-success"));
    }

    [Fact]
    public void EmptyArgsYieldsNoCommand()
    {
        var parsed = Parser().ParseArgs(Array.Empty<string>());
        Assert.Equal("", parsed.Command);
    }
}
