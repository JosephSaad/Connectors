// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Item;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests.TestSalesforce;

/// <summary>Tests for SalesforceItemTransformer (salesforce.item_transformer) — port of
/// tests/test_salesforce/test_salesforce_item_transformer.py.</summary>
public class SalesforceItemTransformerTests
{
    /// <summary>
    /// Fake converter returning canned output (Python patches
    /// ``salesforce.item_transformer.SalesforceConverter``).
    /// </summary>
    private sealed class FakeConverter : SalesforceConverter
    {
        private readonly List<JsonObject> _convertResult;

        public FakeConverter(List<JsonObject> convertResult)
            : base(
                "https://test.my.salesforce.com",
                config: new JsonObject { ["objectList"] = new JsonArray() })
        {
            _convertResult = convertResult;
        }

        public override List<string> ObjectNames => new() { "Account" };

        public override SalesforceObjectHandler? GetHandler(string objectName) => null;

        public override List<JsonObject> Convert(JsonObject sfQueryResult, string? objectName = null)
            => _convertResult;
    }

    /// <summary>Minimal Graph schema for testing (pytest ``schema`` fixture).</summary>
    private static JsonArray Schema() => new(
        new JsonObject { ["name"] = "Url", ["type"] = "String" },
        new JsonObject { ["name"] = "ObjectName", ["type"] = "String" },
        new JsonObject { ["name"] = "Title", ["type"] = "String" },
        new JsonObject { ["name"] = "Description", ["type"] = "String" },
        new JsonObject { ["name"] = "CreatedDate", ["type"] = "DateTime" },
        new JsonObject { ["name"] = "LastModifiedDate", ["type"] = "DateTime" },
        new JsonObject { ["name"] = "Owner", ["type"] = "String" },
        new JsonObject { ["name"] = "Tags", ["type"] = "StringCollection" });

    private static SalesforceItemTransformer MakeTransformer(
        JsonArray schema,
        List<JsonObject> convertResult,
        string tenantId = "everyone")
    {
        return new SalesforceItemTransformer(
            "https://test.my.salesforce.com",
            schema,
            new FakeConverter(convertResult),
            tenantId);
    }

    private static JsonObject RawRecord(string id) => new()
    {
        ["Id"] = id,
        ["objectType"] = "Account",
        ["url"] = $"https://sf.com/{id}",
    };

    [Fact]
    public void CanInstantiate()
    {
        // pytest ``transformer`` fixture — real converter, minimal schema.
        var transformer = new SalesforceItemTransformer(
            instanceUrl: "https://test.my.salesforce.com",
            schema: Schema());
        Assert.NotNull(transformer);
    }

    [Fact]
    public void TransformRecordProducesOutput()
    {
        var transformer = MakeTransformer(Schema(), new List<JsonObject>
        {
            new()
            {
                ["id"] = "001abc",
                ["properties"] = new JsonObject
                {
                    ["Url"] = "https://sf.com/001abc",
                    ["Title"] = "Acme",
                },
                ["content"] = new JsonObject { ["parsedData"] = "description text" },
            },
        });
        var result = transformer.TransformRecord(RawRecord("001abc"));
        Assert.Single(result);
        Assert.Equal("001abc", result[0]["id"]!.GetValue<string>());
        Assert.Equal("text", result[0]["content"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void AclIncludedWhenProvided()
    {
        var transformer = MakeTransformer(Schema(), new List<JsonObject>
        {
            new()
            {
                ["id"] = "001",
                ["properties"] = new JsonObject { ["Url"] = "https://sf.com/001" },
                ["content"] = new JsonObject(),
            },
        });
        var acl = new List<Dictionary<string, string>>
        {
            new() { ["accessType"] = "grant", ["type"] = "user", ["value"] = "user-id" },
        };
        var result = transformer.TransformRecord(RawRecord("001"), acl: acl);

        var resultAcl = (JsonArray)result[0]["acl"]!;
        Assert.Single(resultAcl);
        var entry = (JsonObject)resultAcl[0]!;
        Assert.Equal("grant", entry["accessType"]!.GetValue<string>());
        Assert.Equal("user", entry["type"]!.GetValue<string>());
        Assert.Equal("user-id", entry["value"]!.GetValue<string>());
    }

    [Fact]
    public void FallbackAclUsedWhenNone()
    {
        var transformer = MakeTransformer(
            Schema(),
            new List<JsonObject>
            {
                new()
                {
                    ["id"] = "001",
                    ["properties"] = new JsonObject { ["Url"] = "https://sf.com/001" },
                    ["content"] = new JsonObject(),
                },
            },
            tenantId: "test-tenant");
        var result = transformer.TransformRecord(RawRecord("001"), acl: null);

        var resultAcl = (JsonArray)result[0]["acl"]!;
        Assert.Equal("everyone", resultAcl[0]!["type"]!.GetValue<string>());
        Assert.Equal("everyone", resultAcl[0]!["value"]!.GetValue<string>());
    }

    [Fact]
    public void DeletedItemsPassThrough()
    {
        var transformer = MakeTransformer(Schema(), new List<JsonObject>
        {
            new() { ["type"] = "deleted", ["id"] = "001del" },
        });
        var result = transformer.TransformRecord(RawRecord("001del"));
        Assert.Equal("deleted", result[0]["type"]!.GetValue<string>());
    }

    [Fact]
    public void CollectionTypesGetODataAnnotation()
    {
        var transformer = MakeTransformer(Schema(), new List<JsonObject>
        {
            new()
            {
                ["id"] = "001",
                ["properties"] = new JsonObject
                {
                    ["Url"] = "https://sf.com/001",
                    ["Tags"] = new JsonArray("a", "b"),
                },
                ["content"] = new JsonObject(),
            },
        });
        var result = transformer.TransformRecord(RawRecord("001"));
        var props = (JsonObject)result[0]["properties"]!;
        Assert.True(props.ContainsKey("Tags@odata.type"));
        Assert.Equal("Collection(String)", props["Tags@odata.type"]!.GetValue<string>());
    }

    [Fact]
    public void PrincipalCollectionGetsODataAnnotation()
    {
        var schema = new JsonArray(
            new JsonObject { ["name"] = "Url", ["type"] = "String" },
            new JsonObject { ["name"] = "ObjectName", ["type"] = "String" },
            new JsonObject { ["name"] = "Authors", ["type"] = "PrincipalCollection" });
        var transformer = MakeTransformer(schema, new List<JsonObject>
        {
            new()
            {
                ["id"] = "001",
                ["properties"] = new JsonObject
                {
                    ["Url"] = "https://sf.com/001",
                    ["Authors"] = new JsonArray(
                        new JsonObject { ["externalName"] = "user1", ["externalId"] = "id1" },
                        new JsonObject { ["externalName"] = "user2", ["externalId"] = "id2" }),
                },
                ["content"] = new JsonObject(),
            },
        });
        var result = transformer.TransformRecord(RawRecord("001"));
        var props = (JsonObject)result[0]["properties"]!;
        Assert.True(props.ContainsKey("Authors@odata.type"));
        Assert.Equal(
            "Collection(microsoft.graph.externalConnectors.principal)",
            props["Authors@odata.type"]!.GetValue<string>());
        // each principal dict gets @odata.type injected
        var authors = (JsonArray)props["Authors"]!;
        Assert.Equal(ItemTransformer.PrincipalODataType, authors[0]!["@odata.type"]!.GetValue<string>());
        Assert.Equal("id1", authors[0]!["externalId"]!.GetValue<string>());
        Assert.Equal(ItemTransformer.PrincipalODataType, authors[1]!["@odata.type"]!.GetValue<string>());
        Assert.Equal("id2", authors[1]!["externalId"]!.GetValue<string>());
    }

    [Fact]
    public void PrincipalCollectionItemsGetODataTypeInjected()
    {
        var schema = new JsonArray(
            new JsonObject { ["name"] = "Url", ["type"] = "String" },
            new JsonObject { ["name"] = "ObjectName", ["type"] = "String" },
            new JsonObject { ["name"] = "Assignees", ["type"] = "PrincipalCollection" });
        var transformer = MakeTransformer(schema, new List<JsonObject>
        {
            new()
            {
                ["id"] = "001",
                ["properties"] = new JsonObject
                {
                    ["Url"] = "https://sf.com/001",
                    ["Assignees"] = new JsonArray(
                        new JsonObject { ["entraId"] = "aaa", ["upn"] = "a@test.com" },
                        new JsonObject
                        {
                            ["@odata.type"] = ItemTransformer.PrincipalODataType,
                            ["entraId"] = "bbb",
                            ["upn"] = "b@test.com",
                        }),
                },
                ["content"] = new JsonObject(),
            },
        });
        var result = transformer.TransformRecord(RawRecord("001"));
        var assignees = (JsonArray)result[0]["properties"]!["Assignees"]!;
        // @odata.type injected where missing
        Assert.Equal(ItemTransformer.PrincipalODataType, assignees[0]!["@odata.type"]!.GetValue<string>());
        Assert.Equal("aaa", assignees[0]!["entraId"]!.GetValue<string>());
        // @odata.type preserved when already present
        Assert.Equal(ItemTransformer.PrincipalODataType, assignees[1]!["@odata.type"]!.GetValue<string>());
        Assert.Equal("bbb", assignees[1]!["entraId"]!.GetValue<string>());
    }

    [Fact]
    public void SinglePrincipalGetsODataTypeInjected()
    {
        var schema = new JsonArray(
            new JsonObject { ["name"] = "Url", ["type"] = "String" },
            new JsonObject { ["name"] = "ObjectName", ["type"] = "String" },
            new JsonObject { ["name"] = "CreatedBy", ["type"] = "Principal" });
        var transformer = MakeTransformer(schema, new List<JsonObject>
        {
            new()
            {
                ["id"] = "001",
                ["properties"] = new JsonObject
                {
                    ["Url"] = "https://sf.com/001",
                    ["CreatedBy"] = new JsonObject
                    {
                        ["entraId"] = "b671a5be",
                        ["upn"] = "alex@contoso.com",
                    },
                },
                ["content"] = new JsonObject(),
            },
        });
        var result = transformer.TransformRecord(RawRecord("001"));
        var createdBy = (JsonObject)result[0]["properties"]!["CreatedBy"]!;
        Assert.Equal(ItemTransformer.PrincipalODataType, createdBy["@odata.type"]!.GetValue<string>());
        Assert.Equal("b671a5be", createdBy["entraId"]!.GetValue<string>());
        // single Principal should NOT produce a collection annotation
        Assert.False(((JsonObject)result[0]["properties"]!).ContainsKey("CreatedBy@odata.type"));
    }
}
