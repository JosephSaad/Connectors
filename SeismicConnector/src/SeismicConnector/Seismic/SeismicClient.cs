// Seismic/SeismicClient.cs
// ------------------------
// REST client for the Seismic platform (https://api.seismic.com).
//
// Authentication: OAuth2 client-credentials against the tenant token endpoint
// (https://auth.seismic.com/tenants/{tenant}/connect/token). The token is
// cached and refreshed 60s before expiry.
//
// Endpoints used (integration API v2):
//   GET  /integration/v2/teamsites
//   GET  /integration/v2/teamsites/{id}/contents?modifiedAtStartTime=...
//   GET  /integration/v2/teamsites/{tsId}/contents/{id}
//   GET  /integration/v2/teamsites/{tsId}/contents/{id}/versions
//   GET  /integration/v2/teamsites/{tsId}/contents/{id}/download
//   GET  /integration/v2/users, /integration/v2/groups,
//        /integration/v2/groups/{id}/members
//
// Retry: 429 and 5xx are retried up to MaxRetries. A server-provided
// Retry-After is honoured exactly; computed exponential backoff is used
// otherwise (base 2s, capped at 60s). Tests inject an HttpMessageHandler so no
// real network is touched.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SeismicConnector.Config;
using SeismicConnector.Infrastructure;

namespace SeismicConnector.Seismic;

/// <summary>Raised when the Seismic API returns a non-retryable error (or retries are exhausted).</summary>
public sealed class SeismicApiError : Exception
{
    public int StatusCode { get; }

    public SeismicApiError(int statusCode, string message)
        : base($"Seismic API error {statusCode}: {message}")
    {
        StatusCode = statusCode;
    }
}

public class SeismicClient
{
    private static readonly IAppLogger Logger = Logging.GetLogger("seismic_connector.seismic");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly SeismicSettings _settings;
    private readonly HttpClient _http;
    private readonly CircuitBreaker _breaker;
    private readonly object _tokenLock = new();
    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    /// <summary>Test seam: fixed token (skips the OAuth call entirely).</summary>
    internal string? OverrideAccessToken { get; set; }

    /// <summary>Test seam: sleep function for retry backoff.</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } =
        (delay, ct) => Task.Delay(delay, ct);

    /// <summary>The circuit breaker guarding this client's dependency (Seismic API).</summary>
    public CircuitBreaker Breaker => _breaker;

    public SeismicClient(
        SeismicSettings settings, HttpMessageHandler? handler = null, CircuitBreaker? breaker = null)
    {
        _settings = settings;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(120);
        _breaker = breaker ?? CircuitBreakerRegistry.Register(
            new CircuitBreaker(CircuitBreakerRegistry.SeismicName, CircuitBreakerOptions.FromEnv(), critical: true));
    }

    /// <summary>
    /// Classify an exception for the breaker: sustained-outage signals
    /// (5xx / timeout / connection) trip it; 4xx and honored 429 (flow control)
    /// do NOT — the service is up, it just answered with a client error or a
    /// rate limit.
    /// </summary>
    internal static bool IsTrippingFailure(Exception ex) => ex switch
    {
        SeismicApiError api => api.StatusCode >= 500,
        HttpRequestException => true,
        TaskCanceledException => true,  // HttpClient.Timeout (caller-cancel is handled upstream)
        TimeoutException => true,
        _ => false,
    };

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

        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _settings.ClientId,
                ["client_secret"] = _settings.ClientSecret,
                ["scope"] = "seismic.library.view seismic.teamsite.view seismic.user.view",
            }),
        };
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new SeismicApiError((int)response.StatusCode, $"token request failed: {Truncate(body)}");

        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new SeismicApiError(500, "token response missing access_token");
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
        lock (_tokenLock)
        {
            _accessToken = token;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        }
        Logger.Info("Obtained Seismic access token");
        return token;
    }

    // ── transport with retry (behind the circuit breaker) ────────────────────

    /// <summary>
    /// Retry-wrapped send, itself guarded by the circuit breaker: a sustained
    /// outage trips the breaker so subsequent calls fail fast (degraded mode)
    /// instead of grinding through the full retry ladder each time.
    /// </summary>
    internal Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct = default) =>
        _breaker.ExecuteAsync(token => SendWithRetryCoreAsync(requestFactory, token), IsTrippingFailure, ct);

    private async Task<HttpResponseMessage> SendWithRetryCoreAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        using var span = Tracing.StartActivity("seismic.http", System.Diagnostics.ActivityKind.Client);
        for (var attempt = 0; ; attempt++)
        {
            using var request = requestFactory();
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", await GetTokenAsync(ct).ConfigureAwait(false));
            // Propagate W3C trace context so the collector links this call to
            // the crawl trace (no-op when tracing is off).
            Tracing.InjectTraceContext(request);
            if (attempt == 0)
            {
                span?.SetTag("http.method", request.Method.Method);
                span?.SetTag("url.path", request.RequestUri?.AbsolutePath);
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < _settings.MaxRetries)
            {
                var delay = Math.Min(2.0 * Math.Pow(2, attempt), 60.0);
                Logger.Warning(
                    $"Seismic API network error ({ex.Message}) — retrying in {delay:F0}s "
                    + $"(attempt {attempt + 1}/{_settings.MaxRetries})");
                await DelayAsync(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
                continue;
            }

            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
                return response;

            var retryable = status == 429 || status >= 500;
            if (!retryable || attempt >= _settings.MaxRetries)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                response.Dispose();
                throw new SeismicApiError(status, Truncate(body));
            }

            // Server-provided Retry-After is honoured exactly; otherwise
            // exponential backoff capped at 60s.
            double wait;
            var retryAfter = response.Headers.TryGetValues("Retry-After", out var values)
                ? values.FirstOrDefault()
                : null;
            if (retryAfter is not null
                && double.TryParse(retryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                wait = parsed;
            }
            else
            {
                wait = Math.Min(2.0 * Math.Pow(2, attempt), 60.0);
            }
            response.Dispose();
            Logger.Warning(
                $"Seismic API transient error {status} — retrying in {wait:F0}s "
                + $"(attempt {attempt + 1}/{_settings.MaxRetries})");
            await DelayAsync(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, _settings.ApiBaseUrl + path), ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    // ── paged list helper ────────────────────────────────────────────────────
    // Seismic list endpoints page with ?limit=N&offset=M and return either a
    // bare JSON array or {"items":[...]}.

    private async Task<List<T>> GetPagedAsync<T>(string path, int? cap, CancellationToken ct)
    {
        var results = new List<T>();
        var offset = 0;
        var limit = _settings.PageSize;
        while (true)
        {
            var sep = path.Contains('?') ? "&" : "?";
            using var doc = await GetJsonAsync($"{path}{sep}limit={limit}&offset={offset}", ct)
                .ConfigureAwait(false);
            var items = ExtractItems(doc.RootElement);
            var pageCount = 0;
            foreach (var element in items)
            {
                var item = element.Deserialize<T>(JsonOptions);
                if (item is not null)
                {
                    results.Add(item);
                    pageCount++;
                    if (cap is > 0 && results.Count >= cap.Value)
                        return results;
                }
            }
            if (pageCount < limit)
                return results;
            offset += limit;
        }
    }

    private static IEnumerable<JsonElement> ExtractItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().ToList();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var items)
            && items.ValueKind == JsonValueKind.Array)
        {
            return items.EnumerateArray().ToList();
        }
        return Array.Empty<JsonElement>();
    }

    // ── public API surface ───────────────────────────────────────────────────

    public virtual Task<List<SeismicTeamsite>> GetTeamsitesAsync(CancellationToken ct = default) =>
        GetPagedAsync<SeismicTeamsite>("/integration/v2/teamsites", null, ct);

    /// <summary>List content in a teamsite, optionally filtered by modifiedAt ≥ <paramref name="modifiedSince"/>.</summary>
    public virtual Task<List<SeismicContent>> GetContentsAsync(
        string teamsiteId, DateTime? modifiedSince = null, CancellationToken ct = default)
    {
        var path = $"/integration/v2/teamsites/{Uri.EscapeDataString(teamsiteId)}/contents";
        if (modifiedSince is not null)
        {
            path += "?modifiedAtStartTime="
                + Uri.EscapeDataString(modifiedSince.Value.ToUniversalTime()
                    .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }
        var cap = _settings.QueryLimit > 0 ? _settings.QueryLimit : (int?)null;
        return GetPagedAsync<SeismicContent>(path, cap, ct);
    }

    /// <summary>Fetch one content item by id. Returns null on 404.</summary>
    public virtual async Task<SeismicContent?> GetContentAsync(
        string teamsiteId, string contentId, CancellationToken ct = default)
    {
        try
        {
            using var doc = await GetJsonAsync(
                $"/integration/v2/teamsites/{Uri.EscapeDataString(teamsiteId)}/contents/{Uri.EscapeDataString(contentId)}",
                ct).ConfigureAwait(false);
            return doc.RootElement.Deserialize<SeismicContent>(JsonOptions);
        }
        catch (SeismicApiError ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Find a content item by id when the teamsite is unknown (scans teamsites).</summary>
    public virtual async Task<SeismicContent?> FindContentAsync(string contentId, CancellationToken ct = default)
    {
        foreach (var teamsite in await GetTeamsitesAsync(ct).ConfigureAwait(false))
        {
            var content = await GetContentAsync(teamsite.Id, contentId, ct).ConfigureAwait(false);
            if (content is not null)
                return content;
        }
        return null;
    }

    public virtual Task<List<SeismicContentVersion>> GetContentVersionsAsync(
        string teamsiteId, string contentId, CancellationToken ct = default)
    {
        return GetPagedAsync<SeismicContentVersion>(
            $"/integration/v2/teamsites/{Uri.EscapeDataString(teamsiteId)}/contents/{Uri.EscapeDataString(contentId)}/versions",
            null, ct);
    }

    /// <summary>
    /// Download the current published payload, capped at
    /// <see cref="SeismicSettings.MaxExtractBytes"/>. Returns null when the
    /// download fails or the payload exceeds the cap (metadata-only ingest).
    /// </summary>
    public virtual async Task<byte[]?> DownloadContentAsync(
        string teamsiteId, string contentId, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendWithRetryAsync(
                () => new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{_settings.ApiBaseUrl}/integration/v2/teamsites/{Uri.EscapeDataString(teamsiteId)}"
                    + $"/contents/{Uri.EscapeDataString(contentId)}/download"),
                ct).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } length && length > _settings.MaxExtractBytes)
            {
                Logger.Info($"Content {contentId}: payload {length} bytes exceeds extract cap; metadata-only.");
                return null;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > _settings.MaxExtractBytes)
                {
                    Logger.Info($"Content {contentId}: payload exceeds extract cap mid-stream; metadata-only.");
                    return null;
                }
            }
            return buffer.ToArray();
        }
        catch (SeismicApiError ex)
        {
            Logger.Warning($"Content {contentId}: download failed ({ex.Message}); metadata-only ingest.");
            return null;
        }
    }

    /// <summary>
    /// Fetch per-content usage analytics (views/downloads/shares) as a map by
    /// content id. Enrichment is best-effort: ANY failure logs a warning and
    /// returns an empty map — usage signals must never fail a crawl.
    /// </summary>
    public virtual async Task<Dictionary<string, SeismicContentUsage>> GetContentUsageAsync(
        CancellationToken ct = default)
    {
        try
        {
            var rows = await GetPagedAsync<SeismicContentUsage>(
                "/integration/v2/analytics/contentUsage", null, ct).ConfigureAwait(false);
            var map = new Dictionary<string, SeismicContentUsage>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (!string.IsNullOrEmpty(row.ContentId))
                    map[row.ContentId] = row;
            }
            Logger.Info($"Usage analytics: {map.Count} content item(s) with engagement signals.");
            return map;
        }
        catch (Exception ex)
        {
            Logger.Warning($"Usage analytics fetch failed ({ex.Message}) — items indexed without engagement signals.");
            return new Dictionary<string, SeismicContentUsage>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Fetch a LiveDoc's field/variable/component metadata (the personalization
    /// inputs it exposes). Best-effort: ANY failure logs a warning and returns
    /// an empty list — LiveDoc enrichment must never fail a crawl. Callers gate
    /// this so it is only ever invoked for LiveDoc content.
    /// </summary>
    public virtual async Task<List<SeismicLiveDocField>> GetLiveDocFieldsAsync(
        string teamsiteId, string contentId, CancellationToken ct = default)
    {
        try
        {
            var fields = await GetPagedAsync<SeismicLiveDocField>(
                $"/integration/v2/teamsites/{Uri.EscapeDataString(teamsiteId)}"
                + $"/contents/{Uri.EscapeDataString(contentId)}/livedoc/fields",
                null, ct).ConfigureAwait(false);
            // Drop entries that carry neither a name nor a label (nothing to index).
            return fields.Where(f => !string.IsNullOrWhiteSpace(f.DisplayName)).ToList();
        }
        catch (Exception ex)
        {
            Logger.Warning(
                $"LiveDoc field fetch failed for {contentId} ({ex.Message}) — indexed without field metadata.");
            return new List<SeismicLiveDocField>();
        }
    }

    public virtual Task<List<SeismicUser>> GetUsersAsync(CancellationToken ct = default) =>
        GetPagedAsync<SeismicUser>("/integration/v2/users", null, ct);

    public virtual Task<List<SeismicGroup>> GetGroupsAsync(CancellationToken ct = default) =>
        GetPagedAsync<SeismicGroup>("/integration/v2/groups", null, ct);

    public virtual Task<List<SeismicUser>> GetGroupMembersAsync(string groupId, CancellationToken ct = default) =>
        GetPagedAsync<SeismicUser>(
            $"/integration/v2/groups/{Uri.EscapeDataString(groupId)}/members", null, ct);

    /// <summary>Cheap connectivity probe for validate-config (lists one teamsite page).</summary>
    public virtual async Task<bool> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = await GetJsonAsync("/integration/v2/teamsites?limit=1&offset=0", ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning($"Seismic connectivity probe failed: {ex.Message}");
            return false;
        }
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500] + "...";
}
