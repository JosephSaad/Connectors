// HardeningReviewStressTests.cs
// -----------------------------
// try/catch review + stress round dedicated to the NEW bank-grade hardening
// code (decision ledger, directory hardening, webhook anti-replay, debouncer).
// These paths had only basic unit coverage in HardeningFixesTests; this file
// drives them under adversarial input and concurrency.
//
// The decision-ledger torn-line tests are RED→GREEN regressions for a genuine
// defect: DecisionLedger.Read did not tolerate a torn/partial final line (a
// crash mid-append), so a single torn line permanently broke every later
// Read/Append (ReadTail threw, Record swallowed it, no further decision was
// ever recorded) and made Verify report false. SyncState.ReadFailedRecords
// already isolates torn lines per-line; the ledger did not.

using System.Text;
using System.Text.Json.Nodes;
using ClarizenConnector.Infrastructure;
using ClarizenConnector.Webhook;

namespace ClarizenConnector.Tests;

// ── Decision ledger: tolerant reader for a torn final line (RED→GREEN) ────────

public class DecisionLedgerTornLineTests
{
    private const string Conn = "TornLedgerConn";

    private static void SeedTwoValidRecords()
    {
        DecisionLedger.RecordExclusion(Conn, "Task_1", "no resolvable ACL principals");
        DecisionLedger.RecordRestriction(Conn, "Task_2", "financial (FINANCIAL_DATA_MODE=acl)");
    }

    /// <summary>Simulate a crash mid-append: a partial JSON line with no newline.</summary>
    private static void AppendTornFinalLine()
    {
        var path = DecisionLedger.LedgerPath(Conn);
        File.AppendAllText(path, "{\"seq\":3,\"item_id\":\"Task_3\",\"decision\":\"exclu");
    }

    [Fact]
    public void Read_TolerantOfTornFinalLine_DoesNotThrow()
    {
        using var scope = new SyncStateScope();
        SeedTwoValidRecords();
        AppendTornFinalLine();

        // Pre-fix: Read throws JsonException on the torn line (its catch only
        // handled FileNotFound/DirectoryNotFound).
        var records = DecisionLedger.Read(Conn);
        Assert.Equal(2, records.Count);
        Assert.Equal("Task_1", records[0]["item_id"]!.GetValue<string>());
        Assert.Equal("Task_2", records[1]["item_id"]!.GetValue<string>());
    }

    [Fact]
    public void Record_ContinuesChain_AfterTornFinalLine()
    {
        using var scope = new SyncStateScope();
        SeedTwoValidRecords();
        AppendTornFinalLine();

        // Pre-fix: ReadTail throws inside Record, the catch swallows it, and the
        // decision is NEVER appended — the ledger is permanently broken.
        DecisionLedger.RecordRestriction(Conn, "Task_4", "classification=Restricted");

        var records = DecisionLedger.Read(Conn);
        Assert.Equal(3, records.Count);
        Assert.Contains(records, r => r["item_id"]!.GetValue<string>() == "Task_4");
        // seq stays contiguous 1,2,3 across the torn write.
        Assert.Equal(new long[] { 1, 2, 3 }, records.Select(r => r["seq"]!.GetValue<long>()).ToArray());
        // Chain continues and still verifies (the new record chains from Task_2).
        Assert.True(DecisionLedger.Verify(Conn));
    }

    [Fact]
    public void Verify_TolerantOfTornFinalLine_ReturnsTrue()
    {
        using var scope = new SyncStateScope();
        SeedTwoValidRecords();
        AppendTornFinalLine();

        // The two real records ARE a valid chain; a crash artifact on the tail
        // must not be reported as tamper.
        Assert.True(DecisionLedger.Verify(Conn));
    }

    [Fact]
    public void Read_CorruptInteriorLine_Skipped_VerifyStillDetectsGap()
    {
        using var scope = new SyncStateScope();
        DecisionLedger.RecordExclusion(Conn, "Task_1", "a");
        DecisionLedger.RecordExclusion(Conn, "Task_2", "b");
        DecisionLedger.RecordExclusion(Conn, "Task_3", "c");

        // Corrupt the MIDDLE line (interior corruption, not a torn tail).
        var path = DecisionLedger.LedgerPath(Conn);
        var lines = File.ReadAllLines(path);
        lines[1] = "{ this is not valid json";
        File.WriteAllLines(path, lines);

        // Reader is tolerant (skips the bad line) but the chain now has a seq
        // gap / broken link, which Verify must still report as tamper.
        var records = DecisionLedger.Read(Conn);
        Assert.Equal(2, records.Count);
        Assert.False(DecisionLedger.Verify(Conn));
    }
}

// ── Decision ledger: interior reorder detection + concurrent appends ─────────

public class DecisionLedgerIntegrityStressTests
{
    [Fact]
    public void Verify_DetectsReorder()
    {
        const string conn = "ReorderConn";
        using var scope = new SyncStateScope();
        DecisionLedger.RecordExclusion(conn, "Task_1", "a");
        DecisionLedger.RecordExclusion(conn, "Task_2", "b");
        DecisionLedger.RecordExclusion(conn, "Task_3", "c");

        var path = DecisionLedger.LedgerPath(conn);
        var lines = File.ReadAllLines(path).ToList();
        (lines[0], lines[1]) = (lines[1], lines[0]);   // swap first two records
        File.WriteAllLines(path, lines);

        Assert.False(DecisionLedger.Verify(conn));
    }

    [Fact]
    public async Task ConcurrentAppends_ChainStaysValid_NoTornLines()
    {
        const string conn = "ConcurrentLedgerConn";
        const int total = 300;
        using var scope = new SyncStateScope();

        await Task.WhenAll(Enumerable.Range(0, total).Select(i => Task.Run(() =>
        {
            if (i % 2 == 0)
                DecisionLedger.RecordExclusion(conn, $"Task_{i}", "no resolvable ACL principals");
            else
                DecisionLedger.RecordRestriction(conn, $"Task_{i}", "financial (FINANCIAL_DATA_MODE=acl)");
        })));

        // Every physical line parses (no interleaved/torn writes under the lock).
        var path = DecisionLedger.LedgerPath(conn);
        var rawLines = File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToList();
        Assert.Equal(total, rawLines.Count);
        Assert.All(rawLines, l => Assert.NotNull(JsonNode.Parse(l)));

        // seq is exactly 1..total with no gaps or duplicates.
        var records = DecisionLedger.Read(conn);
        Assert.Equal(total, records.Count);
        Assert.Equal(
            Enumerable.Range(1, total).Select(x => (long)x).ToArray(),
            records.Select(r => r["seq"]!.GetValue<long>()).OrderBy(x => x).ToArray());

        Assert.True(DecisionLedger.Verify(conn));
    }
}

// ── Directory hardening under concurrency / loose dirs / oddball paths ────────

public class DirectoryHardeningStressTests
{
    [Fact]
    public void ConcurrentEnsureOwnerOnly_SamePath_NeverThrows_And0700()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "state");

        Parallel.For(0, 64, _ => DirectoryHardening.EnsureOwnerOnly(target));

        Assert.True(Directory.Exists(target));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(DirectoryHardening.OwnerOnly, File.GetUnixFileMode(target));
    }

    [Fact]
    public void ExistingLooseDir_IsTightenedTo0700()
    {
        if (OperatingSystem.IsWindows())
            return;   // POSIX mode assertion only

        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "loose");
        Directory.CreateDirectory(target);
        File.SetUnixFileMode(
            target,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);   // 0755

        DirectoryHardening.EnsureOwnerOnly(target);

        var mode = File.GetUnixFileMode(target);
        Assert.Equal(DirectoryHardening.OwnerOnly, mode);
        Assert.False(mode.HasFlag(UnixFileMode.GroupRead));
        Assert.False(mode.HasFlag(UnixFileMode.OtherRead));
    }

    [Fact]
    public void OddballPath_UnderAFile_NeverThrows()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "iamafile");
        File.WriteAllText(file, "x");
        var impossible = Path.Combine(file, "child");   // a dir under a file

        // Must be a silent, non-fatal no-op: CreateDirectory throws internally,
        // the helper logs a warning and swallows it (defense-in-depth only).
        DirectoryHardening.EnsureOwnerOnly(impossible);
        Assert.False(Directory.Exists(impossible));
    }

    [Fact]
    public void HardenStateDirectories_Idempotent_UnderRepeatedStartup()
    {
        using var scope = new SyncStateScope();
        var previousLogsRoot = Logging.LogsRoot;
        using var logsDir = new TempDir();
        Logging.LogsRoot = logsDir.Path;
        try
        {
            for (var i = 0; i < 5; i++)
                DirectoryHardening.HardenStateDirectories();
            Assert.True(Directory.Exists(Logging.LogsRoot));
            Assert.True(Directory.Exists(Config.SyncState.LogsDir));
        }
        finally
        {
            Logging.LogsRoot = previousLogsRoot;
        }
    }
}

// ── Webhook anti-replay: bounded seen-signature cache under a flood ───────────

public class WebhookAntiReplayFloodTests
{
    private const string Secret = "flood-secret";
    private static readonly DateTime Now = new(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc);

    private static byte[] BodyFor(int i) =>
        Encoding.UTF8.GetBytes(
            $$"""{"events":[{"entityType":"Task","id":"{{i}}","operation":"update"}]}""");

    private static string TsFor(int i) =>
        new DateTimeOffset(Now.AddSeconds(i)).ToUnixTimeSeconds().ToString();

    private static string SigFor(int i) =>
        new SignatureValidator(Secret).ComputeHex(TsFor(i), BodyFor(i));

    [Fact]
    public void Flood_DistinctSignedEvents_CacheBounded_StillRejectsRecentReplay()
    {
        // Small cap so the flood exercises eviction deterministically. Distinct
        // timestamps (i seconds apart) give distinct, strictly-increasing
        // expiries so drop-oldest is deterministic.
        var auth = new WebhookAuthenticator(
            Secret, requireTimestamp: true, TimeSpan.FromMinutes(5), seenCacheCap: 4);

        // 6 distinct signed events, all fresh → all accepted. With cap 4 the two
        // oldest (0,1) are evicted; the cache holds {2,3,4,5}.
        for (var i = 0; i < 6; i++)
            Assert.Equal(WebhookAuthResult.Ok, auth.Verify(BodyFor(i), SigFor(i), TsFor(i), Now));

        // A replay of a RECENT event (still cached) is still rejected — the
        // bound did not disable replay protection for recent traffic.
        Assert.Equal(WebhookAuthResult.Replay, auth.Verify(BodyFor(5), SigFor(5), TsFor(5), Now));
        Assert.Equal(WebhookAuthResult.Replay, auth.Verify(BodyFor(2), SigFor(2), TsFor(2), Now));

        // A replay of an EVICTED event is accepted again — proving the cache is
        // BOUNDED (it did not grow to hold every signature). Freshness remains
        // the outer bound on the replay window (documented tradeoff).
        Assert.Equal(WebhookAuthResult.Ok, auth.Verify(BodyFor(0), SigFor(0), TsFor(0), Now));
    }

    [Fact]
    public void Flood_ConcurrentDistinctEvents_NeverThrows_AllAccepted()
    {
        var auth = new WebhookAuthenticator(
            Secret, requireTimestamp: true, TimeSpan.FromMinutes(5), seenCacheCap: 64);

        var results = new WebhookAuthResult[512];
        Parallel.For(0, 512, i =>
        {
            // Each event is verified against its own clock (sentUtc == nowUtc) so
            // it is always fresh; signature and timestamp are the matching pair.
            results[i] = auth.Verify(BodyFor(i), SigFor(i), TsFor(i), Now.AddSeconds(i));
        });

        // Every distinct signed event is accepted; the bounded cache under
        // concurrent TryAdd never throws or corrupts.
        Assert.All(results, r => Assert.Equal(WebhookAuthResult.Ok, r));
    }
}

// ── Debouncer: bounded pending map under a concurrent flood ───────────────────

public class DebouncerConcurrencyStressTests
{
    private static readonly DateTime T0 = new(2026, 7, 18, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ConcurrentFlood_RespectsCap_NeverExceeds_NeverThrows()
    {
        const int cap = 100;
        var debouncer = new EventDebouncer(TimeSpan.FromMinutes(5), maxPending: cap);

        Parallel.For(0, 4000, i =>
        {
            // Distinct entities grow/evict; low-id re-offers coalesce (no growth).
            debouncer.Offer(new WebhookEvent("Task", i.ToString(), ChangeKind.Upsert), T0.AddSeconds(i % 60));
            debouncer.Offer(new WebhookEvent("Task", (i % 20).ToString(), ChangeKind.Upsert), T0);
        });

        // Bounded by the cap regardless of input rate, and the two internal maps
        // stayed consistent (DrainAll clears exactly PendingCount, then nothing).
        Assert.True(debouncer.PendingCount <= cap);
        Assert.Equal(cap, debouncer.PendingCount);   // saturated
        var drained = debouncer.DrainAll();
        Assert.Equal(cap, drained.Count);
        Assert.Equal(0, debouncer.PendingCount);
        Assert.Empty(debouncer.DrainAll());
        Assert.True(debouncer.Dropped > 0);
    }
}
