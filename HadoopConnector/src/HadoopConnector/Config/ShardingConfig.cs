// Config/ShardingConfig.cs
// ------------------------
// GRAPH_CONNECTION_SHARDS — multi-connection sharding, the throughput lever
// (docs/SHARDING.md). The Graph ingestion rate limit is PER CONNECTION, so
// spreading the BDH object types across N external connections
// multiplies aggregate write capacity.
//
// Off by default: with the env var unset, IsEnabled is false, TryLoad returns
// false with a null error, and callers take the unchanged single-connection
// path. Env format — a JSON object mapping connection id → object types:
//
//   GRAPH_CONNECTION_SHARDS={"bdhCrmCore":["Contact","Account"],
//                            "bdhCrmPipeline":["Opportunity","Case","Lead"]}
//
// Validation (all failures reported through the error out-param, never
// thrown): valid JSON object; ≥1 shard; every connection id passes the
// connector-id rules and is unique; every value is a non-empty string array;
// every listed object type exists in config/schema.json; and the shards form
// an exact partition of the schema object list (no unassigned objects, no
// object in two shards).

using System.Text.Json;
using System.Text.Json.Nodes;

namespace HadoopConnector.Config;

/// <summary>One Graph external connection and the object types it ingests.</summary>
public sealed record Shard(string ConnectionId, IReadOnlyList<string> ObjectTypes);

public static class ShardingConfig
{
    /// <summary>The environment variable that enables and configures connection sharding.</summary>
    public const string EnvVar = "GRAPH_CONNECTION_SHARDS";

    /// <summary>Cheap gate: true iff the env var is set to a non-empty value (no JSON parse).</summary>
    public static bool IsEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar));

    /// <summary>
    /// Parse and validate GRAPH_CONNECTION_SHARDS against the schema object
    /// list. True only when sharding is enabled and fully valid; false with a
    /// null error when simply disabled; false with a populated multi-line
    /// error describing EVERY problem on validation failure. Never throws for
    /// user-input problems.
    /// </summary>
    public static bool TryLoad(SchemaConfig schema, out IReadOnlyList<Shard> shards, out string? error)
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
            // JsonObject materializes lazily: a duplicated key parses but throws
            // on first access — surface as a validation error, not a crash.
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

        var schemaObjects = schema.ObjectList.Select(o => o.ObjectName).ToList();
        var schemaSet = new HashSet<string>(schemaObjects, StringComparer.OrdinalIgnoreCase);

        var problems = new List<string>();
        var parsedShards = new List<Shard>();
        var connectionIdsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assignmentOwners = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (connectionId, valueNode) in root)
        {
            if (AppConfig.ValidateConnectorId(connectionId) is { } idError)
                problems.Add($"Invalid connection id '{connectionId}': {idError}");

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
                        + "(not in the config/schema.json object list).");
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

        var unassigned = schemaObjects
            .Where(o => !assignmentOwners.ContainsKey(o))
            .ToList();
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

    /// <summary>
    /// Effective connection ids for state fan-out (health endpoint dead-letter
    /// depth, retry tooling): the shard ids when sharding is valid, otherwise
    /// just the base connector id.
    /// </summary>
    public static IReadOnlyList<string> EffectiveConnectionIds(AppConfig config, SchemaConfig schema)
    {
        if (IsEnabled && TryLoad(schema, out var shards, out _))
            return shards.Select(s => s.ConnectionId).ToList();
        return new[] { config.ConnectorId };
    }
}
