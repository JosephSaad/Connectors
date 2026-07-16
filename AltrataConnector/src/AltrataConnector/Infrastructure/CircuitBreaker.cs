// Infrastructure/CircuitBreaker.cs
// -------------------------------
// Reusable circuit breaker for Disaster Recovery & Resilience (degraded mode /
// fail-safe). DISTINCT from the retry/backoff layer: retries ride out transient
// blips within a single call; the breaker detects a SUSTAINED outage across
// calls and fails fast, so a downed dependency stops being hammered and the
// crawl can pause at a safe boundary instead of dead-lettering everything.
//
// States: Closed (normal) → Open (fail fast for OpenDuration) → HalfOpen (allow
// HalfOpenTrials probes) → Closed on trial success, or back to Open on a trial
// failure. Thread-safe; the clock is injectable for deterministic tests.
//
// What counts as a failure is the CALLER's decision (isFailureResult /
// isTripException), so 4xx/validation and honored 429s do NOT trip while
// 5xx/timeout/connection errors do. Disabled (CIRCUIT_BREAKER=false) ⇒ pure
// passthrough with zero locking.

namespace AltrataConnector.Infrastructure;

public enum CircuitState
{
    Closed = 0,
    Open = 1,
    HalfOpen = 2,
}

/// <summary>Thrown when a call is rejected because the breaker is Open (or the
/// half-open probe budget is exhausted) — the fail-fast signal.</summary>
public sealed class CircuitOpenException : Exception
{
    public string Dependency { get; }
    public CircuitOpenException(string dependency)
        : base($"Circuit breaker '{dependency}' is open — failing fast (dependency degraded).") =>
        Dependency = dependency;
}

public sealed record CircuitBreakerOptions
{
    public bool Enabled { get; init; } = true;
    public int FailureThreshold { get; init; } = 5;
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan OpenDuration { get; init; } = TimeSpan.FromSeconds(30);
    public int HalfOpenTrials { get; init; } = 2;
    /// <summary>Critical breakers flip /ready to not-ready when Open.</summary>
    public bool Critical { get; init; }

    /// <summary>Read CIRCUIT_BREAKER_* env vars (all optional; sane defaults).</summary>
    public static CircuitBreakerOptions FromEnv(bool critical)
    {
        static int Int(string name, int fallback)
        {
            var raw = Environment.GetEnvironmentVariable(name);
            return int.TryParse(raw, out var n) && n > 0 ? n : fallback;
        }
        return new CircuitBreakerOptions
        {
            Enabled = !EnvFlags.IsFalse("CIRCUIT_BREAKER"),  // default true; only "false"/0/no disables
            FailureThreshold = Int("CIRCUIT_BREAKER_FAILURE_THRESHOLD", 5),
            Window = TimeSpan.FromSeconds(Int("CIRCUIT_BREAKER_WINDOW_SECONDS", 60)),
            OpenDuration = TimeSpan.FromSeconds(Int("CIRCUIT_BREAKER_OPEN_SECONDS", 30)),
            HalfOpenTrials = Int("CIRCUIT_BREAKER_HALFOPEN_TRIALS", 2),
            Critical = critical,
        };
    }
}

/// <summary>Point-in-time view of a breaker for /metrics and /health.</summary>
public sealed record BreakerSnapshot(string Name, bool Critical, CircuitState State, long Trips, long Resets);

/// <summary>Shared HTTP failure classification for breakers: transport errors
/// and timeouts trip; a graceful-stop cancellation (the token was requested)
/// does not. 4xx / 429 are handled by the result classifier, not here.</summary>
public static class HttpTripPolicy
{
    public static bool IsTrip(Exception ex, CancellationToken ct)
    {
        if (ex is OperationCanceledException && ct.IsCancellationRequested)
            return false;                       // graceful stop — not a dependency failure
        return ex is HttpRequestException       // connection refused / reset / DNS
            || ex is TaskCanceledException;      // HttpClient timeout
    }
}

public sealed class CircuitBreaker
{
    private static readonly IAppLogger Logger = Logging.GetLogger("altrata_connector.breaker");

    private readonly object _lock = new();
    private readonly Func<DateTime> _clock;
    private readonly List<DateTime> _failures = new();

    private CircuitState _state = CircuitState.Closed;
    private DateTime _openUntil;
    private int _halfOpenInFlight;
    private int _halfOpenSuccesses;

    public string Name { get; }
    public CircuitBreakerOptions Options { get; }
    public long TripCount { get; private set; }
    public long ResetCount { get; private set; }

    public CircuitBreaker(string name, CircuitBreakerOptions options, Func<DateTime>? clock = null)
    {
        Name = name;
        Options = options;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Current state; advances Open→HalfOpen lazily once OpenDuration elapses.</summary>
    public CircuitState State
    {
        get
        {
            if (!Options.Enabled)
                return CircuitState.Closed;
            lock (_lock)
            {
                AdvanceOpenToHalfOpen(_clock());
                return _state;
            }
        }
    }

    /// <summary>True when the breaker is Open (fail-fast). Drives readiness.</summary>
    public bool IsOpen => State == CircuitState.Open;

    public BreakerSnapshot Snapshot() => new(Name, Options.Critical, State, TripCount, ResetCount);

    /// <summary>
    /// Run <paramref name="action"/> through the breaker. Throws
    /// <see cref="CircuitOpenException"/> immediately when Open. A result for
    /// which <paramref name="isFailureResult"/> is true, or an exception for
    /// which <paramref name="isTripException"/> is true, counts as a failure;
    /// anything else is a success. When disabled it is a pure passthrough.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        Func<Task<T>> action,
        Func<T, bool>? isFailureResult = null,
        Func<Exception, bool>? isTripException = null)
    {
        if (!Options.Enabled)
            return await action();

        var trial = Enter();
        try
        {
            var result = await action();
            Record(trial, failed: isFailureResult?.Invoke(result) ?? false);
            return result;
        }
        catch (CircuitOpenException)
        {
            // Never counts as a dependency failure — the breaker itself refused.
            Leave(trial);
            throw;
        }
        catch (Exception ex)
        {
            Record(trial, failed: isTripException?.Invoke(ex) ?? true);
            throw;
        }
    }

    // ---- state machine ------------------------------------------------------

    private bool Enter()
    {
        lock (_lock)
        {
            var now = _clock();
            AdvanceOpenToHalfOpen(now);
            switch (_state)
            {
                case CircuitState.Closed:
                    return false;  // not a trial
                case CircuitState.HalfOpen:
                    if (_halfOpenInFlight >= Options.HalfOpenTrials)
                        throw new CircuitOpenException(Name);
                    _halfOpenInFlight++;
                    return true;   // trial call
                default:  // Open
                    throw new CircuitOpenException(Name);
            }
        }
    }

    private void Leave(bool trial)
    {
        if (!trial)
            return;
        lock (_lock)
            _halfOpenInFlight = Math.Max(0, _halfOpenInFlight - 1);
    }

    private void Record(bool trial, bool failed)
    {
        lock (_lock)
        {
            var now = _clock();
            if (trial)
                _halfOpenInFlight = Math.Max(0, _halfOpenInFlight - 1);

            if (failed)
            {
                if (_state == CircuitState.HalfOpen)
                {
                    Trip(now);  // a probe failed — reopen
                }
                else if (_state == CircuitState.Closed)
                {
                    _failures.Add(now);
                    Prune(now);
                    if (_failures.Count >= Options.FailureThreshold)
                        Trip(now);
                }
            }
            else
            {
                if (_state == CircuitState.HalfOpen)
                {
                    _halfOpenSuccesses++;
                    if (_halfOpenSuccesses >= Options.HalfOpenTrials)
                        Close();
                }
                // Closed success: the failure window simply ages out.
            }
        }
    }

    private void AdvanceOpenToHalfOpen(DateTime now)
    {
        if (_state == CircuitState.Open && now >= _openUntil)
        {
            _state = CircuitState.HalfOpen;
            _halfOpenInFlight = 0;
            _halfOpenSuccesses = 0;
            Logger.Info($"Circuit breaker '{Name}' half-open — probing (up to {Options.HalfOpenTrials} trial(s)).");
        }
    }

    private void Trip(DateTime now)
    {
        var wasOpen = _state == CircuitState.Open;
        _state = CircuitState.Open;
        _openUntil = now + Options.OpenDuration;
        _failures.Clear();
        _halfOpenInFlight = 0;
        _halfOpenSuccesses = 0;
        if (!wasOpen)
        {
            TripCount++;
            Logger.Warning($"Circuit breaker '{Name}' OPEN — failing fast for {Options.OpenDuration.TotalSeconds:0}s " +
                           $"(dependency sustained failures ≥ {Options.FailureThreshold}).");
        }
    }

    private void Close()
    {
        _state = CircuitState.Closed;
        _failures.Clear();
        _halfOpenInFlight = 0;
        _halfOpenSuccesses = 0;
        ResetCount++;
        Logger.Info($"Circuit breaker '{Name}' CLOSED — dependency recovered.");
    }

    private void Prune(DateTime now)
    {
        var cutoff = now - Options.Window;
        _failures.RemoveAll(ts => ts < cutoff);
    }
}
