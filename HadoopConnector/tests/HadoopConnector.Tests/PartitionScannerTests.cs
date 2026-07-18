// PartitionScannerTests.cs
// ------------------------
// Hive-layout discovery + pruning: dt watermark ranges, extra partition keys
// at any level, partition-filter pruning, layout noise, depth cap, ordering.

using HadoopConnector.Filters;
using HadoopConnector.Hdfs;

namespace HadoopConnector.Tests;

public class PartitionScannerTests
{
    private static FilterPredicate P(string key, FilterOp op, string? value = null, params string[] values) =>
        new() { Field = key, Op = op, Value = value, Values = values.ToList() };

    private static Task<List<PartitionDir>> Scan(
        FakeBdhSource source, DateOnly? minDt = null,
        List<FilterPredicate>? filters = null, PartitionScanStats? stats = null) =>
        new PartitionScanner(source).ScanAsync(
            "Contact", filters ?? new List<FilterPredicate>(), minDt,
            stats ?? new PartitionScanStats());

    // ── Segment parsing ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("dt=2026-07-01", true, "dt", "2026-07-01")]
    [InlineData("region=EMEA", true, "region", "EMEA")]
    [InlineData("region=EM%3DEA", true, "region", "EM=EA")]   // URL-escaped value
    [InlineData("part-0000.csv", false, "", "")]
    [InlineData("=value", false, "", "")]
    [InlineData("key=", false, "", "")]
    public void TryParseSegment_Matrix(string name, bool ok, string key, string value)
    {
        Assert.Equal(ok, PartitionScanner.TryParseSegment(name, out var k, out var v));
        if (ok)
        {
            Assert.Equal(key, k);
            Assert.Equal(value, v);
        }
    }

    [Theory]
    [InlineData("2026-07-01", true)]
    [InlineData("2026-2-1", false)]     // not strict yyyy-MM-dd
    [InlineData("20260701", false)]
    [InlineData("yesterday", false)]
    public void ParseDt_IsStrict(string value, bool ok) =>
        Assert.Equal(ok, PartitionScanner.ParseDt(value) is not null);

    // ── Discovery ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Scan_FindsLeafPartitions_OldestDtFirst()
    {
        var source = new FakeBdhSource()
            .Add("Contact/dt=2026-07-16/part-0000.jsonl", "{}")
            .Add("Contact/dt=2026-07-14/part-0000.jsonl", "{}")
            .Add("Contact/dt=2026-07-15/part-0000.jsonl", "{}");

        var partitions = await Scan(source);
        Assert.Equal(
            new[] { "2026-07-14", "2026-07-15", "2026-07-16" },
            partitions.Select(p => p.Dt!.Value.ToString("yyyy-MM-dd")).ToArray());
        Assert.All(partitions, p => Assert.Single(p.Files));
    }

    [Fact]
    public async Task Scan_ExtraPartitionKeys_AccumulateAtAnyLevel()
    {
        var source = new FakeBdhSource()
            .Add("Contact/region=EMEA/dt=2026-07-15/part-0000.csv", "Id\nx")
            .Add("Contact/region=NA/dt=2026-07-15/part-0000.csv", "Id\nx");

        var partitions = await Scan(source);
        Assert.Equal(2, partitions.Count);
        Assert.All(partitions, p =>
        {
            Assert.Equal("2026-07-15", p.Keys["dt"]);
            Assert.Contains(p.Keys["region"], new[] { "EMEA", "NA" });
            Assert.Equal(new DateOnly(2026, 7, 15), p.Dt);
        });
    }

    [Fact]
    public async Task Scan_SkipsLayoutNoise()
    {
        var source = new FakeBdhSource()
            .Add("Contact/dt=2026-07-15/_SUCCESS", "")
            .Add("Contact/dt=2026-07-15/.hidden", "x")
            .Add("Contact/dt=2026-07-15/part-0000.jsonl.tmp", "{}")
            .Add("Contact/dt=2026-07-15/part-0000.jsonl", "{}")
            .Add("Contact/dt=2026-07-15/README.txt", "not a data file")
            .Add("Contact/_staging/part-9.jsonl", "{}");

        var partitions = await Scan(source);
        var partition = Assert.Single(partitions);
        Assert.Equal("part-0000.jsonl", Assert.Single(partition.Files).PathSuffix);
    }

    // ── dt watermark pruning ─────────────────────────────────────────────────

    [Fact]
    public async Task Scan_MinDt_PrunesOldPartitionsWithoutListingThem()
    {
        var source = new FakeBdhSource()
            .Add("Contact/dt=2026-07-10/part-0000.jsonl", "{}")
            .Add("Contact/dt=2026-07-14/part-0000.jsonl", "{}")
            .Add("Contact/dt=2026-07-16/part-0000.jsonl", "{}");

        var stats = new PartitionScanStats();
        var partitions = await Scan(source, minDt: new DateOnly(2026, 7, 14), stats: stats);

        Assert.Equal(2, partitions.Count);                       // 14th (inclusive) + 16th
        Assert.Equal(1, stats.PartitionsPruned);                 // the 10th, zero I/O
        Assert.Equal(2, stats.PartitionsScanned);
        Assert.DoesNotContain(partitions, p => p.Dt == new DateOnly(2026, 7, 10));
    }

    [Fact]
    public async Task Scan_MinDt_BoundaryIsInclusive()
    {
        var source = new FakeBdhSource()
            .Add("Contact/dt=2026-07-14/part-0000.jsonl", "{}");
        var partitions = await Scan(source, minDt: new DateOnly(2026, 7, 14));
        Assert.Single(partitions);
    }

    // ── Partition-filter pruning ─────────────────────────────────────────────

    [Fact]
    public async Task Scan_PartitionFilters_PruneNonMatchingKeyDirs()
    {
        var source = new FakeBdhSource()
            .Add("Contact/region=EMEA/dt=2026-07-15/part-0000.jsonl", "{}")
            .Add("Contact/region=NA/dt=2026-07-15/part-0000.jsonl", "{}")
            .Add("Contact/region=APAC/dt=2026-07-15/part-0000.jsonl", "{}");

        var stats = new PartitionScanStats();
        var partitions = await Scan(
            source, filters: new List<FilterPredicate> { P("region", FilterOp.In, null, "EMEA", "NA") },
            stats: stats);

        Assert.Equal(2, partitions.Count);
        Assert.Equal(1, stats.PartitionsPruned);   // APAC pruned before its dt dirs were listed
    }

    [Fact]
    public async Task Scan_DtPartitionFilter_PrunesByDate()
    {
        var source = new FakeBdhSource()
            .Add("Contact/dt=2026-07-15/part-0000.jsonl", "{}")
            .Add("Contact/dt=2020-01-01/part-0000.jsonl", "{}");

        var partitions = await Scan(
            source, filters: new List<FilterPredicate> { P("dt", FilterOp.After, "2026-01-01") });
        Assert.Single(partitions);
        Assert.Equal(new DateOnly(2026, 7, 15), partitions[0].Dt);
    }

    [Fact]
    public async Task Scan_FilterOnAbsentKey_DoesNotPrune()
    {
        // The layout has no region key at all — a region filter cannot prune.
        var source = new FakeBdhSource()
            .Add("Contact/dt=2026-07-15/part-0000.jsonl", "{}");
        var partitions = await Scan(
            source, filters: new List<FilterPredicate> { P("region", FilterOp.Equals, "EMEA") });
        Assert.Single(partitions);
    }

    // ── Misc hardening ───────────────────────────────────────────────────────

    [Fact]
    public async Task Scan_NonPartitionSubdirectories_AreRecursed()
    {
        var source = new FakeBdhSource()
            .Add("Contact/dt=2026-07-15/bucket0/part-0000.jsonl", "{}");
        var partitions = await Scan(source);
        var partition = Assert.Single(partitions);
        // Keys from the ancestor dt segment are retained through the bucket dir.
        Assert.Equal(new DateOnly(2026, 7, 15), partition.Dt);
    }

    [Fact]
    public async Task Scan_MissingObjectRoot_Throws()
    {
        var source = new FakeBdhSource().Add("Other/dt=2026-07-15/p.jsonl", "{}");
        await Assert.ThrowsAsync<HdfsException>(() => Scan(source));
    }

    [Fact]
    public async Task Scan_MixedDirWithFilesAndSubdirs_KeepsBoth()
    {
        var source = new FakeBdhSource()
            .Add("Contact/dt=2026-07-15/part-0000.jsonl", "{}")
            .Add("Contact/loose.jsonl", "{}");
        var partitions = await Scan(source);
        Assert.Equal(2, partitions.Count);
        Assert.Contains(partitions, p => p.Dt is null);   // the loose root leaf
        Assert.Contains(partitions, p => p.Dt == new DateOnly(2026, 7, 15));
    }
}
