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
/// <c>Contact</c>) ingested by this shard — plain names, with any <c>#i/N</c> bucket
/// suffix already stripped. Every type exists in the base schema; a plain type is owned
/// by exactly one shard, a hash-sharded type by every shard holding one of its buckets.</param>
/// <param name="ObjectBuckets">For hash-sharded object types only: plain object name →
/// the bucket subset of that type this shard owns (see <see cref="ShardBucketSpec"/>).
/// <c>null</c>/absent for shards with no hash-sharded types.</param>
public sealed record Shard(
    string ConnectionId,
    IReadOnlyList<string> ObjectTypes,
    IReadOnlyDictionary<string, ShardBucketSpec>? ObjectBuckets = null);

/// <summary>
/// The bucket subset of one hash-sharded object type owned by one shard: this shard
/// ingests exactly the records whose <see cref="ShardHash.Bucket"/> (over
/// <see cref="BucketCount"/>) lands in <see cref="Buckets"/>.
/// </summary>
/// <param name="BucketCount">Total buckets the object type is split into (the <c>N</c> in
/// <c>Object#i/N</c>). Identical across every shard holding a bucket of the type.</param>
/// <param name="Buckets">The bucket indices this shard owns (usually one; several when an
/// operator packs multiple buckets onto one connection).</param>
public sealed record ShardBucketSpec(int BucketCount, IReadOnlySet<int> Buckets)
{
    /// <summary>Whether <paramref name="recordId"/> routes to this shard.</summary>
    public bool Owns(string recordId) => Buckets.Contains(ShardHash.Bucket(recordId, BucketCount));
}

/// <summary>
/// Deterministic record-id → bucket assignment for intra-object hash sharding.
///
/// <para><b>Stability is a contract.</b> The assignment decides which Graph connection
/// permanently owns a record — its item PUTs, deletes, inventory row and reconcile scope
/// all follow it — so the hash must never vary by process, platform or runtime version
/// (ruling out <see cref="string.GetHashCode()"/>, which is randomized per process).
/// FNV-1a 64-bit over the id's UTF-16 code units, reduced modulo the bucket count.
/// Changing this function, or a type's bucket count, is a re-shard: every affected
/// connection needs a full crawl and a <c>reconcile --fix</c> (see docs/SHARDING.md).</para>
///
/// <para><b>Canonical form.</b> Only the first 15 characters are hashed: Salesforce ids
/// come in a case-sensitive 15-char form and an 18-char form (the last 3 characters are a
/// checksum of the casing), and both refer to the same record — hashing the 15-char prefix
/// makes both forms land in the same bucket. The prefix is case-sensitive by design:
/// ids differing in case are different records.</para>
/// </summary>
public static class ShardHash
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>Bucket index in <c>[0, bucketCount)</c> for <paramref name="recordId"/>.</summary>
    public static int Bucket(string recordId, int bucketCount)
    {
        if (bucketCount < 1)
            throw new ArgumentOutOfRangeException(nameof(bucketCount), bucketCount, "bucket count must be >= 1");

        var length = Math.Min(recordId.Length, 15);
        var hash = FnvOffsetBasis;
        for (var i = 0; i < length; i++)
        {
            hash ^= recordId[i];
            hash *= FnvPrime;
        }
        return (int)(hash % (ulong)bucketCount);
    }
}

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
/// <para><b>Intra-object hash sharding.</b> An object type too large for one connection's
/// item quota is split across several connections with <c>"Object#bucket/of"</c> entries:
/// each record routes to bucket <c>ShardHash.Bucket(Id, of)</c>, deterministically, so
/// incremental updates, deletes, the ingested-item inventory and <c>reconcile</c> all stay
/// consistent per connection. Every bucket <c>0..of-1</c> must be assigned to exactly one
/// shard (a shard may own several buckets), and a type cannot be both plain and bucketed:</para>
/// <code>
/// GRAPH_CONNECTION_SHARDS={"sfCaseA":["Case#0/3"],"sfCaseB":["Case#1/3"],"sfCaseC":["Case#2/3","Lead"],...}
/// </code>
/// <para>See <c>docs/SHARDING.md</c> for capacity planning, the Salesforce fetch-cost
/// trade-off, and the re-shard procedure (changing <c>of</c> reassigns records).</para>
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

        try
        {
            // JsonObject materializes lazily: an exactly-duplicated key parses but
            // throws ArgumentException on first access. Surface it as a validation
            // error instead of crashing the command ("never throws for user input").
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

        // Base schema object universe (order preserved for stable reporting).
        var schemaObjects = baseConfig.ObjectNames;
        var schemaSet = new HashSet<string>(schemaObjects, StringComparer.Ordinal);

        var problems = new List<string>();
        var parsedShards = new List<Shard>();
        var connectionIdsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Plain assignments: objectType -> connection ids that claimed it un-bucketed.
        var plainOwners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        // Bucketed assignments: objectType -> every (connection, bucket, of) claim.
        var bucketOwners = new Dictionary<string, List<(string ConnectionId, int Bucket, int Of)>>(StringComparer.Ordinal);

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

            var objectTypes = new List<string>();                 // plain names, listed order, deduped
            var shardBuckets = new Dictionary<string, (int Of, SortedSet<int> Buckets)>(StringComparer.Ordinal);
            foreach (var element in arr)
            {
                if (element is not JsonValue v || !v.TryGetValue(out string? entry) || string.IsNullOrWhiteSpace(entry))
                {
                    problems.Add($"Shard '{connectionId}' has a non-string or empty object-type entry.");
                    continue;
                }

                if (!TryParseEntry(entry!, out var typeName, out var bucket, out var of, out var entryError))
                {
                    problems.Add($"Shard '{connectionId}': {entryError}");
                    continue;
                }

                if (bucket is null)
                {
                    // Plain (un-bucketed) assignment.
                    objectTypes.Add(typeName);
                    if (!plainOwners.TryGetValue(typeName, out var owners))
                        plainOwners[typeName] = owners = new List<string>();
                    owners.Add(connectionId);
                }
                else
                {
                    // Hash-bucket assignment: "Name#i/N".
                    if (!shardBuckets.TryGetValue(typeName, out var acc))
                    {
                        shardBuckets[typeName] = acc = (of!.Value, new SortedSet<int>());
                        objectTypes.Add(typeName);  // plain name once, so object-type filters work unchanged
                    }
                    acc.Buckets.Add(bucket.Value);  // same-shard duplicate surfaces via the global claim list
                    if (!bucketOwners.TryGetValue(typeName, out var claims))
                        bucketOwners[typeName] = claims = new List<(string, int, int)>();
                    claims.Add((connectionId, bucket.Value, of!.Value));
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
            }

            var objectBuckets = shardBuckets.Count == 0
                ? null
                : shardBuckets.ToDictionary(
                    kv => kv.Key,
                    kv => new ShardBucketSpec(kv.Value.Of, kv.Value.Buckets),
                    StringComparer.Ordinal);
            parsedShards.Add(new Shard(connectionId, objectTypes, objectBuckets));
        }

        // ── Plain objects: assigned to exactly one shard, and never ALSO bucketed ─
        foreach (var (objectType, owners) in plainOwners)
        {
            if (owners.Count > 1 && schemaSet.Contains(objectType))
            {
                problems.Add(
                    $"Object type '{objectType}' is assigned to multiple shards: " +
                    $"{string.Join(", ", owners)} (each object must map to exactly one shard).");
            }
            if (bucketOwners.ContainsKey(objectType))
            {
                problems.Add(
                    $"Object type '{objectType}' is assigned both plain and hash-bucketed " +
                    "(use one or the other for a given object type).");
            }
        }

        // ── Bucketed objects: one consistent N, and buckets 0..N-1 each exactly once ─
        foreach (var (objectType, claims) in bucketOwners)
        {
            var counts = claims.Select(c => c.Of).Distinct().ToList();
            if (counts.Count > 1)
            {
                problems.Add(
                    $"Object type '{objectType}' declares inconsistent bucket counts: " +
                    $"{string.Join(", ", counts.OrderBy(c => c))} (every '{objectType}#i/N' must use the same N).");
                continue;  // per-bucket checks are meaningless with mixed N
            }

            var of = counts[0];
            var duplicates = claims.GroupBy(c => c.Bucket).Where(g => g.Count() > 1).ToList();
            foreach (var dup in duplicates.OrderBy(d => d.Key))
            {
                problems.Add(
                    $"Bucket {objectType}#{dup.Key}/{of} is assigned to multiple shards: " +
                    $"{string.Join(", ", dup.Select(c => c.ConnectionId))} (each bucket maps to exactly one shard).");
            }

            var present = claims.Select(c => c.Bucket).ToHashSet();
            var missing = Enumerable.Range(0, of).Where(b => !present.Contains(b)).ToList();
            if (missing.Count > 0)
            {
                problems.Add(
                    $"Object type '{objectType}' is split into {of} buckets but " +
                    $"{missing.Count} are unassigned: {string.Join(", ", missing.Select(b => $"#{b}/{of}"))} " +
                    "(every bucket must map to exactly one shard — records in an unassigned bucket would never ingest).");
            }
        }

        // Unassigned: schema objects no shard claimed (plain or bucketed). Schema order.
        var unassigned = schemaObjects
            .Where(o => !plainOwners.ContainsKey(o) && !bucketOwners.ContainsKey(o))
            .ToList();
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
    /// Parse one shard object-type entry: either a plain name (<c>"Case"</c>) or a
    /// hash-bucket claim (<c>"Case#2/5"</c> — bucket 2 of 5). On success, <paramref name="bucket"/>
    /// and <paramref name="of"/> are both <c>null</c> for a plain entry and both set for a
    /// bucketed one. Object API names never contain <c>#</c>, so the delimiter is unambiguous.
    /// </summary>
    private static bool TryParseEntry(
        string entry, out string typeName, out int? bucket, out int? of, out string? error)
    {
        typeName = entry;
        bucket = null;
        of = null;
        error = null;

        var hash = entry.IndexOf('#');
        if (hash < 0)
            return true;  // plain name

        typeName = entry[..hash];
        var spec = entry[(hash + 1)..];
        var slash = spec.IndexOf('/');

        if (string.IsNullOrWhiteSpace(typeName) || slash < 0 || spec.IndexOf('#') >= 0
            || !int.TryParse(spec[..slash], out var b)
            || !int.TryParse(spec[(slash + 1)..], out var n))
        {
            error = $"malformed bucket entry '{entry}' (expected 'ObjectType#bucket/of, e.g. Case#0/5').";
            return false;
        }

        if (n < 2)
        {
            error = $"bucket entry '{entry}' has bucket count {n} — hash sharding needs at least 2 buckets " +
                    "(use the plain object name for a single connection).";
            return false;
        }

        if (b < 0 || b >= n)
        {
            error = $"bucket entry '{entry}' has bucket index {b} out of range [0, {n}).";
            return false;
        }

        bucket = b;
        of = n;
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
        return CloneWith(baseConfig, shard.ConnectionId, null, shard.ObjectTypes, shard.ObjectBuckets);
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

        // Carry the object's bucket subset (if hash-sharded) so the fetch filter still
        // restricts the single-object run to this shard's records.
        IReadOnlyDictionary<string, ShardBucketSpec>? buckets = null;
        if (shard.ObjectBuckets is not null && shard.ObjectBuckets.TryGetValue(objectType, out var spec))
            buckets = new Dictionary<string, ShardBucketSpec>(StringComparer.Ordinal) { [objectType] = spec };

        return CloneWith(baseConfig, shard.ConnectionId, objectType, null, buckets);
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
        IReadOnlyList<string>? shardObjectTypes,
        IReadOnlyDictionary<string, ShardBucketSpec>? shardObjectBuckets = null)
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
            ShardObjectBuckets = shardObjectBuckets,
        };
    }
}
