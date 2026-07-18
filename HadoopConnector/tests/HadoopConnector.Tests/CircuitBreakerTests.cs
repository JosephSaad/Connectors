// Circuit-breaker core: state transitions, window sampling, half-open probes,
// concurrency-safe counting, disabled passthrough, and config parsing. A
// virtual clock drives all timing deterministically.

using HadoopConnector.Infrastructure;

namespace HadoopConnector.Tests;

public class CircuitBreakerTests
{
    private DateTime _now = new(2026, 7, 13, 10, 0, 0, DateTimeKind.Utc);

    private CircuitBreaker Make(
        int threshold = 3, int openSeconds = 30, int windowSeconds = 60, int halfOpenTrials = 2)
        => new("test", new CircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = threshold,
            OpenDuration = TimeSpan.FromSeconds(openSeconds),
            Window = TimeSpan.FromSeconds(windowSeconds),
            HalfOpenTrials = halfOpenTrials,
        }, () => _now);

    [Fact]
    public void StartsClosed_AllowsCalls()
    {
        var b = Make();
        Assert.Equal(CircuitState.Closed, b.State);
        Assert.True(b.TryAcquire());
    }

    [Fact]
    public void Closed_TripsToOpen_AtThreshold()
    {
        var b = Make(threshold: 3);
        b.OnFailure();
        b.OnFailure();
        Assert.Equal(CircuitState.Closed, b.State);  // 2 < 3
        b.OnFailure();
        Assert.Equal(CircuitState.Open, b.State);    // 3 == threshold → trip
        Assert.False(b.TryAcquire());                // fail fast
        Assert.Equal(1, b.Trips);
    }

    [Fact]
    public void Open_MovesToHalfOpen_AfterOpenDuration()
    {
        var b = Make(threshold: 1, openSeconds: 30);
        b.OnFailure();
        Assert.Equal(CircuitState.Open, b.State);
        Assert.False(b.TryAcquire());

        _now = _now.AddSeconds(29);
        Assert.Equal(CircuitState.Open, b.State);    // not yet
        _now = _now.AddSeconds(2);                   // 31s elapsed
        Assert.Equal(CircuitState.HalfOpen, b.State);
        Assert.True(b.TryAcquire());                 // a trial is admitted
    }

    [Fact]
    public void HalfOpen_ClosesAfterEnoughSuccesses()
    {
        var b = Make(threshold: 1, openSeconds: 10, halfOpenTrials: 2);
        b.OnFailure();
        _now = _now.AddSeconds(11);
        Assert.Equal(CircuitState.HalfOpen, b.State);

        Assert.True(b.TryAcquire());
        b.OnSuccess();
        Assert.Equal(CircuitState.HalfOpen, b.State);  // 1 of 2
        Assert.True(b.TryAcquire());
        b.OnSuccess();
        Assert.Equal(CircuitState.Closed, b.State);    // recovered
        Assert.Equal(1, b.Resets);
    }

    [Fact]
    public void HalfOpen_ReopensOnFailure()
    {
        var b = Make(threshold: 1, openSeconds: 10, halfOpenTrials: 2);
        b.OnFailure();
        _now = _now.AddSeconds(11);
        Assert.Equal(CircuitState.HalfOpen, b.State);

        Assert.True(b.TryAcquire());
        b.OnFailure();                                 // a probe failed
        Assert.Equal(CircuitState.Open, b.State);
        Assert.Equal(2, b.Trips);                      // original + reopen
    }

    [Fact]
    public void HalfOpen_LimitsConcurrentTrials()
    {
        var b = Make(threshold: 1, openSeconds: 10, halfOpenTrials: 2);
        b.OnFailure();
        _now = _now.AddSeconds(11);
        Assert.Equal(CircuitState.HalfOpen, b.State);

        Assert.True(b.TryAcquire());   // trial 1
        Assert.True(b.TryAcquire());   // trial 2
        Assert.False(b.TryAcquire());  // no more probes admitted
    }

    [Fact]
    public void Success_ClearsFailureWindow_InClosed()
    {
        var b = Make(threshold: 3);
        b.OnFailure();
        b.OnFailure();
        b.OnSuccess();                 // healthy → window cleared
        b.OnFailure();
        b.OnFailure();
        Assert.Equal(CircuitState.Closed, b.State);  // only 2 since the reset
    }

    [Fact]
    public void FailuresOutsideWindow_DoNotTrip()
    {
        var b = Make(threshold: 3, windowSeconds: 60);
        b.OnFailure();                 // t=0
        _now = _now.AddSeconds(40);
        b.OnFailure();                 // t=40
        _now = _now.AddSeconds(40);    // t=80 — the first failure aged out
        b.OnFailure();                 // only 2 within the last 60s
        Assert.Equal(CircuitState.Closed, b.State);
    }

    [Fact]
    public void OnIgnored_NeverTrips()
    {
        var b = Make(threshold: 2);
        for (var i = 0; i < 20; i++)
            b.OnIgnored();             // 4xx / 429 flow control
        Assert.Equal(CircuitState.Closed, b.State);
        Assert.Equal(0, b.Trips);
    }

    [Fact]
    public void HalfOpen_IgnoredProbes_DoNotLeakSlots()
    {
        // Regression: probes ending in an ignored outcome (429/4xx) must
        // release their half-open slot. Before the fix, HalfOpenTrials+1
        // ignored probes wedged the breaker OPEN permanently (no probe could
        // ever be admitted again, so it could never close or re-open).
        var b = Make(threshold: 1, openSeconds: 10, halfOpenTrials: 2);
        b.OnFailure();
        _now = _now.AddSeconds(11);
        Assert.Equal(CircuitState.HalfOpen, b.State);

        for (var i = 0; i < 3; i++)  // HalfOpenTrials + 1 ignored probes
        {
            Assert.True(b.TryAcquire());  // a slot must still be admitted
            b.OnIgnored();                // 429/4xx probe outcome — releases it
        }

        // The breaker still admits probes and can close on real successes.
        Assert.True(b.TryAcquire());
        b.OnSuccess();
        Assert.True(b.TryAcquire());
        b.OnSuccess();
        Assert.Equal(CircuitState.Closed, b.State);
        Assert.Equal(1, b.Resets);
    }

    [Fact]
    public void HalfOpen_IgnoredThenFailure_StillReopens()
    {
        // An ignored probe must not mask a subsequent real failure.
        var b = Make(threshold: 1, openSeconds: 10, halfOpenTrials: 2);
        b.OnFailure();
        _now = _now.AddSeconds(11);
        Assert.Equal(CircuitState.HalfOpen, b.State);

        Assert.True(b.TryAcquire());
        b.OnIgnored();
        Assert.True(b.TryAcquire());
        b.OnFailure();
        Assert.Equal(CircuitState.Open, b.State);
    }

    [Fact]
    public void Disabled_IsPurePassthrough()
    {
        var b = new CircuitBreaker("off", CircuitBreakerOptions.Disabled, () => _now);
        for (var i = 0; i < 100; i++)
            b.OnFailure();
        Assert.Equal(CircuitState.Closed, b.State);
        Assert.True(b.TryAcquire());
        Assert.Equal(0, b.Trips);
        Assert.False(b.IsOpen);
    }

    [Fact]
    public async Task ConcurrentFailures_CountSafely()
    {
        // 10 threads each record enough failures that, combined, far exceed the
        // threshold — the breaker must end Open with a consistent single trip.
        var b = Make(threshold: 50, windowSeconds: 300);
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 20; i++)
                b.OnFailure();
        })).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(CircuitState.Open, b.State);
        Assert.Equal(1, b.Trips);   // exactly one open transition despite the race
    }

    [Fact]
    public void FromEnv_ParsesKnobs_AndDisableFlag()
    {
        using (new EnvScope(
            ("CIRCUIT_BREAKER", null),
            ("CIRCUIT_BREAKER_FAILURE_THRESHOLD", "7"),
            ("CIRCUIT_BREAKER_OPEN_SECONDS", "45"),
            ("CIRCUIT_BREAKER_WINDOW_SECONDS", "120"),
            ("CIRCUIT_BREAKER_HALF_OPEN_TRIALS", "3")))
        {
            var o = CircuitBreakerOptions.FromEnv();
            Assert.True(o.Enabled);   // default on
            Assert.Equal(7, o.FailureThreshold);
            Assert.Equal(45, o.OpenDuration.TotalSeconds);
            Assert.Equal(120, o.Window.TotalSeconds);
            Assert.Equal(3, o.HalfOpenTrials);
        }
        using (new EnvScope(("CIRCUIT_BREAKER", "false")))
            Assert.False(CircuitBreakerOptions.FromEnv().Enabled);
        using (new EnvScope(("CIRCUIT_BREAKER", "true")))
            Assert.True(CircuitBreakerOptions.FromEnv().Enabled);
    }
}

public class BreakersRegistryTests : IDisposable
{
    public void Dispose() => Breakers.ResetForTests();

    [Fact]
    public void Default_IsInertPassthrough()
    {
        Breakers.ResetForTests();
        Assert.False(Breakers.IsAnyCriticalOpen);
        Assert.False(Breakers.Hdfs.Enabled);
        Assert.False(Breakers.Graph.Enabled);
        Assert.Empty(Breakers.OpenCritical());
    }

    [Fact]
    public void Initialize_InstallsConfiguredBreakers()
    {
        Breakers.Initialize(new CircuitBreakerOptions { Enabled = true, FailureThreshold = 1 });
        Assert.True(Breakers.Graph.Enabled);

        Breakers.Graph.TripForTests();
        Assert.True(Breakers.IsAnyCriticalOpen);
        Assert.Contains("graph", Breakers.OpenCritical());
        Breakers.ResetForTests();
    }

    [Fact]
    public void Metrics_RenderBreakerStateAndCounters()
    {
        Breakers.Initialize(new CircuitBreakerOptions { Enabled = true, FailureThreshold = 1 });
        Breakers.Hdfs.TripForTests();

        var text = Metrics.RenderPrometheus();
        Assert.Contains("hadoop_connector_circuit_breaker_state{dependency=\"hdfs\"} 2", text);
        Assert.Contains("hadoop_connector_circuit_breaker_state{dependency=\"graph\"} 0", text);
        Assert.Contains("hadoop_connector_circuit_breaker_trips_total{dependency=\"hdfs\"} 1", text);
        Assert.Contains("# TYPE hadoop_connector_circuit_breaker_state gauge", text);
        Breakers.ResetForTests();
    }
}
