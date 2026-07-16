// Regression coverage for the review finding: forget-subject / unsuppress-subject
// were not shard-aware, so under GRAPH_CONNECTION_SHARDS a DSAR erasure withdrew
// nothing and suppressed nothing (the data lives in each shard's own store), and
// the subject was re-ingested on the next crawl. These tests drive the commands
// with sharding on and assert the erasure reaches every shard's store.

using AltrataConnector.Altrata;
using AltrataConnector.Commands;
using AltrataConnector.Config;
using AltrataConnector.Graph;
using AltrataConnector.Identity;

namespace AltrataConnector.Tests;

public class ShardAwareErasureTests : IDisposable
{
    // PersonProfile lands on shard A, WealthIndicator on shard B — so a single
    // subject's items are split across two shard connections.
    private const string ShardMap = """
        {
          "altrataPeopleA": ["PersonProfile", "CareerHistory", "BoardMembership"],
          "altrataWealthB": ["WealthIndicator", "RelationshipPath", "Organization"]
        }
        """;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, null);
        CommandRegistry.ShardRuntimeFactory = null;
    }

    private static void Seed(string dir)
    {
        // Seed shard A with the person profile, shard B with the wealth item —
        // both concerning subject P1 — then dispose so the files are on disk.
        SeedItem(Path.Combine(dir, "altrataPeopleA"), "PersonProfile-P1", Datasets.PersonProfile);
        SeedItem(Path.Combine(dir, "altrataWealthB"), "WealthIndicator-W1", Datasets.WealthIndicator);
    }

    private static void SeedItem(string shardDir, string itemId, string dataset)
    {
        using var rt = TestFixtures.NewRuntime(
            TestFixtures.NewConfig() with { ConnectorId = Path.GetFileName(shardDir) },
            new FakeGraphClient(), shardDir);
        rt.Identity.RecordIngestedItem(new IngestedItem(itemId, dataset, "h", DateTime.UtcNow));
        rt.Identity.RecordItemSubjects(itemId, new[] { "P1" });
    }

    [Fact]
    public async Task ForgetSubject_WithdrawsAndSuppresses_AcrossEveryShard()
    {
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, ShardMap);
        var root = TestFixtures.NewTempDir("sharderase");
        Seed(root);

        var shardGraphs = new Dictionary<string, FakeGraphClient>();
        CommandRegistry.ShardRuntimeFactory = (_, shard) =>
        {
            var graph = new FakeGraphClient();
            shardGraphs[shard.ConnectionId] = graph;
            return TestFixtures.NewRuntime(
                TestFixtures.NewConfig() with { ConnectorId = shard.ConnectionId },
                graph, Path.Combine(root, shard.ConnectionId));
        };

        var baseGraph = new FakeGraphClient();
        using var baseRuntime = TestFixtures.NewRuntime(
            TestFixtures.NewConfig(), baseGraph, Path.Combine(root, "base"));

        var result = await CommandRegistry.ForgetSubjectAsync(
            baseRuntime, altrataId: "P1", email: null, actor: "joseph", confirm: true);

        Assert.Equal(true, result);

        // Each shard's item was withdrawn from ITS OWN connection.
        Assert.Contains("PersonProfile-P1", shardGraphs["altrataPeopleA"].DeletedItems);
        Assert.Contains("WealthIndicator-W1", shardGraphs["altrataWealthB"].DeletedItems);
        Assert.Empty(baseGraph.DeletedItems);  // base has none of the subject's items

        // Durable across re-delivery: the subject is suppressed AND its inventory
        // cleared in each shard's own persisted store (re-open to verify).
        AssertShardErased(Path.Combine(root, "altrataPeopleA"));
        AssertShardErased(Path.Combine(root, "altrataWealthB"));
    }

    private static void AssertShardErased(string shardDir)
    {
        using var rt = TestFixtures.NewRuntime(
            TestFixtures.NewConfig() with { ConnectorId = Path.GetFileName(shardDir) },
            new FakeGraphClient(), shardDir);
        Assert.True(rt.State.IsSubjectSuppressed("P1"));       // future crawls skip it
        Assert.Equal(0, rt.Identity.CountIngestedItems());     // inventory cleared
    }

    [Fact]
    public async Task ForgetSubject_DryRun_DoesNotMutateAnyShard()
    {
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, ShardMap);
        var root = TestFixtures.NewTempDir("sharderasedry");
        Seed(root);

        var shardGraphs = new Dictionary<string, FakeGraphClient>();
        CommandRegistry.ShardRuntimeFactory = (_, shard) =>
        {
            var graph = new FakeGraphClient();
            shardGraphs[shard.ConnectionId] = graph;
            return TestFixtures.NewRuntime(
                TestFixtures.NewConfig() with { ConnectorId = shard.ConnectionId },
                graph, Path.Combine(root, shard.ConnectionId));
        };
        using var baseRuntime = TestFixtures.NewRuntime(
            TestFixtures.NewConfig(), new FakeGraphClient(), Path.Combine(root, "base"));

        await CommandRegistry.ForgetSubjectAsync(
            baseRuntime, altrataId: "P1", email: null, actor: "joseph", confirm: false);

        Assert.Empty(shardGraphs["altrataPeopleA"].DeletedItems);
        Assert.Empty(shardGraphs["altrataWealthB"].DeletedItems);
        // Nothing suppressed or removed on either shard.
        using var a = TestFixtures.NewRuntime(
            TestFixtures.NewConfig() with { ConnectorId = "altrataPeopleA" },
            new FakeGraphClient(), Path.Combine(root, "altrataPeopleA"));
        Assert.False(a.State.IsSubjectSuppressed("P1"));
        Assert.Equal(1, a.Identity.CountIngestedItems());
    }

    [Fact]
    public async Task UnsuppressSubject_LiftsBlock_OnEveryShard()
    {
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, ShardMap);
        var root = TestFixtures.NewTempDir("shardunsup");
        Seed(root);

        CommandRegistry.ShardRuntimeFactory = (_, shard) =>
            TestFixtures.NewRuntime(
                TestFixtures.NewConfig() with { ConnectorId = shard.ConnectionId },
                new FakeGraphClient(), Path.Combine(root, shard.ConnectionId));
        using var baseRuntime = TestFixtures.NewRuntime(
            TestFixtures.NewConfig(), new FakeGraphClient(), Path.Combine(root, "base"));

        await CommandRegistry.ForgetSubjectAsync(
            baseRuntime, "P1", null, "joseph", confirm: true);
        await CommandRegistry.UnsuppressSubjectAsync(baseRuntime, "P1", "joseph", confirm: true);

        // Suppression lifted in each shard's store.
        foreach (var shard in new[] { "altrataPeopleA", "altrataWealthB" })
        {
            using var rt = TestFixtures.NewRuntime(
                TestFixtures.NewConfig() with { ConnectorId = shard },
                new FakeGraphClient(), Path.Combine(root, shard));
            Assert.False(rt.State.IsSubjectSuppressed("P1"));
        }
    }
}
