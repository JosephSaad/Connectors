// Troubleshooting-diagnosability hardening tests: an operator must be able to
// answer "what failed, on which object/record/chunk/file, why, and what happens
// next" from logs + dead-letter records alone. These tests pin:
//
//   • per-line dead-letter read isolation (a torn JSONL line never hides the
//     rest of the queue, and the bad line is logged verbatim with file + line);
//   • corrupt state files (checkpoint / sync_state) warn with the path while
//     missing files stay silent (the normal first-run path);
//   • TDW export parse failures name the export FILE and object type, and the
//     resulting WORKER_CRASH dead-letter record carries the exception type;
//   • per-row transform failures dead-letter with the chunk index + exception
//     type and log the record id + chunk;
//   • classification.json invalid regexes are skipped LOUDLY (category +
//     pattern named) with valid patterns still active;
//   • the top-level CLI handler logs a structured error naming the command and
//     exits nonzero on an unhandled exception.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ClarizenConnector.AclEngine;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Content;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;
using ClarizenConnector.Item;

namespace ClarizenConnector.Tests;

public class DiagnosabilityTests
{
    private const string Connector = "ClarizenAdaptiveWork";

    /// <summary>Capture TestSink lines for the scope; always restores on dispose.</summary>
    private sealed class LogCapture : IDisposable
    {
        public List<(LogLevel Level, string Logger, string Message)> Lines { get; } = new();

        public LogCapture()
        {
            Logging.TestSink = (level, logger, message) => Lines.Add((level, logger, message));
        }

        public void Dispose() => Logging.TestSink = null;

        public IEnumerable<string> Messages(LogLevel level) =>
            Lines.Where(l => l.Level == level).Select(l => l.Message);
    }

    // ── Dead-letter read isolation ───────────────────────────────────────────

    [Fact]
    public void DeadLetter_CorruptLines_AreSkippedAndLogged_GoodEntriesSurvive()
    {
        using var state = new SyncStateScope();
        SyncState.AppendFailedRecords(
            Connector,
            new List<(string, string)> { ("Task_1", "HTTP 400: bad"), ("Task_2", "HTTP 500: boom") },
            "Task");

        // Simulate a crash mid-append (torn line) plus a non-object line.
        var path = SyncState.FailedRecordsPath(Connector);
        File.AppendAllText(path, "{\"item_id\":\"Task_torn\",\"object_ty\n\"just a string\"\n", new UTF8Encoding(false));

        using var capture = new LogCapture();
        var entries = SyncState.ReadFailedRecords(Connector);

        // Both parseable entries survive; the two bad lines are skipped.
        Assert.Equal(2, entries.Count);
        Assert.Equal("Task_1", entries[0]["item_id"]!.GetValue<string>());
        Assert.Equal("Task_2", entries[1]["item_id"]!.GetValue<string>());

        // Each skipped line is logged with the FILE, the LINE NUMBER and the
        // raw content, so nothing about the failure is lost.
        var errors = capture.Messages(LogLevel.Error).ToList();
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains(path) && e.Contains("line 3") && e.Contains("Task_torn"));
        Assert.Contains(errors, e => e.Contains(path) && e.Contains("line 4") && e.Contains("just a string"));
    }

    [Fact]
    public void DeadLetter_MissingFile_IsEmptyAndSilent()
    {
        using var state = new SyncStateScope();
        using var capture = new LogCapture();

        Assert.Empty(SyncState.ReadFailedRecords("NeverWritten"));
        Assert.Empty(capture.Messages(LogLevel.Error));
        Assert.Empty(capture.Messages(LogLevel.Warning));
    }

    // ── Corrupt vs missing state files ───────────────────────────────────────

    [Fact]
    public void Checkpoint_CorruptFile_WarnsWithPath_MissingFileStaysSilent()
    {
        using var state = new SyncStateScope();
        using var capture = new LogCapture();

        // Missing checkpoint: the normal first-run path — no log noise.
        Assert.Null(SyncState.ReadCheckpoint(Connector));
        Assert.Empty(capture.Messages(LogLevel.Warning));

        // Corrupt checkpoint: still returns null (unchanged semantics) but now
        // says WHICH file failed and what happens next.
        var path = Path.Combine(state.LogsDir, $"checkpoint_{Connector}.json");
        File.WriteAllText(path, "{{{ not json");
        Assert.Null(SyncState.ReadCheckpoint(Connector));
        var warning = Assert.Single(capture.Messages(LogLevel.Warning));
        Assert.Contains(path, warning);
        Assert.Contains("could not be parsed", warning);
    }

    [Fact]
    public void LastSync_CorruptFile_WarnsWithPath_AndReturnsNull()
    {
        using var state = new SyncStateScope();
        var path = Path.Combine(state.LogsDir, "sync_state.json");
        File.WriteAllText(path, "not json at all");

        using var capture = new LogCapture();
        Assert.Null(SyncState.ReadLastSync(Connector));

        var warning = Assert.Single(capture.Messages(LogLevel.Warning));
        Assert.Contains(path, warning);
        Assert.Contains(Connector, warning);
    }

    // ── TDW export parse diagnostics ─────────────────────────────────────────

    [Fact]
    public void TdwReadObject_UnparseableJson_NamesFileAndObjectType()
    {
        using var dir = new TempDir();
        var exportFile = Path.Combine(dir.Path, "Task.json");
        File.WriteAllText(exportFile, "{ this is not json");

        using var capture = new LogCapture();
        var reader = new TdwBulkReader(dir.Path);
        var exc = Assert.Throws<InvalidDataException>(() => reader.ReadObject("Task"));

        // The exception now names the export file and the object type, and
        // keeps the original parse error as the inner exception.
        Assert.Contains(exportFile, exc.Message);
        Assert.Contains("'Task'", exc.Message);
        Assert.NotNull(exc.InnerException);
        Assert.Contains(capture.Messages(LogLevel.Error), e => e.Contains(exportFile));
    }

    [Fact]
    public async Task TdwUnparseableFile_DeadLetterRecord_NamesExportFileAndExceptionType()
    {
        using var dir = new TempDir();
        using var state = new SyncStateScope();
        var export = Path.Combine(dir.Path, "tdw");
        Directory.CreateDirectory(export);
        File.WriteAllText(Path.Combine(export, "Task.json"), "definitely not json");

        var config = TestConfig.Make(tdwExportPath: export);
        var (pipeline, _) = BuildPipeline(dir, config);

        var summary = await pipeline.RunAsync(fullCrawl: true);

        // The unusable export is terminal for the object (worker-crash path,
        // crawl continues/completes) and the dead-letter record answers "what
        // failed and why" on its own: object, export file, exception type.
        Assert.Equal(new[] { "Task" }, summary.FailedObjects);
        var entry = Assert.Single(SyncState.ReadFailedRecords(Connector));
        Assert.Equal("WORKER_CRASH", entry["item_id"]!.GetValue<string>());
        Assert.Equal("Task", entry["object_type"]!.GetValue<string>());
        var error = entry["error"]!.GetValue<string>();
        Assert.Contains("InvalidDataException", error);
        Assert.Contains("Task.json", error);
    }

    // ── Per-row transform failure context ────────────────────────────────────

    [Fact]
    public async Task TransformFailure_DeadLetterCarriesChunkIndexAndExceptionType()
    {
        using var dir = new TempDir();
        using var state = new SyncStateScope();
        var export = Path.Combine(dir.Path, "tdw");
        Directory.CreateDirectory(export);

        // Chunk size 2 → chunk 1 = [g0, g1], chunk 2 = [p0, g2]. The poisoned
        // row (reference whose name is a number chokes the converter) must be
        // dead-lettered with ITS chunk index while the other three rows ingest.
        File.WriteAllText(
            Path.Combine(export, "Task.json"),
            "[" + string.Join(",",
                GoodRow("g0"), GoodRow("g1"), PoisonedRow("p0"), GoodRow("g2")) + "]",
            new UTF8Encoding(false));

        var config = TestConfig.Make(tdwExportPath: export, ingestChunkSize: 2);
        var (pipeline, _) = BuildPipeline(dir, config);

        using var capture = new LogCapture();
        var summary = await pipeline.RunAsync(fullCrawl: true);

        Assert.Equal(3, summary.Ingested);
        Assert.Equal(1, summary.Failed);
        Assert.Empty(summary.FailedObjects);  // no worker crash — row isolated

        var entry = Assert.Single(SyncState.ReadFailedRecords(Connector));
        Assert.Equal("Task_p0", entry["item_id"]!.GetValue<string>());
        var error = entry["error"]!.GetValue<string>();
        Assert.StartsWith("[transform chunk 2]", error, StringComparison.Ordinal);
        Assert.Contains("Exception", error);  // exception TYPE preserved

        // The log line identifies object type, record id and chunk, and carries
        // the full exception (type + stack trace) for the unexpected failure.
        var logged = Assert.Single(
            capture.Messages(LogLevel.Error).Where(e => e.Contains("failed to transform")));
        Assert.Contains("[Task]", logged);
        Assert.Contains("Task_p0", logged);
        Assert.Contains("chunk 2", logged);
        Assert.Contains("   at ", logged);  // stack frame present
    }

    private static string GoodRow(string id) =>
        $"{{\"id\":\"/Task/{id}\",\"Name\":\"Task {id}\",\"Owner\":{{\"id\":\"/User/1\"}},\"State\":\"Active\"}}";

    private static string PoisonedRow(string id) =>
        $"{{\"id\":\"/Task/{id}\",\"Name\":\"Task {id}\",\"Owner\":{{\"id\":\"/User/1\"}},\"State\":{{\"name\":123}}}}";

    private static (IngestPipeline Pipeline, RecordingGraphClient Graph) BuildPipeline(
        TempDir dir, AppConfig config)
    {
        var handler = new MockHttpHandler((_, _) =>
            MockHttpHandler.Json(HttpStatusCode.OK, """{"sessionId": "s"}"""));
        var clarizen = new ClarizenClient(
            config, new ApiBudget(1_000_000, callsPerMinute: 6_000_000), handler);
        clarizen.DelayAsync = (_, _) => Task.CompletedTask;
        var store = new IdentityStore("DiagTests", Path.Combine(dir.Path, "id.db"));
        store.Upsert(new PrincipalMapping("/User/1", "user", "o@x.com", "entra-o", DateTime.UtcNow));
        var mapper = new PrincipalMapper(store, "{}");
        var resolver = new AclResolver(mapper, new DirectorySnapshot(), string.Empty, string.Empty);
        var graph = new RecordingGraphClient(config);
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
                        ["State"] = "State",
                    },
                },
            },
        };
        var pipeline = new IngestPipeline(
            config, schema, clarizen, graph, resolver, new ItemConverter(config),
            ha: null,
            inventoryFactory: id => new ItemInventory(id, Path.Combine(dir.Path, $"inv_{id}.db")));
        return (pipeline, graph);
    }

    // ── Classifier config diagnostics ────────────────────────────────────────

    [Fact]
    public void Classifier_InvalidRegex_WarnsWithCategoryAndPattern_ValidPatternsStillActive()
    {
        using var capture = new LogCapture();
        var classifier = ContentClassifier.FromJson("""
            {
              "categories": [
                {
                  "name": "PII",
                  "patterns": [
                    {"name": "broken", "regex": "["},
                    {"name": "email", "regex": "[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}"}
                  ]
                }
              ]
            }
            """);

        // The invalid pattern is skipped LOUDLY — category and pattern named,
        // so a quietly-dead detection rule is visible in the logs.
        var warning = Assert.Single(capture.Messages(LogLevel.Warning));
        Assert.Contains("'PII'", warning);
        Assert.Contains("'broken'", warning);

        // ...and the valid pattern in the same category still detects.
        Assert.Contains("PII", classifier.Detect("contact jane.doe@example.com today"));
    }

    // ── Top-level CLI backstop ───────────────────────────────────────────────

    [Fact]
    public async Task TopLevelHandler_UnhandledCommandException_LogsCommandAndExitsNonzero()
    {
        using var dir = new TempDir();
        var previousRoot = Logging.LogsRoot;
        // Clear the required config so Runtime.Create throws OUTSIDE the
        // command's own try/catch — the top-level backstop must handle it.
        using var env = new EnvScope(
            ("CONNECTOR_ID", null),
            ("CLARIZEN_USERNAME", null),
            ("SECRET_CLARIZEN_PASSWORD", null),
            ("AAD_APP_TENANT_ID", null),
            ("AAD_APP_CLIENT_ID", null),
            ("SECRET_AAD_APP_CLIENT_SECRET", null),
            ("USE_KEY_VAULT", null),
            ("LOG_LEVEL", null));
        try
        {
            Logging.LogsRoot = dir.Path;
            using var capture = new LogCapture();

            var exitCode = await Program.ExecuteAsync(new[] { "ingest-object", "--type", "Task" });

            Assert.Equal(1, exitCode);
            var final = Assert.Single(capture.Lines.Where(l =>
                l.Level == LogLevel.Error && l.Logger == "clarizen_connector.cli"));
            // The structured backstop names the COMMAND and keeps the exception
            // (type + stack) so the failure is diagnosable from logs alone.
            Assert.Contains("'ingest-object'", final.Message);
            Assert.Contains("Exception", final.Message);
        }
        finally
        {
            Logging.ResetForTests();
            Logging.LogsRoot = previousRoot;
        }
    }
}
