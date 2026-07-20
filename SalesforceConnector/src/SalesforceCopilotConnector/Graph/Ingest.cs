// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.AclEngine;
using SalesforceCopilotConnector.Config;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Item;
using SalesforceCopilotConnector.Salesforce;
using NewAclResolver = SalesforceCopilotConnector.AclEngine.AclResolver;

namespace SalesforceCopilotConnector.Graph;

/// <summary>Tracks ingestion outcomes for summary reporting.</summary>
public class IngestionStats
{
    public int TotalFetched { get; set; }
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public int DeletedCount { get; set; }
    public int SkippedCount { get; set; }

    /// <summary>Items withdrawn by the automatic post-full-crawl deletion sweep (Reconciler).</summary>
    public int SweepDeletedCount { get; set; }

    /// <summary>Object types the deletion sweep's mass-deletion safety guard skipped this crawl.</summary>
    public List<string> SweepSkipped { get; } = new();

    public List<string> FailedIds { get; } = new();
    public Dictionary<string, int> ObjectTypeCounts { get; } = new();
    public string AclEngine { get; set; } = "LEGACY";
    public bool AclFallbackUsed { get; set; }
    // {obj_type: {phase: (total_secs, chunk_count)}}
    public Dictionary<string, Dictionary<string, (double TotalSecs, int ChunkCount)>> PhaseTimings { get; } = new();
    private readonly object _timingLock = new();

    /// <summary>Accumulate timing for a pipeline phase (thread-safe).</summary>
    public void RecordPhaseTime(string objType, string phase, double duration)
    {
        lock (_timingLock)
        {
            if (!PhaseTimings.ContainsKey(objType))
                PhaseTimings[objType] = new Dictionary<string, (double, int)>();
            var prev = PhaseTimings[objType].TryGetValue(phase, out var existing) ? existing : (0.0, 0);
            PhaseTimings[objType][phase] = (prev.Item1 + duration, prev.Item2 + 1);
        }
    }
}

/// <summary>Tracks concurrency level: dials down on 429, dials up on sustained success.</summary>
internal sealed class AdaptiveConcurrency
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector");

    private readonly int _max;
    // volatile: Current is read outside _lock by worker loops while
    // OnSuccess/OnThrottle mutate it under the lock.
    private volatile int _current;
    private int _successStreak;
    private readonly object _lock = new();

    public AdaptiveConcurrency(int maxWorkers)
    {
        _max = Math.Max(1, maxWorkers);
        _current = _max;
        _successStreak = 0;
        Metrics.SetAdaptiveConcurrency(_current);
    }

    public int Current => _current;

    public void OnSuccess()
    {
        lock (_lock)
        {
            _successStreak += 1;
            // Ramp up after 3 consecutive successes (not 10) to recover quickly
            if (_successStreak >= 3 && _current < _max)
            {
                _current += 1;
                _successStreak = 0;
                Metrics.SetAdaptiveConcurrency(_current);
                Logger.Info($"Graph concurrency ramped up to {_current}");
            }
        }
    }

    public void OnThrottle()
    {
        Metrics.IncThrottle429();
        lock (_lock)
        {
            var prev = _current;
            _current = Math.Max(1, _current - 1);
            _successStreak = 0;
            Metrics.SetAdaptiveConcurrency(_current);
            if (_current != prev)
                Logger.Warning($"Graph 429 throttling — concurrency reduced to {_current}");
        }
    }
}

/// <summary>
/// Item ingestion pipeline — Salesforce → Graph external items.
///
/// Orchestrates the full ingestion flow:
///
/// <list type="number">
/// <item><b>Fetch</b> — queries all Salesforce objects defined in <c>config/schema.json</c>
///   via the Salesforce REST API (supports full and incremental sync).</item>
/// <item><b>ACL resolution</b> — for each record, resolves the Salesforce sharing model
///   (OWD, roles, groups, territories, sharing rules, parent chains) into
///   Microsoft Graph ACL entries.  Two engines are available:
///   <i>Legacy</i> (<see cref="AclResolver"/>) — default;
///   <i>New</i> (<c>AclEngine</c>) — enabled by setting <c>USE_NEW_ACL_ENGINE=true</c>.</item>
/// <item><b>Transform</b> — converts each Salesforce record + ACLs into a Graph
///   <c>externalItem</c> payload using <see cref="SalesforceItemTransformer"/>.</item>
/// <item><b>Upsert / delete</b> — PUTs each item into the external connection (or
///   DELETEs it if the transformer marks it as <c>deleted</c>).</item>
/// </list>
///
/// <para><b>Debug modes</b></para>
/// <para><c>DEBUG_ITEM_ID</c> — set via the <c>single-item</c> command.  Restricts ingestion
/// to a single Salesforce record ID.</para>
/// <para><c>DEBUG_OBJECT_TYPE</c> — set via the <c>single-object</c> command.  Restricts
/// ingestion to one Salesforce object type (e.g. <c>Case</c>, <c>Account</c>).</para>
///
/// <para><b>Key functions</b></para>
/// <para><see cref="IngestContentAsync"/> — main entry point.  Called by every command
/// that performs ingestion.</para>
/// <para><see cref="LoadContentAsync"/> — PUTs a single transformed item into the Graph
/// external connection.</para>
/// <para><see cref="DeleteContentAsync"/> — DELETEs a single item from the Graph
/// external connection.</para>
/// </summary>
public static class Ingest
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector");
    private static readonly IAppLogger Progress = Logging.GetLogger("progress");

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    // ── Test seams ────────────────────────────────────────────────────────────
    // The Python tests monkeypatch the module-level names ``get_object_config``,
    // ``iter_object_chunks``, ``LegacyAclResolver`` and ``SalesforceItemTransformer``
    // in graph.ingest; these internal hooks are the C# equivalent and default to
    // the real implementations.
    internal static Func<string, SalesforceObjectConfig?> GetObjectConfigHook =
        ApiClient.GetObjectConfig;
    internal static Func<AppConfig, SalesforceObjectConfig, DateTime?, int, IAsyncEnumerable<List<JsonObject>>> IterObjectChunksHook =
        ApiClient.IterObjectChunksAsync;
    internal static Func<AppConfig, Dictionary<string, SalesforceObjectHandler>, GraphClient, AclResolver> LegacyAclResolverFactory =
        (config, handlers, client) => new AclResolver(config, handlers, graphClient: client);
    internal static Func<string, JsonArray, string, SalesforceItemTransformer> TransformerFactory =
        (instanceUrl, schema, tenantId) => new SalesforceItemTransformer(instanceUrl, schema, tenantId: tenantId);
    internal static Func<AppConfig, DateTime?, Task<Dictionary<string, int>>> GetObjectCountsHook =
        ApiClient.GetObjectCountsAsync;
    // WP-SF-2 seam: resolves field-level security and pushes the drop set into the
    // converter handlers before any record is converted.
    internal static Func<FlsFetcher, Dictionary<string, SalesforceObjectHandler>, string, Task> FlsEnforcementHook =
        async (fetcher, handlers, connectorId) =>
            await FlsEnforcement.ApplyAsync(fetcher, handlers, connectorId);
    // HA seam (docs/SQL_CONTRACT.md): when non-null, the per-object worker loop
    // pulls object types from the returned work source (coordinator claims)
    // instead of the static schema list. Null (default) → behavior unchanged.
    internal static Func<AppConfig, List<string>, Task<IObjectWorkSource>>? ObjectWorkSourceFactory;

    // Inventory seams (reconcile support): the primary content-ingest path
    // (IngestCommand/Deploy) sets these to record successful item PUTs / deletes
    // into the ingested-item inventory that `reconcile` compares against the live
    // Salesforce source. Null (default) → no inventory writes (e.g. ingest-item /
    // ingest-object, and tests that don't opt in).
    internal static Action<IReadOnlyList<(string ItemId, string ObjectType)>>? InventoryRecordHook;
    internal static Action<IReadOnlyList<string>>? InventoryDeleteHook;

    // Track sample items for detailed logging
    private static readonly HashSet<string> SampleItemsLoggedByType = new();
    private static readonly object SampleItemsLock = new();
    private const int FailedIdsDisplayLimit = 100;

    /// <summary>PUT a single transformed item into the Graph external connection.</summary>
    public static async Task LoadContentAsync(AppConfig config, GraphClient client, JsonObject item)
    {
        var itemId = item["id"]!.ToString();
        var payload = new JsonObject();
        foreach (var (key, value) in item)
        {
            if (key != "id")
                payload[key] = value?.DeepClone();
        }
        var url = $"{GraphClient.ExternalConnectionsPath}/{config.Connector.Id}/items/{Uri.EscapeDataString(itemId)}";

        // Log sample item request/response for first item of each object type
        var objectType = (item["properties"] as JsonObject)?["ObjectName"]?.ToString();
        bool isNewSampleType;
        lock (SampleItemsLock)
        {
            isNewSampleType = !string.IsNullOrEmpty(objectType) && !SampleItemsLoggedByType.Contains(objectType!);
            if (isNewSampleType)
                SampleItemsLoggedByType.Add(objectType!);
        }
        if (isNewSampleType)
        {
            Logger.Info($"SAMPLE ITEM REQUEST: {objectType} (ID: {itemId})");
            Logger.Info($"PUT {url}");
            Logger.Info("\nRequest Payload:");
            Logger.Info(payload.ToJsonString(IndentedJson));
        }
        else if (Logger.IsEnabledFor(LogLevels.Debug))
        {
            Logger.Debug($"ITEM REQUEST: {itemId} | PUT {url}");
        }

        Logger.Debug($"PUT {url}");

        try
        {
            var response = await client.PutAsync(
                url,
                jsonBody: payload,
                headers: new Dictionary<string, string> { ["content-type"] = "application/json" });

            // Log response for sample items
            bool logResponse;
            lock (SampleItemsLock)
            {
                logResponse = !string.IsNullOrEmpty(objectType)
                    && SampleItemsLoggedByType.Contains(objectType!)
                    && SampleItemsLoggedByType.Count <= 6;
            }
            if (logResponse)
            {
                Logger.Info("\nResponse:");
                var responseBody = response != null && !(response is JsonObject emptyObj && emptyObj.Count == 0)
                    ? response
                    : new JsonObject { ["status"] = "success" };
                Logger.Info(JsonSerializer.Serialize(responseBody, IndentedJson));
                Logger.Info(new string('=', 80) + "\n");
            }
        }
        catch (GraphApiError error)
        {
            Logger.Error($"Failed to load {itemId}: {error.Message}");
            if (error.Body != null)
                Logger.Error($"Graph response: {error.Body}");
            throw;
        }
    }

    /// <summary>DELETE a single item from the Graph external connection.</summary>
    public static async Task DeleteContentAsync(AppConfig config, GraphClient client, string itemId)
    {
        var url = $"{GraphClient.ExternalConnectionsPath}/{config.Connector.Id}/items/{Uri.EscapeDataString(itemId)}";
        Logger.Info($"DELETE {url}");

        try
        {
            await client.DeleteAsync(url);
        }
        catch (GraphApiError error)
        {
            Logger.Error($"Failed to delete {itemId}: {error.Message}");
            if (error.Body != null)
                Logger.Error($"Graph response: {error.Body}");
        }
    }

    // ── New ACL engine helper ─────────────────────────────────────────────────

    /// <summary>
    /// Resolve ACLs for all records using the new AclEngine pipeline.
    ///
    /// Flow
    /// ----
    /// 1. For each record, call AclResolver.ResolveAsync(objectType, recordId)
    ///    concurrently (per object type).
    /// 2. Collect the union of all Salesforce User IDs across every record.
    /// 3. PrincipalMapper bulk-fetches user identity fields (FederationIdentifier /
    ///    UserName / Email) in a single SOQL per 100 IDs.
    /// 4. For each record's AclResult, call PrincipalMapper.ToAclEntriesAsync() to
    ///    produce the final Graph-API-ready ACL list.
    ///
    /// When <paramref name="resolver"/> and <paramref name="mapper"/> are provided they are
    /// reused across calls so that the PrincipalMapper's identity cache persists between chunks.
    ///
    /// Returns
    /// -------
    /// {object_type: {record_id: [acl_entry, ...]}}  – same shape as the legacy
    /// AclResolver so the rest of IngestContentAsync is unchanged.
    /// </summary>
    private static async Task<Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>>> ResolveAclNewEngineAsync(
        AppConfig config,
        GraphClient graphClient,
        Dictionary<string, List<JsonObject>> recordsByObjectType,
        NewAclResolver? resolver = null,
        PrincipalMapper? mapper = null)
    {
        if (resolver == null || mapper == null)
        {
            var sfClient = new SalesforceClient(
                instanceUrl: config.Connector.Salesforce.InstanceUrl,
                apiVersion: config.Connector.Salesforce.ApiVersion,
                accessToken: await ApiClient.GetSalesforceAccessTokenAsync(config));
            resolver = new NewAclResolver(
                sfClient,
                owdFieldMap: config.OwdFieldMap,
                parentMap: config.ParentMap,
                owdOverrides: config.OwdOverrides,
                maxParentDepth: config.Tuning.AclMaxParentDepth,
                useEntityDefinitionOwd: config.UseEntityDefinitionOwd,
                objectNames: config.ObjectNames);
            mapper = new PrincipalMapper(
                sfClient: sfClient,
                graphClient: graphClient,
                tenantId: config.TenantId,
                batchSize: config.Tuning.SalesforceBatchSize);
        }

        var aclMapByObject = new Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>>();

        foreach (var (objectType, records) in recordsByObjectType)
        {
            Logger.Info($"[NewACL] Resolving {records.Count} {objectType} record(s)");

            // Bulk pre-warm: fetch all owner IDs, share entries, groups, roles for
            // this chunk in ~5 SOQL calls total instead of O(N) per-record calls.
            var recordIds = records
                .Where(r => !string.IsNullOrEmpty(r["Id"]?.ToString()))
                .Select(r => r["Id"]!.ToString())
                .ToList();
            await resolver.PrewarmChunkAsync(objectType, recordIds);

            // Pre-warm PrincipalMapper user-identity details for all owners that
            // will likely appear in this chunk (eliminates per-record SOQL in mapper).
            // Use the owner IDs already in the share_fetcher cache as a first approximation.
            var ownerIds = resolver.ShareFetcher.OwnerCache.Values
                .Where(v => !string.IsNullOrEmpty(v))
                .Select(v => v!)
                .ToHashSet();
            if (ownerIds.Count > 0)
                await mapper.PrewarmUsersAsync(ownerIds);

            // Configurable concurrency — after pre-warm most work is in-memory so
            // raising this to 500 is safe and gives a large throughput boost.
            var aclConcurrency = int.Parse(
                Environment.GetEnvironmentVariable("ACL_RESOLVE_CONCURRENCY") ?? "500",
                CultureInfo.InvariantCulture);
            using var semaphore = new SemaphoreSlim(aclConcurrency);

            async Task<AclResult> ResolveWithSem(string ot, string rid)
            {
                await semaphore.WaitAsync();
                try
                {
                    return await resolver.ResolveAsync(ot, rid);
                }
                finally
                {
                    semaphore.Release();
                }
            }

            // Resolve all records for this object type concurrently
            var tasks = records
                .Where(record => !string.IsNullOrEmpty(record["Id"]?.ToString()))
                .Select(record => ResolveWithSem(objectType, record["Id"]!.ToString()))
                .ToList();
            var aclResults = new List<object>();  // AclResult | Exception (gather with return_exceptions=True)
            foreach (var task in tasks)
            {
                try
                {
                    aclResults.Add(await task);
                }
                catch (Exception exc)
                {
                    aclResults.Add(exc);
                }
            }

            var objectAcl = new Dictionary<string, List<Dictionary<string, string>>>();
            var validPairs = new List<(string RecordId, AclResult Result)>();
            // Per-record failures usually share one root cause — attach the full
            // exception (type + stack) to the FIRST failure only so the log stays
            // readable while still carrying one complete diagnostic.
            var resolveStackLogged = false;
            for (var i = 0; i < records.Count && i < aclResults.Count; i++)
            {
                var record = records[i];
                var aclResult = aclResults[i];
                var recordId = record["Id"]!.ToString();
                if (aclResult is Exception resolveError)
                {
                    var resolveMsg =
                        $"[NewACL] resolve_async failed for {objectType}/{recordId}: {resolveError.Message} – using deny-everyone ACL";
                    if (!resolveStackLogged)
                    {
                        resolveStackLogged = true;
                        Logger.Error(resolveMsg, resolveError);
                    }
                    else
                    {
                        Logger.Error(resolveMsg);
                    }
                    objectAcl[recordId] = DenyAllAclEntry();
                }
                else
                {
                    validPairs.Add((recordId, (AclResult)aclResult));
                }
            }

            // Run to_acl_entries() concurrently — it was previously sequential
            // which serialised all Graph/SOQL lookups across 2000 records.
            if (validPairs.Count > 0)
            {
                var entryTasks = validPairs.Select(pair => mapper.ToAclEntriesAsync(pair.Result)).ToList();
                var aclEntryResults = new List<object>();  // List<Dictionary<string,string>> | Exception
                foreach (var task in entryTasks)
                {
                    try
                    {
                        aclEntryResults.Add(await task);
                    }
                    catch (Exception exc)
                    {
                        aclEntryResults.Add(exc);
                    }
                }
                // Same first-failure-carries-the-stack pattern as resolve_async above.
                var entryStackLogged = false;
                for (var i = 0; i < validPairs.Count && i < aclEntryResults.Count; i++)
                {
                    var (recordId, _) = validPairs[i];
                    if (aclEntryResults[i] is Exception entryError)
                    {
                        var entryMsg =
                            $"[NewACL] to_acl_entries failed for {objectType}/{recordId}: {entryError.Message} — using deny-everyone ACL";
                        if (!entryStackLogged)
                        {
                            entryStackLogged = true;
                            Logger.Error(entryMsg, entryError);
                        }
                        else
                        {
                            Logger.Error(entryMsg);
                        }
                        objectAcl[recordId] = DenyAllAclEntry();
                    }
                    else
                    {
                        objectAcl[recordId] = (List<Dictionary<string, string>>)aclEntryResults[i];
                    }
                }
            }

            // Free the raw ACL result objects — only the mapped entries are needed downstream
            aclResults.Clear();

            aclMapByObject[objectType] = objectAcl;
            Logger.Info($"[NewACL] {objectType}: resolved {objectAcl.Count} ACL(s)");
        }

        return aclMapByObject;
    }

    /// <summary>
    /// Return a deny-everyone ACL when ACL resolution fails.
    ///
    /// Items ingested with this ACL will not appear in any user's search results.
    /// This is the safe default — it prevents accidental data exposure when ACL
    /// resolution fails.
    /// </summary>
    private static List<Dictionary<string, string>> DenyAllAclEntry()
    {
        return new List<Dictionary<string, string>>
        {
            new Dictionary<string, string>
            {
                ["accessType"] = "deny",
                ["type"] = "everyone",
                ["value"] = "everyone",
            },
        };
    }

    /// <summary>
    /// Best-effort append to the #11 decision ledger from the ingest hot path.
    ///
    /// The access-affecting ACL decision this line RECORDS has already been applied
    /// to the item's ACL by the time we get here, so the audit write must never be
    /// able to take content ingestion down with it: a ledger fault (a corrupt /
    /// tampered chain — <see cref="InvalidDataException"/> — or a disk/permission
    /// IOException) previously propagated out of the transform loop, aborting the
    /// whole chunk and silently dropping every record in it (never dead-lettered,
    /// surfacing only as a mis-attributed "Graph upload failed" and a COUNT
    /// MISMATCH). The item still ingests with its restricted ACL; the failure is
    /// logged loudly with the subject id + full exception, and <c>Verify()</c>
    /// still reports the chain as broken, so the break is NOT silently tolerated.
    /// </summary>
    private static void TryAppendDecision(
        DecisionLedger? ledger, string decision, string itemId, string? objectType, string reason)
    {
        if (ledger == null)
            return;
        try
        {
            ledger.Append(decision, itemId, objectType, reason);
        }
        catch (Exception ex)
        {
            Logger.Error(
                $"[DecisionLedger] Failed to record '{decision}' decision for {objectType}/{itemId} " +
                "— the ACL decision was still applied and the item is ingested; the audit line was NOT " +
                $"written and the ledger may need repair (run Verify): {ex.GetType().Name}: {ex.Message}",
                ex);
        }
    }

    // ###Dead code. Can be removed
    /// <summary>
    /// Yield <c>(object_type, chunk)</c> tuples where <i>chunk</i> is at most <paramref name="chunkSize"/>
    /// records of the same object type.
    ///
    /// Records stream directly from the Salesforce API page-by-page.  Only a
    /// single chunk is buffered at a time — peak RAM is therefore
    /// <c>chunk_size × avg_record_size</c> rather than
    /// <c>total_records × avg_record_size</c>.
    ///
    /// Object-type filtering (<c>DEBUG_OBJECT_TYPE</c>) is handled upstream in
    /// <c>GetAllItemsFromApiAsync</c> — only the matching thread is started so no
    /// wasted Salesforce API calls occur here.
    ///
    /// <c>DEBUG_ITEM_ID</c> filtering is applied here because all object threads must
    /// run (we don't know which object owns the ID) and we need to drop every
    /// non-matching record before it enters the ingest pipeline.
    /// </summary>
    private static async IAsyncEnumerable<(string ObjectType, List<JsonObject> Chunk)> IterRecordChunksAsync(
        AppConfig config,
        DateTime? since,
        int chunkSize)
    {
        var debugItemId = config.DebugItemId;

        string? currentType = null;
        var buffer = new List<JsonObject>();

        await foreach (var record in ApiClient.GetAllItemsFromApiAsync(config, since))
        {
            var objectType = record["objectType"]?.ToString() ?? "";

            // When the object type changes, flush whatever is in the buffer
            if (objectType != currentType)
            {
                if (buffer.Count > 0 && !string.IsNullOrEmpty(currentType))
                    yield return (currentType!, buffer);
                currentType = objectType;
                buffer = new List<JsonObject>();
            }

            // DEBUG: skip individual records that don't match the requested ID
            if (!string.IsNullOrEmpty(debugItemId) && record["Id"]?.ToString() != debugItemId)
                continue;

            buffer.Add(record);

            // Flush whenever the chunk is full — this is the key memory-saving step
            if (buffer.Count >= chunkSize)
            {
                yield return (currentType!, buffer);
                buffer = new List<JsonObject>();
            }
        }

        // Flush the final partial chunk
        if (buffer.Count > 0 && !string.IsNullOrEmpty(currentType))
            yield return (currentType!, buffer);
    }

    /// <summary>
    /// Transform <paramref name="records"/> and push them to Graph using JSON batching.
    ///
    /// Sub-batches (max 20 items each) are sent <b>concurrently</b>.  The concurrency
    /// level is controlled by <paramref name="concurrency"/> (<see cref="AdaptiveConcurrency"/>):
    /// on 429 throttling it dials down; on sustained success it dials back up.
    ///
    /// Returns the total number of items submitted (success + failure).
    /// </summary>
    // internal so tests can call it directly (Python tests call ``_ingest_chunk_graph_batch``).
    internal static async Task<int> IngestChunkGraphBatchAsync(
        AppConfig config,
        GraphClient client,
        SalesforceItemTransformer transformer,
        List<JsonObject> records,
        Dictionary<string, List<Dictionary<string, string>>> aclMap,
        IngestionStats stats,
        int batchSize,
        string? dlPath = null,
        string objectType = "",
        IngestionDashboard? dashboard = null,
        AdaptiveConcurrency? concurrency = null,
        object? statsLock = null,
        DecisionLedger? decisionLedger = null)
    {
        // 1. Transform all records into Graph external-item payloads.
        // Per-crawl policy snapshots (read once, not per record): classification
        // enforcement (#6b), the ACL scale guard (#9) and item TTL (#8) are all
        // no-ops unless their env vars are configured.
        var classificationPolicy = Classification.Policy.Read();
        var scaleGuardPolicy = AclScaleGuard.Policy.Read();
        var ttlDays = ItemExpiry.TtlDays;
        var ingestNow = DateTimeOffset.UtcNow;

        var transformSw = Stopwatch.StartNew();
        var itemsToProcess = new List<JsonObject>();
        var transformFailures = new List<(string ItemId, string Error)>();
        foreach (var record in records)
        {
            var itemId = record["Id"]?.ToString() ?? "unknown";
            var acl = aclMap.GetValueOrDefault(itemId);
            if (acl == null)
            {
                Logger.Warning(
                    $"No ACL found for item {itemId} ({record["objectType"]?.ToString()}) — using fallback ACL");
            }

            // #6b — classification enforcement (advisory by default). Only narrows the
            // ACL to the configured Entra group when enforcement is active AND the
            // record is top-tier ("Restricted"); otherwise the ACL is untouched.
            var classification = Classification.Evaluate(classificationPolicy, record, acl);
            if (classification.Restricted)
            {
                acl = classification.Acl;
                Metrics.IncClassificationAclRestricted();
                Logger.Info(
                    $"[Classification] {objectType}/{itemId}: tag '{classification.Tag}' is top-tier — " +
                    $"ACL restricted to Entra group '{classificationPolicy.RestrictedGroup}' (CLASSIFICATION_ENFORCE_ACL).");
                TryAppendDecision(
                    decisionLedger,
                    DecisionKinds.AclRestriction, itemId, objectType,
                    $"classification tag '{classification.Tag}' restricted to Entra group {classificationPolicy.RestrictedGroup}");
            }

            // #9 — large-group ACL scale guard. Always records the ACE-count metric;
            // only alters the ACL when it fires with a group/fallback action.
            var guard = AclScaleGuard.Apply(scaleGuardPolicy, itemId, objectType, acl);
            if (guard.Fired)
            {
                if (guard.Action == AclScaleGuardActions.Group)
                {
                    acl = guard.Acl;
                    TryAppendDecision(
                        decisionLedger,
                        DecisionKinds.AclRestriction, itemId, objectType,
                        $"ACL scale guard: {guard.AceCount} ACE(s) > {scaleGuardPolicy.Threshold} — collapsed to group {scaleGuardPolicy.Group}");
                }
                else if (guard.Action == AclScaleGuardActions.Fallback)
                {
                    acl = guard.Acl;
                    TryAppendDecision(
                        decisionLedger,
                        DecisionKinds.Exclusion, itemId, objectType,
                        $"ACL scale guard: {guard.AceCount} ACE(s) > {scaleGuardPolicy.Threshold} — deny-everyone fallback");
                }
            }

            try
            {
                var transformedItems = transformer.TransformRecord(record, acl);
                if (transformedItems == null || transformedItems.Count == 0)
                {
                    Logger.Warning(
                        $"[{objectType}] transform_record returned empty for item {itemId} — record dropped");
                    transformFailures.Add((itemId, $"[Transform] Returned empty result for {objectType}"));
                    continue;
                }
                foreach (var transformed in transformedItems)
                {
                    // #8 — stamp expirationDateTime (skip DELETE markers). No-op when
                    // GRAPH_ITEM_TTL_DAYS is unset.
                    if (transformed["type"]?.ToString() != "deleted")
                        ItemExpiry.ApplyExpiry(transformed, ingestNow, ttlDays);
                    itemsToProcess.Add(transformed);
                }
            }
            catch (Exception exc)
            {
                // Full exception (type + stack) in the log; the dead-letter record
                // below carries type + message for the retry tooling.
                Logger.Error($"[{objectType}] transform_record failed for item {itemId}: {exc.Message}", exc);
                transformFailures.Add((itemId, $"[Transform] {exc.GetType().Name}: {exc.Message}"));
            }
        }

        if (transformFailures.Count > 0)
        {
            lock (statsLock!)
            {
                stats.FailedCount += transformFailures.Count;
                foreach (var (fid, _) in transformFailures)
                {
                    if (stats.FailedIds.Count < FailedIdsDisplayLimit)
                        stats.FailedIds.Add(fid);
                }
            }
            if (!string.IsNullOrEmpty(dlPath))
                SyncState.AppendFailedRecords(dlPath, transformFailures, objectType);
            Logger.Warning(
                $"[{objectType}] {transformFailures.Count} record(s) failed during transform and were written to dead-letter file");
        }

        var transformDur = transformSw.Elapsed.TotalSeconds;

        if (itemsToProcess.Count == 0)
            return 0;

        Logger.Info(string.Format(
            CultureInfo.InvariantCulture,
            "[TIMING] Transform {0}: {1:F1}s for {2} records → {3} items ({4:F0} rec/s)",
            objectType, transformDur, records.Count, itemsToProcess.Count,
            transformDur > 0 ? records.Count / transformDur : 0));
        dashboard?.RecordPhaseTime(objectType, "Transform", transformDur);
        stats.RecordPhaseTime(objectType, "Transform", transformDur);

        var effectiveBatch = Math.Min(batchSize, GraphClient.GraphBatchMaxSize);

        // 2. Build all sub-batch payloads up front
        var subBatches = new List<(List<JsonObject> Items, List<JsonObject> Payload)>();
        for (var start = 0; start < itemsToProcess.Count; start += effectiveBatch)
        {
            var batchItems = itemsToProcess.Skip(start).Take(effectiveBatch).ToList();
            var payload = new List<JsonObject>();
            for (var idx = 0; idx < batchItems.Count; idx++)
            {
                var item = batchItems[idx];
                var itemUrl =
                    $"{GraphClient.ExternalConnectionsPath}/{config.Connector.Id}" +
                    $"/items/{Uri.EscapeDataString(item["id"]!.ToString())}";
                if (item["type"]?.ToString() == "deleted")
                {
                    payload.Add(new JsonObject
                    {
                        ["id"] = idx.ToString(),
                        ["method"] = "DELETE",
                        ["url"] = itemUrl,
                    });
                }
                else
                {
                    var body = new JsonObject();
                    foreach (var (k, v) in item)
                    {
                        if (k != "id")
                            body[k] = v?.DeepClone();
                    }
                    payload.Add(new JsonObject
                    {
                        ["id"] = idx.ToString(),
                        ["method"] = "PUT",
                        ["url"] = itemUrl,
                        ["headers"] = new JsonObject { ["Content-Type"] = "application/json" },
                        ["body"] = body,
                    });
                    if (!string.IsNullOrEmpty(config.DebugItemId))
                    {
                        Logger.Info(
                            $"ITEM REQUEST — PUT {itemUrl}\nRequest Body:\n{body.ToJsonString(IndentedJson)}");
                    }
                    else if (Logger.IsEnabledFor(LogLevels.Debug))
                    {
                        Logger.Debug($"ITEM REQUEST — PUT {itemUrl}");
                    }
                }
            }
            subBatches.Add((batchItems, payload));
        }

        // 3. Send sub-batches concurrently with adaptive throttling
        var submitted = 0;
        statsLock ??= new object();

        var maxItemRetries = config.Tuning.GraphMaxRetries;
        var backoffBase = config.Tuning.GraphRetryBackoffBase;

        static string ExtractError(int status, JsonObject resp)
        {
            var body = resp.ContainsKey("body") ? resp["body"] : new JsonObject();
            if (body is JsonObject bodyObj)
            {
                var errNode = bodyObj.ContainsKey("error") ? bodyObj["error"] : new JsonObject();
                var errMsg = errNode is JsonObject errObj
                    ? errObj["message"]?.ToString() ?? ""
                    : bodyObj.ToJsonString();
                var errCode = errNode is JsonObject codeObj ? codeObj["code"]?.ToString() ?? "" : "";
                return $"[Graph] HTTP {status}: {errCode} -- {errMsg}".TrimEnd(' ', '-');
            }
            return $"[Graph] HTTP {status}: {body?.ToString() ?? "None"}";
        }

        async Task SendOneAsync(List<JsonObject> batchItems, List<JsonObject> payload)
        {
            // Items and payload may shrink on retry (only 429s are re-sent)
            var curItems = new List<JsonObject>(batchItems);
            var curPayload = new List<JsonObject>(payload);
            var totalOk = 0;
            var allFailed = new List<(string ItemId, string Error)>();
            var sawThrottle = false;
            var responses = new List<JsonObject>();
            var failedRequestBodies = new Dictionary<string, JsonNode?>();
            var failedResponseBodies = new Dictionary<string, JsonNode?>();
            var exhaustedRetries = true;

            for (var attempt = 0; attempt <= maxItemRetries; attempt++)
            {
                if (attempt > 0)
                {
                    // Jitter (GRAPH_RETRY_JITTER=true, off by default) applies only to the
                    // COMPUTED backoff — the server's Retry-After below is honoured exactly.
                    var wait = RetryDelay.Jitter(backoffBase * Math.Pow(2, attempt - 1));
                    string? retryAfter = null;
                    // Use Retry-After from first 429 response if available
                    foreach (var r in responses)
                    {
                        if ((r["status"]?.GetValue<int>() ?? 0) == 429)
                        {
                            if (r["headers"] is JsonObject headers)
                                retryAfter = headers["Retry-After"]?.ToString() ?? headers["retry-after"]?.ToString();
                            break;
                        }
                    }
                    if (!string.IsNullOrEmpty(retryAfter)
                        && double.TryParse(retryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var retryAfterSecs))
                    {
                        wait = Math.Max(wait, retryAfterSecs);
                    }
                    // Hard cap: never wait more than 60s regardless of Retry-After
                    const int maxRetryWait = 60;
                    if (wait > maxRetryWait)
                    {
                        Logger.Warning(string.Format(
                            CultureInfo.InvariantCulture,
                            "Retry-After of {0:F0}s exceeds cap; clamping to {1}s",
                            wait, maxRetryWait));
                        wait = maxRetryWait;
                    }
                    Logger.Warning(string.Format(
                        CultureInfo.InvariantCulture,
                        "Retrying {0} throttled items in {1:F0}s (attempt {2}/{3})",
                        curItems.Count, wait, attempt, maxItemRetries));
                    await Task.Delay(TimeSpan.FromSeconds(wait));
                }

                try
                {
                    responses = await client.BatchRequestsAsync(curPayload) ?? new List<JsonObject>();
                    if (!string.IsNullOrEmpty(config.DebugItemId))
                    {
                        Logger.Info(
                            $"ITEM RESPONSE (attempt {attempt}):\n{JsonSerializer.Serialize(responses, IndentedJson)}");
                    }
                    else if (Logger.IsEnabledFor(LogLevels.Debug))
                    {
                        // The isEnabledFor gate (from the Python original) matters here:
                        // serializing the full $batch response array is the hot path's
                        // most expensive log message, and DEBUG is filtered in normal runs.
                        Logger.Debug(
                            $"BATCH RESPONSE (attempt {attempt}): {JsonSerializer.Serialize(responses, IndentedJson)}");
                    }
                }
                catch (Exception exc)
                {
                    Logger.Error(
                        $"[{objectType}] Graph $batch call failed for {curItems.Count} item(s) on attempt " +
                        $"{attempt}/{maxItemRetries} — dead-lettering the whole sub-batch: {exc.Message}",
                        exc);
                    var failedIds = new List<string>();
                    lock (statsLock!)
                    {
                        foreach (var item in curItems)
                        {
                            stats.FailedCount += 1;
                            var fid = item["id"]?.ToString() ?? "unknown";
                            failedIds.Add(fid);
                            if (stats.FailedIds.Count < FailedIdsDisplayLimit)
                                stats.FailedIds.Add(fid);
                        }
                        submitted += curItems.Count;
                    }
                    if (!string.IsNullOrEmpty(dlPath))
                        SyncState.AppendFailedRecords(dlPath, failedIds, objectType, $"[Graph] $batch POST failed: {exc.Message}");
                    dashboard?.ChunkIngested(objectType, totalOk, failedIds.Count + allFailed.Count);
                    return;
                }

                // Sort responses into ok / retry (429) / permanent failure
                var okThisRound = 0;
                var retryItems = new List<JsonObject>();
                var retryPayload = new List<JsonObject>();
                var idToItem = new Dictionary<string, JsonObject>();
                for (var j = 0; j < curItems.Count; j++)
                    idToItem[j.ToString()] = curItems[j];
                var idToReq = new Dictionary<string, JsonObject>();
                for (var j = 0; j < curPayload.Count; j++)
                    idToReq[j.ToString()] = curPayload[j];
                failedRequestBodies = new Dictionary<string, JsonNode?>();
                failedResponseBodies = new Dictionary<string, JsonNode?>();
                var accountedIds = new HashSet<string>();  // Track which items got a response

                lock (statsLock!)
                {
                    if (responses.Count == 0)
                    {
                        // Graph returned empty/null response — all items unaccounted
                        Logger.Error(
                            $"Graph $batch returned empty response for {curItems.Count} items — marking all as failed");
                        foreach (var item in curItems)
                        {
                            stats.FailedCount += 1;
                            var fid = item["id"]?.ToString() ?? "unknown";
                            allFailed.Add((fid, "[Graph] $batch returned empty response"));
                            if (stats.FailedIds.Count < FailedIdsDisplayLimit)
                                stats.FailedIds.Add(fid);
                        }
                    }
                    else
                    {
                        var ingestedPairs = new List<(string ItemId, string ObjectType)>();
                        var deletedIds = new List<string>();
                        foreach (var resp in responses)
                        {
                            var idxStr = resp["id"]?.ToString() ?? "";
                            var item = idToItem.GetValueOrDefault(idxStr);
                            var status = resp["status"]?.GetValue<int>() ?? 0;
                            if (item == null)
                            {
                                Logger.Warning(
                                    $"Graph $batch response has unrecognised id '{idxStr}' — cannot match to a submitted item");
                                continue;
                            }

                            accountedIds.Add(idxStr);

                            if (200 <= status && status < 300)
                            {
                                if (item["type"]?.ToString() == "deleted")
                                {
                                    stats.DeletedCount += 1;
                                    deletedIds.Add(item["id"]!.ToString());
                                }
                                else
                                {
                                    stats.SuccessCount += 1;
                                    ingestedPairs.Add((
                                        item["id"]!.ToString(),
                                        (item["properties"] as JsonObject)?["ObjectName"]?.ToString() ?? ""));
                                }
                                okThisRound += 1;
                            }
                            else if (status == 429)
                            {
                                sawThrottle = true;
                                // Queue for retry — don't count as failed yet
                                retryItems.Add(item);
                                var req = idToReq.GetValueOrDefault(idxStr);
                                if (req != null)
                                    retryPayload.Add(req);
                            }
                            else if (status == 503)
                            {
                                // Transient Graph outage — retry without signalling
                                // throttle (503 is not a rate-limit, don't penalise
                                // adaptive concurrency for it).
                                retryItems.Add(item);
                                var req = idToReq.GetValueOrDefault(idxStr);
                                if (req != null)
                                    retryPayload.Add(req);
                                Logger.Warning($"Graph 503 on item {item["id"]?.ToString() ?? "?"} — will retry");
                            }
                            else
                            {
                                // Permanent failure — capture request + response for debugging
                                stats.FailedCount += 1;
                                var fid = item["id"]?.ToString() ?? "unknown";
                                var detail = ExtractError(status, resp);
                                allFailed.Add((fid, detail));
                                if (stats.FailedIds.Count < FailedIdsDisplayLimit)
                                    stats.FailedIds.Add(fid);
                                var req = idToReq.GetValueOrDefault(idxStr);
                                if (req != null)
                                    failedRequestBodies[fid] = req.ContainsKey("body") ? req["body"] : new JsonObject();
                                failedResponseBodies[fid] = resp;
                                Logger.Error($"Graph batch item {fid} failed — {detail}");
                            }
                        }

                        // Detect items that got NO response at all (missing from Graph batch response)
                        foreach (var (idxStr, item) in idToItem)
                        {
                            if (!accountedIds.Contains(idxStr))
                            {
                                stats.FailedCount += 1;
                                var fid = item["id"]?.ToString() ?? "unknown";
                                allFailed.Add((fid, "[Graph] No response received for this item in $batch"));
                                if (stats.FailedIds.Count < FailedIdsDisplayLimit)
                                    stats.FailedIds.Add(fid);
                                Logger.Error(
                                    $"Graph batch item {fid} — no response received (missing from $batch response)");
                            }
                        }

                        // Inventory bookkeeping (reconcile): mirror the Success/Deleted
                        // accounting above so the ingested-item inventory tracks live Graph
                        // state. Runs under statsLock — single-writer to the SQLite inventory.
                        if (ingestedPairs.Count > 0)
                            InventoryRecordHook?.Invoke(ingestedPairs);
                        if (deletedIds.Count > 0)
                            InventoryDeleteHook?.Invoke(deletedIds);
                    }
                }

                totalOk += okThisRound;

                if (retryItems.Count == 0)
                {
                    exhaustedRetries = false;
                    break;  // No 429s — done
                }

                // Renumber payload IDs for the retry batch
                for (var newIdx = 0; newIdx < retryPayload.Count; newIdx++)
                    retryPayload[newIdx]["id"] = newIdx.ToString();
                curItems = retryItems;
                curPayload = retryPayload;
            }

            if (exhaustedRetries)
            {
                // Exhausted all retries — mark remaining 429s as permanent failures
                lock (statsLock!)
                {
                    foreach (var item in curItems)
                    {
                        stats.FailedCount += 1;
                        var fid = item["id"]?.ToString() ?? "unknown";
                        allFailed.Add((fid, "[Graph] HTTP 429: throttled after all retries"));
                        if (stats.FailedIds.Count < FailedIdsDisplayLimit)
                            stats.FailedIds.Add(fid);
                        Logger.Error($"Graph batch item {fid} failed — 429 after {maxItemRetries} retries");
                    }
                }
            }

            lock (statsLock!)
            {
                submitted += batchItems.Count;
            }

            if (allFailed.Count > 0 && !string.IsNullOrEmpty(dlPath))
            {
                SyncState.AppendFailedRecords(
                    dlPath, allFailed, objectType,
                    requestBodies: failedRequestBodies,
                    responseBodies: failedResponseBodies);
            }
            if (dashboard != null)
            {
                dashboard.ChunkIngested(objectType, totalOk, allFailed.Count);
                foreach (var (fid, detail) in allFailed)
                    dashboard.AddError($"{objectType}/{fid} -- {detail}");
            }

            // Adaptive concurrency feedback
            if (concurrency != null)
            {
                if (sawThrottle)
                    concurrency.OnThrottle();
                else
                    concurrency.OnSuccess();
            }
        }

        void HandleSubBatchCrash(List<JsonObject> failedBatch, Exception exc)
        {
            Logger.Error(
                $"[{objectType}] Unexpected error in _send_one for sub-batch — " +
                $"{failedBatch.Count} item(s) in this sub-batch may be lost: {exc.Message}",
                exc);
            var sbFailed = failedBatch
                .Select(it => (it["id"]?.ToString() ?? "unknown", $"[Graph] sub-batch crash: {exc.Message}"))
                .ToList();
            lock (statsLock!)
            {
                foreach (var it in failedBatch)
                {
                    stats.FailedCount += 1;
                    var fid = it["id"]?.ToString() ?? "unknown";
                    if (stats.FailedIds.Count < FailedIdsDisplayLimit)
                        stats.FailedIds.Add(fid);
                }
                submitted += failedBatch.Count;
            }
            if (!string.IsNullOrEmpty(dlPath))
                SyncState.AppendFailedRecords(dlPath, sbFailed, objectType);
        }

        // Dispatch sub-batches with adaptive parallelism
        var graphSw = Stopwatch.StartNew();
        var i = 0;
        while (i < subBatches.Count)
        {
            var workers = concurrency?.Current ?? 1;
            var window = subBatches.Skip(i).Take(workers).ToList();
            if (workers <= 1)
            {
                foreach (var (batchItems, payload) in window)
                {
                    try
                    {
                        await SendOneAsync(batchItems, payload);
                    }
                    catch (Exception exc)
                    {
                        HandleSubBatchCrash(batchItems, exc);
                    }
                }
            }
            else
            {
                var futures = window
                    .Select(w => (Task: SendOneAsync(w.Items, w.Payload), Items: w.Items))
                    .ToList();
                foreach (var future in futures)
                {
                    try
                    {
                        await future.Task;
                    }
                    catch (Exception exc)
                    {
                        HandleSubBatchCrash(future.Items, exc);
                    }
                }
            }
            i += window.Count;
        }

        var graphDur = graphSw.Elapsed.TotalSeconds;
        Logger.Info(string.Format(
            CultureInfo.InvariantCulture,
            "[TIMING] Graph push {0}: {1:F1}s for {2} items in {3} sub-batches ({4:F0} items/s, concurrency={5})",
            objectType, graphDur, itemsToProcess.Count, subBatches.Count,
            graphDur > 0 ? itemsToProcess.Count / graphDur : 0,
            concurrency?.Current ?? 1));
        dashboard?.RecordPhaseTime(objectType, "Graph Push", graphDur);
        stats.RecordPhaseTime(objectType, "Graph Push", graphDur);

        itemsToProcess.Clear();
        return submitted;
    }

    /// <summary>
    /// Ingest all chunks for a single Salesforce object type.
    ///
    /// Designed to run as one of several concurrent object workers so multiple
    /// object types can be ingested in parallel.  All shared state (<paramref name="stats"/>,
    /// <paramref name="concurrency"/>, <paramref name="dashboard"/>) is thread-safe.
    ///
    /// Returns the number of items submitted to the Graph API.
    /// </summary>
    private static async Task<int> IngestSingleObjectTypeAsync(
        string objectType,
        AppConfig config,
        GraphClient client,
        SalesforceItemTransformer transformer,
        AclResolver? legacyResolver,
        NewAclResolver? newAclResolver,
        PrincipalMapper? newAclMapper,
        GroupAclBuilder? groupAclBuilder,
        IngestionStats stats,
        AdaptiveConcurrency concurrency,
        DateTime? since,
        JsonObject? checkpoint,
        string? sinceIso,
        string? dlPath,
        IngestionDashboard? dashboard,
        object statsLock,
        DecisionLedger? decisionLedger = null)
    {
        var useNewAclEngine = config.UseNewAclEngine;
        var useGroupAcl = config.UseGroupAcl;
        var chunkSize = config.Tuning.IngestChunkSize;
        var graphBatchSize = config.Tuning.IngestGraphBatchSize;
        var connectorId = config.Connector.Id;

        var objConfig = GetObjectConfigHook(objectType);
        if (objConfig == null)
        {
            Logger.Warning($"No config found for object type '{objectType}' — skipping");
            return 0;
        }

        var ingestedCount = 0;
        var chunkIndex = 0;
        var debugItemId = config.DebugItemId;

        // Pipeline: overlap Graph upload of chunk N with ACL resolution of chunk N+1
        Task<int>? pendingGraphFuture = null;
        var pendingChunkIndex = 0;  // chunk # the pending upload belongs to (log context only)
        var fetchSw = Stopwatch.StartNew();  // track SF fetch time

        try
        {
            await foreach (var fetchedRecords in IterObjectChunksHook(config, objConfig, since, chunkSize))
            {
                var fetchDur = fetchSw.Elapsed.TotalSeconds;
                var records = fetchedRecords;

                // ── Debug: single item filter ─────────────────────────────────
                if (!string.IsNullOrEmpty(debugItemId))
                {
                    records = records.Where(r => r["Id"]?.ToString() == debugItemId).ToList();
                    if (records.Count == 0)
                        continue;
                    // Log the raw Salesforce record for single-item debugging
                    Logger.Info(
                        $"SALESFORCE RECORD — {objectType}/{debugItemId}\n{records[0].ToJsonString(IndentedJson)}");
                }

                // ── Graceful stop (dashboard Ctrl+X or service stop) ──────────
                if ((dashboard != null && dashboard.StopRequested) || ServiceStop.Requested)
                {
                    if (pendingGraphFuture != null)
                    {
                        ingestedCount += await pendingGraphFuture;
                        pendingGraphFuture = null;
                    }
                    break;
                }

                chunkIndex += 1;
                var batchSize = records.Count;
                lock (statsLock)
                {
                    stats.TotalFetched += batchSize;
                    stats.ObjectTypeCounts[objectType] =
                        stats.ObjectTypeCounts.GetValueOrDefault(objectType, 0) + batchSize;
                }
                Metrics.AddObjectFetched(objectType, batchSize);

                Logger.Info(
                    "\n" + new string('=', 70) +
                    $"\nSALESFORCE CHUNK: {objectType} chunk #{chunkIndex} ({batchSize} records)\n" +
                    new string('=', 70));
                Logger.Info(string.Format(
                    CultureInfo.InvariantCulture,
                    "[TIMING] SF fetch {0} chunk #{1}: {2:F1}s for {3} records",
                    objectType, chunkIndex, fetchDur, batchSize));
                Progress.Info($"  [{objectType}] chunk #{chunkIndex} — fetched {batchSize} record(s)");
                if (dashboard != null)
                {
                    dashboard.ChunkFetched(objectType, chunkIndex, batchSize);
                    dashboard.RecordPhaseTime(objectType, "SF Fetch", fetchDur);
                }
                stats.RecordPhaseTime(objectType, "SF Fetch", fetchDur);

                // ── Checkpoint: skip already-completed chunks ─────────────────
                if (checkpoint != null)
                {
                    var completedUpTo =
                        (checkpoint["completed"] as JsonObject)?[objectType]?.GetValue<int>() ?? 0;
                    if (chunkIndex <= completedUpTo)
                    {
                        Logger.Info($"Skipping {objectType} chunk #{chunkIndex} (already checkpointed)");
                        lock (statsLock)
                        {
                            stats.SkippedCount += batchSize;
                        }
                        dashboard?.ChunkSkipped(objectType, batchSize);
                        continue;
                    }
                }

                // ── ACL resolution for this chunk ─────────────────────────────
                var aclMapForChunk = new Dictionary<string, List<Dictionary<string, string>>>();
                try
                {
                    Logger.Info($"Resolving ACLs for {batchSize} {objectType} record(s) (chunk #{chunkIndex})");
                    dashboard?.AclStarted(objectType, chunkIndex, batchSize);
                    var aclSw = Stopwatch.StartNew();
                    Dictionary<string, Dictionary<string, List<Dictionary<string, string>>>> aclMapByObject;
                    if (useGroupAcl)
                    {
                        aclMapByObject = await groupAclBuilder!.ResolveAsync(
                            new Dictionary<string, List<JsonObject>> { [objectType] = records });
                    }
                    else if (useNewAclEngine)
                    {
                        aclMapByObject = await ResolveAclNewEngineAsync(
                            config, client,
                            new Dictionary<string, List<JsonObject>> { [objectType] = records },
                            resolver: newAclResolver, mapper: newAclMapper);
                    }
                    else
                    {
                        aclMapByObject = await legacyResolver!.ResolveAsync(
                            new Dictionary<string, List<JsonObject>> { [objectType] = records });
                    }
                    aclMapForChunk = aclMapByObject.GetValueOrDefault(objectType)
                                     ?? new Dictionary<string, List<Dictionary<string, string>>>();
                    aclMapByObject.Clear();
                    var aclDur = aclSw.Elapsed.TotalSeconds;
                    Logger.Info(string.Format(
                        CultureInfo.InvariantCulture,
                        "[TIMING] ACL resolution {0} chunk #{1}: {2:F1}s for {3} records ({4:F0} rec/s) → {5} entries",
                        objectType, chunkIndex, aclDur, batchSize,
                        aclDur > 0 ? batchSize / aclDur : 0,
                        aclMapForChunk.Count));
                    dashboard?.RecordPhaseTime(objectType, "ACL", aclDur);
                    stats.RecordPhaseTime(objectType, "ACL", aclDur);
                }
                catch (Exception error)
                {
                    Logger.Error(
                        $"ACL resolution failed for {objectType} chunk #{chunkIndex} ({batchSize} records), " +
                        $"falling back to deny-everyone ACL: {error.Message}",
                        error);
                    lock (statsLock)
                    {
                        stats.AclFallbackUsed = true;
                    }
                    // Log every affected item so operators can identify which items
                    // received deny-everyone ACLs and need re-ingestion.
                    var affectedIds = records.Select(r => r["Id"]?.ToString() ?? "unknown").ToList();
                    var aclFallbackFailures = affectedIds
                        .Select(rid => (rid, $"[ACL] Resolution failed — deny-everyone ACL fallback applied: {error.Message}"))
                        .ToList();
                    if (!string.IsNullOrEmpty(dlPath))
                        SyncState.AppendFailedRecords(dlPath, aclFallbackFailures, objectType);
                    Logger.Warning(
                        $"[{objectType}] {affectedIds.Count} item(s) in chunk #{chunkIndex} received " +
                        "deny-everyone ACL fallback due to ACL engine error");
                }

                // ── Wait for previous chunk's Graph upload (pipeline) ─────────
                if (pendingGraphFuture != null)
                {
                    var waitSw = Stopwatch.StartNew();
                    try
                    {
                        ingestedCount += await pendingGraphFuture;
                    }
                    catch (Exception exc)
                    {
                        Logger.Error(
                            $"[{objectType}] Graph upload of previous chunk #{pendingChunkIndex} failed — " +
                            $"continuing with next chunk: {exc.Message}",
                            exc);
                    }
                    var waitDur = waitSw.Elapsed.TotalSeconds;
                    if (waitDur > 0.5)
                    {
                        Logger.Info(string.Format(
                            CultureInfo.InvariantCulture,
                            "[TIMING] Pipeline wait {0} chunk #{1}: {2:F1}s (ACL was faster than Graph push)",
                            objectType, chunkIndex, waitDur));
                    }
                    pendingGraphFuture = null;
                }

                // ── Transform + push this chunk via Graph JSON batching ───────
                dashboard?.SetActivity($"[{objectType}] chunk #{chunkIndex} -- Pushing to Graph API");

                var chunkRecords = records;
                var chunkAcl = aclMapForChunk;
                var chunkOt = objectType;
                var chunkCi = chunkIndex;

                pendingGraphFuture = Task.Run(async () =>
                {
                    var submitted = await IngestChunkGraphBatchAsync(
                        config, client, transformer, chunkRecords, chunkAcl, stats, graphBatchSize,
                        dlPath: dlPath, objectType: chunkOt, dashboard: dashboard,
                        concurrency: concurrency, statsLock: statsLock, decisionLedger: decisionLedger);
                    try
                    {
                        SyncState.WriteCheckpoint(connectorId, sinceIso, chunkOt, chunkCi);
                    }
                    catch (Exception cpExc)
                    {
                        // The chunk's items WERE submitted; without this line the outer
                        // "Graph upload failed" catch would misattribute a state-file/SQL
                        // write fault to the Graph push. Rethrow — handling is unchanged.
                        Logger.Error(
                            $"[{chunkOt}] Failed to write checkpoint for chunk #{chunkCi} (connector {connectorId}) " +
                            $"after a successful Graph push — a resume may re-ingest this chunk: {cpExc.Message}",
                            cpExc);
                        throw;
                    }
                    chunkAcl.Clear();
                    return submitted;
                });
                pendingChunkIndex = chunkCi;
                fetchSw.Restart();  // reset for next SF fetch measurement
            }

            // ── Drain last pending upload ─────────────────────────────────────
            if (pendingGraphFuture != null)
            {
                try
                {
                    ingestedCount += await pendingGraphFuture;
                }
                catch (Exception exc)
                {
                    Logger.Error(
                        $"[{objectType}] Graph upload of final chunk #{pendingChunkIndex} failed: {exc.Message}", exc);
                }
                pendingGraphFuture = null;
            }
        }
        finally
        {
            // Mirror the Python pipeline pool's shutdown(wait=True)
            if (pendingGraphFuture != null)
            {
                try
                {
                    await pendingGraphFuture;
                }
                catch
                {
                    // Exceptions from the pending future are not re-raised by shutdown()
                }
            }
        }

        dashboard?.ObjectDone(objectType);

        Logger.Info($"[{objectType}] Object ingestion complete — {ingestedCount} items submitted");
        return ingestedCount;
    }

    /// <summary>
    /// Ingest content from Salesforce — <b>parallel by object type</b>.
    ///
    /// Each Salesforce object type (User, Account, Contact, …) is ingested by
    /// an independent worker.  Workers share the Graph API concurrency
    /// budget (<see cref="AdaptiveConcurrency"/>), the ACL resolver caches, and the
    /// dashboard.
    ///
    /// The number of concurrent object workers is controlled by the
    /// <c>PARALLEL_OBJECT_WORKERS</c> environment variable (default 3).
    /// </summary>
    public static async Task<IngestionStats> IngestContentAsync(
        AppConfig config,
        GraphClient client,
        DateTime? since = null,
        IngestionDashboard? dashboard = null)
    {
        var stats = new IngestionStats();
        var statsLock = new object();
        Logger.Info("Starting ingestion process...");

        // #11 — immutable decision ledger (append-only, hash-chained). Created once
        // per crawl and shared by every object worker so the chain is single. Null
        // unless DECISION_LEDGER is enabled ⇒ no decision recording, byte-identical.
        var decisionLedger = DecisionLedger.CreateIfEnabled(config.Connector.Id);
        if (decisionLedger != null)
            Logger.Info($"Decision ledger enabled — access decisions recorded to {decisionLedger.Path}");

        if (since != null)
            Logger.Info($"Incremental sync from: {PyIsoFormat(since.Value)}");
        else
            Logger.Info("Full sync (all items)");

        var useNewAclEngine = config.UseNewAclEngine;
        var useGroupAcl = config.UseGroupAcl;
        if (useGroupAcl)
            stats.AclEngine = "GROUP (group_acl_builder)";
        else if (useNewAclEngine)
            stats.AclEngine = "NEW (acl_engine)";
        else
            stats.AclEngine = "LEGACY";

        var chunkSize = config.Tuning.IngestChunkSize;
        var graphBatchSize = config.Tuning.IngestGraphBatchSize;
        var maxWorkers = config.Tuning.ParallelObjectWorkers;
        var concurrency = new AdaptiveConcurrency(config.Tuning.GraphConcurrentBatches);
        Logger.Info(
            $"Batching config — chunk_size={chunkSize}, graph_batch_size={graphBatchSize} (max 20 per $batch), " +
            $"concurrent_batches={concurrency.Current}, parallel_objects={maxWorkers}, acl_engine={stats.AclEngine}");

        var transformer = TransformerFactory(
            config.Connector.Salesforce.InstanceUrl,
            config.Connector.Schema,
            config.TenantId);

        // ── WP-SF-2: field-level security ────────────────────────────────────────
        // Resolve which fields Salesforce would hide and withhold them from the
        // index BEFORE any record is converted. Defaults ON (FLS_ENFORCEMENT) in
        // strict mode (FLS_MODE) — see AclEngine/FlsFetcher.cs for the decision rule.
        if (FlsSettings.Enforcement)
        {
            try
            {
                var flsSfClient = new SalesforceClient(
                    instanceUrl: config.Connector.Salesforce.InstanceUrl,
                    apiVersion: config.Connector.Salesforce.ApiVersion,
                    accessToken: await ApiClient.GetSalesforceAccessTokenAsync(config),
                    tokenRefresher: () => ApiClient.GetSalesforceAccessTokenAsync(config).GetAwaiter().GetResult());
                using var flsStore = IdentityStore.CreateStore(config.Connector.Id);
                await FlsEnforcementHook(
                    new FlsFetcher(flsSfClient, flsStore, config.Connector.Salesforce.InstanceUrl),
                    transformer.Handlers,
                    config.Connector.Id);
            }
            catch (Exception exc)
            {
                // Deliberate posture: an FLS metadata hiccup logs LOUDLY and lets the
                // crawl proceed rather than halting all ingestion. The operator's
                // manual `flsFields` list still applies, but the fetched restrictions
                // do NOT — so this run can index fields Salesforce would hide. The
                // in-band failure modes (FieldPermissions unreadable, scope query
                // unreadable) are already handled fail-closed inside FlsFetcher; only
                // an unexpected failure reaches here.
                Logger.Error(
                    "[FLS] Field-level security could not be resolved — THIS CRAWL MAY INDEX FIELDS "
                    + $"SALESFORCE WOULD HIDE. Only the manual schema.json 'flsFields' list is in force. Cause: {exc.Message}",
                    exc);
            }
        }
        else
        {
            Logger.Warning(
                "[FLS] FLS_ENFORCEMENT is off — field-level security is NOT evaluated. "
                + "Every indexed field is visible to every principal on the item's ACL.");
        }

        // ── New ACL engine: initialise once ──────────────────────────────────────
        NewAclResolver? newAclResolver = null;
        PrincipalMapper? newAclMapper = null;
        if (useNewAclEngine)
        {
            var aclSfClient = new SalesforceClient(
                instanceUrl: config.Connector.Salesforce.InstanceUrl,
                apiVersion: config.Connector.Salesforce.ApiVersion,
                accessToken: await ApiClient.GetSalesforceAccessTokenAsync(config),
                tokenRefresher: () => ApiClient.GetSalesforceAccessTokenAsync(config).GetAwaiter().GetResult());
            newAclResolver = new NewAclResolver(
                aclSfClient,
                owdFieldMap: config.OwdFieldMap,
                parentMap: config.ParentMap,
                owdOverrides: config.OwdOverrides,
                maxParentDepth: config.Tuning.AclMaxParentDepth,
                useEntityDefinitionOwd: config.UseEntityDefinitionOwd,
                objectNames: config.ObjectNames);
            newAclMapper = new PrincipalMapper(
                sfClient: aclSfClient,
                graphClient: client,
                tenantId: config.TenantId,
                batchSize: config.Tuning.SalesforceBatchSize);
            Logger.Info("New ACL engine initialised (identity cache persists across chunks)");
            // Pre-fetch OWD for each object so the dashboard can show ACL types
            var owdFetcher = newAclResolver.OwdFetcher;
            var newOwdLabels = new Dictionary<string, string>();
            var owdLabels = new Dictionary<string, string>
            {
                ["Private"] = "Private",
                ["Read"] = "Public Read",
                ["Edit"] = "Public Read/Write",
                ["ReadEditTransfer"] = "Public Read/Write/Transfer",
                ["All"] = "Public Full Access",
                ["ControlledByParent"] = "ControlledByParent",
                ["ControlledByCampaign"] = "ControlledByParent",
                ["ControlledByLeadOrContact"] = "ControlledByParent",
            };
            foreach (var objName in config.ObjectNames)
            {
                var raw = await owdFetcher.GetOwdAsync(objName);
                newOwdLabels[objName] = owdLabels.GetValueOrDefault(raw, raw);
            }
            dashboard?.SetAclTypes(newOwdLabels);
        }

        // Group ACL builder — initialise once (when USE_GROUP_ACL=true)
        GroupAclBuilder? groupAclBuilder = null;
        if (useGroupAcl)
        {
            var grpSfClient = new SalesforceClient(
                instanceUrl: config.Connector.Salesforce.InstanceUrl,
                apiVersion: config.Connector.Salesforce.ApiVersion,
                accessToken: await ApiClient.GetSalesforceAccessTokenAsync(config),
                tokenRefresher: () => ApiClient.GetSalesforceAccessTokenAsync(config).GetAwaiter().GetResult());
            var grpPrincipalMapper = new PrincipalMapper(
                sfClient: grpSfClient,
                graphClient: client,
                tenantId: config.TenantId,
                batchSize: config.Tuning.SalesforceBatchSize);
            groupAclBuilder = new GroupAclBuilder(
                sfClient: grpSfClient,
                owdOverrides: config.OwdOverrides,
                parentMap: config.ParentMap,
                owdFieldMap: config.OwdFieldMap,
                principalMapper: grpPrincipalMapper,
                useEntityDefinitionOwd: config.UseEntityDefinitionOwd,
                objectNames: config.ObjectNames);
            Logger.Info("Group ACL builder initialised");
            // Pre-fetch OWD map so the dashboard can show ACL types
            var owdLabelMap = await groupAclBuilder.PrewarmOwdAsync();
            dashboard?.SetAclTypes(owdLabelMap);
        }

        // Legacy resolver — initialise once and pre-warm caches before workers start
        var legacyResolver = useNewAclEngine || useGroupAcl
            ? null
            : LegacyAclResolverFactory(config, transformer.Handlers, client);
        if (legacyResolver != null)
        {
            Logger.Info("Pre-warming ACL caches before starting parallel object workers...");
            await legacyResolver.PrewarmCachesAsync();
        }

        // ── Checkpointing & dead-letter setup ────────────────────────────────────
        var connectorId = config.Connector.Id;
        var sinceIso = since != null ? PyIsoFormat(since.Value) : null;
        // Skip checkpoint for single-item ingestion — always re-ingest the requested record
        JsonObject? checkpoint;
        if (!string.IsNullOrEmpty(config.DebugItemId))
            checkpoint = null;
        else
            checkpoint = SyncState.ReadCheckpoint(connectorId);
        if (checkpoint != null && checkpoint["since"]?.ToString() == sinceIso)
        {
            Logger.Info($"Checkpoint found — resuming from: {PyDictRepr(checkpoint["completed"] as JsonObject)}");
        }
        else
        {
            checkpoint = null;
        }
        var dlPath = SyncState.FailedRecordsPath(connectorId);

        // ── Determine active object types ────────────────────────────────────────
        List<string> activeTypes;
        if (!string.IsNullOrEmpty(config.DebugObjectType))
            activeTypes = new List<string> { config.DebugObjectType! };
        else
            activeTypes = ApiClient.ObjectConfigs.Select(c => c.ObjectType).ToList();

        // Connection sharding: restrict this connection to its slice of the schema.
        if (config.ShardObjectTypes != null)
            activeTypes = activeTypes.Where(config.ShardObjectTypes.Contains).ToList();

        // Pre-populate dashboard with known object types and record counts
        if (dashboard != null)
        {
            dashboard.SetObjectTypes(activeTypes);
            dashboard.SetActivity("Querying Salesforce record counts...");
            var totalCounts = await GetObjectCountsHook(config, since);
            dashboard.SetTotalCounts(totalCounts);
            // /metrics per-object progress denominators (only known when the
            // record counts were queried, i.e. dashboard runs).
            foreach (var (countObjectType, count) in totalCounts)
                Metrics.SetObjectTotal(countObjectType, count);
            Logger.Info($"Record counts: {PyDictRepr(totalCounts)} (total: {totalCounts.Values.Sum()})");
        }

        // ── Launch parallel object workers ───────────────────────────────────────
        var effectiveWorkers = Math.Min(maxWorkers, activeTypes.Count);
        Logger.Info(
            $"Launching {effectiveWorkers} parallel object worker(s) for {activeTypes.Count} object type(s): " +
            string.Join(", ", activeTypes));
        Progress.Info($"  Ingesting {activeTypes.Count} object types in parallel ({effectiveWorkers} workers)");

        var totalIngested = 0;

        // ── HA seam: claim-based work source (null → static schema list) ────────
        var workSource = ObjectWorkSourceFactory != null
            ? await ObjectWorkSourceFactory(config, activeTypes)
            : null;

        if (workSource != null)
        {
            totalIngested = await RunClaimedObjectWorkersAsync(
                workSource, effectiveWorkers, config, client, transformer,
                legacyResolver, newAclResolver, newAclMapper, groupAclBuilder,
                stats, concurrency, since, checkpoint, sinceIso, dlPath,
                dashboard, statsLock, decisionLedger);
        }
        else
        using (var pool = new SemaphoreSlim(effectiveWorkers, effectiveWorkers))
        {
            var futures = new Dictionary<Task<int>, string>();
            foreach (var objType in activeTypes)
            {
                var ot = objType;
                futures[Task.Run(async () =>
                {
                    await pool.WaitAsync();
                    try
                    {
                        return await IngestSingleObjectTypeAsync(
                            ot,
                            config,
                            client,
                            transformer,
                            legacyResolver,
                            newAclResolver,
                            newAclMapper,
                            groupAclBuilder,
                            stats,
                            concurrency,
                            since,
                            checkpoint,
                            sinceIso,
                            dlPath,
                            dashboard,
                            statsLock,
                            decisionLedger);
                    }
                    finally
                    {
                        pool.Release();
                    }
                })] = ot;
            }

            var remaining = futures.Keys.ToList();
            while (remaining.Count > 0)
            {
                var future = await Task.WhenAny(remaining);
                remaining.Remove(future);
                var objType = futures[future];
                try
                {
                    var count = await future;
                    totalIngested += count;
                    Logger.Info($"[{objType}] completed — {count} items");
                }
                catch (_SkipObjectError exc)
                {
                    Logger.Warning($"[{objType}] skipped — {exc.Message}");
                }
                catch (Exception exc)
                {
                    Logger.Error($"[{objType}] worker failed with an exception", exc);
                    // Track the failure so it surfaces in the final stats
                    lock (statsLock)
                    {
                        var objFetched = stats.ObjectTypeCounts.GetValueOrDefault(objType, 0);
                        if (objFetched > 0)
                        {
                            stats.FailedCount += objFetched;
                            Logger.Error(
                                $"[{objType}] Worker crash — {objFetched} fetched record(s) for this object type " +
                                "may not have been ingested");
                        }
                    }
                    if (!string.IsNullOrEmpty(dlPath))
                    {
                        SyncState.AppendFailedRecords(
                            dlPath,
                            new List<(string, string)>
                            {
                                ("WORKER_CRASH", $"[Worker] {objType} worker thread failed: {exc.Message}"),
                            },
                            objType);
                    }
                }
            }
        }

        // ── Finalise ─────────────────────────────────────────────────────────────
        dashboard?.Finish();

        Progress.Info(
            $"  Ingestion complete: {stats.SuccessCount} succeeded, {stats.FailedCount} failed, " +
            $"{stats.DeletedCount} deleted");
        Logger.Info($"Ingestion complete. Total items ingested: {totalIngested}");

        // ── Count reconciliation — detect silent drops ───────────────────────────
        var accounted = stats.SuccessCount + stats.FailedCount + stats.DeletedCount + stats.SkippedCount;
        if (stats.TotalFetched > 0 && accounted < stats.TotalFetched)
        {
            var gap = stats.TotalFetched - accounted;
            Logger.Error(
                $"COUNT MISMATCH — {gap} item(s) unaccounted for! " +
                $"fetched={stats.TotalFetched}, success={stats.SuccessCount}, failed={stats.FailedCount}, " +
                $"deleted={stats.DeletedCount}, skipped={stats.SkippedCount}, accounted={accounted}");
        }
        else if (stats.TotalFetched > 0)
        {
            Logger.Info(
                $"Count reconciliation OK — fetched={stats.TotalFetched}, accounted={accounted} " +
                $"(success={stats.SuccessCount} + failed={stats.FailedCount} + " +
                $"deleted={stats.DeletedCount} + skipped={stats.SkippedCount})");
        }

        var wasStopped = (dashboard != null && dashboard.StopRequested) || ServiceStop.Requested;
        // In claim-based (HA) mode the checkpoint is shared by all nodes — only
        // the node that closes the crawl clears it (the commands do this after
        // CloseCrawlIfComplete). Default mode clears it here exactly as before.
        if (!wasStopped && workSource == null)
            SyncState.ClearCheckpoint(connectorId);
        if (stats.FailedCount > 0 && File.Exists(dlPath))
            Logger.Info($"Failed items written to dead-letter file: {dlPath}");

        return stats;
    }

    /// <summary>
    /// HA mode worker loop: each worker claims one object type at a time from the
    /// <see cref="IObjectWorkSource"/>, heartbeats while working it, and completes
    /// the claim when the object finishes (status ``failed`` on exception). The
    /// per-object ingestion itself is the exact same
    /// <see cref="IngestSingleObjectTypeAsync"/> path as single-node mode.
    /// </summary>
    private static async Task<int> RunClaimedObjectWorkersAsync(
        IObjectWorkSource workSource,
        int workerCount,
        AppConfig config,
        GraphClient client,
        SalesforceItemTransformer transformer,
        AclResolver? legacyResolver,
        NewAclResolver? newAclResolver,
        PrincipalMapper? newAclMapper,
        GroupAclBuilder? groupAclBuilder,
        IngestionStats stats,
        AdaptiveConcurrency concurrency,
        DateTime? since,
        JsonObject? checkpoint,
        string? sinceIso,
        string? dlPath,
        IngestionDashboard? dashboard,
        object statsLock,
        DecisionLedger? decisionLedger = null)
    {
        var totalIngested = 0;
        var totalLock = new object();

        async Task WorkerLoopAsync()
        {
            while (!ServiceStop.Requested && !(dashboard?.StopRequested ?? false))
            {
                string? objType;
                try
                {
                    objType = await workSource.ClaimNextAsync();
                }
                catch (OperationCanceledException) when (ServiceStop.Requested)
                {
                    // Graceful stop raced the claim call (the coordinator's SQL runs
                    // under the ServiceStop token) — treat exactly like the loop
                    // condition: stop cleanly, claims stay reclaimable.
                    break;
                }
                if (objType == null)
                    break;

                // Re-read the shared checkpoint for THIS claim: when reclaiming an
                // object abandoned by a dead node, the run-start snapshot predates
                // that node's chunk checkpoints — a fresh read resumes where it
                // died instead of re-ingesting the object from scratch. Same
                // since-boundary validation as the run-start read.
                var claimCheckpoint = checkpoint;
                if (string.IsNullOrEmpty(config.DebugItemId))
                {
                    try
                    {
                        var reread = SyncState.ReadCheckpoint(config.Connector.Id);
                        claimCheckpoint = reread != null && reread["since"]?.ToString() == sinceIso
                            ? reread
                            : null;
                    }
                    catch (Exception exc)
                    {
                        Logger.Warning(
                            $"[{objType}] Could not refresh checkpoint for claim; using run-start snapshot: {exc.Message}");
                    }
                }

                using var heartbeat = workSource.BeginHeartbeat(objType);
                var failed = false;
                var count = 0;
                try
                {
                    count = await IngestSingleObjectTypeAsync(
                        objType,
                        config,
                        client,
                        transformer,
                        legacyResolver,
                        newAclResolver,
                        newAclMapper,
                        groupAclBuilder,
                        stats,
                        concurrency,
                        since,
                        claimCheckpoint,
                        sinceIso,
                        dlPath,
                        dashboard,
                        statsLock,
                        decisionLedger);
                }
                catch (_SkipObjectError exc)
                {
                    Logger.Warning($"[{objType}] skipped — {exc.Message}");
                }
                catch (Exception exc)
                {
                    failed = true;
                    Logger.Error($"[{objType}] worker failed with an exception", exc);
                    lock (statsLock)
                    {
                        var objFetched = stats.ObjectTypeCounts.GetValueOrDefault(objType, 0);
                        if (objFetched > 0)
                        {
                            stats.FailedCount += objFetched;
                            Logger.Error(
                                $"[{objType}] Worker crash — {objFetched} fetched record(s) for this object type " +
                                "may not have been ingested");
                        }
                    }
                    if (!string.IsNullOrEmpty(dlPath))
                    {
                        SyncState.AppendFailedRecords(
                            dlPath,
                            new List<(string, string)>
                            {
                                ("WORKER_CRASH", $"[Worker] {objType} worker thread failed: {exc.Message}"),
                            },
                            objType);
                    }
                }

                if (!failed && ((dashboard != null && dashboard.StopRequested) || ServiceStop.Requested))
                {
                    // Graceful stop mid-object: leave the claim held so another node
                    // reclaims it after heartbeat expiry and resumes from the checkpoint.
                    break;
                }

                lock (totalLock)
                {
                    totalIngested += count;
                }
                if (!failed)
                    Logger.Info($"[{objType}] completed — {count} items");
                try
                {
                    await workSource.CompleteAsync(objType, !failed);
                }
                catch (OperationCanceledException) when (ServiceStop.Requested)
                {
                    // Stop raced the completion call — the claim stays held and is
                    // reclaimed after heartbeat expiry; the object's work IS done and
                    // checkpointed, so the reclaiming node skips straight through it.
                    Logger.Info($"[{objType}] service stop during claim completion — claim left for reclaim.");
                    break;
                }
            }
        }

        var workers = new List<Task>();
        for (var w = 0; w < Math.Max(1, workerCount); w++)
            workers.Add(Task.Run(WorkerLoopAsync));
        await Task.WhenAll(workers);
        return totalIngested;
    }

    /// <summary>Format a datetime the way Python's datetime.isoformat() renders it.</summary>
    private static string PyIsoFormat(DateTime value)
    {
        return value.Ticks % TimeSpan.TicksPerSecond == 0
            ? value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
            : value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
    }

    /// <summary>Format a {str: int} mapping the way Python's dict repr renders in log output.</summary>
    private static string PyDictRepr(Dictionary<string, int> values)
    {
        return "{" + string.Join(", ", values.Select(kv => $"'{kv.Key}': {kv.Value}")) + "}";
    }

    /// <summary>Format a JSON object the way Python's dict repr renders in log output.</summary>
    private static string PyDictRepr(JsonObject? values)
    {
        if (values == null)
            return "{}";
        return "{" + string.Join(", ", values.Select(kv => $"'{kv.Key}': {kv.Value?.ToJsonString() ?? "None"}")) + "}";
    }
}
