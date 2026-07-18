// Infrastructure/Metrics.cs
// -------------------------
// Process-wide metrics registry: thread-safe counters/gauges (Interlocked
// longs) plus a Prometheus text-exposition renderer, served by HealthEndpoint
// on /metrics. No external dependencies.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace HadoopConnector.Infrastructure;

public static class Metrics
{
    // Labelled counters (classification): keyed by label / category value.
    private static readonly ConcurrentDictionary<string, long> ItemsClassified = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> SensitiveDetections = new(StringComparer.Ordinal);

    // Labelled counters (filter layer): records filtered, keyed by stage.
    private static readonly ConcurrentDictionary<string, long> RecordsFilteredByStage = new(StringComparer.Ordinal);

    // Monotonic counters.
    private static long _itemsIngested;
    private static long _itemsFailed;
    private static long _itemsDeleted;
    private static long _itemsSkipped;
    private static long _crawlsStarted;
    private static long _crawlsCompleted;
    private static long _throttle429Total;
    private static long _hdfsCalls;
    private static long _partitionsScanned;
    private static long _partitionsPruned;
    private static long _recordsScanned;
    private static long _recordsMatched;

    // Monotonic counters (enterprise ops pack — dashboard/alert anchors).
    private static long _guardRefusals;
    private static long _partialObjects;
    private static long _sweepsSuppressed;

    // Gauges.
    private static long _deadLetterDepth;
    private static long _lastCrawlCompletedUnix;
    private static long _haClaimsHeld;

    private static readonly DateTime StartUtc = DateTime.UtcNow;

    public static void IncItemsIngested(long count = 1) => Interlocked.Add(ref _itemsIngested, count);
    public static void IncItemsFailed(long count = 1) => Interlocked.Add(ref _itemsFailed, count);
    public static void IncItemsDeleted(long count = 1) => Interlocked.Add(ref _itemsDeleted, count);
    public static void IncItemsSkipped(long count = 1) => Interlocked.Add(ref _itemsSkipped, count);
    public static void IncCrawlsStarted(long count = 1) => Interlocked.Add(ref _crawlsStarted, count);
    public static void IncCrawlsCompleted(long count = 1) => Interlocked.Add(ref _crawlsCompleted, count);
    public static void IncThrottle429(long count = 1) => Interlocked.Add(ref _throttle429Total, count);
    public static void IncHdfsCalls(long count = 1) => Interlocked.Add(ref _hdfsCalls, count);
    public static void IncPartitionsScanned(long count = 1) => Interlocked.Add(ref _partitionsScanned, count);
    public static void IncPartitionsPruned(long count = 1) => Interlocked.Add(ref _partitionsPruned, count);
    public static void IncRecordsScanned(long count = 1) => Interlocked.Add(ref _recordsScanned, count);
    public static void IncRecordsMatched(long count = 1) => Interlocked.Add(ref _recordsMatched, count);

    /// <summary>Record rows removed by a filter stage ("partition" | "predicate" | "rowCap").</summary>
    public static void IncRecordsFiltered(string stage, long count = 1) =>
        RecordsFilteredByStage.AddOrUpdate(stage, count, (_, v) => v + count);

    /// <summary>Record one fail-closed scale-guard refusal (FullScanRefusedException).</summary>
    public static void IncGuardRefusals(long count = 1) => Interlocked.Add(ref _guardRefusals, count);

    /// <summary>Record one object marked PARTIAL (row cap hit or oversize file skipped).</summary>
    public static void IncPartialObjects(long count = 1) => Interlocked.Add(ref _partialObjects, count);

    /// <summary>Record one deletion sweep suppressed (incomplete fetch or a mass-deletion guard).</summary>
    public static void IncSweepsSuppressed(long count = 1) => Interlocked.Add(ref _sweepsSuppressed, count);

    /// <summary>Adjust the HA object-claim leases currently held by THIS node (+1/-1).</summary>
    public static void AddHaClaimsHeld(long delta) =>
        Interlocked.Add(ref _haClaimsHeld, delta);

    /// <summary>Record one item classified with the given sensitivity label.</summary>
    public static void IncItemsClassified(string label) =>
        ItemsClassified.AddOrUpdate(label, 1, (_, v) => v + 1);

    /// <summary>Record one sensitive-data detection of the given category.</summary>
    public static void IncSensitiveDetection(string category) =>
        SensitiveDetections.AddOrUpdate(category, 1, (_, v) => v + 1);

    public static void SetDeadLetterDepth(long depth) => Interlocked.Exchange(ref _deadLetterDepth, depth);

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
    public static long HdfsCalls => Interlocked.Read(ref _hdfsCalls);
    public static long PartitionsScanned => Interlocked.Read(ref _partitionsScanned);
    public static long PartitionsPruned => Interlocked.Read(ref _partitionsPruned);
    public static long RecordsScanned => Interlocked.Read(ref _recordsScanned);
    public static long RecordsMatched => Interlocked.Read(ref _recordsMatched);
    public static long GuardRefusals => Interlocked.Read(ref _guardRefusals);
    public static long PartialObjects => Interlocked.Read(ref _partialObjects);
    public static long SweepsSuppressed => Interlocked.Read(ref _sweepsSuppressed);
    public static long DeadLetterDepth => Interlocked.Read(ref _deadLetterDepth);
    public static long HaClaimsHeld => Interlocked.Read(ref _haClaimsHeld);

    /// <summary>Read the filtered-record count for a stage (0 when absent).</summary>
    public static long RecordsFilteredFor(string stage) =>
        RecordsFilteredByStage.TryGetValue(stage, out var v) ? v : 0;
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
        Interlocked.Exchange(ref _hdfsCalls, 0);
        Interlocked.Exchange(ref _partitionsScanned, 0);
        Interlocked.Exchange(ref _partitionsPruned, 0);
        Interlocked.Exchange(ref _recordsScanned, 0);
        Interlocked.Exchange(ref _recordsMatched, 0);
        Interlocked.Exchange(ref _guardRefusals, 0);
        Interlocked.Exchange(ref _partialObjects, 0);
        Interlocked.Exchange(ref _sweepsSuppressed, 0);
        Interlocked.Exchange(ref _deadLetterDepth, 0);
        Interlocked.Exchange(ref _lastCrawlCompletedUnix, 0);
        Interlocked.Exchange(ref _haClaimsHeld, 0);
        ItemsClassified.Clear();
        SensitiveDetections.Clear();
        RecordsFilteredByStage.Clear();
    }

    private const string Prefix = "hadoop_connector_";

    /// <summary>Prometheus text exposition format v0.0.4.</summary>
    public static string RenderPrometheus()
    {
        var sb = new StringBuilder(2048);

        Counter(sb, "items_ingested_total", "Total BDH items successfully ingested into the Graph connection.", ItemsIngested);
        Counter(sb, "items_failed_total", "Total items that failed to ingest.", ItemsFailed);
        Counter(sb, "items_deleted_total", "Total items deleted from the Graph connection.", ItemsDeleted);
        Counter(sb, "items_skipped_total", "Total items skipped (e.g. already checkpointed).", ItemsSkipped);
        Counter(sb, "crawls_started_total", "Total crawl/ingestion runs started.", CrawlsStarted);
        Counter(sb, "crawls_completed_total", "Total crawl/ingestion runs completed.", CrawlsCompleted);
        Counter(sb, "throttled_429_total", "Total HTTP 429 (throttling) responses observed from the Graph API.", Throttle429Total);
        Counter(sb, "hdfs_calls_total", "Total WebHDFS REST calls issued by this process.", HdfsCalls);
        Counter(sb, "partitions_scanned_total", "Total BDH partition directories whose files were read.", PartitionsScanned);
        Counter(sb, "partitions_pruned_total", "Total BDH partition directories pruned before any file I/O (dt watermark or partition filters).", PartitionsPruned);
        Counter(sb, "records_scanned_total", "Total BDH rows read from source files.", RecordsScanned);
        Counter(sb, "records_matched_total", "Total BDH rows that passed the filter layer and entered the pipeline.", RecordsMatched);
        Counter(sb, "guard_refusals_total", "Total fail-closed scale-guard refusals (object with no effective filter refused to crawl).", GuardRefusals);
        Counter(sb, "partial_objects_total", "Total object crawls marked PARTIAL (row cap hit or an oversize file skipped).", PartialObjects);
        Counter(sb, "sweeps_suppressed_total", "Total deletion sweeps suppressed (incomplete source fetch or a mass-deletion safety guard).", SweepsSuppressed);

        Gauge(sb, "tracing_enabled", "1 when OpenTelemetry OTLP export is registered (OTEL_EXPORTER_OTLP_ENDPOINT set), else 0.", Tracing.Enabled ? 1 : 0);
        Gauge(sb, "dead_letter_depth", "Current number of records in the dead-letter queue.", DeadLetterDepth);
        Gauge(sb, "last_crawl_completed_timestamp_seconds", "Unix timestamp (seconds) of the last completed crawl; 0 if none yet.", LastCrawlCompletedUnix);
        Gauge(sb, "ha_claims_held", "HA object-claim leases currently held by this node (0 outside HA mode).", HaClaimsHeld);
        GaugeDouble(sb, "uptime_seconds", "Seconds since the connector process started.", UptimeSeconds);

        // Per-dependency circuit-breaker state + trip/reset counters.
        Breakers.AppendMetrics(sb);

        // Per-stage filter accounting (only present once something is filtered).
        LabelledCounter(sb, "records_filtered_total",
            "BDH rows removed by the filter layer, by stage.", "stage", RecordsFilteredByStage);

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
