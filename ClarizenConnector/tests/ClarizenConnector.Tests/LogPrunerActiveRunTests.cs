// LogPrunerActiveRunTests.cs
// --------------------------
// The pruner must never delete the run directory this process is writing into.
//
// The run directory is stamped once, by the first Logging.Initialize (later calls
// are no-ops), and never changes. PruneIfConfigured runs on every --continuous
// cycle. So after LOG_RETENTION_DAYS of uptime the active directory ages past the
// cutoff and the pruner starts targeting its own live logs, every cycle, forever.
//
// These tests fail on the unfixed tree on BOTH platforms, which needs a little
// care to arrange, because the two fail differently:
//
//   Windows  the recursive delete removes the directory's OTHER entries first —
//            summaries, reports, manifests — and only then throws on the locked
//            connector.log. The directory survives; its contents do not.
//   POSIX    the unlink simply succeeds. The directory is gone and the open
//            handle keeps writing into an unlinked inode, so every subsequent log
//            line is lost in silence.
//
// Asserting only on Directory.Exists would therefore pass on Windows against
// unfixed code. The assertions below are on an artifact INSIDE the active run
// directory, which is destroyed on both platforms.

using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Tests;

public class LogPrunerActiveRunTests : IDisposable
{
    private readonly TempDir _root = new();
    private readonly string? _previousRetention = Environment.GetEnvironmentVariable(LogPruner.RetentionEnvVar);
    private readonly string _previousLogsRoot = Logging.LogsRoot;

    public LogPrunerActiveRunTests() => Logging.ResetForTests();

    public void Dispose()
    {
        Logging.ResetForTests();
        Logging.LogsRoot = _previousLogsRoot;
        Environment.SetEnvironmentVariable(LogPruner.RetentionEnvVar, _previousRetention);
        _root.Dispose();
    }

    /// <summary>An inactive run directory old enough to be pruned, with one artifact in it.</summary>
    private string AgedRunDir(string prefix)
    {
        var dir = Path.Combine(_root.Path, $"{prefix}_20200101_000000");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "summary.txt"), "x");
        return dir;
    }

    /// <summary>
    /// Start a real run: chassis Logging.Initialize stamps the run directory and
    /// opens connector.log inside it, exactly as a service does at startup.
    /// </summary>
    private string StartRun()
    {
        Logging.LogsRoot = _root.Path;
        Logging.Initialize("ingest", verbose: false);
        var dir = Logging.RunDirectory;
        Assert.NotNull(dir);
        File.WriteAllText(Path.Combine(dir!, "summary.txt"), "live");
        return dir!;
    }

    [Fact]
    public void AfterLongUptime_TheLiveRunDirectoryIsNotPruned()
    {
        // The scenario, compressed: a service that has been up for well over the
        // retention window. Rather than back-date the directory (which would not
        // be the live one), the clock is moved forward — which is what actually
        // happens to a --continuous process.
        Environment.SetEnvironmentVariable(LogPruner.RetentionEnvVar, "1");
        var active = StartRun();
        var stale = AgedRunDir("ingest");

        var removed = LogPruner.PruneIfConfigured(_root.Path, nowLocal: DateTime.Now.AddDays(400));

        Assert.True(
            File.Exists(Path.Combine(active, "summary.txt")),
            "the pruner destroyed the contents of the run directory it is writing into. On POSIX the "
            + "directory is unlinked and every subsequent log line goes to a dead inode; on Windows the "
            + "other artifacts are deleted before the locked log file aborts the delete.");
        Assert.False(Directory.Exists(stale), "an aged, INACTIVE run directory should still be pruned.");
        Assert.Equal(1, removed);
    }

    [Fact]
    public void TheActiveDirectoryCanAlsoBeDeclaredExplicitly()
    {
        // Production relies on the Logging.RunDirectory default above. The
        // explicit parameter exists for hosts that manage their own run directory
        // and for tests; pinned so it does not rot.
        Environment.SetEnvironmentVariable(LogPruner.RetentionEnvVar, "1");
        var active = AgedRunDir("live");
        var stale = AgedRunDir("stale");

        var removed = LogPruner.PruneIfConfigured(
            _root.Path, nowLocal: DateTime.Now, activeRunDir: active);

        Assert.True(File.Exists(Path.Combine(active, "summary.txt")));
        Assert.False(Directory.Exists(stale));
        Assert.Equal(1, removed);
    }

    [Fact]
    public void TheGuardSurvivesAPathSpelledDifferently()
    {
        // The comparison is full-path normalised and case-insensitive on purpose.
        // A raw string compare only works while both paths happen to be spelled
        // the same way, and the two sides are built independently — one from
        // Directory.GetDirectories, one from Logging.RunDirectory — so a relative
        // root, a trailing separator or a case difference on Windows would make
        // the guard silently stop guarding.
        Environment.SetEnvironmentVariable(LogPruner.RetentionEnvVar, "1");
        var active = AgedRunDir("live");
        var awkward = Path.Combine(active, ".", "..", Path.GetFileName(active));

        LogPruner.PruneIfConfigured(_root.Path, nowLocal: DateTime.Now, activeRunDir: awkward);

        Assert.True(File.Exists(Path.Combine(active, "summary.txt")),
            "an unnormalised path defeated the active-run guard.");
    }

    [Fact]
    public void WithNoRunOpen_NothingIsSpared()
    {
        // Logging.RunDirectory is null before Initialize, so the guard is inert
        // and ordinary retention applies. Pins that the guard cannot accidentally
        // block all pruning.
        Environment.SetEnvironmentVariable(LogPruner.RetentionEnvVar, "1");
        var stale = AgedRunDir("stale");

        var removed = LogPruner.PruneIfConfigured(_root.Path, nowLocal: DateTime.Now);

        Assert.False(Directory.Exists(stale));
        Assert.Equal(1, removed);
    }
}
