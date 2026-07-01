// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for acl_engine.group_acl_builder — Group-based ACL resolution.
// Port of tests/test_acl_engine/test_group_acl_builder.py.

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.AclEngine;

namespace SalesforceCopilotConnector.Tests.TestAclEngine;

public class GroupAclBuilderTests
{
    // ── Test doubles (Python MagicMock/AsyncMock equivalents) ────────────────

    /// <summary>
    /// Stand-in for Python's ``MagicMock()`` sf_client.  ``QueryAllAsync`` is
    /// routed through a configurable handler (``builder._sf.query_all = AsyncMock(...)``).
    /// </summary>
    internal class FakeSalesforceClient : SalesforceClient
    {
        /// <summary>Configured behaviour for QueryAllAsync (throw to simulate side_effect=Exception).</summary>
        public Func<string, List<JsonObject>>? QueryAllHandler;
        public int QueryAllCallCount;

        public FakeSalesforceClient()
            : base(TestFixtures.InstanceUrl, TestFixtures.ApiVersion, "mock-access-token")
        {
        }

        public override Task<List<JsonObject>> QueryAllAsync(string soql, bool tooling = false)
        {
            QueryAllCallCount += 1;
            if (QueryAllHandler is null)
                throw new InvalidOperationException("QueryAllAsync was not configured on FakeSalesforceClient");
            return Task.FromResult(QueryAllHandler(soql));
        }
    }

    /// <summary>
    /// Stand-in for ``MagicMock()`` principal mapper with
    /// ``mapper._resolve_identifier.return_value = ...``.
    /// </summary>
    internal class FakePrincipalMapper : PrincipalMapper
    {
        public string? ResolveIdentifierReturnValue;
        public int ResolveIdentifierCallCount;

        public FakePrincipalMapper()
            : base(new FakeSalesforceClient())
        {
        }

        internal override Task<string?> ResolveIdentifierAsync(string identifier)
        {
            ResolveIdentifierCallCount += 1;
            return Task.FromResult(ResolveIdentifierReturnValue);
        }
    }

    /// <summary>
    /// GroupAclBuilder whose ``_fetch_and_inject_shares`` can be substituted,
    /// mirroring ``builder._fetch_and_inject_shares = AsyncMock(side_effect=...)``.
    /// </summary>
    internal class TestableGroupAclBuilder : GroupAclBuilder
    {
        public Func<string, List<JsonObject>, Task>? FetchAndInjectSharesHandler;
        public int FetchAndInjectSharesCallCount;

        public TestableGroupAclBuilder(
            SalesforceClient sfClient,
            Dictionary<string, (string ParentField, string ParentObject)> parentMap)
            : base(sfClient, parentMap: parentMap)
        {
        }

        internal override Task FetchAndInjectSharesAsync(
            string objectType,
            List<JsonObject> records,
            int batchSize = 200)
        {
            if (FetchAndInjectSharesHandler is null)
                return base.FetchAndInjectSharesAsync(objectType, records, batchSize);
            FetchAndInjectSharesCallCount += 1;
            return FetchAndInjectSharesHandler(objectType, records);
        }
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    internal static SfUser MakeSfUser(
        string userId = "005000000000001",
        string name = "Test User",
        string email = "test@example.com",
        string federationId = "",
        string parentRoleId = "",
        List<Dictionary<string, object?>>? permissionSets = null)
    {
        return new SfUser(userId)
        {
            Name = name,
            Email = email,
            FederationIdentifier = federationId,
            UserName = $"{name.ToLower().Replace(" ", ".")}@test.com",
            ParentRoleId = parentRoleId,
            PermissionSets = permissionSets
                ?? new List<Dictionary<string, object?>> { new() { ["Id"] = "ps1", ["Label"] = "Read" } },
        };
    }

    internal static TestableGroupAclBuilder MakeBuilder(
        Dictionary<string, EntityVisibility>? owdMap = null,
        List<SfUser>? users = null,
        List<SfGroup>? groups = null,
        HashSet<string>? frozen = null,
        Dictionary<string, (string ParentField, string ParentObject)>? parentMap = null)
    {
        var sfClient = new FakeSalesforceClient();
        var builder = new TestableGroupAclBuilder(
            sfClient,
            parentMap: parentMap ?? new Dictionary<string, (string, string)>());
        builder._owdMap = owdMap ?? new Dictionary<string, EntityVisibility>();
        if (users is not null)
            builder._usersById = users.ToDictionary(u => u.Id);
        if (groups is not null)
            builder._groupsById = groups.ToDictionary(g => g.Id);
        builder._frozenUsers = frozen ?? new HashSet<string>();
        return builder;
    }

    // ── JSON record helpers ──────────────────────────────────────────────────

    internal static JsonObject Share(string userOrGroupId, string type) => new()
    {
        ["UserOrGroupId"] = userOrGroupId,
        ["UserOrGroup"] = new JsonObject { ["Type"] = type },
    };

    internal static JsonObject Shares(params JsonObject[] shares)
    {
        var arr = new JsonArray();
        foreach (var s in shares)
            arr.Add(s);
        return new JsonObject { ["records"] = arr };
    }

    internal static Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>> EmptyAclMaps() => new();

    // ── ACE factory tests ────────────────────────────────────────────────────

    public class AceFactories
    {
        [Fact]
        public void GroupAce()
        {
            var ace = GroupAclBuilder.GroupAce("AccountTopLevel");
            Assert.Equal(
                new Dictionary<string, string>
                {
                    ["accessType"] = "grant",
                    ["type"] = "externalGroup",
                    ["value"] = "AccountTopLevel",
                },
                ace);
        }

        [Fact]
        public void UserAceAadWithGuid()
        {
            var guid = "f1126041-cb51-4f20-82d5-722b4cfcdfa1";
            var ace = GroupAclBuilder.UserAceAad(guid);
            Assert.NotNull(ace);
            Assert.Equal(guid, ace!["value"]);
        }

        [Fact]
        public void UserAceAadRejectsEmail()
        {
            var ace = GroupAclBuilder.UserAceAad("user@tenant.com");
            Assert.Null(ace);
        }

        [Fact]
        public void UserAceExternalReturnsNone()
        {
            var user = MakeSfUser(userId: "005ABC");
            var ace = GroupAclBuilder.UserAceExternal(user);
            Assert.Null(ace);
        }
    }

    // ── PUBLIC OWD tests ─────────────────────────────────────────────────────

    public class PublicOwd
    {
        [Fact]
        public async Task PublicOwdProducesGrantEveryoneAce()
        {
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.Read });
            var records = new List<JsonObject> { new() { ["Id"] = "001ABC", ["objectType"] = "Account" } };

            var result = await builder.BuildAclMapAsync("Account", records, EmptyAclMaps());

            Assert.True(result.ContainsKey("001ABC"));
            Assert.Single(result["001ABC"]);
            Assert.Equal("everyone", result["001ABC"][0]["value"]);
            Assert.Equal("everyone", result["001ABC"][0]["type"]);
            Assert.Equal("grant", result["001ABC"][0]["accessType"]);
        }

        [Fact]
        public async Task EditOwdIsPublic()
        {
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility> { ["Lead"] = EntityVisibility.Edit });
            var records = new List<JsonObject> { new() { ["Id"] = "00Q001" } };
            var result = await builder.BuildAclMapAsync("Lead", records, EmptyAclMaps());
            Assert.Equal("everyone", result["00Q001"][0]["value"]);
        }

        [Fact]
        public async Task ReadEditTransferOwdIsPublic()
        {
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility> { ["Case"] = EntityVisibility.ReadEditTransfer });
            var records = new List<JsonObject> { new() { ["Id"] = "500001" } };
            var result = await builder.BuildAclMapAsync("Case", records, EmptyAclMaps());
            Assert.Equal("everyone", result["500001"][0]["value"]);
        }

        [Fact]
        public async Task MultipleRecordsAllGetSameAcl()
        {
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.Read });
            var records = new List<JsonObject>
            {
                new() { ["Id"] = "001A" },
                new() { ["Id"] = "001B" },
                new() { ["Id"] = "001C" },
            };
            var result = await builder.BuildAclMapAsync("Account", records, EmptyAclMaps());
            Assert.Equal(3, result.Count);
            foreach (var recordId in new[] { "001A", "001B", "001C" })
                Assert.Equal("everyone", result[recordId][0]["value"]);
        }
    }

    // ── PRIVATE OWD tests ────────────────────────────────────────────────────

    public class PrivateOwd
    {
        [Fact]
        public async Task PrivateOwdIncludesGlobalUsersGroup()
        {
            var user1 = MakeSfUser(
                userId: "005U1",
                permissionSets: new List<Dictionary<string, object?>> { new() { ["Id"] = "ps1" } });
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.None },
                users: new List<SfUser> { user1 });
            var records = new List<JsonObject> { new() { ["Id"] = "001X", ["Shares"] = Shares() } };
            var result = await builder.BuildAclMapAsync("Account", records, EmptyAclMaps());
            var acl = result["001X"];
            Assert.Contains(acl, a => a["value"] == "AccountGlobalUsers");
        }

        [Fact]
        public async Task UserShareAddsUserAce()
        {
            var user1 = MakeSfUser(
                userId: "005U1",
                federationId: "f1126041-cb51-4f20-82d5-722b4cfcdfa1",
                permissionSets: new List<Dictionary<string, object?>> { new() { ["Id"] = "ps1" } });
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.None },
                users: new List<SfUser> { user1 });
            var records = new List<JsonObject>
            {
                new() { ["Id"] = "001X", ["Shares"] = Shares(Share("005U1", "User")) },
            };
            var result = await builder.BuildAclMapAsync("Account", records, EmptyAclMaps());
            var acl = result["001X"];
            var userAces = acl.Where(a => a["type"] == "user").ToList();
            Assert.Single(userAces);
            Assert.Equal("f1126041-cb51-4f20-82d5-722b4cfcdfa1", userAces[0]["value"]);
        }

        [Fact]
        public async Task UserShareWithParentRoleAddsRoleGroup()
        {
            var user1 = MakeSfUser(
                userId: "005U1",
                federationId: "f1126041-cb51-4f20-82d5-722b4cfcdfa1",
                parentRoleId: "00E_PARENT",
                permissionSets: new List<Dictionary<string, object?>> { new() { ["Id"] = "ps1" } });
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.None },
                users: new List<SfUser> { user1 });
            var records = new List<JsonObject>
            {
                new() { ["Id"] = "001X", ["Shares"] = Shares(Share("005U1", "User")) },
            };
            var result = await builder.BuildAclMapAsync("Account", records, EmptyAclMaps());
            var acl = result["001X"];
            var groupValues = acl.Where(a => a["type"] == "externalGroup").Select(a => a["value"]).ToList();
            Assert.Contains("Account00EPARENTRole", groupValues);
        }

        [Fact]
        public async Task FrozenUserShareIsSkipped()
        {
            var user1 = MakeSfUser(
                userId: "005FROZEN",
                permissionSets: new List<Dictionary<string, object?>> { new() { ["Id"] = "ps1" } });
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.None },
                users: new List<SfUser> { user1 },
                frozen: new HashSet<string> { "005FROZEN" });
            var records = new List<JsonObject>
            {
                new() { ["Id"] = "001X", ["Shares"] = Shares(Share("005FROZEN", "User")) },
            };
            var result = await builder.BuildAclMapAsync("Account", records, EmptyAclMaps());
            var acl = result["001X"];
            var userAces = acl.Where(a => a["type"] == "user").ToList();
            Assert.Empty(userAces);
        }

        [Fact]
        public async Task UserWithoutPermissionSetsIsSkipped()
        {
            var user1 = MakeSfUser(userId: "005NP", permissionSets: new List<Dictionary<string, object?>>());
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.None },
                users: new List<SfUser> { user1 });
            var records = new List<JsonObject>
            {
                new() { ["Id"] = "001X", ["Shares"] = Shares(Share("005NP", "User")) },
            };
            var result = await builder.BuildAclMapAsync("Account", records, EmptyAclMaps());
            var userAces = result["001X"].Where(a => a["type"] == "user").ToList();
            Assert.Empty(userAces);
        }

        [Fact]
        public async Task GroupShareRoleAddsRoleGroupAce()
        {
            var group = new SfGroup("00G_ROLE_GRP", UserOrGroupType.Role) { RelatedId = "00E_ROLE1" };
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.None },
                users: new List<SfUser>(),
                groups: new List<SfGroup> { group });
            var records = new List<JsonObject>
            {
                new() { ["Id"] = "001X", ["Shares"] = Shares(Share("00G_ROLE_GRP", "Queue")) },
            };
            var result = await builder.BuildAclMapAsync("Account", records, EmptyAclMaps());
            var acl = result["001X"];
            var groupValues = acl.Where(a => a["type"] == "externalGroup").Select(a => a["value"]).ToList();
            Assert.Contains("Account00EROLE1Role", groupValues);
        }

        [Fact]
        public async Task GroupShareOrganizationAddsAllInternalUsers()
        {
            var group = new SfGroup("00G_ORG", UserOrGroupType.Organization);
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.None },
                users: new List<SfUser>(),
                groups: new List<SfGroup> { group });
            var records = new List<JsonObject>
            {
                new() { ["Id"] = "001X", ["Shares"] = Shares(Share("00G_ORG", "Queue")) },
            };
            var result = await builder.BuildAclMapAsync("Account", records, EmptyAclMaps());
            var groupValues = result["001X"].Where(a => a["type"] == "externalGroup").Select(a => a["value"]).ToList();
            Assert.Contains("AccountAllInternalUsers", groupValues);
        }

        [Fact]
        public async Task GroupShareManagerAddsManagerGroup()
        {
            var group = new SfGroup("00G_MGR", UserOrGroupType.Manager) { RelatedId = "005MGR1" };
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.None },
                users: new List<SfUser>(),
                groups: new List<SfGroup> { group });
            var records = new List<JsonObject>
            {
                new() { ["Id"] = "001X", ["Shares"] = Shares(Share("00G_MGR", "Queue")) },
            };
            var result = await builder.BuildAclMapAsync("Account", records, EmptyAclMaps());
            var groupValues = result["001X"].Where(a => a["type"] == "externalGroup").Select(a => a["value"]).ToList();
            Assert.Contains("Account005MGR1Manager", groupValues);
        }

        [Fact]
        public async Task GroupShareRoleAndSubordinates()
        {
            var group = new SfGroup("00G_RAS", UserOrGroupType.RoleAndSubordinates) { RelatedId = "00E_RAS_ROLE" };
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.None },
                users: new List<SfUser>(),
                groups: new List<SfGroup> { group });
            var records = new List<JsonObject>
            {
                new() { ["Id"] = "001X", ["Shares"] = Shares(Share("00G_RAS", "Queue")) },
            };
            var result = await builder.BuildAclMapAsync("Account", records, EmptyAclMaps());
            var groupValues = result["001X"].Where(a => a["type"] == "externalGroup").Select(a => a["value"]).ToList();
            Assert.Contains("Account00ERASROLERoleAndSubordinates", groupValues);
        }

        [Fact]
        public async Task GroupSharePublicGroup()
        {
            var group = new SfGroup("00G_PG", UserOrGroupType.Regular);
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.None },
                users: new List<SfUser>(),
                groups: new List<SfGroup> { group });
            var records = new List<JsonObject>
            {
                new() { ["Id"] = "001X", ["Shares"] = Shares(Share("00G_PG", "Queue")) },
            };
            var result = await builder.BuildAclMapAsync("Account", records, EmptyAclMaps());
            var groupValues = result["001X"].Where(a => a["type"] == "externalGroup").Select(a => a["value"]).ToList();
            Assert.Contains("Account00GPGPublicGroup", groupValues);
        }

        [Fact]
        public async Task NoSharesStillHasGlobalUsers()
        {
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.None },
                users: new List<SfUser>());
            var records = new List<JsonObject> { new() { ["Id"] = "001EMPTY", ["Shares"] = Shares() } };
            var result = await builder.BuildAclMapAsync("Account", records, EmptyAclMaps());
            Assert.Single(result["001EMPTY"]);
            Assert.Equal("AccountGlobalUsers", result["001EMPTY"][0]["value"]);
        }

        [Fact]
        public async Task GroupShareTerritory()
        {
            var group = new SfGroup("00G_TERR", UserOrGroupType.Territory) { RelatedId = "0ML_TERR1" };
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.None },
                users: new List<SfUser>(),
                groups: new List<SfGroup> { group });
            var records = new List<JsonObject>
            {
                new() { ["Id"] = "001X", ["Shares"] = Shares(Share("00G_TERR", "Queue")) },
            };
            var result = await builder.BuildAclMapAsync("Account", records, EmptyAclMaps());
            var groupValues = result["001X"].Where(a => a["type"] == "externalGroup").Select(a => a["value"]).ToList();
            Assert.Contains("Account0MLTERR1Territory", groupValues);
        }

        [Fact]
        public async Task GroupShareTerritoryAndSubordinates()
        {
            var group = new SfGroup("00G_TERR_SUB", UserOrGroupType.TerritoryAndSubordinates)
            {
                RelatedId = "0ML_TERR2",
            };
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.None },
                users: new List<SfUser>(),
                groups: new List<SfGroup> { group });
            var records = new List<JsonObject>
            {
                new() { ["Id"] = "001X", ["Shares"] = Shares(Share("00G_TERR_SUB", "Queue")) },
            };
            var result = await builder.BuildAclMapAsync("Account", records, EmptyAclMaps());
            var groupValues = result["001X"].Where(a => a["type"] == "externalGroup").Select(a => a["value"]).ToList();
            Assert.Contains("Account0MLTERR2TerritoryAndSubordinates", groupValues);
        }

        [Fact]
        public async Task GroupShareTerritoryAndSubordinatesInternal()
        {
            var group = new SfGroup("00G_TERR_INT", UserOrGroupType.TerritoryAndSubordinatesInternal)
            {
                RelatedId = "0ML_TERR3",
            };
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.None },
                users: new List<SfUser>(),
                groups: new List<SfGroup> { group });
            var records = new List<JsonObject>
            {
                new() { ["Id"] = "001X", ["Shares"] = Shares(Share("00G_TERR_INT", "Queue")) },
            };
            var result = await builder.BuildAclMapAsync("Account", records, EmptyAclMaps());
            var groupValues = result["001X"].Where(a => a["type"] == "externalGroup").Select(a => a["value"]).ToList();
            Assert.Contains("Account0MLTERR3TerritoryAndSubordinates", groupValues);
        }
    }

    // ── CONTROLLED BY PARENT tests ───────────────────────────────────────────

    public class ControlledByParent
    {
        /// <summary>If parent object is PUBLIC, child gets grant-everyone ACL.</summary>
        [Fact]
        public async Task ControlledByParentWithPublicParent()
        {
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility>
                {
                    ["Account"] = EntityVisibility.Read,
                    ["Contact"] = EntityVisibility.ControlledByParent,
                },
                parentMap: new Dictionary<string, (string, string)> { ["Contact"] = ("AccountId", "Account") });
            var records = new List<JsonObject> { new() { ["Id"] = "003C1" } };
            var result = await builder.BuildAclMapAsync("Contact", records, EmptyAclMaps());
            Assert.Equal("everyone", result["003C1"][0]["value"]);
        }

        /// <summary>Child inherits parent's full private ACL (GlobalUsers + shares).</summary>
        [Fact]
        public async Task CbpInheritsParentPrivateAcl()
        {
            var owner = MakeSfUser(
                userId: "005OWNER",
                federationId: "a2b3c4d5-e6f7-8901-2345-678901234567");
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility>
                {
                    ["Account"] = EntityVisibility.None,
                    ["Contact"] = EntityVisibility.ControlledByParent,
                },
                users: new List<SfUser> { owner },
                parentMap: new Dictionary<string, (string, string)> { ["Contact"] = ("AccountId", "Account") });
            // Mock SOQL to return parent record
            ((FakeSalesforceClient)builder._sf).QueryAllHandler = _ => new List<JsonObject>
            {
                new() { ["Id"] = "001ACC", ["OwnerId"] = "005OWNER" },
            };

            var contactRecords = new List<JsonObject>
            {
                new() { ["Id"] = "003C1", ["AccountId"] = "001ACC" },
            };

            // Pre-inject shares on parent (simulate _fetch_and_inject_shares)
            builder.FetchAndInjectSharesHandler = (objType, records) =>
            {
                foreach (var r in records)
                    r["Shares"] = Shares(Share("005OWNER", "User"));
                return Task.CompletedTask;
            };

            var result = await builder.BuildAclMapAsync("Contact", contactRecords, EmptyAclMaps());
            var acl = result["003C1"];

            // Should have child's GlobalUsers + parent's GlobalUsers + owner user ACE
            var values = acl.Select(a => a["value"]).ToList();
            Assert.True(values.Contains("ContactGlobalUsers"), "child GlobalUsers missing");
            Assert.True(values.Contains("AccountGlobalUsers"), "parent GlobalUsers missing");
            Assert.True(values.Contains("a2b3c4d5-e6f7-8901-2345-678901234567"), "parent owner ACE missing");
        }

        /// <summary>Contact with no AccountId gets GlobalUsers + owner ACL.</summary>
        [Fact]
        public async Task CbpOrphanNoParentIdGetsOwnerAcl()
        {
            var owner = MakeSfUser(
                userId: "005ORPHAN_OWN",
                federationId: "dddd1111-2222-3333-4444-555566667777");
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility>
                {
                    ["Account"] = EntityVisibility.None,
                    ["Contact"] = EntityVisibility.ControlledByParent,
                },
                users: new List<SfUser> { owner },
                parentMap: new Dictionary<string, (string, string)> { ["Contact"] = ("AccountId", "Account") });
            // No AccountId
            var records = new List<JsonObject> { new() { ["Id"] = "003ORPHAN", ["OwnerId"] = "005ORPHAN_OWN" } };
            var result = await builder.BuildAclMapAsync("Contact", records, EmptyAclMaps());
            var acl = result["003ORPHAN"];
            var values = acl.Select(a => a["value"]).ToList();
            Assert.Contains("ContactGlobalUsers", values);
            Assert.Contains("dddd1111-2222-3333-4444-555566667777", values);
        }

        /// <summary>Contact with no AccountId and no OwnerId gets GlobalUsers only.</summary>
        [Fact]
        public async Task CbpOrphanNoParentNoOwnerGetsGlobalUsersOnly()
        {
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility>
                {
                    ["Account"] = EntityVisibility.None,
                    ["Contact"] = EntityVisibility.ControlledByParent,
                },
                users: new List<SfUser>(),
                parentMap: new Dictionary<string, (string, string)> { ["Contact"] = ("AccountId", "Account") });
            // No AccountId, no OwnerId
            var records = new List<JsonObject> { new() { ["Id"] = "003ORPHAN" } };
            var result = await builder.BuildAclMapAsync("Contact", records, EmptyAclMaps());
            var acl = result["003ORPHAN"];
            Assert.Single(acl);
            Assert.Equal("ContactGlobalUsers", acl[0]["value"]);
        }

        /// <summary>Orphan whose OwnerId isn't in user cache gets GlobalUsers only.</summary>
        [Fact]
        public async Task CbpOrphanOwnerNotInCacheGetsGlobalUsersOnly()
        {
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility>
                {
                    ["Account"] = EntityVisibility.None,
                    ["Contact"] = EntityVisibility.ControlledByParent,
                },
                users: new List<SfUser>(),  // Owner not loaded
                parentMap: new Dictionary<string, (string, string)> { ["Contact"] = ("AccountId", "Account") });
            var records = new List<JsonObject> { new() { ["Id"] = "003X", ["OwnerId"] = "005UNKNOWN" } };
            var result = await builder.BuildAclMapAsync("Contact", records, EmptyAclMaps());
            var acl = result["003X"];
            Assert.Single(acl);
            Assert.Equal("ContactGlobalUsers", acl[0]["value"]);
        }

        /// <summary>Orphan whose owner has no valid AAD identifier gets GlobalUsers only.</summary>
        [Fact]
        public async Task CbpOrphanOwnerUnresolvableGetsGlobalUsersOnly()
        {
            var owner = MakeSfUser(
                userId: "005BAD",
                federationId: "",  // No federation ID
                email: "");
            // Override user_name to be non-email so it's rejected
            owner.UserName = "";
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility>
                {
                    ["Account"] = EntityVisibility.None,
                    ["Contact"] = EntityVisibility.ControlledByParent,
                },
                users: new List<SfUser> { owner },
                parentMap: new Dictionary<string, (string, string)> { ["Contact"] = ("AccountId", "Account") });
            var records = new List<JsonObject> { new() { ["Id"] = "003X", ["OwnerId"] = "005BAD" } };
            var result = await builder.BuildAclMapAsync("Contact", records, EmptyAclMaps());
            var acl = result["003X"];
            Assert.Single(acl);
            Assert.Equal("ContactGlobalUsers", acl[0]["value"]);
        }

        /// <summary>Orphan with principal_mapper resolves owner to AAD GUID.</summary>
        [Fact]
        public async Task CbpOrphanWithPrincipalMapperResolvesOwner()
        {
            var owner = MakeSfUser(
                userId: "005MAP",
                federationId: "user@example.com");
            var mapper = new FakePrincipalMapper
            {
                ResolveIdentifierReturnValue = "eeee1111-2222-3333-4444-555566667777",
            };

            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility>
                {
                    ["Account"] = EntityVisibility.None,
                    ["Contact"] = EntityVisibility.ControlledByParent,
                },
                users: new List<SfUser> { owner },
                parentMap: new Dictionary<string, (string, string)> { ["Contact"] = ("AccountId", "Account") });
            builder._principalMapper = mapper;

            var records = new List<JsonObject> { new() { ["Id"] = "003X", ["OwnerId"] = "005MAP" } };
            var result = await builder.BuildAclMapAsync("Contact", records, EmptyAclMaps());
            var acl = result["003X"];
            var values = acl.Select(a => a["value"]).ToList();
            Assert.Contains("ContactGlobalUsers", values);
            Assert.Contains("eeee1111-2222-3333-4444-555566667777", values);
            Assert.True(mapper.ResolveIdentifierCallCount > 0);  // mapper._resolve_identifier.assert_called()
        }

        /// <summary>CBP object type with no parent_map entry falls back to deny-everyone.</summary>
        [Fact]
        public async Task CbpNoParentMapEntryGetsDenyEveryone()
        {
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility>
                {
                    ["CustomObj__c"] = EntityVisibility.ControlledByParent,
                },
                users: new List<SfUser>(),
                parentMap: new Dictionary<string, (string, string)>());  // No entry for CustomObj__c
            var records = new List<JsonObject> { new() { ["Id"] = "a01001" } };
            var result = await builder.BuildAclMapAsync("CustomObj__c", records, EmptyAclMaps());
            var acl = result["a01001"];
            Assert.Equal("deny", acl[0]["accessType"]);
        }

        /// <summary>Second chunk should reuse cached parent ACLs without re-fetching.</summary>
        [Fact]
        public async Task CbpCacheReuseAcrossChunks()
        {
            var owner = MakeSfUser(
                userId: "005OWNER",
                federationId: "a2b3c4d5-e6f7-8901-2345-678901234567");
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility>
                {
                    ["Account"] = EntityVisibility.None,
                    ["Contact"] = EntityVisibility.ControlledByParent,
                },
                users: new List<SfUser> { owner },
                parentMap: new Dictionary<string, (string, string)> { ["Contact"] = ("AccountId", "Account") });
            var sf = (FakeSalesforceClient)builder._sf;
            sf.QueryAllHandler = _ => new List<JsonObject>
            {
                new() { ["Id"] = "001ACC", ["OwnerId"] = "005OWNER" },
            };

            builder.FetchAndInjectSharesHandler = (objType, records) =>
            {
                foreach (var r in records)
                    r["Shares"] = Shares();
                return Task.CompletedTask;
            };

            // First chunk
            var chunk1 = new List<JsonObject> { new() { ["Id"] = "003C1", ["AccountId"] = "001ACC" } };
            var result1 = await builder.BuildAclMapAsync("Contact", chunk1, EmptyAclMaps());
            Assert.True(result1.ContainsKey("003C1"));

            // Second chunk — same parent, should be cached
            var chunk2 = new List<JsonObject> { new() { ["Id"] = "003C2", ["AccountId"] = "001ACC" } };
            var result2 = await builder.BuildAclMapAsync("Contact", chunk2, EmptyAclMaps());
            Assert.True(result2.ContainsKey("003C2"));

            // query_all should only have been called once (for the first chunk's parent fetch)
            Assert.Equal(1, sf.QueryAllCallCount);
            // _fetch_and_inject_shares called only once (for parent shares in first chunk)
            Assert.Equal(1, builder.FetchAndInjectSharesCallCount);
        }

        /// <summary>Parent role/group shares are inherited by child.</summary>
        [Fact]
        public async Task CbpParentWithGroupShares()
        {
            var group = new SfGroup("00G_ROLE_GRP", UserOrGroupType.Role) { RelatedId = "00E_ROLE1" };
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility>
                {
                    ["Account"] = EntityVisibility.None,
                    ["Contact"] = EntityVisibility.ControlledByParent,
                },
                users: new List<SfUser>(),
                groups: new List<SfGroup> { group },
                parentMap: new Dictionary<string, (string, string)> { ["Contact"] = ("AccountId", "Account") });
            ((FakeSalesforceClient)builder._sf).QueryAllHandler = _ => new List<JsonObject>
            {
                new() { ["Id"] = "001ACC", ["OwnerId"] = "" },
            };

            builder.FetchAndInjectSharesHandler = (objType, records) =>
            {
                foreach (var r in records)
                    r["Shares"] = Shares(Share("00G_ROLE_GRP", "Queue"));
                return Task.CompletedTask;
            };

            var records2 = new List<JsonObject> { new() { ["Id"] = "003C1", ["AccountId"] = "001ACC" } };
            var result = await builder.BuildAclMapAsync("Contact", records2, EmptyAclMaps());
            var acl = result["003C1"];

            // Parent's role group should be in child's ACL
            var values = acl.Where(a => a["type"] == "externalGroup").Select(a => a["value"]).ToList();
            Assert.True(values.Contains("ContactGlobalUsers"), "child GlobalUsers");
            Assert.True(values.Contains("Account00EROLE1Role"), "parent role group inherited");
        }

        /// <summary>Two contacts pointing to different accounts get different ACLs.</summary>
        [Fact]
        public async Task CbpMultipleChildrenDifferentParents()
        {
            var ownerA = MakeSfUser(userId: "005A", federationId: "aaaa1111-2222-3333-4444-555566667777");
            var ownerB = MakeSfUser(userId: "005B", federationId: "bbbb1111-2222-3333-4444-555566667777");
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility>
                {
                    ["Account"] = EntityVisibility.None,
                    ["Contact"] = EntityVisibility.ControlledByParent,
                },
                users: new List<SfUser> { ownerA, ownerB },
                parentMap: new Dictionary<string, (string, string)> { ["Contact"] = ("AccountId", "Account") });
            ((FakeSalesforceClient)builder._sf).QueryAllHandler = _ => new List<JsonObject>
            {
                new() { ["Id"] = "001A", ["OwnerId"] = "005A" },
                new() { ["Id"] = "001B", ["OwnerId"] = "005B" },
            };

            builder.FetchAndInjectSharesHandler = (objType, records) =>
            {
                foreach (var r in records)
                {
                    var ownerId = (string?)r["OwnerId"] ?? "";
                    r["Shares"] = string.IsNullOrEmpty(ownerId)
                        ? Shares()
                        : Shares(Share(ownerId, "User"));
                }
                return Task.CompletedTask;
            };

            var records2 = new List<JsonObject>
            {
                new() { ["Id"] = "003C1", ["AccountId"] = "001A" },
                new() { ["Id"] = "003C2", ["AccountId"] = "001B" },
            };
            var result = await builder.BuildAclMapAsync("Contact", records2, EmptyAclMaps());

            // Contact 1 should have owner A's GUID
            var valsC1 = result["003C1"].Select(a => a["value"]).ToList();
            Assert.Contains("aaaa1111-2222-3333-4444-555566667777", valsC1);
            Assert.DoesNotContain("bbbb1111-2222-3333-4444-555566667777", valsC1);

            // Contact 2 should have owner B's GUID
            var valsC2 = result["003C2"].Select(a => a["value"]).ToList();
            Assert.Contains("bbbb1111-2222-3333-4444-555566667777", valsC2);
            Assert.DoesNotContain("aaaa1111-2222-3333-4444-555566667777", valsC2);
        }

        /// <summary>Child pointing to a parent that SOQL can't find gets deny-everyone.</summary>
        [Fact]
        public async Task CbpParentDeletedGetsDenyEveryone()
        {
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility>
                {
                    ["Account"] = EntityVisibility.None,
                    ["Contact"] = EntityVisibility.ControlledByParent,
                },
                users: new List<SfUser>(),
                parentMap: new Dictionary<string, (string, string)> { ["Contact"] = ("AccountId", "Account") });
            // SOQL returns empty — parent was deleted
            ((FakeSalesforceClient)builder._sf).QueryAllHandler = _ => new List<JsonObject>();

            builder.FetchAndInjectSharesHandler = (objType, records) =>
            {
                foreach (var r in records)
                    r["Shares"] = Shares();
                return Task.CompletedTask;
            };

            var records2 = new List<JsonObject> { new() { ["Id"] = "003C1", ["AccountId"] = "001DELETED" } };
            var result = await builder.BuildAclMapAsync("Contact", records2, EmptyAclMaps());
            var acl = result["003C1"];
            Assert.Equal("deny", acl[0]["accessType"]);
            Assert.Equal("everyone", acl[0]["value"]);
        }

        /// <summary>Chunk with both valid parent refs and orphans: each gets correct ACL.</summary>
        [Fact]
        public async Task CbpMixedOrphansAndValidInSameChunk()
        {
            var owner = MakeSfUser(
                userId: "005OWN",
                federationId: "cccc1111-2222-3333-4444-555566667777");
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility>
                {
                    ["Account"] = EntityVisibility.None,
                    ["Contact"] = EntityVisibility.ControlledByParent,
                },
                users: new List<SfUser> { owner },
                parentMap: new Dictionary<string, (string, string)> { ["Contact"] = ("AccountId", "Account") });
            ((FakeSalesforceClient)builder._sf).QueryAllHandler = _ => new List<JsonObject>
            {
                new() { ["Id"] = "001ACC", ["OwnerId"] = "005OWN" },
            };

            builder.FetchAndInjectSharesHandler = (objType, records) =>
            {
                foreach (var r in records)
                {
                    var ownerId = (string?)r["OwnerId"] ?? "";
                    r["Shares"] = string.IsNullOrEmpty(ownerId)
                        ? Shares()
                        : Shares(Share(ownerId, "User"));
                }
                return Task.CompletedTask;
            };

            var records2 = new List<JsonObject>
            {
                new() { ["Id"] = "003VALID", ["AccountId"] = "001ACC" },
                new() { ["Id"] = "003ORPHAN" },  // No AccountId
            };
            var result = await builder.BuildAclMapAsync("Contact", records2, EmptyAclMaps());

            // Valid child inherits parent ACL
            var validVals = result["003VALID"].Select(a => a["value"]).ToList();
            Assert.Contains("ContactGlobalUsers", validVals);
            Assert.Contains("cccc1111-2222-3333-4444-555566667777", validVals);

            // Orphan gets owner-based ACL (GlobalUsers only since no OwnerId)
            var orphanAcl = result["003ORPHAN"];
            Assert.Equal("ContactGlobalUsers", orphanAcl[0]["value"]);
        }

        /// <summary>If parent SOQL query fails, affected children get deny-everyone.</summary>
        [Fact]
        public async Task CbpParentFetchSoqlFailureDenyEveryone()
        {
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility>
                {
                    ["Account"] = EntityVisibility.None,
                    ["Contact"] = EntityVisibility.ControlledByParent,
                },
                users: new List<SfUser>(),
                parentMap: new Dictionary<string, (string, string)> { ["Contact"] = ("AccountId", "Account") });
            // SOQL raises exception
            ((FakeSalesforceClient)builder._sf).QueryAllHandler = _ => throw new Exception("SOQL timeout");

            builder.FetchAndInjectSharesHandler = (objType, records) =>
            {
                foreach (var r in records)
                    r["Shares"] = Shares();
                return Task.CompletedTask;
            };

            var records2 = new List<JsonObject> { new() { ["Id"] = "003C1", ["AccountId"] = "001ACC" } };
            var result = await builder.BuildAclMapAsync("Contact", records2, EmptyAclMaps());
            var acl = result["003C1"];
            Assert.Equal("deny", acl[0]["accessType"]);
            Assert.Equal("everyone", acl[0]["value"]);
        }
    }

    // ── OWD override tests ──────────────────────────────────────────────────

    public class OwdOverrides
    {
        [Fact]
        public async Task OwdOverrideChangesVisibility()
        {
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.Read });
            // Override to private
            builder._owdMap!["Account"] = EntityVisibility.None;
            builder._usersById = new Dictionary<string, SfUser>();
            builder._frozenUsers = new HashSet<string>();

            var records = new List<JsonObject> { new() { ["Id"] = "001X", ["Shares"] = Shares() } };
            var result = await builder.BuildAclMapAsync("Account", records, EmptyAclMaps());
            // Should get GlobalUsers (private) not TopLevel (public)
            Assert.Equal("AccountGlobalUsers", result["001X"][0]["value"]);
        }
    }

    // ── Group ID consistency test ────────────────────────────────────────────

    /// <summary>Verify that group IDs used in ACLs match the format constants.</summary>
    public class GroupIdConsistency
    {
        [Fact]
        public async Task PublicAclUsesGrantEveryone()
        {
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.Read });
            var records = new List<JsonObject> { new() { ["Id"] = "001X" } };
            var result = await builder.BuildAclMapAsync("Account", records, EmptyAclMaps());
            Assert.Equal(
                new Dictionary<string, string>
                {
                    ["accessType"] = "grant",
                    ["type"] = "everyone",
                    ["value"] = "everyone",
                },
                result["001X"][0]);
        }

        [Fact]
        public async Task PrivateAclUsesGlobalUsersFormat()
        {
            var expected = SfGroupIdFormats.GlobalUsers.Format("Account");
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.None },
                users: new List<SfUser>());
            var records = new List<JsonObject> { new() { ["Id"] = "001X", ["Shares"] = Shares() } };
            var result = await builder.BuildAclMapAsync("Account", records, EmptyAclMaps());
            Assert.Equal(expected, result["001X"][0]["value"]);
        }

        [Fact]
        public async Task RoleGroupIdMatchesFormat()
        {
            var expected = SfGroupIdFormats.Role.Format("Account", "00E_ROLE1");
            var group = new SfGroup("00G1", UserOrGroupType.Role) { RelatedId = "00E_ROLE1" };
            var builder = MakeBuilder(
                owdMap: new Dictionary<string, EntityVisibility> { ["Account"] = EntityVisibility.None },
                users: new List<SfUser>(),
                groups: new List<SfGroup> { group });
            var records = new List<JsonObject>
            {
                new() { ["Id"] = "001X", ["Shares"] = Shares(Share("00G1", "Queue")) },
            };
            var result = await builder.BuildAclMapAsync("Account", records, EmptyAclMaps());
            var groupValues = result["001X"].Where(a => a["type"] == "externalGroup").Select(a => a["value"]).ToList();
            Assert.Contains(expected, groupValues);
        }
    }
}
