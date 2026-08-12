// Infrastructure/CircuitBreaker.cs
// --------------------------------
// A reusable circuit breaker for the connector's external dependencies
// (Disaster Recovery & Resilience — Degraded Mode / Fail-Safe).
//
// This is DISTINCT from the retry/backoff ladder: retries absorb transient
// blips WITHIN a single call, whereas the breaker handles a SUSTAINED outage
// by failing fast ACROSS calls, so the connector stops hammering a dead
// dependency and can pause into degraded mode instead.
//
// States (mirrors the classic pattern; the numeric values are the /metrics
// gauge encoding):
//   Closed   (0) — calls flow; consecutive/windowed failures are counted.
//   Open     (1) — calls fail fast (CircuitOpenException) without touching the
//                  dependency, for OpenDuration.
//   HalfOpen (2) — after OpenDuration, up to HalfOpenTrials probe calls are
//                  allowed; one reachable result closes it, one unreachable
//                  result re-opens it.
//
// "Reachable" = the dependency answered (success, OR a non-tripping error such
// as 4xx / an honored 429 — flow control, the service is up). "Unreachable" =
// a tripping failure (5xx / timeout / connection error). The caller supplies
// the classifier so transport-specific exception types stay in the clients.
//
// The clock is injectable and every transition is under one lock, so the
// breaker is safe to share across the concurrent teamsite/content workers.

using System.Diagnostics.CodeAnalysis;

namespace Connector.Chassis;

/// <summary>Circuit-breaker state; numeric values are the /metrics gauge encoding.</summary>
public enum CircuitState
{
    Closed = 0,
    Open = 1,
    HalfOpen = 2,
}

/// <summary>Thrown when a call is short-circuited because the breaker is open.</summary>
public sealed class CircuitOpenException : Exception
{
    public string BreakerName { get; }

    public CircuitOpenException(string breakerName)
        : base($"Circuit breaker '{breakerName}' is open — dependency call short-circuited (degraded mode).")
    {
        BreakerName = breakerName;
    }
}

/// <summary>Tunable thresholds; read from CIRCUIT_BREAKER_* via <see cref="FromEnv"/>.</summary>
public sealed class CircuitBreakerOptions
{
    /// <summary>Master switch. CIRCUIT_BREAKER=false → pure passthrough escape hatch.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Tripping failures within <see cref="Window"/> that open the breaker.</summary>
    public int FailureThreshold { get; init; } = 5;

    /// <summary>Sliding failure-count window.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long the breaker stays open before allowing probe calls.</summary>
    public TimeSpan OpenDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Concurrent probe calls allowed in the half-open state.</summary>
    public int HalfOpenTrials { get; init; } = 1;

    public const string EnabledEnvVar = "CIRCUIT_BREAKER";

    /// <summary>Build options from CIRCUIT_BREAKER_* env vars (all defaulted; never throws).</summary>
    public static CircuitBreakerOptions FromEnv()
    {
        return new CircuitBreakerOptions
        {
            Enabled = ReadBool(EnabledEnvVar, defaultValue: true),
            FailureThreshold = ReadInt("CIRCUIT_BREAKER_FAILURE_THRESHOLD", 5, min: 1),
            OpenDuration = TimeSpan.FromSeconds(ReadInt("CIRCUIT_BREAKER_OPEN_SECONDS", 30, min: 1)),
            Window = TimeSpan.FromSeconds(ReadInt("CIRCUIT_BREAKER_WINDOW_SECONDS", 60, min: 1)),
            HalfOpenTrials = ReadInt("CIRCUIT_BREAKER_HALF_OPEN_TRIALS", 1, min: 1),
        };
    }

    private static bool ReadBool(string name, bool defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        return raw.Trim().ToLowerInvariant() is "true" or "1" or "yes";
    }

    private static int ReadInt(string name, int defaultValue, int min)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var value) && value >= min ? value : defaultValue;
    }
}

public sealed class CircuitBreaker
{
    private static readonly IAppLogger Logger = Chassis.GetLogger($"{Chassis.Identity.BaseLoggerName}.breaker");

    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;

    private CircuitState _state = CircuitState.Closed;
    private int _failureCount;
    private DateTimeOffset _windowStart;
    private DateTimeOffset _openedAt;
    private int _halfOpenInFlight;

    private long _tripCount;
    private long _resetCount;

    public string Name { get; }

    public CircuitBreakerOptions Options { get; }

    /// <summary>Whether an OPEN state here should flip /health readiness to not-ready.</summary>
    public bool Critical { get; }

    public CircuitBreaker(
        string name,
        CircuitBreakerOptions? options = null,
        bool critical = true,
        Func<DateTimeOffset>? clock = null)
    {
        Name = name;
        Options = options ?? new CircuitBreakerOptions();
        Critical = critical;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _windowStart = _clock();
    }

    public long TripCount => Interlocked.Read(ref _tripCount);

    public long ResetCount => Interlocked.Read(ref _resetCount);

    /// <summary>Current state (evaluates the Open→HalfOpen transition on read).</summary>
    public CircuitState State
    {
        get
        {
            if (!Options.Enabled)
                return CircuitState.Closed;
            lock (_gate)
            {
                EvaluateTransition(_clock());
                return _state;
            }
        }
    }

    /// <summary>
    /// Run <paramref name="operation"/> under the breaker.
    /// <list type="bullet">
    ///   <item>Open → throws <see cref="CircuitOpenException"/> WITHOUT running it.</item>
    ///   <item>Reachable result (success or a non-tripping error) → resets/closes.</item>
    ///   <item>Tripping failure (per <paramref name="isTrippingFailure"/>) → counts toward the threshold.</item>
    /// </list>
    /// The original exception is always re-thrown; the breaker only observes it.
    /// A caller-requested cancellation is neutral (never penalises the dependency).
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<Exception, bool> isTrippingFailure,
        CancellationToken ct = default)
    {
        if (!Options.Enabled)
            return await operation(ct).ConfigureAwait(false);

        var tookHalfOpenSlot = Enter();
        try
        {
            var result = await operation(ct).ConfigureAwait(false);
            OnReachable();
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Our own graceful-stop cancellation — neutral, not a dependency fault.
            throw;
        }
        catch (Exception ex)
        {
            if (isTrippingFailure(ex))
                OnUnreachable();
            else
                OnReachable();
            throw;
        }
        finally
        {
            if (tookHalfOpenSlot)
                ReleaseHalfOpenSlot();
        }
    }

    /// <summary>Non-generic convenience for void operations.</summary>
    public async Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        Func<Exception, bool> isTrippingFailure,
        CancellationToken ct = default)
    {
        await ExecuteAsync<object?>(async token =>
        {
            await operation(token).ConfigureAwait(false);
            return null;
        }, isTrippingFailure, ct).ConfigureAwait(false);
    }

    // ── state machine (all under _gate) ──────────────────────────────────────

    private bool Enter()
    {
        lock (_gate)
        {
            EvaluateTransition(_clock());
            switch (_state)
            {
                case CircuitState.Closed:
                    return false;
                case CircuitState.HalfOpen:
                    if (_halfOpenInFlight < Options.HalfOpenTrials)
                    {
                        _halfOpenInFlight++;
                        return true;
                    }
                    throw new CircuitOpenException(Name);
                default:  // Open
                    throw new CircuitOpenException(Name);
            }
        }
    }

    private void ReleaseHalfOpenSlot()
    {
        lock (_gate)
        {
            if (_halfOpenInFlight > 0)
                _halfOpenInFlight--;
        }
    }

    private void OnReachable()
    {
        lock (_gate)
        {
            _failureCount = 0;
            _windowStart = _clock();
            if (_state == CircuitState.HalfOpen)
            {
                _state = CircuitState.Closed;
                _halfOpenInFlight = 0;
                Interlocked.Increment(ref _resetCount);
                Logger.Info($"Circuit breaker '{Name}' recovered — closed.");
            }
        }
    }

    private void OnUnreachable()
    {
        lock (_gate)
        {
            var now = _clock();
            if (_state == CircuitState.HalfOpen)
            {
                // A probe failed — straight back to open.
                Trip(now);
                return;
            }
            if (_state != CircuitState.Closed)
                return;

            if (now - _windowStart >= Options.Window)
            {
                _windowStart = now;
                _failureCount = 0;
            }
            _failureCount++;
            if (_failureCount >= Options.FailureThreshold)
                Trip(now);
        }
    }

    private void EvaluateTransition(DateTimeOffset now)
    {
        if (_state == CircuitState.Open && now - _openedAt >= Options.OpenDuration)
        {
            _state = CircuitState.HalfOpen;
            _halfOpenInFlight = 0;
            Logger.Info($"Circuit breaker '{Name}' half-open — probing the dependency.");
        }
    }

    private void Trip(DateTimeOffset now)
    {
        var wasOpen = _state == CircuitState.Open;
        _state = CircuitState.Open;
        _openedAt = now;
        _failureCount = 0;
        _halfOpenInFlight = 0;
        if (!wasOpen)
        {
            Interlocked.Increment(ref _tripCount);
            Logger.Warning(
                $"Circuit breaker '{Name}' OPEN — failing fast for {Options.OpenDuration.TotalSeconds:F0}s "
                + "(sustained dependency failure).");
        }
    }

    // ── test seams ───────────────────────────────────────────────────────────

    /// <summary>Test seam: force the breaker open immediately.</summary>
    [SuppressMessage("Design", "CA1030", Justification = "test seam, not an event")]
    internal void ForceOpenForTests()
    {
        lock (_gate)
        {
            Trip(_clock());
        }
    }

    /// <summary>Test seam: current count of in-flight half-open probe slots.
    /// Used by the stress harness to prove a slot is released on every exit
    /// path (no half-open permit leak under concurrent load).</summary>
    internal int HalfOpenInFlight
    {
        get { lock (_gate) return _halfOpenInFlight; }
    }
}
