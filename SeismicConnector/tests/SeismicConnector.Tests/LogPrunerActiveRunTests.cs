// LogPrunerActiveRunTests.cs
// -------------------------
// The pruner must never delete the run directory the process is writing into.
//
// A run directory is stamped once at startup, so in --continuous or service mode
// it eventually ages past LOG_RETENTION_DAYS and the pruner starts targeting its
// own live logs every cycle. The two platforms fail differently and the quiet one
// is worse: on Windows the recursive delete takes the directory's other artifacts
// and then throws on the locked .log; on macOS and Linux the unlink SUCCEEDS and
// the open handle writes into an unlinked inode, losing every subsequent line
// silently. These tests run on all three.

using Connector.Chassis;

namespace SeismicConnector.Tests;

public class LogPrunerActiveRunTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pruner_active_").FullName;
    private readonly string? _previousRetention = Environment.GetEnvironmentVariable("LOG_RETENTION_DAYS");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LOG_RETENTION_DAYS", _previousRetention);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>A run directory named far enough in the past to be prunable.</summary>
    private string AgedRunDir(string prefix)
    {
        var dir = Path.Combine(_root, $"{prefix}_20200101_000000");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{prefix}_20200101_000000.log"), "x");
        return dir;
    }

    [Fact]
    public void TheActiveRunDirectorySurvivesEvenWhenItIsOlderThanRetention()
    {
        Environment.SetEnvironmentVariable("LOG_RETENTION_DAYS", "1");
        var active = AgedRunDir("live");
        var stale = AgedRunDir("stale");

        var pruned = LogPruner.Prune(_root, activeRunDir: active);

        Assert.True(Directory.Exists(active),
            "the pruner deleted the run directory the process is writing into — on POSIX this "
            + "unlinks the open log and every subsequent line is lost silently.");
        Assert.False(Directory.Exists(stale), "an aged, inactive run directory should still be pruned.");
        Assert.Equal(1, pruned);
    }

    [Fact]
    public void WithoutTheActiveDirectoryBeingDeclared_ItIsStillPruned()
    {
        // Pins the reason the parameter exists. A host that does not call
        // Logging.Initialize has a null Logging.RunDirectory, so the chassis
        // cannot infer which directory is live — it must be told. If this ever
        // starts passing, the inference works and the parameter can go.
        Environment.SetEnvironmentVariable("LOG_RETENTION_DAYS", "1");
        var active = AgedRunDir("undeclared");

        LogPruner.Prune(_root);

        Assert.False(Directory.Exists(active));
    }
}
