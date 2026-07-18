using HadoopConnector.Graph;

namespace HadoopConnector.Tests;

public class IdentityStoreTests
{
    private static IdentityStore Open(TempDir dir) =>
        new("TestConnector", Path.Combine(dir.Path, "identity.db"));

    [Fact]
    public void Upsert_Find_RoundTrip()
    {
        using var dir = new TempDir();
        using var store = Open(dir);

        var stamp = new DateTime(2026, 7, 12, 9, 0, 0, DateTimeKind.Utc);
        store.Upsert(new PrincipalMapping("/User/17", "user", "gopi@example.com", "entra-guid-1", stamp));

        var found = store.Find("/User/17");
        Assert.NotNull(found);
        Assert.Equal("user", found!.PrincipalType);
        Assert.Equal("gopi@example.com", found.Email);
        Assert.Equal("entra-guid-1", found.EntraId);
        Assert.Equal(stamp, found.UpdatedUtc);

        Assert.Null(store.Find("/User/999"));
    }

    [Fact]
    public void Upsert_UpdatesExistingRow()
    {
        using var dir = new TempDir();
        using var store = Open(dir);
        store.Upsert(new PrincipalMapping("/User/1", "user", "a@example.com", null, DateTime.UtcNow));
        store.Upsert(new PrincipalMapping("/User/1", "user", "a@example.com", "resolved-now", DateTime.UtcNow));

        Assert.Equal(1, store.Count());
        Assert.Equal("resolved-now", store.Find("/User/1")!.EntraId);
    }

    [Fact]
    public void Counts_DistinguishResolved()
    {
        using var dir = new TempDir();
        using var store = Open(dir);
        store.Upsert(new PrincipalMapping("/User/1", "user", "a@example.com", "guid-a", DateTime.UtcNow));
        store.Upsert(new PrincipalMapping("/User/2", "user", "b@example.com", null, DateTime.UtcNow));
        store.Upsert(new PrincipalMapping("/Group/3", "group", null, string.Empty, DateTime.UtcNow));

        Assert.Equal(3, store.Count());
        Assert.Equal(1, store.ResolvedCount());
    }

    [Fact]
    public void All_ReturnsSortedMappings()
    {
        using var dir = new TempDir();
        using var store = Open(dir);
        store.Upsert(new PrincipalMapping("/User/2", "user", null, null, DateTime.UtcNow));
        store.Upsert(new PrincipalMapping("/Group/1", "group", null, null, DateTime.UtcNow));

        var all = store.All();
        Assert.Equal(2, all.Count);
        Assert.Equal("/Group/1", all[0].SourceId);
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        using var dir = new TempDir();
        using var store = Open(dir);
        store.Upsert(new PrincipalMapping("/User/1", "user", null, null, DateTime.UtcNow));
        store.Clear();
        Assert.Equal(0, store.Count());
    }

    [Fact]
    public void Persistence_SurvivesReopen()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "identity.db");
        using (var store = new IdentityStore("TestConnector", path))
        {
            store.Upsert(new PrincipalMapping("/User/5", "user", "e@example.com", "g", DateTime.UtcNow));
        }
        using (var reopened = new IdentityStore("TestConnector", path))
        {
            Assert.Equal("g", reopened.Find("/User/5")!.EntraId);
        }
    }
}
