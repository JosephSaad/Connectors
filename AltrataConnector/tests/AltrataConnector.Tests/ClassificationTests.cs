// Improvement round 6: unified data classification & sensitivity labeling.
// Label derivation per dataset (folding PII in), path-summary inherits the
// person's label, residency tag, field classifier categories WITHOUT leaking
// values, manifest contains no personal data, properties only when enabled,
// on/off toggle, seat invariant untouched, no-network.

using AltrataConnector.Altrata;
using AltrataConnector.Config;
using AltrataConnector.Entitlement;
using AltrataConnector.Graph;
using AltrataConnector.Identity;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

// ---- taxonomy derivation --------------------------------------------------------

public class SensitivityTaxonomyTests
{
    private static FeedRecord Rec(string dataset, params (string K, string? V)[] fields) => new()
    {
        Dataset = dataset,
        Fields = fields.ToDictionary(f => f.K, f => f.V, StringComparer.OrdinalIgnoreCase),
    };

    [Theory]
    [InlineData(Datasets.PersonProfile, "Restricted")]
    [InlineData(Datasets.WealthIndicator, "Restricted")]
    [InlineData(Datasets.RelationshipPath, "Restricted")]
    [InlineData(Datasets.CareerHistory, "Restricted")]
    [InlineData(Datasets.BoardMembership, "Confidential")]
    [InlineData(Datasets.Organization, "Confidential")]
    public void DatasetBaselinesMatchAltrataSensitivity(string dataset, string expected)
    {
        Assert.Equal(expected, SensitivityClassifier.LabelName(SensitivityClassifier.DatasetBaseline(dataset)));
    }

    [Fact]
    public void PiiLevelFoldsInAsAFloor()
    {
        Assert.Equal(SensitivityLabel.Restricted, SensitivityClassifier.FromPiiLevel(PiiLevel.Sensitive));
        Assert.Equal(SensitivityLabel.Confidential, SensitivityClassifier.FromPiiLevel(PiiLevel.Personal));
        Assert.Equal(SensitivityLabel.Internal, SensitivityClassifier.FromPiiLevel(PiiLevel.None));
    }

    [Fact]
    public void OrganizationWithAWealthFigureEscalatesToRestricted()
    {
        // Org baseline is Confidential, but a wealth field (Sensitive PII) floors to Restricted.
        var org = Rec(Datasets.Organization, ("org_id", "O1"), ("net_worth_usd", "9000000"));
        Assert.Equal("Restricted", SensitivityClassifier.ClassifyLabel(org));
    }

    [Fact]
    public void OrdinaryBoardDataStaysConfidential()
    {
        var board = Rec(Datasets.BoardMembership, ("id", "B1"), ("person_id", "P1"), ("org_name", "Acme"));
        Assert.Equal("Confidential", SensitivityClassifier.ClassifyLabel(board));
    }

    [Fact]
    public void PersonProfileIsAlwaysRestricted()
    {
        var person = Rec(Datasets.PersonProfile, ("id", "P1"), ("person_name", "Ada"));
        Assert.Equal("Restricted", SensitivityClassifier.ClassifyLabel(person));
    }

    [Fact]
    public void UnknownDatasetBaselineIsInternalButFoldsUpWithThePiiFloor()
    {
        // The taxonomy baseline for an unknown dataset is Internal...
        Assert.Equal(SensitivityLabel.Internal, SensitivityClassifier.DatasetBaseline("SomethingElse"));
        // ...but the existing PiiClassifier treats an unknown dataset as Personal
        // (conservative), so the folded label is Confidential — proving the PII
        // classifier is genuinely one input to the unified label.
        var rec = new FeedRecord { Dataset = "SomethingElse", Fields = new Dictionary<string, string?> { ["x"] = "1" } };
        Assert.Equal("Confidential", SensitivityClassifier.ClassifyLabel(rec));
    }
}

// ---- field classifier -----------------------------------------------------------

public class FieldClassifierTests
{
    private static FeedRecord Rec(string dataset, params (string K, string? V)[] fields) => new()
    {
        Dataset = dataset,
        Fields = fields.ToDictionary(f => f.K, f => f.V, StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public void DetectsCategoriesFromFieldNamesAndValueShapes()
    {
        var rec = Rec(Datasets.PersonProfile,
            ("person_name", "Ada Lovelace"),
            ("email", "ada@contoso.com"),
            ("ssn", "123-45-6789"),
            ("net_worth_usd", "9000000"));
        var cats = SensitivityClassifier.DetectCategories(rec);
        Assert.Contains(DataCategories.Name, cats);
        Assert.Contains(DataCategories.Email, cats);
        Assert.Contains(DataCategories.NationalId, cats);
        Assert.Contains(DataCategories.WealthFigure, cats);
    }

    [Fact]
    public void DetectsEmailAndSsnByValueShapeEvenWithGenericFieldNames()
    {
        var rec = Rec(Datasets.PersonProfile,
            ("contact", "ada@x.co"),        // value looks like an email
            ("ref", "987654321"));          // 9-digit → national-id shape
        var cats = SensitivityClassifier.DetectCategories(rec);
        Assert.Contains(DataCategories.Email, cats);
        Assert.Contains(DataCategories.NationalId, cats);
    }

    [Fact]
    public void EmptyValuesAndBenignFieldsDetectNothing()
    {
        var rec = Rec(Datasets.Organization, ("org_name", "Acme"), ("country", "UK"), ("blank", ""));
        Assert.Empty(SensitivityClassifier.DetectCategories(rec));
    }

    [Fact]
    public void CategoriesAreLabelsOnly_NeverTheRawValues()
    {
        var rec = Rec(Datasets.PersonProfile,
            ("person_name", "Ada Lovelace"), ("email", "ada@contoso.com"),
            ("ssn", "123-45-6789"), ("net_worth_usd", "9000000"));
        var cats = SensitivityClassifier.DetectCategories(rec);
        // The detected set is the fixed label vocabulary — no personal values.
        foreach (var c in cats)
            Assert.Contains(c, new[]
            {
                DataCategories.Name, DataCategories.Email, DataCategories.NationalId, DataCategories.WealthFigure,
            });
        var blob = string.Join("|", cats);
        Assert.DoesNotContain("Ada", blob);
        Assert.DoesNotContain("ada@contoso.com", blob);
        Assert.DoesNotContain("123-45-6789", blob);
        Assert.DoesNotContain("9000000", blob);
    }
}

// ---- transformer property stamping ----------------------------------------------

public class ClassificationTransformerTests
{
    private static readonly IReadOnlyList<AclEntry> SeatAcl =
        new[] { new AclEntry { Type = "user", Value = "alice@contoso.com" } };

    private static FeedRecord Person(string id) => new()
    {
        Dataset = Datasets.PersonProfile,
        Fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = id, ["person_name"] = "Ada Lovelace", ["email"] = "ada@x.com", ["net_worth_usd"] = "9000000",
        },
    };

    [Fact]
    public void DisabledByDefaultAddsNoClassificationProperties()
    {
        var item = new ItemTransformer().Transform(Person("P1"), SeatAcl);
        Assert.False(item.Properties.ContainsKey(ItemTransformer.AdvisorySensitivityProp));
        Assert.False(item.Properties.ContainsKey(ItemTransformer.DetectedCategoriesProp));
        Assert.False(item.Properties.ContainsKey(ItemTransformer.DataResidencyProp));
    }

    [Fact]
    public void EnabledStampsLabelCategoriesAndResidency()
    {
        ClassificationStats.ResetForTests();
        var transformer = new ItemTransformer(
            classification: new ClassificationOptions { Enabled = true, Residency = "US" });
        var item = transformer.Transform(Person("P1"), SeatAcl);

        Assert.Equal("Restricted", item.Properties[ItemTransformer.AdvisorySensitivityProp]);
        Assert.Equal("US", item.Properties[ItemTransformer.DataResidencyProp]);
        var cats = Assert.IsType<List<string>>(item.Properties[ItemTransformer.DetectedCategoriesProp]);
        Assert.Contains(DataCategories.Email, cats);
        Assert.Contains(DataCategories.WealthFigure, cats);

        // Metrics recorded by label + category.
        Assert.Equal(1, ClassificationStats.ItemsForLabel("Restricted"));
        Assert.True(ClassificationStats.DetectionsForCategory(DataCategories.Email) >= 1);
        ClassificationStats.ResetForTests();
    }

    [Fact]
    public void ResidencyOmittedWhenUnset()
    {
        var transformer = new ItemTransformer(classification: new ClassificationOptions { Enabled = true });
        var item = transformer.Transform(Person("P1"), SeatAcl);
        Assert.True(item.Properties.ContainsKey(ItemTransformer.AdvisorySensitivityProp));
        Assert.False(item.Properties.ContainsKey(ItemTransformer.DataResidencyProp));
    }

    [Fact]
    public void ClassificationKeepsTheAclSeatOnly()
    {
        var transformer = new ItemTransformer(
            classification: new ClassificationOptions { Enabled = true, Residency = "EU" });
        var item = transformer.Transform(Person("P1"), SeatAcl);
        Assert.Same(SeatAcl, item.Acl);
        Assert.DoesNotContain(item.Acl, e => e.Type is "everyone" or "everyoneExceptGuests");
    }

    [Fact]
    public void EveryoneAclIsRejectedEvenWithClassificationOn()
    {
        var transformer = new ItemTransformer(classification: new ClassificationOptions { Enabled = true });
        var everyone = new[] { new AclEntry { Type = "everyone", Value = "all" } };
        Assert.Throws<EntitlementViolationException>(() => transformer.Transform(Person("P1"), everyone));
    }

    [Fact]
    public void PathSummaryItemInheritsThePersonRestrictedLabel()
    {
        // A PersonProfile item carrying materialized path summaries is still a
        // PersonProfile → Restricted (the summaries inherit that label).
        var index = RelationshipPathIndex.Build(
            new[] { new PathEdge("P1", "P2", 1, 0) }, new[] { new PersonOrg("P1", "Acme") });
        var transformer = new ItemTransformer(pathIndex: index,
            classification: new ClassificationOptions { Enabled = true });
        var item = transformer.Transform(Person("P1"), SeatAcl);

        Assert.Equal("Restricted", item.Properties[ItemTransformer.AdvisorySensitivityProp]);
        Assert.True(item.Properties.ContainsKey("firstDegreeCount"));  // path summary present
    }
}

// ---- config knobs ---------------------------------------------------------------

public class ClassificationConfigTests : IDisposable
{
    public ClassificationConfigTests()
    {
        foreach (var (k, v) in new[]
                 {
                     ("CONNECTOR_ID", "AltrataClsTest"), ("CONNECTOR_NAME", "t"),
                     ("CONNECTOR_DESCRIPTION", "t"), ("AAD_APP_CLIENT_ID", "c"),
                     ("AAD_APP_TENANT_ID", "t"), ("SECRET_AAD_APP_CLIENT_SECRET", "s"),
                 })
            Environment.SetEnvironmentVariable(k, v);
    }

    public void Dispose()
    {
        foreach (var k in new[]
                 {
                     "CONNECTOR_ID", "CONNECTOR_NAME", "CONNECTOR_DESCRIPTION", "AAD_APP_CLIENT_ID",
                     "AAD_APP_TENANT_ID", "SECRET_AAD_APP_CLIENT_SECRET",
                     "CLASSIFICATION", "DATA_RESIDENCY", "CLASSIFICATION_MANIFEST",
                 })
            Environment.SetEnvironmentVariable(k, null);
    }

    [Fact]
    public void DefaultsAreOff()
    {
        var config = AppConfig.Load();
        Assert.False(config.Classification);
        Assert.Null(config.DataResidency);
        Assert.False(config.ClassificationManifest);
    }

    [Fact]
    public void KnobsAreRead()
    {
        Environment.SetEnvironmentVariable("CLASSIFICATION", "true");
        Environment.SetEnvironmentVariable("DATA_RESIDENCY", "EU");
        Environment.SetEnvironmentVariable("CLASSIFICATION_MANIFEST", "true");
        var config = AppConfig.Load();
        Assert.True(config.Classification);
        Assert.Equal("EU", config.DataResidency);
        Assert.True(config.ClassificationManifest);
    }
}

// ---- manifest -------------------------------------------------------------------

public class ClassificationManifestTests
{
    [Fact]
    public void ManifestHasPerItemAndSummaryLinesWithCountsOnly()
    {
        var logs = TestFixtures.NewTempDir("cls_manifest");
        var entries = new[]
        {
            new ClassificationEntry("PersonProfile-P1", "Restricted", new[] { "Email", "Name" }),
            new ClassificationEntry("Organization-O1", "Confidential", Array.Empty<string>()),
            new ClassificationEntry("PersonProfile-P2", "Restricted", new[] { "WealthFigure" }),
        };
        var path = ClassificationManifest.Write("AltrataTest", "d1", entries, logs);

        var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        Assert.Equal(4, lines.Length);  // 3 items + 1 summary
        var summary = lines[^1];
        Assert.Contains("\"type\":\"summary\"", summary);
        Assert.Contains("\"Restricted\":2", summary);
        Assert.Contains("\"Confidential\":1", summary);
        Assert.Contains("\"totalItems\":3", summary);
    }

    [Fact]
    public void ManifestContainsNoPersonalData()
    {
        var logs = TestFixtures.NewTempDir("cls_noleak");
        // Even though the classifier saw personal values, the manifest carries
        // only ids, labels, category names and counts.
        var entries = new[]
        {
            new ClassificationEntry("PersonProfile-P1", "Restricted",
                new[] { "Name", "Email", "NationalId", "WealthFigure" }),
        };
        var path = ClassificationManifest.Write("AltrataTest", "d1", entries, logs);
        var text = File.ReadAllText(path);

        foreach (var secret in new[] { "Ada Lovelace", "ada@contoso.com", "123-45-6789", "9000000" })
            Assert.DoesNotContain(secret, text);
        // Category labels ARE present (they are metadata, not values).
        Assert.Contains("Email", text);
        Assert.Contains("Restricted", text);
    }
}

// ---- end-to-end crawl -----------------------------------------------------------

public class ClassificationCrawlTests
{
    private const string PiiPersons = """
        [{"id":"P1","person_name":"Ada Lovelace","email":"ada@contoso.com","net_worth_usd":"9000000","ssn":"123-45-6789"}]
        """;

    [Fact]
    public async Task DisabledByDefaultLeavesItemsUnchanged()
    {
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, PiiPersons, 1));

        await harness.Engine.RunAsync(CrawlKind.Full);

        var item = Assert.Single(harness.Graph.PutItems);
        Assert.False(item.Properties.ContainsKey(ItemTransformer.AdvisorySensitivityProp));
    }

    [Fact]
    public async Task EnabledStampsSensitivityAndResidencyOnItems()
    {
        using var harness = new CrawlHarness(configure: c => c with
        {
            Classification = true,
            DataResidency = "US",
        });
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, PiiPersons, 1),
            ("orgs.csv", Datasets.Organization, "org_id,organization_name\nO1,Acme\n", 1));

        await harness.Engine.RunAsync(CrawlKind.Full);

        var person = harness.Graph.PutItems.Single(i => i.Id == "PersonProfile-P1");
        Assert.Equal("Restricted", person.Properties[ItemTransformer.AdvisorySensitivityProp]);
        Assert.Equal("US", person.Properties[ItemTransformer.DataResidencyProp]);
        var org = harness.Graph.PutItems.Single(i => i.Id == "Organization-O1");
        Assert.Equal("Confidential", org.Properties[ItemTransformer.AdvisorySensitivityProp]);

        // Seat invariant intact.
        Assert.All(harness.Graph.PutItems, item =>
            Assert.DoesNotContain(item.Acl, e => e.Type is "everyone" or "everyoneExceptGuests"));
    }

    [Fact]
    public async Task ManifestIsWrittenWhenEnabledAndLeaksNoPersonalData()
    {
        using var harness = new CrawlHarness(configure: c => c with
        {
            Classification = true,
            ClassificationManifest = true,
            DataResidency = "EU",
        });
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, PiiPersons, 1));

        await harness.Engine.RunAsync(CrawlKind.Full);

        var manifestPath = ClassificationManifest.ManifestPath(harness.Config.ConnectorId, "d1",
            Path.Combine(harness.Root, "logs"));
        Assert.True(File.Exists(manifestPath));
        var text = File.ReadAllText(manifestPath);

        // Item id → label present; NO personal values anywhere.
        Assert.Contains("PersonProfile-P1", text);
        Assert.Contains("Restricted", text);
        foreach (var secret in new[] { "Ada Lovelace", "ada@contoso.com", "123-45-6789", "9000000" })
            Assert.DoesNotContain(secret, text);
    }

    [Fact]
    public async Task ManifestNotWrittenWhenDisabled()
    {
        using var harness = new CrawlHarness(configure: c => c with { Classification = true });  // manifest OFF
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, PiiPersons, 1));

        await harness.Engine.RunAsync(CrawlKind.Full);

        var manifestPath = ClassificationManifest.ManifestPath(harness.Config.ConnectorId, "d1",
            Path.Combine(harness.Root, "logs"));
        Assert.False(File.Exists(manifestPath));
    }
}
