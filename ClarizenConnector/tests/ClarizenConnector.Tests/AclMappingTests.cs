using System.Text.Json.Nodes;
using ClarizenConnector.AclEngine;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;

namespace ClarizenConnector.Tests;

public class AclMappingTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly IdentityStore _store;

    public AclMappingTests()
    {
        _store = new IdentityStore("AclTests", Path.Combine(_dir.Path, "identity.db"));
        _store.Upsert(new PrincipalMapping("/User/1", "user", "a@example.com", "entra-user-1", DateTime.UtcNow));
        _store.Upsert(new PrincipalMapping("/User/2", "user", "b@example.com", "entra-user-2", DateTime.UtcNow));
        _store.Upsert(new PrincipalMapping("/User/3", "user", "c@example.com", null, DateTime.UtcNow));
        _store.Upsert(new PrincipalMapping("/Group/10", "group", null, "entra-group-10", DateTime.UtcNow));
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    private static DirectorySnapshot Snapshot()
    {
        var snapshot = new DirectorySnapshot();
        snapshot.Users["/User/1"] = new ClarizenUser("/User/1", "a@example.com", "A");
        snapshot.Users["/User/2"] = new ClarizenUser("/User/2", "b@example.com", "B");
        snapshot.Users["/User/3"] = new ClarizenUser("/User/3", "c@example.com", "C");
        snapshot.Groups["/Group/10"] = new ClarizenGroup("/Group/10", "Mapped Group",
            new List<string> { "/User/1" });
        snapshot.Groups["/Group/11"] = new ClarizenGroup("/Group/11", "Unmapped Group",
            new List<string> { "/User/1", "/User/2", "/User/3" });
        snapshot.AddProjectMember("/Project/100", "/User/2");
        snapshot.AddProjectMember("/Project/100", "/Group/10");
        return snapshot;
    }

    private static ClarizenRecord Record(string objectType, string id, params (string Field, string RefId)[] refs)
    {
        var fields = new JsonObject { ["id"] = id };
        foreach (var (field, refId) in refs)
            fields[field] = new JsonObject { ["id"] = refId };
        return new ClarizenRecord(objectType, fields);
    }

    // ── PrincipalMapper ──────────────────────────────────────────────────────

    [Fact]
    public void MapUser_ResolvedAndUnresolved()
    {
        var mapper = new PrincipalMapper(_store, groupMappingJson: "{}");
        Assert.Equal("entra-user-1", mapper.MapUser("/User/1"));
        Assert.Null(mapper.MapUser("/User/3"));       // known, unresolved
        Assert.Null(mapper.MapUser("/User/404"));     // unknown
        Assert.Equal(2, mapper.UnmappedUsers);
    }

    [Fact]
    public void MapGroup_OverrideWinsOverStore()
    {
        var mapper = new PrincipalMapper(
            _store, groupMappingJson: """{"/Group/10": "override-group"}""");
        Assert.Equal("override-group", mapper.MapGroup("/Group/10"));
    }

    [Fact]
    public void MapGroup_FallsBackToStore()
    {
        var mapper = new PrincipalMapper(_store, groupMappingJson: "{}");
        Assert.Equal("entra-group-10", mapper.MapGroup("/Group/10"));
        Assert.Null(mapper.MapGroup("/Group/11"));
        Assert.Equal(1, mapper.UnmappedGroups);
    }

    [Fact]
    public void ParseGroupMapping_InvalidJson_IsEmpty()
    {
        Assert.Empty(PrincipalMapper.ParseGroupMapping("{broken"));
        Assert.Empty(PrincipalMapper.ParseGroupMapping(null));
    }

    [Fact]
    public void MapPrincipals_UnmappedGroupExpandsToMemberUsers()
    {
        var mapper = new PrincipalMapper(_store, groupMappingJson: "{}");
        var (users, groups) = mapper.MapPrincipals(new[] { "/Group/11" }, Snapshot());
        // /User/3 has no Entra id → only users 1 and 2.
        Assert.Equal(new[] { "entra-user-1", "entra-user-2" }, users.OrderBy(u => u).ToArray());
        Assert.Empty(groups);
    }

    [Fact]
    public void MapPrincipals_MappedGroupStaysGroup_AndDeduplicates()
    {
        var mapper = new PrincipalMapper(_store, groupMappingJson: "{}");
        var (users, groups) = mapper.MapPrincipals(
            new[] { "/Group/10", "/User/1", "/User/1" }, Snapshot());
        Assert.Equal(new[] { "entra-user-1" }, users);
        Assert.Equal(new[] { "entra-group-10" }, groups);
    }

    // ── AclResolver ──────────────────────────────────────────────────────────

    [Fact]
    public void PublicMode_GrantsEveryoneExceptGuests()
    {
        var resolver = new AclResolver(
            new PrincipalMapper(_store, "{}"), Snapshot(), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
        var config = new ObjectConfig { ObjectName = "Discussion", AclMode = "public" };
        var acl = resolver.Resolve(Record("Discussion", "/Discussion/5"), config);

        var entry = Assert.Single(acl);
        Assert.Equal(AclEntryType.EveryoneExceptGuests, entry.Type);
        Assert.Equal(AclAccessType.Grant, entry.AccessType);
    }

    [Fact]
    public void OwnerOnlyMode_GrantsOwnerFieldsOnly()
    {
        var resolver = new AclResolver(
            new PrincipalMapper(_store, "{}"), Snapshot(), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
        var config = new ObjectConfig
        {
            ObjectName = "Timesheet", AclMode = "ownerOnly", ProjectField = "WorkItem",
        };
        var record = Record("Timesheet", "/Timesheet/7",
            ("ReportedBy", "/User/1"), ("WorkItem", "/Project/100"));

        var acl = resolver.Resolve(record, config);
        var entry = Assert.Single(acl);
        Assert.Equal(AclEntryType.User, entry.Type);
        Assert.Equal("entra-user-1", entry.Value);
        // Project members (/User/2) must NOT be granted in ownerOnly mode.
        Assert.DoesNotContain(acl, e => e.Value == "entra-user-2");
    }

    [Fact]
    public void ProjectMembersMode_AddsProjectTeam()
    {
        var resolver = new AclResolver(
            new PrincipalMapper(_store, "{}"), Snapshot(), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
        var config = new ObjectConfig
        {
            ObjectName = "Task", AclMode = "projectMembers", ProjectField = "Project",
        };
        var record = Record("Task", "/Task/55",
            ("Owner", "/User/1"), ("Project", "/Project/100"));

        var acl = resolver.Resolve(record, config);
        Assert.Contains(acl, e => e.Type == AclEntryType.User && e.Value == "entra-user-1");   // owner
        Assert.Contains(acl, e => e.Type == AclEntryType.User && e.Value == "entra-user-2");   // member
        Assert.Contains(acl, e => e.Type == AclEntryType.Group && e.Value == "entra-group-10"); // member group
        Assert.All(acl, e => Assert.Equal(AclAccessType.Grant, e.AccessType));
    }

    [Fact]
    public void ProjectMembersMode_RecordIsProjectItself()
    {
        var resolver = new AclResolver(
            new PrincipalMapper(_store, "{}"), Snapshot(), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
        var config = new ObjectConfig
        {
            ObjectName = "Project", AclMode = "projectMembers", ProjectField = "",
        };
        var acl = resolver.Resolve(Record("Project", "/Project/100"), config);
        Assert.Contains(acl, e => e.Value == "entra-user-2");
    }

    [Fact]
    public void AdminGroup_IsAppended()
    {
        var resolver = new AclResolver(
            new PrincipalMapper(_store, "{}"), Snapshot(),
            adminGroupId: "entra-admins", fallbackGroupId: string.Empty);
        var config = new ObjectConfig { ObjectName = "Task", AclMode = "ownerOnly" };
        var acl = resolver.Resolve(Record("Task", "/Task/1", ("Owner", "/User/1")), config);
        Assert.Contains(acl, e => e.Type == AclEntryType.Group && e.Value == "entra-admins");
    }

    [Fact]
    public void NoPrincipals_UsesFallbackGroup_WhenConfigured()
    {
        var resolver = new AclResolver(
            new PrincipalMapper(_store, "{}"), Snapshot(),
            adminGroupId: string.Empty, fallbackGroupId: "entra-fallback");
        var config = new ObjectConfig { ObjectName = "Task", AclMode = "ownerOnly" };
        var acl = resolver.Resolve(Record("Task", "/Task/1"), config);
        var entry = Assert.Single(acl);
        Assert.Equal("entra-fallback", entry.Value);
    }

    [Fact]
    public void NoPrincipals_NoFallback_ReturnsEmptyAcl()
    {
        var resolver = new AclResolver(
            new PrincipalMapper(_store, "{}"), Snapshot(),
            adminGroupId: string.Empty, fallbackGroupId: string.Empty);
        var config = new ObjectConfig { ObjectName = "Task", AclMode = "ownerOnly" };
        Assert.Empty(resolver.Resolve(Record("Task", "/Task/1"), config));
    }

    [Fact]
    public void AclEntry_SerializesToGraphShape()
    {
        var json = new AclEntry(AclEntryType.Group, "gid", AclAccessType.Deny).ToJson();
        Assert.Equal("group", json["type"]!.GetValue<string>());
        Assert.Equal("gid", json["value"]!.GetValue<string>());
        Assert.Equal("deny", json["accessType"]!.GetValue<string>());
    }
}
