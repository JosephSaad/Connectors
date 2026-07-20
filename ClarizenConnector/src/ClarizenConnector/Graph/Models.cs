// Graph/Models.cs
// ---------------
// External-item and ACL models serialized to the Graph external connections
// API (PUT /external/connections/{id}/items/{itemId}).

using System.Text.Json.Nodes;

namespace ClarizenConnector.Graph;

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

    /// <summary>Graph property name → value (string, number, bool, string[] or
    /// DateTime ISO). A <see cref="GraphPropertyBag"/> rather than a Dictionary:
    /// every write is checked against the Graph connection schema, so a property
    /// the schema does not declare cannot be stamped at all. See
    /// <see cref="GraphPropertyRegistry"/>.</summary>
    public GraphPropertyBag Properties { get; init; } = new();

    /// <summary>Text content indexed for search/Copilot grounding.</summary>
    public string Content { get; set; } = string.Empty;

    public List<AclEntry> Acl { get; init; } = new();

    /// <summary>Optional index self-expiry (GRAPH_ITEM_TTL_DAYS). When set it is
    /// emitted as <c>expirationDateTime</c> so the item ages out if crawling
    /// stops. Null (the default) omits the field entirely.</summary>
    public DateTime? ExpirationDateTime { get; set; }

    /// <summary>Serialize to the externalItem JSON payload.
    /// <para>
    /// Defence in depth: the property bag already rejects an undeclared name at
    /// the moment it is stamped, but this is the last point before a Graph PUT,
    /// so it re-checks the whole bag. Any future route that assembles properties
    /// some other way still cannot produce a body Graph would reject.
    /// </para><para>
    /// The re-check ignores <c>GraphPropertyRegistry.SuspendEnforcement</c>. That
    /// scope exists so StampedPropertyInventory can OBSERVE what the code stamps;
    /// it must not also widen the last checkpoint before the wire, or the
    /// guarantee stated above would hold only outside a suspension — which is
    /// exactly what it used to do.
    /// </para></summary>
    public JsonObject ToJson()
    {
        var properties = new JsonObject();
        foreach (var (name, value) in Properties)
        {
            GraphPropertyRegistry.AssertDeclaredIgnoringSuspension(name);
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

        var json = new JsonObject
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
        if (ExpirationDateTime is { } expiry)
            json["expirationDateTime"] = expiry.ToUniversalTime().ToString("o");
        return json;
    }
}
