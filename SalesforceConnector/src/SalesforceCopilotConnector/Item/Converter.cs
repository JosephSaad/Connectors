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
using SalesforceCopilotConnector.AclEngine;
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

    /// <summary>
    /// Salesforce fields that feed a <c>__System.*</c> Graph property in addition to
    /// their ordinary mapped property. Part of the ALIAS CLOSURE (see
    /// <c>SalesforceObjectHandler.IsFlsRestricted</c>): a drop spelled as ANY name a
    /// field can produce must gate EVERY name it produces. Before this map,
    /// <c>__System.User.CreatedBy.Id</c> gated the system column but left
    /// <c>CreatedByUrl</c> — which embeds the same user Id — in the payload.
    /// </summary>
    public static readonly Dictionary<string, string> SystemPropertyByField = new(StringComparer.Ordinal)
    {
        ["CreatedById"] = SystemCreatedByUserId,
        ["LastModifiedById"] = SystemModifiedByUserId,
    };

    /// <summary>
    /// The camelCase slot names Salesforce uses inside a COMPOUND value — address
    /// compounds (<c>BillingAddress</c>, <c>MailingAddress</c>, …) and geolocation
    /// compounds. Salesforce writes relationship sub-objects with API field names
    /// (PascalCase or <c>__c</c>-suffixed); only compounds use these exact lowercase
    /// keys, which is what makes shape detection reliable without inference.
    /// </summary>
    internal static readonly HashSet<string> CompoundSlotNames = new(StringComparer.Ordinal)
    {
        "street", "city", "state", "postalCode", "country",
        "stateCode", "countryCode", "geocodeAccuracy", "latitude", "longitude",
    };

    /// <summary>
    /// The canonical slot order an assembled address is rendered in. Matches the
    /// order <see cref="SalesforceObjectHandler.SerializeAddressObject"/> emits, so
    /// an address assembled from components is byte-identical to the same address
    /// serialised from the compound when no component is restricted.
    /// </summary>
    internal static readonly string[] AddressSlotOrder = { "street", "city", "state", "postalCode", "country" };

    /// <summary>
    /// True when <paramref name="value"/> is the JSON shape of a Salesforce COMPOUND
    /// value rather than a relationship sub-object.
    ///
    /// <para>A compound carries no <c>FieldPermissions</c> rows of its own — FLS lives
    /// on its COMPONENTS — so a compound value can never be FLS-evaluated and must
    /// never be indexed. See docs/FLS.md.</para>
    /// </summary>
    internal static bool IsCompoundValue(JsonObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        foreach (var pair in value)
        {
            if (CompoundSlotNames.Contains(pair.Key) && pair.Value is not JsonObject and not JsonArray)
            {
                return true;
            }
        }
        return false;
    }

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
        FlsFields = new HashSet<string>(FlsNameComparer);
        if (sfObjectConfig["flsFields"] is JsonArray flsArray)
        {
            foreach (var fls in flsArray)
            {
                FlsFields.Add(fls!.GetValue<string>());
            }
        }
        RuntimeFlsFields = new HashSet<string>(FlsNameComparer);
        _effectiveFlsFields = new HashSet<string>(FlsFields, FlsNameComparer);
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

        // Operator-declared relationship → target object map, used by FLS to find
        // whose FieldPermissions govern a dotted "Relationship.Field" candidate.
        // Undeclared relationships are NOT guessed — FlsPolicy fails them closed.
        RelationshipObjects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (sfObjectConfig["relationshipObjects"] is JsonObject relationshipMap)
        {
            foreach (var pair in relationshipMap)
            {
                if (pair.Value?.GetValue<string>() is { Length: > 0 } target)
                {
                    RelationshipObjects[pair.Key] = target;
                }
            }
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

        AddressGroups = BuildAddressGroups(sfObjectConfig);
        AddressComponentFields = new HashSet<string>(
            AddressGroups.SelectMany(g => g.Components.Values), StringComparer.OrdinalIgnoreCase);

        ParentRecordLookupPaths = BuildParentRecordLookupPaths();

        AuditFlsFieldSpellings();
    }

    /// <summary>
    /// The comparer used for EVERY field-level-security name comparison.
    ///
    /// <para>Deliberately case-INSENSITIVE. The <c>flsFields</c> list is hand-typed by
    /// operators and Salesforce's own API treats field names case-insensitively, so an
    /// ordinal comparison turned a casing typo (<c>billingaddress</c>) into a silent
    /// no-op that leaked the field in full while looking, in config, exactly like a
    /// drop. A casing mismatch now WORKS, and
    /// <see cref="AuditFlsFieldSpellings"/> additionally warns so the config can be
    /// corrected. Over-matching is impossible: Salesforce forbids two fields on one
    /// object whose API names differ only by case.</para>
    /// </summary>
    internal static readonly StringComparer FlsNameComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// One declared compound-address assembly: the display name it is emitted under,
    /// the Graph property it targets, and the ordinary component fields it is built
    /// from. Components are SELECTed individually, so each one carries its own
    /// <c>FieldPermissions</c> evidence and is gated by literal name.
    /// </summary>
    public sealed record AddressGroup(
        string Name,
        string PropertyName,
        IReadOnlyDictionary<string, string> Components);

    private static List<AddressGroup> BuildAddressGroups(JsonObject sfObjectConfig)
    {
        var groups = new List<AddressGroup>();
        if (sfObjectConfig["addressFields"] is not JsonObject declared)
        {
            return groups;
        }

        foreach (var pair in declared)
        {
            if (pair.Value is not JsonObject spec)
            {
                continue;
            }
            var propertyName = spec["property"]?.GetValue<string>();
            if (string.IsNullOrEmpty(propertyName) || spec["components"] is not JsonObject componentMap)
            {
                continue;
            }

            var components = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var slot in Converter.AddressSlotOrder)
            {
                if (componentMap[slot]?.GetValue<string>() is { Length: > 0 } sfField)
                {
                    components[slot] = sfField;
                }
            }
            if (components.Count > 0)
            {
                groups.Add(new AddressGroup(pair.Key, propertyName!, components));
            }
        }
        return groups;
    }

    /// <summary>
    /// Warn at startup about <c>flsFields</c> entries whose spelling does not exactly
    /// match anything this object can emit.
    ///
    /// <para>The entry still takes effect — matching is case-insensitive (see
    /// <see cref="FlsNameComparer"/>) — but an operator who typed
    /// <c>billingaddress</c> or a field this object does not select deserves to be
    /// told rather than to discover it in an audit. Silence was the defect.</para>
    /// </summary>
    private void AuditFlsFieldSpellings()
    {
        if (FlsFields.Count == 0)
        {
            return;
        }

        var exact = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in SelectedFields)
        {
            exact.Add(pair.Key);
            exact.Add(pair.Value);
        }
        foreach (var pair in Converter.MetadataColumnSchemaMapping)
        {
            exact.Add(pair.Key);
            exact.Add(pair.Value);
        }
        exact.Add(Converter.SystemCreatedByUserId);
        exact.Add(Converter.SystemModifiedByUserId);
        var insensitive = new HashSet<string>(exact, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in FlsFields)
        {
            if (exact.Contains(entry))
            {
                continue;
            }
            if (insensitive.Contains(entry))
            {
                var canonical = exact.First(e => StringComparer.OrdinalIgnoreCase.Equals(e, entry));
                Converter.Logger.Warning(
                    $"[FLS] [{ObjectName}] flsFields entry '{entry}' does not match the declared "
                    + $"spelling '{canonical}'. It is being applied case-insensitively; correct "
                    + "config/schema.json to remove this warning.");
            }
            else
            {
                Converter.Logger.Warning(
                    $"[FLS] [{ObjectName}] flsFields entry '{entry}' matches no selected field, "
                    + "no mapped Graph property and no metadata column on this object. It will "
                    + "drop nothing — check for a typo.");
            }
        }
    }

    public string ObjectName { get; }

    /// <summary>
    /// Declared compound-address assemblies for this object (config <c>addressFields</c>).
    /// Empty for objects with no address.
    /// </summary>
    public IReadOnlyList<AddressGroup> AddressGroups { get; }

    /// <summary>
    /// Every Salesforce field consumed by an <see cref="AddressGroups"/> entry. These
    /// are SELECTed and FLS-evaluated individually, then assembled — so they are
    /// skipped by both ordinary assembly loops to avoid emitting the address twice.
    /// </summary>
    public HashSet<string> AddressComponentFields { get; }

    public Dictionary<string, string> SelectedFields { get; }

    public string? ParentObjectName { get; }

    public string? ObjectNameAsChild { get; }

    public string IconUrl { get; }

    /// <summary>
    /// The operator's explicit per-object <c>flsFields</c> list from config/schema.json.
    /// Entries may name either the Salesforce field or the Graph property it maps to.
    /// </summary>
    public HashSet<string> FlsFields { get; }

    /// <summary>
    /// Field restrictions discovered from Salesforce at crawl time (WP-SF-2), set via
    /// <see cref="ApplyFlsDrops"/>. Kept separate from <see cref="FlsFields"/> so the
    /// fetched set can never silently shrink what an operator listed explicitly.
    /// </summary>
    public HashSet<string> RuntimeFlsFields { get; }

    /// <summary>
    /// Cached union of <see cref="FlsFields"/> and <see cref="RuntimeFlsFields"/>.
    /// Recomputed by <see cref="ApplyFlsDrops"/>; the constructor seeds it from the
    /// config list. (Direct mutation of <see cref="FlsFields"/> after construction is
    /// not a supported path and would not be reflected here.)
    /// </summary>
    private HashSet<string> _effectiveFlsFields;

    /// <summary>
    /// The fields this handler will drop: the operator's manual list UNIONED with
    /// the permissions fetched from Salesforce. Never a subset of either input.
    /// </summary>
    public IReadOnlyCollection<string> EffectiveFlsFields => _effectiveFlsFields;

    /// <summary>
    /// Record field restrictions discovered from Salesforce field-level security.
    ///
    /// UNIONS with the existing set — repeated calls accumulate and an empty set is
    /// a no-op, so a crawl that fetches "everything is readable" can never clear an
    /// operator's explicit <c>flsFields</c> entry.
    /// </summary>
    public void ApplyFlsDrops(IEnumerable<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        foreach (var field in fields)
        {
            if (!string.IsNullOrEmpty(field))
            {
                RuntimeFlsFields.Add(field);
            }
        }
        var effective = new HashSet<string>(FlsFields, FlsNameComparer);
        effective.UnionWith(RuntimeFlsFields);
        _effectiveFlsFields = effective;
    }

    /// <summary>
    /// True when <paramref name="fieldKey"/> (or the Graph property it maps to) must
    /// be withheld from the index under field-level security.
    ///
    /// <para>This is called from BOTH assembly loops in
    /// <see cref="BuildItemPropertiesAndContent"/>. Gating only one of them leaks:
    /// the value would vanish from the Graph property but survive verbatim in the
    /// searchable content body (or vice versa).</para>
    ///
    /// <para>Both spellings are accepted because the pre-WP-SF-2 <c>flsFields</c>
    /// precedent keyed on the GRAPH PROPERTY name (<c>props[flsField] = null</c>)
    /// while the fetched permissions are keyed on the SALESFORCE FIELD name.</para>
    ///
    /// <para>ALIAS CLOSURE. One Salesforce field can surface under several names: its
    /// own API name, the Graph property <c>selectedFields</c> maps it to, the property
    /// the metadata mapping gives it, and (for the two user-Id columns) a
    /// <c>__System.*</c> property. A drop spelled as ANY of those must gate ALL of
    /// them, or a restriction gates one output and the same value escapes through
    /// another — which is exactly how <c>__System.User.CreatedBy.Id</c> gated the
    /// system column while <c>CreatedByUrl</c> shipped the very same user Id. The
    /// closure is computed from DECLARED maps only; nothing is inferred from name
    /// shape.</para>
    ///
    /// <para>COMPOUND FIELDS. Not handled here, and deliberately so: a compound
    /// carries no <c>FieldPermissions</c> rows, so no name match on it can ever be
    /// evidence-backed. Compounds are instead never indexed at all — see
    /// <see cref="IsUnindexableCompound"/> — and addresses are assembled from
    /// individually-selected, individually-gated components. See docs/FLS.md.</para>
    /// </summary>
    private bool IsFlsRestricted(string fieldKey, string? propertyName)
    {
        // Fast path first: the overwhelmingly common case is no FLS at all, and this
        // runs once per field per record. Only then pay for the env-var read.
        if (_effectiveFlsFields.Count == 0)
        {
            return false;
        }
        if (!FlsSettings.Enforcement)
        {
            return false;
        }
        if (_effectiveFlsFields.Contains(fieldKey))
        {
            return true;
        }
        if (propertyName is not null && _effectiveFlsFields.Contains(propertyName))
        {
            return true;
        }
        foreach (var alias in AliasesOf(fieldKey))
        {
            if (_effectiveFlsFields.Contains(alias))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Every DECLARED name <paramref name="fieldKey"/> can be emitted under, other
    /// than itself. Sourced only from configured maps — never from name shape.
    /// </summary>
    private IEnumerable<string> AliasesOf(string fieldKey)
    {
        if (SelectedFields.TryGetValue(fieldKey, out var selected))
        {
            yield return selected;
        }
        if (Converter.MetadataColumnSchemaMapping.TryGetValue(fieldKey, out var metadata))
        {
            yield return metadata;
        }
        if (Converter.SystemPropertyByField.TryGetValue(fieldKey, out var systemProperty))
        {
            yield return systemProperty;
        }
    }

    /// <summary>
    /// True when <paramref name="value"/> is a COMPOUND value that must not reach the
    /// index by any route.
    ///
    /// <para>THE STRUCTURAL RULE. Salesforce puts <c>FieldPermissions</c> rows on a
    /// compound's COMPONENTS, never on the compound itself. A compound value is
    /// therefore un-evaluable: no amount of name matching can produce evidence about
    /// it, and indexing it republishes every component regardless of that component's
    /// FLS. So the connector does not index compounds at all — not as a Graph
    /// property, not flattened into the searchable body. An address reaches the index
    /// only via <c>addressFields</c>, assembled from components that were each
    /// selected and each gated by literal name.</para>
    ///
    /// <para>Declared relationship sub-objects (<c>CreatedBy</c>, <c>Parent</c>, …) are
    /// exempt: those are real objects whose sub-fields have their own permissions and
    /// their own gates. Everything else that arrives compound-shaped fails CLOSED,
    /// which is what makes this cover shapes nobody has enumerated — custom address
    /// compounds, geolocation compounds, Person-Account address compounds, and any
    /// object added to the config later.</para>
    ///
    /// <para>Suppression is gated on <see cref="FlsSettings.Enforcement"/> so that
    /// <c>FLS_ENFORCEMENT=off</c> remains the documented escape hatch to the old
    /// behaviour.</para>
    /// </summary>
    private bool IsUnindexableCompound(string fieldKey, JsonNode? value)
    {
        if (!FlsSettings.Enforcement || value is not JsonObject candidate)
        {
            return false;
        }
        if (ObjectFields.ContainsKey(fieldKey)
            || RelationshipObjects.ContainsKey(fieldKey)
            || Converter.MetadataObjectColumnSchemaMapping.ContainsKey(fieldKey)
            || ChildHandlerMap.ContainsKey(fieldKey))
        {
            return false;
        }
        return Converter.IsCompoundValue(candidate);
    }

    /// <summary>
    /// FLS gate for the two <c>__System.User.*.Id</c> columns, which are written
    /// outside both assembly loops and so miss the gates in them.
    ///
    /// <para>A drop on these can legitimately be spelled three ways: the Salesforce
    /// field (<c>CreatedById</c>), the ordinary metadata property it also feeds
    /// (<c>CreatedByUrl</c>), or the system property itself
    /// (<c>__System.User.CreatedBy.Id</c>). All three must gate — and, crucially, all
    /// three must gate BOTH outputs. Until the alias closure landed in
    /// <see cref="IsFlsRestricted"/> the invariant held only one way: the
    /// <c>__System.*</c> spelling suppressed the system column and left
    /// <c>CreatedByUrl</c> publishing the identical user Id. The closure now makes the
    /// three spellings genuinely interchangeable, so this method is a thin alias of
    /// the ordinary gate rather than a special case.</para>
    /// </summary>
    private bool IsSystemUserColumnRestricted(string fieldKey, string systemProperty) =>
        IsFlsRestricted(fieldKey, systemProperty);

    /// <summary>Resolve the Graph property a Salesforce field maps to, if any.</summary>
    private string? MappedPropertyName(string fieldKey)
    {
        if (SelectedFields.TryGetValue(fieldKey, out var selected))
        {
            return selected;
        }
        return Converter.MetadataColumnSchemaMapping.GetValueOrDefault(fieldKey);
    }

    /// <summary>Set externally by the transformer to reflect the actual Graph schema properties.</summary>
    public HashSet<string>? GraphSchemaProperties { get; set; }

    /// <summary>Set externally by the transformer (Python: dynamic ``graph_schema_property_types`` attribute).</summary>
    public Dictionary<string, string?>? GraphSchemaPropertyTypes { get; set; }

    public Dictionary<string, string> FieldDataTypes { get; }

    public Dictionary<string, List<string>> ObjectFields { get; }

    /// <summary>
    /// The per-object <c>relationshipObjects</c> map from config/schema.json:
    /// relationship name (the part before the dot in a dotted <c>selectedFields</c>
    /// key) → the Salesforce object it points at.
    ///
    /// <para>Field-level security for <c>Contact.Phone</c> on Case lives in
    /// CONTACT's FieldPermissions, so the target must be known to evaluate it.
    /// Resolution is declared rather than inferred: a wrong guess would evaluate
    /// against the wrong object's permissions and under-drop.</para>
    /// </summary>
    public Dictionary<string, string> RelationshipObjects { get; }

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

            // ── FLS GATE, PASS 1 of 2: GRAPH PROPERTIES (WP-SF-2) ─────────────
            // The matching gate for the CONTENT body is further down in this same
            // method. BOTH are required — a field gated here but not there is
            // stripped from the property and then re-emitted verbatim into the
            // searchable body that Copilot grounds on.
            if (IsFlsRestricted(fieldKey, MappedPropertyName(fieldKey)))
            {
                fieldMapping.Add((fieldKey, "(withheld)", "FLS: restricted", false));
                continue;
            }

            // NOTE — address components are deliberately NOT skipped here. When a
            // component maps to a real Graph schema property the operator asked for
            // that property, and this loop publishes it, gated exactly like any other
            // field. Suppressing it would be silent data loss. The components that
            // exist only to feed the assembly map to `_sf_` placeholders, which are
            // never Graph properties, so this loop passes over them anyway; the
            // CONTENT loop is where they must be skipped, to keep the address from
            // appearing twice.

            // A compound value carries no FieldPermissions of its own and can never be
            // FLS-evaluated, so it is not indexed by any route. See IsUnindexableCompound.
            if (IsUnindexableCompound(fieldKey, fieldValue))
            {
                Converter.Logger.Warning(
                    $"[FLS] [{ObjectName}] compound field '{fieldKey}' is not indexable: Salesforce "
                    + "publishes FieldPermissions on its components, not on the compound, so its "
                    + "value cannot be field-level-security checked. Declare an addressFields group "
                    + "in config/schema.json to index it from individually-gated components.");
                fieldMapping.Add((fieldKey, "(withheld)", "FLS: un-evaluable compound", false));
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

        // ── COMPOUND ADDRESS ASSEMBLY ─────────────────────────────────────────
        // Built from individually-selected, individually-gated component fields
        // (BillingStreet, BillingCity, …) rather than from the compound Salesforce
        // returns. Every part therefore carries its own FieldPermissions evidence, and
        // a restricted component simply does not appear in the assembled text.
        var addressContentLines = new List<string>();
        foreach (var group in AddressGroups)
        {
            var permitted = new JsonObject();
            foreach (var slot in Converter.AddressSlotOrder)
            {
                if (!group.Components.TryGetValue(slot, out var componentField))
                {
                    continue;
                }
                if (IsFlsRestricted(componentField, MappedPropertyName(componentField)))
                {
                    fieldMapping.Add((componentField, $"{group.Name}.{slot}", "FLS: restricted", false));
                    continue;
                }
                if (record.TryGetPropertyValue(componentField, out var componentValue) && componentValue is not null)
                {
                    permitted[slot] = componentValue.DeepClone();
                }
            }

            // A withheld leading slot must not leave the address starting on a
            // separator (", Springfield" / "- 94105"). The separators only ever
            // prefixed a slot, so trimming them from the front is exactly "render the
            // permitted slots"; when nothing is withheld there is nothing to trim and
            // the text is byte-identical to the old compound serialisation.
            var assembled = SerializeAddressObject(permitted).TrimStart(',', '-', ' ');
            if (string.IsNullOrEmpty(assembled))
            {
                continue;
            }
            if (graphProps.Contains(group.PropertyName))
            {
                props[group.PropertyName] = assembled;
                fieldMapping.Add((group.Name, group.PropertyName, "addressFields", true));
            }
            else
            {
                addressContentLines.Add($"{group.Name}: {assembled}");
                fieldMapping.Add((group.Name, "content.value", "addressFields → content", false));
            }
        }

        // Pre-WP-SF-2 precedent: an flsFields entry leaves its Graph property present
        // and null, which is what the Graph transformer skipped over before. It is NOT
        // the enforcement point — on its own it only ever nulled the property and let
        // the value through in the content body.
        //
        // SCHEMA CONFORMANCE. The key is now written only when it IS a declared Graph
        // schema property. Writing it unconditionally meant listing a non-property
        // field (a compound, a relationship path, or simply a typo) posted an
        // UNDECLARED null property to Graph — Graph/Ingest.cs applies no
        // schema-conformance filter before push, so this loop was the last line of
        // defence and it had no check at all. The lookup is case-insensitive and
        // resolves to the SCHEMA's spelling, so a casing typo in flsFields yields the
        // declared property rather than a second, differently-cased ghost of it.
        foreach (var flsField in FlsFields)
        {
            var declared = ResolveGraphPropertyName(flsField, graphProps);
            if (declared is not null)
            {
                props[declared] = null;
            }
        }

        if (props.TryGetPropertyValue("AccountId", out var accountIdNode) && Converter.PyTruthy(accountIdNode))
        {
            props["AccountUrl"] = $"{instanceUrl}/{Converter.PyStr(accountIdNode)}";
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

        // ── FLS GATE, OUT-OF-BAND COLUMNS (WP-SF-3) ───────────────────────────
        // These two are written outside BOTH assembly loops, so neither PASS-1 nor
        // PASS-2 sees them. On the ingest path graphProps comes from the deployed
        // Graph schema, which never carries the __System.* names, so the guard above
        // made these dead. On the DIRECT-CONVERTER path it does not: schemaProperties
        // falls back to Converter.BuildSchemaProperties, which unions the __System.*
        // names in, and the value was then emitted with no FLS check whatsoever.
        if (graphProps.Contains(Converter.SystemCreatedByUserId)
            && record.ContainsKey("CreatedById")
            && !IsSystemUserColumnRestricted("CreatedById", Converter.SystemCreatedByUserId))
        {
            props[Converter.SystemCreatedByUserId] = Converter.PyStr(record["CreatedById"]);
        }

        if (graphProps.Contains(Converter.SystemModifiedByUserId)
            && record.ContainsKey("LastModifiedById")
            && !IsSystemUserColumnRestricted("LastModifiedById", Converter.SystemModifiedByUserId))
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

            // ── FLS GATE, PASS 2 of 2: SEARCHABLE CONTENT BODY (WP-SF-2) ──────
            // The counterpart to the gate in the property loop above. This is the
            // one the pre-WP-SF-2 `flsFields` precedent was missing: it nulled the
            // property and left "FieldName: <secret>" sitting in the body.
            if (IsFlsRestricted(fieldKey, MappedPropertyName(fieldKey)))
            {
                continue;
            }

            // PASS-2 counterparts of the two PASS-1 gates above. Both are required:
            // gating a compound in one loop only moves the leak to the other.
            if (AddressComponentFields.Contains(fieldKey) || IsUnindexableCompound(fieldKey, fieldValue))
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
                    // Nested relationship sub-fields are flattened into the body as
                    // "Parent.Child: value" — they need the same PASS-2 gate.
                    //
                    // The mapped property name MUST be passed here, exactly as the
                    // PASS-1 counterpart in AddSchemaPropertyForObjectField does.
                    // Passing null accepted only the Salesforce spelling of the drop,
                    // so a drop written in Graph-property spelling gated the property
                    // and then leaked the value into the body as "Parent.Child: <value>".
                    var subFieldKey = $"{fieldKey}.{subPair.Key}";
                    if (IsFlsRestricted(subFieldKey, MappedPropertyName(subFieldKey)))
                    {
                        continue;
                    }
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

        // Assembled addresses go LAST: they are composed values, and keeping them at
        // the end leaves the raw field list stable regardless of how many address
        // groups an object declares.
        contentParts.AddRange(addressContentLines);

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

            // FLS gate for nested relationship sub-fields on the PROPERTY path
            // (the PASS-1 counterpart of the "Parent.Child" gate in the content loop).
            if (IsFlsRestricted(lookupKey, propertyName))
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

    /// <summary>
    /// Resolve <paramref name="name"/> to the spelling declared in
    /// <paramref name="graphProps"/>, or <c>null</c> when it is not a declared Graph
    /// schema property at all. Case-insensitive so a hand-typed <c>flsFields</c> entry
    /// resolves to the schema's canonical spelling instead of minting a ghost property.
    /// </summary>
    private static string? ResolveGraphPropertyName(string name, HashSet<string> graphProps)
    {
        if (graphProps.Contains(name))
        {
            return name;
        }
        foreach (var declared in graphProps)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(declared, name))
            {
                return declared;
            }
        }
        return null;
    }

    /// <summary>
    /// Serialise address slots into the single display string the index carries.
    ///
    /// <para>Called with the PERMITTED slots only. Missing slots — whether absent from
    /// the record or withheld by field-level security — are skipped exactly as an empty
    /// value always was, so an address with nothing restricted serialises
    /// byte-identically to the way the compound used to.</para>
    /// </summary>
    private static string SerializeAddressObject(JsonObject token)
    {
        var parts = new List<string>();
        try
        {
            JsonNode? Slot(string name) => token.TryGetPropertyValue(name, out var v) ? v : null;

            if (Converter.PyTruthy(Slot("street")))
            {
                parts.Add(Converter.PyStr(Slot("street")));
            }
            if (Converter.PyTruthy(Slot("city")))
            {
                parts.Add($", {Converter.PyStr(Slot("city"))}");
            }
            if (Converter.PyTruthy(Slot("state")))
            {
                parts.Add($", {Converter.PyStr(Slot("state"))}");
            }
            if (Converter.PyTruthy(Slot("postalCode")))
            {
                parts.Add($" - {Converter.PyStr(Slot("postalCode"))}");
            }
            if (Converter.PyTruthy(Slot("country")))
            {
                parts.Add($", {Converter.PyStr(Slot("country"))}");
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
