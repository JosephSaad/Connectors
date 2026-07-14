// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests.TestSalesforce;

/// <summary>
/// Tests for the intra-object hash-sharding extension of <see cref="ShardingConfig"/> —
/// the <c>"Object#bucket/of"</c> entry syntax of GRAPH_CONNECTION_SHARDS. Joins the
/// "EnvVars" collection because it mutates the process-global environment; the env var is
/// saved and restored per test.
/// </summary>
[Collection("EnvVars")]
public class ShardBucketConfigTests : IDisposable
{
    private readonly string? _savedShards;

    public ShardBucketConfigTests()
    {
        _savedShards = Environment.GetEnvironmentVariable(ShardingConfig.EnvVar);
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, _savedShards);
    }

    private static AppConfig ConfigWithObjects(params string[] objectNames)
    {
        var objectList = new JsonArray();
        foreach (var name in objectNames)
            objectList.Add(new JsonObject { ["objectName"] = name });
        var schema = new JsonObject { ["objectList"] = objectList };

        return new AppConfig
        {
            ClientId = "00000000-0000-0000-0000-000000000000",
            TenantId = TestFixtures.TenantId,
            RepoRoot = Settings.RepoRoot,
            SchemaConfig = schema,
            ObjectNames = Settings.BuildObjectNameList(schema),
            Connector = new ConnectorSettings
            {
                Id = "BaseConnector",
                Name = "Base Connector",
                Description = "Base connector for bucket-sharding tests.",
                Schema = new JsonArray(),
                Template = new JsonObject { ["id"] = "display" },
                Salesforce = new SalesforceSettings
                {
                    InstanceUrl = TestFixtures.InstanceUrl,
                    ApiVersion = TestFixtures.ApiVersion,
                    ClientId = "mock-salesforce-client-id",
                    ClientSecret = "mock-salesforce-client-secret",
                },
            },
        };
    }

    private static void SetShards(string json) =>
        Environment.SetEnvironmentVariable(ShardingConfig.EnvVar, json);

    // ── happy path ──────────────────────────────────────────────────────────────

    [Fact]
    public void BucketedObjectSplitsAcrossShardsWithPlainNameInObjectTypes()
    {
        SetShards("""{"connA":["Case#0/3","Lead"],"connB":["Case#1/3"],"connC":["Case#2/3"]}""");
        var ok = ShardingConfig.TryLoad(ConfigWithObjects("Case", "Lead"), out var shards, out var error);

        Assert.True(ok, error);
        Assert.Equal(3, shards.Count);

        var connA = shards.Single(s => s.ConnectionId == "connA");
        // Plain name (no "#0/3" suffix) so every existing object-type filter keeps working.
        Assert.Equal(new[] { "Case", "Lead" }, connA.ObjectTypes);
        Assert.NotNull(connA.ObjectBuckets);
        var specA = connA.ObjectBuckets!["Case"];
        Assert.Equal(3, specA.BucketCount);
        Assert.Equal(new[] { 0 }, specA.Buckets.OrderBy(b => b));
        Assert.False(connA.ObjectBuckets!.ContainsKey("Lead"));  // plain type carries no spec

        var connC = shards.Single(s => s.ConnectionId == "connC");
        Assert.Equal(new[] { 2 }, connC.ObjectBuckets!["Case"].Buckets.OrderBy(b => b));
    }

    [Fact]
    public void OneShardMayOwnSeveralBucketsOfTheSameObject()
    {
        SetShards("""{"connA":["Case#0/4","Case#3/4"],"connB":["Case#1/4","Case#2/4"]}""");
        var ok = ShardingConfig.TryLoad(ConfigWithObjects("Case"), out var shards, out var error);

        Assert.True(ok, error);
        var connA = shards.Single(s => s.ConnectionId == "connA");
        Assert.Equal(new[] { "Case" }, connA.ObjectTypes);  // plain name listed once
        Assert.Equal(new[] { 0, 3 }, connA.ObjectBuckets!["Case"].Buckets.OrderBy(b => b));
    }

    // ── validation failures ─────────────────────────────────────────────────────

    [Fact]
    public void MissingBucketIsReported()
    {
        SetShards("""{"connA":["Case#0/3"],"connB":["Case#2/3"]}""");
        var ok = ShardingConfig.TryLoad(ConfigWithObjects("Case"), out _, out var error);

        Assert.False(ok);
        Assert.Contains("#1/3", error);
        Assert.Contains("unassigned", error);
    }

    [Fact]
    public void DuplicateBucketIsReported()
    {
        SetShards("""{"connA":["Case#0/2","Case#1/2"],"connB":["Case#1/2"]}""");
        var ok = ShardingConfig.TryLoad(ConfigWithObjects("Case"), out _, out var error);

        Assert.False(ok);
        Assert.Contains("Case#1/2", error);
        Assert.Contains("multiple shards", error);
    }

    [Fact]
    public void InconsistentBucketCountsAreReported()
    {
        SetShards("""{"connA":["Case#0/2"],"connB":["Case#1/3"]}""");
        var ok = ShardingConfig.TryLoad(ConfigWithObjects("Case"), out _, out var error);

        Assert.False(ok);
        Assert.Contains("inconsistent bucket counts", error);
    }

    [Fact]
    public void PlainAndBucketedMixIsReported()
    {
        SetShards("""{"connA":["Case"],"connB":["Case#0/2"],"connC":["Case#1/2"]}""");
        var ok = ShardingConfig.TryLoad(ConfigWithObjects("Case"), out _, out var error);

        Assert.False(ok);
        Assert.Contains("both plain and hash-bucketed", error);
    }

    [Theory]
    [InlineData("Case#0/1", "at least 2 buckets")]   // N=1 is just the plain form
    [InlineData("Case#2/2", "out of range")]         // index == N
    [InlineData("Case#-1/2", "out of range")]        // negative index
    [InlineData("Case#a/2", "malformed")]            // non-numeric index
    [InlineData("Case#0", "malformed")]              // missing "/N"
    [InlineData("#0/2", "malformed")]                // missing object name
    public void MalformedBucketEntriesAreReported(string entry, string expectedFragment)
    {
        SetShards($$"""{"connA":["{{entry}}"]}""");
        var ok = ShardingConfig.TryLoad(ConfigWithObjects("Case"), out _, out var error);

        Assert.False(ok);
        Assert.Contains(expectedFragment, error);
    }

    [Fact]
    public void BucketedUnknownObjectTypeIsReported()
    {
        SetShards("""{"connA":["Ghost#0/2"],"connB":["Ghost#1/2","Case"]}""");
        var ok = ShardingConfig.TryLoad(ConfigWithObjects("Case"), out _, out var error);

        Assert.False(ok);
        Assert.Contains("unknown object type 'Ghost'", error);
    }

    // ── clone propagation ───────────────────────────────────────────────────────

    [Fact]
    public void ForShardCarriesBucketSpecsOntoTheConfig()
    {
        SetShards("""{"connA":["Case#0/3","Lead"],"connB":["Case#1/3","Case#2/3"]}""");
        var baseConfig = ConfigWithObjects("Case", "Lead");
        Assert.True(ShardingConfig.TryLoad(baseConfig, out var shards, out var error), error);

        var connB = ShardingConfig.ForShard(baseConfig, shards.Single(s => s.ConnectionId == "connB"));
        Assert.Equal("connB", connB.Connector.Id);
        Assert.NotNull(connB.ShardObjectBuckets);
        Assert.Equal(new[] { 1, 2 }, connB.ShardObjectBuckets!["Case"].Buckets.OrderBy(b => b));

        // A shard with only plain types carries no bucket map at all.
        var connA = ShardingConfig.ForShard(baseConfig, shards.Single(s => s.ConnectionId == "connA"));
        Assert.NotNull(connA.ShardObjectBuckets);
        Assert.False(connA.ShardObjectBuckets!.ContainsKey("Lead"));
    }

    [Fact]
    public void ForShardObjectCarriesOnlyThatObjectsSpec()
    {
        SetShards("""{"connA":["Case#0/2","Lead"],"connB":["Case#1/2"]}""");
        var baseConfig = ConfigWithObjects("Case", "Lead");
        Assert.True(ShardingConfig.TryLoad(baseConfig, out var shards, out var error), error);
        var shardA = shards.Single(s => s.ConnectionId == "connA");

        var caseConfig = ShardingConfig.ForShardObject(baseConfig, shardA, "Case");
        Assert.Equal("Case", caseConfig.DebugObjectType);
        Assert.Equal(new[] { 0 }, caseConfig.ShardObjectBuckets!["Case"].Buckets.OrderBy(b => b));

        var leadConfig = ShardingConfig.ForShardObject(baseConfig, shardA, "Lead");
        Assert.Null(leadConfig.ShardObjectBuckets);  // plain object → no record-level filter
    }

    [Fact]
    public void PlainOnlyConfigsStillCarryNullBucketMap()
    {
        SetShards("""{"connA":["Case"],"connB":["Lead"]}""");
        var baseConfig = ConfigWithObjects("Case", "Lead");
        Assert.True(ShardingConfig.TryLoad(baseConfig, out var shards, out var error), error);

        var connA = ShardingConfig.ForShard(baseConfig, shards.Single(s => s.ConnectionId == "connA"));
        Assert.Null(connA.ShardObjectBuckets);  // byte-identical default behavior preserved
        Assert.Null(shards.Single(s => s.ConnectionId == "connA").ObjectBuckets);
    }
}
