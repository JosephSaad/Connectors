// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for the CLI argument parser (CommandRegistry.BuildParser).

using SalesforceCopilotConnector.Commands;

namespace SalesforceCopilotConnector.Tests.TestCommands;

public class CliParserTests
{
    /// <summary>pytest `parser` fixture.</summary>
    private static ArgumentParser Parser() => CommandRegistry.BuildParser();

    [Fact]
    public void NoArgsCommandIsNone()
    {
        var args = Parser().ParseArgs(Array.Empty<string>());
        Assert.Null(args.Command);
    }

    [Fact]
    public void GuideSetsFunc()
    {
        var args = Parser().ParseArgs(new[] { "guide" });
        Assert.Equal((Func<ParsedArgs, Task<bool?>>)Guide.CmdGuide, args.Func);
    }

    [Fact]
    public void FullDeploymentSetsFunc()
    {
        var args = Parser().ParseArgs(new[] { "full-deployment" });
        Assert.Equal((Func<ParsedArgs, Task<bool?>>)Deploy.CmdFullDeploymentAsync, args.Func);
    }

    [Fact]
    public void FullDeploymentVerbose()
    {
        var args = Parser().ParseArgs(new[] { "full-deployment", "--verbose" });
        Assert.True(args.GetBool("verbose"));
        Assert.Equal((Func<ParsedArgs, Task<bool?>>)Deploy.CmdFullDeploymentAsync, args.Func);
    }

    [Fact]
    public void IngestSetsFunc()
    {
        var args = Parser().ParseArgs(new[] { "ingest" });
        Assert.Equal((Func<ParsedArgs, Task<bool?>>)IngestCommand.CmdIngestAsync, args.Func);
    }

    [Fact]
    public void IngestItemSetsFunc()
    {
        var args = Parser().ParseArgs(new[] { "ingest-item", "--id", "500abc123" });
        Assert.Equal((Func<ParsedArgs, Task<bool?>>)IngestItem.CmdIngestItemAsync, args.Func);
        Assert.Equal("500abc123", args.GetString("id"));
    }

    [Fact]
    public void IngestItemRequiresId()
    {
        Assert.Throws<ArgumentParserExit>(() => Parser().ParseArgs(new[] { "ingest-item" }));
    }

    [Fact]
    public void IngestObjectSetsFunc()
    {
        var args = Parser().ParseArgs(new[] { "ingest-object", "--type", "Case" });
        Assert.Equal((Func<ParsedArgs, Task<bool?>>)IngestObject.CmdIngestObjectAsync, args.Func);
        Assert.Equal("Case", args.GetString("type"));
    }

    [Fact]
    public void IngestObjectRequiresType()
    {
        Assert.Throws<ArgumentParserExit>(() => Parser().ParseArgs(new[] { "ingest-object" }));
    }

    [Fact]
    public void VerboseBeforeSubcommand()
    {
        // --verbose after subcommand works.
        var args = Parser().ParseArgs(new[] { "ingest", "--verbose" });
        Assert.True(args.GetBool("verbose"));
        Assert.Equal("ingest", args.Command);
    }

    [Fact]
    public void UnknownCommandRaisesSystemExit()
    {
        Assert.Throws<ArgumentParserExit>(() => Parser().ParseArgs(new[] { "nonexistent-command" }));
    }

    [Fact]
    public void DefaultVerboseIsFalse()
    {
        var args = Parser().ParseArgs(new[] { "ingest" });
        Assert.False(args.GetBool("verbose"));
    }

    [Fact]
    public void FullDeploymentContinuousDefaults()
    {
        var args = Parser().ParseArgs(new[] { "full-deployment" });
        Assert.False(args.GetBool("continuous"));
        Assert.Equal(24, args.GetInt("full_crawl_hours"));
        Assert.Equal(4, args.GetInt("incremental_hours"));
    }

    [Fact]
    public void FullDeploymentContinuousWithHours()
    {
        var args = Parser().ParseArgs(
            new[] { "full-deployment", "--continuous", "--full-crawl-hours", "48", "--incremental-hours", "2" });
        Assert.True(args.GetBool("continuous"));
        Assert.Equal(48, args.GetInt("full_crawl_hours"));
        Assert.Equal(2, args.GetInt("incremental_hours"));
    }

    [Fact]
    public void IngestContinuousDefaults()
    {
        var args = Parser().ParseArgs(new[] { "ingest" });
        Assert.False(args.GetBool("continuous"));
        Assert.Equal(24, args.GetInt("full_crawl_hours"));
        Assert.Equal(4, args.GetInt("incremental_hours"));
    }

    [Fact]
    public void IngestContinuousWithHours()
    {
        var args = Parser().ParseArgs(
            new[] { "ingest", "--continuous", "--full-crawl-hours", "48", "--incremental-hours", "6" });
        Assert.True(args.GetBool("continuous"));
        Assert.Equal(48, args.GetInt("full_crawl_hours"));
        Assert.Equal(6, args.GetInt("incremental_hours"));
    }
}
