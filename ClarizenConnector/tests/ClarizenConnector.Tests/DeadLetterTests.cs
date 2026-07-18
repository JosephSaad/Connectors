using System.Text;
using System.Text.Json.Nodes;
using ClarizenConnector.Config;

namespace ClarizenConnector.Tests;

public class DeadLetterTests
{
    private const string Connector = "TestConnector";

    // RED→GREEN regression for the dead-letter writer's torn-tail gluing gap.
    // AppendFailedRecords opened the JSONL in append mode and wrote immediately;
    // if a prior crash left a TORN (unterminated) final line, the first new
    // record was glued onto that fragment, producing one unparseable line that
    // ReadFailedRecords silently skips — LOSING a failed item from the retry
    // safety net. Pre-fix the new record is unreadable; post-fix the writer
    // seals the boundary first, so the new record lands on its own parseable line.
    [Fact]
    public void Append_AfterTornFinalLine_NewRecordSurvivesAndIsParseable()
    {
        using var scope = new SyncStateScope();
        var path = SyncState.FailedRecordsPath(Connector);

        // Simulate a crash mid-append: a partial JSON record with NO trailing
        // newline written directly into the dead-letter file.
        File.WriteAllText(path, "{\"item_id\":\"Task_torn\",\"object_type\":\"Ta", new UTF8Encoding(false));

        // A new failure arrives after the crash.
        SyncState.AppendFailedRecords(
            Connector,
            new List<(string, string)> { ("Task_new", "HTTP 500: boom") },
            "Task");

        // The NEW record must be present and parseable — the torn fragment must
        // not have swallowed it. Pre-fix it is glued onto the fragment as one
        // unparseable line and ReadFailedRecords drops it entirely.
        var entries = SyncState.ReadFailedRecords(Connector);
        var newRecord = Assert.Single(
            entries, e => e["item_id"]?.GetValue<string>() == "Task_new");
        Assert.Equal("Task", newRecord["object_type"]!.GetValue<string>());
        Assert.Equal("HTTP 500: boom", newRecord["error"]!.GetValue<string>());

        // The torn fragment sits on its own (now isolated) line and does not
        // corrupt the new record's line: exactly one non-empty line parses.
        var parseableLines = File.ReadAllLines(path)
            .Where(l => l.Trim().Length > 0)
            .Count(l => { try { return JsonNode.Parse(l) is JsonObject; } catch { return false; } });
        Assert.Equal(1, parseableLines);
    }

    [Fact]
    public void Append_Read_Clear_RoundTrip()
    {
        using var scope = new SyncStateScope();
        Assert.Empty(SyncState.ReadFailedRecords(Connector));

        SyncState.AppendFailedRecords(
            Connector,
            new List<(string, string)> { ("Task_1", "HTTP 400: bad"), ("Task_2", "HTTP 500: boom") },
            "Task");

        var entries = SyncState.ReadFailedRecords(Connector);
        Assert.Equal(2, entries.Count);
        Assert.Equal("Task_1", entries[0]["item_id"]!.GetValue<string>());
        Assert.Equal("Task", entries[0]["object_type"]!.GetValue<string>());
        Assert.Equal("HTTP 400: bad", entries[0]["error"]!.GetValue<string>());
        Assert.NotNull(entries[0]["timestamp"]);

        SyncState.ClearFailedRecords(Connector);
        Assert.Empty(SyncState.ReadFailedRecords(Connector));
    }

    [Fact]
    public void Append_IsCumulative()
    {
        using var scope = new SyncStateScope();
        SyncState.AppendFailedRecords(Connector, new List<(string, string)> { ("A_1", "e1") }, "A");
        SyncState.AppendFailedRecords(Connector, new List<(string, string)> { ("A_2", "e2") }, "A");
        Assert.Equal(2, SyncState.ReadFailedRecords(Connector).Count);
    }

    [Fact]
    public void Append_EmptyList_WritesNothing()
    {
        using var scope = new SyncStateScope();
        SyncState.AppendFailedRecords(Connector, new List<(string, string)>(), "A");
        Assert.False(File.Exists(Path.Combine(scope.LogsDir, $"failed_records_{Connector}.jsonl"))
                     && new FileInfo(Path.Combine(scope.LogsDir, $"failed_records_{Connector}.jsonl")).Length > 0);
        Assert.Empty(SyncState.ReadFailedRecords(Connector));
    }

    [Fact]
    public void Append_IncludesRequestAndResponseBodies()
    {
        using var scope = new SyncStateScope();
        // This test asserts VERBATIM request/response bodies, so it pins full
        // mode explicitly — the shipped default is now redacted.
        using var env = new EnvScope(("DEADLETTER_PAYLOAD_MODE", "full"));
        var request = new Dictionary<string, JsonNode?>
        {
            ["Task_9"] = new JsonObject { ["id"] = "Task_9", ["properties"] = new JsonObject() },
        };
        var response = new Dictionary<string, JsonNode?>
        {
            ["Task_9"] = new JsonObject { ["error"] = "schema mismatch" },
        };
        SyncState.AppendFailedRecords(
            Connector, new List<(string, string)> { ("Task_9", "HTTP 400") }, "Task", request, response);

        var entry = Assert.Single(SyncState.ReadFailedRecords(Connector));
        Assert.Equal("Task_9", entry["request_body"]!["id"]!.GetValue<string>());
        Assert.Equal("schema mismatch", entry["response_body"]!["error"]!.GetValue<string>());
    }

    [Fact]
    public void DeadLetterFile_IsOneJsonObjectPerLine()
    {
        using var scope = new SyncStateScope();
        SyncState.AppendFailedRecords(
            Connector,
            new List<(string, string)> { ("X_1", "e"), ("X_2", "e"), ("X_3", "e") },
            "X");
        var lines = File.ReadAllLines(SyncState.FailedRecordsPath(Connector))
            .Where(l => l.Trim().Length > 0)
            .ToList();
        Assert.Equal(3, lines.Count);
        foreach (var line in lines)
            Assert.NotNull(JsonNode.Parse(line));
    }
}
