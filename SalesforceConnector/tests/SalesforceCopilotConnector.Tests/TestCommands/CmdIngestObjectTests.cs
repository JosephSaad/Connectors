// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for the ingest-object command.

using SalesforceCopilotConnector.Commands;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests.TestCommands;

/// <summary>
/// Port of the `_ingest_object_patches` pytest fixture: patches the external
/// dependencies of cmd_ingest_object and records calls; restores the real
/// implementations on Dispose.
/// </summary>
internal sealed class IngestObjectPatches : IDisposable
{
    public AppConfig Config;

    public bool IsConnectionReadyResult = true;
    public int IngestContentCallCount;
    public AppConfig? IngestContentLastConfig;

    public IngestObjectPatches()
    {
        Config = TestFixtures.TestConfig();

        IngestObject.LoadConfigHook = () => Config;
        IngestObject.IsConnectionReadyHook = (_, _) => Task.FromResult(IsConnectionReadyResult);
        IngestObject.IngestContentHook = (config, _, _) =>
        {
            IngestContentCallCount++;
            IngestContentLastConfig = config;
            return Task.FromResult(new IngestionStats { TotalFetched = 3, SuccessCount = 3 });
        };
        CommandRegistry.SetupLoggingOverride = (_, _, _) => ("fake_log.log", "fake_summary.log");
        CommandRegistry.WriteSummaryOverride = (_, _, _, _, _, _, _) => { };
    }

    public void Dispose()
    {
        IngestObject.LoadConfigHook = Settings.LoadConfig;
        IngestObject.IsConnectionReadyHook = Connection.IsConnectionReadyAsync;
        IngestObject.IngestContentHook =
            (config, client, since) => Ingest.IngestContentAsync(config, client, since: since);
        CommandRegistry.SetupLoggingOverride = null;
        CommandRegistry.WriteSummaryOverride = null;
    }
}

[Collection("CommandHooks")]
public sealed class CmdIngestObjectTests : IDisposable
{
    private readonly IngestObjectPatches _patches = new();

    public void Dispose() => _patches.Dispose();

    /// <summary>pytest `mock_args` fixture.</summary>
    private static ParsedArgs MockArgs()
    {
        var args = new ParsedArgs();
        args.Set("verbose", false);
        args.Set("type", "Case");
        return args;
    }

    [Fact]
    public async Task SetsDebugObjectTypeOnConfig()
    {
        await IngestObject.CmdIngestObjectAsync(MockArgs());
        var configUsed = _patches.IngestContentLastConfig;
        Assert.NotNull(configUsed);
        Assert.Equal("Case", configUsed!.DebugObjectType);
    }

    [Fact]
    public async Task CallsIngestContent()
    {
        await IngestObject.CmdIngestObjectAsync(MockArgs());
        Assert.Equal(1, _patches.IngestContentCallCount);
    }

    [Fact]
    public async Task ConnectionNotReady()
    {
        _patches.IsConnectionReadyResult = false;
        await IngestObject.CmdIngestObjectAsync(MockArgs());
        Assert.Equal(0, _patches.IngestContentCallCount);
    }
}
