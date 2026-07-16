// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Wire-level tests for the intra-object hash-sharding record filter: the REAL
// ApiClient fetch paths against the loopback server, with a ShardObjectBuckets
// restriction on the config. Every consumer of these streams (crawl, checkpoints,
// inventory, deletion sweep, reconcile, ingest-item) inherits routing from these
// two choke points, so this is the load-bearing behavior of the whole feature.

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Salesforce;
using SalesforceCopilotConnector.Tests.TestInfrastructure;

namespace SalesforceCopilotConnector.Tests.TestSalesforce;

[Collection("EnvVars")]
public class ShardBucketFetchTests
{
    private sealed class RealSfSessionScope : IDisposable
    {
        private readonly HttpClient _saved = ApiClient.SfSession;
        public RealSfSessionScope() => ApiClient.SfSession = new HttpClient();
        public void Dispose() => ApiClient.SfSession = _saved;
    }

    /// <summary>Twelve well-formed ids — enough that every bucket of a 2-way split is non-empty.</summary>
    private static readonly string[] TestIds = Enumerable.Range(0, 12)
        .Select(i => $"001A00{i:D9}")
        .ToArray();

    private static AppConfig ConfigFor(string instanceUrl, ShardBucketSpec? caseSpec)
    {
        var b = TestFixtures.TestConfig();
        return new AppConfig
        {
            ClientId = b.ClientId,
            TenantId = b.TenantId,
            RepoRoot = b.RepoRoot,
            SchemaConfig = b.SchemaConfig,
            OwdFieldMap = b.OwdFieldMap,
            ParentMap = b.ParentMap,
            OwdOverrides = b.OwdOverrides,
            ObjectNames = b.ObjectNames,
            UseNewAclEngine = b.UseNewAclEngine,
            UseGroupAcl = b.UseGroupAcl,
            UseEntityDefinitionOwd = b.UseEntityDefinitionOwd,
            DebugObjectType = b.DebugObjectType,
            DebugItemId = b.DebugItemId,
            Tuning = b.Tuning,
            ShardObjectBuckets = caseSpec is null
                ? null
                : new Dictionary<string, ShardBucketSpec>(StringComparer.Ordinal) { ["Case"] = caseSpec },
            Connector = new ConnectorSettings
            {
                Id = $"sf-bucket-{Guid.NewGuid():N}"[..30],
                Name = b.Connector.Name,
                Description = b.Connector.Description,
                Schema = b.Connector.Schema,
                Template = b.Connector.Template,
                Salesforce = new SalesforceSettings
                {
                    InstanceUrl = instanceUrl,
                    ApiVersion = b.Connector.Salesforce.ApiVersion,
                    ClientId = "wire-client-id",
                    ClientSecret = "wire-client-secret",
                },
            },
        };
    }

    private static string RecordsPage(params string[] ids)
    {
        var records = string.Join(",", ids.Select(id =>
            $"{{\"attributes\":{{\"type\":\"Case\"}},\"Id\":\"{id}\",\"Subject\":\"s-{id}\"}}"));
        return $"{{\"totalSize\":{ids.Length},\"done\":true,\"records\":[{records}]}}";
    }

    [Fact]
    public async Task RecordFetchYieldsOnlyOwnedBucketAndShardsPartitionExactly()
    {
        using var server = new LoopbackJsonServer();
        using var session = new RealSfSessionScope();
        server.Script = (_, _) => (200, RecordsPage(TestIds), null);
        var objectConfig = new SalesforceObjectConfig("Case", new[] { "Id", "Subject" });

        var yielded = new Dictionary<int, List<string>>();
        for (var bucket = 0; bucket < 2; bucket++)
        {
            var config = ConfigFor(server.BaseUrl, new ShardBucketSpec(2, new HashSet<int> { bucket }));
            var ids = new List<string>();
            await foreach (var record in ApiClient.FetchSalesforceRecordsAsync(config, "wire-token", objectConfig))
                ids.Add(record["Id"]!.GetValue<string>());
            yielded[bucket] = ids;
        }

        // Each shard yields exactly the ids the hash assigns it...
        for (var bucket = 0; bucket < 2; bucket++)
        {
            var expected = TestIds.Where(id => ShardHash.Bucket(id, 2) == bucket).ToList();
            Assert.NotEmpty(expected);  // guard: the fixture must exercise both buckets
            Assert.Equal(expected, yielded[bucket]);
        }

        // ...and together they partition the object: every record on exactly one shard.
        var union = yielded[0].Concat(yielded[1]).OrderBy(id => id, StringComparer.Ordinal).ToList();
        Assert.Equal(TestIds.OrderBy(id => id, StringComparer.Ordinal), union);
    }

    [Fact]
    public async Task UnbucketedConfigYieldsEveryRecordUnchanged()
    {
        using var server = new LoopbackJsonServer();
        using var session = new RealSfSessionScope();
        server.Script = (_, _) => (200, RecordsPage(TestIds), null);

        var ids = new List<string>();
        await foreach (var record in ApiClient.FetchSalesforceRecordsAsync(
                           ConfigFor(server.BaseUrl, caseSpec: null), "wire-token",
                           new SalesforceObjectConfig("Case", new[] { "Id", "Subject" })))
            ids.Add(record["Id"]!.GetValue<string>());

        Assert.Equal(TestIds, ids);  // default path byte-identical: no filtering
    }

    [Fact]
    public async Task IdFetchAppliesTheSameBucketFilterAsTheRecordFetch()
    {
        // The deletion sweep diffs FetchRecordIdsAsync against the per-connection
        // inventory; if this filter ever diverged from the record fetch, a shard would
        // see other shards' records as stale and mass-delete them. Same filter, proven.
        using var server = new LoopbackJsonServer();
        using var session = new RealSfSessionScope();
        server.Script = (_, _) => (200, RecordsPage(TestIds), null);
        var spec = new ShardBucketSpec(2, new HashSet<int> { 1 });
        var config = ConfigFor(server.BaseUrl, spec);

        var ids = new List<string>();
        await foreach (var id in ApiClient.FetchRecordIdsAsync(
                           config, "wire-token", new SalesforceObjectConfig("Case", new[] { "Id", "Subject" })))
            ids.Add(id);

        Assert.Equal(TestIds.Where(spec.Owns), ids);
        Assert.NotEmpty(ids);
    }

    [Fact]
    public async Task ObjectCountsAreScaledToTheOwnedBucketShare()
    {
        using var server = new LoopbackJsonServer();
        using var session = new RealSfSessionScope();
        server.Script = (_, request) => request.PathAndQuery == "/services/oauth2/token"
            ? (200, "{\"access_token\":\"count-token\"}", null)
            : (200, "{\"totalSize\":100,\"done\":true,\"records\":[]}", null);

        // This shard owns 1 of 4 Case buckets → the dashboard ETA estimate is 100/4.
        var config = ConfigFor(server.BaseUrl, new ShardBucketSpec(4, new HashSet<int> { 0 }));
        var counts = await ApiClient.GetObjectCountsAsync(config);

        Assert.Equal(25, counts["Case"]);
    }
}
