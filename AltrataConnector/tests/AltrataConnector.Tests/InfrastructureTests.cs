using AltrataConnector.Altrata;
using AltrataConnector.Config;
using AltrataConnector.Graph;
using AltrataConnector.Infrastructure;

namespace AltrataConnector.Tests;

public class EnvLoaderTests
{
    [Fact]
    public void ParsesKeyValueCommentsQuotesAndExport()
    {
        var pairs = EnvLoader.Parse(new[]
        {
            "# comment",
            "",
            "PLAIN=value",
            "QUOTED=\"hello world\"",
            "SINGLE='single quoted'",
            "export EXPORTED=yes",
            "INLINE=value # trailing comment",
            "NOEQUALS",
            "=nokey",
        }).ToDictionary(p => p.Key, p => p.Value);

        Assert.Equal("value", pairs["PLAIN"]);
        Assert.Equal("hello world", pairs["QUOTED"]);
        Assert.Equal("single quoted", pairs["SINGLE"]);
        Assert.Equal("yes", pairs["EXPORTED"]);
        Assert.Equal("value", pairs["INLINE"]);
        Assert.False(pairs.ContainsKey("NOEQUALS"));
        Assert.Equal(5, pairs.Count);
    }

    [Fact]
    public void ProcessEnvironmentWinsOverFiles()
    {
        var root = TestFixtures.NewTempDir("env");
        Directory.CreateDirectory(Path.Combine(root, "env"));
        File.WriteAllText(Path.Combine(root, "env", ".env.local"),
            "ALTRATA_TEST_ONLY_A=file\nALTRATA_TEST_ONLY_B=file\n");
        Environment.SetEnvironmentVariable("ALTRATA_TEST_ONLY_A", "process");
        try
        {
            EnvLoader.Load(root);
            Assert.Equal("process", Environment.GetEnvironmentVariable("ALTRATA_TEST_ONLY_A"));
            Assert.Equal("file", Environment.GetEnvironmentVariable("ALTRATA_TEST_ONLY_B"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALTRATA_TEST_ONLY_A", null);
            Environment.SetEnvironmentVariable("ALTRATA_TEST_ONLY_B", null);
        }
    }

    [Fact]
    public void UserFileSupplementsMainFile()
    {
        var root = TestFixtures.NewTempDir("env2");
        Directory.CreateDirectory(Path.Combine(root, "env"));
        File.WriteAllText(Path.Combine(root, "env", ".env.local"), "ALTRATA_TEST_MAIN=1\n");
        File.WriteAllText(Path.Combine(root, "env", ".env.local.user"), "SECRET_ALTRATA_TEST=shh\n");
        try
        {
            EnvLoader.Load(root);
            Assert.Equal("1", Environment.GetEnvironmentVariable("ALTRATA_TEST_MAIN"));
            Assert.Equal("shh", Environment.GetEnvironmentVariable("SECRET_ALTRATA_TEST"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALTRATA_TEST_MAIN", null);
            Environment.SetEnvironmentVariable("SECRET_ALTRATA_TEST", null);
        }
    }
}

public class SettingsTests
{
    [Theory]
    [InlineData("AltrataWealth", true)]
    [InlineData("ab", false)]                      // too short
    [InlineData("has spaces", false)]              // not alphanumeric
    [InlineData("MicrosoftAltrata", false)]        // reserved prefix
    [InlineData("SharePointFeed", false)]          // reserved prefix
    [InlineData("Altrata2026", true)]
    public void ConnectorIdValidation(string id, bool valid)
    {
        var errors = new List<string>();
        AppConfig.ValidateConnectorId(id, errors);
        Assert.Equal(valid, errors.Count == 0);
    }
}

public class MetricsTests
{
    [Fact]
    public void PrometheusRenderingIncludesHelpTypeAndValues()
    {
        Metrics.ResetForTests();
        Metrics.Increment("altrata_items_ingested_total", 41);
        Metrics.Increment("altrata_items_ingested_total");
        Metrics.SetDeadLetterDepth(3);

        var text = Metrics.RenderPrometheus();

        Assert.Contains("# HELP altrata_items_ingested_total", text);
        Assert.Contains("# TYPE altrata_items_ingested_total counter", text);
        Assert.Contains("altrata_items_ingested_total 42", text);
        Assert.Contains("altrata_deadletter_depth 3", text);
        Assert.Contains("# TYPE altrata_deadletter_depth gauge", text);
        Assert.Contains("altrata_api_billable_lookups_total", text);
        Metrics.ResetForTests();
    }
}

public class LogPrunerTests : IDisposable
{
    public void Dispose() => Environment.SetEnvironmentVariable("LOG_RETENTION_DAYS", null);

    [Fact]
    public void PrunesOnlyOldRunDirectories()
    {
        var root = TestFixtures.NewTempDir("logsprune");
        Directory.CreateDirectory(Path.Combine(root, "ingest_20200101_120000"));   // old
        Directory.CreateDirectory(Path.Combine(root, "ingest_20990101_120000"));   // future
        Directory.CreateDirectory(Path.Combine(root, "not-a-run-dir"));

        Environment.SetEnvironmentVariable("LOG_RETENTION_DAYS", "30");
        var removed = LogPruner.Prune(root);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(Path.Combine(root, "ingest_20200101_120000")));
        Assert.True(Directory.Exists(Path.Combine(root, "ingest_20990101_120000")));
        Assert.True(Directory.Exists(Path.Combine(root, "not-a-run-dir")));
    }

    [Fact]
    public void DisabledWhenRetentionUnset()
    {
        var root = TestFixtures.NewTempDir("logsprune2");
        Directory.CreateDirectory(Path.Combine(root, "ingest_20200101_120000"));
        Environment.SetEnvironmentVariable("LOG_RETENTION_DAYS", null);
        Assert.Equal(0, LogPruner.Prune(root));
        Assert.True(Directory.Exists(Path.Combine(root, "ingest_20200101_120000")));
    }
}

public class HaLeaseTests
{
    [Fact]
    public void OnlyOneOwnerHoldsALease()
    {
        var store = new InMemoryLeaseStore();
        var now = DateTime.UtcNow;
        Assert.True(store.TryAcquire("delivery:d1", "node-a", TimeSpan.FromMinutes(5), now));
        Assert.False(store.TryAcquire("delivery:d1", "node-b", TimeSpan.FromMinutes(5), now));
        Assert.True(store.TryAcquire("delivery:d1", "node-a", TimeSpan.FromMinutes(5), now));  // re-entrant
    }

    [Fact]
    public void ExpiredLeaseCanBeTakenOver()
    {
        var store = new InMemoryLeaseStore();
        var now = DateTime.UtcNow;
        Assert.True(store.TryAcquire("u", "node-a", TimeSpan.FromMinutes(5), now));
        Assert.True(store.TryAcquire("u", "node-b", TimeSpan.FromMinutes(5), now.AddMinutes(6)));
        Assert.False(store.Renew("u", "node-a", TimeSpan.FromMinutes(5), now.AddMinutes(7)));
    }

    [Fact]
    public void ReleaseFreesTheLease()
    {
        var store = new InMemoryLeaseStore();
        var now = DateTime.UtcNow;
        store.TryAcquire("u", "node-a", TimeSpan.FromMinutes(5), now);
        store.Release("u", "node-a");
        Assert.True(store.TryAcquire("u", "node-b", TimeSpan.FromMinutes(5), now));
    }
}

public class ItemTransformerTests
{
    [Fact]
    public void BuildsGraphSafeItemIds()
    {
        // Graph-safe ids keep the legacy shape; ids needing sanitization gain a
        // short stable hash suffix so distinct raw ids can never collide
        // (see CodeReviewRound2Tests.SanitizedIdsNeverCollide).
        Assert.Equal("PersonProfile-P1", ItemTransformer.BuildItemId("PersonProfile", "P1"));
        var dirty = ItemTransformer.BuildItemId("PersonProfile", "a/b c");
        // Hash is joined with '_' (a char the sanitizer folds away), so the hashed
        // form is provably disjoint from the legacy pass-through form — see
        // ItemTransformer.BuildItemId and ItemIdCollisionTests.
        Assert.StartsWith("PersonProfile-a-b-c_", dirty);
        Assert.Matches("^PersonProfile-a-b-c_[0-9a-f]{12}$", dirty);
        Assert.Equal(dirty, ItemTransformer.BuildItemId("PersonProfile", "a/b c"));  // deterministic
    }

    [Fact]
    public void RecordWithoutIdThrows()
    {
        var transformer = new ItemTransformer();
        var record = new FeedRecord
        {
            Dataset = Datasets.PersonProfile,
            Fields = new Dictionary<string, string?> { ["person_name"] = "Ada" },
        };
        Assert.Throws<TransformException>(() =>
            transformer.Transform(record, new[] { new AclEntry { Type = "user", Value = "u" } }));
    }

    [Fact]
    public void MapsWellKnownPropertiesAndContent()
    {
        var transformer = new ItemTransformer();
        var record = new FeedRecord
        {
            Dataset = Datasets.WealthIndicator,
            Fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = "W1",
                ["person_name"] = "Ada Lovelace",
                ["net_worth_usd"] = "9000000",
                ["country"] = "UK",
            },
        };
        var item = transformer.Transform(record, new[] { new AclEntry { Type = "user", Value = "u" } });

        Assert.Equal("WealthIndicator-W1", item.Id);
        Assert.Equal("W1", item.Properties["altrataId"]);
        Assert.Equal("Ada Lovelace", item.Properties["personName"]);
        Assert.Equal("9000000", item.Properties["netWorthUsd"]);
        Assert.Equal("UK", item.Properties["country"]);
        Assert.Equal("PII-Sensitive-Wealth", item.Properties["piiClassification"]);
        Assert.Equal("Wealth profile — Ada Lovelace", item.Properties["title"]);
        Assert.Contains("net worth usd: 9000000", item.Content.Value);
    }

    [Fact]
    public void RejectsEveryoneAclDefensively()
    {
        var transformer = new ItemTransformer();
        var record = new FeedRecord
        {
            Dataset = Datasets.PersonProfile,
            Fields = new Dictionary<string, string?> { ["id"] = "P1" },
        };
        Assert.Throws<AltrataConnector.Entitlement.EntitlementViolationException>(() =>
            transformer.Transform(record, new[] { new AclEntry { Type = "everyone", Value = "all" } }));
    }
}
