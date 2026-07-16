// Clarizen/ClarizenRecord.cs
// --------------------------
// Thin wrapper over the JSON entity returned by the Clarizen REST API (or the
// TDW bulk reader). Clarizen entity ids arrive as "/EntityType/NNNN"; ItemId
// normalizes to a Graph-safe external item id "EntityType_NNNN".

using System.Text.Json.Nodes;

namespace ClarizenConnector.Clarizen;

public sealed class ClarizenRecord
{
    public ClarizenRecord(string objectType, JsonObject fields)
    {
        ObjectType = objectType;
        Fields = fields;
    }

    /// <summary>Clarizen entity type (Project, Task, Milestone, Issue, ...).</summary>
    public string ObjectType { get; }

    public JsonObject Fields { get; }

    /// <summary>Raw Clarizen id, e.g. "/Task/1234567" (or a bare id from TDW exports).</summary>
    public string RawId =>
        Fields.TryGetPropertyValue("id", out var id) && id is not null
            ? id.GetValue<string>()
            : Fields.TryGetPropertyValue("Id", out var id2) && id2 is not null
                ? id2.GetValue<string>()
                : string.Empty;

    /// <summary>Numeric/local part of the id ("1234567" from "/Task/1234567").</summary>
    public string LocalId
    {
        get
        {
            var raw = RawId;
            var slash = raw.LastIndexOf('/');
            return slash >= 0 ? raw[(slash + 1)..] : raw;
        }
    }

    /// <summary>Graph external item id: "{ObjectType}_{LocalId}" (Graph item ids must be alphanumeric-safe).</summary>
    public string ItemId => $"{ObjectType}_{LocalId}";

    /// <summary>Field value as string (handles Clarizen reference objects {id, name} → name).</summary>
    public string? GetString(string field)
    {
        if (!Fields.TryGetPropertyValue(field, out var node) || node is null)
            return null;
        return node switch
        {
            JsonValue value => value.TryGetValue<string>(out var s) ? s : value.ToJsonString().Trim('"'),
            JsonObject obj when obj.TryGetPropertyValue("name", out var name) && name is not null =>
                name.GetValue<string>(),
            JsonObject obj when obj.TryGetPropertyValue("id", out var id) && id is not null =>
                id.GetValue<string>(),
            _ => node.ToJsonString(),
        };
    }

    /// <summary>Reference-field id ("/User/17" from {"id": "/User/17"}), or the raw string value.</summary>
    public string? GetReferenceId(string field)
    {
        if (!Fields.TryGetPropertyValue(field, out var node) || node is null)
            return null;
        return node switch
        {
            JsonObject obj when obj.TryGetPropertyValue("id", out var id) && id is not null =>
                id.GetValue<string>(),
            JsonValue value when value.TryGetValue<string>(out var s) => s,
            _ => null,
        };
    }

    public JsonNode? Get(string field) =>
        Fields.TryGetPropertyValue(field, out var node) ? node : null;
}
