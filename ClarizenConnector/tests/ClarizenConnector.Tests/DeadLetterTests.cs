using System.Text.Json.Nodes;
using ClarizenConnector.Config;

namespace ClarizenConnector.Tests;

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
