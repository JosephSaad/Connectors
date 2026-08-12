// Infrastructure/Metrics.cs
// -------------------------
// Clarizen-side metrics facade: the process-wide registry of this connector's
// counters/gauges (thread-safe Interlocked longs + concurrent labelled maps)
// plus the Prometheus text-exposition renderer served by HealthEndpoint on
// /metrics. The reusable rendering mechanism lives in the chassis
// (Connector.Chassis.MetricsRenderer); this type owns only Clarizen's series
// and its exact exposition — label values NOT escaped, a plain
// tracing_enabled gauge (Tracing.Enabled), circuit breakers via Breakers.
// Byte-identical to the pre-facade output.

using System.Collections.Concurrent;
using System.Text;
using static Connector.Chassis.MetricsRenderer;

namespace ClarizenConnector.Infrastructure;

/// <summary>
/// Static registry of Clarizen's process counters/gauges rendered in Prometheus
/// text exposition format. All mutation is via
/// <see cref="System.Threading.Interlocked"/> so the increment/set methods are
/// safe to call from any pipeline thread.
/// </summary>
public static class Metrics
{
    // Monotonic counters (only ever increase over the process lifetime).
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

    // Gauges (can go up or down / be set to an absolute value).
    private static long _deadLetterDepth;
    private static long _lastCrawlCompletedUnix;
    private static long _haClaimsHeld;
    private static long _apiBudgetRemaining;
    private static long _webhookReceiverUp;

    // Labelled classification / governance / content-gate counters.
    private static readonly ConcurrentDictionary<string, long> ItemsClassified = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> SensitiveDetections = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> ItemsAclRestricted = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> ContentGateBlocked = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> ContentGateScanUnavailable = new(StringComparer.Ordinal);

    // Process start, used to derive uptime. Captured at type init.
    private static readonly DateTime StartUtc = DateTime.UtcNow;

    // ── Counter increments ────────────────────────────────────────────────────

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

    /// <summary>Record one item whose ACL was restricted by governance
    /// (reason: "financial" or "classification").</summary>
    public static void IncItemsAclRestricted(string reason) =>
        ItemsAclRestricted.AddOrUpdate(reason, 1, (_, v) => v + 1);

    /// <summary>Record one item quarantined by the content gate, by category.</summary>
    public static void IncContentGateBlocked(string category) =>
        ContentGateBlocked.AddOrUpdate(category, 1, (_, v) => v + 1);

    /// <summary>Record one content-gate scan that could not complete, by kind
    /// ("binary" or "text").</summary>
    public static void IncContentGateScanUnavailable(string kind) =>
        ContentGateScanUnavailable.AddOrUpdate(kind, 1, (_, v) => v + 1);

    // ── Gauge setters ─────────────────────────────────────────────────────────

    public static void SetDeadLetterDepth(long depth) => Interlocked.Exchange(ref _deadLetterDepth, depth);

    public static void SetLastCrawlCompletedUnix(long unixSeconds) =>
        Interlocked.Exchange(ref _lastCrawlCompletedUnix, unixSeconds);

    /// <summary>HA object-type leases this node currently holds (0 outside HA mode).</summary>
    public static void SetHaClaimsHeld(long count) => Interlocked.Exchange(ref _haClaimsHeld, count);

    public static void SetApiBudgetRemaining(long remaining) => Interlocked.Exchange(ref _apiBudgetRemaining, remaining);

    /// <summary>1 while the webhook receiver is bound and listening, else 0.</summary>
    public static void SetWebhookReceiverUp(bool up) => Interlocked.Exchange(ref _webhookReceiverUp, up ? 1 : 0);

    /// <summary>
    /// Convenience: stamp <see cref="SetLastCrawlCompletedUnix"/> with the current
    /// UTC time. Call from the crawl-completion path.
    /// </summary>
    public static void MarkCrawlCompletedNow() =>
        SetLastCrawlCompletedUnix(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    // ── Read-only accessors ────────────────────────────────────────────────────

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
    public static long HaClaimsHeld => Math.Max(0, Interlocked.Read(ref _haClaimsHeld));

    /// <summary>Read the classified-item count for a label (0 when absent).</summary>
    public static long ItemsClassifiedFor(string label) =>
        ItemsClassified.TryGetValue(label, out var v) ? v : 0;

    /// <summary>Read the sensitive-detection count for a category (0 when absent).</summary>
    public static long SensitiveDetectionsFor(string category) =>
        SensitiveDetections.TryGetValue(category, out var v) ? v : 0;

    /// <summary>Read the ACL-restriction count for a reason (0 when absent).</summary>
    public static long ItemsAclRestrictedFor(string reason) =>
        ItemsAclRestricted.TryGetValue(reason, out var v) ? v : 0;

    /// <summary>Read the content-gate block count for a category (0 when absent).</summary>
    public static long ContentGateBlockedFor(string category) =>
        ContentGateBlocked.TryGetValue(category, out var v) ? v : 0;

    /// <summary>Total content-gate blocks across every category.</summary>
    public static long ContentGateBlockedTotal => ContentGateBlocked.Values.Sum();

    /// <summary>Read the incomplete-scan count for a kind (0 when absent).</summary>
    public static long ContentGateScanUnavailableFor(string kind) =>
        ContentGateScanUnavailable.TryGetValue(kind, out var v) ? v : 0;

    /// <summary>Seconds since the process (metrics registry) started.</summary>
    public static double UptimeSeconds => (DateTime.UtcNow - StartUtc).TotalSeconds;

    /// <summary>
    /// Test seam: reset every counter and gauge to zero. Not called in
    /// production; the registry is process-lived.
    /// </summary>
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
        Interlocked.Exchange(ref _deadLetterDepth, 0);
        Interlocked.Exchange(ref _lastCrawlCompletedUnix, 0);
        Interlocked.Exchange(ref _haClaimsHeld, 0);
        Interlocked.Exchange(ref _apiBudgetRemaining, 0);
        Interlocked.Exchange(ref _webhookReceiverUp, 0);
        ItemsClassified.Clear();
        SensitiveDetections.Clear();
        ItemsAclRestricted.Clear();
        ContentGateBlocked.Clear();
        ContentGateScanUnavailable.Clear();
    }

    // ── Prometheus rendering ─────────────────────────────────────────────────

    /// <summary>
    /// Render the registry in Prometheus text exposition format (v0.0.4). Metric
    /// names are prefixed <c>clarizen_connector_</c>, label values are NOT
    /// escaped, a PLAIN <c>tracing_enabled</c> gauge (value from
    /// <see cref="Tracing.Enabled"/>), and circuit breakers are delegated to the
    /// connector's <see cref="Breakers"/>. Labelled counters are omitted entirely
    /// when empty. Uptime is computed at render time.
    /// </summary>
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
        Gauge(sb, "ha_claims_held", "HA object-type leases this node currently holds (0 outside HA mode).", HaClaimsHeld);
        Gauge(sb, "api_budget_remaining", "Remaining Clarizen API calls in today's client-side budget.", ApiBudgetRemaining);
        Gauge(sb, "last_crawl_completed_timestamp_seconds", "Unix timestamp (seconds) of the last completed crawl; 0 if none yet.", LastCrawlCompletedUnix);
        GaugeDouble(sb, "uptime_seconds", "Seconds since the connector process started.", UptimeSeconds);

        // Per-dependency circuit-breaker state + trip/reset counters, rendered
        // by the connector's own Breakers.
        Breakers.AppendMetrics(sb);

        // Labelled classification counters (only present once something is
        // classified/detected — keeps the exposition clean when off).
        if (!ItemsClassified.IsEmpty)
            LabeledCounter(sb, "items_classified_total",
                "Items classified, by sensitivity label.", "label", ItemsClassified);
        if (!SensitiveDetections.IsEmpty)
            LabeledCounter(sb, "sensitive_detections_total",
                "Sensitive-data detections, by category.", "category", SensitiveDetections);
        if (!ItemsAclRestricted.IsEmpty)
            LabeledCounter(sb, "items_acl_restricted_total",
                "Items whose ACL was restricted by governance, by reason.", "reason", ItemsAclRestricted);

        // ContentGate (CONTENT_GATE). Absent entirely when the gate is off, so
        // the exposition is byte-identical to before the feature by default.
        if (!ContentGateBlocked.IsEmpty)
            LabeledCounter(sb, "content_gate_blocked_total",
                "Items quarantined by the content gate, by category.", "category", ContentGateBlocked);
        if (!ContentGateScanUnavailable.IsEmpty)
            LabeledCounter(sb, "content_gate_scan_unavailable_total",
                "Content-gate scans that could not complete, by kind (binary|text).",
                "kind", ContentGateScanUnavailable);

        return sb.ToString();
    }
}
