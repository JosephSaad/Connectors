// Altrata/ApiClient.cs
// --------------------
// Altrata REST API enrichment client (secondary ingestion path, used by
// `ingest-item --id`). OAuth2 client-credentials against ALTRATA_TOKEN_URL,
// profile lookups against ALTRATA_API_BASE_URL.
//
//   * Client-side throttle: ALTRATA_API_CALLS_PER_MINUTE (sliding window) —
//     the vendor rate limit is never the first line of defence.
//   * Cost tracking: every successful lookup is billable; a lifetime counter
//     is persisted in the state store and surfaced on /metrics as
//     altrata_api_billable_lookups_total.
//   * Every lookup is audit-logged with actor + purpose (see AuditLog).

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using AltrataConnector.Config;
using AltrataConnector.Infrastructure;
using AltrataConnector.State;

namespace AltrataConnector.Altrata;

/// <summary>Sliding-window rate limiter (client-side vendor throttle).</summary>
public sealed class RateLimiter
{
    private readonly int _callsPerMinute;
    private readonly Queue<DateTime> _window = new();
    private readonly object _sync = new();

    public RateLimiter(int callsPerMinute) =>
        _callsPerMinute = Math.Max(1, callsPerMinute);

    /// <summary>
    /// Register an intended call at <paramref name="utcNow"/>; returns how long
    /// the caller must wait before actually making it (zero when under the limit).
    /// </summary>
    public TimeSpan ReserveDelay(DateTime utcNow)
    {
        lock (_sync)
        {
            while (_window.Count > 0 && utcNow - _window.Peek() >= TimeSpan.FromMinutes(1))
                _window.Dequeue();

            if (_window.Count < _callsPerMinute)
            {
                _window.Enqueue(utcNow);
                return TimeSpan.Zero;
            }

            var oldest = _window.Peek();
            var wait = oldest + TimeSpan.FromMinutes(1) - utcNow;
            if (wait < TimeSpan.Zero)
                wait = TimeSpan.Zero;
            _window.Dequeue();
            _window.Enqueue(utcNow + wait);
            return wait;
        }
    }
}

public sealed class AltrataApiException : Exception
{
    public int StatusCode { get; }
    public AltrataApiException(int statusCode, string message) : base(message) =>
        StatusCode = statusCode;
}

public sealed class AltrataApiClient
{
    private static readonly IAppLogger Logger = Logging.GetLogger("altrata_connector.api");

    private readonly AppConfig _config;
    private readonly HttpClient _http;
    private readonly IStateStore _state;
    private readonly IAuditLog _audit;
    private readonly RateLimiter _rateLimiter;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTime> _clock;
    private readonly CircuitBreaker _breaker;

    private string? _token;
    private DateTime _tokenExpiresUtc;

    public CircuitState BreakerState => _breaker.State;
    public BreakerSnapshot BreakerSnapshot => _breaker.Snapshot();

    public AltrataApiClient(AppConfig config, IStateStore state, IAuditLog audit,
        HttpMessageHandler? handler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTime>? clock = null,
        CircuitBreaker? breaker = null)
    {
        _config = config;
        _state = state;
        _audit = audit;
        // Injected handler = tests; otherwise the enterprise connectivity
        // handler (PROXY_URL / PROXY_BYPASS / CA_BUNDLE_PATH).
        _http = handler == null
            ? new HttpClient(HttpConnectivity.CreateHandler())
            : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(60);
        _rateLimiter = new RateLimiter(config.AltrataApiCallsPerMinute);
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
        _clock = clock ?? (() => DateTime.UtcNow);
        _breaker = breaker ?? new CircuitBreaker("altrata-api", CircuitBreakerOptions.FromEnv(critical: false));
    }

    /// <summary>Send through the enrichment-API breaker: 5xx / timeout / connection
    /// trips; 4xx (responding) and graceful-stop cancellation do not.</summary>
    private Task<HttpResponseMessage> BreakeredSendAsync(
        Func<HttpRequestMessage> factory, CancellationToken ct) =>
        _breaker.ExecuteAsync(
            () => _http.SendAsync(factory(), ct),
            isFailureResult: r => (int)r.StatusCode >= 500,
            isTripException: ex => HttpTripPolicy.IsTrip(ex, ct));

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token != null && _clock() < _tokenExpiresUtc - TimeSpan.FromMinutes(5))
            return _token;

        using var response = await BreakeredSendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _config.AltrataTokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = _config.AltrataClientId!,
                    ["client_secret"] = _config.AltrataClientSecret!,
                    ["grant_type"] = "client_credentials",
                }),
            };
            Telemetry.InjectTraceContext(request);
            return request;
        }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new AltrataApiException((int)response.StatusCode,
                $"Altrata token request failed ({(int)response.StatusCode})");

        using var doc = JsonDocument.Parse(body);
        _token = doc.RootElement.GetProperty("access_token").GetString()
                 ?? throw new AltrataApiException(500, "Altrata token response had no access_token");
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        _tokenExpiresUtc = _clock().AddSeconds(expiresIn);
        return _token;
    }

    /// <summary>
    /// On-demand person profile lookup. Throttled, billable (persisted counter)
    /// and audit-logged with the caller's purpose string.
    /// </summary>
    public async Task<FeedRecord> LookupPersonAsync(string altrataId, string actor, string purpose,
        CancellationToken ct = default)
    {
        if (!_config.ApiConfigured)
            throw new InvalidOperationException(
                "Altrata API not configured: set ALTRATA_API_BASE_URL, ALTRATA_TOKEN_URL, " +
                "ALTRATA_CLIENT_ID and SECRET_ALTRATA_CLIENT_SECRET.");

        // Span the billable enrichment lookup. PII CAUTION: tag only the opaque
        // subject id — never the profile response (name/email/wealth).
        using var span = Telemetry.Span("api.lookup", ActivityKind.Client);
        Telemetry.SetTag(span, "altrata.subject.id", altrataId);

        var wait = _rateLimiter.ReserveDelay(_clock());
        if (wait > TimeSpan.Zero)
        {
            Logger.Info($"Rate limit: waiting {wait.TotalSeconds:0.#}s before Altrata API call " +
                        $"({_config.AltrataApiCallsPerMinute}/min)");
            await _delay(wait, ct);
        }

        var url = $"{_config.AltrataApiBaseUrl!.TrimEnd('/')}/persons/{Uri.EscapeDataString(altrataId)}";
        var token = await GetTokenAsync(ct);
        using var response = await BreakeredSendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Telemetry.InjectTraceContext(request);
            return request;
        }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new AltrataApiException((int)response.StatusCode,
                $"Altrata person lookup for '{altrataId}' failed ({(int)response.StatusCode})");

        // Successful lookup ⇒ billable: bump the persisted counter and audit it.
        var total = _state.IncrementBillableLookups();
        Metrics.Set("altrata_api_billable_lookups_total", total);
        Telemetry.SetTag(span, "altrata.api.billable_total", total);
        _audit.Append(new AuditEntry
        {
            Actor = actor,
            Action = "api_lookup",
            AltrataId = altrataId,
            Purpose = purpose,
            Billable = true,
        });
        Logger.Info($"Billable Altrata lookup #{total}: person {altrataId} (purpose: {purpose})");

        using var doc = JsonDocument.Parse(body);
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            fields[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => property.Value.GetString(),
                _ => property.Value.GetRawText(),
            };
        }
        if (!fields.ContainsKey("id"))
            fields["id"] = altrataId;
        return new FeedRecord { Dataset = Datasets.PersonProfile, Fields = fields };
    }

    /// <summary>Best-effort token check for validate-config.</summary>
    public async Task<bool> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            await GetTokenAsync(ct);
            return true;
        }
        catch (Exception exc)
        {
            Logger.Warning($"Altrata API probe failed: {exc.Message}");
            return false;
        }
    }
}
