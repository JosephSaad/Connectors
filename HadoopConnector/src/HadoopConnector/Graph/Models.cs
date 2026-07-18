// Graph/Models.cs
// ---------------
// External-item and ACL models serialized to the Graph external connections
// API (PUT /external/connections/{id}/items/{itemId}).

using System.Text.Json.Nodes;

namespace HadoopConnector.Graph;

public enum AclAccessType
{
    Grant,
    Deny,
}

public enum AclEntryType
{
    User,
    Group,
    Everyone,
    EveryoneExceptGuests,
}

public sealed record AclEntry(AclEntryType Type, string Value, AclAccessType AccessType)
{
    public JsonObject ToJson() => new()
    {
        ["type"] = Type switch
        {
            AclEntryType.User => "user",
            AclEntryType.Group => "group",
            AclEntryType.Everyone => "everyone",
            AclEntryType.EveryoneExceptGuests => "everyoneExceptGuests",
            _ => "user",
        },
        ["value"] = Value,
        ["accessType"] = AccessType == AclAccessType.Grant ? "grant" : "deny",
    };
}

public sealed class ExternalItem
{
    public required string Id { get; init; }

    /// <summary>Graph property name → value (string, number, bool, string[] or DateTime ISO).</summary>
    public Dictionary<string, object?> Properties { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Text content indexed for search/Copilot grounding.</summary>
    public string Content { get; set; } = string.Empty;

    public List<AclEntry> Acl { get; init; } = new();

    /// <summary>Serialize to the externalItem JSON payload.</summary>
    public JsonObject ToJson()
    {
        var properties = new JsonObject();
        foreach (var (name, value) in Properties)
        {
            if (value is null)
                continue;
            properties[name] = value switch
            {
                string s => JsonValue.Create(s),
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                decimal m => JsonValue.Create(m),
                DateTime dt => JsonValue.Create(dt.ToUniversalTime().ToString("o")),
                IEnumerable<string> list => new JsonArray(
                    list.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
                _ => JsonValue.Create(value.ToString()),
            };
        }

        var acl = new JsonArray();
        foreach (var entry in Acl)
            acl.Add(entry.ToJson());

        return new JsonObject
        {
            ["id"] = Id,
            ["properties"] = properties,
            ["content"] = new JsonObject
            {
                ["value"] = Content,
                ["type"] = "text",
            },
            ["acl"] = acl,
        };
    }
}
