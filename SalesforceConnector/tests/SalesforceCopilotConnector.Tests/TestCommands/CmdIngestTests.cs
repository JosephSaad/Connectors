// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for the ingest command.

using SalesforceCopilotConnector.Commands;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests.TestCommands;

/// <summary>
/// Port of the `_ingest_patches` pytest fixture: patches the external
/// dependencies of cmd_ingest and records calls; restores the real
/// implementations on Dispose.
/// </summary>
internal sealed class IngestPatches : IDisposable
{
    public AppConfig Config;

    public bool IsConnectionReadyResult = true;
    public IngestionStats IngestContentResult = new() { TotalFetched = 5, SuccessCount = 5 };
    public int IngestContentCallCount;

    public IngestPatches()
    {
        Config = TestFixtures.TestConfig();

        IngestCommand.LoadConfigHook = () => Config;
        IngestCommand.IsConnectionReadyHook = (_, _) => Task.FromResult(IsConnectionReadyResult);
        IngestCommand.IngestContentHook = (_, _, _, _) =>
        {
            IngestContentCallCount++;
            return Task.FromResult(IngestContentResult);
        };
        CommandRegistry.SetupLoggingOverride = (_, _, _) => ("fake_log.log", "fake_summary.log");
        CommandRegistry.WriteSummaryOverride = (_, _, _, _, _, _, _) => { };
    }

    public void Dispose()
    {
        IngestCommand.LoadConfigHook = Settings.LoadConfig;
        IngestCommand.IsConnectionReadyHook = Connection.IsConnectionReadyAsync;
        IngestCommand.IngestContentHook =
            (config, client, since, dashboard) => Ingest.IngestContentAsync(config, client, since: since, dashboard: dashboard);
        CommandRegistry.SetupLoggingOverride = null;
        CommandRegistry.WriteSummaryOverride = null;
    }
}

[Collection("CommandHooks")]
public sealed class CmdIngestTests : IDisposable
{
    private readonly IngestPatches _patches = new();

    public void Dispose() => _patches.Dispose();

    /// <summary>pytest `mock_args` fixture.</summary>
    private static ParsedArgs MockArgs()
    {
        var args = new ParsedArgs();
        args.Set("verbose", false);
        args.Set("continuous", false);
        args.Set("full_crawl_hours", 24);
        args.Set("incremental_hours", 4);
        return args;
    }

    [Fact]
    public async Task SuccessfulIngest()
    {
        var result = await IngestCommand.CmdIngestAsync(MockArgs());
        Assert.True(result);
    }

    [Fact]
    public async Task ConnectionNotReady()
    {
        _patches.IsConnectionReadyResult = false;
        var result = await IngestCommand.CmdIngestAsync(MockArgs());
        Assert.False(result);
    }

    [Fact]
    public async Task IngestWithFailures()
    {
        var stats = new IngestionStats { TotalFetched = 5, SuccessCount = 3, FailedCount = 2 };
        stats.FailedIds.AddRange(new[] { "a", "b" });
        _patches.IngestContentResult = stats;
        var result = await IngestCommand.CmdIngestAsync(MockArgs());
        Assert.False(result);
    }

    [Fact]
    public void ClampHoursMinimum()
    {
        Assert.Equal(12, IngestCommand.ClampHours(5));
    }

    [Fact]
    public void ClampHoursMaximum()
    {
        Assert.Equal(168, IngestCommand.ClampHours(200));
    }

    [Fact]
    public void ClampHoursWithinRange()
    {
        Assert.Equal(24, IngestCommand.ClampHours(24));
    }

    [Fact]
    public async Task NonContinuousRunsOnce()
    {
        // Without --continuous, cmd_ingest returns after one run.
        var args = MockArgs();
        args.Set("continuous", false);
        var result = await IngestCommand.CmdIngestAsync(args);
        Assert.True(result);
        Assert.Equal(1, _patches.IngestContentCallCount);
    }
}
