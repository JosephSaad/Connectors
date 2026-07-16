// Sensitivity label derivation (precedence), financial fold-in, property
// stamping, metrics, and the manifest shape.

using ClarizenConnector.Config;
using ClarizenConnector.Content;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;
using ClarizenConnector.Item;

namespace ClarizenConnector.Tests;

public class SensitivityLabelDerivationTests
{
    private static IReadOnlySet<string> Cats(params string[] c) =>
        new HashSet<string>(c, StringComparer.Ordinal);

    [Fact]
    public void Default_WhenNothingDetected_IsPerObjectDefault()
    {
        Assert.Equal(SensitivityLabel.Internal,
            SensitivityClassifier.DeriveLabel(null, Cats(), isFinancial: false));
        Assert.Equal(SensitivityLabel.Public,
            SensitivityClassifier.DeriveLabel("Public", Cats(), isFinancial: false));
        Assert.Equal(SensitivityLabel.Confidential,
            SensitivityClassifier.DeriveLabel("Confidential", Cats(), isFinancial: false));
    }

    [Fact]
    public void Financial_FloorsAtConfidential()
    {
        Assert.Equal(SensitivityLabel.Confidential,
            SensitivityClassifier.DeriveLabel("Public", Cats(), isFinancial: true));
        Assert.Equal(SensitivityLabel.Confidential,
            SensitivityClassifier.DeriveLabel(null, Cats(), isFinancial: true));
        // A higher default is not lowered by financial.
        Assert.Equal(SensitivityLabel.Restricted,
            SensitivityClassifier.DeriveLabel("Restricted", Cats(), isFinancial: true));
    }

    [Fact]
    public void PiiPciSecret_ForceRestricted_BeatingFinancialAndDefault()
    {
        Assert.Equal(SensitivityLabel.Restricted,
            SensitivityClassifier.DeriveLabel("Public", Cats("PII"), isFinancial: false));
        Assert.Equal(SensitivityLabel.Restricted,
            SensitivityClassifier.DeriveLabel("Internal", Cats("PCI"), isFinancial: true));
        Assert.Equal(SensitivityLabel.Restricted,
            SensitivityClassifier.DeriveLabel("Confidential", Cats("Secret"), isFinancial: false));
    }

    [Fact]
    public void FinancialCategory_AloneDoesNotRestrict()
    {
        // "Financial" is present in categories but is NOT a restricted category.
        Assert.Equal(SensitivityLabel.Confidential,
            SensitivityClassifier.DeriveLabel("Internal", Cats("Financial"), isFinancial: true));
    }

    [Fact]
    public void ParseLabel_CaseInsensitive_AndUnknownNull()
    {
        Assert.Equal(SensitivityLabel.Restricted, SensitivityClassifier.ParseLabel("restricted"));
        Assert.Equal(SensitivityLabel.Public, SensitivityClassifier.ParseLabel("PUBLIC"));
        Assert.Null(SensitivityClassifier.ParseLabel("Nope"));
        Assert.Null(SensitivityClassifier.ParseLabel(""));
        Assert.Null(SensitivityClassifier.ParseLabel(null));
    }
}

public class SensitivityClassifierApplyTests
{
    private static ContentClassifier RepoContent() =>
        ContentClassifier.Load(Path.Combine(AppContext.BaseDirectory, "config", "classification.json"));

    private static ObjectConfig Object(string sensitivityDefault = "") => new()
    {
        ObjectName = "Task",
        SensitivityDefault = sensitivityDefault,
        SelectedFields = new Dictionary<string, string> { ["Name"] = "Title" },
    };

    private static ExternalItem Item(string content, bool financial = false)
    {
        var item = new ExternalItem { Id = "Task_1" };
        item.Content = content;
        item.Properties["Title"] = "A task";
        if (financial)
            item.Properties[FinancialFieldClassifier.ContainsFinancialProperty] = true;
        return item;
    }

    [Fact]
    public void Apply_StampsLabelAndCategories()
    {
        Metrics.ResetForTests();
        var classifier = new SensitivityClassifier(RepoContent());
        var item = Item("reach me at a@b.com");
        var outcome = classifier.Classify(item, Object());

        Assert.Equal(SensitivityLabel.Restricted, outcome.Label);
        Assert.Equal("Restricted", item.Properties[SensitivityClassifier.LabelProperty]);
        var cats = Assert.IsType<string[]>(item.Properties[SensitivityClassifier.CategoriesProperty]);
        Assert.Contains("PII", cats);
        Assert.Equal(1, Metrics.ItemsClassifiedFor("Restricted"));
        Assert.Equal(1, Metrics.SensitiveDetectionsFor("PII"));
        Metrics.ResetForTests();
    }

    [Fact]
    public void Apply_CleanItem_GetsDefaultLabel_EmptyCategories()
    {
        Metrics.ResetForTests();
        var classifier = new SensitivityClassifier(RepoContent());
        var item = Item("Migrate the tenant and verify.");
        var outcome = classifier.Classify(item, Object("Public"));

        Assert.Equal(SensitivityLabel.Public, outcome.Label);
        Assert.Empty(Assert.IsType<string[]>(item.Properties[SensitivityClassifier.CategoriesProperty]));
        Assert.Equal(1, Metrics.ItemsClassifiedFor("Public"));
        Metrics.ResetForTests();
    }

    [Fact]
    public void Apply_FinancialFoldIn_AddsFinancialCategory_Confidential()
    {
        var classifier = new SensitivityClassifier(RepoContent());
        var item = Item("Quarterly figures", financial: true);
        var outcome = classifier.Classify(item, Object());

        Assert.Equal(SensitivityLabel.Confidential, outcome.Label);
        Assert.Contains("Financial", Assert.IsType<string[]>(item.Properties[SensitivityClassifier.CategoriesProperty]));
    }

    [Fact]
    public void Apply_FinancialPlusPii_IsRestricted_WithBothCategories()
    {
        var classifier = new SensitivityClassifier(RepoContent());
        var item = Item("owner a@b.com", financial: true);
        var outcome = classifier.Classify(item, Object());

        Assert.Equal(SensitivityLabel.Restricted, outcome.Label);
        var cats = Assert.IsType<string[]>(item.Properties[SensitivityClassifier.CategoriesProperty]);
        Assert.Contains("Financial", cats);
        Assert.Contains("PII", cats);
    }

    [Fact]
    public void ScanText_IncludesPropertiesNotTaxonomyProps()
    {
        var item = new ExternalItem { Id = "X" };
        item.Content = "body";
        item.Properties["Title"] = "secret a@b.com";
        item.Properties["Tags"] = new[] { "x", "y" };
        item.Properties[SensitivityClassifier.LabelProperty] = "ShouldBeIgnored";
        var text = SensitivityClassifier.ScanText(item);
        Assert.Contains("a@b.com", text);
        Assert.Contains("body", text);
        Assert.Contains("x", text);
        Assert.DoesNotContain("ShouldBeIgnored", text);
    }
}

public class ClassificationManifestTests
{
    [Fact]
    public void Manifest_WritesItemLinesAndSummary()
    {
        using var dir = new TempDir();
        var manifest = new ClassificationManifest("Conn", dir.Path, new DateTime(2026, 7, 13, 10, 0, 0, DateTimeKind.Utc));
        manifest.Record("Task_1", "Task", SensitivityLabel.Restricted, new[] { "PII" });
        manifest.Record("Task_2", "Task", SensitivityLabel.Confidential, new[] { "Financial" });
        manifest.Record("Task_3", "Task", SensitivityLabel.Internal, Array.Empty<string>());
        manifest.Flush();

        var lines = File.ReadAllLines(manifest.Path).Where(l => l.Trim().Length > 0).ToList();
        Assert.Equal(4, lines.Count);  // 3 items + summary

        var first = System.Text.Json.Nodes.JsonNode.Parse(lines[0])!;
        Assert.Equal("Task_1", first["item_id"]!.GetValue<string>());
        Assert.Equal("Restricted", first["sensitivity_label"]!.GetValue<string>());
        Assert.Equal("PII", first["categories"]!.AsArray()[0]!.GetValue<string>());

        var summary = System.Text.Json.Nodes.JsonNode.Parse(lines[^1])!;
        Assert.Equal("summary", summary["kind"]!.GetValue<string>());
        Assert.Equal(3, summary["total"]!.GetValue<int>());
        Assert.Equal(1, summary["counts"]!["Restricted"]!.GetValue<int>());
        Assert.Equal(1, summary["counts"]!["Confidential"]!.GetValue<int>());
        Assert.Equal(1, summary["counts"]!["Internal"]!.GetValue<int>());
    }

    [Fact]
    public void Manifest_PathIsPerConnectorTimestamped()
    {
        using var dir = new TempDir();
        var manifest = new ClassificationManifest("MyConn", dir.Path);
        Assert.Contains("classification_MyConn_", manifest.Path);
        Assert.EndsWith(".jsonl", manifest.Path);
    }
}
