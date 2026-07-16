// Checkpoint round-trip, sync timestamps and dead-letter write/read/clear
// (file backend).

using System.Text.Json.Nodes;
using SeismicConnector.Config;

namespace SeismicConnector.Tests;

public class CheckpointTests
{
    [Fact]
    public void Checkpoint_RoundTrips()
    {
        using var state = new TempStateDir();
        Assert.Null(SyncState.ReadCheckpoint("Conn"));

        SyncState.WriteCheckpoint("Conn", "2026-07-01T00:00:00", "ContentItem:ts1", 3);
        SyncState.WriteCheckpoint("Conn", "2026-07-01T00:00:00", "Library", 1);

        var checkpoint = SyncState.ReadCheckpoint("Conn");
        Assert.NotNull(checkpoint);
        Assert.Equal("2026-07-01T00:00:00", checkpoint!["since"]?.GetValue<string>());
        Assert.Equal(3, checkpoint["completed"]!["ContentItem:ts1"]!.GetValue<int>());
        Assert.Equal(1, checkpoint["completed"]!["Library"]!.GetValue<int>());
    }

    [Fact]
    public void Checkpoint_ChunkIndexOnlyIncreases()
    {
        using var state = new TempStateDir();
        SyncState.WriteCheckpoint("Conn", null, "ContentItem:ts1", 5);
        SyncState.WriteCheckpoint("Conn", null, "ContentItem:ts1", 2);  // stale writer loses
        var checkpoint = SyncState.ReadCheckpoint("Conn");
        Assert.Equal(5, checkpoint!["completed"]!["ContentItem:ts1"]!.GetValue<int>());
    }

    [Fact]
    public void Checkpoint_DifferentSince_ResetsCompletedMap()
    {
        using var state = new TempStateDir();
        SyncState.WriteCheckpoint("Conn", "2026-01-01T00:00:00", "Library", 4);
        SyncState.WriteCheckpoint("Conn", "2026-02-02T00:00:00", "Library", 1);  // new crawl boundary
        var checkpoint = SyncState.ReadCheckpoint("Conn");
        Assert.Equal("2026-02-02T00:00:00", checkpoint!["since"]?.GetValue<string>());
        Assert.Equal(1, checkpoint["completed"]!["Library"]!.GetValue<int>());
    }

    [Fact]
    public void Checkpoint_Clear_RemovesFile()
    {
        using var state = new TempStateDir();
        SyncState.WriteCheckpoint("Conn", null, "Library", 1);
        SyncState.ClearCheckpoint("Conn");
        Assert.Null(SyncState.ReadCheckpoint("Conn"));
        SyncState.ClearCheckpoint("Conn");  // idempotent
    }

    [Fact]
    public void LastSync_RoundTrips_PerConnector()
    {
        using var state = new TempStateDir();
        Assert.Null(SyncState.ReadLastSync("A"));

        var timestamp = new DateTime(2026, 7, 12, 8, 30, 0, DateTimeKind.Utc);
        SyncState.WriteLastSync("A", timestamp);
        SyncState.WriteLastSync("B", timestamp.AddHours(1));

        Assert.Equal(timestamp, SyncState.ReadLastSync("A"));
        Assert.Equal(timestamp.AddHours(1), SyncState.ReadLastSync("B"));
    }
}

public class DeadLetterTests
{
    [Fact]
    public void FailedRecords_WriteAndReadBack()
    {
        using var state = new TempStateDir();
        var path = SyncState.FailedRecordsPath("Conn");
        SyncState.AppendFailedRecords(
            path,
            new List<(string, string)> { ("item-1", "HTTP 500"), ("item-2", "HTTP 429") },
            "ContentItem",
            requestBodies: new Dictionary<string, JsonNode?>
            {
                ["item-1"] = new JsonObject { ["id"] = "item-1" },
            },
            responseBodies: new Dictionary<string, JsonNode?>
            {
                ["item-2"] = new JsonObject { ["error"] = "throttled" },
            });

        var records = SyncState.ReadFailedRecords("Conn");
        Assert.Equal(2, records.Count);
        Assert.Equal("item-1", records[0]["item_id"]?.GetValue<string>());
        Assert.Equal("HTTP 500", records[0]["error"]?.GetValue<string>());
        Assert.Equal("ContentItem", records[0]["object_type"]?.GetValue<string>());
        Assert.Equal("item-1", records[0]["request_body"]?["id"]?.GetValue<string>());
        Assert.Equal("throttled", records[1]["response_body"]?["error"]?.GetValue<string>());
    }

    [Fact]
    public void FailedRecords_AppendAccumulates()
    {
        using var state = new TempStateDir();
        var path = SyncState.FailedRecordsPath("Conn");
        SyncState.AppendFailedRecords(path, new List<string> { "a" }, "ContentItem", "err1");
        SyncState.AppendFailedRecords(path, new List<string> { "b" }, "Library", "err2");

        var records = SyncState.ReadFailedRecords("Conn");
        Assert.Equal(2, records.Count);
        Assert.Equal("Library", records[1]["object_type"]?.GetValue<string>());
        Assert.Equal("err2", records[1]["error"]?.GetValue<string>());
    }

    [Fact]
    public void FailedRecords_EmptyListIsNoop()
    {
        using var state = new TempStateDir();
        var path = SyncState.FailedRecordsPath("Conn");
        SyncState.AppendFailedRecords(path, new List<string>(), "ContentItem", "err");
        Assert.Empty(SyncState.ReadFailedRecords("Conn"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void FailedRecords_Clear()
    {
        using var state = new TempStateDir();
        var path = SyncState.FailedRecordsPath("Conn");
        SyncState.AppendFailedRecords(path, new List<string> { "a" }, "ContentItem", "err");
        SyncState.ClearFailedRecords("Conn");
        Assert.Empty(SyncState.ReadFailedRecords("Conn"));
        SyncState.ClearFailedRecords("Conn");  // idempotent
    }

    [Fact]
    public void FailedRecords_ConcurrentAppends_DoNotInterleave()
    {
        using var state = new TempStateDir();
        var path = SyncState.FailedRecordsPath("Conn");
        Parallel.For(0, 20, i =>
            SyncState.AppendFailedRecords(path, new List<string> { $"item-{i}" }, "ContentItem", "err"));

        var records = SyncState.ReadFailedRecords("Conn");
        Assert.Equal(20, records.Count);  // every line parsed cleanly
    }
}
