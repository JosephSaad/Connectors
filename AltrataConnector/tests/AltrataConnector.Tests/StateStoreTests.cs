using AltrataConnector.Commands;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

public class CheckpointTests
{
    private static FileStateStore NewStore(out string root)
    {
        root = TestFixtures.NewTempDir("ckpt");
        return new FileStateStore("AltrataTest",
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));
    }

    [Fact]
    public void CheckpointRoundTrips()
    {
        var store = NewStore(out _);
        Assert.Null(store.GetCheckpoint());

        var checkpoint = new CrawlCheckpoint
        {
            DeliveryId = "2026-07-01_full",
            Dataset = "PersonProfile",
            FileName = "persons_001.json",
            RecordIndex = 420,
        };
        store.SaveCheckpoint(checkpoint);

        var loaded = store.GetCheckpoint();
        Assert.NotNull(loaded);
        Assert.Equal("2026-07-01_full", loaded!.DeliveryId);
        Assert.Equal("PersonProfile", loaded.Dataset);
        Assert.Equal("persons_001.json", loaded.FileName);
        Assert.Equal(420, loaded.RecordIndex);
    }

    [Fact]
    public void CheckpointSurvivesNewStoreInstance()
    {
        var store = NewStore(out var root);
        store.SaveCheckpoint(new CrawlCheckpoint
        {
            DeliveryId = "d1", Dataset = "Organization", FileName = "orgs.csv", RecordIndex = 7,
        });

        var reopened = new FileStateStore("AltrataTest",
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));
        Assert.Equal(7, reopened.GetCheckpoint()!.RecordIndex);
    }

    [Fact]
    public void ClearCheckpointRemovesIt()
    {
        var store = NewStore(out _);
        store.SaveCheckpoint(new CrawlCheckpoint
        {
            DeliveryId = "d1", Dataset = "Organization", FileName = "orgs.csv", RecordIndex = 1,
        });
        store.ClearCheckpoint();
        Assert.Null(store.GetCheckpoint());
    }

    [Fact]
    public void SyncTimestampsRoundTrip()
    {
        var store = NewStore(out _);
        Assert.Null(store.GetLastSync(CrawlKind.Full));
        var when = new DateTime(2026, 7, 12, 8, 30, 0, DateTimeKind.Utc);
        store.SetLastSync(CrawlKind.Full, when);
        Assert.Equal(when, store.GetLastSync(CrawlKind.Full));
        Assert.Null(store.GetLastSync(CrawlKind.Incremental));
    }

    [Fact]
    public void DeliveryLedgerRoundTrips()
    {
        var store = NewStore(out _);
        Assert.False(store.IsDeliveryProcessed("d1"));
        store.MarkDeliveryProcessed("d1", DateTime.UtcNow);
        Assert.True(store.IsDeliveryProcessed("d1"));
        Assert.Contains("d1", store.ListProcessedDeliveries());
    }

    [Fact]
    public void KeyValueRoundTripsAndDeletes()
    {
        var store = NewStore(out _);
        Assert.Null(store.GetValue("k"));
        store.SetValue("k", "v");
        Assert.Equal("v", store.GetValue("k"));
        store.SetValue("k", null);
        Assert.Null(store.GetValue("k"));
    }

    [Fact]
    public void WipeAllRemovesEverything()
    {
        var store = NewStore(out _);
        store.SaveCheckpoint(new CrawlCheckpoint
        {
            DeliveryId = "d1", Dataset = "Organization", FileName = "f", RecordIndex = 1,
        });
        store.AddDeadLetter(new DeadLetterRecord { ItemId = "x" });
        store.SetValue("k", "v");
        store.WipeAll();
        Assert.Null(store.GetCheckpoint());
        Assert.Empty(store.ReadDeadLetters());
        Assert.Null(store.GetValue("k"));
    }
}

public class DeadLetterTests
{
    private static FileStateStore NewStore()
    {
        var root = TestFixtures.NewTempDir("dlq");
        return new FileStateStore("AltrataTest",
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));
    }

    [Fact]
    public void DeadLettersAppendAndReadBack()
    {
        var store = NewStore();
        store.AddDeadLetter(new DeadLetterRecord
        {
            ItemId = "PersonProfile-P1",
            Dataset = "PersonProfile",
            DeliveryId = "d1",
            Error = "boom",
            PayloadJson = """{"id":"PersonProfile-P1"}""",
        });
        store.AddDeadLetter(new DeadLetterRecord { ItemId = "PersonProfile-P2", Dataset = "PersonProfile" });

        var records = store.ReadDeadLetters();
        Assert.Equal(2, records.Count);
        Assert.Equal("PersonProfile-P1", records[0].ItemId);
        Assert.Equal("boom", records[0].Error);
        Assert.Equal(1, records[0].Attempts);
    }

    [Fact]
    public void MalformedLinesAreSkipped()
    {
        var store = NewStore();
        store.AddDeadLetter(new DeadLetterRecord { ItemId = "ok" });
        File.AppendAllText(store.DeadLetterPath, "not-json{{{\n");
        store.AddDeadLetter(new DeadLetterRecord { ItemId = "ok2" });

        var records = store.ReadDeadLetters();
        Assert.Equal(2, records.Count);
    }

    [Fact]
    public void ReplaceRewritesTheQueue()
    {
        var store = NewStore();
        store.AddDeadLetter(new DeadLetterRecord { ItemId = "a" });
        store.AddDeadLetter(new DeadLetterRecord { ItemId = "b" });
        store.ReplaceDeadLetters(new[] { new DeadLetterRecord { ItemId = "b", Attempts = 2 } });

        var records = store.ReadDeadLetters();
        Assert.Single(records);
        Assert.Equal("b", records[0].ItemId);
        Assert.Equal(2, records[0].Attempts);
    }

    [Fact]
    public async Task RetryFailedReplaysAndKeepsFailures()
    {
        var root = TestFixtures.NewTempDir("retry");
        var config = TestFixtures.NewConfig();
        var graph = new FakeGraphClient();
        using var runtime = TestFixtures.NewRuntime(config, graph, root);
        // Upsert replays rebuild the ACL from the CURRENT seats (fail closed
        // when empty), so the store must hold a seat list.
        runtime.Identity.ReplaceSeats(TestFixtures.DefaultSeats());

        var goodPayload = System.Text.Json.JsonSerializer.Serialize(new AltrataConnector.Graph.ExternalItem
        {
            Id = "PersonProfile-P1",
            Acl = new[] { new AltrataConnector.Graph.AclEntry { Type = "user", Value = "alice@contoso.com" } },
            Properties = new Dictionary<string, object?> { ["title"] = "P1" },
        });
        var badPayload = System.Text.Json.JsonSerializer.Serialize(new AltrataConnector.Graph.ExternalItem
        {
            Id = "PersonProfile-P2",
            Acl = new[] { new AltrataConnector.Graph.AclEntry { Type = "user", Value = "alice@contoso.com" } },
            Properties = new Dictionary<string, object?>(),
        });
        runtime.State.AddDeadLetter(new DeadLetterRecord
            { ItemId = "PersonProfile-P1", PayloadJson = goodPayload });
        runtime.State.AddDeadLetter(new DeadLetterRecord
            { ItemId = "PersonProfile-P2", PayloadJson = badPayload });
        graph.FailingItemIds.Add("PersonProfile-P2");

        var result = await CommandRegistry.RetryFailedAsync(runtime, clearOnSuccess: false);

        Assert.Equal(false, result);  // one record still failing
        Assert.Single(graph.PutItems);
        var remaining = runtime.State.ReadDeadLetters();
        Assert.Single(remaining);
        Assert.Equal("PersonProfile-P2", remaining[0].ItemId);
        Assert.Equal(2, remaining[0].Attempts);
    }

    [Fact]
    public async Task RetryFailedClearOnSuccessEmptiesQueue()
    {
        var root = TestFixtures.NewTempDir("retry2");
        var config = TestFixtures.NewConfig();
        var graph = new FakeGraphClient();
        using var runtime = TestFixtures.NewRuntime(config, graph, root);
        runtime.Identity.ReplaceSeats(TestFixtures.DefaultSeats());

        var payload = System.Text.Json.JsonSerializer.Serialize(new AltrataConnector.Graph.ExternalItem
        {
            Id = "PersonProfile-P1",
            Acl = new[] { new AltrataConnector.Graph.AclEntry { Type = "user", Value = "alice@contoso.com" } },
            Properties = new Dictionary<string, object?>(),
        });
        runtime.State.AddDeadLetter(new DeadLetterRecord
            { ItemId = "PersonProfile-P1", PayloadJson = payload });

        var result = await CommandRegistry.RetryFailedAsync(runtime, clearOnSuccess: true);

        Assert.Equal(true, result);
        Assert.Empty(runtime.State.ReadDeadLetters());
        Assert.Single(graph.PutItems);
    }
}

public class CostCounterTests
{
    [Fact]
    public void BillableCounterPersistsAcrossInstances()
    {
        var root = TestFixtures.NewTempDir("cost");
        var store = new FileStateStore("AltrataTest",
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));

        Assert.Equal(0, store.GetBillableLookupCount());
        Assert.Equal(1, store.IncrementBillableLookups());
        Assert.Equal(3, store.IncrementBillableLookups(2));

        var reopened = new FileStateStore("AltrataTest",
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));
        Assert.Equal(3, reopened.GetBillableLookupCount());
    }
}
