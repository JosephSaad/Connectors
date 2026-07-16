// Config/SchemaConfig.cs
// ----------------------
// Models + loader for config/schema.json (the Clarizen object list) and
// config/graph-schema.json (the Graph connection schema properties).
//
// schema.json drives everything object-related: which Clarizen entity types
// are crawled, which fields are selected, how fields map to Graph property
// names, which fields are classified as financial, and how ACLs are sourced.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClarizenConnector.Config;

public sealed class SchemaConfig
{
    [JsonPropertyName("objectList")]
    public List<ObjectConfig> ObjectList { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static SchemaConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<SchemaConfig>(json, Options)
            ?? throw new InvalidDataException($"Could not parse schema config '{path}'.");
        config.Validate(path);
        return config;
    }

    public static string DefaultPath =>
        Path.Combine(Directory.GetCurrentDirectory(), "config", "schema.json");

    internal void Validate(string path)
    {
        if (ObjectList.Count == 0)
            throw new InvalidDataException($"Schema config '{path}' has an empty objectList.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in ObjectList)
        {
            if (string.IsNullOrWhiteSpace(obj.ObjectName))
                throw new InvalidDataException($"Schema config '{path}': objectName missing.");
            if (!seen.Add(obj.ObjectName))
                throw new InvalidDataException($"Schema config '{path}': duplicate objectName '{obj.ObjectName}'.");
            if (obj.SelectedFields.Count == 0)
                throw new InvalidDataException(
                    $"Schema config '{path}': object '{obj.ObjectName}' has no selectedFields.");
            foreach (var financial in obj.FinancialFields)
            {
                if (!obj.SelectedFields.ContainsKey(financial))
                    throw new InvalidDataException(
                        $"Schema config '{path}': object '{obj.ObjectName}' lists financial field "
                        + $"'{financial}' that is not in selectedFields.");
            }
        }
    }

    public ObjectConfig? FindObject(string objectName) =>
        ObjectList.FirstOrDefault(o =>
            string.Equals(o.ObjectName, objectName, StringComparison.OrdinalIgnoreCase));
}

public sealed class ObjectConfig
{
    /// <summary>Clarizen entity type name (Project, Task, Milestone, Issue, Risk, Timesheet, ...).</summary>
    [JsonPropertyName("objectName")]
    public string ObjectName { get; set; } = string.Empty;

    /// <summary>Human-readable label used in result cards / logs.</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Clarizen field name → Graph property name. Fields prefixed
    /// <c>_cz_</c> on the value side are queried but consumed by computed
    /// properties (content body, ACL inputs) rather than set directly.</summary>
    [JsonPropertyName("selectedFields")]
    public Dictionary<string, string> SelectedFields { get; set; } = new();

    /// <summary>Clarizen field names classified as financial (budget/cost/rate/
    /// actuals/revenue). Must be a subset of selectedFields keys.</summary>
    [JsonPropertyName("financialFields")]
    public List<string> FinancialFields { get; set; } = new();

    /// <summary>Optional CZQL WHERE fragment appended to the crawl query.</summary>
    [JsonPropertyName("filterCondition")]
    public string FilterCondition { get; set; } = string.Empty;

    /// <summary>ACL sourcing mode: projectMembers (default), ownerOnly, public.</summary>
    [JsonPropertyName("aclMode")]
    public string AclMode { get; set; } = "projectMembers";

    /// <summary>Clarizen field holding the parent project reference (for
    /// project-membership ACLs on child work items). Empty for Project itself.</summary>
    [JsonPropertyName("projectField")]
    public string ProjectField { get; set; } = string.Empty;

    /// <summary>Result-card icon URL.</summary>
    [JsonPropertyName("iconUrl")]
    public string IconUrl { get; set; } = string.Empty;

    // ── Attachment content ingestion (docs/ATTACHMENTS.md) ───────────────────

    /// <summary>Clarizen field holding the attachment's download URL. When set
    /// (and ATTACHMENT_INGESTION=true) this object type is treated as an
    /// attachment carrier — its binary is downloaded, its text extracted, and
    /// appended to the record's externalItem content.</summary>
    [JsonPropertyName("attachmentUrlField")]
    public string AttachmentUrlField { get; set; } = string.Empty;

    /// <summary>Clarizen field holding the attachment filename (drives extension
    /// detection). Defaults to <c>Name</c> when empty.</summary>
    [JsonPropertyName("attachmentNameField")]
    public string AttachmentNameField { get; set; } = string.Empty;

    /// <summary>Optional Clarizen field with the content type / mime hint.</summary>
    [JsonPropertyName("attachmentContentTypeField")]
    public string AttachmentContentTypeField { get; set; } = string.Empty;

    /// <summary>Optional Clarizen field with the declared file size (bytes), used
    /// to skip oversize files before downloading.</summary>
    [JsonPropertyName("attachmentSizeField")]
    public string AttachmentSizeField { get; set; } = string.Empty;

    /// <summary>True when this object carries a downloadable attachment.</summary>
    [JsonIgnore]
    public bool IsAttachment => !string.IsNullOrWhiteSpace(AttachmentUrlField);

    /// <summary>Per-object baseline sensitivity label (Public | Internal |
    /// Confidential | Restricted). The unified classifier floors the derived
    /// label at this default; empty → Internal. See docs/CLASSIFICATION.md.</summary>
    [JsonPropertyName("sensitivityDefault")]
    public string SensitivityDefault { get; set; } = string.Empty;

    /// <summary>Graph property names for the selected fields, excluding
    /// _cz_-prefixed computed placeholders.</summary>
    [JsonIgnore]
    public IEnumerable<KeyValuePair<string, string>> DirectPropertyFields =>
        SelectedFields.Where(kv => !kv.Value.StartsWith("_cz_", StringComparison.Ordinal));
}
