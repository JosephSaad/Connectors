// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Infrastructure/HealthEndpoint.cs
// --------------------------------
// Liveness / readiness / metrics HTTP endpoint for the observability surface
// (#9). Gated on HEALTH_PORT: HEALTH_PORT<=0 → StartIfConfigured returns null
// and nothing is served. Otherwise an HttpListener runs on a background thread
// serving:
//
//   GET /health   → 200 "OK"           (liveness — the process is up)
//   GET /ready    → 200 "READY"        (readiness — config loaded)
//   GET /metrics  → 200 Prometheus text (Metrics.RenderPrometheus + live
//                                        dead-letter depth from SyncState)
//
// Everything else → 404. The listener prefers the wildcard bind
// (http://+:{port}/) and falls back to http://localhost:{port}/ when the
// wildcard bind is denied (no admin URL ACL). Disposing the returned handle
// stops the listener cleanly. This type must NEVER throw into the caller.

using System.Globalization;
using System.Net;
using System.Text;
using SalesforceCopilotConnector.Config;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Infrastructure;

/// <summary>
/// Background HTTP endpoint exposing liveness, readiness and Prometheus metrics.
/// Off by default (<c>HEALTH_PORT&lt;=0</c>); never throws into the caller.
/// </summary>
public sealed class HealthEndpoint : IDisposable
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector");

    /// <summary>Env var holding the port to serve on. <c>&lt;=0</c> disables the endpoint.</summary>
    public const string PortEnvVar = "HEALTH_PORT";

    private readonly HttpListener _listener;
    private readonly AppConfig _config;
    private readonly Thread _thread;
    private volatile bool _stopped;

    private HealthEndpoint(HttpListener listener, AppConfig config)
    {
        _listener = listener;
        _config = config;
        _thread = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "health-endpoint",
        };
        _thread.Start();
    }

    /// <summary>
    /// Start the endpoint if <c>HEALTH_PORT&gt;0</c>; otherwise return <c>null</c>.
    /// Any failure to bind is logged and yields <c>null</c> (the caller keeps
    /// running without an endpoint). Never throws.
    /// </summary>
    public static IDisposable? StartIfConfigured(AppConfig config)
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
            {
                return null;  // could not bind — already logged
            }

            Logger.Info($"Health endpoint listening on port {port} (/health, /ready, /metrics)");
            return new HealthEndpoint(listener, config);
        }
        catch (Exception exc)
        {
            // Must never throw into the caller.
            Logger.Error($"Health endpoint failed to start: {exc.Message}", exc);
            return null;
        }
    }

    /// <summary>
    /// Open an <see cref="HttpListener"/> on <paramref name="port"/>, preferring
    /// the wildcard prefix and falling back to localhost when the wildcard bind
    /// is denied. Returns <c>null</c> if neither prefix can be bound.
    /// </summary>
    private static HttpListener? TryStartListener(int port)
    {
        foreach (var prefix in new[]
                 {
                     $"http://+:{port}/",
                     $"http://localhost:{port}/",
                 })
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
                context = _listener.GetContext();  // blocks until a request or listener stop
            }
            catch (Exception) when (_stopped)
            {
                return;  // listener stopped during Dispose — expected
            }
            catch (Exception exc)
            {
                if (_stopped)
                {
                    return;
                }
                Logger.Warning($"Health endpoint: accept error: {exc.Message}");
                // A persistently broken listener (fd exhaustion, dead socket) must not
                // spin this thread hot and flood the rotating log — pace the retries.
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
                Write(context, 200, "OK", "text/plain; charset=utf-8");
                break;
            case "/ready":
                // Readiness = configuration loaded. StartIfConfigured only runs
                // with a non-null AppConfig, so if we are serving we are ready.
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

    /// <summary>
    /// Render the metrics exposition, refreshing the live dead-letter depth from
    /// <see cref="SyncState.ReadFailedRecords"/> first. The read is guarded — a
    /// state-store failure must not break the endpoint; the last known gauge
    /// value is served instead.
    /// </summary>
    private string RenderMetrics()
    {
        try
        {
            // Under connection sharding each shard dead-letters against its own
            // connector id, so the depth is the sum across shards; otherwise the
            // base connector id's queue is the whole story.
            int depth;
            if (Salesforce.ShardingConfig.IsEnabled
                && Salesforce.ShardingConfig.TryLoad(_config, out var shards, out _))
            {
                depth = shards.Sum(s => SyncState.ReadFailedRecords(s.ConnectionId).Count);
            }
            else
            {
                depth = SyncState.ReadFailedRecords(_config.Connector.Id).Count;
            }
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
            // best effort
        }
    }

    /// <summary>Stop the listener and join the background thread. Never throws.</summary>
    public void Dispose()
    {
        if (_stopped)
        {
            return;
        }
        _stopped = true;
        try
        {
            _listener.Stop();
        }
        catch
        {
            // ignore
        }
        try
        {
            _listener.Close();
        }
        catch
        {
            // ignore
        }
        try
        {
            if (!_thread.Join(TimeSpan.FromSeconds(2)))
            {
                // Background thread; process teardown will reap it if it lingers.
            }
        }
        catch
        {
            // ignore
        }
        Logger.Info("Health endpoint stopped");
    }
}
