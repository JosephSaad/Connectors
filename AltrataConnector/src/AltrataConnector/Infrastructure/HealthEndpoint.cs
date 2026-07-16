// Infrastructure/HealthEndpoint.cs
// --------------------------------
// Liveness / readiness / metrics HTTP endpoint. Gated on HEALTH_PORT:
// HEALTH_PORT<=0 → StartIfConfigured returns null and nothing is served.
//
//   GET /health   → 200 "OK"            (liveness — the process is up)
//   GET /ready    → 200 "READY"         (readiness — config loaded)
//   GET /metrics  → 200 Prometheus text (Metrics.RenderPrometheus + live
//                                        dead-letter depth from the state store)
//
// Prefers the wildcard bind (http://+:{port}/), falls back to localhost when
// the wildcard bind is denied (no admin URL ACL). Never throws into the caller.

using System.Globalization;
using System.Net;
using System.Text;
using AltrataConnector.State;

namespace AltrataConnector.Infrastructure;

public sealed class HealthEndpoint : IDisposable
{
    private static readonly IAppLogger Logger = Logging.GetLogger("altrata_connector");

    public const string PortEnvVar = "HEALTH_PORT";

    private readonly HttpListener _listener;
    private readonly Func<int> _deadLetterDepth;
    private readonly Func<long> _billableLookups;
    private readonly Func<IReadOnlyList<BreakerSnapshot>> _breakers;
    private readonly Thread _thread;
    private volatile bool _stopped;

    private HealthEndpoint(HttpListener listener, Func<int> deadLetterDepth, Func<long> billableLookups,
        Func<IReadOnlyList<BreakerSnapshot>> breakers)
    {
        _listener = listener;
        _deadLetterDepth = deadLetterDepth;
        _billableLookups = billableLookups;
        _breakers = breakers;
        _thread = new Thread(ListenLoop) { IsBackground = true, Name = "health-endpoint" };
        _thread.Start();
    }

    /// <summary>Single-store convenience overload.</summary>
    public static IDisposable? StartIfConfigured(IStateStore state) =>
        StartIfConfigured(() => state.ReadDeadLetters().Count, state.GetBillableLookupCount,
            () => Array.Empty<BreakerSnapshot>());

    /// <summary>
    /// Provider-based overload: under connection sharding each shard
    /// dead-letters against its own connection id, so the caller supplies a
    /// depth provider that sums across shard stores. The breaker provider drives
    /// the /ready readiness gate (not-ready when a critical breaker is Open) and
    /// the per-dependency breaker metric lines.
    /// </summary>
    public static IDisposable? StartIfConfigured(Func<int> deadLetterDepth, Func<long> billableLookups,
        Func<IReadOnlyList<BreakerSnapshot>>? breakers = null)
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
                return null;

            Logger.Info($"Health endpoint listening on port {port} (/health, /ready, /metrics)");
            return new HealthEndpoint(listener, deadLetterDepth, billableLookups,
                breakers ?? (() => Array.Empty<BreakerSnapshot>()));
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
                    Logger.Warning($"Health endpoint: wildcard bind denied, fell back to {prefix} " +
                                   "(only reachable from localhost). Reserve a URL ACL to bind all interfaces.");
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
                Thread.Sleep(1000);  // don't spin hot on a broken listener
                continue;
            }

            try
            {
                HandleRequest(context);
            }
            catch (Exception exc)
            {
                Logger.Warning($"Health endpoint: request handling error: {exc.Message}");
                try { context.Response.Abort(); } catch { /* best effort */ }
            }
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        switch (path)
        {
            case "/health":
                // Liveness stays UP even in degraded mode — the process is fine.
                Write(context, 200, "OK", "text/plain; charset=utf-8");
                break;
            case "/ready":
                // Readiness reflects degraded mode: not-ready (503) when a
                // CRITICAL dependency breaker is Open.
                var openCritical = SafeBreakers()
                    .Where(b => b.Critical && b.State == CircuitState.Open)
                    .Select(b => b.Name)
                    .ToList();
                if (openCritical.Count > 0)
                    Write(context, 503,
                        $"NOT READY (degraded: {string.Join(",", openCritical)} breaker open)",
                        "text/plain; charset=utf-8");
                else
                    Write(context, 200, "READY", "text/plain; charset=utf-8");
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
        var breakers = SafeBreakers();
        try
        {
            Metrics.SetDeadLetterDepth(_deadLetterDepth());
            Metrics.Set("altrata_api_billable_lookups_total", _billableLookups());
            Metrics.Set("altrata_tracing_enabled", Telemetry.TracingEnabled ? 1 : 0);
            Metrics.Set("altrata_breaker_open",
                breakers.Any(b => b.Critical && b.State == CircuitState.Open) ? 1 : 0);
        }
        catch (Exception exc)
        {
            Logger.Warning($"Health endpoint: could not refresh state gauges: {exc.Message}");
        }
        // Labeled info line carries the exporter target (a string can't be a
        // gauge value); the gauge above answers "on or off".
        var info = Telemetry.TracingEnabled
            ? $"altrata_tracing_info{{endpoint=\"{Escape(Telemetry.OtlpEndpoint!)}\"}} 1\n"
            : "altrata_tracing_info{endpoint=\"\"} 0\n";

        // Per-dependency breaker lines (state 0/1/2 + trip/reset counters).
        var sb = new StringBuilder();
        sb.Append("# HELP altrata_breaker_state Circuit breaker state (0 closed, 1 open, 2 half-open).\n");
        sb.Append("# TYPE altrata_breaker_state gauge\n");
        foreach (var b in breakers)
            sb.Append($"altrata_breaker_state{{dependency=\"{Escape(b.Name)}\"}} {(int)b.State}\n");
        sb.Append("# HELP altrata_breaker_trips_total Times each breaker has opened.\n");
        sb.Append("# TYPE altrata_breaker_trips_total counter\n");
        foreach (var b in breakers)
            sb.Append($"altrata_breaker_trips_total{{dependency=\"{Escape(b.Name)}\"}} {b.Trips}\n");
        sb.Append("# HELP altrata_breaker_resets_total Times each breaker has recovered (closed from half-open).\n");
        sb.Append("# TYPE altrata_breaker_resets_total counter\n");
        foreach (var b in breakers)
            sb.Append($"altrata_breaker_resets_total{{dependency=\"{Escape(b.Name)}\"}} {b.Resets}\n");

        return Metrics.RenderPrometheus()
               + "# HELP altrata_tracing_info OpenTelemetry OTLP export target (label only).\n"
               + "# TYPE altrata_tracing_info gauge\n"
               + info
               + sb
               + AltrataConnector.Altrata.ClassificationStats.RenderPrometheus();
    }

    private IReadOnlyList<BreakerSnapshot> SafeBreakers()
    {
        try { return _breakers(); }
        catch { return Array.Empty<BreakerSnapshot>(); }
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "");

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
