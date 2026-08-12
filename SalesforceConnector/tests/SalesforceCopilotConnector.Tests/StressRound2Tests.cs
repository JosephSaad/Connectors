// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// StressRound2Tests.cs
// --------------------
// Round-2 stress suite. Round 1 (LegacyAclResolverCycleStressTests +
// tools/StressHarness) covered cyclic-graph blow-up, pipeline throughput,
// throttle bursts, dead-letter and stop/resume. Round 2 attacks new
// dimensions:
//
//   1. Round2ResolverChurnTests — the memoized group resolver + depth cap under
//      MUTATING group graphs: membership edits between resolve waves (prewarm
//      swap + cache invalidation), cache invalidation racing in-flight
//      resolves, and the single-flight prewarm regression (red before the
//      LegacyAclResolver.PrewarmCachesAsync latch fix, green after).
//   2. Round2SqliteStateTests — the real SQLite-backed state classes
//      (ItemInventory, IdentityStore) plus the file checkpoint / dead-letter
//      layer under concurrent multi-connection load: no unhandled
//      "database is locked", no lost writes.
//   3. Round2HaLeaseTests — active-active lease contention against a SQLite
//      lease store implementing the SQL contract's claim semantics
//      (open-or-join / claim-with-token / heartbeat / complete / close):
//      failover storms, split-brain prevention, lease renewal under clock
//      skew. (The T-SQL procs themselves need SQL Server — HaContentionTests
//      cover them in the sql-integration job; these tests prove the same
//      protocol invariants offline and drive the REAL Ingest HA worker loop.)
//   4. Round2HaPipelineTests — two simulated nodes running the REAL
//      Ingest.IngestContentAsync claim loop against one shared lease store +
//      shared checkpoint: clean active-active split (zero duplicate acks) and
//      leader death mid-crawl (stale-lease reclaim + checkpoint resume).

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SalesforceCopilotConnector.Config;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;
using Xunit.Abstractions;

namespace SalesforceCopilotConnector.Tests;

// ═════════════════════════════════════════════════════════════════════════════
// Shared helpers
// ═════════════════════════════════════════════════════════════════════════════

internal static class Round2Support
{
    public const string UserPrefix = "005";
    public const string GroupPrefix = "00G";

    internal sealed class TestableResolver : AclResolver
    {
        public TestableResolver()
            : base(new AppConfig { TenantId = "11111111-2222-3333-4444-555555555555" })
        {
        }
    }

    /// <summary>A group graph plus its brute-force reachability oracle.</summary>
    internal sealed class GroupGraph
    {
        public required Dictionary<string, Group> Groups { get; init; }
        public required Dictionary<string, List<string>> Members { get; init; }
        public required List<string> GroupIds { get; init; }
        public required HashSet<string> OrgGroups { get; init; }

        /// <summary>Total reachability: users of every reachable group; Organization → everyone.</summary>
        public (HashSet<string> Users, bool Everyone) Oracle(string start)
        {
            var users = new HashSet<string>();
            var everyone = false;
            var seen = new HashSet<string>();
            var stack = new Stack<string>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var g = stack.Pop();
                if (!seen.Add(g))
                    continue;
                if (OrgGroups.Contains(g))
                {
                    everyone = true;
                    continue;
                }
                foreach (var m in Members.GetValueOrDefault(g) ?? new List<string>())
                {
                    if (m == g)
                        continue;
                    if (m.StartsWith(UserPrefix, StringComparison.Ordinal))
                        users.Add(m);
                    else if (Groups.ContainsKey(m))
                        stack.Push(m);
                }
            }
            return (users, everyone);
        }
    }

    public static GroupGraph BuildRandomGraph(int seed, int nGroups, int nUsers, int orgEvery = 0)
    {
        var rng = new Random(seed);
        var groupIds = Enumerable.Range(0, nGroups).Select(i => $"{GroupPrefix}{i:D5}").ToList();
        var userIds = Enumerable.Range(0, nUsers).Select(i => $"{UserPrefix}{i:D6}").ToList();
        var groups = new Dictionary<string, Group>();
        var members = new Dictionary<string, List<string>>();
        var orgGroups = new HashSet<string>();
        for (var i = 0; i < nGroups; i++)
        {
            var gid = groupIds[i];
            var isOrg = orgEvery > 0 && i % orgEvery == 0 && i != 0;
            groups[gid] = new Group { Id = gid, Type = isOrg ? "Organization" : "Regular" };
            if (isOrg)
            {
                orgGroups.Add(gid);
                members[gid] = new List<string>();
                continue;
            }
            var list = new List<string>();
            for (var k = 0; k < rng.Next(0, 4); k++)
                list.Add(userIds[rng.Next(userIds.Count)]);
            for (var k = 0; k < rng.Next(0, 5); k++)
                list.Add(groupIds[rng.Next(groupIds.Count)]);  // backward/self edges → cycles
            members[gid] = list;
        }
        return new GroupGraph { Groups = groups, Members = members, GroupIds = groupIds, OrgGroups = orgGroups };
    }

    /// <summary>
    /// One mutation epoch: rebuild the graph with ~<paramref name="fraction"/> of
    /// the non-org groups' member lists regenerated (members added AND removed),
    /// exactly what a fresh prewarm sees after Salesforce group edits.
    /// </summary>
    public static GroupGraph Mutate(GroupGraph g, int seed, double fraction = 0.20)
    {
        var rng = new Random(seed);
        var userIds = g.Members.Values.SelectMany(m => m)
            .Where(m => m.StartsWith(UserPrefix, StringComparison.Ordinal))
            .Distinct()
            .ToList();
        if (userIds.Count == 0)
            userIds.Add($"{UserPrefix}000000");
        var groups = new Dictionary<string, Group>(g.Groups.Count);
        var members = new Dictionary<string, List<string>>(g.Members.Count);
        foreach (var gid in g.GroupIds)
        {
            groups[gid] = g.Groups[gid];
            if (!g.OrgGroups.Contains(gid) && rng.NextDouble() < fraction)
            {
                var list = new List<string>();
                for (var k = 0; k < rng.Next(0, 4); k++)
                    list.Add(userIds[rng.Next(userIds.Count)]);
                for (var k = 0; k < rng.Next(0, 5); k++)
                    list.Add(g.GroupIds[rng.Next(g.GroupIds.Count)]);
                members[gid] = list;
            }
            else
            {
                members[gid] = new List<string>(g.Members[gid]);
            }
        }
        return new GroupGraph
        {
            Groups = groups,
            Members = members,
            GroupIds = new List<string>(g.GroupIds),
            OrgGroups = new HashSet<string>(g.OrgGroups),
        };
    }

    private static FieldInfo Field(string name) =>
        typeof(AclResolver).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"AclResolver.{name} not found");

    public static AclResolver MakeResolver(GroupGraph g)
    {
        var resolver = new TestableResolver();
        InstallGraph(resolver, g);
        return resolver;
    }

    /// <summary>Swap in a new prewarm snapshot (what PrewarmCachesAsync publishes).</summary>
    public static void InstallGraph(AclResolver resolver, GroupGraph g)
    {
        Field("_allGroupsById").SetValue(resolver, g.Groups);
        Field("_allGroupMembersByGroup").SetValue(resolver, g.Members);
    }

    /// <summary>
    /// Invalidate the long-lived group-closure cache under the resolver's own
    /// lock — the same discipline CacheGroup uses, so this is exactly what a
    /// cache-invalidation feature (or a per-epoch re-prewarm) would do.
    /// </summary>
    public static void ClearGroupCache(AclResolver resolver)
    {
        var cache = (System.Collections.IDictionary)Field("_groupCache").GetValue(resolver)!;
        var gate = Field("_cacheLock").GetValue(resolver)!;
        lock (gate)
        {
            cache.Clear();
        }
    }

    /// <summary>G0 → {U0, G1}, …, G(n-1) → {U(n-1), G0}: one n-cycle, one frame per level.</summary>
    public static GroupGraph LinearCycle(int depth)
    {
        var groups = new Dictionary<string, Group>();
        var members = new Dictionary<string, List<string>>();
        var ids = new List<string>();
        for (var i = 0; i < depth; i++)
        {
            var gid = $"{GroupPrefix}{i:D5}";
            ids.Add(gid);
            groups[gid] = new Group { Id = gid, Type = "Regular" };
            members[gid] = new List<string> { $"{UserPrefix}{i:D6}", $"{GroupPrefix}{(i + 1) % depth:D5}" };
        }
        return new GroupGraph { Groups = groups, Members = members, GroupIds = ids, OrgGroups = new HashSet<string>() };
    }

    public static async Task AwaitBoundedAsync(Task work, int seconds, string what)
    {
        var done = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(seconds)));
        Assert.True(done == work, $"{what} did not complete within {seconds}s — possible deadlock/livelock");
        await work;  // propagate failures
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// 1. Resolver under mutating graphs + invalidation churn
// ═════════════════════════════════════════════════════════════════════════════

public class Round2ResolverChurnTests
{
    private readonly ITestOutputHelper _out;

    public Round2ResolverChurnTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task MutationEpochs_SharedResolver_ConcurrentResolvesMatchOracleEveryEpoch()
    {
        // ONE resolver lives across 12 mutation epochs. Each epoch edits ~20% of
        // group memberships, swaps the prewarm snapshot and invalidates the
        // closure cache (the churn a re-prewarm produces), then hammers every
        // group concurrently ×2. Any memo that outlived its resolution — or any
        // cache entry that survived invalidation — would surface as a stale
        // grant set vs the epoch's brute-force oracle.
        var g = Round2Support.BuildRandomGraph(seed: 101, nGroups: 80, nUsers: 60, orgEvery: 19);
        var resolver = Round2Support.MakeResolver(g);

        var totalResolves = 0;
        var sw = Stopwatch.StartNew();
        for (var epoch = 0; epoch < 12; epoch++)
        {
            if (epoch > 0)
            {
                g = Round2Support.Mutate(g, seed: 101_000 + epoch);
                Round2Support.InstallGraph(resolver, g);       // prewarm swap
                Round2Support.ClearGroupCache(resolver);       // invalidation
            }

            var epochGraph = g;
            var epochNo = epoch;
            var failures = new ConcurrentBag<string>();
            var tasks = new List<Task>();
            foreach (var gid in epochGraph.GroupIds)
            {
                for (var rep = 0; rep < 2; rep++)
                {
                    var captured = gid;
                    tasks.Add(Task.Run(async () =>
                    {
                        var (users, everyone) = await resolver.ResolveGroupAsync(captured);
                        var (oracleUsers, oracleEveryone) = epochGraph.Oracle(captured);
                        if (!oracleUsers.SetEquals(users) || oracleEveryone != everyone)
                        {
                            failures.Add(
                                $"epoch={epochNo} group={captured}: expected {oracleUsers.Count} users " +
                                $"(everyone={oracleEveryone}), got {users.Count} (everyone={everyone}); " +
                                $"missing=[{string.Join(",", oracleUsers.Except(users).Take(5))}] " +
                                $"extra=[{string.Join(",", users.Except(oracleUsers).Take(5))}]");
                        }
                        Interlocked.Increment(ref totalResolves);
                    }));
                }
            }
            await Round2Support.AwaitBoundedAsync(Task.WhenAll(tasks), 60, $"epoch {epoch} resolve wave");
            Assert.True(failures.IsEmpty,
                $"stale/wrong grants after mutation epoch {epoch}:\n" + string.Join("\n", failures.Take(5)));
        }
        sw.Stop();
        _out.WriteLine(
            $"[churn-epochs] 12 epochs × 80 groups × 2 concurrent reps = {totalResolves} resolves " +
            $"on one shared resolver in {sw.ElapsedMilliseconds} ms " +
            $"({totalResolves * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds):F0} resolves/s); " +
            "0 stale grants, 0 oracle mismatches");
    }

    [Fact]
    public async Task InvalidationChurn_DuringConcurrentResolves_NoCorruptionNoDeadlock()
    {
        // Cache invalidation racing IN-FLIGHT resolves: 8 hammer tasks resolve
        // against the oracle for ~1.5s while a churn task clears the closure
        // cache every ~1ms under the resolver's lock. Invalidation must only
        // ever cost performance — never correctness, never a deadlock.
        var g = Round2Support.BuildRandomGraph(seed: 7, nGroups: 60, nUsers: 50, orgEvery: 0);
        var resolver = Round2Support.MakeResolver(g);

        var failures = new ConcurrentBag<string>();
        var resolves = 0;
        var clears = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));

        // A DEDICATED thread, not a pool task. The eight hammers below saturate
        // the thread pool, so a pool-scheduled churn loop measures scheduling
        // rather than churn: Task.Delay(1) actually slept ~15 ms (Windows' default
        // timer resolution is ~15.6 ms) for ~32 clears in the 1500 ms window, and
        // Task.Yield() was worse still at 1, because yielding parks the
        // continuation behind eight busy hammers. Its own thread lets the loop
        // pace itself, and Sleep(0) hands the rest of the timeslice to any ready
        // thread of equal priority so the hammers are not starved.
        var churn = Task.Factory.StartNew(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                Round2Support.ClearGroupCache(resolver);
                Interlocked.Increment(ref clears);
                Thread.Sleep(0);
            }
        }, TaskCreationOptions.LongRunning);

        var hammers = Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            var i = worker;
            while (!stop.IsCancellationRequested)
            {
                var gid = g.GroupIds[i % g.GroupIds.Count];
                i += 7;  // co-prime stride → all groups covered per worker
                var (users, everyone) = await resolver.ResolveGroupAsync(gid);
                var (oracleUsers, oracleEveryone) = g.Oracle(gid);
                if (!oracleUsers.SetEquals(users) || everyone != oracleEveryone)
                    failures.Add($"group={gid}: expected {oracleUsers.Count}, got {users.Count}");
                Interlocked.Increment(ref resolves);
            }
        })).ToList();

        await Round2Support.AwaitBoundedAsync(
            Task.WhenAll(hammers.Concat(new[] { churn })), 60, "invalidation churn run");

        Assert.True(failures.IsEmpty, string.Join(" | ", failures.Take(5)));
        Assert.True(resolves > 500, $"expected real progress under churn, got {resolves} resolves");
        Assert.True(clears > 100, $"expected real churn, got {clears} cache clears");
        _out.WriteLine(
            $"[churn-live] {resolves} concurrent resolves ({resolves / 1.5:F0}/s) raced " +
            $"{clears} cache invalidations in 1.5s — 0 wrong grants, 0 deadlocks");
    }

    [Theory]
    [InlineData(350)]   // within MaxGroupNestingDepth (400) → exact full closure
    [InlineData(5000)]  // beyond the cap → fail-closed cut at exactly 400 grants
    public async Task DepthCappedChains_UnderConcurrentChurn_TerminateFailClosed(int depth)
    {
        // The 400-level depth cap under concurrent access + cache invalidation
        // churn: a `depth`-cycle resolved from 8 different offsets at once.
        // Within the cap every start sees the complete closure (`depth` users);
        // beyond it every start is cut at exactly the cap (400 users) — bounded,
        // never over-granting, never a stack overflow, cache churn irrelevant.
        var g = Round2Support.LinearCycle(depth);
        var resolver = Round2Support.MakeResolver(g);
        var expected = Math.Min(depth, 400);

        using var stop = new CancellationTokenSource();
        var clears = 0;
        var churn = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                Round2Support.ClearGroupCache(resolver);
                Interlocked.Increment(ref clears);
                try
                {
                    await Task.Delay(1, stop.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        });

        var sw = Stopwatch.StartNew();
        var failures = new ConcurrentBag<string>();
        var tasks = Enumerable.Range(0, 8).Select(k => Task.Run(async () =>
        {
            var start = $"{Round2Support.GroupPrefix}{k * (depth / 8):D5}";
            var (users, everyone) = await resolver.ResolveGroupAsync(start);
            if (users.Count != expected || everyone)
                failures.Add($"start={start}: got {users.Count} users (expected {expected}), everyone={everyone}");
        })).ToList();
        await Round2Support.AwaitBoundedAsync(Task.WhenAll(tasks), 60, $"depth-{depth} chain resolves");
        sw.Stop();
        stop.Cancel();
        await churn;

        Assert.True(failures.IsEmpty, string.Join(" | ", failures.Take(5)));
        _out.WriteLine(
            $"[depth-cap] {depth}-cycle × 8 concurrent starts under {clears} cache clears: " +
            $"every resolve returned exactly {expected} grants in {sw.ElapsedMilliseconds} ms " +
            (depth > 400 ? "(fail-closed at the 400 cap, no stack overflow)" : "(full closure)"));
    }

    // ── Single-flight prewarm regression (red → green) ───────────────────────

    private sealed class CountingPrewarmResolver : AclResolver
    {
        private int _coreRuns;
        public volatile bool FailNext;

        public CountingPrewarmResolver()
            : base(new AppConfig { TenantId = "11111111-2222-3333-4444-555555555555" })
        {
        }

        public int CoreRuns => Volatile.Read(ref _coreRuns);

        internal override async Task PrewarmCachesCoreAsync()
        {
            Interlocked.Increment(ref _coreRuns);
            await Task.Delay(25);  // hold the bulk fetch open so racers pile up
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("simulated bulk-fetch failure");
            }
        }
    }

    [Fact]
    public async Task ConcurrentPrewarm_IsSingleFlight_BulkFetchRunsExactlyOnce()
    {
        // REGRESSION for the LegacyAclResolver prewarm latch: the old
        // ``if (_prewarmed) return;`` check-then-act let every concurrent first
        // caller run the whole bulk fetch (duplicate SOQL; and the loser
        // republished all eight cache fields mid-resolve, so a walk could mix
        // two fetch epochs). With the single-flight task latch, 64 concurrent
        // callers must share exactly one core execution.
        var resolver = new CountingPrewarmResolver();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
        {
            await gate.Task;
            await resolver.PrewarmCachesAsync();
        })).ToList();
        gate.SetResult();
        await Round2Support.AwaitBoundedAsync(Task.WhenAll(callers), 30, "concurrent prewarm");

        Assert.Equal(1, resolver.CoreRuns);

        // Later calls stay latched onto the completed warm-up.
        await resolver.PrewarmCachesAsync();
        Assert.Equal(1, resolver.CoreRuns);
        _out.WriteLine("[prewarm] 64 concurrent PrewarmCachesAsync calls → 1 bulk fetch (single-flight)");
    }

    [Fact]
    public async Task PrewarmFailure_AllConcurrentCallersObserveIt_AndRetryRunsOnceMore()
    {
        // A failed bulk fetch must fail every caller that awaited that attempt
        // (matching the old semantics where the run aborted) and must NOT wedge
        // the latch: the next call retries with exactly one more core run.
        var resolver = new CountingPrewarmResolver { FailNext = true };
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcomes = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        {
            await gate.Task;
            try
            {
                await resolver.PrewarmCachesAsync();
                return "ok";
            }
            catch (InvalidOperationException)
            {
                return "failed";
            }
        })).ToList();
        gate.SetResult();
        await Round2Support.AwaitBoundedAsync(Task.WhenAll(outcomes), 30, "faulted prewarm wave");

        Assert.All(outcomes, o => Assert.Equal("failed", o.Result));
        Assert.Equal(1, resolver.CoreRuns);

        // Retry succeeds and runs the core exactly once more.
        await resolver.PrewarmCachesAsync();
        Assert.Equal(2, resolver.CoreRuns);
        await resolver.PrewarmCachesAsync();
        Assert.Equal(2, resolver.CoreRuns);
        _out.WriteLine("[prewarm] faulted attempt: 32/32 callers observed the failure, 1 core run; retry ran 1 more");
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// 2. SQLite + file state layer under concurrency
// ═════════════════════════════════════════════════════════════════════════════

[Collection("IngestGlobalState")]
public class Round2SqliteStateTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _tmp;
    private readonly string _savedLogsDir = SyncState.LogsDir;

    public Round2SqliteStateTests(ITestOutputHelper output)
    {
        _out = output;
        _tmp = Directory.CreateTempSubdirectory("round2_sqlite_").FullName;
    }

    public void Dispose()
    {
        SyncState.LogsDir = _savedLogsDir;
        try
        {
            Directory.Delete(_tmp, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string JournalMode(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "?";
    }

    [Fact]
    public async Task ItemInventory_EightConnectionsOneDb_NoLockErrorsNoLostWrites()
    {
        // 8 concurrent ItemInventory instances (8 SqliteConnections — the
        // multi-process analog) hammer ONE inventory DB file: upsert batches,
        // deletes, contended same-key upserts, and reads. SQLite must serialize
        // writers via its busy handler — an unhandled SqliteException
        // ("database is locked") or a missing/extra row is a failure.
        var db = Path.Combine(_tmp, "inv.db");
        const int Writers = 8, Batches = 40, PerBatch = 50;
        var errors = new ConcurrentBag<Exception>();
        var statements = 0L;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sw = new Stopwatch();
        var tasks = Enumerable.Range(0, Writers).Select(w => Task.Run(async () =>
        {
            await gate.Task;
            try
            {
                using var inv = new ItemInventory("round2", db);
                for (var b = 0; b < Batches; b++)
                {
                    var objectType = $"T{w % 3}";
                    inv.RecordSeen(
                        Enumerable.Range(0, PerBatch).Select(i => ($"INV-{w}-{b}-{i}", objectType)),
                        DateTime.UtcNow);
                    // Contended rows: every writer upserts the same 25 shared ids.
                    inv.RecordSeen(
                        Enumerable.Range(0, 25).Select(i => ($"SHARED-{i}", "Shared")),
                        DateTime.UtcNow);
                    Interlocked.Add(ref statements, PerBatch + 25);
                    if (b % 5 == 4)
                    {
                        inv.Remove(Enumerable.Range(0, PerBatch).Select(i => $"INV-{w}-{b - 1}-{i}"));
                        Interlocked.Add(ref statements, PerBatch);
                    }
                    if (b % 7 == 3)
                    {
                        _ = inv.Count();
                        _ = inv.IdsForObject(objectType);
                        Interlocked.Add(ref statements, 2);
                    }
                }
            }
            catch (Exception exc)
            {
                errors.Add(exc);
            }
        })).ToList();
        sw.Start();
        gate.SetResult();
        await Round2Support.AwaitBoundedAsync(Task.WhenAll(tasks), 120, "inventory hammer");
        sw.Stop();

        Assert.True(errors.IsEmpty,
            "unhandled SQLite errors under concurrency: " +
            string.Join(" | ", errors.Take(3).Select(e => $"{e.GetType().Name}: {e.Message}")));

        // Lost-write check: per writer, batches {3,8,13,18,23,28,33,38} were
        // removed → 32 surviving batches × 50 rows, plus the 25 shared rows.
        var expected = Writers * (Batches - Batches / 5) * PerBatch + 25;
        using (var check = new ItemInventory("round2", db))
        {
            Assert.Equal(expected, check.Count());
            var shared = check.IdsForObject("Shared");
            Assert.Equal(25, shared.Count);
            Assert.Equal(
                Enumerable.Range(0, 25).Select(i => $"SHARED-{i}").OrderBy(x => x, StringComparer.Ordinal),
                shared);
        }
        _out.WriteLine(
            $"[sqlite-inv] 8 connections, {statements} statements in {sw.ElapsedMilliseconds} ms " +
            $"({statements * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds):F0} ops/s); " +
            $"0 'database is locked' errors, 0 retries needed, final rows {expected} exact; " +
            $"journal_mode={JournalMode(db)}");
    }

    [Fact]
    public async Task IdentityStore_WalMode_WriterWithConcurrentReaders_NoLockErrorsNoTornReads()
    {
        // The IdentityStore's documented concurrency contract: WAL, one writer,
        // many readers. One writer replaces group memberships as fast as it can
        // (400 transactional ReplaceMembers) while 5 reader connections hammer
        // GetMembers. Member ids are stamped with their write round, so a torn
        // read — a membership mixing two ReplaceMembers transactions, or a
        // wrong-sized set — is directly detectable. "database is locked" on any
        // connection is a failure.
        var db = Path.Combine(_tmp, "identity.db");
        const int Groups = 10, Rounds = 40;

        static HashSet<MemberEntry> RoundMembers(int k, int r) =>
            Enumerable.Range(0, (r % 4) + 1)
                .Select(i => new MemberEntry($"u-{k}-r{r}-m{i}", "user"))
                .ToHashSet();

        using (var seed = new IdentityStore(db, "round2-conn"))
        {
            for (var k = 0; k < Groups; k++)
            {
                seed.UpsertGroup($"grp-{k}", displayName: $"Group {k}");
                seed.ReplaceMembers($"grp-{k}", RoundMembers(k, 0));
            }
        }

        var errors = new ConcurrentBag<Exception>();
        var tornReads = new ConcurrentBag<string>();
        var reads = 0L;
        var writes = 0L;
        var writerDone = false;

        var sw = Stopwatch.StartNew();
        var writer = Task.Run(() =>
        {
            try
            {
                using var store = new IdentityStore(db, "round2-conn");
                for (var r = 1; r <= Rounds; r++)
                {
                    for (var k = 0; k < Groups; k++)
                    {
                        store.ReplaceMembers($"grp-{k}", RoundMembers(k, r));
                        Interlocked.Increment(ref writes);
                    }
                }
            }
            catch (Exception exc)
            {
                errors.Add(exc);
            }
            finally
            {
                Volatile.Write(ref writerDone, true);
            }
        });

        var readers = Enumerable.Range(0, 5).Select(reader => Task.Run(() =>
        {
            try
            {
                using var store = new IdentityStore(db, "round2-conn");
                var rng = new Random(1000 + reader);
                while (!Volatile.Read(ref writerDone))
                {
                    var k = rng.Next(Groups);
                    var members = store.GetMembers($"grp-{k}");
                    Interlocked.Increment(ref reads);
                    if (members.Count == 0)
                    {
                        tornReads.Add($"grp-{k}: EMPTY membership observed (mid-ReplaceMembers state leaked)");
                        continue;
                    }
                    var roundsSeen = members
                        .Select(m => m.MemberId.Split('-')[2])
                        .Distinct()
                        .ToList();
                    if (roundsSeen.Count != 1)
                    {
                        tornReads.Add($"grp-{k}: members mix write rounds [{string.Join(",", roundsSeen)}]");
                        continue;
                    }
                    var round = int.Parse(roundsSeen[0].TrimStart('r'), CultureInfo.InvariantCulture);
                    if (members.Count != (round % 4) + 1)
                        tornReads.Add($"grp-{k}: round {round} has {members.Count} members, expected {(round % 4) + 1}");
                }
            }
            catch (Exception exc)
            {
                errors.Add(exc);
            }
        })).ToList();

        await Round2Support.AwaitBoundedAsync(
            Task.WhenAll(readers.Concat(new[] { writer })), 120, "identity writer/reader hammer");
        sw.Stop();

        Assert.True(errors.IsEmpty,
            "unhandled SQLite errors: " +
            string.Join(" | ", errors.Take(3).Select(e => $"{e.GetType().Name}: {e.Message}")));
        Assert.True(tornReads.IsEmpty, "torn reads observed:\n" + string.Join("\n", tornReads.Take(5)));

        var journal = JournalMode(db);
        Assert.Equal("wal", journal);
        using (var check = new IdentityStore(db, "round2-conn"))
        {
            for (var k = 0; k < Groups; k++)
                Assert.Equal(RoundMembers(k, Rounds), check.GetMembers($"grp-{k}"));
        }
        _out.WriteLine(
            $"[sqlite-idstore] WAL writer/readers: {writes} transactional membership replaces raced " +
            $"{reads} concurrent reads in {sw.ElapsedMilliseconds} ms " +
            $"({(writes + reads) * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds):F0} ops/s); " +
            $"0 lock errors, 0 torn reads, final memberships exact; journal_mode={journal}");
    }

    [Fact]
    public async Task MixedStateLayer_CheckpointsDeadLetterInventory_ConcurrentAndConsistent()
    {
        // The pipeline's full state surface at once: 12 tasks interleave
        // checkpoint writes (shared JSON file, monotonic per object), dead-letter
        // appends (shared JSONL), and inventory upserts (shared SQLite DB).
        SyncState.LogsDir = Path.Combine(_tmp, "logs");
        Directory.CreateDirectory(SyncState.LogsDir);
        const string ConnectorId = "round2mixed";
        var db = Path.Combine(_tmp, "mixed_inv.db");
        var dlPath = SyncState.FailedRecordsPath(ConnectorId);
        const int Tasks = 12, Ops = 120;
        var errors = new ConcurrentBag<Exception>();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sw = new Stopwatch();
        var workers = Enumerable.Range(0, Tasks).Select(t => Task.Run(async () =>
        {
            await gate.Task;
            try
            {
                using var inv = new ItemInventory(ConnectorId, db);
                for (var i = 1; i <= Ops; i++)
                {
                    SyncState.WriteCheckpoint(ConnectorId, "2026-07-17T00:00:00", $"Obj{t % 4}", i);
                    SyncState.AppendFailedRecords(
                        dlPath,
                        new List<(string, string)> { ($"DL-{t}-{i}", "[Graph] HTTP 500: simulated") },
                        $"Obj{t % 4}");
                    inv.RecordSeen(new[] { ($"MIX-{t}-{i}", $"Obj{t % 4}") }, DateTime.UtcNow);
                }
            }
            catch (Exception exc)
            {
                errors.Add(exc);
            }
        })).ToList();
        sw.Start();
        gate.SetResult();
        await Round2Support.AwaitBoundedAsync(Task.WhenAll(workers), 120, "mixed state hammer");
        sw.Stop();

        Assert.True(errors.IsEmpty,
            "state layer raised under concurrency: " +
            string.Join(" | ", errors.Take(3).Select(e => $"{e.GetType().Name}: {e.Message}")));

        // Checkpoint: parseable, since preserved, per-object chunk == max written.
        var checkpoint = SyncState.ReadCheckpoint(ConnectorId);
        Assert.NotNull(checkpoint);
        Assert.Equal("2026-07-17T00:00:00", checkpoint!["since"]?.GetValue<string>());
        var completed = checkpoint["completed"]!.AsObject();
        for (var o = 0; o < 4; o++)
            Assert.Equal(Ops, completed[$"Obj{o}"]!.GetValue<int>());

        // Dead-letter: every line parseable, exactly one entry per append.
        var dl = SyncState.ReadFailedRecords(ConnectorId);
        Assert.Equal(Tasks * Ops, dl.Count);
        Assert.Equal(Tasks * Ops, dl.Select(e => e["item_id"]!.GetValue<string>()).Distinct().Count());

        // Inventory: no lost upserts.
        using (var check = new ItemInventory(ConnectorId, db))
        {
            Assert.Equal(Tasks * Ops, check.Count());
        }
        var totalOps = Tasks * Ops * 3;
        _out.WriteLine(
            $"[mixed-state] {totalOps} interleaved checkpoint/dead-letter/inventory ops in " +
            $"{sw.ElapsedMilliseconds} ms ({totalOps * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds):F0} ops/s); " +
            $"checkpoint monotonic+parseable, {Tasks * Ops} dead-letter lines intact, inventory exact");
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// 3. HA lease contention on a SQLite lease store (contract semantics)
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// SQLite implementation of the SQL contract's HA claim semantics
/// (usp_OpenOrJoinCrawl / usp_ClaimNextObject / usp_HeartbeatClaim /
/// usp_CompleteClaim / usp_CloseCrawlIfComplete): every transition runs in a
/// BEGIN IMMEDIATE transaction (the applock/UPDLOCK stand-in), claims are
/// idempotent per token, staleness is heartbeat-age vs the CALLER's clock (so
/// clock skew is modelled per node via <see cref="SkewMs"/>), and complete /
/// close are guarded exactly like the procs (owner match; open→closed wins once).
/// </summary>
internal sealed class SqliteLeaseDb : IDisposable
{
    private readonly string _path;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _busyRetries;

    /// <summary>Per-node clock skew in ms (heartbeats and staleness use the node's own clock).</summary>
    public Func<string, long> SkewMs { get; set; } = _ => 0;

    public long BusyRetries => Interlocked.Read(ref _busyRetries);

    public SqliteLeaseDb(string path)
    {
        _path = path;
        using var conn = Open();
        Exec(conn, "PRAGMA journal_mode=WAL;");
        Exec(conn,
            """
            CREATE TABLE IF NOT EXISTS crawl (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                status TEXT NOT NULL,
                created_by TEXT NOT NULL,
                closed_by TEXT
            );
            CREATE TABLE IF NOT EXISTS claims (
                object_type  TEXT PRIMARY KEY,
                status       TEXT NOT NULL,
                owner        TEXT,
                heartbeat_ms INTEGER,
                token        TEXT
            );
            """);
    }

    private long Now(string node) => _clock.ElapsedMilliseconds + SkewMs(node);

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_path}");
        conn.Open();
        Exec(conn, "PRAGMA busy_timeout=5000;");
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }

    private static int Affected(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Run <paramref name="body"/> inside BEGIN IMMEDIATE with busy retries counted.</summary>
    private T Tx<T>(Func<SqliteConnection, T> body)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var conn = Open();
            try
            {
                Exec(conn, "BEGIN IMMEDIATE;");
                var result = body(conn);
                Exec(conn, "COMMIT;");
                return result;
            }
            catch (SqliteException exc) when (
                (exc.SqliteErrorCode is 5 or 6) && attempt < 50)  // SQLITE_BUSY / SQLITE_LOCKED
            {
                try
                {
                    Exec(conn, "ROLLBACK;");
                }
                catch (SqliteException)
                {
                }
                Interlocked.Increment(ref _busyRetries);
                Thread.Sleep(2);
            }
        }
    }

    public bool OpenOrJoin(string node, IReadOnlyList<string> objectTypes)
    {
        return Tx(conn =>
        {
            var status = Scalar(conn, "SELECT status FROM crawl WHERE id = 1;");
            if (status is null)
            {
                Exec(conn, "INSERT INTO crawl (id, status, created_by) VALUES (1, 'open', $n);", ("$n", node));
                foreach (var objectType in objectTypes)
                    Exec(conn, "INSERT INTO claims (object_type, status) VALUES ($o, 'pending');", ("$o", objectType));
                return true;  // created
            }
            return false;  // joined
        });
    }

    /// <summary>
    /// Claim one pending object, or reclaim one whose heartbeat is stale by the
    /// claimer's clock. Same token → the already-committed claim comes back
    /// (commit-ack-loss retry), never a second object.
    /// </summary>
    public (string? ObjectType, bool WasReclaim, string? PreviousOwner) ClaimNext(
        string node, long timeoutMs, Guid token)
    {
        return Tx<(string?, bool, string?)>(conn =>
        {
            var tokenText = token.ToString("N");
            // 1. Idempotent retry: my committed-but-unacked claim.
            var mine = Scalar(conn,
                "SELECT object_type FROM claims WHERE status = 'claimed' AND owner = $n AND token = $t LIMIT 1;",
                ("$n", node), ("$t", tokenText));
            if (mine is string mineObj)
                return (mineObj, false, node);

            var now = Now(node);
            // 2. First pending object.
            var pending = Scalar(conn,
                "SELECT object_type FROM claims WHERE status = 'pending' ORDER BY object_type LIMIT 1;");
            if (pending is string pendingObj)
            {
                Affected(conn,
                    "UPDATE claims SET status = 'claimed', owner = $n, heartbeat_ms = $h, token = $t " +
                    "WHERE object_type = $o;",
                    ("$n", node), ("$h", now), ("$t", tokenText), ("$o", pendingObj));
                return (pendingObj, false, null);
            }
            // 3. Stale claim (heartbeat older than timeout by MY clock).
            using var staleCmd = conn.CreateCommand();
            staleCmd.CommandText =
                "SELECT object_type, owner FROM claims " +
                "WHERE status = 'claimed' AND heartbeat_ms < $cutoff ORDER BY object_type LIMIT 1;";
            staleCmd.Parameters.AddWithValue("$cutoff", now - timeoutMs);
            using (var reader = staleCmd.ExecuteReader())
            {
                if (reader.Read())
                {
                    var staleObj = reader.GetString(0);
                    var previousOwner = reader.IsDBNull(1) ? null : reader.GetString(1);
                    reader.Close();
                    Affected(conn,
                        "UPDATE claims SET owner = $n, heartbeat_ms = $h, token = $t WHERE object_type = $o;",
                        ("$n", node), ("$h", now), ("$t", tokenText), ("$o", staleObj));
                    return (staleObj, true, previousOwner);
                }
            }
            return (null, false, null);
        });
    }

    /// <summary>Renew the lease. False when the lease was lost (reclaimed/completed elsewhere).</summary>
    public bool Heartbeat(string node, string objectType)
    {
        return Tx(conn => Affected(conn,
            "UPDATE claims SET heartbeat_ms = $h WHERE object_type = $o AND owner = $n AND status = 'claimed';",
            ("$h", Now(node)), ("$o", objectType), ("$n", node)) > 0);
    }

    /// <summary>Terminal transition, guarded by ownership. False when the lease was lost first.</summary>
    public bool Complete(string node, string objectType, string status)
    {
        return Tx(conn => Affected(conn,
            "UPDATE claims SET status = $s WHERE object_type = $o AND owner = $n AND status = 'claimed';",
            ("$s", status), ("$o", objectType), ("$n", node)) > 0);
    }

    /// <summary>Exactly-one open→closed winner; retry-safe via closed_by (like the proc's ClosedBy).</summary>
    public bool CloseIfComplete(string node)
    {
        return Tx(conn =>
        {
            var remaining = Convert.ToInt32(Scalar(conn,
                "SELECT COUNT(*) FROM claims WHERE status IN ('pending', 'claimed');"),
                CultureInfo.InvariantCulture);
            if (remaining > 0)
                return false;
            var won = Affected(conn,
                "UPDATE crawl SET status = 'closed', closed_by = $n WHERE id = 1 AND status = 'open';",
                ("$n", node)) > 0;
            if (won)
                return true;
            return Equals(Scalar(conn, "SELECT closed_by FROM crawl WHERE id = 1;"), node);
        });
    }

    public bool IsClosed() =>
        Tx(conn => Equals(Scalar(conn, "SELECT status FROM crawl WHERE id = 1;"), "closed"));

    public bool AllTerminal() =>
        Tx(conn => Convert.ToInt32(
            Scalar(conn, "SELECT COUNT(*) FROM claims WHERE status IN ('pending', 'claimed');"),
            CultureInfo.InvariantCulture) == 0);

    public Dictionary<string, (string Status, string? Owner)> Snapshot()
    {
        return Tx(conn =>
        {
            var result = new Dictionary<string, (string, string?)>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT object_type, status, owner FROM claims;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result[reader.GetString(0)] = (reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
            return result;
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }
}

public class Round2HaLeaseTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _tmp;

    public Round2HaLeaseTests(ITestOutputHelper output)
    {
        _out = output;
        _tmp = Directory.CreateTempSubdirectory("round2_lease_").FullName;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tmp, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task OpenOrJoinAndClose_Race_ExactlyOneCreatorExactlyOneCloser()
    {
        using var db = new SqliteLeaseDb(Path.Combine(_tmp, "openclose.db"));
        var objects = Enumerable.Range(0, 12).Select(i => $"Obj{i:D2}").ToList();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            return db.OpenOrJoin($"n{i}", objects);
        })).ToList();
        gate.SetResult();
        await Round2Support.AwaitBoundedAsync(Task.WhenAll(opens), 30, "open race");
        Assert.Equal(1, opens.Count(t => t.Result));

        // Drain all work through one node, then race the close.
        while (db.ClaimNext("drain", 300_000, Guid.NewGuid()).ObjectType is { } claimed)
            Assert.True(db.Complete("drain", claimed, "done"));

        var closeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closes = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await closeGate.Task;
            return db.CloseIfComplete($"n{i}");
        })).ToList();
        closeGate.SetResult();
        await Round2Support.AwaitBoundedAsync(Task.WhenAll(closes), 30, "close race");
        Assert.Equal(1, closes.Count(t => t.Result));
        _out.WriteLine(
            $"[lease-open/close] 8-node open race → 1 creator; 8-node close race → 1 closer; " +
            $"busy retries={db.BusyRetries}");
    }

    [Fact]
    public async Task ClaimToken_Retry_GetsCommittedClaimBackNotASecondObject()
    {
        using var db = new SqliteLeaseDb(Path.Combine(_tmp, "token.db"));
        db.OpenOrJoin("nA", new[] { "Account", "Contact", "Lead" });

        var token = Guid.NewGuid();
        var (first, _, _) = db.ClaimNext("nA", 300_000, token);
        Assert.NotNull(first);
        var (retry, _, _) = db.ClaimNext("nA", 300_000, token);   // commit-ack-loss retry
        Assert.Equal(first, retry);
        var (second, _, _) = db.ClaimNext("nA", 300_000, Guid.NewGuid());
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }

    private sealed class StormStats
    {
        public int Acquisitions;
        public int Failovers;
        public int PrematureReclaims;
        public int LostCompletes;
        public readonly ConcurrentDictionary<string, int> Active = new();
        public readonly ConcurrentDictionary<string, int> MaxActive = new();
        public readonly ConcurrentDictionary<string, int> DoneRecorded = new();

        public void EnterObject(string objectType)
        {
            var now = Active.AddOrUpdate(objectType, 1, (_, v) => v + 1);
            MaxActive.AddOrUpdate(objectType, now, (_, v) => Math.Max(v, now));
        }

        public void ExitObject(string objectType) => Active.AddOrUpdate(objectType, 0, (_, v) => v - 1);
    }

    /// <summary>
    /// One simulated node: claim → work (with heartbeats) → complete, until the
    /// crawl closes or the node dies. Death (<paramref name="killAtStep"/>) is
    /// deterministic: the node freezes at that work step of its first claim —
    /// no more heartbeats, no complete, the lease is abandoned mid-work.
    /// </summary>
    private static async Task RunNodeAsync(
        SqliteLeaseDb db,
        string node,
        long timeoutMs,
        int heartbeatMs,
        Func<string, int> workStepsFor,
        int stepMs,
        StormStats stats,
        ConcurrentDictionary<string, bool> killed,
        ConcurrentDictionary<string, string> holding,
        Func<string, int?>? killAtStep = null)
    {
        while (!killed.GetValueOrDefault(node))
        {
            var (objectType, wasReclaim, previousOwner) = db.ClaimNext(node, timeoutMs, Guid.NewGuid());
            if (objectType == null)
            {
                if (db.CloseIfComplete(node) || db.IsClosed())
                    return;
                await Task.Delay(15);
                continue;
            }

            Interlocked.Increment(ref stats.Acquisitions);
            if (wasReclaim)
            {
                Interlocked.Increment(ref stats.Failovers);
                if (previousOwner != null && !killed.GetValueOrDefault(previousOwner))
                    Interlocked.Increment(ref stats.PrematureReclaims);
            }

            holding[node] = objectType;
            stats.EnterObject(objectType);
            var abandoned = false;
            var lastBeat = 0L;
            var beatClock = Stopwatch.StartNew();
            var workSteps = workStepsFor(node);
            for (var step = 0; step < workSteps; step++)
            {
                if (killAtStep?.Invoke(node) is int deathStep && step == deathStep)
                    killed[node] = true;  // the node dies mid-work, holding the lease
                if (killed.GetValueOrDefault(node))
                {
                    abandoned = true;  // died mid-work: lease left to go stale
                    break;
                }
                if (beatClock.ElapsedMilliseconds - lastBeat >= heartbeatMs)
                {
                    lastBeat = beatClock.ElapsedMilliseconds;
                    db.Heartbeat(node, objectType);
                }
                await Task.Delay(stepMs);
            }
            stats.ExitObject(objectType);
            holding.TryRemove(node, out _);

            if (abandoned)
                return;

            if (db.Complete(node, objectType, "done"))
                stats.DoneRecorded.AddOrUpdate(objectType, 1, (_, v) => v + 1);
            else
                Interlocked.Increment(ref stats.LostCompletes);  // lease lost first (skew)
        }
    }

    [Fact]
    public async Task FailoverStorm_LeadersKilledMidCrawl_ExactlyOnceCompletionNoDoubleOwnership()
    {
        // 8 active-active nodes, 24 objects, ~120ms of work per object with 150ms
        // lease renewals against a 2s claim timeout. Six nodes are killed
        // mid-crawl, one after another, each while it holds a claim (a failover
        // storm). Every abandoned lease must go stale and be reclaimed by
        // EXACTLY one survivor, no object may ever have two live workers, every
        // object completes 'done' exactly once, and the crawl closes exactly once.
        using var db = new SqliteLeaseDb(Path.Combine(_tmp, "storm.db"));
        const int Nodes = 8, Objects = 24;
        // A lease timeout cannot distinguish "dead" from "merely descheduled", so
        // this margin has to exceed the worst scheduler stall, not the typical one.
        // At 300 ms with 40 ms heartbeats it was ample on developer machines and on
        // Linux CI, but a shared two-core Windows runner with eight node tasks plus
        // the rest of the suite stalls a live owner past 300 ms often enough to make
        // this test intermittently fail: a survivor then correctly reclaims a lease
        // whose owner was only paused, and MaxActive for that object reads 2. That
        // is the lease protocol behaving as designed, so the assertions below are
        // right and the timings were wrong. 2000/150 keeps the same 13x heartbeat
        // margin while tolerating a stall an order of magnitude longer.
        const long TimeoutMs = 2000;
        var objects = Enumerable.Range(0, Objects).Select(i => $"Obj{i:D2}").ToList();
        var created = db.OpenOrJoin("n0", objects);
        Assert.True(created);
        for (var i = 1; i < Nodes; i++)
            Assert.False(db.OpenOrJoin($"n{i}", objects));

        var stats = new StormStats();
        var killed = new ConcurrentDictionary<string, bool>();
        var holding = new ConcurrentDictionary<string, string>();

        // The storm: n0..n5 are leaders that die mid-object — each at step 3 of
        // the work on its first claim (deterministically holding the lease), so
        // six leases are abandoned mid-crawl and must fail over to n6/n7.
        var victims = new HashSet<string>(Enumerable.Range(0, 6).Select(i => $"n{i}"));

        var sw = Stopwatch.StartNew();
        var nodes = Enumerable.Range(0, Nodes).Select(i => Task.Run(() => RunNodeAsync(
            db, $"n{i}", TimeoutMs, heartbeatMs: 150, workStepsFor: _ => 8, stepMs: 15,
            stats, killed, holding,
            killAtStep: node => victims.Contains(node) ? 3 : null))).ToList();

        await Round2Support.AwaitBoundedAsync(Task.WhenAll(nodes), 120, "failover storm");
        sw.Stop();

        // Every object done, recorded exactly once, by exactly one final owner.
        var snapshot = db.Snapshot();
        Assert.Equal(Objects, snapshot.Count);
        Assert.All(snapshot.Values, v => Assert.Equal("done", v.Status));
        Assert.Equal(Objects, stats.DoneRecorded.Count);
        Assert.All(stats.DoneRecorded.Values, count => Assert.Equal(1, count));

        // Split-brain check: no object ever had two concurrently-active workers,
        // and with live heartbeats (150ms ≪ 2000ms) no live owner was ever ousted.
        Assert.All(stats.MaxActive, kv => Assert.Equal(1, kv.Value));
        Assert.Equal(0, stats.PrematureReclaims);
        Assert.Equal(0, stats.LostCompletes);

        // The storm actually stormed: all six leaders died holding work, and
        // each abandoned lease was reclaimed by exactly one survivor — so the
        // 24 objects took exactly 24 + 6 acquisitions.
        Assert.Equal(6, killed.Count);
        Assert.Equal(6, stats.Failovers);
        Assert.Equal(Objects + 6, stats.Acquisitions);
        Assert.True(db.IsClosed());

        _out.WriteLine(
            $"[lease-storm] nodes=8 objects=24 leaders killed mid-object=6 → " +
            $"acquisitions={stats.Acquisitions} (24 objects + 6 re-claims), failovers={stats.Failovers}, " +
            $"premature reclaims=0, lost completes=0, max concurrent owners per object=1, " +
            $"done exactly-once=24/24, crawl closed once, busy retries={db.BusyRetries}, " +
            $"wall={sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task ClockSkew_WithinRenewalMargin_NoPrematureReclaims()
    {
        // Nodes disagree on the time by ±40ms against a 300ms timeout with 40ms
        // renewals. Because staleness is judged by the CLAIMER's clock, the
        // worst-case apparent heartbeat age is (interval + skew spread) ≈ 120ms
        // — far inside the timeout — so no live lease may ever be reclaimed.
        using var db = new SqliteLeaseDb(Path.Combine(_tmp, "skew_ok.db"));
        var skews = new Dictionary<string, long> { ["n0"] = -40, ["n1"] = 0, ["n2"] = 40 };
        db.SkewMs = node => skews.GetValueOrDefault(node, 0);

        var objects = Enumerable.Range(0, 12).Select(i => $"Obj{i:D2}").ToList();
        db.OpenOrJoin("n0", objects);
        var stats = new StormStats();
        var killed = new ConcurrentDictionary<string, bool>();
        var holding = new ConcurrentDictionary<string, string>();

        var sw = Stopwatch.StartNew();
        var nodes = skews.Keys.Select(n => Task.Run(() => RunNodeAsync(
            db, n, 300, heartbeatMs: 40, workStepsFor: _ => 8, stepMs: 15,
            stats, killed, holding))).ToList();
        await Round2Support.AwaitBoundedAsync(Task.WhenAll(nodes), 120, "skewed crawl");
        sw.Stop();

        Assert.All(db.Snapshot().Values, v => Assert.Equal("done", v.Status));
        Assert.Equal(0, stats.PrematureReclaims);
        Assert.Equal(0, stats.Failovers);
        Assert.Equal(0, stats.LostCompletes);
        Assert.All(stats.MaxActive, kv => Assert.Equal(1, kv.Value));
        _out.WriteLine(
            $"[lease-skew ±40ms] 3 nodes, 12 objects, timeout 300ms, renewals 40ms: " +
            $"failovers=0, premature reclaims=0, max owners/object=1, " +
            $"acquisitions={stats.Acquisitions}, wall={sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task ClockSkew_BeyondTimeout_CausesBoundedDoubleWorkButNeverDoubleCompletion()
    {
        // Pathological skew: one node runs 700ms fast against a 300ms timeout,
        // so it sees perfectly-healthy leases as stale and steals them. The
        // design's honest failure mode is DOUBLE WORK (bounded to 2 concurrent
        // workers — why chunk checkpoints exist); its hard guarantee is that
        // ownership transitions stay serialized: the ousted owner's complete
        // no-ops, every object still completes exactly once, close stays single.
        using var db = new SqliteLeaseDb(Path.Combine(_tmp, "skew_bad.db"));
        var skews = new Dictionary<string, long> { ["n0"] = 0, ["n1"] = 0, ["fast"] = 700 };
        db.SkewMs = node => skews.GetValueOrDefault(node, 0);

        // 6 objects; the two honest nodes work slowly (~450ms per object, fresh
        // 50ms renewals throughout) while the fast-clocked node finishes its
        // share quickly and then idles — guaranteeing it scans for stale leases
        // while the honest nodes are mid-object with perfectly-live heartbeats.
        var objects = Enumerable.Range(0, 6).Select(i => $"Obj{i:D2}").ToList();
        db.OpenOrJoin("n0", objects);
        var stats = new StormStats();
        var killed = new ConcurrentDictionary<string, bool>();
        var holding = new ConcurrentDictionary<string, string>();

        var sw = Stopwatch.StartNew();
        var nodes = skews.Keys.Select(n => Task.Run(() => RunNodeAsync(
            db, n, 300, heartbeatMs: 50, workStepsFor: node => node == "fast" ? 2 : 30, stepMs: 15,
            stats, killed, holding))).ToList();
        await Round2Support.AwaitBoundedAsync(Task.WhenAll(nodes), 120, "pathological skew crawl");
        sw.Stop();

        Assert.All(db.Snapshot().Values, v => Assert.Equal("done", v.Status));
        // The pathology is real…
        Assert.True(stats.PrematureReclaims > 0,
            "a +700ms node against a 300ms timeout must steal live leases — the scenario did not engage");
        // …and bounded: never more than two workers on an object, completion
        // still exactly-once, ousted completes rejected by the owner guard.
        Assert.All(stats.MaxActive, kv => Assert.InRange(kv.Value, 1, 2));
        Assert.Equal(6, stats.DoneRecorded.Count);
        Assert.All(stats.DoneRecorded.Values, count => Assert.Equal(1, count));
        Assert.Equal(stats.PrematureReclaims, stats.LostCompletes);
        _out.WriteLine(
            $"[lease-skew +700ms] premature reclaims={stats.PrematureReclaims} (double-work engaged), " +
            $"max owners/object={stats.MaxActive.Values.Max()} (≤2), ousted completes rejected={stats.LostCompletes}, " +
            $"done exactly-once=6/6, wall={sw.ElapsedMilliseconds} ms — " +
            "skew ≥ timeout causes bounded double-work, never double-completion");
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// 4. Real Ingest pipeline: two nodes, one lease store, one checkpoint store
// ═════════════════════════════════════════════════════════════════════════════

[Collection("IngestGlobalState")]
public class Round2HaPipelineTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly CheckpointSupport.IngestHookGuard _guard = new();
    private readonly string _tmp;

    public Round2HaPipelineTests(ITestOutputHelper output)
    {
        _out = output;
        _tmp = Directory.CreateTempSubdirectory("round2_hapipe_").FullName;
        SyncState.LogsDir = _tmp;
        ServiceStop.Reset();
    }

    public void Dispose()
    {
        Ingest.ObjectWorkSourceFactory = null;
        _guard.Dispose();
        ServiceStop.Reset();
        try
        {
            Directory.Delete(_tmp, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static readonly string[] ObjectTypes =
        { "Account", "Contact", "Lead", "Opportunity", "Case", "Order" };

    /// <summary>Graph client fake with latency + exactly-once ack bookkeeping per node.</summary>
    private sealed class AckTrackingClient : GraphClient
    {
        public int LatencyMs { get; init; } = 2;

        public ConcurrentDictionary<string, int> Acked { get; } = new();

        public override async Task<List<JsonObject>> BatchRequestsAsync(List<JsonObject> requestsPayload)
        {
            if (LatencyMs > 0)
                await Task.Delay(LatencyMs);
            var responses = new List<JsonObject>(requestsPayload.Count);
            foreach (var request in requestsPayload)
            {
                var url = request["url"]!.ToString();
                var itemId = Uri.UnescapeDataString(url[(url.LastIndexOf('/') + 1)..]);
                Acked.AddOrUpdate(itemId, 1, (_, v) => v + 1);
                responses.Add(new JsonObject { ["id"] = request["id"]!.ToString(), ["status"] = 200 });
            }
            return responses;
        }
    }

    /// <summary>
    /// IObjectWorkSource over the SQLite lease store — what
    /// HaCoordinator.CreateWorkSource is to the SQL procs. ClaimNextAsync polls
    /// while the crawl is incomplete (the continuous command's rejoin loop
    /// collapsed into the source) so a survivor can pick up a dead node's stale
    /// lease. A node flagged Dead freezes: no claims, no heartbeats, no
    /// completes — its held lease is abandoned exactly like a crashed process.
    /// </summary>
    private sealed class LeaseWorkSource : IObjectWorkSource
    {
        private readonly SqliteLeaseDb _db;
        private readonly string _node;
        private readonly long _timeoutMs;
        private readonly int _heartbeatMs;

        public volatile bool Dead;
        public int Claims;
        public int Reclaims;

        public LeaseWorkSource(SqliteLeaseDb db, string node, long timeoutMs, int heartbeatMs)
        {
            _db = db;
            _node = node;
            _timeoutMs = timeoutMs;
            _heartbeatMs = heartbeatMs;
        }

        public async Task<string?> ClaimNextAsync()
        {
            while (true)
            {
                if (Dead)
                    return null;
                var (objectType, wasReclaim, _) = _db.ClaimNext(_node, _timeoutMs, Guid.NewGuid());
                if (objectType != null)
                {
                    Interlocked.Increment(ref Claims);
                    if (wasReclaim)
                        Interlocked.Increment(ref Reclaims);
                    return objectType;
                }
                if (_db.AllTerminal() || _db.IsClosed())
                    return null;
                await Task.Delay(15);  // crawl incomplete: another node's lease may yet go stale
            }
        }

        public IDisposable BeginHeartbeat(string objectType)
        {
            var cts = new CancellationTokenSource();
            var loop = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(_heartbeatMs, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    if (!Dead)
                        _db.Heartbeat(_node, objectType);
                }
            });
            return new HeartbeatScope(cts, loop);
        }

        public Task CompleteAsync(string objectType, bool succeeded)
        {
            if (!Dead)
                _db.Complete(_node, objectType, succeeded ? "done" : "failed");
            return Task.CompletedTask;
        }

        private sealed class HeartbeatScope : IDisposable
        {
            private readonly CancellationTokenSource _cts;
            private readonly Task _loop;

            public HeartbeatScope(CancellationTokenSource cts, Task loop)
            {
                _cts = cts;
                _loop = loop;
            }

            public void Dispose()
            {
                _cts.Cancel();
                try
                {
                    _loop.Wait(TimeSpan.FromSeconds(5));
                }
                catch (AggregateException)
                {
                }
                _cts.Dispose();
            }
        }
    }

    private static AppConfig PipelineConfig() => new()
    {
        ClientId = "00000000-0000-0000-0000-000000000000",
        TenantId = "00000000-0000-0000-0000-000000000001",
        RepoRoot = "",
        SchemaConfig = new JsonObject(),
        OwdFieldMap = new Dictionary<string, string>(),
        ParentMap = new Dictionary<string, (string, string)>(),
        OwdOverrides = new Dictionary<string, string>(),
        ObjectNames = ObjectTypes.ToList(),
        UseNewAclEngine = false,
        UseGroupAcl = false,
        UseEntityDefinitionOwd = false,
        DebugObjectType = "Account",  // static list only sizes the worker pool (claims drive the work)
        DebugItemId = null,
        Tuning = new TuningSettings
        {
            GraphApiVersion = "v1.0",
            GraphMaxRetries = 2,
            GraphRetryBackoffBase = 1,
            ConnectionTimeoutSeconds = 600,
            ConnectionRetryIntervalSeconds = 15,
            SchemaRetryIntervalSeconds = 15,
            SalesforceQueryLimit = 0,
            SalesforceBatchSize = 100,
            AclMaxParentDepth = 5,
            IngestChunkSize = 100,
            IngestGraphBatchSize = 20,
            GraphConcurrentBatches = 4,
            ParallelObjectWorkers = 1,  // one worker per node → clean claim accounting
        },
        Connector = new ConnectorSettings
        {
            Id = "Round2HA",
            Name = "Round 2 HA",
            Description = "Two-node HA pipeline stress.",
            Schema = new JsonArray(),
            Template = new JsonObject { ["id"] = "display" },
            Salesforce = new SalesforceSettings
            {
                InstanceUrl = "https://round2.my.salesforce.com",
                ApiVersion = "v60.0",
                ClientId = "round2-client",
                ClientSecret = "round2-secret",
            },
        },
    };

    private static string ItemId(string objectType, int index) => $"{objectType.ToUpperInvariant()[..3]}{index:D5}";

    private static JsonObject MakeRecord(string objectType, int index) => new()
    {
        ["Id"] = ItemId(objectType, index),
        ["objectType"] = objectType,
        ["url"] = $"https://round2/{objectType}/{index}",
    };

    /// <summary>Install the fetch/ACL/transform hooks; optional per-chunk death trigger.</summary>
    private static void InstallHooks(int itemsPerType, Func<string, int, bool>? dieBeforeChunk = null)
    {
        Ingest.GetObjectConfigHook = objectType =>
            new SalesforceObjectConfig(ObjectType: objectType, Fields: new[] { "Id" });
        Ingest.IterObjectChunksHook = (_, objCfg, _, chunkSize) =>
            ChunksAsync(objCfg.ObjectType, itemsPerType, chunkSize, dieBeforeChunk);
        Ingest.LegacyAclResolverFactory = (config, _, _) => new CheckpointSupport.FakeAclResolver(
            config,
            request => request.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToDictionary(
                    record => record["Id"]!.GetValue<string>(),
                    _ => new List<Dictionary<string, string>>())));
        var transformer = new CheckpointSupport.FakeTransformer
        {
            OnTransform = (item, _) => new List<JsonObject> { CheckpointSupport.Item(item["Id"]!.GetValue<string>()) },
        };
        Ingest.TransformerFactory = (_, _, _) => transformer;
        Ingest.GetObjectCountsHook = (_, _) => Task.FromResult(new Dictionary<string, int>());
    }

    private static async IAsyncEnumerable<List<JsonObject>> ChunksAsync(
        string objectType, int count, int chunkSize, Func<string, int, bool>? dieBeforeChunk)
    {
        var chunkIndex = 0;
        for (var start = 1; start <= count; start += chunkSize)
        {
            chunkIndex++;
            if (dieBeforeChunk != null && dieBeforeChunk(objectType, chunkIndex))
                throw new InvalidOperationException($"simulated node crash before {objectType} chunk #{chunkIndex}");
            var n = Math.Min(chunkSize, count - start + 1);
            var chunk = new List<JsonObject>(n);
            for (var i = 0; i < n; i++)
                chunk.Add(MakeRecord(objectType, start + i));
            yield return chunk;
            await Task.Yield();
        }
    }

    [Fact]
    public async Task TwoNodes_ActiveActive_SplitTheCrawl_ZeroDuplicateAcks()
    {
        // Two REAL IngestContentAsync runs ("nodes") share one SQLite lease store
        // and one checkpoint store. Every object must be worked by exactly one
        // node, every item PUT exactly once across the fleet, and the crawl must
        // close exactly once — the active-active zero-double-crawl guarantee.
        const int ItemsPerType = 300;
        using var db = new SqliteLeaseDb(Path.Combine(_tmp, "pipe_clean.db"));
        Assert.True(db.OpenOrJoin("nodeA", ObjectTypes));

        InstallHooks(ItemsPerType);
        var cfg = PipelineConfig();
        var wsA = new LeaseWorkSource(db, "nodeA", timeoutMs: 400, heartbeatMs: 25);
        var wsB = new LeaseWorkSource(db, "nodeB", timeoutMs: 400, heartbeatMs: 25);
        var sources = new ConcurrentQueue<IObjectWorkSource>(new IObjectWorkSource[] { wsA, wsB });
        Ingest.ObjectWorkSourceFactory = (_, _) =>
            Task.FromResult(sources.TryDequeue(out var ws)
                ? ws
                : throw new InvalidOperationException("more pipeline runs than work sources"));

        var clientA = new AckTrackingClient();
        var clientB = new AckTrackingClient();

        var sw = Stopwatch.StartNew();
        var runA = Task.Run(() => Ingest.IngestContentAsync(cfg, clientA, since: null, dashboard: null));
        var runB = Task.Run(() => Ingest.IngestContentAsync(cfg, clientB, since: null, dashboard: null));
        await Round2Support.AwaitBoundedAsync(Task.WhenAll(runA, runB), 120, "two-node crawl");
        sw.Stop();
        var statsA = await runA;
        var statsB = await runB;

        var total = ObjectTypes.Length * ItemsPerType;
        // Zero double-crawl: the union of per-node acks covers every item exactly once.
        var overlap = clientA.Acked.Keys.Count(id => clientB.Acked.ContainsKey(id));
        Assert.Equal(0, overlap);
        Assert.Equal(total, clientA.Acked.Count + clientB.Acked.Count);
        Assert.All(clientA.Acked.Values, v => Assert.Equal(1, v));
        Assert.All(clientB.Acked.Values, v => Assert.Equal(1, v));
        Assert.Equal(total, statsA.SuccessCount + statsB.SuccessCount);
        Assert.Equal(0, statsA.FailedCount + statsB.FailedCount);
        Assert.Equal(0, statsA.SkippedCount + statsB.SkippedCount);

        // Each object claimed exactly once, by exactly one node; close once.
        Assert.Equal(ObjectTypes.Length, wsA.Claims + wsB.Claims);
        Assert.Equal(0, wsA.Reclaims + wsB.Reclaims);
        Assert.All(db.Snapshot().Values, v => Assert.Equal("done", v.Status));
        Assert.True(db.CloseIfComplete("nodeA"));
        Assert.False(db.CloseIfComplete("nodeB"));  // exactly-one closer

        _out.WriteLine(
            $"[ha-pipeline clean] 2 nodes × real IngestContentAsync, {total} items / 6 objects: " +
            $"nodeA claimed {wsA.Claims} (acked {clientA.Acked.Count}), " +
            $"nodeB claimed {wsB.Claims} (acked {clientB.Acked.Count}); " +
            $"duplicate acks=0, lost items=0, failovers=0, crawl closed once, " +
            $"wall={sw.Elapsed.TotalSeconds:F1}s, busy retries={db.BusyRetries}");
    }

    [Fact]
    public async Task NodeDiesMidCrawl_SurvivorReclaimsStalelease_ResumesFromCheckpointWithoutDuplicates()
    {
        // Leader-death failover through the REAL pipeline: nodeA claims its first
        // object, ingests + checkpoints 2 chunks, then hard-crashes (fetch throws,
        // work source freezes — no complete, no heartbeats). Its lease goes stale;
        // nodeB — still crawling — reclaims it, re-reads the SHARED checkpoint
        // (the claim-time re-read in RunClaimedObjectWorkersAsync) and finishes
        // the object from chunk 3. Chunk-atomic checkpointing means zero
        // duplicate acks; nothing may be lost; 'done' lands exactly once.
        const int ItemsPerType = 300;  // 3 chunks of 100 per object
        using var db = new SqliteLeaseDb(Path.Combine(_tmp, "pipe_failover.db"));
        Assert.True(db.OpenOrJoin("nodeA", ObjectTypes));

        var cfg = PipelineConfig();
        var wsA = new LeaseWorkSource(db, "nodeA", timeoutMs: 350, heartbeatMs: 25);
        var wsB = new LeaseWorkSource(db, "nodeB", timeoutMs: 350, heartbeatMs: 25);

        // nodeA starts alone, so its first claim is deterministically the first
        // pending object in ordinal order — "Account". It dies right before that
        // object's chunk #3; the Dead guard keeps nodeB's later re-enumeration
        // of the reclaimed object alive.
        const string VictimObject = "Account";
        InstallHooks(ItemsPerType, dieBeforeChunk: (objectType, chunkIndex) =>
        {
            if (!wsA.Dead && objectType == VictimObject && chunkIndex == 3)
            {
                wsA.Dead = true;  // freeze the node: lease abandoned mid-object
                return true;
            }
            return false;
        });

        // nodeA starts alone; nodeB joins shortly after — it must still be
        // polling for work when nodeA's lease turns stale.
        var sources = new ConcurrentQueue<IObjectWorkSource>(new IObjectWorkSource[] { wsA, wsB });
        Ingest.ObjectWorkSourceFactory = (_, _) =>
            Task.FromResult(sources.TryDequeue(out var ws)
                ? ws
                : throw new InvalidOperationException("more pipeline runs than work sources"));

        var clientA = new AckTrackingClient();
        var clientB = new AckTrackingClient();
        var sw = Stopwatch.StartNew();
        var runA = Task.Run(() => Ingest.IngestContentAsync(cfg, clientA, since: null, dashboard: null));
        await Task.Delay(30);
        var runB = Task.Run(() => Ingest.IngestContentAsync(cfg, clientB, since: null, dashboard: null));
        await Round2Support.AwaitBoundedAsync(Task.WhenAll(runA, runB), 120, "failover crawl");
        sw.Stop();
        var statsB = await runB;

        Assert.True(wsA.Dead, "the death trigger never fired — nodeA did not reach Account chunk #3");
        var total = ObjectTypes.Length * ItemsPerType;

        // Nothing lost, nothing double-ingested: chunk-atomic death means the
        // 200 items nodeA checkpointed stay skipped, the rest lands via nodeB.
        var duplicates = clientA.Acked.Keys.Count(id => clientB.Acked.ContainsKey(id));
        Assert.Equal(0, duplicates);
        Assert.Equal(total, clientA.Acked.Count + clientB.Acked.Count);
        Assert.Equal(200, clientA.Acked.Count);  // exactly chunks 1-2 of the victim object

        // The survivor reclaimed the stale lease and resumed from the shared
        // checkpoint: exactly nodeA's 200 checkpointed items were skipped.
        Assert.Equal(1, wsB.Reclaims);
        Assert.Equal(200, statsB.SkippedCount);
        var snapshot = db.Snapshot();
        Assert.All(snapshot.Values, v => Assert.Equal("done", v.Status));
        Assert.Equal("nodeB", snapshot[VictimObject].Owner);

        // The crash is visible to operators in the dead-letter file.
        var deadLetters = SyncState.ReadFailedRecords(cfg.Connector.Id);
        Assert.Contains(deadLetters, record =>
            record["item_id"]?.ToString() == "WORKER_CRASH"
            && record["object_type"]?.ToString() == VictimObject);

        Assert.True(db.CloseIfComplete("nodeB"));

        _out.WriteLine(
            $"[ha-pipeline failover] nodeA died before {VictimObject} chunk #3 " +
            $"(acked {clientA.Acked.Count}); lease went stale (350ms) and nodeB reclaimed it " +
            $"(reclaims={wsB.Reclaims}), skipped {statsB.SkippedCount} checkpointed items, " +
            $"finished the crawl: union acks={clientA.Acked.Count + clientB.Acked.Count}/{total}, " +
            $"duplicates=0, done exactly-once=6/6, crawl closed once, wall={sw.Elapsed.TotalSeconds:F1}s");
    }
}
