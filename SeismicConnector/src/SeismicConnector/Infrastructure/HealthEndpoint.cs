// Infrastructure/HealthEndpoint.cs
// --------------------------------
// Liveness / readiness / metrics HTTP endpoint (docs/OBSERVABILITY.md). Gated
// on HEALTH_PORT: <=0 → StartIfConfigured returns null and nothing is served.
// Otherwise an HttpListener runs on a background thread serving:
//
//   GET /health   → 200 "OK"            (liveness — the process is up)
//   GET /ready    → 200 "READY"         (readiness — config loaded)
//   GET /metrics  → 200 Prometheus text (Metrics.RenderPrometheus + live
//                                        dead-letter depth from SyncState)
//
// Everything else → 404. Wildcard bind preferred; falls back to localhost
// when the wildcard bind is denied (no admin URL ACL on Windows). This type
// must NEVER throw into the caller.

using System.Globalization;
using System.Net;
using System.Text;
using SeismicConnector.Config;
using SeismicConnector.Seismic;

namespace SeismicConnector.Infrastructure;

public sealed class HealthEndpoint : IDisposable
{
    private static readonly IAppLogger Logger = Logging.GetLogger("seismic_connector");

    public const string PortEnvVar = "HEALTH_PORT";

    private readonly HttpListener _listener;
    private readonly AppConfig? _config;
    private readonly string _connectorId;
    private readonly Thread _thread;
    private volatile bool _stopped;

    private HealthEndpoint(HttpListener listener, string connectorId, AppConfig? config)
    {
        _listener = listener;
        _connectorId = connectorId;
        _config = config;
        _thread = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "health-endpoint",
        };
        _thread.Start();
    }

    /// <summary>Start the endpoint if HEALTH_PORT&gt;0; otherwise null. Never throws.</summary>
    public static IDisposable? StartIfConfigured(string connectorId, AppConfig? config = null)
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable(PortEnvVar);
            if (string.IsNullOrWhiteSpace(raw)
                || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                || port <= 0)
            {
                return null;
            }

            var listener = TryStartListener(port);
            if (listener == null)
                return null;

            Logger.Info($"Health endpoint listening on port {port} (/health, /ready, /metrics)");
            return new HealthEndpoint(listener, connectorId, config);
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
                Thread.Sleep(1000);  // never spin hot on a broken listener
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
                // Liveness: the process is up. Stays 200 even in degraded mode —
                // a paused connector is alive and should NOT be restarted.
                Write(context, 200, "OK", "text/plain; charset=utf-8");
                break;
            case "/ready":
                // Readiness: not-ready (503) while a critical dependency breaker
                // is open, so a load balancer / orchestrator routes away until
                // the dependency recovers. Liveness above stays green.
                if (CircuitBreakerRegistry.AnyCriticalOpen)
                {
                    var open = string.Join(",", CircuitBreakerRegistry.OpenCriticalNames);
                    Write(context, 503, $"NOT READY (circuit open: {open})", "text/plain; charset=utf-8");
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
            // Under connection sharding each shard dead-letters against its own
            // connection id, so the live depth is the SUM across shards.
            int depth;
            if (_config is not null && ShardingConfig.TryLoad(_config, out var shards, out _))
                depth = shards.Sum(s => SyncState.ReadFailedRecords(s.ConnectionId).Count);
            else
                depth = SyncState.ReadFailedRecords(_connectorId).Count;
            Metrics.SetDeadLetterDepth(depth);
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
        }
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
        Logger.Info("Health endpoint stopped");
    }
}
