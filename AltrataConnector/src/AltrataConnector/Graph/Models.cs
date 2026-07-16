// Graph/Models.cs
// ---------------
// Minimal Microsoft Graph external-connection models used by the connector.

using System.Text.Json.Serialization;

namespace AltrataConnector.Graph;

/// <summary>One ACL entry on an externalItem. The connector only ever emits
/// grants for licensed seat principals — see Entitlement/SeatAclBuilder.</summary>
public sealed record AclEntry
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }          // user | group | everyone | everyoneExceptGuests

    [JsonPropertyName("value")]
    public required string Value { get; init; }         // object id / UPN / group id

    [JsonPropertyName("accessType")]
    public string AccessType { get; init; } = "grant";  // grant | deny

    [JsonPropertyName("identitySource")]
    public string IdentitySource { get; init; } = "azureActiveDirectory";
}

public sealed record ExternalItemContent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "text";

    [JsonPropertyName("value")]
    public string Value { get; init; } = "";
}

/// <summary>An externalItem ready to PUT to Graph.</summary>
public sealed record ExternalItem
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("acl")]
    public required IReadOnlyList<AclEntry> Acl { get; init; }

    [JsonPropertyName("properties")]
    public required IReadOnlyDictionary<string, object?> Properties { get; init; }

    [JsonPropertyName("content")]
    public ExternalItemContent Content { get; init; } = new();
}

/// <summary>Schema property definition (config/graph-schema.json shape).</summary>
public sealed record SchemaProperty
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "string";

    [JsonPropertyName("isSearchable")]
    public bool IsSearchable { get; init; }

    [JsonPropertyName("isQueryable")]
    public bool IsQueryable { get; init; }

    [JsonPropertyName("isRetrievable")]
    public bool IsRetrievable { get; init; }

    [JsonPropertyName("isRefinable")]
    public bool IsRefinable { get; init; }

    [JsonPropertyName("labels")]
    public IReadOnlyList<string>? Labels { get; init; }
}

public sealed record GraphSchema
{
    [JsonPropertyName("baseType")]
    public string BaseType { get; init; } = "microsoft.graph.externalItem";

    [JsonPropertyName("properties")]
    public required IReadOnlyList<SchemaProperty> Properties { get; init; }
}
