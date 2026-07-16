// ACL mapping: Seismic principals → Entra grants, fallback policy, dedupe.

using SeismicConnector.Graph;
using SeismicConnector.Seismic;

namespace SeismicConnector.Tests;

public class AclMapperTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteIdentityStore _store;

    public AclMapperTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "seismic-acl-" + Guid.NewGuid().ToString("N") + ".db");
        _store = new SqliteIdentityStore(_dbPath);
        _store.UpsertPrincipal(new PrincipalMapping("su-1", "user", "amy@contoso.com", "entra-user-1", "Amy"));
        _store.UpsertPrincipal(new PrincipalMapping("amy@contoso.com", "user", "amy@contoso.com", "entra-user-1", "Amy"));
        _store.UpsertPrincipal(new PrincipalMapping("sg-1", "group", null, "entra-group-1", "Sales"));
        _store.UpsertPrincipal(new PrincipalMapping("su-unmapped", "user", "ghost@contoso.com", null, "Ghost"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbPath);
        }
        catch
        {
        }
    }

    private AclMapper Mapper(string fallback = "skip") => new(_store, fallback, "tenant-guid");

    [Fact]
    public void MappedUserAndGroup_BecomeGrants()
    {
        var result = Mapper().Resolve(new List<SeismicPermission>
        {
            new() { PrincipalId = "su-1", PrincipalType = "user" },
            new() { PrincipalId = "sg-1", PrincipalType = "group" },
        });

        Assert.False(result.Skipped);
        Assert.Equal(2, result.Entries.Count);
        Assert.Contains(result.Entries, e => e is { Type: "user", Value: "entra-user-1", AccessType: "grant" });
        Assert.Contains(result.Entries, e => e is { Type: "group", Value: "entra-group-1", AccessType: "grant" });
        Assert.Equal(0, result.UnmappedPrincipals);
    }

    [Fact]
    public void UnknownUser_FallsBackToEmailRow()
    {
        var result = Mapper().Resolve(new List<SeismicPermission>
        {
            new() { PrincipalId = "unknown-id", PrincipalType = "user", Email = "AMY@contoso.com" },
        });
        Assert.Single(result.Entries);
        Assert.Equal("entra-user-1", result.Entries[0].Value);
    }

    [Fact]
    public void UnmappedPrincipals_AreCountedAndDropped()
    {
        var result = Mapper().Resolve(new List<SeismicPermission>
        {
            new() { PrincipalId = "su-1", PrincipalType = "user" },
            new() { PrincipalId = "su-unmapped", PrincipalType = "user" },
        });
        Assert.Single(result.Entries);
        Assert.Equal(1, result.UnmappedPrincipals);
        Assert.False(result.Skipped);
    }

    [Fact]
    public void NothingMapped_FallbackSkip_SkipsItem()
    {
        var result = Mapper("skip").Resolve(new List<SeismicPermission>
        {
            new() { PrincipalId = "su-unmapped", PrincipalType = "user" },
        });
        Assert.True(result.Skipped);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void NothingMapped_FallbackTenant_GrantsEveryone()
    {
        var result = Mapper("tenant").Resolve(new List<SeismicPermission>
        {
            new() { PrincipalId = "su-unmapped", PrincipalType = "user" },
        });
        Assert.False(result.Skipped);
        var entry = Assert.Single(result.Entries);
        Assert.Equal("everyone", entry.Type);
        Assert.Equal("tenant-guid", entry.Value);
    }

    [Fact]
    public void ItemPermissions_WinOverTeamsitePermissions()
    {
        var result = Mapper().Resolve(
            itemPermissions: new List<SeismicPermission>
            {
                new() { PrincipalId = "su-1", PrincipalType = "user" },
            },
            teamsitePermissions: new List<SeismicPermission>
            {
                new() { PrincipalId = "sg-1", PrincipalType = "group" },
            });
        var entry = Assert.Single(result.Entries);
        Assert.Equal("user", entry.Type);
    }

    [Fact]
    public void TeamsitePermissions_ApplyWhenItemHasNone()
    {
        var result = Mapper().Resolve(
            itemPermissions: new List<SeismicPermission>(),
            teamsitePermissions: new List<SeismicPermission>
            {
                new() { PrincipalId = "sg-1", PrincipalType = "group" },
            });
        var entry = Assert.Single(result.Entries);
        Assert.Equal("group", entry.Type);
    }

    [Fact]
    public void DuplicatePrincipals_AreDeduplicated()
    {
        var result = Mapper().Resolve(new List<SeismicPermission>
        {
            new() { PrincipalId = "su-1", PrincipalType = "user", Permission = "view" },
            new() { PrincipalId = "su-1", PrincipalType = "user", Permission = "edit" },
        });
        Assert.Single(result.Entries);
    }

    [Fact]
    public void ToJsonArray_ProducesGraphAclShape()
    {
        var result = Mapper().Resolve(new List<SeismicPermission>
        {
            new() { PrincipalId = "sg-1", PrincipalType = "group" },
        });
        var json = result.ToJsonArray();
        Assert.Equal("group", json[0]!["type"]?.GetValue<string>());
        Assert.Equal("entra-group-1", json[0]!["value"]?.GetValue<string>());
        Assert.Equal("grant", json[0]!["accessType"]?.GetValue<string>());
    }
}
