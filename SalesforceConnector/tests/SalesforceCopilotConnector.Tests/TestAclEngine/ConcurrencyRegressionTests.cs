// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// TestAclEngine/ConcurrencyRegressionTests.cs
// -------------------------------------------
// Regression tests for the 2026-07 code-review findings around shared mutable
// state in the ACL engine.  One PrincipalMapper / ShareFetcher / QueueHandler
// instance is shared across parallel object workers (Graph/Ingest.cs launches
// up to N ToAclEntriesAsync tasks concurrently), so their caches must be safe
// for concurrent reads and writes.
//
// Covers:
//   - PrincipalMapper._principalCache / _userDetailsCache under parallel
//     ResolveIdentifierAsync / ToAclEntriesAsync (HIGH: data race → crash or
//     torn read → item indexed with another user's principal)
//   - ShareFetcher slow-path caches (_ownerCache, _noOwnerIdTypes,
//     _parentFieldCache, _accessLevelFieldCache) under parallel cache-miss
//     lookups (MED-1)
//   - QueueHandler.PrewarmAsync actually feeding ResolveStaticGroupAsync so a
//     prewarmed lookup issues zero per-group SOQL (MED-2), with per-group SOQL
//     fallback kept when the prewarm query fails
//   - AclResolver.WarnedNoParent warn-dedup path under concurrency (LOW-1)

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.AclEngine;
// Import only GraphClient by alias — a bare `using ...Graph;` would make
// AclResolver ambiguous with SalesforceCopilotConnector.AclEngine.AclResolver.
using GraphClient = SalesforceCopilotConnector.Graph.GraphClient;

namespace SalesforceCopilotConnector.Tests.TestAclEngine;

// ---------------------------------------------------------------------------
// Helpers / fixtures
// ---------------------------------------------------------------------------

/// <summary>Fake SalesforceClient with pluggable query/describe handlers.</summary>
file sealed class ConcurrencyFakeSfClient : SalesforceClient
{
    public Func<string, List<JsonObject>> QueryAllHandler = _ => new List<JsonObject>();
    public Func<string, JsonObject> DescribeHandler = _ => new JsonObject { ["fields"] = new JsonArray() };
    private int _queryAllCallCount;
    public int QueryAllCallCount => _queryAllCallCount;

    public ConcurrencyFakeSfClient()
        : base("https://test.my.salesforce.com", "60.0", "mock-token")
    {
    }

    public override Task<List<JsonObject>> QueryAllAsync(string soql, bool tooling = false)
    {
        Interlocked.Increment(ref _queryAllCallCount);
        return Task.FromResult(QueryAllHandler(soql));
    }

    public override Task<JsonObject> DescribeSObjectAsync(string sobjectName)
        => Task.FromResult(DescribeHandler(sobjectName));
}

// ---------------------------------------------------------------------------
// HIGH — PrincipalMapper caches under concurrency
// ---------------------------------------------------------------------------

public class PrincipalMapperConcurrencyTests
{
    private const int Workers = 16;

    [Fact]
    public async Task ParallelResolveIdentifierCallsAreSafeAndCorrect()
    {
        // No Graph client → each identifier resolves to itself and is cached.
        var mapper = new PrincipalMapper(new ConcurrencyFakeSfClient());

        var identifiers = Enumerable.Range(0, 2000)
            .Select(i => $"user{i}@nokia.com")
            .ToList();

        // Every worker resolves every identifier: massively concurrent writes
        // of distinct keys plus concurrent re-reads of already-written keys.
        var tasks = Enumerable.Range(0, Workers).Select(_ => Task.Run(async () =>
        {
            foreach (var ident in identifiers)
            {
                var resolved = await mapper.ResolveIdentifierAsync(ident);
                Assert.Equal(ident, resolved);
            }
        }));

        await Task.WhenAll(tasks);  // no InvalidOperationException / corruption

        // Every mapping landed in the cache with the correct value (no torn writes).
        foreach (var ident in identifiers)
        {
            Assert.True(mapper._principalCache.TryGetValue(ident, out var cached));
            Assert.Equal(ident, cached);
        }
    }

    [Fact]
    public async Task ParallelPrewarmAndToAclEntriesAreSafeAndCorrect()
    {
        var users = Enumerable.Range(0, 300)
            .Select(i => new JsonObject
            {
                ["Id"] = $"005{i:D6}",
                ["FederationIdentifier"] = $"user{i}@nokia.com",
                ["UserName"] = (string?)null,
                ["Email"] = (string?)null,
            })
            .ToList();
        var sf = new ConcurrencyFakeSfClient
        {
            QueryAllHandler = _ => users.Select(u => (JsonObject)u.DeepClone()).ToList(),
        };
        var mapper = new PrincipalMapper(sf);
        var allIds = users.Select(u => (string)u["Id"]!).ToHashSet();

        // Mirror Graph/Ingest.cs: concurrent prewarm + ACL mapping on the SAME
        // mapper instance (writes to _userDetailsCache race with hot-path reads).
        var results = new ConcurrentBag<List<Dictionary<string, string>>>();
        var tasks = Enumerable.Range(0, Workers).Select(w => Task.Run(async () =>
        {
            if (w % 2 == 0)
            {
                await mapper.PrewarmUsersAsync(new HashSet<string>(allIds));
            }
            var acl = await mapper.ToAclEntriesAsync(new AclResult(
                objectType: "Account",
                recordId: $"001-{w}",
                userIds: new HashSet<string>(allIds)));
            results.Add(acl);
        }));

        await Task.WhenAll(tasks);

        // Every worker produced the full, correctly mapped grant list.
        var expected = users.Select(u => (string)u["FederationIdentifier"]!).ToHashSet();
        foreach (var acl in results)
        {
            Assert.Equal(expected.Count, acl.Count);
            Assert.All(acl, entry =>
            {
                Assert.Equal("grant", entry["accessType"]);
                Assert.Equal("user", entry["type"]);
                Assert.Contains(entry["value"], expected);
            });
        }
    }
}

// ---------------------------------------------------------------------------
// MED-1 — ShareFetcher slow-path caches under concurrency
// ---------------------------------------------------------------------------

public class ShareFetcherConcurrencyTests
{
    private const int Workers = 16;

    [Fact]
    public async Task ParallelUserOwnerLookupsAreSafeAndCorrect()
    {
        // objectType == "User" takes the slow path's cache-write branch
        // (_ownerCache[recordId] = recordId) without any SOQL.
        var fetcher = new ShareFetcher(new ConcurrencyFakeSfClient());
        var recordIds = Enumerable.Range(0, 2000).Select(i => $"005{i:D6}").ToList();

        var tasks = Enumerable.Range(0, Workers).Select(_ => Task.Run(async () =>
        {
            foreach (var rid in recordIds)
            {
                var owner = await fetcher.GetOwnerIdAsync("User", rid);
                Assert.Equal(rid, owner);
            }
        }));

        await Task.WhenAll(tasks);  // no exception, no corrupted cache

        foreach (var rid in recordIds)
        {
            Assert.True(fetcher.OwnerCache.TryGetValue(rid, out var cached));
            Assert.Equal(rid, cached);
        }
    }

    [Fact]
    public async Task ParallelSlowPathShareAndOwnerLookupsAreSafe()
    {
        // Exercises all four slow-path caches concurrently:
        //   _parentFieldCache/_accessLevelFieldCache via GetShareEntriesAsync
        //   (describe-based discovery), _noOwnerIdTypes via INVALID_FIELD owner
        //   queries, _ownerCache via the User branch.
        var sf = new ConcurrencyFakeSfClient
        {
            DescribeHandler = _ => new JsonObject
            {
                ["fields"] = new JsonArray(
                    new JsonObject
                    {
                        ["name"] = "ParentId",
                        ["type"] = "reference",
                        ["referenceTo"] = new JsonArray("SomethingElse"),
                    },
                    new JsonObject { ["name"] = "AccessLevel", ["type"] = "picklist" }),
            },
            QueryAllHandler = soql =>
            {
                if (soql.StartsWith("SELECT OwnerId FROM NoOwner"))
                    throw new InvalidOperationException("INVALID_FIELD: No such column 'OwnerId'");
                if (soql.Contains("__Share"))
                {
                    return new List<JsonObject>
                    {
                        new()
                        {
                            ["UserOrGroupId"] = "005SHAREDUSER1",
                            ["RowCause"] = "Manual",
                            ["AccessLevel"] = "Read",
                        },
                    };
                }
                return new List<JsonObject>();
            },
        };
        var fetcher = new ShareFetcher(sf);
        var objectTypes = Enumerable.Range(0, 200).Select(i => $"Obj{i}__c").ToList();
        var noOwnerTypes = Enumerable.Range(0, 200).Select(i => $"NoOwner{i}").ToList();

        var tasks = Enumerable.Range(0, Workers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < objectTypes.Count; i++)
            {
                var entries = await fetcher.GetShareEntriesAsync(objectTypes[i], $"a00-{w}-{i}");
                Assert.Single(entries);
                Assert.Equal("005SHAREDUSER1", entries[0].UserOrGroupId);
                Assert.Equal("Read", entries[0].AccessLevel);

                var owner = await fetcher.GetOwnerIdAsync(noOwnerTypes[i], $"a01-{w}-{i}");
                Assert.Null(owner);
            }
        }));

        await Task.WhenAll(tasks);  // no exception from concurrent cache writes
    }
}

// ---------------------------------------------------------------------------
// MED-2 — QueueHandler prewarm cache must feed ResolveStaticGroupAsync
// ---------------------------------------------------------------------------

public class QueueHandlerPrewarmTests
{
    private static List<JsonObject> MemberRows(params (string GroupId, string MemberId)[] rows)
        => rows.Select(r => new JsonObject
        {
            ["GroupId"] = r.GroupId,
            ["UserOrGroupId"] = r.MemberId,
        }).ToList();

    [Fact]
    public async Task PrewarmedStaticGroupResolutionIssuesNoQuery()
    {
        var sf = new ConcurrencyFakeSfClient
        {
            QueryAllHandler = soql =>
            {
                Assert.StartsWith("SELECT GroupId, UserOrGroupId FROM GroupMember", soql);
                return MemberRows(
                    ("00Groot", "005USERA"),
                    ("00Groot", "00Gnested"),
                    ("00Gnested", "005USERB"),
                    ("00Gnested", "00Gdeep"),
                    ("00Gdeep", "005USERC"));
            },
        };
        var handler = new QueueHandler(sf);

        await handler.PrewarmAsync();
        Assert.Equal(1, sf.QueryAllCallCount);  // exactly the one bulk prewarm SOQL

        var users = await handler.ResolveStaticGroupAsync("00Groot");

        Assert.Equal(new HashSet<string> { "005USERA", "005USERB", "005USERC" }, users);
        // Every nested-group node was served from the prewarm cache: zero
        // additional SOQL (previously each node fired a per-group query).
        Assert.Equal(1, sf.QueryAllCallCount);
    }

    [Fact]
    public async Task PrewarmedLookupOfUnknownGroupIsEmptyWithoutQuery()
    {
        var sf = new ConcurrencyFakeSfClient
        {
            QueryAllHandler = _ => MemberRows(("00Groot", "005USERA")),
        };
        var handler = new QueueHandler(sf);
        await handler.PrewarmAsync();

        var users = await handler.ResolveStaticGroupAsync("00Gunknown");

        Assert.Empty(users);
        Assert.Equal(1, sf.QueryAllCallCount);
    }

    [Fact]
    public async Task FailedPrewarmFallsBackToPerGroupSoql()
    {
        var sf = new ConcurrencyFakeSfClient();
        sf.QueryAllHandler = soql =>
        {
            if (soql.StartsWith("SELECT GroupId, UserOrGroupId FROM GroupMember"))
                throw new InvalidOperationException("boom");
            if (soql.Contains("WHERE GroupId = '00Groot'"))
                return new List<JsonObject> { new() { ["UserOrGroupId"] = "005USERA" } };
            return new List<JsonObject>();
        };
        var handler = new QueueHandler(sf);

        await handler.PrewarmAsync();  // fails, cache stays unset
        var users = await handler.ResolveStaticGroupAsync("00Groot");

        Assert.Equal(new HashSet<string> { "005USERA" }, users);
        // 1 failed prewarm + 1 per-group fallback query
        Assert.Equal(2, sf.QueryAllCallCount);
    }

    // LOW-2: _groupMembers is written under _prewarmLock but read lock-free on the
    // hot path, so the reference field must be volatile (mirrors
    // AdaptiveConcurrency._current) to guarantee readers see the populated map.
    [Fact]
    public void GroupMembersFieldIsVolatile()
    {
        var field = typeof(QueueHandler)
            .GetField("_groupMembers", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        Assert.Contains(typeof(IsVolatile), field!.GetRequiredCustomModifiers());
    }

    // LOW-2: the volatile marking is a memory-visibility hardening only — behaviour
    // is unchanged. Concurrent prewarm + resolution still produces correct results.
    [Fact]
    public async Task ConcurrentPrewarmAndResolveIsCorrect()
    {
        var sf = new ConcurrencyFakeSfClient
        {
            QueryAllHandler = soql =>
            {
                Assert.StartsWith("SELECT GroupId, UserOrGroupId FROM GroupMember", soql);
                return MemberRows(
                    ("00Groot", "005USERA"),
                    ("00Groot", "00Gnested"),
                    ("00Gnested", "005USERB"));
            },
        };
        var handler = new QueueHandler(sf);

        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            await handler.PrewarmAsync();
            return await handler.ResolveStaticGroupAsync("00Groot");
        }));

        var results = await Task.WhenAll(tasks);
        Assert.All(results, r =>
            Assert.Equal(new HashSet<string> { "005USERA", "005USERB" }, r));
    }
}

// ---------------------------------------------------------------------------
// LOW-3 — PrincipalMapper.ResolveIdentifierAsync collapses concurrent misses
// ---------------------------------------------------------------------------

/// <summary>
/// GraphClient fake whose GetAsync blocks on a gate until released, so all
/// concurrent lookups for the same identifier pile up simultaneously.  Counts
/// the total number of Graph calls to prove in-flight dedup.
/// </summary>
file sealed class GatedGraphClient : GraphClient
{
    public const string Guid = "12345678-1234-1234-1234-123456789abc";
    private int _getCallCount;
    public int GetCallCount => Volatile.Read(ref _getCallCount);
    private readonly TaskCompletionSource _gate =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _gate.TrySetResult();

    public override async Task<JsonNode?> GetAsync(
        string pathOrUrl, Dictionary<string, string>? headers = null)
    {
        Interlocked.Increment(ref _getCallCount);
        await _gate.Task;
        // Direct-path lookup returns a GUID → no follow-up filter call.
        return new JsonObject { ["id"] = Guid };
    }
}

public class PrincipalMapperDedupTests
{
    [Fact]
    public async Task ConcurrentMissesForSameIdentifierCollapseToOneLookup()
    {
        var graph = new GatedGraphClient();
        var mapper = new PrincipalMapper(new ConcurrencyFakeSfClient(), graphClient: graph);
        const string identifier = "user@nokia.com";

        // Fire many concurrent resolutions of the SAME identifier; they all miss
        // the cache and must share a single in-flight Graph lookup.
        var tasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => mapper.ResolveIdentifierAsync(identifier)))
            .ToList();

        // Let every caller reach (and block on) the gated Graph call.
        while (graph.GetCallCount == 0)
            await Task.Delay(5);
        await Task.Delay(50);

        graph.Release();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(GatedGraphClient.Guid, r));
        // Without dedup this would be up to 32; with dedup it is exactly 1.
        Assert.Equal(1, graph.GetCallCount);
        Assert.Equal(GatedGraphClient.Guid, mapper._principalCache[identifier]);
    }

    [Fact]
    public async Task DistinctIdentifiersEachResolveIndependently()
    {
        var graph = new GatedGraphClient();
        graph.Release();  // no blocking needed here
        var mapper = new PrincipalMapper(new ConcurrencyFakeSfClient(), graphClient: graph);

        var identifiers = Enumerable.Range(0, 10).Select(i => $"user{i}@nokia.com").ToList();
        foreach (var id in identifiers)
            Assert.Equal(GatedGraphClient.Guid, await mapper.ResolveIdentifierAsync(id));

        // One lookup per distinct identifier; a repeat is served from the cache.
        Assert.Equal(10, graph.GetCallCount);
        Assert.Equal(GatedGraphClient.Guid, await mapper.ResolveIdentifierAsync(identifiers[0]));
        Assert.Equal(10, graph.GetCallCount);
    }
}

// ---------------------------------------------------------------------------
// LOW-1 — AclResolver.WarnedNoParent warn-dedup path is thread-safe
// ---------------------------------------------------------------------------

public class ResolverWarnDedupConcurrencyTests
{
    [Fact]
    public async Task ParallelMissingParentLookupsDoNotThrow()
    {
        // Empty parentMap → every object type takes the WarnedNoParent path.
        var resolver = new AclResolver(
            new ConcurrencyFakeSfClient(),
            owdFieldMap: new Dictionary<string, string>(),
            parentMap: new Dictionary<string, (string, string)>());

        // Unique names so the static warn set always sees fresh Adds.
        var suffix = Guid.NewGuid().ToString("N");
        var objectTypes = Enumerable.Range(0, 500)
            .Select(i => $"Custom{i}_{suffix}__c")
            .ToList();

        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            foreach (var objectType in objectTypes)
            {
                var (parentField, parentType) = await resolver.GetControllingParentInfoAsync(objectType);
                Assert.Null(parentField);
                Assert.Null(parentType);
            }
        }));

        await Task.WhenAll(tasks);  // unsynchronized HashSet.Add used to throw here
    }
}
