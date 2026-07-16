// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// TestGraph/LegacyAclResolverCycleTests.cs
// ----------------------------------------
// Regression tests for the 2026-07 code-review findings in the legacy ACL
// resolver and the Graph ingest concurrency dial:
//
//   - LOW-MED: Graph.AclResolver.ResolveGroupAsync used to guard only direct
//     self-reference; a membership cycle A→B→A recursed until stack overflow.
//     A visited set now terminates the walk, cyclic references contribute
//     nothing, and partially expanded groups on the cycle path are not cached
//     (so a later top-level resolve still sees the full membership closure).
//
//   - LOW-1: AdaptiveConcurrency.Current is read by worker loops outside the
//     lock while OnSuccess/OnThrottle mutate it — it must stay within
//     [1, max] under concurrent updates.

using System.Reflection;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests.TestGraph;

public class LegacyAclResolverCycleTests
{
    /// <summary>Uses the protected test-double ctor (no Salesforce token fetch).</summary>
    private sealed class TestableResolver : AclResolver
    {
        public TestableResolver()
            : base(new AppConfig { TenantId = "11111111-2222-3333-4444-555555555555" })
        {
        }
    }

    /// <summary>
    /// Build a resolver whose pre-warmed group caches are seeded directly
    /// (the Python tests patch the equivalent private attributes).
    /// </summary>
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

    private static Group RegularGroup(string id) => new() { Id = id, Type = "Regular" };

    [Fact]
    public async Task CyclicGroupMembershipTerminatesWithFullClosure()
    {
        // A → {U1, B}, B → {U2, A}: previously recursed to stack overflow.
        var resolver = MakeResolver(
            new Dictionary<string, Group>
            {
                ["00GA"] = RegularGroup("00GA"),
                ["00GB"] = RegularGroup("00GB"),
            },
            new Dictionary<string, List<string>>
            {
                ["00GA"] = new() { "005USER1", "00GB" },
                ["00GB"] = new() { "005USER2", "00GA" },
            });

        var (userIds, includesEveryone) = await resolver.ResolveGroupAsync("00GA");

        Assert.Equal(new HashSet<string> { "005USER1", "005USER2" }, userIds);
        Assert.False(includesEveryone);

        // Groups on the cycle path were not cached with a partial expansion:
        // resolving B afterwards must also yield the full closure.
        var (bUserIds, _) = await resolver.ResolveGroupAsync("00GB");
        Assert.Equal(new HashSet<string> { "005USER1", "005USER2" }, bUserIds);
    }

    [Fact]
    public async Task ThreeNodeCycleTerminates()
    {
        // A → B → C → A with one user hanging off each node.
        var resolver = MakeResolver(
            new Dictionary<string, Group>
            {
                ["00GA"] = RegularGroup("00GA"),
                ["00GB"] = RegularGroup("00GB"),
                ["00GC"] = RegularGroup("00GC"),
            },
            new Dictionary<string, List<string>>
            {
                ["00GA"] = new() { "005USER1", "00GB" },
                ["00GB"] = new() { "005USER2", "00GC" },
                ["00GC"] = new() { "005USER3", "00GA" },
            });

        var (userIds, _) = await resolver.ResolveGroupAsync("00GA");

        Assert.Equal(new HashSet<string> { "005USER1", "005USER2", "005USER3" }, userIds);
    }

    [Fact]
    public async Task DiamondMembershipIsNotMisreportedAsCycle()
    {
        // A → {B, C}, B → {D}, C → {D}, D → {U1}: D is reached twice via
        // different paths but is NOT a cycle — its users must be included and
        // the acyclic results must be cached.
        var resolver = MakeResolver(
            new Dictionary<string, Group>
            {
                ["00GA"] = RegularGroup("00GA"),
                ["00GB"] = RegularGroup("00GB"),
                ["00GC"] = RegularGroup("00GC"),
                ["00GD"] = RegularGroup("00GD"),
            },
            new Dictionary<string, List<string>>
            {
                ["00GA"] = new() { "00GB", "00GC" },
                ["00GB"] = new() { "00GD" },
                ["00GC"] = new() { "00GD" },
                ["00GD"] = new() { "005USER1" },
            });

        var (userIds, includesEveryone) = await resolver.ResolveGroupAsync("00GA");

        Assert.Equal(new HashSet<string> { "005USER1" }, userIds);
        Assert.False(includesEveryone);

        // Acyclic expansion → result was cached for subsequent lookups.
        var groupCache = (Dictionary<string, (HashSet<string> UserIds, bool IncludesEveryone)>)
            typeof(AclResolver)
                .GetField("_groupCache", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(resolver)!;
        Assert.True(groupCache.ContainsKey("00GA"));
        Assert.True(groupCache.ContainsKey("00GD"));
    }
}

public class AdaptiveConcurrencyTests
{
    [Fact]
    public async Task CurrentStaysWithinBoundsUnderConcurrentUpdates()
    {
        const int max = 8;
        var concurrency = new AdaptiveConcurrency(max);
        using var cts = new CancellationTokenSource();

        var mutators = Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 5000; i++)
            {
                if ((i + w) % 3 == 0)
                    concurrency.OnThrottle();
                else
                    concurrency.OnSuccess();
            }
        })).ToList();

        var reader = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var current = concurrency.Current;
                Assert.InRange(current, 1, max);
            }
        });

        await Task.WhenAll(mutators);
        cts.Cancel();
        await reader;
    }
}
