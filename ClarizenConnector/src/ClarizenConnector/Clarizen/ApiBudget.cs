// Clarizen/ApiBudget.cs
// ---------------------
// Client-side API-quota governor. Clarizen orgs have a per-day API call quota;
// CLARIZEN_API_CALLS_PER_DAY sets the client-side budget this connector may
// spend of it (leave headroom for interactive users!). The budget resets at
// UTC midnight. When the budget is exhausted the client throws
// ClarizenQuotaExceededException — the crawl checkpoint-saves and the next
// scheduled cycle resumes where it left off.
//
// A secondary pacing knob, CLARIZEN_MAX_CALLS_PER_MINUTE (default 60), smooths
// bursts: MinInterval = 60s / rate; PaceDelay() returns how long the caller
// should wait before the next call.

namespace ClarizenConnector.Clarizen;

public sealed class ClarizenQuotaExceededException : Exception
{
    public ClarizenQuotaExceededException(string message) : base(message)
    {
    }
}

public sealed class ApiBudget
{
    private readonly object _sync = new();
    private readonly int _callsPerDay;
    private readonly int _callsPerMinute;
    private readonly Func<DateTime> _utcNow;

    private DateTime _windowDateUtc;
    private int _used;
    private DateTime _lastCallUtc = DateTime.MinValue;

    public ApiBudget(int callsPerDay, int callsPerMinute = 60, Func<DateTime>? utcNow = null)
    {
        _callsPerDay = Math.Max(1, callsPerDay);
        _callsPerMinute = Math.Max(1, callsPerMinute);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _windowDateUtc = _utcNow().Date;
    }

    public int CallsPerDay => _callsPerDay;

    /// <summary>Remaining calls in the current UTC-day window.</summary>
    public int Remaining
    {
        get
        {
            lock (_sync)
            {
                RollWindow();
                return Math.Max(0, _callsPerDay - _used);
            }
        }
    }

    /// <summary>Calls consumed in the current UTC-day window.</summary>
    public int Used
    {
        get
        {
            lock (_sync)
            {
                RollWindow();
                return _used;
            }
        }
    }

    /// <summary>Next UTC midnight, when the window resets.</summary>
    public DateTime ResetsAtUtc
    {
        get
        {
            lock (_sync)
            {
                RollWindow();
                return _windowDateUtc.AddDays(1);
            }
        }
    }

    /// <summary>Minimum spacing between calls from the per-minute rate.</summary>
    public TimeSpan MinInterval => TimeSpan.FromSeconds(60.0 / _callsPerMinute);

    /// <summary>
    /// Consume one call from the budget. Throws ClarizenQuotaExceededException
    /// when the daily budget is exhausted. Returns the pacing delay the caller
    /// should apply BEFORE issuing the call (zero when under the rate).
    /// </summary>
    public TimeSpan Consume()
    {
        lock (_sync)
        {
            RollWindow();
            if (_used >= _callsPerDay)
            {
                throw new ClarizenQuotaExceededException(
                    $"Clarizen API budget exhausted: {_used}/{_callsPerDay} calls used today "
                    + $"(resets at {_windowDateUtc.AddDays(1):yyyy-MM-dd'T'HH:mm:ss}Z). "
                    + "The crawl checkpoint was saved; the next cycle will resume.");
            }
            _used++;

            var now = _utcNow();
            var earliest = _lastCallUtc + MinInterval;
            var delay = earliest > now ? earliest - now : TimeSpan.Zero;
            _lastCallUtc = now + delay;
            return delay;
        }
    }

    /// <summary>True when at least <paramref name="calls"/> remain today.</summary>
    public bool HasBudget(int calls = 1)
    {
        lock (_sync)
        {
            RollWindow();
            return _callsPerDay - _used >= calls;
        }
    }

    private void RollWindow()
    {
        var today = _utcNow().Date;
        if (today > _windowDateUtc)
        {
            _windowDateUtc = today;
            _used = 0;
        }
    }
}
