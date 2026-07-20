// Verifier probes, round 6 — two deployment-blocking defects.
//
// FIX 1 — SCHEMA DRIFT. The content gate stamps ContentGateStatus on every item,
//   but that property was never declared in config/graph-schema.json. Microsoft
//   Graph rejects properties that are not in the registered connection schema,
//   so the whole feature was undeployable as shipped — and invisibly so, because
//   nothing in the build or the suite compares what the CODE stamps against what
//   the SCHEMA declares. D1/D2 pin the declaration; D3 is the drift GUARD, and
//   D4 proves the guard actually fails when a property goes undeclared (a guard
//   that cannot fail is not a guard).
//
// FIX 2 — RESIDUAL BINARY BYPASS. The text half of "fail-open must not report
//   clean" was fixed (round 5); the BINARY half still returned GateVerdict.Pass,
//   so an attachment whose bytes no scanner ever saw was indexed AND stamped
//   'clean'. B1-B6 close it at the stage, the enricher seam, and end-to-end.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClarizenConnector.AclEngine;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Content;
using ClarizenConnector.ContentGate;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;
using ClarizenConnector.Item;

namespace ClarizenConnector.Tests;

// ── FIX 1: the Graph connection schema must declare everything we publish ─────

public class VerifierRound6SchemaDriftProbes
{
    private static string GraphSchemaPath =>
        Path.Combine(AppContext.BaseDirectory, "config", "graph-schema.json");

    private static string SchemaPath =>
        Path.Combine(AppContext.BaseDirectory, "config", "schema.json");

    /// <summary>Every property name declared in config/graph-schema.json.</summary>
    private static HashSet<string> DeclaredProperties()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(GraphSchemaPath));
        return document.RootElement
            .EnumerateArray()
            .Select(property => property.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The Graph property names StampedPropertyInventory OBSERVES the connector
    /// stamping, over the real schema.json.
    /// <para>
    /// CORRECTION (round 9): this used to be documented as "every property the
    /// connector can stamp". It is not. The inventory executes a maintained set
    /// of stamper CALL SITES, so a stamp written anywhere it does not invoke —
    /// proven with a bare literal added to the crawl loop in Graph/Ingest.cs —
    /// is invisible to it and leaves D3/D5 green. These probes therefore pin the
    /// EARLY-WARNING signal, not the guarantee. The guarantee is the write path
    /// (GraphPropertyBag rejecting the stamp, and IngestPipeline refusing to
    /// dead-letter that rejection); see VerifierProbesRound9.cs.
    /// </para>
    /// </summary>
    private static HashSet<string> StampedProperties() =>
        StampedPropertyInventory.Collect(SchemaConfig.Load(SchemaPath))
            .ToHashSet(StringComparer.Ordinal);

    // D1: the specific defect — the gate's own property was missing entirely.
    [Fact]
    public void D1_ContentGateStatus_IsDeclaredInTheGraphSchema()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(GraphSchemaPath));
        var property = document.RootElement
            .EnumerateArray()
            .SingleOrDefault(p =>
                p.GetProperty("name").GetString() == ContentGateStage.StatusProperty);

        Assert.NotEqual(JsonValueKind.Undefined, property.ValueKind);
        Assert.Equal("String", property.GetProperty("type").GetString());
    }

    // D2: and it is declared the way its siblings are — a declaration with the
    // wrong flags is as undeployable as no declaration for the uses it has
    // (filtering quarantined/incomplete items out of a Copilot vertical).
    [Fact]
    public void D2_ContentGateStatus_MatchesTheSiblingStatusPropertyShape()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(GraphSchemaPath));
        var properties = document.RootElement.EnumerateArray().ToList();

        JsonElement ByName(string name) =>
            properties.Single(p => p.GetProperty("name").GetString() == name);

        var gate = ByName(ContentGateStage.StatusProperty);
        var sibling = ByName(AttachmentEnricher.StatusProperty);

        foreach (var flag in new[] { "isSearchable", "isRetrievable", "isQueryable", "isRefinable" })
        {
            Assert.Equal(
                sibling.GetProperty(flag).GetBoolean(),
                gate.GetProperty(flag).GetBoolean());
        }

        Assert.True(gate.GetProperty("isRetrievable").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(gate.GetProperty("description").GetString()));
    }

    // D3: THE DRIFT GUARD. Fails the build the moment the code stamps a property
    // the connection schema does not declare — the class of defect that is
    // otherwise invisible until a real Graph deployment rejects the item.
    [Fact]
    public void D3_EveryPropertyTheCodeStamps_IsDeclaredInTheGraphSchema()
    {
        var undeclared = StampedProperties().Except(DeclaredProperties()).OrderBy(n => n).ToList();

        Assert.True(
            undeclared.Count == 0,
            "config/graph-schema.json does not declare the following property name(s) that the "
            + $"connector stamps on external items: {string.Join(", ", undeclared)}. Microsoft Graph "
            + "rejects undeclared properties, so shipping this makes the connector undeployable. "
            + "Declare them in config/graph-schema.json.");
    }

    // D4: the guard can actually fail.
    //
    // The version this replaced was DECORATIVE. It mutated a local HashSet and
    // asserted set arithmetic on it:
    //     stamped.Add("NotInTheSchema");
    //     Assert.Contains("NotInTheSchema", stamped.Except(declared));
    // which exercises no production code and cannot fail no matter how broken
    // the guard is — it is a proof that HashSet.Except works. That reads as
    // coverage while providing none, which is worse than an absent test.
    //
    // This version drives a genuinely undeclared property through the REAL
    // production path — the property bag on a real ExternalItem — and asserts
    // the connector refuses it.
    [Fact]
    public void D4_TheDriftGuard_RejectsAGenuinelyUndeclaredProperty()
    {
        Assert.NotEmpty(DeclaredProperties());
        Assert.NotEmpty(StampedProperties());

        const string undeclared = "ThisPropertyIsNotInTheGraphSchema";
        Assert.DoesNotContain(undeclared, DeclaredProperties());

        var item = new ExternalItem { Id = "Task_1" };

        // 1. The stamp itself is refused, naming the property.
        var thrown = Assert.Throws<UndeclaredGraphPropertyException>(
            () => item.Properties[undeclared] = "value");
        Assert.Equal(undeclared, thrown.PropertyName);
        Assert.Contains(undeclared, thrown.Message, StringComparison.Ordinal);

        // 2. Nothing was written, so no Graph body can carry it.
        Assert.False(item.Properties.ContainsKey(undeclared));
        Assert.Null(item.ToJson()["properties"]![undeclared]);

        // 3. A declared property on the very same item still works — the guard
        //    rejects the undeclared name, not writes in general.
        item.Properties["Title"] = "ok";
        Assert.Equal("ok", item.ToJson()["properties"]!["Title"]!.GetValue<string>());
    }

    // D5: the reverse direction, reported as the task asked. Not an error —
    // a declared-but-unstamped property is merely dead schema — but it is worth
    // knowing about, so it is asserted rather than left to inspection.
    [Fact]
    public void D5_NoDeclaredPropertyIsUnused()
    {
        var unused = DeclaredProperties().Except(StampedProperties()).OrderBy(n => n).ToList();
        Assert.True(
            unused.Count == 0,
            "config/graph-schema.json declares property name(s) nothing stamps: "
            + string.Join(", ", unused));
    }
}

// ── FIX 2: unscannable BINARY is never 'clean' (stage level) ──────────────────

public class VerifierRound6BinaryFailOpenProbes : IDisposable
{
    public VerifierRound6BinaryFailOpenProbes() => Metrics.ResetForTests();

    public void Dispose() => Metrics.ResetForTests();

    private static AppConfig Config(string binaryFailMode = "closed", int maxScanMb = 16) =>
        TestConfig.Make(
            contentGate: true,
            contentGateBinaryFailMode: binaryFailMode,
            contentGateMaxScanMb: maxScanMb);

    private static ContentGateStage Stage(AppConfig config, IMalwareScanner? malware) =>
        new(config, InjectionScanner.Load(), malware);

    // B1: THE DEFECT. Scanner outage + fail-open used to return Pass, which is
    // the connector asserting "we scanned this and it was clean" about bytes no
    // scanner ever saw. Fail-open still INDEXES (that is the documented mode) —
    // it just may not claim the content is clean.
    [Fact]
    public async Task B1_UnscannableBinary_FailOpen_IsIncompleteNotPass()
    {
        var verdict = await Stage(Config("open"), FakeMalwareScanner.AlwaysUnavailable())
            .ScanBinaryAsync(new byte[] { 1, 2, 3 }, "invoice.docx");

        Assert.False(verdict.IsBlocked);                            // fail-open still indexes
        Assert.Equal(GateOutcome.Incomplete, verdict.Outcome);      // but never "clean"
        Assert.True(verdict.IsIncomplete);
        Assert.Equal(GateCategories.MalwareUnscannable, verdict.Category);
        Assert.Equal(1, Metrics.ContentGateScanUnavailableFor("binary"));   // and never silently
    }

    // B2: the stamp is the false-assurance surface that actually reaches Graph.
    [Fact]
    public async Task B2_UnscannableBinary_FailOpen_StampIsIncompleteNeverClean()
    {
        var verdict = await Stage(Config("open"), FakeMalwareScanner.AlwaysUnavailable())
            .ScanBinaryAsync(new byte[] { 1 }, "invoice.docx");

        var item = new ExternalItem { Id = "Attachment_1" };
        ContentGateStage.Stamp(item, verdict, enabled: true);

        Assert.Equal(
            ContentGateStage.IncompletePrefix + GateCategories.MalwareUnscannable,
            item.Properties[ContentGateStage.StatusProperty]);
        Assert.NotEqual(
            ContentGateStage.CleanStatus,
            item.Properties[ContentGateStage.StatusProperty]);
    }

    // B3: the other two fail-open routes into the same helper — no scanner wired,
    // and content over the scan cap — must behave identically. The cap route in
    // particular is the one an operator hits without any outage at all.
    [Theory]
    [InlineData("no-scanner")]
    [InlineData("over-cap")]
    public async Task B3_EveryUnscannableBinaryRoute_FailOpen_IsIncomplete(string route)
    {
        var verdict = route == "no-scanner"
            ? await Stage(Config("open"), malware: null)
                .ScanBinaryAsync(new byte[] { 1 }, "x.docx")
            : await Stage(Config("open", maxScanMb: 1), FakeMalwareScanner.AlwaysClean())
                .ScanBinaryAsync(new byte[2 * 1024 * 1024], "big.docx");

        Assert.False(verdict.IsBlocked);
        Assert.True(verdict.IsIncomplete);
        Assert.Equal(GateCategories.MalwareUnscannable, verdict.Category);
        Assert.Equal(1, Metrics.ContentGateScanUnavailableFor("binary"));
    }

    // B4: the documented DEFAULT is unchanged — binary fails CLOSED. The fix
    // must not have quietly relaxed the fail mode while making Pass honest.
    [Fact]
    public async Task B4_UnscannableBinary_FailClosed_StillBlocks_ByDefault()
    {
        var stage = Stage(Config(), FakeMalwareScanner.AlwaysUnavailable());
        Assert.True(stage.BinaryFailClosed);

        var verdict = await stage.ScanBinaryAsync(new byte[] { 1 }, "x.docx");

        Assert.True(verdict.IsBlocked);
        Assert.False(verdict.IsIncomplete);
        Assert.Equal(GateCategories.MalwareUnscannable, verdict.Category);
    }

    // B5: no over-correction. A binary the scanner actually read and cleared is
    // still a genuine Pass, and infected bytes still Block.
    [Fact]
    public async Task B5_ScannedBinary_KeepsItsRealVerdict()
    {
        var clean = await Stage(Config("open"), FakeMalwareScanner.AlwaysClean())
            .ScanBinaryAsync(new byte[] { 1 }, "ok.docx");
        Assert.Equal(GateOutcome.Clean, clean.Outcome);
        Assert.False(clean.IsIncomplete);
        Assert.Equal(0, Metrics.ContentGateScanUnavailableFor("binary"));

        var infected = await Stage(Config("open"), FakeMalwareScanner.AlwaysInfected())
            .ScanBinaryAsync(new byte[] { 1 }, "bad.docx");
        Assert.True(infected.IsBlocked);
        Assert.Equal(GateCategories.Malware, infected.Category);
    }

    // B6: the gate off is still a strict no-op — Pass, no property, no metric.
    [Fact]
    public async Task B6_GateDisabled_IsStillAStrictNoOp()
    {
        var config = TestConfig.Make(contentGate: false, contentGateBinaryFailMode: "open");
        var verdict = await Stage(config, FakeMalwareScanner.AlwaysUnavailable())
            .ScanBinaryAsync(new byte[] { 1 }, "x.docx");

        Assert.Equal(GateOutcome.Clean, verdict.Outcome);
        Assert.Equal(0, Metrics.ContentGateScanUnavailableFor("binary"));
    }
}

// ── FIX 2: the verdict survives the enricher seam and reaches Graph ───────────

public class VerifierRound6BinaryFailOpenSeamProbes : IDisposable
{
    public VerifierRound6BinaryFailOpenSeamProbes() => Metrics.ResetForTests();

    public void Dispose() => Metrics.ResetForTests();

    private static ObjectConfig AttachmentConfig() => new()
    {
        ObjectName = "Attachment",
        AclMode = "ownerOnly",
        AttachmentUrlField = "DownloadUrl",
        AttachmentNameField = "Name",
        SelectedFields = new Dictionary<string, string>
        {
            ["Name"] = "Title",
            ["DownloadUrl"] = "_cz_DownloadUrl",
        },
    };

    private static ClarizenRecord Record() =>
        new("Attachment", new JsonObject
        {
            ["id"] = "/Attachment/7",
            ["Name"] = "report.txt",
            ["DownloadUrl"] = "https://cz/file",
        });

    private static ExternalItem Item() => new()
    {
        Id = "Attachment_7",
        Acl = { new AclEntry(AclEntryType.User, "u1", AclAccessType.Grant) },
    };

    private static AttachmentEnricher Enricher(AppConfig config, byte[] payload) =>
        new(
            config,
            new FakeAttachmentDownloader((_, _) => DownloadResult.Success(payload)),
            new ContentExtractor(),
            new ContentGateStage(config, InjectionScanner.Load(), FakeMalwareScanner.AlwaysUnavailable()));

    // B7: the enricher seam. Fail-open indexes the attachment text (unchanged
    // posture, asserted), but the item now CARRIES the incomplete verdict. Before
    // the fix the Pass verdict was dropped on the floor here and the item went to
    // Graph with nothing recording that its bytes were never scanned.
    [Fact]
    public async Task B7_Enricher_FailOpen_IndexesContent_ButStampsIncomplete()
    {
        var item = Item();
        var config = TestConfig.Make(
            attachmentIngestion: true, contentGate: true, contentGateBinaryFailMode: "open");

        var status = await Enricher(config, Encoding.UTF8.GetBytes("some attachment text"))
            .EnrichAsync(item, Record(), AttachmentConfig());

        Assert.Equal("extracted", status);                                        // posture unchanged
        Assert.Contains("some attachment text", item.Content, StringComparison.Ordinal);
        Assert.Equal(
            ContentGateStage.IncompletePrefix + GateCategories.MalwareUnscannable,
            item.Properties[ContentGateStage.StatusProperty]);
        Assert.Equal(1, Metrics.ContentGateScanUnavailableFor("binary"));
    }

    // B8: fail-closed at the seam is unchanged — nothing derived from the
    // unscanned bytes is appended to the item.
    [Fact]
    public async Task B8_Enricher_FailClosed_StillWithholdsTheContent()
    {
        var item = Item();
        var config = TestConfig.Make(
            attachmentIngestion: true, contentGate: true, contentGateBinaryFailMode: "closed");

        var status = await Enricher(config, Encoding.UTF8.GetBytes("some attachment text"))
            .EnrichAsync(item, Record(), AttachmentConfig());

        Assert.Equal("skipped:" + GateCategories.MalwareUnscannable, status);
        Assert.DoesNotContain("some attachment text", item.Content, StringComparison.Ordinal);
        Assert.Equal(
            ContentGateStage.BlockedPrefix + GateCategories.MalwareUnscannable,
            item.Properties[ContentGateStage.StatusProperty]);
    }
}

// ── FIX 2 end-to-end: the item really reaches Graph stamped incomplete ────────

public class VerifierRound6BinaryFailOpenPipelineProbes : IDisposable
{
    private const string Connector = "ClarizenAdaptiveWork";

    private readonly TempDir _dir = new();
    private readonly SyncStateScope _stateScope = new();
    private readonly IdentityStore _store;
    private readonly SchemaConfig _schema;
    private readonly FakeGraphClient _graph;

    public VerifierRound6BinaryFailOpenPipelineProbes()
    {
        Metrics.ResetForTests();
        _store = new IdentityStore("GateR6", Path.Combine(_dir.Path, "identity.db"));
        _store.Upsert(new PrincipalMapping(
            "/User/1", "user", "o@example.com", "entra-owner", DateTime.UtcNow));
        _schema = new SchemaConfig
        {
            ObjectList = new List<ObjectConfig>
            {
                new()
                {
                    ObjectName = "Attachment",
                    DisplayName = "Attachment",
                    AclMode = "ownerOnly",
                    AttachmentUrlField = "DownloadUrl",
                    AttachmentNameField = "Name",
                    SelectedFields = new Dictionary<string, string>
                    {
                        ["Name"] = "Title",
                        ["DownloadUrl"] = "_cz_DownloadUrl",
                        ["CreatedBy"] = "CreatedBy",
                    },
                },
            },
        };
        _graph = new FakeGraphClient(TestConfig.Make());
    }

    public void Dispose()
    {
        Metrics.ResetForTests();
        _store.Dispose();
        _stateScope.Dispose();
        _dir.Dispose();
    }

    private ClarizenClient Clarizen()
    {
        var handler = new MockHttpHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/authentication/login"))
                return MockHttpHandler.Json(HttpStatusCode.OK, """{"sessionId": "s"}""");
            var page = new JsonObject
            {
                ["entities"] = new JsonArray(new JsonObject
                {
                    ["id"] = "/Attachment/7",
                    ["Name"] = "spec.txt",
                    ["DownloadUrl"] = "https://cz.example/download/7",
                    ["CreatedBy"] = new JsonObject { ["id"] = "/User/1" },
                }),
                ["paging"] = new JsonObject { ["hasMore"] = false },
            };
            return MockHttpHandler.Json(HttpStatusCode.OK, page.ToJsonString());
        });
        var client = new ClarizenClient(
            TestConfig.Make(), new ApiBudget(1_000_000, callsPerMinute: 6_000_000), handler);
        client.DelayAsync = (_, _) => Task.CompletedTask;
        return client;
    }

    private IngestPipeline Pipeline(AppConfig config)
    {
        var mapper = new PrincipalMapper(_store, "{}");
        var resolver = new AclResolver(
            mapper, new DirectorySnapshot(), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
        var gate = new ContentGateStage(
            config, InjectionScanner.Load(), FakeMalwareScanner.AlwaysUnavailable());
        var enricher = new AttachmentEnricher(
            config,
            new FakeAttachmentDownloader((_, _) =>
                DownloadResult.Success(Encoding.UTF8.GetBytes("Perfectly ordinary attachment prose."))),
            new ContentExtractor(),
            gate);
        return new IngestPipeline(
            config, _schema, Clarizen(), _graph, resolver, new ItemConverter(config),
            ha: null,
            inventoryFactory: id => new ItemInventory(id, Path.Combine(_dir.Path, $"inv_{id}.db")),
            attachmentEnricher: enricher, sensitivityClassifier: null, contentGate: gate);
    }

    // B9: the whole point of the defect. The attachment's bytes were never
    // scanned; its extracted text scans clean; the item is PUT to Graph. It must
    // NOT arrive carrying ContentGateStatus="clean" — the later item-level TEXT
    // scan knows nothing about the unscanned bytes and must not overwrite the
    // incomplete verdict with a clean bill of health.
    [Fact]
    public async Task B9_UnscannedAttachment_IsIndexed_ButNeverStampedClean()
    {
        var config = TestConfig.Make(
            attachmentIngestion: true, contentGate: true, contentGateBinaryFailMode: "open");

        var summary = await Pipeline(config).RunAsync(fullCrawl: true);

        Assert.Equal(1, summary.Ingested);                 // fail-open: still indexed
        Assert.Equal(0, summary.Quarantined);
        var put = Assert.Single(_graph.Sent, s => s.Method == HttpMethod.Put);
        var status = put.Body!.AsObject()["properties"]!
            .AsObject()[ContentGateStage.StatusProperty]!.GetValue<string>();

        Assert.NotEqual(ContentGateStage.CleanStatus, status);
        Assert.Equal(
            ContentGateStage.IncompletePrefix + GateCategories.MalwareUnscannable, status);
        Assert.Equal(1, Metrics.ContentGateScanUnavailableFor("binary"));
        Assert.Empty(SyncState.ReadFailedRecords(Connector));
    }

    // B10: the same crawl under the DEFAULT binary fail mode is quarantined, not
    // indexed — the asymmetry the gate documents is intact.
    [Fact]
    public async Task B10_UnscannedAttachment_FailClosed_IsQuarantined()
    {
        var config = TestConfig.Make(
            attachmentIngestion: true, contentGate: true, contentGateBinaryFailMode: "closed");

        var summary = await Pipeline(config).RunAsync(fullCrawl: true);

        Assert.Equal(0, summary.Ingested);
        Assert.Equal(1, summary.Quarantined);
        Assert.DoesNotContain(_graph.Sent, s => s.Method == HttpMethod.Put);
        Assert.NotEmpty(SyncState.ReadFailedRecords(Connector));
    }
}
