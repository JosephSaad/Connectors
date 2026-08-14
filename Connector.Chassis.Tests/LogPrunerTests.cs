// LogPrunerTests.cs
// -----------------
// LogPruner runs at the start of EVERY command and every --continuous cycle in
// all five connectors, and it deletes directories. Both halves of that make it
// unusually unforgiving: a crash here happens before the connector does any
// work, and a mistake here destroys log and audit evidence that nothing else
// can reconstruct. The tests below are therefore weighted towards the "refuse
// to act" paths (disabled, clamped, active run, ledger present) rather than the
// happy path, because those are the ones whose failure is silent.
//
// Every case pins the clock with the nowLocal parameter. A pruner test that
// leans on DateTime.Now is a test that starts failing on some future date, and
// the whole point of a run-dir timestamp regression is that it fails quietly.

using System.Reflection;

namespace Connector.Chassis.Tests;

/// <summary>
/// <see cref="LogPruner.Prune"/> — retention parsing, the calendar clamp, run-dir
/// name matching, the active-run guard and the decision-ledger guard.
///
/// Mutates LOG_RETENTION_DAYS, LOG_LEVEL, Logging.Root and Logging.RunDirectory,
/// all process-global; every one is restored. Joins the "EnvVars" collection so
/// it never interleaves with another env-mutating class if collection
/// parallelism is ever switched back on.
/// </summary>
[Collection("EnvVars")]
public sealed class LogPrunerTests : IDisposable
{
    /// <summary>
    /// Fixed local clock for every Prune call. Chosen at midnight so
    /// (Clock - DateTime.MinValue).TotalDays is an exact integer — the clamp
    /// boundary test below needs to name that integer without rounding slop.
    /// </summary>
    private static readonly DateTime Clock = new(2026, 3, 1, 0, 0, 0);

    /// <summary>Run-dir timestamp far enough before <see cref="Clock"/> to be prunable at any sane retention.</summary>
    private const string AgedStamp = "20200101_000000";

    private readonly string _root = Directory.CreateTempSubdirectory("chassis_log_pruner_").FullName;
    private readonly string? _savedRetention = Environment.GetEnvironmentVariable(LogPruner.RetentionEnvVar);
    private readonly string? _savedRunDirectory = Logging.RunDirectory;

    public LogPrunerTests()
    {
        // Ambient retention from the developer's shell (or a leaked test) would
        // turn "disabled by default" cases green for the wrong reason.
        Environment.SetEnvironmentVariable(LogPruner.RetentionEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(LogPruner.RetentionEnvVar, _savedRetention);
        SetLoggingRunDirectory(_savedRunDirectory);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp dir we could not remove is not a test failure.
        }
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static void Retention(string? value) =>
        Environment.SetEnvironmentVariable(LogPruner.RetentionEnvVar, value);

    /// <summary>
    /// Create a directory under the logs root, plus <c>connector.log</c> and any
    /// extra files named relative to it. Relative names use '/' for readability
    /// and are translated — Windows would treat the raw string as a filename.
    /// </summary>
    private string MakeRunDir(string name, params string[] files)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        foreach (var relative in new[] { "connector.log" }.Concat(files))
        {
            var path = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x\n");
        }
        return dir;
    }

    /// <summary>A file sitting directly in the logs root (state, checkpoints, the current ledger).</summary>
    private string MakeRootFile(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "{}\n");
        return path;
    }

    /// <summary>Log handler that keeps the formatted lines for assertion.</summary>
    private sealed class Collector : LogHandler
    {
        private readonly List<string> _lines = new();

        public IReadOnlyList<string> Lines
        {
            get { lock (_lines) return _lines.ToArray(); }
        }

        public void Add(string line)
        {
            lock (_lines) _lines.Add(line);
        }

        protected override void Emit(LogRecord record) => Add(Format(record));
    }

    /// <summary>
    /// Run <paramref name="action"/> with the chassis's log output captured.
    ///
    /// Two sinks, because the pruner's warnings are the only externally visible
    /// evidence that the clamp and the ledger guard fired at all, and which sink
    /// carries them depends on globals this test does not own. LogPruner resolves
    /// its logger ONCE, in its static initialiser, from whatever
    /// Chassis.Identity.BaseLoggerName happened to be when the type was first
    /// touched — i.e. from test ORDER across the assembly — so we attach to
    /// Logging.Root and rely on propagation instead of guessing the name. And
    /// Logging.Mode may already be Standard (Logging.Initialize sets it and there
    /// is no public way back), in which case records bypass handlers entirely and
    /// go to Logging.TestSink. Capturing both makes the assertion independent of
    /// both accidents.
    /// </summary>
    private static IReadOnlyList<string> CaptureLog(Action action)
    {
        var collector = new Collector();
        var savedTestSink = Logging.TestSink;
        var savedRootLevel = Logging.Root.Level;
        var savedLogLevel = Environment.GetEnvironmentVariable("LOG_LEVEL");
        Logging.Root.Level = LogLevels.Debug;          // Seismic-mode gate
        Environment.SetEnvironmentVariable("LOG_LEVEL", "DEBUG");  // Standard-mode floor
        Logging.Root.AddHandler(collector);
        Logging.TestSink = (_, _, message) => collector.Add(message);
        try
        {
            action();
        }
        finally
        {
            Logging.TestSink = savedTestSink;
            Logging.Root.RemoveHandler(collector);
            Logging.Root.Level = savedRootLevel;
            Environment.SetEnvironmentVariable("LOG_LEVEL", savedLogLevel);
        }
        return collector.Lines;
    }

    /// <summary>
    /// Assign <see cref="Logging.RunDirectory"/>, whose setter is private.
    ///
    /// WHY REFLECTION rather than the supported writer: the only public way to
    /// set RunDirectory is Logging.Initialize, and that permanently flips
    /// Logging.Mode to Standard — Mode has no public route back to Seismic, so
    /// one call here would silently change the log format and gating every other
    /// test in this assembly observes, for the rest of the process. Poking the
    /// property (and restoring it in Dispose) mutates exactly the one global
    /// these tests are about.
    /// </summary>
    private static void SetLoggingRunDirectory(string? value) =>
        typeof(Logging)
            .GetProperty(nameof(Logging.RunDirectory), BindingFlags.Public | BindingFlags.Static)!
            .SetValue(null, value);

    // ── LOG_RETENTION_DAYS parsing ───────────────────────────────────────────

    /// <summary>
    /// Retention is opt-in and fails CLOSED. Anything that is not a positive
    /// integer must read as 0/disabled rather than as some coerced number — a
    /// misparse in the other direction ("" → 0 days → cutoff = now) would delete
    /// every run directory on the box on the next command.
    /// </summary>
    [Theory]
    [InlineData(null)]      // unset: the default posture
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]       // documented "keep everything"
    [InlineData("-1")]
    [InlineData("-2147483648")]
    [InlineData("banana")]
    [InlineData("7.5")]     // no decimals: NumberStyles.Integer
    [InlineData("1e3")]
    [InlineData("1,000")]   // no group separators
    [InlineData("0x1E")]
    public void RetentionDaysIsZeroForEveryValueThatIsNotAPositiveInteger(string? raw)
    {
        Retention(raw);
        Assert.Equal(0, LogPruner.RetentionDays);
    }

    /// <summary>
    /// The tolerances that ARE part of the contract. Retention typically arrives
    /// from a compose file or a k8s manifest, where a trailing space or newline
    /// survives quoting; silently disabling retention because of invisible
    /// whitespace is a data-growth incident nobody would connect to the cause.
    /// </summary>
    [Theory]
    [InlineData("7", 7)]
    [InlineData(" 7 ", 7)]
    [InlineData("\t7\n", 7)]
    [InlineData("+7", 7)]
    [InlineData("2147483647", int.MaxValue)]
    public void RetentionDaysAcceptsPaddingAndAnExplicitSign(string raw, int expected)
    {
        Retention(raw);
        Assert.Equal(expected, LogPruner.RetentionDays);
    }

    /// <summary>
    /// Disabled means the pruner touches nothing at all, not merely that it
    /// computes a permissive cutoff. A five-year-old run directory survives.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("banana")]
    public void PruneIsAPureNoOpWhileRetentionIsDisabled(string? raw)
    {
        Retention(raw);
        var stale = MakeRunDir($"stale_{AgedStamp}");

        Assert.Equal(0, LogPruner.Prune(_root, Clock));
        Assert.True(Directory.Exists(stale));
    }

    // ── the calendar clamp ───────────────────────────────────────────────────

    /// <summary>
    /// now.AddDays(-int.MaxValue) throws ArgumentOutOfRangeException, and the
    /// cutoff is computed OUTSIDE Prune's try/catch — so before the clamp existed
    /// a single fat-fingered env var took the connector down at startup, before
    /// it did any work, on every command and every --continuous cycle. The clamp
    /// must absorb it and keep everything (the safe direction), and it must say
    /// so, because the operator who typed the number believes retention is on.
    /// </summary>
    [Theory]
    [InlineData("1000000")]
    [InlineData("2147483647")]
    public void AnUnexpressibleRetentionWindowClampsAndWarnsInsteadOfThrowing(string raw)
    {
        Retention(raw);
        var stale = MakeRunDir($"stale_{AgedStamp}");

        var pruned = 0;
        var lines = CaptureLog(() => pruned = LogPruner.Prune(_root, Clock));

        Assert.Equal(0, pruned);
        Assert.True(Directory.Exists(stale), "clamping must keep MORE, never delete more.");
        Assert.Contains(lines, l =>
            l.Contains(LogPruner.RetentionEnvVar, StringComparison.Ordinal)
            && l.Contains("nothing will ever be pruned", StringComparison.Ordinal));
    }

    /// <summary>
    /// The clamp has to sit exactly on the age of the calendar. One day early and
    /// it swallows legitimate (if silly) windows, permanently disabling retention
    /// for anyone who set one; one day late and the AddDays overflow is back.
    /// The largest window that must NOT warn is one day short of
    /// Clock - DateTime.MinValue; everything from there up must warn.
    /// </summary>
    [Fact]
    public void TheClampFiresExactlyWhenTheWindowOutrunsTheCalendar()
    {
        var calendarDays = (int)(Clock - DateTime.MinValue).TotalDays;

        Retention((calendarDays - 1).ToString());
        var below = CaptureLog(() => LogPruner.Prune(_root, Clock));
        Assert.DoesNotContain(below, l => l.Contains("nothing will ever be pruned", StringComparison.Ordinal));

        Retention(calendarDays.ToString());
        var at = CaptureLog(() => LogPruner.Prune(_root, Clock));
        Assert.Contains(at, l => l.Contains("nothing will ever be pruned", StringComparison.Ordinal));
    }

    // ── run-directory name matching ──────────────────────────────────────────

    /// <summary>
    /// The shapes the pruner is allowed to delete: {prefix}_{yyyyMMdd_HHmmss}
    /// with a prefix of letters, digits and hyphens. These are the real prefixes
    /// Seismic stamps — the only connector on this pruner today — so a regex
    /// change that stops matching them means retention silently stops running,
    /// and "silently" is the operative word: the pruner reports pruning 0
    /// directories exactly as it does when there is genuinely nothing to remove.
    /// </summary>
    [Theory]
    [InlineData("ingest")]
    [InlineData("reconcile")]
    [InlineData("full-deployment")]
    [InlineData("ingest-object")]
    [InlineData("identity-dry-run")]
    [InlineData("reacl2")]
    public void AgedRunDirectoriesWithARecognisedNameArePruned(string prefix)
    {
        Retention("7");
        var dir = MakeRunDir($"{prefix}_{AgedStamp}");

        Assert.Equal(1, LogPruner.Prune(_root, Clock));
        Assert.False(Directory.Exists(dir));
    }

    /// <summary>
    /// Everything else in the logs root is off limits. The pruner shares its root
    /// with directories operators create by hand (archive/, quarantine/) and with
    /// near-miss names; deleting one of those is unrecoverable, so the match is
    /// fully anchored and the timestamp must be a real date.
    /// </summary>
    [Theory]
    [InlineData("archive")]                        // operator's own folder
    [InlineData("quarantine-2026")]
    [InlineData("20200101_000000")]                // timestamp with no prefix
    [InlineData("ingest_20200101_000000_old")]     // suffixed by hand
    [InlineData("ingest_2020010_000000")]          // 7-digit date
    [InlineData("ingest_20200101_00000")]          // 5-digit time
    [InlineData("ingest.20200101_000000")]         // dot, not underscore
    [InlineData("ingest 20200101_000000")]         // space in the prefix
    [InlineData("ingest_99999999_999999")]         // right shape, not a date
    [InlineData("ingest_20200230_000000")]         // 30 February
    [InlineData("ingest_20201301_000000")]         // month 13
    [InlineData("ingest_20200101_250000")]         // hour 25
    public void DirectoriesThatAreNotRunDirectoriesAreNeverPruned(string name)
    {
        Retention("7");
        var dir = MakeRunDir(name);
        // Age it on disk too, so nothing but the NAME can be keeping it alive.
        Directory.SetLastWriteTime(dir, new DateTime(2019, 1, 1));

        Assert.Equal(0, LogPruner.Prune(_root, Clock));
        Assert.True(Directory.Exists(dir));
    }

    /// <summary>
    /// DOCUMENTS CURRENT BEHAVIOUR, AND IT IS A MIGRATION LANDMINE — see the
    /// defect report. The chassis prefix class is [A-Za-z0-9-]+, which excludes
    /// '_'. Seismic, the only connector on this pruner today, stamps hyphenated
    /// prefixes, so nothing is broken right now. But every private copy this
    /// pruner is meant to replace accepts underscores (Clarizen and Hadoop use
    /// "^(?&lt;prefix&gt;.+)_", Salesforce an unanchored "_(\d{8}_\d{6})$"), and those
    /// three stamp exactly the prefixes below. Moving any of them onto the shared
    /// pruner as it stands turns their retention off with no error, no warning
    /// and no failing test — it simply stops matching and reports "pruned 0".
    ///
    /// Pinned here so that widening the character class flips this assertion
    /// loudly, instead of the behaviour changing unnoticed in either direction.
    /// </summary>
    [Theory]
    [InlineData("ingest_object")]
    [InlineData("setup_connection")]
    [InlineData("retry_failed")]
    [InlineData("identity_dry_run")]
    [InlineData("validate_config")]
    public void RunDirectoriesWhosePrefixContainsAnUnderscoreAreNotMatched(string prefix)
    {
        Retention("1");
        var dir = MakeRunDir($"{prefix}_{AgedStamp}");

        Assert.Equal(0, LogPruner.Prune(_root, Clock));
        Assert.True(Directory.Exists(dir));
    }

    /// <summary>
    /// The pruner enumerates DIRECTORIES only. Everything the connectors keep at
    /// the logs root — sync state, checkpoints, the dead-letter queue, the
    /// current decision ledger — lives there precisely because it must outlive
    /// retention, and a file whose name happens to look like a run directory must
    /// not change that.
    /// </summary>
    [Fact]
    public void LooseFilesInTheLogsRootAreNeverTouched()
    {
        Retention("7");
        var survivors = new[]
        {
            MakeRootFile("sync_state.json"),
            MakeRootFile("checkpoint_ACL22032026.json"),
            MakeRootFile("failed_records_ACL22032026.jsonl"),
            MakeRootFile("decision_ledger_ACL22032026.jsonl"),
            MakeRootFile("decisions_hadoop-prod.jsonl"),
            MakeRootFile($"ingest_{AgedStamp}"),   // a FILE named like a run dir
        };
        foreach (var file in survivors)
            File.SetLastWriteTime(file, new DateTime(2019, 1, 1));
        var stale = MakeRunDir($"reconcile_{AgedStamp}");

        Assert.Equal(1, LogPruner.Prune(_root, Clock));

        Assert.False(Directory.Exists(stale));
        Assert.All(survivors, path => Assert.True(File.Exists(path), $"{path} was deleted"));
    }

    // ── cutoff arithmetic ────────────────────────────────────────────────────

    /// <summary>
    /// The cutoff is inclusive on the keep side (stamp >= cutoff survives). One
    /// second either way decides whether a directory lives, and the boundary is
    /// the difference between "N days of logs" and "N days minus the last run".
    /// </summary>
    [Fact]
    public void TheCutoffIsInclusiveOnTheKeepSide()
    {
        Retention("7");
        // Clock is 2026-03-01T00:00:00, so a 7-day window cuts at 2026-02-22T00:00:00.
        var onTheBoundary = MakeRunDir("ingest_20260222_000000");
        var oneSecondOlder = MakeRunDir("ingest_20260221_235959");

        Assert.Equal(1, LogPruner.Prune(_root, Clock));

        Assert.True(Directory.Exists(onTheBoundary));
        Assert.False(Directory.Exists(oneSecondOlder));
    }

    /// <summary>
    /// Age is judged by the name's timestamp against the SUPPLIED clock, never by
    /// mtime and never by the wall clock. Run directories are written to
    /// throughout a run, so an mtime-based pruner would keep whatever ran longest
    /// rather than whatever ran most recently.
    /// </summary>
    [Fact]
    public void AgeComesFromTheDirectoryNameAndTheSuppliedClock()
    {
        Retention("7");
        var freshNameStaleMtime = MakeRunDir("ingest_20260228_120000");
        Directory.SetLastWriteTime(freshNameStaleMtime, new DateTime(2001, 1, 1));
        var staleNameFreshMtime = MakeRunDir($"ingest_{AgedStamp}");
        Directory.SetLastWriteTime(staleNameFreshMtime, DateTime.Now);
        // Dated after the supplied clock: a clock skew must not read as "ancient".
        var future = MakeRunDir("ingest_20990101_000000");

        Assert.Equal(1, LogPruner.Prune(_root, Clock));

        Assert.True(Directory.Exists(freshNameStaleMtime));
        Assert.False(Directory.Exists(staleNameFreshMtime));
        Assert.True(Directory.Exists(future));
    }

    /// <summary>Omitting nowLocal falls back to the wall clock, which is still far past 2020.</summary>
    [Fact]
    public void TheDefaultClockIsTheWallClock()
    {
        Retention("1");
        var stale = MakeRunDir($"ingest_{AgedStamp}");

        Assert.Equal(1, LogPruner.Prune(_root));
        Assert.False(Directory.Exists(stale));
    }

    // ── the active-run guard ─────────────────────────────────────────────────

    /// <summary>
    /// The run directory is stamped once at startup, so a long-lived --continuous
    /// or service process eventually ages its OWN live directory past the cutoff
    /// and starts targeting it every cycle. On Windows the recursive delete takes
    /// the sibling artifacts and then throws on the locked .log; on macOS and
    /// Linux the unlink succeeds and every subsequent log line disappears into an
    /// unlinked inode with no error at all.
    /// </summary>
    [Fact]
    public void TheActiveRunDirectoryIsKeptEvenWhenItIsOlderThanRetention()
    {
        Retention("1");
        var active = MakeRunDir($"live_{AgedStamp}");
        var stale = MakeRunDir($"stale_{AgedStamp}");

        Assert.Equal(1, LogPruner.Prune(_root, Clock, active));

        Assert.True(Directory.Exists(active));
        Assert.False(Directory.Exists(stale));
    }

    /// <summary>
    /// Hosts that call Logging.Initialize need not pass activeRunDir; the pruner
    /// reads Logging.RunDirectory instead. Seismic — today's only caller — passes
    /// it explicitly precisely BECAUSE it never calls Initialize, so if this
    /// fallback is dropped every existing caller keeps working and the fleet's CI
    /// stays green while the guard quietly stops existing for the next host that
    /// relies on it. That is what this test is for.
    /// </summary>
    [Fact]
    public void TheActiveRunDirectoryFallsBackToLoggingRunDirectory()
    {
        Retention("1");
        var active = MakeRunDir($"live_{AgedStamp}");
        var stale = MakeRunDir($"stale_{AgedStamp}");
        SetLoggingRunDirectory(active);

        Assert.Equal(1, LogPruner.Prune(_root, Clock));

        Assert.True(Directory.Exists(active));
        Assert.False(Directory.Exists(stale));
    }

    /// <summary>
    /// activeRunDir REPLACES the fallback, it does not add to it: exactly one
    /// directory is ever exempt. Worth pinning in both directions — a stale
    /// Logging.RunDirectory left behind by another host must not pin a second
    /// directory forever, and a caller that passes the wrong path must not get an
    /// accidental extra layer of protection that hides the mistake.
    /// </summary>
    [Fact]
    public void AnExplicitActiveRunDirectoryReplacesTheLoggingFallback()
    {
        Retention("1");
        var declared = MakeRunDir($"declared_{AgedStamp}");
        var fromLogging = MakeRunDir($"logging_{AgedStamp}");
        SetLoggingRunDirectory(fromLogging);

        Assert.Equal(1, LogPruner.Prune(_root, Clock, declared));

        Assert.True(Directory.Exists(declared));
        Assert.False(Directory.Exists(fromLogging));
    }

    /// <summary>
    /// The guard compares FULL paths, so a caller that spells its run directory
    /// relatively, or with '.' / '..' segments, or with doubled separators, is
    /// still recognised. Hosts build this path by concatenation from config, so
    /// the spelling is not under the pruner's control.
    /// </summary>
    [Fact]
    public void TheActiveRunGuardNormalisesThePathBeforeComparing()
    {
        Retention("1");
        var name = $"live_{AgedStamp}";
        var active = MakeRunDir(name);
        var sep = Path.DirectorySeparatorChar;
        var roundabout = string.Join(sep, _root, ".", name, "..", name);

        Assert.Equal(0, LogPruner.Prune(_root, Clock, roundabout));
        Assert.True(Directory.Exists(active));
    }

    /// <summary>
    /// The compare is OrdinalIgnoreCase ON PURPOSE, and the choice is not
    /// symmetric in cost. On Windows and on a case-insensitive macOS volume,
    /// "LIVE_..." and "live_..." are the SAME directory, and a case-sensitive
    /// compare would let the pruner delete the live run. On Linux they would be
    /// two different directories and the guard over-protects one of them — which
    /// leaves a stale directory behind, an annoyance rather than data loss. This
    /// test is deliberately platform-independent: it asserts the comparison rule,
    /// not the filesystem's, so it means the same thing on both.
    /// </summary>
    [Fact]
    public void TheActiveRunGuardIgnoresCase()
    {
        Retention("1");
        var name = $"live_{AgedStamp}";
        var active = MakeRunDir(name);

        var shouted = Path.Combine(_root, name.ToUpperInvariant());
        Assert.Equal(0, LogPruner.Prune(_root, Clock, shouted));
        Assert.True(Directory.Exists(active));
    }

    /// <summary>
    /// A trailing separator is the one spelling Path.GetFullPath does NOT
    /// normalise away — on both Windows and POSIX it comes back with the slash
    /// still attached, while Directory.GetDirectories yields entries without one,
    /// so the OrdinalIgnoreCase compare misses and the live run directory becomes
    /// prunable. See the defect report: hosts that build the path with
    /// Path.Combine(root, name) + separator, or that read it from config where a
    /// trailing slash is idiomatic, silently lose the guard.
    ///
    /// The expectation is derived from GetFullPath at run time rather than
    /// hard-coded, so this asserts the RULE ("the guard is exactly a normalised
    /// full-path compare") and stays honest if a future runtime starts trimming
    /// the separator on one platform and not the other.
    /// </summary>
    [Fact]
    public void ATrailingSeparatorIsNotNormalisedAwayAndDefeatsTheGuard()
    {
        Retention("1");
        var active = MakeRunDir($"live_{AgedStamp}");
        var withSeparator = active + Path.DirectorySeparatorChar;
        var stillMatches = string.Equals(
            Path.GetFullPath(withSeparator), Path.GetFullPath(active), StringComparison.OrdinalIgnoreCase);

        var pruned = LogPruner.Prune(_root, Clock, withSeparator);

        Assert.Equal(stillMatches ? 0 : 1, pruned);
        Assert.Equal(stillMatches, Directory.Exists(active));
    }

    // ── the decision-ledger guard ────────────────────────────────────────────

    /// <summary>
    /// Both fleet naming conventions must be in the guard. Seismic, Salesforce
    /// and Altrata write decision_ledger_{id}.jsonl; Clarizen and Hadoop write
    /// decisions_{id}.jsonl. A guard carrying only the first reads as present in
    /// the code and covers half the fleet — the other half's tamper-evident audit
    /// records get deleted by a retention setting, which is exactly the outcome
    /// the guard exists to make impossible.
    /// </summary>
    [Fact]
    public void LedgerFilePatternsCoversBothFleetNamingConventions()
    {
        Assert.Contains("decision_ledger_*.jsonl", LogPruner.LedgerFilePatterns);
        Assert.Contains("decisions_*.jsonl", LogPruner.LedgerFilePatterns);
    }

    /// <summary>
    /// An aged run directory still holding a legacy per-run ledger is kept and
    /// NAMED, both conventions, nested or not. Silence here would be the worst
    /// outcome available: compliance evidence gone, and no record that it was.
    /// </summary>
    [Theory]
    [InlineData("decision_ledger_ACL22032026.jsonl")]
    [InlineData("decisions_hadoop-prod.jsonl")]
    [InlineData("decision_ledger_.jsonl")]                 // empty id still matches the glob
    [InlineData("nested/decision_ledger_seismic.jsonl")]   // SearchOption.AllDirectories
    [InlineData("a/b/c/decisions_clarizen.jsonl")]
    public void ARunDirectoryHoldingADecisionLedgerIsKeptAndNamed(string relativePath)
    {
        Retention("1");
        var dir = MakeRunDir($"ingest_{AgedStamp}", relativePath);
        var fileName = Path.GetFileName(relativePath);

        var pruned = 0;
        var lines = CaptureLog(() => pruned = LogPruner.Prune(_root, Clock));

        Assert.Equal(0, pruned);
        Assert.True(Directory.Exists(dir), "retention deleted a directory holding audit records.");
        Assert.Contains(lines, l =>
            l.Contains("KEPT", StringComparison.Ordinal)
            && l.Contains(dir, StringComparison.Ordinal)
            && l.Contains(fileName, StringComparison.Ordinal));
    }

    /// <summary>
    /// The other half of the guard, and the half that rots quietly: the patterns
    /// must NOT match the ordinary artifacts every run directory contains. A
    /// guard widened to (say) "*.jsonl" or "decision*" would protect every run
    /// directory that ever wrote a dead-letter record, and retention would appear
    /// to be configured while never deleting anything.
    /// </summary>
    [Theory]
    [InlineData("checkpoint_ACL22032026.json")]
    [InlineData("failed_records_ACL22032026.jsonl")]
    [InlineData("connector.log")]
    [InlineData("summary_ingest_20200101_000000.log")]
    [InlineData("reconciliation_report.json")]
    [InlineData("classification_manifest.jsonl")]
    [InlineData("decision_ledger.jsonl")]     // no '_{id}' — the glob needs the underscore
    [InlineData("decisions.jsonl")]
    [InlineData("my_decisions_x.jsonl")]      // the glob is anchored at the start of the name
    public void OrdinaryRunArtifactsDoNotBlockPruning(string relativePath)
    {
        Retention("1");
        var dir = MakeRunDir($"ingest_{AgedStamp}", relativePath);

        Assert.Equal(1, LogPruner.Prune(_root, Clock));
        Assert.False(Directory.Exists(dir));
    }

    /// <summary>
    /// The CURRENT ledger lives at the logs root, one level above the run
    /// directories, so it is outside the blast radius by construction — and it
    /// must not confer protection on the run directories beside it. If it did,
    /// every connector that has ever written a decision would stop pruning
    /// entirely the moment it wrote its first ledger line.
    /// </summary>
    [Fact]
    public void ALedgerAtTheLogsRootDoesNotProtectTheRunDirectoriesBesideIt()
    {
        Retention("1");
        var rootLedger = MakeRootFile("decision_ledger_ACL22032026.jsonl");
        var stale = MakeRunDir($"ingest_{AgedStamp}");

        Assert.Equal(1, LogPruner.Prune(_root, Clock));

        Assert.False(Directory.Exists(stale));
        Assert.True(File.Exists(rootLedger));
    }

    /// <summary>
    /// A ledger exempts only its OWN directory. Mixed roots are the normal case
    /// after the ledger moved to the logs root — a few old run dirs still hold
    /// one, the rest do not — and the count must reflect what was actually
    /// deleted so the operator's "removed N" line is not fiction.
    /// </summary>
    [Fact]
    public void PruneCountsOnlyTheDirectoriesItActuallyDeleted()
    {
        Retention("1");
        var active = MakeRunDir($"live_{AgedStamp}");
        var withLedger = MakeRunDir($"audited_{AgedStamp}", "decisions_clarizen.jsonl");
        var deletable1 = MakeRunDir($"ingest_{AgedStamp}");
        var deletable2 = MakeRunDir("reconcile_20200202_010101");
        var recent = MakeRunDir("ingest_20260228_000000");
        var notARunDir = MakeRunDir("archive");

        Assert.Equal(2, LogPruner.Prune(_root, Clock, active));

        Assert.False(Directory.Exists(deletable1));
        Assert.False(Directory.Exists(deletable2));
        Assert.True(Directory.Exists(active));
        Assert.True(Directory.Exists(withLedger));
        Assert.True(Directory.Exists(recent));
        Assert.True(Directory.Exists(notARunDir));
    }

    // ── never throws ─────────────────────────────────────────────────────────

    /// <summary>
    /// Prune is called before the connector does any work, so every failure mode
    /// it has must degrade to "pruned nothing". A missing logs root is the normal
    /// state of a first run; a path that is a file, or that cannot exist at all,
    /// is what a mis-set LOGS_DIR looks like.
    /// </summary>
    [Fact]
    public void PruneReturnsZeroRatherThanThrowingOnAnUnusableLogsRoot()
    {
        Retention("7");
        var missing = Path.Combine(_root, "no", "such", "place");
        var file = MakeRootFile("not-a-directory.txt");

        Assert.Equal(0, LogPruner.Prune(missing, Clock));
        Assert.Equal(0, LogPruner.Prune(file, Clock));
        Assert.Equal(0, LogPruner.Prune(string.Empty, Clock));
        Assert.True(File.Exists(file));
    }

    /// <summary>
    /// An open log file inside a doomed run directory, which is the exact shape
    /// of the active-run bug the guard above exists for. The two platforms
    /// genuinely differ and BOTH branches are asserted here rather than skipped,
    /// because the divergence is the justification for the guard:
    ///
    ///   Windows — the delete is refused while a handle is open, so the directory
    ///   survives, the pruner warns, and the run continues. Loud.
    ///   POSIX   — the unlink SUCCEEDS. The directory is gone, the writer keeps
    ///   writing into an unlinked inode, and every later log line is lost with no
    ///   error anywhere. Silent, and worse.
    ///
    /// Either way the sibling directory is still pruned and the count is honest:
    /// one bad directory must never abort the sweep.
    /// </summary>
    [Fact]
    public void OneUndeletableRunDirectoryNeverAbortsTheSweep()
    {
        Retention("1");
        var held = MakeRunDir($"held_{AgedStamp}");
        var deletable = MakeRunDir($"ingest_{AgedStamp}");

        var pruned = 0;
        using (File.Open(Path.Combine(held, "connector.log"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var lines = CaptureLog(() => pruned = LogPruner.Prune(_root, Clock));

            Assert.False(Directory.Exists(deletable), "the sweep stopped at the problem directory.");
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(1, pruned);
                Assert.True(Directory.Exists(held));
                Assert.Contains(lines, l => l.Contains("could not delete", StringComparison.Ordinal));
            }
            else
            {
                Assert.Equal(2, pruned);
                Assert.False(Directory.Exists(held));
                Assert.DoesNotContain(lines, l => l.Contains("could not delete", StringComparison.Ordinal));
            }
        }
    }
}
