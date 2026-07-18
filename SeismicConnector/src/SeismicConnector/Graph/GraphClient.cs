// Graph/GraphClient.cs
// --------------------
// Microsoft Graph external-connections API client.
//
// * Client-credentials token against AAD_APP_OAUTH_AUTHORITY_HOST (default
//   login.microsoftonline.com; sovereign clouds override it), cached and
//   refreshed 60s before expiry.
// * Sovereign-cloud endpoint override: GRAPH_BASE_URL, GRAPH_SCOPE and
//   GRAPH_API_VERSION are read LIVE from the environment on every access
//   (falling back to the loaded config, then the public-cloud defaults), so
//   each cycle/shard sees current env state.
// * Retry contract (docs/RETRY.md): 429 and 5xx retried up to MaxRetries. A
//   server-provided Retry-After is ALWAYS honoured exactly (never jittered);
//   computed exponential backoff (base * 2^attempt) gets optional ±20% jitter
//   when GRAPH_RETRY_JITTER=true. Every wait is capped at 60s (a clamp
//   warning is logged when Retry-After exceeds the cap).
// * $batch ingestion: up to 20 requests per POST; per-item failures inside a
//   2xx batch envelope are surfaced individually. 429 sub-responses are
//   re-sent in shrinking retry rounds (up to MaxRetries) whose wait honours
//   the sub-response Retry-After; 503 sub-responses retry too but never
//   signal throttle to adaptive concurrency.
//
// Tests inject an HttpMessageHandler and an override token, so no network is
// touched.

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SeismicConnector.Config;
using SeismicConnector.Infrastructure;

namespace SeismicConnector.Graph;

/// <summary>Raised on non-retryable Graph errors or when retries are exhausted.</summary>
public sealed class GraphApiError : Exception
{
    public int StatusCode { get; }
    public string? Body { get; }

    public GraphApiError(int statusCode, string message, string? body = null)
        : base($"Graph API error {statusCode}: {message}")
    {
        StatusCode = statusCode;
        Body = body;
    }
}

/// <summary>Outcome of one item inside a $batch envelope.</summary>
public sealed record BatchItemResult(string ItemId, bool Success, int Status, string? Error, JsonNode? ResponseBody);

/// <summary>Outcome of a full $batch retry ladder: per-item results + throttle signal.</summary>
public sealed record BatchOutcome(IReadOnlyList<BatchItemResult> Results, bool SawThrottle);

public class GraphClient
{
    private static readonly IAppLogger Logger = Logging.GetLogger("seismic_connector.graph");

    private readonly GraphSettings _settings;
    private readonly HttpClient _http;
    private readonly CircuitBreaker _breaker;
    private readonly object _tokenLock = new();
    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    /// <summary>Test seam: fixed bearer token (skips the OAuth call).</summary>
    internal string? OverrideAccessToken { get; set; }

    /// <summary>Test seam: replaces Task.Delay so retry tests run instantly.</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } =
        (delay, ct) => Task.Delay(delay, ct);

    /// <summary>Delays actually waited (inspected by retry tests).</summary>
    internal List<double> ObservedDelaysSeconds { get; } = new();

    /// <summary>The circuit breaker guarding this client's dependency (Microsoft Graph).</summary>
    public CircuitBreaker Breaker => _breaker;

    public GraphClient(GraphSettings settings, HttpMessageHandler? handler = null, CircuitBreaker? breaker = null)
    {
        _settings = settings;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(120);
        _breaker = breaker ?? CircuitBreakerRegistry.Register(
            new CircuitBreaker(CircuitBreakerRegistry.GraphName, CircuitBreakerOptions.FromEnv(), critical: true));
    }

    /// <summary>
    /// Classify an exception for the breaker: sustained-outage signals
    /// (5xx / timeout / connection) trip it; 4xx and honored 429 (flow control)
    /// do NOT.
    /// </summary>
    internal static bool IsTrippingFailure(Exception ex) => ex switch
    {
        GraphApiError api => api.StatusCode >= 500,
        HttpRequestException => true,
        TaskCanceledException => true,  // HttpClient.Timeout (caller-cancel handled upstream)
        TimeoutException => true,
        _ => false,
    };

    // Sovereign/national clouds host Graph at different endpoints with matching
    // token audiences (e.g. https://graph.microsoft.us for US Government,
    // https://microsoftgraph.chinacloudapi.cn for 21Vianet). The env overrides
    // are read PER ACCESS so each cycle/shard sees live env state; unset falls
    // back to the loaded config values (themselves defaulted from the same
    // env vars at startup), keeping default behaviour byte-identical.

    /// <summary>Graph endpoint: live GRAPH_BASE_URL override, else the configured base.</summary>
    public string BaseUrl =>
        Environment.GetEnvironmentVariable("GRAPH_BASE_URL") is { Length: > 0 } baseUrl
            ? baseUrl.TrimEnd('/')
            : _settings.BaseUrl;

    /// <summary>Graph API version: live GRAPH_API_VERSION override, else the configured version.</summary>
    public string ApiVersion =>
        Environment.GetEnvironmentVariable("GRAPH_API_VERSION") is { Length: > 0 } version
            ? version
            : _settings.ApiVersion;

    public string ApiRoot => $"{BaseUrl}/{ApiVersion}";

    /// <summary>Token audience: GRAPH_SCOPE env override, else &lt;base&gt;/.default.</summary>
    internal string Scope =>
        Environment.GetEnvironmentVariable("GRAPH_SCOPE") is { Length: > 0 } scope
            ? scope
            : $"{BaseUrl}/.default";

    /// <summary>OAuth authority host (sovereign clouds): AAD_APP_OAUTH_AUTHORITY_HOST, default public cloud.</summary>
    internal string AuthorityHost =>
        Environment.GetEnvironmentVariable("AAD_APP_OAUTH_AUTHORITY_HOST") is { Length: > 0 } host
            ? host.TrimEnd('/')
            : "https://login.microsoftonline.com";

    // ── auth ─────────────────────────────────────────────────────────────────

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (OverrideAccessToken is not null)
            return OverrideAccessToken;

        lock (_tokenLock)
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt - TimeSpan.FromSeconds(60))
                return _accessToken;
        }

        var tokenUrl =
            $"{AuthorityHost}/{_settings.TenantId}/oauth2/v2.0/token";
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _settings.ClientId,
                ["client_secret"] = _settings.ClientSecret,
                ["scope"] = Scope,
            }),
        };
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new GraphApiError((int)response.StatusCode, "token request failed", body);

        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new GraphApiError(500, "token response missing access_token");
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
        lock (_tokenLock)
        {
            _accessToken = token;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        }
        return token;
    }

    // ── transport with retry ─────────────────────────────────────────────────

    /// <summary>Max seconds any single retry wait may take (Retry-After included).</summary>
    internal const double MaxWaitSeconds = 60.0;

    /// <summary>
    /// Retry-wrapped send, itself guarded by the circuit breaker: a sustained
    /// outage trips the breaker so subsequent calls fail fast (degraded mode).
    /// </summary>
    public Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct = default) =>
        _breaker.ExecuteAsync(token => SendWithRetryCoreAsync(requestFactory, token), IsTrippingFailure, ct);

    private async Task<HttpResponseMessage> SendWithRetryCoreAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        using var span = Tracing.StartActivity("graph.http", System.Diagnostics.ActivityKind.Client);
        for (var attempt = 0; ; attempt++)
        {
            using var request = requestFactory();
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", await GetTokenAsync(ct).ConfigureAwait(false));
            // Propagate W3C trace context (no-op when tracing is off).
            Tracing.InjectTraceContext(request);
            if (attempt == 0)
            {
                span?.SetTag("http.method", request.Method.Method);
                span?.SetTag("url.path", request.RequestUri?.AbsolutePath);
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < _settings.MaxRetries)
            {
                var delay = ComputeBackoff(attempt);
                Logger.Warning(
                    $"Graph network error ({ex.Message}) — retrying in {delay:F0}s "
                    + $"(attempt {attempt + 1}/{_settings.MaxRetries})");
                await WaitAsync(delay, ct).ConfigureAwait(false);
                continue;
            }

            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
                return response;

            if (status == 429)
                Metrics.IncThrottle429();

            var retryable = status == 429 || status >= 500;
            if (!retryable || attempt >= _settings.MaxRetries)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                response.Dispose();
                throw new GraphApiError(status, retryable ? "retries exhausted" : "non-retryable", body);
            }

            var wait = NextDelaySeconds(attempt, ReadRetryAfter(response));
            response.Dispose();
            Logger.Warning(
                $"Graph transient error {status} — retrying in {wait.ToString("F1", CultureInfo.InvariantCulture)}s "
                + $"(attempt {attempt + 1}/{_settings.MaxRetries})");
            await WaitAsync(wait, ct).ConfigureAwait(false);
        }
    }

    private static string? ReadRetryAfter(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null;

    /// <summary>
    /// Pure delay policy (unit-tested): a parseable server Retry-After is
    /// honoured EXACTLY (never jittered); otherwise the computed exponential
    /// backoff (base·2^attempt) with optional jitter. Both capped at 60s.
    /// </summary>
    internal double NextDelaySeconds(int attempt, string? retryAfterHeader)
    {
        double wait;
        if (retryAfterHeader is not null
            && double.TryParse(retryAfterHeader, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            wait = parsed;  // exact — never jittered
        }
        else
        {
            wait = RetryDelay.Jitter(ComputeBackoff(attempt));
        }
        if (wait > MaxWaitSeconds)
        {
            Logger.Warning(string.Format(
                CultureInfo.InvariantCulture,
                "Retry-After of {0:F0}s exceeds cap; clamping to {1:F0}s",
                wait, MaxWaitSeconds));
            wait = MaxWaitSeconds;
        }
        return wait;
    }

    private double ComputeBackoff(int attempt) =>
        Math.Min(_settings.RetryBackoffBase * Math.Pow(2, attempt), MaxWaitSeconds);

    private Task WaitAsync(double seconds, CancellationToken ct)
    {
        ObservedDelaysSeconds.Add(seconds);
        return DelayAsync(TimeSpan.FromSeconds(seconds), ct);
    }

    // ── plain JSON verbs ─────────────────────────────────────────────────────

    public async Task<JsonNode?> GetAsync(string path, CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, ApiRoot + path), ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<JsonNode?> PostAsync(string path, JsonNode body, CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, ApiRoot + path)
            {
                Content = JsonContent(body),
            }, ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<JsonNode?> PatchAsync(string path, JsonNode body, CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Patch, ApiRoot + path)
            {
                Content = JsonContent(body),
            }, ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<JsonNode?> PutAsync(string path, JsonNode body, CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Put, ApiRoot + path)
            {
                Content = JsonContent(body),
            }, ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>DELETE; 404 is treated as success (already gone).</summary>
    public async Task<bool> DeleteAsync(string path, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendWithRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Delete, ApiRoot + path), ct).ConfigureAwait(false);
            return true;
        }
        catch (GraphApiError ex) when (ex.StatusCode == 404)
        {
            return true;
        }
    }

    private static HttpContent JsonContent(JsonNode body) =>
        new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            // A 2xx response with an unparseable body. Returning null is safe —
            // every caller treats a null body as "no data" (e.g. a $batch round
            // then marks its items "missing from $batch response") — but leave a
            // trail: this is the only place the malformed payload is visible.
            Logger.Warning(
                $"Graph returned a 2xx response with an unparseable JSON body ({ex.Message}); "
                + $"body starts: '{body[..Math.Min(200, body.Length)]}'");
            return null;
        }
    }

    // ── externalItem operations ──────────────────────────────────────────────

    public Task<JsonNode?> PutExternalItemAsync(
        string connectionId, string itemId, JsonNode item, CancellationToken ct = default)
    {
        return PutAsync(
            $"/external/connections/{connectionId}/items/{Uri.EscapeDataString(itemId)}", item, ct);
    }

    public Task<bool> DeleteExternalItemAsync(
        string connectionId, string itemId, CancellationToken ct = default)
    {
        return DeleteAsync(
            $"/external/connections/{connectionId}/items/{Uri.EscapeDataString(itemId)}", ct);
    }

    /// <summary>
    /// Update ONLY an externalItem's ACL (permission re-ACL) without re-sending
    /// its content or properties. PATCHes the item's <c>acl</c> collection via
    /// the dedicated sub-resource, so a permission change is reflected in the
    /// index without a content round-trip.
    /// </summary>
    public Task<JsonNode?> PatchExternalItemAclAsync(
        string connectionId, string itemId, JsonArray acl, CancellationToken ct = default)
    {
        // The externalItem's acl is addressable on its own; PATCH replaces it.
        return PatchAsync(
            $"/external/connections/{connectionId}/items/{Uri.EscapeDataString(itemId)}",
            new JsonObject { ["acl"] = acl.DeepClone() },
            ct);
    }

    // ── $batch ingestion ─────────────────────────────────────────────────────

    /// <summary>
    /// PUT a set of externalItems via one $batch POST per round (max 20 items).
    /// 429 sub-responses are retried in shrinking rounds up to MaxRetries; the
    /// wait between rounds is max(computed backoff with optional jitter, the
    /// first 429 sub-response's Retry-After), capped at 60s. 503 sub-responses
    /// retry too but do NOT set the throttle signal (they are outages, not
    /// rate limits — adaptive concurrency must not be penalised for them).
    /// Items still throttled after the last round are failures.
    /// </summary>
    public async Task<BatchOutcome> PutExternalItemsBatchWithRetryAsync(
        string connectionId,
        IReadOnlyList<(string ItemId, JsonNode Item)> items,
        CancellationToken ct = default)
    {
        if (items.Count == 0)
            return new BatchOutcome(Array.Empty<BatchItemResult>(), false);
        if (items.Count > 20)
            throw new ArgumentException($"Graph $batch supports at most 20 requests; got {items.Count}.");

        var results = new List<BatchItemResult>();
        var sawThrottle = false;
        var pending = items.ToList();
        string? lastRetryAfter = null;

        for (var attempt = 0; ; attempt++)
        {
            if (attempt > 0)
            {
                // Jitter applies only to the COMPUTED backoff; the server's
                // Retry-After is honoured as a floor (never scaled down).
                var wait = RetryDelay.Jitter(_settings.RetryBackoffBase * Math.Pow(2, attempt - 1));
                if (lastRetryAfter is not null
                    && double.TryParse(lastRetryAfter, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var retryAfterSecs))
                {
                    wait = Math.Max(wait, retryAfterSecs);
                }
                if (wait > MaxWaitSeconds)
                {
                    Logger.Warning(string.Format(
                        CultureInfo.InvariantCulture,
                        "Retry-After of {0:F0}s exceeds cap; clamping to {1:F0}s", wait, MaxWaitSeconds));
                    wait = MaxWaitSeconds;
                }
                Logger.Warning(string.Format(
                    CultureInfo.InvariantCulture,
                    "Retrying {0} throttled items in {1:F0}s (attempt {2}/{3})",
                    pending.Count, wait, attempt, _settings.MaxRetries));
                await WaitAsync(wait, ct).ConfigureAwait(false);
            }

            var (roundResults, retryAfterHeader) = await SendBatchRoundAsync(connectionId, pending, ct)
                .ConfigureAwait(false);
            lastRetryAfter = retryAfterHeader;

            var retryIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var result in roundResults)
            {
                if (result.Status == 429)
                {
                    sawThrottle = true;
                    Metrics.IncThrottle429();
                    retryIds.Add(result.ItemId);
                }
                else if (result.Status == 503)
                {
                    // Transient outage — retry without the throttle signal.
                    Logger.Warning($"Graph 503 on item {result.ItemId} — will retry");
                    retryIds.Add(result.ItemId);
                }
                else
                {
                    results.Add(result);
                }
            }

            if (retryIds.Count == 0)
                return new BatchOutcome(results, sawThrottle);

            if (attempt >= _settings.MaxRetries)
            {
                foreach (var result in roundResults.Where(r => retryIds.Contains(r.ItemId)))
                {
                    Logger.Error(
                        $"Graph batch item {result.ItemId} failed — {result.Status} after {_settings.MaxRetries} retries");
                    results.Add(result with
                    {
                        Error = $"HTTP {result.Status}: throttled/unavailable after all retries",
                    });
                }
                return new BatchOutcome(results, sawThrottle);
            }

            pending = pending.Where(p => retryIds.Contains(p.ItemId)).ToList();
        }
    }

    /// <summary>Single-round batch PUT (no per-item retries). Kept for targeted paths and tests.</summary>
    public async Task<List<BatchItemResult>> PutExternalItemsBatchAsync(
        string connectionId,
        IReadOnlyList<(string ItemId, JsonNode Item)> items,
        CancellationToken ct = default)
    {
        if (items.Count == 0)
            return new List<BatchItemResult>();
        if (items.Count > 20)
            throw new ArgumentException($"Graph $batch supports at most 20 requests; got {items.Count}.");
        var (results, _) = await SendBatchRoundAsync(connectionId, items, ct).ConfigureAwait(false);
        return results;
    }

    /// <summary>
    /// POST one $batch envelope and classify sub-responses. Returns the
    /// per-item results plus the Retry-After header of the FIRST 429
    /// sub-response (used by the ladder's inter-round wait).
    /// </summary>
    private async Task<(List<BatchItemResult> Results, string? RetryAfter)> SendBatchRoundAsync(
        string connectionId,
        IReadOnlyList<(string ItemId, JsonNode Item)> items,
        CancellationToken ct)
    {
        var requests = new JsonArray();
        foreach (var (itemId, item) in items)
        {
            requests.Add(new JsonObject
            {
                ["id"] = itemId,
                ["method"] = "PUT",
                ["url"] = $"/external/connections/{connectionId}/items/{Uri.EscapeDataString(itemId)}",
                ["headers"] = new JsonObject { ["Content-Type"] = "application/json" },
                ["body"] = item.DeepClone(),
            });
        }

        var envelope = new JsonObject { ["requests"] = requests };
        var response = await PostAsync("/$batch", envelope, ct).ConfigureAwait(false);

        // Hot-path gate (from the Python original's isEnabledFor(DEBUG)):
        // serializing the full $batch response array is the most expensive log
        // message in the pipeline, and DEBUG is filtered in normal runs.
        if (Logger.IsEnabledFor(LogLevels.Debug))
            Logger.Debug($"BATCH RESPONSE: {response?.ToJsonString() ?? "null"}");

        var results = new List<BatchItemResult>();
        string? retryAfter = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (response?["responses"] is JsonArray responses)
        {
            foreach (var node in responses)
            {
                if (node is not JsonObject obj)
                    continue;
                var id = obj["id"]?.GetValue<string>() ?? "";
                var status = obj["status"]?.GetValue<int>() ?? 0;
                seen.Add(id);
                if (status is >= 200 and < 300)
                {
                    results.Add(new BatchItemResult(id, true, status, null, null));
                    continue;
                }

                if (status == 429 && retryAfter is null && obj["headers"] is JsonObject headers)
                {
                    retryAfter = headers["Retry-After"]?.GetValue<string>()
                        ?? headers["retry-after"]?.GetValue<string>();
                }
                var errorBody = obj["body"];
                var message = errorBody?["error"]?["message"]?.GetValue<string>() ?? $"HTTP {status}";
                results.Add(new BatchItemResult(id, false, status, message, errorBody?.DeepClone()));
            }
        }

        // Anything the envelope did not answer for is a failure (defensive; never retried).
        foreach (var (itemId, _) in items)
        {
            if (!seen.Contains(itemId))
                results.Add(new BatchItemResult(itemId, false, 0, "missing from $batch response", null));
        }
        return (results, retryAfter);
    }
}
