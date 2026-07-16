// Infrastructure/Metrics.cs
// -------------------------
// Process-wide metrics registry: thread-safe counters/gauges (Interlocked
// longs) plus a Prometheus text-exposition renderer, served by HealthEndpoint
// on /metrics. No external dependencies.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace ClarizenConnector.Infrastructure;

public static class Metrics
{
    // Labelled counters (classification): keyed by label / category value.
    private static readonly ConcurrentDictionary<string, long> ItemsClassified = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> SensitiveDetections = new(StringComparer.Ordinal);

    // Monotonic counters.
    private static long _itemsIngested;
    private static long _itemsFailed;
    private static long _itemsDeleted;
    private static long _itemsSkipped;
    private static long _crawlsStarted;
    private static long _crawlsCompleted;
    private static long _throttle429Total;
    private static long _clarizenApiCalls;
    private static long _attachmentsExtracted;
    private static long _attachmentsSkipped;
    private static long _webhookEventsReceived;
    private static long _webhookEventsAccepted;
    private static long _webhookEventsRejected;

    // Gauges.
    private static long _deadLetterDepth;
    private static long _lastCrawlCompletedUnix;
    private static long _apiBudgetRemaining;
    private static long _webhookReceiverUp;

    private static readonly DateTime StartUtc = DateTime.UtcNow;

    public static void IncItemsIngested(long count = 1) => Interlocked.Add(ref _itemsIngested, count);
    public static void IncItemsFailed(long count = 1) => Interlocked.Add(ref _itemsFailed, count);
    public static void IncItemsDeleted(long count = 1) => Interlocked.Add(ref _itemsDeleted, count);
    public static void IncItemsSkipped(long count = 1) => Interlocked.Add(ref _itemsSkipped, count);
    public static void IncCrawlsStarted(long count = 1) => Interlocked.Add(ref _crawlsStarted, count);
    public static void IncCrawlsCompleted(long count = 1) => Interlocked.Add(ref _crawlsCompleted, count);
    public static void IncThrottle429(long count = 1) => Interlocked.Add(ref _throttle429Total, count);
    public static void IncClarizenApiCalls(long count = 1) => Interlocked.Add(ref _clarizenApiCalls, count);
    public static void IncAttachmentsExtracted(long count = 1) => Interlocked.Add(ref _attachmentsExtracted, count);
    public static void IncAttachmentsSkipped(long count = 1) => Interlocked.Add(ref _attachmentsSkipped, count);
    public static void IncWebhookEventsReceived(long count = 1) => Interlocked.Add(ref _webhookEventsReceived, count);
    public static void IncWebhookEventsAccepted(long count = 1) => Interlocked.Add(ref _webhookEventsAccepted, count);
    public static void IncWebhookEventsRejected(long count = 1) => Interlocked.Add(ref _webhookEventsRejected, count);

    /// <summary>Record one item classified with the given sensitivity label.</summary>
    public static void IncItemsClassified(string label) =>
        ItemsClassified.AddOrUpdate(label, 1, (_, v) => v + 1);

    /// <summary>Record one sensitive-data detection of the given category.</summary>
    public static void IncSensitiveDetection(string category) =>
        SensitiveDetections.AddOrUpdate(category, 1, (_, v) => v + 1);

    public static void SetDeadLetterDepth(long depth) => Interlocked.Exchange(ref _deadLetterDepth, depth);
    public static void SetApiBudgetRemaining(long remaining) => Interlocked.Exchange(ref _apiBudgetRemaining, remaining);
    /// <summary>1 while the webhook receiver is bound and listening, else 0.</summary>
    public static void SetWebhookReceiverUp(bool up) => Interlocked.Exchange(ref _webhookReceiverUp, up ? 1 : 0);

    public static void SetLastCrawlCompletedUnix(long unixSeconds) =>
        Interlocked.Exchange(ref _lastCrawlCompletedUnix, unixSeconds);

    public static void MarkCrawlCompletedNow() =>
        SetLastCrawlCompletedUnix(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    public static long ItemsIngested => Interlocked.Read(ref _itemsIngested);
    public static long ItemsFailed => Interlocked.Read(ref _itemsFailed);
    public static long ItemsDeleted => Interlocked.Read(ref _itemsDeleted);
    public static long ItemsSkipped => Interlocked.Read(ref _itemsSkipped);
    public static long CrawlsStarted => Interlocked.Read(ref _crawlsStarted);
    public static long CrawlsCompleted => Interlocked.Read(ref _crawlsCompleted);
    public static long Throttle429Total => Interlocked.Read(ref _throttle429Total);
    public static long ClarizenApiCalls => Interlocked.Read(ref _clarizenApiCalls);
    public static long AttachmentsExtracted => Interlocked.Read(ref _attachmentsExtracted);
    public static long AttachmentsSkipped => Interlocked.Read(ref _attachmentsSkipped);
    public static long WebhookEventsReceived => Interlocked.Read(ref _webhookEventsReceived);
    public static long WebhookEventsAccepted => Interlocked.Read(ref _webhookEventsAccepted);
    public static long WebhookEventsRejected => Interlocked.Read(ref _webhookEventsRejected);
    public static long WebhookReceiverUp => Interlocked.Read(ref _webhookReceiverUp);
    public static long DeadLetterDepth => Interlocked.Read(ref _deadLetterDepth);
    public static long ApiBudgetRemaining => Interlocked.Read(ref _apiBudgetRemaining);
    public static long LastCrawlCompletedUnix => Interlocked.Read(ref _lastCrawlCompletedUnix);

    /// <summary>Read the classified-item count for a label (0 when absent).</summary>
    public static long ItemsClassifiedFor(string label) =>
        ItemsClassified.TryGetValue(label, out var v) ? v : 0;

    /// <summary>Read the sensitive-detection count for a category (0 when absent).</summary>
    public static long SensitiveDetectionsFor(string category) =>
        SensitiveDetections.TryGetValue(category, out var v) ? v : 0;

    public static double UptimeSeconds => (DateTime.UtcNow - StartUtc).TotalSeconds;

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _itemsIngested, 0);
        Interlocked.Exchange(ref _itemsFailed, 0);
        Interlocked.Exchange(ref _itemsDeleted, 0);
        Interlocked.Exchange(ref _itemsSkipped, 0);
        Interlocked.Exchange(ref _crawlsStarted, 0);
        Interlocked.Exchange(ref _crawlsCompleted, 0);
        Interlocked.Exchange(ref _throttle429Total, 0);
        Interlocked.Exchange(ref _clarizenApiCalls, 0);
        Interlocked.Exchange(ref _attachmentsExtracted, 0);
        Interlocked.Exchange(ref _attachmentsSkipped, 0);
        Interlocked.Exchange(ref _webhookEventsReceived, 0);
        Interlocked.Exchange(ref _webhookEventsAccepted, 0);
        Interlocked.Exchange(ref _webhookEventsRejected, 0);
        Interlocked.Exchange(ref _webhookReceiverUp, 0);
        Interlocked.Exchange(ref _deadLetterDepth, 0);
        Interlocked.Exchange(ref _apiBudgetRemaining, 0);
        Interlocked.Exchange(ref _lastCrawlCompletedUnix, 0);
        ItemsClassified.Clear();
        SensitiveDetections.Clear();
    }

    private const string Prefix = "clarizen_connector_";

    /// <summary>Prometheus text exposition format v0.0.4.</summary>
    public static string RenderPrometheus()
    {
        var sb = new StringBuilder(2048);

        Counter(sb, "items_ingested_total", "Total Clarizen items successfully ingested into the Graph connection.", ItemsIngested);
        Counter(sb, "items_failed_total", "Total items that failed to ingest.", ItemsFailed);
        Counter(sb, "items_deleted_total", "Total items deleted from the Graph connection.", ItemsDeleted);
        Counter(sb, "items_skipped_total", "Total items skipped (e.g. already checkpointed).", ItemsSkipped);
        Counter(sb, "crawls_started_total", "Total crawl/ingestion runs started.", CrawlsStarted);
        Counter(sb, "crawls_completed_total", "Total crawl/ingestion runs completed.", CrawlsCompleted);
        Counter(sb, "throttled_429_total", "Total HTTP 429 (throttling) responses observed from the Graph API.", Throttle429Total);
        Counter(sb, "clarizen_api_calls_total", "Total Clarizen REST API calls issued by this process.", ClarizenApiCalls);
        Counter(sb, "attachments_extracted_total", "Total attachments whose text content was extracted and indexed.", AttachmentsExtracted);
        Counter(sb, "attachments_skipped_total", "Total attachments skipped (oversize, disallowed type, or no extractable text).", AttachmentsSkipped);
        Counter(sb, "webhook_events_received_total", "Total webhook change notifications received (before validation).", WebhookEventsReceived);
        Counter(sb, "webhook_events_accepted_total", "Total webhook events accepted (validated) and enqueued for targeted ingest.", WebhookEventsAccepted);
        Counter(sb, "webhook_events_rejected_total", "Total webhook posts rejected (bad signature, malformed, or unsupported).", WebhookEventsRejected);

        Gauge(sb, "webhook_receiver_up", "1 while the webhook receiver is bound and listening, else 0.", WebhookReceiverUp);
        Gauge(sb, "tracing_enabled", "1 when OpenTelemetry OTLP export is registered (OTEL_EXPORTER_OTLP_ENDPOINT set), else 0.", Tracing.Enabled ? 1 : 0);
        Gauge(sb, "dead_letter_depth", "Current number of records in the dead-letter queue.", DeadLetterDepth);
        Gauge(sb, "api_budget_remaining", "Remaining Clarizen API calls in today's client-side budget.", ApiBudgetRemaining);
        Gauge(sb, "last_crawl_completed_timestamp_seconds", "Unix timestamp (seconds) of the last completed crawl; 0 if none yet.", LastCrawlCompletedUnix);
        GaugeDouble(sb, "uptime_seconds", "Seconds since the connector process started.", UptimeSeconds);

        // Per-dependency circuit-breaker state + trip/reset counters.
        Breakers.AppendMetrics(sb);

        // Labelled classification counters (only present once something is
        // classified/detected — keeps the exposition clean when off).
        LabelledCounter(sb, "items_classified_total",
            "Items classified, by sensitivity label.", "label", ItemsClassified);
        LabelledCounter(sb, "sensitive_detections_total",
            "Sensitive-data detections, by category.", "category", SensitiveDetections);

        return sb.ToString();
    }

    /// <summary>Render a labelled counter family (one line per label value).</summary>
    private static void LabelledCounter(
        StringBuilder sb, string name, string help, string labelKey,
        ConcurrentDictionary<string, long> values)
    {
        if (values.IsEmpty)
            return;
        var full = Prefix + name;
        sb.Append("# HELP ").Append(full).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(full).Append(" counter\n");
        foreach (var kv in values.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append(full).Append('{').Append(labelKey).Append("=\"").Append(kv.Key).Append("\"} ")
              .Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
    }

    private static void Counter(StringBuilder sb, string name, string help, long value) =>
        Metric(sb, name, help, "counter", value.ToString(CultureInfo.InvariantCulture));

    private static void Gauge(StringBuilder sb, string name, string help, long value) =>
        Metric(sb, name, help, "gauge", value.ToString(CultureInfo.InvariantCulture));

    private static void GaugeDouble(StringBuilder sb, string name, string help, double value) =>
        Metric(sb, name, help, "gauge", value.ToString("0.###", CultureInfo.InvariantCulture));

    private static void Metric(StringBuilder sb, string name, string help, string type, string value)
    {
        var full = Prefix + name;
        sb.Append("# HELP ").Append(full).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(full).Append(' ').Append(type).Append('\n');
        sb.Append(full).Append(' ').Append(value).Append('\n');
    }
}
