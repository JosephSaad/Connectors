// Webhook/WebhookEvent.cs
// -----------------------
// The change-notification model plus a defensive parser for Clarizen webhook
// payloads. A notification carries one or more entity changes; each maps to a
// targeted work item — an upsert (re-ingest by id) or a delete (tombstone
// withdraw). Parsing is namespace-agnostic and tolerant: unknown fields are
// ignored, and a single malformed entry never aborts the whole batch.
//
// Accepted body shapes (either a single object or an array under "events" /
// "notifications" / "changes"):
//
//   {"events": [{"entityType": "Task", "id": "/Task/123", "operation": "update"}]}
//   {"entityType": "Task", "id": "/Task/123", "operation": "delete"}
//   [{"objectType": "Task", "entityId": "123", "action": "created"}]
//
// Field name aliases (case-insensitive): entityType|objectType|type,
// id|entityId|objectId, operation|action|changeType|eventType.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClarizenConnector.Webhook;

public enum ChangeKind
{
    Upsert,
    Delete,
}

/// <summary>One entity change. <see cref="LocalId"/> is the bare numeric id
/// ("123" from "/Task/123"); <see cref="ItemId"/> is the Graph external id.</summary>
public sealed record WebhookEvent(string ObjectType, string LocalId, ChangeKind Kind)
{
    /// <summary>Graph external item id: "{ObjectType}_{LocalId}".</summary>
    public string ItemId => $"{ObjectType}_{LocalId}";

    /// <summary>Coalescing key: same entity regardless of change kind.</summary>
    public string Key => $"{ObjectType}|{LocalId}";
}

public static class WebhookEventParser
{
    /// <summary>
    /// Parse a raw JSON body into events. Returns an empty list for anything
    /// unparseable or empty — the receiver treats "no events" as a malformed
    /// rejection so callers never silently drop a real change. Never throws.
    /// </summary>
    public static List<WebhookEvent> Parse(string body)
    {
        var events = new List<WebhookEvent>();
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return events;
        }
        if (root is null)
            return events;

        var array = ExtractArray(root);
        foreach (var node in array)
        {
            if (node is JsonObject entry && ParseEntry(entry) is { } parsed)
                events.Add(parsed);
        }
        return events;
    }

    private static IEnumerable<JsonNode?> ExtractArray(JsonNode root)
    {
        switch (root)
        {
            case JsonArray array:
                return array;
            case JsonObject obj:
                foreach (var key in new[] { "events", "notifications", "changes" })
                {
                    if (obj.TryGetPropertyValue(key, out var value) && value is JsonArray inner)
                        return inner;
                }
                // A bare single-change object.
                return new JsonNode?[] { obj };
            default:
                return Array.Empty<JsonNode?>();
        }
    }

    internal static WebhookEvent? ParseEntry(JsonObject entry)
    {
        var objectType = FirstString(entry, "entityType", "objectType", "type");
        var rawId = FirstString(entry, "id", "entityId", "objectId");
        var operation = FirstString(entry, "operation", "action", "changeType", "eventType");
        if (string.IsNullOrWhiteSpace(objectType) || string.IsNullOrWhiteSpace(rawId))
            return null;

        var kind = ClassifyKind(operation);
        if (kind is null)
            return null;

        return new WebhookEvent(objectType!.Trim(), NormalizeLocalId(rawId!), kind.Value);
    }

    /// <summary>Map a Clarizen operation word to a change kind; null when unknown.</summary>
    internal static ChangeKind? ClassifyKind(string? operation)
    {
        if (string.IsNullOrWhiteSpace(operation))
            return null;
        return operation.Trim().ToLowerInvariant() switch
        {
            "create" or "created" or "insert" or "inserted"
                or "update" or "updated" or "modify" or "modified" or "upsert" or "change" or "changed"
                => ChangeKind.Upsert,
            "delete" or "deleted" or "remove" or "removed" or "destroy" or "destroyed"
                => ChangeKind.Delete,
            _ => null,
        };
    }

    /// <summary>"/Task/123" → "123"; a bare id is returned unchanged.</summary>
    internal static string NormalizeLocalId(string rawId)
    {
        var trimmed = rawId.Trim().TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    private static string? FirstString(JsonObject entry, params string[] names)
    {
        foreach (var (key, value) in entry)
        {
            foreach (var name in names)
            {
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)
                    && value is JsonValue jv && jv.TryGetValue<string>(out var s)
                    && !string.IsNullOrWhiteSpace(s))
                {
                    return s;
                }
            }
        }
        return null;
    }
}
