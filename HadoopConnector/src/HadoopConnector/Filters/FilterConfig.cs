// Filters/FilterConfig.cs
// -----------------------
// The connector's SIGNATURE FEATURE: a strong, per-object-type, config-driven
// filter layer that minimizes the records scanned out of BDH's 150M+ rows
// before anything else happens. Loaded from config/filters.json:
//
// {
//   "objects": {
//     "Contact": {
//       "partition": [
//         { "key": "region", "op": "in", "values": ["EMEA", "NA"] },
//         { "key": "dt", "op": "withinLastDays", "value": 120 }
//       ],
//       "anyOf": [
//         { "allOf": [
//           { "field": "Status", "op": "equals", "value": "Active" },
//           { "field": "AnnualRevenue", "op": "gte", "value": 100000 }
//         ] }
//       ]
//     }
//   },
//   "fullScanAllowed": ["Account"]
// }
//
//   • partition — predicates on Hive partition KEYS (dt=..., region=...):
//     evaluated on directory names, so non-matching partitions are pruned with
//     ZERO file I/O. Date operators apply to the dt key.
//   • anyOf/allOf — record predicates evaluated while STREAMING rows. anyOf is
//     an OR of AND-groups; a top-level "allOf" is shorthand for one group.
//     Field lookup is case-insensitive.
//   • fullScanAllowed — objects explicitly exempt from the fail-closed scale
//     guard (see BdhFetcher: an object with NO filter refuses to crawl unless
//     listed here or ALLOW_FULL_SCAN=true).
//
// Validation is strict at load time: an unknown operator, a missing field/key,
// or missing/malformed operands is a CONFIG ERROR (InvalidDataException) —
// never silently ignored, because a dropped filter at this scale is an outage.

using System.Text.Json.Nodes;

namespace HadoopConnector.Filters;

public enum FilterOp
{
    Equals,
    NotEquals,
    In,
    NotIn,
    Prefix,
    Contains,
    Gte,
    Lte,
    Between,
    WithinLastDays,
    After,
    Before,
    IsNull,
    IsNotNull,
}

public sealed class FilterPredicate
{
    /// <summary>Record field (record predicates) or partition key (partition filters).</summary>
    public required string Field { get; init; }

    public required FilterOp Op { get; init; }

    /// <summary>Scalar operand (equals/prefix/gte/withinLastDays/...).</summary>
    public string? Value { get; init; }

    /// <summary>List operand (in/notIn/between).</summary>
    public List<string> Values { get; init; } = new();
}

/// <summary>An AND group of predicates.</summary>
public sealed class FilterGroup
{
    public List<FilterPredicate> AllOf { get; init; } = new();
}

/// <summary>Filter definition for one object type.</summary>
public sealed class ObjectFilter
{
    /// <summary>Partition-key predicates (directory pruning, zero I/O).</summary>
    public List<FilterPredicate> Partition { get; init; } = new();

    /// <summary>Record predicates: OR of AND groups. Empty → all rows match.</summary>
    public List<FilterGroup> AnyOf { get; init; } = new();

    /// <summary>True when this object has at least one partition or record predicate.</summary>
    public bool HasAnyFilter => Partition.Count > 0 || AnyOf.Count > 0;
}

public sealed class FilterSet
{
    private readonly Dictionary<string, ObjectFilter> _objects;

    public FilterSet(Dictionary<string, ObjectFilter> objects, HashSet<string> fullScanAllowed)
    {
        _objects = objects;
        FullScanAllowed = fullScanAllowed;
    }

    /// <summary>Object types the operator explicitly exempted from the fail-closed guard.</summary>
    public IReadOnlySet<string> FullScanAllowed { get; }

    public IReadOnlyDictionary<string, ObjectFilter> Objects => _objects;

    /// <summary>An always-empty set (tests / filters file absent).</summary>
    public static FilterSet Empty { get; } = new(
        new Dictionary<string, ObjectFilter>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public ObjectFilter? For(string objectName) =>
        _objects.TryGetValue(objectName, out var filter) ? filter : null;

    public bool IsFullScanAllowed(string objectName) => FullScanAllowed.Contains(objectName);

    public static string DefaultPath =>
        Path.Combine(Directory.GetCurrentDirectory(), "config", "filters.json");

    /// <summary>Load and validate config/filters.json. A missing file yields an
    /// empty set (every object then trips the fail-closed guard unless allowed).</summary>
    public static FilterSet Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return Empty;
        return Parse(File.ReadAllText(path), path);
    }

    /// <summary>Parse + validate filters JSON; malformed → InvalidDataException.</summary>
    public static FilterSet Parse(string json, string sourceName = "filters.json")
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (System.Text.Json.JsonException exc)
        {
            throw new InvalidDataException($"Filter config '{sourceName}' is not valid JSON: {exc.Message}");
        }
        if (root is not JsonObject rootObj)
            throw new InvalidDataException($"Filter config '{sourceName}' must be a JSON object.");

        var objects = new Dictionary<string, ObjectFilter>(StringComparer.OrdinalIgnoreCase);
        if (rootObj.TryGetPropertyValue("objects", out var objectsNode) && objectsNode is not null)
        {
            if (objectsNode is not JsonObject objectsObj)
                throw new InvalidDataException($"Filter config '{sourceName}': 'objects' must be an object.");
            foreach (var (objectName, filterNode) in objectsObj)
            {
                if (filterNode is not JsonObject filterObj)
                {
                    throw new InvalidDataException(
                        $"Filter config '{sourceName}': entry '{objectName}' must be an object.");
                }
                objects[objectName] = ParseObjectFilter(filterObj, sourceName, objectName);
            }
        }

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rootObj.TryGetPropertyValue("fullScanAllowed", out var allowedNode) && allowedNode is not null)
        {
            if (allowedNode is not JsonArray allowedArray)
            {
                throw new InvalidDataException(
                    $"Filter config '{sourceName}': 'fullScanAllowed' must be an array of object names.");
            }
            foreach (var entry in allowedArray)
            {
                if (entry is JsonValue value && value.TryGetValue<string>(out var name)
                    && !string.IsNullOrWhiteSpace(name))
                {
                    allowed.Add(name);
                }
                else
                {
                    throw new InvalidDataException(
                        $"Filter config '{sourceName}': 'fullScanAllowed' entries must be strings.");
                }
            }
        }

        return new FilterSet(objects, allowed);
    }

    private static ObjectFilter ParseObjectFilter(JsonObject obj, string sourceName, string objectName)
    {
        var partition = new List<FilterPredicate>();
        if (obj.TryGetPropertyValue("partition", out var partitionNode) && partitionNode is not null)
        {
            if (partitionNode is not JsonArray partitionArray)
            {
                throw new InvalidDataException(
                    $"Filter config '{sourceName}': '{objectName}.partition' must be an array.");
            }
            foreach (var predNode in partitionArray)
                partition.Add(ParsePredicate(predNode, sourceName, objectName, keyName: "key"));
        }

        var groups = new List<FilterGroup>();
        var hasAnyOf = obj.TryGetPropertyValue("anyOf", out var anyOfNode) && anyOfNode is not null;
        var hasAllOf = obj.TryGetPropertyValue("allOf", out var allOfNode) && allOfNode is not null;
        if (hasAnyOf && hasAllOf)
        {
            throw new InvalidDataException(
                $"Filter config '{sourceName}': '{objectName}' has both 'anyOf' and 'allOf' — use one "
                + "(allOf is shorthand for a single anyOf group).");
        }
        if (hasAnyOf)
        {
            if (anyOfNode is not JsonArray anyOfArray)
            {
                throw new InvalidDataException(
                    $"Filter config '{sourceName}': '{objectName}.anyOf' must be an array of groups.");
            }
            foreach (var groupNode in anyOfArray)
            {
                if (groupNode is not JsonObject groupObj
                    || !groupObj.TryGetPropertyValue("allOf", out var groupAllOf)
                    || groupAllOf is not JsonArray groupArray)
                {
                    throw new InvalidDataException(
                        $"Filter config '{sourceName}': every '{objectName}.anyOf' entry must be "
                        + "{\"allOf\": [ ...predicates ]}.");
                }
                var group = new FilterGroup();
                foreach (var predNode in groupArray)
                    group.AllOf.Add(ParsePredicate(predNode, sourceName, objectName, keyName: "field"));
                if (group.AllOf.Count == 0)
                {
                    throw new InvalidDataException(
                        $"Filter config '{sourceName}': '{objectName}' has an empty allOf group.");
                }
                groups.Add(group);
            }
        }
        else if (hasAllOf)
        {
            if (allOfNode is not JsonArray allOfArray)
            {
                throw new InvalidDataException(
                    $"Filter config '{sourceName}': '{objectName}.allOf' must be an array of predicates.");
            }
            var group = new FilterGroup();
            foreach (var predNode in allOfArray)
                group.AllOf.Add(ParsePredicate(predNode, sourceName, objectName, keyName: "field"));
            if (group.AllOf.Count > 0)
                groups.Add(group);
        }

        // Unknown keys are config errors too — a typo ("anyof", "filters") must
        // not silently produce an unfiltered object at this scale.
        foreach (var (key, _) in obj)
        {
            if (key is not ("partition" or "anyOf" or "allOf" or "notes"))
            {
                throw new InvalidDataException(
                    $"Filter config '{sourceName}': '{objectName}' has unknown key '{key}' "
                    + "(expected partition, anyOf, allOf or notes).");
            }
        }

        return new ObjectFilter { Partition = partition, AnyOf = groups };
    }

    internal static FilterPredicate ParsePredicate(
        JsonNode? node, string sourceName, string objectName, string keyName)
    {
        if (node is not JsonObject obj)
        {
            throw new InvalidDataException(
                $"Filter config '{sourceName}': '{objectName}' has a non-object predicate.");
        }

        var field = obj[keyName]?.GetValue<string>()
            ?? obj[keyName == "key" ? "field" : "key"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(field))
        {
            throw new InvalidDataException(
                $"Filter config '{sourceName}': '{objectName}' predicate is missing '{keyName}'.");
        }

        var opRaw = obj["op"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(opRaw))
        {
            throw new InvalidDataException(
                $"Filter config '{sourceName}': '{objectName}.{field}' predicate is missing 'op'.");
        }
        if (!TryParseOp(opRaw, out var op))
        {
            throw new InvalidDataException(
                $"Filter config '{sourceName}': '{objectName}.{field}' has unknown operator '{opRaw}'.");
        }

        string? value = null;
        if (obj.TryGetPropertyValue("value", out var valueNode) && valueNode is JsonValue jv)
            value = jv.TryGetValue<string>(out var s) ? s : jv.ToJsonString().Trim('"');

        var values = new List<string>();
        if (obj.TryGetPropertyValue("values", out var valuesNode) && valuesNode is JsonArray array)
        {
            foreach (var entry in array)
            {
                if (entry is JsonValue ev)
                    values.Add(ev.TryGetValue<string>(out var s) ? s : ev.ToJsonString().Trim('"'));
            }
        }

        // Operand arity validation — malformed predicates are config errors.
        switch (op)
        {
            case FilterOp.In or FilterOp.NotIn:
                if (values.Count == 0)
                {
                    throw new InvalidDataException(
                        $"Filter config '{sourceName}': '{objectName}.{field}' {opRaw} requires 'values'.");
                }
                break;
            case FilterOp.Between:
                if (values.Count != 2)
                {
                    throw new InvalidDataException(
                        $"Filter config '{sourceName}': '{objectName}.{field}' between requires "
                        + "exactly two 'values' [low, high].");
                }
                break;
            case FilterOp.IsNull or FilterOp.IsNotNull:
                break;  // no operand
            case FilterOp.WithinLastDays:
                if (value is null || !int.TryParse(value, out var days) || days < 0)
                {
                    throw new InvalidDataException(
                        $"Filter config '{sourceName}': '{objectName}.{field}' withinLastDays requires "
                        + "a non-negative integer 'value'.");
                }
                break;
            default:
                if (value is null)
                {
                    throw new InvalidDataException(
                        $"Filter config '{sourceName}': '{objectName}.{field}' {opRaw} requires 'value'.");
                }
                break;
        }

        return new FilterPredicate { Field = field, Op = op, Value = value, Values = values };
    }

    internal static bool TryParseOp(string raw, out FilterOp op)
    {
        switch (raw.Trim())
        {
            case "equals": op = FilterOp.Equals; return true;
            case "notEquals": op = FilterOp.NotEquals; return true;
            case "in": op = FilterOp.In; return true;
            case "notIn": op = FilterOp.NotIn; return true;
            case "prefix": op = FilterOp.Prefix; return true;
            case "contains": op = FilterOp.Contains; return true;
            case "gte": case ">=": op = FilterOp.Gte; return true;
            case "lte": case "<=": op = FilterOp.Lte; return true;
            case "between": op = FilterOp.Between; return true;
            case "withinLastDays": op = FilterOp.WithinLastDays; return true;
            case "after": op = FilterOp.After; return true;
            case "before": op = FilterOp.Before; return true;
            case "isNull": op = FilterOp.IsNull; return true;
            case "isNotNull": op = FilterOp.IsNotNull; return true;
            default: op = default; return false;
        }
    }
}
