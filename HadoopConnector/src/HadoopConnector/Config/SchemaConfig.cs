// Config/SchemaConfig.cs
// ----------------------
// Models + loader for config/schema.json (the BDH object list) and
// config/graph-schema.json (the Graph connection schema properties).
//
// schema.json drives everything object-related: which Salesforce-shaped BDH
// objects are crawled, which fields map to Graph property names, how each
// object's ACL is sourced (aclMode), and where its export lives (sourcePath).

using System.Text.Json;
using System.Text.Json.Serialization;

namespace HadoopConnector.Config;

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

    internal static readonly string[] ValidAclModes = { "ownerOnly", "group", "public" };

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
            if (!ValidAclModes.Contains(obj.AclMode, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Schema config '{path}': object '{obj.ObjectName}' has invalid aclMode "
                    + $"'{obj.AclMode}' (expected ownerOnly | group | public).");
            }
            if (string.Equals(obj.AclMode, "group", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(obj.AclGroupId))
            {
                throw new InvalidDataException(
                    $"Schema config '{path}': object '{obj.ObjectName}' uses aclMode=group "
                    + "but has no aclGroupId (an Entra group object id is required).");
            }
        }
    }

    public ObjectConfig? FindObject(string objectName) =>
        ObjectList.FirstOrDefault(o =>
            string.Equals(o.ObjectName, objectName, StringComparison.OrdinalIgnoreCase));
}

public sealed class ObjectConfig
{
    /// <summary>Salesforce-shaped BDH object name (Contact, Account, Opportunity, Case, Lead, ...).</summary>
    [JsonPropertyName("objectName")]
    public string ObjectName { get; set; } = string.Empty;

    /// <summary>Human-readable label used in result cards / logs.</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>BDH column name → Graph property name. Fields prefixed
    /// <c>_bdh_</c> on the value side are read but consumed by computed
    /// properties (content body) rather than set directly.</summary>
    [JsonPropertyName("selectedFields")]
    public Dictionary<string, string> SelectedFields { get; set; } = new();

    /// <summary>ACL sourcing mode: ownerOnly (default), group, public.
    /// BDH has no Salesforce sharing tables, so this is deliberately coarse —
    /// see docs and README for the trade-off vs the live Salesforce connector.</summary>
    [JsonPropertyName("aclMode")]
    public string AclMode { get; set; } = "ownerOnly";

    /// <summary>Entra group object id granted access when aclMode=group.</summary>
    [JsonPropertyName("aclGroupId")]
    public string AclGroupId { get; set; } = string.Empty;

    /// <summary>Record field carrying the Salesforce owner user id (aclMode=ownerOnly).</summary>
    [JsonPropertyName("ownerField")]
    public string OwnerField { get; set; } = string.Empty;

    /// <summary>Record field carrying the owner's email (fallback resolution).</summary>
    [JsonPropertyName("ownerEmailField")]
    public string OwnerEmailField { get; set; } = string.Empty;

    /// <summary>Sub-directory of the BDH root holding this object's partitions;
    /// defaults to the object name.</summary>
    [JsonPropertyName("sourcePath")]
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Result-card icon URL.</summary>
    [JsonPropertyName("iconUrl")]
    public string IconUrl { get; set; } = string.Empty;

    /// <summary>Per-object baseline sensitivity label (Public | Internal |
    /// Confidential | Restricted). The unified classifier floors the derived
    /// label at this default; empty → Internal. See docs/CLASSIFICATION.md.</summary>
    [JsonPropertyName("sensitivityDefault")]
    public string SensitivityDefault { get; set; } = string.Empty;

    [JsonIgnore]
    public string EffectiveOwnerField =>
        string.IsNullOrWhiteSpace(OwnerField) ? "OwnerId" : OwnerField;

    [JsonIgnore]
    public string EffectiveOwnerEmailField =>
        string.IsNullOrWhiteSpace(OwnerEmailField) ? "OwnerEmail" : OwnerEmailField;

    /// <summary>Graph property names for the selected fields, excluding
    /// _bdh_-prefixed computed placeholders.</summary>
    [JsonIgnore]
    public IEnumerable<KeyValuePair<string, string>> DirectPropertyFields =>
        SelectedFields.Where(kv => !kv.Value.StartsWith("_bdh_", StringComparison.Ordinal));
}
