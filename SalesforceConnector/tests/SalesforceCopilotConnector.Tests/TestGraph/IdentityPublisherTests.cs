// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for graph.identity_publisher — Graph API publishing with SQLite diff.
// Port of tests/test_graph/test_identity_publisher.py.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.AclEngine;
using SalesforceCopilotConnector.Graph;

namespace SalesforceCopilotConnector.Tests.TestGraph;

// ── Helpers ──────────────────────────────────────────────────────────────────

internal static class IdentityPublisherTestHelpers
{
    /// <summary>Return the deterministic GUID that MakeUser generates for <paramref name="userId"/>.</summary>
    public static string UserGuid(string userId)
    {
        var h = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(userId))).ToLowerInvariant();
        return $"{h[..8]}-{h[8..12]}-{h[12..16]}-{h[16..20]}-{h[20..32]}";
    }

    /// <summary>
    /// Create a test user with a deterministic GUID-format federation_identifier
    /// so the static FlattenCrawlResult (GUID-only filter) keeps the user.
    /// </summary>
    public static SfUser MakeUser(string userId = "005A", string federationId = "", string email = "")
    {
        var fakeGuid = UserGuid(userId);
        return new SfUser(userId)
        {
            Name = "User",
            Email = !string.IsNullOrEmpty(email) ? email : $"{userId.ToLowerInvariant()}@test.com",
            FederationIdentifier = !string.IsNullOrEmpty(federationId) ? federationId : fakeGuid,
        };
    }

    public static IdentityCrawlResult MakeCrawlResult(List<GroupMembership> groups)
    {
        return new IdentityCrawlResult
        {
            TopLevelGroups = new List<TopLevelGroupInfo>(),
            GatheredGroups = groups,
            TotalUsersEmitted = groups.Sum(g => g.Users.Count),
            TotalGroupsEmitted = groups.Count,
        };
    }
}

/// <summary>Port of the ``store`` / ``graph_client`` / ``publisher`` fixtures.</summary>
public abstract class IdentityPublisherTestBase : IDisposable
{
    protected readonly string TmpPath;
    protected readonly IdentityStore Store;
    protected readonly FakeGraphClient Client;
    protected readonly IdentityPublisher Publisher;

    protected IdentityPublisherTestBase()
    {
        TmpPath = Path.Combine(Path.GetTempPath(), "sf-identity-pub-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TmpPath);
        Store = new IdentityStore(
            dbPath: Path.Combine(TmpPath, "pub_test.db"),
            connectionId: "test-conn");
        Client = new FakeGraphClient();
        Publisher = new IdentityPublisher(
            graphClient: Client,
            connectionId: "test-conn",
            store: Store);
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

// ── _build_member_payload tests ──────────────────────────────────────────────

public class TestBuildMemberPayload
{
    [Fact]
    public void ExternalUser()
    {
        var m = new MemberEntry("005A", "user", "external");
        var payload = IdentityPublisher.BuildMemberPayload(m);
        Assert.True(JsonNode.DeepEquals(payload, new JsonObject
        {
            ["id"] = "005A",
            ["type"] = "user",
            ["identitySource"] = "external",
        }));
    }

    [Fact]
    public void AadUser()
    {
        var m = new MemberEntry("f1126041-cb51-4f20-82d5-722b4cfcdfa1", "user", "azureActiveDirectory");
        var payload = IdentityPublisher.BuildMemberPayload(m);
        Assert.True(JsonNode.DeepEquals(payload, new JsonObject
        {
            ["id"] = "f1126041-cb51-4f20-82d5-722b4cfcdfa1",
            ["type"] = "user",
            ["identitySource"] = "azureActiveDirectory",
        }));
    }

    [Fact]
    public void ExternalGroup()
    {
        var m = new MemberEntry("AccountTopLevel", "externalGroup", "external");
        var payload = IdentityPublisher.BuildMemberPayload(m);
        Assert.True(JsonNode.DeepEquals(payload, new JsonObject
        {
            ["id"] = "AccountTopLevel",
            ["type"] = "externalGroup",
            ["identitySource"] = "external",
        }));
    }
}

// ── _flatten_crawl_result tests ──────────────────────────────────────────────

public class TestFlattenCrawlResult
{
    [Fact]
    public void UsersBecomeUserMembers()
    {
        var crawl = IdentityPublisherTestHelpers.MakeCrawlResult(new List<GroupMembership>
        {
            new()
            {
                GroupId = "G1",
                DisplayName = "Group One",
                Users = new List<SfUser>
                {
                    IdentityPublisherTestHelpers.MakeUser("005A"),
                    IdentityPublisherTestHelpers.MakeUser("005B"),
                },
            },
        });
        var flat = IdentityPublisher.FlattenCrawlResult(crawl);
        Assert.Contains("G1", flat.Keys);
        var (name, members) = flat["G1"];
        Assert.Equal("Group One", name);
        Assert.Equal(2, members.Count);
        var ids = members.Select(m => m.MemberId).ToHashSet();
        Assert.Equal(
            new HashSet<string>
            {
                IdentityPublisherTestHelpers.UserGuid("005A"),
                IdentityPublisherTestHelpers.UserGuid("005B"),
            },
            ids);
        // All user members should be azureActiveDirectory
        Assert.All(members, m => Assert.Equal("azureActiveDirectory", m.IdentitySource));
    }

    [Fact]
    public void AadUsersUseFederationId()
    {
        var aadGuid = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
        var crawl = IdentityPublisherTestHelpers.MakeCrawlResult(new List<GroupMembership>
        {
            new()
            {
                GroupId = "G1",
                Users = new List<SfUser>
                {
                    IdentityPublisherTestHelpers.MakeUser("005A", federationId: aadGuid),
                },
            },
        });
        var flat = IdentityPublisher.FlattenCrawlResult(crawl);
        var members = flat["G1"].Members;
        var m = members.First();
        Assert.Equal(aadGuid, m.MemberId);
        Assert.Equal("azureActiveDirectory", m.IdentitySource);
    }

    [Fact]
    public void NonGuidFederationIdIsSkipped()
    {
        var crawl = IdentityPublisherTestHelpers.MakeCrawlResult(new List<GroupMembership>
        {
            new()
            {
                GroupId = "G1",
                Users = new List<SfUser>
                {
                    IdentityPublisherTestHelpers.MakeUser("005A", federationId: "alice@tenant.com"),
                },
            },
        });
        var flat = IdentityPublisher.FlattenCrawlResult(crawl);
        var userMembers = flat["G1"].Members.Where(m => m.MemberType == "user").ToList();
        Assert.Empty(userMembers);  // email filtered out
    }

    [Fact]
    public void ChildGroupsBecomeExternalGroupMembers()
    {
        var crawl = IdentityPublisherTestHelpers.MakeCrawlResult(new List<GroupMembership>
        {
            new()
            {
                GroupId = "G1",
                ChildGroups = new List<ChildGroupRef>
                {
                    new()
                    {
                        GroupId = "Child1",
                        GroupType = GroupIdentityType.RoleWithParent,
                        NeedsGather = false,
                    },
                },
            },
        });
        var flat = IdentityPublisher.FlattenCrawlResult(crawl);
        var members = flat["G1"].Members;
        var m = members.First();
        Assert.Equal("Child1", m.MemberId);
        Assert.Equal("externalGroup", m.MemberType);
    }
}

// ── Publish with fresh store (all creates) ───────────────────────────────────

public class TestPublishAllNew : IdentityPublisherTestBase
{
    [Fact]
    public async Task CreatesGroupsAndAddsMembers()
    {
        var crawl = IdentityPublisherTestHelpers.MakeCrawlResult(new List<GroupMembership>
        {
            new()
            {
                GroupId = "G1",
                DisplayName = "Group 1",
                Users = new List<SfUser>
                {
                    IdentityPublisherTestHelpers.MakeUser("005A"),
                    IdentityPublisherTestHelpers.MakeUser("005B"),
                },
            },
        });
        var stats = await Publisher.PublishAsync(crawl);

        Assert.Equal(1, stats.GroupsCreated);
        Assert.Equal(2, stats.MembersAdded);
        Assert.Equal(0, stats.GroupsDeleted);
        Assert.Equal(0, stats.Errors);

        // Verify Graph API calls: 1 POST for group creation + 2 POSTs for members = 3
        var postCalls = Client.PostCalls;
        Assert.Equal(3, postCalls.Count);
        // First call is group creation (URL ends with /groups)
        Assert.EndsWith("/groups", postCalls[0].Url);

        // Verify store was updated
        Assert.True(Store.GroupExists("G1"));
        Assert.Equal(2, Store.GetMembers("G1").Count);
    }

    [Fact]
    public async Task SessionRecorded()
    {
        var crawl = IdentityPublisherTestHelpers.MakeCrawlResult(new List<GroupMembership>
        {
            new()
            {
                GroupId = "G1",
                Users = new List<SfUser> { IdentityPublisherTestHelpers.MakeUser() },
            },
        });
        await Publisher.PublishAsync(crawl);

        var session = Store.GetLastSession();
        Assert.NotNull(session);
        Assert.Equal("completed", session!["status"]);
        Assert.Equal(1, session["groups_created"]);
    }
}

// ── Publish with no changes ──────────────────────────────────────────────────

public class TestPublishUnchanged : IdentityPublisherTestBase
{
    [Fact]
    public async Task NoApiCallsWhenUnchanged()
    {
        // Pre-populate store with email-based member (matching what flatten produces)
        Store.UpsertGroup("G1", "Group 1");
        Store.AddMember("G1", new MemberEntry(
            IdentityPublisherTestHelpers.UserGuid("005A"), "user", "azureActiveDirectory"));

        var crawl = IdentityPublisherTestHelpers.MakeCrawlResult(new List<GroupMembership>
        {
            new()
            {
                GroupId = "G1",
                DisplayName = "Group 1",
                Users = new List<SfUser> { IdentityPublisherTestHelpers.MakeUser("005A") },
            },
        });
        var stats = await Publisher.PublishAsync(crawl);

        Assert.Equal(1, stats.GroupsUnchanged);
        Assert.Equal(0, stats.GroupsCreated);
        Assert.Equal(0, stats.GroupsUpdated);
        Assert.Equal(0, stats.ApiCallsMade);
        Assert.Empty(Client.PutCalls);
        Assert.Empty(Client.PostCalls);
        Assert.Empty(Client.DeleteCalls);
    }
}

// ── Publish with member changes ──────────────────────────────────────────────

public class TestPublishUpdate : IdentityPublisherTestBase
{
    [Fact]
    public async Task AddsNewMembersRemovesStale()
    {
        Store.UpsertGroup("G1");
        Store.AddMember("G1", new MemberEntry(
            IdentityPublisherTestHelpers.UserGuid("005A"), "user", "azureActiveDirectory"));
        Store.AddMember("G1", new MemberEntry(
            IdentityPublisherTestHelpers.UserGuid("005B"), "user", "azureActiveDirectory"));

        var crawl = IdentityPublisherTestHelpers.MakeCrawlResult(new List<GroupMembership>
        {
            new()
            {
                GroupId = "G1",
                Users = new List<SfUser>
                {
                    IdentityPublisherTestHelpers.MakeUser("005A"),
                    IdentityPublisherTestHelpers.MakeUser("005C"),
                },
            },
        });
        var stats = await Publisher.PublishAsync(crawl);

        Assert.Equal(1, stats.GroupsUpdated);
        Assert.Equal(1, stats.MembersAdded);   // 005c@test.com
        Assert.Equal(1, stats.MembersRemoved);  // 005b@test.com

        // Store should have the new membership
        var memberIds = Store.GetMembers("G1").Select(m => m.MemberId).ToHashSet();
        Assert.Equal(
            new HashSet<string>
            {
                IdentityPublisherTestHelpers.UserGuid("005A"),
                IdentityPublisherTestHelpers.UserGuid("005C"),
            },
            memberIds);
    }
}

// ── Publish with stale groups ────────────────────────────────────────────────

public class TestPublishDelete : IdentityPublisherTestBase
{
    [Fact]
    public async Task DeletesStaleGroups()
    {
        Store.UpsertGroup("STALE");
        Store.AddMember("STALE", new MemberEntry("005X", "user"));

        var crawl = IdentityPublisherTestHelpers.MakeCrawlResult(new List<GroupMembership>());  // No groups in new crawl

        var stats = await Publisher.PublishAsync(crawl);

        Assert.Equal(1, stats.GroupsDeleted);
        Assert.Single(Client.DeleteCalls);
        Assert.False(Store.GroupExists("STALE"));
    }

    [Fact]
    public async Task Delete404TreatedAsSuccess()
    {
        Store.UpsertGroup("GONE");
        Client.OnDelete = _ => throw new GraphApiError(404, "Not found");

        var crawl = IdentityPublisherTestHelpers.MakeCrawlResult(new List<GroupMembership>());
        var stats = await Publisher.PublishAsync(crawl);

        Assert.Equal(1, stats.GroupsDeleted);
        Assert.Equal(0, stats.Errors);
        Assert.False(Store.GroupExists("GONE"));
    }
}

// ── Error handling ────────────────────────────────────────────────────────────

public class TestPublishErrors : IdentityPublisherTestBase
{
    [Fact]
    public async Task PutFailureCountsAsError()
    {
        Client.OnPost = (_, _) => throw new GraphApiError(500, "Internal error");

        var crawl = IdentityPublisherTestHelpers.MakeCrawlResult(new List<GroupMembership>
        {
            new()
            {
                GroupId = "G1",
                Users = new List<SfUser> { IdentityPublisherTestHelpers.MakeUser() },
            },
        });
        var stats = await Publisher.PublishAsync(crawl);

        Assert.True(stats.Errors >= 1);
        Assert.Equal(0, stats.GroupsCreated);
    }

    [Fact]
    public async Task MemberAddFailureCounted()
    {
        // First post call succeeds (group creation), subsequent ones fail (member add)
        var callCount = 0;
        Client.OnPost = (_, _) =>
        {
            callCount += 1;
            if (callCount == 1)
                return new JsonObject();  // group creation succeeds
            throw new GraphApiError(500, "error");
        };

        var crawl = IdentityPublisherTestHelpers.MakeCrawlResult(new List<GroupMembership>
        {
            new()
            {
                GroupId = "G1",
                Users = new List<SfUser> { IdentityPublisherTestHelpers.MakeUser("005A") },
            },
        });
        var stats = await Publisher.PublishAsync(crawl);

        Assert.True(stats.Errors >= 1);
        Assert.Equal(0, stats.MembersAdded);
    }

    [Fact]
    public async Task Member409TreatedAsSuccess()
    {
        // First post = group creation (success), second = member add (409)
        var callCount = 0;
        Client.OnPost = (_, _) =>
        {
            callCount += 1;
            if (callCount == 1)
                return new JsonObject();  // group creation
            throw new GraphApiError(409, "Conflict");
        };

        var crawl = IdentityPublisherTestHelpers.MakeCrawlResult(new List<GroupMembership>
        {
            new()
            {
                GroupId = "G1",
                Users = new List<SfUser> { IdentityPublisherTestHelpers.MakeUser("005A") },
            },
        });
        var stats = await Publisher.PublishAsync(crawl);

        Assert.Equal(1, stats.MembersAdded);
        Assert.Equal(0, stats.Errors);
    }

    [Fact]
    public async Task SessionMarkedFailedOnException()
    {
        Client.OnPost = (_, _) => throw new InvalidOperationException("boom");

        var crawl = IdentityPublisherTestHelpers.MakeCrawlResult(new List<GroupMembership>
        {
            new()
            {
                GroupId = "G1",
                Users = new List<SfUser> { IdentityPublisherTestHelpers.MakeUser() },
            },
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Publisher.PublishAsync(crawl));

        // The session should be recorded as failed
        // (get_last_session only returns completed, so it should be None)
        Assert.Null(Store.GetLastSession());
    }
}

// ── End-to-end scenario ──────────────────────────────────────────────────────

public class TestEndToEnd : IdentityPublisherTestBase
{
    /// <summary>Simulate two crawls: first creates everything, second only applies changes.</summary>
    [Fact]
    public async Task TwoCrawlsSecondIsIncremental()
    {
        // Crawl 1: initial
        var crawl1 = IdentityPublisherTestHelpers.MakeCrawlResult(new List<GroupMembership>
        {
            new()
            {
                GroupId = "AccountTopLevel",
                DisplayName = "Account",
                Users = new List<SfUser>
                {
                    IdentityPublisherTestHelpers.MakeUser("005A"),
                    IdentityPublisherTestHelpers.MakeUser("005B"),
                    IdentityPublisherTestHelpers.MakeUser("005C"),
                },
            },
            new()
            {
                GroupId = "AccountGlobalUsers",
                DisplayName = "GlobalUsers",
                Users = new List<SfUser> { IdentityPublisherTestHelpers.MakeUser("005ADMIN") },
            },
        });
        var stats1 = await Publisher.PublishAsync(crawl1);
        Assert.Equal(2, stats1.GroupsCreated);
        Assert.Equal(4, stats1.MembersAdded);

        // Reset mock call counts
        Client.ResetMock();

        // Crawl 2: 005B removed, 005D added, GlobalUsers unchanged
        var crawl2 = IdentityPublisherTestHelpers.MakeCrawlResult(new List<GroupMembership>
        {
            new()
            {
                GroupId = "AccountTopLevel",
                DisplayName = "Account",
                Users = new List<SfUser>
                {
                    IdentityPublisherTestHelpers.MakeUser("005A"),
                    IdentityPublisherTestHelpers.MakeUser("005C"),
                    IdentityPublisherTestHelpers.MakeUser("005D"),
                },
            },
            new()
            {
                GroupId = "AccountGlobalUsers",
                DisplayName = "GlobalUsers",
                Users = new List<SfUser> { IdentityPublisherTestHelpers.MakeUser("005ADMIN") },
            },
        });
        var stats2 = await Publisher.PublishAsync(crawl2);

        Assert.Equal(0, stats2.GroupsCreated);
        Assert.Equal(1, stats2.GroupsUpdated);  // Only TopLevel changed
        Assert.Equal(1, stats2.GroupsUnchanged);  // GlobalUsers unchanged
        Assert.Equal(1, stats2.MembersAdded);   // 005d@test.com
        Assert.Equal(1, stats2.MembersRemoved);  // 005b@test.com

        // No PUT calls (no new groups), only POST (add) and DELETE (remove)
        Assert.Empty(Client.PutCalls);
        Assert.Single(Client.PostCalls);   // add 005d
        Assert.Single(Client.DeleteCalls);  // remove 005b

        // Final store state
        var tlMembers = Store.GetMembers("AccountTopLevel").Select(m => m.MemberId).ToHashSet();
        Assert.Equal(
            new HashSet<string>
            {
                IdentityPublisherTestHelpers.UserGuid("005A"),
                IdentityPublisherTestHelpers.UserGuid("005C"),
                IdentityPublisherTestHelpers.UserGuid("005D"),
            },
            tlMembers);
    }

    /// <summary>Group created in crawl 1, updated in crawl 2, deleted in crawl 3.</summary>
    [Fact]
    public async Task ThreeCrawlsGroupLifecycle()
    {
        // Crawl 1: create
        var stats1 = await Publisher.PublishAsync(IdentityPublisherTestHelpers.MakeCrawlResult(
            new List<GroupMembership>
            {
                new()
                {
                    GroupId = "G1",
                    Users = new List<SfUser> { IdentityPublisherTestHelpers.MakeUser("005A") },
                },
            }));
        Assert.Equal(1, stats1.GroupsCreated);
        Client.ResetMock();

        // Crawl 2: update membership
        var stats2 = await Publisher.PublishAsync(IdentityPublisherTestHelpers.MakeCrawlResult(
            new List<GroupMembership>
            {
                new()
                {
                    GroupId = "G1",
                    Users = new List<SfUser> { IdentityPublisherTestHelpers.MakeUser("005B") },
                },
            }));
        Assert.Equal(1, stats2.GroupsUpdated);
        Client.ResetMock();

        // Crawl 3: group gone
        var stats3 = await Publisher.PublishAsync(
            IdentityPublisherTestHelpers.MakeCrawlResult(new List<GroupMembership>()));
        Assert.Equal(1, stats3.GroupsDeleted);
        Assert.False(Store.GroupExists("G1"));
    }
}
