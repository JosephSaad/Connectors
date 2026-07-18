// Hdfs/WebHdfsClient.cs
// ---------------------
// WebHDFS REST client for the BDH Hadoop data mart (HDFS_MODE=webhdfs).
//
//   • Endpoint: HDFS_NAMENODE_URL, e.g. http://namenode:9870/webhdfs/v1 (a
//     Knox gateway URL works identically — Kerberos/SPNEGO itself is out of
//     scope; point the connector at Knox for kerberized clusters).
//   • Auth: simple auth via ?user.name=<HDFS_USER> and/or a delegation token
//     (?delegation=<token>, SECRET_HDFS_DELEGATION_TOKEN). Both optional.
//   • LISTSTATUS enumerates a directory; OPEN streams a file (the namenode's
//     307 redirect to a datanode is followed by HttpClient automatically).
//   • Retry ladder: 429/5xx with Retry-After honoured exactly, every wait
//     clamped to 60 s — same policy as the Graph client.
//   • Circuit breaker (Breakers.Hdfs): 5xx/transport failures count, 4xx and
//     honoured throttles are ignored, and the acquired slot is settled exactly
//     once on EVERY exit path (settle-once + try/finally) so a HalfOpen probe
//     can never leak and wedge the breaker.

using System.Net;
using System.Text.Json.Nodes;
using HadoopConnector.Config;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Hdfs;

public class WebHdfsClient : IBdhSource
{
    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector.webhdfs");

    private const int MaxRetries = 4;

    private readonly string _baseUrl;
    private readonly string _rootPath;
    private readonly string? _user;
    private readonly string? _delegationToken;
    private readonly HttpClient _http;
    private readonly CircuitBreaker _breaker;

    /// <summary>Async delay seam so tests never really sleep.</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } =
        (delay, ct) => Task.Delay(delay, ct);

    public WebHdfsClient(AppConfig config, HttpMessageHandler? handler = null, CircuitBreaker? breaker = null)
        : this(config.HdfsNamenodeUrl ?? string.Empty, config.BdhRootPath, config.HdfsUser,
               config.HdfsDelegationToken, handler, breaker)
    {
    }

    public WebHdfsClient(
        string namenodeUrl,
        string rootPath,
        string? user = null,
        string? delegationToken = null,
        HttpMessageHandler? handler = null,
        CircuitBreaker? breaker = null)
    {
        if (string.IsNullOrWhiteSpace(namenodeUrl))
            throw new ArgumentException("Invalid configuration: Missing HDFS_NAMENODE_URL");
        _baseUrl = namenodeUrl.TrimEnd('/');
        _rootPath = "/" + rootPath.Trim('/');
        _user = user;
        _delegationToken = delegationToken;
        _breaker = breaker ?? CircuitBreaker.Disabled;
        // No injected test handler → the shared enterprise transport policy
        // (PROXY_URL/PROXY_BYPASS, CA_BUNDLE_PATH — WebHDFS behind internal TLS
        // very often chains to a PRIVATE enterprise CA; see the env template).
        _http = handler is null
            ? new HttpClient(HttpTransport.CreateHandler()) { Timeout = TimeSpan.FromSeconds(120) }
            : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
    }

    public string Description => $"webhdfs {_baseUrl}{_rootPath}";

    /// <summary>Build the full request URI for an operation (testable, pure-ish).</summary>
    internal Uri BuildUri(string relativePath, string op)
    {
        var absolute = _rootPath;
        var trimmed = relativePath.Trim('/');
        if (trimmed.Length > 0)
            absolute += "/" + trimmed;
        var escapedPath = string.Join(
            "/", absolute.Split('/').Select(Uri.EscapeDataString));
        var query = $"?op={op}";
        if (!string.IsNullOrEmpty(_user))
            query += $"&user.name={Uri.EscapeDataString(_user)}";
        if (!string.IsNullOrEmpty(_delegationToken))
            query += $"&delegation={Uri.EscapeDataString(_delegationToken)}";
        return new Uri($"{_baseUrl}{escapedPath}{query}");
    }

    // ── LISTSTATUS ───────────────────────────────────────────────────────────

    public async Task<List<HdfsFileStatus>> ListAsync(string relativePath, CancellationToken ct = default)
    {
        var response = await SendAsync(BuildUri(relativePath, "LISTSTATUS"), ct).ConfigureAwait(false);
        if (response.CircuitOpen)
            throw new CircuitOpenException("hdfs");
        if (!response.IsSuccess)
        {
            throw new HdfsException(
                $"LISTSTATUS '{relativePath}' failed (HTTP {(int)response.StatusCode}): "
                + $"{RemoteExceptionMessage(response.Body) ?? response.RawBody}")
            { StatusCode = (int)response.StatusCode };
        }
        return ParseListStatus(response.Body);
    }

    /// <summary>Parse a FileStatuses envelope; malformed → HdfsException (never a silent empty list).</summary>
    internal static List<HdfsFileStatus> ParseListStatus(JsonNode? body)
    {
        if (body?["FileStatuses"]?["FileStatus"] is not JsonArray statuses)
            throw new HdfsException("Malformed LISTSTATUS response (no FileStatuses.FileStatus array).");
        var entries = new List<HdfsFileStatus>(statuses.Count);
        foreach (var entry in statuses)
        {
            if (entry is not JsonObject obj)
                continue;
            var suffix = obj["pathSuffix"]?.GetValue<string>() ?? string.Empty;
            var type = obj["type"]?.GetValue<string>() ?? "FILE";
            var length = obj["length"]?.GetValue<long>() ?? 0;
            var modTime = obj["modificationTime"]?.GetValue<long>() ?? 0;
            entries.Add(new HdfsFileStatus(
                suffix,
                string.Equals(type, "DIRECTORY", StringComparison.OrdinalIgnoreCase),
                length, modTime));
        }
        return entries;
    }

    public async Task<bool> ExistsAsync(string relativePath, CancellationToken ct = default)
    {
        var response = await SendAsync(BuildUri(relativePath, "GETFILESTATUS"), ct).ConfigureAwait(false);
        if (response.CircuitOpen)
            throw new CircuitOpenException("hdfs");
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        if (!response.IsSuccess)
        {
            throw new HdfsException(
                $"GETFILESTATUS '{relativePath}' failed (HTTP {(int)response.StatusCode}): "
                + $"{RemoteExceptionMessage(response.Body) ?? response.RawBody}")
            { StatusCode = (int)response.StatusCode };
        }
        return true;
    }

    /// <summary>Best-effort connectivity probe (validate-config).</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            return await ExistsAsync(string.Empty, ct).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            // Best-effort probe: swallow-and-report is the contract, but include
            // the exception TYPE — "connection refused" vs "invalid URI" point at
            // very different fixes (HDFS_NAMENODE_URL reachability vs its value).
            Logger.Warning(
                $"WebHDFS connectivity check failed for {_baseUrl}: "
                + $"{exc.GetType().Name}: {exc.Message}");
            return false;
        }
    }

    // ── OPEN ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// OPEN a file for streaming read. The namenode's redirect to a datanode is
    /// followed transparently. The returned stream is the raw response stream —
    /// callers bound consumption (BdhFileParser's BoundedStream).
    /// </summary>
    public async Task<Stream> OpenAsync(string relativePath, CancellationToken ct = default)
    {
        if (!_breaker.TryAcquire())
            throw new CircuitOpenException("hdfs");

        // Settle the acquired slot exactly once on every exit path.
        var settled = false;
        void Settle(Action classify)
        {
            if (settled)
                return;
            settled = true;
            classify();
        }

        try
        {
            var uri = BuildUri(relativePath, "OPEN");
            for (var attempt = 0; ; attempt++)
            {
                HttpResponseMessage response;
                try
                {
                    Metrics.IncHdfsCalls();
                    response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Settle(_breaker.OnIgnored);  // cancellation is flow control, not an outage
                    throw;
                }
                catch (HttpRequestException exc) when (attempt < MaxRetries)
                {
                    // Transport failure before any stream bytes — safe to retry.
                    var delay = Math.Min(60, 2 * Math.Pow(2, attempt));
                    Logger.Warning(
                        $"WebHDFS OPEN '{relativePath}' failed ({exc.Message}); "
                        + $"retry {attempt + 1}/{MaxRetries} in {delay:0.##}s");
                    await DelayAsync(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
                    continue;
                }
                catch (Exception exc)
                {
                    Settle(_breaker.OnFailure);
                    throw new HdfsException($"OPEN '{relativePath}' failed: {exc.Message}", exc);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    // Same 429/5xx + Retry-After ladder as SendAsync. This is safe
                    // for OPEN because it runs BEFORE any stream bytes are consumed —
                    // a retried request re-fetches the file from the start.
                    if ((status == 429 || status >= 500) && attempt < MaxRetries)
                    {
                        var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds
                            ?? (response.Headers.RetryAfter?.Date is { } date
                                ? Math.Max(0, (date - DateTimeOffset.UtcNow).TotalSeconds)
                                : (double?)null);
                        // Retry-After honoured exactly; every wait clamped to 60 s.
                        var delay = Math.Min(60, retryAfter ?? Math.Min(60, 2 * Math.Pow(2, attempt)));
                        response.Dispose();
                        Logger.Warning(
                            $"WebHDFS OPEN '{relativePath}' returned HTTP {status}; "
                            + $"retry {attempt + 1}/{MaxRetries} in {delay:0.##}s");
                        await DelayAsync(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
                        continue;
                    }

                    var raw = await SafeReadAsync(response, ct).ConfigureAwait(false);
                    response.Dispose();
                    if (status >= 500)
                        Settle(_breaker.OnFailure);
                    else
                        Settle(_breaker.OnIgnored);
                    throw new HdfsException($"OPEN '{relativePath}' failed (HTTP {status}): {raw}")
                    { StatusCode = status };
                }

                Settle(_breaker.OnSuccess);
                return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            // Any escape without a terminal classification releases the slot as
            // ignored, so a wedged HalfOpen can never happen.
            Settle(_breaker.OnIgnored);
        }
    }

    // ── HTTP plumbing (JSON ops) ─────────────────────────────────────────────

    internal async Task<HdfsResponse> SendAsync(Uri uri, CancellationToken ct)
    {
        if (!_breaker.TryAcquire())
        {
            return new HdfsResponse
            {
                StatusCode = HttpStatusCode.ServiceUnavailable,
                RawBody = "circuit open (hdfs); failing fast",
                CircuitOpen = true,
            };
        }

        var settled = false;
        void Settle(Action classify)
        {
            if (settled)
                return;
            settled = true;
            classify();
        }

        try
        {
            for (var attempt = 0; ; attempt++)
            {
                HttpResponseMessage response;
                string raw;
                try
                {
                    Metrics.IncHdfsCalls();
                    response = await _http.GetAsync(uri, ct).ConfigureAwait(false);
                    raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
                catch (HttpRequestException exc) when (attempt < MaxRetries)
                {
                    var delay = Math.Min(60, 2 * Math.Pow(2, attempt));
                    // uri.AbsolutePath only — the query string can carry the
                    // HDFS delegation token and must never reach a log line.
                    Logger.Warning(
                        $"WebHDFS request for '{uri.AbsolutePath}' failed ({exc.Message}); "
                        + $"retry {attempt + 1}/{MaxRetries} in {delay:0.##}s");
                    await DelayAsync(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
                    continue;
                }
                catch (OperationCanceledException)
                {
                    Settle(_breaker.OnIgnored);
                    throw;
                }
                catch (Exception)
                {
                    Settle(_breaker.OnFailure);  // retries exhausted on a transport failure
                    throw;
                }

                using (response)
                {
                    var status = response.StatusCode;
                    if (((int)status == 429 || (int)status >= 500) && attempt < MaxRetries)
                    {
                        var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds
                            ?? (response.Headers.RetryAfter?.Date is { } date
                                ? Math.Max(0, (date - DateTimeOffset.UtcNow).TotalSeconds)
                                : (double?)null);
                        // Retry-After honoured exactly; every wait clamped to 60 s.
                        var delay = Math.Min(60, retryAfter ?? Math.Min(60, 2 * Math.Pow(2, attempt)));
                        Logger.Warning(
                            $"WebHDFS request for '{uri.AbsolutePath}' returned HTTP {(int)status}; "
                            + $"retry {attempt + 1}/{MaxRetries} in {delay:0.##}s");
                        await DelayAsync(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
                        continue;
                    }

                    // Terminal outcome — classify for the breaker. 5xx = outage;
                    // 4xx/429 ignored; 2xx clears the window.
                    if ((int)status >= 500)
                        Settle(_breaker.OnFailure);
                    else if ((int)status is >= 400 and < 500)
                        Settle(_breaker.OnIgnored);
                    else
                        Settle(_breaker.OnSuccess);

                    JsonNode? parsed = null;
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        try
                        {
                            parsed = JsonNode.Parse(raw);
                        }
                        catch
                        {
                            // non-JSON body
                        }
                    }
                    return new HdfsResponse
                    {
                        StatusCode = status,
                        Body = parsed,
                        RawBody = raw,
                    };
                }
            }
        }
        finally
        {
            Settle(_breaker.OnIgnored);
        }
    }

    /// <summary>Extract the message from a WebHDFS RemoteException body, or null.</summary>
    internal static string? RemoteExceptionMessage(JsonNode? body) =>
        body?["RemoteException"]?["message"]?.GetValue<string>();

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return raw.Length > 512 ? raw[..512] : raw;
        }
        catch
        {
            // Best-effort error-body read for a message that is ALREADY being
            // thrown as an HdfsException — a secondary read failure here must
            // not mask the primary error; the empty string just means "no body".
            return string.Empty;
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Lightweight response wrapper for WebHDFS JSON operations.</summary>
public sealed class HdfsResponse
{
    public required HttpStatusCode StatusCode { get; init; }
    public JsonNode? Body { get; init; }
    public string RawBody { get; init; } = string.Empty;

    /// <summary>True when the request was not sent because the HDFS circuit is open.</summary>
    public bool CircuitOpen { get; init; }

    public bool IsSuccess => (int)StatusCode is >= 200 and < 300;
}
