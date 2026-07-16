// Webhook/WebhookReceiver.cs
// --------------------------
// HTTP receiver for Clarizen change notifications (event-driven incremental).
// Gated on CLARIZEN_WEBHOOK_PORT (<=0 → disabled) and, crucially, on
// CLARIZEN_WEBHOOK_SECRET: without a secret the receiver refuses to start
// (fail-closed) so an unauthenticated endpoint is never exposed. The polling
// cursor remains the default and the fallback whenever the receiver is off or
// down — see docs/WEBHOOKS.md.
//
// Request handling (POST {path}, default /webhook):
//   1. read the raw body (size-capped);
//   2. validate the HMAC signature over those exact bytes BEFORE parsing —
//      invalid/missing → 401, nothing parsed or enqueued;
//   3. parse events (malformed / no events → 400);
//   4. enqueue each event into the processor (debounced/coalesced) → 202.
// Non-POST → 405, other paths → 404.
//
// Structurally mirrors HealthEndpoint (background HttpListener thread, wildcard
// bind with localhost fallback, never throws into the caller). Metrics:
// webhook_events_received/accepted/rejected and the webhook_receiver_up gauge.

using System.Globalization;
using System.Net;
using System.Text;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Webhook;

public sealed class WebhookReceiver : IDisposable
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector.webhook");

    public const string PortEnvVar = "CLARIZEN_WEBHOOK_PORT";
    public const string SecretEnvVar = "CLARIZEN_WEBHOOK_SECRET";
    public const string PathEnvVar = "CLARIZEN_WEBHOOK_PATH";
    public const string HeaderEnvVar = "CLARIZEN_WEBHOOK_SIGNATURE_HEADER";

    /// <summary>Body size cap (bytes) — a webhook notification is small; reject floods.</summary>
    internal const int MaxBodyBytes = 1 * 1024 * 1024;

    private readonly HttpListener _listener;
    private readonly SignatureValidator _validator;
    private readonly WebhookProcessor _processor;
    private readonly string _path;
    private readonly string _signatureHeader;
    private readonly Thread _thread;
    private volatile bool _stopped;

    private WebhookReceiver(
        HttpListener listener,
        SignatureValidator validator,
        WebhookProcessor processor,
        string path,
        string signatureHeader)
    {
        _listener = listener;
        _validator = validator;
        _processor = processor;
        _path = path;
        _signatureHeader = signatureHeader;
        _thread = new Thread(ListenLoop) { IsBackground = true, Name = "webhook-receiver" };
        _thread.Start();
        Metrics.SetWebhookReceiverUp(true);
    }

    /// <summary>
    /// Start the receiver when CLARIZEN_WEBHOOK_PORT&gt;0 AND
    /// CLARIZEN_WEBHOOK_SECRET is set; otherwise return null (disabled or
    /// fail-closed on a missing secret). Never throws.
    /// </summary>
    public static WebhookReceiver? StartIfConfigured(WebhookProcessor processor)
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable(PortEnvVar);
            if (string.IsNullOrWhiteSpace(raw)
                || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                || port <= 0)
            {
                return null;  // disabled
            }

            var secret = SecretProvider.GetSecret(SecretEnvVar);
            if (string.IsNullOrWhiteSpace(secret))
            {
                Logger.Error(
                    $"{PortEnvVar} is set but {SecretEnvVar} is missing — refusing to start an "
                    + "unauthenticated webhook receiver. Falling back to polling only.");
                return null;
            }

            var path = NormalizePath(Environment.GetEnvironmentVariable(PathEnvVar));
            var header = EnvFlags.GetString(HeaderEnvVar, SignatureValidator.DefaultHeader);

            var listener = TryStartListener(port);
            if (listener is null)
                return null;

            Logger.Info($"Webhook receiver listening on port {port} (POST {path}).");
            return new WebhookReceiver(listener, new SignatureValidator(secret!), processor, path, header);
        }
        catch (Exception exc)
        {
            Logger.Error($"Webhook receiver failed to start: {exc.Message}");
            return null;
        }
    }

    internal static string NormalizePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "/webhook";
        var path = raw.Trim();
        return path.StartsWith('/') ? path : "/" + path;
    }

    private static HttpListener? TryStartListener(int port)
    {
        foreach (var prefix in new[] { $"http://+:{port}/", $"http://localhost:{port}/" })
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                if (prefix.StartsWith("http://localhost", StringComparison.Ordinal))
                {
                    Logger.Warning(
                        $"Webhook receiver: wildcard bind denied, fell back to {prefix} "
                        + "(only reachable from localhost). Reserve a URL ACL to bind all interfaces.");
                }
                return listener;
            }
            catch (Exception exc)
            {
                ((IDisposable)listener).Dispose();
                Logger.Warning($"Webhook receiver: could not bind {prefix}: {exc.Message}");
            }
        }
        Logger.Error($"Webhook receiver: could not bind any prefix on port {port}; receiver disabled.");
        return null;
    }

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
                Logger.Warning($"Webhook receiver: request handling error: {exc.Message}");
                TryAbort(context);
            }
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var path = request.Url?.AbsolutePath ?? "/";
        if (!string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
        {
            Write(context, 404, "Not Found");
            return;
        }
        if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            Write(context, 405, "Method Not Allowed");
            return;
        }

        Metrics.IncWebhookEventsReceived();

        var (body, tooLarge) = ReadBody(request);
        if (tooLarge)
        {
            Metrics.IncWebhookEventsRejected();
            Logger.Warning("Webhook: rejected an oversize body.");
            Write(context, 413, "Payload Too Large");
            return;
        }

        // SECURITY BOUNDARY: validate the signature over the raw bytes BEFORE
        // parsing or enqueuing anything.
        var signature = request.Headers[_signatureHeader];
        if (!_validator.IsValid(body, signature))
        {
            Metrics.IncWebhookEventsRejected();
            Logger.Warning(
                $"Webhook: rejected a post with an invalid or missing '{_signatureHeader}' signature.");
            Write(context, 401, "Invalid signature");
            return;
        }

        var events = WebhookEventParser.Parse(Encoding.UTF8.GetString(body));
        if (events.Count == 0)
        {
            Metrics.IncWebhookEventsRejected();
            Logger.Warning("Webhook: rejected a malformed or empty payload (no recognisable events).");
            Write(context, 400, "No recognisable events");
            return;
        }

        foreach (var evt in events)
        {
            _processor.Enqueue(evt);
            Metrics.IncWebhookEventsAccepted();
        }
        Logger.Info($"Webhook: accepted {events.Count} event(s).");
        Write(context, 202, "Accepted");
    }

    /// <summary>Read the request body up to the cap; returns tooLarge=true past it.</summary>
    private static (byte[] Body, bool TooLarge) ReadBody(HttpListenerRequest request)
    {
        if (request.ContentLength64 > MaxBodyBytes)
            return (Array.Empty<byte>(), true);

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = request.InputStream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
                return (Array.Empty<byte>(), true);
            buffer.Write(chunk, 0, read);
        }
        return (buffer.ToArray(), false);
    }

    private static void Write(HttpListenerContext context, int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var response = context.Response;
        response.StatusCode = status;
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength64 = bytes.LongLength;
        using var output = response.OutputStream;
        output.Write(bytes, 0, bytes.Length);
    }

    private static void TryAbort(HttpListenerContext context)
    {
        try
        {
            context.Response.Abort();
        }
        catch
        {
            // best effort
        }
    }

    public void Dispose()
    {
        if (_stopped)
            return;
        _stopped = true;
        Metrics.SetWebhookReceiverUp(false);
        try { _listener.Stop(); } catch { /* ignore */ }
        try { _listener.Close(); } catch { /* ignore */ }
        try { _thread.Join(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        Logger.Info("Webhook receiver stopped.");
    }
}
