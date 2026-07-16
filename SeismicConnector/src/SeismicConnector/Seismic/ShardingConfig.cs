// Seismic/ShardingConfig.cs
// -------------------------
// GRAPH_CONNECTION_SHARDS — multi-connection sharding (docs/SHARDING.md).
//
// The Graph ingestion rate limit is PER CONNECTION, so sharding the schema
// object types across several external connections multiplies write
// throughput (k shards ≈ k × single-connection throughput). Off by default:
// when the env var is unset, IsEnabled is false, TryLoad returns false with
// no error, and callers take the unchanged single-connection path.
//
// Env format — a JSON object mapping each connection id to the object types
// it owns:
//   GRAPH_CONNECTION_SHARDS={"seismicContent":["ContentItem"],"seismicLibs":["Library"]}
//
// Validation (all failures collected into the error out-param, no exceptions):
// every connection id passes Settings.ValidateConnectorId and is unique;
// every listed object type exists in the enabled schema object list; every
// schema object is assigned to EXACTLY one shard (unassigned and
// doubly-assigned objects are both reported).

using System.Text.Json;
using System.Text.Json.Nodes;
using SeismicConnector.Config;

namespace SeismicConnector.Seismic;

/// <summary>One Graph external connection and the schema object types it ingests.</summary>
public sealed record Shard(string ConnectionId, IReadOnlyList<string> ObjectTypes);

public static class ShardingConfig
{
    /// <summary>The environment variable that enables and configures connection sharding.</summary>
    public const string EnvVar = "GRAPH_CONNECTION_SHARDS";

    /// <summary>
    /// Cheap gate: true when GRAPH_CONNECTION_SHARDS is set to a non-empty
    /// value (does not validate the JSON — call <see cref="TryLoad"/>).
    /// </summary>
    public static bool IsEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar));

    /// <summary>
    /// Parse and validate GRAPH_CONNECTION_SHARDS against the enabled schema
    /// objects of <paramref name="baseConfig"/>. Returns true only when
    /// sharding is enabled and fully valid; disabled → false with null error;
    /// invalid → false with a multi-line error naming every problem.
    /// </summary>
    public static bool TryLoad(AppConfig baseConfig, out IReadOnlyList<Shard> shards, out string? error)
    {
        shards = Array.Empty<Shard>();
        error = null;

        var raw = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(raw))
            return false;  // disabled — not an error

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(raw);
        }
        catch (JsonException ex)
        {
            error = $"{EnvVar} is not valid JSON: {ex.Message}";
            return false;
        }

        if (parsed is not JsonObject root)
        {
            error = $"{EnvVar} must be a JSON object mapping connectionId -> [objectTypes...].";
            return false;
        }

        try
        {
            // JsonObject materializes lazily: an exactly-duplicated key parses
            // but throws on first access — surface it as a validation error.
            _ = root.Count;
        }
        catch (ArgumentException ex)
        {
            error = $"{EnvVar} contains a duplicate connection id key: {ex.Message}";
            return false;
        }

        if (root.Count == 0)
        {
            error = $"{EnvVar} declares no shards (empty JSON object).";
            return false;
        }

        var schemaObjects = baseConfig.EnabledObjects;
        var schemaSet = new HashSet<string>(schemaObjects, StringComparer.Ordinal);

        var problems = new List<string>();
        var parsedShards = new List<Shard>();
        var connectionIdsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assignmentOwners = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (connectionId, valueNode) in root)
        {
            try
            {
                Settings.ValidateConnectorId(connectionId);
            }
            catch (ConfigException ex)
            {
                problems.Add($"Invalid connection id '{connectionId}': {ex.Message}");
            }

            if (!connectionIdsSeen.Add(connectionId))
                problems.Add($"Duplicate connection id '{connectionId}' (each shard needs a distinct connection).");

            if (valueNode is not JsonArray arr)
            {
                problems.Add($"Shard '{connectionId}' must map to a JSON array of object types.");
                continue;
            }

            var objectTypes = new List<string>();
            foreach (var element in arr)
            {
                if (element is JsonValue v && v.TryGetValue(out string? typeName)
                    && !string.IsNullOrWhiteSpace(typeName))
                {
                    objectTypes.Add(typeName!);
                }
                else
                {
                    problems.Add($"Shard '{connectionId}' has a non-string or empty object-type entry.");
                }
            }

            if (objectTypes.Count == 0)
            {
                problems.Add($"Shard '{connectionId}' lists no object types.");
                continue;
            }

            foreach (var objectType in objectTypes)
            {
                if (!schemaSet.Contains(objectType))
                {
                    problems.Add(
                        $"Shard '{connectionId}' references unknown object type '{objectType}' "
                        + "(not in the enabled schema object list).");
                }
                if (!assignmentOwners.TryGetValue(objectType, out var owners))
                {
                    owners = new List<string>();
                    assignmentOwners[objectType] = owners;
                }
                owners.Add(connectionId);
            }

            parsedShards.Add(new Shard(connectionId, objectTypes));
        }

        foreach (var (objectType, owners) in assignmentOwners)
        {
            if (owners.Count > 1 && schemaSet.Contains(objectType))
            {
                problems.Add(
                    $"Object type '{objectType}' is assigned to multiple shards: "
                    + $"{string.Join(", ", owners)} (each object must map to exactly one shard).");
            }
        }

        var unassigned = schemaObjects.Where(o => !assignmentOwners.ContainsKey(o)).ToList();
        if (unassigned.Count > 0)
        {
            problems.Add(
                $"{unassigned.Count} schema object(s) not assigned to any shard: "
                + $"{string.Join(", ", unassigned)} (every object must map to exactly one shard).");
        }

        if (problems.Count > 0)
        {
            error = $"Invalid {EnvVar}:" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(p => "  - " + p));
            return false;
        }

        shards = parsedShards;
        return true;
    }

    /// <summary>Clone the base config bound to the shard's Graph connection.</summary>
    public static AppConfig ForShard(AppConfig baseConfig, Shard shard) =>
        baseConfig.ForConnection(shard.ConnectionId);
}
