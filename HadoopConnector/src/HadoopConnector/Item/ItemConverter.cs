// Item/ItemConverter.cs
// ---------------------
// BDH record → Graph externalItem conversion.
//
//   • selectedFields drive the property map; `_bdh_*` values are consumed by
//     the content body instead of becoming Graph properties.
//   • Standard properties on every item: ObjectName, Url (deep link into the
//     LIVE Salesforce org — the ids are the same), IconUrl, plus the mapped
//     fields.
//   • Freshness: every item carries sourceSystem="BDH-Hadoop" and dataAsOf
//     (the partition dt / sync timestamp) as refinable properties, so Copilot
//     answers can surface the up-to-24h staleness of the cheap path.
//   • Content = display name + description-ish fields, indexed for Copilot
//     grounding.

using System.Text;
using System.Text.Json.Nodes;
using HadoopConnector.Config;
using HadoopConnector.Graph;
using HadoopConnector.Hdfs;

namespace HadoopConnector.Item;

public sealed class ItemConverter
{
    /// <summary>The sourceSystem property value marking items from this connector.</summary>
    public const string SourceSystem = "BDH-Hadoop";

    // The names of the Graph properties Convert emits for EVERY item, outside
    // selectedFields. Named constants so the emission below and the schema
    // validation that reasons about them read the SAME symbol — a second,
    // duplicated list in the validator would drift the first time a standard
    // property is added or renamed here.
    public const string ObjectNameProperty = "ObjectName";
    public const string UrlProperty = "Url";
    public const string IconUrlProperty = "IconUrl";
    public const string SourceSystemProperty = "SourceSystem";
    public const string DataAsOfProperty = "DataAsOf";

    /// <summary>
    /// Every Graph property name <see cref="Convert"/> can emit on its own,
    /// independently of <c>selectedFields</c> — THIS emitter's contribution to
    /// <see cref="AlwaysEmittedProperties"/>.
    /// <para>
    /// selectedFields cannot safely name any of these on EITHER side. A
    /// columnPolicies entry named after one reports a restriction that is not
    /// delivered (the standard property of that name keeps being emitted); a
    /// selectedFields value mapping ONTO one destroys it (loop 1 overwrites the
    /// standard value with the mapped column's). SchemaConfig enforces both
    /// directions — but off <see cref="AlwaysEmittedProperties.Names"/>, NOT off
    /// this list: this connector has a second always-emitting code path
    /// (<see cref="SensitivityClassifier"/>), and a check reading only this list
    /// missed it entirely. See <c>ValidateSelectedFields</c>.
    /// </para>
    /// <para>
    /// IconUrl and DataAsOf are emitted conditionally (only when non-empty), but
    /// they belong here all the same: whether they are populated is a property of
    /// the RECORD, so a config that collides with them is broken for some records
    /// and not others, which is worse than being broken for all of them.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> StandardPropertyNames = new[]
    {
        ObjectNameProperty,
        UrlProperty,
        IconUrlProperty,
        SourceSystemProperty,
        DataAsOfProperty,
    };

    private readonly string _appBaseUrl;
    private readonly int _itemTtlDays;

    public ItemConverter(AppConfig config, string? appBaseUrl = null)
    {
        _appBaseUrl = (appBaseUrl ?? config.ItemUrlBase).TrimEnd('/');
        _itemTtlDays = config.GraphItemTtlDays;
    }

    /// <summary>Deep link into the live Salesforce org for a record (same id space).</summary>
    public string BuildUrl(BdhRecord record) =>
        $"{_appBaseUrl}/{Uri.EscapeDataString(record.RawId)}";

    /// <summary>Convert one record. ACL is supplied by the caller (AclResolver).</summary>
    public ExternalItem Convert(
        BdhRecord record, ObjectConfig objectConfig, List<AclEntry> acl)
    {
        var item = new ExternalItem
        {
            Id = record.ItemId,
            Acl = acl,
        };

        item.Properties[ObjectNameProperty] = objectConfig.ObjectName;
        item.Properties[UrlProperty] = BuildUrl(record);
        if (!string.IsNullOrEmpty(objectConfig.IconUrl))
            item.Properties[IconUrlProperty] = objectConfig.IconUrl;

        // Freshness/lag markers — refinable so Copilot can filter/surface them.
        item.Properties[SourceSystemProperty] = SourceSystem;
        if (!string.IsNullOrEmpty(record.DataAsOf))
            item.Properties[DataAsOfProperty] = record.DataAsOf;

        // LOOP 1 of 2 — Graph PROPERTIES. The column-policy gate here MUST stay
        // in lock-step with the one in BuildContent (loop 2): a column gated in
        // only one of them is stripped from the property and still indexed in
        // the content body Copilot grounds on, which is a leak, not a partial
        // fix. See ColumnPolicyTests, which asserts on both surfaces at once.
        foreach (var (field, property) in objectConfig.SelectedFields)
        {
            if (RouteFor(property) != FieldRoute.Property)
                continue;
            switch (objectConfig.PolicyFor(field))
            {
                case ColumnPolicyAction.Drop:
                    continue;
                case ColumnPolicyAction.Mask:
                    // Deliberately never reads the value: a masked column's data
                    // does not enter the item at all, only its name survives.
                    item.Properties[property] = ColumnPolicy.MaskMarker;
                    continue;
                default:
                    item.Properties[property] = ToPropertyValue(record.Get(field));
                    continue;
            }
        }

        // Stale-index expiry (GRAPH_ITEM_TTL_DAYS): stamp expirationDateTime so
        // the index self-expires this item if crawling stops — defense after an
        // outage. Unset (0) leaves the item permanent, as before.
        if (_itemTtlDays > 0)
            item.ExpirationDateTime = DateTime.UtcNow.AddDays(_itemTtlDays);

        item.Content = BuildContent(record, objectConfig);
        return item;
    }

    /// <summary>Where one selectedFields entry is emitted.</summary>
    public enum FieldRoute
    {
        /// <summary>A Graph property named by the mapping value (loop 1).</summary>
        Property,

        /// <summary>A <c>_bdh_</c> placeholder: a content-body line keyed by the
        /// COLUMN name (loop 2).</summary>
        Content,

        /// <summary>No usable property name — emitted nowhere.</summary>
        Unusable,
    }

    /// <summary>
    /// The single routing decision both emission loops read, so they cannot
    /// drift: exactly one of them handles any given entry, and neither
    /// dereferences the mapping value.
    /// <para>
    /// A missing/blank property name is <see cref="FieldRoute.Unusable"/> rather
    /// than a crash. <c>"Comp__c": null</c> in selectedFields is legal JSON, and
    /// the deserializer stores a null string — which used to NRE here on EVERY
    /// record, and, because IngestPipeline catches per record, dead-lettered 100%
    /// of the object silently instead of failing loudly. Such a config is now
    /// rejected at load (SchemaConfig.ValidateSelectedFields), so this is the
    /// second line of defence for hand-built ObjectConfigs; emitting nothing is
    /// the fail-closed outcome — a nameless property cannot be indexed anyway.
    /// </para>
    /// </summary>
    public static FieldRoute RouteFor(string? property) =>
        string.IsNullOrWhiteSpace(property) ? FieldRoute.Unusable
        : property.StartsWith("_bdh_", StringComparison.Ordinal) ? FieldRoute.Content
        : FieldRoute.Property;

    /// <summary>Flatten a JSON value to a Graph property value.</summary>
    internal static object? ToPropertyValue(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                if (obj.TryGetPropertyValue("name", out var name) && name is not null)
                    return name.GetValue<string>();
                if (obj.TryGetPropertyValue("value", out var enumValue) && enumValue is JsonValue ev)
                    return ev.TryGetValue<string>(out var s) ? s : ev.ToJsonString();
                if (obj.TryGetPropertyValue("id", out var id) && id is not null)
                    return id.GetValue<string>();
                return obj.ToJsonString();
            case JsonArray array:
                return array
                    .Select(element => ToPropertyValue(element)?.ToString() ?? string.Empty)
                    .Where(s => s.Length > 0)
                    .ToArray();
            case JsonValue value:
                if (value.TryGetValue<bool>(out var b))
                    return b;
                if (value.TryGetValue<long>(out var l))
                    return l;
                if (value.TryGetValue<double>(out var d))
                    return d;
                if (value.TryGetValue<string>(out var str))
                    return str;
                return value.ToJsonString();
            default:
                return node.ToJsonString();
        }
    }

    /// <summary>Searchable text body: name, _bdh_ content fields, freshness note.</summary>
    internal string BuildContent(BdhRecord record, ObjectConfig objectConfig)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{objectConfig.DisplayName}: {TitleFor(record, objectConfig)}");

        // LOOP 2 of 2 — searchable CONTENT body. Mirror of the property gate in
        // Convert; see the note there. This is the loop that leaks if only the
        // property loop is gated, because a _bdh_-routed column NEVER passes
        // through the property loop at all.
        foreach (var (field, property) in objectConfig.SelectedFields)
        {
            if (RouteFor(property) != FieldRoute.Content)
                continue;
            switch (objectConfig.PolicyFor(field))
            {
                case ColumnPolicyAction.Drop:
                    continue;
                case ColumnPolicyAction.Mask:
                    // Emitted even when the source value is empty, so the body
                    // never reveals whether a restricted column was populated —
                    // and so the key is retained exactly as in the property loop.
                    sb.AppendLine($"{field}: {ColumnPolicy.MaskMarker}");
                    continue;
            }
            var value = record.GetString(field);
            if (string.IsNullOrWhiteSpace(value))
                continue;
            sb.AppendLine($"{field}: {StripHtml(value)}");
        }
        if (!string.IsNullOrEmpty(record.DataAsOf))
            sb.AppendLine($"Data as of: {record.DataAsOf} (BDH nightly sync)");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The content body's opening title line, read off the record's "Name"
    /// column. This is a THIRD emission path for a record value, outside both
    /// selectedFields loops — a policy on Name that only reached those loops
    /// would strip the Graph property and still print the value at the top of
    /// the text Copilot reads. Gate it identically: dropped ⇒ fall back to the
    /// record id, masked ⇒ the marker.
    /// </summary>
    private static string TitleFor(BdhRecord record, ObjectConfig objectConfig) =>
        objectConfig.PolicyFor("Name") switch
        {
            ColumnPolicyAction.Drop => record.ItemId,
            ColumnPolicyAction.Mask => ColumnPolicy.MaskMarker,
            _ => record.GetString("Name") ?? record.ItemId,
        };

    /// <summary>Cheap HTML tag strip for rich-text description fields.</summary>
    internal static string StripHtml(string value)
    {
        if (!value.Contains('<'))
            return System.Net.WebUtility.HtmlDecode(value);
        var sb = new StringBuilder(value.Length);
        var inTag = false;
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '<':
                    inTag = true;
                    break;
                case '>':
                    inTag = false;
                    sb.Append(' ');
                    break;
                default:
                    if (!inTag)
                        sb.Append(ch);
                    break;
            }
        }
        return System.Net.WebUtility.HtmlDecode(sb.ToString()).Trim();
    }
}
