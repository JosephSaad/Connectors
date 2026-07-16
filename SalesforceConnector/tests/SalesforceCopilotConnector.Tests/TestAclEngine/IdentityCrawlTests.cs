// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for AclEngine.IdentitySync — Identity Crawl handler.

using SalesforceCopilotConnector.AclEngine;

namespace SalesforceCopilotConnector.Tests.TestAclEngine;

// ── Helpers ──────────────────────────────────────────────────────────────────

/// <summary>
/// Fake IdentityQueryClient with configurable async method results
/// (port of the Python `_mock_query_client` helper).
/// </summary>
file sealed class FakeQueryClient : IdentityQueryClient
{
    public Dictionary<string, EntityVisibility> OwdMap = new() { ["Account"] = EntityVisibility.Read };
    public List<SfUser> AuthorizedUsers = new();
    public List<SfUser> GlobalUsers = new();
    public List<string> GroupShareIds = new();
    public List<SfGroup> Groups = new();
    public Dictionary<string, string> RoleHierarchy = new();
    public HashSet<string> RolesAssigned = new();
    public List<SfUser> RoleUsers = new();
    public List<SfUser> GroupMembers = new();
    public SfUser? SingleUser;
    public List<SfUser> MgrAndSubs = new();
    public List<SfUser> TerritoryUsers = new();
    public HashSet<string> DescendantTerritoryIds = new();

    // Call counters (mirror Python's `assert_not_awaited` checks)
    public int AuthorizedUsersCalls;

    public FakeQueryClient()
        : base(
            new SalesforceClient("https://test.my.salesforce.com", "60.0", "mock-token"),
            owdFieldMap: new Dictionary<string, string>())
    {
    }

    public override Task<Dictionary<string, EntityVisibility>> GetOrgWideDefaultsAsync()
        => Task.FromResult(new Dictionary<string, EntityVisibility>(OwdMap));

    public override Task<List<SfUser>> GetAuthorizedUsersAsync(string objectName)
    {
        AuthorizedUsersCalls++;
        return Task.FromResult(new List<SfUser>(AuthorizedUsers));
    }

    public override Task<List<SfUser>> GetGlobalAccessUsersAsync(string objectName)
        => Task.FromResult(new List<SfUser>(GlobalUsers));

    public override Task<List<string>> GetGroupShareIdsAsync(string objectName)
        => Task.FromResult(new List<string>(GroupShareIds));

    public override Task<List<SfGroup>> GetGroupsByIdsAsync(List<string> groupIds)
        => Task.FromResult(new List<SfGroup>(Groups));

    public override Task<Dictionary<string, string>> GetRoleHierarchyAsync()
        => Task.FromResult(new Dictionary<string, string>(RoleHierarchy));

    public override Task<HashSet<string>> GetRolesAssignedToUsersAsync()
        => Task.FromResult(new HashSet<string>(RolesAssigned));

    public override Task<List<SfUser>> GetUsersForRoleAsync(string roleId)
        => Task.FromResult(new List<SfUser>(RoleUsers));

    public override Task<List<SfUser>> GetGroupMemberUsersAsync(string groupId)
        => Task.FromResult(new List<SfUser>(GroupMembers));

    public override Task<SfUser?> GetUserByIdAsync(string userId)
        => Task.FromResult(SingleUser);

    public override Task<List<SfUser>> GetManagerAndSubordinatesAsync(string managerId)
        => Task.FromResult(new List<SfUser>(MgrAndSubs));

    public override Task<List<SfUser>> GetTerritoryUsersAsync(string territoryId)
        => Task.FromResult(new List<SfUser>(TerritoryUsers));

    public override Task<HashSet<string>> GetAllDescendantTerritoryIdsAsync(string territoryId)
        => Task.FromResult(new HashSet<string>(DescendantTerritoryIds));

    // Python's MagicMock returns truthy values for these
    public override bool HasShareTable(string objectName) => true;

    public override bool HasOwdField(string objectName) => true;
}

file static class CrawlTestHelpers
{
    public static SfUser MakeUser(string userId = "005U1", string name = "User")
        => new(userId) { Name = name, Email = $"{name.ToLowerInvariant()}@test.com" };

    public static SalesforceClient DummySfClient()
        => new("https://test.my.salesforce.com", "60.0", "mock-token");
}

// ── IdentityCrawlEnumerator tests ────────────────────────────────────────────

public class IdentityCrawlEnumeratorTests
{
    [Fact]
    public async Task EmitsOneGroupPerObject()
    {
        var qc = new FakeQueryClient
        {
            OwdMap = new Dictionary<string, EntityVisibility>
            {
                ["Account"] = EntityVisibility.Read,
                ["Lead"] = EntityVisibility.None,
            },
        };
        var enumerator = new IdentityCrawlEnumerator(
            queryClient: qc,
            objectNames: new List<string> { "Account", "Lead" },
            parentMap: new Dictionary<string, (string, string)>(),
            owdOverrides: new Dictionary<string, string>());
        var groups = await enumerator.EnumerateAsync();
        Assert.Equal(2, groups.Count);
        var ids = groups.Select(g => g.GroupId).ToHashSet();
        Assert.Contains("AccountTopLevel", ids);
        Assert.Contains("LeadTopLevel", ids);
    }

    [Fact]
    public async Task OwdIsPreservedInMetadata()
    {
        var qc = new FakeQueryClient
        {
            OwdMap = new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.None },
        };
        var enumerator = new IdentityCrawlEnumerator(
            queryClient: qc,
            objectNames: new List<string> { "Account" },
            parentMap: new Dictionary<string, (string, string)>(),
            owdOverrides: new Dictionary<string, string>());
        var groups = await enumerator.EnumerateAsync();
        Assert.Equal(EntityVisibility.None, groups[0].Owd);
    }

    [Fact]
    public async Task ControlledByParentResolvesToPublic()
    {
        var qc = new FakeQueryClient
        {
            OwdMap = new Dictionary<string, EntityVisibility>
            {
                ["Account"] = EntityVisibility.Read,
                ["Contact"] = EntityVisibility.ControlledByParent,
            },
        };
        var enumerator = new IdentityCrawlEnumerator(
            queryClient: qc,
            objectNames: new List<string> { "Contact" },
            parentMap: new Dictionary<string, (string, string)> { ["Contact"] = ("AccountId", "Account") },
            owdOverrides: new Dictionary<string, string>());
        var groups = await enumerator.EnumerateAsync();
        Assert.Equal(EntityVisibility.Read, groups[0].Owd);
    }

    [Fact]
    public async Task ControlledByParentStaysPrivateWhenParentPrivate()
    {
        var qc = new FakeQueryClient
        {
            OwdMap = new Dictionary<string, EntityVisibility>
            {
                ["Account"] = EntityVisibility.None,
                ["Contact"] = EntityVisibility.ControlledByParent,
            },
        };
        var enumerator = new IdentityCrawlEnumerator(
            queryClient: qc,
            objectNames: new List<string> { "Contact" },
            parentMap: new Dictionary<string, (string, string)> { ["Contact"] = ("AccountId", "Account") },
            owdOverrides: new Dictionary<string, string>());
        var groups = await enumerator.EnumerateAsync();
        Assert.Equal(EntityVisibility.ControlledByParent, groups[0].Owd);
    }

    [Fact]
    public async Task OwdOverridesApplied()
    {
        var qc = new FakeQueryClient
        {
            OwdMap = new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.Read },
        };
        var enumerator = new IdentityCrawlEnumerator(
            queryClient: qc,
            objectNames: new List<string> { "Account" },
            parentMap: new Dictionary<string, (string, string)>(),
            owdOverrides: new Dictionary<string, string> { ["Account"] = "Private" });
        var groups = await enumerator.EnumerateAsync();
        Assert.Equal(EntityVisibility.None, groups[0].Owd);
    }
}

// ── IdentityGatherer tests ───────────────────────────────────────────────────

public class IdentityGathererPublicTests
{
    [Fact]
    public async Task PublicOwdSkipsUserQuery()
    {
        var users = new List<SfUser> { CrawlTestHelpers.MakeUser("005A", "Alice"), CrawlTestHelpers.MakeUser("005B", "Bob") };
        var qc = new FakeQueryClient { AuthorizedUsers = users };
        var gatherer = new IdentityGatherer(qc);

        var result = await gatherer.BuildTopLevelGroupAsync("Account", EntityVisibility.Read);
        Assert.Equal("AccountTopLevel", result.GroupId);
        // PUBLIC OWD uses grant-everyone on content items; no users needed in group
        Assert.Empty(result.Users);
        Assert.Empty(result.ChildGroups);
        Assert.Equal(0, qc.AuthorizedUsersCalls);
    }

    [Fact]
    public async Task PublicOwdEmptyGroup()
    {
        var qc = new FakeQueryClient { AuthorizedUsers = new List<SfUser>() };
        var gatherer = new IdentityGatherer(qc);
        var result = await gatherer.BuildTopLevelGroupAsync("Lead", EntityVisibility.Edit);
        Assert.Empty(result.Users);
        Assert.Equal(0, qc.AuthorizedUsersCalls);
    }
}

public class IdentityGathererPrivateTests
{
    [Fact]
    public async Task PrivateOwdCreatesGlobalUsersChild()
    {
        var qc = new FakeQueryClient
        {
            OwdMap = new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.None },
            GroupShareIds = new List<string>(),
            RoleHierarchy = new Dictionary<string, string>(),
            RolesAssigned = new HashSet<string>(),
        };
        var gatherer = new IdentityGatherer(qc);
        var result = await gatherer.BuildTopLevelGroupAsync("Account", EntityVisibility.None);
        Assert.Empty(result.Users);
        var childIds = result.ChildGroups.Select(c => c.GroupId).ToList();
        Assert.Contains("AccountGlobalUsers", childIds);
    }

    [Fact]
    public async Task PrivateOwdCreatesRoleGroupsFromShares()
    {
        var roleGroup = new SfGroup("00G_ROLE", UserOrGroupType.Role)
        {
            RelatedId = "00E_ROLE1",
        };
        var qc = new FakeQueryClient
        {
            GroupShareIds = new List<string> { "00G_ROLE" },
            Groups = new List<SfGroup> { roleGroup },
            RoleHierarchy = new Dictionary<string, string> { ["00E_ROLE1"] = "" },
            RolesAssigned = new HashSet<string> { "00E_ROLE1" },
        };
        var gatherer = new IdentityGatherer(qc);
        var result = await gatherer.BuildTopLevelGroupAsync("Account", EntityVisibility.None);
        var childIds = result.ChildGroups.Select(c => c.GroupId).ToList();
        Assert.Contains("Account00EROLE1Role", childIds);
    }

    [Fact]
    public async Task PrivateOwdCreatesRoleHierarchy()
    {
        var roleGroup = new SfGroup("00G_ROLE", UserOrGroupType.Role)
        {
            RelatedId = "00E_CHILD",
        };
        var qc = new FakeQueryClient
        {
            GroupShareIds = new List<string> { "00G_ROLE" },
            Groups = new List<SfGroup> { roleGroup },
            RoleHierarchy = new Dictionary<string, string> { ["00E_CHILD"] = "00E_PARENT", ["00E_PARENT"] = "" },
            RolesAssigned = new HashSet<string> { "00E_CHILD", "00E_PARENT" },
        };
        var gatherer = new IdentityGatherer(qc);
        var result = await gatherer.BuildTopLevelGroupAsync("Account", EntityVisibility.None);
        var childIds = result.ChildGroups.Select(c => c.GroupId).ToList();
        Assert.Contains("Account00ECHILDRole", childIds);
        Assert.Contains("Account00EPARENTRole", childIds);
    }

    [Fact]
    public async Task PrivateOwdOrganizationShare()
    {
        var orgGroup = new SfGroup("00G_ORG", UserOrGroupType.Organization);
        var qc = new FakeQueryClient
        {
            GroupShareIds = new List<string> { "00G_ORG" },
            Groups = new List<SfGroup> { orgGroup },
            RoleHierarchy = new Dictionary<string, string>(),
            RolesAssigned = new HashSet<string>(),
        };
        var gatherer = new IdentityGatherer(qc);
        var result = await gatherer.BuildTopLevelGroupAsync("Account", EntityVisibility.None);
        var childIds = result.ChildGroups.Select(c => c.GroupId).ToList();
        Assert.Contains("AccountAllInternalUsers", childIds);
    }

    [Fact]
    public async Task PrivateOwdManagerShare()
    {
        var mgrGroup = new SfGroup("00G_MGR", UserOrGroupType.Manager)
        {
            RelatedId = "005MGR1",
        };
        var qc = new FakeQueryClient
        {
            GroupShareIds = new List<string> { "00G_MGR" },
            Groups = new List<SfGroup> { mgrGroup },
            RoleHierarchy = new Dictionary<string, string>(),
            RolesAssigned = new HashSet<string>(),
        };
        var gatherer = new IdentityGatherer(qc);
        var result = await gatherer.BuildTopLevelGroupAsync("Account", EntityVisibility.None);
        var childIds = result.ChildGroups.Select(c => c.GroupId).ToList();
        Assert.Contains("Account005MGR1Manager", childIds);
    }

    [Fact]
    public async Task PrivateOwdPublicGroupShare()
    {
        var pgGroup = new SfGroup("00G_PG", UserOrGroupType.Regular)
        {
            GroupMembers = new List<string> { "005M1", "005M2" },
        };
        var qc = new FakeQueryClient
        {
            GroupShareIds = new List<string> { "00G_PG" },
            Groups = new List<SfGroup> { pgGroup },
            RoleHierarchy = new Dictionary<string, string>(),
            RolesAssigned = new HashSet<string>(),
        };
        var gatherer = new IdentityGatherer(qc);
        var result = await gatherer.BuildTopLevelGroupAsync("Account", EntityVisibility.None);
        var childIds = result.ChildGroups.Select(c => c.GroupId).ToList();
        Assert.Contains("Account00GPGPublicGroup", childIds);
    }

    [Fact]
    public async Task EmptyPublicGroupIsNotCreated()
    {
        var pgGroup = new SfGroup("00G_EMPTY", UserOrGroupType.Regular)
        {
            GroupMembers = new List<string>(),
        };
        var qc = new FakeQueryClient
        {
            GroupShareIds = new List<string> { "00G_EMPTY" },
            Groups = new List<SfGroup> { pgGroup },
            RoleHierarchy = new Dictionary<string, string>(),
            RolesAssigned = new HashSet<string>(),
        };
        var gatherer = new IdentityGatherer(qc);
        var result = await gatherer.BuildTopLevelGroupAsync("Account", EntityVisibility.None);
        var childIds = result.ChildGroups.Select(c => c.GroupId).ToList();
        Assert.DoesNotContain("Account00G_EMPTYPublicGroup", childIds);
    }

    [Fact]
    public async Task PrivateOwdTerritoryShare()
    {
        var terrGroup = new SfGroup("00G_TERR", UserOrGroupType.Territory)
        {
            RelatedId = "0ML_TERR1",
        };
        var qc = new FakeQueryClient
        {
            GroupShareIds = new List<string> { "00G_TERR" },
            Groups = new List<SfGroup> { terrGroup },
            RoleHierarchy = new Dictionary<string, string>(),
            RolesAssigned = new HashSet<string>(),
        };
        var gatherer = new IdentityGatherer(qc);
        var result = await gatherer.BuildTopLevelGroupAsync("Account", EntityVisibility.None);
        var childIds = result.ChildGroups.Select(c => c.GroupId).ToList();
        Assert.Contains("Account0MLTERR1Territory", childIds);
    }

    [Fact]
    public async Task PrivateOwdTerritoryAndSubordinatesShare()
    {
        var terrGroup = new SfGroup("00G_TERR_SUB", UserOrGroupType.TerritoryAndSubordinates)
        {
            RelatedId = "0ML_TERR2",
        };
        var qc = new FakeQueryClient
        {
            GroupShareIds = new List<string> { "00G_TERR_SUB" },
            Groups = new List<SfGroup> { terrGroup },
            RoleHierarchy = new Dictionary<string, string>(),
            RolesAssigned = new HashSet<string>(),
        };
        var gatherer = new IdentityGatherer(qc);
        var result = await gatherer.BuildTopLevelGroupAsync("Account", EntityVisibility.None);
        var childIds = result.ChildGroups.Select(c => c.GroupId).ToList();
        Assert.Contains("Account0MLTERR2TerritoryAndSubordinates", childIds);
    }
}

public class IdentityGathererChildGroupsTests
{
    [Fact]
    public async Task GatherGlobalAccessUsers()
    {
        var users = new List<SfUser> { CrawlTestHelpers.MakeUser("005ADM", "Admin") };
        var qc = new FakeQueryClient { GlobalUsers = users };
        var gatherer = new IdentityGatherer(qc);

        var childRef = new ChildGroupRef
        {
            GroupId = "AccountGlobalUsers",
            GroupType = GroupIdentityType.GlobalAccessUsers,
            Metadata = new Dictionary<string, object?> { ["ChildGroupType"] = "GlobalAccessUsers" },
        };
        var result = await gatherer.GatherChildGroupAsync("Account", childRef);
        Assert.Single(result.Users);
        Assert.Equal("005ADM", result.Users[0].Id);
    }

    [Fact]
    public async Task GatherRoleWithParent()
    {
        var users = new List<SfUser> { CrawlTestHelpers.MakeUser("005R1", "RoleUser") };
        var qc = new FakeQueryClient { RoleUsers = users };
        var gatherer = new IdentityGatherer(qc);

        var childRef = new ChildGroupRef
        {
            GroupId = "Account00E_ROLE1Role",
            GroupType = GroupIdentityType.RoleWithParent,
            Metadata = new Dictionary<string, object?> { ["RoleId"] = "00E_ROLE1", ["ParentRoleId"] = "00E_PARENT" },
        };
        var result = await gatherer.GatherChildGroupAsync("Account", childRef);
        Assert.Single(result.Users);
        Assert.Single(result.ChildGroups);
        Assert.Equal("Account00EPARENTRole", result.ChildGroups[0].GroupId);
        Assert.False(result.ChildGroups[0].NeedsGather);
    }

    [Fact]
    public async Task GatherRoleWithoutParent()
    {
        var qc = new FakeQueryClient { RoleUsers = new List<SfUser> { CrawlTestHelpers.MakeUser() } };
        var gatherer = new IdentityGatherer(qc);

        var childRef = new ChildGroupRef
        {
            GroupId = "Account00E_ROOTRole",
            GroupType = GroupIdentityType.RoleWithoutParent,
            Metadata = new Dictionary<string, object?> { ["RoleId"] = "00E_ROOT", ["ParentRoleId"] = "" },
        };
        var result = await gatherer.GatherChildGroupAsync("Account", childRef);
        Assert.Empty(result.ChildGroups);
    }

    [Fact]
    public async Task GatherAllInternalUsers()
    {
        var users = new List<SfUser> { CrawlTestHelpers.MakeUser("005A"), CrawlTestHelpers.MakeUser("005B") };
        var qc = new FakeQueryClient { AuthorizedUsers = users };
        var gatherer = new IdentityGatherer(qc);

        var childRef = new ChildGroupRef
        {
            GroupId = "AccountAllInternalUsers",
            GroupType = GroupIdentityType.AllInternalUsers,
            Metadata = new Dictionary<string, object?>(),
        };
        var result = await gatherer.GatherChildGroupAsync("Account", childRef);
        Assert.Equal(2, result.Users.Count);
    }

    [Fact]
    public async Task GatherPublicGroup()
    {
        var members = new List<SfUser> { CrawlTestHelpers.MakeUser("005M1"), CrawlTestHelpers.MakeUser("005M2") };
        var qc = new FakeQueryClient { GroupMembers = members };
        var gatherer = new IdentityGatherer(qc);

        var childRef = new ChildGroupRef
        {
            GroupId = "Account00G_PGPublicGroup",
            GroupType = GroupIdentityType.PublicGroup,
            Metadata = new Dictionary<string, object?> { ["PublicGroupId"] = "00G_PG" },
        };
        var result = await gatherer.GatherChildGroupAsync("Account", childRef);
        Assert.Equal(2, result.Users.Count);
    }

    [Fact]
    public async Task GatherManager()
    {
        var manager = CrawlTestHelpers.MakeUser("005MGR", "Manager");
        var qc = new FakeQueryClient { SingleUser = manager };
        var gatherer = new IdentityGatherer(qc);

        var childRef = new ChildGroupRef
        {
            GroupId = "Account005MGRManager",
            GroupType = GroupIdentityType.Manager,
            Metadata = new Dictionary<string, object?> { ["RelatedId"] = "005MGR" },
        };
        var result = await gatherer.GatherChildGroupAsync("Account", childRef);
        Assert.Single(result.Users);
        Assert.Equal("005MGR", result.Users[0].Id);
    }

    [Fact]
    public async Task GatherManagerAndSubordinates()
    {
        var users = new List<SfUser>
        {
            CrawlTestHelpers.MakeUser("005MGR"),
            CrawlTestHelpers.MakeUser("005SUB1"),
            CrawlTestHelpers.MakeUser("005SUB2"),
        };
        var qc = new FakeQueryClient { MgrAndSubs = users };
        var gatherer = new IdentityGatherer(qc);

        var childRef = new ChildGroupRef
        {
            GroupId = "Account005MGRManagerAndSubordinates",
            GroupType = GroupIdentityType.ManagerAndSubordinates,
            Metadata = new Dictionary<string, object?> { ["RelatedId"] = "005MGR" },
        };
        var result = await gatherer.GatherChildGroupAsync("Account", childRef);
        Assert.Equal(3, result.Users.Count);
    }

    [Fact]
    public async Task GatherTerritory()
    {
        var users = new List<SfUser>
        {
            CrawlTestHelpers.MakeUser("005T1", "TerrUser"),
            CrawlTestHelpers.MakeUser("005T2", "TerrUser2"),
        };
        var qc = new FakeQueryClient { TerritoryUsers = users };
        var gatherer = new IdentityGatherer(qc);

        var childRef = new ChildGroupRef
        {
            GroupId = "Account0ML_T1Territory",
            GroupType = GroupIdentityType.Territory,
            Metadata = new Dictionary<string, object?> { ["RelatedId"] = "0ML_T1" },
        };
        var result = await gatherer.GatherChildGroupAsync("Account", childRef);
        Assert.Equal(2, result.Users.Count);
    }

    [Fact]
    public async Task GatherTerritoryAndSubordinates()
    {
        var users = new List<SfUser> { CrawlTestHelpers.MakeUser("005T1", "TerrUser") };
        var qc = new FakeQueryClient
        {
            TerritoryUsers = users,
            DescendantTerritoryIds = new HashSet<string> { "0ML_CHILD1" },
        };
        var gatherer = new IdentityGatherer(qc);

        var childRef = new ChildGroupRef
        {
            GroupId = "Account0ML_T1TerritoryAndSubordinates",
            GroupType = GroupIdentityType.TerritoryAndSubordinates,
            Metadata = new Dictionary<string, object?> { ["RelatedId"] = "0ML_T1" },
        };
        var result = await gatherer.GatherChildGroupAsync("Account", childRef);
        // GetTerritoryUsersAsync called for root + 1 descendant; same users deduped
        Assert.True(result.Users.Count >= 1);
    }
}

// ── IdentitySyncHandler integration tests ────────────────────────────────────

public class IdentitySyncHandlerTests
{
    [Fact]
    public void FullCrawlPublicObject()
    {
        var instance = new FakeQueryClient
        {
            OwdMap = new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.Read },
            AuthorizedUsers = new List<SfUser>(),
        };

        var handler = new IdentitySyncHandler(
            sfClient: CrawlTestHelpers.DummySfClient(),
            objectNames: new List<string> { "Account" });
        handler._queryClient = instance;

        var result = handler.RunFullCrawl();

        Assert.Single(result.TopLevelGroups);
        Assert.Equal(EntityVisibility.Read, result.TopLevelGroups[0].Owd);
        // PUBLIC OWD: no users emitted (grant-everyone used on content items)
        Assert.Equal(0, result.TotalUsersEmitted);
        Assert.True(result.TotalGroupsEmitted >= 1);
        Assert.Equal(0, instance.AuthorizedUsersCalls);
    }

    [Fact]
    public void IncrementalCrawlSameAsFull()
    {
        var instance = new FakeQueryClient
        {
            OwdMap = new Dictionary<string, EntityVisibility> { ["Lead"] = EntityVisibility.Edit },
            AuthorizedUsers = new List<SfUser>(),
        };

        var handler = new IdentitySyncHandler(
            sfClient: CrawlTestHelpers.DummySfClient(),
            objectNames: new List<string> { "Lead" });
        handler._queryClient = instance;

        var fullResult = handler.RunFullCrawl();
        var incrResult = handler.RunIncrementalCrawl();

        Assert.Equal(fullResult.TopLevelGroups.Count, incrResult.TopLevelGroups.Count);
    }
}

// ── Group ID consistency between identity sync and ACL builder ───────────────

/// <summary>
/// Critical verification: group IDs used in identity crawl MUST exactly match
/// group IDs referenced in content ACLs.
/// </summary>
public class GroupIdConsistencyAcrossModulesTests
{
    [Fact]
    public void TopLevelIdMatches()
    {
        // Identity crawl format
        var identityId = SfGroupIdFormats.TopLevel.Format("Account");
        // ACL builder format (same constant)
        var aclId = SfGroupIdFormats.TopLevel.Format("Account");
        Assert.Equal(identityId, aclId);
        Assert.Equal("AccountTopLevel", aclId);
    }

    [Fact]
    public void GlobalUsersIdMatches()
    {
        var identityId = SfGroupIdFormats.GlobalUsers.Format("Account");
        var aclId = SfGroupIdFormats.GlobalUsers.Format("Account");
        Assert.Equal(identityId, aclId);
        Assert.Equal("AccountGlobalUsers", aclId);
    }

    [Fact]
    public void RoleIdMatches()
    {
        var roleId = "00E5g000001ABC";
        var identityId = SfGroupIdFormats.Role.Format("Account", roleId);
        var aclId = SfGroupIdFormats.Role.Format("Account", roleId);
        Assert.Equal(identityId, aclId);
        Assert.Equal($"Account{roleId}Role", aclId);
    }

    [Fact]
    public void AllInternalUsersIdMatches()
    {
        var identityId = SfGroupIdFormats.AllInternalUsers.Format("Account");
        var aclId = SfGroupIdFormats.AllInternalUsers.Format("Account");
        Assert.Equal(identityId, aclId);
        Assert.Equal("AccountAllInternalUsers", aclId);
    }

    [Fact]
    public void ManagerIdMatches()
    {
        var userId = "005DEF";
        var identityId = SfGroupIdFormats.Manager.Format("Account", userId);
        var aclId = SfGroupIdFormats.Manager.Format("Account", userId);
        Assert.Equal(identityId, aclId);
        Assert.Equal($"Account{userId}Manager", aclId);
    }

    [Fact]
    public void PublicGroupIdMatches()
    {
        var groupId = "00G_XYZ";
        var identityId = SfGroupIdFormats.PublicGroup.Format("Account", groupId);
        var aclId = SfGroupIdFormats.PublicGroup.Format("Account", groupId);
        Assert.Equal(identityId, aclId);
        Assert.Equal("Account00GXYZPublicGroup", aclId);
    }

    [Fact]
    public void TerritoryIdMatches()
    {
        var terrId = "0ML_ABC";
        var identityId = SfGroupIdFormats.Territory.Format("Account", terrId);
        var aclId = SfGroupIdFormats.Territory.Format("Account", terrId);
        Assert.Equal(identityId, aclId);
        Assert.Equal("Account0MLABCTerritory", aclId);
    }

    [Fact]
    public void TerritoryAndSubordinatesIdMatches()
    {
        var terrId = "0ML_ABC";
        var identityId = SfGroupIdFormats.TerritoryAndSubordinates.Format("Account", terrId);
        var aclId = SfGroupIdFormats.TerritoryAndSubordinates.Format("Account", terrId);
        Assert.Equal(identityId, aclId);
        Assert.Equal("Account0MLABCTerritoryAndSubordinates", aclId);
    }
}
