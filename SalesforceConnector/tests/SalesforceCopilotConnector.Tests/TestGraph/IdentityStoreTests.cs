// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for graph.identity_store — SQLite state store for identity crawl.
// Port of tests/test_graph/test_identity_store.py.

using SalesforceCopilotConnector.Graph;

namespace SalesforceCopilotConnector.Tests.TestGraph;

/// <summary>Port of the ``store`` fixture — a fresh IdentityStore backed by a temp SQLite DB.</summary>
public abstract class IdentityStoreTestBase : IDisposable
{
    protected readonly string TmpPath;
    protected readonly IdentityStore Store;

    protected IdentityStoreTestBase()
    {
        TmpPath = Path.Combine(Path.GetTempPath(), "sf-identity-store-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TmpPath);
        Store = new IdentityStore(
            dbPath: Path.Combine(TmpPath, "test_identity.db"),
            connectionId: "test-conn");
    }

    public virtual void Dispose()
    {
        Store.Close();
        try
        {
            Directory.Delete(TmpPath, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
        GC.SuppressFinalize(this);
    }
}

// ── MemberEntry tests ────────────────────────────────────────────────────────

public class TestMemberEntry
{
    [Fact]
    public void EqualityByIdAndType()
    {
        var a = new MemberEntry("005A", "user", "external");
        var b = new MemberEntry("005A", "user", "azureActiveDirectory");
        Assert.Equal(a, b);  // identity_source is NOT part of equality
    }

    [Fact]
    public void InequalityDifferentId()
    {
        var a = new MemberEntry("005A", "user");
        var b = new MemberEntry("005B", "user");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void InequalityDifferentType()
    {
        var a = new MemberEntry("005A", "user");
        var b = new MemberEntry("005A", "externalGroup");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void HashableInSet()
    {
        var s = new HashSet<MemberEntry>
        {
            new("005A", "user"),
            new("005A", "user"),
            new("005B", "user"),
        };
        Assert.Equal(2, s.Count);
    }

    [Fact]
    public void Frozen()
    {
        // Python: assigning to a frozen dataclass raises AttributeError.
        // C#: MemberEntry properties are get-only — verify they cannot be written.
        var m = new MemberEntry("005A", "user");
        var property = m.GetType().GetProperty(nameof(MemberEntry.MemberId))!;
        Assert.False(property.CanWrite);
    }
}

// ── Group CRUD tests ─────────────────────────────────────────────────────────

public class TestGroupCrud : IdentityStoreTestBase
{
    [Fact]
    public void GroupExistsFalseInitially()
    {
        Assert.False(Store.GroupExists("G1"));
    }

    [Fact]
    public void UpsertAndExists()
    {
        Store.UpsertGroup("G1", "Group One");
        Assert.True(Store.GroupExists("G1"));
    }

    [Fact]
    public void UpsertUpdatesDisplayName()
    {
        Store.UpsertGroup("G1", "Old Name");
        Store.UpsertGroup("G1", "New Name");
        // Still only one group
        Assert.Equal(new HashSet<string> { "G1" }, Store.GetAllGroupIds());
    }

    [Fact]
    public void GetAllGroupIds()
    {
        Store.UpsertGroup("G1");
        Store.UpsertGroup("G2");
        Store.UpsertGroup("G3");
        Assert.Equal(new HashSet<string> { "G1", "G2", "G3" }, Store.GetAllGroupIds());
    }

    [Fact]
    public void DeleteGroup()
    {
        Store.UpsertGroup("G1");
        Store.DeleteGroup("G1");
        Assert.False(Store.GroupExists("G1"));
    }

    [Fact]
    public void DeleteGroupCascadesMembers()
    {
        Store.UpsertGroup("G1");
        Store.AddMember("G1", new MemberEntry("005A", "user"));
        Store.AddMember("G1", new MemberEntry("005B", "user"));
        Store.DeleteGroup("G1");
        Assert.Equal(new HashSet<MemberEntry>(), Store.GetMembers("G1"));
    }

    [Fact]
    public void IsolationByConnectionId()
    {
        var db = Path.Combine(TmpPath, "shared.db");
        var storeA = new IdentityStore(dbPath: db, connectionId: "conn-a");
        var storeB = new IdentityStore(dbPath: db, connectionId: "conn-b");

        storeA.UpsertGroup("G1", "A's group");
        storeB.UpsertGroup("G2", "B's group");

        Assert.Equal(new HashSet<string> { "G1" }, storeA.GetAllGroupIds());
        Assert.Equal(new HashSet<string> { "G2" }, storeB.GetAllGroupIds());

        storeA.Close();
        storeB.Close();
    }
}

// ── Member CRUD tests ────────────────────────────────────────────────────────

public class TestMemberCrud : IdentityStoreTestBase
{
    [Fact]
    public void AddAndGetMembers()
    {
        Store.UpsertGroup("G1");
        var m1 = new MemberEntry("005A", "user");
        var m2 = new MemberEntry("GRP-Child", "externalGroup");
        Store.AddMember("G1", m1);
        Store.AddMember("G1", m2);
        var members = Store.GetMembers("G1");
        Assert.Equal(2, members.Count);
        Assert.Contains(m1, members);
        Assert.Contains(m2, members);
    }

    [Fact]
    public void AddDuplicateMemberIsIgnored()
    {
        Store.UpsertGroup("G1");
        var m = new MemberEntry("005A", "user");
        Store.AddMember("G1", m);
        Store.AddMember("G1", m);  // Should not raise
        Assert.Single(Store.GetMembers("G1"));
    }

    [Fact]
    public void RemoveMember()
    {
        Store.UpsertGroup("G1");
        var m = new MemberEntry("005A", "user");
        Store.AddMember("G1", m);
        Store.RemoveMember("G1", m);
        Assert.Empty(Store.GetMembers("G1"));
    }

    [Fact]
    public void RemoveNonexistentMemberIsSafe()
    {
        Store.UpsertGroup("G1");
        Store.RemoveMember("G1", new MemberEntry("005X", "user"));  // No error
    }

    [Fact]
    public void ReplaceMembers()
    {
        Store.UpsertGroup("G1");
        Store.AddMember("G1", new MemberEntry("005OLD", "user"));
        Store.AddMember("G1", new MemberEntry("005KEEP", "user"));

        var newMembers = new HashSet<MemberEntry>
        {
            new("005KEEP", "user"),
            new("005NEW", "user"),
        };
        Store.ReplaceMembers("G1", newMembers);

        var members = Store.GetMembers("G1");
        Assert.Equal(2, members.Count);
        var memberIds = members.Select(m => m.MemberId).ToHashSet();
        Assert.Contains("005KEEP", memberIds);
        Assert.Contains("005NEW", memberIds);
        Assert.DoesNotContain("005OLD", memberIds);
    }

    [Fact]
    public void EmptyGroupHasNoMembers()
    {
        Store.UpsertGroup("G1");
        Assert.Equal(new HashSet<MemberEntry>(), Store.GetMembers("G1"));
    }
}

// ── Diff computation tests ───────────────────────────────────────────────────

public class TestComputeDiff : IdentityStoreTestBase
{
    [Fact]
    public void AllNewGroups()
    {
        var newGroups = new Dictionary<string, (string DisplayName, HashSet<MemberEntry> Members)>
        {
            ["G1"] = ("Group 1", new HashSet<MemberEntry> { new("005A", "user") }),
            ["G2"] = ("Group 2", new HashSet<MemberEntry> { new("005B", "user") }),
        };
        var diffs = Store.ComputeDiff(newGroups);
        var actions = diffs.ToDictionary(d => d.GroupId, d => d.Action);
        Assert.Equal(
            new Dictionary<string, string> { ["G1"] = "create", ["G2"] = "create" },
            actions);
    }

    [Fact]
    public void AllStaleGroups()
    {
        Store.UpsertGroup("STALE1");
        Store.UpsertGroup("STALE2");
        var diffs = Store.ComputeDiff(new Dictionary<string, (string, HashSet<MemberEntry>)>());
        var actions = diffs.ToDictionary(d => d.GroupId, d => d.Action);
        Assert.Equal(
            new Dictionary<string, string> { ["STALE1"] = "delete", ["STALE2"] = "delete" },
            actions);
    }

    [Fact]
    public void UnchangedGroup()
    {
        Store.UpsertGroup("G1");
        Store.AddMember("G1", new MemberEntry("005A", "user"));

        var newGroups = new Dictionary<string, (string, HashSet<MemberEntry>)>
        {
            ["G1"] = ("G1", new HashSet<MemberEntry> { new("005A", "user") }),
        };
        var diffs = Store.ComputeDiff(newGroups);
        Assert.Single(diffs);
        Assert.Equal("unchanged", diffs[0].Action);
        Assert.False(diffs[0].HasChanges);
    }

    [Fact]
    public void MembersAdded()
    {
        Store.UpsertGroup("G1");
        Store.AddMember("G1", new MemberEntry("005A", "user"));

        var newGroups = new Dictionary<string, (string, HashSet<MemberEntry>)>
        {
            ["G1"] = ("G1", new HashSet<MemberEntry>
            {
                new("005A", "user"),
                new("005B", "user"),
            }),
        };
        var diffs = Store.ComputeDiff(newGroups);
        Assert.Single(diffs);
        Assert.Equal("update", diffs[0].Action);
        Assert.Single(diffs[0].MembersToAdd);
        Assert.Equal("005B", diffs[0].MembersToAdd[0].MemberId);
        Assert.Empty(diffs[0].MembersToRemove);
    }

    [Fact]
    public void MembersRemoved()
    {
        Store.UpsertGroup("G1");
        Store.AddMember("G1", new MemberEntry("005A", "user"));
        Store.AddMember("G1", new MemberEntry("005B", "user"));

        var newGroups = new Dictionary<string, (string, HashSet<MemberEntry>)>
        {
            ["G1"] = ("G1", new HashSet<MemberEntry> { new("005A", "user") }),
        };
        var diffs = Store.ComputeDiff(newGroups);
        Assert.Single(diffs);
        Assert.Equal("update", diffs[0].Action);
        Assert.Single(diffs[0].MembersToRemove);
        Assert.Equal("005B", diffs[0].MembersToRemove[0].MemberId);
    }

    [Fact]
    public void MixedAddAndRemove()
    {
        Store.UpsertGroup("G1");
        Store.AddMember("G1", new MemberEntry("005A", "user"));
        Store.AddMember("G1", new MemberEntry("005B", "user"));

        var newGroups = new Dictionary<string, (string, HashSet<MemberEntry>)>
        {
            ["G1"] = ("G1", new HashSet<MemberEntry>
            {
                new("005A", "user"),
                new("005C", "user"),
            }),
        };
        var diffs = Store.ComputeDiff(newGroups);
        var d = diffs[0];
        Assert.Equal("update", d.Action);
        var addedIds = d.MembersToAdd.Select(m => m.MemberId).ToHashSet();
        var removedIds = d.MembersToRemove.Select(m => m.MemberId).ToHashSet();
        Assert.Equal(new HashSet<string> { "005C" }, addedIds);
        Assert.Equal(new HashSet<string> { "005B" }, removedIds);
    }

    [Fact]
    public void MixedCreateUpdateDeleteUnchanged()
    {
        Store.UpsertGroup("KEEP_SAME");
        Store.AddMember("KEEP_SAME", new MemberEntry("005A", "user"));

        Store.UpsertGroup("WILL_UPDATE");
        Store.AddMember("WILL_UPDATE", new MemberEntry("005X", "user"));

        Store.UpsertGroup("WILL_DELETE");

        var newGroups = new Dictionary<string, (string, HashSet<MemberEntry>)>
        {
            ["KEEP_SAME"] = ("Same", new HashSet<MemberEntry> { new("005A", "user") }),
            ["WILL_UPDATE"] = ("Updated", new HashSet<MemberEntry> { new("005Y", "user") }),
            ["BRAND_NEW"] = ("New", new HashSet<MemberEntry> { new("005Z", "user") }),
        };
        var diffs = Store.ComputeDiff(newGroups);
        var actions = diffs.ToDictionary(d => d.GroupId, d => d.Action);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["KEEP_SAME"] = "unchanged",
                ["WILL_UPDATE"] = "update",
                ["BRAND_NEW"] = "create",
                ["WILL_DELETE"] = "delete",
            },
            actions);
    }

    [Fact]
    public void ApiCallsEstimate()
    {
        var newGroups = new Dictionary<string, (string, HashSet<MemberEntry>)>
        {
            ["G1"] = ("G1", new HashSet<MemberEntry>
            {
                new("005A", "user"),
                new("005B", "user"),
            }),
        };
        var diffs = Store.ComputeDiff(newGroups);
        var d = diffs[0];
        Assert.Equal("create", d.Action);
        // 1 PUT group + 2 POST members = 3
        Assert.Equal(3, d.ApiCallsNeeded);
    }

    [Fact]
    public void UnchangedNeedsZeroCalls()
    {
        Store.UpsertGroup("G1");
        Store.AddMember("G1", new MemberEntry("005A", "user"));

        var diffs = Store.ComputeDiff(new Dictionary<string, (string, HashSet<MemberEntry>)>
        {
            ["G1"] = ("G1", new HashSet<MemberEntry> { new("005A", "user") }),
        });
        Assert.Equal(0, diffs[0].ApiCallsNeeded);
    }
}

// ── Sync session tests ───────────────────────────────────────────────────────

public class TestSyncSession : IdentityStoreTestBase
{
    [Fact]
    public void StartAndCompleteSession()
    {
        var sid = Store.StartSession();
        Assert.False(string.IsNullOrEmpty(sid));
        var stats = new SyncSessionStats
        {
            GroupsCreated = 2,
            GroupsUpdated = 1,
            GroupsDeleted = 0,
            GroupsUnchanged = 5,
            MembersAdded = 10,
            MembersRemoved = 3,
            ApiCallsMade = 16,
            Errors = 0,
        };
        Store.CompleteSession(sid, stats);

        var last = Store.GetLastSession();
        Assert.NotNull(last);
        Assert.Equal(sid, last!["session_id"]);
        Assert.Equal("completed", last["status"]);
        Assert.Equal("identity", last["crawl_type"]);
        Assert.Equal(2, last["groups_created"]);
        Assert.Equal(10, last["members_added"]);
        Assert.Equal(16, last["api_calls_made"]);
    }

    [Fact]
    public void NoCompletedSessionReturnsNone()
    {
        Assert.Null(Store.GetLastSession());
    }

    [Fact]
    public void FailedSessionNotReturnedAsLast()
    {
        var sid = Store.StartSession();
        Store.CompleteSession(sid, new SyncSessionStats(), status: "failed");
        Assert.Null(Store.GetLastSession());
    }

    [Fact]
    public void CrawlTypeStored()
    {
        var sid = Store.StartSession(crawlType: "content");
        Store.CompleteSession(sid, new SyncSessionStats());
        var last = Store.GetLastSession();
        Assert.Equal("content", last!["crawl_type"]);
    }

    [Fact]
    public void FilterByCrawlType()
    {
        var sid1 = Store.StartSession(crawlType: "identity");
        Store.CompleteSession(sid1, new SyncSessionStats { GroupsCreated = 5 });

        var sid2 = Store.StartSession(crawlType: "content");
        Store.CompleteSession(sid2, new SyncSessionStats { ContentTotalFetched = 100, ContentSuccess = 95 });

        var identityLast = Store.GetLastSession(crawlType: "identity");
        Assert.NotNull(identityLast);
        Assert.Equal("identity", identityLast!["crawl_type"]);
        Assert.Equal(5, identityLast["groups_created"]);

        var contentLast = Store.GetLastSession(crawlType: "content");
        Assert.NotNull(contentLast);
        Assert.Equal("content", contentLast!["crawl_type"]);
        Assert.Equal(100, contentLast["content_total_fetched"]);
        Assert.Equal(95, contentLast["content_success"]);
    }

    [Fact]
    public void ContentCrawlStats()
    {
        var sid = Store.StartSession(crawlType: "content");
        var stats = new SyncSessionStats
        {
            ContentTotalFetched = 50,
            ContentSuccess = 48,
            ContentFailed = 2,
            ContentDeleted = 3,
            ContentAclEngine = "GROUP",
            Errors = 2,
        };
        Store.CompleteSession(sid, stats);

        var last = Store.GetLastSession(crawlType: "content");
        Assert.Equal(50, last!["content_total_fetched"]);
        Assert.Equal(48, last["content_success"]);
        Assert.Equal(2, last["content_failed"]);
        Assert.Equal(3, last["content_deleted"]);
        Assert.Equal("GROUP", last["content_acl_engine"]);
    }

    [Fact]
    public void DryRunSavedSession()
    {
        var sid = Store.StartSession(crawlType: "identity-dry-run");
        var stats = new SyncSessionStats { GroupsCreated = 10, MembersAdded = 25 };
        Store.CompleteSession(sid, stats);

        var last = Store.GetLastSession(crawlType: "identity-dry-run");
        Assert.NotNull(last);
        Assert.Equal("identity-dry-run", last!["crawl_type"]);
        Assert.Equal(10, last["groups_created"]);
    }

    [Fact]
    public void SyncTypeStored()
    {
        var sid = Store.StartSession(crawlType: "content", syncType: "incremental");
        Store.CompleteSession(sid, new SyncSessionStats { SyncType = "incremental", ContentTotalFetched = 10 });
        var last = Store.GetLastSession(crawlType: "content");
        Assert.Equal("incremental", last!["sync_type"]);
        Assert.Equal(10, last["content_total_fetched"]);
    }

    [Fact]
    public void SyncTypeDefaultsToFull()
    {
        var sid = Store.StartSession(crawlType: "content");
        Store.CompleteSession(sid, new SyncSessionStats());
        var last = Store.GetLastSession(crawlType: "content");
        Assert.Equal("full", last!["sync_type"]);
    }

    [Fact]
    public void GetLastSuccessfulContentCrawlTime()
    {
        // No sessions yet
        Assert.Null(Store.GetLastSuccessfulContentCrawlTime());

        // Add a completed content session
        var sid = Store.StartSession(crawlType: "content", syncType: "full");
        Store.CompleteSession(sid, new SyncSessionStats { ContentTotalFetched = 50 });

        var result = Store.GetLastSuccessfulContentCrawlTime();
        Assert.NotNull(result);
        // Should be a datetime object
        Assert.IsType<DateTime>(result!.Value);
    }

    [Fact]
    public void GetLastSuccessfulContentCrawlTimeIgnoresIdentity()
    {
        // Only identity session exists
        var sid = Store.StartSession(crawlType: "identity");
        Store.CompleteSession(sid, new SyncSessionStats { GroupsCreated = 5 });

        Assert.Null(Store.GetLastSuccessfulContentCrawlTime());
    }
}

// ── Stats test ────────────────────────────────────────────────────────────────

public class TestStats : IdentityStoreTestBase
{
    [Fact]
    public void EmptyStoreStats()
    {
        var stats = Store.GetStats();
        Assert.Equal(
            new Dictionary<string, int> { ["groups"] = 0, ["members"] = 0 },
            stats);
    }

    [Fact]
    public void StatsAfterPopulation()
    {
        Store.UpsertGroup("G1");
        Store.UpsertGroup("G2");
        Store.AddMember("G1", new MemberEntry("005A", "user"));
        Store.AddMember("G1", new MemberEntry("005B", "user"));
        Store.AddMember("G2", new MemberEntry("005C", "user"));

        var stats = Store.GetStats();
        Assert.Equal(
            new Dictionary<string, int> { ["groups"] = 2, ["members"] = 3 },
            stats);
    }
}

// ── Field cache tests ────────────────────────────────────────────────────────

public class TestFieldCache : IdentityStoreTestBase
{
    [Fact]
    public void NoCacheReturnsNone()
    {
        Assert.Null(Store.GetCachedFields("https://org.salesforce.com", "Account"));
    }

    [Fact]
    public void SaveAndLoad()
    {
        var url = "https://myorg.my.salesforce.com";
        Store.SaveCachedFields(url, "Account", new[] { "Id", "Name", "Industry" });
        var result = Store.GetCachedFields(url, "Account");
        Assert.Equal(new[] { "Id", "Name", "Industry" }, result);
    }

    [Fact]
    public void DifferentObjectsIsolated()
    {
        var url = "https://org.salesforce.com";
        Store.SaveCachedFields(url, "Account", new[] { "Id", "Name" });
        Store.SaveCachedFields(url, "Lead", new[] { "Id", "Email" });
        Assert.Equal(new[] { "Id", "Name" }, Store.GetCachedFields(url, "Account"));
        Assert.Equal(new[] { "Id", "Email" }, Store.GetCachedFields(url, "Lead"));
    }

    [Fact]
    public void DifferentOrgsIsolated()
    {
        Store.SaveCachedFields("https://org1.salesforce.com", "Account", new[] { "Id", "Name" });
        Store.SaveCachedFields("https://org2.salesforce.com", "Account", new[] { "Id", "Website" });
        Assert.Equal(new[] { "Id", "Name" }, Store.GetCachedFields("https://org1.salesforce.com", "Account"));
        Assert.Equal(new[] { "Id", "Website" }, Store.GetCachedFields("https://org2.salesforce.com", "Account"));
    }

    [Fact]
    public void UpsertOverwrites()
    {
        var url = "https://org.salesforce.com";
        Store.SaveCachedFields(url, "Account", new[] { "Id", "Name", "Phone" });
        Store.SaveCachedFields(url, "Account", new[] { "Id", "Name" });
        Assert.Equal(new[] { "Id", "Name" }, Store.GetCachedFields(url, "Account"));
    }

    [Fact]
    public void ClearSpecificObject()
    {
        var url = "https://org.salesforce.com";
        Store.SaveCachedFields(url, "Account", new[] { "Id" });
        Store.SaveCachedFields(url, "Lead", new[] { "Id" });
        var deleted = Store.ClearFieldCache(url, "Account");
        Assert.Equal(1, deleted);
        Assert.Null(Store.GetCachedFields(url, "Account"));
        Assert.Equal(new[] { "Id" }, Store.GetCachedFields(url, "Lead"));
    }

    [Fact]
    public void ClearAllForOrg()
    {
        var url = "https://org.salesforce.com";
        Store.SaveCachedFields(url, "Account", new[] { "Id" });
        Store.SaveCachedFields(url, "Lead", new[] { "Id" });
        var deleted = Store.ClearFieldCache(url);
        Assert.Equal(2, deleted);
        Assert.Null(Store.GetCachedFields(url, "Account"));
        Assert.Null(Store.GetCachedFields(url, "Lead"));
    }

    [Fact]
    public void ClearAll()
    {
        Store.SaveCachedFields("https://org1.salesforce.com", "Account", new[] { "Id" });
        Store.SaveCachedFields("https://org2.salesforce.com", "Lead", new[] { "Id" });
        var deleted = Store.ClearFieldCache();
        Assert.Equal(2, deleted);
    }
}
