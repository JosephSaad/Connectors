// Altrata/FeedModels.cs
// ---------------------
// Bulk feed delivery models. A delivery is a directory under FEED_PATH
// (files arrive via external SFTP) containing manifest.json plus one or more
// per-dataset data files (JSON array or CSV).

using System.Text.Json.Serialization;

namespace AltrataConnector.Altrata;

/// <summary>Datasets delivered by the Altrata bulk feeds.</summary>
public static class Datasets
{
    public const string PersonProfile = "PersonProfile";
    public const string Organization = "Organization";
    public const string BoardMembership = "BoardMembership";
    public const string RelationshipPath = "RelationshipPath";
    public const string WealthIndicator = "WealthIndicator";
    public const string CareerHistory = "CareerHistory";

    public static readonly string[] All =
    {
        PersonProfile, Organization, BoardMembership,
        RelationshipPath, WealthIndicator, CareerHistory,
    };

    public static bool IsKnown(string dataset) =>
        All.Contains(dataset, StringComparer.OrdinalIgnoreCase);

    /// <summary>Canonical casing for a dataset name (throws on unknown).</summary>
    public static string Canonical(string dataset) =>
        All.FirstOrDefault(d => d.Equals(dataset, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException(
            $"Unknown dataset '{dataset}'. Known datasets: {string.Join(", ", All)}");
}

public sealed record ManifestFile
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("dataset")]
    public required string Dataset { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("recordCount")]
    public required int RecordCount { get; init; }
}

public sealed record Manifest
{
    [JsonPropertyName("deliveryId")]
    public required string DeliveryId { get; init; }

    /// <summary>"full" or "incremental".</summary>
    [JsonPropertyName("deliveryType")]
    public string DeliveryType { get; init; } = "full";

    [JsonPropertyName("generatedUtc")]
    public DateTime GeneratedUtc { get; init; }

    [JsonPropertyName("files")]
    public required IReadOnlyList<ManifestFile> Files { get; init; }
}

/// <summary>One parsed feed record: dataset + raw field map.</summary>
public sealed record FeedRecord
{
    public required string Dataset { get; init; }
    public required IReadOnlyDictionary<string, string?> Fields { get; init; }

    /// <summary>Record id: first of id / altrata_id / person_id / org_id / relationship_id.</summary>
    public string? Id =>
        Get("id") ?? Get("altrata_id") ?? Get("person_id") ?? Get("org_id") ?? Get("relationship_id");

    public string? Get(string field) =>
        Fields.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>Change-operation values that mark a delta tombstone.</summary>
    private static readonly string[] DeleteOps = { "delete", "deleted", "remove", "purge" };

    /// <summary>
    /// True when a delta delivery marks this record as deleted upstream
    /// (op / action / change_type ∈ {delete, deleted, remove, purge}, or
    /// is_deleted / deleted = true). Tombstones withdraw the externalItem
    /// instead of upserting it — see docs/FEEDS.md "Delta deliveries".
    /// </summary>
    public bool IsTombstone
    {
        get
        {
            var op = Get("op") ?? Get("action") ?? Get("change_type");
            if (op != null && DeleteOps.Contains(op.Trim().ToLowerInvariant()))
                return true;
            var flag = Get("is_deleted") ?? Get("deleted");
            return flag != null &&
                   (flag.Equals("true", StringComparison.OrdinalIgnoreCase) || flag == "1");
        }
    }
}

/// <summary>A delivery on disk: manifest + directory.</summary>
public sealed record Delivery(string Directory, Manifest Manifest)
{
    public string Id => Manifest.DeliveryId;
}
