// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Infrastructure/Alerting.cs
// --------------------------
// Outbound webhook alerting for the observability surface (#11).
//
// `RaiseAsync` POSTs a small JSON envelope to ALERT_WEBHOOK_URL when that env
// var is set, and is a strict no-op otherwise. Alerting must NEVER break a
// crawl: every failure (missing URL, network error, non-2xx, timeout) is
// swallowed and logged, never thrown to the caller.
//
// `MaybeAlertDeadLetter` fires a `dead_letter` alert when the dead-letter depth
// exceeds ALERT_DEADLETTER_THRESHOLD (>0 to enable).
//
// Wave 2 wires the calls into the pipeline; these are the exposed seams.

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SalesforceCopilotConnector.Infrastructure;

/// <summary>
/// Fire-and-forget webhook alerts. Gated on <c>ALERT_WEBHOOK_URL</c>; every
/// failure is swallowed so a crawl is never broken by alerting.
/// </summary>
public static class Alerting
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector");

    /// <summary>Env var holding the alert webhook URL. Unset → alerting disabled.</summary>
    public const string WebhookUrlEnvVar = "ALERT_WEBHOOK_URL";

    /// <summary>Env var holding the dead-letter depth threshold. <c>&gt;0</c> enables the alert.</summary>
    public const string DeadLetterThresholdEnvVar = "ALERT_DEADLETTER_THRESHOLD";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>
    /// HTTP client used for webhook POSTs. Lazily created with a short timeout;
    /// tests inject a fake handler by assigning this. Never null once accessed.
    /// </summary>
    internal static HttpClient HttpClient { get; set; } = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>
    /// Optional connector id stamped into the alert envelope as <c>connector</c>.
    /// Wave 2 sets this once at startup; when null the field is omitted.
    /// </summary>
    internal static string? ConnectorId { get; set; }

    /// <summary>
    /// POST an alert envelope <c>{kind, message, connector?, timestamp, data?}</c>
    /// to <c>ALERT_WEBHOOK_URL</c>. No-op (returns a completed task) when the env
    /// var is unset. All failures are swallowed and logged.
    /// </summary>
    /// <param name="kind">Short machine-readable alert kind, e.g. <c>crawl_failed</c>.</param>
    /// <param name="message">Human-readable description.</param>
    /// <param name="data">Optional structured payload serialized under <c>data</c>.</param>
    public static async Task RaiseAsync(string kind, string message, object? data = null)
    {
        var url = Environment.GetEnvironmentVariable(WebhookUrlEnvVar);
        if (string.IsNullOrWhiteSpace(url))
        {
            return;  // alerting disabled
        }

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
                Logger.Warning(
                    $"Alerting: webhook returned HTTP {(int)response.StatusCode} for alert '{kind}'");
            }
        }
        catch (Exception exc)
        {
            // Never let alerting break a crawl — swallow and log.
            Logger.Warning($"Alerting: failed to POST alert '{kind}' to webhook: {exc.Message}");
        }
    }

    /// <summary>
    /// Raise a <c>dead_letter</c> alert when <paramref name="depth"/> exceeds
    /// <c>ALERT_DEADLETTER_THRESHOLD</c> (a positive integer enables the check).
    /// No-op when the threshold is unset, non-positive, or not exceeded.
    /// Fire-and-forget: the POST is not awaited by the caller.
    /// </summary>
    public static void MaybeAlertDeadLetter(string connectorId, int depth)
    {
        var raw = Environment.GetEnvironmentVariable(DeadLetterThresholdEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var threshold)
            || threshold <= 0)
        {
            return;
        }
        if (depth <= threshold)
        {
            return;
        }

        var data = new Dictionary<string, object?>
        {
            ["connector"] = connectorId,
            ["depth"] = depth,
            ["threshold"] = threshold,
        };
        // Fire-and-forget; RaiseAsync itself swallows all failures. Observe the
        // task to avoid unobserved-exception warnings (there won't be any).
        _ = RaiseAsync(
                "dead_letter",
                $"Dead-letter depth {depth} exceeded threshold {threshold} for connector '{connectorId}'",
                data)
            .ContinueWith(
                t => Logger.Error($"Alerting: dead-letter alert task faulted: {t.Exception?.Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
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
        {
            envelope["connector"] = connector;
        }
        if (data != null)
        {
            envelope["data"] = data;
        }
        return JsonSerializer.Serialize(envelope, JsonOptions);
    }
}
