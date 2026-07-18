// Hdfs/BdhRecord.cs
// -----------------
// One BDH row: a Salesforce-shaped record read from a Hadoop data-mart file
// (CSV or JSONL). BDH mirrors Salesforce nightly, so the record id IS the
// Salesforce record Id (15/18-char alphanumeric) and the Graph external item
// id is that same value — the BDH connector and the live Salesforce connector
// share one id space (use SEPARATE Graph connections; see README).

using System.Text.Json.Nodes;

namespace HadoopConnector.Hdfs;

public sealed class BdhRecord
{
    public BdhRecord(string objectType, JsonObject fields, string? dataAsOf = null, string? sourcePath = null)
    {
        ObjectType = objectType;
        Fields = fields;
        DataAsOf = dataAsOf;
        SourcePath = sourcePath;
    }

    /// <summary>Salesforce-shaped object type (Contact, Account, Opportunity, Case, Lead, ...).</summary>
    public string ObjectType { get; }

    public JsonObject Fields { get; }

    /// <summary>Freshness marker: the Hive partition dt (yyyy-MM-dd) or file sync
    /// timestamp this row was read from. Surfaced as the refinable dataAsOf
    /// property so Copilot answers can show staleness (BDH lags Salesforce).</summary>
    public string? DataAsOf { get; }

    /// <summary>Relative source path (partition dir / file) the row came from.</summary>
    public string? SourcePath { get; }

    /// <summary>Raw Salesforce record id ("Id"/"id"/"ID" column), or empty.</summary>
    public string RawId => GetString("Id") ?? string.Empty;

    /// <summary>Graph external item id — the Salesforce record Id verbatim, so
    /// this connector is swappable with the live Salesforce connector.</summary>
    public string ItemId => RawId;

    /// <summary>True when the id is a Graph-safe token ([A-Za-z0-9_-], 1..128).</summary>
    public static bool IsSafeItemId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128)
            return false;
        foreach (var ch in id)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('_' or '-'))
                return false;
        }
        return true;
    }

    /// <summary>Case-insensitive field lookup (BDH exports vary in column casing).</summary>
    public JsonNode? Get(string field)
    {
        if (Fields.TryGetPropertyValue(field, out var node))
            return node;
        foreach (var kv in Fields)
        {
            if (string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }
        return null;
    }

    /// <summary>Field value as a string (case-insensitive lookup); null when absent.</summary>
    public string? GetString(string field)
    {
        var node = Get(field);
        return node switch
        {
            null => null,
            JsonValue value => value.TryGetValue<string>(out var s) ? s : value.ToJsonString().Trim('"'),
            JsonObject obj when obj.TryGetPropertyValue("name", out var name) && name is not null =>
                name.GetValue<string>(),
            _ => node.ToJsonString(),
        };
    }
}
