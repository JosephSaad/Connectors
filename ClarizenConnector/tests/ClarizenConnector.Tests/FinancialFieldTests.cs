using ClarizenConnector.Config;
using ClarizenConnector.Graph;
using ClarizenConnector.Item;

namespace ClarizenConnector.Tests;

public class FinancialFieldTests
{
    private static ObjectConfig ProjectConfig() => new()
    {
        ObjectName = "Project",
        SelectedFields = new Dictionary<string, string>
        {
            ["Name"] = "Title",
            ["PlannedCost"] = "PlannedCost",
            ["ActualCost"] = "ActualCost",
            ["Description"] = "_cz_Description",
        },
        FinancialFields = new List<string> { "PlannedCost", "ActualCost" },
    };

    private static ExternalItem Item(bool withFinancials = true)
    {
        var item = new ExternalItem
        {
            Id = "Project_1",
            Acl =
            {
                new AclEntry(AclEntryType.User, "user-a", AclAccessType.Grant),
                new AclEntry(AclEntryType.Group, "group-b", AclAccessType.Grant),
                new AclEntry(AclEntryType.User, "user-c", AclAccessType.Deny),
            },
        };
        item.Properties["Title"] = "Apollo";
        if (withFinancials)
        {
            item.Properties["PlannedCost"] = 120000.0;
            item.Properties["ActualCost"] = 90000.0;
        }
        return item;
    }

    [Fact]
    public void FinancialPropertyNames_MapsFieldsToGraphProperties()
    {
        var names = FinancialFieldClassifier.FinancialPropertyNames(ProjectConfig());
        Assert.Equal(new[] { "ActualCost", "PlannedCost" }, names.OrderBy(n => n).ToArray());
    }

    [Fact]
    public void NoFinancialData_NothingApplied()
    {
        var item = Item(withFinancials: false);
        var applied = FinancialFieldClassifier.Apply(item, ProjectConfig(), TestConfig.Make(financialMode: "acl", financialGroupId: "fin-group"));
        Assert.False(applied);
        Assert.False(item.Properties.ContainsKey(FinancialFieldClassifier.ClassificationProperty));
        Assert.Equal(3, item.Acl.Count);  // untouched
    }

    [Fact]
    public void TagMode_AddsClassificationOnly()
    {
        var item = Item();
        var applied = FinancialFieldClassifier.Apply(item, ProjectConfig(), TestConfig.Make(financialMode: "tag"));
        Assert.True(applied);
        Assert.Equal(true, item.Properties[FinancialFieldClassifier.ContainsFinancialProperty]);
        Assert.Equal("financial", item.Properties[FinancialFieldClassifier.ClassificationProperty]);
        Assert.Equal(120000.0, item.Properties["PlannedCost"]);   // values kept
        Assert.Equal(3, item.Acl.Count);                          // ACL untouched
    }

    [Fact]
    public void FilterMode_StripsFinancialValues()
    {
        var item = Item();
        FinancialFieldClassifier.Apply(item, ProjectConfig(), TestConfig.Make(financialMode: "filter"));
        Assert.False(item.Properties.ContainsKey("PlannedCost"));
        Assert.False(item.Properties.ContainsKey("ActualCost"));
        Assert.Equal("Apollo", item.Properties["Title"]);         // non-financial kept
        Assert.Equal("financial", item.Properties[FinancialFieldClassifier.ClassificationProperty]);
        Assert.Equal(3, item.Acl.Count);                          // ACL untouched
    }

    [Fact]
    public void AclMode_RestrictsGrantsToFinanceGroup_PreservingDenies()
    {
        var item = Item();
        FinancialFieldClassifier.Apply(
            item, ProjectConfig(), TestConfig.Make(financialMode: "acl", financialGroupId: "fin-group"));

        Assert.Equal(120000.0, item.Properties["PlannedCost"]);   // values kept
        var grants = item.Acl.Where(e => e.AccessType == AclAccessType.Grant).ToList();
        var denies = item.Acl.Where(e => e.AccessType == AclAccessType.Deny).ToList();
        var grant = Assert.Single(grants);
        Assert.Equal(AclEntryType.Group, grant.Type);
        Assert.Equal("fin-group", grant.Value);
        var deny = Assert.Single(denies);
        Assert.Equal("user-c", deny.Value);
    }

    [Fact]
    public void CzPlaceholderFinancialFields_AreIgnoredForPropertyStripping()
    {
        var config = new ObjectConfig
        {
            ObjectName = "X",
            SelectedFields = new Dictionary<string, string>
            {
                ["Name"] = "Title",
                ["SecretCost"] = "_cz_SecretCost",
            },
            FinancialFields = new List<string> { "SecretCost" },
        };
        // _cz_ fields never become Graph properties, so nothing to classify.
        var item = new ExternalItem { Id = "X_1" };
        item.Properties["Title"] = "t";
        Assert.False(FinancialFieldClassifier.Apply(item, config, TestConfig.Make(financialMode: "filter")));
    }

    [Fact]
    public void AppConfig_RejectsAclModeWithoutGroup()
    {
        using var scope = new EnvScope(
            ("CONNECTOR_ID", "ClarizenTest"),
            ("CONNECTOR_NAME", "t"),
            ("CONNECTOR_DESCRIPTION", "t"),
            ("CLARIZEN_USERNAME", "u"),
            ("SECRET_CLARIZEN_PASSWORD", "p"),
            ("AAD_APP_TENANT_ID", "t"),
            ("AAD_APP_CLIENT_ID", "c"),
            ("SECRET_AAD_APP_CLIENT_SECRET", "s"),
            ("FINANCIAL_DATA_MODE", "acl"),
            ("FINANCIAL_DATA_GROUP_ID", null),
            ("USE_KEY_VAULT", null));
        var exc = Assert.Throws<ArgumentException>(() => AppConfig.Load());
        Assert.Contains("FINANCIAL_DATA_GROUP_ID", exc.Message);
    }
}
