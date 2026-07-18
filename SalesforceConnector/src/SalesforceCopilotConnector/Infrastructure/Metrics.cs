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

using System.Globalization;
using System.Text;

namespace SalesforceCopilotConnector.Infrastructure;

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

    // Gauges (can go up or down / be set to an absolute value).
    private static long _deadLetterDepth;
    private static long _lastCrawlCompletedUnix;
    private static long _adaptiveConcurrencyLevel;
    private static long _haClaimsHeld;

    // Per-object crawl progress (records fetched vs the count reported by
    // Salesforce at crawl start). Rendered as two labeled gauge families.
    // ConcurrentDictionary: written from parallel object workers.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long>
        ObjectRecordsTotal = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long>
        ObjectRecordsFetched = new();

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

    // ── Gauge setters ────────────────────────────────────────────────────────

    /// <summary>Set the current dead-letter queue depth.</summary>
    public static void SetDeadLetterDepth(long depth) => Interlocked.Exchange(ref _deadLetterDepth, depth);

    /// <summary>Set the last-crawl-completed timestamp (unix seconds).</summary>
    public static void SetLastCrawlCompletedUnix(long unixSeconds) =>
        Interlocked.Exchange(ref _lastCrawlCompletedUnix, unixSeconds);

    /// <summary>
    /// Convenience: stamp <see cref="SetLastCrawlCompletedUnix"/> with the current
    /// UTC time. Call from the crawl-completion path.
    /// </summary>
    public static void MarkCrawlCompletedNow() =>
        SetLastCrawlCompletedUnix(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    /// <summary>Set the current adaptive Graph concurrency level (AdaptiveConcurrency seam).</summary>
    public static void SetAdaptiveConcurrency(long level) =>
        Interlocked.Exchange(ref _adaptiveConcurrencyLevel, level);

    /// <summary>An HA object claim's heartbeat started on this node.</summary>
    public static void IncHaClaimsHeld() => Interlocked.Increment(ref _haClaimsHeld);

    /// <summary>An HA object claim's heartbeat ended (claim completed/released).</summary>
    public static void DecHaClaimsHeld()
    {
        // Clamp at zero: a double-dispose must not push the gauge negative.
        long current;
        while ((current = Interlocked.Read(ref _haClaimsHeld)) > 0)
        {
            if (Interlocked.CompareExchange(ref _haClaimsHeld, current - 1, current) == current)
                return;
        }
    }

    // ── Per-object crawl progress ────────────────────────────────────────────

    /// <summary>Record the Salesforce-reported record count for one object type (crawl start).</summary>
    public static void SetObjectTotal(string objectType, long total) =>
        ObjectRecordsTotal[objectType] = total;

    /// <summary>Add fetched records for one object type (per-chunk seam).</summary>
    public static void AddObjectFetched(string objectType, long count) =>
        ObjectRecordsFetched.AddOrUpdate(objectType, count, (_, previous) => previous + count);

    /// <summary>Clear per-object progress at the start of a crawl cycle.</summary>
    public static void ResetObjectProgress()
    {
        ObjectRecordsTotal.Clear();
        ObjectRecordsFetched.Clear();
    }

    // ── Read-only accessors (handy for tests) ────────────────────────────────

    public static long ItemsIngested => Interlocked.Read(ref _itemsIngested);
    public static long ItemsFailed => Interlocked.Read(ref _itemsFailed);
    public static long ItemsDeleted => Interlocked.Read(ref _itemsDeleted);
    public static long ItemsSkipped => Interlocked.Read(ref _itemsSkipped);
    public static long CrawlsStarted => Interlocked.Read(ref _crawlsStarted);
    public static long CrawlsCompleted => Interlocked.Read(ref _crawlsCompleted);
    public static long Throttle429Total => Interlocked.Read(ref _throttle429Total);
    public static long DeadLetterDepth => Interlocked.Read(ref _deadLetterDepth);
    public static long LastCrawlCompletedUnix => Interlocked.Read(ref _lastCrawlCompletedUnix);
    public static long AdaptiveConcurrencyLevel => Interlocked.Read(ref _adaptiveConcurrencyLevel);
    public static long HaClaimsHeld => Interlocked.Read(ref _haClaimsHeld);

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
        Interlocked.Exchange(ref _deadLetterDepth, 0);
        Interlocked.Exchange(ref _lastCrawlCompletedUnix, 0);
        Interlocked.Exchange(ref _adaptiveConcurrencyLevel, 0);
        Interlocked.Exchange(ref _haClaimsHeld, 0);
        ResetObjectProgress();
    }

    // ── Prometheus rendering ─────────────────────────────────────────────────

    private const string Prefix = "salesforce_connector_";

    /// <summary>
    /// Render the registry in Prometheus text exposition format (v0.0.4): a
    /// <c># HELP</c> and <c># TYPE</c> line per metric followed by the sample
    /// line. Metric names are prefixed <c>salesforce_connector_</c>. Uptime is
    /// computed at render time.
    /// </summary>
    public static string RenderPrometheus()
    {
        var sb = new StringBuilder(2048);

        Counter(sb, "items_ingested_total", "Total Salesforce items successfully ingested into the Graph connection.", ItemsIngested);
        Counter(sb, "items_failed_total", "Total items that failed to ingest.", ItemsFailed);
        Counter(sb, "items_deleted_total", "Total items deleted from the Graph connection.", ItemsDeleted);
        Counter(sb, "items_skipped_total", "Total items skipped (e.g. already checkpointed).", ItemsSkipped);
        Counter(sb, "crawls_started_total", "Total crawl/ingestion runs started.", CrawlsStarted);
        Counter(sb, "crawls_completed_total", "Total crawl/ingestion runs completed.", CrawlsCompleted);
        Counter(sb, "throttled_429_total", "Total HTTP 429 (throttling) responses observed from the Graph API.", Throttle429Total);

        Gauge(sb, "dead_letter_depth", "Current number of records in the dead-letter queue.", DeadLetterDepth);
        Gauge(sb, "last_crawl_completed_timestamp_seconds", "Unix timestamp (seconds) of the last completed crawl; 0 if none yet.", LastCrawlCompletedUnix);
        Gauge(sb, "adaptive_concurrency_level", "Current adaptive Graph batch concurrency level (dials down on 429s, up on sustained success); 0 until the first crawl.", AdaptiveConcurrencyLevel);
        Gauge(sb, "ha_claims_held", "HA object claims currently held (heartbeating) by this node; 0 outside HA mode.", HaClaimsHeld);
        GaugeDouble(sb, "uptime_seconds", "Seconds since the connector process started.", UptimeSeconds);

        LabeledGaugeFamily(
            sb, "object_records_total",
            "Salesforce-reported record count per object type for the current crawl.",
            ObjectRecordsTotal);
        LabeledGaugeFamily(
            sb, "object_records_fetched",
            "Records fetched from Salesforce per object type in the current crawl.",
            ObjectRecordsFetched);

        return sb.ToString();
    }

    /// <summary>
    /// Render a gauge family with an <c>object_type</c> label, one sample per
    /// object type (sorted for stable output). Families with no samples are
    /// omitted entirely — Prometheus treats absent series as absent, and this
    /// keeps default scrapes byte-stable with the pre-family output.
    /// </summary>
    private static void LabeledGaugeFamily(
        StringBuilder sb,
        string name,
        string help,
        System.Collections.Concurrent.ConcurrentDictionary<string, long> samples)
    {
        if (samples.IsEmpty)
            return;
        var full = Prefix + name;
        sb.Append("# HELP ").Append(full).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(full).Append(' ').Append("gauge").Append('\n');
        foreach (var (objectType, value) in samples.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append(full)
              .Append("{object_type=\"").Append(EscapeLabelValue(objectType)).Append("\"} ")
              .Append(value.ToString(CultureInfo.InvariantCulture))
              .Append('\n');
        }
    }

    /// <summary>Prometheus label-value escaping: backslash, double quote, newline.</summary>
    internal static string EscapeLabelValue(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

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
