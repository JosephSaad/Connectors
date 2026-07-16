// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for the item conversion engine (Item.Converter).

using System.Text.Json;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.AclEngine;
using SalesforceCopilotConnector.Item;

namespace SalesforceCopilotConnector.Tests.TestItem;

public class ItemConverterTests
{
    private static JsonObject Records(JsonObject record) =>
        new JsonObject { ["records"] = new JsonArray { record } };

    private static string? ItemType(JsonObject item) =>
        item.TryGetPropertyValue("type", out var type) ? type?.GetValue<string>() : null;

    [Fact]
    public void LoadConverterConfigReturnsValid()
    {
        var config = Converter.LoadConverterConfig();
        Assert.IsType<JsonObject>(config);
        Assert.True(config.ContainsKey("objectList"));
    }

    [Fact]
    public void SalesforceConverterCanBeInstantiated()
    {
        var converter = new SalesforceConverter(instanceUrl: "https://test.my.salesforce.com");
        Assert.NotNull(converter);
        Assert.True(converter.ObjectNames.Count > 0);
    }

    [Fact]
    public void ConvertSimpleAccount()
    {
        var converter = new SalesforceConverter(instanceUrl: "https://test.my.salesforce.com");
        var record = new JsonObject
        {
            ["Id"] = "001abc",
            ["Name"] = "Acme Corp",
            ["IsDeleted"] = false,
            ["objectType"] = "Account",
            ["url"] = "https://test.my.salesforce.com/001abc",
            ["attributes"] = new JsonObject { ["type"] = "Account" },
            ["OwnerId"] = "005abc",
            ["Owner"] = new JsonObject
            {
                ["Name"] = "Test User",
                ["UserRole"] = new JsonObject { ["Id"] = "role1", ["ParentRoleId"] = null },
            },
            ["CreatedDate"] = "2024-01-01T00:00:00.000+0000",
            ["LastModifiedDate"] = "2024-06-01T00:00:00.000+0000",
            ["CreatedById"] = "005abc",
            ["CreatedBy"] = new JsonObject { ["Name"] = "Creator" },
            ["LastModifiedById"] = "005abc",
            ["LastModifiedBy"] = new JsonObject { ["Name"] = "Modifier" },
        };
        var sfResult = Records(record);
        var items = converter.Convert(sfResult, objectName: "Account");
        Assert.True(items.Count >= 1);
        var item = items[0];
        Assert.True(item.ContainsKey("id"));
    }

    [Fact]
    public void DeletedRecordProducesDeletedItem()
    {
        var converter = new SalesforceConverter(instanceUrl: "https://test.my.salesforce.com");
        var record = new JsonObject
        {
            ["Id"] = "001del",
            ["Name"] = "Deleted Corp",
            ["IsDeleted"] = true,
            ["objectType"] = "Account",
            ["url"] = "https://test.my.salesforce.com/001del",
            ["attributes"] = new JsonObject { ["type"] = "Account" },
            ["OwnerId"] = "005abc",
            ["Owner"] = new JsonObject
            {
                ["Name"] = "Test User",
                ["UserRole"] = new JsonObject { ["Id"] = "role1", ["ParentRoleId"] = null },
            },
            ["CreatedDate"] = "2024-01-01T00:00:00.000+0000",
            ["LastModifiedDate"] = "2024-06-01T00:00:00.000+0000",
            ["CreatedById"] = "005abc",
            ["CreatedBy"] = new JsonObject { ["Name"] = "Creator" },
            ["LastModifiedById"] = "005abc",
            ["LastModifiedBy"] = new JsonObject { ["Name"] = "Modifier" },
        };
        var items = converter.Convert(Records(record), objectName: "Account");
        Assert.True(items.Count >= 1);
        Assert.Equal("deleted", ItemType(items[0]));
    }

    [Fact]
    public void ContentFieldMappedToParsedData()
    {
        // If Description is present, it should appear in the content.parsedData.
        var converter = new SalesforceConverter(instanceUrl: "https://test.my.salesforce.com");
        var record = new JsonObject
        {
            ["Id"] = "001desc",
            ["IsDeleted"] = false,
            ["objectType"] = "Account",
            ["url"] = "https://test.my.salesforce.com/001desc",
            ["attributes"] = new JsonObject { ["type"] = "Account" },
            ["Name"] = "Acme Corp",
            ["Description"] = "This is the account description.",
            ["OwnerId"] = "005abc",
            ["Owner"] = new JsonObject
            {
                ["Name"] = "Test User",
                ["UserRole"] = new JsonObject { ["Id"] = "role1", ["ParentRoleId"] = null },
            },
            ["CreatedDate"] = "2024-01-01T00:00:00.000+0000",
            ["LastModifiedDate"] = "2024-06-01T00:00:00.000+0000",
            ["CreatedById"] = "005abc",
            ["CreatedBy"] = new JsonObject { ["Name"] = "Creator" },
            ["LastModifiedById"] = "005abc",
            ["LastModifiedBy"] = new JsonObject { ["Name"] = "Modifier" },
        };
        var items = converter.Convert(Records(record), objectName: "Account");
        var nonDeleted = items.Where(i => ItemType(i) != "deleted").ToList();
        if (nonDeleted.Count > 0)
        {
            var content = nonDeleted[0].TryGetPropertyValue("content", out var contentNode) ? contentNode : null;
            if (content is JsonObject contentObject)
            {
                var parsedData = contentObject["parsedData"]?.GetValue<string>() ?? "";
                Assert.Contains("This is the account description", parsedData);
            }
        }
    }

    [Fact]
    public void MetadataColumnsMapped()
    {
        var converter = new SalesforceConverter(instanceUrl: "https://test.my.salesforce.com");
        var record = new JsonObject
        {
            ["Id"] = "001meta",
            ["Name"] = "Meta Corp",
            ["IsDeleted"] = false,
            ["objectType"] = "Account",
            ["url"] = "https://test.my.salesforce.com/001meta",
            ["attributes"] = new JsonObject { ["type"] = "Account" },
            ["OwnerId"] = "005abc",
            ["Owner"] = new JsonObject
            {
                ["Name"] = "Owner User",
                ["UserRole"] = new JsonObject { ["Id"] = "role1", ["ParentRoleId"] = null },
            },
            ["CreatedDate"] = "2024-01-01T00:00:00.000+0000",
            ["LastModifiedDate"] = "2024-06-01T00:00:00.000+0000",
            ["CreatedById"] = "005abc",
            ["CreatedBy"] = new JsonObject { ["Name"] = "Creator" },
            ["LastModifiedById"] = "005def",
            ["LastModifiedBy"] = new JsonObject { ["Name"] = "Modifier" },
        };
        var items = converter.Convert(Records(record), objectName: "Account");
        var nonDeleted = items.Where(i => ItemType(i) != "deleted").ToList();
        if (nonDeleted.Count > 0)
        {
            var props = nonDeleted[0]["properties"] as JsonObject ?? new JsonObject();
            // At least some metadata should be present
            Assert.True(props.ContainsKey("Id") || props.ContainsKey("CreatedDate") || props.ContainsKey("Owner"));
        }
    }

    // -----------------------------------------------------------------------
    // _share_table_name from acl_engine
    // -----------------------------------------------------------------------

    [Fact]
    public void ShareTableNameStandardObject()
    {
        Assert.Equal("AccountShare", ShareFetcher.ShareTableName("Account"));
        Assert.Equal("CaseShare", ShareFetcher.ShareTableName("Case"));
    }

    [Fact]
    public void ShareTableNameCustomObject()
    {
        Assert.Equal("Work_Order__Share", ShareFetcher.ShareTableName("Work_Order__c"));
        Assert.Equal("Customer_Project__Share", ShareFetcher.ShareTableName("Customer_Project__c"));
    }
}

/// <summary>Non-graph-schema selectedFields must appear in content, not be silently dropped.</summary>
public class TestNonSchemaFieldsInContent
{
    private static JsonObject BuildContactRecord(Dictionary<string, JsonNode?>? overrides = null)
    {
        var baseRecord = new JsonObject
        {
            ["attributes"] = new JsonObject { ["type"] = "Contact" },
            ["Id"] = "003abc",
            ["Name"] = "Test User",
            ["IsDeleted"] = false,
            ["OwnerId"] = "005abc",
            ["Owner"] = new JsonObject
            {
                ["attributes"] = new JsonObject { ["type"] = "User" },
                ["Name"] = "Owner",
                ["UserRole"] = null,
            },
            ["CreatedDate"] = "2024-01-01T00:00:00.000+0000",
            ["LastModifiedDate"] = "2024-06-01T00:00:00.000+0000",
            ["CreatedById"] = "005abc",
            ["CreatedBy"] = new JsonObject
            {
                ["attributes"] = new JsonObject { ["type"] = "User" },
                ["Name"] = "Creator",
            },
            ["LastModifiedById"] = "005abc",
            ["LastModifiedBy"] = new JsonObject
            {
                ["attributes"] = new JsonObject { ["type"] = "User" },
                ["Name"] = "Modifier",
            },
        };
        if (overrides is not null)
        {
            foreach (var pair in overrides)
            {
                baseRecord[pair.Key] = pair.Value;
            }
        }
        return baseRecord;
    }

    private static JsonObject Records(JsonObject record) =>
        new JsonObject { ["records"] = new JsonArray { record } };

    private static string? ItemType(JsonObject item) =>
        item.TryGetPropertyValue("type", out var type) ? type?.GetValue<string>() : null;

    private static string ContentValue(JsonObject item)
    {
        var content = item.TryGetPropertyValue("content", out var contentNode) ? contentNode : null;
        return content is JsonObject contentObject
            ? contentObject["parsedData"]?.GetValue<string>() ?? ""
            : "";
    }

    [Fact]
    public void NonSchemaSelectedFieldsAppearInContent()
    {
        // Fields in selectedFields but NOT in graph-schema should appear in content.parsedData.
        var converter = new SalesforceConverter(instanceUrl: "https://test.my.salesforce.com");
        var handler = converter.GetHandler("Contact");
        Assert.NotNull(handler);

        // Simulate the transformer setting the real Graph schema properties
        // (a small subset — only Id, Name, ObjectName, url, AccountId, Status exist in graph-schema)
        handler!.GraphSchemaProperties = new HashSet<string> { "Id", "Name", "ObjectName", "url", "AccountId", "Status" };

        var record = BuildContactRecord(new Dictionary<string, JsonNode?>
        {
            ["Email"] = "anna@example.com",
            ["FirstName"] = "Anna",
            ["LastName"] = "Smith",
            ["Phone"] = "+1-555-1234",
            ["Title"] = "Architect",
        });
        var items = converter.Convert(Records(record), objectName: "Contact");
        var nonDeleted = items.Where(i => ItemType(i) != "deleted").ToList();
        Assert.Single(nonDeleted);

        var item = nonDeleted[0];
        var props = item["properties"]!.AsObject();
        var contentValue = ContentValue(item);

        // These ARE in graph-schema → should be in properties
        Assert.Equal("Test User", props["Name"]?.GetValue<string>());
        Assert.True(props.ContainsKey("ObjectName"));

        // These are NOT in graph-schema → should NOT be in properties
        Assert.False(props.ContainsKey("Email"));
        Assert.False(props.ContainsKey("FirstName"));
        Assert.False(props.ContainsKey("LastName"));
        Assert.False(props.ContainsKey("Phone"));
        Assert.False(props.ContainsKey("JobTitle"));  // Title maps to JobTitle

        // They SHOULD appear in content.parsedData instead
        Assert.Contains("anna@example.com", contentValue);
        Assert.Contains("Anna", contentValue);
        Assert.Contains("Smith", contentValue);
        Assert.Contains("+1-555-1234", contentValue);
        Assert.Contains("Architect", contentValue);
    }

    [Fact]
    public void SchemaFieldsNotDuplicatedInContent()
    {
        // Fields that ARE in the graph-schema should be in properties, NOT in content.
        var converter = new SalesforceConverter(instanceUrl: "https://test.my.salesforce.com");
        var handler = converter.GetHandler("Contact");
        Assert.NotNull(handler);
        handler!.GraphSchemaProperties = new HashSet<string> { "Id", "Name", "ObjectName", "url", "AccountId", "Status" };

        var record = BuildContactRecord(new Dictionary<string, JsonNode?> { ["Name"] = "Anna Smith" });
        var items = converter.Convert(Records(record), objectName: "Contact");
        var item = items.Where(i => ItemType(i) != "deleted").ToList()[0];

        Assert.Equal("Anna Smith", item["properties"]!.AsObject()["Name"]?.GetValue<string>());
        var contentValue = ContentValue(item);
        // Name should NOT appear in content (it's in graph-schema → properties)
        Assert.DoesNotContain("Name: Anna Smith", contentValue);
    }

    [Fact]
    public void NullNonSchemaFieldsOmittedFromContent()
    {
        // Null-valued non-schema fields should not appear in content.
        var converter = new SalesforceConverter(instanceUrl: "https://test.my.salesforce.com");
        var handler = converter.GetHandler("Contact");
        Assert.NotNull(handler);
        handler!.GraphSchemaProperties = new HashSet<string> { "Id", "Name", "ObjectName", "url" };

        var record = BuildContactRecord();
        // Explicitly set to null (simulates Salesforce null return)
        record["Email"] = null;
        record["Phone"] = null;
        var items = converter.Convert(Records(record), objectName: "Contact");
        var item = items.Where(i => ItemType(i) != "deleted").ToList()[0];
        var contentValue = ContentValue(item);

        Assert.DoesNotContain("Email", contentValue);
        Assert.DoesNotContain("Phone", contentValue);
    }

    [Fact]
    public void SyntheticUrlAndObjecttypeNotInContent()
    {
        // Synthetic 'url' and 'objectType' keys on the record must not leak into content.
        var converter = new SalesforceConverter(instanceUrl: "https://test.my.salesforce.com");
        var handler = converter.GetHandler("Contact");
        Assert.NotNull(handler);
        handler!.GraphSchemaProperties = new HashSet<string> { "Id", "Name", "ObjectName", "url" };

        var record = BuildContactRecord();
        // These are added by api_client before converter sees the record
        record["url"] = "https://test.my.salesforce.com/003abc";
        record["objectType"] = "Contact";

        var items = converter.Convert(Records(record), objectName: "Contact");
        var item = items.Where(i => ItemType(i) != "deleted").ToList()[0];
        var contentValue = ContentValue(item);

        Assert.DoesNotContain("objectType", contentValue);
        Assert.DoesNotContain("objectType: Contact", contentValue);
        // url as a standalone content entry should not appear
        // (url is a schema property set synthetically, not a content field)
        Assert.DoesNotContain("url: https://", contentValue);
    }
}

// ---------------------------------------------------------------------------
// _convert_value – datetime handling
// ---------------------------------------------------------------------------

/// <summary>Verify that all Salesforce datetime formats are normalised to ISO 8601 with Z.</summary>
public class TestConvertValueDatetime
{
    private static string? ConvertDatetime(string value) =>
        Converter.ConvertValue(JsonValue.Create(value), "datetime")?.GetValue<string>();

    private static string? ConvertDatetime(DateTime value) =>
        Converter.ConvertValue(JsonSerializer.SerializeToNode(value), "datetime")?.GetValue<string>();

    [Fact]
    public void SalesforceOffsetFormat()
    {
        // Salesforce standard: +0000 offset without colon.
        Assert.Equal("2024-04-11T09:10:06Z", ConvertDatetime("2024-04-11T09:10:06.000+0000"));
    }

    [Fact]
    public void SalesforceOffsetWithColon()
    {
        // Offset with colon (+00:00).
        Assert.Equal("2024-01-01T00:00:00Z", ConvertDatetime("2024-01-01T00:00:00.000+00:00"));
    }

    [Fact]
    public void ZSuffix()
    {
        // Trailing Z suffix.
        Assert.Equal("2024-06-15T14:30:00Z", ConvertDatetime("2024-06-15T14:30:00Z"));
    }

    [Fact]
    public void ZSuffixWithMilliseconds()
    {
        // Z suffix with milliseconds.
        Assert.Equal("2024-06-15T14:30:00.123000Z", ConvertDatetime("2024-06-15T14:30:00.123Z"));
    }

    [Fact]
    public void NonUtcPositiveOffset()
    {
        // Non-UTC positive offset is converted to UTC.
        Assert.Equal("2024-03-20T09:30:00Z", ConvertDatetime("2024-03-20T15:00:00+05:30"));
    }

    [Fact]
    public void NonUtcNegativeOffset()
    {
        // Non-UTC negative offset is converted to UTC.
        Assert.Equal("2024-03-20T17:00:00Z", ConvertDatetime("2024-03-20T10:00:00-07:00"));
    }

    [Fact]
    public void NoFractionalSeconds()
    {
        // Date string without fractional seconds.
        Assert.Equal("2024-04-11T09:10:06Z", ConvertDatetime("2024-04-11T09:10:06+0000"));
    }

    [Fact]
    public void DateOnlyNoTime()
    {
        // Date-only string (Salesforce Date fields).
        Assert.Equal("2024-04-11T00:00:00Z", ConvertDatetime("2024-04-11"));
    }

    [Fact]
    public void NoneReturnsNone()
    {
        // None input returns None.
        Assert.Null(Converter.ConvertValue(null, "datetime"));
    }

    [Fact]
    public void AwareDatetimeObject()
    {
        // Already-aware datetime object (Python: tzinfo=timezone.utc).
        var dt = new DateTime(2024, 4, 11, 9, 10, 6, DateTimeKind.Utc);
        Assert.Equal("2024-04-11T09:10:06Z", ConvertDatetime(dt));
    }

    [Fact]
    public void NaiveDatetimeObjectAssumedUtc()
    {
        // Naive datetime is assumed UTC.
        var dt = new DateTime(2024, 4, 11, 9, 10, 6, DateTimeKind.Unspecified);
        Assert.Equal("2024-04-11T09:10:06Z", ConvertDatetime(dt));
    }

    [Fact]
    public void NonZeroMillisecondsPreserved()
    {
        // Non-zero fractional seconds are preserved.
        Assert.Equal("2024-04-11T09:10:06.500000Z", ConvertDatetime("2024-04-11T09:10:06.500+0000"));
    }
}
