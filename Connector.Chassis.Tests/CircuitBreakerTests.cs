// CircuitBreakerTests.cs
// ----------------------
// Connector.Chassis/CircuitBreaker.cs + CircuitBreakerRegistry.cs.
//
// The breaker is the fleet's degraded-mode fail-safe: when it is wrong, either
// the connector hammers a dead dependency for hours (fails to open / recovers
// too eagerly) or it refuses a healthy one forever (fails to close / leaks a
// half-open permit). Both are outages, and neither shows up in a happy-path
// test, so everything below is a boundary, an error path, or an invariant the
// callers (GraphClient, the crawl's degraded-mode pause, /health readiness,
// /metrics) actually depend on.
//
// The clock is a constructor seam on the type, so every timing test here is
// deterministic — no Thread.Sleep, nothing that can flake on a loaded runner.
// Process-global state touched: the CircuitBreakerRegistry (snapshot/restored
// per class) and CIRCUIT_BREAKER_* env vars (snapshot/restored per class).

namespace Connector.Chassis.Tests;

/// <summary>Manual clock for the breaker's <c>Func&lt;DateTimeOffset&gt;</c> seam.
/// Named with the module prefix because helper types share one assembly with
/// every other chassis test file.</summary>
internal sealed class CircuitBreakerFakeClock
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public Func<DateTimeOffset> Func => () => _now;

    public void Advance(TimeSpan by) => _now += by;
}

// ── state machine ────────────────────────────────────────────────────────────

public class CircuitBreakerStateMachineTests
{
    private static readonly Func<Exception, bool> AllTrip = _ => true;
    private static readonly Func<Exception, bool> NoneTrip = _ => false;

    private static CircuitBreaker Breaker(
        CircuitBreakerFakeClock clock,
        int threshold = 3,
        double openSecs = 30,
        double windowSecs = 60,
        int trials = 1,
        bool critical = true) =>
        new("dep", new CircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = threshold,
            OpenDuration = TimeSpan.FromSeconds(openSecs),
            Window = TimeSpan.FromSeconds(windowSecs),
            HalfOpenTrials = trials,
        }, critical, clock.Func);

    /// <summary>A tripping failure: the operation throws and the classifier says "unreachable".</summary>
    private static async Task Fail(CircuitBreaker b) =>
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            b.ExecuteAsync<int>(_ => throw new InvalidOperationException("5xx"), AllTrip));

    private static Task<int> Succeed(CircuitBreaker b) =>
        b.ExecuteAsync(_ => Task.FromResult(42), AllTrip);

    [Fact]
    public void FreshBreakerIsClosedWithZeroedCounters()
    {
        // /metrics renders State as the gauge value and Trip/Reset as counters;
        // a non-zero start would look like an incident on every process boot.
        var b = Breaker(new CircuitBreakerFakeClock());
        Assert.Equal(CircuitState.Closed, b.State);
        Assert.Equal(0, b.TripCount);
        Assert.Equal(0, b.ResetCount);
        Assert.Equal("dep", b.Name);
        Assert.True(b.Critical);
    }

    [Fact]
    public async Task ClosedTripsAtTheThresholdAndNotOneFailureEarlier()
    {
        // Off-by-one here is the difference between "opens after 5 failures" and
        // "opens after 4", i.e. between the documented contract and a breaker
        // that pauses the crawl earlier than the operator configured.
        var clock = new CircuitBreakerFakeClock();
        var b = Breaker(clock, threshold: 3);

        await Fail(b);
        await Fail(b);
        Assert.Equal(CircuitState.Closed, b.State);  // 2 of 3 — still serving
        Assert.Equal(0, b.TripCount);

        await Fail(b);
        Assert.Equal(CircuitState.Open, b.State);
        Assert.Equal(1, b.TripCount);
    }

    [Fact]
    public async Task OpenRejectsWithoutInvokingTheOperation()
    {
        // The entire point of the open state: the dependency is not touched.
        // If the work ran anyway the breaker would be decoration, and a dead
        // Graph endpoint would still get one call per queued item.
        var clock = new CircuitBreakerFakeClock();
        var b = Breaker(clock, threshold: 1);
        await Fail(b);

        var ran = false;
        var ex = await Assert.ThrowsAsync<CircuitOpenException>(() =>
            b.ExecuteAsync<int>(_ => { ran = true; return Task.FromResult(1); }, AllTrip));

        Assert.False(ran);
        // Callers log/branch on the name (degraded-mode ledger entries carry it).
        Assert.Equal("dep", ex.BreakerName);
        Assert.Contains("dep", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectionsWhileOpenDoNotPushTheRecoveryDeadlineForward()
    {
        // A busy caller keeps hitting a breaker that is already open. Those
        // rejections must not be recorded as fresh failures — if they re-armed
        // _openedAt, a connector under load could never reach half-open and the
        // outage would outlive the dependency's recovery. Recovery has to be a
        // function of the ORIGINAL trip time only.
        var clock = new CircuitBreakerFakeClock();
        var b = Breaker(clock, threshold: 1, openSecs: 10);
        await Fail(b);

        for (var i = 0; i < 9; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<CircuitOpenException>(() => Succeed(b));
        }

        Assert.Equal(1, b.TripCount);  // rejections are not trips
        clock.Advance(TimeSpan.FromSeconds(1));  // exactly 10s after the trip
        Assert.Equal(CircuitState.HalfOpen, b.State);
    }

    [Fact]
    public async Task OpenBecomesHalfOpenExactlyAtTheOpenDurationNotATickEarlier()
    {
        // Guards the `>=` in EvaluateTransition. A 29s/31s test passes whether
        // the comparison is > or >=; this one pins the boundary itself.
        var clock = new CircuitBreakerFakeClock();
        var b = Breaker(clock, threshold: 1, openSecs: 10);
        await Fail(b);
        Assert.Equal(CircuitState.Open, b.State);

        clock.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1));
        Assert.Equal(CircuitState.Open, b.State);

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(CircuitState.HalfOpen, b.State);
    }

    [Fact]
    public async Task HalfOpenSuccessClosesAndZeroesTheFailureCount()
    {
        // Recovery must also forget the pre-outage failures. If the count
        // survived, the first failure after a recovery would re-trip a breaker
        // whose threshold is nowhere near reached.
        var clock = new CircuitBreakerFakeClock();
        var b = Breaker(clock, threshold: 3, openSecs: 10);
        await Fail(b);
        await Fail(b);
        await Fail(b);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(CircuitState.HalfOpen, b.State);

        Assert.Equal(42, await Succeed(b));
        Assert.Equal(CircuitState.Closed, b.State);
        Assert.Equal(1, b.ResetCount);

        await Fail(b);
        await Fail(b);
        Assert.Equal(CircuitState.Closed, b.State);  // count really did restart at 0
        Assert.Equal(1, b.TripCount);
    }

    [Fact]
    public async Task HalfOpenFailureReopensAndRestartsTheFullOpenDuration()
    {
        // A failed probe must buy the dependency another full OpenDuration of
        // quiet, not drop back into a state where the next call probes again.
        var clock = new CircuitBreakerFakeClock();
        var b = Breaker(clock, threshold: 1, openSecs: 10);
        await Fail(b);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(CircuitState.HalfOpen, b.State);

        await Fail(b);
        Assert.Equal(CircuitState.Open, b.State);
        Assert.Equal(2, b.TripCount);  // the re-trip is counted separately

        clock.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1));
        Assert.Equal(CircuitState.Open, b.State);  // deadline measured from the RE-trip
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(CircuitState.HalfOpen, b.State);
    }

    [Fact]
    public async Task HalfOpenNonTrippingErrorClosesTheBreakerBecauseTheDependencyAnswered()
    {
        // "Reachable" is the classifier's verdict, not "no exception". A 4xx or
        // an honoured 429 during a probe proves the service is up, so it must
        // close the breaker — otherwise a dependency that only ever answers 404
        // for the probe URL stays fenced off forever.
        var clock = new CircuitBreakerFakeClock();
        var b = Breaker(clock, threshold: 1, openSecs: 10);
        await Fail(b);
        clock.Advance(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            b.ExecuteAsync<int>(_ => throw new InvalidOperationException("404"), NoneTrip));

        Assert.Equal(CircuitState.Closed, b.State);
        Assert.Equal(1, b.ResetCount);
    }

    [Fact]
    public async Task HalfOpenClosesOnTheFirstSuccessEvenWithSeveralTrialsConfigured()
    {
        // HalfOpenTrials is a CONCURRENCY permit count, not a success quota:
        // one reachable result closes the breaker. (The pre-consolidation
        // connector breakers required HalfOpenTrials successes — anyone porting
        // that rule back into the chassis changes recovery latency for all five
        // connectors, and this test is what says no.)
        var clock = new CircuitBreakerFakeClock();
        var b = Breaker(clock, threshold: 1, openSecs: 10, trials: 3);
        await Fail(b);
        clock.Advance(TimeSpan.FromSeconds(10));

        await Succeed(b);
        Assert.Equal(CircuitState.Closed, b.State);
        Assert.Equal(1, b.ResetCount);
    }

    [Fact]
    public async Task HalfOpenAdmitsOnlyTheConfiguredNumberOfProbesAndReleasesThePermit()
    {
        // The probe budget is what stops a half-open breaker from letting the
        // whole worker pool loose on a dependency that may still be dead. The
        // second half matters more: the permit must come back on every exit
        // path, because nothing else ever resets _halfOpenInFlight while the
        // breaker stays half-open — a leaked permit is permanent.
        var clock = new CircuitBreakerFakeClock();
        var b = Breaker(clock, threshold: 1, openSecs: 10, trials: 1);
        await Fail(b);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(CircuitState.HalfOpen, b.State);

        var gate = new TaskCompletionSource();
        var probe = b.ExecuteAsync(async _ => { await gate.Task; return 1; }, AllTrip);
        Assert.Equal(1, b.HalfOpenInFlight);

        // Budget exhausted → fail fast, and the rejection must not consume or
        // corrupt the in-flight count.
        await Assert.ThrowsAsync<CircuitOpenException>(() => Succeed(b));
        Assert.Equal(1, b.HalfOpenInFlight);

        gate.SetResult();
        await probe;
        Assert.Equal(0, b.HalfOpenInFlight);
        Assert.Equal(CircuitState.Closed, b.State);
    }

    [Fact]
    public async Task CancelledProbeReleasesItsPermitAndLeavesTheBreakerHalfOpen()
    {
        // Graceful stop during a probe: neutral, so the breaker must neither
        // close nor re-open — but it MUST hand the permit back. If the finally
        // is ever dropped, this is the path that wedges a breaker half-open
        // with zero permits, rejecting a healthy dependency until restart.
        var clock = new CircuitBreakerFakeClock();
        var b = Breaker(clock, threshold: 1, openSecs: 10);
        await Fail(b);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(CircuitState.HalfOpen, b.State);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            b.ExecuteAsync<int>(_ => throw new OperationCanceledException(cts.Token), AllTrip, cts.Token));

        Assert.Equal(0, b.HalfOpenInFlight);
        Assert.Equal(CircuitState.HalfOpen, b.State);
        Assert.Equal(1, b.TripCount);   // not re-opened
        Assert.Equal(0, b.ResetCount);  // not closed either

        await Succeed(b);  // the freed permit is usable
        Assert.Equal(CircuitState.Closed, b.State);
    }

    [Fact]
    public async Task AThrowingClassifierCannotWedgeTheBreaker()
    {
        // A caller's classifier that throws (e.g. dereferencing a null
        // StatusCode) is a caller bug, but it must not leave the breaker in a
        // state no traffic can leave. Documents today's behaviour: the
        // classifier's exception REPLACES the original — see the defect report —
        // while the permit is still released and the state is untouched.
        var clock = new CircuitBreakerFakeClock();
        var b = Breaker(clock, threshold: 1, openSecs: 10);
        await Fail(b);
        clock.Advance(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            b.ExecuteAsync<int>(
                _ => throw new InvalidOperationException("the real transport error"),
                _ => throw new NotSupportedException("bad classifier")));

        Assert.Equal(0, b.HalfOpenInFlight);
        Assert.Equal(CircuitState.HalfOpen, b.State);

        await Succeed(b);
        Assert.Equal(CircuitState.Closed, b.State);
    }

    [Fact]
    public async Task OneReachableResultResetsTheFailureCountWhileClosed()
    {
        // Threshold counting is effectively consecutive-ish: any answer from the
        // dependency clears the tally. A flapping-but-alive dependency must not
        // accumulate its way to an open breaker.
        var clock = new CircuitBreakerFakeClock();
        var b = Breaker(clock, threshold: 3);

        await Fail(b);
        await Fail(b);
        await Succeed(b);
        await Fail(b);
        await Fail(b);

        Assert.Equal(CircuitState.Closed, b.State);
        Assert.Equal(0, b.TripCount);
    }

    [Fact]
    public async Task TheFailureWindowRestartsExactlyAtTheWindowBoundary()
    {
        // Guards the `>=` in the window rollover. Two breakers, identical input,
        // one tick apart: at the boundary the old failures are discarded, one
        // tick before it they still count. Slipping this comparison changes how
        // long a slow trickle of 5xx takes to open the breaker.
        var atBoundary = new CircuitBreakerFakeClock();
        var b1 = Breaker(atBoundary, threshold: 3, windowSecs: 60);
        await Fail(b1);
        await Fail(b1);
        atBoundary.Advance(TimeSpan.FromSeconds(60));
        await Fail(b1);
        Assert.Equal(CircuitState.Closed, b1.State);  // window rolled: 1 of 3

        var justInside = new CircuitBreakerFakeClock();
        var b2 = Breaker(justInside, threshold: 3, windowSecs: 60);
        await Fail(b2);
        await Fail(b2);
        justInside.Advance(TimeSpan.FromSeconds(60) - TimeSpan.FromTicks(1));
        await Fail(b2);
        Assert.Equal(CircuitState.Open, b2.State);    // same window: 3 of 3
    }

    [Fact]
    public async Task NonTrippingFailuresNeverOpenTheBreaker()
    {
        // 4xx storms (bad ids, permission denials) are the connector's own
        // fault, not the dependency's. If they tripped the breaker, one bad
        // batch of items would pause an otherwise healthy crawl.
        var clock = new CircuitBreakerFakeClock();
        var b = Breaker(clock, threshold: 2);

        for (var i = 0; i < 25; i++)
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                b.ExecuteAsync<int>(_ => throw new InvalidOperationException("400"), NoneTrip));

        Assert.Equal(CircuitState.Closed, b.State);
        Assert.Equal(0, b.TripCount);
    }

    [Fact]
    public async Task OurOwnCancellationIsNeutralButADependencyTimeoutIsNot()
    {
        // Both arrive as OperationCanceledException; only the token tells them
        // apart. Getting this wrong either trips every breaker on Ctrl-C /
        // service stop, or lets HttpClient timeouts (TaskCanceledException with
        // our token NOT cancelled) slip past the breaker entirely — the exact
        // failure mode the breaker exists to catch.
        var clock = new CircuitBreakerFakeClock();

        var gracefulStop = Breaker(clock, threshold: 1);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gracefulStop.ExecuteAsync<int>(_ => throw new OperationCanceledException(cts.Token), AllTrip, cts.Token));
        Assert.Equal(CircuitState.Closed, gracefulStop.State);
        Assert.Equal(0, gracefulStop.TripCount);

        var timeout = Breaker(clock, threshold: 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            timeout.ExecuteAsync<int>(_ => throw new TaskCanceledException("HttpClient timeout"), AllTrip));
        Assert.Equal(CircuitState.Open, timeout.State);
    }

    [Fact]
    public async Task DisabledIsPurePassthroughAndNeverReportsOpen()
    {
        // CIRCUIT_BREAKER=false is the operator's escape hatch: every call must
        // reach the dependency and the observability surface must stay quiet,
        // even if something else forced the internal state open.
        var b = new CircuitBreaker("off", new CircuitBreakerOptions { Enabled = false, FailureThreshold = 1 });

        var ran = 0;
        for (var i = 0; i < 10; i++)
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                b.ExecuteAsync<int>(_ => { ran++; throw new InvalidOperationException("5xx"); }, AllTrip));

        Assert.Equal(10, ran);
        Assert.Equal(CircuitState.Closed, b.State);

        b.ForceOpenForTests();
        Assert.Equal(CircuitState.Closed, b.State);  // Enabled=false short-circuits the getter
        var stillRan = false;
        await b.ExecuteAsync(_ => { stillRan = true; return Task.CompletedTask; }, AllTrip);
        Assert.True(stillRan);
    }

    [Fact]
    public async Task TheVoidOverloadRunsTheSameStateMachine()
    {
        // ExecuteAsync(Func<CancellationToken, Task>) wraps the generic one. If
        // that wrapper ever stops forwarding, void-returning callers (deletes,
        // ACL pushes) would silently lose breaker protection while the typed
        // callers kept it.
        var clock = new CircuitBreakerFakeClock();
        var b = Breaker(clock, threshold: 2);

        for (var i = 0; i < 2; i++)
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                b.ExecuteAsync(_ => throw new InvalidOperationException("5xx"), AllTrip));

        Assert.Equal(CircuitState.Open, b.State);

        var ran = false;
        await Assert.ThrowsAsync<CircuitOpenException>(() =>
            b.ExecuteAsync(_ => { ran = true; return Task.CompletedTask; }, AllTrip));
        Assert.False(ran);
    }

    [Fact]
    public async Task ConcurrentFailuresTripExactlyOnce()
    {
        // The breaker is shared across the teamsite/content worker pool. Trips
        // are counted for /metrics and alerting; a storm that lands mid-flight
        // must produce one trip, not one per worker, and must not corrupt the
        // in-flight/threshold bookkeeping. Frozen clock: no window rollover can
        // interfere, so the assertion is exact rather than "roughly one".
        var clock = new CircuitBreakerFakeClock();
        var b = Breaker(clock, threshold: 5, openSecs: 300, windowSecs: 300);

        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(async () =>
        {
            try
            {
                await b.ExecuteAsync<int>(_ => throw new InvalidOperationException("5xx"), AllTrip);
            }
            catch (InvalidOperationException)
            {
            }
            catch (CircuitOpenException)
            {
            }
        })));

        Assert.Equal(CircuitState.Open, b.State);
        Assert.Equal(1, b.TripCount);
        Assert.Equal(0, b.HalfOpenInFlight);
    }
}

// ── registry (process-global) ────────────────────────────────────────────────

public class CircuitBreakerRegistryTests : IDisposable
{
    private readonly IReadOnlyList<CircuitBreaker> _preexisting;

    public CircuitBreakerRegistryTests()
    {
        // The registry is a process-wide static with no Unregister, so the only
        // honest way to get a clean slate is snapshot → clear → restore. Without
        // the restore this file would delete registrations made by any other
        // test file once everything lands in one assembly.
        _preexisting = CircuitBreakerRegistry.All;
        CircuitBreakerRegistry.ResetForTests();
    }

    public void Dispose()
    {
        CircuitBreakerRegistry.ResetForTests();
        foreach (var breaker in _preexisting)
            CircuitBreakerRegistry.Register(breaker);
        GC.SuppressFinalize(this);
    }

    private static CircuitBreaker Make(string name, bool critical = true, bool enabled = true,
        CircuitBreakerFakeClock? clock = null) =>
        new(name, new CircuitBreakerOptions
        {
            Enabled = enabled,
            FailureThreshold = 1,
            OpenDuration = TimeSpan.FromSeconds(10),
        }, critical, clock?.Func);

    [Fact]
    public void RegisterReturnsTheSameInstanceAndGetFindsItByName()
    {
        // Clients register inline (`_breaker = breaker ?? Registry.Register(new ...)`),
        // so the return value has to be the breaker they then use — a copy would
        // give the client and /health two different state machines.
        var breaker = Make("graph");
        Assert.Same(breaker, CircuitBreakerRegistry.Register(breaker));
        Assert.Same(breaker, CircuitBreakerRegistry.Get("graph"));
        Assert.Null(CircuitBreakerRegistry.Get("nope"));
    }

    [Fact]
    public void RegisteringTheSameNameTwiceReplacesRatherThanDuplicates()
    {
        // Re-running command setup in one process (validate-config then crawl)
        // registers twice. Duplicates would double-count breakers in /metrics
        // and leave a stale, permanently-closed breaker shadowing the live one.
        CircuitBreakerRegistry.Register(Make("graph"));
        var second = CircuitBreakerRegistry.Register(Make("graph"));

        Assert.Single(CircuitBreakerRegistry.All);
        Assert.Same(second, CircuitBreakerRegistry.Get("graph"));
    }

    [Fact]
    public void RegistryKeysAreOrdinalSoNamesDifferingOnlyInCaseAreDistinct()
    {
        // Ordinal keys, deliberately: the same string means the same breaker on
        // Linux and on Windows. A case-insensitive comparer would silently merge
        // two dependencies on one OS and not the other.
        CircuitBreakerRegistry.Register(Make("graph"));
        CircuitBreakerRegistry.Register(Make("Graph"));

        Assert.Equal(2, CircuitBreakerRegistry.All.Count);
        Assert.NotSame(CircuitBreakerRegistry.Get("graph"), CircuitBreakerRegistry.Get("Graph"));
    }

    [Fact]
    public void AllIsOrderedOrdinalForStableRendering()
    {
        // /metrics output is diffed and scraped; the ordering must not depend on
        // insertion order, hash order, or the runner's current culture. Ordinal
        // puts uppercase before lowercase — a switch to the culture-aware
        // default comparer would reorder these three.
        CircuitBreakerRegistry.Register(Make("b"));
        CircuitBreakerRegistry.Register(Make("A"));
        CircuitBreakerRegistry.Register(Make("a"));

        Assert.Equal(new[] { "A", "a", "b" }, CircuitBreakerRegistry.All.Select(x => x.Name));
    }

    [Fact]
    public void AnEmptyRegistryIsReady()
    {
        Assert.Empty(CircuitBreakerRegistry.All);
        Assert.False(CircuitBreakerRegistry.AnyCriticalOpen);
        Assert.Empty(CircuitBreakerRegistry.OpenCriticalNames);
    }

    [Fact]
    public void AnOpenCriticalBreakerFlipsReadinessAndIsNamed()
    {
        // This is the /health readiness signal and the crawl's degraded-mode
        // pause trigger. If it stops flipping, the connector keeps advertising
        // itself as healthy while Graph is unreachable.
        var breaker = CircuitBreakerRegistry.Register(Make("graph"));
        Assert.False(CircuitBreakerRegistry.AnyCriticalOpen);

        breaker.ForceOpenForTests();

        Assert.True(CircuitBreakerRegistry.AnyCriticalOpen);
        Assert.Equal(new[] { "graph" }, CircuitBreakerRegistry.OpenCriticalNames);
    }

    [Fact]
    public void ANonCriticalOpenBreakerDoesNotAffectReadiness()
    {
        // Criticality is what separates "the index is broken" from "an optional
        // enrichment call is down". A non-critical outage must stay visible in
        // /metrics (All) without taking the whole connector out of rotation.
        var aux = CircuitBreakerRegistry.Register(Make("aux", critical: false));
        aux.ForceOpenForTests();

        Assert.False(CircuitBreakerRegistry.AnyCriticalOpen);
        Assert.Empty(CircuitBreakerRegistry.OpenCriticalNames);
        Assert.Single(CircuitBreakerRegistry.All);
        Assert.Equal(CircuitState.Open, aux.State);
    }

    [Fact]
    public void OpenCriticalNamesListsEveryOpenCriticalBreakerOrdinallyOrdered()
    {
        // The names go straight into the readiness body and the degraded-mode
        // ledger entry, so operators read them: all of them, stable order, and
        // no non-critical or closed breaker smuggled in.
        var salesforce = CircuitBreakerRegistry.Register(Make("salesforce"));
        var graph = CircuitBreakerRegistry.Register(Make("graph"));
        CircuitBreakerRegistry.Register(Make("healthy"));
        var aux = CircuitBreakerRegistry.Register(Make("aux", critical: false));

        salesforce.ForceOpenForTests();
        graph.ForceOpenForTests();
        aux.ForceOpenForTests();

        Assert.Equal(new[] { "graph", "salesforce" }, CircuitBreakerRegistry.OpenCriticalNames);
    }

    [Fact]
    public void ReadinessReturnsOnceTheOpenDurationElapsesEvenWithNoSuccessfulProbe()
    {
        // Documents a real operational consequence rather than the code: the
        // registry only counts Open, and reading State advances Open→HalfOpen on
        // the clock alone. So /health reports ready again after OpenDuration
        // with zero evidence the dependency recovered. Reported as a surprising
        // behaviour; a fix would change readiness, so lock the current one down.
        var clock = new CircuitBreakerFakeClock();
        var graph = CircuitBreakerRegistry.Register(Make("graph", clock: clock));
        graph.ForceOpenForTests();
        Assert.True(CircuitBreakerRegistry.AnyCriticalOpen);

        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.False(CircuitBreakerRegistry.AnyCriticalOpen);
        Assert.Empty(CircuitBreakerRegistry.OpenCriticalNames);
        Assert.Equal(CircuitState.HalfOpen, graph.State);
    }

    [Fact]
    public void ADisabledBreakerCanNeverTakeTheConnectorOutOfRotation()
    {
        // CIRCUIT_BREAKER=false must be a total escape hatch: it reaches
        // readiness too, not just the call path.
        var breaker = CircuitBreakerRegistry.Register(Make("graph", enabled: false));
        breaker.ForceOpenForTests();

        Assert.False(CircuitBreakerRegistry.AnyCriticalOpen);
        Assert.Empty(CircuitBreakerRegistry.OpenCriticalNames);
    }
}

// ── options from the environment ─────────────────────────────────────────────

public class CircuitBreakerOptionsEnvTests : IDisposable
{
    private static readonly string[] Vars =
    {
        CircuitBreakerOptions.EnabledEnvVar,
        "CIRCUIT_BREAKER_FAILURE_THRESHOLD",
        "CIRCUIT_BREAKER_OPEN_SECONDS",
        "CIRCUIT_BREAKER_WINDOW_SECONDS",
        "CIRCUIT_BREAKER_HALF_OPEN_TRIALS",
    };

    private readonly Dictionary<string, string?> _saved = new(StringComparer.Ordinal);

    public CircuitBreakerOptionsEnvTests()
    {
        // Env vars are process-global. Snapshot AND clear: a CI runner that
        // exports CIRCUIT_BREAKER_* would otherwise turn the defaults test red.
        foreach (var name in Vars)
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved)
            Environment.SetEnvironmentVariable(name, value);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AnUnsetEnvironmentYieldsTheDocumentedDefaults()
    {
        // These five numbers are the fleet-wide resilience posture and are
        // quoted in the operator guides; changing one silently re-tunes every
        // connector that does not override it.
        var o = CircuitBreakerOptions.FromEnv();

        Assert.True(o.Enabled);
        Assert.Equal(5, o.FailureThreshold);
        Assert.Equal(TimeSpan.FromSeconds(30), o.OpenDuration);
        Assert.Equal(TimeSpan.FromSeconds(60), o.Window);
        Assert.Equal(1, o.HalfOpenTrials);
        Assert.Equal("CIRCUIT_BREAKER", CircuitBreakerOptions.EnabledEnvVar);
    }

    [Fact]
    public void EachTunableIsReadFromItsOwnVariable()
    {
        // Guards the variable NAMES, which are the operator-facing contract and
        // are easy to typo into a var nobody sets. (Note the underscore in
        // HALF_OPEN_TRIALS — one connector's pre-consolidation breaker spelled
        // it HALFOPEN_TRIALS.)
        Environment.SetEnvironmentVariable("CIRCUIT_BREAKER_FAILURE_THRESHOLD", "9");
        Environment.SetEnvironmentVariable("CIRCUIT_BREAKER_OPEN_SECONDS", "120");
        Environment.SetEnvironmentVariable("CIRCUIT_BREAKER_WINDOW_SECONDS", "300");
        Environment.SetEnvironmentVariable("CIRCUIT_BREAKER_HALF_OPEN_TRIALS", "4");

        var o = CircuitBreakerOptions.FromEnv();

        Assert.Equal(9, o.FailureThreshold);
        Assert.Equal(TimeSpan.FromSeconds(120), o.OpenDuration);
        Assert.Equal(TimeSpan.FromSeconds(300), o.Window);
        Assert.Equal(4, o.HalfOpenTrials);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("2.5")]
    public void OutOfRangeOrUnparseableNumbersFallBackToTheDefault(string raw)
    {
        // FromEnv must never throw during startup config read, and must never
        // produce a nonsense breaker (threshold 0 would open on the first call;
        // a zero/negative OpenDuration would make "open" meaningless).
        foreach (var name in Vars.Skip(1))
            Environment.SetEnvironmentVariable(name, raw);

        var o = CircuitBreakerOptions.FromEnv();

        Assert.Equal(5, o.FailureThreshold);
        Assert.Equal(TimeSpan.FromSeconds(30), o.OpenDuration);
        Assert.Equal(TimeSpan.FromSeconds(60), o.Window);
        Assert.Equal(1, o.HalfOpenTrials);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("  True  ")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("")]
    [InlineData("   ")]
    public void TheBreakerStaysEnabledForBlankAndRecognisedTruthyValues(string raw)
    {
        Environment.SetEnvironmentVariable(CircuitBreakerOptions.EnabledEnvVar, raw);
        Assert.True(CircuitBreakerOptions.FromEnv().Enabled);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("0")]
    [InlineData("no")]
    public void AnExplicitOffValueDisablesTheBreaker(string raw)
    {
        Environment.SetEnvironmentVariable(CircuitBreakerOptions.EnabledEnvVar, raw);
        Assert.False(CircuitBreakerOptions.FromEnv().Enabled);
    }

    [Theory]
    [InlineData("on")]
    [InlineData("enabled")]
    [InlineData("ture")]
    [InlineData("Y")]
    public void AnUnRECOGNISEDValueAlsoDisablesTheBreaker(string raw)
    {
        // DOCUMENTS A DEFECT, does not endorse it. CIRCUIT_BREAKER is a
        // default-ON protective switch, and the chassis's own EnvFlags.IsFalse
        // doc names this very variable as the reason it exists: "a mistyped env
        // var must not be what silently switches it off". FromEnv nonetheless
        // uses IsTrueOrDefault semantics, so CIRCUIT_BREAKER=on ships a
        // connector with no breaker at all. Reported in `defects`; if it is
        // fixed to !IsFalse, this test is the one that should change.
        Environment.SetEnvironmentVariable(CircuitBreakerOptions.EnabledEnvVar, raw);
        Assert.False(CircuitBreakerOptions.FromEnv().Enabled);
    }
}
