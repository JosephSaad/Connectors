// Graph/GraphClient.cs
// --------------------
// Microsoft Graph external-connections API client.
//
//   * client-credentials token (scope from GRAPH_SCOPE / sovereign default)
//   * EnsureConnectionAsync   — create-or-get /external/connections/{id}
//   * RegisterSchemaAsync     — PATCH schema, poll the async operation
//   * PutItemAsync            — single PUT (ingest-item / retry-failed)
//   * PutItemsBatchAsync      — $batch PUT pipeline (bulk ingest hot path)
//   * UpdateItemAclsBatchAsync — $batch PATCH acl (seat-change re-ACL pass)
//   * DeleteItemAsync         — DELETE the externalItem (purge / retention)
//
// Sovereign clouds (docs/RETRY.md, env/README.md): GRAPH_BASE_URL and
// GRAPH_SCOPE override the public-cloud endpoint/audience (e.g.
// https://graph.microsoft.us). When unset, both are byte-identical to the
// public defaults. Read live per access so each cycle/shard sees env state.
//
// Retry policy (docs/RETRY.md): 429/5xx retried up to GRAPH_MAX_RETRIES.
// A server Retry-After is honoured exactly (never jittered) but clamped to a
// 60 s hard cap (mirrors the $batch retry ladder); computed exponential
// backoff (base·2^attempt, cap 60 s) gets optional ±20% jitter when
// GRAPH_RETRY_JITTER=true. Inside a $batch only throttled (429) and 503 items
// are re-sent; other failures are surfaced per item. The HttpMessageHandler
// is injectable so tests exercise the full pipeline without the network.

using System.Globalization;
using System.Net;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AltrataConnector.Config;
using AltrataConnector.Entitlement;
using AltrataConnector.Infrastructure;

namespace AltrataConnector.Graph;

public interface IGraphClient
{
    Task EnsureConnectionAsync(CancellationToken ct = default);
    Task RegisterSchemaAsync(GraphSchema schema, CancellationToken ct = default);
    Task PutItemAsync(ExternalItem item, CancellationToken ct = default);
    Task<IReadOnlyList<BatchOpResult>> PutItemsBatchAsync(
        IReadOnlyList<ExternalItem> items, CancellationToken ct = default);
    Task UpdateItemAclAsync(string itemId, IReadOnlyList<AclEntry> acl, CancellationToken ct = default);
    Task<IReadOnlyList<BatchOpResult>> UpdateItemAclsBatchAsync(
        IReadOnlyList<AclUpdate> updates, CancellationToken ct = default);
    Task DeleteItemAsync(string itemId, CancellationToken ct = default);
    /// <summary>Withdraw items via $batch DELETE (delta tombstones / purge).
    /// A 404 inside the batch counts as success — deletion is idempotent.</summary>
    Task<IReadOnlyList<BatchOpResult>> DeleteItemsBatchAsync(
        IReadOnlyList<string> itemIds, CancellationToken ct = default);
    Task<bool> ConnectionExistsAsync(CancellationToken ct = default);
    /// <summary>Graph dependency breaker state (Closed when disabled) — drives
    /// the crawl's degraded-mode pause and /health readiness.</summary>
    CircuitState BreakerState => CircuitState.Closed;
}

/// <summary>One ACL rewrite for the batched re-ACL pass.</summary>
public sealed record AclUpdate(string ItemId, IReadOnlyList<AclEntry> Acl);

/// <summary>Per-item outcome of a $batch operation.</summary>
public sealed record BatchOpResult(string ItemId, bool Success, int Status, string? Error);

public sealed class GraphClientException : Exception
{
    public int StatusCode { get; }
    public GraphClientException(int statusCode, string message) : base(message) =>
        StatusCode = statusCode;
}

/// <summary>Tracks $batch concurrency: dials down on 429, dials up on sustained success.
/// Ported from the reference connector (ramp after 3 consecutive successes).</summary>
internal sealed class AdaptiveConcurrency
{
    private static readonly IAppLogger Logger = Logging.GetLogger("altrata_connector.graph");

    private readonly int _max;
    private int _current;
    private int _successStreak;
    private readonly object _lock = new();

    public AdaptiveConcurrency(int maxWorkers)
    {
        _max = Math.Max(1, maxWorkers);
        _current = _max;
    }

    public int Current
    {
        get { lock (_lock) return _current; }
    }

    public void OnSuccess()
    {
        lock (_lock)
        {
            _successStreak += 1;
            // Ramp up after 3 consecutive successes (not 10) to recover quickly.
            if (_successStreak >= 3 && _current < _max)
            {
                _current += 1;
                _successStreak = 0;
                Logger.Info($"Graph concurrency ramped up to {_current}");
            }
        }
    }

    public void OnThrottle()
    {
        Metrics.Increment("altrata_graph_throttle_429_total");
        lock (_lock)
        {
            var prev = _current;
            _current = Math.Max(1, _current - 1);
            _successStreak = 0;
            if (_current != prev)
                Logger.Warning($"Graph 429 throttling — concurrency reduced to {_current}");
        }
    }
}

public sealed class GraphClient : IGraphClient
{
    private static readonly IAppLogger Logger = Logging.GetLogger("altrata_connector.graph");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Hard limit imposed by the Graph API — do not exceed.
    /// https://learn.microsoft.com/en-us/graph/json-batching#batch-size-limitations</summary>
    public const int GraphBatchMaxSize = 20;

    /// <summary>Hard cap on any single retry wait, including server Retry-After.</summary>
    internal const int MaxRetryWaitSeconds = 60;

    // ---- sovereign-cloud endpoint override (live-read, like other env knobs) ----

    /// <summary>Graph host: GRAPH_BASE_URL override, default public cloud.</summary>
    public static string GraphBaseUrl
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("GRAPH_BASE_URL");
            return string.IsNullOrEmpty(custom) ? "https://graph.microsoft.com" : custom.TrimEnd('/');
        }
    }

    /// <summary>Token scope: GRAPH_SCOPE override, default {GraphBaseUrl}/.default
    /// so a sovereign base URL automatically yields the matching audience.</summary>
    public static string GraphScope
    {
        get
        {
            var scope = Environment.GetEnvironmentVariable("GRAPH_SCOPE");
            return !string.IsNullOrEmpty(scope) ? scope : $"{GraphBaseUrl}/.default";
        }
    }

    private readonly AppConfig _config;
    private readonly HttpClient _http;
    private readonly Func<double, CancellationToken, Task> _delay;

    private string? _token;
    private DateTime _tokenExpiresUtc;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly CircuitBreaker _breaker;

    /// <summary>{GRAPH_BASE_URL}/{GRAPH_API_VERSION} — live so tests/shards can flip env.</summary>
    public string BaseUrl => $"{GraphBaseUrl}/{_config.GraphApiVersion}";

    /// <summary>Graph dependency breaker state (Closed when disabled).</summary>
    public CircuitState BreakerState => _breaker.State;

    /// <summary>Breaker snapshot for /metrics and /health.</summary>
    public BreakerSnapshot BreakerSnapshot => _breaker.Snapshot();

    public GraphClient(AppConfig config, HttpMessageHandler? handler = null,
        Func<double, CancellationToken, Task>? delay = null, CircuitBreaker? breaker = null)
    {
        _config = config;
        // Injected handler = tests; otherwise the enterprise connectivity
        // handler (PROXY_URL / PROXY_BYPASS / CA_BUNDLE_PATH — fails fast
        // naming the setting on bad input).
        _http = handler == null
            ? new HttpClient(HttpConnectivity.CreateHandler())
            : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(120);
        _delay = delay ?? ((seconds, ct) => Task.Delay(TimeSpan.FromSeconds(seconds), ct));
        _breaker = breaker ?? new CircuitBreaker("graph", CircuitBreakerOptions.FromEnv(critical: true));
    }

    // ---- token -------------------------------------------------------------

    private X509Certificate2? _clientCertificate;
    private bool _authModeLogged;

    /// <summary>Token-request form: certificate client_assertion when
    /// GRAPH_CLIENT_CERT_PATH / GRAPH_CLIENT_CERT_THUMBPRINT is configured
    /// (certificate WINS over the client secret), else the secret. Only the
    /// MODE is ever logged — never key material or the assertion.</summary>
    internal Dictionary<string, string> BuildTokenRequestForm(string tokenUrl)
    {
        if (CertificateCredential.Configured)
        {
            _clientCertificate ??= CertificateCredential.Load();
            if (!_authModeLogged)
            {
                Logger.Info($"Graph auth mode: {CertificateCredential.ModeDescription} " +
                            $"(certificate thumbprint {_clientCertificate.Thumbprint})");
                _authModeLogged = true;
            }
            return new Dictionary<string, string>
            {
                ["client_id"] = _config.AadClientId,
                ["client_assertion_type"] = CertificateCredential.ClientAssertionType,
                ["client_assertion"] = CertificateCredential.BuildClientAssertion(
                    _clientCertificate, _config.AadClientId, tokenUrl),
                ["scope"] = GraphScope,
                ["grant_type"] = "client_credentials",
            };
        }

        if (!_authModeLogged)
        {
            Logger.Info("Graph auth mode: client secret");
            _authModeLogged = true;
        }
        return new Dictionary<string, string>
        {
            ["client_id"] = _config.AadClientId,
            ["client_secret"] = _config.AadClientSecret,
            ["scope"] = GraphScope,
            ["grant_type"] = "client_credentials",
        };
    }

    internal async Task<string> GetTokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token != null && DateTime.UtcNow < _tokenExpiresUtc - TimeSpan.FromMinutes(5))
                return _token;

            var url = $"https://login.microsoftonline.com/{_config.AadTenantId}/oauth2/v2.0/token";
            using var content = new FormUrlEncodedContent(BuildTokenRequestForm(url));
            using var response = await _http.PostAsync(url, content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new GraphClientException((int)response.StatusCode,
                    $"Token request failed ({(int)response.StatusCode}): {Truncate(body)}");

            using var doc = JsonDocument.Parse(body);
            _token = doc.RootElement.GetProperty("access_token").GetString()
                     ?? throw new GraphClientException(500, "Token response had no access_token");
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
            _tokenExpiresUtc = DateTime.UtcNow.AddSeconds(expiresIn);
            return _token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    // ---- retrying transport (behind the circuit breaker) ------------------------

    /// <summary>
    /// Every Graph HTTP call funnels through here, so the breaker sees one
    /// outcome per call (after the internal retry ladder). Open ⇒ fail fast with
    /// <see cref="CircuitOpenException"/>; a 5xx result or a transport/timeout
    /// exception is a breaker failure, while 2xx/3xx/4xx/429 (dependency is
    /// responding) and a graceful-stop cancellation are not.
    /// </summary>
    internal Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct) =>
        _breaker.ExecuteAsync(
            () => SendWithRetryCoreAsync(requestFactory, ct),
            isFailureResult: r => (int)r.StatusCode >= 500,
            isTripException: ex => HttpTripPolicy.IsTrip(ex, ct));

    private async Task<HttpResponseMessage> SendWithRetryCoreAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            using var request = requestFactory();
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", await GetTokenAsync(ct));
            // Propagate W3C trace context so the collector links Graph calls to
            // the crawl span (no-op when no Activity is current).
            Telemetry.InjectTraceContext(request);

            HttpResponseMessage response;
            try
            {
                Metrics.Increment("altrata_graph_requests_total");
                response = await _http.SendAsync(request, ct);
            }
            catch (HttpRequestException exc) when (attempt < _config.GraphMaxRetries)
            {
                attempt++;
                Metrics.Increment("altrata_graph_retries_total");
                var backoff = RetryDelay.Jitter(
                    RetryDelay.ComputeBackoff(_config.GraphRetryBackoffBase, attempt));
                Logger.Warning($"Graph transport error ({exc.Message}); retry {attempt}/{_config.GraphMaxRetries} in {backoff:0.##}s");
                await _delay(backoff, ct);
                continue;
            }

            if (!IsRetryable(response.StatusCode) || attempt >= _config.GraphMaxRetries)
                return response;

            attempt++;
            Metrics.Increment("altrata_graph_retries_total");
            if ((int)response.StatusCode == 429)
                Metrics.Increment("altrata_graph_throttle_429_total");
            double delaySeconds;
            var retryAfter = ReadRetryAfterSeconds(response);
            if (retryAfter.HasValue)
            {
                // Server value honoured EXACTLY — never jittered — but clamped to
                // the 60 s hard cap (mirrors the $batch retry ladder).
                delaySeconds = retryAfter.Value;
                if (delaySeconds > MaxRetryWaitSeconds)
                {
                    Logger.Warning(string.Format(CultureInfo.InvariantCulture,
                        "Retry-After of {0:F0}s exceeds cap; clamping to {1}s",
                        delaySeconds, MaxRetryWaitSeconds));
                    delaySeconds = MaxRetryWaitSeconds;
                }
                Logger.Warning($"Graph {(int)response.StatusCode}; honouring Retry-After {delaySeconds:0.##}s (retry {attempt}/{_config.GraphMaxRetries})");
            }
            else
            {
                delaySeconds = RetryDelay.Jitter(
                    RetryDelay.ComputeBackoff(_config.GraphRetryBackoffBase, attempt));
                Logger.Warning($"Graph {(int)response.StatusCode}; backing off {delaySeconds:0.##}s (retry {attempt}/{_config.GraphMaxRetries})");
            }
            response.Dispose();
            await _delay(delaySeconds, ct);
        }
    }

    internal static bool IsRetryable(HttpStatusCode status) =>
        status == (HttpStatusCode)429
        || status is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    internal static double? ReadRetryAfterSeconds(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter == null)
            return null;
        if (retryAfter.Delta.HasValue)
            return retryAfter.Delta.Value.TotalSeconds;
        if (retryAfter.Date.HasValue)
            return Math.Max(0, (retryAfter.Date.Value.UtcDateTime - DateTime.UtcNow).TotalSeconds);
        return null;
    }

    // ---- operations --------------------------------------------------------------

    public async Task<bool> ConnectionExistsAsync(CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get,
                $"{BaseUrl}/external/connections/{_config.ConnectorId}"), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        await EnsureSuccessAsync(response, "get connection");
        return true;
    }

    public async Task EnsureConnectionAsync(CancellationToken ct = default)
    {
        if (await ConnectionExistsAsync(ct))
        {
            Logger.Info($"Connection '{_config.ConnectorId}' already exists");
            return;
        }

        Logger.Info($"Creating connection '{_config.ConnectorId}'...");
        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/external/connections");
            request.Content = JsonContent.Create(new
            {
                id = _config.ConnectorId,
                name = _config.ConnectorName,
                description = _config.ConnectorDescription,
            }, options: JsonOptions);
            return request;
        }, ct);
        await EnsureSuccessAsync(response, "create connection");
        Logger.Info($"Connection '{_config.ConnectorId}' created");
    }

    public async Task RegisterSchemaAsync(GraphSchema schema, CancellationToken ct = default)
    {
        Logger.Info("Registering schema (this can take several minutes)...");
        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Patch,
                $"{BaseUrl}/external/connections/{_config.ConnectorId}/schema");
            request.Content = JsonContent.Create(schema, options: JsonOptions);
            return request;
        }, ct);

        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            var location = response.Headers.Location?.ToString();
            if (location != null)
                await PollOperationAsync(location, ct);
            return;
        }
        await EnsureSuccessAsync(response, "register schema");
    }

    private async Task PollOperationAsync(string operationUrl, CancellationToken ct)
    {
        for (var i = 0; i < 120; i++)
        {
            await _delay(10, ct);
            using var response = await SendWithRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Get, operationUrl), ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new GraphClientException((int)response.StatusCode,
                    $"Schema operation poll failed: {Truncate(body)}");
            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            switch (status)
            {
                case "completed":
                    Logger.Info("Schema registration completed");
                    return;
                case "failed":
                    throw new GraphClientException(500, $"Schema registration failed: {Truncate(body)}");
                default:
                    Logger.Info($"Schema registration status: {status ?? "unknown"}...");
                    break;
            }
        }
        throw new GraphClientException(408, "Schema registration timed out after 20 minutes");
    }

    public async Task PutItemAsync(ExternalItem item, CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Put,
                $"{BaseUrl}/external/connections/{_config.ConnectorId}/items/{Uri.EscapeDataString(item.Id)}");
            request.Content = JsonContent.Create(item, options: JsonOptions);
            return request;
        }, ct);
        await EnsureSuccessAsync(response, $"put item {item.Id}");
    }

    public async Task UpdateItemAclAsync(string itemId, IReadOnlyList<AclEntry> acl,
        CancellationToken ct = default)
    {
        SeatAclBuilder.AssertNeverEveryone(acl);
        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Patch,
                $"{BaseUrl}/external/connections/{_config.ConnectorId}/items/{Uri.EscapeDataString(itemId)}");
            request.Content = JsonContent.Create(new { acl }, options: JsonOptions);
            return request;
        }, ct);
        await EnsureSuccessAsync(response, $"update acl for item {itemId}");
    }

    public async Task DeleteItemAsync(string itemId, CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Delete,
                $"{BaseUrl}/external/connections/{_config.ConnectorId}/items/{Uri.EscapeDataString(itemId)}"), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;  // already gone — deletion is idempotent
        await EnsureSuccessAsync(response, $"delete item {itemId}");
    }

    // ---- $batch pipeline ------------------------------------------------------------

    private string ItemPath(string itemId) =>
        $"/external/connections/{_config.ConnectorId}/items/{Uri.EscapeDataString(itemId)}";

    public Task<IReadOnlyList<BatchOpResult>> PutItemsBatchAsync(
        IReadOnlyList<ExternalItem> items, CancellationToken ct = default)
    {
        var requests = new List<(string ItemId, JsonObject Request)>(items.Count);
        foreach (var item in items)
        {
            // Seat entitlement invariant holds on the batched path too.
            SeatAclBuilder.AssertNeverEveryone(item.Acl);
            var body = (JsonObject)JsonSerializer.SerializeToNode(item, JsonOptions)!;
            body.Remove("id");  // the id travels in the URL, not the body
            requests.Add((item.Id, new JsonObject
            {
                ["method"] = "PUT",
                ["url"] = ItemPath(item.Id),
                ["headers"] = new JsonObject { ["Content-Type"] = "application/json" },
                ["body"] = body,
            }));
        }
        return DispatchBatchesAsync(requests, "put", ct);
    }

    public Task<IReadOnlyList<BatchOpResult>> UpdateItemAclsBatchAsync(
        IReadOnlyList<AclUpdate> updates, CancellationToken ct = default)
    {
        var requests = new List<(string ItemId, JsonObject Request)>(updates.Count);
        foreach (var update in updates)
        {
            SeatAclBuilder.AssertNeverEveryone(update.Acl);
            requests.Add((update.ItemId, new JsonObject
            {
                ["method"] = "PATCH",
                ["url"] = ItemPath(update.ItemId),
                ["headers"] = new JsonObject { ["Content-Type"] = "application/json" },
                ["body"] = new JsonObject
                {
                    ["acl"] = JsonSerializer.SerializeToNode(update.Acl, JsonOptions),
                },
            }));
        }
        return DispatchBatchesAsync(requests, "patch-acl", ct);
    }

    public Task<IReadOnlyList<BatchOpResult>> DeleteItemsBatchAsync(
        IReadOnlyList<string> itemIds, CancellationToken ct = default)
    {
        var requests = itemIds.Select(itemId => (itemId, new JsonObject
        {
            ["method"] = "DELETE",
            ["url"] = ItemPath(itemId),
        })).ToList();
        return DispatchBatchesAsync(requests, "delete", ct, notFoundOk: true);
    }

    /// <summary>
    /// Split into sub-batches of GRAPH_BATCH_SIZE (≤20) and send them in
    /// adaptive waves: each wave runs up to AdaptiveConcurrency.Current
    /// sub-batches concurrently; 429s dial the next wave down, sustained
    /// success dials it back up (max GRAPH_BATCH_WORKERS).
    /// </summary>
    private async Task<IReadOnlyList<BatchOpResult>> DispatchBatchesAsync(
        List<(string ItemId, JsonObject Request)> requests, string op, CancellationToken ct,
        bool notFoundOk = false)
    {
        var results = new List<BatchOpResult>(requests.Count);
        if (requests.Count == 0)
            return results;

        using var span = Telemetry.Span("graph.batch", ActivityKind.Client);
        Telemetry.SetTag(span, "altrata.graph.op", op);
        Telemetry.SetTag(span, "altrata.graph.batch.size", requests.Count);

        var subBatchSize = Math.Min(Math.Max(1, _config.GraphBatchSize), GraphBatchMaxSize);
        var subBatches = new List<List<(string ItemId, JsonObject Request)>>();
        for (var i = 0; i < requests.Count; i += subBatchSize)
            subBatches.Add(requests.GetRange(i, Math.Min(subBatchSize, requests.Count - i)));

        var adaptive = new AdaptiveConcurrency(_config.GraphBatchWorkers);
        var resultsLock = new object();
        var next = 0;
        while (next < subBatches.Count)
        {
            ct.ThrowIfCancellationRequested();
            var wave = subBatches.Skip(next).Take(adaptive.Current).ToList();
            next += wave.Count;
            await Task.WhenAll(wave.Select(async subBatch =>
            {
                var batchResults = await SendSubBatchWithRetriesAsync(subBatch, adaptive, ct, notFoundOk);
                lock (resultsLock)
                    results.AddRange(batchResults);
            }));
        }
        Telemetry.SetTag(span, "altrata.graph.batch.ok", results.Count(r => r.Success));
        Telemetry.SetTag(span, "altrata.graph.batch.failed", results.Count(r => !r.Success));
        return results;
    }

    /// <summary>
    /// The per-sub-batch retry ladder (ported from the reference connector):
    /// only 429s (throttle) and 503s (transient outage, no throttle signal)
    /// are re-sent; the Retry-After from the first 429 raises the computed
    /// wait; every wait is capped at 60 s; items missing from the response
    /// and empty responses are failures; exhausted retries mark the rest as
    /// permanent 429 failures.
    /// </summary>
    internal async Task<List<BatchOpResult>> SendSubBatchWithRetriesAsync(
        List<(string ItemId, JsonObject Request)> subBatch, AdaptiveConcurrency adaptive,
        CancellationToken ct, bool notFoundOk = false)
    {
        var results = new List<BatchOpResult>();
        var current = subBatch;
        List<JsonObject> responses = new();

        for (var attempt = 0; attempt <= _config.GraphMaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                // Jitter applies only to the COMPUTED backoff; the server's
                // Retry-After below raises it and the 60 s cap bounds both.
                var wait = RetryDelay.Jitter(
                    _config.GraphRetryBackoffBase * Math.Pow(2, attempt - 1));
                var retryAfter = responses
                    .Where(r => (r["status"]?.GetValue<int>() ?? 0) == 429)
                    .Select(r => (r["headers"] as JsonObject)?["Retry-After"]?.ToString()
                                 ?? (r["headers"] as JsonObject)?["retry-after"]?.ToString())
                    .FirstOrDefault(v => !string.IsNullOrEmpty(v));
                if (retryAfter != null &&
                    double.TryParse(retryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var ra))
                {
                    wait = Math.Max(wait, ra);
                }
                if (wait > MaxRetryWaitSeconds)
                {
                    Logger.Warning(string.Format(CultureInfo.InvariantCulture,
                        "Retry-After of {0:F0}s exceeds cap; clamping to {1}s", wait, MaxRetryWaitSeconds));
                    wait = MaxRetryWaitSeconds;
                }
                Logger.Warning(string.Format(CultureInfo.InvariantCulture,
                    "Retrying {0} throttled items in {1:F0}s (attempt {2}/{3})",
                    current.Count, wait, attempt, _config.GraphMaxRetries));
                await _delay(wait, ct);
            }

            // Renumber ids 0..n-1 for this round.
            var payload = new JsonArray();
            for (var i = 0; i < current.Count; i++)
            {
                var request = (JsonObject)current[i].Request.DeepClone();
                request["id"] = i.ToString(CultureInfo.InvariantCulture);
                payload.Add(request);
            }

            try
            {
                responses = await PostBatchAsync(payload, ct);
            }
            catch (OperationCanceledException)
            {
                // Graceful stop / cancellation — propagate untouched; the crawl
                // saves its checkpoint and exits at the superchunk boundary.
                throw;
            }
            catch (CircuitOpenException)
            {
                // Breaker open ⇒ degraded pause, NOT a per-item failure. Propagate
                // so the crawl checkpoints and pauses instead of dead-lettering.
                throw;
            }
            catch (Exception exc)
            {
                Logger.Error($"Graph $batch call failed for {current.Count} item(s) " +
                             $"(op attempt {attempt}/{_config.GraphMaxRetries}): {exc.GetType().Name}: {exc.Message} " +
                             "— all items in this sub-batch dead-letter for retry-failed.");
                results.AddRange(current.Select(entry =>
                    new BatchOpResult(entry.ItemId, false, 0, $"[Graph] $batch POST failed: {exc.Message}")));
                return results;
            }

            if (Logger.IsDebugEnabled)
            {
                // Hot-path log gating: serializing the full $batch response array is
                // the most expensive log message in the pipeline — DEBUG only.
                Logger.Debug($"BATCH RESPONSE (attempt {attempt}): {JsonSerializer.Serialize(responses)}");
            }

            var retryable = new List<(string ItemId, JsonObject Request)>();
            var sawThrottle = false;

            if (responses.Count == 0)
            {
                Logger.Error($"Graph $batch returned empty response for {current.Count} items — marking all as failed");
                results.AddRange(current.Select(entry =>
                    new BatchOpResult(entry.ItemId, false, 0, "[Graph] $batch returned empty response")));
                adaptive.OnSuccess();  // not a throttle — don't punish concurrency
                return results;
            }

            var accounted = new HashSet<int>();
            foreach (var response in responses)
            {
                if (!int.TryParse(response["id"]?.ToString(), out var index)
                    || index < 0 || index >= current.Count)
                {
                    Logger.Warning($"Graph $batch response has unrecognised id '{response["id"]}' — cannot match to a submitted item");
                    continue;
                }
                accounted.Add(index);
                var (itemId, request) = current[index];
                var status = response["status"]?.GetValue<int>() ?? 0;

                if (status is >= 200 and < 300 || (notFoundOk && status == 404))
                {
                    // 404 on DELETE = already gone — idempotent success.
                    results.Add(new BatchOpResult(itemId, true, status, null));
                }
                else if (status == 429)
                {
                    sawThrottle = true;
                    retryable.Add((itemId, request));
                }
                else if (status == 503)
                {
                    // Transient outage — retry without a throttle signal (503 is
                    // not a rate limit; don't penalise adaptive concurrency).
                    Logger.Warning($"Graph 503 on item {itemId} — will retry");
                    retryable.Add((itemId, request));
                }
                else
                {
                    var error = ExtractBatchError(status, response);
                    results.Add(new BatchOpResult(itemId, false, status, error));
                    Logger.Error($"Graph batch item {itemId} failed — {error}");
                }
            }

            // Items with NO response at all are failures, not silent drops.
            for (var i = 0; i < current.Count; i++)
            {
                if (!accounted.Contains(i))
                {
                    results.Add(new BatchOpResult(current[i].ItemId, false, 0,
                        "[Graph] No response received for this item in $batch"));
                    Logger.Error($"Graph batch item {current[i].ItemId} — no response received (missing from $batch response)");
                }
            }

            if (sawThrottle)
                adaptive.OnThrottle();

            if (retryable.Count == 0)
            {
                if (!sawThrottle)
                    adaptive.OnSuccess();
                return results;
            }
            current = retryable;
        }

        // Exhausted all retries — remaining throttled items are permanent failures.
        foreach (var (itemId, _) in current)
        {
            results.Add(new BatchOpResult(itemId, false, 429,
                "[Graph] HTTP 429: throttled after all retries"));
            Logger.Error($"Graph batch item {itemId} failed — 429 after {_config.GraphMaxRetries} retries");
        }
        return results;
    }

    /// <summary>POST /$batch (≤20 requests) with the standard outer transport retry.</summary>
    internal async Task<List<JsonObject>> PostBatchAsync(JsonArray requests, CancellationToken ct)
    {
        if (requests.Count > GraphBatchMaxSize)
            throw new ArgumentException(
                $"Batch size {requests.Count} exceeds the Graph API maximum of {GraphBatchMaxSize}. " +
                "Split the payload into smaller chunks before calling PostBatchAsync().");

        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/$batch");
            var envelope = new JsonObject { ["requests"] = (JsonArray)requests.DeepClone() };
            request.Content = new StringContent(envelope.ToJsonString(), Encoding.UTF8, "application/json");
            return request;
        }, ct);
        await EnsureSuccessAsync(response, "$batch");

        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            var parsed = JsonNode.Parse(body);
            if (parsed is JsonObject obj && obj["responses"] is JsonArray array)
                return array.OfType<JsonObject>().ToList();
        }
        catch (JsonException exc)
        {
            // Fall through — treated as an empty response by the ladder
            // (unchanged), but distinguish "garbled body" from "genuinely
            // empty" for the operator. Only the length and parser position are
            // logged; the body itself is never logged (it can embed item
            // payloads, and error text may quote them).
            Logger.Warning($"Graph $batch response body did not parse as JSON " +
                           $"({exc.Message}; body length {body.Length} chars) — treating as an empty response.");
        }
        return new List<JsonObject>();
    }

    private static string ExtractBatchError(int status, JsonObject response)
    {
        var body = response["body"];
        if (body is JsonObject bodyObj)
        {
            var error = bodyObj["error"] as JsonObject;
            var message = error?["message"]?.ToString() ?? bodyObj.ToJsonString();
            var code = error?["code"]?.ToString() ?? "";
            return $"[Graph] HTTP {status}: {code} -- {message}".TrimEnd(' ', '-');
        }
        return $"[Graph] HTTP {status}: {body?.ToString() ?? "None"}";
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync();
        throw new GraphClientException((int)response.StatusCode,
            $"Graph {operation} failed ({(int)response.StatusCode}): {Truncate(body)}");
    }

    private static string Truncate(string text) =>
        text.Length <= 500 ? text : text[..500] + "...";
}
