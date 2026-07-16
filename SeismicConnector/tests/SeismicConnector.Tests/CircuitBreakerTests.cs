// Improvement round 5: circuit breakers + degraded-mode / fail-safe.
//
// Covers the standalone state machine (closed/open/half-open with an injected
// clock), the tripping classification (5xx/timeout/conn trip; 4xx/429 do not),
// thread-safety, client integration (a real breaker fails fast after a
// sustained outage), degraded-mode pausing at a checkpoint with resume-and-no-
// loss, readiness flip, metrics/counters, and the disabled=passthrough escape
// hatch and happy-path-inert proof.

using System.Net;
using SeismicConnector.Config;
using SeismicConnector.Graph;
using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector.Tests;

// ── manual clock ─────────────────────────────────────────────────────────────

internal sealed class FakeClock
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public DateTimeOffset Now => _now;
    public Func<DateTimeOffset> Func => () => _now;
    public void Advance(TimeSpan by) => _now += by;
}

// ── state machine ────────────────────────────────────────────────────────────

public class CircuitBreakerStateMachineTests
{
    private static readonly Func<Exception, bool> AllTrip = _ => true;
    private static readonly Func<Exception, bool> NoneTrip = _ => false;

    private static CircuitBreaker Breaker(FakeClock clock, int threshold = 3, int openSecs = 30,
        int windowSecs = 60, int trials = 1) =>
        new("test", new CircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = threshold,
            OpenDuration = TimeSpan.FromSeconds(openSecs),
            Window = TimeSpan.FromSeconds(windowSecs),
            HalfOpenTrials = trials,
        }, critical: true, clock.Func);

    private static async Task Fail(CircuitBreaker b) =>
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            b.ExecuteAsync<int>(_ => throw new InvalidOperationException("boom"), AllTrip));

    private static Task<int> Succeed(CircuitBreaker b) =>
        b.ExecuteAsync(_ => Task.FromResult(42), AllTrip);

    [Fact]
    public async Task Closed_OpensAfterThresholdTrippingFailures()
    {
        var clock = new FakeClock();
        var b = Breaker(clock, threshold: 3);
        Assert.Equal(CircuitState.Closed, b.State);

        await Fail(b);
        await Fail(b);
        Assert.Equal(CircuitState.Closed, b.State);  // 2 < threshold
        await Fail(b);
        Assert.Equal(CircuitState.Open, b.State);     // 3rd trips
        Assert.Equal(1, b.TripCount);
    }

    [Fact]
    public async Task Open_FailsFast_WithoutRunningTheOperation()
    {
        var clock = new FakeClock();
        var b = Breaker(clock, threshold: 1);
        await Fail(b);
        Assert.Equal(CircuitState.Open, b.State);

        var ran = false;
        await Assert.ThrowsAsync<CircuitOpenException>(() =>
            b.ExecuteAsync<int>(_ => { ran = true; return Task.FromResult(1); }, AllTrip));
        Assert.False(ran);  // dependency never touched
    }

    [Fact]
    public async Task Open_TransitionsToHalfOpen_AfterOpenDuration()
    {
        var clock = new FakeClock();
        var b = Breaker(clock, threshold: 1, openSecs: 30);
        await Fail(b);
        Assert.Equal(CircuitState.Open, b.State);

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(CircuitState.Open, b.State);      // still open
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(CircuitState.HalfOpen, b.State);  // probe window
    }

    [Fact]
    public async Task HalfOpen_SuccessCloses_AndResets()
    {
        var clock = new FakeClock();
        var b = Breaker(clock, threshold: 1, openSecs: 10);
        await Fail(b);
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(CircuitState.HalfOpen, b.State);

        Assert.Equal(42, await Succeed(b));
        Assert.Equal(CircuitState.Closed, b.State);
        Assert.Equal(1, b.ResetCount);
    }

    [Fact]
    public async Task HalfOpen_FailureReopens()
    {
        var clock = new FakeClock();
        var b = Breaker(clock, threshold: 1, openSecs: 10);
        await Fail(b);
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(CircuitState.HalfOpen, b.State);

        await Fail(b);
        Assert.Equal(CircuitState.Open, b.State);
        Assert.Equal(2, b.TripCount);  // original + re-trip
    }

    [Fact]
    public async Task NonTrippingFailure_DoesNotCount()
    {
        var clock = new FakeClock();
        var b = Breaker(clock, threshold: 2);
        // 4xx / 429-style errors: the op throws, but the classifier says "reachable".
        for (var i = 0; i < 10; i++)
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                b.ExecuteAsync<int>(_ => throw new InvalidOperationException("4xx"), NoneTrip));
        Assert.Equal(CircuitState.Closed, b.State);
        Assert.Equal(0, b.TripCount);
    }

    [Fact]
    public async Task Success_ResetsFailureCount()
    {
        var clock = new FakeClock();
        var b = Breaker(clock, threshold: 3);
        await Fail(b);
        await Fail(b);
        await Succeed(b);       // resets the count
        await Fail(b);
        await Fail(b);
        Assert.Equal(CircuitState.Closed, b.State);  // never reached 3 in a row
    }

    [Fact]
    public async Task Window_ExpiresFailureCount()
    {
        var clock = new FakeClock();
        var b = Breaker(clock, threshold: 3, windowSecs: 60);
        await Fail(b);
        await Fail(b);
        clock.Advance(TimeSpan.FromSeconds(61));  // window elapsed
        await Fail(b);
        Assert.Equal(CircuitState.Closed, b.State);  // count restarted, only 1 in the new window
    }

    [Fact]
    public async Task HalfOpenTrials_LimitConcurrentProbes()
    {
        var clock = new FakeClock();
        var b = Breaker(clock, threshold: 1, openSecs: 5, trials: 1);
        await Fail(b);
        clock.Advance(TimeSpan.FromSeconds(6));  // → half-open, 1 trial permitted

        var gate = new TaskCompletionSource();
        // First probe occupies the single trial slot and blocks.
        var probe1 = b.ExecuteAsync(async _ => { await gate.Task; return 1; }, AllTrip);
        // Second concurrent probe must fast-fail (no permit left).
        await Assert.ThrowsAsync<CircuitOpenException>(() => b.ExecuteAsync(_ => Task.FromResult(2), AllTrip));
        gate.SetResult();
        await probe1;
        Assert.Equal(CircuitState.Closed, b.State);
    }

    [Fact]
    public async Task Disabled_IsPurePassthrough_NeverOpens()
    {
        var clock = new FakeClock();
        var b = new CircuitBreaker("off", new CircuitBreakerOptions { Enabled = false, FailureThreshold = 1 },
            clock: clock.Func);
        var ran = 0;
        for (var i = 0; i < 20; i++)
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                b.ExecuteAsync<int>(_ => { ran++; throw new InvalidOperationException(); }, AllTrip));
        Assert.Equal(20, ran);                       // op ALWAYS runs
        Assert.Equal(CircuitState.Closed, b.State);  // never trips
        Assert.Equal(0, b.TripCount);
    }

    [Fact]
    public async Task HappyPath_IsInert_StaysClosed()
    {
        var clock = new FakeClock();
        var b = Breaker(clock, threshold: 3);
        var ran = 0;
        for (var i = 0; i < 500; i++)
            Assert.Equal(7, await b.ExecuteAsync(_ => { ran++; return Task.FromResult(7); }, AllTrip));
        Assert.Equal(500, ran);
        Assert.Equal(CircuitState.Closed, b.State);
        Assert.Equal(0, b.TripCount);
        Assert.Equal(0, b.ResetCount);
    }

    [Fact]
    public async Task CallerCancellation_IsNeutral_NotADependencyFault()
    {
        var clock = new FakeClock();
        var b = Breaker(clock, threshold: 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            b.ExecuteAsync<int>(_ => throw new OperationCanceledException(cts.Token), AllTrip, cts.Token));
        Assert.Equal(CircuitState.Closed, b.State);  // our own cancel never trips
        Assert.Equal(0, b.TripCount);
    }
}

// ── thread safety ────────────────────────────────────────────────────────────

public class CircuitBreakerConcurrencyTests
{
    [Fact]
    public async Task ConcurrentFailures_TripExactlyOnce_NoCorruption()
    {
        var b = new CircuitBreaker("concurrent", new CircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = 5,
            OpenDuration = TimeSpan.FromMinutes(5),
            Window = TimeSpan.FromMinutes(5),
        });

        var tasks = Enumerable.Range(0, 200).Select(_ => Task.Run(async () =>
        {
            try
            {
                await b.ExecuteAsync<int>(_ => throw new InvalidOperationException("boom"), _ => true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (CircuitOpenException)
            {
            }
        }));
        await Task.WhenAll(tasks);

        Assert.Equal(CircuitState.Open, b.State);
        Assert.Equal(1, b.TripCount);  // opened once despite the storm
    }
}

// ── tripping classification (per client) ────────────────────────────────────

public class TrippingClassificationTests
{
    [Theory]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(429, false)]   // flow control, not an outage
    [InlineData(400, false)]
    [InlineData(404, false)]
    public void Graph_ClassifiesByStatus(int status, bool trips)
    {
        Assert.Equal(trips, GraphClient.IsTrippingFailure(new GraphApiError(status, "x")));
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(429, false)]
    [InlineData(401, false)]
    public void Seismic_ClassifiesByStatus(int status, bool trips)
    {
        Assert.Equal(trips, SeismicClient.IsTrippingFailure(new SeismicApiError(status, "x")));
    }

    [Fact]
    public void NetworkAndTimeout_Trip()
    {
        Assert.True(GraphClient.IsTrippingFailure(new HttpRequestException("conn refused")));
        Assert.True(GraphClient.IsTrippingFailure(new TaskCanceledException("timeout")));
        Assert.True(SeismicClient.IsTrippingFailure(new HttpRequestException("dns")));
    }

    [Fact]
    public void UnrelatedException_DoesNotTrip()
    {
        Assert.False(GraphClient.IsTrippingFailure(new InvalidOperationException()));
    }
}

// ── client integration: real breaker fails fast ─────────────────────────────

public class BreakerClientIntegrationTests
{
    private static GraphClient GraphWith(FakeHttpHandler handler, CircuitBreaker breaker) =>
        new(TestConfig.Build().Graph, handler, breaker)
        {
            OverrideAccessToken = "token",
            DelayAsync = (_, _) => Task.CompletedTask,
        };

    private static CircuitBreaker Breaker(int threshold) =>
        new("graph-test", new CircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = threshold,
            OpenDuration = TimeSpan.FromMinutes(5),
            Window = TimeSpan.FromMinutes(5),
        });

    [Fact]
    public async Task SustainedServerErrors_OpenTheBreaker_ThenFailFast()
    {
        var handler = new FakeHttpHandler();
        handler.When(HttpMethod.Get, "/external", (_, _) =>
            FakeHttpHandler.Json(HttpStatusCode.InternalServerError, """{"error":{"message":"down"}}"""));
        var breaker = Breaker(threshold: 2);
        var client = GraphWith(handler, breaker);

        // Two full (retried) operations fail with 5xx → the breaker opens.
        await Assert.ThrowsAsync<GraphApiError>(() => client.GetAsync("/external/x"));
        await Assert.ThrowsAsync<GraphApiError>(() => client.GetAsync("/external/x"));
        Assert.Equal(CircuitState.Open, breaker.State);

        var httpCallsBefore = handler.Requests.Count;
        // Next call short-circuits: CircuitOpenException, NO new HTTP traffic.
        await Assert.ThrowsAsync<CircuitOpenException>(() => client.GetAsync("/external/x"));
        Assert.Equal(httpCallsBefore, handler.Requests.Count);
    }

    [Fact]
    public async Task PersistentThrottling429_DoesNotOpenTheBreaker()
    {
        var handler = new FakeHttpHandler();
        handler.When(HttpMethod.Get, "/external", (_, _) =>
        {
            var r = FakeHttpHandler.Json((HttpStatusCode)429, """{"error":{"message":"slow down"}}""");
            r.Headers.Add("Retry-After", "1");
            return r;
        });
        var breaker = Breaker(threshold: 2);
        var client = GraphWith(handler, breaker);

        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<GraphApiError>(() => client.GetAsync("/external/x"));
        Assert.Equal(CircuitState.Closed, breaker.State);  // 429 is flow control, never trips
        Assert.Equal(0, breaker.TripCount);
    }

    [Fact]
    public async Task ClientErrors4xx_DoNotOpenTheBreaker()
    {
        var handler = new FakeHttpHandler();
        handler.When(HttpMethod.Get, "/external", (_, _) =>
            FakeHttpHandler.Json(HttpStatusCode.BadRequest, """{"error":{"message":"bad"}}"""));
        var breaker = Breaker(threshold: 2);
        var client = GraphWith(handler, breaker);

        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<GraphApiError>(() => client.GetAsync("/external/x"));
        Assert.Equal(CircuitState.Closed, breaker.State);
    }
}

// ── degraded mode: pause at checkpoint + resume with no loss ─────────────────

public class DegradedModePipelineTests
{
    [Fact]
    public async Task CriticalBreakerOpensMidCrawl_PausesAtChunkBoundary_AndResumes()
    {
        Metrics.ResetForTests();
        try
        {
            using var harness = new PipelineHarness(exclusions: new ExclusionRules());
            harness.AddTeamsite("ts1");
            for (var i = 0; i < 12; i++)  // chunk size 10 → 2 chunks (10 + 2)
                harness.AddContent(TestContent.Make($"c{i:D2}"));

            // Degrade once 10 items (chunk 1) have been PUT; a flag lets crawl 2 resume.
            var resumed = false;
            harness.Pipeline.CriticalBreakerOpenProbe =
                () => !resumed && harness.PutItemIds().Count >= 10;

            // Crawl 1 pauses after chunk 1.
            var ok1 = await harness.Pipeline.RunCrawlAsync(
                fullCrawl: true, objectTypeFilter: "ContentItem");
            Assert.True(ok1);  // paused, not failed
            Assert.Equal(10, harness.PutItemIds().Count);
            Assert.NotNull(harness.Store.GetTrackedItem("c00"));
            Assert.Null(harness.Store.GetTrackedItem("c11"));         // not yet ingested
            Assert.Equal(1, Metrics.DegradedPauses);

            // Checkpoint saved, last-sync NOT written → the next run resumes.
            var checkpoint = SyncState.ReadCheckpoint(harness.Config.Connector.Id);
            Assert.NotNull(checkpoint);
            Assert.Equal(1, checkpoint!["completed"]!["ContentItem:ts1"]!.GetValue<int>());
            Assert.Null(SyncState.ReadLastSync(harness.Config.Connector.Id));

            // Crawl 2 resumes: chunk 1 skipped (checkpoint), chunk 2 ingested.
            resumed = true;
            var ok2 = await harness.Pipeline.RunCrawlAsync(
                fullCrawl: true, objectTypeFilter: "ContentItem");
            Assert.True(ok2);

            // No loss, no duplication: exactly 12 distinct items, each PUT once.
            var putIds = harness.PutItemIds();
            Assert.Equal(12, putIds.Distinct().Count());
            Assert.All(putIds.GroupBy(x => x), g => Assert.True(g.Count() == 1, $"{g.Key} PUT {g.Count()}x"));
            for (var i = 0; i < 12; i++)
                Assert.NotNull(harness.Store.GetTrackedItem($"c{i:D2}"));

            // Completed crawl: checkpoint cleared, last-sync written.
            Assert.Null(SyncState.ReadCheckpoint(harness.Config.Connector.Id));
            Assert.NotNull(SyncState.ReadLastSync(harness.Config.Connector.Id));
        }
        finally
        {
            Metrics.ResetForTests();
        }
    }

    [Fact]
    public async Task BreakerOpenAtStart_PausesImmediately_NoWork()
    {
        Metrics.ResetForTests();
        try
        {
            using var harness = new PipelineHarness();
            harness.AddTeamsite("ts1");
            harness.AddContent(TestContent.Make("c1"));
            harness.Pipeline.CriticalBreakerOpenProbe = () => true;  // open from the start

            var ok = await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem");

            Assert.True(ok);                       // paused, not a failure
            Assert.Empty(harness.PutItemIds());    // no work started
            Assert.Null(SyncState.ReadLastSync(harness.Config.Connector.Id));
            Assert.Equal(1, Metrics.DegradedPauses);
        }
        finally
        {
            Metrics.ResetForTests();
        }
    }

    [Fact]
    public async Task NoBreakerOpen_CrawlCompletesNormally()
    {
        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1"));
        harness.Pipeline.CriticalBreakerOpenProbe = () => false;  // healthy

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));
        Assert.Contains("c1", harness.PutItemIds());
        Assert.NotNull(SyncState.ReadLastSync(harness.Config.Connector.Id));
    }
}

// ── readiness + metrics ──────────────────────────────────────────────────────

public class BreakerObservabilityTests : IDisposable
{
    public BreakerObservabilityTests()
    {
        CircuitBreakerRegistry.ResetForTests();
        Metrics.ResetForTests();
    }

    public void Dispose()
    {
        CircuitBreakerRegistry.ResetForTests();
        Metrics.ResetForTests();
    }

    [Fact]
    public void Readiness_FlipsWhenCriticalBreakerOpens()
    {
        var breaker = CircuitBreakerRegistry.Register(
            new CircuitBreaker("graph", new CircuitBreakerOptions { Enabled = true, FailureThreshold = 1 },
                critical: true));
        Assert.False(CircuitBreakerRegistry.AnyCriticalOpen);   // ready

        breaker.ForceOpenForTests();
        Assert.True(CircuitBreakerRegistry.AnyCriticalOpen);    // NOT ready
        Assert.Contains("graph", CircuitBreakerRegistry.OpenCriticalNames);
    }

    [Fact]
    public void NonCriticalBreakerOpen_DoesNotAffectReadiness()
    {
        var breaker = CircuitBreakerRegistry.Register(
            new CircuitBreaker("aux", new CircuitBreakerOptions { Enabled = true, FailureThreshold = 1 },
                critical: false));
        breaker.ForceOpenForTests();
        Assert.False(CircuitBreakerRegistry.AnyCriticalOpen);
    }

    [Fact]
    public void Metrics_RenderBreakerStateAndCounters()
    {
        var clock = new FakeClock();
        var breaker = CircuitBreakerRegistry.Register(
            new CircuitBreaker("seismic", new CircuitBreakerOptions
            {
                Enabled = true, FailureThreshold = 1, OpenDuration = TimeSpan.FromSeconds(10),
            }, critical: true, clock.Func));

        var closed = Metrics.RenderPrometheus();
        Assert.Contains("seismic_connector_circuit_breaker_state{dependency=\"seismic\"} 0", closed);

        breaker.ForceOpenForTests();
        var open = Metrics.RenderPrometheus();
        Assert.Contains("seismic_connector_circuit_breaker_state{dependency=\"seismic\"} 1", open);
        Assert.Contains("seismic_connector_circuit_breaker_trips_total{dependency=\"seismic\"} 1", open);

        clock.Advance(TimeSpan.FromSeconds(11));
        var halfOpen = Metrics.RenderPrometheus();
        Assert.Contains("seismic_connector_circuit_breaker_state{dependency=\"seismic\"} 2", halfOpen);
    }

    [Fact]
    public void Metrics_DegradedPausesCounter()
    {
        Metrics.IncDegradedPauses(2);
        Assert.Contains("seismic_connector_degraded_pauses_total 2", Metrics.RenderPrometheus());
    }
}

// ── config parsing ───────────────────────────────────────────────────────────

public class CircuitBreakerOptionsTests : IDisposable
{
    private static readonly string[] Vars =
    {
        "CIRCUIT_BREAKER", "CIRCUIT_BREAKER_FAILURE_THRESHOLD", "CIRCUIT_BREAKER_OPEN_SECONDS",
        "CIRCUIT_BREAKER_WINDOW_SECONDS", "CIRCUIT_BREAKER_HALF_OPEN_TRIALS",
    };

    public void Dispose()
    {
        foreach (var v in Vars)
            Environment.SetEnvironmentVariable(v, null);
    }

    [Fact]
    public void Defaults_EnabledWithSaneThresholds()
    {
        foreach (var v in Vars)
            Environment.SetEnvironmentVariable(v, null);
        var o = CircuitBreakerOptions.FromEnv();
        Assert.True(o.Enabled);
        Assert.Equal(5, o.FailureThreshold);
        Assert.Equal(TimeSpan.FromSeconds(30), o.OpenDuration);
        Assert.Equal(TimeSpan.FromSeconds(60), o.Window);
        Assert.Equal(1, o.HalfOpenTrials);
    }

    [Fact]
    public void EscapeHatch_DisablesBreaker()
    {
        Environment.SetEnvironmentVariable("CIRCUIT_BREAKER", "false");
        Assert.False(CircuitBreakerOptions.FromEnv().Enabled);
    }

    [Fact]
    public void Overrides_AreParsed()
    {
        Environment.SetEnvironmentVariable("CIRCUIT_BREAKER_FAILURE_THRESHOLD", "10");
        Environment.SetEnvironmentVariable("CIRCUIT_BREAKER_OPEN_SECONDS", "120");
        Environment.SetEnvironmentVariable("CIRCUIT_BREAKER_WINDOW_SECONDS", "300");
        Environment.SetEnvironmentVariable("CIRCUIT_BREAKER_HALF_OPEN_TRIALS", "3");
        var o = CircuitBreakerOptions.FromEnv();
        Assert.Equal(10, o.FailureThreshold);
        Assert.Equal(TimeSpan.FromSeconds(120), o.OpenDuration);
        Assert.Equal(TimeSpan.FromSeconds(300), o.Window);
        Assert.Equal(3, o.HalfOpenTrials);
    }

    [Fact]
    public void InvalidValues_FallBackToDefaults()
    {
        Environment.SetEnvironmentVariable("CIRCUIT_BREAKER_FAILURE_THRESHOLD", "not-a-number");
        Environment.SetEnvironmentVariable("CIRCUIT_BREAKER_OPEN_SECONDS", "0");  // below min
        var o = CircuitBreakerOptions.FromEnv();
        Assert.Equal(5, o.FailureThreshold);
        Assert.Equal(TimeSpan.FromSeconds(30), o.OpenDuration);
    }
}
