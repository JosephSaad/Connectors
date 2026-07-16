using AltrataConnector.Identity;

namespace AltrataConnector.Tests;

public class EntityNormalizerTests
{
    [Theory]
    [InlineData("  Ada   LOVELACE ", "ada lovelace")]
    [InlineData("José Núñez", "jose nunez")]
    [InlineData("O'Brien, Patrick", "o brien patrick")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void NormalizeNameFoldsCaseDiacriticsAndPunctuation(string input, string? expected)
    {
        Assert.Equal(expected, EntityNormalizer.NormalizeName(input));
    }

    [Theory]
    [InlineData("Acme Corp", "acme")]
    [InlineData("Acme Corporation", "acme")]
    [InlineData("Acme Holdings Ltd", "acme")]
    [InlineData("Acme", "acme")]
    [InlineData("Ltd", "ltd")]  // never strip the only token
    public void NormalizeEmployerStripsCorporateSuffixes(string input, string expected)
    {
        Assert.Equal(expected, EntityNormalizer.NormalizeEmployer(input));
    }
}

public class EntityResolverTests : IDisposable
{
    private readonly SqliteIdentityStore _store;

    public EntityResolverTests()
    {
        _store = new SqliteIdentityStore(
            Path.Combine(TestFixtures.NewTempDir("resolver"), "identity.db"));
        _store.ReplaceCrmContacts(new[]
        {
            new CrmContact { Id = "C1", Email = "Ada@Contoso.com", Name = "Ada Lovelace", Employer = "Analytical Engines Ltd" },
            new CrmContact { Id = "C2", Email = null, Name = "Charles Babbage", Employer = "Difference Machines Inc" },
        });
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public void EmailMatchWinsAndIsCaseInsensitive()
    {
        var resolver = new EntityResolver(_store);
        var match = resolver.Match("ADA@CONTOSO.COM", "Someone Else", "Elsewhere");
        Assert.NotNull(match);
        Assert.Equal("C1", match!.CrmContactId);
        Assert.Equal(EntityResolver.RuleEmail, match.MatchRule);
    }

    [Fact]
    public void NameEmployerMatchIsSecondRule()
    {
        var resolver = new EntityResolver(_store);
        var match = resolver.Match(null, "charles BABBAGE", "Difference Machines");
        Assert.NotNull(match);
        Assert.Equal("C2", match!.CrmContactId);
        Assert.Equal(EntityResolver.RuleNameEmployer, match.MatchRule);
    }

    [Fact]
    public void NameAloneDoesNotMatch()
    {
        var resolver = new EntityResolver(_store);
        Assert.Null(resolver.Match(null, "Charles Babbage", null));
        Assert.Null(resolver.Match(null, "Charles Babbage", "Wrong Employer"));
    }

    [Fact]
    public void NoMatchReturnsNull()
    {
        var resolver = new EntityResolver(_store);
        Assert.Null(resolver.Match("nobody@nowhere.com", "Nobody", "Nowhere"));
    }

    [Fact]
    public void ResolvePersistsCrosswalkEntry()
    {
        var resolver = new EntityResolver(_store);
        var when = new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc);
        var match = resolver.Resolve("P42", "ada@contoso.com", null, null, when);

        Assert.NotNull(match);
        var entry = _store.GetCrosswalk("P42");
        Assert.NotNull(entry);
        Assert.Equal("C1", entry!.CrmContactId);
        Assert.Equal(EntityResolver.RuleEmail, entry.MatchRule);
        Assert.Equal(when, entry.LinkedUtc);
    }

    [Fact]
    public void ResolveWithoutMatchWritesNothing()
    {
        var resolver = new EntityResolver(_store);
        Assert.Null(resolver.Resolve("P99", "unknown@x.com", "Unknown", "Unknown"));
        Assert.Null(_store.GetCrosswalk("P99"));
        Assert.Empty(_store.ListCrosswalk());
    }
}

public class IdentityStoreTests : IDisposable
{
    private readonly SqliteIdentityStore _store;

    public IdentityStoreTests() =>
        _store = new SqliteIdentityStore(Path.Combine(TestFixtures.NewTempDir("idstore"), "identity.db"));

    public void Dispose() => _store.Dispose();

    [Fact]
    public void SeatsReplaceAndReadBack()
    {
        _store.ReplaceSeats(TestFixtures.DefaultSeats());
        Assert.Equal(2, _store.GetSeats().Count);

        _store.ReplaceSeats(new[] { new SeatPrincipal(SeatPrincipalKind.Group, "g-1") });
        var seats = _store.GetSeats();
        Assert.Single(seats);
        Assert.Equal(SeatPrincipalKind.Group, seats[0].Kind);
    }

    [Fact]
    public void IngestedItemsUpsertAndQueryByAclHash()
    {
        _store.RecordIngestedItem(new IngestedItem("i1", "PersonProfile", "hashA", DateTime.UtcNow));
        _store.RecordIngestedItem(new IngestedItem("i2", "PersonProfile", "hashA", DateTime.UtcNow));
        _store.RecordIngestedItem(new IngestedItem("i2", "PersonProfile", "hashB", DateTime.UtcNow));  // upsert

        Assert.Equal(2, _store.CountIngestedItems());
        var stale = _store.ListItemsWithAclHashOtherThan("hashB");
        Assert.Single(stale);
        Assert.Equal("i1", stale[0].ItemId);
    }

    [Fact]
    public void RemoveIngestedItemDeletes()
    {
        _store.RecordIngestedItem(new IngestedItem("i1", "PersonProfile", "h", DateTime.UtcNow));
        _store.RemoveIngestedItem("i1");
        Assert.Equal(0, _store.CountIngestedItems());
    }

    [Fact]
    public void PersistsAcrossReopen()
    {
        var path = Path.Combine(TestFixtures.NewTempDir("idstore2"), "identity.db");
        using (var store = new SqliteIdentityStore(path))
        {
            store.ReplaceSeats(TestFixtures.DefaultSeats());
            store.UpsertCrosswalk(new CrosswalkEntry("P1", "C1", "email", DateTime.UtcNow));
        }
        using (var reopened = new SqliteIdentityStore(path))
        {
            Assert.Equal(2, reopened.GetSeats().Count);
            Assert.NotNull(reopened.GetCrosswalk("P1"));
        }
    }

    [Fact]
    public void WipeAllClearsEverything()
    {
        _store.ReplaceSeats(TestFixtures.DefaultSeats());
        _store.ReplaceCrmContacts(new[] { new CrmContact { Id = "C1", Email = "a@b.c" } });
        _store.UpsertCrosswalk(new CrosswalkEntry("P1", "C1", "email", DateTime.UtcNow));
        _store.RecordIngestedItem(new IngestedItem("i1", "PersonProfile", "h", DateTime.UtcNow));

        _store.WipeAll();

        Assert.Empty(_store.GetSeats());
        Assert.Equal(0, _store.CountCrmContacts());
        Assert.Empty(_store.ListCrosswalk());
        Assert.Equal(0, _store.CountIngestedItems());
    }
}
