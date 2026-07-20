// End-to-end pipeline behaviour against mocked HTTP: version supersede,
// unchanged-version skip, expiry withdrawal, late-flag withdrawal,
// unpublish withdrawal, not-seen withdrawal, ACL skip, checkpoint resume.

using System.Net;
using System.Text.Json.Nodes;
using SeismicConnector.Config;
using SeismicConnector.Graph;
using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector.Tests;

public sealed class PipelineHarness : IDisposable
{
    public AppConfig Config { get; }
    public FakeSeismicClient Seismic { get; } = new();
    public FakeHttpHandler GraphHandler { get; } = new();
    public GraphClient Graph { get; }
    public SqliteIdentityStore Store { get; }
    public IngestPipeline Pipeline { get; }
    public TempStateDir State { get; } = new();
    public List<string> DeletedItemIds { get; } = new();

    /// <summary>Item id → last ACL array PATCHed via the re-ACL path (content never re-sent).</summary>
    public Dictionary<string, JsonNode> AclPatches { get; } = new(StringComparer.Ordinal);

    /// <summary>Ordered list of item ids whose ACL was PATCHed.</summary>
    public List<string> AclPatchedItemIds { get; } = new();

    private readonly string _dbPath;

    public PipelineHarness(
        ExclusionRules? exclusions = null, string fallbackAcl = "skip", bool enrichUsage = false,
        bool liveDocFieldIndexing = false, bool permissionReacl = false,
        bool classification = false, bool classificationEnforceAcl = false,
        string? classificationEnforceGroup = null, int graphItemTtlDays = 0,
        IEnumerable<string>? objects = null,
        int chunkSize = 10, int graphBatchSize = 5, int batchWorkers = 2,
        HaCoordinator? ha = null,
        bool contentGate = false, string? contentGateIcapUrl = null,
        string contentGateBinaryFailMode = "closed", string contentGateTextFailMode = "open",
        long contentGateMaxScanBytes = 25L * 1024 * 1024,
        SeismicConnector.Seismic.ClassificationRules? contentGateRules = null,
        SeismicConnector.Security.IMalwareScanner? malwareScanner = null)
    {
        Config = TestConfig.Build(
            exclusions: exclusions, fallbackAcl: fallbackAcl, enrichUsage: enrichUsage,
            liveDocFieldIndexing: liveDocFieldIndexing, permissionReacl: permissionReacl,
            classification: classification, classificationEnforceAcl: classificationEnforceAcl,
            classificationEnforceGroup: classificationEnforceGroup, graphItemTtlDays: graphItemTtlDays,
            objects: objects, chunkSize: chunkSize, graphBatchSize: graphBatchSize,
            batchWorkers: batchWorkers,
            contentGate: contentGate, contentGateIcapUrl: contentGateIcapUrl,
            contentGateBinaryFailMode: contentGateBinaryFailMode,
            contentGateTextFailMode: contentGateTextFailMode,
            contentGateMaxScanBytes: contentGateMaxScanBytes,
            contentGateRules: contentGateRules);
        _ha = ha;
        _dbPath = Path.Combine(Path.GetTempPath(), "seismic-pipe-" + Guid.NewGuid().ToString("N") + ".db");
        Store = new SqliteIdentityStore(_dbPath);
        Store.UpsertPrincipal(new PrincipalMapping("seismic-user-1", "user", "amy@contoso.com", "entra-user-1", "Amy"));

        GraphHandler.When(HttpMethod.Post, "/$batch", FakeHttpHandler.BatchSuccess);
        GraphHandler.When(HttpMethod.Delete, "/items/", (request, _) =>
        {
            var url = request.RequestUri!.ToString();
            DeletedItemIds.Add(Uri.UnescapeDataString(url[(url.LastIndexOf('/') + 1)..]));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        // ACL-only re-ACL PATCH: record the item id and the ACL body so tests
        // can assert content was NOT re-sent (body has acl only).
        GraphHandler.When(HttpMethod.Patch, "/items/", (request, body) =>
        {
            var url = request.RequestUri!.ToString();
            var itemId = Uri.UnescapeDataString(url[(url.LastIndexOf('/') + 1)..]);
            AclPatchedItemIds.Add(itemId);
            if (body is not null && JsonNode.Parse(body) is JsonObject obj && obj["acl"] is JsonNode acl)
                AclPatches[itemId] = acl.DeepClone();
            return FakeHttpHandler.Json(HttpStatusCode.OK, "{}");
        });

        Graph = new GraphClient(Config.Graph, GraphHandler)
        {
            OverrideAccessToken = "token",
            DelayAsync = (_, _) => Task.CompletedTask,
        };
        Pipeline = new IngestPipeline(Config, Seismic, Graph, Store, ha: _ha, malwareScanner: malwareScanner);
    }

    private readonly HaCoordinator? _ha;

    public void AddTeamsite(
        string id, string name = "", bool restricted = false,
        List<SeismicPermission>? permissions = null)
    {
        Seismic.Teamsites.Add(new SeismicTeamsite
        {
            Id = id,
            Name = name.Length > 0 ? name : $"Teamsite {id}",
            IsRestricted = restricted,
            Permissions = permissions ?? new List<SeismicPermission>(),
        });
        Seismic.ContentsByTeamsite.TryAdd(id, new List<SeismicContent>());
    }

    public void AddContent(SeismicContent content)
    {
        Seismic.ContentsByTeamsite.TryGetValue(content.TeamsiteId, out var list);
        if (list is null)
        {
            list = new List<SeismicContent>();
            Seismic.ContentsByTeamsite[content.TeamsiteId] = list;
        }
        list.Add(content);
        Seismic.Payloads[content.Id] = System.Text.Encoding.UTF8.GetBytes($"payload of {content.Id}");
    }

    /// <summary>Every externalItem id PUT through /$batch, in order.</summary>
    public List<string> PutItemIds() =>
        GraphHandler.Requests
            .Where(r => r.Method == HttpMethod.Post && r.Url.Contains("/$batch"))
            .SelectMany(r => JsonNode.Parse(r.Body!)!["requests"]!.AsArray()
                .Select(req => req!["id"]!.GetValue<string>()))
            .ToList();

    /// <summary>The last PUT body for an item id, or null.</summary>
    public JsonNode? LastPutBody(string itemId) =>
        GraphHandler.Requests
            .Where(r => r.Method == HttpMethod.Post && r.Url.Contains("/$batch"))
            .SelectMany(r => JsonNode.Parse(r.Body!)!["requests"]!.AsArray().Select(n => n!.DeepClone()))
            .LastOrDefault(req => req!["id"]!.GetValue<string>() == itemId)?["body"];

    public void Dispose()
    {
        State.Dispose();
        Store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbPath);
        }
        catch
        {
        }
    }
}

public class PipelineTests
{
    [Fact]
    public async Task FullCrawl_IngestsContentAndLibraryItems()
    {
        using var harness = new PipelineHarness(fallbackAcl: "tenant");
        harness.AddTeamsite("ts1", "Sales Collateral");
        harness.AddContent(TestContent.Make("c1"));
        harness.AddContent(TestContent.Make("c2"));

        var ok = await harness.Pipeline.RunCrawlAsync(fullCrawl: true);

        Assert.True(ok);
        var putIds = harness.PutItemIds();
        Assert.Contains("c1", putIds);
        Assert.Contains("c2", putIds);
        Assert.Contains("lib-ts1", putIds);  // Library object enabled
        Assert.Equal(3, harness.Pipeline.Stats.Ingested);
        Assert.Equal("ingested", harness.Store.GetTrackedItem("c1")!.Status);
        // Crawl completed → checkpoint cleared, last-sync stamped.
        Assert.Null(SyncState.ReadCheckpoint(harness.Config.Connector.Id));
        Assert.NotNull(SyncState.ReadLastSync(harness.Config.Connector.Id));
    }

    [Fact]
    public async Task UnchangedVersion_IsSkipped_NewVersionSupersedesInPlace()
    {
        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", versionId: "v1"));

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));
        Assert.Equal(1, harness.Pipeline.Stats.Ingested);

        // Second crawl, same version → skipped, no second PUT.
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));
        Assert.Equal(1, harness.PutItemIds().Count(id => id == "c1"));
        Assert.True(harness.Pipeline.Stats.Skipped >= 1);

        // Version bumps → PUT over the SAME externalItem id (update in place).
        harness.Seismic.ContentsByTeamsite["ts1"].Clear();
        harness.AddContent(TestContent.Make("c1", versionId: "v2"));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));

        Assert.Equal(2, harness.PutItemIds().Count(id => id == "c1"));
        Assert.Equal("v2", harness.Store.GetTrackedItem("c1")!.VersionId);
        Assert.Equal("v2", harness.LastPutBody("c1")?["properties"]?["versionId"]?.GetValue<string>());
        Assert.Empty(harness.DeletedItemIds);  // supersede is an update, not delete+add
    }

    [Fact]
    public async Task MneFlaggedContent_IsNeverIngested_AndIsReported()
    {
        var rules = new ExclusionRules
        {
            ExcludedFlags = new List<string> { "MNE" },
            FlagProperties = new List<string> { "classification" },
        };
        using var harness = new PipelineHarness(rules);
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("clean"));
        harness.AddContent(TestContent.Make("flagged", properties: new List<SeismicProperty>
        {
            new() { Name = "classification", Value = "MNE" },
        }));

        var report = new ReconciliationReport();
        harness.Pipeline.Report = report;
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));

        var putIds = harness.PutItemIds();
        Assert.Contains("clean", putIds);
        Assert.DoesNotContain("flagged", putIds);
        Assert.Equal(1, harness.Pipeline.Stats.Excluded);
        Assert.Equal(1, report.ExcludedCount);
        Assert.Equal(1, report.CountsByRule["mne-flag"]);
        Assert.Equal("excluded", harness.Store.GetTrackedItem("flagged")!.Status);
    }

    [Fact]
    public async Task LateMneFlag_WithdrawsPreviouslyIngestedItem()
    {
        var rules = new ExclusionRules
        {
            ExcludedFlags = new List<string> { "MNE" },
            FlagProperties = new List<string> { "classification" },
        };
        using var harness = new PipelineHarness(rules);
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1"));

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));
        Assert.Equal("ingested", harness.Store.GetTrackedItem("c1")!.Status);

        // The item gains an MNE flag between crawls.
        harness.Seismic.ContentsByTeamsite["ts1"].Clear();
        harness.AddContent(TestContent.Make("c1", properties: new List<SeismicProperty>
        {
            new() { Name = "classification", Value = "MNE" },
        }));

        var report = new ReconciliationReport();
        harness.Pipeline.Report = report;
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));

        Assert.Contains("c1", harness.DeletedItemIds);        // withdrawn from Graph
        Assert.Equal("excluded", harness.Store.GetTrackedItem("c1")!.Status);
        Assert.Equal(1, report.WithdrawnCount);
        Assert.True(harness.Pipeline.Stats.Withdrawn >= 1);
    }

    [Fact]
    public async Task RestrictedLibrary_ContentIsExcluded_AndPreviouslyIngestedIsWithdrawn()
    {
        // First crawl: nothing restricted.
        var openRules = new ExclusionRules();
        using var harness = new PipelineHarness(openRules);
        harness.AddTeamsite("ts1", "Deal Room");
        harness.AddContent(TestContent.Make("c1"));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));
        Assert.Equal("ingested", harness.Store.GetTrackedItem("c1")!.Status);

        // The library becomes restricted (config change) → whole-teamsite withdrawal.
        var restricted = new ExclusionFilter(new ExclusionRules
        {
            RestrictedLibraries = new List<string> { "Deal Room" },
        });
        var pipeline = new IngestPipeline(
            harness.Config, harness.Seismic, harness.Graph, harness.Store, restricted);
        var report = new ReconciliationReport();
        pipeline.Report = report;

        Assert.True(await pipeline.RunCrawlAsync(fullCrawl: true));
        Assert.Contains("c1", harness.DeletedItemIds);
        Assert.Equal("excluded", harness.Store.GetTrackedItem("c1")!.Status);
        Assert.True(report.WithdrawnCount >= 1);
        Assert.True(report.ExcludedCount >= 1);  // the teamsite-level record
    }

    [Fact]
    public async Task ExpiredContent_IsWithdrawnAndNeverReingested()
    {
        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", expiresAt: DateTime.UtcNow.AddDays(30)));

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));
        Assert.Equal(1, harness.Pipeline.Stats.Ingested);

        // Simulate the expiry date passing: rewrite the tracked row into the past.
        var tracked = harness.Store.GetTrackedItem("c1")!;
        harness.Store.UpsertTrackedItem(tracked with { ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1) });
        // Source now reports the same (expired) content.
        harness.Seismic.ContentsByTeamsite["ts1"].Clear();
        harness.AddContent(TestContent.Make("c1", expiresAt: DateTime.UtcNow.AddMinutes(-1)));

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));
        Assert.Contains("c1", harness.DeletedItemIds);
        Assert.Null(harness.Store.GetTrackedItem("c1"));
        Assert.Equal(1, harness.PutItemIds().Count(id => id == "c1"));  // never re-PUT
    }

    [Fact]
    public async Task UnpublishedContent_IsWithdrawn()
    {
        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1"));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));

        harness.Seismic.ContentsByTeamsite["ts1"].Clear();
        harness.AddContent(TestContent.Make("c1", status: "unpublished"));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));

        Assert.Contains("c1", harness.DeletedItemIds);
        Assert.Null(harness.Store.GetTrackedItem("c1"));
    }

    [Fact]
    public async Task ContentDeletedInSeismic_IsWithdrawnByFullCrawl()
    {
        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1"));
        harness.AddContent(TestContent.Make("c2"));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));

        // c2 disappears from the source entirely.
        harness.Seismic.ContentsByTeamsite["ts1"].RemoveAll(c => c.Id == "c2");
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));

        Assert.Contains("c2", harness.DeletedItemIds);
        Assert.DoesNotContain("c1", harness.DeletedItemIds);
        Assert.Null(harness.Store.GetTrackedItem("c2"));
        Assert.NotNull(harness.Store.GetTrackedItem("c1"));
    }

    [Fact]
    public async Task NoMappablePrincipals_FallbackSkip_ItemNotIngested()
    {
        using var harness = new PipelineHarness();  // fallback skip; only seismic-user-1 mapped
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", permissions: new List<SeismicPermission>
        {
            new() { PrincipalId = "unknown-user", PrincipalType = "user" },
        }));

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));
        Assert.DoesNotContain("c1", harness.PutItemIds());
        Assert.Equal(1, harness.Pipeline.Stats.AclSkipped);
    }

    [Fact]
    public async Task CheckpointResume_SkipsCompletedChunks()
    {
        using var harness = new PipelineHarness(exclusions: new ExclusionRules());
        harness.AddTeamsite("ts1");
        for (var i = 0; i < 12; i++)  // chunk size 10 → 2 chunks
            harness.AddContent(TestContent.Make($"c{i:D2}"));

        // Pretend a previous run completed chunk 1 of ts1 (and the Library chunk).
        SyncState.WriteCheckpoint(harness.Config.Connector.Id, null, "ContentItem:ts1", 1);
        SyncState.WriteCheckpoint(harness.Config.Connector.Id, null, "Library", 1);

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));

        var putIds = harness.PutItemIds();
        // Only the 2 items of chunk 2 were PUT; chunk 1's 10 items were skipped.
        Assert.Equal(2, putIds.Count(id => id.StartsWith("c", StringComparison.Ordinal)));
        Assert.Contains("c10", putIds);
        Assert.Contains("c11", putIds);
        Assert.True(harness.Pipeline.Stats.Skipped >= 10);
    }

    [Fact]
    public async Task BatchFailures_AreDeadLettered_AndCrawlReportsFailure()
    {
        // Custom Graph handler: "bad" fails with 400 inside the $batch envelope.
        using var harness2 = new PipelineHarness(exclusions: new ExclusionRules());
        harness2.AddTeamsite("ts1");
        harness2.AddContent(TestContent.Make("good"));
        harness2.AddContent(TestContent.Make("bad"));
        var handler2 = new FakeHttpHandler();
        handler2.When(HttpMethod.Post, "/$batch", (request, body) =>
        {
            var responses = new JsonArray();
            foreach (var req in JsonNode.Parse(body!)!["requests"]!.AsArray())
            {
                var id = req!["id"]!.GetValue<string>();
                responses.Add(new JsonObject
                {
                    ["id"] = id,
                    ["status"] = id == "bad" ? 400 : 200,
                    ["body"] = id == "bad"
                        ? new JsonObject { ["error"] = new JsonObject { ["message"] = "schema mismatch" } }
                        : null,
                });
            }
            return FakeHttpHandler.Json(HttpStatusCode.OK, new JsonObject { ["responses"] = responses }.ToJsonString());
        });
        var graph = new GraphClient(harness2.Config.Graph, handler2)
        {
            OverrideAccessToken = "token",
            DelayAsync = (_, _) => Task.CompletedTask,
        };
        var pipeline = new IngestPipeline(
            harness2.Config, harness2.Seismic, graph, harness2.Store);

        var ok = await pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem");

        Assert.False(ok);
        Assert.Equal(1, pipeline.Stats.Failed);
        Assert.Equal(1, pipeline.Stats.Ingested);
        var deadLetter = SyncState.ReadFailedRecords(harness2.Config.Connector.Id);
        var record = Assert.Single(deadLetter);
        Assert.Equal("bad", record["item_id"]?.GetValue<string>());
        Assert.Equal("schema mismatch", record["error"]?.GetValue<string>());
        // Failed item is NOT tracked as ingested.
        Assert.Null(harness2.Store.GetTrackedItem("bad"));
        Assert.NotNull(harness2.Store.GetTrackedItem("good"));
    }

    [Fact]
    public async Task IngestSingle_IngestsOneItem()
    {
        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1"));

        Assert.True(await harness.Pipeline.IngestSingleAsync("c1", "ts1"));
        Assert.Equal(new[] { "c1" }, harness.PutItemIds());
        Assert.NotNull(harness.Store.GetTrackedItem("c1"));
    }

    [Fact]
    public async Task WebhookDeleteEvent_WithdrawsTrackedItem()
    {
        using var harness = new PipelineHarness();
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1"));
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));

        await harness.Pipeline.ProcessEventsAsync(new[]
        {
            new ContentEvent { Type = "contentDeleted", ContentId = "c1" },
        });

        Assert.Contains("c1", harness.DeletedItemIds);
        Assert.Null(harness.Store.GetTrackedItem("c1"));
    }

    [Fact]
    public async Task IncrementalCrawl_UsesModifiedSinceCursor()
    {
        using var harness = new PipelineHarness(exclusions: new ExclusionRules());
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("old", modifiedAt: DateTime.UtcNow.AddDays(-10)));

        // Simulate a previous sync 5 days ago.
        SyncState.WriteLastSync(harness.Config.Connector.Id, DateTime.UtcNow.AddDays(-5));
        harness.AddContent(TestContent.Make("new", modifiedAt: DateTime.UtcNow.AddHours(-1)));

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: false, objectTypeFilter: "ContentItem"));

        var putIds = harness.PutItemIds();
        Assert.Contains("new", putIds);
        Assert.DoesNotContain("old", putIds);  // filtered by the modifiedAt cursor
    }
}
