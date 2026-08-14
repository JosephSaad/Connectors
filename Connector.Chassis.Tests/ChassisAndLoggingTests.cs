// ChassisAndLoggingTests.cs
// -------------------------
// The identity and logging core: Chassis.cs (Identity, LoggerFactory,
// CorrelationIdProvider) and Logging.cs (Initialize/RunDirectory, ParseLevel,
// the Standard sink's gating and render path).
//
// Everything here is process-global. Chassis.Identity, Chassis.LoggerFactory,
// Logging.Mode, Logging.RunDirectory, Logging.LogsRoot, LOG_LEVEL and LOG_FORMAT
// are all static, and the five connector suites plus every other file in this
// assembly read them. ChassisCoreGlobals below snapshots the lot on entry and
// puts it back on Dispose — including Logging.Mode, which has no public way back
// to Seismic (Logging.Configure would get there but only by closing and dropping
// the root logger's handlers, which is real collateral damage to whatever else is
// in the assembly). Every test in this file therefore opens with
// `using var g = new ChassisCoreGlobals();` unless it mutates nothing at all.

using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Connector.Chassis.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Chassis.Init / ChassisIdentity
// ─────────────────────────────────────────────────────────────────────────────

public class ChassisCoreIdentityTests
{
    [Fact]
    public void InitInstallsTheIdentityAndDerivesLoggerNamesFromTheBaseName()
    {
        using var g = new ChassisCoreGlobals();

        // All three fields are deliberately DIFFERENT. Four of the five
        // connectors set ConnectorId == BaseLoggerName ("clarizen_connector"),
        // so a refactor that derived the service/ledger names from ConnectorId
        // would pass every connector suite and only break here.
        Chassis.Init(new ChassisIdentity("zeta_connector", "Zeta Connector Service", "zeta"));

        Assert.Equal("zeta_connector", Chassis.Identity.ConnectorId);
        Assert.Equal("Zeta Connector Service", Chassis.Identity.EventLogSource);
        Assert.Equal("zeta", Chassis.Identity.BaseLoggerName);
        Assert.Equal("zeta.service", Chassis.Identity.ServiceLoggerName);
        Assert.Equal("zeta.ledger", Chassis.Identity.LedgerLoggerName);
    }

    [Fact]
    public void IdentityIsReadLiveByItsConsumersRatherThanSnapshotted()
    {
        using var g = new ChassisCoreGlobals();

        Chassis.Init(new ChassisIdentity("zeta_connector", "Zeta Connector Service", "zeta"));

        // These three are `=> Chassis.Identity.X` expression properties, so they
        // track a later Init. If any of them were ever changed to a cached
        // static readonly field, a host that calls Init after the type loads
        // would silently emit another connector's Event Log source / metric
        // prefix / ActivitySource name — cross-connector contamination that no
        // single-connector suite can see.
        Assert.Equal("Zeta Connector Service", EventLogSink.Source);
        Assert.Equal("zeta_connector_", MetricsRenderer.Prefix);
        Assert.Equal("Zeta Connector Service", Tracing.ActivitySourceName);
    }

    [Fact]
    public void InitIsLastWriterWinsAndHasNoOnceOnlyGuard()
    {
        using var g = new ChassisCoreGlobals();

        Chassis.Init(new ChassisIdentity("alpha_connector", "Alpha", "alpha"));
        Chassis.Init(new ChassisIdentity("beta_connector", "Beta", "beta"));

        // Unlike Logging.Initialize, Init does NOT latch the first caller. This
        // is the documented "call once at startup" contract, not a defect — but
        // it is worth pinning, because chassis components resolve their loggers
        // into `static readonly` fields at type load (CircuitBreaker.cs:102,
        // DecisionLedger.cs:161, ...). A second Init after any component has
        // been touched re-points Identity while those cached logger names keep
        // the FIRST identity, and nothing anywhere reports the divergence.
        Assert.Equal(new ChassisIdentity("beta_connector", "Beta", "beta"), Chassis.Identity);
        Assert.Equal("beta.service", Chassis.Identity.ServiceLoggerName);
        Assert.Equal("beta.ledger", Chassis.Identity.LedgerLoggerName);
    }

    [Fact]
    public void DerivedNamesArePlainSuffixesSoADottedBaseNameNestsInTheLoggerHierarchy()
    {
        // Pure record construction — no global state touched, no scope needed.
        // The derived names are fed straight to Logging.GetLogger, whose parent
        // lookup splits on '.', so a dotted base name puts the service/ledger
        // loggers UNDER it and they inherit its level. Callers configure a whole
        // connector's verbosity by levelling the base logger; that only works
        // while the suffixes stay plain concatenation.
        Assert.Equal("a.b.service", new ChassisIdentity("c", "S", "a.b").ServiceLoggerName);
        Assert.Equal("a.b.ledger", new ChassisIdentity("c", "S", "a.b").LedgerLoggerName);

        // No validation: an empty base name yields loggers named ".service"
        // (which resolve to the root logger's level). Documented, not endorsed.
        Assert.Equal(".service", new ChassisIdentity("c", "S", "").ServiceLoggerName);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Chassis.LoggerFactory — the seam that let Salesforce (a CPython `logging`
// port, not a variant of this logger) adopt chassis components at all.
// ─────────────────────────────────────────────────────────────────────────────

public class ChassisCoreLoggerFactorySeamTests
{
    [Fact]
    public void GetLoggerConsultsTheHostFactoryOnEveryCallAndNeverCaches()
    {
        using var g = new ChassisCoreGlobals();

        var asked = new List<string>();
        Chassis.LoggerFactory = name =>
        {
            asked.Add(name);
            return new ChassisCoreRecordingLogger(name);
        };

        var first = Chassis.GetLogger("alpha");
        var second = Chassis.GetLogger("alpha");

        // Two calls, two factory invocations. If the chassis ever memoised the
        // result, a host that swaps its factory (or whose factory returns
        // per-scope loggers) would keep getting the stale one.
        Assert.Equal(new[] { "alpha", "alpha" }, asked);
        Assert.NotSame(first, second);
        Assert.IsType<ChassisCoreRecordingLogger>(first);
        Assert.Equal("alpha", first.Name);
    }

    [Fact]
    public void WithNoHostFactoryTheSeamResolvesToTheBuiltInChassisLogger()
    {
        using var g = new ChassisCoreGlobals();

        // This assignment is byte-for-byte the field initializer on
        // Chassis.LoggerFactory, i.e. what an unwired host runs with. The point
        // of the assertion is that the fallback is not merely "a LoggerObject"
        // but the SAME cache entry Logging.GetLogger hands out — same handlers,
        // same level, same parent chain. A fallback that minted a fresh logger
        // would silently detach chassis components from the host's handler
        // configuration.
        Chassis.LoggerFactory = Logging.GetLogger;

        var viaSeam = Chassis.GetLogger("chassiscore.seam.fallback");

        Assert.IsType<LoggerObject>(viaSeam);
        Assert.Same(Logging.GetLoggerObject("chassiscore.seam.fallback"), viaSeam);
    }

    [Fact]
    public void ABrokenHostFactoryFailsLoudlyInsteadOfFallingBackToChassisLogging()
    {
        using var g = new ChassisCoreGlobals();

        Chassis.LoggerFactory = _ => throw new InvalidOperationException("host factory not ready");

        // No defensive try/catch in Chassis.GetLogger, and that is the correct
        // shape: components resolve their logger in a static field initializer,
        // so a broken factory surfaces as a TypeInitializationException at
        // startup. A "helpful" catch that fell back to Logging.GetLogger would
        // reintroduce exactly the silent change of log format this seam exists
        // to prevent — Salesforce would start emitting chassis-format lines with
        // nothing failing.
        var ex = Assert.Throws<InvalidOperationException>(() => Chassis.GetLogger("boom"));
        Assert.Equal("host factory not ready", ex.Message);
    }

    [Fact]
    public void TheFactoryReceivesExactlyTheNamesChassisComponentsDeriveFromIdentity()
    {
        using var g = new ChassisCoreGlobals();

        var asked = new List<string>();
        Chassis.LoggerFactory = name =>
        {
            asked.Add(name);
            return new ChassisCoreRecordingLogger(name);
        };
        Chassis.Init(new ChassisIdentity("zeta_connector", "Zeta", "zeta"));

        // The exact call shapes at ServiceHost.cs:65, DecisionLedger.cs:161 and
        // CircuitBreaker.cs:102. A host's factory dispatches on these strings
        // (Salesforce maps them onto its own handler tree), so renaming a
        // derived logger is a breaking change for every host, not a cosmetic
        // one — this test is where that gets noticed.
        Chassis.GetLogger(Chassis.Identity.ServiceLoggerName);
        Chassis.GetLogger(Chassis.Identity.LedgerLoggerName);
        Chassis.GetLogger($"{Chassis.Identity.BaseLoggerName}.breaker");

        Assert.Equal(new[] { "zeta.service", "zeta.ledger", "zeta.breaker" }, asked);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Chassis.CorrelationIdProvider on emitted records — both emission paths.
// ─────────────────────────────────────────────────────────────────────────────

public class ChassisCoreCorrelationTests
{
    [Fact]
    public void StandardTextLinesCarryTheFirstEightCharsOfTheCorrelationId()
    {
        using var g = new ChassisCoreGlobals();
        g.UseStandardMode();
        Chassis.CorrelationIdProvider = () => "0123456789abcdef";
        var rendered = new List<string>();
        Logging.RenderedSink = rendered.Add;

        Logging.GetLogger("chassiscore.corr.text").Error("boom");

        // The whole operator-visible text contract in one assertion: timestamp,
        // level padded to 7 columns, an 8-char correlation prefix in brackets,
        // logger, message. Alert rules and log greps are written against this
        // shape; any drift here changes them silently.
        var line = Assert.Single(rendered);
        Assert.Matches(
            @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} \[ERROR  \] \[01234567\] chassiscore\.corr\.text: boom$",
            line);
        Assert.DoesNotContain("89abcdef", line);  // truncated, not embedded whole
    }

    [Fact]
    public void ANullOrUnsetCorrelationProviderOmitsThePrefixEntirely()
    {
        using var g = new ChassisCoreGlobals();
        g.UseStandardMode();
        var rendered = new List<string>();
        Logging.RenderedSink = rendered.Add;
        var log = Logging.GetLogger("chassiscore.corr.none");

        // Outside a correlation scope the provider returns null; a host that
        // never wires one leaves the delegate null. Both must render the same
        // prefix-free line — an empty "[] " prefix would break the same greps.
        Chassis.CorrelationIdProvider = () => null;
        log.Error("boom");
        Chassis.CorrelationIdProvider = null;
        log.Error("boom");

        Assert.Equal(2, rendered.Count);
        Assert.All(rendered, line => Assert.Matches(
            @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} \[ERROR  \] chassiscore\.corr\.none: boom$", line));
    }

    [Fact]
    public void TheProviderIsInvokedOncePerEmittedRecordAndNotAtAllForDroppedOnes()
    {
        using var g = new ChassisCoreGlobals();
        g.UseStandardMode();
        Environment.SetEnvironmentVariable("LOG_LEVEL", "ERROR");
        var calls = 0;
        Chassis.CorrelationIdProvider = () => { calls++; return "aaaaaaaa"; };
        var rendered = new List<string>();
        Logging.RenderedSink = rendered.Add;
        var log = Logging.GetLogger("chassiscore.corr.count");

        log.Warning("below the floor");

        // Gate first, provider second. Hosts wire this to an AsyncLocal scope
        // lookup; invoking it for records that are about to be discarded turns a
        // suppressed log level into per-record work on the hot path.
        Assert.Equal(0, calls);
        Assert.Empty(rendered);

        log.Error("one");
        log.Error("two");

        // Re-read per record, not captured once at GetLogger time: a logger
        // cached in a static field must still pick up the CURRENT crawl cycle.
        Assert.Equal(2, calls);
        Assert.Equal(2, rendered.Count);
    }

    [Fact]
    public void StandardJsonLinesStampTheFullCorrelationIdAndOmitTheFieldWhenNull()
    {
        using var g = new ChassisCoreGlobals();
        g.UseStandardMode();
        Environment.SetEnvironmentVariable("LOG_FORMAT", "json");
        var rendered = new List<string>();
        Logging.RenderedSink = rendered.Add;
        var log = Logging.GetLogger("chassiscore.corr.json");

        Chassis.CorrelationIdProvider = () => "0123456789abcdef";
        log.Info("hello");
        Chassis.CorrelationIdProvider = () => null;
        log.Info("quiet");

        Assert.Equal(2, rendered.Count);
        var stamped = JsonDocument.Parse(rendered[0]).RootElement;
        Assert.Equal("INFO", stamped.GetProperty("level").GetString());
        Assert.Equal("chassiscore.corr.json", stamped.GetProperty("logger").GetString());
        Assert.Equal("hello", stamped.GetProperty("message").GetString());
        // Unlike the text renderer, json keeps the id WHOLE — a log pipeline
        // joins on this field, so truncating it here would break the join.
        Assert.Equal("0123456789abcdef", stamped.GetProperty("correlation_id").GetString());
        // ISO-8601 round-trip in UTC ("o"), not local time: ingestion pipelines
        // parse it without a timezone hint.
        var timestamp = DateTimeOffset.Parse(
            stamped.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        Assert.Equal(TimeSpan.Zero, timestamp.Offset);

        // Omitted, not null-valued: a consumer distinguishes "outside a scope"
        // from "scope with a null id" by the key's absence.
        Assert.False(JsonDocument.Parse(rendered[1]).RootElement
            .TryGetProperty("correlation_id", out _));
    }

    [Fact]
    public void ACorrelationIdShorterThanEightCharsThrowsOutOfTheTextRenderer()
    {
        using var g = new ChassisCoreGlobals();
        g.UseStandardMode();
        Chassis.CorrelationIdProvider = () => "short";  // 5 chars
        var seamSaw = new List<string>();
        var mirrorSaw = new List<string>();
        var rendered = new List<string>();
        Logging.TestSink = (_, _, message) => seamSaw.Add(message);
        Logging.EventLogMirrorHook = (_, _, message) => mirrorSaw.Add(message);
        Logging.RenderedSink = rendered.Add;
        var log = Logging.GetLogger("chassiscore.corr.short");

        // DEFECT, documented not fixed: Logging.DefaultRender slices
        // `e.CorrelationId[..8]` with no length check and Logging.Write does not
        // wrap dialect.Render, so the exception escapes into the CALLER. A host
        // whose correlation ids are shorter than 8 chars takes down whatever was
        // logging. Reported separately; this test pins today's behaviour.
        Assert.Throws<ArgumentOutOfRangeException>(() => log.Error("boom"));

        // ...and it half-emits: the test seam and the Windows Event Log mirror
        // both fire BEFORE the render, so an operator sees an Event Log entry
        // for a line that never reached the log file or the console.
        Assert.Equal(new[] { "boom" }, seamSaw);
        Assert.Equal(new[] { "boom" }, mirrorSaw);
        Assert.Empty(rendered);

        // Only the text renderer truncates, so json mode survives the same id.
        Environment.SetEnvironmentVariable("LOG_FORMAT", "json");
        log.Error("boom");
        Assert.Equal("short", JsonDocument.Parse(Assert.Single(rendered)).RootElement
            .GetProperty("correlation_id").GetString());
    }

    [Fact]
    public void SeismicJsonRecordsAlsoCarryTheCorrelationIdButMessageOnlyHandlersNever()
    {
        using var g = new ChassisCoreGlobals();
        g.UseSeismicMode();
        Logging.JsonFormat = true;
        Chassis.CorrelationIdProvider = () => "seismic-corr";

        // The handler/propagation path is the OTHER emission route (Seismic
        // never calls Logging.Initialize), and it reads the provider from its
        // own formatter. Both routes must honour the same host wiring.
        var full = new ChassisCoreCapturingHandler();
        var progress = new ChassisCoreCapturingHandler { MessageOnly = true };
        var log = Logging.GetLoggerObject("chassiscore.corr.seismic");
        try
        {
            log.Level = LogLevels.Debug;
            log.Propagate = false;  // keep this off whatever the root logger has
            log.AddHandler(full);
            log.AddHandler(progress);

            log.Info("hello");

            var json = JsonDocument.Parse(Assert.Single(full.Lines)).RootElement;
            Assert.Equal("INFO", json.GetProperty("level").GetString());
            Assert.Equal("chassiscore.corr.seismic", json.GetProperty("logger").GetString());
            Assert.Equal("seismic-corr", json.GetProperty("correlation_id").GetString());

            // MessageOnly is the progress console: it stays bare in json mode,
            // so a progress bar never turns into a stream of JSON objects.
            Assert.Equal("hello", Assert.Single(progress.Lines));

            full.Lines.Clear();
            Chassis.CorrelationIdProvider = null;
            log.Info("hello");
            Assert.False(JsonDocument.Parse(Assert.Single(full.Lines)).RootElement
                .TryGetProperty("correlation_id", out _));
        }
        finally
        {
            // Loggers live forever in a static dictionary — leaving handlers or
            // a level on this one would leak into every later test.
            log.RemoveHandler(full);
            log.RemoveHandler(progress);
            log.Level = LogLevels.NotSet;
            log.Propagate = true;
        }
    }

    [Fact]
    public void StandardWriteRunsSeamMirrorAndRenderInThatOrderOverTheComposedMessage()
    {
        using var g = new ChassisCoreGlobals();
        g.UseStandardMode();
        var order = new List<string>();
        Logging.TestSink = (_, _, message) => order.Add("seam:" + message);
        Logging.EventLogMirrorHook = (_, _, message) => order.Add("mirror:" + message);
        Logging.RenderedSink = _ => order.Add("render");

        Logging.GetLogger("chassiscore.order").Error("boom", new InvalidOperationException("bad"));

        // Ordering is a contract, not an accident: the Event Log mirror must see
        // the same composed text the file gets (exception folded onto a second
        // line), and it must see it even if rendering later fails. Reordering
        // render ahead of the mirror would lose Event Log entries for exactly
        // the records that are hardest to render.
        Assert.Equal(3, order.Count);
        Assert.StartsWith("seam:boom\n", order[0]);
        Assert.StartsWith("mirror:boom\n", order[1]);
        Assert.Contains("InvalidOperationException", order[1]);
        Assert.Equal("render", order[2]);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Logging.Initialize / RunDirectory
// ─────────────────────────────────────────────────────────────────────────────

public class ChassisCoreInitializeTests
{
    [Fact]
    public void InitializeOpensAStampedRunDirectoryAndConnectorLog()
    {
        using var g = new ChassisCoreGlobals();
        var root = g.NewTempDir();
        Logging.LogsRoot = root;
        Logging.HardenDirectoryHook = d => Directory.CreateDirectory(d);

        Logging.Initialize("chassiscore", verbose: false);

        Assert.Equal(LoggingMode.Standard, Logging.Mode);
        Assert.Equal(LogLevel.Warning, Logging.ConsoleLevel);
        var dir = Logging.RunDirectory;
        Assert.NotNull(dir);
        // Path.GetFileName/GetDirectoryName rather than splitting on '/': this
        // has to hold on Windows too.
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(root),
            Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(dir)!));
        Assert.Matches(@"^chassiscore_\d{8}_\d{6}$", Path.GetFileName(dir));

        var logFile = Path.Combine(dir, "connector.log");
        Assert.True(File.Exists(logFile));

        Logging.GetLogger("chassiscore.init").Error("marker");
        Assert.Contains("marker", ChassisCoreGlobals.ReadWhileOpen(logFile));
    }

    [Fact]
    public void ASecondInitializeDoesNotRestampTheRunDirectory()
    {
        using var g = new ChassisCoreGlobals();
        var root = g.NewTempDir();
        Logging.LogsRoot = root;
        Logging.HardenDirectoryHook = d => Directory.CreateDirectory(d);

        Logging.Initialize("first", verbose: false);
        var firstRun = Logging.RunDirectory!;

        // A DIFFERENT prefix on the second call. Asserting equality with the
        // same prefix would pass on a broken implementation whenever both calls
        // land in the same wall-clock second, which is almost always.
        Logging.Initialize("second", verbose: true);

        Assert.Equal(firstRun, Logging.RunDirectory);
        Assert.Empty(Directory.GetDirectories(root, "second_*"));

        // The first call's file handle is still THE sink. Four connectors and
        // LogPruner's active-run guard depend on this: a restamp would orphan
        // the open handle and expose the live run to the pruner.
        Logging.GetLogger("chassiscore.init.idempotent").Error("after-second");
        Assert.Contains(
            "after-second",
            ChassisCoreGlobals.ReadWhileOpen(Path.Combine(firstRun, "connector.log")));

        // DEFECT, documented not fixed: Mode and ConsoleLevel are assigned
        // BEFORE the `if (_clarizenFileWriter != null) return;` guard, so the
        // second call is not the no-op its docstring promises — verbose:true
        // has flipped the process-wide console threshold to DEBUG.
        Assert.Equal(LogLevel.Debug, Logging.ConsoleLevel);
    }

    [Fact]
    public void WhenTheRunDirectoryCannotBeOpenedInitializeFallsBackToConsoleAndStopsBeingIdempotent()
    {
        using var g = new ChassisCoreGlobals();
        // No HardenDirectoryHook (the hook is what CREATES the directory), and a
        // LogsRoot two levels below anything that exists, so the StreamWriter
        // open fails with DirectoryNotFoundException on Windows and POSIX alike.
        Logging.LogsRoot = Path.Combine(g.NewTempDir(), "does-not-exist", "nested");
        Logging.HardenDirectoryHook = null;

        Logging.Initialize("alpha", verbose: false);

        Assert.Equal(LoggingMode.Standard, Logging.Mode);
        Assert.Contains("continuing with console-only logging", g.Stderr.ToString());
        // RunDirectory is assigned before the writer opens and is NOT rolled
        // back by the catch, so it now names a directory that was never created.
        Assert.NotNull(Logging.RunDirectory);
        Assert.StartsWith("alpha_", Path.GetFileName(Logging.RunDirectory));
        Assert.False(Directory.Exists(Logging.RunDirectory));

        Logging.Initialize("beta", verbose: false);

        // DEFECT, documented not fixed: the idempotence guard is keyed on the
        // FILE WRITER, which is still null on the fallback path, so every later
        // Initialize re-runs and restamps RunDirectory. LogPruner's active-run
        // exclusion reads Logging.RunDirectory, so on a host that cannot create
        // its log directory the "active run" moves under the pruner's feet.
        Assert.StartsWith("beta_", Path.GetFileName(Logging.RunDirectory));
    }

    [Fact]
    public void ResetForTestsReleasesTheFileHandleButKeepsModeAndHooks()
    {
        using var g = new ChassisCoreGlobals();
        var root = g.NewTempDir();
        Logging.LogsRoot = root;
        Logging.HardenDirectoryHook = d => Directory.CreateDirectory(d);
        Logging.Initialize("chassiscore", verbose: true);
        var dir = Logging.RunDirectory!;
        Logging.TestSink = (_, _, _) => { };
        Logging.RenderedSink = _ => { };

        Logging.ResetForTests();

        Assert.Null(Logging.RunDirectory);
        Assert.Null(Logging.TestSink);
        Assert.Null(Logging.RenderedSink);
        Assert.Equal(LogLevel.Warning, Logging.ConsoleLevel);  // was Debug (verbose)
        // Mode and the hooks deliberately SURVIVE: the connector suites wire
        // them once from a [ModuleInitializer] and call ResetForTests between
        // cases. Clearing them here would silently drop those suites back into
        // Seismic mode for every test after the first reset.
        Assert.Equal(LoggingMode.Standard, Logging.Mode);
        Assert.NotNull(Logging.HardenDirectoryHook);

        // The writer is closed, not just forgotten. On Windows the StreamWriter
        // holds the file with FileShare.Read, which excludes delete, so this
        // line throws IOException if the handle leaked. (On POSIX the unlink
        // would succeed either way — this assertion only bites on Windows.)
        File.Delete(Path.Combine(dir, "connector.log"));

        // And the next Initialize opens a genuinely new run.
        Logging.Initialize("chassiscore2", verbose: false);
        Assert.NotNull(Logging.RunDirectory);
        Assert.NotEqual(dir, Logging.RunDirectory);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Logging.ParseLevel and level gating
// ─────────────────────────────────────────────────────────────────────────────

public class ChassisCoreLevelTests
{
    [Theory]
    [InlineData("DEBUG", LogLevel.Debug)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("Info", LogLevel.Info)]
    [InlineData("INFO", LogLevel.Info)]
    [InlineData("WARNING", LogLevel.Warning)]
    [InlineData("warning", LogLevel.Warning)]
    [InlineData("WARN", LogLevel.Warning)]
    [InlineData("warn", LogLevel.Warning)]
    [InlineData("ERROR", LogLevel.Error)]
    [InlineData("error", LogLevel.Error)]
    public void ParseLevelMapsTheDocumentedNamesCaseInsensitively(string raw, LogLevel expected)
    {
        // LOG_LEVEL is set by hand in service definitions and .env files, so the
        // accepted spellings (including the WARN alias) are an operator-facing
        // contract. Pure function — nothing global to restore.
        Assert.Equal(expected, Logging.ParseLevel(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TRACE")]
    [InlineData("FATAL")]
    [InlineData("CRITICAL")]
    [InlineData(" WARNING")]
    [InlineData("WARNING ")]
    [InlineData("30")]
    public void ParseLevelFallsBackToDebugForEverythingElseWhichRemovesTheFloorEntirely(string? raw)
    {
        // Debug is the "no extra floor" sentinel: DefaultEffectiveLevel takes
        // max(floor, sinks), so Debug never raises anything. Two traps live in
        // this list and both are silent — LOG_LEVEL=CRITICAL (a real level name
        // in LogLevels and in Python, set by an operator trying to go QUIET)
        // and a stray space from a .env file both produce the most verbose
        // setting available rather than the intended one.
        Assert.Equal(LogLevel.Debug, Logging.ParseLevel(raw));
    }

    [Fact]
    public void ParseLevelIsCultureInvariant()
    {
        CultureInfo turkish;
        try
        {
            turkish = new CultureInfo("tr-TR");
        }
        catch (CultureNotFoundException)
        {
            return;  // globalization-invariant runtime: nothing to guard here
        }

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = turkish;
            // ToUpperInvariant, not ToUpper: under tr-TR the latter maps 'i' to
            // 'İ', so "info"/"warning" would miss every switch arm and every
            // Turkish-locale host would silently lose its LOG_LEVEL floor.
            Assert.Equal(LogLevel.Info, Logging.ParseLevel("info"));
            Assert.Equal(LogLevel.Warning, Logging.ParseLevel("warning"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(null, LogLevel.Warning, LogLevel.Warning)]
    [InlineData(null, LogLevel.Debug, LogLevel.Debug)]
    [InlineData("ERROR", LogLevel.Debug, LogLevel.Error)]
    [InlineData("INFO", LogLevel.Warning, LogLevel.Warning)]
    [InlineData("WARNING", LogLevel.Debug, LogLevel.Warning)]
    [InlineData("nonsense", LogLevel.Warning, LogLevel.Warning)]
    public void WithNoRunDirectoryTheEffectiveLevelIsTheHigherOfTheFloorAndTheConsole(
        string? logLevel, LogLevel consoleLevel, LogLevel expected)
    {
        using var g = new ChassisCoreGlobals();
        g.UseStandardMode();
        Logging.ResetForTests();  // guarantee no file writer is open
        Environment.SetEnvironmentVariable("LOG_LEVEL", logLevel);
        Logging.ConsoleLevel = consoleLevel;  // set AFTER the reset, which forces Warning

        // Console-only: the console threshold is the whole story unless
        // LOG_LEVEL raises it. LOG_LEVEL can only ever raise — "INFO" against a
        // WARNING console still yields WARNING.
        Assert.Equal(expected, Logging.EffectiveLevel());
    }

    [Fact]
    public void AnOpenRunDirectoryDropsTheEffectiveLevelToDebugUnlessLogLevelRaisesIt()
    {
        using var g = new ChassisCoreGlobals();
        g.UseStandardMode();
        Logging.LogsRoot = g.NewTempDir();
        Logging.HardenDirectoryHook = d => Directory.CreateDirectory(d);

        Logging.Initialize("chassiscore", verbose: false);

        // The file sink captures DEBUG+ whenever a run is open, so the hot-path
        // gate must stop advertising WARNING or callers will skip building the
        // very DEBUG messages the run log exists to hold.
        Assert.Equal(LogLevel.Warning, Logging.ConsoleLevel);
        Assert.Equal(LogLevel.Debug, Logging.EffectiveLevel());

        Environment.SetEnvironmentVariable("LOG_LEVEL", "ERROR");
        // LOG_LEVEL is a floor for BOTH sinks, not just the console.
        Assert.Equal(LogLevel.Error, Logging.EffectiveLevel());
    }

    [Fact]
    public void TheHotPathGateAdvertisesAHigherLevelThanTheSinkActuallyEnforces()
    {
        using var g = new ChassisCoreGlobals();
        g.UseStandardMode();
        Logging.ResetForTests();
        Logging.ConsoleLevel = LogLevel.Warning;
        var seen = new List<string>();
        Logging.TestSink = (_, _, message) => seen.Add(message);
        var log = Logging.GetLogger("chassiscore.gate");

        // EffectiveLevel (WARNING here) is only an advertisement; the sink gate
        // is SinkFloor, which with LOG_LEVEL unset is DEBUG. So IsEnabledFor
        // says "don't bother" for a record the sink would happily have taken.
        // It is documented in StandardLogDialect.SinkFloor and it is load
        // bearing — a caller that trusts the gate drops INFO lines that would
        // otherwise be in the operator's file. Pinned so nobody "fixes" one
        // half without the other.
        Assert.False(log.IsEnabledFor(LogLevel.Info));
        log.Info("still written");
        Assert.Equal(new[] { "still written" }, seen);
    }

    [Fact]
    public void TheLogLevelFloorDropsRecordsBeforeAnySeamAndPerLoggerLevelsAreIgnored()
    {
        using var g = new ChassisCoreGlobals();
        g.UseStandardMode();
        Environment.SetEnvironmentVariable("LOG_LEVEL", "ERROR");
        var seam = 0;
        var mirror = 0;
        var render = 0;
        var correlation = 0;
        Logging.TestSink = (_, _, _) => seam++;
        Logging.EventLogMirrorHook = (_, _, _) => mirror++;
        Logging.RenderedSink = _ => render++;
        Chassis.CorrelationIdProvider = () => { correlation++; return null; };
        var log = Logging.GetLoggerObject("chassiscore.floor");
        try
        {
            log.Warning("dropped");

            // Nothing downstream runs for a suppressed record — in particular
            // the Event Log mirror stays silent, so raising LOG_LEVEL really
            // does quieten the Windows Event Log and not just the file.
            Assert.Equal(0, seam + mirror + render + correlation);

            log.Error("kept");
            Assert.Equal(1, seam);
            Assert.Equal(1, mirror);
            Assert.Equal(1, render);
            Assert.Equal(1, correlation);

            // Standard mode routes straight to Logging.Write and never consults
            // the LoggerObject's own Level — this logger is pinned to ERROR and
            // still emits INFO. Callers coming from the Seismic/Python side
            // expect per-logger levels to work; in Standard mode LOG_LEVEL is
            // the only knob.
            log.Level = LogLevels.Error;
            Environment.SetEnvironmentVariable("LOG_LEVEL", null);
            log.Info("per-logger level ignored");
            Assert.Equal(2, seam);
        }
        finally
        {
            log.Level = LogLevels.NotSet;  // the logger cache outlives this test
        }
    }

    [Fact]
    public void InSeismicModeTheTypedGateUsesTheHandlerEffectiveLevelNotTheConsoleThreshold()
    {
        using var g = new ChassisCoreGlobals();
        g.UseSeismicMode();
        Logging.ConsoleLevel = LogLevel.Debug;  // a Standard knob — must not leak in
        var log = Logging.GetLoggerObject("chassiscore.seismic.gate");
        try
        {
            log.Level = LogLevels.Error;

            // IsEnabledFor(LogLevel) has two implementations behind one
            // signature. Picking the wrong branch would make Seismic callers
            // format messages their handlers then discard, or (worse) skip ones
            // the handlers wanted.
            Assert.False(log.IsEnabledFor(LogLevel.Warning));
            Assert.True(log.IsEnabledFor(LogLevel.Error));
        }
        finally
        {
            log.Level = LogLevels.NotSet;
        }
    }

    [Fact]
    public void EffectiveLevelResolvesAnAncestorCreatedAfterTheChild()
    {
        using var g = new ChassisCoreGlobals();
        g.UseSeismicMode();
        Logging.Root.Level = LogLevels.Warning;  // restored by the scope
        var child = Logging.GetLoggerObject("chassiscore.ancestor.late.child");
        var parent = Logging.GetLoggerObject("chassiscore.ancestor.late");
        try
        {
            Assert.False(child.IsEnabledFor(LogLevels.Info));  // falls through to root

            parent.Level = LogLevels.Info;

            // Parent lookup happens at CALL time against the live logger
            // dictionary, so configuring a parent later re-levels children that
            // already exist. Module-level loggers are created in import order,
            // which is not the order hosts configure them in — caching the
            // parent at construction would make verbosity depend on that order.
            Assert.True(child.IsEnabledFor(LogLevels.Info));
        }
        finally
        {
            parent.Level = LogLevels.NotSet;
        }
    }

    [Fact]
    public void TheTypedLogLevelEnumMatchesThePythonIntConstants()
    {
        // LoggerObject.Dispatch casts an int LogLevels constant straight to
        // LogLevel before handing it to Logging.Write. If the two ever drift,
        // every Standard-mode record is mislabelled with no compiler error and
        // no test failure anywhere else.
        Assert.Equal(LogLevels.Debug, (int)LogLevel.Debug);
        Assert.Equal(LogLevels.Info, (int)LogLevel.Info);
        Assert.Equal(LogLevels.Warning, (int)LogLevel.Warning);
        Assert.Equal(LogLevels.Error, (int)LogLevel.Error);
        Assert.Equal(LogLevel.Warning, (LogLevel)LogLevels.Warning);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// The restore contract the rest of this file rests on
// ─────────────────────────────────────────────────────────────────────────────

public class ChassisCoreGlobalStateTests
{
    [Fact]
    public void TheScopeRestoresEveryGlobalItSnapshots()
    {
        // Every other test here trusts ChassisCoreGlobals to hand the assembly
        // back exactly as it found it. If that trust is misplaced the damage
        // shows up as an unrelated failure in someone else's file, days later
        // and with no obvious cause — so the restore is asserted directly, with
        // the scope deliberately wrecking each global first.
        var identity = Chassis.Identity;
        var loggerFactory = Chassis.LoggerFactory;
        var correlationIdProvider = Chassis.CorrelationIdProvider;
        var mode = Logging.Mode;
        var runDirectory = Logging.RunDirectory;
        var logsRoot = Logging.LogsRoot;
        var consoleLevel = Logging.ConsoleLevel;
        var dialect = Logging.Dialect;
        var hardenHook = Logging.HardenDirectoryHook;
        var mirrorHook = Logging.EventLogMirrorHook;
        var rootLevel = Logging.Root.Level;
        var jsonFormat = ChassisCoreGlobals.JsonFormatCacheValue;
        var logLevelEnv = Environment.GetEnvironmentVariable("LOG_LEVEL");
        var logFormatEnv = Environment.GetEnvironmentVariable("LOG_FORMAT");
        var stdout = Console.Out;
        var stderr = Console.Error;

        using (var g = new ChassisCoreGlobals())
        {
            g.UseStandardMode();
            Chassis.Init(new ChassisIdentity("wrecked", "Wrecked", "wrecked"));
            Chassis.LoggerFactory = name => new ChassisCoreRecordingLogger(name);
            Chassis.CorrelationIdProvider = () => "aaaaaaaa";
            Logging.LogsRoot = g.NewTempDir();
            Logging.HardenDirectoryHook = d => Directory.CreateDirectory(d);
            Logging.EventLogMirrorHook = (_, _, _) => { };
            Logging.Dialect = new StandardLogDialect();
            Logging.Root.Level = LogLevels.Error;
            Logging.JsonFormat = true;
            Environment.SetEnvironmentVariable("LOG_LEVEL", "ERROR");
            Environment.SetEnvironmentVariable("LOG_FORMAT", "json");
            Logging.Initialize("wrecked", verbose: true);
            Assert.NotNull(Logging.RunDirectory);
        }

        Assert.Equal(identity, Chassis.Identity);
        Assert.Same(loggerFactory, Chassis.LoggerFactory);
        Assert.Equal(correlationIdProvider, Chassis.CorrelationIdProvider);
        Assert.Equal(mode, Logging.Mode);
        Assert.Equal(runDirectory, Logging.RunDirectory);
        Assert.Equal(logsRoot, Logging.LogsRoot);
        Assert.Equal(consoleLevel, Logging.ConsoleLevel);
        Assert.Same(dialect, Logging.Dialect);
        Assert.Equal(hardenHook, Logging.HardenDirectoryHook);
        Assert.Equal(mirrorHook, Logging.EventLogMirrorHook);
        Assert.Null(Logging.TestSink);
        Assert.Null(Logging.RenderedSink);
        Assert.Equal(rootLevel, Logging.Root.Level);
        // Compared as the raw nullable cache, not through the JsonFormat getter:
        // reading the getter would itself resolve and latch a value.
        Assert.Equal(jsonFormat, ChassisCoreGlobals.JsonFormatCacheValue);
        Assert.Equal(logLevelEnv, Environment.GetEnvironmentVariable("LOG_LEVEL"));
        Assert.Equal(logFormatEnv, Environment.GetEnvironmentVariable("LOG_FORMAT"));
        Assert.Same(stdout, Console.Out);
        Assert.Same(stderr, Console.Error);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Snapshot-and-restore for every process-global the identity/logging core
/// touches. Collections do not run in parallel, but this assembly is shared with
/// every other chassis test file, so anything left behind here surfaces as an
/// unrelated failure somewhere else.
/// </summary>
internal sealed class ChassisCoreGlobals : IDisposable
{
    // Logging.Mode and Logging.RunDirectory have private setters and no public
    // route back. Logging.Configure(false) would reach Seismic, but only by
    // closing and removing the root logger's handlers, which would break any
    // other test in the assembly that had configured them. Reflection is the
    // narrower blast radius.
    private static readonly MethodInfo ModeSetter = PrivateSetter(nameof(Logging.Mode));
    private static readonly MethodInfo RunDirectorySetter = PrivateSetter(nameof(Logging.RunDirectory));
    private static readonly FieldInfo JsonFormatCache =
        typeof(Logging).GetField("_jsonFormat", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Logging._jsonFormat is gone; JsonFormat cannot be restored.");

    private readonly ChassisIdentity _identity = Chassis.Identity;
    private readonly Func<string, IAppLogger> _loggerFactory = Chassis.LoggerFactory;
    private readonly Func<string?>? _correlationIdProvider = Chassis.CorrelationIdProvider;
    private readonly LoggingMode _mode = Logging.Mode;
    private readonly string? _runDirectory = Logging.RunDirectory;
    private readonly string _logsRoot = Logging.LogsRoot;
    private readonly LogLevel _consoleLevel = Logging.ConsoleLevel;
    private readonly StandardLogDialect _dialect = Logging.Dialect;
    private readonly Action<string>? _hardenHook = Logging.HardenDirectoryHook;
    private readonly Action<LogLevel, string, string>? _mirrorHook = Logging.EventLogMirrorHook;
    private readonly Action<LogLevel, string, string>? _testSink = Logging.TestSink;
    private readonly Action<string>? _renderedSink = Logging.RenderedSink;
    private readonly int _rootLevel = Logging.Root.Level;
    private readonly bool? _jsonFormat = (bool?)JsonFormatCache.GetValue(null);
    private readonly string? _logLevelEnv = Environment.GetEnvironmentVariable("LOG_LEVEL");
    private readonly string? _logFormatEnv = Environment.GetEnvironmentVariable("LOG_FORMAT");
    private readonly TextWriter _stdout = Console.Out;
    private readonly TextWriter _stderr = Console.Error;
    private readonly List<string> _tempDirectories = new();

    internal ChassisCoreGlobals()
    {
        // Both env vars are cleared up front so a developer machine or CI agent
        // that happens to export LOG_LEVEL cannot change what these tests assert.
        Environment.SetEnvironmentVariable("LOG_LEVEL", null);
        Environment.SetEnvironmentVariable("LOG_FORMAT", null);
        Logging.ResetJsonFormatCache();

        // The Standard sink fans out to the real console; capture it so test
        // output stays readable and so the Initialize fallback warning is
        // assertable.
        Stdout = new StringWriter();
        Stderr = new StringWriter();
        Console.SetOut(Stdout);
        Console.SetError(Stderr);
    }

    /// <summary>
    /// The raw <c>Logging._jsonFormat</c> cache (null = unresolved). Exposed so
    /// the restore contract can be asserted without going through the
    /// <see cref="Logging.JsonFormat"/> getter, which latches a value as a side
    /// effect of being read.
    /// </summary>
    internal static bool? JsonFormatCacheValue => (bool?)JsonFormatCache.GetValue(null);

    internal StringWriter Stdout { get; }

    internal StringWriter Stderr { get; }

    /// <summary>Select Standard mode with the stock dialect and no leftover sinks.</summary>
    internal void UseStandardMode()
    {
        Logging.Dialect = new StandardLogDialect();
        Logging.TestSink = null;
        Logging.RenderedSink = null;
        Logging.EventLogMirrorHook = null;
        Chassis.CorrelationIdProvider = null;
        Logging.UseStandardMode();
    }

    /// <summary>Select Seismic mode. No public API goes this way — see ModeSetter.</summary>
    internal void UseSeismicMode() => SetMode(LoggingMode.Seismic);

    internal string NewTempDir()
    {
        var dir = Directory.CreateTempSubdirectory("chassiscore").FullName;
        _tempDirectories.Add(dir);
        return dir;
    }

    /// <summary>
    /// Read a log file that the Standard sink still holds open. File.ReadAllText
    /// opens with FileShare.Read, which on Windows is refused while a writer
    /// holds the file — the second handle has to permit the first handle's write
    /// access. POSIX does not care; this is the portable spelling.
    /// </summary>
    internal static string ReadWhileOpen(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        // Closes the run-directory writer first: on Windows the temp directory
        // cannot be deleted while connector.log is open.
        Logging.ResetForTests();

        Chassis.Init(_identity);
        Chassis.LoggerFactory = _loggerFactory;
        Chassis.CorrelationIdProvider = _correlationIdProvider;

        SetMode(_mode);
        if (_runDirectory is not null)
            RunDirectorySetter.Invoke(null, new object?[] { _runDirectory });
        Logging.LogsRoot = _logsRoot;
        Logging.ConsoleLevel = _consoleLevel;
        Logging.Dialect = _dialect;
        Logging.HardenDirectoryHook = _hardenHook;
        Logging.EventLogMirrorHook = _mirrorHook;
        Logging.TestSink = _testSink;
        Logging.RenderedSink = _renderedSink;
        Logging.Root.Level = _rootLevel;
        JsonFormatCache.SetValue(null, _jsonFormat);

        Environment.SetEnvironmentVariable("LOG_LEVEL", _logLevelEnv);
        Environment.SetEnvironmentVariable("LOG_FORMAT", _logFormatEnv);

        foreach (var dir in _tempDirectories)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // A leaked handle would only ever bite on Windows, and failing
                // teardown here would mask the real assertion failure.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Console.SetOut(_stdout);
        Console.SetError(_stderr);
    }

    private static void SetMode(LoggingMode mode)
    {
        ModeSetter.Invoke(null, new object?[] { mode });
        if (Logging.Mode != mode)
            throw new InvalidOperationException(
                $"Failed to restore Logging.Mode to {mode}; the rest of the assembly would run in the wrong mode.");
    }

    private static MethodInfo PrivateSetter(string propertyName) =>
        typeof(Logging).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static)
            ?.GetSetMethod(nonPublic: true)
        ?? throw new InvalidOperationException(
            $"Logging.{propertyName} has no reachable setter; global state cannot be restored.");
}

/// <summary>Host-supplied logger, for asserting that the LoggerFactory seam is used.</summary>
internal sealed class ChassisCoreRecordingLogger : IAppLogger
{
    internal ChassisCoreRecordingLogger(string name) => Name = name;

    public string Name { get; }

    internal List<string> Lines { get; } = new();

    public void Debug(string message) => Lines.Add("DEBUG " + message);

    public void Info(string message) => Lines.Add("INFO " + message);

    public void Warning(string message) => Lines.Add("WARNING " + message);

    public void Error(string message) => Lines.Add("ERROR " + message);

    public void Error(string message, Exception? ex) => Lines.Add("ERROR " + message);

    public bool IsEnabledFor(int level) => true;

    public bool IsEnabledFor(LogLevel level) => true;
}

/// <summary>Seismic-path handler that keeps the formatted line instead of writing it.</summary>
internal sealed class ChassisCoreCapturingHandler : LogHandler
{
    internal List<string> Lines { get; } = new();

    protected override void Emit(LogRecord record) => Lines.Add(Format(record));
}
