// IdentitySyncTests.cs
// --------------------
// Identity directory load from the BDH User export. The critical integrity
// invariant: a PARTIAL user directory (row cap hit, or an oversize User file
// skipped) must FAIL the sync loudly rather than proceed — a silently-incomplete
// directory applies coarse/fallback ACLs across the entire crawl.

using HadoopConnector.AclEngine;
using HadoopConnector.Config;
using HadoopConnector.Filters;
using HadoopConnector.Hdfs;

namespace HadoopConnector.Tests;

public class IdentitySyncTests
{
    private static string UserRow(int n) =>
        $$"""{"Id":"005U{{n:D9}}","Email":"u{{n}}@example.com","Name":"User {{n}}","IsActive":"true"}""";

    private static IdentitySync Sync(AppConfig config, FakeBdhSource source)
    {
        var fetcher = new BdhFetcher(config, source, FilterSet.Empty);
        return new IdentitySync(fetcher, new FakeGraphClient(config), config);
    }

    [Fact]
    public async Task LoadDirectory_CompleteExport_LoadsAllUsers()
    {
        var source = new FakeBdhSource().Add(
            "User/dt=2026-07-15/part-0000.jsonl",
            string.Join("\n", Enumerable.Range(1, 3).Select(UserRow)));
        var config = TestConfig.Make();  // IdentityObject defaults to "User"

        var directory = await Sync(config, source).LoadDirectoryAsync();

        Assert.Equal(3, directory.Count);
    }

    // Regression (MEDIUM-5): a row-capped User export is silently partial. Before
    // the fix, LoadDirectoryAsync iterated result.Records ignoring Truncated and
    // returned a truncated directory; now it must refuse the sync.
    [Fact]
    public async Task LoadDirectory_RowCappedExport_FailsLoudly()
    {
        var source = new FakeBdhSource().Add(
            "User/dt=2026-07-15/part-0000.jsonl",
            string.Join("\n", Enumerable.Range(1, 10).Select(UserRow)));
        var config = TestConfig.Make(maxRecordsPerObject: 2);

        var exc = await Assert.ThrowsAsync<InvalidDataException>(
            () => Sync(config, source).LoadDirectoryAsync());
        Assert.Contains("INCOMPLETE", exc.Message);
        Assert.Contains("row cap", exc.Message);
    }

    // The oversize-file skip is equally partial and must also refuse the sync.
    [Fact]
    public async Task LoadDirectory_OversizeUserFileSkipped_FailsLoudly()
    {
        var source = new FakeBdhSource().Add(
            "User/dt=2026-07-15/big.jsonl",
            string.Join("\n", Enumerable.Range(1, 3).Select(UserRow)));
        var config = TestConfig.Make(maxFileBytes: 20);  // every User file is "oversize"

        var exc = await Assert.ThrowsAsync<InvalidDataException>(
            () => Sync(config, source).LoadDirectoryAsync());
        Assert.Contains("INCOMPLETE", exc.Message);
        Assert.Contains("oversize", exc.Message);
    }
}
