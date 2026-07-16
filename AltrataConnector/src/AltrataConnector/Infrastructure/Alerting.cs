// Infrastructure/Alerting.cs
// --------------------------
// Operational alert webhook. When ALERT_WEBHOOK_URL is set, alerts are POSTed
// as JSON ({connector, severity, event, message, timestamp, details}).
// ALERT_DEADLETTER_THRESHOLD (default 0 = disabled) fires an alert when the
// dead-letter queue depth crosses the threshold. Failures to deliver an alert
// are logged and swallowed — alerting must never break ingestion.

using System.Globalization;
using System.Net.Http.Json;

namespace AltrataConnector.Infrastructure;

public interface IAlertSink
{
    Task SendAsync(string severity, string @event, string message,
        IReadOnlyDictionary<string, object?>? details = null);

    /// <summary>Fire a dead-letter-depth alert when the configured threshold is crossed.</summary>
    Task CheckDeadLetterDepthAsync(int depth);
}

public sealed class Alerting : IAlertSink
{
    private static readonly IAppLogger Logger = Logging.GetLogger("altrata_connector.alerting");

    public const string WebhookEnvVar = "ALERT_WEBHOOK_URL";
    public const string ThresholdEnvVar = "ALERT_DEADLETTER_THRESHOLD";

    private readonly HttpClient _http;
    private readonly string _connectorId;

    public Alerting(string connectorId, HttpClient? http = null)
    {
        _connectorId = connectorId;
        _http = http ?? SharedHttp;
    }

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static string? WebhookUrl
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(WebhookEnvVar);
            return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        }
    }

    public static int DeadLetterThreshold
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(ThresholdEnvVar);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
                ? n
                : 0;
        }
    }

    public async Task SendAsync(string severity, string @event, string message,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        var url = WebhookUrl;
        if (url == null)
            return;
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["connector"] = _connectorId,
                ["severity"] = severity,
                ["event"] = @event,
                ["message"] = message,
                ["timestamp"] = DateTime.UtcNow.ToString("o"),
                ["details"] = details,
            };
            using var response = await _http.PostAsJsonAsync(url, payload);
            if (!response.IsSuccessStatusCode)
                Logger.Warning($"Alert webhook returned {(int)response.StatusCode} for event '{@event}'");
        }
        catch (Exception exc)
        {
            Logger.Warning($"Alert webhook delivery failed for event '{@event}': {exc.Message}");
        }
    }

    /// <summary>Fire a dead-letter-depth alert when the configured threshold is crossed.</summary>
    public async Task CheckDeadLetterDepthAsync(int depth)
    {
        var threshold = DeadLetterThreshold;
        if (threshold <= 0 || depth < threshold)
            return;
        await SendAsync("warning", "deadletter_threshold",
            $"Dead-letter queue depth {depth} reached threshold {threshold}",
            new Dictionary<string, object?> { ["depth"] = depth, ["threshold"] = threshold });
    }
}
