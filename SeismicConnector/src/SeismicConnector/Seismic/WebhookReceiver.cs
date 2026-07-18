// Seismic/WebhookReceiver.cs
// --------------------------
// Optional near-real-time update channel. When SEISMIC_WEBHOOK_PORT > 0 an
// HttpListener accepts Seismic content webhooks:
//
//   POST /webhook   body: {"type":"contentPublished","contentId":"...","teamsiteId":"..."}
//                   (single event or a JSON array of events)
//
// Events are queued; the continuous loop drains the queue between scheduled
// crawls and performs targeted single-item ingest / withdrawal. Polling on
// the modifiedAt cursor remains the default and the fallback — a missed
// webhook is always healed by the next incremental crawl.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using SeismicConnector.Infrastructure;

namespace SeismicConnector.Seismic;

public sealed class WebhookReceiver : IDisposable
{
    private static readonly IAppLogger Logger = Logging.GetLogger("seismic_connector.webhook");

    public const string PortEnvVar = "SEISMIC_WEBHOOK_PORT";

    /// <summary>Shared-secret env var. Without it the receiver refuses to start
    /// (fail-closed) so an unauthenticated endpoint is never exposed.</summary>
    public const string SecretEnvVar = "SEISMIC_WEBHOOK_SECRET";

    /// <summary>Hard cap on an accepted request body (defends against unbounded reads).</summary>
    internal const int MaxBodyBytes = 1 * 1024 * 1024;

    /// <summary>
    /// Cap on queued (undrained) events. A signed burst can enqueue faster than
    /// the ~15s drain cadence; without a bound the queue grows in memory until the
    /// next drain. Past this depth the OLDEST queued events are dropped (drop-
    /// oldest back-pressure) — dedup already happens at drain, and any dropped
    /// event is always healed by the next reconciling crawl (polling is the
    /// safety net), so shedding load here never loses data permanently.
    /// </summary>
    internal const int MaxQueuedEvents = 10_000;

    private readonly HttpListener _listener;
    private readonly WebhookAntiReplay _antiReplay;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<ContentEvent> _events = new();
    private volatile bool _stopped;

    private WebhookReceiver(HttpListener listener, WebhookAntiReplay antiReplay)
    {
        _listener = listener;
        _antiReplay = antiReplay;
        _thread = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "seismic-webhook",
        };
        _thread.Start();
    }

    /// <summary>Pending events count (dashboard surface).</summary>
    public int PendingCount => _events.Count;

    /// <summary>Start the receiver when SEISMIC_WEBHOOK_PORT&gt;0. Resolves the
    /// shared secret from SEISMIC_WEBHOOK_SECRET; returns null (fail-closed) when
    /// a port is set without a secret, so polling remains the safety net. Never throws.</summary>
    public static WebhookReceiver? StartIfConfigured(int port) =>
        StartIfConfigured(port, SecretProvider.GetSecret(SecretEnvVar));

    /// <summary>Test/DI seam: start with an explicit secret (anti-replay from env).</summary>
    internal static WebhookReceiver? StartIfConfigured(int port, string? secret) =>
        StartIfConfigured(port, secret, antiReplay: null);

    /// <summary>Test/DI seam: start with an explicit secret and (optionally) an
    /// injected anti-replay policy — lets tests drive a deterministic clock.</summary>
    internal static WebhookReceiver? StartIfConfigured(int port, string? secret, WebhookAntiReplay? antiReplay)
    {
        if (port <= 0)
            return null;
        if (string.IsNullOrWhiteSpace(secret))
        {
            Logger.Error(
                $"{PortEnvVar} is set but {SecretEnvVar} is missing — refusing to start an "
                + "unauthenticated webhook receiver; falling back to polling only.");
            return null;
        }
        var validator = new SignatureValidator(secret!);
        antiReplay ??= WebhookAntiReplay.FromEnv(validator);
        foreach (var prefix in new[] { $"http://+:{port}/", $"http://localhost:{port}/" })
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                var replayNote = antiReplay.RequireTimestamp
                    ? $"HMAC + anti-replay (signed timestamp required, {antiReplay.Window.TotalMinutes:F0}-min window)"
                    : "HMAC (anti-replay in MIGRATION mode — timestamp optional; senders without one have no replay protection)";
                Logger.Info($"Seismic webhook receiver listening on {prefix}webhook ({replayNote})");
                return new WebhookReceiver(listener, antiReplay);
            }
            catch (Exception exc)
            {
                ((IDisposable)listener).Dispose();
                Logger.Warning($"Webhook receiver: could not bind {prefix}: {exc.Message}");
            }
        }
        Logger.Error($"Webhook receiver: could not bind port {port}; falling back to polling only.");
        return null;
    }

    /// <summary>Drain all queued events (deduplicated by content id, last event wins).</summary>
    public IReadOnlyList<ContentEvent> DrainEvents()
    {
        var byId = new Dictionary<string, ContentEvent>(StringComparer.Ordinal);
        while (_events.TryDequeue(out var evt))
        {
            if (!string.IsNullOrEmpty(evt.ContentId))
                byId[evt.ContentId] = evt;
        }
        Metrics.SetWebhookQueueDepth(_events.Count);
        return byId.Values.ToList();
    }

    /// <summary>Parse a webhook body: a single event object or an array of events.</summary>
    internal static List<ContentEvent> ParseBody(string body)
    {
        var events = new List<ContentEvent>();
        if (string.IsNullOrWhiteSpace(body))
            return events;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    var evt = element.Deserialize<ContentEvent>(JsonOptions);
                    if (evt is not null)
                        events.Add(evt);
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var evt = doc.RootElement.Deserialize<ContentEvent>(JsonOptions);
                if (evt is not null)
                    events.Add(evt);
            }
        }
        catch (JsonException exc)
        {
            Logger.Warning($"Webhook receiver: invalid JSON body ignored: {exc.Message}");
        }
        return events;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private void ListenLoop()
    {
        while (!_stopped)
        {
            HttpListenerContext context;
            try
            {
                context = _listener.GetContext();
            }
            catch (Exception) when (_stopped)
            {
                // Dispose() stopped the listener; GetContext throwing here is the
                // normal shutdown handshake — nothing to log.
                return;
            }
            catch (Exception exc)
            {
                if (_stopped)
                    return;
                Logger.Warning($"Webhook receiver: accept error: {exc.Message}");
                Thread.Sleep(1000);
                continue;
            }

            try
            {
                HandleRequest(context);
            }
            catch (Exception exc)
            {
                Logger.Warning($"Webhook receiver: request error: {exc.Message}");
                try
                {
                    context.Response.Abort();
                }
                catch
                {
                    // The response is already broken/closed — aborting it is
                    // best-effort cleanup; there is nothing further to do.
                }
            }
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        var request = context.Request;
        if (request.HttpMethod != "POST" || request.Url?.AbsolutePath != "/webhook")
        {
            Respond(context, 404, "Not Found");
            return;
        }

        // Reject an over-large declared body before reading anything.
        if (request.ContentLength64 > MaxBodyBytes)
        {
            Respond(context, 413, "Payload Too Large");
            return;
        }

        // Read the EXACT raw bytes (capped) — the HMAC is computed over these,
        // before any decode/parse.
        if (!TryReadCappedBody(request, out var raw))
        {
            Respond(context, 413, "Payload Too Large");
            return;
        }

        // SECURITY BOUNDARY: validate the signature AND anti-replay posture over
        // the raw body before the payload is decoded, parsed, or enqueued. Any
        // non-accepted verdict → rejected, nothing acted on.
        var signature = request.Headers[SignatureValidator.DefaultHeader];
        var timestamp = request.Headers[_antiReplay.TimestampHeader];
        var verdict = _antiReplay.Check(raw, timestamp, signature);
        if (verdict != WebhookVerdict.Accepted)
        {
            // Name the peer so repeated bad sources (misconfigured sender vs.
            // probing/replay) are attributable from the log alone. The signature
            // and timestamp VALUES are never logged.
            Metrics.IncWebhookRejected();
            var (status, reason) = verdict switch
            {
                WebhookVerdict.ReplayDetected => (409, "Replay detected"),
                WebhookVerdict.StaleTimestamp => (401, "Stale or unparseable timestamp"),
                WebhookVerdict.MissingTimestamp => (401, "Missing required timestamp"),
                _ => (401, "Invalid signature"),
            };
            Logger.Warning(
                $"Webhook receiver: rejected request ({reason.ToLowerInvariant()}) "
                + $"(remote {request.RemoteEndPoint?.ToString() ?? "unknown"}, "
                + $"{raw.Length} byte body).");
            Respond(context, status, reason);
            return;
        }

        var body = (request.ContentEncoding ?? Encoding.UTF8).GetString(raw);
        var events = ParseBody(body);
        var dropped = 0;
        foreach (var evt in events)
            dropped += Enqueue(evt);
        if (dropped > 0)
        {
            Metrics.IncWebhookDropped(dropped);
            Logger.Warning(
                $"Webhook receiver: queue at capacity ({MaxQueuedEvents}); dropped {dropped} oldest "
                + "event(s) under load — the next crawl reconciles them.");
        }
        Metrics.IncWebhookAccepted();
        Metrics.SetWebhookQueueDepth(_events.Count);
        Logger.Info($"Webhook receiver: queued {events.Count} event(s)");
        Respond(context, 202, "Accepted");
    }

    /// <summary>Enqueue one event, bounding the queue to <see cref="MaxQueuedEvents"/>
    /// (drop-oldest). Returns how many events were shed.</summary>
    private int Enqueue(ContentEvent evt) => EnqueueCapped(_events, evt, MaxQueuedEvents);

    /// <summary>Append <paramref name="evt"/> then trim the queue to <paramref name="cap"/> by
    /// dropping the oldest entries. Returns the number dropped. (Static so the cap is unit-testable
    /// without binding an HttpListener.)</summary>
    internal static int EnqueueCapped(ConcurrentQueue<ContentEvent> queue, ContentEvent evt, int cap)
    {
        queue.Enqueue(evt);
        var dropped = 0;
        while (queue.Count > cap && queue.TryDequeue(out _))
            dropped++;
        return dropped;
    }

    /// <summary>Read the request body into memory, aborting past <see cref="MaxBodyBytes"/>
    /// (guards against an unbounded/chunked body with no Content-Length).</summary>
    private static bool TryReadCappedBody(HttpListenerRequest request, out byte[] body)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = request.InputStream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                body = Array.Empty<byte>();
                return false;
            }
            buffer.Write(chunk, 0, read);
        }
        body = buffer.ToArray();
        return true;
    }

    private static void Respond(HttpListenerContext context, int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength64 = bytes.LongLength;
        using var output = context.Response.OutputStream;
        output.Write(bytes, 0, bytes.Length);
    }

    public void Dispose()
    {
        if (_stopped)
            return;
        _stopped = true;
        // Shutdown is best-effort by design: Stop/Close on an already-broken
        // listener and Join on a wedged thread may throw, and there is no
        // useful recovery during dispose — the process is tearing this down.
        try
        {
            _listener.Stop();
        }
        catch
        {
        }
        try
        {
            _listener.Close();
        }
        catch
        {
        }
        try
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
    }
}
