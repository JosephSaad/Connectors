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

    private readonly HttpListener _listener;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<ContentEvent> _events = new();
    private volatile bool _stopped;

    private WebhookReceiver(HttpListener listener)
    {
        _listener = listener;
        _thread = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "seismic-webhook",
        };
        _thread.Start();
    }

    /// <summary>Pending events count (dashboard surface).</summary>
    public int PendingCount => _events.Count;

    /// <summary>Start the receiver when SEISMIC_WEBHOOK_PORT&gt;0; otherwise null. Never throws.</summary>
    public static WebhookReceiver? StartIfConfigured(int port)
    {
        if (port <= 0)
            return null;
        foreach (var prefix in new[] { $"http://+:{port}/", $"http://localhost:{port}/" })
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                Logger.Info($"Seismic webhook receiver listening on {prefix}webhook");
                return new WebhookReceiver(listener);
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

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var body = reader.ReadToEnd();
        var events = ParseBody(body);
        foreach (var evt in events)
            _events.Enqueue(evt);
        Logger.Info($"Webhook receiver: queued {events.Count} event(s)");
        Respond(context, 202, "Accepted");
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
