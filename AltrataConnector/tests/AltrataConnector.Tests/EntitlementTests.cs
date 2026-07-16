using AltrataConnector.Entitlement;
using AltrataConnector.Graph;
using AltrataConnector.Identity;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

public class SeatAclBuilderTests
{
    [Fact]
    public void BuildsUserAndGroupGrantsOnly()
    {
        var seats = new List<SeatPrincipal>
        {
            new(SeatPrincipalKind.UserUpn, "alice@contoso.com"),
            new(SeatPrincipalKind.UserObjectId, "3f2a1b7c-9d4e-4f6a-8b2c-1d5e7f9a0b3c"),
            new(SeatPrincipalKind.Group, "11111111-2222-3333-4444-555555555555"),
        };
        var acl = SeatAclBuilder.BuildAcl(seats);

        Assert.Equal(3, acl.Count);
        Assert.All(acl, entry => Assert.Equal("grant", entry.AccessType));
        Assert.All(acl, entry => Assert.Equal("azureActiveDirectory", entry.IdentitySource));
        Assert.Equal(2, acl.Count(e => e.Type == "user"));
        Assert.Equal(1, acl.Count(e => e.Type == "group"));
        Assert.DoesNotContain(acl, e => e.Type is "everyone" or "everyoneExceptGuests");
    }

    [Fact]
    public void EmptySeatListFailsClosed()
    {
        var exc = Assert.Throws<EntitlementViolationException>(() =>
            SeatAclBuilder.BuildAcl(Array.Empty<SeatPrincipal>()));
        Assert.Contains("empty", exc.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("everyone")]
    [InlineData("Everyone")]
    [InlineData("everyoneExceptGuests")]
    public void NeverEveryoneInvariantIsEnforced(string forbiddenType)
    {
        var acl = new[]
        {
            new AclEntry { Type = "user", Value = "alice@contoso.com" },
            new AclEntry { Type = forbiddenType, Value = "everyone" },
        };
        Assert.Throws<EntitlementViolationException>(() => SeatAclBuilder.AssertNeverEveryone(acl));
    }

    [Fact]
    public void SeatHashIsOrderIndependentAndCaseInsensitive()
    {
        var a = new List<SeatPrincipal>
        {
            new(SeatPrincipalKind.UserUpn, "Alice@contoso.com"),
            new(SeatPrincipalKind.UserUpn, "bob@contoso.com"),
        };
        var b = new List<SeatPrincipal>
        {
            new(SeatPrincipalKind.UserUpn, "bob@CONTOSO.com"),
            new(SeatPrincipalKind.UserUpn, "alice@contoso.com"),
        };
        Assert.Equal(SeatAclBuilder.ComputeSeatHash(a), SeatAclBuilder.ComputeSeatHash(b));
    }

    [Fact]
    public void SeatHashChangesWhenSeatsChange()
    {
        var a = new List<SeatPrincipal> { new(SeatPrincipalKind.UserUpn, "alice@contoso.com") };
        var b = new List<SeatPrincipal>
        {
            new(SeatPrincipalKind.UserUpn, "alice@contoso.com"),
            new(SeatPrincipalKind.UserUpn, "carol@contoso.com"),
        };
        Assert.NotEqual(SeatAclBuilder.ComputeSeatHash(a), SeatAclBuilder.ComputeSeatHash(b));
    }
}

public class SeatServiceTests
{
    [Fact]
    public void ParsesPlainArraySeatFile()
    {
        var seats = SeatService.ParseSeatFile(
            """["alice@contoso.com", "3f2a1b7c-9d4e-4f6a-8b2c-1d5e7f9a0b3c"]""");
        Assert.Equal(2, seats.Count);
        Assert.Equal(SeatPrincipalKind.UserUpn, seats[0].Kind);
        Assert.Equal(SeatPrincipalKind.UserObjectId, seats[1].Kind);
    }

    [Fact]
    public void ParsesUsersGroupsObjectAndDeduplicates()
    {
        var seats = SeatService.ParseSeatFile("""
            {"users": ["alice@contoso.com", "ALICE@contoso.com"], "groups": ["g-1"]}
            """);
        Assert.Equal(2, seats.Count);
        Assert.Contains(seats, s => s.Kind == SeatPrincipalKind.Group && s.Value == "g-1");
    }

    [Fact]
    public void SeatGroupIdOverridesFile()
    {
        var root = TestFixtures.NewTempDir("seatgrp");
        var config = TestFixtures.NewConfig(dataDir: root);
        var configWithGroup = new AltrataConnector.Config.AppConfig
        {
            ConnectorId = config.ConnectorId,
            ConnectorName = config.ConnectorName,
            ConnectorDescription = config.ConnectorDescription,
            AadClientId = config.AadClientId,
            AadTenantId = config.AadTenantId,
            AadClientSecret = config.AadClientSecret,
            SeatGroupId = "22222222-3333-4444-5555-666666666666",
            SeatListPath = Path.Combine(root, "nonexistent.json"),
        };
        using var identity = new SqliteIdentityStore(Path.Combine(root, "identity.db"));
        var state = new FileStateStore("AltrataTest", Path.Combine(root, "logs"), Path.Combine(root, "data"));
        var service = new SeatService(configWithGroup, identity, state);

        var seats = service.LoadSeats();
        Assert.Single(seats);
        Assert.Equal(SeatPrincipalKind.Group, seats[0].Kind);
    }

    [Fact]
    public void SyncSeatsDetectsChangeViaHash()
    {
        var root = TestFixtures.NewTempDir("seatsync");
        var seatPath = Path.Combine(root, "seats.json");
        TestFixtures.WriteSeatFile(seatPath, "alice@contoso.com");

        var config = TestFixtures.NewConfig(seatListPath: seatPath);
        using var identity = new SqliteIdentityStore(Path.Combine(root, "identity.db"));
        var state = new FileStateStore("AltrataTest", Path.Combine(root, "logs"), Path.Combine(root, "data"));
        var service = new SeatService(config, identity, state);

        // First sync: changed (no previous hash), nothing to re-ACL.
        var first = service.SyncSeats();
        Assert.True(first.Changed);
        Assert.Null(first.PreviousHash);
        Assert.False(first.RequiresReAcl);
        service.CommitSeatHash(first.SeatHash);

        // Same list: unchanged.
        var second = service.SyncSeats();
        Assert.False(second.Changed);
        Assert.False(second.RequiresReAcl);

        // Different list: changed + re-ACL required.
        TestFixtures.WriteSeatFile(seatPath, "alice@contoso.com", "bob@contoso.com");
        var third = service.SyncSeats();
        Assert.True(third.Changed);
        Assert.Equal(first.SeatHash, third.PreviousHash);
        Assert.True(third.RequiresReAcl);

        // Seats were persisted into the identity store.
        Assert.Equal(2, identity.GetSeats().Count);
    }

    [Fact]
    public void EmptySeatFileRefusesToSync()
    {
        var root = TestFixtures.NewTempDir("seatempty");
        var seatPath = Path.Combine(root, "seats.json");
        File.WriteAllText(seatPath, """{"users": [], "groups": []}""");

        var config = TestFixtures.NewConfig(seatListPath: seatPath);
        using var identity = new SqliteIdentityStore(Path.Combine(root, "identity.db"));
        var state = new FileStateStore("AltrataTest", Path.Combine(root, "logs"), Path.Combine(root, "data"));
        var service = new SeatService(config, identity, state);

        Assert.Throws<EntitlementViolationException>(() => service.SyncSeats());
    }
}
