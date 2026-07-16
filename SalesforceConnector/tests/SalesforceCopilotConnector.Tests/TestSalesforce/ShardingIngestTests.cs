// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Item;
using SalesforceCopilotConnector.Salesforce;
using Xunit;

namespace SalesforceCopilotConnector.Tests.TestSalesforce;

/// <summary>
/// End-to-end proof that connection sharding actually restricts ingestion: a config
/// carrying <see cref="Salesforce.AppConfig.ShardObjectTypes"/> (produced by
/// <c>ShardingConfig.ForShard</c>) must drive the real ingest pipeline to process only
/// that shard's object types, out of the full schema in <c>config/schema.json</c>.
/// </summary>
[Collection("IngestGlobalState")]
public class ShardingIngestTests : IDisposable
{
    private readonly Func<string, SalesforceObjectConfig?> _getCfg = Ingest.GetObjectConfigHook;
    private readonly Func<AppConfig, SalesforceObjectConfig, DateTime?, int, IAsyncEnumerable<List<JsonObject>>> _iter = Ingest.IterObjectChunksHook;
    private readonly Func<AppConfig, DateTime?, Task<Dictionary<string, int>>> _counts = Ingest.GetObjectCountsHook;
    private readonly Func<AppConfig, Dictionary<string, SalesforceObjectHandler>, GraphClient, AclResolver> _acl = Ingest.LegacyAclResolverFactory;
    private readonly Func<string, JsonArray, string, SalesforceItemTransformer> _tf = Ingest.TransformerFactory;

    public void Dispose()
    {
        Ingest.GetObjectConfigHook = _getCfg;
        Ingest.IterObjectChunksHook = _iter;
        Ingest.GetObjectCountsHook = _counts;
        Ingest.LegacyAclResolverFactory = _acl;
        Ingest.TransformerFactory = _tf;
    }

    [Fact]
    public async Task ShardObjectTypesRestrictsIngestedObjects()
    {
        // A shard covering two of the schema's objects, built through the real ForShard path.
        var shardCfg = ShardingConfig.ForShard(
            TestFixtures.TestConfig(),
            new Shard("shardCrmA", new[] { "Account", "Contact" }));
        Assert.Equal(new[] { "Account", "Contact" }, shardCfg.ShardObjectTypes);

        var requested = new ConcurrentBag<string>();
        Ingest.GetObjectConfigHook = t => new SalesforceObjectConfig(t, new[] { "Id" });
        Ingest.GetObjectCountsHook = (_, _) => Task.FromResult(new Dictionary<string, int>());
        Ingest.IterObjectChunksHook = (_, objCfg, _, _) =>
        {
            requested.Add(objCfg.ObjectType);
            return EmptyChunks();
        };
        Ingest.LegacyAclResolverFactory = (config, _, _) =>
            new CheckpointSupport.FakeAclResolver(config, _ => new Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>>());
        Ingest.TransformerFactory = (_, _, _) => new CheckpointSupport.FakeTransformer();

        var client = new CheckpointSupport.FakeGraphClient { OnBatch = _ => new List<JsonObject>() };

        await Ingest.IngestContentAsync(shardCfg, client, since: null, dashboard: null);

        // Only the shard's two object types were fetched — not the other ~16 in the schema.
        Assert.Equal(new[] { "Account", "Contact" }, requested.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    private static async IAsyncEnumerable<List<JsonObject>> EmptyChunks()
    {
        await Task.CompletedTask;
        yield break;
    }
}
