// Seismic/ItemTransformer.cs
// --------------------------
// Builds the Graph externalItem payload for one Seismic content item:
// properties (aligned with config/graph-schema.json), extracted content text,
// and resolved ACL. The externalItem id is the Seismic content id, so a new
// published version PUTs over the same id (update-in-place supersede).

using System.Globalization;
using System.Text.Json.Nodes;

namespace SeismicConnector.Seismic;

public sealed class ItemTransformer
{
    /// <summary>Cap on indexed content text (Graph rejects oversized payloads).</summary>
    internal const int MaxContentChars = 400_000;

    private readonly CompositeExtractor _extractor;
    private readonly int _ttlDays;

    /// <param name="extractor">Content text extractor (defaults to the shared composite).</param>
    /// <param name="ttlDays">GRAPH_ITEM_TTL_DAYS: when &gt; 0, stamp every item with
    /// <c>expirationDateTime = now + ttlDays</c> so the index self-expires if crawling
    /// stops. 0 = unset = no expiration stamped (current behaviour).</param>
    public ItemTransformer(CompositeExtractor? extractor = null, int ttlDays = 0)
    {
        _extractor = extractor ?? CompositeExtractor.Default;
        _ttlDays = Math.Max(0, ttlDays);
    }

    /// <summary>Cap on the number of LiveDoc field labels folded into the content text.</summary>
    internal const int MaxLiveDocFieldsInText = 500;

    /// <summary>
    /// Build the externalItem JSON. <paramref name="payload"/> is the
    /// downloaded document payload (null → metadata-only indexing);
    /// <paramref name="usage"/> carries optional engagement signals
    /// (SEISMIC_ENRICH_USAGE) indexed as refinable ranking properties;
    /// <paramref name="liveDocFields"/> carries optional LiveDoc field/variable
    /// metadata (LIVEDOC_FIELD_INDEXING) indexed as refinable properties and
    /// woven into the searchable content text.
    /// </summary>
    public JsonObject Transform(
        SeismicContent content,
        SeismicTeamsite? teamsite,
        byte[]? payload,
        AclResult acl,
        SeismicContentUsage? usage = null,
        IReadOnlyList<SeismicLiveDocField>? liveDocFields = null)
    {
        var text = payload is not null
            ? _extractor.ExtractFor(content.Format, payload)
            : string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            text = content.Description ?? string.Empty;

        var properties = new JsonObject
        {
            ["title"] = content.Name,
            ["contentId"] = content.Id,
            ["teamsiteId"] = content.TeamsiteId,
            ["teamsiteName"] = teamsite?.Name ?? "",
            ["format"] = content.Format,
            ["versionId"] = content.CurrentVersionId,
            ["distribution"] = content.Distribution,
            ["url"] = content.Url ?? "",
            ["ownerEmail"] = content.OwnerEmail ?? "",
            ["isLiveDoc"] = content.IsLiveDoc,
        };
        if (content.ModifiedAt is not null)
            properties["lastModifiedDateTime"] = Iso(content.ModifiedAt.Value);
        if (content.PublishedAt is not null)
            properties["publishedDateTime"] = Iso(content.PublishedAt.Value);
        if (content.ExpiresAt is not null)
            properties["expiresDateTime"] = Iso(content.ExpiresAt.Value);
        if (usage is not null)
        {
            properties["viewCount"] = usage.ViewCount;
            properties["downloadCount"] = usage.DownloadCount;
            properties["shareCount"] = usage.ShareCount;
            properties["popularityScore"] = usage.PopularityScore;
        }

        // LiveDoc field/variable metadata: distinct display tokens (label or
        // name), stable order preserved. Indexed both as a refinable string
        // collection AND appended to the content text so free-text search hits
        // the input names ("find the deck parameterized by 'region'").
        if (liveDocFields is { Count: > 0 })
        {
            var fieldNames = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in liveDocFields)
            {
                var token = field.DisplayName;
                if (!string.IsNullOrWhiteSpace(token) && seen.Add(token))
                    fieldNames.Add(token);
            }
            if (fieldNames.Count > 0)
            {
                properties["liveDocFieldNames"] = new JsonArray(
                    fieldNames.Select(n => (JsonNode)n).ToArray());
                properties["liveDocFieldCount"] = fieldNames.Count;
                var woven = "LiveDoc fields: "
                    + string.Join(", ", fieldNames.Take(MaxLiveDocFieldsInText));
                text = string.IsNullOrWhiteSpace(text) ? woven : text + "\n" + woven;
            }
        }

        if (text.Length > MaxContentChars)
            text = text[..MaxContentChars];

        var externalItem = new JsonObject
        {
            ["id"] = content.Id,
            ["acl"] = acl.ToJsonArray(),
            ["properties"] = properties,
            ["content"] = new JsonObject
            {
                ["type"] = "text",
                ["value"] = text,
            },
        };

        // Stale-index expiry (GRAPH_ITEM_TTL_DAYS): stamp a rolling expiration so
        // an item that stops being refreshed (crawler outage, source drift) ages
        // out of the index instead of lingering indefinitely. Each fresh crawl
        // re-stamps a new now+TTL, so live content never expires.
        if (_ttlDays > 0)
            externalItem["expirationDateTime"] = Iso(DateTime.UtcNow.AddDays(_ttlDays));

        return externalItem;
    }

    private static string Iso(DateTime value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
