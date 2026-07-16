using AltrataConnector.Altrata;
using AltrataConnector.Config;
using AltrataConnector.Entitlement;
using AltrataConnector.Identity;
using AltrataConnector.Infrastructure;
using AltrataConnector.Ingestion;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

/// <summary>Harness wiring a full crawl engine over temp dirs and fakes.</summary>
public sealed class CrawlHarness : IDisposable
{
    public string Root { get; }
    public string FeedPath { get; }
    public AppConfig Config { get; }
    public FileStateStore State { get; }
    public SqliteIdentityStore Identity { get; }
    public FakeGraphClient Graph { get; } = new();
    public FakeAlertSink Alerts { get; } = new();
    public SeatService Seats { get; }
    public CrawlEngine Engine { get; }

    private readonly string? _previousLogsDir;

    public CrawlHarness(int retentionDays = 0, string retentionMode = "archive",
        string? crmContactsPath = null, Func<AppConfig, AppConfig>? configure = null)
    {
        Root = TestFixtures.NewTempDir("crawl");
        FeedPath = Path.Combine(Root, "feed");
        Directory.CreateDirectory(FeedPath);

        var seatPath = Path.Combine(Root, "seats.json");
        TestFixtures.WriteSeatFile(seatPath, "alice@contoso.com", "bob@contoso.com");

        // Route default-path artifacts (reconciliation reports, audit) into the temp root.
        _previousLogsDir = Environment.GetEnvironmentVariable("LOGS_DIR");
        Environment.SetEnvironmentVariable("LOGS_DIR", Path.Combine(Root, "logs"));

        Config = TestFixtures.NewConfig(feedPath: FeedPath, seatListPath: seatPath,
            dataDir: Path.Combine(Root, "data"), retentionDays: retentionDays,
            retentionMode: retentionMode, crmContactsPath: crmContactsPath);
        if (configure != null)
            Config = configure(Config);
        State = new FileStateStore(Config.ConnectorId,
            logsDir: Path.Combine(Root, "logs"), dataDir: Path.Combine(Root, "data"));
        Identity = new SqliteIdentityStore(Path.Combine(Root, "data", "identity.db"));
        Seats = new SeatService(Config, Identity, State);
        Engine = new CrawlEngine(Config, Graph, State, Identity, Seats, Alerts);
        ServiceStop.ResetForTests();
    }

    public void WriteSeats(params string[] users) =>
        TestFixtures.WriteSeatFile(Path.Combine(Root, "seats.json"), users);

    public void Dispose()
    {
        Identity.Dispose();
        Environment.SetEnvironmentVariable("LOGS_DIR", _previousLogsDir);
        ServiceStop.ResetForTests();
    }
}

public class CrawlEngineTests
{
    [Fact]
    public async Task FullCrawlIngestsRecordsWithSeatOnlyAcls()
    {
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile,
                TestFixtures.PersonJson(("P1", "Ada Lovelace", "ada@x.com", "Acme"),
                                        ("P2", "Charles Babbage", null, null)), 2));

        var result = await harness.Engine.RunAsync(CrawlKind.Full);

        Assert.Equal(1, result.DeliveriesProcessed);
        Assert.Equal(0, result.DeliveriesRejected);
        Assert.Equal(2, result.ItemsIngested);
        Assert.Equal(2, harness.Graph.PutItems.Count);

        // Every ACL grants ONLY the two seats; never everyone.
        foreach (var item in harness.Graph.PutItems)
        {
            Assert.Equal(2, item.Acl.Count);
            Assert.All(item.Acl, e => Assert.Equal("user", e.Type));
            Assert.DoesNotContain(item.Acl, e => e.Type is "everyone" or "everyoneExceptGuests");
        }

        // Delivery marked processed; registry populated; reconciliation written.
        Assert.True(harness.State.IsDeliveryProcessed("d1"));
        Assert.Equal(2, harness.Identity.CountIngestedItems());
        Assert.True(File.Exists(Reconciliation.ReportPath(harness.Config.ConnectorId, "d1")));
        Assert.Single(result.Reconciliations);
        Assert.Equal(Reconciliation.StatusReconciled, result.Reconciliations[0].Status);
    }

    [Fact]
    public async Task ChecksumMismatchRejectsDeliveryAndAlerts()
    {
        using var harness = new CrawlHarness();
        var delivery = TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P1", "A", null, null)), 1));
        File.AppendAllText(Path.Combine(delivery.Directory, "persons.json"), "tampered");

        var result = await harness.Engine.RunAsync(CrawlKind.Full);

        Assert.Equal(1, result.DeliveriesRejected);
        Assert.Equal(0, result.ItemsIngested);
        Assert.Empty(harness.Graph.PutItems);              // nothing ingested
        Assert.False(harness.State.IsDeliveryProcessed("d1"));
        Assert.Contains(harness.Alerts.Alerts, a => a.Event == "delivery_rejected");
        Assert.Equal(Reconciliation.StatusRejected, result.Reconciliations[0].Status);
    }

    [Fact]
    public async Task FailedItemsAreDeadLetteredAndStillReconcile()
    {
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile,
                TestFixtures.PersonJson(("P1", "A", null, null), ("P2", "B", null, null),
                                        ("P3", "C", null, null)), 3));
        harness.Graph.FailingItemIds.Add("PersonProfile-P2");

        var result = await harness.Engine.RunAsync(CrawlKind.Full);

        Assert.Equal(2, result.ItemsIngested);
        Assert.Equal(1, result.ItemsDeadLettered);
        var deadLetters = harness.State.ReadDeadLetters();
        Assert.Single(deadLetters);
        Assert.Equal("PersonProfile-P2", deadLetters[0].ItemId);
        Assert.NotEmpty(deadLetters[0].PayloadJson);

        // ingested + dead-lettered == manifest ⇒ reconciled and marked processed.
        Assert.Equal(Reconciliation.StatusReconciled, result.Reconciliations[0].Status);
        Assert.True(harness.State.IsDeliveryProcessed("d1"));
    }

    [Fact]
    public async Task IncrementalCrawlSkipsProcessedDeliveries()
    {
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P1", "A", null, null)), 1));

        await harness.Engine.RunAsync(CrawlKind.Full);
        Assert.Single(harness.Graph.PutItems);

        TestFixtures.WriteDelivery(harness.FeedPath, "d2",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P9", "Z", null, null)), 1));

        var result = await harness.Engine.RunAsync(CrawlKind.Incremental);
        Assert.Equal(1, result.DeliveriesProcessed);
        Assert.Equal(2, harness.Graph.PutItems.Count);
        Assert.Equal("PersonProfile-P9", harness.Graph.PutItems[^1].Id);
    }

    [Fact]
    public async Task DatasetFilterIngestsOnlyThatDataset()
    {
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P1", "A", null, null)), 1),
            ("wealth.json", Datasets.WealthIndicator,
                """[{"id":"W1","person_id":"P1","net_worth_usd":"9000000"}]""", 1));

        await harness.Engine.RunAsync(CrawlKind.Full, Datasets.WealthIndicator);

        Assert.Single(harness.Graph.PutItems);
        Assert.StartsWith("WealthIndicator-", harness.Graph.PutItems[0].Id);
    }

    [Fact]
    public async Task CheckpointResumeSkipsAlreadyIngestedRecords()
    {
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile,
                TestFixtures.PersonJson(("P1", "A", null, null), ("P2", "B", null, null),
                                        ("P3", "C", null, null), ("P4", "D", null, null)), 4));

        // Simulate a crash after 2 records were ingested.
        harness.State.SaveCheckpoint(new CrawlCheckpoint
        {
            DeliveryId = "d1",
            Dataset = Datasets.PersonProfile,
            FileName = "persons.json",
            RecordIndex = 2,
        });

        var result = await harness.Engine.RunAsync(CrawlKind.Full);

        // Only P3/P4 re-PUT; reconciliation counts the earlier 2 as ingested.
        Assert.Equal(2, harness.Graph.PutItems.Count);
        Assert.Equal(new[] { "PersonProfile-P3", "PersonProfile-P4" },
            harness.Graph.PutItems.Select(i => i.Id).ToArray());
        Assert.Equal(Reconciliation.StatusReconciled, result.Reconciliations[0].Status);
        Assert.Null(harness.State.GetCheckpoint());  // cleared on completion
    }

    [Fact]
    public async Task GracefulStopFinishesChunkAndSavesCheckpoint()
    {
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile,
                TestFixtures.PersonJson(("P1", "A", null, null), ("P2", "B", null, null),
                                        ("P3", "C", null, null), ("P4", "D", null, null),
                                        ("P5", "E", null, null), ("P6", "F", null, null)), 6));

        // Request the stop after the second PUT (mid-chunk, batch size 2).
        harness.Graph.FailWhen = item =>
        {
            if (harness.Graph.PutItems.Count == 1)
                ServiceStop.Request();
            return false;
        };

        var result = await harness.Engine.RunAsync(CrawlKind.Full);

        Assert.True(result.Stopped);
        // The in-flight chunk (P1,P2) completed before stopping.
        Assert.Equal(2, harness.Graph.PutItems.Count);
        var checkpoint = harness.State.GetCheckpoint();
        Assert.NotNull(checkpoint);
        Assert.Equal("d1", checkpoint!.DeliveryId);
        Assert.Equal(2, checkpoint.RecordIndex);
        Assert.False(harness.State.IsDeliveryProcessed("d1"));  // resumes next run
    }

    [Fact]
    public async Task SeatChangeTriggersReAclPassOverExistingItems()
    {
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P1", "A", null, null)), 1));

        await harness.Engine.RunAsync(CrawlKind.Full);
        Assert.Empty(harness.Graph.AclUpdates);
        var originalHash = harness.State.GetValue(StateKeys.SeatListHash);
        Assert.NotNull(originalHash);

        // Change the seat list, add a new delivery, crawl again.
        harness.WriteSeats("alice@contoso.com", "bob@contoso.com", "carol@contoso.com");
        TestFixtures.WriteDelivery(harness.FeedPath, "d2",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P2", "B", null, null)), 1));

        await harness.Engine.RunAsync(CrawlKind.Incremental);

        // Existing item P1 was re-ACLed with the 3-seat ACL.
        Assert.Single(harness.Graph.AclUpdates);
        Assert.Equal("PersonProfile-P1", harness.Graph.AclUpdates[0].ItemId);
        Assert.Equal(3, harness.Graph.AclUpdates[0].Acl.Count);

        // Hash committed and item registry updated to the new hash.
        var newHash = harness.State.GetValue(StateKeys.SeatListHash);
        Assert.NotEqual(originalHash, newHash);
        Assert.Empty(harness.Identity.ListItemsWithAclHashOtherThan(newHash!));
    }

    [Fact]
    public async Task UnchangedSeatsDoNotTriggerReAcl()
    {
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P1", "A", null, null)), 1));

        await harness.Engine.RunAsync(CrawlKind.Full);
        await harness.Engine.RunAsync(CrawlKind.Full);

        Assert.Empty(harness.Graph.AclUpdates);
    }

    [Fact]
    public async Task EmptySeatListAbortsCrawlBeforeAnyIngestion()
    {
        using var harness = new CrawlHarness();
        harness.WriteSeats();  // zero seats
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P1", "A", null, null)), 1));

        await Assert.ThrowsAsync<EntitlementViolationException>(
            () => harness.Engine.RunAsync(CrawlKind.Full));
        Assert.Empty(harness.Graph.PutItems);
    }

    [Fact]
    public async Task EntityResolutionLinksCrmContactOntoItem()
    {
        var crmDir = TestFixtures.NewTempDir("crm");
        var crmPath = Path.Combine(crmDir, "contacts.json");
        File.WriteAllText(crmPath,
            """[{"id":"C77","email":"ada@x.com","name":"Ada Lovelace","employer":"Acme Ltd"}]""");

        using var harness = new CrawlHarness(crmContactsPath: crmPath);
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile,
                TestFixtures.PersonJson(("P1", "Ada Lovelace", "ada@x.com", "Acme"),
                                        ("P2", "Nobody Known", null, null)), 2));

        await harness.Engine.RunAsync(CrawlKind.Full);

        var linked = harness.Graph.PutItems.Single(i => i.Id == "PersonProfile-P1");
        Assert.Equal("C77", linked.Properties["crmContactId"]);
        Assert.Equal("email", linked.Properties["crmMatchRule"]);

        var unlinked = harness.Graph.PutItems.Single(i => i.Id == "PersonProfile-P2");
        Assert.False(unlinked.Properties.ContainsKey("crmContactId"));

        // Crosswalk persisted.
        var entry = harness.Identity.GetCrosswalk("P1");
        Assert.NotNull(entry);
        Assert.Equal("C77", entry!.CrmContactId);
    }

    [Fact]
    public async Task PiiClassificationIsStampedOnEveryItem()
    {
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("wealth.json", Datasets.WealthIndicator,
                """[{"id":"W1","person_id":"P1","net_worth_usd":"9000000"}]""", 1),
            ("orgs.csv", Datasets.Organization, "org_id,organization_name\nO1,Acme\n", 1));

        await harness.Engine.RunAsync(CrawlKind.Full);

        var wealth = harness.Graph.PutItems.Single(i => i.Id.StartsWith("WealthIndicator-"));
        Assert.Equal("PII-Sensitive-Wealth", wealth.Properties["piiClassification"]);
        var org = harness.Graph.PutItems.Single(i => i.Id.StartsWith("Organization-"));
        Assert.Equal("Non-Personal", org.Properties["piiClassification"]);
    }
}

public class RetentionTests
{
    [Fact]
    public async Task ProcessedDeliveriesArchiveAfterRetentionWindow()
    {
        using var harness = new CrawlHarness(retentionDays: 7, retentionMode: "archive");
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P1", "A", null, null)), 1));

        await harness.Engine.RunAsync(CrawlKind.Full);
        Assert.True(Directory.Exists(Path.Combine(harness.FeedPath, "d1")));

        // Backdate the processing timestamp past the retention window.
        harness.State.SetValue("delivery_processed_d1",
            DateTime.UtcNow.AddDays(-8).ToString("o"));

        var acted = Retention.Apply(harness.Config, harness.State);

        Assert.Contains("d1", acted);
        Assert.False(Directory.Exists(Path.Combine(harness.FeedPath, "d1")));
        Assert.True(Directory.Exists(Path.Combine(harness.FeedPath, "archive", "d1")));
        // State untouched.
        Assert.True(harness.State.IsDeliveryProcessed("d1"));
    }

    [Fact]
    public async Task UnprocessedAndRecentDeliveriesAreLeftAlone()
    {
        using var harness = new CrawlHarness(retentionDays: 7, retentionMode: "delete");
        TestFixtures.WriteDelivery(harness.FeedPath, "recent",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P1", "A", null, null)), 1));
        TestFixtures.WriteDelivery(harness.FeedPath, "unprocessed",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P2", "B", null, null)), 1));
        harness.State.MarkDeliveryProcessed("recent", DateTime.UtcNow);
        harness.State.SetValue("delivery_processed_recent", DateTime.UtcNow.ToString("o"));

        var acted = Retention.Apply(harness.Config, harness.State);

        Assert.Empty(acted);
        Assert.True(Directory.Exists(Path.Combine(harness.FeedPath, "recent")));
        Assert.True(Directory.Exists(Path.Combine(harness.FeedPath, "unprocessed")));
        await Task.CompletedTask;
    }
}
