// Infrastructure/HealthEndpoint.cs
// --------------------------------
// Liveness / readiness / metrics HTTP endpoint. Gated on HEALTH_PORT:
// HEALTH_PORT<=0 → StartIfConfigured returns null and nothing is served.
// Otherwise an HttpListener runs on a background thread serving:
//
//   GET /health   → 200 "OK"            (liveness — the process is up)
//   GET /ready    → 200 "READY"         (readiness — config loaded)
//   GET /metrics  → 200 Prometheus text (Metrics.RenderPrometheus + live
//                                        dead-letter depth)
//
// Everything else → 404. The listener prefers the wildcard bind
// (http://+:{port}/) and falls back to http://localhost:{port}/ when the
// wildcard bind is denied (no admin URL ACL). Disposing the returned handle
// stops the listener cleanly. This type must NEVER throw into the caller.

using System.Globalization;
using System.Net;
using System.Text;

namespace ClarizenConnector.Infrastructure;

public sealed class HealthEndpoint : IDisposable
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector");

    /// <summary>Env var holding the port to serve on. <c>&lt;=0</c> disables the endpoint.</summary>
    public const string PortEnvVar = "HEALTH_PORT";

    private readonly HttpListener _listener;
    private readonly Func<int>? _deadLetterDepth;
    private readonly Thread _thread;
    private volatile bool _stopped;

    private HealthEndpoint(HttpListener listener, Func<int>? deadLetterDepth)
    {
        _listener = listener;
        _deadLetterDepth = deadLetterDepth;
        _thread = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "health-endpoint",
        };
        _thread.Start();
    }

    /// <summary>
    /// Start the endpoint if <c>HEALTH_PORT&gt;0</c>; otherwise return
    /// <c>null</c>. Any failure to bind is logged and yields <c>null</c>
    /// (the caller keeps running without an endpoint). Never throws.
    /// </summary>
    /// <param name="deadLetterDepth">Optional callback refreshing the live
    /// dead-letter depth for the /metrics route.</param>
    public static IDisposable? StartIfConfigured(Func<int>? deadLetterDepth = null)
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

            var listener = TryStartListener(port);
            if (listener == null)
                return null;  // could not bind — already logged

            Logger.Info($"Health endpoint listening on port {port} (/health, /ready, /metrics)");
            return new HealthEndpoint(listener, deadLetterDepth);
        }
        catch (Exception exc)
        {
            Logger.Error($"Health endpoint failed to start: {exc.Message}");
            return null;
        }
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
                        $"Health endpoint: wildcard bind denied, fell back to {prefix} "
                        + "(only reachable from localhost). Reserve a URL ACL to bind all interfaces.");
                }
                return listener;
            }
            catch (Exception exc)
            {
                ((IDisposable)listener).Dispose();
                Logger.Warning($"Health endpoint: could not bind {prefix}: {exc.Message}");
            }
        }
        Logger.Error($"Health endpoint: could not bind any prefix on port {port}; endpoint disabled.");
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
                Logger.Warning($"Health endpoint: accept error: {exc.Message}");
                // Don't spin hot on a persistently broken listener.
                Thread.Sleep(1000);
                continue;
            }

            try
            {
                HandleRequest(context);
            }
            catch (Exception exc)
            {
                Logger.Warning($"Health endpoint: request handling error: {exc.Message}");
                TryAbort(context);
            }
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        switch (path)
        {
            case "/health":
                // Liveness — the process is up. Stays 200 even when degraded
                // so an orchestrator does not KILL a connector that is merely
                // waiting out a dependency outage.
                Write(context, 200, "OK", "text/plain; charset=utf-8");
                break;
            case "/ready":
                // Readiness — flips to 503 (not ready) while a critical
                // dependency's breaker is open (degraded mode), so traffic /
                // scale-in decisions see the connector as temporarily out.
                if (Breakers.IsAnyCriticalOpen)
                {
                    Write(context, 503,
                        $"DEGRADED: circuit open for {string.Join(", ", Breakers.OpenCritical())}",
                        "text/plain; charset=utf-8");
                }
                else
                {
                    Write(context, 200, "READY", "text/plain; charset=utf-8");
                }
                break;
            case "/metrics":
                Write(context, 200, RenderMetrics(), "text/plain; version=0.0.4; charset=utf-8");
                break;
            default:
                Write(context, 404, "Not Found", "text/plain; charset=utf-8");
                break;
        }
    }

    private string RenderMetrics()
    {
        try
        {
            if (_deadLetterDepth is not null)
                Metrics.SetDeadLetterDepth(_deadLetterDepth());
        }
        catch (Exception exc)
        {
            Logger.Warning($"Health endpoint: could not read dead-letter depth: {exc.Message}");
        }
        return Metrics.RenderPrometheus();
    }

    private static void Write(HttpListenerContext context, int status, string body, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var response = context.Response;
        response.StatusCode = status;
        response.ContentType = contentType;
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

    /// <summary>Stop the listener and join the background thread. Never throws.</summary>
    public void Dispose()
    {
        if (_stopped)
            return;
        _stopped = true;
        try { _listener.Stop(); } catch { /* ignore */ }
        try { _listener.Close(); } catch { /* ignore */ }
        try { _thread.Join(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        Logger.Info("Health endpoint stopped");
    }
}
