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

        item.Properties["ObjectName"] = objectConfig.ObjectName;
        item.Properties["Url"] = BuildUrl(record);
        if (!string.IsNullOrEmpty(objectConfig.IconUrl))
            item.Properties["IconUrl"] = objectConfig.IconUrl;

        // Freshness/lag markers — refinable so Copilot can filter/surface them.
        item.Properties["SourceSystem"] = SourceSystem;
        if (!string.IsNullOrEmpty(record.DataAsOf))
            item.Properties["DataAsOf"] = record.DataAsOf;

        foreach (var (field, property) in objectConfig.SelectedFields)
        {
            if (property.StartsWith("_bdh_", StringComparison.Ordinal))
                continue;
            item.Properties[property] = ToPropertyValue(record.Get(field));
        }

        // Stale-index expiry (GRAPH_ITEM_TTL_DAYS): stamp expirationDateTime so
        // the index self-expires this item if crawling stops — defense after an
        // outage. Unset (0) leaves the item permanent, as before.
        if (_itemTtlDays > 0)
            item.ExpirationDateTime = DateTime.UtcNow.AddDays(_itemTtlDays);

        item.Content = BuildContent(record, objectConfig);
        return item;
    }

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
        var title = record.GetString("Name") ?? record.ItemId;
        sb.AppendLine($"{objectConfig.DisplayName}: {title}");

        foreach (var (field, property) in objectConfig.SelectedFields)
        {
            if (!property.StartsWith("_bdh_", StringComparison.Ordinal))
                continue;
            var value = record.GetString(field);
            if (string.IsNullOrWhiteSpace(value))
                continue;
            sb.AppendLine($"{field}: {StripHtml(value)}");
        }
        if (!string.IsNullOrEmpty(record.DataAsOf))
            sb.AppendLine($"Data as of: {record.DataAsOf} (BDH nightly sync)");
        return sb.ToString().TrimEnd();
    }

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
