using System.Text.Json.Nodes;
using HadoopConnector.Hdfs;
using HadoopConnector.Config;
using HadoopConnector.Graph;
using HadoopConnector.Item;

namespace HadoopConnector.Tests;

public class ItemConverterTests
{
    private static ObjectConfig OpportunityConfig() => new()
    {
        ObjectName = "Opportunity",
        DisplayName = "Opportunity",
        SelectedFields = new Dictionary<string, string>
        {
            ["Name"] = "Title",
            ["Description"] = "_bdh_Description",
            ["StageName"] = "Stage",
            ["Amount"] = "Amount",
            ["Probability"] = "Probability",
        },
        IconUrl = "https://icons.example/opportunity.png",
    };

    private static BdhRecord Record(string? dataAsOf = "2026-07-15") => new(
        "Opportunity",
        new JsonObject
        {
            ["Id"] = "0065e00000abcde",
            ["Name"] = "Migrate tenant",
            ["Description"] = "<p>Move &amp; verify</p>",
            ["StageName"] = "Negotiation",
            ["Amount"] = 1234.5,
            ["Probability"] = 42.5,
        },
        dataAsOf,
        "Opportunity/dt=2026-07-15/part-000.jsonl");

    private static readonly List<AclEntry> DefaultAcl = new()
    {
        new AclEntry(AclEntryType.User, "u1", AclAccessType.Grant),
    };

    [Fact]
    public void Convert_MapsFieldsToGraphProperties()
    {
        var converter = new ItemConverter(TestConfig.Make());
        var item = converter.Convert(Record(), OpportunityConfig(), DefaultAcl);

        Assert.Equal("0065e00000abcde", item.Id);
        Assert.Equal("Migrate tenant", item.Properties["Title"]);
        Assert.Equal("Negotiation", item.Properties["Stage"]);
        Assert.Equal(42.5, item.Properties["Probability"]);
        Assert.Equal("Opportunity", item.Properties["ObjectName"]);
        Assert.Equal("https://icons.example/opportunity.png", item.Properties["IconUrl"]);
        // _bdh_ fields feed the content body, never Graph properties.
        Assert.False(item.Properties.ContainsKey("_bdh_Description"));
        Assert.False(item.Properties.ContainsKey("Description"));
    }

    [Fact]
    public void Convert_StampsFreshnessMarkers()
    {
        var converter = new ItemConverter(TestConfig.Make());
        var item = converter.Convert(Record(), OpportunityConfig(), DefaultAcl);
        Assert.Equal(ItemConverter.SourceSystem, item.Properties["SourceSystem"]);
        Assert.Equal("BDH-Hadoop", item.Properties["SourceSystem"]);
        Assert.Equal("2026-07-15", item.Properties["DataAsOf"]);
    }

    [Fact]
    public void Convert_NoDataAsOf_OmitsTheProperty()
    {
        var converter = new ItemConverter(TestConfig.Make());
        var item = converter.Convert(Record(dataAsOf: null), OpportunityConfig(), DefaultAcl);
        Assert.False(item.Properties.ContainsKey("DataAsOf"));
        Assert.Equal("BDH-Hadoop", item.Properties["SourceSystem"]);  // always present
    }

    [Fact]
    public void Convert_BuildsDeepLinkIntoLiveOrg()
    {
        // Same id space as the live Salesforce org — the deep link opens the
        // real record.
        var converter = new ItemConverter(
            TestConfig.Make(), appBaseUrl: "https://org.lightning.force.com");
        var item = converter.Convert(Record(), OpportunityConfig(), DefaultAcl);
        Assert.Equal(
            "https://org.lightning.force.com/0065e00000abcde",
            item.Properties["Url"]);
    }

    [Fact]
    public void Convert_ContentIncludesTitleAndBdhFields_HtmlStripped()
    {
        var converter = new ItemConverter(TestConfig.Make());
        var item = converter.Convert(Record(), OpportunityConfig(), DefaultAcl);
        Assert.Contains("Opportunity: Migrate tenant", item.Content);
        Assert.Contains("Move & verify", item.Content);
        Assert.DoesNotContain("<p>", item.Content);
        // Freshness note for Copilot grounding.
        Assert.Contains("Data as of: 2026-07-15", item.Content);
    }

    [Fact]
    public void ToPropertyValue_FlattensShapes()
    {
        Assert.Null(ItemConverter.ToPropertyValue(null));
        Assert.Equal("hello", ItemConverter.ToPropertyValue(JsonValue.Create("hello")));
        Assert.Equal(true, ItemConverter.ToPropertyValue(JsonNode.Parse("true")));
        Assert.Equal(5L, ItemConverter.ToPropertyValue(JsonNode.Parse("5")));
        Assert.Equal(2.5, ItemConverter.ToPropertyValue(JsonNode.Parse("2.5")));
        Assert.Equal("RefName",
            ItemConverter.ToPropertyValue(JsonNode.Parse("""{"id": "0015e01", "name": "RefName"}""")));
        Assert.Equal("0015e01",
            ItemConverter.ToPropertyValue(JsonNode.Parse("""{"id": "0015e01"}""")));
        Assert.Equal("EnumVal",
            ItemConverter.ToPropertyValue(JsonNode.Parse("""{"value": "EnumVal"}""")));
        var array = ItemConverter.ToPropertyValue(JsonNode.Parse("""["a", "b"]"""));
        Assert.Equal(new[] { "a", "b" }, Assert.IsType<string[]>(array));
    }

    [Fact]
    public void StripHtml_RemovesTagsAndDecodesEntities()
    {
        Assert.Equal("plain", ItemConverter.StripHtml("plain"));
        Assert.Equal("a  b", ItemConverter.StripHtml("a <b>b</b>").TrimEnd());
        Assert.Contains("x < y", ItemConverter.StripHtml("x &lt; y"));
    }

    [Fact]
    public void ExternalItem_ToJson_Shape()
    {
        var item = new ExternalItem { Id = "0065e00000abcde", Acl = { DefaultAcl[0] } };
        item.Properties["Title"] = "T";
        item.Properties["Count"] = 3L;
        item.Properties["When"] = new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc);
        item.Properties["Tags"] = new[] { "a", "b" };
        item.Properties["Skip"] = null;
        item.Content = "body";

        var json = item.ToJson();
        Assert.Equal("0065e00000abcde", json["id"]!.GetValue<string>());
        Assert.Equal("T", json["properties"]!["Title"]!.GetValue<string>());
        Assert.Equal(3, json["properties"]!["Count"]!.GetValue<long>());
        Assert.StartsWith("2026-07-12", json["properties"]!["When"]!.GetValue<string>());
        Assert.Equal(2, json["properties"]!["Tags"]!.AsArray().Count);
        Assert.Null(json["properties"]!["Skip"]);
        Assert.Equal("body", json["content"]!["value"]!.GetValue<string>());
        Assert.Equal("text", json["content"]!["type"]!.GetValue<string>());
        Assert.Single(json["acl"]!.AsArray());
    }

    [Fact]
    public void BdhRecord_IdHandling()
    {
        // The Graph external item id IS the Salesforce record id, verbatim.
        var record = new BdhRecord("Contact", new JsonObject { ["Id"] = "0035e00000abcde" });
        Assert.Equal("0035e00000abcde", record.RawId);
        Assert.Equal("0035e00000abcde", record.ItemId);

        // BDH exports vary in column casing — the id column is found either way.
        var lower = new BdhRecord("Contact", new JsonObject { ["id"] = "0035e00000fghij" });
        Assert.Equal("0035e00000fghij", lower.ItemId);

        var missing = new BdhRecord("Contact", new JsonObject { ["Name"] = "No id" });
        Assert.Equal(string.Empty, missing.RawId);
    }

    [Fact]
    public void BdhRecord_CaseInsensitiveFieldLookup()
    {
        var record = new BdhRecord("Contact", new JsonObject
        {
            ["Id"] = "0035e00000abcde",
            ["OwnerEmail"] = "a@example.com",
            ["Status"] = new JsonObject { ["name"] = "Active" },
        });
        Assert.Equal("a@example.com", record.GetString("owneremail"));
        Assert.Equal("a@example.com", record.GetString("OWNEREMAIL"));
        Assert.Null(record.GetString("Ghost"));
        // Object-with-name flattens to the name (reference-shaped columns).
        Assert.Equal("Active", record.GetString("status"));
    }

    // Stale-index expiry (#8, GRAPH_ITEM_TTL_DAYS).

    [Fact]
    public void Convert_NoTtlConfigured_LeavesExpirationUnset()
    {
        var converter = new ItemConverter(TestConfig.Make());  // graphItemTtlDays: 0
        var item = converter.Convert(Record(), OpportunityConfig(), DefaultAcl);

        Assert.Null(item.ExpirationDateTime);
        Assert.Null(item.ToJson()["expirationDateTime"]);
    }

    [Fact]
    public void Convert_TtlConfigured_StampsExpirationDateTime()
    {
        var before = DateTime.UtcNow;
        var converter = new ItemConverter(TestConfig.Make(graphItemTtlDays: 30));
        var item = converter.Convert(Record(), OpportunityConfig(), DefaultAcl);

        Assert.NotNull(item.ExpirationDateTime);
        var expiry = item.ExpirationDateTime!.Value;
        // now + 30d, allowing a small window for test execution time.
        Assert.InRange(
            expiry,
            before.AddDays(30).AddMinutes(-1),
            DateTime.UtcNow.AddDays(30).AddMinutes(1));

        // ...and it serializes as ISO-8601 on the externalItem payload.
        var iso = item.ToJson()["expirationDateTime"]!.GetValue<string>();
        Assert.Equal(expiry.ToUniversalTime(), DateTimeOffset.Parse(iso).UtcDateTime, TimeSpan.FromSeconds(1));
    }
}
