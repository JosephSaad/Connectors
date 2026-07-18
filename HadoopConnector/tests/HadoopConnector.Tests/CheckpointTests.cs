using HadoopConnector.Config;

namespace HadoopConnector.Tests;

public class CheckpointTests
{
    private const string Connector = "TestConnector";

    [Fact]
    public void Checkpoint_RoundTrip()
    {
        using var scope = new SyncStateScope();
        Assert.Null(SyncState.ReadCheckpoint(Connector));

        SyncState.WriteCheckpoint(Connector, "2026-07-01T00:00:00+00:00", "Project", 3);
        SyncState.WriteCheckpoint(Connector, "2026-07-01T00:00:00+00:00", "Task", 7);

        var checkpoint = SyncState.ReadCheckpoint(Connector);
        Assert.NotNull(checkpoint);
        Assert.Equal("2026-07-01T00:00:00+00:00", checkpoint!["since"]!.GetValue<string>());
        Assert.Equal(3, checkpoint["completed"]!["Project"]!.GetValue<int>());
        Assert.Equal(7, checkpoint["completed"]!["Task"]!.GetValue<int>());
    }

    [Fact]
    public void Checkpoint_ChunkIndexOnlyAdvances()
    {
        using var scope = new SyncStateScope();
        SyncState.WriteCheckpoint(Connector, null, "Project", 5);
        SyncState.WriteCheckpoint(Connector, null, "Project", 2);

        var checkpoint = SyncState.ReadCheckpoint(Connector);
        Assert.Equal(5, checkpoint!["completed"]!["Project"]!.GetValue<int>());
    }

    [Fact]
    public void Checkpoint_DifferentSince_ResetsCompletedMap()
    {
        using var scope = new SyncStateScope();
        SyncState.WriteCheckpoint(Connector, "2026-07-01T00:00:00+00:00", "Project", 4);
        SyncState.WriteCheckpoint(Connector, "2026-07-02T00:00:00+00:00", "Task", 1);

        var checkpoint = SyncState.ReadCheckpoint(Connector);
        Assert.Equal("2026-07-02T00:00:00+00:00", checkpoint!["since"]!.GetValue<string>());
        Assert.Null(checkpoint["completed"]!["Project"]);
        Assert.Equal(1, checkpoint["completed"]!["Task"]!.GetValue<int>());
    }

    [Fact]
    public void Checkpoint_Clear_RemovesFile()
    {
        using var scope = new SyncStateScope();
        SyncState.WriteCheckpoint(Connector, null, "Project", 1);
        Assert.NotNull(SyncState.ReadCheckpoint(Connector));
        SyncState.ClearCheckpoint(Connector);
        Assert.Null(SyncState.ReadCheckpoint(Connector));
        // Clearing twice is a no-op, not an error.
        SyncState.ClearCheckpoint(Connector);
    }

    [Fact]
    public void LastSync_RoundTrip_Utc()
    {
        using var scope = new SyncStateScope();
        Assert.Null(SyncState.ReadLastSync(Connector));

        var stamp = new DateTime(2026, 7, 12, 8, 30, 15, DateTimeKind.Utc);
        SyncState.WriteLastSync(Connector, stamp);

        var read = SyncState.ReadLastSync(Connector);
        Assert.NotNull(read);
        Assert.Equal(stamp, read!.Value);
        Assert.Equal(DateTimeKind.Utc, read.Value.Kind);
    }

    [Fact]
    public void LastSync_PerConnectorIsolation()
    {
        using var scope = new SyncStateScope();
        SyncState.WriteLastSync("A", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        SyncState.WriteLastSync("B", new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1, SyncState.ReadLastSync("A")!.Value.Month);
        Assert.Equal(2, SyncState.ReadLastSync("B")!.Value.Month);
    }

    [Fact]
    public void CorruptCheckpointFile_TreatedAsMissing()
    {
        using var scope = new SyncStateScope();
        Directory.CreateDirectory(SyncState.LogsDir);
        File.WriteAllText(
            Path.Combine(SyncState.LogsDir, $"checkpoint_{Connector}.json"), "{not json!!");
        Assert.Null(SyncState.ReadCheckpoint(Connector));
    }

    [Fact]
    public void IsoFormat_ParseIso_RoundTrip()
    {
        var stamp = new DateTime(2026, 7, 12, 8, 30, 15, 123, DateTimeKind.Utc);
        var iso = SyncState.IsoFormat(stamp);
        Assert.EndsWith("+00:00", iso);
        Assert.Equal(stamp, SyncState.ParseIso(iso));
    }

    [Fact]
    public void ParseIso_Invalid_Throws()
    {
        Assert.Throws<FormatException>(() => SyncState.ParseIso("not-a-date"));
    }
}
