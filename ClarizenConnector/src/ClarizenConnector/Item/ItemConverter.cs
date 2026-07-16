// Item/ItemConverter.cs
// ---------------------
// Clarizen record → Graph externalItem conversion.
//
//   • selectedFields drive the property map; `_cz_*` values are consumed by
//     the content body instead of becoming Graph properties.
//   • Standard properties on every item: ObjectName, Url (deep link into
//     Clarizen), IconUrl, plus the mapped fields.
//   • Content = display name + description-ish fields, indexed for Copilot
//     grounding.
//   • Financial classification is applied last (FinancialFieldClassifier).

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;

namespace ClarizenConnector.Item;

public sealed class ItemConverter
{
    private readonly AppConfig _config;
    private readonly string _appBaseUrl;

    public ItemConverter(AppConfig config, string? appBaseUrl = null)
    {
        _config = config;
        _appBaseUrl = (appBaseUrl
            ?? Environment.GetEnvironmentVariable("CLARIZEN_APP_URL")
            ?? "https://app.clarizen.com").TrimEnd('/');
    }

    /// <summary>Deep link into the Clarizen web app for a record.</summary>
    public string BuildUrl(ClarizenRecord record) =>
        $"{_appBaseUrl}/Clarizen/Link.aspx?entityType={Uri.EscapeDataString(record.ObjectType)}"
        + $"&id={Uri.EscapeDataString(record.LocalId)}";

    /// <summary>Convert one record. ACL is supplied by the caller (AclResolver).</summary>
    public ExternalItem Convert(
        ClarizenRecord record, ObjectConfig objectConfig, List<AclEntry> acl)
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

        foreach (var (field, property) in objectConfig.SelectedFields)
        {
            if (property.StartsWith("_cz_", StringComparison.Ordinal))
                continue;
            item.Properties[property] = ToPropertyValue(record.Get(field));
        }

        item.Content = BuildContent(record, objectConfig);

        FinancialFieldClassifier.Apply(item, objectConfig, _config);
        return item;
    }

    /// <summary>Flatten a Clarizen JSON value to a Graph property value.</summary>
    internal static object? ToPropertyValue(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                // Reference objects {id, name} → display name; enum values {value} → value.
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

    /// <summary>Searchable text body: name, description and _cz_ content fields.</summary>
    internal string BuildContent(ClarizenRecord record, ObjectConfig objectConfig)
    {
        var sb = new StringBuilder();
        var title = record.GetString("Name") ?? record.ItemId;
        sb.AppendLine($"{objectConfig.DisplayName}: {title}");

        foreach (var (field, property) in objectConfig.SelectedFields)
        {
            if (!property.StartsWith("_cz_", StringComparison.Ordinal))
                continue;
            var value = record.GetString(field);
            if (string.IsNullOrWhiteSpace(value))
                continue;
            sb.AppendLine($"{field}: {StripHtml(value)}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Cheap HTML tag strip for Clarizen rich-text description fields.</summary>
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
