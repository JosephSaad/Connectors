using System.Text.Json.Nodes;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;
using ClarizenConnector.Item;

namespace ClarizenConnector.Tests;

public class ItemConverterTests
{
    private static ObjectConfig TaskConfig() => new()
    {
        ObjectName = "Task",
        DisplayName = "Task",
        SelectedFields = new Dictionary<string, string>
        {
            ["Name"] = "Title",
            ["Description"] = "_cz_Description",
            ["State"] = "State",
            ["Project"] = "ProjectName",
            ["PercentCompleted"] = "PercentCompleted",
            ["ActualCost"] = "ActualCost",
        },
        FinancialFields = new List<string> { "ActualCost" },
        IconUrl = "https://icons.example/task.png",
    };

    private static ClarizenRecord Record() => new("Task", new JsonObject
    {
        ["id"] = "/Task/123",
        ["Name"] = "Migrate tenant",
        ["Description"] = "<p>Move &amp; verify</p>",
        ["State"] = new JsonObject { ["id"] = "/State/Active", ["name"] = "Active" },
        ["Project"] = new JsonObject { ["id"] = "/Project/9", ["name"] = "Apollo" },
        ["PercentCompleted"] = 42.5,
        ["ActualCost"] = 1234.5,
    });

    private static readonly List<AclEntry> DefaultAcl = new()
    {
        new AclEntry(AclEntryType.User, "u1", AclAccessType.Grant),
    };

    [Fact]
    public void Convert_MapsFieldsToGraphProperties()
    {
        var converter = new ItemConverter(TestConfig.Make());
        var item = converter.Convert(Record(), TaskConfig(), DefaultAcl);

        Assert.Equal("Task_123", item.Id);
        Assert.Equal("Migrate tenant", item.Properties["Title"]);
        Assert.Equal("Active", item.Properties["State"]);          // reference → name
        Assert.Equal("Apollo", item.Properties["ProjectName"]);
        Assert.Equal(42.5, item.Properties["PercentCompleted"]);
        Assert.Equal("Task", item.Properties["ObjectName"]);
        Assert.Equal("https://icons.example/task.png", item.Properties["IconUrl"]);
        Assert.False(item.Properties.ContainsKey("_cz_Description"));
        Assert.False(item.Properties.ContainsKey("Description"));
    }

    [Fact]
    public void Convert_BuildsDeepLink()
    {
        var converter = new ItemConverter(TestConfig.Make(), appBaseUrl: "https://app.clarizen.com");
        var item = converter.Convert(Record(), TaskConfig(), DefaultAcl);
        Assert.Equal(
            "https://app.clarizen.com/Clarizen/Link.aspx?entityType=Task&id=123",
            item.Properties["Url"]);
    }

    [Fact]
    public void Convert_ContentIncludesTitleAndCzFields_HtmlStripped()
    {
        var converter = new ItemConverter(TestConfig.Make());
        var item = converter.Convert(Record(), TaskConfig(), DefaultAcl);
        Assert.Contains("Task: Migrate tenant", item.Content);
        Assert.Contains("Move & verify", item.Content);
        Assert.DoesNotContain("<p>", item.Content);
    }

    [Fact]
    public void Convert_AppliesFinancialClassification()
    {
        var converter = new ItemConverter(TestConfig.Make(financialMode: "tag"));
        var item = converter.Convert(Record(), TaskConfig(), DefaultAcl);
        Assert.Equal(true, item.Properties[FinancialFieldClassifier.ContainsFinancialProperty]);
        Assert.Equal("financial", item.Properties[FinancialFieldClassifier.ClassificationProperty]);
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
            ItemConverter.ToPropertyValue(JsonNode.Parse("""{"id": "/X/1", "name": "RefName"}""")));
        Assert.Equal("/X/1",
            ItemConverter.ToPropertyValue(JsonNode.Parse("""{"id": "/X/1"}""")));
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
        var item = new ExternalItem { Id = "Task_1", Acl = { DefaultAcl[0] } };
        // Property NAMES here must be ones config/graph-schema.json declares —
        // the bag rejects anything else. The point under test is the C# value
        // type → JSON value mapping, so one declared name per value shape.
        item.Properties["Title"] = "T";
        item.Properties["FileSize"] = 3L;
        item.Properties["StartDate"] = new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc);
        item.Properties["DetectedCategories"] = new[] { "a", "b" };
        item.Properties["IconUrl"] = null;
        item.Content = "body";

        var json = item.ToJson();
        Assert.Equal("Task_1", json["id"]!.GetValue<string>());
        Assert.Equal("T", json["properties"]!["Title"]!.GetValue<string>());
        Assert.Equal(3, json["properties"]!["FileSize"]!.GetValue<long>());
        Assert.StartsWith("2026-07-12", json["properties"]!["StartDate"]!.GetValue<string>());
        Assert.Equal(2, json["properties"]!["DetectedCategories"]!.AsArray().Count);
        Assert.Null(json["properties"]!["IconUrl"]);
        Assert.Equal("body", json["content"]!["value"]!.GetValue<string>());
        Assert.Equal("text", json["content"]!["type"]!.GetValue<string>());
        Assert.Single(json["acl"]!.AsArray());
    }

    [Fact]
    public void ClarizenRecord_IdHandling()
    {
        var record = new ClarizenRecord("Task", new JsonObject { ["id"] = "/Task/987" });
        Assert.Equal("/Task/987", record.RawId);
        Assert.Equal("987", record.LocalId);
        Assert.Equal("Task_987", record.ItemId);

        var bare = new ClarizenRecord("Task", new JsonObject { ["id"] = "987" });
        Assert.Equal("Task_987", bare.ItemId);
    }
}
