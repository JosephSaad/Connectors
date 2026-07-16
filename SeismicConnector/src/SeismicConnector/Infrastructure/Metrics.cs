// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Infrastructure/Metrics.cs
// -------------------------
// Process-wide metrics registry for the observability surface (#9).
//
// A tiny, allocation-free set of thread-safe counters and gauges tracked as
// plain `long`s (via Interlocked) plus a Prometheus text-exposition renderer.
// The increment/set methods are the seams the ingestion pipeline (Wave 2) calls;
// they are harmless no-ops in the sense that if nobody ever calls them the
// counters simply read zero. `RenderPrometheus()` is served by
// `HealthEndpoint` on the `/metrics` route.
//
// No external dependencies — this is intentionally the simplest thing that
// produces valid Prometheus exposition format so scraping works without a
// client library.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace SeismicConnector.Infrastructure;

/// <summary>
/// Static registry of process counters/gauges rendered in Prometheus text
/// exposition format. All mutation is via <see cref="System.Threading.Interlocked"/>
/// so the increment/set methods are safe to call from any pipeline thread.
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
    private static long _itemsReAcled;
    private static long _aclDriftDetected;
    private static long _degradedPauses;

    // Gauges (can go up or down / be set to an absolute value).
    private static long _deadLetterDepth;
    private static long _lastCrawlCompletedUnix;
    private static long _lastDriftFindings;

    // Labeled extraction counters (format → count). The content-extraction
    // pipeline records one attempt per payload and one success per non-empty
    // extraction, so scrape-side success ratios per format are trivial.
    private static readonly ConcurrentDictionary<string, long> ExtractionAttempts = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> ExtractionSuccesses = new(StringComparer.Ordinal);

    // Data-classification counters: items by final sensitivity label, and
    // sensitive detections by category (both across the process lifetime).
    private static readonly ConcurrentDictionary<string, long> ClassifiedByLabel = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> DetectionsByCategory = new(StringComparer.Ordinal);

    // Process start, used to derive uptime. Captured at type init.
    private static readonly DateTime StartUtc = DateTime.UtcNow;

    // ── Counter increments (Wave 2 pipeline seams) ───────────────────────────

    /// <summary>Record <paramref name="count"/> successfully ingested items.</summary>
    public static void IncItemsIngested(long count = 1) => Interlocked.Add(ref _itemsIngested, count);

    /// <summary>Record <paramref name="count"/> items that failed to ingest.</summary>
    public static void IncItemsFailed(long count = 1) => Interlocked.Add(ref _itemsFailed, count);

    /// <summary>Record <paramref name="count"/> items deleted from the connection.</summary>
    public static void IncItemsDeleted(long count = 1) => Interlocked.Add(ref _itemsDeleted, count);

    /// <summary>Record <paramref name="count"/> items skipped (e.g. checkpointed).</summary>
    public static void IncItemsSkipped(long count = 1) => Interlocked.Add(ref _itemsSkipped, count);

    /// <summary>Record that a crawl/ingestion run has started.</summary>
    public static void IncCrawlsStarted(long count = 1) => Interlocked.Add(ref _crawlsStarted, count);

    /// <summary>Record that a crawl/ingestion run has completed.</summary>
    public static void IncCrawlsCompleted(long count = 1) => Interlocked.Add(ref _crawlsCompleted, count);

    /// <summary>Record <paramref name="count"/> HTTP 429 (throttling) responses seen.</summary>
    public static void IncThrottle429(long count = 1) => Interlocked.Add(ref _throttle429Total, count);

    /// <summary>Record <paramref name="count"/> items whose ACL was refreshed (permission re-ACL).</summary>
    public static void IncItemsReAcled(long count = 1) => Interlocked.Add(ref _itemsReAcled, count);

    /// <summary>Record <paramref name="count"/> items whose resolved ACL drifted from the indexed ACL.</summary>
    public static void IncAclDriftDetected(long count = 1) => Interlocked.Add(ref _aclDriftDetected, count);

    /// <summary>Record that a crawl paused into degraded mode (a critical breaker was open).</summary>
    public static void IncDegradedPauses(long count = 1) => Interlocked.Add(ref _degradedPauses, count);

    /// <summary>Record one content-extraction attempt for <paramref name="format"/> (labelled counter).</summary>
    public static void RecordExtraction(string format, bool success)
    {
        var label = NormalizeFormatLabel(format);
        ExtractionAttempts.AddOrUpdate(label, 1, (_, v) => v + 1);
        if (success)
            ExtractionSuccesses.AddOrUpdate(label, 1, (_, v) => v + 1);
    }

    private static string NormalizeFormatLabel(string format)
    {
        var label = (format ?? "").Trim().TrimStart('.').ToLowerInvariant();
        // Prometheus label values: keep them boring — alphanumerics plus '-'
        // and '_' (both legal in label values and used by synthetic labels
        // like "livedoc-fields"). Anything else collapses to "other".
        return label.Length == 0 || !label.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            ? "other"
            : label;
    }

    /// <summary>Read-only snapshot for tests / diagnostics.</summary>
    internal static IReadOnlyDictionary<string, long> ExtractionAttemptsSnapshot =>
        new Dictionary<string, long>(ExtractionAttempts);

    internal static IReadOnlyDictionary<string, long> ExtractionSuccessesSnapshot =>
        new Dictionary<string, long>(ExtractionSuccesses);

    /// <summary>
    /// Record one classified item: bumps <c>items_classified_total{label}</c>
    /// and, per detected category, <c>sensitive_detections_total{category}</c>.
    /// </summary>
    public static void RecordClassification(string label, IEnumerable<string> categories)
    {
        ClassifiedByLabel.AddOrUpdate(label, 1, (_, v) => v + 1);
        foreach (var category in categories)
            DetectionsByCategory.AddOrUpdate(category, 1, (_, v) => v + 1);
    }

    internal static IReadOnlyDictionary<string, long> ClassifiedByLabelSnapshot =>
        new Dictionary<string, long>(ClassifiedByLabel);

    internal static IReadOnlyDictionary<string, long> DetectionsByCategorySnapshot =>
        new Dictionary<string, long>(DetectionsByCategory);

    // ── Gauge setters ────────────────────────────────────────────────────────

    /// <summary>Set the current dead-letter queue depth.</summary>
    public static void SetDeadLetterDepth(long depth) => Interlocked.Exchange(ref _deadLetterDepth, depth);

    /// <summary>Set the last-crawl-completed timestamp (unix seconds).</summary>
    public static void SetLastCrawlCompletedUnix(long unixSeconds) =>
        Interlocked.Exchange(ref _lastCrawlCompletedUnix, unixSeconds);

    /// <summary>Set the finding count of the last `reconcile` drift sweep.</summary>
    public static void SetLastDriftFindings(long count) =>
        Interlocked.Exchange(ref _lastDriftFindings, count);

    /// <summary>
    /// Convenience: stamp <see cref="SetLastCrawlCompletedUnix"/> with the current
    /// UTC time. Call from the crawl-completion path.
    /// </summary>
    public static void MarkCrawlCompletedNow() =>
        SetLastCrawlCompletedUnix(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    // ── Read-only accessors (handy for tests) ────────────────────────────────

    public static long ItemsIngested => Interlocked.Read(ref _itemsIngested);
    public static long ItemsFailed => Interlocked.Read(ref _itemsFailed);
    public static long ItemsDeleted => Interlocked.Read(ref _itemsDeleted);
    public static long ItemsSkipped => Interlocked.Read(ref _itemsSkipped);
    public static long CrawlsStarted => Interlocked.Read(ref _crawlsStarted);
    public static long CrawlsCompleted => Interlocked.Read(ref _crawlsCompleted);
    public static long Throttle429Total => Interlocked.Read(ref _throttle429Total);
    public static long ItemsReAcled => Interlocked.Read(ref _itemsReAcled);
    public static long AclDriftDetected => Interlocked.Read(ref _aclDriftDetected);
    public static long DegradedPauses => Interlocked.Read(ref _degradedPauses);
    public static long DeadLetterDepth => Interlocked.Read(ref _deadLetterDepth);
    public static long LastDriftFindings => Interlocked.Read(ref _lastDriftFindings);
    public static long LastCrawlCompletedUnix => Interlocked.Read(ref _lastCrawlCompletedUnix);

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
        Interlocked.Exchange(ref _itemsReAcled, 0);
        Interlocked.Exchange(ref _aclDriftDetected, 0);
        Interlocked.Exchange(ref _degradedPauses, 0);
        Interlocked.Exchange(ref _deadLetterDepth, 0);
        Interlocked.Exchange(ref _lastCrawlCompletedUnix, 0);
        Interlocked.Exchange(ref _lastDriftFindings, 0);
        ExtractionAttempts.Clear();
        ExtractionSuccesses.Clear();
        ClassifiedByLabel.Clear();
        DetectionsByCategory.Clear();
    }

    // ── Prometheus rendering ─────────────────────────────────────────────────

    private const string Prefix = "seismic_connector_";

    /// <summary>
    /// Render the registry in Prometheus text exposition format (v0.0.4): a
    /// <c># HELP</c> and <c># TYPE</c> line per metric followed by the sample
    /// line. Metric names are prefixed <c>seismic_connector_</c>. Uptime is
    /// computed at render time.
    /// </summary>
    public static string RenderPrometheus()
    {
        var sb = new StringBuilder(2048);

        Counter(sb, "items_ingested_total", "Total Seismic items successfully ingested into the Graph connection.", ItemsIngested);
        Counter(sb, "items_failed_total", "Total items that failed to ingest.", ItemsFailed);
        Counter(sb, "items_deleted_total", "Total items deleted from the Graph connection.", ItemsDeleted);
        Counter(sb, "items_skipped_total", "Total items skipped (e.g. already checkpointed).", ItemsSkipped);
        Counter(sb, "crawls_started_total", "Total crawl/ingestion runs started.", CrawlsStarted);
        Counter(sb, "crawls_completed_total", "Total crawl/ingestion runs completed.", CrawlsCompleted);
        Counter(sb, "throttled_429_total", "Total HTTP 429 (throttling) responses observed from the Graph API.", Throttle429Total);
        Counter(sb, "items_reacled_total", "Total items whose ACL was refreshed after a permission change (re-ACL).", ItemsReAcled);
        Counter(sb, "acl_drift_detected_total", "Total items whose resolved ACL drifted from the indexed ACL.", AclDriftDetected);
        Counter(sb, "degraded_pauses_total", "Total crawls that paused into degraded mode (a critical breaker was open).", DegradedPauses);

        // Per-dependency circuit-breaker state (0=closed, 1=open, 2=half-open)
        // plus trip/reset counters, so a scrape shows dependency health.
        RenderCircuitBreakers(sb);

        LabeledCounter(sb, "extraction_attempts_total",
            "Content-extraction attempts per payload format.", ExtractionAttempts);
        LabeledCounter(sb, "extraction_success_total",
            "Content extractions that produced non-empty text, per payload format.", ExtractionSuccesses);

        LabeledCounter(sb, "items_classified_total",
            "Items classified, by unified sensitivity label.", ClassifiedByLabel, "label");
        LabeledCounter(sb, "sensitive_detections_total",
            "Sensitive-data detections, by category (PII/PCI/secret/MNE-adjacent).", DetectionsByCategory, "category");

        Gauge(sb, "dead_letter_depth", "Current number of records in the dead-letter queue.", DeadLetterDepth);
        Gauge(sb, "last_crawl_completed_timestamp_seconds", "Unix timestamp (seconds) of the last completed crawl; 0 if none yet.", LastCrawlCompletedUnix);
        Gauge(sb, "last_drift_findings", "Finding count of the last reconcile drift sweep; 0 when in sync or never run.", LastDriftFindings);
        GaugeDouble(sb, "uptime_seconds", "Seconds since the connector process started.", UptimeSeconds);

        // Tracing state: a labelled gauge whose value is 1 when OTLP export is
        // registered (0 otherwise); the endpoint/service ride as labels so a
        // scrape shows where traces go.
        var endpoint = Tracing.ExporterEndpoint ?? "";
        sb.Append("# HELP ").Append(Prefix).Append("tracing_enabled Whether OpenTelemetry OTLP trace export is active (1) or off (0).\n");
        sb.Append("# TYPE ").Append(Prefix).Append("tracing_enabled gauge\n");
        sb.Append(Prefix).Append("tracing_enabled{exporter_endpoint=\"")
            .Append(EscapeLabel(endpoint)).Append("\",service_name=\"")
            .Append(EscapeLabel(Tracing.ServiceName)).Append("\"} ")
            .Append(Tracing.Enabled ? "1" : "0").Append('\n');

        return sb.ToString();
    }

    /// <summary>Escape a Prometheus label value (backslash, quote, newline).</summary>
    private static string EscapeLabel(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    /// <summary>Render the per-dependency circuit-breaker state gauge + trip/reset counters.</summary>
    private static void RenderCircuitBreakers(StringBuilder sb)
    {
        var breakers = CircuitBreakerRegistry.All;

        sb.Append("# HELP ").Append(Prefix)
            .Append("circuit_breaker_state Circuit-breaker state per dependency (0=closed, 1=open, 2=half-open).\n");
        sb.Append("# TYPE ").Append(Prefix).Append("circuit_breaker_state gauge\n");
        foreach (var breaker in breakers)
        {
            sb.Append(Prefix).Append("circuit_breaker_state{dependency=\"")
                .Append(EscapeLabel(breaker.Name)).Append("\"} ")
                .Append((int)breaker.State).Append('\n');
        }

        sb.Append("# HELP ").Append(Prefix)
            .Append("circuit_breaker_trips_total Times a dependency breaker opened (closed→open).\n");
        sb.Append("# TYPE ").Append(Prefix).Append("circuit_breaker_trips_total counter\n");
        foreach (var breaker in breakers)
        {
            sb.Append(Prefix).Append("circuit_breaker_trips_total{dependency=\"")
                .Append(EscapeLabel(breaker.Name)).Append("\"} ")
                .Append(breaker.TripCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        sb.Append("# HELP ").Append(Prefix)
            .Append("circuit_breaker_resets_total Times a dependency breaker recovered (half-open→closed).\n");
        sb.Append("# TYPE ").Append(Prefix).Append("circuit_breaker_resets_total counter\n");
        foreach (var breaker in breakers)
        {
            sb.Append(Prefix).Append("circuit_breaker_resets_total{dependency=\"")
                .Append(EscapeLabel(breaker.Name)).Append("\"} ")
                .Append(breaker.ResetCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
    }

    private static void LabeledCounter(
        StringBuilder sb, string name, string help, ConcurrentDictionary<string, long> values,
        string labelName = "format")
    {
        var full = Prefix + name;
        sb.Append("# HELP ").Append(full).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(full).Append(' ').Append("counter").Append('\n');
        foreach (var (label, value) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append(full).Append('{').Append(labelName).Append("=\"").Append(EscapeLabel(label)).Append("\"} ")
                .Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
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
        // '\n' line endings; HELP text needs backslash and newline escaping per
        // the exposition format, but our help strings contain neither.
        sb.Append("# HELP ").Append(full).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(full).Append(' ').Append(type).Append('\n');
        sb.Append(full).Append(' ').Append(value).Append('\n');
    }
}
