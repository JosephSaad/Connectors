// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Item/Models.cs
// --------------
// Data models for Microsoft Graph external items.
//
// These lightweight classes mirror the Graph ``externalItem`` JSON structure
// and provide a ``ToDict()`` method for serialisation.
//
// Classes
// -------
// Content
//     Wraps the ``parsedData`` text body that Microsoft Search indexes for
//     full-text queries.
//
// AccessControlEntry
//     A single ACL grant or deny entry (``accessType``, ``type``, ``value``).
//
// SearchableItem
//     A full external item: ID, properties dict, optional content, optional
//     ACL list, and a ``type`` of ``"searchable"``.  Converted to JSON via
//     ``ToDict()`` before being PUT to Graph.
//
// DeletedItem
//     Represents a Salesforce record whose ``IsDeleted`` flag is ``True``.
//     Converted to ``{"id": ..., "type": "deleted"}`` so the ingestion
//     pipeline issues a DELETE instead of a PUT.

using System.Text.Json.Nodes;

namespace SalesforceCopilotConnector.Item;

public class Content
{
    /// <summary>Initialise with optional parsed text body.</summary>
    public Content(string parsedData = "")
    {
        ParsedData = parsedData;
    }

    public string ParsedData { get; set; }

    /// <summary>Serialise to a Graph-compatible dict.</summary>
    public JsonObject ToDict()
    {
        return new JsonObject { ["parsedData"] = ParsedData };
    }
}

public class AccessControlEntry
{
    /// <summary>Initialise an ACL entry with access type, principal type, and value.</summary>
    public AccessControlEntry(string accessType, string principalType, string value)
    {
        AccessType = accessType;
        PrincipalType = principalType;
        Value = value;
    }

    public string AccessType { get; set; }

    public string PrincipalType { get; set; }

    public string Value { get; set; }

    /// <summary>Serialise to a Graph-compatible dict.</summary>
    public JsonObject ToDict()
    {
        return new JsonObject
        {
            ["accessType"] = AccessType,
            ["type"] = PrincipalType,
            ["value"] = Value,
        };
    }
}

public class SearchableItem
{
    /// <summary>Initialise a searchable item with the given Salesforce record ID.</summary>
    public SearchableItem(string itemId)
    {
        Id = itemId;
    }

    public string Id { get; set; }

    public bool ShouldHashId { get; set; } = false;

    public JsonObject Properties { get; } = new JsonObject();

    public Content? Content { get; set; }

    public List<AccessControlEntry>? Acl { get; set; }

    public string ItemType { get; set; } = "searchable";

    /// <summary>Serialise to a Graph-compatible external-item dict.</summary>
    public JsonObject ToDict()
    {
        var properties = new JsonObject();
        foreach (var pair in Properties)
        {
            properties[pair.Key] = pair.Value?.DeepClone();
        }

        var result = new JsonObject
        {
            ["id"] = Id,
            ["properties"] = properties,
            ["content"] = Content?.ToDict(),
            ["type"] = ItemType,
        };
        if (ShouldHashId)
        {
            result["shouldHashId"] = true;
        }
        if (Acl is not null)
        {
            var acl = new JsonArray();
            foreach (var entry in Acl)
            {
                acl.Add(entry.ToDict());
            }
            result["acl"] = acl;
        }
        return result;
    }
}

public class DeletedItem
{
    /// <summary>Initialise a deleted-item marker with the given record ID.</summary>
    public DeletedItem(string itemId)
    {
        Id = itemId;
    }

    public string Id { get; set; }

    public string ItemType { get; set; } = "deleted";

    /// <summary>Serialise to a dict for the Graph DELETE pathway.</summary>
    public JsonObject ToDict()
    {
        return new JsonObject { ["id"] = Id, ["type"] = ItemType };
    }
}
