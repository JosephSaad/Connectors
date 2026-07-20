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
    private readonly IPurposePolicy _purpose;
    private readonly IUsageBudget _usage;
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
        CircuitBreaker? breaker = null,
        IPurposePolicy? purpose = null,
        IUsageBudget? usage = null)
    {
        _config = config;
        _state = state;
        _audit = audit;
        // Purpose-based authorization (#7). Null → read PURPOSE_ALLOWLIST from
        // the environment (unset = record-only, enforcement off).
        _purpose = purpose ?? PurposePolicy.FromEnv();
        // Enforceable usage ceilings (WP-AL-4). Null → whatever AppConfig parsed
        // from ALTRATA_MAX_LOOKUPS_PER_DAY / _PER_WINDOW; both unset = no ceiling.
        _usage = usage ?? new UsageMeter(state, UsageBudgetOptions.FromConfig(config));
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

        // Purpose-based authorization / per-action veto (#7) — BEFORE any
        // billable/sensitive work. When PURPOSE_ALLOWLIST is configured a
        // disallowed purpose is DENIED fail-closed and audited; when unset the
        // purpose is recorded only (back-compat) and the off posture is logged.
        var decision = _purpose.Evaluate(actor, purpose);
        if (_purpose.Enforcing && !decision.Allowed)
        {
            _audit.Append(new AuditEntry
            {
                Actor = actor,
                Action = "api_lookup",
                AltrataId = altrataId,
                Purpose = purpose,
                Billable = false,
                Decision = "deny",
            });
            Metrics.Increment("altrata_purpose_denied_total");
            Logger.Warning($"Purpose '{purpose}' by actor '{actor}' DENIED for lookup {altrataId} " +
                           $"({decision.Reason}) — not in PURPOSE_ALLOWLIST; fail-closed, no billable call made.");
            throw new PurposeDeniedException(
                $"Purpose '{purpose}' is not permitted (actor '{actor}', {decision.Reason}). " +
                "Set PURPOSE_ALLOWLIST to include it, or use an approved purpose.");
        }
        if (!_purpose.Enforcing)
            Logger.Info("Purpose enforcement OFF (PURPOSE_ALLOWLIST unset) — recording purpose only.");

        // Enforceable usage ceiling (WP-AL-4) — AFTER the purpose veto and BEFORE
        // the rate limiter, the token fetch, the HTTP call and the billable
        // counter. The order is load-bearing in BOTH directions:
        //   * after the veto, so a disallowed purpose never consumes budget —
        //     otherwise anyone who can invoke the connector could exhaust the
        //     day's ceiling with calls that were never going to be permitted,
        //     denying service to the legitimate workload;
        //   * before everything else, so a refusal costs nothing at all: no
        //     token, no request, no bill.
        // The rate limiter only makes a caller WAIT; this is the thing that
        // actually DECLINES. Modelled on the purpose deny above: PII-safe audit
        // entry, dedicated metric, typed exception, fail-closed.
        var budget = _usage.TryReserve(_clock());
        if (!budget.Allowed)
        {
            _audit.Append(new AuditEntry
            {
                Actor = actor,
                Action = "api_lookup",
                AltrataId = altrataId,
                Purpose = purpose,
                Billable = false,
                Decision = "deny",
            });
            Metrics.Increment("altrata_usage_denied_total");
            Logger.Warning(
                $"Usage ceiling reached ({budget.Reason}: {budget.Used}/{budget.Limit}) — lookup " +
                $"{altrataId} REFUSED fail-closed; no billable call made, no request sent.");
            throw new UsageBudgetExceededException(
                $"Altrata usage ceiling reached ({budget.Reason}): {budget.Used} of {budget.Limit} " +
                $"billable lookup(s) already used. The lookup was refused and nothing was billed. " +
                $"Raise {UsageBudgetOptions.MaxPerDayEnvVar} / {UsageBudgetOptions.MaxPerWindowEnvVar}, " +
                "or wait for the window to roll over.");
        }
        if (!_usage.Enforcing)
            Logger.Info(
                $"Usage ceilings OFF ({UsageBudgetOptions.MaxPerDayEnvVar} / " +
                $"{UsageBudgetOptions.MaxPerWindowEnvVar} unset) — billable lookups are counted but never refused.");

        // From here on the reservation is held. Anything that prevents the lookup
        // from becoming BILLABLE must give it back, or a flapping upstream would
        // burn the allowance without producing a single usable result.
        var billed = false;
        try
        {
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

            // Successful lookup ⇒ billable. The flag is set HERE — the instant
            // the 2xx is in hand — and deliberately BEFORE the bookkeeping that
            // records it.
            //
            // It used to be set after IncrementBillableLookups returned, which
            // made the accounting optimistic in the one direction that must
            // never be optimistic: if that state write failed (read-only state
            // file, full volume) on an otherwise successful call, 'billed' was
            // still false, so the exception filter below handed the reservation
            // BACK. The vendor had served the lookup and would invoice it, yet
            // the ceiling forgot it — and a host whose state file had gone
            // read-only could walk straight past its limit, one uncounted
            // billable call at a time.
            //
            // Reserved-and-kept is the conservative side of that trade: at
            // worst the ceiling counts a call that somehow was not billed and
            // refuses marginally early, which is recoverable. Over-spending
            // against a licensed source is not.
            billed = true;
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
                Decision = "allow",
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
        catch when (ReleaseUnlessBilled(billed, budget.Reservation))
        {
            // ReleaseUnlessBilled always returns false, so this filter never
            // catches — it only runs the release as a side effect, leaving the
            // original exception and its stack trace completely untouched.
            throw;
        }
    }

    /// <summary>Exception-filter side effect: hand a reservation back when the
    /// lookup did not become billable. ALWAYS returns false so the filter never
    /// matches and no exception is ever swallowed or re-thrown.</summary>
    private bool ReleaseUnlessBilled(bool billed, UsageReservation? reservation)
    {
        if (!billed && reservation != null)
        {
            try
            {
                _usage.Release(reservation);
            }
            catch (Exception exc)
            {
                // Never let bookkeeping mask the real failure. A lost release is
                // conservative (the reservation stays consumed until the window
                // rolls) and must not become the exception the caller sees.
                Logger.Warning($"Could not release the usage reservation after a failed lookup: {exc.Message}");
            }
        }
        return false;
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
