// Infrastructure/Metrics.cs
// -------------------------
// In-process counters/gauges rendered as Prometheus text (exposition format
// 0.0.4) by the /metrics endpoint. All members are thread-safe.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace AltrataConnector.Infrastructure;

public static class Metrics
{
    private sealed record MetricDef(string Name, string Help, string Type);

    private static readonly ConcurrentDictionary<string, double> Values = new();

    private static readonly MetricDef[] Definitions =
    {
        new("altrata_items_ingested_total", "External items successfully ingested into Microsoft Graph.", "counter"),
        new("altrata_items_failed_total", "Items that exhausted retries and were dead-lettered.", "counter"),
        new("altrata_items_deleted_total", "External items withdrawn (purge / retention).", "counter"),
        new("altrata_graph_requests_total", "Microsoft Graph HTTP requests issued.", "counter"),
        new("altrata_graph_retries_total", "Microsoft Graph requests retried after 429/5xx.", "counter"),
        new("altrata_deadletter_depth", "Replayable records currently waiting in the dead-letter queue.", "gauge"),
        new("altrata_deadletter_unreplayable", "Un-replayable dead-letter records (transform failures) awaiting re-ingest or retirement.", "gauge"),
        new("altrata_deliveries_processed_total", "Feed deliveries fully processed (checksums valid, reconciled).", "counter"),
        new("altrata_deliveries_rejected_total", "Feed deliveries rejected (checksum mismatch).", "counter"),
        new("altrata_api_billable_lookups_total", "Billable Altrata enrichment API lookups (lifetime, persisted).", "counter"),
        new("altrata_seat_count", "Seat principals currently entitled to see results.", "gauge"),
        new("altrata_reacl_passes_total", "ACL update passes triggered by seat-list changes.", "counter"),
        new("altrata_path_index_edges", "Relationship-path index edges after the last rebuild.", "gauge"),
        new("altrata_subjects_erased_total", "Subjects erased via forget-subject (DSAR).", "counter"),
        new("altrata_items_erased_total", "External items withdrawn via per-subject erasure.", "counter"),
        new("altrata_suppression_list_size", "Erased subject ids currently suppressed from re-ingestion.", "gauge"),
        new("altrata_tracing_enabled", "1 when OpenTelemetry OTLP tracing is exporting, else 0.", "gauge"),
        new("altrata_breaker_open", "1 when a critical dependency circuit breaker is open (degraded), else 0.", "gauge"),
        new("altrata_items_suppressed_total", "Records skipped at crawl time because the subject is suppressed.", "counter"),
        new("altrata_crawl_in_progress", "1 while a crawl is running, else 0.", "gauge"),
        new("altrata_last_full_crawl_timestamp_seconds", "Unix time of the last completed full crawl.", "gauge"),
        new("altrata_last_incremental_crawl_timestamp_seconds", "Unix time of the last completed incremental crawl.", "gauge"),
    };

    public static void Increment(string name, double delta = 1) =>
        Values.AddOrUpdate(name, delta, (_, old) => old + delta);

    public static void Set(string name, double value) =>
        Values[name] = value;

    public static double Get(string name) =>
        Values.TryGetValue(name, out var v) ? v : 0;

    public static void SetDeadLetterDepth(int depth) => Set("altrata_deadletter_depth", depth);

    /// <summary>Reset all values (test seam).</summary>
    internal static void ResetForTests() => Values.Clear();

    public static string RenderPrometheus()
    {
        var sb = new StringBuilder();
        foreach (var def in Definitions)
        {
            sb.Append("# HELP ").Append(def.Name).Append(' ').Append(def.Help).Append('\n');
            sb.Append("# TYPE ").Append(def.Name).Append(' ').Append(def.Type).Append('\n');
            sb.Append(def.Name).Append(' ')
              .Append(Get(def.Name).ToString("0.############", CultureInfo.InvariantCulture))
              .Append('\n');
        }
        return sb.ToString();
    }
}
