// SQLite identity store: principal mapping + tracked-item queries.

using SeismicConnector.Graph;

namespace SeismicConnector.Tests;

public class IdentityStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteIdentityStore _store;

    public IdentityStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "seismic-id-" + Guid.NewGuid().ToString("N") + ".db");
        _store = new SqliteIdentityStore(_dbPath);
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

    [Fact]
    public void Principal_UpsertAndGet()
    {
        _store.UpsertPrincipal(new PrincipalMapping("u1", "user", "amy@contoso.com", "entra-1", "Amy"));
        var mapping = _store.GetPrincipal("u1");
        Assert.NotNull(mapping);
        Assert.Equal("entra-1", mapping!.EntraObjectId);
        Assert.Equal("amy@contoso.com", mapping.Email);
        Assert.Equal("entra-1", _store.GetEntraObjectId("u1"));
        Assert.Null(_store.GetEntraObjectId("nope"));
    }

    [Fact]
    public void Principal_UpsertOverwrites()
    {
        _store.UpsertPrincipal(new PrincipalMapping("u1", "user", "old@contoso.com", null, "Old"));
        _store.UpsertPrincipal(new PrincipalMapping("u1", "user", "new@contoso.com", "entra-9", "New"));
        var mapping = _store.GetPrincipal("u1")!;
        Assert.Equal("new@contoso.com", mapping.Email);
        Assert.Equal("entra-9", mapping.EntraObjectId);
        Assert.Single(_store.GetAllPrincipals());
    }

    [Fact]
    public void CountMappedPrincipals_IgnoresUnmapped()
    {
        _store.UpsertPrincipal(new PrincipalMapping("u1", "user", null, "entra-1", null));
        _store.UpsertPrincipal(new PrincipalMapping("u2", "user", null, null, null));
        _store.UpsertPrincipal(new PrincipalMapping("g1", "group", null, "entra-2", null));
        Assert.Equal(2, _store.CountMappedPrincipals());
    }

    [Fact]
    public void TrackedItem_RoundTrip()
    {
        var now = DateTime.UtcNow;
        var expires = now.AddDays(30);
        _store.UpsertTrackedItem(new TrackedItem("c1", "v1", "ts1", expires, now, "ingested"));

        var tracked = _store.GetTrackedItem("c1");
        Assert.NotNull(tracked);
        Assert.Equal("v1", tracked!.VersionId);
        Assert.Equal("ts1", tracked.TeamsiteId);
        Assert.Equal("ingested", tracked.Status);
        Assert.Equal(expires, tracked.ExpiresAtUtc!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void TrackedItem_VersionSupersede_Overwrites()
    {
        var now = DateTime.UtcNow;
        _store.UpsertTrackedItem(new TrackedItem("c1", "v1", "ts1", null, now, "ingested"));
        _store.UpsertTrackedItem(new TrackedItem("c1", "v2", "ts1", null, now.AddMinutes(5), "ingested"));
        Assert.Equal("v2", _store.GetTrackedItem("c1")!.VersionId);
        Assert.Single(_store.GetAllTrackedItems());
    }

    [Fact]
    public void ExpiredItems_QueryFiltersCorrectly()
    {
        var now = DateTime.UtcNow;
        _store.UpsertTrackedItem(new TrackedItem("past", "v1", "ts1", now.AddDays(-1), now, "ingested"));
        _store.UpsertTrackedItem(new TrackedItem("future", "v1", "ts1", now.AddDays(1), now, "ingested"));
        _store.UpsertTrackedItem(new TrackedItem("never", "v1", "ts1", null, now, "ingested"));
        _store.UpsertTrackedItem(new TrackedItem("excluded", "v1", "ts1", now.AddDays(-2), now, "excluded"));

        var expired = _store.GetExpiredItems(now);
        Assert.Single(expired);
        Assert.Equal("past", expired[0].ItemId);
    }

    [Fact]
    public void ItemsNotSeenSince_FindsStaleIngestedOnly()
    {
        var crawlStart = DateTime.UtcNow;
        _store.UpsertTrackedItem(new TrackedItem("stale", "v1", "ts1", null, crawlStart.AddHours(-2), "ingested"));
        _store.UpsertTrackedItem(new TrackedItem("fresh", "v1", "ts1", null, crawlStart.AddMinutes(5), "ingested"));
        _store.UpsertTrackedItem(new TrackedItem("staleExcluded", "v1", "ts1", null, crawlStart.AddHours(-2), "excluded"));

        var notSeen = _store.GetItemsNotSeenSince(crawlStart);
        Assert.Single(notSeen);
        Assert.Equal("stale", notSeen[0].ItemId);
    }

    [Fact]
    public void RemoveTrackedItem_Deletes()
    {
        _store.UpsertTrackedItem(new TrackedItem("c1", "v1", "ts1", null, DateTime.UtcNow, "ingested"));
        _store.RemoveTrackedItem("c1");
        Assert.Null(_store.GetTrackedItem("c1"));
        _store.RemoveTrackedItem("c1");  // idempotent
    }

    [Fact]
    public void Store_PersistsAcrossReopen()
    {
        _store.UpsertPrincipal(new PrincipalMapping("u1", "user", "a@b.c", "entra-1", null));
        _store.Dispose();

        using var reopened = new SqliteIdentityStore(_dbPath);
        Assert.Equal("entra-1", reopened.GetEntraObjectId("u1"));
    }
}
