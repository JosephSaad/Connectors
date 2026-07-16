using AltrataConnector.Altrata;

namespace AltrataConnector.Tests;

public class FeedReaderTests
{
    [Fact]
    public void ParsesJsonArray()
    {
        var records = FeedReader.ParseJson(
            """[{"id":"P1","person_name":"Ada Lovelace","net_worth_usd":1000000},{"id":"P2","person_name":null}]""",
            Datasets.PersonProfile);
        Assert.Equal(2, records.Count);
        Assert.Equal("P1", records[0].Id);
        Assert.Equal("Ada Lovelace", records[0].Get("person_name"));
        Assert.Equal("1000000", records[0].Get("net_worth_usd"));  // numbers become raw text
        Assert.Null(records[1].Get("person_name"));
    }

    [Fact]
    public void ParsesCsvWithQuotedFields()
    {
        var lines = new[]
        {
            "id,person_name,employer",
            "P1,\"Lovelace, Ada\",\"Analytical Engines \"\"R\"\" Us\"",
            "P2,Charles Babbage,",
        };
        var records = FeedReader.ParseCsv(lines, Datasets.PersonProfile);
        Assert.Equal(2, records.Count);
        Assert.Equal("Lovelace, Ada", records[0].Get("person_name"));
        Assert.Equal("Analytical Engines \"R\" Us", records[0].Get("employer"));
        Assert.Null(records[1].Get("employer"));
    }

    [Fact]
    public void RecordIdFallsBackThroughKnownFields()
    {
        var record = new FeedRecord
        {
            Dataset = Datasets.Organization,
            Fields = new Dictionary<string, string?> { ["org_id"] = "O9" },
        };
        Assert.Equal("O9", record.Id);
    }

    [Fact]
    public void UnknownDatasetIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Datasets.Canonical("Bogus"));
        Assert.Equal("WealthIndicator", Datasets.Canonical("wealthindicator"));
    }

    [Fact]
    public void DiscoverDeliveriesSkipsArchiveAndSorts()
    {
        var feed = TestFixtures.NewTempDir("feed");
        TestFixtures.WriteDelivery(feed, "2026-07-02_incr",
            ("p.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P1", "A", null, null)), 1));
        TestFixtures.WriteDelivery(feed, "2026-07-01_full",
            ("p.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P1", "A", null, null)), 1));
        Directory.CreateDirectory(Path.Combine(feed, "archive", "old"));
        Directory.CreateDirectory(Path.Combine(feed, "no-manifest-here"));

        var deliveries = FeedReader.DiscoverDeliveries(feed);
        Assert.Equal(2, deliveries.Count);
        Assert.Equal("2026-07-01_full", deliveries[0].Id);
        Assert.Equal("2026-07-02_incr", deliveries[1].Id);
    }
}

public class ManifestChecksumTests
{
    [Fact]
    public void ValidChecksumsPass()
    {
        var feed = TestFixtures.NewTempDir("sum_ok");
        var delivery = TestFixtures.WriteDelivery(feed, "d1",
            ("p.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P1", "A", null, null)), 1));
        FeedReader.ValidateChecksums(delivery);  // must not throw
    }

    [Fact]
    public void TamperedFileFailsChecksum()
    {
        var feed = TestFixtures.NewTempDir("sum_bad");
        var delivery = TestFixtures.WriteDelivery(feed, "d1",
            ("p.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P1", "A", null, null)), 1));
        File.AppendAllText(Path.Combine(delivery.Directory, "p.json"), " ");

        var exc = Assert.Throws<ChecksumMismatchException>(() => FeedReader.ValidateChecksums(delivery));
        Assert.Equal("p.json", exc.FileName);
    }

    [Fact]
    public void MissingFileFails()
    {
        var feed = TestFixtures.NewTempDir("sum_missing");
        var delivery = TestFixtures.WriteDelivery(feed, "d1",
            ("p.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P1", "A", null, null)), 1));
        File.Delete(Path.Combine(delivery.Directory, "p.json"));

        Assert.Throws<FileNotFoundException>(() => FeedReader.ValidateChecksums(delivery));
    }
}

public class ReconciliationTests
{
    [Fact]
    public void ReconciledWhenIngestedPlusDeadLetteredEqualsManifest()
    {
        var file = new FileReconciliation
        {
            File = "p.json", Dataset = "PersonProfile",
            ManifestCount = 10, ParsedCount = 10, Ingested = 8, DeadLettered = 2,
        };
        Assert.True(file.Reconciled);
        Assert.Equal(0, file.Delta);
    }

    [Fact]
    public void MismatchWhenCountsDisagree()
    {
        var file = new FileReconciliation
        {
            File = "p.json", Dataset = "PersonProfile",
            ManifestCount = 10, ParsedCount = 9, Ingested = 8, DeadLettered = 1,
        };
        Assert.False(file.Reconciled);
        Assert.Equal(-1, file.Delta);
    }

    [Fact]
    public void SummaryAggregatesAndSetsStatus()
    {
        var files = new[]
        {
            new FileReconciliation { File = "a", Dataset = "PersonProfile", ManifestCount = 5, ParsedCount = 5, Ingested = 5, DeadLettered = 0 },
            new FileReconciliation { File = "b", Dataset = "WealthIndicator", ManifestCount = 3, ParsedCount = 3, Ingested = 2, DeadLettered = 1 },
        };
        var summary = Reconciliation.Summarize("d1", files);
        Assert.Equal(Reconciliation.StatusReconciled, summary.Status);
        Assert.Equal(8, summary.TotalManifestRecords);
        Assert.Equal(7, summary.TotalIngested);
        Assert.Equal(1, summary.TotalDeadLettered);
    }

    [Fact]
    public void SummaryMismatchWhenAnyFileOff()
    {
        var files = new[]
        {
            new FileReconciliation { File = "a", Dataset = "PersonProfile", ManifestCount = 5, ParsedCount = 5, Ingested = 3, DeadLettered = 1 },
        };
        Assert.Equal(Reconciliation.StatusMismatch, Reconciliation.Summarize("d1", files).Status);
    }

    [Fact]
    public void RejectedWhenErrorPresent()
    {
        var summary = Reconciliation.Summarize("d1", Array.Empty<FileReconciliation>(), "checksum mismatch");
        Assert.Equal(Reconciliation.StatusRejected, summary.Status);
    }

    [Fact]
    public void ReportWritesJsonlWithSummaryLine()
    {
        var logs = TestFixtures.NewTempDir("recon_logs");
        var files = new[]
        {
            new FileReconciliation { File = "a", Dataset = "PersonProfile", ManifestCount = 1, ParsedCount = 1, Ingested = 1, DeadLettered = 0 },
        };
        var summary = Reconciliation.Summarize("d/1 weird id", files);
        var path = Reconciliation.WriteReport("AltrataTest", summary, logs);

        Assert.True(File.Exists(path));
        var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        Assert.Equal(2, lines.Length);  // 1 file line + 1 summary line
        Assert.Contains("\"type\":\"file\"", lines[0]);
        Assert.Contains("\"type\":\"summary\"", lines[1]);
        Assert.Contains("reconciled", lines[1]);
    }
}

public class PiiClassifierTests
{
    private static FeedRecord Record(string dataset, params (string K, string? V)[] fields) => new()
    {
        Dataset = dataset,
        Fields = fields.ToDictionary(f => f.K, f => f.V, StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public void WealthIndicatorIsAlwaysSensitive()
    {
        Assert.Equal(PiiLevel.Sensitive, PiiClassifier.Classify(Record(Datasets.WealthIndicator, ("id", "1"))));
    }

    [Fact]
    public void OrganizationWithoutPersonalFieldsIsNone()
    {
        var record = Record(Datasets.Organization, ("org_id", "O1"), ("country", "UK"));
        Assert.Equal(PiiLevel.None, PiiClassifier.Classify(record));
        Assert.Equal("Non-Personal", PiiClassifier.ClassifyLabel(record));
    }

    [Fact]
    public void PersonProfileIsPersonalAndEscalatesOnWealthFields()
    {
        var plain = Record(Datasets.PersonProfile, ("id", "P1"), ("person_name", "Ada"));
        Assert.Equal(PiiLevel.Personal, PiiClassifier.Classify(plain));

        var wealthy = Record(Datasets.PersonProfile, ("id", "P1"), ("net_worth_usd", "5000000"));
        Assert.Equal(PiiLevel.Sensitive, PiiClassifier.Classify(wealthy));
        Assert.Equal("PII-Sensitive-Wealth", PiiClassifier.ClassifyLabel(wealthy));
    }

    [Fact]
    public void EmptyValuesDoNotEscalate()
    {
        var record = Record(Datasets.Organization, ("org_name", "Acme"), ("net_worth_usd", null));
        Assert.Equal(PiiLevel.None, PiiClassifier.Classify(record));
    }
}
