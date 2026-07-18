// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// TestGraph/LegacyAclResolverCycleStressTests.cs
// ----------------------------------------------
// ADVERSARIAL stress harness for Graph.AclResolver.ResolveGroupAsync — the
// legacy sharing-model group-expansion engine with cycle detection and the
// subtle "partial expansions on a cycle path are not cached" bookkeeping
// (LegacyAclResolver.cs CacheGroup / OpenCycleTargets).
//
// The correctness oracle is simple and total: the set of users a group grants
// is the set of Salesforce User Ids reachable from it by following
// group->member edges, regardless of any cycles.  Cycles must change
// *termination and caching*, never the final user set.  A partial expansion
// that is wrongly cached is a SILENT UNDER-GRANT (a security defect: an item
// is indexed as visible to fewer users than Salesforce actually permits), so
// these tests hammer the shared-resolver cache with randomised cyclic graphs
// and assert every group still resolves to its full reachable closure.
//
// Scenarios:
//   1. Fresh resolver per group (baseline correctness on adversarial graphs).
//   2. ONE shared resolver, every group resolved in randomised order
//      (cache-poisoning detector — the high-value invariant).
//   3. ONE shared resolver, all groups resolved CONCURRENTLY (races on the
//      lock-guarded _groupCache must not lose grants or throw).
//   4. Deep linear chain (termination + resolution-time measurement).

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Salesforce;
using Xunit.Abstractions;

namespace SalesforceCopilotConnector.Tests.TestGraph;

public class LegacyAclResolverCycleStressTests
{
    private readonly ITestOutputHelper _out;

    public LegacyAclResolverCycleStressTests(ITestOutputHelper output) => _out = output;

    private const string UserPrefix = "005";
    private const string GroupPrefix = "00G";

    private sealed class TestableResolver : AclResolver
    {
        public TestableResolver()
            : base(new AppConfig { TenantId = "11111111-2222-3333-4444-555555555555" })
        {
        }
    }

    private static AclResolver MakeResolver(
        Dictionary<string, Group> groupsById,
        Dictionary<string, List<string>> membersByGroup)
    {
        var resolver = new TestableResolver();
        typeof(AclResolver)
            .GetField("_allGroupsById", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(resolver, groupsById);
        typeof(AclResolver)
            .GetField("_allGroupMembersByGroup", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(resolver, membersByGroup);
        return resolver;
    }

    /// <summary>A randomly generated group graph plus its brute-force oracle.</summary>
    private sealed class Graph
    {
        public required Dictionary<string, Group> Groups { get; init; }
        public required Dictionary<string, List<string>> Members { get; init; }
        public required List<string> GroupIds { get; init; }
        public required HashSet<string> OrgGroups { get; init; }

        /// <summary>
        /// Total reachability oracle: follow group->group edges, collecting user
        /// members of every reachable group.  An Organization group short-circuits
        /// to "everyone" and — matching the resolver — does NOT expand its members.
        /// </summary>
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
                    continue; // Organization does not enumerate members
                }
                foreach (var m in Members.GetValueOrDefault(g) ?? new List<string>())
                {
                    if (m == g)
                        continue; // resolver skips direct self-reference
                    if (m.StartsWith(UserPrefix, StringComparison.Ordinal))
                        users.Add(m);
                    else if (Groups.ContainsKey(m))
                        stack.Push(m);
                    // members that are neither users nor known groups contribute nothing
                }
            }
            return (users, everyone);
        }
    }

    /// <summary>
    /// Build a random directed group graph guaranteed to contain cycles.
    /// nGroups groups, nUsers users; each group gets a random handful of user
    /// members and group members (edges point anywhere, including backwards and
    /// to self), so back-edges and multi-node cycles are dense.
    /// </summary>
    private static Graph BuildRandomGraph(int seed, int nGroups, int nUsers, int orgEvery = 0)
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
            var nUserMembers = rng.Next(0, 4);
            for (var k = 0; k < nUserMembers; k++)
                list.Add(userIds[rng.Next(userIds.Count)]);
            var nGroupMembers = rng.Next(0, 5);
            for (var k = 0; k < nGroupMembers; k++)
                list.Add(groupIds[rng.Next(groupIds.Count)]); // may point backwards/self -> cycles
            members[gid] = list;
        }

        return new Graph { Groups = groups, Members = members, GroupIds = groupIds, OrgGroups = orgGroups };
    }

    private static bool HasCycle(Graph g)
    {
        var color = new Dictionary<string, int>(); // 0=unseen,1=onstack,2=done
        bool Dfs(string n)
        {
            color[n] = 1;
            foreach (var m in g.Members.GetValueOrDefault(n) ?? new List<string>())
            {
                if (!g.Groups.ContainsKey(m) || g.OrgGroups.Contains(m))
                    continue;
                var c = color.GetValueOrDefault(m, 0);
                if (c == 1) return true;
                if (c == 0 && Dfs(m)) return true;
            }
            color[n] = 2;
            return false;
        }
        return g.GroupIds.Any(id => color.GetValueOrDefault(id, 0) == 0 && Dfs(id));
    }

    // ── Scenario 1: fresh resolver per group ─────────────────────────────────

    [Fact]
    public async Task FreshResolver_EveryGroupResolvesToFullClosure_AcrossManyRandomGraphs()
    {
        var totalGroups = 0;
        var cyclicGraphs = 0;
        var sw = Stopwatch.StartNew();
        for (var seed = 1; seed <= 60; seed++)
        {
            var g = BuildRandomGraph(seed, nGroups: 40, nUsers: 30, orgEvery: 13);
            if (HasCycle(g)) cyclicGraphs++;
            foreach (var gid in g.GroupIds)
            {
                var resolver = MakeResolver(g.Groups, g.Members); // fresh each time
                var (users, everyone) = await resolver.ResolveGroupAsync(gid);
                var (oracleUsers, oracleEveryone) = g.Oracle(gid);
                Assert.True(oracleUsers.SetEquals(users),
                    $"seed={seed} group={gid}: expected {oracleUsers.Count} users, got {users.Count} " +
                    $"(missing: {string.Join(",", oracleUsers.Except(users))}; extra: {string.Join(",", users.Except(oracleUsers))})");
                Assert.Equal(oracleEveryone, everyone);
                totalGroups++;
            }
        }
        sw.Stop();
        _out.WriteLine($"[fresh] {totalGroups} group resolves across 60 graphs " +
                       $"({cyclicGraphs}/60 contained cycles) in {sw.ElapsedMilliseconds} ms");
    }

    // ── Scenario 2: shared resolver, randomised order (cache poisoning) ───────

    [Fact]
    public async Task SharedResolver_RandomOrder_NeverCachesPartialExpansion()
    {
        var totalGroups = 0;
        var sw = Stopwatch.StartNew();
        for (var seed = 1; seed <= 60; seed++)
        {
            var g = BuildRandomGraph(seed, nGroups: 50, nUsers: 40, orgEvery: 17);
            var resolver = MakeResolver(g.Groups, g.Members); // ONE resolver reused for all groups

            // Resolve every group in a shuffled order: an earlier resolve that
            // wrongly cached a partial (cycle-cut) expansion would corrupt a later
            // top-level resolve of that same group.
            var order = g.GroupIds.ToList();
            var shuffle = new Random(seed * 7919);
            for (var i = order.Count - 1; i > 0; i--)
            {
                var j = shuffle.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }

            foreach (var gid in order)
            {
                var (users, everyone) = await resolver.ResolveGroupAsync(gid);
                var (oracleUsers, oracleEveryone) = g.Oracle(gid);
                Assert.True(oracleUsers.SetEquals(users),
                    $"seed={seed} group={gid} (shared/random-order): expected {oracleUsers.Count} users, got {users.Count} " +
                    $"(missing: {string.Join(",", oracleUsers.Except(users))})");
                Assert.Equal(oracleEveryone, everyone);
                totalGroups++;
            }
        }
        sw.Stop();
        _out.WriteLine($"[shared/random-order] {totalGroups} group resolves on reused resolvers in {sw.ElapsedMilliseconds} ms");
    }

    // ── Scenario 3: shared resolver, concurrent resolution ───────────────────

    [Fact]
    public async Task SharedResolver_Concurrent_NoLostGrantsNoRaces()
    {
        for (var seed = 1; seed <= 20; seed++)
        {
            var g = BuildRandomGraph(seed, nGroups: 60, nUsers: 50, orgEvery: 0);
            var resolver = MakeResolver(g.Groups, g.Members);

            // Hammer the single shared resolver from many threads at once; every
            // group is resolved concurrently, several times, to race the
            // lock-guarded _groupCache reads/writes.
            var failures = new ConcurrentBag<string>();
            var tasks = new List<Task>();
            foreach (var gid in g.GroupIds)
            {
                for (var rep = 0; rep < 3; rep++)
                {
                    var captured = gid;
                    tasks.Add(Task.Run(async () =>
                    {
                        var (users, _) = await resolver.ResolveGroupAsync(captured);
                        var (oracleUsers, _) = g.Oracle(captured);
                        if (!oracleUsers.SetEquals(users))
                            failures.Add($"seed={seed} group={captured}: expected {oracleUsers.Count}, got {users.Count} " +
                                         $"(missing {string.Join(",", oracleUsers.Except(users))})");
                    }));
                }
            }
            await Task.WhenAll(tasks);
            Assert.True(failures.IsEmpty, string.Join(" | ", failures.Take(5)));
        }
    }

    // ── Scenario 4: deep linear chain (termination + timing) ─────────────────

    private static (Dictionary<string, Group>, Dictionary<string, List<string>>) LinearCycle(int depth)
    {
        // G0 -> {U0, G1}, G1 -> {U1, G2}, ..., G(depth-1) -> {U(depth-1), G0}:
        // a single giant cycle of length `depth` (recurses one frame per node).
        var groups = new Dictionary<string, Group>();
        var members = new Dictionary<string, List<string>>();
        for (var i = 0; i < depth; i++)
        {
            var gid = $"{GroupPrefix}{i:D5}";
            groups[gid] = new Group { Id = gid, Type = "Regular" };
            members[gid] = new List<string> { $"{UserPrefix}{i:D6}", $"{GroupPrefix}{(i + 1) % depth:D5}" };
        }
        return (groups, members);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(300)]
    public async Task DeepLinearChain_WithinDepthCap_ResolvesFullClosure(int depth)
    {
        // Depths comfortably under MaxGroupNestingDepth (400) must resolve to the
        // complete closure and terminate quickly.
        var (groups, members) = LinearCycle(depth);
        var resolver = MakeResolver(groups, members);

        var sw = Stopwatch.StartNew();
        var (users, everyone) = await resolver.ResolveGroupAsync($"{GroupPrefix}{0:D5}");
        sw.Stop();

        Assert.Equal(depth, users.Count);
        Assert.False(everyone);
        _out.WriteLine($"[deep-chain] depth={depth}: resolved {users.Count} users in {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task DeepLinearChain_BeyondDepthCap_TerminatesWithoutStackOverflow()
    {
        // A 5000-deep nesting chain would previously recurse ~5000 frames and throw an
        // (uncatchable) StackOverflowException, taking down the whole process.  The
        // depth guard must make this terminate gracefully: fail-closed (a bounded set
        // of grants) rather than crash.  The key assertion is simply that we get here.
        const int depth = 5000;
        var (groups, members) = LinearCycle(depth);
        var resolver = MakeResolver(groups, members);

        var sw = Stopwatch.StartNew();
        var (users, everyone) = await resolver.ResolveGroupAsync($"{GroupPrefix}{0:D5}");
        sw.Stop();

        Assert.False(everyone);
        Assert.InRange(users.Count, 1, depth); // fail-closed: bounded, never over-grants
        _out.WriteLine($"[deep-chain] depth={depth} (beyond cap): terminated with " +
                       $"{users.Count} users in {sw.ElapsedMilliseconds} ms (no stack overflow)");
    }
}
