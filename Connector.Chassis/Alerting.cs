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

namespace Connector.Chassis;

/// <summary>
/// Fire-and-forget webhook alerts. Gated on <c>ALERT_WEBHOOK_URL</c>; every
/// failure is swallowed so a crawl is never broken by alerting.
/// </summary>
public static class Alerting
{
    private static readonly IAppLogger Logger = Chassis.GetLogger(Chassis.Identity.BaseLoggerName);

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
    /// HTTP client used for webhook POSTs. Lazily created with a short timeout
    /// through the shared outbound transport (PROXY_URL / CA_BUNDLE_PATH);
    /// tests inject a fake handler by assigning this. Never null once accessed.
    /// Lazy so a transport misconfiguration surfaces as a loggable failure at
    /// first use rather than a type-initialization error.
    /// </summary>
    internal static HttpClient HttpClient
    {
        get => _httpClient ??= CreateDefaultClient();
        set => _httpClient = value;
    }

    private static HttpClient? _httpClient;

    /// <summary>
    /// How the alerting client's transport is built. Defaults to the chassis's
    /// own <see cref="HttpTransport.CreateHandler"/>; a host with its own
    /// transport assigns this at startup instead.
    /// </summary>
    /// <remarks>
    /// This exists because the fleet has four transports, not one. The chassis
    /// has HttpTransport; Salesforce has HttpClientFactory; Altrata has
    /// HttpConnectivity; Hadoop has its own HttpTransport that merely shares the
    /// name. All four read PROXY_URL and CA_BUNDLE_PATH and all four are already
    /// in production, so consolidating <c>Alerting</c> by picking a winner would
    /// silently change which TLS and proxy behaviour every other connector gets
    /// for its webhooks — a real behavioural change smuggled in as a refactor.
    ///
    /// A factory lets the shared alerting logic — thresholds, payload shape,
    /// dead-letter escalation, the degradation guard below — be genuinely shared
    /// while each connector keeps the transport it was hardened with. Same seam
    /// pattern as <see cref="Chassis.LoggerFactory"/> and
    /// <see cref="Logging.Dialect"/>: the chassis owns the mechanism, the host
    /// keeps the contract it cannot give up.
    ///
    /// Assign before the first alert. A null return is treated as a factory
    /// failure and takes the fallback path, so a misconfigured host degrades
    /// rather than throwing from a static property getter.
    /// </remarks>
    public static Func<HttpMessageHandler>? HandlerFactory { get; set; }

    /// <summary>
    /// The alerting client, degrading to the bare default transport if the
    /// configured one cannot be built.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpTransport.CreateHandler"/> reads PROXY_URL and
    /// CA_BUNDLE_PATH, so a misconfigured deployment can make it throw. Without
    /// the catch that exception surfaces from a property getter on first use —
    /// which, for a static like this, means a TypeInitializationException at an
    /// arbitrary point in a crawl rather than at startup.
    ///
    /// Alerting must never be the thing that takes a process down: it is how an
    /// operator finds out something else went wrong. A bad proxy or CA path has
    /// already failed fast, naming the setting, where the Graph and source
    /// clients were built — by the time alerting runs, the useful signal has
    /// been delivered and crashing here only removes the messenger.
    ///
    /// The fallback client has no proxy or custom CA, so in a locked-down network
    /// the webhook POST will simply fail and be logged. That is the intended
    /// trade: a failed alert is recoverable, a dead connector is not.
    ///
    /// Ported from the Hadoop connector, which carried this guard while the
    /// chassis did not — the same asymmetry ServiceStop had.
    /// </remarks>
    private static HttpClient CreateDefaultClient()
    {
        try
        {
            var handler = (HandlerFactory ?? HttpTransport.CreateHandler)()
                ?? throw new InvalidOperationException(
                    "Alerting.HandlerFactory returned null.");
            return new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(5),
            };
        }
        catch (Exception exc)
        {
            Logger.Warning(
                $"Alerting transport could not be built ({exc.GetType().Name}: {exc.Message}) — "
                + "falling back to the default transport, so proxy and CA-bundle settings will NOT "
                + "apply to alert webhooks. Alerts may fail to send on a restricted network.");
            return new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        }
    }

    /// <summary>
    /// Optional connector id stamped into the alert envelope as <c>connector</c>.
    /// Wave 2 sets this once at startup; when null the field is omitted.
    /// </summary>
    public static string? ConnectorId { get; set; }

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
    /// Callers must await the returned task: on a one-shot run the process exits
    /// right after the crawl summary, and an unawaited webhook POST would be
    /// aborted mid-flight (RaiseAsync already swallows every delivery failure,
    /// so awaiting can never break the crawl).
    /// </summary>
    public static Task MaybeAlertDeadLetterAsync(string connectorId, int depth)
    {
        var raw = Environment.GetEnvironmentVariable(DeadLetterThresholdEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Task.CompletedTask;
        }
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var threshold)
            || threshold <= 0)
        {
            return Task.CompletedTask;
        }
        if (depth <= threshold)
        {
            return Task.CompletedTask;
        }

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
