// Hdfs/BdhFetcher.cs
// ------------------
// The BDH source fetch: partition pruning → streamed parsing → record
// predicates → row cap, with per-stage accounting. This is where the filter
// layer's guarantees are enforced:
//
//   FAIL-CLOSED SCALE GUARD — an object with NO filter configured refuses to
//   crawl (FullScanRefusedException) unless ALLOW_FULL_SCAN=true or the
//   object is listed in filters.json fullScanAllowed. At 150M+ rows an
//   accidental unfiltered scan is an outage, so the default is refusal.
//
//   ROW CAP (BDH_MAX_RECORDS_PER_OBJECT) — a safety valve, never a silent
//   truncation: hitting it logs, raises a webhook alert, marks the fetch
//   Truncated (the crawl records a partial object and SKIPS the deletion
//   sweep for it, because the source id set is incomplete).
//
//   WATERMARK / LAG — incremental crawls only read dt partitions newer than
//   (last sync − BDH_LAG_HOURS): the overlap window re-reads the newest
//   partitions to absorb BDH's late-arriving nightly loads.

using HadoopConnector.Config;
using HadoopConnector.Filters;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Hdfs;

/// <summary>Thrown when the fail-closed scale guard refuses an unfiltered crawl.</summary>
public sealed class FullScanRefusedException : Exception
{
    public FullScanRefusedException(string objectName)
        : base($"'{objectName}' has no filter configured in config/filters.json. At BDH scale an "
               + "unfiltered scan is refused by default — add partition/record filters, list the "
               + "object under fullScanAllowed, or set ALLOW_FULL_SCAN=true to override.")
        => ObjectName = objectName;

    public string ObjectName { get; }
}

public sealed class FetchStats
{
    public int PartitionsScanned { get; set; }
    public int PartitionsPruned { get; set; }
    public int FilesRead { get; set; }
    public int FilesSkippedOversize { get; set; }
    public long RecordsScanned { get; set; }
    public long RecordsFilteredPredicate { get; set; }
    public long RecordsDroppedNoId { get; set; }
    public long RecordsMalformed { get; set; }
    public long RecordsMatched { get; set; }
}

public sealed class FetchResult
{
    public List<BdhRecord> Records { get; init; } = new();
    public FetchStats Stats { get; init; } = new();

    /// <summary>True when BDH_MAX_RECORDS_PER_OBJECT stopped the fetch early —
    /// the crawl is partial for this object and the deletion sweep must not run.</summary>
    public bool Truncated { get; set; }
}

public sealed class BdhFetcher
{
    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector.fetch");

    private readonly AppConfig _config;
    private readonly IBdhSource _source;
    private readonly FilterSet _filters;
    private readonly FilterEngine _engine;
    private readonly PartitionScanner _scanner;
    private readonly Func<DateTime> _nowUtc;

    public BdhFetcher(AppConfig config, IBdhSource source, FilterSet filters, Func<DateTime>? nowUtc = null)
    {
        _config = config;
        _source = source;
        _filters = filters;
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
        _engine = new FilterEngine(_nowUtc);
        _scanner = new PartitionScanner(source, _engine);
    }

    public IBdhSource Source => _source;

    public FilterSet Filters => _filters;

    /// <summary>Root directory (relative to the BDH root) for one object type.</summary>
    internal static string ObjectRoot(ObjectConfig objectConfig) =>
        string.IsNullOrWhiteSpace(objectConfig.SourcePath)
            ? objectConfig.ObjectName
            : objectConfig.SourcePath.Trim('/');

    /// <summary>
    /// The minimum dt partition an incremental crawl must read:
    /// (since − BDH_LAG_HOURS) as a date. Full crawls return null (no dt
    /// watermark; partition filters still apply).
    /// </summary>
    internal static DateOnly? MinDtFor(bool fullCrawl, DateTime? sinceUtc, int lagHours)
    {
        if (fullCrawl || sinceUtc is not { } since)
            return null;
        return DateOnly.FromDateTime(since.AddHours(-Math.Max(0, lagHours)));
    }

    /// <summary>Enforce the fail-closed scale guard for one object type.</summary>
    public void GuardFullScan(ObjectConfig objectConfig)
    {
        var filter = _filters.For(objectConfig.ObjectName);
        if (filter is { HasAnyFilter: true })
            return;
        if (_filters.IsFullScanAllowed(objectConfig.ObjectName))
            return;
        if (_config.AllowFullScan)
            return;
        throw new FullScanRefusedException(objectConfig.ObjectName);
    }

    /// <summary>Objects in the schema that would trip the guard (validate-config --strict).</summary>
    public List<string> UnfilteredObjects(IEnumerable<ObjectConfig> objects)
    {
        var refused = new List<string>();
        foreach (var objectConfig in objects)
        {
            var filter = _filters.For(objectConfig.ObjectName);
            if (filter is { HasAnyFilter: true })
                continue;
            if (_filters.IsFullScanAllowed(objectConfig.ObjectName))
                continue;
            refused.Add(objectConfig.ObjectName);
        }
        return refused;
    }

    // ── Crawl fetch ──────────────────────────────────────────────────────────

    public async Task<FetchResult> FetchAsync(
        ObjectConfig objectConfig,
        bool fullCrawl,
        DateTime? sinceUtc,
        CancellationToken ct = default,
        bool bypassScanGuard = false)
    {
        if (!bypassScanGuard)
            GuardFullScan(objectConfig);

        using var span = Tracing.StartSourceFetch(objectConfig.ObjectName, "bdh");
        var result = new FetchResult();
        var stats = result.Stats;
        var filter = _filters.For(objectConfig.ObjectName);
        var minDt = MinDtFor(fullCrawl, sinceUtc, _config.BdhLagHours);
        var root = ObjectRoot(objectConfig);

        var scanStats = new PartitionScanStats();
        var partitions = await _scanner.ScanAsync(
                root, filter?.Partition ?? new List<FilterPredicate>(), minDt, scanStats, ct)
            .ConfigureAwait(false);
        stats.PartitionsScanned = scanStats.PartitionsScanned;
        stats.PartitionsPruned = scanStats.PartitionsPruned;
        Metrics.IncPartitionsScanned(scanStats.PartitionsScanned);
        Metrics.IncPartitionsPruned(scanStats.PartitionsPruned);

        var cap = _config.BdhMaxRecordsPerObject;
        var seenIdWarning = false;

        foreach (var partition in partitions)
        {
            ct.ThrowIfCancellationRequested();
            var dataAsOf = partition.Dt?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            foreach (var file in partition.Files)
            {
                ct.ThrowIfCancellationRequested();
                if (file.Length > _config.BdhMaxFileBytes)
                {
                    stats.FilesSkippedOversize++;
                    Logger.Warning(
                        $"{objectConfig.ObjectName}: skipping oversize file "
                        + $"'{partition.RelativePath}/{file.PathSuffix}' ({file.Length} bytes > "
                        + $"BDH_MAX_FILE_BYTES={_config.BdhMaxFileBytes}).");
                    continue;
                }
                var filePath = PartitionScanner.Join(partition.RelativePath, file.PathSuffix);
                var fileAsOf = dataAsOf ?? (file.ModificationTimeMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(file.ModificationTimeMs)
                        .UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                    : null);

                var parser = new BdhFileParser();
                var stream = await _source.OpenAsync(filePath, ct).ConfigureAwait(false);
                stats.FilesRead++;
                foreach (var row in parser.Parse(stream, file.PathSuffix, _config.BdhMaxFileBytes))
                {
                    ct.ThrowIfCancellationRequested();
                    stats.RecordsScanned++;
                    Metrics.IncRecordsScanned();

                    var record = new BdhRecord(objectConfig.ObjectName, row, fileAsOf, filePath);
                    if (!_engine.MatchesRecord(filter, record))
                    {
                        stats.RecordsFilteredPredicate++;
                        Metrics.IncRecordsFiltered("predicate");
                        continue;
                    }
                    if (!BdhRecord.IsSafeItemId(record.RawId))
                    {
                        stats.RecordsDroppedNoId++;
                        if (!seenIdWarning)
                        {
                            seenIdWarning = true;
                            Logger.Error(
                                $"{objectConfig.ObjectName}: dropping row(s) with a missing/unsafe id "
                                + $"in '{filePath}' — each needs an 'Id' column with a Graph-safe value.");
                        }
                        continue;
                    }
                    result.Records.Add(record);
                    stats.RecordsMatched++;
                    Metrics.IncRecordsMatched();

                    if (cap > 0 && result.Records.Count >= cap)
                    {
                        result.Truncated = true;
                        break;
                    }
                }
                stats.RecordsMalformed += parser.ParseErrors;
                if (result.Truncated)
                    break;
            }
            if (result.Truncated)
                break;
        }

        if (result.Truncated)
        {
            Logger.Warning(
                $"{objectConfig.ObjectName}: BDH_MAX_RECORDS_PER_OBJECT={cap} row cap hit — the crawl "
                + "is PARTIAL for this object (deletion sweep skipped). Tighten config/filters.json "
                + "or raise the cap.");
            await Alerting.RaiseAsync(
                "row_cap_hit",
                $"'{objectConfig.ObjectName}' hit the BDH_MAX_RECORDS_PER_OBJECT={cap} row cap; "
                + "crawl marked partial",
                new Dictionary<string, object?>
                {
                    ["objectType"] = objectConfig.ObjectName,
                    ["cap"] = cap,
                    ["recordsScanned"] = stats.RecordsScanned,
                }).ConfigureAwait(false);
        }

        span?.SetTag("bdh.records_scanned", stats.RecordsScanned);
        span?.SetTag("bdh.records_matched", stats.RecordsMatched);
        span?.SetTag("bdh.partitions_pruned", stats.PartitionsPruned);
        Logger.Info(
            $"{objectConfig.ObjectName}: partitions {stats.PartitionsScanned} scanned / "
            + $"{stats.PartitionsPruned} pruned; records {stats.RecordsScanned} scanned / "
            + $"{stats.RecordsFilteredPredicate} filtered / {stats.RecordsMatched} matched"
            + (stats.RecordsMalformed > 0 ? $"; {stats.RecordsMalformed} malformed row(s) skipped" : string.Empty)
            + (stats.RecordsDroppedNoId > 0 ? $"; {stats.RecordsDroppedNoId} row(s) without a usable id" : string.Empty)
            + ".");
        return result;
    }

    // ── Targeted single-record lookup (ingest-item / retry-failed) ───────────

    /// <summary>
    /// Find one record by Salesforce id, scanning NEWEST partitions first and
    /// stopping at the first match. Record predicates are bypassed (a targeted
    /// operator action), partition filters and bounds still apply.
    /// </summary>
    public async Task<BdhRecord?> FindByIdAsync(
        ObjectConfig objectConfig, string id, CancellationToken ct = default)
    {
        if (!BdhRecord.IsSafeItemId(id))
        {
            Logger.Warning($"FindByIdAsync: rejecting unsafe record id for {objectConfig.ObjectName}.");
            return null;
        }
        var filter = _filters.For(objectConfig.ObjectName);
        var scanStats = new PartitionScanStats();
        var partitions = await _scanner.ScanAsync(
                ObjectRoot(objectConfig), filter?.Partition ?? new List<FilterPredicate>(),
                minDt: null, scanStats, ct)
            .ConfigureAwait(false);

        foreach (var partition in partitions.AsEnumerable().Reverse())  // newest dt first
        {
            var dataAsOf = partition.Dt?.ToString(
                "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            foreach (var file in partition.Files)
            {
                ct.ThrowIfCancellationRequested();
                if (file.Length > _config.BdhMaxFileBytes)
                    continue;
                var filePath = PartitionScanner.Join(partition.RelativePath, file.PathSuffix);
                var parser = new BdhFileParser();
                var stream = await _source.OpenAsync(filePath, ct).ConfigureAwait(false);
                foreach (var row in parser.Parse(stream, file.PathSuffix, _config.BdhMaxFileBytes))
                {
                    ct.ThrowIfCancellationRequested();
                    var record = new BdhRecord(objectConfig.ObjectName, row, dataAsOf, filePath);
                    if (string.Equals(record.RawId, id, StringComparison.Ordinal))
                        return record;
                }
            }
        }
        return null;
    }
}
