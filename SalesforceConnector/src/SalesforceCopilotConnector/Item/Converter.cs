// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Item/Converter.cs
// -----------------
// Salesforce → Graph external item conversion engine.
//
// Transforms raw Salesforce SOQL query results into Microsoft Graph
// ``externalItem`` payloads.  The conversion is driven entirely by the
// schema defined in ``config/schema.json``.
//
// Key concepts
// ------------
// * **SalesforceObjectHandler** — one per Salesforce object type (e.g. Account,
//   Case).  Reads ``selectedFields`` from the schema config to know which
//   Salesforce fields map to which Graph schema properties.  Handles nested
//   relationship objects (``Owner.Name``), address serialisation, type
//   coercion (bool / int / float / datetime), and parent-child hierarchies
//   (e.g. Account → Opportunity).
//
// * **SalesforceConverter** — high-level facade.  Instantiated once per
//   ingestion run.  Call ``Convert(sfQueryResult)`` to get a list of
//   ``externalItem`` dicts ready for the Graph PUT API.
//
// * **Converter.BuildHandlersFromConfig(config)** — factory that creates the
//   full handler tree (parents + children) from ``config/schema.json``.
//
// Constants
// ---------
// MetadataColumns
//     Standard Salesforce metadata fields (Id, OwnerId, CreatedDate, etc.)
//     that are always requested in SOQL queries regardless of the schema.
//
// MetadataColumnSchemaMapping
//     Maps Salesforce metadata field names to their Graph schema property names.
//
// TypeConverters
//     Maps .NET type names (from Salesforce describe metadata) to the type
//     tags used by ``ConvertValue``.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Item;

public static class Converter
{
    internal static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector");

    public const string ContentFieldName = "Description";
    public const string AuthorsSourceProperty = "Authors";
    public const string CreatedBySourceProperty = "CreatedBy";
    public const string LastModifiedBySourceProperty = "LastModifiedBy";
    public const string SystemCreatedByUserId = "__System.User.CreatedBy.Id";
    public const string SystemModifiedByUserId = "__System.User.ModifiedBy.Id";

    public static readonly string[] MetadataColumns =
    {
        //"Id",
        "LastModifiedDate",
        "IsDeleted",
        "Owner.UserRole.Id",
        "Owner.UserRole.ParentRoleId",
        "OwnerId",
        "Owner.Name",
        "LastModifiedById",
        "LastModifiedBy.Name",
        "CreatedById",
        "CreatedBy.Name",
        "CreatedDate",
    };

    public static readonly Dictionary<string, string> MetadataColumnSchemaMapping = new()
    {
        ["CreatedDate"] = "CreatedDate",
        ["LastModifiedDate"] = "LastModifiedDate",
        ["LastModifiedBy.Name"] = LastModifiedBySourceProperty,
        ["LastModifiedById"] = "LastModifiedByUrl",
        ["CreatedById"] = "CreatedByUrl",
        ["CreatedBy.Name"] = CreatedBySourceProperty,
        ["Owner.Name"] = "Owner",
        ["OwnerId"] = "OwnerUrl",
        ["Id"] = "Id",
    };

    public static readonly Dictionary<string, List<string>> MetadataObjectColumnSchemaMapping = new()
    {
        ["LastModifiedBy"] = new List<string> { "Name" },
        ["CreatedBy"] = new List<string> { "Name" },
        ["Owner"] = new List<string> { "Name" },
    };

    public static readonly Dictionary<string, string> TypeConverters = new()
    {
        ["System.Boolean"] = "bool",
        ["System.Double"] = "float",
        ["System.DateTime"] = "datetime",
        ["System.Int32"] = "int",
        ["System.Int64"] = "int",
        ["System.String"] = "str",
    };

    /// <summary>Load the schema config from <paramref name="path"/> or fall back to the default settings.</summary>
    public static JsonObject LoadConverterConfig(string? path = null)
    {
        if (path is not null)
        {
            using var stream = File.OpenRead(path);
            return JsonNode.Parse(stream)!.AsObject();
        }
        return Settings.LoadSchemaConfig();
    }

    /// <summary>Map a .NET assembly-qualified type name to a type tag.</summary>
    internal static string? ResolveType(string assemblyQualifiedName)
    {
        if (string.IsNullOrEmpty(assemblyQualifiedName))
        {
            return null;
        }
        var dotnetType = assemblyQualifiedName.Split(',')[0].Trim();
        return TypeConverters.TryGetValue(dotnetType, out var tag) ? tag : null;
    }

    /// <summary>Coerce <paramref name="value"/> to the type indicated by <paramref name="typeTag"/>.</summary>
    public static JsonNode? ConvertValue(JsonNode? value, string typeTag)
    {
        if (value is null)
        {
            return null;
        }
        if (typeTag == "bool")
        {
            var kind = value.GetValueKind();
            if (kind == JsonValueKind.True || kind == JsonValueKind.False)
            {
                return JsonValue.Create(kind == JsonValueKind.True);
            }
            if (kind == JsonValueKind.String)
            {
                return JsonValue.Create(value.GetValue<string>().ToLowerInvariant() == "true");
            }
            return JsonValue.Create(PyTruthy(value));
        }
        if (typeTag == "float")
        {
            return JsonValue.Create(ToDouble(value));
        }
        if (typeTag == "int")
        {
            return JsonValue.Create(ToInt(value));
        }
        if (typeTag == "datetime")
        {
            // Python: parse the ISO string (Z → +00:00), assume UTC when naive,
            // then emit ``astimezone(utc).isoformat().replace("+00:00", "Z")``.
            var normalized = PyStr(value).Replace("Z", "+00:00");
            return JsonValue.Create(PyIsoUtcZ(ParseIsoDateTime(normalized)));
        }
        return JsonValue.Create(PyStr(value));
    }

    /// <summary>
    /// Create <see cref="SalesforceObjectHandler"/> instances from the schema config.
    ///
    /// Parent handlers are created first, then child handlers are attached to
    /// their respective parents.  Returns a dict keyed by object name.
    /// </summary>
    public static Dictionary<string, SalesforceObjectHandler> BuildHandlersFromConfig(
        JsonObject config,
        string iconUrl = "")
    {
        var handlers = new Dictionary<string, SalesforceObjectHandler>();
        var children = new List<JsonObject>();

        foreach (var node in config["objectList"]!.AsArray())
        {
            var objectConfig = node!.AsObject();
            if (PyTruthy(objectConfig["parentObjectName"]))
            {
                children.Add(objectConfig);
                continue;
            }
            handlers[objectConfig["objectName"]!.GetValue<string>()] =
                new SalesforceObjectHandler(objectConfig, iconUrl: iconUrl);
        }

        foreach (var childConfig in children)
        {
            var childHandler = new SalesforceObjectHandler(childConfig, iconUrl: iconUrl);
            var parentName = childConfig["parentObjectName"]!.GetValue<string>();
            if (handlers.TryGetValue(parentName, out var parentHandler))
            {
                parentHandler.ChildHandlers.Add(childHandler);
                if (childHandler.ObjectNameAsChild is not null)
                {
                    parentHandler.ChildHandlerMap[childHandler.ObjectNameAsChild] = childHandler;
                }
            }
            handlers[childConfig["objectName"]!.GetValue<string>()] = childHandler;
        }

        return handlers;
    }

    /// <summary>Collect the full set of Graph schema property names from all handlers.</summary>
    internal static HashSet<string> BuildSchemaProperties(Dictionary<string, SalesforceObjectHandler> handlers)
    {
        var props = new HashSet<string> { "ObjectName", "url", "IconUrl", "AccountUrl" };
        props.UnionWith(MetadataColumnSchemaMapping.Values);
        props.UnionWith(new[] { AuthorsSourceProperty, SystemCreatedByUserId, SystemModifiedByUserId });
        foreach (var handler in handlers.Values)
        {
            props.UnionWith(handler.SelectedFields.Values);
        }
        return props;
    }

    // ── Python-semantics helpers (shared with the handler classes) ──────────

    /// <summary>Python truthiness for JSON values (``bool(value)``).</summary>
    internal static bool PyTruthy(JsonNode? node)
    {
        if (node is null)
        {
            return false;
        }
        switch (node.GetValueKind())
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return false;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.String:
                return node.GetValue<string>().Length > 0;
            case JsonValueKind.Number:
                return ToDouble(node) != 0.0;
            case JsonValueKind.Object:
                return node.AsObject().Count > 0;
            case JsonValueKind.Array:
                return node.AsArray().Count > 0;
            default:
                return true;
        }
    }

    /// <summary>Python ``str(value)`` for JSON values.</summary>
    internal static string PyStr(JsonNode? node)
    {
        if (node is null)
        {
            return "None";
        }
        switch (node.GetValueKind())
        {
            case JsonValueKind.Null:
                return "None";
            case JsonValueKind.String:
                return node.GetValue<string>();
            case JsonValueKind.True:
                return "True";
            case JsonValueKind.False:
                return "False";
            case JsonValueKind.Number:
                if (TryGetInteger(node, out var l))
                {
                    return l.ToString(CultureInfo.InvariantCulture);
                }
                return PyFloatRepr(ToDouble(node));
            default:
                return node.ToJsonString();
        }
    }

    /// <summary>Python ``repr(list_of_str)`` — e.g. <c>['Id', 'Name']</c>.</summary>
    internal static string PyListRepr(IEnumerable<string> values)
    {
        return "[" + string.Join(", ", values.Select(v => "'" + v + "'")) + "]";
    }

    /// <summary>Python ``repr(float)`` / ``str(float)``: shortest round-trip with ``.0`` for integral values.</summary>
    internal static string PyFloatRepr(double d)
    {
        if (double.IsNaN(d))
        {
            return "nan";
        }
        if (double.IsPositiveInfinity(d))
        {
            return "inf";
        }
        if (double.IsNegativeInfinity(d))
        {
            return "-inf";
        }
        var s = d.ToString(CultureInfo.InvariantCulture);
        if (!s.Contains('.') && !s.Contains('e') && !s.Contains('E'))
        {
            s += ".0";
        }
        return s.Replace("E", "e");
    }

    internal static bool TryGetInteger(JsonNode node, out long result)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out result))
            {
                return true;
            }
            if (value.TryGetValue<int>(out var i))
            {
                result = i;
                return true;
            }
            if (value.TryGetValue<short>(out var sh))
            {
                result = sh;
                return true;
            }
            if (value.TryGetValue<byte>(out var b))
            {
                result = b;
                return true;
            }
        }
        result = 0;
        return false;
    }

    internal static double ToDouble(JsonNode node)
    {
        if (node is JsonValue value)
        {
            var kind = node.GetValueKind();
            if (kind == JsonValueKind.Number)
            {
                if (value.TryGetValue<double>(out var d))
                {
                    return d;
                }
                if (value.TryGetValue<float>(out var f))
                {
                    return f;
                }
                if (value.TryGetValue<decimal>(out var m))
                {
                    return (double)m;
                }
                if (TryGetInteger(node, out var l))
                {
                    return l;
                }
            }
            else if (kind == JsonValueKind.String)
            {
                // Python float(str) → ValueError on bad input; FormatException maps to it.
                return double.Parse(value.GetValue<string>().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            else if (kind == JsonValueKind.True)
            {
                return 1.0;
            }
            else if (kind == JsonValueKind.False)
            {
                return 0.0;
            }
        }
        throw new FormatException($"could not convert to float: {node.ToJsonString()}");
    }

    internal static long ToInt(JsonNode node)
    {
        if (node is JsonValue value)
        {
            var kind = node.GetValueKind();
            if (kind == JsonValueKind.Number)
            {
                if (TryGetInteger(node, out var l))
                {
                    return l;
                }
                // Python int(float) truncates toward zero.
                return (long)ToDouble(node);
            }
            if (kind == JsonValueKind.String)
            {
                // Python int(str) → ValueError on "5.5"; long.Parse throws FormatException.
                return long.Parse(value.GetValue<string>().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
            }
            if (kind == JsonValueKind.True)
            {
                return 1;
            }
            if (kind == JsonValueKind.False)
            {
                return 0;
            }
        }
        throw new FormatException($"could not convert to int: {node.ToJsonString()}");
    }

    /// <summary>
    /// Parse an ISO-8601 datetime string like Python ``datetime.fromisoformat``
    /// (after ``Z`` → ``+00:00`` replacement); naive values are assumed UTC.
    /// </summary>
    internal static DateTimeOffset ParseIsoDateTime(string normalized)
    {
        // Python 3.11 fromisoformat also accepts ±HHMM offsets (Salesforce emits "+0000").
        var candidate = Regex.Replace(normalized, @"([+-])(\d{2})(\d{2})$", "$1$2:$3");
        if (!DateTimeOffset.TryParse(
                candidate,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            throw new FormatException($"Invalid isoformat string: '{normalized}'");
        }
        return parsed;
    }

    /// <summary>
    /// Format a datetime like Python ``dt.astimezone(utc).isoformat().replace("+00:00", "Z")``:
    /// seconds always present, microseconds only when non-zero, ``Z`` suffix.
    /// </summary>
    internal static string PyIsoUtcZ(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        var microseconds = utc.Ticks % TimeSpan.TicksPerSecond / 10;
        var s = utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        if (microseconds != 0)
        {
            s += "." + microseconds.ToString("D6", CultureInfo.InvariantCulture);
        }
        return s + "Z";
    }
}

public class SalesforceObjectHandler
{
    /// <summary>Initialise a handler from a single object entry in the schema config.</summary>
    public SalesforceObjectHandler(
        JsonObject sfObjectConfig,
        string iconUrl = "",
        List<SalesforceObjectHandler>? childHandlers = null)
    {
        ObjectName = sfObjectConfig["objectName"]!.GetValue<string>();
        SelectedFields = new Dictionary<string, string>();
        foreach (var pair in sfObjectConfig["selectedFields"]!.AsObject())
        {
            SelectedFields[pair.Key] = pair.Value!.GetValue<string>();
        }
        ParentObjectName = sfObjectConfig["parentObjectName"]?.GetValue<string>();
        ObjectNameAsChild = sfObjectConfig["objectNameAsChild"]?.GetValue<string>();
        IconUrl = !string.IsNullOrEmpty(iconUrl)
            ? iconUrl
            : sfObjectConfig["iconUrl"]?.GetValue<string>() ?? "";
        FlsFields = new HashSet<string>();
        if (sfObjectConfig["flsFields"] is JsonArray flsArray)
        {
            foreach (var fls in flsArray)
            {
                FlsFields.Add(fls!.GetValue<string>());
            }
        }
        // Set externally by the transformer to reflect the actual Graph schema properties
        GraphSchemaProperties = null;

        FieldDataTypes = new Dictionary<string, string>();
        if (sfObjectConfig["SfColumnTypes"] is JsonObject rawTypes)
        {
            foreach (var pair in rawTypes)
            {
                var resolved = Converter.ResolveType(pair.Value?.GetValue<string>() ?? "");
                if (resolved is not null)
                {
                    FieldDataTypes[pair.Key] = resolved;
                }
            }
        }

        FieldDataTypes["LastModifiedDate"] = "datetime";
        FieldDataTypes["CreatedDate"] = "datetime";

        ObjectFields = new Dictionary<string, List<string>>();
        foreach (var key in SelectedFields.Keys)
        {
            if (!key.Contains('.'))
            {
                continue;
            }
            var split = key.Split('.', 2);
            var parentKey = split[0];
            var childKey = split[1];
            if (!ObjectFields.TryGetValue(parentKey, out var childKeys))
            {
                childKeys = new List<string>();
                ObjectFields[parentKey] = childKeys;
            }
            childKeys.Add(childKey);
        }

        ChildHandlers = childHandlers ?? new List<SalesforceObjectHandler>();
        ChildHandlerMap = new Dictionary<string, SalesforceObjectHandler>();
        foreach (var childHandler in ChildHandlers)
        {
            if (childHandler.ObjectNameAsChild is not null)
            {
                ChildHandlerMap[childHandler.ObjectNameAsChild] = childHandler;
            }
        }

        ParentRecordLookupPaths = BuildParentRecordLookupPaths();
    }

    public string ObjectName { get; }

    public Dictionary<string, string> SelectedFields { get; }

    public string? ParentObjectName { get; }

    public string? ObjectNameAsChild { get; }

    public string IconUrl { get; }

    public HashSet<string> FlsFields { get; }

    /// <summary>Set externally by the transformer to reflect the actual Graph schema properties.</summary>
    public HashSet<string>? GraphSchemaProperties { get; set; }

    /// <summary>Set externally by the transformer (Python: dynamic ``graph_schema_property_types`` attribute).</summary>
    public Dictionary<string, string?>? GraphSchemaPropertyTypes { get; set; }

    public Dictionary<string, string> FieldDataTypes { get; }

    public Dictionary<string, List<string>> ObjectFields { get; }

    public List<SalesforceObjectHandler> ChildHandlers { get; }

    internal Dictionary<string, SalesforceObjectHandler> ChildHandlerMap { get; }

    public string[] ParentRecordLookupPaths { get; }

    /// <summary>Return the parent record ID from <paramref name="record"/>, or <c>null</c> if not found.</summary>
    public string? GetParentRecordId(JsonObject record)
    {
        foreach (var fieldPath in ParentRecordLookupPaths)
        {
            var value = GetRecordValue(record, fieldPath);
            if (Converter.PyTruthy(value))
            {
                return Converter.PyStr(value);
            }
        }
        return null;
    }

    /// <summary>Build an ordered array of field paths used to locate the parent record ID.</summary>
    private string[] BuildParentRecordLookupPaths()
    {
        if (string.IsNullOrEmpty(ParentObjectName))
        {
            return Array.Empty<string>();
        }

        var expectedPropertyName = $"{ParentObjectName}Id";
        var lookupPaths = new List<string>();

        foreach (var pair in SelectedFields)
        {
            if (pair.Value == expectedPropertyName && !lookupPaths.Contains(pair.Key))
            {
                lookupPaths.Add(pair.Key);
            }
        }

        foreach (var fallbackPath in new[] { expectedPropertyName, $"{ParentObjectName}.Id" })
        {
            if (!lookupPaths.Contains(fallbackPath))
            {
                lookupPaths.Add(fallbackPath);
            }
        }

        return lookupPaths.ToArray();
    }

    /// <summary>Retrieve a value from <paramref name="record"/> using a dot-separated <paramref name="fieldPath"/>.</summary>
    private static JsonNode? GetRecordValue(JsonObject record, string fieldPath)
    {
        if (record.ContainsKey(fieldPath))
        {
            return record[fieldPath];
        }

        JsonNode? current = record;
        foreach (var part in fieldPath.Split('.'))
        {
            if (current is not JsonObject currentObject)
            {
                return null;
            }
            current = currentObject.TryGetPropertyValue(part, out var next) ? next : null;
            if (current is null)
            {
                return null;
            }
        }
        return current;
    }

    /// <summary>Convert a Salesforce query result into a list of Graph external-item dicts.</summary>
    public List<JsonObject> ConstructIngestionItems(
        JsonObject sfQueryResult,
        string instanceUrl,
        HashSet<string> schemaProperties)
    {
        var records = sfQueryResult["records"] as JsonArray ?? new JsonArray();
        var allItems = new List<JsonObject>();
        foreach (var record in records)
        {
            var items = ConstructItemsForRecordAndChildren(
                record!.AsObject(),
                instanceUrl,
                schemaProperties);
            if (items is not null && items.Count > 0)
            {
                allItems.AddRange(items);
            }
        }
        return allItems;
    }

    /// <summary>Build ingestion items for a single record and its inline child records.</summary>
    private List<JsonObject>? ConstructItemsForRecordAndChildren(
        JsonObject record,
        string instanceUrl,
        HashSet<string> schemaProperties)
    {
        var recordId = record.TryGetPropertyValue("Id", out var idNode) ? idNode : null;
        if (!Converter.PyTruthy(recordId))
        {
            Converter.Logger.Warning(
                $"[{ObjectName}] Skipping record with missing/null Id — record keys: " +
                Converter.PyListRepr(record.Select(pair => pair.Key)));
            return null;
        }

        var childItems = new List<JsonObject>();
        foreach (var pair in record)
        {
            if (ChildHandlerMap.TryGetValue(pair.Key, out var childHandler) && pair.Value is JsonObject childResult)
            {
                childItems.AddRange(
                    childHandler.ConstructIngestionItems(childResult, instanceUrl, schemaProperties));
            }
        }

        if (record.TryGetPropertyValue("IsDeleted", out var isDeleted)
            && isDeleted is not null
            && isDeleted.GetValueKind() == JsonValueKind.True)
        {
            childItems.Add(new DeletedItem(Converter.PyStr(recordId)).ToDict());
            return childItems;
        }

        var item = new SearchableItem(Converter.PyStr(recordId));
        item.Content = BuildItemPropertiesAndContent(
            record,
            instanceUrl,
            item.Properties,
            schemaProperties);
        childItems.Add(item.ToDict());
        return childItems;
    }

    /// <summary>
    /// Populate <paramref name="props"/> from the Salesforce <paramref name="record"/> and return a <see cref="Content"/> object.
    ///
    /// Maps selected fields and metadata columns to their Graph schema
    /// property names, performs type coercion, and collects remaining fields
    /// into the full-text content body.
    /// </summary>
    private Content BuildItemPropertiesAndContent(
        JsonObject record,
        string instanceUrl,
        JsonObject props,
        HashSet<string> schemaProperties)
    {
        // Use the real Graph schema properties if available; fall back to converter's schema_properties
        var graphProps = GraphSchemaProperties ?? schemaProperties;

        props["ObjectName"] = ObjectName;
        props["url"] = $"{instanceUrl}{Converter.PyStr(record["Id"])}";
        if (graphProps.Contains("IconUrl"))
        {
            props["IconUrl"] = IconUrl;
        }

        var content = new Content();

        // Collect field mapping trace for debug logging
        // Use the real Graph schema properties if available; fall back to converter's schema_properties
        var fieldMapping = new List<(string SfField, string GraphProp, string Source, bool InSchema)>();
        fieldMapping.Add(("(object_name)", "ObjectName", "synthetic", graphProps.Contains("ObjectName")));
        fieldMapping.Add(("(instance_url + Id)", "url", "synthetic", graphProps.Contains("url")));
        if (graphProps.Contains("IconUrl"))
        {
            fieldMapping.Add(("(icon_url)", "IconUrl", "synthetic", graphProps.Contains("IconUrl")));
        }

        foreach (var pair in record)
        {
            var fieldKey = pair.Key;
            var fieldValue = pair.Value;
            if (fieldKey == "attributes")
            {
                continue;
            }

            if (SelectedFields.TryGetValue(fieldKey, out var propertyName))
            {
                if (graphProps.Contains(propertyName))
                {
                    AddSchemaPropertyForField(
                        props,
                        record,
                        fieldKey,
                        propertyName,
                        instanceUrl);
                    fieldMapping.Add((fieldKey, propertyName, "selectedFields", true));
                    if (fieldKey == Converter.ContentFieldName || propertyName == Converter.ContentFieldName)
                    {
                        var rawValue = record.TryGetPropertyValue(fieldKey, out var rawNode) ? rawNode : null;
                        var rawString = rawValue is not null && rawValue.GetValueKind() == JsonValueKind.String
                            ? rawValue.GetValue<string>()
                            : null;
                        content = new Content(!string.IsNullOrEmpty(rawString) ? rawString : "");
                    }
                }
                else
                {
                    fieldMapping.Add((fieldKey, propertyName, "selectedFields → content", false));
                }
            }
            else if (Converter.MetadataColumnSchemaMapping.TryGetValue(fieldKey, out var metadataPropertyName))
            {
                if (graphProps.Contains(metadataPropertyName))
                {
                    AddSchemaPropertyForField(
                        props,
                        record,
                        fieldKey,
                        metadataPropertyName,
                        instanceUrl);
                    fieldMapping.Add((fieldKey, metadataPropertyName, "metadata", true));
                }
                else
                {
                    fieldMapping.Add((fieldKey, metadataPropertyName, "metadata → content", false));
                }
            }
            else if (fieldValue is JsonObject)
            {
                if (ObjectFields.TryGetValue(fieldKey, out var objectKeys))
                {
                    AddSchemaPropertyForObjectField(
                        props,
                        record,
                        fieldKey,
                        objectKeys,
                        SelectedFields,
                        instanceUrl);
                }
                else if (Converter.MetadataObjectColumnSchemaMapping.TryGetValue(fieldKey, out var metadataKeys))
                {
                    AddSchemaPropertyForObjectField(
                        props,
                        record,
                        fieldKey,
                        metadataKeys,
                        Converter.MetadataColumnSchemaMapping,
                        instanceUrl);
                }
            }
        }

        foreach (var flsField in FlsFields)
        {
            props[flsField] = null;
        }

        if (props.ContainsKey("AccountId"))
        {
            props["AccountUrl"] = $"{instanceUrl}/{Converter.PyStr(props["AccountId"])}";
        }

        var authors = GetAuthorsSourceProperty(props, graphProps);
        if (authors is not null && authors.Count > 0)
        {
            var authorsArray = new JsonArray();
            foreach (var author in authors)
            {
                authorsArray.Add(author);
            }
            props[Converter.AuthorsSourceProperty] = authorsArray;
        }

        if (graphProps.Contains(Converter.SystemCreatedByUserId) && record.ContainsKey("CreatedById"))
        {
            props[Converter.SystemCreatedByUserId] = Converter.PyStr(record["CreatedById"]);
        }

        if (graphProps.Contains(Converter.SystemModifiedByUserId) && record.ContainsKey("LastModifiedById"))
        {
            props[Converter.SystemModifiedByUserId] = Converter.PyStr(record["LastModifiedById"]);
        }

        var contentParts = new List<string>();
        if (!string.IsNullOrEmpty(content.ParsedData))
        {
            contentParts.Add(content.ParsedData);
        }

        foreach (var pair in record)
        {
            var fieldKey = pair.Key;
            var fieldValue = pair.Value;
            if (fieldKey is "attributes" or "Id" or "url" or "objectType")
            {
                continue;
            }

            var fieldInSchema = false;
            if (SelectedFields.TryGetValue(fieldKey, out var propertyName))
            {
                if (graphProps.Contains(propertyName))
                {
                    fieldInSchema = true;
                }
            }
            else if (Converter.MetadataColumnSchemaMapping.TryGetValue(fieldKey, out var metadataPropertyName))
            {
                if (graphProps.Contains(metadataPropertyName))
                {
                    fieldInSchema = true;
                }
            }

            if (fieldInSchema || fieldValue is null)
            {
                continue;
            }

            if (fieldValue is JsonObject nestedObject)
            {
                foreach (var subPair in nestedObject)
                {
                    if (subPair.Key != "attributes"
                        && subPair.Value is not null
                        && subPair.Value is not JsonObject
                        && subPair.Value is not JsonArray)
                    {
                        contentParts.Add($"{fieldKey}.{subPair.Key}: {Converter.PyStr(subPair.Value)}");
                        fieldMapping.Add(($"{fieldKey}.{subPair.Key}", "content.value", "unmapped (nested)", false));
                    }
                }
            }
            else if (fieldValue is not JsonArray)
            {
                contentParts.Add($"{fieldKey}: {Converter.PyStr(fieldValue)}");
                fieldMapping.Add((fieldKey, "content.value", "unmapped", false));
            }
        }

        if (contentParts.Count > 0)
        {
            content.ParsedData = string.Join(", ", contentParts);
        }

        // Emit the field-mapping table at DEBUG level (visible with --verbose or in log file)
        if (fieldMapping.Count > 0)
        {
            var recordId = record.TryGetPropertyValue("Id", out var idNode) && idNode is not null
                ? Converter.PyStr(idNode)
                : "?";
            var header = $"FIELD MAPPING TABLE — {ObjectName}/{recordId}";
            var lines = new List<string>
            {
                string.Format("  {0,-45} {1,-35} {2,-12} {3}", "SF Field", "Graph Target", "In Schema", "Source"),
                string.Format("  {0} {1} {2} {3}", new string('─', 45), new string('─', 35), new string('─', 12), new string('─', 25)),
            };
            foreach (var (sfField, graphProp, source, inSchema) in fieldMapping)
            {
                var schemaFlag = inSchema ? "✓" : "✗";
                lines.Add(string.Format("  {0,-45} {1,-35} {2,-12} {3}", sfField, graphProp, schemaFlag, source));
            }
            Converter.Logger.Debug($"{header}\n{string.Join("\n", lines)}");
        }

        return content;
    }

    /// <summary>Add a single scalar or address field to <paramref name="props"/> with type coercion.</summary>
    private void AddSchemaPropertyForField(
        JsonObject props,
        JsonObject record,
        string fieldKey,
        string propertyName,
        string instanceUrl)
    {
        var value = record.TryGetPropertyValue(fieldKey, out var node) ? node : null;

        if (value is JsonObject addressObject && addressObject.ContainsKey("street") && addressObject.Count > 1)
        {
            props[propertyName] = SerializeAddressObject(addressObject);
            return;
        }

        if (FieldDataTypes.TryGetValue(fieldKey, out var typeTag))
        {
            try
            {
                if (value is not null)
                {
                    props[propertyName] = Converter.ConvertValue(value, typeTag);
                }
                return;
            }
            catch (Exception error) when (
                error is FormatException or OverflowException or InvalidOperationException or InvalidCastException)
            {
                Converter.Logger.Error(
                    $"Could not parse {ObjectName}.{fieldKey} for record "
                    + $"{record["Id"]?.ToString() ?? "(no Id)"}: {error.Message}");
                props[propertyName] = typeTag switch
                {
                    "bool" => JsonValue.Create(false),
                    "float" => JsonValue.Create(0.0),
                    "int" => JsonValue.Create(0),
                    "datetime" => JsonValue.Create(""),
                    "str" => JsonValue.Create(""),
                    _ => JsonValue.Create(""),
                };
                return;
            }
        }

        try
        {
            var fieldData = value is not null ? Converter.PyStr(value) : null;
            if (fieldData is not null
                && fieldKey.ToLowerInvariant().Contains("id")
                && propertyName.ToLowerInvariant().Contains("url"))
            {
                props[propertyName] = $"{instanceUrl}/{fieldData}";
            }
            else
            {
                props[propertyName] = fieldData;
            }
        }
        catch (Exception error)  // pragma: no cover - defensive fallback
        {
            Converter.Logger.Error($"Could not parse {ObjectName}.{fieldKey}: {error.Message}");
            props[propertyName] = "";
        }
    }

    /// <summary>Extract sub-fields from a nested relationship object into <paramref name="props"/>.</summary>
    private void AddSchemaPropertyForObjectField(
        JsonObject props,
        JsonObject record,
        string fieldKey,
        List<string> keys,
        IReadOnlyDictionary<string, string> schemaMapping,
        string instanceUrl = "")
    {
        if (!record.TryGetPropertyValue(fieldKey, out var parentNode) || parentNode is not JsonObject parentObject)
        {
            return;
        }

        foreach (var key in keys)
        {
            var lookupKey = $"{fieldKey}.{key}";
            if (!schemaMapping.TryGetValue(lookupKey, out var propertyName))
            {
                continue;
            }

            try
            {
                JsonNode? nestedValue;
                if (key.Contains('.'))
                {
                    nestedValue = parentObject;
                    foreach (var part in key.Split('.'))
                    {
                        nestedValue = nestedValue is JsonObject nestedObject
                            ? (nestedObject.TryGetPropertyValue(part, out var next) ? next : null)
                            : null;
                        if (nestedValue is null)
                        {
                            break;
                        }
                    }
                }
                else
                {
                    nestedValue = parentObject.TryGetPropertyValue(key, out var direct) ? direct : null;
                }

                if (nestedValue is not null)
                {
                    var fieldData = Converter.PyStr(nestedValue);
                    if (!string.IsNullOrEmpty(instanceUrl)
                        && key.ToLowerInvariant().Contains("id")
                        && propertyName.ToLowerInvariant().Contains("url"))
                    {
                        props[propertyName] = $"{instanceUrl}/{fieldData}";
                    }
                    else
                    {
                        props[propertyName] = fieldData;
                    }
                }
            }
            catch (Exception error)  // pragma: no cover - defensive fallback
            {
                Converter.Logger.Error(
                    $"Could not parse {ObjectName}.{fieldKey} for record "
                    + $"{record["Id"]?.ToString() ?? "(no Id)"}: {error.Message}");
                props[propertyName] = "";
            }
        }
    }

    /// <summary>Serialise a Salesforce compound address dict into a single string.</summary>
    private static string SerializeAddressObject(JsonObject token)
    {
        var parts = new List<string>();
        try
        {
            if (Converter.PyTruthy(token["street"]))
            {
                parts.Add(Converter.PyStr(token["street"]));
            }
            if (Converter.PyTruthy(token["city"]))
            {
                parts.Add($", {Converter.PyStr(token["city"])}");
            }
            if (Converter.PyTruthy(token["state"]))
            {
                parts.Add($", {Converter.PyStr(token["state"])}");
            }
            if (Converter.PyTruthy(token["postalCode"]))
            {
                parts.Add($" - {Converter.PyStr(token["postalCode"])}");
            }
            if (Converter.PyTruthy(token["country"]))
            {
                parts.Add($", {Converter.PyStr(token["country"])}");
            }
        }
        catch (Exception error)  // pragma: no cover - defensive fallback
        {
            Converter.Logger.Error($"Could not parse address: {error.Message}");
            return "";
        }
        return string.Join("", parts);
    }

    /// <summary>Return a deduplicated list of author names from CreatedBy/LastModifiedBy.</summary>
    private static List<string>? GetAuthorsSourceProperty(
        JsonObject props,
        HashSet<string> schemaProperties)
    {
        if (!schemaProperties.Contains(Converter.AuthorsSourceProperty))
        {
            return null;
        }

        var authors = new List<string>();
        var createdBy = props.TryGetPropertyValue(Converter.CreatedBySourceProperty, out var createdNode)
            ? createdNode
            : null;
        if (Converter.PyTruthy(createdBy))
        {
            var name = Converter.PyStr(createdBy);
            if (!authors.Contains(name))
            {
                authors.Add(name);
            }
        }

        var lastModifiedBy = props.TryGetPropertyValue(Converter.LastModifiedBySourceProperty, out var modifiedNode)
            ? modifiedNode
            : null;
        if (Converter.PyTruthy(lastModifiedBy))
        {
            var name = Converter.PyStr(lastModifiedBy);
            if (!authors.Contains(name))
            {
                authors.Add(name);
            }
        }

        return authors.Count > 0 ? authors : null;
    }
}

public class SalesforceConverter
{
    private readonly string _instanceUrl;
    private readonly Dictionary<string, SalesforceObjectHandler> _handlers;
    private readonly HashSet<string> _schemaProperties;

    /// <summary>Initialise the converter with a Salesforce instance URL and schema config.</summary>
    public SalesforceConverter(
        string instanceUrl,
        JsonObject? config = null,
        HashSet<string>? schemaProperties = null,
        string iconUrl = "")
    {
        _instanceUrl = instanceUrl;
        var effectiveConfig = config ?? Converter.LoadConverterConfig();
        _handlers = Converter.BuildHandlersFromConfig(effectiveConfig, iconUrl: iconUrl);
        _schemaProperties = schemaProperties ?? Converter.BuildSchemaProperties(_handlers);
    }

    /// <summary>The registered handlers keyed by object name (Python: private ``_handlers``,
    /// accessed directly by ``SalesforceItemTransformer``).</summary>
    public Dictionary<string, SalesforceObjectHandler> Handlers => _handlers;

    /// <summary>List all registered Salesforce object names.</summary>
    // virtual so tests can substitute a fake converter (Python tests mock SalesforceConverter).
    public virtual List<string> ObjectNames => _handlers.Keys.ToList();

    /// <summary>List object names that are top-level parents (not children).</summary>
    public List<string> ParentObjectNames =>
        _handlers
            .Where(pair => pair.Value.ParentObjectName is null)
            .Select(pair => pair.Key)
            .ToList();

    /// <summary>Return a copy of the Graph schema property names.</summary>
    public HashSet<string> SchemaProperties => new HashSet<string>(_schemaProperties);

    /// <summary>Return the handler for <paramref name="objectName"/>, or <c>null</c> if not registered.</summary>
    // virtual so tests can substitute a fake converter (Python tests mock SalesforceConverter).
    public virtual SalesforceObjectHandler? GetHandler(string objectName)
    {
        return _handlers.TryGetValue(objectName, out var handler) ? handler : null;
    }

    /// <summary>
    /// Convert a Salesforce query result into Graph external-item dicts.
    ///
    /// If <paramref name="objectName"/> is <c>null</c> it is inferred from the
    /// first record's ``attributes.type``.
    /// </summary>
    // virtual so tests can substitute a fake converter (Python tests mock SalesforceConverter).
    public virtual List<JsonObject> Convert(
        JsonObject sfQueryResult,
        string? objectName = null)
    {
        var effectiveObjectName = !string.IsNullOrEmpty(objectName)
            ? objectName
            : InferObjectName(sfQueryResult);
        if (!_handlers.TryGetValue(effectiveObjectName, out var handler))
        {
            throw new ArgumentException(
                $"Unknown object '{effectiveObjectName}'. Available: {Converter.PyListRepr(ObjectNames)}");
        }
        return handler.ConstructIngestionItems(
            sfQueryResult,
            _instanceUrl,
            _schemaProperties);
    }

    /// <summary>Infer the Salesforce object name from the first record's attributes.</summary>
    private static string InferObjectName(JsonObject sfQueryResult)
    {
        var records = sfQueryResult["records"] as JsonArray ?? new JsonArray();
        if (records.Count == 0)
        {
            throw new ArgumentException("Cannot infer object_name from an empty records list");
        }
        var attributes = records[0] is JsonObject firstRecord
            && firstRecord.TryGetPropertyValue("attributes", out var attributesNode)
                ? attributesNode
                : null;
        if (attributes is not JsonObject attributesObject || !attributesObject.ContainsKey("type"))
        {
            throw new ArgumentException("Cannot infer object_name: first record has no attributes.type");
        }
        return Converter.PyStr(attributesObject["type"]);
    }
}
