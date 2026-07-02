// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Graph;

namespace SalesforceCopilotConnector.Salesforce;

/// <summary>
/// One Graph external connection and the set of Salesforce object types it ingests.
///
/// A shard maps 1:1 to a Microsoft Graph external connection (its own connection id,
/// schema, ACL groups and index quota). Sharding the Salesforce object types across
/// several connections multiplies write throughput because the Graph ingestion rate
/// limit is <b>per connection</b> — see <c>docs/CAPACITY.md</c> (§6, "Multiple
/// connections") and <c>docs/SHARDING.md</c>.
/// </summary>
/// <param name="ConnectionId">The Graph external-connection id for this shard. Validated
/// with the same rules as <see cref="Settings.ValidateConnectorId"/>.</param>
/// <param name="ObjectTypes">The Salesforce object types (e.g. <c>Account</c>,
/// <c>Contact</c>) ingested by this shard. Every type exists in the base schema and is
/// owned by exactly one shard.</param>
public sealed record Shard(string ConnectionId, IReadOnlyList<string> ObjectTypes);

/// <summary>
/// Parse, validate and apply the <c>GRAPH_CONNECTION_SHARDS</c> multi-connection
/// sharding configuration (improvements-contract item #2).
///
/// <para><b>Off by default.</b> When <c>GRAPH_CONNECTION_SHARDS</c> is unset (or empty
/// whitespace) <see cref="IsEnabled"/> is <c>false</c>, <see cref="TryLoad"/> returns
/// <c>false</c> with no error, and callers fall through to the unchanged single-connection
/// path. No code path, log line or state file changes in the default configuration.</para>
///
/// <para><b>Env format.</b> <c>GRAPH_CONNECTION_SHARDS</c> is a JSON object mapping each
/// connection id to the list of object types it owns:</para>
/// <code>
/// GRAPH_CONNECTION_SHARDS={"salesforceCrmA":["Account","Contact","Opportunity"],"salesforceCrmB":["Case","Lead"]}
/// </code>
///
/// <para><b>Validation</b> (all failures reported through the <c>error</c> out-param, no
/// exceptions): every connection id passes <see cref="Settings.ValidateConnectorId"/>;
/// every listed object type exists in the base schema object list
/// (<see cref="Settings.BuildObjectNameList"/>); and every schema object is assigned to
/// <b>exactly one</b> shard — unassigned objects and objects assigned to more than one
/// shard are both reported.</para>
///
/// <para><b>How Wave 2 iterates (per-shard crawl loop).</b> The single-connection default
/// path is untouched; when sharding is enabled the orchestrator replaces the one
/// connection setup+ingest with a loop over the shards. Each shard is a fully independent
/// connection, so each iteration does the same work a single deployment does today, scoped
/// to the shard:</para>
/// <code>
/// if (ShardingConfig.TryLoad(baseConfig, out var shards, out var error))
/// {
///     var combined = new IngestionStats();
///     foreach (var shard in shards)
///     {
///         // 1. Set up THIS shard's connection + schema + search settings, exactly as the
///         //    single-connection deploy does, but against shard.ConnectionId.
///         //    (ForShard swaps Connector.Id so EnsureConnection/EnsureSchema target it.)
///         var shardBase = ShardingConfig.ForShard(baseConfig, shard);
///         await EnsureConnectionAsync(shardBase, client, ts);
///         await EnsureSchemaAsync(shardBase, client);
///         await SetSearchSettingsAsync(shardBase, client);
///
///         // 2. Ingest ONLY this shard's object types, aggregating IngestionStats.
///         //    Ingest.IngestContentAsync honors a per-config single-object restriction
///         //    via DebugObjectType, so iterate the shard's objects (see remarks on the
///         //    restriction mechanism below):
///         foreach (var objectType in shard.ObjectTypes)
///         {
///             var perObject = ShardingConfig.ForShardObject(baseConfig, shard, objectType);
///             var s = await Ingest.IngestContentAsync(perObject, client, since, dashboard);
///             ShardingConfig.Accumulate(combined, s);
///         }
///     }
/// }
/// </code>
///
/// <para><b>Object-restriction mechanism (important — no Ingest.cs edit required).</b>
/// <see cref="Ingest.IngestContentAsync"/> derives the object types it fetches and
/// ingests from <c>ApiClient.ObjectConfigs</c> (a process-wide static list) unless
/// <c>AppConfig.DebugObjectType</c> is set, in which case it restricts to that <b>single</b>
/// object type — this is the same seam the <c>ingest-object</c> command uses. It does
/// <b>not</b> read <c>AppConfig.ObjectNames</c> to decide what to ingest. Therefore the
/// only per-config object restriction the ingest pipeline honors unchanged is a single
/// object type. <see cref="ForShardObject"/> sets exactly that field, so the Wave-2 loop
/// restricts a multi-object shard by iterating its object types (one honored
/// <see cref="Ingest.IngestContentAsync"/> call each). <see cref="ForShard"/> additionally
/// sets <c>DebugObjectType</c> when a shard happens to contain exactly one object type.</para>
///
/// <para><b>Optional single-call multi-object seam (not required; documented for the
/// orchestrator).</b> If Wave 2 would rather issue one ingest call per shard instead of
/// one per object type, add a nullable <c>IReadOnlyList&lt;string&gt;? ShardObjectTypes</c>
/// field to <c>AppConfig</c> (default <c>null</c> ⇒ byte-identical default behavior) and
/// read it in three places, each a one-line change that prefers the shard set when
/// non-null:
/// <list type="bullet">
///   <item><c>Graph/Ingest.cs</c> — the <c>activeTypes</c> assignment in
///     <c>IngestContentAsync</c> ("Determine active object types"): when
///     <c>config.ShardObjectTypes != null</c>, use it instead of
///     <c>ApiClient.ObjectConfigs.Select(c =&gt; c.ObjectType)</c>.</item>
///   <item><c>Salesforce/ApiClient.cs</c> — <c>GetAllItemsFromApiAsync</c>, the
///     <c>activeConfigs</c> filter: intersect <c>ObjectConfigs</c> with the shard set.</item>
///   <item><c>Salesforce/ApiClient.cs</c> — <c>GetObjectCountsAsync</c>, the same
///     <c>activeConfigs</c> filter, so the dashboard counts only the shard's objects.</item>
/// </list>
/// This class cannot add that field itself (it lives on <c>AppConfig</c> in
/// <c>Settings.cs</c>, which item #2 must not edit). Until it exists, the per-object
/// iteration above is the self-contained mechanism and is fully honored today.</para>
/// </summary>
public static class ShardingConfig
{
    /// <summary>The environment variable that enables and configures connection sharding.</summary>
    public const string EnvVar = "GRAPH_CONNECTION_SHARDS";

    /// <summary>
    /// <c>true</c> when <c>GRAPH_CONNECTION_SHARDS</c> is set to a non-empty value.
    ///
    /// This is a cheap gate: it does not validate the JSON. When <c>false</c>, callers
    /// must take the unchanged single-connection path. When <c>true</c>, call
    /// <see cref="TryLoad"/> to parse and validate.
    /// </summary>
    public static bool IsEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar));

    /// <summary>
    /// Parse and validate <c>GRAPH_CONNECTION_SHARDS</c> against <paramref name="baseConfig"/>'s
    /// schema object list.
    ///
    /// <para>Returns <c>true</c> with a populated, validated <paramref name="shards"/> list
    /// and <c>null</c> <paramref name="error"/> on success. Returns <c>false</c> when the
    /// feature is disabled (env unset — <paramref name="error"/> is <c>null</c>, this is not
    /// an error) or when validation fails (<paramref name="error"/> describes every problem).
    /// Never throws for user-input problems.</para>
    ///
    /// <para>Validation rules (all enforced; failures collected into
    /// <paramref name="error"/>):</para>
    /// <list type="number">
    ///   <item>The value parses as a JSON object.</item>
    ///   <item>At least one shard is declared.</item>
    ///   <item>Each connection id passes <see cref="Settings.ValidateConnectorId"/>
    ///     (length 3–32, alphanumeric only, not a reserved Microsoft prefix), and connection
    ///     ids are unique across shards.</item>
    ///   <item>Each shard's value is a non-empty JSON array of strings.</item>
    ///   <item>Every listed object type exists in the base schema object list.</item>
    ///   <item>Every base-schema object is assigned to exactly one shard — unassigned and
    ///     duplicately-assigned objects are both reported.</item>
    /// </list>
    /// </summary>
    /// <param name="baseConfig">The base configuration (as produced by
    /// <see cref="Settings.LoadConfig"/>); its <see cref="AppConfig.ObjectNames"/> defines
    /// the schema object universe every shard is validated against.</param>
    /// <param name="shards">On success, the validated shards. On any non-success, an empty
    /// list.</param>
    /// <param name="error">On validation failure, a human-readable, multi-line description
    /// of every problem. <c>null</c> on success and when the feature is simply disabled.</param>
    /// <returns><c>true</c> only when sharding is enabled and fully valid.</returns>
    public static bool TryLoad(
        AppConfig baseConfig,
        out IReadOnlyList<Shard> shards,
        out string? error)
    {
        shards = Array.Empty<Shard>();
        error = null;

        var raw = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(raw))
        {
            // Disabled — not an error. Caller uses the single-connection path.
            return false;
        }

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

        if (root.Count == 0)
        {
            error = $"{EnvVar} declares no shards (empty JSON object).";
            return false;
        }

        // Base schema object universe (order preserved for stable reporting).
        var schemaObjects = baseConfig.ObjectNames;
        var schemaSet = new HashSet<string>(schemaObjects, StringComparer.Ordinal);

        var problems = new List<string>();
        var parsedShards = new List<Shard>();
        var connectionIdsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // objectType -> list of connection ids that claimed it (for duplicate detection).
        var assignmentOwners = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (connectionId, valueNode) in root)
        {
            // ── Connection id: same rules as a normal connector id ────────────────
            try
            {
                Settings.ValidateConnectorId(connectionId);
            }
            catch (ArgumentException ex)
            {
                problems.Add($"Invalid connection id '{connectionId}': {ex.Message}");
            }

            if (!connectionIdsSeen.Add(connectionId))
                problems.Add($"Duplicate connection id '{connectionId}' (each shard needs a distinct connection).");

            // ── Object-type list: must be a non-empty array of strings ────────────
            if (valueNode is not JsonArray arr)
            {
                problems.Add($"Shard '{connectionId}' must map to a JSON array of object types.");
                continue;
            }

            var objectTypes = new List<string>();
            foreach (var element in arr)
            {
                if (element is JsonValue v && v.TryGetValue(out string? typeName) && !string.IsNullOrWhiteSpace(typeName))
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

            // ── Every listed object type must exist in the base schema ────────────
            foreach (var objectType in objectTypes)
            {
                if (!schemaSet.Contains(objectType))
                {
                    problems.Add(
                        $"Shard '{connectionId}' references unknown object type '{objectType}' " +
                        "(not in the base schema object list).");
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

        // ── Every schema object assigned to exactly one shard ─────────────────────
        // Duplicates: object types claimed by more than one shard.
        foreach (var (objectType, owners) in assignmentOwners)
        {
            if (owners.Count > 1 && schemaSet.Contains(objectType))
            {
                problems.Add(
                    $"Object type '{objectType}' is assigned to multiple shards: " +
                    $"{string.Join(", ", owners)} (each object must map to exactly one shard).");
            }
        }

        // Unassigned: schema objects no shard claimed. Reported in schema order.
        var unassigned = schemaObjects.Where(o => !assignmentOwners.ContainsKey(o)).ToList();
        if (unassigned.Count > 0)
        {
            problems.Add(
                $"{unassigned.Count} schema object(s) not assigned to any shard: " +
                $"{string.Join(", ", unassigned)} (every object must map to exactly one shard).");
        }

        if (problems.Count > 0)
        {
            error = $"Invalid {EnvVar}:" + Environment.NewLine +
                    string.Join(Environment.NewLine, problems.Select(p => "  - " + p));
            return false;
        }

        shards = parsedShards;
        return true;
    }

    /// <summary>
    /// Clone <paramref name="baseConfig"/> for <paramref name="shard"/>: the returned config
    /// targets the shard's Graph connection (<c>Connector.Id = shard.ConnectionId</c>) and,
    /// when the shard contains exactly one object type, restricts ingestion to it via
    /// <c>DebugObjectType</c>.
    ///
    /// <para>Use this to set up the shard's connection and schema (the connection id flows
    /// into <c>EnsureConnection</c>/<c>EnsureSchema</c>/<c>SetSearchSettings</c> and every
    /// Graph item URL through <c>config.Connector.Id</c>). For a multi-object shard, call
    /// <see cref="ForShardObject"/> per object type to ingest — see the class remarks for
    /// why the ingest pipeline honors only a single-object per-config restriction.</para>
    /// </summary>
    /// <param name="baseConfig">The base configuration to clone.</param>
    /// <param name="shard">The shard whose connection id (and, if singular, object type) to apply.</param>
    /// <returns>A clone bound to the shard's connection.</returns>
    public static AppConfig ForShard(AppConfig baseConfig, Shard shard)
    {
        return CloneWith(baseConfig, shard.ConnectionId, null, shard.ObjectTypes);
    }

    /// <summary>
    /// Clone <paramref name="baseConfig"/> for one object type of <paramref name="shard"/>:
    /// the returned config targets the shard's Graph connection and restricts ingestion to
    /// <paramref name="objectType"/> via <c>DebugObjectType</c>.
    ///
    /// <para>This is the building block the Wave-2 loop uses to ingest a multi-object shard:
    /// iterate <c>shard.ObjectTypes</c>, and for each one pass the result to
    /// <see cref="Ingest.IngestContentAsync"/> — it fetches, resolves ACLs for, transforms
    /// and pushes only that object type, into the shard's connection. Aggregate the returned
    /// <see cref="IngestionStats"/> across the object types (and shards) with
    /// <see cref="Accumulate"/>.</para>
    /// </summary>
    /// <param name="baseConfig">The base configuration to clone.</param>
    /// <param name="shard">The shard whose connection id to apply.</param>
    /// <param name="objectType">The single object type to restrict ingestion to. Must be one
    /// of <paramref name="shard"/>'s object types.</param>
    /// <returns>A clone bound to the shard's connection and restricted to <paramref name="objectType"/>.</returns>
    /// <exception cref="ArgumentException">If <paramref name="objectType"/> is not owned by the shard.</exception>
    public static AppConfig ForShardObject(AppConfig baseConfig, Shard shard, string objectType)
    {
        if (!shard.ObjectTypes.Contains(objectType))
        {
            throw new ArgumentException(
                $"Object type '{objectType}' is not owned by shard '{shard.ConnectionId}'.",
                nameof(objectType));
        }
        return CloneWith(baseConfig, shard.ConnectionId, objectType, null);
    }

    /// <summary>
    /// Fold the per-shard / per-object <paramref name="source"/> stats into
    /// <paramref name="target"/> so the Wave-2 loop can report one combined
    /// <see cref="IngestionStats"/> across every shard.
    ///
    /// Counters are summed; failed-id samples, per-object-type counts, phase timings and the
    /// ACL-fallback flag are merged. The ACL-engine label is carried over from the first
    /// non-empty source. Existing single-connection reporting is unaffected (nothing calls
    /// this on the default path).
    /// </summary>
    public static void Accumulate(IngestionStats target, IngestionStats source)
    {
        target.TotalFetched += source.TotalFetched;
        target.SuccessCount += source.SuccessCount;
        target.FailedCount += source.FailedCount;
        target.DeletedCount += source.DeletedCount;
        target.SkippedCount += source.SkippedCount;
        target.FailedIds.AddRange(source.FailedIds);
        target.AclFallbackUsed |= source.AclFallbackUsed;
        if (target.AclEngine == "LEGACY" && source.AclEngine != "LEGACY")
            target.AclEngine = source.AclEngine;

        foreach (var (objType, count) in source.ObjectTypeCounts)
            target.ObjectTypeCounts[objType] = target.ObjectTypeCounts.GetValueOrDefault(objType, 0) + count;

        foreach (var (objType, phases) in source.PhaseTimings)
            foreach (var (phase, timing) in phases)
                target.RecordPhaseTime(objType, phase, timing.TotalSecs);
    }

    /// <summary>
    /// Clone an <see cref="AppConfig"/> through its public surface, swapping the connector id
    /// and (optionally) the single-object <c>DebugObjectType</c> restriction. Every other
    /// field is carried over by reference, matching <c>CommandRegistry.ReplaceConfig</c>
    /// (the caches, maps and schema are read-only and safe to share).
    ///
    /// A private clone helper lives here (rather than reusing <c>CommandRegistry.ReplaceConfig</c>)
    /// because that helper cannot change <c>Connector.Id</c>, which is the whole point of a
    /// shard. <see cref="AppConfig"/> and <see cref="ConnectorSettings"/> are init-only, so we
    /// build fresh instances via object initializers.
    /// </summary>
    private static AppConfig CloneWith(
        AppConfig baseConfig,
        string connectionId,
        string? debugObjectType,
        IReadOnlyList<string>? shardObjectTypes)
    {
        var baseConnector = baseConfig.Connector;
        var shardConnector = new ConnectorSettings
        {
            Id = connectionId,
            Name = baseConnector.Name,
            Description = baseConnector.Description,
            Schema = baseConnector.Schema,
            Template = baseConnector.Template,
            Salesforce = baseConnector.Salesforce,
        };

        return new AppConfig
        {
            ClientId = baseConfig.ClientId,
            TenantId = baseConfig.TenantId,
            Connector = shardConnector,
            RepoRoot = baseConfig.RepoRoot,
            Tuning = baseConfig.Tuning,
            SchemaConfig = baseConfig.SchemaConfig,
            OwdFieldMap = baseConfig.OwdFieldMap,
            ParentMap = baseConfig.ParentMap,
            OwdOverrides = baseConfig.OwdOverrides,
            ObjectNames = baseConfig.ObjectNames,
            UseNewAclEngine = baseConfig.UseNewAclEngine,
            UseGroupAcl = baseConfig.UseGroupAcl,
            UseEntityDefinitionOwd = baseConfig.UseEntityDefinitionOwd,
            DebugObjectType = debugObjectType ?? baseConfig.DebugObjectType,
            DebugItemId = baseConfig.DebugItemId,
            ShardObjectTypes = shardObjectTypes,
        };
    }
}
