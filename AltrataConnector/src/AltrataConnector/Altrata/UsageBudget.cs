// Altrata/UsageBudget.cs
// ----------------------
// WP-AL-4 — ENFORCEABLE usage ceilings for the billable enrichment API.
//
// ── THE GAP THIS CLOSES ─────────────────────────────────────────────────────
// The connector already COUNTED billable lookups (IStateStore.IncrementBillable
// Lookups → altrata_api_billable_lookups_total) and already SMOOTHED the call
// rate (ApiClient.RateLimiter). Neither can REFUSE a lookup: the counter is a
// post-hoc tally and the limiter only makes the caller wait. A runaway or
// abusive workload was therefore billable without bound. This adds the missing
// third thing — a ceiling that DECLINES.
//
// ── WHERE IT SITS (order matters) ───────────────────────────────────────────
// In AltrataApiClient.LookupPersonAsync the budget check runs
//   AFTER  the purpose veto (a disallowed purpose must never even CONSUME
//          budget — an attacker could otherwise exhaust the day's ceiling with
//          calls that were never going to be permitted, a denial-of-service on
//          the legitimate workload), and
//   BEFORE the rate limiter, the OAuth token fetch, the HTTP lookup and the
//          billable counter.
// Refusal is FAIL-CLOSED and modelled exactly on the purpose deny: PII-safe
// audit entry (Decision="deny", Billable=false), a dedicated metric
// (altrata_usage_denied_total) and a typed UsageBudgetExceededException.
// Nothing is billed, no request is ever enqueued.
//
// ── RESERVE / RELEASE ───────────────────────────────────────────────────────
// The ceiling is charged by RESERVING before the call and RELEASING if the call
// never became billable (breaker open, 5xx, timeout, graceful stop). Reserving
// up front is what makes the check atomic — a pure "read, then increment after
// success" would let N concurrent callers all observe used < limit and overshoot
// by N-1. A process that dies between reserve and release leaves the reservation
// consumed: deliberately conservative (errs toward the ceiling, never past it),
// and self-healing at the next window rollover.
//
// ── SCOPE OF THE CEILING (read this before setting a number) ────────────────
// The counters live in the state store's key/value facility, so the ceiling is
// scoped to ONE STATE STORE — i.e. per (backend, connector id):
//
//   * CONNECTION SHARDING does NOT multiply it today. The only caller of the
//     billable lookup is `ingest-item`, which runs on the BASE runtime
//     (Runtime.Create → CONNECTOR_ID); it is not shard-aware, so shard state
//     stores are never used for enrichment. If a shard-aware caller of
//     LookupPersonAsync is ever added, each shard would carry its OWN counter
//     and the real fleet ceiling would silently become N x the configured
//     value. Whoever adds that caller must revisit this.
//
//   * HOST COUNT DOES multiply it on the FILE backend (the default). Each host
//     keeps its own data/{CONNECTOR_ID}_state.json, so M hosts running the same
//     CONNECTOR_ID enforce M x the configured ceiling in aggregate. Set
//     USE_SQL_SERVER=true for a genuinely fleet-wide ceiling: dbo.altrata_kv is
//     shared and MutateValue reserves under UPDLOCK/HOLDLOCK in a transaction,
//     so concurrent nodes serialise against one counter.
//
// ── WINDOW SEMANTICS ────────────────────────────────────────────────────────
//   ALTRATA_MAX_LOOKUPS_PER_DAY    — calendar UTC day (TUMBLING: resets at
//        00:00 UTC). Matches how a "per day" contractual allowance reads.
//   ALTRATA_MAX_LOOKUPS_PER_WINDOW — ROLLING window of ALTRATA_USAGE_WINDOW_HOURS
//        (default 24). Approximated with 60 fixed buckets of window/60, summed
//        over the trailing window. The leading partial bucket is counted in
//        full, so the rolling check is marginally CONSERVATIVE (it can refuse
//        very slightly early, never late). Changing the window length resets the
//        rolling counter — the old buckets measure a different span.
// Both may be set; a lookup must clear BOTH. Both unset = no ceiling = the
// connector's pre-WP-AL-4 behaviour, byte-identical.

using System.Globalization;
using System.Text.Json;
using AltrataConnector.Config;
using AltrataConnector.State;

namespace AltrataConnector.Altrata;

/// <summary>Thrown when a lookup is refused because a usage ceiling is reached.
/// Fail-closed: nothing was billed and no request was sent.</summary>
public sealed class UsageBudgetExceededException : Exception
{
    public UsageBudgetExceededException(string message) : base(message) { }
}

/// <summary>The receipt for a granted reservation, so it can be released
/// against exactly the counters it charged (see <see cref="IUsageBudget.Release"/>).</summary>
public sealed record UsageReservation(string Day, long BucketIndex);

/// <summary>Outcome of a ceiling check. <see cref="Reservation"/> is non-null
/// only when <see cref="Allowed"/> and enforcement is on.</summary>
public sealed record UsageBudgetDecision(
    bool Allowed, string Reason, long Used, long Limit, UsageReservation? Reservation);

/// <summary>Current consumption, for reporting / gauges (no mutation).</summary>
public sealed record UsageSnapshot(long DayUsed, long DayLimit, long WindowUsed, long WindowLimit);

public interface IUsageBudget
{
    /// <summary>True when at least one ceiling is configured.</summary>
    bool Enforcing { get; }

    /// <summary>Atomically charge one lookup against every configured ceiling,
    /// or refuse. Never partially charges.</summary>
    UsageBudgetDecision TryReserve(DateTime utcNow);

    /// <summary>Give back a reservation whose lookup never became billable.</summary>
    void Release(UsageReservation reservation);

    /// <summary>Read consumption without charging.</summary>
    UsageSnapshot Peek(DateTime utcNow);
}

/// <summary>Ceiling configuration. Sourced from <see cref="AppConfig"/> so the
/// values are parsed and validated once, at load, by AppConfig.Load — a bad
/// number fails validate-config / startup rather than mid-crawl.</summary>
public sealed record UsageBudgetOptions
{
    public const string MaxPerDayEnvVar = "ALTRATA_MAX_LOOKUPS_PER_DAY";
    public const string MaxPerWindowEnvVar = "ALTRATA_MAX_LOOKUPS_PER_WINDOW";
    public const string WindowHoursEnvVar = "ALTRATA_USAGE_WINDOW_HOURS";

    /// <summary>Max billable lookups per calendar UTC day. 0 = unset = no ceiling.</summary>
    public int MaxPerDay { get; init; }

    /// <summary>Max billable lookups per rolling window. 0 = unset = no ceiling.</summary>
    public int MaxPerWindow { get; init; }

    /// <summary>Length of the rolling window in hours (default 24).</summary>
    public int WindowHours { get; init; } = 24;

    public bool Enforcing => MaxPerDay > 0 || MaxPerWindow > 0;

    /// <summary>Rolling window in seconds.</summary>
    public long WindowSeconds => (long)WindowHours * 3600;

    /// <summary>Bucket granularity: 1/60th of the window, at least one second.</summary>
    public long BucketSeconds => Math.Max(1, WindowSeconds / 60);

    /// <summary>How many trailing buckets make up the window.</summary>
    public long BucketCount => (WindowSeconds + BucketSeconds - 1) / BucketSeconds;

    public static UsageBudgetOptions FromConfig(AppConfig config) => new()
    {
        MaxPerDay = config.AltrataMaxLookupsPerDay,
        MaxPerWindow = config.AltrataMaxLookupsPerWindow,
        WindowHours = config.AltrataUsageWindowHours,
    };
}

/// <summary>
/// Durable usage meter over <see cref="IStateStore"/>'s key/value facility. All
/// counters for both windows live in ONE key, mutated through
/// <see cref="IStateStore.MutateValue"/>, so a reservation reads and writes both
/// ceilings inside a single lock acquisition: there is no window where the daily
/// counter has been charged but the rolling one has refused.
/// </summary>
public sealed class UsageMeter : IUsageBudget
{
    /// <summary>Key/value state key holding the whole usage ledger.</summary>
    public const string StateKey = StateKeys.UsageBudget;

    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly IStateStore _state;
    private readonly UsageBudgetOptions _options;

    public UsageMeter(IStateStore state, UsageBudgetOptions options)
    {
        _state = state;
        _options = options;
    }

    public UsageBudgetOptions Options => _options;

    public bool Enforcing => _options.Enforcing;

    /// <summary>Persisted shape of <see cref="StateKey"/>.</summary>
    private sealed class UsageDoc
    {
        /// <summary>UTC calendar day the daily counter belongs to (yyyy-MM-dd).</summary>
        public string Day { get; set; } = "";
        public long DayCount { get; set; }
        /// <summary>Bucket width the rolling buckets were recorded at; a change
        /// invalidates them (they measure a different span).</summary>
        public long BucketSeconds { get; set; }
        /// <summary>Bucket index (as a string key) → lookups charged in it.</summary>
        public Dictionary<string, long> Buckets { get; set; } = new();
    }

    private static UsageDoc Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new UsageDoc();
        try
        {
            return JsonSerializer.Deserialize<UsageDoc>(raw) ?? new UsageDoc();
        }
        catch (JsonException)
        {
            // An unreadable ledger must not fail OPEN into unbounded spend, and
            // must not wedge the connector either. Starting a fresh window is the
            // conservative-enough choice: the ceiling is immediately enforceable
            // again, at worst allowing one extra window's worth of lookups.
            return new UsageDoc();
        }
    }

    private static string DayOf(DateTime utcNow) =>
        utcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private long BucketOf(DateTime utcNow) =>
        (long)(utcNow - DateTime.UnixEpoch).TotalSeconds / _options.BucketSeconds;

    /// <summary>Roll the day over and drop buckets that have aged out of the
    /// window. Returns the live rolling total.</summary>
    private long Normalize(UsageDoc doc, DateTime utcNow, long bucketIndex)
    {
        var today = DayOf(utcNow);
        if (!string.Equals(doc.Day, today, StringComparison.Ordinal))
        {
            doc.Day = today;
            doc.DayCount = 0;
        }

        if (doc.BucketSeconds != _options.BucketSeconds)
        {
            doc.BucketSeconds = _options.BucketSeconds;
            doc.Buckets.Clear();
        }

        var oldestKept = bucketIndex - (_options.BucketCount - 1);
        foreach (var key in doc.Buckets.Keys.ToList())
        {
            if (!long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                || index < oldestKept || index > bucketIndex)
            {
                doc.Buckets.Remove(key);
            }
        }
        return doc.Buckets.Values.Sum();
    }

    public UsageBudgetDecision TryReserve(DateTime utcNow)
    {
        if (!Enforcing)
            return new UsageBudgetDecision(true, "no-ceiling", 0, 0, null);

        var bucketIndex = BucketOf(utcNow);
        UsageBudgetDecision decision = new(true, "within-ceiling", 0, 0, null);

        _state.MutateValue(StateKey, raw =>
        {
            var doc = Parse(raw);
            var windowUsed = Normalize(doc, utcNow, bucketIndex);

            if (_options.MaxPerDay > 0 && doc.DayCount >= _options.MaxPerDay)
            {
                decision = new UsageBudgetDecision(
                    false, "daily-ceiling", doc.DayCount, _options.MaxPerDay, null);
            }
            else if (_options.MaxPerWindow > 0 && windowUsed >= _options.MaxPerWindow)
            {
                decision = new UsageBudgetDecision(
                    false, $"rolling-{_options.WindowHours}h-ceiling",
                    windowUsed, _options.MaxPerWindow, null);
            }
            else
            {
                doc.DayCount++;
                var key = bucketIndex.ToString(CultureInfo.InvariantCulture);
                doc.Buckets[key] = doc.Buckets.TryGetValue(key, out var n) ? n + 1 : 1;
                decision = new UsageBudgetDecision(
                    true, "within-ceiling", doc.DayCount, _options.MaxPerDay,
                    new UsageReservation(doc.Day, bucketIndex));
            }

            // The pruned/rolled document is written back on refusal too — the
            // rollover housekeeping is what makes the next window enforceable.
            return JsonSerializer.Serialize(doc, JsonOptions);
        });

        return decision;
    }

    public void Release(UsageReservation reservation)
    {
        if (!Enforcing)
            return;

        _state.MutateValue(StateKey, raw =>
        {
            var doc = Parse(raw);

            // Only give the day back if the day has not rolled since the
            // reservation — after a rollover the counter is a different window.
            if (string.Equals(doc.Day, reservation.Day, StringComparison.Ordinal) && doc.DayCount > 0)
                doc.DayCount--;

            var key = reservation.BucketIndex.ToString(CultureInfo.InvariantCulture);
            if (doc.Buckets.TryGetValue(key, out var n))
            {
                if (n <= 1)
                    doc.Buckets.Remove(key);
                else
                    doc.Buckets[key] = n - 1;
            }

            return JsonSerializer.Serialize(doc, JsonOptions);
        });
    }

    public UsageSnapshot Peek(DateTime utcNow)
    {
        var doc = Parse(_state.GetValue(StateKey));
        var bucketIndex = BucketOf(utcNow);
        // Normalize on a local copy only — Peek never writes.
        var windowUsed = Normalize(doc, utcNow, bucketIndex);
        return new UsageSnapshot(doc.DayCount, _options.MaxPerDay, windowUsed, _options.MaxPerWindow);
    }
}
