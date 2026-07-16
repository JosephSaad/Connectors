// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for the incremental identity crawl path (#12):
//   * AclEngine.IdentitySyncHandler.RunIncrementalCrawlAsync — watermark-driven
//     gather of only the objects whose identity data changed.
//   * Graph.ObjectScopedIdentityStore — scoping the publish diff to the changed
//     objects so unchanged-object groups are left in place.
//   * Graph.Identity.TryGetIdentityWatermark — the no-prior-session → full
//     fallback decision.
//
// Mirrors the fake-driven style of TestAclEngine/IdentityCrawlTests.cs and
// TestGraph/IdentityPublisherTests.cs. The default (full) identity path is left
// untouched; those existing tests continue to exercise it.

using SalesforceCopilotConnector.AclEngine;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Tests.TestGraph;

namespace SalesforceCopilotConnector.Tests.TestAclEngine;

// ── Fake query client with per-object incremental change flags ───────────────

/// <summary>
/// Fake <see cref="IdentityQueryClient"/> that (a) serves canned gather data and
/// (b) reports, per object, whether it "changed" since the watermark.  Records
/// which objects were probed and which were actually gathered so tests can prove
/// unchanged objects are skipped.
/// </summary>
file sealed class FakeIncrementalQueryClient : IdentityQueryClient
{
    public Dictionary<string, EntityVisibility> OwdMap = new();
    // objectName → did it change since the watermark?
    public Dictionary<string, bool> ChangedByObject = new();
    // Global-access users returned for any private object's GlobalUsers child.
    public List<SfUser> GlobalUsers = new();

    // Observability
    public List<(string ObjectName, DateTime Since)> ChangeProbes = new();
    public HashSet<string> GlobalAccessGathered = new();

    public FakeIncrementalQueryClient()
        : base(
            new SalesforceClient("https://test.my.salesforce.com", "60.0", "mock-token"),
            owdFieldMap: new Dictionary<string, string>())
    {
    }

    public override Task<Dictionary<string, EntityVisibility>> GetOrgWideDefaultsAsync()
        => Task.FromResult(new Dictionary<string, EntityVisibility>(OwdMap));

    public override Task<bool> HasIdentityChangesSinceAsync(string objectName, DateTime since)
    {
        ChangeProbes.Add((objectName, since));
        return Task.FromResult(ChangedByObject.GetValueOrDefault(objectName, true));
    }

    // Private-OWD gather path: GlobalUsers child group + no share groups.
    public override Task<List<string>> GetGroupShareIdsAsync(string objectName)
        => Task.FromResult(new List<string>());

    public override Task<List<SfGroup>> GetGroupsByIdsAsync(List<string> groupIds)
        => Task.FromResult(new List<SfGroup>());

    public override Task<Dictionary<string, string>> GetRoleHierarchyAsync()
        => Task.FromResult(new Dictionary<string, string>());

    public override Task<HashSet<string>> GetRolesAssignedToUsersAsync()
        => Task.FromResult(new HashSet<string>());

    public override Task<List<SfUser>> GetGlobalAccessUsersAsync(string objectName)
    {
        GlobalAccessGathered.Add(objectName);
        return Task.FromResult(new List<SfUser>(GlobalUsers));
    }

    public override Task<List<SfUser>> GetAuthorizedUsersAsync(string objectName)
        => Task.FromResult(new List<SfUser>());

    public override bool HasShareTable(string objectName) => true;

    public override bool HasOwdField(string objectName) => true;
}

file static class IncTestHelpers
{
    public static SalesforceClient DummySfClient()
        => new("https://test.my.salesforce.com", "60.0", "mock-token");

    public static IdentitySyncHandler MakeHandler(FakeIncrementalQueryClient qc, List<string> objects)
    {
        var handler = new IdentitySyncHandler(sfClient: DummySfClient(), objectNames: objects);
        handler._queryClient = qc;
        return handler;
    }
}

// ── RunIncrementalCrawlAsync: only changed objects are gathered ──────────────

public class IncrementalCrawlGathersOnlyChangedTests
{
    [Fact]
    public async Task OnlyChangedObjectIsGathered()
    {
        // Account changed, Lead unchanged. Both PRIVATE so a change would create
        // an AccountGlobalUsers / LeadGlobalUsers child.
        var qc = new FakeIncrementalQueryClient
        {
            OwdMap = new Dictionary<string, EntityVisibility>
            {
                ["Account"] = EntityVisibility.None,
                ["Lead"] = EntityVisibility.None,
            },
            ChangedByObject = new Dictionary<string, bool> { ["Account"] = true, ["Lead"] = false },
        };
        var handler = IncTestHelpers.MakeHandler(qc, new List<string> { "Account", "Lead" });

        var result = await handler.RunIncrementalCrawlAsync(DateTime.UtcNow.AddHours(-1));

        // Both objects probed for change...
        Assert.Equal(2, qc.ChangeProbes.Count);
        // ...but only Account was gathered.
        var topObjects = result.TopLevelGroups.Select(t => t.ObjectName).ToHashSet();
        Assert.Contains("Account", topObjects);
        Assert.DoesNotContain("Lead", topObjects);

        var gatheredIds = result.GatheredGroups.Select(g => g.GroupId).ToHashSet();
        Assert.Contains("AccountTopLevel", gatheredIds);
        Assert.Contains("AccountGlobalUsers", gatheredIds);
        Assert.DoesNotContain("LeadTopLevel", gatheredIds);
        // The unchanged object's expensive gather query never ran.
        Assert.Contains("Account", qc.GlobalAccessGathered);
        Assert.DoesNotContain("Lead", qc.GlobalAccessGathered);
    }

    [Fact]
    public async Task NoChangesProducesEmptyCrawl()
    {
        var qc = new FakeIncrementalQueryClient
        {
            OwdMap = new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.None },
            ChangedByObject = new Dictionary<string, bool> { ["Account"] = false },
        };
        var handler = IncTestHelpers.MakeHandler(qc, new List<string> { "Account" });

        var result = await handler.RunIncrementalCrawlAsync(DateTime.UtcNow.AddHours(-1));

        Assert.Empty(result.TopLevelGroups);
        Assert.Empty(result.GatheredGroups);
        Assert.Equal(0, result.TotalGroupsEmitted);
        Assert.Empty(qc.GlobalAccessGathered);
    }

    [Fact]
    public async Task ChangedObjectMembershipMatchesFullCrawl()
    {
        // With every object flagged changed, the incremental crawl output must
        // equal a full crawl's output (this is the "fell back to full" shape).
        SfUser Admin() => new("005ADM") { Name = "Admin", Email = "admin@test.com" };
        var incQc = new FakeIncrementalQueryClient
        {
            OwdMap = new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.None },
            ChangedByObject = new Dictionary<string, bool> { ["Account"] = true },
            GlobalUsers = new List<SfUser> { Admin() },
        };
        var incHandler = IncTestHelpers.MakeHandler(incQc, new List<string> { "Account" });
        var inc = await incHandler.RunIncrementalCrawlAsync(DateTime.UtcNow.AddHours(-1));

        var fullQc = new FakeIncrementalQueryClient
        {
            OwdMap = new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.None },
            GlobalUsers = new List<SfUser> { Admin() },
        };
        var fullHandler = IncTestHelpers.MakeHandler(fullQc, new List<string> { "Account" });
        var full = await fullHandler.RunFullCrawlAsync();

        Assert.Equal(
            full.GatheredGroups.Select(g => g.GroupId).OrderBy(x => x),
            inc.GatheredGroups.Select(g => g.GroupId).OrderBy(x => x));
        Assert.Equal(full.TotalGroupsEmitted, inc.TotalGroupsEmitted);
        Assert.Equal(full.TotalUsersEmitted, inc.TotalUsersEmitted);
    }
}

// ── ObjectScopedIdentityStore: scoping + pass-through ────────────────────────

public class ObjectScopedIdentityStoreTests : IDisposable
{
    private readonly string _tmp;
    private readonly IdentityStore _inner;

    public ObjectScopedIdentityStoreTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "sf-inc-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
        _inner = new IdentityStore(Path.Combine(_tmp, "scope.db"), "test-conn");
    }

    public void Dispose()
    {
        _inner.Close();
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void GetAllGroupIdsReturnsOnlyChangedObjects()
    {
        _inner.UpsertGroup("AccountTopLevel");
        _inner.UpsertGroup("AccountGlobalUsers");
        _inner.UpsertGroup("LeadTopLevel");

        var scoped = new ObjectScopedIdentityStore(_inner, new[] { "Account" });
        var ids = scoped.GetAllGroupIds();

        Assert.Contains("AccountTopLevel", ids);
        Assert.Contains("AccountGlobalUsers", ids);
        Assert.DoesNotContain("LeadTopLevel", ids);
    }

    [Fact]
    public void ComputeDiffNeverDeletesUnchangedObjectGroups()
    {
        // Store has an Account group and a Lead group. We only crawl Account,
        // and the crawl no longer contains AccountGlobalUsers.
        _inner.UpsertGroup("AccountTopLevel");
        _inner.UpsertGroup("AccountGlobalUsers");
        _inner.UpsertGroup("LeadTopLevel");

        var scoped = new ObjectScopedIdentityStore(_inner, new[] { "Account" });
        var desired = new Dictionary<string, (string, HashSet<MemberEntry>)>
        {
            ["AccountTopLevel"] = ("Account", new HashSet<MemberEntry>()),
        };
        var diffs = scoped.ComputeDiff(desired);

        // AccountGlobalUsers deleted (in scope, gone from crawl); LeadTopLevel NOT.
        var deletes = diffs.Where(d => d.Action == "delete").Select(d => d.GroupId).ToHashSet();
        Assert.Contains("AccountGlobalUsers", deletes);
        Assert.DoesNotContain("LeadTopLevel", deletes);
        // AccountTopLevel present in both → unchanged.
        Assert.Contains(diffs, d => d.GroupId == "AccountTopLevel" && d.Action == "unchanged");
    }

    [Fact]
    public void LongestPrefixWinsSoSiblingObjectsDoNotCollide()
    {
        // "Account" and "AccountPlan" share a prefix. Scoping to "Account" only
        // must NOT pull in AccountPlan groups.
        _inner.UpsertGroup("AccountTopLevel");
        _inner.UpsertGroup("AccountPlanTopLevel");

        var scoped = new ObjectScopedIdentityStore(_inner, new[] { "AccountPlan" });
        var ids = scoped.GetAllGroupIds();

        Assert.Contains("AccountPlanTopLevel", ids);
        Assert.DoesNotContain("AccountTopLevel", ids);
    }

    [Fact]
    public void WritesPassThroughToInner()
    {
        var scoped = new ObjectScopedIdentityStore(_inner, new[] { "Account" });
        scoped.UpsertGroup("AccountTopLevel", "Account");
        scoped.ReplaceMembers("AccountTopLevel", new HashSet<MemberEntry>
        {
            new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "user", "azureActiveDirectory"),
        });

        Assert.True(_inner.GroupExists("AccountTopLevel"));
        Assert.Single(_inner.GetMembers("AccountTopLevel"));
    }
}

// ── End-to-end: incremental publish leaves unchanged groups in place ─────────

public class IncrementalPublishEndToEndTests : IDisposable
{
    private readonly string _tmp;
    private readonly IdentityStore _store;
    private readonly FakeGraphClient _client;

    public IncrementalPublishEndToEndTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "sf-inc-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
        _store = new IdentityStore(Path.Combine(_tmp, "e2e.db"), "test-conn");
        _client = new FakeGraphClient();
    }

    public void Dispose()
    {
        _store.Close();
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static string Guid1(string seed)
    {
        var h = Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(seed)))
            .ToLowerInvariant();
        return $"{h[..8]}-{h[8..12]}-{h[12..16]}-{h[16..20]}-{h[20..32]}";
    }

    [Fact]
    public async Task ChangedMembershipAddRemoveReflectedAndUnchangedGroupUntouched()
    {
        // Seed store: Account (changed obj) has members A,B; Lead (unchanged obj)
        // has member X. Both from a prior full crawl.
        var a = Guid1("A");
        var b = Guid1("B");
        var c = Guid1("C");
        var x = Guid1("X");
        _store.UpsertGroup("AccountTopLevel", "Account");
        _store.ReplaceMembers("AccountTopLevel", new HashSet<MemberEntry>
        {
            new(a, "user", "azureActiveDirectory"),
            new(b, "user", "azureActiveDirectory"),
        });
        _store.UpsertGroup("LeadTopLevel", "Lead");
        _store.ReplaceMembers("LeadTopLevel", new HashSet<MemberEntry>
        {
            new(x, "user", "azureActiveDirectory"),
        });

        // Incremental crawl re-gathered ONLY Account: now members A,C (B removed).
        var crawl = new IdentityCrawlResult
        {
            TopLevelGroups = new List<TopLevelGroupInfo>
            {
                new() { GroupId = "AccountTopLevel", ObjectName = "Account", DisplayName = "Account" },
            },
            GatheredGroups = new List<GroupMembership>
            {
                new()
                {
                    GroupId = "AccountTopLevel",
                    DisplayName = "Account",
                    // Federation ids are already-GUID → static flatten keeps them.
                    Users = new List<SfUser>
                    {
                        new("005A") { FederationIdentifier = a },
                        new("005C") { FederationIdentifier = c },
                    },
                },
            },
        };
        crawl.TotalGroupsEmitted = crawl.GatheredGroups.Count;
        crawl.TotalUsersEmitted = crawl.GatheredGroups.Sum(g => g.Users.Count);

        // Publish through the scoped store (no mapper → static GUID flatten).
        var scoped = new ObjectScopedIdentityStore(_store, new[] { "Account" });
        var publisher = new IdentityPublisher(
            graphClient: _client, connectionId: "test-conn", store: scoped);
        var stats = await publisher.PublishAsync(crawl);

        // Account: 1 add (C), 1 remove (B), group itself updated.
        Assert.Equal(1, stats.GroupsUpdated);
        Assert.Equal(1, stats.MembersAdded);
        Assert.Equal(1, stats.MembersRemoved);
        Assert.Equal(0, stats.GroupsDeleted);

        // Store: Account now {A,C}; Lead group and its member X untouched.
        var accIds = _store.GetMembers("AccountTopLevel").Select(m => m.MemberId).ToHashSet();
        Assert.Equal(new HashSet<string> { a, c }, accIds);
        Assert.True(_store.GroupExists("LeadTopLevel"));
        var leadIds = _store.GetMembers("LeadTopLevel").Select(m => m.MemberId).ToHashSet();
        Assert.Equal(new HashSet<string> { x }, leadIds);

        // Graph: exactly one add + one remove, no group PUT/DELETE.
        Assert.Single(_client.PostCalls);
        Assert.Single(_client.DeleteCalls);
    }

    [Fact]
    public async Task EmptyIncrementalCrawlIsNoOp()
    {
        // Nothing changed → crawl empty, scoped to no objects → nothing deleted.
        _store.UpsertGroup("AccountTopLevel", "Account");
        _store.ReplaceMembers("AccountTopLevel", new HashSet<MemberEntry>
        {
            new(Guid1("A"), "user", "azureActiveDirectory"),
        });

        var crawl = new IdentityCrawlResult();  // no groups
        var scoped = new ObjectScopedIdentityStore(_store, Array.Empty<string>());
        var publisher = new IdentityPublisher(
            graphClient: _client, connectionId: "test-conn", store: scoped);
        var stats = await publisher.PublishAsync(crawl);

        Assert.Equal(0, stats.GroupsCreated);
        Assert.Equal(0, stats.GroupsUpdated);
        Assert.Equal(0, stats.GroupsDeleted);
        Assert.Empty(_client.PostCalls);
        Assert.Empty(_client.DeleteCalls);
        Assert.Empty(_client.PutCalls);
        // Account group + member still present.
        Assert.True(_store.GroupExists("AccountTopLevel"));
        Assert.Single(_store.GetMembers("AccountTopLevel"));
    }
}

// ── Watermark / fallback decision (no prior session → full) ──────────────────

public class IncrementalWatermarkDecisionTests : IDisposable
{
    private readonly string _tmp;
    private readonly IdentityStore _store;

    public IncrementalWatermarkDecisionTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "sf-inc-wm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
        _store = new IdentityStore(Path.Combine(_tmp, "wm.db"), "test-conn");
    }

    public void Dispose()
    {
        _store.Close();
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void NoPriorIdentitySessionFallsBackToFull()
    {
        // Fresh store: no completed identity session → decision is "fall back".
        Assert.False(Identity.TryGetIdentityWatermark(_store, out _));
    }

    [Fact]
    public void ContentOnlySessionDoesNotSatisfyIdentityWatermark()
    {
        // A completed CONTENT crawl must not be mistaken for an identity session.
        var sid = _store.StartSession(crawlType: "content", syncType: "full");
        _store.CompleteSession(sid, new SyncSessionStats { SessionId = sid }, status: "completed");

        Assert.False(Identity.TryGetIdentityWatermark(_store, out _));
    }

    [Fact]
    public void CompletedIdentitySessionYieldsWatermark()
    {
        var before = DateTime.UtcNow.AddSeconds(-2);
        var sid = _store.StartSession(crawlType: "identity", syncType: "full");
        _store.CompleteSession(sid, new SyncSessionStats { SessionId = sid }, status: "completed");
        var after = DateTime.UtcNow.AddSeconds(2);

        Assert.True(Identity.TryGetIdentityWatermark(_store, out var since));
        // Watermark is the session start time, in a sane range.
        Assert.InRange(since, before, after);
    }
}
