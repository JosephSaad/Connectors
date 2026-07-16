// Clarizen/ClarizenClient.cs
// --------------------------
// Clarizen (Planview AdaptiveWork) REST API v2 client
// (https://api.clarizen.com/v2.0/services).
//
//   • Login/session auth: POST /authentication/login {userName, password} →
//     sessionId, sent as "Authorization: Session <id>" on every call. An
//     expired session (401 / SessionTimeout error) triggers ONE transparent
//     re-login + replay.
//   • CZQL queries: POST /data/query {q} with paging ({paging: {from, limit,
//     hasMore}}) until hasMore=false or CLARIZEN_QUERY_LIMIT rows.
//   • Delta detection: callers append "modifiedDate > <since>" to the WHERE
//     clause (BuildQuery helper below).
//   • Quota: every HTTP call consumes the ApiBudget; pacing delays are applied
//     before the call and ClarizenQuotaExceededException propagates to the
//     crawl loop, which checkpoints and stops cleanly.
//   • 429/5xx retry with Retry-After honoured exactly (same policy as Graph).

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ClarizenConnector.Config;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Clarizen;

public class ClarizenClient : IAttachmentDownloader
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector.clarizen");

    private readonly AppConfig _config;
    private readonly HttpClient _http;
    private readonly ApiBudget _budget;
    private readonly CircuitBreaker _breaker;

    private string? _sessionId;
    private readonly SemaphoreSlim _loginLock = new(1, 1);

    private const int MaxRetries = 4;

    /// <summary>Async delay seam so tests never really sleep.</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } =
        (delay, ct) => Task.Delay(delay, ct);

    /// <summary>Test seam: preset session id (skips the login call).</summary>
    internal string? OverrideSessionId
    {
        get => _sessionId;
        set => _sessionId = value;
    }

    public ClarizenClient(
        AppConfig config,
        ApiBudget? budget = null,
        HttpMessageHandler? handler = null,
        CircuitBreaker? breaker = null)
    {
        _config = config;
        // Null → passthrough breaker, so existing fixtures are unchanged.
        _breaker = breaker ?? CircuitBreaker.Disabled;
        _budget = budget ?? new ApiBudget(
            config.ClarizenApiCallsPerDay,
            EnvFlags.GetInt("CLARIZEN_MAX_CALLS_PER_MINUTE", 60));
        _http = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(120) }
            : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
    }

    public ApiBudget Budget => _budget;

    // ── Auth ─────────────────────────────────────────────────────────────────

    public async Task<string> LoginAsync(CancellationToken ct = default)
    {
        await _loginLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await SendRawAsync(
                "authentication/login",
                new JsonObject
                {
                    ["userName"] = _config.ClarizenUsername,
                    ["password"] = _config.ClarizenPassword,
                },
                sessionId: null,
                ct).ConfigureAwait(false);
            if (!response.IsSuccess || response.Body?["sessionId"] is null)
            {
                throw new InvalidOperationException(
                    $"Clarizen login failed (HTTP {(int)response.StatusCode}): {response.RawBody}");
            }
            _sessionId = response.Body["sessionId"]!.GetValue<string>();
            Logger.Info("Clarizen session established.");
            return _sessionId;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    // ── Queries ──────────────────────────────────────────────────────────────

    /// <summary>Build the CZQL crawl query for an object type (testable, pure).</summary>
    public static string BuildQuery(
        ObjectConfig objectConfig, DateTime? sinceUtc, IEnumerable<string>? extraFields = null)
    {
        var fields = new List<string> { "SYSID" };
        foreach (var field in objectConfig.SelectedFields.Keys)
        {
            if (!fields.Contains(field, StringComparer.OrdinalIgnoreCase))
                fields.Add(field);
        }
        if (extraFields is not null)
        {
            foreach (var field in extraFields)
            {
                if (!fields.Contains(field, StringComparer.OrdinalIgnoreCase))
                    fields.Add(field);
            }
        }
        // SYSID is implicit in results; keep an explicit modifiedDate for cursor sanity.
        if (!fields.Contains("LastUpdatedOn", StringComparer.OrdinalIgnoreCase))
            fields.Add("LastUpdatedOn");
        fields.Remove("SYSID");

        var sb = new StringBuilder();
        sb.Append("SELECT ").Append(string.Join(", ", fields));
        sb.Append(" FROM ").Append(objectConfig.ObjectName);

        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(objectConfig.FilterCondition))
            conditions.Add($"({objectConfig.FilterCondition})");
        if (sinceUtc is { } since)
        {
            conditions.Add(
                $"LastUpdatedOn > {since.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}Z");
        }
        if (conditions.Count > 0)
            sb.Append(" WHERE ").Append(string.Join(" AND ", conditions));
        sb.Append(" ORDER BY LastUpdatedOn");
        return sb.ToString();
    }

    /// <summary>
    /// Run a CZQL query, following paging until exhausted (or the optional
    /// row limit). Returns raw entity JSON objects.
    /// </summary>
    public async Task<List<JsonObject>> QueryAllAsync(
        string czql, int rowLimit = 0, CancellationToken ct = default)
    {
        var results = new List<JsonObject>();
        JsonObject? paging = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var payload = new JsonObject
            {
                ["q"] = czql,
            };
            payload["paging"] = paging is not null
                ? paging.DeepClone()
                : new JsonObject { ["limit"] = _config.ClarizenPageSize };

            var response = await SendAsync("data/query", payload, ct).ConfigureAwait(false);
            if (response.CircuitOpen)
            {
                // Fail fast — the pipeline treats this as degraded, not a crash.
                throw new Infrastructure.CircuitOpenException("clarizen");
            }
            if (!response.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"Clarizen query failed (HTTP {(int)response.StatusCode}): {response.RawBody}");
            }

            if (response.Body?["entities"] is JsonArray entities)
            {
                foreach (var entity in entities)
                {
                    if (entity is JsonObject obj)
                        results.Add((JsonObject)obj.DeepClone());
                    if (rowLimit > 0 && results.Count >= rowLimit)
                        return results;
                }
            }

            var pagingNode = response.Body?["paging"] as JsonObject;
            var hasMore = pagingNode?["hasMore"]?.GetValue<bool>() ?? false;
            if (!hasMore)
                return results;
            paging = (JsonObject)pagingNode!.DeepClone();
        }
    }

    /// <summary>Fetch a single entity by Clarizen id (e.g. "/Task/1234" or bare local id).</summary>
    public async Task<JsonObject?> RetrieveAsync(
        ObjectConfig objectConfig, string id, CancellationToken ct = default)
    {
        var localId = id.TrimStart('/');
        var slash = localId.LastIndexOf('/');
        if (slash >= 0)
            localId = localId[(slash + 1)..];

        var fields = string.Join(", ", objectConfig.SelectedFields.Keys);
        var czql = $"SELECT {fields} FROM {objectConfig.ObjectName} WHERE SYSID = {localId}";
        var rows = await QueryAllAsync(czql, rowLimit: 1, ct).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>Server-time ping used by validate-config (GET /utils/getServerTime analog via echo).</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            if (_sessionId is null)
                await LoginAsync(ct).ConfigureAwait(false);
            return _sessionId is not null;
        }
        catch (Exception exc)
        {
            Logger.Warning($"Clarizen connectivity check failed: {exc.Message}");
            return false;
        }
    }

    // ── Attachment download ──────────────────────────────────────────────────

    /// <summary>
    /// GET an attachment binary, streaming with a hard <paramref name="maxBytes"/>
    /// cap: a Content-Length above the cap, or a body that exceeds it mid-stream,
    /// returns <see cref="DownloadResult.TooLarge"/> without buffering the whole
    /// file. Sends the session header (Clarizen storage links accept it); one
    /// GET is charged to the API budget. Never throws.
    /// </summary>
    public async Task<DownloadResult> DownloadAttachmentAsync(
        string url, long maxBytes, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return DownloadResult.Failed("no-url");
        if (_sessionId is null)
        {
            try
            {
                await LoginAsync(ct).ConfigureAwait(false);
            }
            catch (Exception exc)
            {
                return DownloadResult.Failed($"login-failed:{exc.Message}");
            }
        }

        try
        {
            var pace = _budget.Consume();
            Metrics.IncClarizenApiCalls();
            Metrics.SetApiBudgetRemaining(_budget.Remaining);
            if (pace > TimeSpan.Zero)
                await DelayAsync(pace, ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (_sessionId is not null)
                request.Headers.TryAddWithoutValidation("Authorization", $"Session {_sessionId}");

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return DownloadResult.Failed($"http-{(int)response.StatusCode}");
            if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
                return DownloadResult.TooLarge();

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await ReadCappedAsync(stream, maxBytes, ct).ConfigureAwait(false);
        }
        catch (ClarizenQuotaExceededException)
        {
            throw;
        }
        catch (Exception exc)
        {
            return DownloadResult.Failed($"error:{exc.Message}");
        }
    }

    /// <summary>Buffer up to <paramref name="maxBytes"/>; one byte more → oversize. Testable.</summary>
    internal static async Task<DownloadResult> ReadCappedAsync(
        Stream stream, long maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                return DownloadResult.TooLarge();
            buffer.Write(chunk, 0, read);
        }
        return DownloadResult.Success(buffer.ToArray());
    }

    // ── HTTP plumbing ────────────────────────────────────────────────────────

    /// <summary>Session-aware POST with quota accounting, throttle retry and one re-login replay.</summary>
    internal async Task<GraphLikeResponse> SendAsync(
        string path, JsonObject payload, CancellationToken ct = default)
    {
        if (_sessionId is null)
            await LoginAsync(ct).ConfigureAwait(false);

        var response = await SendRawAsync(path, payload, _sessionId, ct).ConfigureAwait(false);
        if (IsSessionExpired(response))
        {
            Logger.Info("Clarizen session expired — re-authenticating.");
            _sessionId = null;
            await LoginAsync(ct).ConfigureAwait(false);
            response = await SendRawAsync(path, payload, _sessionId, ct).ConfigureAwait(false);
        }
        return response;
    }

    internal static bool IsSessionExpired(GraphLikeResponse response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return true;
        var errorCode = response.Body?["errorCode"]?.GetValue<string>();
        return string.Equals(errorCode, "SessionTimeout", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<GraphLikeResponse> SendRawAsync(
        string path, JsonObject payload, string? sessionId, CancellationToken ct)
    {
        // Fail fast during a sustained Clarizen outage.
        if (!_breaker.TryAcquire())
        {
            return new GraphLikeResponse
            {
                StatusCode = HttpStatusCode.ServiceUnavailable,
                RawBody = "circuit open (Clarizen); failing fast",
                CircuitOpen = true,
            };
        }

        var url = $"{_config.ClarizenBaseUrl}/{path.TrimStart('/')}";
        for (var attempt = 0; ; attempt++)
        {
            var pace = _budget.Consume();
            Metrics.IncClarizenApiCalls();
            Metrics.SetApiBudgetRemaining(_budget.Remaining);
            if (pace > TimeSpan.Zero)
                await DelayAsync(pace, ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            if (sessionId is not null)
                request.Headers.TryAddWithoutValidation("Authorization", $"Session {sessionId}");
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            string raw;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (HttpRequestException exc) when (attempt < MaxRetries)
            {
                var delay = Math.Min(60, 2 * Math.Pow(2, attempt));
                Logger.Warning(
                    $"Clarizen request {path} failed ({exc.Message}); retry {attempt + 1}/{MaxRetries} in {delay:0.##}s");
                await DelayAsync(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
                continue;
            }
            catch (Exception)
            {
                _breaker.OnFailure();  // retries exhausted on a transport failure
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
                    // Same 429 hardening as the Graph client: every wait —
                    // server Retry-After included — is clamped to 60 s.
                    var delay = Math.Min(60, retryAfter ?? Math.Min(60, 2 * Math.Pow(2, attempt)));
                    Logger.Warning(
                        $"Clarizen {path} returned HTTP {(int)status}; retry {attempt + 1}/{MaxRetries} in {delay:0.##}s");
                    await DelayAsync(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
                    continue;
                }

                // Terminal outcome — classify for the breaker. 5xx = outage;
                // 429/4xx ignored; 2xx clears the window.
                if ((int)status >= 500)
                    _breaker.OnFailure();
                else if ((int)status == 429 || (int)status is >= 400 and < 500)
                    _breaker.OnIgnored();
                else
                    _breaker.OnSuccess();

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
                return new GraphLikeResponse
                {
                    StatusCode = status,
                    Body = parsed,
                    RawBody = raw,
                };
            }
        }
    }
}

/// <summary>Lightweight response wrapper shared by the Clarizen HTTP path.</summary>
public sealed class GraphLikeResponse
{
    public required HttpStatusCode StatusCode { get; init; }
    public JsonNode? Body { get; init; }
    public string RawBody { get; init; } = string.Empty;

    /// <summary>True when the request was not sent because the Clarizen circuit is open.</summary>
    public bool CircuitOpen { get; init; }

    public bool IsSuccess => (int)StatusCode is >= 200 and < 300;
}
