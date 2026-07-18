// Infrastructure/Alerting.cs
// --------------------------
// Outbound webhook alerting. RaiseAsync POSTs a small JSON envelope to
// ALERT_WEBHOOK_URL when that env var is set, and is a strict no-op otherwise.
// Alerting must NEVER break a crawl: every failure (missing URL, network
// error, non-2xx, timeout) is swallowed and logged, never thrown to the
// caller. MaybeAlertDeadLetterAsync fires a `dead_letter` alert when the
// dead-letter depth exceeds ALERT_DEADLETTER_THRESHOLD (>0 to enable).

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace HadoopConnector.Infrastructure;

public static class Alerting
{
    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector");

    public const string WebhookUrlEnvVar = "ALERT_WEBHOOK_URL";
    public const string DeadLetterThresholdEnvVar = "ALERT_DEADLETTER_THRESHOLD";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>HTTP client for webhook POSTs; tests inject a fake handler by
    /// assigning this. Default construction honours the shared transport policy
    /// (PROXY_URL / CA_BUNDLE_PATH) — webhook receivers sit behind the same
    /// corporate egress as everything else.</summary>
    internal static HttpClient HttpClient { get; set; } = CreateDefaultClient();

    private static HttpClient CreateDefaultClient()
    {
        try
        {
            return new HttpClient(HttpTransport.CreateHandler()) { Timeout = TimeSpan.FromSeconds(5) };
        }
        catch
        {
            // A misconfigured PROXY_URL/CA_BUNDLE_PATH already failed fast (with
            // the setting named) when the WebHDFS/Graph clients were built in
            // Runtime.Create; alerting must never be the thing that crashes a
            // process, so it degrades to the bare default transport here.
            return new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        }
    }

    /// <summary>Optional connector id stamped into the alert envelope as <c>connector</c>.</summary>
    internal static string? ConnectorId { get; set; }

    /// <summary>
    /// POST an alert envelope <c>{kind, message, connector?, timestamp, data?}</c>
    /// to <c>ALERT_WEBHOOK_URL</c>. No-op when the env var is unset. All
    /// failures are swallowed and logged.
    /// </summary>
    public static async Task RaiseAsync(string kind, string message, object? data = null)
    {
        var url = Environment.GetEnvironmentVariable(WebhookUrlEnvVar);
        if (string.IsNullOrWhiteSpace(url))
            return;

        string body;
        try
        {
            body = BuildEnvelope(kind, message, data);
        }
        catch (Exception exc)
        {
            Logger.Error($"Alerting: failed to serialize alert '{kind}': {exc.Message}");
            return;
        }

        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await HttpClient.PostAsync(url, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Warning($"Alerting: webhook returned HTTP {(int)response.StatusCode} for alert '{kind}'");
            }
        }
        catch (Exception exc)
        {
            Logger.Warning($"Alerting: failed to POST alert '{kind}' to webhook: {exc.Message}");
        }
    }

    /// <summary>
    /// Raise a <c>dead_letter</c> alert when <paramref name="depth"/> exceeds
    /// <c>ALERT_DEADLETTER_THRESHOLD</c> (a positive integer enables the check).
    /// Callers must await the returned task so a one-shot run does not exit
    /// with the webhook POST mid-flight.
    /// </summary>
    public static Task MaybeAlertDeadLetterAsync(string connectorId, int depth)
    {
        var raw = Environment.GetEnvironmentVariable(DeadLetterThresholdEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
            return Task.CompletedTask;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var threshold)
            || threshold <= 0)
        {
            return Task.CompletedTask;
        }
        if (depth <= threshold)
            return Task.CompletedTask;

        var data = new Dictionary<string, object?>
        {
            ["connector"] = connectorId,
            ["depth"] = depth,
            ["threshold"] = threshold,
        };
        return RaiseAsync(
            "dead_letter",
            $"Dead-letter depth {depth} exceeded threshold {threshold} for connector '{connectorId}'",
            data);
    }

    /// <summary>Build the alert JSON envelope. Separated for testability.</summary>
    internal static string BuildEnvelope(string kind, string message, object? data)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["kind"] = kind,
            ["message"] = message,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };
        var connector = ConnectorId;
        if (!string.IsNullOrEmpty(connector))
            envelope["connector"] = connector;
        if (data != null)
            envelope["data"] = data;
        return JsonSerializer.Serialize(envelope, JsonOptions);
    }
}
