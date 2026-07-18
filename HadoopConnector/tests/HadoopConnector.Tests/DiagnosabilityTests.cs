// Troubleshooting-diagnosability hardening: an operator must be able to answer
// "what failed, on which object/item/file, why, and what happens next" from
// the logs alone — and no single corrupt record/line/file may take down more
// than its own scope. These tests pin the boundaries added by the audit:
//
//   • dead-letter READ isolates a torn JSONL line (warn + skip, intact entries
//     survive) instead of crashing every queue reader,
//   • corrupt checkpoint / sync-state files fall back EXACTLY as before but now
//     say so in the log,
//   • a per-record conversion crash dead-letters THAT record (with the item id
//     and source file in the log) and the chunk continues,
//   • a per-file read failure logs the exact file path before the existing
//     fail-the-object (WORKER_CRASH) semantics run — and the crawl continues,
//   • an exception escaping a CLI command produces a structured final log
//     naming the command and args, with a nonzero exit code.

using System.Text;
using HadoopConnector;
using HadoopConnector.AclEngine;
using HadoopConnector.Config;
using HadoopConnector.Filters;
using HadoopConnector.Graph;
using HadoopConnector.Hdfs;
using HadoopConnector.Infrastructure;
using HadoopConnector.Item;

namespace HadoopConnector.Tests;

public class DiagnosabilityTests
{
    private const string Connector = "BdhHadoopMart";

    private static string Tid(int n) => $"T{n:D12}";

    /// <summary>Capture (level, message) pairs through Logging.TestSink for the scope.</summary>
    private sealed class LogCapture : IDisposable
    {
        public List<(LogLevel Level, string Logger, string Message)> Lines { get; } = new();

        public LogCapture() =>
            Logging.TestSink = (level, logger, message) => Lines.Add((level, logger, message));

        public bool Any(LogLevel level, string substring) =>
            Lines.Any(l => l.Level == level && l.Message.Contains(substring, StringComparison.Ordinal));

        public void Dispose() => Logging.TestSink = null;
    }

    // ── Dead-letter IO: a torn line must not crash the queue readers ─────────

    [Fact]
    public void ReadFailedRecords_CorruptLine_IsSkippedWithWarning_IntactEntriesSurvive()
    {
        using var scope = new SyncStateScope();
        using var logs = new LogCapture();

        // Two intact entries with a torn line (crash mid-append) between them.
        SyncState.AppendFailedRecords(
            Connector, new List<(string, string)> { (Tid(1), "HTTP 400: bad") }, "Task");
        File.AppendAllText(
            SyncState.FailedRecordsPath(Connector),
            "{\"item_id\":\"TORN_ENTRY\",\"object_ty\n", new UTF8Encoding(false));
        SyncState.AppendFailedRecords(
            Connector, new List<(string, string)> { (Tid(2), "HTTP 500: boom") }, "Task");

        var entries = SyncState.ReadFailedRecords(Connector);

        Assert.Equal(2, entries.Count);
        Assert.Equal(Tid(1), entries[0]["item_id"]!.GetValue<string>());
        Assert.Equal(Tid(2), entries[1]["item_id"]!.GetValue<string>());
        // The warning names the file and the line number of the bad entry.
        Assert.True(logs.Any(LogLevel.Warning, "line 2"),
            "expected a warning naming the corrupt dead-letter line");
        Assert.True(logs.Any(LogLevel.Warning, $"failed_records_{Connector}.jsonl"));
    }

    [Fact]
    public void ReadFailedRecords_NonObjectLine_IsSkippedWithWarning()
    {
        using var scope = new SyncStateScope();
        using var logs = new LogCapture();

        // Valid JSON but not an object — must be isolated the same way.
        File.WriteAllText(
            SyncState.FailedRecordsPath(Connector), "42\n", new UTF8Encoding(false));

        Assert.Empty(SyncState.ReadFailedRecords(Connector));
        Assert.True(logs.Any(LogLevel.Warning, "line 1"));
    }

    // ── Checkpoint / sync-state corruption: same fallback, now visible ───────

    [Fact]
    public void ReadCheckpoint_CorruptFile_ReturnsNull_AndWarns()
    {
        using var scope = new SyncStateScope();
        using var logs = new LogCapture();

        File.WriteAllText(
            Path.Combine(scope.LogsDir, $"checkpoint_{Connector}.json"),
            "{ not json !!", new UTF8Encoding(false));

        Assert.Null(SyncState.ReadCheckpoint(Connector));
        Assert.True(logs.Any(LogLevel.Warning, $"checkpoint_{Connector}.json"),
            "expected a warning naming the corrupt checkpoint file");
        Assert.True(logs.Any(LogLevel.Warning, "restarts from chunk 0"));
    }

    [Fact]
    public void ReadCheckpoint_MissingFile_IsSilent()
    {
        using var scope = new SyncStateScope();
        using var logs = new LogCapture();

        Assert.Null(SyncState.ReadCheckpoint(Connector));
        Assert.Empty(logs.Lines);  // a first run must not warn
    }

    [Fact]
    public void ReadLastSync_CorruptFile_ReturnsNull_AndWarns()
    {
        using var scope = new SyncStateScope();
        using var logs = new LogCapture();

        File.WriteAllText(
            Path.Combine(scope.LogsDir, "sync_state.json"), "###", new UTF8Encoding(false));

        Assert.Null(SyncState.ReadLastSync(Connector));
        Assert.True(logs.Any(LogLevel.Warning, "sync_state.json"));
        Assert.True(logs.Any(LogLevel.Warning, "never-synced"));
    }

    [Fact]
    public void WriteLastSync_CorruptExistingFile_Warns_AndRewritesValidFile()
    {
        using var scope = new SyncStateScope();
        using var logs = new LogCapture();

        File.WriteAllText(
            Path.Combine(scope.LogsDir, "sync_state.json"), "not-json", new UTF8Encoding(false));

        var stamp = new DateTime(2026, 7, 17, 8, 0, 0, DateTimeKind.Utc);
        SyncState.WriteLastSync(Connector, stamp);

        Assert.Equal(stamp, SyncState.ReadLastSync(Connector));
        Assert.True(logs.Any(LogLevel.Warning, "corrupt"));
    }

    // ── Per-record conversion crash: dead-letter + continue ──────────────────

    private sealed class PipelineFixture : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly SyncStateScope StateScope = new();
        public readonly AppConfig Config;
        public readonly SchemaConfig Schema;
        public readonly FakeGraphClient Graph;
        public readonly FakeBdhSource Source = new();
        public readonly IdentityStore Store;

        public PipelineFixture()
        {
            Config = TestConfig.Make(ingestChunkSize: 10, allowFullScan: true);
            Schema = new SchemaConfig
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
                            ["OwnerId"] = "OwnerId",
                        },
                    },
                },
            };
            Graph = new FakeGraphClient(Config);
            Store = new IdentityStore("DiagTests", Path.Combine(Dir.Path, "identity.db"));
            Store.Upsert(new PrincipalMapping(
                "005U0000001", "user", "owner@example.com", "entra-owner", DateTime.UtcNow));
        }

        public IngestPipeline Pipeline()
        {
            var fetcher = new BdhFetcher(Config, Source, FilterSet.Empty);
            var resolver = new AclResolver(
                new PrincipalMapper(Store), adminGroupId: string.Empty, fallbackGroupId: string.Empty);
            return new IngestPipeline(
                Config, Schema, fetcher, Graph, resolver, new ItemConverter(Config),
                ha: null,
                inventoryFactory: id => new ItemInventory(id, Path.Combine(Dir.Path, $"inv_{id}.db")));
        }

        public void Dispose()
        {
            Store.Dispose();
            StateScope.Dispose();
            Dir.Dispose();
        }
    }

    [Fact]
    public async Task ConversionCrash_DeadLettersThatRecord_ChunkContinues()
    {
        using var fixture = new PipelineFixture();
        using var logs = new LogCapture();

        // Record 2's Name is an object whose "name" is a NUMBER — the property
        // flattener throws for exactly this record; records 1 and 3 are fine.
        fixture.Source.Add("Task/dt=2026-07-15/part-0000.jsonl", string.Join("\n",
            $$"""{"Id":"{{Tid(1)}}","Name":"Task 1","OwnerId":"005U0000001"}""",
            $$"""{"Id":"{{Tid(2)}}","Name":{"name":123},"OwnerId":"005U0000001"}""",
            $$"""{"Id":"{{Tid(3)}}","Name":"Task 3","OwnerId":"005U0000001"}"""));

        var summary = await fixture.Pipeline().RunAsync(fullCrawl: true);

        // One record failed; the other two were still ingested; the OBJECT did
        // not fail and the crawl completed normally.
        Assert.Equal(2, summary.Ingested);
        Assert.Equal(1, summary.Failed);
        Assert.Empty(summary.FailedObjects);
        Assert.NotNull(SyncState.ReadLastSync(Connector));

        var entry = Assert.Single(SyncState.ReadFailedRecords(Connector));
        Assert.Equal(Tid(2), entry["item_id"]!.GetValue<string>());
        Assert.Equal("Task", entry["object_type"]!.GetValue<string>());
        Assert.StartsWith("[Convert]", entry["error"]!.GetValue<string>());

        // The error log identifies the record AND its source file.
        Assert.True(logs.Any(LogLevel.Error, Tid(2)),
            "expected an ERROR naming the failed record id");
        Assert.True(logs.Any(LogLevel.Error, "Task/dt=2026-07-15/part-0000.jsonl"),
            "expected the ERROR to name the source file");

        // The two good records really reached Graph.
        Assert.Contains(fixture.Graph.Sent, s => s.Path.EndsWith($"items/{Tid(1)}")
                                                 || s.Path == "$batch");
    }

    // ── Per-file read failure: log names the file, crawl continues ───────────

    [Fact]
    public async Task FileReadFailure_LogsFilePath_ObjectFails_CrawlContinues()
    {
        using var fixture = new PipelineFixture();
        using var logs = new LogCapture();

        // "Task" has a good file; a second object "Broken" has a .json export
        // that is not valid JSON — its parse throws mid-file-read.
        fixture.Schema.ObjectList.Add(new ObjectConfig
        {
            ObjectName = "Broken",
            DisplayName = "Broken",
            AclMode = "ownerOnly",
            SelectedFields = new Dictionary<string, string> { ["Name"] = "Title" },
        });
        fixture.Source.Add("Task/dt=2026-07-15/part-0000.jsonl",
            $$"""{"Id":"{{Tid(1)}}","Name":"Task 1","OwnerId":"005U0000001"}""");
        fixture.Source.Add("Broken/dt=2026-07-15/export.json", "this is not json");

        var summary = await fixture.Pipeline().RunAsync(fullCrawl: true);

        // Existing semantics preserved: the bad file fails ITS object
        // (WORKER_CRASH dead-letter), the other object still ingested.
        Assert.Equal(new[] { "Broken" }, summary.FailedObjects);
        Assert.Equal(1, summary.Ingested);
        var entry = Assert.Single(SyncState.ReadFailedRecords(Connector));
        Assert.Equal("WORKER_CRASH", entry["item_id"]!.GetValue<string>());
        Assert.Equal("Broken", entry["object_type"]!.GetValue<string>());

        // NEW: the log pinpoints the exact file that failed to read.
        Assert.True(logs.Any(LogLevel.Error, "Broken/dt=2026-07-15/export.json"),
            "expected an ERROR naming the unreadable BDH file");
    }

    // ── Top-level CLI backstop: structured log + nonzero exit ────────────────

    [Fact]
    public async Task ExecuteAsync_UnhandledConfigError_LogsCommandContext_ExitsNonzero()
    {
        // ingest-object bootstraps Runtime.Create OUTSIDE its own try/catch, so
        // a missing CONNECTOR_ID escapes to Program's final handler.
        using var env = new EnvScope(
            ("CONNECTOR_ID", null),
            ("AAD_APP_TENANT_ID", null),
            ("AAD_APP_CLIENT_ID", null),
            ("SECRET_AAD_APP_CLIENT_SECRET", null));
        using var dir = new TempDir();
        using var logs = new LogCapture();
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        var previousLogsRoot = Logging.LogsRoot;
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        Logging.LogsRoot = dir.Path;  // keep the bootstrap's run dir out of CWD
        try
        {
            var exitCode = await Program.ExecuteAsync(
                new[] { "ingest-object", "--type", "Task" });

            Assert.Equal(1, exitCode);
            Assert.True(logs.Any(LogLevel.Error, "ingest-object"),
                "expected the final ERROR to name the command");
            Assert.True(logs.Any(LogLevel.Error, "--type Task"),
                "expected the final ERROR to include the args summary");
            Assert.True(logs.Any(LogLevel.Error, "Invalid configuration"),
                "expected the final ERROR to carry the exception");
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
            Logging.TestSink = null;
            Logging.ResetForTests();
            Logging.LogsRoot = previousLogsRoot;
        }
    }

    [Fact]
    public void SummarizeArgs_JoinsArgv_AndHandlesEmpty()
    {
        Assert.Equal("(none)", Program.SummarizeArgs(Array.Empty<string>()));
        Assert.Equal("ingest --continuous", Program.SummarizeArgs(new[] { "ingest", "--continuous" }));
    }
}
