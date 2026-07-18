using System.Text.Json.Nodes;
using HadoopConnector.Config;

namespace HadoopConnector.Tests;

public class DeadLetterTests
{
    private const string Connector = "TestConnector";

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
        // Genuine full-mode round-trip (checks the response body value verbatim):
        // the DEFAULT is now 'redacted', so pin 'full' explicitly here.
        using var env = new EnvScope((DeadLetterRedaction.ModeEnvVar, "full"));
        using var scope = new SyncStateScope();
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
    public void Append_AfterCrashTornFinalLine_SealsBoundaryAndPreservesNewRecord()
    {
        using var scope = new SyncStateScope();

        // First, write a legitimate failure so the queue has a good entry, then
        // simulate a process killed mid-append: a partial, unterminated final line
        // (a torn tail — no trailing newline) left directly on the dead-letter
        // file. Without the boundary seal, the NEXT append is glued onto this
        // fragment, producing one unparseable line that ReadFailedRecords silently
        // skips — LOSING the new failure from the retry safety net.
        SyncState.AppendFailedRecords(
            Connector, new List<(string, string)> { ("Good_1", "HTTP 400: earlier") }, "Task");
        var path = SyncState.FailedRecordsPath(Connector);
        File.AppendAllText(path, "{\"item_id\":\"Torn_2\",\"object_type\":\"Task\",\"error\":\"HTTP 5");

        // The recovery append must seal the torn tail first, so the new record
        // lands on its own parseable line.
        SyncState.AppendFailedRecords(
            Connector, new List<(string, string)> { ("New_3", "HTTP 503: after crash") }, "Task");

        var entries = SyncState.ReadFailedRecords(Connector);
        // The NEW record survived intact and parseable, and the torn fragment did
        // not corrupt it.
        var newRecord = entries.SingleOrDefault(e => e["item_id"]?.GetValue<string>() == "New_3");
        Assert.NotNull(newRecord);
        Assert.Equal("Task", newRecord!["object_type"]!.GetValue<string>());
        Assert.Equal("HTTP 503: after crash", newRecord["error"]!.GetValue<string>());
        // The pre-crash good entry is still present too.
        Assert.Contains(entries, e => e["item_id"]?.GetValue<string>() == "Good_1");
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
