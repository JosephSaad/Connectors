// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Tests;

/// <summary>
/// LOG_RETENTION_DAYS pruning of logs/{prefix}_{yyyyMMdd_HHmmss}/ run dirs
/// (Infrastructure/LogPruner.cs, contract in docs/SQL_CONTRACT.md).
/// Mutates process-global env vars, so it joins the "EnvVars" collection.
/// </summary>
[Collection("EnvVars")]
public sealed class LogPrunerTests : IDisposable
{
    private readonly string _tmp;
    private readonly string? _savedRetention;
    private readonly string? _savedUseSql;

    public LogPrunerTests()
    {
        _tmp = Directory.CreateTempSubdirectory("log_pruner_tests_").FullName;
        _savedRetention = Environment.GetEnvironmentVariable("LOG_RETENTION_DAYS");
        _savedUseSql = Environment.GetEnvironmentVariable("USE_SQL_SERVER");
        Environment.SetEnvironmentVariable("LOG_RETENTION_DAYS", null);
        // Keep the pruner off the SQL path regardless of ambient env.
        Environment.SetEnvironmentVariable("USE_SQL_SERVER", null);
        SalesforceCopilotConnector.Config.SyncState.ResetProviderCache();
        LogPruner.LogsDirOverride = _tmp;
    }

    public void Dispose()
    {
        LogPruner.LogsDirOverride = null;
        Environment.SetEnvironmentVariable("LOG_RETENTION_DAYS", _savedRetention);
        Environment.SetEnvironmentVariable("USE_SQL_SERVER", _savedUseSql);
        SalesforceCopilotConnector.Config.SyncState.ResetProviderCache();
        try
        {
            Directory.Delete(_tmp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Create a run dir named {prefix}_{yyyyMMdd_HHmmss} with a log file inside.</summary>
    private string MakeRunDir(string prefix, DateTime stamp)
    {
        var name = $"{prefix}_{stamp.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}";
        var dir = Path.Combine(_tmp, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{name}.log"), "log line\n");
        return dir;
    }

    /// <summary>Create a state file directly in the logs root.</summary>
    private string MakeRootFile(string name)
    {
        var path = Path.Combine(_tmp, name);
        File.WriteAllText(path, "{}");
        return path;
    }

    [Fact]
    public void DisabledByDefaultKeepsEverything()
    {
        // LOG_RETENTION_DAYS unset → pruning is a no-op.
        var oldDir = MakeRunDir("deployment", DateTime.Now.AddDays(-365));
        LogPruner.Prune();
        Assert.True(Directory.Exists(oldDir));
    }

    [Fact]
    public void ZeroDisablesPruning()
    {
        Environment.SetEnvironmentVariable("LOG_RETENTION_DAYS", "0");
        var oldDir = MakeRunDir("ingestion", DateTime.Now.AddDays(-365));
        LogPruner.Prune();
        Assert.True(Directory.Exists(oldDir));
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("7.5")]
    [InlineData("-3")]
    public void InvalidOrNegativeDisablesPruning(string value)
    {
        Environment.SetEnvironmentVariable("LOG_RETENTION_DAYS", value);
        var oldDir = MakeRunDir("incremental", DateTime.Now.AddDays(-365));
        LogPruner.Prune();  // must not throw
        Assert.True(Directory.Exists(oldDir));
    }

    [Fact]
    public void PrunesOldRunDirsKeepsRecentOnes()
    {
        Environment.SetEnvironmentVariable("LOG_RETENTION_DAYS", "7");
        var oldDeploy = MakeRunDir("deployment", DateTime.Now.AddDays(-10));
        var oldIngest = MakeRunDir("ingest_object_Case", DateTime.Now.AddDays(-8));
        var newDeploy = MakeRunDir("deployment", DateTime.Now.AddDays(-1));
        var newIngest = MakeRunDir("ingestion", DateTime.Now);

        LogPruner.Prune();

        Assert.False(Directory.Exists(oldDeploy));
        Assert.False(Directory.Exists(oldIngest));
        Assert.True(Directory.Exists(newDeploy));
        Assert.True(Directory.Exists(newIngest));
    }

    [Fact]
    public void NeverTouchesRootStateFilesOrNonMatchingDirs()
    {
        Environment.SetEnvironmentVariable("LOG_RETENTION_DAYS", "7");
        var syncState = MakeRootFile("sync_state.json");
        var checkpoint = MakeRootFile("checkpoint_ACL22032026.json");
        var deadLetter = MakeRootFile("failed_records_ACL22032026.jsonl");
        // Age the root files well past the retention window — they must survive anyway.
        var old = DateTime.Now.AddDays(-30);
        File.SetLastWriteTime(syncState, old);
        File.SetLastWriteTime(checkpoint, old);
        File.SetLastWriteTime(deadLetter, old);
        // Directories that don't match {prefix}_{yyyyMMdd_HHmmss} are ignored.
        var archive = Path.Combine(_tmp, "archive");
        Directory.CreateDirectory(archive);
        var badStamp = Path.Combine(_tmp, "deployment_99999999_999999");
        Directory.CreateDirectory(badStamp);
        var oldDir = MakeRunDir("retry_failed", DateTime.Now.AddDays(-30));

        LogPruner.Prune();

        Assert.True(File.Exists(syncState));
        Assert.True(File.Exists(checkpoint));
        Assert.True(File.Exists(deadLetter));
        Assert.True(Directory.Exists(archive));
        Assert.True(Directory.Exists(badStamp));
        Assert.False(Directory.Exists(oldDir));
    }

    [Fact]
    public void ExactBoundaryDirIsKept()
    {
        // A run dir exactly at (or newer than) the cutoff is kept; strictly older is pruned.
        Environment.SetEnvironmentVariable("LOG_RETENTION_DAYS", "7");
        var boundary = MakeRunDir("deployment", DateTime.Now.AddDays(-7).AddMinutes(5));
        var justOver = MakeRunDir("deployment", DateTime.Now.AddDays(-7).AddMinutes(-5));

        LogPruner.Prune();

        Assert.True(Directory.Exists(boundary));
        Assert.False(Directory.Exists(justOver));
    }

    [Fact]
    public void MissingLogsRootIsHarmless()
    {
        Environment.SetEnvironmentVariable("LOG_RETENTION_DAYS", "7");
        LogPruner.LogsDirOverride = Path.Combine(_tmp, "does_not_exist");
        LogPruner.Prune();  // must not throw
    }
}
