// End-to-end: classification properties appear on ingested items only when
// CLASSIFICATION is on (PII/PCI/Secret force Restricted, the per-object
// sensitivityDefault floors the label otherwise), and the manifest is written
// when CLASSIFICATION_MANIFEST is on. In-memory BDH source + mocked Graph.

using System.Text.Json.Nodes;
using HadoopConnector.AclEngine;
using HadoopConnector.Hdfs;
using HadoopConnector.Config;
using HadoopConnector.Content;
using HadoopConnector.Filters;
using HadoopConnector.Graph;
using HadoopConnector.Infrastructure;
using HadoopConnector.Item;

namespace HadoopConnector.Tests;

public class ClassificationPipelineTests : IDisposable
{
    private const string Connector = "BdhHadoopMart";
    private const string RecordId = "T000000000001";

    private readonly TempDir _dir = new();
    private readonly SyncStateScope _stateScope = new();
    private readonly IdentityStore _store;
    private readonly SchemaConfig _schema;
    private readonly FakeGraphClient _graph;
    private readonly FakeBdhSource _source;

    public ClassificationPipelineTests()
    {
        _store = new IdentityStore("Classify", Path.Combine(_dir.Path, "identity.db"));
        _store.Upsert(new PrincipalMapping(
            "005U0000001", "user", "o@example.com", "entra-owner", DateTime.UtcNow));
        _schema = new SchemaConfig
        {
            ObjectList = new List<ObjectConfig>
            {
                new()
                {
                    ObjectName = "Task",
                    DisplayName = "Task",
                    AclMode = "ownerOnly",
                    SensitivityDefault = "Internal",
                    SelectedFields = new Dictionary<string, string>
                    {
                        ["Name"] = "Title",
                        ["Description"] = "_bdh_Description",
                        ["OwnerId"] = "OwnerId",
                    },
                },
            },
        };
        // A PII email lives in the description content.
        _source = new FakeBdhSource();
        _source.Add(
            "Task/dt=2026-07-15/part-0000.jsonl",
            $$"""{"Id":"{{RecordId}}","Name":"Onboard vendor","Description":"Primary contact is jane.doe@vendor.com for signoff.","OwnerId":"005U0000001"}""");
        _graph = new FakeGraphClient(TestConfig.Make());
    }

    public void Dispose()
    {
        _store.Dispose();
        _stateScope.Dispose();
        _dir.Dispose();
    }

    private Func<string, IItemInventory> InventoryFactory => id =>
        new ItemInventory(id, Path.Combine(_dir.Path, $"inv_{id}.db"));

    private IngestPipeline Pipeline(
        AppConfig config, SchemaConfig? schema = null, FakeBdhSource? source = null)
    {
        var fetcher = new BdhFetcher(config, source ?? _source, FilterSet.Empty);
        var resolver = new AclResolver(
            new PrincipalMapper(_store), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
        SensitivityClassifier? classifier = config.Classification
            ? new SensitivityClassifier(ContentClassifier.Load(
                Path.Combine(AppContext.BaseDirectory, "config", "classification.json")))
            : null;
        return new IngestPipeline(
            config, schema ?? _schema, fetcher, _graph, resolver, new ItemConverter(config),
            ha: null, inventoryFactory: InventoryFactory, sensitivityClassifier: classifier);
    }

    private JsonObject IngestedProperties()
    {
        var put = Assert.Single(_graph.Sent, s => s.Method == HttpMethod.Put);
        return put.Body!.AsObject()["properties"]!.AsObject();
    }

    private JsonArray IngestedAcl()
    {
        var put = Assert.Single(_graph.Sent, s => s.Method == HttpMethod.Put);
        return put.Body!.AsObject()["acl"]!.AsArray();
    }

    [Fact]
    public async Task ClassificationOn_StampsLabelAndCategories()
    {
        var summary = await Pipeline(TestConfig.Make(allowFullScan: true, classification: true))
            .RunAsync(fullCrawl: true);
        Assert.Equal(1, summary.Ingested);

        var props = IngestedProperties();
        Assert.Equal("Restricted", props["advisorySensitivity"]!.GetValue<string>());  // PII in description
        Assert.Contains("PII", props["DetectedCategories"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task ClassificationOff_NoTaxonomyProperties()
    {
        var summary = await Pipeline(TestConfig.Make(allowFullScan: true, classification: false))
            .RunAsync(fullCrawl: true);
        Assert.Equal(1, summary.Ingested);

        var props = IngestedProperties();
        Assert.Null(props["advisorySensitivity"]);
        Assert.Null(props["DetectedCategories"]);
    }

    [Fact]
    public async Task SensitivityDefault_FloorsLabel_WhenNothingDetected()
    {
        // A clean record (no PII/PCI/Secret) on an object whose schema default
        // is Confidential must carry the per-object baseline, not Internal.
        var schema = new SchemaConfig
        {
            ObjectList = new List<ObjectConfig>
            {
                new()
                {
                    ObjectName = "Task",
                    DisplayName = "Task",
                    AclMode = "ownerOnly",
                    SensitivityDefault = "Confidential",
                    SelectedFields = new Dictionary<string, string>
                    {
                        ["Name"] = "Title",
                        ["OwnerId"] = "OwnerId",
                    },
                },
            },
        };
        var source = new FakeBdhSource();
        source.Add(
            "Task/dt=2026-07-15/part-0000.jsonl",
            $$"""{"Id":"{{RecordId}}","Name":"Quarterly plan","OwnerId":"005U0000001"}""");

        var summary = await Pipeline(
                TestConfig.Make(allowFullScan: true, classification: true), schema, source)
            .RunAsync(fullCrawl: true);
        Assert.Equal(1, summary.Ingested);

        var props = IngestedProperties();
        Assert.Equal("Confidential", props["advisorySensitivity"]!.GetValue<string>());
        Assert.Empty(props["DetectedCategories"]!.AsArray());
    }

    [Fact]
    public async Task Manifest_WrittenWhenEnabled_WithSummary()
    {
        var config = TestConfig.Make(
            allowFullScan: true, classification: true, classificationManifest: true);
        await Pipeline(config).RunAsync(fullCrawl: true);

        var manifests = Directory.GetFiles(SyncState.LogsDir, "classification_*.jsonl");
        var path = Assert.Single(manifests);
        var lines = File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToList();
        Assert.Equal(2, lines.Count);  // 1 item + summary

        var item = JsonNode.Parse(lines[0])!;
        Assert.Equal(RecordId, item["item_id"]!.GetValue<string>());
        Assert.Equal("Restricted", item["sensitivity_label"]!.GetValue<string>());

        var summary = JsonNode.Parse(lines[^1])!;
        Assert.Equal("summary", summary["kind"]!.GetValue<string>());
        Assert.Equal(1, summary["counts"]!["Restricted"]!.GetValue<int>());
    }

    [Fact]
    public async Task Manifest_NotWritten_WhenDisabled()
    {
        await Pipeline(TestConfig.Make(
                allowFullScan: true, classification: true, classificationManifest: false))
            .RunAsync(fullCrawl: true);
        Assert.Empty(Directory.GetFiles(SyncState.LogsDir, "classification_*.jsonl"));
    }

    // ── #6 classification enforcement (advisory by default) ──────────────────

    [Fact]
    public async Task Advisory_ByDefault_RestrictedItemKeepsResolvedAcl()
    {
        // Classification ON but enforcement OFF (default): the Restricted tag is
        // advisory — the item keeps its resolved owner ACL, not narrowed.
        var summary = await Pipeline(TestConfig.Make(allowFullScan: true, classification: true))
            .RunAsync(fullCrawl: true);
        Assert.Equal(1, summary.Ingested);
        Assert.Equal("Restricted", IngestedProperties()["advisorySensitivity"]!.GetValue<string>());

        var acl = IngestedAcl();
        Assert.Contains(acl, e => e!["type"]!.GetValue<string>() == "user"
                                  && e["value"]!.GetValue<string>() == "entra-owner");
        Assert.DoesNotContain(acl, e => e!["value"]!.GetValue<string>() == "restricted-grp");
    }

    [Fact]
    public async Task Enforce_RestrictsRestrictedItemAclToGroup_AndAuditsDecision()
    {
        var summary = await Pipeline(TestConfig.Make(
                allowFullScan: true, classification: true,
                classificationEnforceAcl: true, classificationRestrictedGroupId: "restricted-grp"))
            .RunAsync(fullCrawl: true);
        Assert.Equal(1, summary.Ingested);

        // The Restricted item's ACL is narrowed to exactly the enforcement group.
        var acl = IngestedAcl();
        var only = Assert.Single(acl);
        Assert.Equal("group", only!["type"]!.GetValue<string>());
        Assert.Equal("restricted-grp", only["value"]!.GetValue<string>());
        Assert.Equal("grant", only["accessType"]!.GetValue<string>());
        Assert.DoesNotContain(acl, e => e!["value"]!.GetValue<string>() == "entra-owner");

        // ...and the restriction is recorded in the immutable decision ledger.
        var ledgerPath = Path.Combine(SyncState.LogsDir, "decisions_BdhHadoopMart.jsonl");
        var entry = Assert.Single(File.ReadAllLines(ledgerPath).Where(l => l.Trim().Length > 0)
            .Select(l => JsonNode.Parse(l)!.AsObject()));
        Assert.Equal("ACL_RESTRICTION", entry["decision"]!.GetValue<string>());
        Assert.Equal(RecordId, entry["item_id"]!.GetValue<string>());
        Assert.True(DecisionLedger.Verify(ledgerPath).Ok);
    }

    [Fact]
    public async Task Exclusion_NoResolvableAcl_IsAuditedInLedger()
    {
        // A record owned by an unmapped principal resolves to zero ACL entries →
        // it is withheld from the index, and that EXCLUSION is audited.
        var source = new FakeBdhSource();
        source.Add(
            "Task/dt=2026-07-15/part-0000.jsonl",
            $$"""{"Id":"{{RecordId}}","Name":"Orphan task","OwnerId":"005U9999999"}""");

        var summary = await Pipeline(TestConfig.Make(allowFullScan: true), source: source)
            .RunAsync(fullCrawl: true);
        Assert.Equal(0, summary.Ingested);
        Assert.Equal(1, summary.NoAclSkipped);

        var ledgerPath = Path.Combine(SyncState.LogsDir, "decisions_BdhHadoopMart.jsonl");
        var entry = Assert.Single(File.ReadAllLines(ledgerPath).Where(l => l.Trim().Length > 0)
            .Select(l => JsonNode.Parse(l)!.AsObject()));
        Assert.Equal("EXCLUSION", entry["decision"]!.GetValue<string>());
        Assert.Equal(RecordId, entry["item_id"]!.GetValue<string>());
        Assert.True(DecisionLedger.Verify(ledgerPath).Ok);
    }
}
