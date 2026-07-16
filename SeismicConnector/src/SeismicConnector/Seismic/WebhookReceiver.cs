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

    private readonly HttpListener _listener;
    private readonly SignatureValidator _validator;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<ContentEvent> _events = new();
    private volatile bool _stopped;

    private WebhookReceiver(HttpListener listener, SignatureValidator validator)
    {
        _listener = listener;
        _validator = validator;
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

    /// <summary>Test/DI seam: start with an explicit secret.</summary>
    internal static WebhookReceiver? StartIfConfigured(int port, string? secret)
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
        foreach (var prefix in new[] { $"http://+:{port}/", $"http://localhost:{port}/" })
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                Logger.Info($"Seismic webhook receiver listening on {prefix}webhook (HMAC-authenticated)");
                return new WebhookReceiver(listener, validator);
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

        // SECURITY BOUNDARY: validate the signature over the raw body before the
        // payload is decoded, parsed, or enqueued. Invalid/missing → 401, nothing acted on.
        var signature = request.Headers[SignatureValidator.DefaultHeader];
        if (!_validator.IsValid(raw, signature))
        {
            Logger.Warning("Webhook receiver: rejected request with invalid/missing signature.");
            Respond(context, 401, "Invalid signature");
            return;
        }

        var body = (request.ContentEncoding ?? Encoding.UTF8).GetString(raw);
        var events = ParseBody(body);
        foreach (var evt in events)
            _events.Enqueue(evt);
        Logger.Info($"Webhook receiver: queued {events.Count} event(s)");
        Respond(context, 202, "Accepted");
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
