// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for the ingest-item command.

using SalesforceCopilotConnector.Commands;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests.TestCommands;

/// <summary>
/// Port of the `_ingest_item_patches` pytest fixture: patches the external
/// dependencies of cmd_ingest_item and records calls; restores the real
/// implementations on Dispose.
/// </summary>
internal sealed class IngestItemPatches : IDisposable
{
    public AppConfig Config;

    public bool IsConnectionReadyResult = true;
    public int IngestContentCallCount;
    public AppConfig? IngestContentLastConfig;

    public IngestItemPatches()
    {
        Config = TestFixtures.TestConfig();

        IngestItem.LoadConfigHook = () => Config;
        IngestItem.IsConnectionReadyHook = (_, _) => Task.FromResult(IsConnectionReadyResult);
        IngestItem.IngestContentHook = (config, _, _) =>
        {
            IngestContentCallCount++;
            IngestContentLastConfig = config;
            return Task.FromResult(new IngestionStats { TotalFetched = 1, SuccessCount = 1 });
        };
        CommandRegistry.SetupLoggingOverride = (_, _, _) => ("fake_log.log", "fake_summary.log");
        CommandRegistry.WriteSummaryOverride = (_, _, _, _, _, _, _) => { };
    }

    public void Dispose()
    {
        IngestItem.LoadConfigHook = Settings.LoadConfig;
        IngestItem.IsConnectionReadyHook = Connection.IsConnectionReadyAsync;
        IngestItem.IngestContentHook =
            (config, client, since) => Ingest.IngestContentAsync(config, client, since: since);
        CommandRegistry.SetupLoggingOverride = null;
        CommandRegistry.WriteSummaryOverride = null;
    }
}

[Collection("CommandHooks")]
public sealed class CmdIngestItemTests : IDisposable
{
    private readonly IngestItemPatches _patches = new();

    public void Dispose() => _patches.Dispose();

    /// <summary>pytest `mock_args` fixture.</summary>
    private static ParsedArgs MockArgs()
    {
        var args = new ParsedArgs();
        args.Set("verbose", false);
        args.Set("id", "500abc123");
        return args;
    }

    [Fact]
    public async Task SetsDebugItemIdOnConfig()
    {
        await IngestItem.CmdIngestItemAsync(MockArgs());
        var configUsed = _patches.IngestContentLastConfig;
        Assert.NotNull(configUsed);
        Assert.Equal("500abc123", configUsed!.DebugItemId);
    }

    [Fact]
    public async Task CallsIngestContent()
    {
        await IngestItem.CmdIngestItemAsync(MockArgs());
        Assert.Equal(1, _patches.IngestContentCallCount);
    }

    [Fact]
    public async Task ConnectionNotReady()
    {
        _patches.IsConnectionReadyResult = false;
        await IngestItem.CmdIngestItemAsync(MockArgs());
        Assert.Equal(0, _patches.IngestContentCallCount);
    }
}
