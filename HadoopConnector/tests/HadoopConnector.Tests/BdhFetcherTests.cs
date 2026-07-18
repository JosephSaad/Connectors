// BdhFetcherTests.cs
// ------------------
// The BDH source fetch end-to-end (in-memory source): fail-closed full-scan
// guard, watermark/lag math, partition pruning + record predicates with
// per-stage stats, the row-cap safety valve (partial, never silent), id
// validation, oversize-file skip, malformed-row accounting, FindByIdAsync,
// and the LocalPathSource (temp-dir) path.

using System.Text.Json.Nodes;
using HadoopConnector.Config;
using HadoopConnector.Filters;
using HadoopConnector.Hdfs;

namespace HadoopConnector.Tests;

public class BdhFetcherTests
{
    private static ObjectConfig Contact(string? sourcePath = null) => new()
    {
        ObjectName = "Contact",
        DisplayName = "Contact",
        SelectedFields = new Dictionary<string, string> { ["Name"] = "Title" },
        SourcePath = sourcePath ?? string.Empty,
    };

    private static string Row(string id, params (string K, string V)[] fields)
    {
        var obj = new JsonObject { ["Id"] = id };
        foreach (var (k, v) in fields)
            obj[k] = v;
        return obj.ToJsonString();
    }

    private static FilterSet ContactFilter(string json = """
        {"objects": {"Contact": {"allOf": [{"field": "Id", "op": "isNotNull"}]}}}
        """) => FilterSet.Parse(json);

    // ── Fail-closed full-scan guard ──────────────────────────────────────────

    [Fact]
    public async Task Fetch_UnfilteredObject_RefusesToCrawl()
    {
        var fetcher = new BdhFetcher(
            TestConfig.Make(), new FakeBdhSource().Add("Contact/dt=2026-07-15/p.jsonl", Row("C1")),
            FilterSet.Empty);
        var exc = await Assert.ThrowsAsync<FullScanRefusedException>(
            () => fetcher.FetchAsync(Contact(), fullCrawl: true, sinceUtc: null));
        Assert.Equal("Contact", exc.ObjectName);
        Assert.Contains("ALLOW_FULL_SCAN", exc.Message);
    }

    [Fact]
    public async Task Fetch_FullScanAllowedList_Exempts()
    {
        var filters = FilterSet.Parse("""{"fullScanAllowed": ["Contact"]}""");
        var source = new FakeBdhSource().Add("Contact/dt=2026-07-15/p.jsonl", Row("C1"));
        var result = await new BdhFetcher(TestConfig.Make(), source, filters)
            .FetchAsync(Contact(), fullCrawl: true, sinceUtc: null);
        Assert.Single(result.Records);
    }

    [Fact]
    public async Task Fetch_AllowFullScanEnv_Exempts()
    {
        var source = new FakeBdhSource().Add("Contact/dt=2026-07-15/p.jsonl", Row("C1"));
        var result = await new BdhFetcher(TestConfig.Make(allowFullScan: true), source, FilterSet.Empty)
            .FetchAsync(Contact(), fullCrawl: true, sinceUtc: null);
        Assert.Single(result.Records);
    }

    [Fact]
    public async Task Fetch_BypassScanGuard_ForIdentityLoads()
    {
        var source = new FakeBdhSource().Add("User/dt=2026-07-15/p.jsonl", Row("U1"));
        var config = new ObjectConfig
        {
            ObjectName = "User",
            SelectedFields = new Dictionary<string, string> { ["Id"] = "Id" },
        };
        var result = await new BdhFetcher(TestConfig.Make(), source, FilterSet.Empty)
            .FetchAsync(config, fullCrawl: true, sinceUtc: null, bypassScanGuard: true);
        Assert.Single(result.Records);
    }

    [Fact]
    public void UnfilteredObjects_ReportsGuardTrips()
    {
        var filters = FilterSet.Parse(
            """{"objects": {"Contact": {"allOf": [{"field": "Id", "op": "isNotNull"}]}}, "fullScanAllowed": ["Lead"]}""");
        var fetcher = new BdhFetcher(TestConfig.Make(), new FakeBdhSource(), filters);
        var objects = new[]
        {
            Contact(),
            new ObjectConfig { ObjectName = "Lead", SelectedFields = { ["Id"] = "Id" } },
            new ObjectConfig { ObjectName = "Case", SelectedFields = { ["Id"] = "Id" } },
        };
        Assert.Equal(new[] { "Case" }, fetcher.UnfilteredObjects(objects));
    }

    // ── Watermark / lag math ─────────────────────────────────────────────────

    [Fact]
    public void MinDtFor_FullCrawl_IsNull() =>
        Assert.Null(BdhFetcher.MinDtFor(fullCrawl: true, DateTime.UtcNow, 24));

    [Fact]
    public void MinDtFor_NoSince_IsNull() =>
        Assert.Null(BdhFetcher.MinDtFor(fullCrawl: false, null, 24));

    [Fact]
    public void MinDtFor_SubtractsLagHours()
    {
        var since = new DateTime(2026, 7, 17, 6, 0, 0, DateTimeKind.Utc);
        // 6:00 − 24h = 2026-07-16 06:00 → date 2026-07-16.
        Assert.Equal(new DateOnly(2026, 7, 16), BdhFetcher.MinDtFor(false, since, 24));
        // 6:00 − 12h = 2026-07-16 18:00 → date 2026-07-16.
        Assert.Equal(new DateOnly(2026, 7, 16), BdhFetcher.MinDtFor(false, since, 12));
        // Zero lag: same date.
        Assert.Equal(new DateOnly(2026, 7, 17), BdhFetcher.MinDtFor(false, since, 0));
        // Negative lag is clamped to zero, never a future watermark.
        Assert.Equal(new DateOnly(2026, 7, 17), BdhFetcher.MinDtFor(false, since, -5));
    }

    [Fact]
    public async Task IncrementalFetch_ReadsOnlyPartitionsInsideTheOverlapWindow()
    {
        var source = new FakeBdhSource()
            .Add("Contact/dt=2026-07-10/p.jsonl", Row("OLD"))
            .Add("Contact/dt=2026-07-16/p.jsonl", Row("C16"))
            .Add("Contact/dt=2026-07-17/p.jsonl", Row("C17"));
        var fetcher = new BdhFetcher(TestConfig.Make(lagHours: 24), source, ContactFilter());

        var since = new DateTime(2026, 7, 17, 6, 0, 0, DateTimeKind.Utc);
        var result = await fetcher.FetchAsync(Contact(), fullCrawl: false, since);

        // Watermark 2026-07-16: the 10th is pruned, 16th + 17th re-read (overlap).
        Assert.Equal(new[] { "C16", "C17" }, result.Records.Select(r => r.ItemId).ToArray());
        Assert.Equal(1, result.Stats.PartitionsPruned);
        Assert.Equal(2, result.Stats.PartitionsScanned);
    }

    [Fact]
    public async Task FullCrawl_ReadsEverything_NoWatermark()
    {
        var source = new FakeBdhSource()
            .Add("Contact/dt=2020-01-01/p.jsonl", Row("ANCIENT"))
            .Add("Contact/dt=2026-07-17/p.jsonl", Row("NEW"));
        var result = await new BdhFetcher(TestConfig.Make(), source, ContactFilter())
            .FetchAsync(Contact(), fullCrawl: true, sinceUtc: null);
        Assert.Equal(2, result.Records.Count);
    }

    // ── Record predicates + stats ────────────────────────────────────────────

    [Fact]
    public async Task Fetch_RecordPredicates_FilterWhileStreaming_WithStats()
    {
        var filters = FilterSet.Parse("""
            {"objects": {"Contact": {"allOf": [{"field": "Status", "op": "equals", "value": "Active"}]}}}
            """);
        var source = new FakeBdhSource().Add(
            "Contact/dt=2026-07-15/p.jsonl",
            Row("C1", ("Status", "Active")) + "\n"
            + Row("C2", ("Status", "Inactive")) + "\n"
            + Row("C3", ("Status", "ACTIVE")) + "\n");
        var result = await new BdhFetcher(TestConfig.Make(), source, filters)
            .FetchAsync(Contact(), fullCrawl: true, sinceUtc: null);

        Assert.Equal(new[] { "C1", "C3" }, result.Records.Select(r => r.ItemId).ToArray());
        Assert.Equal(3, result.Stats.RecordsScanned);
        Assert.Equal(1, result.Stats.RecordsFilteredPredicate);
        Assert.Equal(2, result.Stats.RecordsMatched);
        Assert.Equal(1, result.Stats.FilesRead);
    }

    [Fact]
    public async Task Fetch_StampsDataAsOfAndSourcePath()
    {
        var source = new FakeBdhSource().Add("Contact/dt=2026-07-15/p.jsonl", Row("C1"));
        var result = await new BdhFetcher(TestConfig.Make(), source, ContactFilter())
            .FetchAsync(Contact(), fullCrawl: true, sinceUtc: null);
        var record = Assert.Single(result.Records);
        Assert.Equal("2026-07-15", record.DataAsOf);
        Assert.Equal("Contact/dt=2026-07-15/p.jsonl", record.SourcePath);
    }

    [Fact]
    public async Task Fetch_SourcePathOverride_IsUsed()
    {
        var source = new FakeBdhSource().Add("exports/crm_contact/dt=2026-07-15/p.jsonl", Row("C1"));
        var result = await new BdhFetcher(TestConfig.Make(), source, ContactFilter())
            .FetchAsync(Contact(sourcePath: "exports/crm_contact"), fullCrawl: true, sinceUtc: null);
        Assert.Single(result.Records);
    }

    // ── Row cap (partial, never silent) ──────────────────────────────────────

    [Fact]
    public async Task Fetch_RowCap_MarksTruncated_StopsReading()
    {
        var source = new FakeBdhSource().Add(
            "Contact/dt=2026-07-15/p.jsonl",
            string.Join("\n", Enumerable.Range(1, 10).Select(i => Row($"C{i:D3}"))));
        var result = await new BdhFetcher(
                TestConfig.Make(maxRecordsPerObject: 4), source, ContactFilter())
            .FetchAsync(Contact(), fullCrawl: true, sinceUtc: null);

        Assert.True(result.Truncated);
        Assert.Equal(4, result.Records.Count);
        Assert.True(result.Stats.RecordsScanned <= 5);   // stopped at the cap, not the file end
    }

    [Fact]
    public async Task Fetch_RowCapZero_Disabled()
    {
        var source = new FakeBdhSource().Add(
            "Contact/dt=2026-07-15/p.jsonl",
            string.Join("\n", Enumerable.Range(1, 10).Select(i => Row($"C{i:D3}"))));
        var result = await new BdhFetcher(
                TestConfig.Make(maxRecordsPerObject: 0), source, ContactFilter())
            .FetchAsync(Contact(), fullCrawl: true, sinceUtc: null);
        Assert.False(result.Truncated);
        Assert.Equal(10, result.Records.Count);
    }

    // ── Hardening ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Fetch_DropsRowsWithMissingOrUnsafeIds()
    {
        // Predicates run BEFORE id validation, so use a filter that does not
        // touch Id — both bad rows must reach (and fail) the id check.
        var filters = FilterSet.Parse(
            """{"objects": {"Contact": {"allOf": [{"field": "Name", "op": "isNotNull"}]}}}""");
        var source = new FakeBdhSource().Add(
            "Contact/dt=2026-07-15/p.jsonl",
            Row("C1", ("Name", "one")) + "\n"
            + """{"Name":"no id"}""" + "\n"
            + """{"Id":"../escape","Name":"bad id"}""" + "\n"
            + Row("C2", ("Name", "two")));
        var result = await new BdhFetcher(TestConfig.Make(), source, filters)
            .FetchAsync(Contact(), fullCrawl: true, sinceUtc: null);

        Assert.Equal(new[] { "C1", "C2" }, result.Records.Select(r => r.ItemId).ToArray());
        Assert.Equal(2, result.Stats.RecordsDroppedNoId);
    }

    [Fact]
    public async Task Fetch_MalformedRows_CountedNotFatal()
    {
        var source = new FakeBdhSource().Add(
            "Contact/dt=2026-07-15/p.jsonl",
            Row("C1") + "\n{ nope\n" + Row("C2"));
        var result = await new BdhFetcher(TestConfig.Make(), source, ContactFilter())
            .FetchAsync(Contact(), fullCrawl: true, sinceUtc: null);
        Assert.Equal(2, result.Records.Count);
        Assert.Equal(1, result.Stats.RecordsMalformed);
    }

    [Fact]
    public async Task Fetch_OversizeFiles_SkippedByListingLength()
    {
        var big = Row("BIG1") + "\n" + Row("BIG2");
        var source = new FakeBdhSource()
            .Add("Contact/dt=2026-07-15/big.jsonl", big)
            .Add("Contact/dt=2026-07-15/small.jsonl", Row("C1"));
        var result = await new BdhFetcher(
                TestConfig.Make(maxFileBytes: 20), source, ContactFilter())
            .FetchAsync(Contact(), fullCrawl: true, sinceUtc: null);

        Assert.Equal(new[] { "C1" }, result.Records.Select(r => r.ItemId).ToArray());
        Assert.Equal(1, result.Stats.FilesSkippedOversize);
    }

    [Fact]
    public async Task Fetch_CsvFilesWork()
    {
        var source = new FakeBdhSource().Add(
            "Contact/dt=2026-07-15/p.csv", "Id,Name\nC1,Acme\nC2,Beta\n");
        var result = await new BdhFetcher(TestConfig.Make(), source, ContactFilter())
            .FetchAsync(Contact(), fullCrawl: true, sinceUtc: null);
        Assert.Equal(2, result.Records.Count);
        Assert.Equal("Acme", result.Records[0].GetString("Name"));
    }

    // ── FindByIdAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task FindById_ScansNewestFirst_StopsOnMatch()
    {
        var source = new FakeBdhSource()
            .Add("Contact/dt=2026-07-14/p.jsonl", Row("C1", ("Name", "old")))
            .Add("Contact/dt=2026-07-16/p.jsonl", Row("C1", ("Name", "new")));
        var fetcher = new BdhFetcher(TestConfig.Make(), source, ContactFilter());

        var record = await fetcher.FindByIdAsync(Contact(), "C1");
        Assert.NotNull(record);
        Assert.Equal("new", record!.GetString("Name"));    // newest partition wins
        Assert.Equal("2026-07-16", record.DataAsOf);
        Assert.Equal(1, source.OpenCalls);                 // stopped after the first hit
    }

    [Fact]
    public async Task FindById_BypassesRecordPredicates()
    {
        var filters = FilterSet.Parse("""
            {"objects": {"Contact": {"allOf": [{"field": "Status", "op": "equals", "value": "Active"}]}}}
            """);
        var source = new FakeBdhSource()
            .Add("Contact/dt=2026-07-15/p.jsonl", Row("C1", ("Status", "Inactive")));
        var record = await new BdhFetcher(TestConfig.Make(), source, filters)
            .FindByIdAsync(Contact(), "C1");
        Assert.NotNull(record);   // targeted lookup ignores the predicate
    }

    [Fact]
    public async Task FindById_UnsafeId_RejectedWithoutScanning()
    {
        var source = new FakeBdhSource().Add("Contact/dt=2026-07-15/p.jsonl", Row("C1"));
        var record = await new BdhFetcher(TestConfig.Make(), source, ContactFilter())
            .FindByIdAsync(Contact(), "../../etc/passwd");
        Assert.Null(record);
        Assert.Equal(0, source.ListCalls);
    }

    [Fact]
    public async Task FindById_NotFound_ReturnsNull()
    {
        var source = new FakeBdhSource().Add("Contact/dt=2026-07-15/p.jsonl", Row("C1"));
        Assert.Null(await new BdhFetcher(TestConfig.Make(), source, ContactFilter())
            .FindByIdAsync(Contact(), "MISSING"));
    }

    // ── LocalPathSource ──────────────────────────────────────────────────────

    [Fact]
    public async Task LocalPathSource_ReadsTheSameLayoutFromDisk()
    {
        using var dir = new TempDir();
        var partition = Path.Combine(dir.Path, "Contact", "dt=2026-07-15");
        Directory.CreateDirectory(partition);
        File.WriteAllText(Path.Combine(partition, "part-0000.jsonl"), Row("C1") + "\n" + Row("C2"));
        File.WriteAllText(Path.Combine(partition, "_SUCCESS"), string.Empty);

        using var source = new LocalPathSource(dir.Path);
        var result = await new BdhFetcher(TestConfig.Make(), source, ContactFilter())
            .FetchAsync(Contact(), fullCrawl: true, sinceUtc: null);

        Assert.Equal(2, result.Records.Count);
        Assert.Equal("2026-07-15", result.Records[0].DataAsOf);
    }

    [Fact]
    public void LocalPathSource_RefusesTraversalEscapes()
    {
        using var dir = new TempDir();
        var source = new LocalPathSource(dir.Path);
        Assert.Throws<HdfsException>(() => source.Resolve("../../etc/passwd"));
    }
}
