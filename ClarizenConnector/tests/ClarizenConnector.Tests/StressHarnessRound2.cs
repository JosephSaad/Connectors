// StressHarnessRound2.cs
// ----------------------
// Round-2 stress scenarios (new dimensions on top of StressHarness.cs; nothing
// from round 1 is repeated). Everything runs in-process against offline fakes —
// no network. Every scenario asserts a hard invariant AND emits REAL measured
// numbers via StressLog.
//
// Scenarios:
//   1. TDW bulk export path at scale (100k+ rows) with malformed/truncated rows
//      mixed in — per-row error isolation (bad rows dead-lettered, good rows
//      ingested), checkpoint correctness mid-export, bounded memory.
//   2. Financial-field governance at scale — filter mode never leaks a value
//      (properties OR content), acl mode always restricts, classifier
//      throughput, ReDoS timeout guard.
//   3. Clarizen-side and Graph-side breakers interacting (open vs half-open in
//      both directions), cancellation storms mid-transition, clean resume.
//   4. HA lease contention on ONE shared SQLite DB — failover storm with
//      exactly-once claim/close semantics, plus multi-node writers on one
//      SQLite inventory file (no lost writes, no unhandled "database is locked").
//   5. Incremental crawl churn soak — interleaved webhook events + deletions;
//      final inventory/Graph state exactly matches source truth (drift = 0).

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClarizenConnector.AclEngine;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Content;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;
using ClarizenConnector.Item;
using ClarizenConnector.Webhook;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace ClarizenConnector.Tests;

// ── Shared round-2 fixtures ──────────────────────────────────────────────────

/// <summary>Thread-safe virtual clock (Interlocked ticks) shared by breakers.</summary>
internal sealed class ClockBox
{
    private long _ticks;

    public ClockBox(DateTime start) => _ticks = start.Ticks;

    public DateTime Now => new(Interlocked.Read(ref _ticks), DateTimeKind.Utc);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

/// <summary>
/// Thread-safe GraphClient stand-in that APPLIES puts/deletes to a dictionary
/// (id → last PUT body), so the test can compare final Graph state against
/// source truth exactly. Safe under the pipeline's parallel batch windows
/// (unlike FakeGraphClient.Sent, which is a plain list).
/// </summary>
internal sealed class RecordingGraphClient : GraphClient
{
    private readonly object _gate = new();
    private long _puts;
    private long _deletes;

    public Dictionary<string, JsonObject> Applied { get; } = new(StringComparer.Ordinal);

    public long Puts => Interlocked.Read(ref _puts);

    public long Deletes => Interlocked.Read(ref _deletes);

    /// <summary>Invoked after every applied request (hook for cancellation tests).</summary>
    public Action? OnApplied { get; set; }

    public RecordingGraphClient(AppConfig config) : base(config) => OverrideToken = "t";

    public override Task<GraphResponse> SendWithRetryAsync(
        HttpMethod method, string path, JsonNode? body, CancellationToken ct = default)
    {
        if (path == "$batch" && body is JsonObject envelope
            && envelope["requests"] is JsonArray requests)
        {
            var responses = new JsonArray();
            foreach (var request in requests)
            {
                var obj = (JsonObject)request!;
                var url = obj["url"]!.GetValue<string>();
                var itemId = url[(url.LastIndexOf('/') + 1)..];
                var m = obj["method"]!.GetValue<string>();
                lock (_gate)
                {
                    if (m == "PUT")
                    {
                        Applied[itemId] = (JsonObject)obj["body"]!.DeepClone();
                        Interlocked.Increment(ref _puts);
                    }
                    else if (m == "DELETE")
                    {
                        Applied.Remove(itemId);
                        Interlocked.Increment(ref _deletes);
                    }
                }
                responses.Add(new JsonObject
                {
                    ["id"] = obj["id"]!.GetValue<string>(),
                    ["status"] = 200,
                });
            }
            OnApplied?.Invoke();
            return Task.FromResult(new GraphResponse
            {
                StatusCode = HttpStatusCode.OK,
                Body = new JsonObject { ["responses"] = responses },
            });
        }

        var id = path[(path.LastIndexOf('/') + 1)..];
        if (method == HttpMethod.Put)
        {
            lock (_gate)
            {
                Applied[id] = body is JsonObject single
                    ? (JsonObject)single.DeepClone()
                    : new JsonObject();
                Interlocked.Increment(ref _puts);
            }
            OnApplied?.Invoke();
        }
        else if (method == HttpMethod.Delete)
        {
            lock (_gate)
            {
                Applied.Remove(id);
                Interlocked.Increment(ref _deletes);
            }
            OnApplied?.Invoke();
        }
        return Task.FromResult(new GraphResponse { StatusCode = HttpStatusCode.OK });
    }

    public HashSet<string> AppliedIds()
    {
        lock (_gate)
            return new HashSet<string>(Applied.Keys, StringComparer.Ordinal);
    }

    public string? AppliedTitle(string itemId)
    {
        lock (_gate)
        {
            return Applied.TryGetValue(itemId, out var body)
                ? body["properties"]?["Title"]?.GetValue<string>()
                : null;
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Scenario 1 — TDW bulk export path at scale + per-row isolation + checkpoints
// ═════════════════════════════════════════════════════════════════════════════

public class TdwBulkScaleStressTests : IDisposable
{
    private const string Connector = "ClarizenAdaptiveWork";
    private readonly ITestOutputHelper _out;

    public TdwBulkScaleStressTests(ITestOutputHelper output)
    {
        _out = output;
        Metrics.ResetForTests();
    }

    public void Dispose() => Metrics.ResetForTests();

    private static SchemaConfig Schema() => new()
    {
        ObjectList = new List<ObjectConfig>
        {
            new()
            {
                ObjectName = "Task",
                DisplayName = "Task",
                AclMode = "ownerOnly",
                SelectedFields = new Dictionary<string, string>
                {
                    ["Name"] = "Title",
                    ["Owner"] = "Owner",
                    ["State"] = "State",
                },
            },
        },
    };

    private static (IngestPipeline Pipeline, RecordingGraphClient Graph, IdentityStore Store,
        Func<string, IItemInventory> InvFactory)
        BuildPipeline(TempDir dir, AppConfig config)
    {
        var handler = new MockHttpHandler((_, _) =>
            MockHttpHandler.Json(HttpStatusCode.OK, """{"sessionId": "s"}"""));
        var clarizen = new ClarizenClient(
            config, new ApiBudget(100_000_000, callsPerMinute: 600_000_000), handler);
        clarizen.DelayAsync = (_, _) => Task.CompletedTask;
        var store = new IdentityStore("TdwScale", Path.Combine(dir.Path, "id.db"));
        store.Upsert(new PrincipalMapping("/User/1", "user", "o@x.com", "entra-o", DateTime.UtcNow));
        var mapper = new PrincipalMapper(store, "{}");
        var resolver = new AclResolver(mapper, new DirectorySnapshot(), string.Empty, string.Empty);
        var graph = new RecordingGraphClient(config);
        var invFactory = (Func<string, IItemInventory>)(id =>
            new ItemInventory(id, Path.Combine(dir.Path, $"inv_{id}.db")));
        var pipeline = new IngestPipeline(
            config, Schema(), clarizen, graph, resolver, new ItemConverter(config),
            ha: null, inventoryFactory: invFactory);
        return (pipeline, graph, store, invFactory);
    }

    /// <summary>Row kinds for the synthetic export.</summary>
    private enum RowKind
    {
        Good = 0,        // string id "/Task/gN"
        NumericId = 1,   // TDW-style bare numeric id (valid, must ingest)
        Poisoned = 2,    // malformed reference value → per-row transform failure
        IdLess = 3,      // id is an object → unusable id, dropped by validation
    }

    private static List<RowKind> BuildKinds(int good, int numeric, int poisoned, int idless)
    {
        var kinds = new List<RowKind>(good + numeric + poisoned + idless);
        kinds.AddRange(Enumerable.Repeat(RowKind.Good, good));
        kinds.AddRange(Enumerable.Repeat(RowKind.NumericId, numeric));
        kinds.AddRange(Enumerable.Repeat(RowKind.Poisoned, poisoned));
        kinds.AddRange(Enumerable.Repeat(RowKind.IdLess, idless));
        var rng = new Random(42);                       // deterministic spread
        for (var i = kinds.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (kinds[i], kinds[j]) = (kinds[j], kinds[i]);
        }
        return kinds;
    }

    private static void WriteExport(string path, IReadOnlyList<RowKind> kinds)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        w.Write('[');
        int g = 0, n = 0, p = 0, x = 0;
        for (var i = 0; i < kinds.Count; i++)
        {
            if (i > 0)
                w.Write(',');
            switch (kinds[i])
            {
                case RowKind.Good:
                    w.Write($"{{\"id\":\"/Task/g{g}\",\"Name\":\"Task g{g}\","
                            + "\"Owner\":{\"id\":\"/User/1\"},\"State\":\"Active\"}");
                    g++;
                    break;
                case RowKind.NumericId:
                    // Bare NUMERIC entity id, exactly as a TDW warehouse export
                    // would ship it. Must be a valid row, not a crash.
                    w.Write($"{{\"id\":{900000 + n},\"Name\":\"Task num{n}\","
                            + "\"Owner\":{\"id\":\"/User/1\"},\"State\":\"Active\"}");
                    n++;
                    break;
                case RowKind.Poisoned:
                    // A reference object whose display name is a NUMBER — the
                    // converter's reference flattening chokes on this one row.
                    w.Write($"{{\"id\":\"/Task/p{p}\",\"Name\":\"Task p{p}\","
                            + "\"Owner\":{\"id\":\"/User/1\"},\"State\":{\"name\":123}}");
                    p++;
                    break;
                default:
                    // id present but structurally unusable (object, not scalar).
                    w.Write($"{{\"id\":{{\"broken\":true}},\"Name\":\"Task x{x}\","
                            + "\"Owner\":{\"id\":\"/User/1\"},\"State\":\"Active\"}");
                    x++;
                    break;
            }
        }
        w.Write(']');
    }

    // REGRESSION (HIGH, red→green): one malformed row must NOT abort the whole
    // TDW export. Before the fix (a) a bare numeric id threw from
    // ClarizenRecord.RawId inside id validation and (b) a poisoned reference
    // value threw from ItemConverter inside the chunk transform — either way the
    // whole object worker crashed (WORKER_CRASH), every good row was lost and
    // nothing row-level was dead-lettered.
    [Fact]
    public async Task TdwExport_MalformedRows_AreIsolated_GoodRowsIngested()
    {
        using var dir = new TempDir();
        using var state = new SyncStateScope();
        var export = Path.Combine(dir.Path, "tdw");
        Directory.CreateDirectory(export);

        // 6 good + 2 numeric-id (valid!) + 2 poisoned rows in one small export.
        var kinds = new List<RowKind>
        {
            RowKind.Good, RowKind.NumericId, RowKind.Poisoned, RowKind.Good, RowKind.Good,
            RowKind.Poisoned, RowKind.NumericId, RowKind.Good, RowKind.Good, RowKind.Good,
        };
        WriteExport(Path.Combine(export, "Task.json"), kinds);

        var config = TestConfig.Make(tdwExportPath: export, ingestChunkSize: 100, graphBatchSize: 20);
        var (pipeline, graph, store, invFactory) = BuildPipeline(dir, config);
        using var storeScope = store;

        var summary = await pipeline.RunAsync(fullCrawl: true);

        // All 8 usable rows ingested; the 2 poisoned rows dead-lettered
        // individually; NO worker-crash abort.
        Assert.Equal(8, summary.Ingested);
        Assert.Equal(2, summary.Failed);
        Assert.Empty(summary.FailedObjects);
        var applied = graph.AppliedIds();
        Assert.Equal(8, applied.Count);
        Assert.Contains("Task_900000", applied);   // numeric TDW id ingested
        Assert.Contains("Task_900001", applied);
        Assert.DoesNotContain(applied, id => id.StartsWith("Task_p", StringComparison.Ordinal));

        var deadLetter = SyncState.ReadFailedRecords(Connector);
        Assert.Equal(2, deadLetter.Count);
        Assert.All(deadLetter, r => Assert.StartsWith(
            "Task_p", r["item_id"]!.GetValue<string>(), StringComparison.Ordinal));
        Assert.DoesNotContain(deadLetter, r => r["item_id"]!.GetValue<string>() == "WORKER_CRASH");

        using (var inv = invFactory(Connector))
            Assert.Equal(8, inv.Count());
        StressLog.Line(_out,
            "[tdw] per-row isolation: 2 poisoned rows dead-lettered individually, "
            + "8/8 good rows (incl. 2 numeric TDW ids) ingested, no WORKER_CRASH — PASS");
    }

    // REGRESSION (MEDIUM, red→green): a cancellation that lands MID-CHUNK is a
    // graceful stop, not a worker crash — nothing may be dead-lettered as
    // WORKER_CRASH and the run must report Stopped with its checkpoint retained.
    [Fact]
    public async Task CancellationMidChunk_IsGracefulStop_NotWorkerCrash()
    {
        using var dir = new TempDir();
        using var state = new SyncStateScope();
        var export = Path.Combine(dir.Path, "tdw");
        Directory.CreateDirectory(export);
        WriteExport(
            Path.Combine(export, "Task.json"),
            Enumerable.Repeat(RowKind.Good, 80).ToList());

        // chunk 80, batch 5 → 16 sub-batches → 2+ dispatch windows: cancelling
        // during the first window makes the second window's cancellation check
        // throw mid-chunk.
        var config = TestConfig.Make(tdwExportPath: export, ingestChunkSize: 80, graphBatchSize: 5);
        var (pipeline, graph, store, _) = BuildPipeline(dir, config);
        using var storeScope = store;

        using var cts = new CancellationTokenSource();
        graph.OnApplied = () => cts.Cancel();       // cancel during the first window

        var summary = await pipeline.RunAsync(fullCrawl: true, onlyObjects: null, cts.Token);

        Assert.True(summary.Stopped, "mid-chunk cancellation must surface as a graceful stop");
        Assert.Empty(summary.FailedObjects);
        // NOTHING dead-lettered — cancellation is flow control, not a failure.
        Assert.Empty(SyncState.ReadFailedRecords(Connector));
        // The interrupted chunk was not checkpointed; the cursor did not advance.
        Assert.Null(SyncState.ReadCheckpoint(Connector));
        Assert.Null(SyncState.ReadLastSync(Connector));

        // Resume with a fresh token: everything ingests exactly once per id.
        graph.OnApplied = null;
        var resumed = await pipeline.RunAsync(fullCrawl: true);
        Assert.False(resumed.Stopped);
        Assert.Equal(80, graph.AppliedIds().Count);
        StressLog.Line(_out,
            "[tdw] mid-chunk cancellation: graceful stop, 0 dead-letter entries, "
            + $"clean resume to {graph.AppliedIds().Count}/80 items — PASS");
    }

    // THE SCALE RUN: 101,000-row TDW export (100k good + 500 numeric + 300
    // poisoned + 200 id-less), interrupted mid-export and resumed. Verifies
    // per-row isolation at volume, checkpoint correctness, exact final state
    // and bounded memory. Numbers: rows/s, RSS delta.
    [Fact]
    public async Task TdwBulk_100kRows_MalformedMix_CheckpointedResume_BoundedMemory()
    {
        const int good = 100_000, numeric = 500, poisoned = 300, idless = 200;
        using var dir = new TempDir();
        using var state = new SyncStateScope();
        var export = Path.Combine(dir.Path, "tdw");
        Directory.CreateDirectory(export);

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        var rssBefore = Environment.WorkingSet;

        var kinds = BuildKinds(good, numeric, poisoned, idless);
        var exportFile = Path.Combine(export, "Task.json");
        WriteExport(exportFile, kinds);
        var exportBytes = new FileInfo(exportFile).Length;

        var config = TestConfig.Make(tdwExportPath: export, ingestChunkSize: 5000, graphBatchSize: 20);
        var (pipeline, graph, store, invFactory) = BuildPipeline(dir, config);
        using var storeScope = store;

        // Run 1: cancel at the chunk boundary after 40,000 processed records —
        // a mid-export interruption with a checkpoint on disk.
        using var cts = new CancellationTokenSource();
        pipeline.OnProgress = (_, done, _) =>
        {
            if (done >= 40_000)
                cts.Cancel();
        };
        var sw = Stopwatch.StartNew();
        var run1 = await pipeline.RunAsync(fullCrawl: true, onlyObjects: null, cts.Token);
        var run1Ms = sw.ElapsedMilliseconds;

        Assert.True(run1.Stopped);
        var checkpoint = SyncState.ReadCheckpoint(Connector);
        Assert.NotNull(checkpoint);
        var completedChunks = checkpoint!["completed"]!["Task"]!.GetValue<int>();
        Assert.Equal(8, completedChunks);               // 8 × 5000 = 40,000

        // Run 2: resume — checkpointed chunks are skipped, the rest completes.
        pipeline.OnProgress = null;
        var run2 = await pipeline.RunAsync(fullCrawl: true);
        sw.Stop();

        Assert.False(run2.Stopped);
        Assert.Equal(8, run2.SkippedChunks);

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        var rssAfter = Environment.WorkingSet;
        var rssDeltaMb = (rssAfter - rssBefore) / 1024.0 / 1024.0;

        // Exact final state: every usable row ingested exactly once, every
        // poisoned row dead-lettered exactly once, id-less rows dropped.
        var applied = graph.AppliedIds();
        Assert.Equal(good + numeric, applied.Count);
        Assert.Contains("Task_g0", applied);
        Assert.Contains($"Task_g{good - 1}", applied);
        Assert.Contains("Task_900000", applied);
        Assert.Contains($"Task_{900000 + numeric - 1}", applied);
        Assert.DoesNotContain(applied, id => id.StartsWith("Task_p", StringComparison.Ordinal));

        var deadLetter = SyncState.ReadFailedRecords(Connector);
        Assert.Equal(poisoned, deadLetter.Count);
        Assert.Equal(poisoned, deadLetter
            .Select(r => r["item_id"]!.GetValue<string>())
            .Distinct(StringComparer.Ordinal)
            .Count());                                   // no double dead-letter on resume
        Assert.DoesNotContain(deadLetter, r => r["item_id"]!.GetValue<string>() == "WORKER_CRASH");

        using (var inv = invFactory(Connector))
            Assert.Equal(good + numeric, inv.Count());
        Assert.NotNull(SyncState.ReadLastSync(Connector));

        // Memory stayed bounded — a small multiple of the export, not a blow-up.
        Assert.True(rssAfter - rssBefore < 1_500_000_000,
            $"RSS grew by {rssDeltaMb:0} MB during the 100k TDW crawl — unbounded memory");

        var totalRows = good + numeric + poisoned + idless;
        var rate = (good + numeric + poisoned) / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        StressLog.Line(_out,
            $"[tdw] bulk scale: {totalRows:N0}-row export ({exportBytes / 1024 / 1024} MB JSON) "
            + $"→ ingested {applied.Count:N0}, dead-lettered {deadLetter.Count} poisoned, "
            + $"dropped {idless} id-less; interrupted at chunk {completedChunks}, resume skipped "
            + $"{run2.SkippedChunks} chunks (run1 {run1Ms} ms + run2 {sw.ElapsedMilliseconds - run1Ms} ms "
            + $"= {sw.ElapsedMilliseconds} ms, {rate:N0} rows/s); RSS delta {rssDeltaMb:0.0} MB — PASS");
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Scenario 2 — financial-field governance under load + ReDoS resistance
// ═════════════════════════════════════════════════════════════════════════════

public class FinancialGovernanceStressTests
{
    private readonly ITestOutputHelper _out;

    public FinancialGovernanceStressTests(ITestOutputHelper output) => _out = output;

    private static ObjectConfig FinancialObject() => new()
    {
        ObjectName = "Project",
        DisplayName = "Project",
        SelectedFields = new Dictionary<string, string>
        {
            ["Name"] = "Title",
            ["PlannedCost"] = "PlannedCost",
            ["BillingRate"] = "BillingRate",
            ["Owner"] = "Owner",
        },
        FinancialFields = new List<string> { "PlannedCost", "BillingRate" },
    };

    private static ClarizenRecord Record(int i, bool financial)
    {
        var fields = new JsonObject
        {
            ["id"] = $"/Project/{i}",
            ["Name"] = $"Project {i}",
            ["Owner"] = new JsonObject { ["id"] = "/User/1", ["name"] = "Owner" },
        };
        if (financial)
        {
            fields["PlannedCost"] = 120000.5 + i;
            fields["BillingRate"] = $"FIN-SECRET-{i} per hour";
        }
        return new ClarizenRecord("Project", fields);
    }

    private static List<AclEntry> Acl() => new()
    {
        new AclEntry(AclEntryType.User, "user-a", AclAccessType.Grant),
        new AclEntry(AclEntryType.User, "user-d", AclAccessType.Deny),
    };

    // REGRESSION (HIGH, red→green): a field DECLARED financial but routed into
    // the searchable content body (_cz_*) bypassed every FINANCIAL_DATA_MODE —
    // filter did not redact it from content, acl did not restrict the item, tag
    // did not classify it. Governance said redact; the value leaked.
    [Fact]
    public void FinancialContentField_FilterMode_RedactsContent_AndClassifies()
    {
        var objectConfig = new ObjectConfig
        {
            ObjectName = "Project",
            DisplayName = "Project",
            SelectedFields = new Dictionary<string, string>
            {
                ["Name"] = "Title",
                ["BillingRate"] = "_cz_BillingRate",   // financial → CONTENT body
            },
            FinancialFields = new List<string> { "BillingRate" },
        };
        var record = new ClarizenRecord("Project", new JsonObject
        {
            ["id"] = "/Project/1",
            ["Name"] = "Apollo",
            ["BillingRate"] = "FIN-SENTINEL-4711 per hour",
        });

        var converter = new ItemConverter(TestConfig.Make(financialMode: "filter"));
        var item = converter.Convert(record, objectConfig, Acl());

        // Governance says redact: the value must appear NOWHERE on the item.
        Assert.DoesNotContain("FIN-SENTINEL-4711", item.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("FIN-SENTINEL-4711", item.ToJson().ToJsonString(), StringComparison.Ordinal);
        // And the item is still classified as financial for DLP/verticals.
        Assert.Equal(true, item.Properties[FinancialFieldClassifier.ContainsFinancialProperty]);
        Assert.Equal("financial", item.Properties[FinancialFieldClassifier.ClassificationProperty]);
        StressLog.Line(_out,
            "[financial] _cz_ content-routed financial field: filter mode redacts the "
            + "content body and still classifies — no leak — PASS");
    }

    // REGRESSION (HIGH, red→green): acl mode must restrict an item whose ONLY
    // financial data lives in the content body.
    [Fact]
    public void FinancialContentField_AclMode_RestrictsGrantsToFinanceGroup()
    {
        var objectConfig = new ObjectConfig
        {
            ObjectName = "Project",
            DisplayName = "Project",
            SelectedFields = new Dictionary<string, string>
            {
                ["Name"] = "Title",
                ["BillingRate"] = "_cz_BillingRate",
            },
            FinancialFields = new List<string> { "BillingRate" },
        };
        var record = new ClarizenRecord("Project", new JsonObject
        {
            ["id"] = "/Project/1",
            ["Name"] = "Apollo",
            ["BillingRate"] = "175.00/hr",
        });

        var converter = new ItemConverter(
            TestConfig.Make(financialMode: "acl", financialGroupId: "fin-group"));
        var item = converter.Convert(record, objectConfig, Acl());

        var grants = item.Acl.Where(e => e.AccessType == AclAccessType.Grant).ToList();
        var grant = Assert.Single(grants);
        Assert.Equal("fin-group", grant.Value);
        Assert.Single(item.Acl, e => e.AccessType == AclAccessType.Deny);  // denies preserved
        // acl mode ingests the value (visible to the finance group only).
        Assert.Contains("175.00/hr", item.Content, StringComparison.Ordinal);
        StressLog.Line(_out,
            "[financial] _cz_ content-routed financial field: acl mode restricts grants "
            + "to the finance group (denies preserved) — PASS");
    }

    // REGRESSION (MEDIUM, red→green): tag mode must classify such an item.
    [Fact]
    public void FinancialContentField_TagMode_ClassifiesItem()
    {
        var objectConfig = new ObjectConfig
        {
            ObjectName = "Project",
            DisplayName = "Project",
            SelectedFields = new Dictionary<string, string>
            {
                ["Name"] = "Title",
                ["BillingRate"] = "_cz_BillingRate",
            },
            FinancialFields = new List<string> { "BillingRate" },
        };
        var record = new ClarizenRecord("Project", new JsonObject
        {
            ["id"] = "/Project/1",
            ["Name"] = "Apollo",
            ["BillingRate"] = "175.00/hr",
        });

        var item = new ItemConverter(TestConfig.Make(financialMode: "tag"))
            .Convert(record, objectConfig, Acl());

        Assert.Equal(true, item.Properties[FinancialFieldClassifier.ContainsFinancialProperty]);
        Assert.Contains("175.00/hr", item.Content, StringComparison.Ordinal);  // tag keeps values
        StressLog.Line(_out,
            "[financial] _cz_ content-routed financial field: tag mode classifies the item — PASS");
    }

    // SCALE: 100k mixed records through the converter in filter mode on all
    // cores. HARD invariant: zero financial values anywhere on any produced
    // item (properties, content, full serialized payload) when governance says
    // redact. Numbers: items/s.
    [Fact]
    public void FilterMode_100kMixedRecords_ZeroLeaks_Throughput()
    {
        const int n = 100_000;
        var objectConfig = FinancialObject();
        var config = TestConfig.Make(financialMode: "filter");
        var converter = new ItemConverter(config);

        long leaks = 0, financialTagged = 0, cleanUntouched = 0;
        var sw = Stopwatch.StartNew();
        Parallel.For(0, n, i =>
        {
            var financial = i % 2 == 0;
            var item = converter.Convert(Record(i, financial), objectConfig, Acl());
            if (financial)
            {
                var serialized = item.ToJson().ToJsonString();
                var leaked =
                    item.Properties.ContainsKey("PlannedCost")
                    || item.Properties.ContainsKey("BillingRate")
                    || serialized.Contains("FIN-SECRET-", StringComparison.Ordinal)
                    || item.Content.Contains("FIN-SECRET-", StringComparison.Ordinal);
                if (leaked)
                    Interlocked.Increment(ref leaks);
                if (item.Properties.TryGetValue(
                        FinancialFieldClassifier.ContainsFinancialProperty, out var v) && v is true)
                    Interlocked.Increment(ref financialTagged);
            }
            else
            {
                if (!item.Properties.ContainsKey(FinancialFieldClassifier.ClassificationProperty))
                    Interlocked.Increment(ref cleanUntouched);
            }
        });
        sw.Stop();

        Assert.Equal(0, Interlocked.Read(ref leaks));
        Assert.Equal(n / 2, Interlocked.Read(ref financialTagged));
        Assert.Equal(n / 2, Interlocked.Read(ref cleanUntouched));
        var rate = n / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        StressLog.Line(_out,
            $"[financial] filter mode at scale: {n:N0} records ({n / 2:N0} financial / {n / 2:N0} clean) "
            + $"in {sw.ElapsedMilliseconds} ms ({rate:N0} items/s); leaked values = 0, "
            + $"classified = {financialTagged:N0}, clean untouched = {cleanUntouched:N0} — NO LEAK");
    }

    // SCALE: acl mode on 20k financial records — every one restricted to the
    // finance group with denies preserved; clean records untouched.
    [Fact]
    public void AclMode_20kRecords_AlwaysRestricted()
    {
        const int n = 20_000;
        var objectConfig = FinancialObject();
        var converter = new ItemConverter(
            TestConfig.Make(financialMode: "acl", financialGroupId: "fin-group"));

        long violations = 0;
        var sw = Stopwatch.StartNew();
        Parallel.For(0, n, i =>
        {
            var financial = i % 2 == 0;
            var item = converter.Convert(Record(i, financial), objectConfig, Acl());
            var grants = item.Acl.Where(e => e.AccessType == AclAccessType.Grant).ToList();
            var denies = item.Acl.Count(e => e.AccessType == AclAccessType.Deny);
            var ok = financial
                ? grants.Count == 1 && grants[0].Value == "fin-group" && denies == 1
                : grants.Count == 1 && grants[0].Value == "user-a" && denies == 1;
            if (!ok)
                Interlocked.Increment(ref violations);
        });
        sw.Stop();

        Assert.Equal(0, Interlocked.Read(ref violations));
        StressLog.Line(_out,
            $"[financial] acl mode at scale: {n:N0} records in {sw.ElapsedMilliseconds} ms "
            + $"({n / Math.Max(0.001, sw.Elapsed.TotalSeconds):N0} items/s); "
            + "restriction violations = 0 — PASS");
    }

    // ReDoS: a catastrophic-backtracking pattern in the classifier config must
    // hit the 2s match timeout and be treated as no-match — never hang.
    [Fact]
    public void Classifier_CatastrophicPattern_TimeoutGuardHolds()
    {
        var evil = ContentClassifier.FromJson("""
            {"categories": [
              {"name": "Evil1", "patterns": [{"name": "nested", "regex": "^(a+)+$"}]},
              {"name": "Evil2", "patterns": [{"name": "nested2", "regex": "^(\\d+)+x$"}]}
            ]}
            """);
        var attackA = new string('a', 60_000) + "b";
        var attackD = new string('7', 60_000) + "y";

        var sw = Stopwatch.StartNew();
        var resultA = evil.Detect(attackA);
        var aMs = sw.ElapsedMilliseconds;
        var resultD = evil.Detect(attackD);
        sw.Stop();

        Assert.Empty(resultA);
        Assert.Empty(resultD);
        // Two patterns × 2s timeout each, with headroom — NOT exponential time.
        Assert.True(sw.ElapsedMilliseconds < 20_000,
            $"adversarial detect took {sw.ElapsedMilliseconds} ms — timeout guard failed");
        StressLog.Line(_out,
            $"[redos] catastrophic patterns ^(a+)+$ and ^(\\d+)+x$ on 60k-char adversarial "
            + $"strings: {aMs} ms + {sw.ElapsedMilliseconds - aMs} ms, both no-match "
            + $"(2s regex timeout held; would be ~2^60000 steps unguarded) — PASS");
    }

    // The SHIPPED pattern set against adversarial corpora (Luhn-candidate digit
    // floods, email floods, oversized text) — time bounded by MaxScanChars +
    // the match timeout, correct detections. Numbers: ms per corpus.
    [Fact]
    public void Classifier_DefaultPatterns_AdversarialCorpora_Bounded()
    {
        var classifier = ContentClassifier.Load();
        Assert.True(classifier.Categories.Count >= 3);

        // (a) 1MB digit/dash flood: enormous Luhn candidate surface, all invalid.
        var digitFlood = string.Concat(Enumerable.Repeat("1234567890-", 100_000));
        // (b) 1MB of an email-adjacent flood.
        var emailFlood = string.Concat(Enumerable.Repeat("a@a.aa ", 150_000));
        // (c) 4MB text — must be truncated to MaxScanChars, not scanned whole.
        var oversized = new string('z', 4 * 1024 * 1024) + " 378282246310005";
        // (d) a real mixed payload with valid PCI (Luhn-valid) + PII + secret.
        var real = "call 555-123-4567; card 4111 1111 1111 1111; AKIAABCDEFGHIJKLMNOP done";

        var sw = Stopwatch.StartNew();
        var a = classifier.Detect(digitFlood);
        var aMs = sw.ElapsedMilliseconds;
        var b = classifier.Detect(emailFlood);
        var bMs = sw.ElapsedMilliseconds - aMs;
        var c = classifier.Detect(oversized);
        var cMs = sw.ElapsedMilliseconds - aMs - bMs;
        var d = classifier.Detect(real);
        sw.Stop();

        Assert.DoesNotContain("PCI", a);           // no Luhn-valid run in the flood
        Assert.Contains("PII", b);                 // email matches (fast first-hit)
        Assert.Empty(c);                           // Luhn number sits past the 1MB scan cap
        Assert.Contains("PCI", d);
        Assert.Contains("PII", d);
        Assert.Contains("Secret", d);
        // 8 shipped patterns × 2s timeout each = worst-case 16s per corpus.
        Assert.True(aMs < 20_000 && bMs < 20_000 && cMs < 20_000,
            $"corpus scans took {aMs}/{bMs}/{cMs} ms — bound failed");
        StressLog.Line(_out,
            $"[redos] shipped patterns vs adversarial corpora: 1MB digit-flood {aMs} ms "
            + $"(PCI=false, Luhn rejects all), 1MB email-flood {bMs} ms, 4MB oversized {cMs} ms "
            + $"(clipped at 1MB scan cap, planted PCI past cap ignored), real mixed payload "
            + $"detects PII+PCI+Secret — all bounded — PASS");
    }

    // Classifier + sensitivity-label throughput at volume: 30k items with ~10%
    // PII, ~30% financial. Labels must be exact per construction.
    [Fact]
    public void SensitivityClassification_30kItems_CorrectLabels_Throughput()
    {
        const int n = 30_000;
        var classifier = new SensitivityClassifier(ContentClassifier.Load());
        var objectConfig = new ObjectConfig
        {
            ObjectName = "Task",
            SensitivityDefault = "Internal",
            SelectedFields = new Dictionary<string, string>(),
        };

        long wrong = 0;
        var sw = Stopwatch.StartNew();
        Parallel.For(0, n, i =>
        {
            var pii = i % 10 == 0;
            var financial = i % 10 is 1 or 2 or 3;
            var item = new ExternalItem { Id = $"Task_{i}" };
            item.Properties["Title"] = pii
                ? $"Contact jane.doe{i}@example.com about the review"
                : $"Routine task {i} status update";
            if (financial)
                item.Properties[FinancialFieldClassifier.ContainsFinancialProperty] = true;
            var outcome = classifier.Classify(item, objectConfig);

            var expected = pii
                ? SensitivityLabel.Restricted
                : financial ? SensitivityLabel.Confidential : SensitivityLabel.Internal;
            if (outcome.Label != expected)
                Interlocked.Increment(ref wrong);
        });
        sw.Stop();

        Assert.Equal(0, Interlocked.Read(ref wrong));
        var rate = n / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        StressLog.Line(_out,
            $"[financial] sensitivity classification: {n:N0} items in {sw.ElapsedMilliseconds} ms "
            + $"({rate:N0} items/s); label errors = 0 (PII→Restricted, financial→Confidential, "
            + "default→Internal) — PASS");
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Scenario 3 — Clarizen-side and Graph-side breakers interacting
// ═════════════════════════════════════════════════════════════════════════════

public class DualBreakerInterplayStressTests : IDisposable
{
    private const string Connector = "ClarizenAdaptiveWork";
    private readonly ITestOutputHelper _out;

    public DualBreakerInterplayStressTests(ITestOutputHelper output)
    {
        _out = output;
        Metrics.ResetForTests();
    }

    public void Dispose()
    {
        Breakers.ResetForTests();
        Metrics.ResetForTests();
    }

    private enum Mode
    {
        Ok,
        Err,
    }

    /// <summary>Take every free half-open slot, assert the expected count, release them.</summary>
    private static int FreeHalfOpenSlots(CircuitBreaker breaker)
    {
        var taken = 0;
        while (breaker.TryAcquire())
            taken++;
        for (var i = 0; i < taken; i++)
            breaker.OnIgnored();
        return taken;
    }

    [Fact]
    public async Task DualBreakers_OpenVersusHalfOpen_BothDirections_NoCrossWedge_CleanResume()
    {
        using var dir = new TempDir();
        using var state = new SyncStateScope();
        var clock = new ClockBox(DateTime.UtcNow);
        Breakers.Initialize(new CircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = 1,
            OpenDuration = TimeSpan.FromSeconds(30),
            Window = TimeSpan.FromSeconds(60),
            HalfOpenTrials = 1,
        }, () => clock.Now);

        var clarizenMode = Mode.Ok;
        var graphMode = Mode.Ok;
        var clarizenCalls = 0;
        var graphCalls = 0;

        var clarizenHandler = new MockHttpHandler((request, _) =>
        {
            Interlocked.Increment(ref clarizenCalls);
            if (request.RequestUri!.AbsolutePath.EndsWith("/authentication/login"))
                return MockHttpHandler.Json(HttpStatusCode.OK, """{"sessionId": "s"}""");
            if (clarizenMode == Mode.Err)
                return MockHttpHandler.Json(HttpStatusCode.ServiceUnavailable, "outage");
            var entities = new JsonArray();
            for (var i = 1; i <= 20; i++)
            {
                entities.Add(new JsonObject
                {
                    ["id"] = $"/X/{i}",
                    ["Name"] = $"Item {i}",
                    ["Owner"] = new JsonObject { ["id"] = "/User/1" },
                });
            }
            return MockHttpHandler.Json(HttpStatusCode.OK, new JsonObject
            {
                ["entities"] = entities,
                ["paging"] = new JsonObject { ["hasMore"] = false },
            }.ToJsonString());
        });
        var graphHandler = new MockHttpHandler((request, body) =>
        {
            Interlocked.Increment(ref graphCalls);
            if (graphMode == Mode.Err)
                return MockHttpHandler.Json(HttpStatusCode.ServiceUnavailable, "outage");
            if (request.RequestUri!.AbsolutePath.EndsWith("/$batch"))
            {
                var requests = (JsonArray)JsonNode.Parse(body)!["requests"]!;
                var responses = new JsonArray();
                foreach (var r in requests)
                    responses.Add(new JsonObject { ["id"] = r!["id"]!.GetValue<string>(), ["status"] = 200 });
                return MockHttpHandler.Json(HttpStatusCode.OK,
                    new JsonObject { ["responses"] = responses }.ToJsonString());
            }
            return MockHttpHandler.Json(HttpStatusCode.OK, "{}");
        });

        var config = TestConfig.Make(graphMaxRetries: 0, ingestChunkSize: 20, graphBatchSize: 10);
        var clarizen = new ClarizenClient(
            config, new ApiBudget(100_000_000, callsPerMinute: 600_000_000),
            clarizenHandler, Breakers.Clarizen)
        {
            OverrideSessionId = "s",
        };
        clarizen.DelayAsync = (_, _) => Task.CompletedTask;
        var graph = new GraphClient(config, graphHandler, Breakers.Graph) { OverrideToken = "t" };
        graph.DelayAsync = (_, _) => Task.CompletedTask;

        var schema = new SchemaConfig
        {
            ObjectList = new[] { "Alpha", "Beta", "Gamma" }.Select(name => new ObjectConfig
            {
                ObjectName = name,
                DisplayName = name,
                AclMode = "ownerOnly",
                SelectedFields = new Dictionary<string, string>
                {
                    ["Name"] = "Title",
                    ["Owner"] = "Owner",
                },
            }).ToList(),
        };
        var store = new IdentityStore("DualBrk", Path.Combine(dir.Path, "id.db"));
        using var storeScope = store;
        store.Upsert(new PrincipalMapping("/User/1", "user", "o@x.com", "entra-o", DateTime.UtcNow));
        var resolver = new AclResolver(
            new PrincipalMapper(store, "{}"), new DirectorySnapshot(), string.Empty, string.Empty);
        var pipeline = new IngestPipeline(
            config, schema, clarizen, graph, resolver, new ItemConverter(config),
            ha: null,
            inventoryFactory: id => new ItemInventory(id, Path.Combine(dir.Path, $"inv_{id}.db")));

        // ── Phase 1: Clarizen OPEN + Graph HALF-OPEN ─────────────────────────
        Breakers.Graph.TripForTests();               // Graph opens at t0
        clock.Advance(TimeSpan.FromSeconds(31));     // t31 → Graph half-open
        Assert.Equal(CircuitState.HalfOpen, Breakers.Graph.State);
        Breakers.Clarizen.TripForTests();            // Clarizen opens at t31
        Assert.Equal(CircuitState.Open, Breakers.Clarizen.State);

        var p1 = await pipeline.RunAsync(fullCrawl: true);
        Assert.True(p1.Degraded);
        Assert.Equal(0, p1.Ingested);
        Assert.Equal(0, Volatile.Read(ref clarizenCalls));   // nothing hammered
        Assert.Equal(0, Volatile.Read(ref graphCalls));
        // The half-open Graph breaker was NOT cross-wedged by the Clarizen
        // outage: its full probe allowance is still available.
        Assert.Equal(CircuitState.HalfOpen, Breakers.Graph.State);
        Assert.Equal(1, FreeHalfOpenSlots(Breakers.Graph));
        Assert.Null(SyncState.ReadLastSync(Connector));

        // ── Phase 2 (mirror): Clarizen HALF-OPEN + Graph OPEN ────────────────
        clock.Advance(TimeSpan.FromSeconds(31));     // t62 → Clarizen half-open
        Assert.Equal(CircuitState.HalfOpen, Breakers.Clarizen.State);
        Breakers.Graph.TripForTests();               // Graph re-opens at t62
        Assert.Equal(CircuitState.Open, Breakers.Graph.State);

        var p2 = await pipeline.RunAsync(fullCrawl: true);
        Assert.True(p2.Degraded);
        Assert.Equal(0, Volatile.Read(ref clarizenCalls));
        Assert.Equal(0, Volatile.Read(ref graphCalls));
        Assert.Equal(CircuitState.HalfOpen, Breakers.Clarizen.State);
        Assert.Equal(1, FreeHalfOpenSlots(Breakers.Clarizen));  // no cross-wedge either way

        // ── Phase 3: both recover — crawl resumes cleanly, breakers close ────
        clock.Advance(TimeSpan.FromSeconds(31));     // t93 → Graph half-open too
        Assert.Equal(CircuitState.HalfOpen, Breakers.Graph.State);
        var p3 = await pipeline.RunAsync(fullCrawl: true);
        Assert.False(p3.Degraded);
        Assert.Equal(60, p3.Ingested);               // 3 object types × 20
        Assert.Equal(CircuitState.Closed, Breakers.Clarizen.State);
        Assert.Equal(CircuitState.Closed, Breakers.Graph.State);
        Assert.True(Breakers.Clarizen.Resets >= 1);
        Assert.True(Breakers.Graph.Resets >= 1);
        Assert.NotNull(SyncState.ReadLastSync(Connector));

        // ── Phase 4: REAL mid-crawl Graph trip while Clarizen stays healthy ──
        var callsBefore = Volatile.Read(ref clarizenCalls);
        graphMode = Mode.Err;
        var p4 = await pipeline.RunAsync(fullCrawl: true);
        Assert.True(p4.Degraded);                    // breaker tripped mid-crawl
        Assert.Equal(CircuitState.Open, Breakers.Graph.State);
        Assert.Equal(CircuitState.Closed, Breakers.Clarizen.State);  // untouched
        Assert.True(Volatile.Read(ref clarizenCalls) > callsBefore); // source WAS reachable
        // At most the first dispatch window (2 sub-batches × 10) failed before
        // the trip; nothing after the fail-fast point was dead-lettered.
        var deadLetter = SyncState.ReadFailedRecords(Connector);
        Assert.InRange(deadLetter.Count, 1, 20);
        Assert.DoesNotContain(deadLetter, r => r["item_id"]!.GetValue<string>() == "WORKER_CRASH");

        // Recovery: dependency heals → next cycle completes; state exact.
        graphMode = Mode.Ok;
        clock.Advance(TimeSpan.FromSeconds(31));
        SyncState.ClearFailedRecords(Connector);
        var p5 = await pipeline.RunAsync(fullCrawl: true);
        Assert.False(p5.Degraded);
        Assert.Equal(CircuitState.Closed, Breakers.Graph.State);

        StressLog.Line(_out,
            "[breakers] open-vs-half-open both directions: degraded pauses with 0 calls to either "
            + "dependency, half-open probe allowance intact both ways (no cross-wedge); recovery "
            + $"crawl ingested 60/60; real mid-crawl Graph trip dead-lettered {deadLetter.Count} "
            + $"(≤ one window) then resumed clean; trips: clarizen={Breakers.Clarizen.Trips}, "
            + $"graph={Breakers.Graph.Trips}; resets: clarizen={Breakers.Clarizen.Resets}, "
            + $"graph={Breakers.Graph.Resets} — PASS");
    }

    // Cancellation storm racing both breakers through open→half-open→open
    // transitions: after the storm neither breaker may be wedged, and both must
    // recover to Closed when the dependencies heal.
    [Fact]
    public async Task DualBreakers_CancellationStorm_MidTransition_NeverWedges()
    {
        var clock = new ClockBox(DateTime.UtcNow);
        Breakers.Initialize(new CircuitBreakerOptions
        {
            Enabled = true,
            FailureThreshold = 3,
            OpenDuration = TimeSpan.FromSeconds(5),
            Window = TimeSpan.FromSeconds(60),
            HalfOpenTrials = 2,
        }, () => clock.Now);

        // Deterministic OK/5xx block cycling, sized so each dependency suffers
        // sustained outages long enough to genuinely trip its breaker
        // (Clarizen retries 4× per call → a 30-call error block yields 5+
        // consecutive terminal failures ≥ threshold 3).
        var clarizenFlip = 0L;
        var graphFlip = 0L;
        var clarizenHandler = new MockHttpHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/authentication/login"))
                return MockHttpHandler.Json(HttpStatusCode.OK, """{"sessionId": "s"}""");
            var c = Interlocked.Increment(ref clarizenFlip);
            return (c / 30) % 2 == 0
                ? MockHttpHandler.Json(HttpStatusCode.OK,
                    """{"entities": [], "paging": {"hasMore": false}}""")
                : MockHttpHandler.Json(HttpStatusCode.BadGateway, "x");
        });
        var graphHandler = new MockHttpHandler((_, _) =>
        {
            var c = Interlocked.Increment(ref graphFlip);
            return (c / 12) % 2 == 0
                ? MockHttpHandler.Json(HttpStatusCode.OK, "{}")
                : MockHttpHandler.Json(HttpStatusCode.ServiceUnavailable, "x");
        });

        var config = TestConfig.Make(graphMaxRetries: 2);
        var clarizen = new ClarizenClient(
            config, new ApiBudget(100_000_000, callsPerMinute: 600_000_000),
            clarizenHandler, Breakers.Clarizen)
        {
            OverrideSessionId = "s",
        };
        var graph = new GraphClient(config, graphHandler, Breakers.Graph) { OverrideToken = "t" };

        // Every 5th retry-wait aborts as if the caller cancelled mid-backoff.
        long delayCalls = 0;
        Task StormDelay(TimeSpan _, CancellationToken __) =>
            Interlocked.Increment(ref delayCalls) % 5 == 0
                ? throw new OperationCanceledException()
                : Task.CompletedTask;
        clarizen.DelayAsync = StormDelay;
        graph.DelayAsync = StormDelay;

        const int workers = 16;
        const int perWorker = 250;
        long ops = 0, exceptions = 0, rejectedFast = 0;
        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, workers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < perWorker; i++)
            {
                // Keep time moving so Open→HalfOpen transitions race the probes.
                if (w == 0 && i % 10 == 0)
                    clock.Advance(TimeSpan.FromSeconds(6));
                try
                {
                    if ((w + i) % 2 == 0)
                    {
                        var r = await graph.PutAsync("items/x", new JsonObject());
                        if (r.CircuitOpen)
                            Interlocked.Increment(ref rejectedFast);
                    }
                    else
                    {
                        await clarizen.QueryAllAsync("SELECT a FROM B");
                    }
                }
                catch
                {
                    Interlocked.Increment(ref exceptions);  // 5xx terminal / cancel / open
                }
                Interlocked.Increment(ref ops);
            }
        })).ToArray();
        await Task.WhenAll(tasks);
        sw.Stop();

        // Invariant: NEITHER breaker is wedged. Any state is legal, but a
        // half-open breaker must still admit probes, and an open one must
        // transition to half-open when its window elapses.
        foreach (var breaker in new[] { Breakers.Clarizen, Breakers.Graph })
        {
            if (breaker.State == CircuitState.Open)
                clock.Advance(TimeSpan.FromSeconds(6));
            var stateNow = breaker.State;
            if (stateNow == CircuitState.HalfOpen)
            {
                Assert.True(breaker.TryAcquire(),
                    $"breaker '{breaker.Name}' wedged in half-open after the storm");
                breaker.OnIgnored();
            }
        }

        // Both dependencies genuinely cycled: each breaker tripped at least
        // once during the storm (the transitions were real, not idle).
        Assert.True(Breakers.Clarizen.Trips >= 1, "clarizen breaker never tripped — storm too soft");
        Assert.True(Breakers.Graph.Trips >= 1, "graph breaker never tripped — storm too soft");

        // Recovery: dependencies heal; both breakers must close within a
        // bounded number of probes.
        clarizen.DelayAsync = (_, _) => Task.CompletedTask;
        graph.DelayAsync = (_, _) => Task.CompletedTask;
        var healHandler = new MockHttpHandler((_, _) => MockHttpHandler.Json(HttpStatusCode.OK, "{}"));
        var healClarizen = new ClarizenClient(
            config, new ApiBudget(100_000_000, callsPerMinute: 600_000_000),
            healHandler, Breakers.Clarizen)
        {
            OverrideSessionId = "s",
        };
        healClarizen.DelayAsync = (_, _) => Task.CompletedTask;
        var healGraph = new GraphClient(config, healHandler, Breakers.Graph) { OverrideToken = "t" };
        healGraph.DelayAsync = (_, _) => Task.CompletedTask;
        for (var i = 0;
             i < 100 && (Breakers.Clarizen.State != CircuitState.Closed
                         || Breakers.Graph.State != CircuitState.Closed);
             i++)
        {
            clock.Advance(TimeSpan.FromSeconds(6));
            try { await healClarizen.QueryAllAsync("SELECT a FROM B"); } catch { /* ignore */ }
            await healGraph.PutAsync("items/x", new JsonObject());
        }
        Assert.Equal(CircuitState.Closed, Breakers.Clarizen.State);
        Assert.Equal(CircuitState.Closed, Breakers.Graph.State);

        var total = Interlocked.Read(ref ops);
        StressLog.Line(_out,
            $"[breakers] cancellation storm mid-transition: {total:N0} concurrent ops "
            + $"({workers} workers) in {sw.ElapsedMilliseconds} ms "
            + $"({total / Math.Max(0.001, sw.Elapsed.TotalSeconds):N0} ops/s); "
            + $"exceptions(5xx/cancel)={Interlocked.Read(ref exceptions):N0}, "
            + $"fail-fast rejections={Interlocked.Read(ref rejectedFast):N0}; transitions: "
            + $"clarizen trips={Breakers.Clarizen.Trips}/resets={Breakers.Clarizen.Resets}, "
            + $"graph trips={Breakers.Graph.Trips}/resets={Breakers.Graph.Resets}; "
            + "post-storm: no wedge, both breakers recovered to Closed — PASS");
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Scenario 4 — HA lease contention + SQLite state concurrency
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// ISqlGateway backed by a REAL shared SQLite database file. Translates the
/// fixed statement set HaCoordinator issues (T-SQL) to SQLite so multiple
/// simulated nodes contend on one state DB with genuine cross-connection
/// locking. The clock is injectable so lease expiry is deterministic. Counts
/// SQLITE_BUSY/SQLITE_LOCKED errors — the harness asserts none escape.
/// </summary>
internal sealed class SqliteHaGateway : ISqlGateway, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly Func<DateTime> _now;
    private readonly StrongBox<long> _busyErrors;
    private readonly StrongBox<long> _ops;

    public SqliteHaGateway(
        string dbPath, Func<DateTime> now, StrongBox<long> busyErrors, StrongBox<long> ops)
    {
        _now = now;
        _busyErrors = busyErrors;
        _ops = ops;
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        Run("PRAGMA busy_timeout=5000;");
        Run("""
            CREATE TABLE IF NOT EXISTS CrawlRuns (
                CrawlKey TEXT PRIMARY KEY, ConnectorId TEXT, Kind TEXT,
                OpenedBy TEXT, OpenedAtUtc TEXT, Status TEXT,
                ClosedBy TEXT, ClosedAtUtc TEXT);
            CREATE TABLE IF NOT EXISTS ObjectClaims (
                CrawlKey TEXT, ObjectType TEXT, NodeId TEXT,
                HeartbeatUtc TEXT, Status TEXT,
                PRIMARY KEY (CrawlKey, ObjectType));
            """);
    }

    private void Run(string sql)
    {
        using var command = _conn.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static string Translate(string sql)
    {
        if (sql.Contains("IF NOT EXISTS", StringComparison.Ordinal))
        {
            // OpenOrJoinCrawl's guarded insert → native SQLite upsert-ignore.
            return """
                INSERT OR IGNORE INTO CrawlRuns
                    (CrawlKey, ConnectorId, Kind, OpenedBy, OpenedAtUtc, Status)
                VALUES (@key, @connector, @kind, @node, @now, 'open');
                """;
        }
        var s = sql.Replace(
            "DATEDIFF(second, HeartbeatUtc, SYSUTCDATETIME())",
            "(CAST(strftime('%s', @now) AS INTEGER) - CAST(strftime('%s', HeartbeatUtc) AS INTEGER))");
        s = s.Replace("SYSUTCDATETIME()", "@now").Replace("dbo.", "");
        return s;
    }

    private SqliteCommand Build(string sql, (string Name, object? Value)[] args)
    {
        var translated = Translate(sql);
        var command = _conn.CreateCommand();
        command.CommandText = translated;
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        if (translated.Contains("@now", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue(
                "@now", _now().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        }
        return command;
    }

    private T Guarded<T>(Func<T> action)
    {
        Interlocked.Increment(ref _ops.Value);
        try
        {
            return action();
        }
        catch (SqliteException exc) when (exc.SqliteErrorCode is 5 or 6)
        {
            Interlocked.Increment(ref _busyErrors.Value);  // BUSY / LOCKED escaped
            throw;
        }
    }

    public int Execute(string sql, params (string Name, object? Value)[] args) =>
        Guarded(() =>
        {
            using var command = Build(sql, args);
            return command.ExecuteNonQuery();
        });

    public object? Scalar(string sql, params (string Name, object? Value)[] args) =>
        Guarded(() =>
        {
            using var command = Build(sql, args);
            return command.ExecuteScalar();
        });

    public List<Dictionary<string, object?>> Query(
        string sql, params (string Name, object? Value)[] args) =>
        Guarded(() =>
        {
            using var command = Build(sql, args);
            using var reader = command.ExecuteReader();
            var rows = new List<Dictionary<string, object?>>();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return rows;
        });

    public object? Raw(string sql)
    {
        using var command = _conn.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public void Dispose() => _conn.Dispose();
}

public class HaLeaseSqliteStressTests
{
    private readonly ITestOutputHelper _out;

    public HaLeaseSqliteStressTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task HaFailoverStorm_OneSqliteDb_NoDoubleCrawl_ExactlyOneCloser()
    {
        using var dir = new TempDir();
        using var scope = new EnvScope(
            ("HA_CLAIM_TIMEOUT_SECONDS", "120"),
            ("HA_HEARTBEAT_SECONDS", "60"));
        var dbPath = Path.Combine(dir.Path, "ha_state.db");
        var clock = new ClockBox(new DateTime(2026, 7, 17, 8, 0, 0, DateTimeKind.Utc));
        var busy = new StrongBox<long>(0);
        var opsBox = new StrongBox<long>(0);

        const int nodeCount = 8;
        const int objectCount = 48;
        var objectTypes = Enumerable.Range(0, objectCount).Select(i => $"t{i:00}").ToList();

        var gateways = Enumerable.Range(0, nodeCount)
            .Select(_ => new SqliteHaGateway(dbPath, () => clock.Now, busy, opsBox))
            .ToList();
        var nodes = gateways
            .Select((g, i) => new HaCoordinator(g, $"node-{i}"))
            .ToList();
        try
        {
            var sw = Stopwatch.StartNew();

            // All nodes open/join the same crawl concurrently — one row wins.
            var slot = clock.Now;
            var keys = await Task.WhenAll(nodes.Select(node =>
                Task.Run(() => node.OpenOrJoinCrawl("Conn", "full", slot))));
            var crawlKey = keys[0];
            Assert.All(keys, k => Assert.Equal(crawlKey, k));
            Assert.Equal(1L, Convert.ToInt64(
                gateways[0].Raw("SELECT COUNT(*) FROM CrawlRuns;"), CultureInfo.InvariantCulture));

            // Wave 1: every node races to claim every object type (each in its
            // own shuffled order, all released simultaneously by a start gate
            // so the leases are contended for real).
            var wins = new List<string>[nodeCount];
            using (var startGate = new Barrier(nodeCount))
            {
                await Task.WhenAll(Enumerable.Range(0, nodeCount).Select(n => Task.Run(() =>
                {
                    var order = objectTypes.ToList();
                    var shuffle = new Random(1000 + n);
                    for (var i = order.Count - 1; i > 0; i--)
                    {
                        var j = shuffle.Next(i + 1);
                        (order[i], order[j]) = (order[j], order[i]);
                    }
                    startGate.SignalAndWait();
                    var mine = new List<string>();
                    foreach (var type in order)
                    {
                        if (nodes[n].TryClaimObject(crawlKey, type))
                            mine.Add(type);
                    }
                    wins[n] = mine;
                })));
            }

            // EXACTLY one owner per object type — no double-crawl.
            var claimedBy = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var n = 0; n < nodeCount; n++)
            {
                foreach (var type in wins[n])
                {
                    Assert.False(claimedBy.ContainsKey(type),
                        $"object '{type}' claimed by node-{claimedBy.GetValueOrDefault(type)} AND node-{n}");
                    claimedBy[type] = n;
                }
            }
            Assert.Equal(objectCount, claimedBy.Count);

            // FAILOVER STORM: the 3 nodes holding the MOST leases die
            // (heartbeats stop) — chosen dynamically so the reclaim path is
            // guaranteed real work. Time passes the lease timeout; survivors
            // re-stamp their own heartbeats, then race to reclaim.
            var ranked = Enumerable.Range(0, nodeCount)
                .OrderByDescending(n => wins[n].Count)
                .ToList();
            var deadNodes = ranked.Take(3).ToList();
            var survivors = ranked.Skip(3).ToList();
            var deadClaims = deadNodes.SelectMany(n => wins[n]).ToHashSet(StringComparer.Ordinal);
            Assert.True(deadClaims.Count >= objectCount * 3 / nodeCount,
                $"storm setup: dead nodes hold only {deadClaims.Count} leases");
            clock.Advance(TimeSpan.FromSeconds(121));
            await Task.WhenAll(survivors.Select(n => Task.Run(() =>
            {
                foreach (var type in wins[n])
                    nodes[n].Heartbeat(crawlKey, type);
            })));

            var takeovers = new List<string>[nodeCount];
            using (var reclaimGate = new Barrier(survivors.Count))
            {
                await Task.WhenAll(survivors.Select(n => Task.Run(() =>
                {
                    var order = objectTypes.ToList();
                    var shuffle = new Random(2000 + n);
                    for (var i = order.Count - 1; i > 0; i--)
                    {
                        var j = shuffle.Next(i + 1);
                        (order[i], order[j]) = (order[j], order[i]);
                    }
                    reclaimGate.SignalAndWait();
                    var mine = new List<string>();
                    foreach (var type in order)
                    {
                        // A survivor only *takes over* a claim it did not already own.
                        if (!wins[n].Contains(type) && nodes[n].TryClaimObject(crawlKey, type))
                            mine.Add(type);
                        Thread.Yield();   // interleave the survivors' sweeps
                    }
                    takeovers[n] = mine;
                })));
            }

            // Every dead lease reclaimed by EXACTLY one survivor; no live lease stolen.
            var reclaimedBy = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var n in survivors)
            {
                foreach (var type in takeovers[n])
                {
                    Assert.Contains(type, deadClaims);
                    Assert.False(reclaimedBy.ContainsKey(type),
                        $"dead lease '{type}' reclaimed twice (node-{reclaimedBy.GetValueOrDefault(type)} and node-{n})");
                    reclaimedBy[type] = n;
                }
            }
            Assert.Equal(deadClaims.Count, reclaimedBy.Count);

            // Everyone completes what they now own; every node races the close.
            await Task.WhenAll(survivors.Select(n => Task.Run(() =>
            {
                foreach (var type in wins[n])
                    nodes[n].CompleteObject(crawlKey, type);
                foreach (var type in takeovers[n])
                    nodes[n].CompleteObject(crawlKey, type);
            })));
            var closeResults = await Task.WhenAll(nodes.Select(node =>
                Task.Run(() => node.TryCloseCrawl(crawlKey, objectTypes))));
            Assert.Equal(1, closeResults.Count(r => r));   // exactly ONE closer
            Assert.Equal("closed", (string)gateways[0].Raw(
                $"SELECT Status FROM CrawlRuns WHERE CrawlKey = '{crawlKey}';")!);

            // Generation 2: failed claims are terminal — the crawl still closes
            // (status 'failed'), exactly one closer, and a failed claim is NEVER
            // reclaimed even after the lease timeout.
            var types2 = Enumerable.Range(0, 12).Select(i => $"g2_{i:00}").ToList();
            var key2 = nodes[0].OpenOrJoinCrawl("Conn", "incremental", clock.Now);
            var wins2 = new List<string>[nodeCount];
            await Task.WhenAll(Enumerable.Range(0, nodeCount).Select(n => Task.Run(() =>
            {
                var mine = new List<string>();
                foreach (var type in types2)
                {
                    if (nodes[n].TryClaimObject(key2, type))
                        mine.Add(type);
                }
                wins2[n] = mine;
            })));
            var failedTypes = new HashSet<string>(StringComparer.Ordinal);
            for (var n = 0; n < nodeCount; n++)
            {
                foreach (var type in wins2[n])
                {
                    if (failedTypes.Count < 2)
                    {
                        nodes[n].FailObject(key2, type);
                        failedTypes.Add(type);
                    }
                    else
                    {
                        nodes[n].CompleteObject(key2, type);
                    }
                }
            }
            clock.Advance(TimeSpan.FromSeconds(300));
            foreach (var type in failedTypes)
                Assert.False(nodes[0].TryClaimObject(key2, type), "terminal 'failed' claim was reclaimed");
            var close2 = await Task.WhenAll(nodes.Select(node =>
                Task.Run(() => node.TryCloseCrawl(key2, types2))));
            Assert.Equal(1, close2.Count(r => r));
            Assert.Equal("failed", (string)gateways[0].Raw(
                $"SELECT Status FROM CrawlRuns WHERE CrawlKey = '{key2}';")!);

            sw.Stop();
            Assert.Equal(0, Interlocked.Read(ref busy.Value));  // no unhandled locks

            var totalOps = Interlocked.Read(ref opsBox.Value);
            StressLog.Line(_out,
                $"[ha] failover storm on ONE SQLite DB: {nodeCount} nodes × {objectCount} leases "
                + $"→ 48/48 single-owner claims (ownership {string.Join("/", wins.Select(w => w.Count))}); "
                + $"killed top-3 owners ({deadClaims.Count} leases) → "
                + $"{reclaimedBy.Count}/{deadClaims.Count} reclaimed exactly-once by survivors "
                + $"(spread {string.Join("/", survivors.Select(n => takeovers[n].Count))}); close race: 1 winner of 8; "
                + $"gen-2 with 2 failed claims → closed-as-failed, 1 winner, failed leases never reclaimed; "
                + $"{totalOps:N0} SQL ops in {sw.ElapsedMilliseconds} ms "
                + $"({totalOps / Math.Max(0.001, sw.Elapsed.TotalSeconds):N0} ops/s); "
                + "database-is-locked escapes = 0 — PASS");
        }
        finally
        {
            foreach (var gateway in gateways)
                gateway.Dispose();
        }
    }

    [Fact]
    public async Task SharedSqliteInventory_MultiNodeWriters_NoLoss_NoLockErrors()
    {
        using var dir = new TempDir();
        using var state = new SyncStateScope();
        var dbPath = Path.Combine(dir.Path, "shared_inventory.db");
        const string connector = "HaShared";
        const int nodeCount = 6;
        const int iterations = 250;
        const int batch = 20;

        long lockErrors = 0, invOps = 0;
        var sw = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, nodeCount).Select(n => Task.Run(() =>
        {
            // Each node holds its OWN connection to the SAME inventory file —
            // exactly what two HA crawl nodes sharing a state DB would do.
            using var inventory = new ItemInventory(connector, dbPath);
            for (var iter = 0; iter < iterations; iter++)
            {
                try
                {
                    inventory.RecordSeen(
                        Enumerable.Range(0, batch).Select(k => ($"n{n}_i{iter}_k{k}", "Task")),
                        DateTime.UtcNow);
                    Interlocked.Increment(ref invOps);
                    if (iter % 5 == 4)
                    {
                        // Remove the first half of the batch recorded 4 iterations ago.
                        inventory.Remove(
                            Enumerable.Range(0, 10).Select(k => $"n{n}_i{iter - 4}_k{k}"));
                        Interlocked.Increment(ref invOps);
                    }
                }
                catch (SqliteException exc) when (exc.SqliteErrorCode is 5 or 6)
                {
                    Interlocked.Increment(ref lockErrors);
                }

                // Checkpoint + dead-letter churn interleaved with inventory writes.
                SyncState.AppendFailedRecords(
                    connector,
                    new List<(string, string)> { ($"dl_n{n}_i{iter}", "err") },
                    "Task");
                SyncState.WriteCheckpoint(connector, null, "Task", iter + 1);
            }
        })));
        sw.Stop();

        Assert.Equal(0, Interlocked.Read(ref lockErrors));

        // Exact bookkeeping: nothing lost, nothing phantom.
        const int removedPerNode = (iterations / 5) * 10;
        var expected = nodeCount * (iterations * batch - removedPerNode);
        using (var check = new ItemInventory(connector, dbPath))
            Assert.Equal(expected, check.Count());
        Assert.Equal(nodeCount * iterations, SyncState.ReadFailedRecords(connector).Count);
        var checkpoint = SyncState.ReadCheckpoint(connector);
        Assert.Equal(iterations, checkpoint!["completed"]!["Task"]!.GetValue<int>());

        var writes = (long)nodeCount * iterations * batch + (long)nodeCount * removedPerNode;
        StressLog.Line(_out,
            $"[ha] shared SQLite inventory: {nodeCount} concurrent node connections on ONE file, "
            + $"{writes:N0} row writes + {nodeCount * iterations:N0} dead-letter appends + "
            + $"{nodeCount * iterations:N0} checkpoint writes in {sw.ElapsedMilliseconds} ms "
            + $"({writes / Math.Max(0.001, sw.Elapsed.TotalSeconds):N0} rows/s); final inventory "
            + $"= {expected:N0} (exact), dead-letter = {nodeCount * iterations:N0} (0 lost), "
            + $"checkpoint monotonic max = {iterations}; database-is-locked escapes = 0 — PASS");
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Scenario 5 — incremental crawl churn soak (webhooks + deletions, zero drift)
// ═════════════════════════════════════════════════════════════════════════════

public class IncrementalChurnSoakTests : IDisposable
{
    private const string Connector = "ClarizenAdaptiveWork";
    private readonly ITestOutputHelper _out;

    public IncrementalChurnSoakTests(ITestOutputHelper output)
    {
        _out = output;
        Metrics.ResetForTests();
    }

    public void Dispose() => Metrics.ResetForTests();

    private sealed record Entity(string Name, DateTime UpdatedUtc);

    [Fact]
    public async Task ChurnSoak_IncrementalCrawls_Webhooks_Deletions_ZeroDrift()
    {
        using var dir = new TempDir();
        using var state = new SyncStateScope();
        // The soak legitimately deletes large batches — disable both
        // mass-deletion guards for this run (their documented escape hatch).
        using var envScope = new EnvScope(
            ("DELETION_SYNC_MAX_ITEMS", "0"),
            ("DELETION_SYNC_MAX_PERCENT", "0"));

        // ── Source truth ─────────────────────────────────────────────────────
        var truthLock = new object();
        var live = new Dictionary<int, Entity>();
        var nextId = 1;
        var rng = new Random(1234);

        JsonObject Row(int id, Entity e) => new()
        {
            ["id"] = $"/Task/{id}",
            ["Name"] = e.Name,
            ["Owner"] = new JsonObject { ["id"] = "/User/1" },
            ["LastUpdatedOn"] = e.UpdatedUtc.ToString("o", CultureInfo.InvariantCulture),
        };

        var handler = new MockHttpHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/authentication/login"))
                return MockHttpHandler.Json(HttpStatusCode.OK, """{"sessionId": "s"}""");

            var q = JsonNode.Parse(body)?["q"]?.GetValue<string>() ?? string.Empty;
            var entities = new JsonArray();
            lock (truthLock)
            {
                var byId = Regex.Match(q, @"WHERE SYSID = (\S+)");
                if (byId.Success)
                {
                    if (int.TryParse(byId.Groups[1].Value, out var id)
                        && live.TryGetValue(id, out var e))
                    {
                        entities.Add(Row(id, e));
                    }
                }
                else
                {
                    DateTime? since = null;
                    var m = Regex.Match(q, @"LastUpdatedOn > ([0-9T:\-]+)Z");
                    if (m.Success)
                    {
                        since = DateTime.Parse(
                            m.Groups[1].Value + "Z", CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal);
                    }
                    foreach (var (id, e) in live)
                    {
                        // Inclusive compare: the query's since is truncated to
                        // seconds, so >= never misses a same-second change
                        // (idempotent re-serves are harmless).
                        if (since is null || e.UpdatedUtc >= since.Value)
                            entities.Add(Row(id, e));
                    }
                }
            }
            return MockHttpHandler.Json(HttpStatusCode.OK, new JsonObject
            {
                ["entities"] = entities,
                ["paging"] = new JsonObject { ["hasMore"] = false },
            }.ToJsonString());
        });

        // ── Connector under test ─────────────────────────────────────────────
        var config = TestConfig.Make(ingestChunkSize: 500, graphBatchSize: 20);
        var schema = new SchemaConfig
        {
            ObjectList = new List<ObjectConfig>
            {
                new()
                {
                    ObjectName = "Task",
                    DisplayName = "Task",
                    AclMode = "ownerOnly",
                    SelectedFields = new Dictionary<string, string>
                    {
                        ["Name"] = "Title",
                        ["Owner"] = "Owner",
                        ["LastUpdatedOn"] = "LastUpdatedOn",
                    },
                },
            },
        };
        var clarizen = new ClarizenClient(
            config, new ApiBudget(100_000_000, callsPerMinute: 600_000_000), handler);
        clarizen.DelayAsync = (_, _) => Task.CompletedTask;
        var store = new IdentityStore("Churn", Path.Combine(dir.Path, "id.db"));
        using var storeScope = store;
        store.Upsert(new PrincipalMapping("/User/1", "user", "o@x.com", "entra-o", DateTime.UtcNow));
        var resolver = new AclResolver(
            new PrincipalMapper(store, "{}"), new DirectorySnapshot(), string.Empty, string.Empty);
        var graph = new RecordingGraphClient(config);
        var pipeline = new IngestPipeline(
            config, schema, clarizen, graph, resolver, new ItemConverter(config),
            ha: null,
            inventoryFactory: id => new ItemInventory(id, Path.Combine(dir.Path, $"inv_{id}.db")));
        var processor = new WebhookProcessor(schema, clarizen, pipeline, TimeSpan.FromSeconds(2));
        var vnow = DateTime.UtcNow;
        processor.UtcNow = () => vnow;

        long offered = 0, coalesced = 0;
        void Offer(WebhookEvent evt)
        {
            if (processor.Debouncer.Offer(evt, vnow))
                Interlocked.Increment(ref coalesced);
            Interlocked.Increment(ref offered);
        }

        // ── Seed + first full crawl ──────────────────────────────────────────
        const int seed = 2500;
        lock (truthLock)
        {
            for (var i = 0; i < seed; i++)
            {
                live[nextId] = new Entity($"Task {nextId} r0", DateTime.UtcNow);
                nextId++;
            }
        }
        var sw = Stopwatch.StartNew();
        var first = await pipeline.RunAsync(fullCrawl: true);
        Assert.Equal(seed, first.Ingested);

        // ── Churn rounds ─────────────────────────────────────────────────────
        const int rounds = 12;
        const int createsPerRound = 150, updatesPerRound = 250, deletesPerRound = 100;
        long totalCreates = 0, totalUpdates = 0, totalDeletes = 0, racedUpserts = 0, silentDeletes = 0;
        var incrementalServed = 0L;

        for (var round = 1; round <= rounds; round++)
        {
            var created = new List<int>();
            var updated = new List<int>();
            var deleted = new List<int>();
            lock (truthLock)
            {
                for (var i = 0; i < createsPerRound; i++)
                {
                    live[nextId] = new Entity($"Task {nextId} r{round}", DateTime.UtcNow);
                    created.Add(nextId);
                    nextId++;
                }
                var ids = live.Keys.Where(id => !created.Contains(id)).ToList();
                for (var i = 0; i < updatesPerRound && ids.Count > 0; i++)
                {
                    var id = ids[rng.Next(ids.Count)];
                    live[id] = new Entity($"Task {id} r{round}u", DateTime.UtcNow);
                    updated.Add(id);
                }
                for (var i = 0; i < deletesPerRound && ids.Count > 0; i++)
                {
                    var id = ids[rng.Next(ids.Count)];
                    if (!live.Remove(id))
                        continue;
                    deleted.Add(id);
                    updated.Remove(id);
                }
            }
            totalCreates += created.Count;
            totalUpdates += updated.Count;
            totalDeletes += deleted.Count;

            // Webhook traffic: most changes are evented (some as duplicate
            // bursts that must coalesce); ~15% are silent and only the polling
            // cursor / deletion sweep may reconcile them.
            foreach (var id in created.Concat(updated))
            {
                var roll = rng.Next(100);
                if (roll < 15)
                    continue;                        // silent — poll discovers it
                var evt = new WebhookEvent("Task", id.ToString(CultureInfo.InvariantCulture), ChangeKind.Upsert);
                var copies = roll < 35 ? 5 : 1;      // 20% arrive as bursts
                for (var c = 0; c < copies; c++)
                    Offer(evt);
            }
            foreach (var id in deleted)
            {
                var roll = rng.Next(100);
                if (roll < 40)
                {
                    silentDeletes++;                 // only the sweep can fix it
                    continue;
                }
                Offer(new WebhookEvent("Task", id.ToString(CultureInfo.InvariantCulture), ChangeKind.Delete));
            }
            // Deletes that raced ahead of their notification: an UPSERT event
            // arrives for an already-deleted record → must become a withdraw.
            foreach (var id in deleted.Take(3))
            {
                Offer(new WebhookEvent("Task", id.ToString(CultureInfo.InvariantCulture), ChangeKind.Upsert));
                racedUpserts++;
            }
            // Unknown object types are dropped harmlessly.
            Offer(new WebhookEvent("Ghost", $"{round}", ChangeKind.Upsert));

            // Drain the debounced queue, then run the incremental crawl.
            vnow = vnow.AddSeconds(3);
            await processor.DrainOnceAsync();
            var inc = await pipeline.RunAsync(fullCrawl: false);
            Assert.False(inc.Degraded);
            Assert.False(inc.Stopped);
            incrementalServed += inc.Ingested;
        }

        // Final full crawl: the existence sweep withdraws every silent delete.
        var final = await pipeline.RunAsync(fullCrawl: true);
        sw.Stop();
        Assert.False(final.Degraded);
        Assert.Empty(final.SweepSkipped);

        // ── Drift check: Graph state and inventory vs source truth ───────────
        HashSet<string> expected;
        Dictionary<int, Entity> snapshot;
        lock (truthLock)
        {
            snapshot = new Dictionary<int, Entity>(live);
            expected = live.Keys.Select(id => $"Task_{id}").ToHashSet(StringComparer.Ordinal);
        }
        var applied = graph.AppliedIds();
        var missing = expected.Except(applied).ToList();
        var phantom = applied.Except(expected).ToList();
        var nameMismatches = snapshot.Count(kv =>
            graph.AppliedTitle($"Task_{kv.Key}") != kv.Value.Name);

        List<string> inventoryIds;
        using (var inv = new ItemInventory(Connector, Path.Combine(dir.Path, $"inv_{Connector}.db")))
            inventoryIds = inv.IdsForObject("Task");
        var invMissing = expected.Except(inventoryIds).Count();
        var invPhantom = inventoryIds.Except(expected).Count();

        var drift = missing.Count + phantom.Count + nameMismatches + invMissing + invPhantom;
        Assert.Empty(missing);
        Assert.Empty(phantom);
        Assert.Equal(0, nameMismatches);
        Assert.Equal(0, invMissing);
        Assert.Equal(0, invPhantom);
        Assert.Equal(0, drift);

        var events = Interlocked.Read(ref offered);
        StressLog.Line(_out,
            $"[churn] soak: seed {seed:N0} + {rounds} rounds "
            + $"({totalCreates:N0} creates / {totalUpdates:N0} updates / {totalDeletes:N0} deletes, "
            + $"{silentDeletes} silent deletes, {racedUpserts} raced upserts) — "
            + $"{events:N0} webhook events offered ({Interlocked.Read(ref coalesced):N0} coalesced), "
            + $"{rounds} incremental crawls served {incrementalServed:N0} rows, "
            + $"{graph.Puts:N0} PUTs / {graph.Deletes:N0} DELETEs applied in {sw.ElapsedMilliseconds} ms "
            + $"({events * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds):N0} events/s incl. crawls); "
            + $"final live = {expected.Count:N0}; DRIFT = {drift} (missing {missing.Count}, "
            + $"phantom {phantom.Count}, name mismatches {nameMismatches}, "
            + $"inventory Δ {invMissing + invPhantom}) — ZERO DRIFT");
    }
}
