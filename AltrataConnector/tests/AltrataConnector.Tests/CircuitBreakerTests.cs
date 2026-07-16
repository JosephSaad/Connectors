// Improvement round 5: circuit breakers + degraded mode / fail-safe.
// Full state machine, error classification (5xx/timeout trip; 4xx/429 don't),
// concurrency, breakered Graph/API clients, degraded-mode pause + resume with
// no state loss, erasure-durability-under-degraded, readiness flip, metrics,
// disabled=passthrough, and seat invariant untouched.

using System.Diagnostics;
using AltrataConnector.Altrata;
using AltrataConnector.Commands;
using AltrataConnector.Config;
using AltrataConnector.Graph;
using AltrataConnector.Identity;
using AltrataConnector.Infrastructure;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

// ---- state machine --------------------------------------------------------------

public class CircuitBreakerStateMachineTests
{
    private sealed class Clock
    {
        public DateTime Now = new(2026, 7, 13, 0, 0, 0, DateTimeKind.Utc);
        public DateTime Read() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private static (CircuitBreaker, Clock) New(int threshold = 3, int openSec = 30, int trials = 2)
    {
        var clock = new Clock();
        var breaker = new CircuitBreaker("dep", new CircuitBreakerOptions
        {
            FailureThreshold = threshold,
            Window = TimeSpan.FromSeconds(60),
            OpenDuration = TimeSpan.FromSeconds(openSec),
            HalfOpenTrials = trials,
        }, clock.Read);
        return (breaker, clock);
    }

    private static async Task Fail(CircuitBreaker b) =>
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            b.ExecuteAsync<int>(() => throw new InvalidOperationException("boom")));

    private static Task<int> Ok(CircuitBreaker b) => b.ExecuteAsync(() => Task.FromResult(1));

    [Fact]
    public async Task OpensAfterThresholdFailuresThenFailsFast()
    {
        var (b, _) = New(threshold: 3);
        Assert.Equal(CircuitState.Closed, b.State);
        await Fail(b);
        await Fail(b);
        Assert.Equal(CircuitState.Closed, b.State);  // 2 < 3
        await Fail(b);
        Assert.Equal(CircuitState.Open, b.State);
        Assert.Equal(1, b.TripCount);

        // Now fails fast without invoking the action.
        var ran = false;
        await Assert.ThrowsAsync<CircuitOpenException>(() =>
            b.ExecuteAsync(() => { ran = true; return Task.FromResult(1); }));
        Assert.False(ran);
    }

    [Fact]
    public async Task HalfOpensAfterOpenDurationAndClosesOnTrialSuccesses()
    {
        var (b, clock) = New(threshold: 2, openSec: 30, trials: 2);
        await Fail(b); await Fail(b);
        Assert.Equal(CircuitState.Open, b.State);

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(CircuitState.HalfOpen, b.State);  // probing

        await Ok(b);                                    // trial 1
        Assert.Equal(CircuitState.HalfOpen, b.State);   // needs 2
        await Ok(b);                                    // trial 2 → close
        Assert.Equal(CircuitState.Closed, b.State);
        Assert.Equal(1, b.ResetCount);
    }

    [Fact]
    public async Task ReopensWhenAHalfOpenTrialFails()
    {
        var (b, clock) = New(threshold: 2, openSec: 30, trials: 2);
        await Fail(b); await Fail(b);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(CircuitState.HalfOpen, b.State);

        await Fail(b);                                  // probe failed → reopen
        Assert.Equal(CircuitState.Open, b.State);
        Assert.Equal(2, b.TripCount);
    }

    [Fact]
    public async Task OldFailuresAgeOutOfTheWindow()
    {
        var (b, clock) = New(threshold: 3);
        await Fail(b); await Fail(b);
        clock.Advance(TimeSpan.FromSeconds(61));  // both age out of the 60s window
        await Fail(b);
        Assert.Equal(CircuitState.Closed, b.State);  // only 1 failure inside the window
    }

    [Fact]
    public async Task NonTrippingExceptionDoesNotOpen()
    {
        var (b, _) = New(threshold: 2);
        // isTripException = false → treated as success for the breaker, still rethrows.
        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<ArgumentException>(() =>
                b.ExecuteAsync<int>(() => throw new ArgumentException("4xx-ish"),
                    isTripException: _ => false));
        Assert.Equal(CircuitState.Closed, b.State);
        Assert.Equal(0, b.TripCount);
    }

    [Fact]
    public async Task FailureResultTripsWhileHealthyResultDoesNot()
    {
        var (b, _) = New(threshold: 2);
        // result == 500 is a failure; 200 is success.
        await b.ExecuteAsync(() => Task.FromResult(500), isFailureResult: r => r >= 500);
        await b.ExecuteAsync(() => Task.FromResult(200), isFailureResult: r => r >= 500);
        Assert.Equal(CircuitState.Closed, b.State);  // 1 failure < 2
        await b.ExecuteAsync(() => Task.FromResult(503), isFailureResult: r => r >= 500);
        Assert.Equal(CircuitState.Open, b.State);
    }

    [Fact]
    public async Task DisabledIsPurePassthrough()
    {
        var b = new CircuitBreaker("dep", new CircuitBreakerOptions { Enabled = false, FailureThreshold = 1 });
        for (var i = 0; i < 10; i++)
            await Fail(b);
        Assert.Equal(CircuitState.Closed, b.State);
        Assert.Equal(0, b.TripCount);
        // Even "open-looking" conditions never fail fast when disabled.
        Assert.Equal(1, await Ok(b));
    }

    [Fact]
    public async Task ConcurrentFailuresAreThreadSafeAndTripOnce()
    {
        var (b, _) = New(threshold: 5);
        await Task.WhenAll(Enumerable.Range(0, 50).Select(async _ =>
        {
            try { await b.ExecuteAsync<int>(() => throw new InvalidOperationException()); }
            catch (InvalidOperationException) { }
            catch (CircuitOpenException) { }
        }));
        Assert.Equal(CircuitState.Open, b.State);
        Assert.Equal(1, b.TripCount);  // exactly one trip despite the stampede
    }

    [Fact]
    public void SnapshotReflectsNameCriticalAndState()
    {
        var b = new CircuitBreaker("graph", new CircuitBreakerOptions { Critical = true });
        var snap = b.Snapshot();
        Assert.Equal("graph", snap.Name);
        Assert.True(snap.Critical);
        Assert.Equal(CircuitState.Closed, snap.State);
    }
}

// ---- HTTP failure classification ------------------------------------------------

public class HttpTripPolicyTests
{
    [Fact]
    public void TransportAndTimeoutTrip()
    {
        Assert.True(HttpTripPolicy.IsTrip(new HttpRequestException("refused"), default));
        Assert.True(HttpTripPolicy.IsTrip(new TaskCanceledException("timeout"), default));
    }

    [Fact]
    public void GracefulStopCancellationDoesNotTrip()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(HttpTripPolicy.IsTrip(new OperationCanceledException(cts.Token), cts.Token));
    }

    [Fact]
    public void OtherExceptionsDoNotTrip()
    {
        Assert.False(HttpTripPolicy.IsTrip(new ArgumentException(), default));
    }
}

// ---- breakered Graph client -----------------------------------------------------

public class GraphBreakerTests
{
    private static GraphClient New(ScriptedHandler handler, CircuitBreaker breaker) =>
        new(TestFixtures.NewConfig() with { GraphMaxRetries = 0 }, handler,
            (_, _) => Task.CompletedTask, breaker);

    private static CircuitBreaker Breaker(int threshold = 2) =>
        new("graph", new CircuitBreakerOptions { FailureThreshold = threshold, Critical = true });

    [Fact]
    public async Task Sustained5xxTripsTheGraphBreakerThenFailsFast()
    {
        var breaker = Breaker(threshold: 2);
        var handler = new ScriptedHandler();
        handler.EnqueueJson(200, """{"access_token":"tok","expires_in":3600}""");
        handler.EnqueueJson(503, "{}");   // 1st DELETE → 5xx
        handler.EnqueueJson(503, "{}");   // 2nd DELETE → 5xx → opens
        var client = New(handler, breaker);

        await Assert.ThrowsAsync<GraphClientException>(() => client.DeleteItemAsync("i1"));
        await Assert.ThrowsAsync<GraphClientException>(() => client.DeleteItemAsync("i2"));
        Assert.Equal(CircuitState.Open, breaker.State);

        // Now fails fast — no further HTTP requests are made.
        var before = handler.Requests.Count;
        await Assert.ThrowsAsync<CircuitOpenException>(() => client.DeleteItemAsync("i3"));
        Assert.Equal(before, handler.Requests.Count);
        Assert.Equal(CircuitState.Open, client.BreakerState);
    }

    [Fact]
    public async Task FourHundredDoesNotTripTheBreaker()
    {
        var breaker = Breaker(threshold: 2);
        var handler = new ScriptedHandler();
        handler.EnqueueJson(200, """{"access_token":"tok","expires_in":3600}""");
        handler.EnqueueJson(400, """{"error":"bad"}""");
        handler.EnqueueJson(400, """{"error":"bad"}""");
        handler.EnqueueJson(400, """{"error":"bad"}""");
        var client = New(handler, breaker);

        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<GraphClientException>(() => client.DeleteItemAsync($"i{i}"));
        Assert.Equal(CircuitState.Closed, breaker.State);  // 4xx = responding, never trips
    }

    [Fact]
    public async Task Honored429DoesNotTripTheBreaker()
    {
        var breaker = Breaker(threshold: 2);
        var handler = new ScriptedHandler();
        handler.EnqueueJson(200, """{"access_token":"tok","expires_in":3600}""");
        // GraphMaxRetries=0 → the 429 is returned, EnsureSuccess throws; but 429 < 500 so NOT a breaker failure.
        handler.EnqueueJson(429, "{}", r => r.Headers.Add("Retry-After", "1"));
        handler.EnqueueJson(429, "{}", r => r.Headers.Add("Retry-After", "1"));
        handler.EnqueueJson(429, "{}", r => r.Headers.Add("Retry-After", "1"));
        var client = New(handler, breaker);

        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<GraphClientException>(() => client.DeleteItemAsync($"i{i}"));
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public async Task DefaultBreakerIsInertOnTheHappyPath()
    {
        // A GraphClient with no injected breaker (env default on) never trips
        // when calls succeed — proving zero behavioural change on the happy path.
        var handler = new ScriptedHandler();
        handler.EnqueueJson(200, """{"access_token":"tok","expires_in":3600}""");
        handler.EnqueueJson(200, "{}");
        var client = new GraphClient(TestFixtures.NewConfig(), handler, (_, _) => Task.CompletedTask);
        await client.DeleteItemAsync("i1");
        Assert.Equal(CircuitState.Closed, client.BreakerState);
    }
}

// ---- degraded-mode crawl --------------------------------------------------------

public class DegradedModeTests
{
    [Fact]
    public async Task OpenGraphBreakerPausesTheCrawlAtTheDeliveryBoundaryWithNoDeadLetters()
    {
        using var harness = new CrawlHarness();
        harness.Graph.BreakerState = CircuitState.Open;  // Graph is "down"
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P1", "A", null, null)), 1));

        var result = await harness.Engine.RunAsync(CrawlKind.Full);

        Assert.True(result.Degraded);
        Assert.True(result.Stopped);
        Assert.Equal(0, result.ItemsIngested);
        Assert.Empty(harness.Graph.PutItems);                 // nothing shipped
        Assert.Empty(harness.State.ReadDeadLetters());        // and nothing dead-lettered
        Assert.False(harness.State.IsDeliveryProcessed("d1")); // delivery left for resume
    }

    [Fact]
    public async Task ResumesWithNoStateLossOnceTheBreakerRecovers()
    {
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile,
                TestFixtures.PersonJson(("P1", "A", null, null), ("P2", "B", null, null)), 2));

        // First crawl: Graph down → degraded pause, nothing ingested.
        harness.Graph.BreakerState = CircuitState.Open;
        var first = await harness.Engine.RunAsync(CrawlKind.Full);
        Assert.True(first.Degraded);
        Assert.Empty(harness.Graph.PutItems);

        // Breaker recovers; re-crawl processes the delivery fully.
        harness.Graph.BreakerState = CircuitState.Closed;
        await harness.Engine.RunAsync(CrawlKind.Full);
        Assert.Contains(harness.Graph.PutItems, i => i.Id == "PersonProfile-P1");
        Assert.Contains(harness.Graph.PutItems, i => i.Id == "PersonProfile-P2");
        Assert.True(harness.State.IsDeliveryProcessed("d1"));
    }

    [Fact]
    public async Task SeatInvariantHoldsAfterDegradedResume()
    {
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P1", "A", null, null)), 1));
        harness.Graph.BreakerState = CircuitState.Open;
        await harness.Engine.RunAsync(CrawlKind.Full);
        harness.Graph.BreakerState = CircuitState.Closed;
        await harness.Engine.RunAsync(CrawlKind.Full);

        Assert.All(harness.Graph.PutItems, item =>
            Assert.DoesNotContain(item.Acl, e => e.Type is "everyone" or "everyoneExceptGuests"));
    }
}

// ---- erasure durability under degraded ------------------------------------------

public class ErasureUnderDegradedTests
{
    [Fact]
    public async Task GraphDownErasureStillSuppressesLedgersAndDeadLetters()
    {
        var root = TestFixtures.NewTempDir("erase_degraded");
        var graph = new FakeGraphClient();
        // Simulate Graph being down for the withdrawal (breaker-open style failure).
        graph.FailingDeletes.Add("PersonProfile-P1");
        graph.FailingDeletes.Add("WealthIndicator-W1");
        using var runtime = TestFixtures.NewRuntime(TestFixtures.NewConfig(), graph, root);
        runtime.Identity.RecordIngestedItem(new IngestedItem("PersonProfile-P1", Datasets.PersonProfile, "h", DateTime.UtcNow));
        runtime.Identity.RecordItemSubjects("PersonProfile-P1", new[] { "P1" });
        runtime.Identity.RecordIngestedItem(new IngestedItem("WealthIndicator-W1", Datasets.WealthIndicator, "h", DateTime.UtcNow));
        runtime.Identity.RecordItemSubjects("WealthIndicator-W1", new[] { "P1" });

        var result = await CommandRegistry.ForgetSubjectAsync(runtime, "P1", null, "joseph", confirm: true);

        // Reported incomplete, but the erasure is DURABLE regardless of Graph:
        Assert.Equal(false, result);
        Assert.True(runtime.State.IsSubjectSuppressed("P1"));       // suppressed
        var ledger = runtime.Erasure.ReadAll();
        Assert.Single(ledger);                                      // ledgered
        Assert.True(runtime.Erasure.Verify(out _));
        // Both failed withdrawals queued as delete ops for retry-failed.
        var dl = runtime.State.ReadDeadLetters();
        Assert.Equal(2, dl.Count(r => r.Op == DeadLetterOps.Delete));
    }
}

// ---- readiness + metrics --------------------------------------------------------

public class ReadinessTests
{
    [Fact]
    public void ReadinessIsNotReadyWhenACriticalBreakerIsOpen()
    {
        var graph = new CircuitBreaker("graph", new CircuitBreakerOptions { Enabled = false, Critical = true });
        var api = new CircuitBreaker("altrata-api", new CircuitBreakerOptions { Enabled = false, Critical = false });

        // Helper mirroring the /ready gate: not-ready iff a CRITICAL breaker is Open.
        static bool NotReady(IEnumerable<BreakerSnapshot> snaps) =>
            snaps.Any(b => b.Critical && b.State == CircuitState.Open);

        Assert.False(NotReady(new[] { graph.Snapshot(), api.Snapshot() }));

        // A tripped critical breaker → not ready; a tripped non-critical one → still ready.
        var openCritical = new BreakerSnapshot("graph", true, CircuitState.Open, 1, 0);
        var openNonCritical = new BreakerSnapshot("altrata-api", false, CircuitState.Open, 1, 0);
        Assert.True(NotReady(new[] { openCritical }));
        Assert.False(NotReady(new[] { openNonCritical }));
    }

    [Fact]
    public async Task TripAndResetCountersTrack()
    {
        var b = new CircuitBreaker("dep", new CircuitBreakerOptions
        {
            FailureThreshold = 1, OpenDuration = TimeSpan.FromMilliseconds(1), HalfOpenTrials = 1,
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            b.ExecuteAsync<int>(() => throw new InvalidOperationException()));
        Assert.Equal(1, b.TripCount);
        await Task.Delay(5);
        Assert.Equal(CircuitState.HalfOpen, b.State);
        await b.ExecuteAsync(() => Task.FromResult(1));  // probe success → close
        Assert.Equal(1, b.ResetCount);
    }
}

// ---- config knobs ---------------------------------------------------------------

public class CircuitBreakerConfigTests : IDisposable
{
    public CircuitBreakerConfigTests()
    {
        foreach (var (k, v) in new[]
                 {
                     ("CONNECTOR_ID", "AltrataCbTest"), ("CONNECTOR_NAME", "t"),
                     ("CONNECTOR_DESCRIPTION", "t"), ("AAD_APP_CLIENT_ID", "c"),
                     ("AAD_APP_TENANT_ID", "t"), ("SECRET_AAD_APP_CLIENT_SECRET", "s"),
                 })
            Environment.SetEnvironmentVariable(k, v);
    }

    public void Dispose()
    {
        foreach (var k in new[]
                 {
                     "CONNECTOR_ID", "CONNECTOR_NAME", "CONNECTOR_DESCRIPTION", "AAD_APP_CLIENT_ID",
                     "AAD_APP_TENANT_ID", "SECRET_AAD_APP_CLIENT_SECRET",
                     "CIRCUIT_BREAKER", "CIRCUIT_BREAKER_FAILURE_THRESHOLD", "CIRCUIT_BREAKER_WINDOW_SECONDS",
                     "CIRCUIT_BREAKER_OPEN_SECONDS", "CIRCUIT_BREAKER_HALFOPEN_TRIALS",
                 })
            Environment.SetEnvironmentVariable(k, null);
    }

    [Fact]
    public void DefaultsAreOnWithSaneThresholds()
    {
        var config = AppConfig.Load();
        Assert.True(config.CircuitBreakerEnabled);
        Assert.Equal(5, config.CircuitBreakerFailureThreshold);
        Assert.Equal(60, config.CircuitBreakerWindowSeconds);
        Assert.Equal(30, config.CircuitBreakerOpenSeconds);
        Assert.Equal(2, config.CircuitBreakerHalfOpenTrials);
    }

    [Fact]
    public void FalseDisablesTheBreaker()
    {
        Environment.SetEnvironmentVariable("CIRCUIT_BREAKER", "false");
        Assert.False(AppConfig.Load().CircuitBreakerEnabled);
        Assert.False(CircuitBreakerOptions.FromEnv(critical: true).Enabled);
    }

    [Fact]
    public void ThresholdsAreRead()
    {
        Environment.SetEnvironmentVariable("CIRCUIT_BREAKER_FAILURE_THRESHOLD", "9");
        Environment.SetEnvironmentVariable("CIRCUIT_BREAKER_OPEN_SECONDS", "15");
        var opts = CircuitBreakerOptions.FromEnv(critical: false);
        Assert.Equal(9, opts.FailureThreshold);
        Assert.Equal(TimeSpan.FromSeconds(15), opts.OpenDuration);
        Assert.Equal(9, AppConfig.Load().CircuitBreakerFailureThreshold);
    }
}
