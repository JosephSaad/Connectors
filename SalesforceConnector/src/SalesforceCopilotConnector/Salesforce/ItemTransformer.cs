// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Item;

namespace SalesforceCopilotConnector.Salesforce;

/// <summary>Module-level constants and helpers of ``salesforce/item_transformer.py``.</summary>
public static class ItemTransformer
{
    public const string PrincipalODataType = "#microsoft.graph.externalConnectors.principal";

    public static readonly Dictionary<string, string> CollectionSchemaToODataType = new()
    {
        ["StringCollection"] = "String",
        ["Int64Collection"] = "Int64",
        ["DoubleCollection"] = "Double",
        ["BooleanCollection"] = "Boolean",
        ["DateTimeCollection"] = "DateTime",
        ["PrincipalCollection"] = "microsoft.graph.externalConnectors.principal",
    };

    /// <summary>Inject ``@odata.type`` into a principal object if not already present.</summary>
    public static JsonNode? InjectPrincipalODataType(JsonNode? obj)
    {
        if (obj is JsonObject dict && !dict.ContainsKey("@odata.type"))
        {
            var result = new JsonObject { ["@odata.type"] = PrincipalODataType };
            foreach (var (key, value) in dict)
                result[key] = value?.DeepClone();
            return result;
        }
        return obj;
    }

    /// <summary>
    /// Return a deny-everyone ACL when no ACL could be resolved.
    ///
    /// Items ingested with this ACL will not appear in any user's search results.
    /// This is the safe default — it prevents accidental data exposure when ACL
    /// resolution fails.
    /// </summary>
    public static List<Dictionary<string, string>> FallbackAcl()
    {
        return new List<Dictionary<string, string>>
        {
            new()
            {
                ["accessType"] = "deny",
                ["type"] = "everyone",
                ["value"] = "everyone",
            },
        };
    }
}

public class SalesforceItemTransformer
{
    private readonly string _tenantId;
    private readonly Dictionary<string, string?> _schemaPropertyTypes;
    private readonly HashSet<string> _schemaProperties;
    private readonly SalesforceConverter _converter;
    private readonly HashSet<string> _supportedObjects;

    /// <summary>Initialize the transformer with a Salesforce *instanceUrl* and Graph connector *schema*.</summary>
    public SalesforceItemTransformer(string instanceUrl, JsonArray schema, string tenantId = "everyone")
        : this(instanceUrl, schema, new SalesforceConverter(instanceUrl), tenantId)
    {
    }

    /// <summary>
    /// Test seam — inject a converter (the Python tests patch
    /// ``salesforce.item_transformer.SalesforceConverter``).
    /// </summary>
    internal SalesforceItemTransformer(
        string instanceUrl,
        JsonArray schema,
        SalesforceConverter converter,
        string tenantId = "everyone")
    {
        _tenantId = tenantId;
        _schemaPropertyTypes = new Dictionary<string, string?>();
        foreach (var prop in schema)
        {
            if (prop is JsonObject propObj &&
                propObj["name"]?.GetValue<string>() is { Length: > 0 } name)
            {
                _schemaPropertyTypes[name] = propObj["type"]?.GetValue<string>();
            }
        }
        _schemaProperties = new HashSet<string>(_schemaPropertyTypes.Keys);
        _converter = converter;
        _supportedObjects = new HashSet<string>(_converter.ObjectNames);
        // Inject the real Graph schema properties into each handler so the
        // debug mapping table and principal promotion can use live schema info.
        // (Python also assigns ``graph_schema_property_types`` dynamically; that
        // attribute is never read anywhere, so it is intentionally omitted here.)
        foreach (var objectName in _converter.ObjectNames)
        {
            var handler = _converter.GetHandler(objectName);
            if (handler is null)
                continue;
            handler.GraphSchemaProperties = new HashSet<string>(_schemaProperties);
        }
    }

    /// <summary>Map of supported object names to their converter handlers.</summary>
    // virtual so tests can substitute canned handlers (Python tests set ``mock.handlers``).
    public virtual Dictionary<string, SalesforceObjectHandler> Handlers
    {
        get
        {
            var result = new Dictionary<string, SalesforceObjectHandler>();
            foreach (var objectName in _supportedObjects)
            {
                if (_converter.GetHandler(objectName) is { } handler)
                    result[objectName] = handler;
            }
            return result;
        }
    }

    /// <summary>Convert a raw Salesforce record into one or more Graph connector external items.</summary>
    // virtual so tests can substitute canned output (Python tests mock SalesforceItemTransformer).
    public virtual List<JsonObject> TransformRecord(
        JsonObject item,
        List<Dictionary<string, string>>? acl = null)
    {
        var objectType = item["objectType"]?.GetValue<string>();
        var queryResult = new JsonObject { ["records"] = new JsonArray(item.DeepClone()) };
        var convertedItems = _converter.Convert(queryResult, objectType);
        var transformedItems = new List<JsonObject>();
        foreach (var convertedItem in convertedItems)
        {
            if (convertedItem["type"]?.GetValue<string>() == "deleted")
            {
                transformedItems.Add(convertedItem);
                continue;
            }
            transformedItems.Add(BuildLiveItem(item, convertedItem, acl));
        }
        return transformedItems;
    }

    /// <summary>Assemble a Graph connector external item from raw and converted data.</summary>
    private JsonObject BuildLiveItem(
        JsonObject rawItem,
        JsonObject convertedItem,
        List<Dictionary<string, string>>? acl)
    {
        var convertedProperties = convertedItem["properties"] as JsonObject ?? new JsonObject();
        var convertedUrl = convertedProperties["url"]?.GetValue<string>();
        var properties = new JsonObject
        {
            ["url"] = !string.IsNullOrEmpty(convertedUrl) ? convertedUrl : rawItem["url"]?.GetValue<string>(),
            ["ObjectName"] = rawItem["objectType"]?.GetValue<string>(),
        };

        foreach (var (key, value) in convertedProperties)
        {
            if (key is "ObjectName" or "url" || value is null)
                continue;
            if (!_schemaProperties.Contains(key))
                continue;

            var normalizedValue = NormalizeSchemaValue(key, value.DeepClone());
            ApplyCollectionAnnotation(properties, key, normalizedValue);
            properties[key] = normalizedValue;
        }

        var convertedContent = convertedItem["content"] as JsonObject;
        var contentValue = convertedContent?["parsedData"]?.GetValue<string>();

        var convertedId = convertedItem["id"]?.GetValue<string>();

        var aclEntries = acl is { Count: > 0 } ? acl : ItemTransformer.FallbackAcl();
        var aclArray = new JsonArray();
        foreach (var entry in aclEntries)
        {
            var entryObj = new JsonObject();
            foreach (var (entryKey, entryValue) in entry)
                entryObj[entryKey] = entryValue;
            aclArray.Add(entryObj);
        }

        return new JsonObject
        {
            ["id"] = !string.IsNullOrEmpty(convertedId) ? convertedId : rawItem["Id"]?.GetValue<string>(),
            ["properties"] = properties,
            ["content"] = new JsonObject
            {
                ["value"] = !string.IsNullOrEmpty(contentValue) ? contentValue : "",
                ["type"] = "text",
            },
            ["acl"] = aclArray,
        };
    }

    /// <summary>Wrap scalar values in a list when the schema declares a collection type.</summary>
    private JsonNode? NormalizeSchemaValue(string liveKey, JsonNode value)
    {
        var schemaType = _schemaPropertyTypes.GetValueOrDefault(liveKey) ?? "";

        if (schemaType == "Principal")
            return ItemTransformer.InjectPrincipalODataType(value);

        if (!ItemTransformer.CollectionSchemaToODataType.ContainsKey(schemaType))
            return value;

        var items = value as JsonArray ?? new JsonArray(value);

        if (schemaType == "PrincipalCollection")
        {
            var result = new JsonArray();
            foreach (var item in items.ToList())
                result.Add(ItemTransformer.InjectPrincipalODataType(item?.DeepClone()));
            return result;
        }

        return items;
    }

    /// <summary>Add an ``@odata.type`` annotation to *properties* when *liveKey* is a collection type.</summary>
    private void ApplyCollectionAnnotation(
        JsonObject properties,
        string liveKey,
        JsonNode? value)
    {
        var schemaCollectionType = ItemTransformer.CollectionSchemaToODataType.GetValueOrDefault(
            _schemaPropertyTypes.GetValueOrDefault(liveKey) ?? "");
        if (schemaCollectionType is not null)
            properties[$"{liveKey}@odata.type"] = $"Collection({schemaCollectionType})";
    }
}
