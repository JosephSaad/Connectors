// Webhook/EventDebouncer.cs
// -------------------------
// Coalesces webhook events for the same entity within a short window so a
// burst of notifications for one record results in a single targeted ingest.
//
// Rule: last-writer-wins per entity key (ObjectType|LocalId). Offering an
// event (re)starts that entity's window and overwrites its pending change
// kind, so duplicates collapse and the final observed state (upsert vs delete)
// is what gets applied. An entity becomes "ready" when its window has elapsed
// with no newer event; DrainReady returns and removes those.
//
// Bounded (MaxPending, CLARIZEN_WEBHOOK_MAX_PENDING): a flood of DISTINCT valid
// signed events would otherwise grow the pending map without limit. At the cap
// a new distinct entity evicts the OLDEST pending entity (drop-oldest
// back-pressure), so memory is bounded by the cap regardless of input rate.
// The evicted entity is not lost forever — the polling cursor reconciles it on
// the next crawl. Re-offering an already-pending entity never grows the map.
//
// Pure and clock-injectable — the processor supplies real time in production,
// tests supply a virtual clock. Thread-safe: the receiver offers from HTTP
// threads while the processor drains from its worker.

namespace ClarizenConnector.Webhook;

public sealed class EventDebouncer
{
    /// <summary>Default cap on distinct pending entities (CLARIZEN_WEBHOOK_MAX_PENDING).</summary>
    public const int DefaultMaxPending = 100_000;

    private static readonly Infrastructure.IAppLogger Logger =
        Infrastructure.Logging.GetLogger("clarizen_connector.webhook");

    private readonly object _lock = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    // seq → key, ordered by insertion so the smallest seq is the oldest entity.
    private readonly SortedDictionary<long, string> _order = new();
    private readonly TimeSpan _window;
    private readonly int _maxPending;
    private long _seq;
    private long _dropped;

    private readonly record struct Pending(WebhookEvent Event, DateTime LastSeenUtc, long Seq);

    public EventDebouncer(TimeSpan window, int maxPending = DefaultMaxPending)
    {
        _window = window;
        _maxPending = Math.Max(1, maxPending);
    }

    /// <summary>Number of entities currently waiting out their window.</summary>
    public int PendingCount
    {
        get
        {
            lock (_lock)
                return _pending.Count;
        }
    }

    /// <summary>Configured cap on distinct pending entities.</summary>
    public int MaxPending => _maxPending;

    /// <summary>Total entities dropped by back-pressure since construction.</summary>
    public long Dropped
    {
        get
        {
            lock (_lock)
                return _dropped;
        }
    }

    /// <summary>
    /// Record an event: (re)starts the entity's debounce window and overwrites
    /// its pending change kind (last-writer-wins). Returns true when this was a
    /// coalesced duplicate of an already-pending entity. When the map is at the
    /// MaxPending cap, a new distinct entity evicts the oldest pending one.
    /// </summary>
    public bool Offer(WebhookEvent evt, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (_pending.TryGetValue(evt.Key, out var existing))
            {
                // Coalesce: keep the entity's original insertion order, refresh
                // the event + last-seen so its window restarts. No growth.
                _pending[evt.Key] = existing with { Event = evt, LastSeenUtc = nowUtc };
                return true;
            }

            // New distinct entity. Enforce the cap with drop-oldest back-pressure.
            if (_pending.Count >= _maxPending)
                EvictOldest();

            var seq = _seq++;
            _pending[evt.Key] = new Pending(evt, nowUtc, seq);
            _order[seq] = evt.Key;
            return false;
        }
    }

    /// <summary>Evict the oldest pending entity (smallest seq). Caller holds the lock.</summary>
    private void EvictOldest()
    {
        if (_order.Count == 0)
            return;
        var oldest = _order.First();
        _order.Remove(oldest.Key);
        _pending.Remove(oldest.Value);
        _dropped++;
        Logger.Warning(
            $"Webhook debouncer at capacity ({_maxPending}); dropped oldest pending entity "
            + $"'{oldest.Value}' (total dropped {_dropped}). The polling cursor will reconcile it.");
    }

    /// <summary>
    /// Return (and remove) every entity whose window elapsed at or before
    /// <paramref name="nowUtc"/> — i.e. no newer event within the window.
    /// </summary>
    public List<WebhookEvent> DrainReady(DateTime nowUtc)
    {
        lock (_lock)
        {
            var ready = new List<Pending>();
            foreach (var pending in _pending.Values)
            {
                if (nowUtc - pending.LastSeenUtc >= _window)
                    ready.Add(pending);
            }
            foreach (var pending in ready)
            {
                _pending.Remove(pending.Event.Key);
                _order.Remove(pending.Seq);
            }
            return ready.Select(p => p.Event).ToList();
        }
    }

    /// <summary>Flush everything immediately regardless of window (shutdown drain).</summary>
    public List<WebhookEvent> DrainAll()
    {
        lock (_lock)
        {
            var all = _pending.Values.Select(p => p.Event).ToList();
            _pending.Clear();
            _order.Clear();
            return all;
        }
    }
}
