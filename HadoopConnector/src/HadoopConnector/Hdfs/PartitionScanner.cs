// Hdfs/PartitionScanner.cs
// ------------------------
// Hive-style partition discovery + pruning for the BDH layout:
//
//     {root}/{object}/dt=YYYY-MM-DD/...            (dt required by convention)
//     {root}/{object}/region=EMEA/dt=YYYY-MM-DD/   (extra keys allowed, any order)
//
// The scanner walks directories breadth-first and prunes BEFORE opening any
// file — the whole point at 150M+ rows:
//
//   • dt pruning      — partitions older than minDt (the incremental watermark
//     minus the BDH_LAG_HOURS overlap window) are skipped without listing
//     their contents.
//   • key filters     — partition predicates from config/filters.json prune a
//     directory the moment a PRESENT key mismatches (absent keys never prune;
//     they may appear deeper).
//
// Depth is capped (MaxDepth) so a pathological layout cannot recurse forever.
// Stats (partitions scanned/pruned) feed /metrics and the crawl logs.

using System.Globalization;
using HadoopConnector.Filters;

namespace HadoopConnector.Hdfs;

/// <summary>A leaf partition directory holding data files.</summary>
public sealed record PartitionDir(
    string RelativePath,
    IReadOnlyDictionary<string, string> Keys,
    DateOnly? Dt,
    IReadOnlyList<HdfsFileStatus> Files);

public sealed class PartitionScanStats
{
    public int PartitionsScanned { get; set; }
    public int PartitionsPruned { get; set; }
}

public sealed class PartitionScanner
{
    /// <summary>Recursion bound for the partition walk (a Hive layout is shallow).</summary>
    internal const int MaxDepth = 8;

    private readonly IBdhSource _source;
    private readonly FilterEngine _engine;

    public PartitionScanner(IBdhSource source, FilterEngine? engine = null)
    {
        _source = source;
        _engine = engine ?? new FilterEngine();
    }

    /// <summary>Parse one "key=value" path segment (Hive partition naming).</summary>
    internal static bool TryParseSegment(string name, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;
        var eq = name.IndexOf('=');
        if (eq <= 0 || eq == name.Length - 1)
            return false;
        key = name[..eq];
        value = Uri.UnescapeDataString(name[(eq + 1)..]);
        return true;
    }

    /// <summary>Parse a dt partition value (strict yyyy-MM-dd), or null.</summary>
    internal static DateOnly? ParseDt(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Enumerate the leaf partitions of <paramref name="objectRoot"/> that
    /// survive dt-watermark and partition-filter pruning, oldest dt first.
    /// </summary>
    public async Task<List<PartitionDir>> ScanAsync(
        string objectRoot,
        IReadOnlyList<FilterPredicate> partitionFilters,
        DateOnly? minDt,
        PartitionScanStats stats,
        CancellationToken ct = default)
    {
        var leaves = new List<PartitionDir>();
        await WalkAsync(
                objectRoot, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                partitionFilters, minDt, stats, leaves, depth: 0, ct)
            .ConfigureAwait(false);
        return leaves
            .OrderBy(p => p.Dt ?? DateOnly.MinValue)
            .ThenBy(p => p.RelativePath, StringComparer.Ordinal)
            .ToList();
    }

    private async Task WalkAsync(
        string path,
        Dictionary<string, string> keys,
        IReadOnlyList<FilterPredicate> partitionFilters,
        DateOnly? minDt,
        PartitionScanStats stats,
        List<PartitionDir> leaves,
        int depth,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > MaxDepth)
            return;

        var entries = await _source.ListAsync(path, ct).ConfigureAwait(false);
        var files = entries
            .Where(e => !e.IsDirectory
                        && !BdhFileParser.IsIgnorableEntry(e.PathSuffix)
                        && BdhFileParser.IsDataFile(e.PathSuffix))
            .ToList();
        var dirs = entries
            .Where(e => e.IsDirectory && !BdhFileParser.IsIgnorableEntry(e.PathSuffix))
            .ToList();

        if (files.Count > 0)
        {
            // Leaf (or mixed) directory: keys accumulated so far describe it.
            keys.TryGetValue("dt", out var dtRaw);
            var dt = dtRaw is null ? null : ParseDt(dtRaw);
            stats.PartitionsScanned++;
            leaves.Add(new PartitionDir(
                path,
                new Dictionary<string, string>(keys, StringComparer.OrdinalIgnoreCase),
                dt, files));
        }

        foreach (var dir in dirs)
        {
            if (TryParseSegment(dir.PathSuffix, out var key, out var value))
            {
                // dt watermark pruning — zero I/O below an out-of-window date.
                if (string.Equals(key, "dt", StringComparison.OrdinalIgnoreCase)
                    && minDt is { } bound
                    && ParseDt(value) is { } dt
                    && dt < bound)
                {
                    stats.PartitionsPruned++;
                    continue;
                }

                var childKeys = new Dictionary<string, string>(keys, StringComparer.OrdinalIgnoreCase)
                {
                    [key] = value,
                };
                // Partition-key filters — prune on a present-key mismatch.
                if (!_engine.MatchesPartition(partitionFilters, childKeys))
                {
                    stats.PartitionsPruned++;
                    continue;
                }
                await WalkAsync(
                        Join(path, dir.PathSuffix), childKeys, partitionFilters, minDt,
                        stats, leaves, depth + 1, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                // Non-partition sub-directory (e.g. a bucketing dir) — recurse.
                await WalkAsync(
                        Join(path, dir.PathSuffix), keys, partitionFilters, minDt,
                        stats, leaves, depth + 1, ct)
                    .ConfigureAwait(false);
            }
        }
    }

    internal static string Join(string basePath, string suffix) =>
        basePath.Length == 0 ? suffix : $"{basePath.TrimEnd('/')}/{suffix}";
}
