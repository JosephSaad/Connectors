// Infrastructure/CircuitBreaker.cs
// --------------------------------
// A reusable three-state circuit breaker (Closed → Open → HalfOpen) that fails
// fast during a SUSTAINED outage — distinct from the retry/backoff layer, which
// smooths transient blips. Retry handles "this one call hiccuped"; the breaker
// handles "this dependency has been down for a while, stop hammering it".
//
// Only REAL failures count: OnFailure() is for 5xx / timeouts / connection
// errors. 4xx/validation and honoured 429-with-Retry-After call OnIgnored()
// (429 is flow control, not an outage) so they never trip the breaker. A
// success clears the failure window, so intermittent errors that recover never
// accumulate to a trip.
//
// State machine:
//   Closed   — calls allowed. Failures within the rolling Window are counted;
//              at FailureThreshold the breaker trips to Open.
//   Open      — calls rejected (TryAcquire == false) until OpenDuration elapses,
//              then the breaker moves to HalfOpen.
//   HalfOpen — up to HalfOpenTrials probe calls are allowed. HalfOpenTrials
//              successes close the breaker; any failure re-opens it.
//
// Clock-injectable (Func<DateTime> now) for deterministic tests and thread-safe
// (one lock) because crawls run concurrent object/batch workers. When disabled
// it is a pure passthrough: TryAcquire always true, OnFailure/OnSuccess no-ops,
// State always Closed — the guaranteed-unchanged escape hatch.

namespace ClarizenConnector.Infrastructure;

/// <summary>Thrown when a call is rejected because a dependency's breaker is open.
/// The pipeline treats it as degraded mode (pause + checkpoint), not a crash.</summary>
public sealed class CircuitOpenException : Exception
{
    public CircuitOpenException(string dependency)
        : base($"Circuit open for dependency '{dependency}'; failing fast.")
        => Dependency = dependency;

    public string Dependency { get; }
}

public enum CircuitState
{
    Closed = 0,
    HalfOpen = 1,
    Open = 2,
}

public sealed class CircuitBreakerOptions
{
    public bool Enabled { get; init; } = true;
    public int FailureThreshold { get; init; } = 5;
    public TimeSpan OpenDuration { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(60);
    public int HalfOpenTrials { get; init; } = 2;

    /// <summary>A disabled (passthrough) options set.</summary>
    public static CircuitBreakerOptions Disabled { get; } = new() { Enabled = false };

    /// <summary>Read CIRCUIT_BREAKER_* from the environment (CIRCUIT_BREAKER=false disables).</summary>
    public static CircuitBreakerOptions FromEnv() => new()
    {
        Enabled = CircuitBreakerEnabledFromEnv(),
        FailureThreshold = Math.Max(1, EnvFlags.GetInt("CIRCUIT_BREAKER_FAILURE_THRESHOLD", 5)),
        OpenDuration = TimeSpan.FromSeconds(Math.Max(1, EnvFlags.GetInt("CIRCUIT_BREAKER_OPEN_SECONDS", 30))),
        Window = TimeSpan.FromSeconds(Math.Max(1, EnvFlags.GetInt("CIRCUIT_BREAKER_WINDOW_SECONDS", 60))),
        HalfOpenTrials = Math.Max(1, EnvFlags.GetInt("CIRCUIT_BREAKER_HALF_OPEN_TRIALS", 2)),
    };

    /// <summary>CIRCUIT_BREAKER defaults to true; only an explicit false disables it.</summary>
    /// <remarks>
    /// Spelled !IsFalse, which is what the summary above has always claimed. The
    /// previous form — blank-or-IsTrue — disagreed with it: an unrecognised value
    /// such as CIRCUIT_BREAKER=on (or a typo like "ture") is not blank and is not
    /// truthy, so it returned false and left every breaker in passthrough. A
    /// protective default must only be switched off deliberately.
    /// </remarks>
    internal static bool CircuitBreakerEnabledFromEnv() => !EnvFlags.IsFalse("CIRCUIT_BREAKER");
}

public sealed class CircuitBreaker
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector.breaker");

    private readonly CircuitBreakerOptions _options;
    private readonly Func<DateTime> _now;
    private readonly object _lock = new();

    // Failure timestamps within the sampling window (Closed state).
    private readonly Queue<DateTime> _failures = new();
    private CircuitState _state = CircuitState.Closed;
    private DateTime _openedAtUtc;
    private int _halfOpenSuccesses;
    private int _halfOpenInFlight;
    private long _trips;
    private long _resets;

    public CircuitBreaker(string name, CircuitBreakerOptions options, Func<DateTime>? now = null)
    {
        Name = name;
        _options = options;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public string Name { get; }

    public bool Enabled => _options.Enabled;

    /// <summary>A shared passthrough breaker (never trips). Used when a client is unbreakered.</summary>
    public static CircuitBreaker Disabled { get; } =
        new("disabled", CircuitBreakerOptions.Disabled);

    public long Trips
    {
        get { lock (_lock) return _trips; }
    }

    public long Resets
    {
        get { lock (_lock) return _resets; }
    }

    /// <summary>Current state, after refreshing any elapsed Open→HalfOpen transition.</summary>
    public CircuitState State
    {
        get
        {
            if (!_options.Enabled)
                return CircuitState.Closed;
            lock (_lock)
            {
                Refresh();
                return _state;
            }
        }
    }

    /// <summary>True when the breaker is Open (fail-fast) — after refresh.</summary>
    public bool IsOpen => State == CircuitState.Open;

    /// <summary>
    /// Decide whether a call may proceed. Closed → always. Open → false (unless
    /// OpenDuration elapsed, in which case it becomes HalfOpen and admits a
    /// trial). HalfOpen → admits up to HalfOpenTrials concurrent probes.
    /// Disabled → always true.
    /// </summary>
    public bool TryAcquire()
    {
        if (!_options.Enabled)
            return true;
        lock (_lock)
        {
            Refresh();
            switch (_state)
            {
                case CircuitState.Closed:
                    return true;
                case CircuitState.HalfOpen:
                    if (_halfOpenInFlight < _options.HalfOpenTrials)
                    {
                        _halfOpenInFlight++;
                        return true;
                    }
                    return false;
                default:  // Open
                    return false;
            }
        }
    }

    /// <summary>Record a successful call. Closed: clears the failure window.
    /// HalfOpen: counts toward closing the breaker.</summary>
    public void OnSuccess()
    {
        if (!_options.Enabled)
            return;
        lock (_lock)
        {
            Refresh();
            if (_state == CircuitState.HalfOpen)
            {
                _halfOpenInFlight = Math.Max(0, _halfOpenInFlight - 1);
                _halfOpenSuccesses++;
                if (_halfOpenSuccesses >= _options.HalfOpenTrials)
                    Close();
            }
            else if (_state == CircuitState.Closed)
            {
                _failures.Clear();  // a healthy call resets the window
            }
        }
    }

    /// <summary>Record a REAL failure (5xx / timeout / connection error). HalfOpen:
    /// re-opens immediately. Closed: trips once the window count hits the threshold.</summary>
    public void OnFailure()
    {
        if (!_options.Enabled)
            return;
        lock (_lock)
        {
            Refresh();
            var now = _now();
            if (_state == CircuitState.HalfOpen)
            {
                _halfOpenInFlight = Math.Max(0, _halfOpenInFlight - 1);
                Trip(now);  // a probe failed → back to Open
                return;
            }
            if (_state == CircuitState.Open)
                return;  // already open (a straggler from before the trip)

            _failures.Enqueue(now);
            Prune(now);
            if (_failures.Count >= _options.FailureThreshold)
                Trip(now);
        }
    }

    /// <summary>Record an outcome that must NOT affect the breaker's failure
    /// accounting: 4xx/validation and honoured 429-with-Retry-After (flow
    /// control, not an outage). In HalfOpen the probe slot acquired by
    /// <see cref="TryAcquire"/> is still released — otherwise ignored probes
    /// would leak slots until the breaker could never close or re-open.</summary>
    public void OnIgnored()
    {
        if (!_options.Enabled)
            return;
        lock (_lock)
        {
            Refresh();
            if (_state == CircuitState.HalfOpen)
                _halfOpenInFlight = Math.Max(0, _halfOpenInFlight - 1);
        }
    }

    /// <summary>Test seam: force the breaker Open immediately.</summary>
    internal void TripForTests()
    {
        lock (_lock)
            Trip(_now());
    }

    /// <summary>Test seam: force the breaker Closed and clear all counters/state.</summary>
    internal void ResetState()
    {
        lock (_lock)
        {
            _state = CircuitState.Closed;
            _failures.Clear();
            _halfOpenSuccesses = 0;
            _halfOpenInFlight = 0;
        }
    }

    // ── internals (all called under _lock) ───────────────────────────────────

    private void Refresh()
    {
        if (_state == CircuitState.Open && _now() - _openedAtUtc >= _options.OpenDuration)
        {
            _state = CircuitState.HalfOpen;
            _halfOpenSuccesses = 0;
            _halfOpenInFlight = 0;
            Logger.Info($"Circuit '{Name}': open→half-open (probing recovery).");
        }
    }

    private void Trip(DateTime now)
    {
        var wasOpen = _state == CircuitState.Open;
        _state = CircuitState.Open;
        _openedAtUtc = now;
        _failures.Clear();
        _halfOpenSuccesses = 0;
        _halfOpenInFlight = 0;
        if (!wasOpen)
        {
            _trips++;
            Logger.Warning(
                $"Circuit '{Name}': TRIPPED (open) — failing fast for {_options.OpenDuration.TotalSeconds:0}s.");
        }
    }

    private void Close()
    {
        _state = CircuitState.Closed;
        _failures.Clear();
        _halfOpenSuccesses = 0;
        _halfOpenInFlight = 0;
        _resets++;
        Logger.Info($"Circuit '{Name}': recovered (half-open→closed).");
    }

    private void Prune(DateTime now)
    {
        while (_failures.Count > 0 && now - _failures.Peek() > _options.Window)
            _failures.Dequeue();
    }
}
