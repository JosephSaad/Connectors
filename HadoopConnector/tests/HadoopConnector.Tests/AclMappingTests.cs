using System.Text.Json.Nodes;
using HadoopConnector.AclEngine;
using HadoopConnector.Hdfs;
using HadoopConnector.Config;
using HadoopConnector.Graph;

namespace HadoopConnector.Tests;

public class AclMappingTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly IdentityStore _store;

    public AclMappingTests()
    {
        _store = new IdentityStore("AclTests", Path.Combine(_dir.Path, "identity.db"));
        _store.Upsert(new PrincipalMapping("005000000000001", "user", "a@example.com", "entra-user-1", DateTime.UtcNow));
        _store.Upsert(new PrincipalMapping("005000000000002", "user", "b@example.com", "entra-user-2", DateTime.UtcNow));
        _store.Upsert(new PrincipalMapping("005000000000003", "user", "c@example.com", null, DateTime.UtcNow));
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    private PrincipalMapper Mapper() => new(_store);

    private static BdhRecord Record(
        string objectType, string id, string? ownerId = null, string? ownerEmail = null)
    {
        var fields = new JsonObject { ["Id"] = id };
        if (ownerId is not null)
            fields["OwnerId"] = ownerId;
        if (ownerEmail is not null)
            fields["OwnerEmail"] = ownerEmail;
        return new BdhRecord(objectType, fields);
    }

    // ── PrincipalMapper ──────────────────────────────────────────────────────

    [Fact]
    public void MapOwnerId_ResolvedAndUnresolved()
    {
        var mapper = Mapper();
        Assert.Equal("entra-user-1", mapper.MapOwnerId("005000000000001"));
        Assert.Null(mapper.MapOwnerId("005000000000003"));   // known, unresolved
        Assert.Null(mapper.MapOwnerId("005000000000404"));   // unknown
        Assert.Equal(2, mapper.UnmappedOwners);
    }

    [Fact]
    public void MapOwnerEmail_UsesEmailIndex()
    {
        var mapper = Mapper();
        Assert.Equal("entra-user-2", mapper.MapOwnerEmail("b@example.com"));
        Assert.Equal("entra-user-2", mapper.MapOwnerEmail("B@EXAMPLE.COM"));  // case-insensitive
        Assert.Null(mapper.MapOwnerEmail("nobody@example.com"));
        Assert.Equal(1, mapper.UnmappedOwners);
    }

    [Fact]
    public void ResolveOwner_IdMappingWins_EmailIsFallback()
    {
        var mapper = Mapper();
        // Id resolves → the (different) email mapping is not consulted.
        Assert.Equal("entra-user-1", mapper.ResolveOwner("005000000000001", "b@example.com"));
        // Id unknown → email index resolves.
        Assert.Equal("entra-user-2", mapper.ResolveOwner("005000000000404", "b@example.com"));
        // Unresolved Entra id on the id mapping → email still saves the day.
        Assert.Equal("entra-user-2", mapper.ResolveOwner("005000000000003", "b@example.com"));
    }

    [Fact]
    public void ResolveOwner_NeitherResolves_CountedOnce()
    {
        var mapper = Mapper();
        Assert.Null(mapper.ResolveOwner("005000000000404", "nobody@example.com"));
        Assert.Equal(1, mapper.UnmappedOwners);
        // Both blank → nothing to resolve, nothing counted.
        Assert.Null(mapper.ResolveOwner(null, null));
        Assert.Null(mapper.ResolveOwner("", " "));
        Assert.Equal(1, mapper.UnmappedOwners);
    }

    // ── AclResolver ──────────────────────────────────────────────────────────

    [Fact]
    public void PublicMode_GrantsEveryoneExceptGuests()
    {
        var resolver = new AclResolver(Mapper(), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
        var config = new ObjectConfig { ObjectName = "Account", AclMode = "public" };
        var acl = resolver.Resolve(Record("Account", "001000000000005"), config);

        var entry = Assert.Single(acl);
        Assert.Equal(AclEntryType.EveryoneExceptGuests, entry.Type);
        Assert.Equal(AclAccessType.Grant, entry.AccessType);
    }

    [Fact]
    public void GroupMode_GrantsConfiguredGroup()
    {
        var resolver = new AclResolver(Mapper(), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
        var config = new ObjectConfig
        {
            ObjectName = "Account", AclMode = "group", AclGroupId = "entra-group-sales",
        };
        var acl = resolver.Resolve(Record("Account", "001000000000005"), config);

        var entry = Assert.Single(acl);
        Assert.Equal(AclEntryType.Group, entry.Type);
        Assert.Equal("entra-group-sales", entry.Value);
    }

    [Fact]
    public void GroupMode_MissingGroupId_ReturnsEmpty_NeverWide()
    {
        // Belt-and-braces: schema validation rejects this shape up front, but at
        // runtime a group-mode object with no group id must yield an EMPTY acl
        // (item skipped) — never a wider grant, not even the fallback group.
        var resolver = new AclResolver(
            Mapper(), adminGroupId: "entra-admins", fallbackGroupId: "entra-fallback");
        var config = new ObjectConfig { ObjectName = "Account", AclMode = "group", AclGroupId = "" };
        Assert.Empty(resolver.Resolve(Record("Account", "001000000000005"), config));
    }

    [Fact]
    public void OwnerOnlyMode_GrantsOwnerOnly()
    {
        var resolver = new AclResolver(Mapper(), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
        var config = new ObjectConfig { ObjectName = "Contact", AclMode = "ownerOnly" };
        var record = Record("Contact", "003000000000007", ownerId: "005000000000001");

        var acl = resolver.Resolve(record, config);
        var entry = Assert.Single(acl);
        Assert.Equal(AclEntryType.User, entry.Type);
        Assert.Equal("entra-user-1", entry.Value);
    }

    [Fact]
    public void OwnerOnlyMode_EmailFallback_WhenIdUnmapped()
    {
        var resolver = new AclResolver(Mapper(), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
        var config = new ObjectConfig { ObjectName = "Contact", AclMode = "ownerOnly" };
        var record = Record(
            "Contact", "003000000000008", ownerId: "005000000000404", ownerEmail: "b@example.com");

        var acl = resolver.Resolve(record, config);
        var entry = Assert.Single(acl);
        Assert.Equal("entra-user-2", entry.Value);
    }

    [Fact]
    public void OwnerOnlyMode_CustomOwnerFields_AreHonoured()
    {
        var resolver = new AclResolver(Mapper(), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
        var config = new ObjectConfig
        {
            ObjectName = "Case", AclMode = "ownerOnly",
            OwnerField = "AssignedTo", OwnerEmailField = "AssignedToEmail",
        };
        var record = new BdhRecord("Case", new JsonObject
        {
            ["Id"] = "500000000000009",
            ["AssignedTo"] = "005000000000001",
            ["OwnerId"] = "005000000000404",   // default field must NOT be used
        });

        var acl = resolver.Resolve(record, config);
        var entry = Assert.Single(acl);
        Assert.Equal("entra-user-1", entry.Value);
    }

    [Fact]
    public void AdminGroup_AppendedToResolvedAcl_Only()
    {
        var resolver = new AclResolver(Mapper(), adminGroupId: "entra-admins", fallbackGroupId: string.Empty);
        var config = new ObjectConfig { ObjectName = "Contact", AclMode = "ownerOnly" };

        var resolved = resolver.Resolve(
            Record("Contact", "003000000000010", ownerId: "005000000000001"), config);
        Assert.Contains(resolved, e => e.Type == AclEntryType.Group && e.Value == "entra-admins");

        // Unmapped owner with no fallback → the admin grant alone is NOT enough:
        // the acl stays empty and the item is skipped.
        var empty = resolver.Resolve(
            Record("Contact", "003000000000011", ownerId: "005000000000404"), config);
        Assert.Empty(empty);
    }

    [Fact]
    public void AdminGroup_NotDuplicated_WhenAlsoTheAclGroup()
    {
        var resolver = new AclResolver(Mapper(), adminGroupId: "entra-group-sales", fallbackGroupId: string.Empty);
        var config = new ObjectConfig
        {
            ObjectName = "Account", AclMode = "group", AclGroupId = "entra-group-sales",
        };
        var acl = resolver.Resolve(Record("Account", "001000000000012"), config);
        Assert.Single(acl);
    }

    [Fact]
    public void NoPrincipals_UsesFallbackGroup_WhenConfigured()
    {
        var resolver = new AclResolver(Mapper(), adminGroupId: string.Empty, fallbackGroupId: "entra-fallback");
        var config = new ObjectConfig { ObjectName = "Contact", AclMode = "ownerOnly" };
        var acl = resolver.Resolve(Record("Contact", "003000000000013"), config);
        var entry = Assert.Single(acl);
        Assert.Equal("entra-fallback", entry.Value);
    }

    [Fact]
    public void Fallback_NeverWidens_AResolvedAcl()
    {
        // The fallback applies only when NOTHING resolved — a resolved owner
        // grant must not additionally receive the fallback group.
        var resolver = new AclResolver(Mapper(), adminGroupId: string.Empty, fallbackGroupId: "entra-fallback");
        var config = new ObjectConfig { ObjectName = "Contact", AclMode = "ownerOnly" };
        var acl = resolver.Resolve(
            Record("Contact", "003000000000014", ownerId: "005000000000001"), config);

        var entry = Assert.Single(acl);
        Assert.Equal("entra-user-1", entry.Value);
        Assert.DoesNotContain(acl, e => e.Value == "entra-fallback");
    }

    [Fact]
    public void Fallback_PlusAdmin_AppendsBoth_DedupedWhenEqual()
    {
        var resolver = new AclResolver(Mapper(), adminGroupId: "entra-admins", fallbackGroupId: "entra-fallback");
        var config = new ObjectConfig { ObjectName = "Contact", AclMode = "ownerOnly" };
        var acl = resolver.Resolve(Record("Contact", "003000000000015"), config);
        Assert.Equal(2, acl.Count);
        Assert.Contains(acl, e => e.Value == "entra-fallback");
        Assert.Contains(acl, e => e.Value == "entra-admins");

        var same = new AclResolver(Mapper(), adminGroupId: "entra-shared", fallbackGroupId: "entra-shared");
        var deduped = same.Resolve(Record("Contact", "003000000000016"), config);
        Assert.Single(deduped);
    }

    [Fact]
    public void NoPrincipals_NoFallback_ReturnsEmptyAcl()
    {
        var resolver = new AclResolver(Mapper(), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
        var config = new ObjectConfig { ObjectName = "Contact", AclMode = "ownerOnly" };
        Assert.Empty(resolver.Resolve(Record("Contact", "003000000000017"), config));
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
