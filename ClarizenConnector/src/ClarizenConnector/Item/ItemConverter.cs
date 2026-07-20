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
    /// <summary>Clarizen entity type, stamped on every item.</summary>
    public const string ObjectNameProperty = "ObjectName";

    /// <summary>Deep link into the Clarizen web app, stamped on every item.</summary>
    public const string UrlProperty = "Url";

    /// <summary>Result-card icon, stamped when the object config supplies one.</summary>
    public const string IconUrlProperty = "IconUrl";

    /// <summary>Graph property names this converter stamps on every item
    /// irrespective of schema.json. Declared here (rather than as loose string
    /// literals) so the schema drift guard can enumerate what the code actually
    /// publishes — see <see cref="Config.SchemaConfig.ReservedPropertyNames"/>
    /// for the connector-computed half.</summary>
    internal static readonly IReadOnlyList<string> StandardPropertyNames = new[]
    {
        ObjectNameProperty,
        UrlProperty,
        IconUrlProperty,
    };

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

        item.Properties[ObjectNameProperty] = objectConfig.ObjectName;
        item.Properties[UrlProperty] = BuildUrl(record);
        if (!string.IsNullOrEmpty(objectConfig.IconUrl))
            item.Properties[IconUrlProperty] = objectConfig.IconUrl;

        foreach (var (field, property) in objectConfig.SelectedFields)
        {
            if (property.StartsWith("_cz_", StringComparison.Ordinal))
                continue;
            item.Properties[property] = ToPropertyValue(record.Get(field));
        }

        // Financial fields routed to the CONTENT body (_cz_*) never become
        // properties, so property stripping cannot govern them. Detect them on
        // the source record: filter mode redacts the value from the content
        // body; every mode still classifies (and acl mode restricts) the item.
        var contentFinancial =
            FinancialFieldClassifier.RecordHasContentFinancial(record, objectConfig);
        var redactContent = contentFinancial
            && string.Equals(_config.FinancialDataMode, "filter", StringComparison.OrdinalIgnoreCase);
        item.Content = BuildContent(
            record, objectConfig,
            redactContent
                ? FinancialFieldClassifier.ContentFinancialFields(objectConfig)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null);

        FinancialFieldClassifier.Apply(item, objectConfig, _config, contentFinancial);

        // Optional stale-index expiry: stamp expirationDateTime = now + TTL so
        // the Graph index self-expires if crawling stops after an outage.
        if (_config.GraphItemTtlDays > 0)
            item.ExpirationDateTime = DateTime.UtcNow.AddDays(_config.GraphItemTtlDays);

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

    /// <summary>Searchable text body: name, description and _cz_ content fields.
    /// Fields in <paramref name="redactFields"/> (financial fields under
    /// FINANCIAL_DATA_MODE=filter) are omitted so their values never reach the
    /// index through the content body.</summary>
    internal string BuildContent(
        ClarizenRecord record, ObjectConfig objectConfig, IReadOnlySet<string>? redactFields = null)
    {
        var sb = new StringBuilder();
        var title = record.GetString("Name") ?? record.ItemId;
        sb.AppendLine($"{objectConfig.DisplayName}: {title}");

        foreach (var (field, property) in objectConfig.SelectedFields)
        {
            if (!property.StartsWith("_cz_", StringComparison.Ordinal))
                continue;
            if (redactFields is not null && redactFields.Contains(field))
                continue;  // governance says redact — never index the value
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
