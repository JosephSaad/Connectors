// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Item;

namespace SalesforceCopilotConnector.Salesforce;

public sealed record SalesforceObjectConfig(
    string ObjectType,
    string[] Fields,
    string FilterCondition = "");

public sealed record SalesforceErrorInfo(
    string? ErrorCode,
    string? Message,
    string RawText);

/// <summary>Raised when a Salesforce object type is not available in this org.</summary>
public class _SkipObjectError : InvalidOperationException
{
    public _SkipObjectError(string message) : base(message) { }
}

public static class ApiClient
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector");

    /// <summary>
    /// Create an <see cref="HttpClient"/> with connection pooling for Salesforce API calls.
    ///
    /// Reusing a pooled client avoids repeated TCP + TLS handshakes — this alone can
    /// cut per-request latency by 50-80 ms on typical Salesforce endpoints.
    /// </summary>
    private static HttpClient BuildSfSession()
    {
        var handler = new SocketsHttpHandler { MaxConnectionsPerServer = 20 };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    // Shared pooled client — used across all Salesforce fetch tasks.
    // Settable so tests can substitute a fake HttpMessageHandler
    // (Python tests monkeypatch the module-level ``_sf_session``).
    public static HttpClient SfSession { get; set; } = BuildSfSession();

    // ── Per-org, per-object field-list cache (SQLite-backed) ───────────────────
    // After the per-field retry loop discovers which fields work for a given org,
    // we persist the final list in the SQLite state store so subsequent runs skip
    // the retry loop entirely.  The store is located at data/{connection_id}_identity.db.
    //
    // We need a connection_id to open the store, but the cache functions only
    // receive instance_url.  We use a module-level variable that is set when
    // load_config() is called (via the first FetchSalesforceRecordsAsync call).
    private static string? _activeConnectionId;

    /// <summary>Return cached field list from SQLite, or <c>null</c> if not found.</summary>
    private static string[]? LoadFieldCache(string instanceUrl, string objectType)
    {
        if (string.IsNullOrEmpty(_activeConnectionId))
            return null;
        try
        {
            using var store = IdentityStore.CreateStore(_activeConnectionId);
            var result = store.GetCachedFields(instanceUrl, objectType);
            if (result is { Length: > 0 })
            {
                Logger.Info($"[FieldCache] Loaded {result.Length} cached fields for {objectType} from SQLite");
            }
            return result;
        }
        catch (Exception exc)
        {
            Logger.Warning($"[FieldCache] Could not load cache for {objectType}: {exc.Message}");
        }
        return null;
    }

    /// <summary>Persist working field list to SQLite so the next run skips the retry loop.</summary>
    private static void SaveFieldCache(string instanceUrl, string objectType, string[] fields)
    {
        if (string.IsNullOrEmpty(_activeConnectionId))
            return;
        try
        {
            using var store = IdentityStore.CreateStore(_activeConnectionId);
            store.SaveCachedFields(instanceUrl, objectType, fields);
            Logger.Info($"[FieldCache] Saved {fields.Length} working fields for {objectType} to SQLite");
        }
        catch (Exception exc)
        {
            Logger.Warning($"[FieldCache] Could not save cache for {objectType}: {exc.Message}");
        }
    }

    private static readonly Regex[] InvalidFieldNamePatterns =
    {
        new(@"No such column '([^']+)' on entity", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"No such column '([^']+)' on sobject", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    private static readonly Regex InvalidRelationshipPattern = new(
        @"Didn't understand relationship '([^']+)' in field path",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Return *fields* as an order-preserving array with duplicates and blanks removed.</summary>
    private static string[] DedupeFields(IEnumerable<string> fields)
    {
        var seen = new HashSet<string>();
        var ordered = new List<string>();
        foreach (var field in fields)
        {
            if (string.IsNullOrEmpty(field) || seen.Contains(field))
                continue;
            seen.Add(field);
            ordered.Add(field);
        }
        return ordered.ToArray();
    }

    /// <summary>Build a <see cref="SalesforceObjectConfig"/> for each object defined in the converter config.</summary>
    private static SalesforceObjectConfig[] BuildObjectConfigs()
    {
        var converterConfig = Converter.LoadConverterConfig();
        var converterObjects = new Dictionary<string, JsonObject>();
        foreach (var node in (JsonArray)converterConfig["objectList"]!)
        {
            if (node is JsonObject objectConfig)
                converterObjects[objectConfig["objectName"]!.GetValue<string>()] = objectConfig;
        }

        var configs = new List<SalesforceObjectConfig>();

        foreach (var objectName in SalesforceConstants.OrderedObjectNames)
        {
            converterObjects.TryGetValue(objectName, out var objectConfig);
            var selectedFields = (objectConfig?["selectedFields"] as JsonObject)?
                .Select(kv => kv.Key).ToList() ?? new List<string>();
            var fields = DedupeFields(new[] { "Id" }.Concat(selectedFields).Concat(Converter.MetadataColumns));
            configs.Add(new SalesforceObjectConfig(
                ObjectType: objectName,
                Fields: fields,
                FilterCondition: objectConfig?["filterCondition"]?.GetValue<string>() ?? ""));
        }

        // configs.Add(
        //     new SalesforceObjectConfig(
        //         ObjectType: "Customer_Project__c",
        //         Fields: DedupeFields(new[] { "Id" }.Concat(LEGACY_QUERY_FIELDS["Customer_Project__c"]))));

        return configs.ToArray();
    }

    private static SalesforceObjectConfig[]? _objectConfigs;

    public static SalesforceObjectConfig[] ObjectConfigs => _objectConfigs ??= BuildObjectConfigs();

    // ── HTTP plumbing ──────────────────────────────────────────────────────────

    private sealed record SfResponse(int StatusCode, string Reason, string Text)
    {
        public bool Ok => StatusCode < 400;
    }

    /// <summary>
    /// Issue a GET through the shared pooled client, mirroring the Python session
    /// retry policy: total=3, backoff_factor=0.5, status_forcelist=[502, 503, 504].
    /// </summary>
    private static async Task<SfResponse> SfGetAsync(string url, IReadOnlyDictionary<string, string> headers, int timeoutSeconds)
    {
        const int totalRetries = 3;
        const double backoffFactor = 0.5;
        for (var attempt = 0; ; attempt++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var (key, value) in headers)
                request.Headers.TryAddWithoutValidation(key, value);

            HttpResponseMessage response;
            try
            {
                response = await SfSession.SendAsync(request, cts.Token);
            }
            catch (Exception) when (attempt < totalRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(backoffFactor * Math.Pow(2, attempt)));
                continue;
            }

            if ((int)response.StatusCode is 502 or 503 or 504 && attempt < totalRetries)
            {
                response.Dispose();
                await Task.Delay(TimeSpan.FromSeconds(backoffFactor * Math.Pow(2, attempt)));
                continue;
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(CancellationToken.None);
                return new SfResponse((int)response.StatusCode, response.ReasonPhrase ?? "", text);
            }
        }
    }

    /// <summary>
    /// Run lightweight ``SELECT COUNT()`` queries to get total record counts per object type.
    ///
    /// Salesforce returns the count in the ``totalSize`` response field for
    /// ``COUNT()`` queries, so no actual records are transferred.  This is
    /// used by the dashboard to calculate a stable ETA from the start.
    /// </summary>
    public static async Task<Dictionary<string, int>> GetObjectCountsAsync(AppConfig config, DateTime? since = null)
    {
        var accessToken = await GetSalesforceAccessTokenAsync(config);
        var baseUrl = config.Connector.Salesforce.InstanceUrl;
        var apiVersion = config.Connector.Salesforce.ApiVersion;
        var headers = new Dictionary<string, string>
        {
            ["accept"] = "application/json",
            ["authorization"] = $"Bearer {accessToken}",
        };

        var activeConfigs = ObjectConfigs;
        if (!string.IsNullOrEmpty(config.DebugObjectType))
            activeConfigs = ObjectConfigs.Where(c => c.ObjectType == config.DebugObjectType).ToArray();
        if (config.ShardObjectTypes != null)
            activeConfigs = activeConfigs.Where(c => config.ShardObjectTypes.Contains(c.ObjectType)).ToArray();

        var counts = new Dictionary<string, int>();
        foreach (var objCfg in activeConfigs)
        {
            var soql = $"SELECT COUNT() FROM {objCfg.ObjectType}";
            var whereClauses = new List<string>();
            if (!string.IsNullOrEmpty(objCfg.FilterCondition))
                whereClauses.Add(objCfg.FilterCondition);
            if (since.HasValue)
                whereClauses.Add($"LastModifiedDate >= {Utils.ToIsoZ(since.Value)}");
            if (whereClauses.Count > 0)
                soql += $" WHERE {string.Join(" AND ", whereClauses)}";
            var url = BuildQueryUrl(baseUrl, apiVersion, soql);
            try
            {
                var resp = await SfGetAsync(url, headers, 30);
                if (resp.Ok)
                {
                    var total = (JsonNode.Parse(resp.Text) as JsonObject)?["totalSize"]?.GetValue<int>() ?? 0;

                    // Intra-object hash sharding: SOQL cannot COUNT() a hash bucket, so
                    // estimate this connection's share as total × ownedBuckets/bucketCount.
                    // Feeds only the dashboard ETA — ingestion itself filters per record.
                    if (config.ShardObjectBuckets is not null
                        && config.ShardObjectBuckets.TryGetValue(objCfg.ObjectType, out var bucketSpec))
                    {
                        total = (int)Math.Round((double)total * bucketSpec.Buckets.Count / bucketSpec.BucketCount);
                    }

                    counts[objCfg.ObjectType] = total;
                    Logger.Info($"COUNT {objCfg.ObjectType}: {counts[objCfg.ObjectType]} records");
                }
            }
            catch (Exception exc)
            {
                Logger.Warning($"COUNT query failed for {objCfg.ObjectType}: {exc.Message}");
            }
        }
        return counts;
    }

    /// <summary>Authenticate with Salesforce using client-credentials and return an access token.</summary>
    public static async Task<string> GetSalesforceAccessTokenAsync(AppConfig config)
    {
        var tokenUrl = $"{config.Connector.Salesforce.InstanceUrl}/services/oauth2/token";
        Logger.Info($"Authenticating with Salesforce at {tokenUrl}");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = config.Connector.Salesforce.ClientId,
            ["client_secret"] = config.Connector.Salesforce.ClientSecret,
        });
        using var response = await SfSession.PostAsync(tokenUrl, content, cts.Token);
        var text = await response.Content.ReadAsStringAsync(CancellationToken.None);

        if ((int)response.StatusCode >= 400)
        {
            throw new InvalidOperationException(
                $"Failed to authenticate with Salesforce: {(int)response.StatusCode} {response.ReasonPhrase} - {text}");
        }

        var data = JsonNode.Parse(text) as JsonObject;
        var accessToken = data?["access_token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(accessToken))
            throw new InvalidOperationException("Salesforce authentication response did not contain an access token");

        Logger.Info("Successfully authenticated with Salesforce");
        return accessToken;
    }

    /// <summary>
    /// Yield all Salesforce records across configured objects, optionally filtered by *since*.
    ///
    /// Records for each object type are fetched **in parallel** using one task per
    /// object type, then merged into a single sequential stream ordered by object
    /// type.  This dramatically reduces total fetch time for large orgs.
    ///
    /// When ``config.DebugObjectType`` is set (``ingest-object`` command), only
    /// the matching object's task is started — no unnecessary Salesforce API calls
    /// are made for the other objects.
    ///
    /// When ``config.DebugItemId`` is set (``ingest-item`` command), all object
    /// tasks start (we don't know which object the ID belongs to) but each task
    /// stops as soon as it has yielded the matching record, thanks to the sentinel
    /// drain in the consumer.
    /// </summary>
    public static async IAsyncEnumerable<JsonObject> GetAllItemsFromApiAsync(AppConfig config, DateTime? since = null)
    {
        var debugObjectType = config.DebugObjectType;

        // When a specific object type is requested, restrict to that one config only.
        // This avoids starting N-1 unnecessary Salesforce fetch tasks.
        var activeConfigs = !string.IsNullOrEmpty(debugObjectType)
            ? ObjectConfigs.Where(cfg => cfg.ObjectType == debugObjectType).ToList()
            : ObjectConfigs.ToList();
        if (config.ShardObjectTypes != null)
            activeConfigs = activeConfigs.Where(cfg => config.ShardObjectTypes.Contains(cfg.ObjectType)).ToList();

        if (!string.IsNullOrEmpty(debugObjectType) && activeConfigs.Count == 0)
        {
            Logger.Warning($"DEBUG_OBJECT_TYPE '{debugObjectType}' not found in OBJECT_CONFIGS — nothing to fetch.");
            yield break;
        }

        var accessToken = await GetSalesforceAccessTokenAsync(config);

        // One bounded queue per active object type (channel completion is the sentinel)
        var perObjectQueues = activeConfigs
            .Select(objCfg => (Config: objCfg, Channel: Channel.CreateBounded<JsonObject>(200)))
            .ToList();

        async Task ProducerAsync(SalesforceObjectConfig objCfg, ChannelWriter<JsonObject> writer)
        {
            try
            {
                await foreach (var record in FetchSalesforceRecordsAsync(config, accessToken, objCfg, since))
                {
                    var cleanUrl =
                        $"{config.Connector.Salesforce.InstanceUrl}/{record["Id"]?.GetValue<string>()}"
                        .Replace("'", "")
                        .Replace("\"", "");
                    record["url"] = cleanUrl;
                    await writer.WriteAsync(record);  // blocks when queue is full → natural back-pressure
                }
            }
            catch (Exception exc)
            {
                Logger.Error(
                    $"Producer thread failed for {objCfg.ObjectType}: {exc.Message} — remaining records for this " +
                    "object type will NOT be fetched. This may cause missing items.",
                    exc);
            }
            finally
            {
                writer.Complete();
            }
        }

        // Start one producer task per active object type
        var tasks = new List<Task>();
        foreach (var (objCfg, channel) in perObjectQueues)
        {
            tasks.Add(Task.Run(() => ProducerAsync(objCfg, channel.Writer)));
            Logger.Debug($"Started fetch thread for {objCfg.ObjectType}");
        }

        Logger.Info(
            $"Fetching {activeConfigs.Count} object type(s) in parallel: " +
            string.Join(", ", activeConfigs.Select(c => c.ObjectType)));

        // Drain queues in schema order (preserves grouping required by the ingest pipeline)
        foreach (var (_, channel) in perObjectQueues)
        {
            await foreach (var item in channel.Reader.ReadAllAsync())
                yield return item;
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Yield record chunks (lists of at most *chunkSize* records) for a single object type.
    ///
    /// Each record is enriched with ``objectType`` and ``url`` fields,
    /// identical to the output of <see cref="GetAllItemsFromApiAsync"/>.
    ///
    /// If the Salesforce fetch fails mid-pagination, any partially buffered
    /// records are still yielded so they are not silently lost.
    /// </summary>
    public static async IAsyncEnumerable<List<JsonObject>> IterObjectChunksAsync(
        AppConfig config,
        SalesforceObjectConfig objectConfig,
        DateTime? since,
        int chunkSize)
    {
        var accessToken = await GetSalesforceAccessTokenAsync(config);
        var buffer = new List<JsonObject>();
        await using (var enumerator = FetchSalesforceRecordsAsync(config, accessToken, objectConfig, since).GetAsyncEnumerator())
        {
            while (true)
            {
                JsonObject record;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                        break;
                    record = enumerator.Current;
                }
                catch (_SkipObjectError)
                {
                    throw;  // Let skip-object errors propagate as-is
                }
                catch (Exception exc)
                {
                    Logger.Error(
                        $"Salesforce fetch failed for {objectConfig.ObjectType} mid-pagination — {buffer.Count} record(s) in buffer, " +
                        $"remaining pages will NOT be fetched: {exc.Message}",
                        exc);
                    break;
                }

                var cleanUrl =
                    $"{config.Connector.Salesforce.InstanceUrl}/{record["Id"]?.GetValue<string>()}"
                    .Replace("'", "")
                    .Replace("\"", "");
                record["url"] = cleanUrl;
                buffer.Add(record);
                if (buffer.Count >= chunkSize)
                {
                    yield return buffer;
                    buffer = new List<JsonObject>();
                }
            }
        }
        if (buffer.Count > 0)
            yield return buffer;
    }

    /// <summary>Return the <see cref="SalesforceObjectConfig"/> for *objectType*, or <c>null</c>.</summary>
    public static SalesforceObjectConfig? GetObjectConfig(string objectType)
    {
        foreach (var cfg in ObjectConfigs)
        {
            if (cfg.ObjectType == objectType)
                return cfg;
        }
        return null;
    }

    /// <summary>Fetch records for a single Salesforce object, retrying on unsupported-field errors.</summary>
    public static async IAsyncEnumerable<JsonObject> FetchSalesforceRecordsAsync(
        AppConfig config,
        string accessToken,
        SalesforceObjectConfig objectConfig,
        DateTime? since = null)
    {
        _activeConnectionId = config.Connector.Id;

        var baseUrl = config.Connector.Salesforce.InstanceUrl;
        var apiVersion = config.Connector.Salesforce.ApiVersion;

        static Dictionary<string, string> MakeHeaders(string token) => new()
        {
            ["accept"] = "application/json",
            ["accept-language"] = "en-US,en;q=0.9,en-IN;q=0.8",
            ["content-type"] = "application/json",
            ["authorization"] = $"Bearer {token}",
        };

        var headers = MakeHeaders(accessToken);
        var currentToken = accessToken;

        var activeConfig = objectConfig;
        var allRemovedFields = new List<string>();

        // ── Load per-org field cache (skip retry loop on subsequent runs) ─────────
        var cachedFields = LoadFieldCache(
            config.Connector.Salesforce.InstanceUrl, objectConfig.ObjectType);
        if (cachedFields is not null)
        {
            // Use the pre-validated field list directly — no retry loop needed
            activeConfig = objectConfig with { Fields = cachedFields };
            Logger.Info(
                $"[FieldCache] Using {cachedFields.Length} cached fields for {objectConfig.ObjectType} (skipping retry loop)");
        }

        while (true)
        {
            var soql = BuildSoqlQuery(
                activeConfig,
                since,
                queryLimit: config.Tuning.SalesforceQueryLimit,
                recordId: config.DebugItemId);
            var queryUrl = BuildQueryUrl(baseUrl, apiVersion, soql);
            Logger.Info($"Querying Salesforce {activeConfig.ObjectType}: {soql}");

            // Hoisted out of the record loop: one dictionary lookup per fetch, not per record.
            var bucketSpec = config.ShardObjectBuckets is not null
                && config.ShardObjectBuckets.TryGetValue(activeConfig.ObjectType, out var spec)
                    ? spec
                    : null;

            var nextUrl = (string?)queryUrl;
            var fetchedCount = 0;
            var retryRequested = false;

            while (nextUrl is not null)
            {
                var response = await SfGetAsync(nextUrl, headers, 60);

                // ── 401: token expired — refresh once and retry immediately ────────────────
                if (response.StatusCode == 401)
                {
                    Logger.Warning(
                        $"[SF] 401 Unauthorized for {activeConfig.ObjectType} — refreshing Salesforce token and retrying");
                    try
                    {
                        currentToken = await GetSalesforceAccessTokenAsync(config);
                        headers = MakeHeaders(currentToken);
                        response = await SfGetAsync(nextUrl, headers, 60);
                    }
                    catch (Exception exc)
                    {
                        throw new InvalidOperationException(
                            $"Token refresh failed for {activeConfig.ObjectType}: {exc.Message}", exc);
                    }
                }

                if (!response.Ok)
                {
                    // ── 400 INVALID_TYPE: object not available in this org — skip ──────
                    var errorInfo = ExtractSalesforceErrorInfo(response);
                    if (response.StatusCode == 400 && errorInfo.ToString().Contains("INVALID_TYPE"))
                    {
                        throw new _SkipObjectError(
                            $"{activeConfig.ObjectType} is not available in this Salesforce org (INVALID_TYPE)");
                    }

                    var (retryConfig, removedFields) = BuildRetryObjectConfig(activeConfig, errorInfo);
                    if (retryConfig is not null && nextUrl == queryUrl && fetchedCount == 0)
                    {
                        allRemovedFields.AddRange(removedFields);
                        activeConfig = retryConfig;
                        retryRequested = true;
                        break;
                    }

                    throw new InvalidOperationException(FormatSalesforceFetchError(activeConfig.ObjectType, response));
                }

                var data = JsonNode.Parse(response.Text) as JsonObject ?? new JsonObject();
                if (data["records"] is JsonArray records)
                {
                    foreach (var node in records)
                    {
                        if (node is not JsonObject record)
                            continue;
                        record["objectType"] = activeConfig.ObjectType;
                        fetchedCount++;

                        // Intra-object hash sharding: this connection only owns records whose
                        // id hashes into its bucket subset. Filtering at THIS choke point is
                        // what keeps the whole feature coherent — the crawl, checkpoints,
                        // ingest-item, the inventory and the deletion sweep all consume this
                        // stream, so they inherit the same routing. (fetchedCount still counts
                        // every fetched record: the INVALID_FIELD retry guard predates the
                        // filter and must keep its exact semantics.)
                        if (bucketSpec is not null)
                        {
                            var recordId = record["Id"]?.GetValue<string>();
                            if (recordId is not null && !bucketSpec.Owns(recordId))
                                continue;
                        }
                        yield return record;
                    }
                }

                var nextRecordsUrl = data["nextRecordsUrl"]?.GetValue<string>();
                nextUrl = !string.IsNullOrEmpty(nextRecordsUrl) ? $"{baseUrl}{nextRecordsUrl}" : null;
            }

            if (retryRequested)
                continue;

            if (allRemovedFields.Count > 0)
            {
                Logger.Warning(
                    $"Salesforce {activeConfig.ObjectType}: skipped unsupported field(s): {string.Join(", ", allRemovedFields)}");
                // Persist the working field list so the next run skips the retry loop
                if (cachedFields is null)  // only save when we just discovered it
                {
                    SaveFieldCache(
                        config.Connector.Salesforce.InstanceUrl,
                        activeConfig.ObjectType,
                        activeConfig.Fields);
                }
            }
            Logger.Info($"Fetched {fetchedCount} {activeConfig.ObjectType} records from Salesforce");
            yield break;
        }
    }

    /// <summary>
    /// Yield the <c>Id</c> of every live Salesforce record for <paramref name="objectConfig"/> — a
    /// lightweight <c>SELECT Id FROM {ObjectType} [WHERE {FilterCondition}]</c> that paginates via
    /// <c>nextRecordsUrl</c> exactly like <see cref="FetchSalesforceRecordsAsync"/>.
    ///
    /// <para>No <c>since</c> clause is applied, so this is the FULL current id set. The SAME
    /// <see cref="SalesforceObjectConfig.FilterCondition"/> the content crawl uses is applied so
    /// records filtered out of the crawl are never considered stale by the deletion sweep. This is
    /// the sweep's <b>source of truth</b>: a fresh id-only query at sweep time — never the set of
    /// ids ingested during the crawl (an item that still exists but merely failed to ingest this
    /// run must not be swept).</para>
    ///
    /// <para>Only <c>Id</c> is projected (always a valid column), so the per-org field-retry loop
    /// and field cache that <see cref="FetchSalesforceRecordsAsync"/> needs are unnecessary here.
    /// A single 401 triggers one token refresh, mirroring the record fetch. An object type that is
    /// not available in this org still raises <see cref="_SkipObjectError"/> (never an empty set —
    /// treating "object missing" as "everything deleted" would be a data-loss bug).</para>
    /// </summary>
    public static async IAsyncEnumerable<string> FetchRecordIdsAsync(
        AppConfig config,
        string accessToken,
        SalesforceObjectConfig objectConfig,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var baseUrl = config.Connector.Salesforce.InstanceUrl;
        var apiVersion = config.Connector.Salesforce.ApiVersion;

        var headers = new Dictionary<string, string>
        {
            ["accept"] = "application/json",
            ["accept-language"] = "en-US,en;q=0.9,en-IN;q=0.8",
            ["content-type"] = "application/json",
            ["authorization"] = $"Bearer {accessToken}",
        };

        // Id-only projection with the crawl's filter, no delta cursor — the full live id set.
        var idOnlyConfig = objectConfig with { Fields = new[] { "Id" } };
        var soql = BuildSoqlQuery(idOnlyConfig, since: null);
        var queryUrl = BuildQueryUrl(baseUrl, apiVersion, soql);
        Logger.Info($"Querying Salesforce ids {objectConfig.ObjectType}: {soql}");

        // Intra-object hash sharding: restrict to this connection's bucket subset, exactly
        // like FetchSalesforceRecordsAsync — the deletion sweep diffs these ids against this
        // connection's inventory, so both sides must see the same slice of the object.
        var bucketSpec = config.ShardObjectBuckets is not null
            && config.ShardObjectBuckets.TryGetValue(objectConfig.ObjectType, out var spec)
                ? spec
                : null;

        var nextUrl = (string?)queryUrl;
        while (nextUrl is not null)
        {
            ct.ThrowIfCancellationRequested();
            var response = await SfGetAsync(nextUrl, headers, 60);

            // 401: token expired — refresh once and retry immediately.
            if (response.StatusCode == 401)
            {
                Logger.Warning(
                    $"[SF] 401 Unauthorized for {objectConfig.ObjectType} (id fetch) — refreshing token and retrying");
                var refreshed = await GetSalesforceAccessTokenAsync(config);
                headers["authorization"] = $"Bearer {refreshed}";
                response = await SfGetAsync(nextUrl, headers, 60);
            }

            if (!response.Ok)
            {
                var errorInfo = ExtractSalesforceErrorInfo(response);
                if (response.StatusCode == 400 && errorInfo.ToString().Contains("INVALID_TYPE"))
                {
                    throw new _SkipObjectError(
                        $"{objectConfig.ObjectType} is not available in this Salesforce org (INVALID_TYPE)");
                }
                throw new InvalidOperationException(
                    FormatSalesforceFetchError(objectConfig.ObjectType, response));
            }

            var data = JsonNode.Parse(response.Text) as JsonObject ?? new JsonObject();
            if (data["records"] is JsonArray records)
            {
                foreach (var node in records)
                {
                    if (node is not JsonObject record)
                        continue;
                    var id = record["Id"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(id) && (bucketSpec is null || bucketSpec.Owns(id!)))
                        yield return id!;
                }
            }

            var nextRecordsUrl = data["nextRecordsUrl"]?.GetValue<string>();
            nextUrl = !string.IsNullOrEmpty(nextRecordsUrl) ? $"{baseUrl}{nextRecordsUrl}" : null;
        }
    }

    /// <summary>Construct a full Salesforce REST API query URL from the given SOQL.</summary>
    private static string BuildQueryUrl(string baseUrl, string apiVersion, string soql)
    {
        return $"{baseUrl}/services/data/{apiVersion}/query?q={WebUtility.UrlEncode(soql)}";
    }

    /// <summary>Format a human-readable error message for a failed Salesforce fetch.</summary>
    private static string FormatSalesforceFetchError(string objectType, SfResponse response)
    {
        return "Failed to fetch " +
               $"{objectType} from Salesforce: {response.StatusCode} {response.Reason} - {response.Text}";
    }

    /// <summary>Return a JSON node rendered as a plain string when possible.</summary>
    private static string? NodeToString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? s) ? s : node?.ToString();

    /// <summary>Extract a structured <see cref="SalesforceErrorInfo"/> from an error response.</summary>
    private static SalesforceErrorInfo ExtractSalesforceErrorInfo(SfResponse response)
    {
        JsonNode? payload;
        try
        {
            payload = JsonNode.Parse(response.Text);
        }
        catch (JsonException)
        {
            return new SalesforceErrorInfo(ErrorCode: null, Message: null, RawText: response.Text);
        }

        if (payload is JsonArray array)
        {
            foreach (var entry in array)
            {
                if (entry is JsonObject entryObj)
                {
                    var errorCode = NodeToString(entryObj["errorCode"]);
                    var message = NodeToString(entryObj["message"]);
                    if (!string.IsNullOrEmpty(errorCode) || !string.IsNullOrEmpty(message))
                        return new SalesforceErrorInfo(ErrorCode: errorCode, Message: message, RawText: response.Text);
                }
            }
        }
        else if (payload is JsonObject payloadObj)
        {
            return new SalesforceErrorInfo(
                ErrorCode: NodeToString(payloadObj["errorCode"]),
                Message: NodeToString(payloadObj["message"]),
                RawText: response.Text);
        }

        return new SalesforceErrorInfo(ErrorCode: null, Message: null, RawText: response.Text);
    }

    /// <summary>Return a new config with unsupported fields removed, or <c>(null, empty)</c> if retry is not possible.</summary>
    private static (SalesforceObjectConfig? RetryConfig, string[] RemovedFields) BuildRetryObjectConfig(
        SalesforceObjectConfig objectConfig,
        SalesforceErrorInfo errorInfo)
    {
        var unsupportedFields = ExtractUnsupportedFields(objectConfig.Fields, errorInfo);
        if (unsupportedFields.Length == 0)
            return (null, Array.Empty<string>());

        var unsupportedSet = new HashSet<string>(unsupportedFields);
        var remainingFields = objectConfig.Fields.Where(field => !unsupportedSet.Contains(field)).ToArray();
        if (remainingFields.Length == 0 || remainingFields.Length == objectConfig.Fields.Length)
            return (null, Array.Empty<string>());

        return (objectConfig with { Fields = remainingFields }, unsupportedFields);
    }

    /// <summary>Identify field names flagged as invalid or using an unsupported relationship in *errorInfo*.</summary>
    public static string[] ExtractUnsupportedFields(string[] fields, SalesforceErrorInfo errorInfo)
    {
        if (errorInfo.ErrorCode != "INVALID_FIELD" || string.IsNullOrEmpty(errorInfo.Message))
            return Array.Empty<string>();

        foreach (var pattern in InvalidFieldNamePatterns)
        {
            var match = pattern.Match(errorInfo.Message);
            if (match.Success)
                return MatchExactOrPrefixedFields(fields, match.Groups[1].Value);
        }

        var relationshipMatch = InvalidRelationshipPattern.Match(errorInfo.Message);
        if (relationshipMatch.Success)
        {
            var relationshipName = relationshipMatch.Groups[1].Value.ToLowerInvariant();
            return fields
                .Where(field => field.Split('.', 2)[0].ToLowerInvariant() == relationshipName)
                .ToArray();
        }

        return Array.Empty<string>();
    }

    /// <summary>Return fields matching *candidate* exactly or starting with ``candidate.`` (case-insensitive).</summary>
    private static string[] MatchExactOrPrefixedFields(string[] fields, string candidate)
    {
        var candidateLower = candidate.ToLowerInvariant();
        var exactMatches = fields.Where(field => field.ToLowerInvariant() == candidateLower).ToArray();
        if (exactMatches.Length > 0)
            return exactMatches;

        var prefix = $"{candidateLower}.";
        return fields.Where(field => field.ToLowerInvariant().StartsWith(prefix)).ToArray();
    }

    /// <summary>
    /// Build a SOQL SELECT string for *objectConfig*, with optional *since* and *queryLimit* clauses.
    ///
    /// When *recordId* is provided, a ``WHERE Id = '&lt;recordId&gt;'`` clause is added
    /// so that only the single matching record is returned.  This is used by the
    /// ``ingest-item`` command to avoid fetching the entire object table.
    ///
    /// When *queryLimit* is ``0`` (or negative) no ``LIMIT`` clause is added and Salesforce
    /// paginates the full result set automatically at 2 000 records per page via
    /// ``nextRecordsUrl``.  Set a positive value only for local testing / debugging.
    /// </summary>
    public static string BuildSoqlQuery(
        SalesforceObjectConfig objectConfig,
        DateTime? since,
        int queryLimit = 0,
        string? recordId = null)
    {
        var soql = $"SELECT {string.Join(", ", objectConfig.Fields)} FROM {objectConfig.ObjectType}";
        var whereClauses = new List<string>();
        if (!string.IsNullOrEmpty(recordId))
            whereClauses.Add($"Id = '{recordId}'");
        if (!string.IsNullOrEmpty(objectConfig.FilterCondition))
            whereClauses.Add(objectConfig.FilterCondition);
        if (since.HasValue)
            whereClauses.Add($"LastModifiedDate >= {Utils.ToIsoZ(since.Value)}");
        if (whereClauses.Count > 0)
            soql += $" WHERE {string.Join(" AND ", whereClauses)}";
        if (queryLimit > 0)
            soql += $" LIMIT {queryLimit}";
        return soql;
    }
}
