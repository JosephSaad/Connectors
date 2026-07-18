// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for the full-deployment command.

using SalesforceCopilotConnector.Commands;
using SalesforceCopilotConnector.Config;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests.TestCommands;

/// <summary>
/// The command tests all mutate the shared static test hooks on the command
/// classes and <see cref="CommandRegistry"/>; keep them in one collection so
/// xUnit never runs two of these classes in parallel.
/// </summary>
[CollectionDefinition("CommandHooks")]
public sealed class CommandHooksCollection
{
}

/// <summary>
/// Port of the `_deployment_patches` pytest fixture: patches all external
/// dependencies of cmd_full_deployment and records calls; restores the real
/// implementations on Dispose.
/// </summary>
internal sealed class DeploymentPatches : IDisposable
{
    public AppConfig Config;

    public string? EnsureConnectionResult = "created";
    public bool IsConnectionReadyResult = true;
    public IngestionStats IngestContentResult = new() { TotalFetched = 5, SuccessCount = 5 };
    public Exception? IngestContentException;

    public int SetupLoggingCallCount;
    public int ClearCheckpointCallCount;
    public int RunIdentitySyncCallCount;
    public int RecordContentCrawlCallCount;
    public string? RecordContentCrawlLastSyncType;
    public int IngestContentCallCount;
    public DateTime? IngestContentLastSince;

    public DeploymentPatches()
    {
        Config = TestFixtures.TestConfig();

        Deploy.LoadConfigHook = () => Config;
        Deploy.ClearCheckpointHook = _ => ClearCheckpointCallCount++;
        Deploy.EnsureConnectionHook = (_, _, _) => Task.FromResult<string?>(EnsureConnectionResult);
        Deploy.EnsureSchemaHook = (_, _) => Task.CompletedTask;
        Deploy.SetSearchSettingsHook = (_, _) => Task.CompletedTask;
        Deploy.IsConnectionReadyHook = (_, _) => Task.FromResult(IsConnectionReadyResult);
        Deploy.RunIdentitySyncHook = (_, _) =>
        {
            RunIdentitySyncCallCount++;
            return Task.FromResult(new SyncSessionStats());
        };
        Deploy.RecordContentCrawlHook = (_, _, syncType) =>
        {
            RecordContentCrawlCallCount++;
            RecordContentCrawlLastSyncType = syncType;
        };
        Deploy.GetLastContentCrawlTimeHook = _ => null;
        Deploy.IngestContentHook = (_, _, since, _) =>
        {
            IngestContentCallCount++;
            IngestContentLastSince = since;
            if (IngestContentException is not null)
                throw IngestContentException;
            return Task.FromResult(IngestContentResult);
        };
        CommandRegistry.SetupLoggingOverride = (_, _, _) =>
        {
            SetupLoggingCallCount++;
            return ("fake_log.log", "fake_summary.log");
        };
        CommandRegistry.WriteSummaryOverride = (_, _, _, _, _, _, _) => { };
    }

    public void Dispose()
    {
        Deploy.LoadConfigHook = Settings.LoadConfig;
        Deploy.ClearCheckpointHook = SyncState.ClearCheckpoint;
        Deploy.EnsureConnectionHook = Connection.EnsureConnectionAsync;
        Deploy.EnsureSchemaHook = Schema.EnsureSchemaAsync;
        Deploy.SetSearchSettingsHook = Connection.SetSearchSettingsAsync;
        Deploy.IsConnectionReadyHook = Connection.IsConnectionReadyAsync;
        Deploy.RunIdentitySyncHook = Identity.RunIdentitySyncAsync;
        Deploy.RecordContentCrawlHook = Identity.RecordContentCrawl;
        Deploy.GetLastContentCrawlTimeHook = Identity.GetLastContentCrawlTime;
        Deploy.IngestContentHook =
            (config, client, since, dashboard) => Ingest.IngestContentAsync(config, client, since: since, dashboard: dashboard);
        CommandRegistry.SetupLoggingOverride = null;
        CommandRegistry.WriteSummaryOverride = null;
    }
}

[Collection("CommandHooks")]
public sealed class CmdFullDeploymentTests : IDisposable
{
    private readonly DeploymentPatches _patches = new();

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

    /// <summary>Copy of the test config with group ACL enabled (Python builds a new AppConfig).</summary>
    private AppConfig GroupAclConfig()
    {
        var testConfig = _patches.Config;
        return new AppConfig
        {
            ClientId = testConfig.ClientId,
            TenantId = testConfig.TenantId,
            Connector = testConfig.Connector,
            RepoRoot = testConfig.RepoRoot,
            Tuning = testConfig.Tuning,
            SchemaConfig = testConfig.SchemaConfig,
            OwdFieldMap = testConfig.OwdFieldMap,
            ParentMap = testConfig.ParentMap,
            OwdOverrides = testConfig.OwdOverrides,
            ObjectNames = testConfig.ObjectNames,
            UseNewAclEngine = false,
            UseGroupAcl = true,
            UseEntityDefinitionOwd = false,
            DebugObjectType = null,
            DebugItemId = null,
        };
    }

    [Fact]
    public async Task SuccessfulDeployment()
    {
        var result = await Deploy.CmdFullDeploymentAsync(MockArgs());
        Assert.True(result);
    }

    [Fact]
    public async Task EnsureConnectionReturnsNone()
    {
        _patches.EnsureConnectionResult = null;
        var result = await Deploy.CmdFullDeploymentAsync(MockArgs());
        Assert.False(result);
    }

    [Fact]
    public async Task ConnectionNotReady()
    {
        _patches.IsConnectionReadyResult = false;
        var result = await Deploy.CmdFullDeploymentAsync(MockArgs());
        Assert.False(result);
    }

    [Fact]
    public async Task IngestRaisesReturnsFalse()
    {
        _patches.IngestContentException = new InvalidOperationException("boom");
        var result = await Deploy.CmdFullDeploymentAsync(MockArgs());
        Assert.False(result);
    }

    [Fact]
    public async Task SetupLoggingIsCalled()
    {
        await Deploy.CmdFullDeploymentAsync(MockArgs());
        Assert.Equal(1, _patches.SetupLoggingCallCount);
    }

    [Fact]
    public void ClampHoursMinimum()
    {
        Assert.Equal(12, Deploy.ClampHours(5));
    }

    [Fact]
    public void ClampHoursMaximum()
    {
        Assert.Equal(168, Deploy.ClampHours(200));
    }

    [Fact]
    public void ClampHoursWithinRange()
    {
        Assert.Equal(24, Deploy.ClampHours(24));
    }

    [Fact]
    public async Task NonContinuousRunsOnce()
    {
        // Without --continuous, cmd_full_deployment returns after one run.
        var args = MockArgs();
        args.Set("continuous", false);
        var result = await Deploy.CmdFullDeploymentAsync(args);
        Assert.True(result);
        Assert.Equal(1, _patches.IngestContentCallCount);
    }

    [Fact]
    public async Task IdentitySyncSkippedOnIncrementalWhenFlagDisabled()
    {
        // With IDENTITY_SYNC_ON_INCREMENTAL explicitly disabled, the identity
        // crawl must NOT run during incremental sync (the pre-#5 behavior).
        var saved = Environment.GetEnvironmentVariable("IDENTITY_SYNC_ON_INCREMENTAL");
        Environment.SetEnvironmentVariable("IDENTITY_SYNC_ON_INCREMENTAL", "false");
        try
        {
            _patches.Config = GroupAclConfig();

            // Simulate incremental by calling _run_full_deployment with since
            await Deploy.RunFullDeploymentAsync(
                MockArgs(),
                since: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            // Identity sync should NOT be called for incremental
            Assert.Equal(0, _patches.RunIdentitySyncCallCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("IDENTITY_SYNC_ON_INCREMENTAL", saved);
        }
    }


    [Fact]
    public async Task IdentitySyncRunsOnFull()
    {
        // Identity crawl SHOULD run during full sync.
        _patches.Config = GroupAclConfig();

        await Deploy.RunFullDeploymentAsync(MockArgs(), since: null);

        Assert.Equal(1, _patches.RunIdentitySyncCallCount);
    }

    [Fact]
    public async Task FullDeploymentRecordsContentCrawl()
    {
        // Content crawl stats should be recorded in SQLite after ingestion.
        await Deploy.CmdFullDeploymentAsync(MockArgs());
        Assert.Equal(1, _patches.RecordContentCrawlCallCount);
        Assert.Equal("full", _patches.RecordContentCrawlLastSyncType);
    }

    [Fact]
    public async Task IncrementalRecordsSyncType()
    {
        // Incremental runs should record sync_type='incremental'.
        await Deploy.RunFullDeploymentAsync(
            MockArgs(),
            since: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Contains("incremental", _patches.RecordContentCrawlLastSyncType);
    }

    [Fact]
    public async Task IncrementalDoesNotClearCheckpoint()
    {
        // Incremental runs should not clear the checkpoint.
        await Deploy.RunFullDeploymentAsync(
            MockArgs(),
            since: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(0, _patches.ClearCheckpointCallCount);
    }

    [Fact]
    public async Task FullClearsCheckpoint()
    {
        // Full runs should clear the checkpoint.
        await Deploy.RunFullDeploymentAsync(MockArgs(), since: null);
        Assert.Equal(1, _patches.ClearCheckpointCallCount);
    }

    [Fact]
    public async Task IngestContentReceivesSince()
    {
        // ingest_content should receive the 'since' parameter.
        var sinceTime = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        await Deploy.RunFullDeploymentAsync(MockArgs(), since: sinceTime);
        Assert.Equal(sinceTime, _patches.IngestContentLastSince);
    }
}
