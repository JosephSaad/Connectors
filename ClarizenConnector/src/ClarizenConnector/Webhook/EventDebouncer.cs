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
// Pure and clock-injectable — the processor supplies real time in production,
// tests supply a virtual clock. Thread-safe: the receiver offers from HTTP
// threads while the processor drains from its worker.

namespace ClarizenConnector.Webhook;

public sealed class EventDebouncer
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly TimeSpan _window;

    private readonly record struct Pending(WebhookEvent Event, DateTime LastSeenUtc);

    public EventDebouncer(TimeSpan window) => _window = window;

    /// <summary>Number of entities currently waiting out their window.</summary>
    public int PendingCount
    {
        get
        {
            lock (_lock)
                return _pending.Count;
        }
    }

    /// <summary>
    /// Record an event: (re)starts the entity's debounce window and overwrites
    /// its pending change kind (last-writer-wins). Returns true when this was a
    /// coalesced duplicate of an already-pending entity.
    /// </summary>
    public bool Offer(WebhookEvent evt, DateTime nowUtc)
    {
        lock (_lock)
        {
            var coalesced = _pending.ContainsKey(evt.Key);
            _pending[evt.Key] = new Pending(evt, nowUtc);
            return coalesced;
        }
    }

    /// <summary>
    /// Return (and remove) every entity whose window elapsed at or before
    /// <paramref name="nowUtc"/> — i.e. no newer event within the window.
    /// </summary>
    public List<WebhookEvent> DrainReady(DateTime nowUtc)
    {
        lock (_lock)
        {
            var ready = new List<WebhookEvent>();
            foreach (var (key, pending) in _pending)
            {
                if (nowUtc - pending.LastSeenUtc >= _window)
                    ready.Add(pending.Event);
            }
            foreach (var evt in ready)
                _pending.Remove(evt.Key);
            return ready;
        }
    }

    /// <summary>Flush everything immediately regardless of window (shutdown drain).</summary>
    public List<WebhookEvent> DrainAll()
    {
        lock (_lock)
        {
            var all = _pending.Values.Select(p => p.Event).ToList();
            _pending.Clear();
            return all;
        }
    }
}
