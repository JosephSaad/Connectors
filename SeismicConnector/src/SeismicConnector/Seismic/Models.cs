// Seismic/Models.cs
// -----------------
// Plain data models for Seismic REST resources. Only the fields the connector
// consumes are modelled; unknown JSON properties are ignored on deserialize.

using System.Text.Json.Serialization;

namespace SeismicConnector.Seismic;

/// <summary>A Seismic library / teamsite.</summary>
public sealed class SeismicTeamsite
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("isRestricted")]
    public bool IsRestricted { get; init; }
}

/// <summary>A permission entry on a teamsite or content item.</summary>
public sealed class SeismicPermission
{
    /// <summary><c>user</c> or <c>group</c>.</summary>
    [JsonPropertyName("principalType")]
    public string PrincipalType { get; init; } = "user";

    [JsonPropertyName("principalId")]
    public required string PrincipalId { get; init; }

    /// <summary>Best-effort email for user principals (used for Entra mapping).</summary>
    [JsonPropertyName("email")]
    public string? Email { get; init; }

    /// <summary><c>view</c>, <c>edit</c>, <c>manage</c>, ...</summary>
    [JsonPropertyName("permission")]
    public string Permission { get; init; } = "view";
}

/// <summary>A named content property (metadata field) on a content item.</summary>
public sealed class SeismicProperty
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    /// <summary>Multi-value properties (e.g. compliance flags).</summary>
    [JsonPropertyName("values")]
    public List<string>? Values { get; init; }

    /// <summary>All values as a flat list (single value included when set).</summary>
    [JsonIgnore]
    public IEnumerable<string> AllValues
    {
        get
        {
            if (!string.IsNullOrEmpty(Value))
                yield return Value;
            if (Values is not null)
            {
                foreach (var v in Values)
                {
                    if (!string.IsNullOrEmpty(v))
                        yield return v;
                }
            }
        }
    }
}

/// <summary>A Seismic content item (document, presentation, LiveDoc, ...).</summary>
public sealed class SeismicContent
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("teamsiteId")]
    public string TeamsiteId { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary><c>doc</c>, <c>ppt</c>, <c>pdf</c>, <c>livedoc</c>, <c>video</c>, ...</summary>
    [JsonPropertyName("format")]
    public string Format { get; init; } = "";

    /// <summary><c>published</c> / <c>draft</c> / <c>unpublished</c>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = "published";

    /// <summary>Id of the CURRENT published version. Only this version is indexed.</summary>
    [JsonPropertyName("currentVersionId")]
    public string CurrentVersionId { get; init; } = "";

    [JsonPropertyName("modifiedAt")]
    public DateTime? ModifiedAt { get; init; }

    [JsonPropertyName("publishedAt")]
    public DateTime? PublishedAt { get; init; }

    /// <summary>Compliance expiry; the item is withdrawn once this passes.</summary>
    [JsonPropertyName("expiresAt")]
    public DateTime? ExpiresAt { get; init; }

    [JsonPropertyName("ownerEmail")]
    public string? OwnerEmail { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    /// <summary>Distribution restriction: <c>internal-only</c> or <c>client-approved</c>.</summary>
    [JsonPropertyName("distribution")]
    public string Distribution { get; init; } = "internal-only";

    [JsonPropertyName("properties")]
    public List<SeismicProperty> Properties { get; init; } = new();

    [JsonPropertyName("permissions")]
    public List<SeismicPermission> Permissions { get; init; } = new();

    /// <summary>Lookup of a property's values by name (case-insensitive). Empty when absent.</summary>
    public IReadOnlyList<string> PropertyValues(string name) =>
        Properties
            .Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => p.AllValues)
            .ToList();

    /// <summary>
    /// True for LiveDoc (generated-document template) content. Recognises the
    /// Seismic <c>livedoc</c> format label and its historical spellings.
    /// </summary>
    [JsonIgnore]
    public bool IsLiveDoc
    {
        get
        {
            var f = Format.Trim().TrimStart('.').ToLowerInvariant();
            return f is "livedoc" or "live-doc" or "livedocument";
        }
    }
}

/// <summary>
/// One personalization input a LiveDoc exposes — a field, variable or
/// component that a user fills in when generating a document from the
/// template (e.g. "client name", "product", "region").
/// </summary>
public sealed class SeismicLiveDocField
{
    /// <summary>The field/variable key.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>Human-readable label shown in the generation UI.</summary>
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    /// <summary>Field kind, e.g. <c>text</c>, <c>dropdown</c>, <c>date</c>, <c>component</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Best display token: the label when present, else the name.</summary>
    [JsonIgnore]
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Label) ? Label!.Trim()
        : !string.IsNullOrWhiteSpace(Name) ? Name.Trim()
        : "";
}

/// <summary>A content version summary.</summary>
public sealed class SeismicContentVersion
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "published";

    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; init; }
}

/// <summary>A Seismic user (identity crawl).</summary>
public sealed class SeismicUser
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("fullName")]
    public string? FullName { get; init; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; init; } = true;
}

/// <summary>A Seismic group (identity crawl).</summary>
public sealed class SeismicGroup
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";
}

/// <summary>Engagement/usage signals for one content item (analytics API).</summary>
public sealed class SeismicContentUsage
{
    [JsonPropertyName("contentId")]
    public required string ContentId { get; init; }

    [JsonPropertyName("viewCount")]
    public long ViewCount { get; init; }

    [JsonPropertyName("downloadCount")]
    public long DownloadCount { get; init; }

    [JsonPropertyName("shareCount")]
    public long ShareCount { get; init; }

    /// <summary>Simple engagement aggregate used as the ranking signal.</summary>
    [JsonIgnore]
    public long PopularityScore => ViewCount + 2 * DownloadCount + 3 * ShareCount;
}

/// <summary>A webhook / polling change event.</summary>
public sealed class ContentEvent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("contentId")]
    public string ContentId { get; init; } = "";

    [JsonPropertyName("teamsiteId")]
    public string? TeamsiteId { get; init; }

    public bool IsDelete =>
        Type.Equals("contentDeleted", StringComparison.OrdinalIgnoreCase)
        || Type.Equals("contentUnpublished", StringComparison.OrdinalIgnoreCase);
}
