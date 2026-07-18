// Graph/GraphClient.cs
// --------------------
// Minimal Microsoft Graph client for the external connections API.
//
//   • Client-credentials token flow against AAD_APP_TENANT_ID (token cached
//     until 5 minutes before expiry). Sovereign clouds are fully supported:
//     GRAPH_BASE_URL moves the resource endpoint (e.g. https://graph.microsoft.us),
//     GRAPH_SCOPE overrides the token audience (default "{GRAPH_BASE_URL}/.default"),
//     GRAPH_API_VERSION picks v1.0/beta, and AAD_APP_OAUTH_AUTHORITY_HOST moves
//     the token authority (default https://login.microsoftonline.com).
//   • SendWithRetryAsync (429-hardened, mirroring the Salesforce connector):
//     retries transient statuses {429, 500, 502, 503, 504} up to
//     GRAPH_MAX_RETRIES. A NUMERIC server Retry-After is honoured (unparseable
//     values fall back to computed backoff); computed backoff is
//     GRAPH_RETRY_BACKOFF_BASE * 2^attempt with optional ±20% jitter
//     (GRAPH_RETRY_JITTER=true). EVERY wait — server or computed — is clamped
//     to 60 s, with a warning when the clamp fires. See docs/RETRY.md.
//   • Test seams: injectable HttpMessageHandler, delay function and token.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ClarizenConnector.Config;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Graph;

public sealed class GraphResponse
{
    public required HttpStatusCode StatusCode { get; init; }
    public JsonNode? Body { get; init; }
    public string RawBody { get; init; } = string.Empty;
    public HttpResponseHeaders? Headers { get; init; }

    /// <summary>True when the request was NOT sent because the Graph circuit is
    /// open (fail-fast). Callers treat this as "degraded", not a real failure —
    /// the item is neither ingested nor dead-lettered.</summary>
    public bool CircuitOpen { get; init; }

    public bool IsSuccess => (int)StatusCode is >= 200 and < 300;
}

public class GraphClient
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector.graph");

    /// <summary>Transient HTTP statuses worth retrying (matches the SF connector exactly).</summary>
    internal static readonly HashSet<int> RetryableStatusCodes = new() { 429, 500, 502, 503, 504 };

    private readonly AppConfig _config;
    private readonly HttpClient _http;
    private readonly CircuitBreaker _breaker;

    private string? _token;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    /// <summary>Cached signing certificate (resolved once per client lifetime).</summary>
    private System.Security.Cryptography.X509Certificates.X509Certificate2? _clientCertificate;
    private bool _authModeLogged;

    /// <summary>Async delay seam so tests never really sleep.</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } =
        (delay, ct) => Task.Delay(delay, ct);

    /// <summary>Test seam: preset token (skips the token endpoint entirely).</summary>
    internal string? OverrideToken { get; set; }

    public GraphClient(AppConfig config, HttpMessageHandler? handler = null, CircuitBreaker? breaker = null)
    {
        _config = config;
        // Null → passthrough (disabled) breaker, so the many test fixtures that
        // construct a GraphClient directly are byte-identical to before.
        _breaker = breaker ?? CircuitBreaker.Disabled;
        // Injected handlers (tests) bypass the factory; production clients get
        // PROXY_URL / PROXY_BYPASS / CA_BUNDLE_PATH support (docs/DEPLOYMENT_ENTERPRISE.md).
        _http = handler is null
            ? new HttpClient(HttpClientFactory.CreateHandler()) { Timeout = TimeSpan.FromSeconds(120) }
            : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
    }

    /// <summary>Versioned Graph base: {GRAPH_BASE_URL}/{GRAPH_API_VERSION}.</summary>
    public string BaseUrl => $"{_config.GraphBaseUrl}/{_config.GraphApiVersion}";

    /// <summary>Token audience: GRAPH_SCOPE, or "{GRAPH_BASE_URL}/.default" (sovereign-safe).</summary>
    public string TokenScope =>
        string.IsNullOrWhiteSpace(_config.GraphScope)
            ? $"{_config.GraphBaseUrl}/.default"
            : _config.GraphScope!;

    /// <summary>Token endpoint: {AAD_APP_OAUTH_AUTHORITY_HOST}/{tenant}/oauth2/v2.0/token.</summary>
    public string TokenEndpoint =>
        $"{_config.AadAuthorityHost}/{_config.AadTenantId}/oauth2/v2.0/token";

    // ── Token ────────────────────────────────────────────────────────────────

    internal async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        if (OverrideToken is not null)
            return OverrideToken;
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiresAt)
            return _token;

        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiresAt)
                return _token;

            // Certificate credential (GRAPH_CLIENT_CERT_PATH / _THUMBPRINT)
            // wins over the client secret when both are configured. The mode is
            // logged once; key material never is.
            var form = new Dictionary<string, string>
            {
                ["client_id"] = _config.AadClientId,
                ["scope"] = TokenScope,
                ["grant_type"] = "client_credentials",
            };
            if (_config.UseGraphCertificate)
            {
                _clientCertificate ??= ClientAssertion.Resolve(_config);
                form["client_assertion_type"] = ClientAssertion.AssertionType;
                form["client_assertion"] = ClientAssertion.Build(
                    _clientCertificate, _config.AadClientId, TokenEndpoint);
                LogAuthModeOnce(
                    !string.IsNullOrWhiteSpace(_config.GraphClientCertPath)
                        ? "certificate (GRAPH_CLIENT_CERT_PATH)"
                        : "certificate (GRAPH_CLIENT_CERT_THUMBPRINT, Windows store)");
            }
            else
            {
                form["client_secret"] = _config.AadClientSecret
                    ?? throw new InvalidOperationException(
                        "Invalid configuration: Missing SECRET_AAD_APP_CLIENT_SECRET "
                        + "(and no certificate credential is configured).");
                LogAuthModeOnce("client secret (SECRET_AAD_APP_CLIENT_SECRET)");
            }

            using var content = new FormUrlEncodedContent(form);
            using var response = await _http.PostAsync(TokenEndpoint, content, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // A 5xx token endpoint is a real Graph/AAD outage; count it.
                // 4xx here is a config/credential error, not an outage.
                if ((int)response.StatusCode >= 500)
                    _breaker.OnFailure();
                throw new InvalidOperationException(
                    $"Failed to acquire Graph token (HTTP {(int)response.StatusCode}): {Truncate(body)}");
            }
            var json = JsonNode.Parse(body)!.AsObject();
            _token = json["access_token"]!.GetValue<string>();
            var expiresIn = json.TryGetPropertyValue("expires_in", out var exp) && exp is not null
                ? exp.GetValue<int>()
                : 3600;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 300));
            return _token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    // ── Requests ─────────────────────────────────────────────────────────────

    public Task<GraphResponse> GetAsync(string path, CancellationToken ct = default) =>
        SendWithRetryAsync(HttpMethod.Get, path, null, ct);

    public Task<GraphResponse> PostAsync(string path, JsonNode body, CancellationToken ct = default) =>
        SendWithRetryAsync(HttpMethod.Post, path, body, ct);

    public Task<GraphResponse> PatchAsync(string path, JsonNode body, CancellationToken ct = default) =>
        SendWithRetryAsync(HttpMethod.Patch, path, body, ct);

    public Task<GraphResponse> PutAsync(string path, JsonNode body, CancellationToken ct = default) =>
        SendWithRetryAsync(HttpMethod.Put, path, body, ct);

    public Task<GraphResponse> DeleteAsync(string path, CancellationToken ct = default) =>
        SendWithRetryAsync(HttpMethod.Delete, path, null, ct);

    /// <summary>
    /// Send a request with throttling/transient retry. <paramref name="path"/>
    /// may be absolute or relative to {GRAPH_BASE_URL}/{version}/.
    /// </summary>
    public virtual async Task<GraphResponse> SendWithRetryAsync(
        HttpMethod method, string path, JsonNode? body, CancellationToken ct = default)
    {
        var url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? path
            : $"{BaseUrl}/{path.TrimStart('/')}";

        // Fail fast during a sustained Graph outage: when the breaker is open we
        // do not even attempt the call (no timeout wait, no hammering). The
        // caller sees CircuitOpen and treats it as degraded, not a failure.
        if (!_breaker.TryAcquire())
        {
            return new GraphResponse
            {
                StatusCode = HttpStatusCode.ServiceUnavailable,
                RawBody = "circuit open (Graph); failing fast",
                CircuitOpen = true,
            };
        }

        // Once TryAcquire() has (in HalfOpen) taken a probe slot, EVERY exit path
        // below must settle that slot exactly once via OnSuccess/OnFailure/
        // OnIgnored — otherwise a HalfOpen probe leaks its slot and, after
        // HalfOpenTrials leaks, the breaker admits no more probes and wedges
        // forever (it can neither close on a success nor re-open on a failure).
        // This mirrors ClarizenClient.SendRawAsync: the terminal classification
        // sets `settled`; the finally only releases (as IGNORED — flow control,
        // not an outage) when NO terminal outcome was recorded, which covers the
        // otherwise-uncaught escapes: a cancellation during a retry wait or the
        // token fetch, and a non-5xx token-fetch error (InvalidOperationException
        // from GetTokenAsync). A 5xx token error already settles via OnFailure
        // inside GetTokenAsync (which trips to Open, making the finally a no-op).
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
                using var request = new HttpRequestMessage(method, url);
                // Token fetched per attempt so a long retry ladder never rides an
                // expired token (refresh is cheap — cached until 5 min pre-expiry).
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", await GetTokenAsync(ct).ConfigureAwait(false));
                if (body is not null)
                {
                    request.Content = new StringContent(
                        body.ToJsonString(), Encoding.UTF8, "application/json");
                }

                HttpResponseMessage response;
                string raw;
                try
                {
                    response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                    raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
                catch (HttpRequestException exc) when (attempt < _config.GraphMaxRetries)
                {
                    var delaySeconds = RetryDelay.ForAttempt(_config.GraphRetryBackoffBase, attempt, null);
                    Logger.Warning(
                        $"Graph request {method} {url} failed ({exc.Message}); "
                        + $"retry {attempt + 1}/{_config.GraphMaxRetries} in {delaySeconds:0.##}s");
                    await DelayAsync(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
                    continue;
                }
                catch (Exception)
                {
                    // Retries exhausted on a network/transport failure — a REAL
                    // outage signal for the breaker before the exception propagates.
                    Settle(_breaker.OnFailure);
                    throw;
                }

                using (response)
                {
                    var status = (int)response.StatusCode;
                    if (status == 429)
                        Metrics.IncThrottle429();

                    if (RetryableStatusCodes.Contains(status) && attempt < _config.GraphMaxRetries)
                    {
                        var retryAfter = ParseRetryAfter(response.Headers);
                        var delaySeconds = RetryDelay.ForAttempt(
                            _config.GraphRetryBackoffBase, attempt, retryAfter, out var clamped);
                        if (clamped)
                        {
                            Logger.Warning(string.Format(
                                CultureInfo.InvariantCulture,
                                "Retry-After of {0:F0}s exceeds cap; clamping to {1:F0}s",
                                retryAfter ?? 0, RetryDelay.MaxRetryWaitSeconds));
                        }
                        Logger.Warning(
                            $"Graph API transient error {status} for {method} {url} — "
                            + $"retrying in {delaySeconds:0.##}s (attempt {attempt + 1}/{_config.GraphMaxRetries})"
                            + (retryAfter is not null && !clamped ? " (server Retry-After)" : string.Empty));
                        await DelayAsync(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
                        continue;
                    }

                    // Terminal outcome — classify for the breaker. 5xx = real outage;
                    // 429 (flow control) and 4xx (client/validation) are ignored;
                    // 2xx/3xx clear the failure window.
                    Settle(() => RecordBreakerOutcome(status));

                    JsonNode? parsed = null;
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        try
                        {
                            parsed = JsonNode.Parse(raw);
                        }
                        catch
                        {
                            // non-JSON body (e.g. 202 Accepted empty, HTML error page)
                        }
                    }
                    return new GraphResponse
                    {
                        StatusCode = response.StatusCode,
                        Body = parsed,
                        RawBody = raw,
                        Headers = response.Headers,
                    };
                }
            }
        }
        finally
        {
            // Any escape without a terminal classification — cancellation during a
            // retry wait or the token fetch, or a non-5xx token-fetch error —
            // releases the probe slot as an IGNORED outcome so a HalfOpen probe
            // can never be leaked and the breaker can never wedge.
            Settle(_breaker.OnIgnored);
        }
    }

    /// <summary>Log the active Graph credential mode once per client — the mode
    /// and its SOURCE setting only, never key material.</summary>
    private void LogAuthModeOnce(string mode)
    {
        if (_authModeLogged)
            return;
        _authModeLogged = true;
        Logger.Info($"Graph auth mode: {mode}.");
    }

    /// <summary>Classify a terminal HTTP status for the Graph breaker.</summary>
    private void RecordBreakerOutcome(int status)
    {
        if (status >= 500)
            _breaker.OnFailure();          // real outage
        else if (status == 429 || status is >= 400 and < 500)
            _breaker.OnIgnored();          // flow control / client error — not an outage
        else
            _breaker.OnSuccess();          // 2xx/3xx
    }

    /// <summary>
    /// Parse Retry-After as NUMERIC delta-seconds only (matching the SF
    /// hardening): an HTTP-date or garbage value returns null so the caller
    /// falls back to computed backoff instead of trusting an unbounded date.
    /// </summary>
    internal static double? ParseRetryAfter(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Retry-After", out var values))
            return null;
        var rawValue = values.FirstOrDefault();
        if (rawValue is null)
            return null;
        return double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? Math.Max(0, seconds)
            : null;
    }

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500] + "...";
}
