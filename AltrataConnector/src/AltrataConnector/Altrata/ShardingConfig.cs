// Altrata/ShardingConfig.cs
// -------------------------
// Multi-connection sharding (GRAPH_CONNECTION_SHARDS) — the throughput lever
// from docs/SHARDING.md. A single Graph external connection is rate-limited,
// so spreading the Altrata datasets across N connections multiplies write
// capacity. OFF BY DEFAULT: when the env var is unset the connector behaves
// exactly as a single-connection deployment (strict no-op until you opt in).
//
// Env format — JSON object mapping each connection id to the datasets it owns:
//
//   GRAPH_CONNECTION_SHARDS='{
//     "altrataPeopleA":  ["PersonProfile", "CareerHistory", "BoardMembership"],
//     "altrataWealthB":  ["WealthIndicator", "RelationshipPath"],
//     "altrataOrgsC":    ["Organization"]
//   }'
//
// The shards must PARTITION the dataset list exactly: every known dataset in
// exactly one shard. TryLoad reports every problem through the error
// out-param; it never throws on bad user input.

using System.Text.Json;
using AltrataConnector.Config;

namespace AltrataConnector.Altrata;

/// <summary>One shard: a Graph connection id plus the datasets it owns.</summary>
public sealed record Shard(string ConnectionId, IReadOnlyList<string> Datasets);

public static class ShardingConfig
{
    public const string EnvVar = "GRAPH_CONNECTION_SHARDS";

    /// <summary>Cheap gate — true iff GRAPH_CONNECTION_SHARDS is set (no JSON parse).</summary>
    public static bool IsEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar));

    /// <summary>
    /// Parse + validate the shard map. Returns:
    ///   false, error == null   → sharding disabled (env unset);
    ///   false, error != null   → misconfigured — print the error and ABORT
    ///                            (never run a partial/overlapping crawl);
    ///   true                   → shards is the validated partition.
    /// </summary>
    public static bool TryLoad(out IReadOnlyList<Shard> shards, out string? error)
    {
        shards = Array.Empty<Shard>();
        error = null;
        var raw = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        return TryParse(raw, out shards, out error);
    }

    /// <summary>Pure parse/validate seam (unit-testable without env mutation).</summary>
    public static bool TryParse(string json, out IReadOnlyList<Shard> shards, out string? error)
    {
        shards = Array.Empty<Shard>();
        var problems = new List<string>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException exc)
        {
            error = $"{EnvVar} is not valid JSON: {exc.Message}";
            return false;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = $"{EnvVar} must be a JSON object of connectionId -> [datasets]";
                return false;
            }

            var parsed = new List<Shard>();
            var seenConnectionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var connectionId = property.Name;

                var idErrors = new List<string>();
                AppConfig.ValidateConnectorId(connectionId, idErrors);
                foreach (var idError in idErrors)
                    problems.Add($"shard '{connectionId}': {idError.Replace("CONNECTOR_ID", "connection id")}");
                if (!seenConnectionIds.Add(connectionId))
                    problems.Add($"connection id '{connectionId}' appears more than once");

                if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() == 0)
                {
                    problems.Add($"shard '{connectionId}' must map to a non-empty array of dataset names");
                    continue;
                }

                var datasets = new List<string>();
                foreach (var element in property.Value.EnumerateArray())
                {
                    var name = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        problems.Add($"shard '{connectionId}' contains a non-string/empty dataset entry");
                        continue;
                    }
                    if (!Datasets.IsKnown(name))
                    {
                        problems.Add($"shard '{connectionId}' lists unknown dataset '{name}' " +
                                     $"(known: {string.Join(", ", Datasets.All)})");
                        continue;
                    }
                    var canonical = Datasets.Canonical(name);
                    if (owner.TryGetValue(canonical, out var other))
                        problems.Add($"dataset '{canonical}' is assigned to both '{other}' and '{connectionId}'");
                    else
                        owner[canonical] = connectionId;
                    datasets.Add(canonical);
                }
                parsed.Add(new Shard(connectionId, datasets));
            }

            if (parsed.Count == 0)
                problems.Add($"{EnvVar} must define at least one shard");

            // Exact partition: every known dataset assigned to exactly one shard.
            foreach (var dataset in Datasets.All)
            {
                if (!owner.ContainsKey(dataset))
                    problems.Add($"dataset '{dataset}' is not assigned to any shard " +
                                 "(the shard map must partition the full dataset list)");
            }

            if (problems.Count > 0)
            {
                error = $"Invalid {EnvVar}: " + string.Join("; ", problems);
                return false;
            }

            shards = parsed;
            error = null;
            return true;
        }
    }

    /// <summary>Clone the base config bound to the shard's connection id. The
    /// connection id is also the state key, so each shard gets its own
    /// checkpoint, dead-letter queue, identity DB and seat hash.</summary>
    public static AppConfig ForShard(AppConfig baseConfig, Shard shard) =>
        baseConfig with
        {
            ConnectorId = shard.ConnectionId,
            ConnectorName = $"{baseConfig.ConnectorName} ({shard.ConnectionId})",
        };
}
