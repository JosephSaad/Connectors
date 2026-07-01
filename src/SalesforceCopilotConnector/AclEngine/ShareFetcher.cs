// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// acl_engine/share_fetcher.py
// ---------------------------
// Step 3.1: Fetch record-level share data from Salesforce.
//
// Responsibilities
// ----------------
// * GetOwnerIdAsync      – Read the OwnerId field from a record.
// * GetShareEntriesAsync – Query the <ObjectType>Share table and return every
//                          UserOrGroupId that has been explicitly granted access,
//                          along with the RowCause and AccessLevel for that grant.
//
// Dynamic field discovery (no hard-coding)
// -----------------------------------------
// Two share-table fields require discovery because their names vary by object:
//
//   Parent reference field
//     - Standard objects : "<ObjectType>Id"  (e.g. "AccountId", "CaseId")
//     - Custom objects   : "ParentId"
//     Discovered via sObject describe → referenceTo matching object_type.
//
//   Access level field
//     - Standard objects : "<ObjectType>AccessLevel"  (e.g. "AccountAccessLevel")
//     - Custom objects   : "AccessLevel"
//     Discovered via sObject describe → picklist field ending in "AccessLevel".
//
// Both are cached in-process so each object type only pays the describe cost once.
//
// curl equivalent for share table query:
//     GET /services/data/v60.0/query
//         ?q=SELECT+AccountId%2CUserOrGroupId%2CAccountAccessLevel%2CRowCause
//            +FROM+AccountShare+WHERE+AccountId%3D'<record_id>'

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.AclEngine;

/// <summary>
/// Fetches the owner and explicit share grants for a single Salesforce record.
///
/// Parameters
/// ----------
/// sfClient : SalesforceClient instance.
/// </summary>
public class ShareFetcher
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector.acl_engine");

    private readonly SalesforceClient _sf;
    // Cache: object_type → parent field name on the share table
    private readonly Dictionary<string, string?> _parentFieldCache = new();
    // Cache: object_type → access level field name on the share table
    private readonly Dictionary<string, string?> _accessLevelFieldCache = new();
    // Bulk pre-warm caches (populated by PrewarmChunkAsync)
    // record_id → OwnerId
    private readonly Dictionary<string, string?> _ownerCache = new();
    // Cross-module accessor mirroring Python's `share_fetcher._owner_cache`
    // usage from graph/ingest.py.
    internal IReadOnlyDictionary<string, string?> OwnerCache => _ownerCache;
    // record_id → list of ShareEntry
    private readonly Dictionary<string, List<ShareEntry>> _shareCache = new();
    // Object types whose sObject has no OwnerId field.
    // Populated dynamically the first time a query returns INVALID_FIELD,
    // so subsequent batches and runs skip the query immediately.
    private readonly HashSet<string> _noOwnerIdTypes = new();

    public ShareFetcher(SalesforceClient sfClient)
    {
        _sf = sfClient;
    }

    /// <summary>
    /// Derive the share table API name for a given sObject type.
    ///
    /// Standard objects : Account   → AccountShare
    /// Custom objects   : Work_Order__c → Work_Order__Share
    ///                    (strip '__c', append '__Share')
    /// </summary>
    internal static string ShareTableName(string objectType)
    {
        if (objectType.EndsWith("__c"))
            return objectType[..^3] + "__Share";
        return objectType + "Share";
    }

    // ── Bulk pre-warm (call once per chunk before resolving records) ──────────

    /// <summary>
    /// Bulk-fetch all owner IDs and all share entries for *recordIds* in
    /// O(ceil(N/batchSize)) SOQL calls instead of O(N).
    ///
    /// After this call, GetOwnerIdAsync() and GetShareEntriesAsync() serve every
    /// record in *recordIds* from memory without hitting Salesforce.
    ///
    /// Call once per chunk before launching the concurrent resolution of the records.
    /// </summary>
    public async Task PrewarmChunkAsync(
        string objectType,
        List<string> recordIds,
        int batchSize = 100)
    {
        if (recordIds.Count == 0)
            return;

        var shareObject = ShareTableName(objectType);
        var (parentField, accessLevelField) = await GetShareFieldsAsync(objectType, shareObject);

        // Pre-initialise share cache so records with zero shares don't fall
        // back to a per-record SOQL (empty list = "queried, no results").
        foreach (var rid in recordIds)
        {
            _ownerCache.TryAdd(rid, null);
            if (!_shareCache.ContainsKey(rid))
                _shareCache[rid] = new List<ShareEntry>();
        }

        // Objects known to lack an OwnerId field in Salesforce.
        // User owns itself (no OwnerId column exists on the User sObject).
        // Other objects (e.g. Product2) are added dynamically on first failure.
        for (var i = 0; i < recordIds.Count; i += batchSize)
        {
            var batch = recordIds.GetRange(i, Math.Min(batchSize, recordIds.Count - i));
            var idsIn = string.Join(", ", batch.Select(rid => $"'{rid}'"));

            // ── Bulk owner fetch ──────────────────────────────────────────────
            if (objectType == "User")
            {
                // Salesforce User sObject has no OwnerId column — each user
                // record is owned by itself.
                foreach (var rid in batch)
                    _ownerCache[rid] = rid;
            }
            else if (_noOwnerIdTypes.Contains(objectType))
            {
                // Previously discovered via INVALID_FIELD — leave owner as None
                // (already pre-initialised above).
            }
            else
            {
                try
                {
                    var ownerRows = await _sf.QueryAllAsync(
                        $"SELECT Id, OwnerId FROM {objectType} WHERE Id IN ({idsIn})");
                    foreach (var r in ownerRows)
                    {
                        var id = r["Id"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(id))
                            _ownerCache[id] = r["OwnerId"]?.GetValue<string>();
                    }
                }
                catch (InvalidOperationException exc)
                {
                    var excStr = exc.Message;
                    if (excStr.Contains("INVALID_FIELD"))
                    {
                        Logger.Warning(
                            $"[ShareFetcher] OwnerId field does not exist on {objectType} (will skip for all batches): {exc.Message}");
                        _noOwnerIdTypes.Add(objectType);
                    }
                    else
                    {
                        Logger.Warning(
                            $"[ShareFetcher] Bulk owner fetch failed for {objectType} batch {i / batchSize + 1} (transient): {exc.Message}");
                    }
                }
            }

            // ── Bulk share-table fetch ────────────────────────────────────────
            if (string.IsNullOrEmpty(parentField))
                continue;

            var selectFields = new List<string> { "UserOrGroupId", "RowCause", parentField };
            if (!string.IsNullOrEmpty(accessLevelField))
                selectFields.Add(accessLevelField);

            try
            {
                var shareRows = await _sf.QueryAllAsync(
                    $"SELECT {string.Join(", ", selectFields)} "
                    + $"FROM {shareObject} "
                    + $"WHERE {parentField} IN ({idsIn})");
                foreach (var r in shareRows)
                {
                    var parentId = r[parentField]?.GetValue<string>();
                    var userOrGroupId = r["UserOrGroupId"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(parentId) || string.IsNullOrEmpty(userOrGroupId))
                        continue;
                    var entry = new ShareEntry(
                        userOrGroupId: userOrGroupId,
                        rowCause: r["RowCause"]?.GetValue<string>(),
                        accessLevel: !string.IsNullOrEmpty(accessLevelField)
                            ? r[accessLevelField]?.GetValue<string>()
                            : null);
                    if (!_shareCache.TryGetValue(parentId, out var entries))
                    {
                        entries = new List<ShareEntry>();
                        _shareCache[parentId] = entries;
                    }
                    entries.Add(entry);
                }
            }
            catch (InvalidOperationException exc)
            {
                Logger.Warning(
                    $"[ShareFetcher] Bulk share fetch failed for {shareObject} batch {i / batchSize + 1}: {exc.Message}");
            }
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Return the OwnerId for the given record, or null if not found.
    ///
    /// Serves from the bulk pre-warm cache when PrewarmChunkAsync() has been
    /// called for this record; falls back to a per-record SOQL otherwise.
    /// </summary>
    public async Task<string?> GetOwnerIdAsync(string objectType, string recordId)
    {
        // Fast path — bulk cache hit
        if (_ownerCache.TryGetValue(recordId, out var cached))
            return cached;

        // Salesforce User sObject has no OwnerId column — each user owns itself.
        if (objectType == "User")
        {
            _ownerCache[recordId] = recordId;
            return recordId;
        }

        // Object previously discovered to have no OwnerId field (INVALID_FIELD).
        if (_noOwnerIdTypes.Contains(objectType))
        {
            _ownerCache[recordId] = null;
            return null;
        }

        // Slow path — per-record fallback (incremental ingest / single-record mode)
        var soql = $"SELECT OwnerId FROM {objectType} WHERE Id = '{recordId}' LIMIT 1";
        List<JsonObject> records;
        try
        {
            records = await _sf.QueryAllAsync(soql);
        }
        catch (InvalidOperationException exc)
        {
            var excStr = exc.Message;
            if (excStr.Contains("INVALID_FIELD"))
            {
                Logger.Warning(
                    $"[ShareFetcher] OwnerId field does not exist on {objectType}; skipping for future lookups: {exc.Message}");
                _noOwnerIdTypes.Add(objectType);
            }
            else
            {
                Logger.Warning($"[ShareFetcher] Could not fetch owner for {objectType}/{recordId}: {exc.Message}");
            }
            return null;
        }

        if (records.Count == 0)
        {
            Logger.Warning($"[ShareFetcher] Record not found: {objectType} / {recordId}");
            return null;
        }

        var ownerId = records[0]["OwnerId"]?.GetValue<string>();
        Logger.Info($"[ShareFetcher] Owner of {objectType}/{recordId} → {ownerId}");
        return ownerId;
    }

    /// <summary>
    /// Return all explicit share grants for *recordId*.
    ///
    /// Serves from the bulk pre-warm cache when PrewarmChunkAsync() has been
    /// called for this record; falls back to a per-record SOQL otherwise.
    /// </summary>
    public async Task<List<ShareEntry>> GetShareEntriesAsync(string objectType, string recordId)
    {
        // Fast path — bulk cache hit (empty list is a valid cached result)
        if (_shareCache.TryGetValue(recordId, out var cachedEntries))
            return cachedEntries;

        // Slow path — per-record fallback
        var shareObject = ShareTableName(objectType);

        // Discover both fields concurrently in a single describe call
        var (parentField, accessLevelField) = await GetShareFieldsAsync(objectType, shareObject);

        if (string.IsNullOrEmpty(parentField))
        {
            Logger.Warning(
                $"[ShareFetcher] Cannot determine parent field for {shareObject}; "
                + $"share table will be skipped for {objectType}/{recordId}");
            return new List<ShareEntry>();
        }

        // Build SELECT list generically – include access level only if discovered
        var selectFields = new List<string> { "UserOrGroupId", "RowCause" };
        if (!string.IsNullOrEmpty(accessLevelField))
            selectFields.Add(accessLevelField);

        var soql =
            $"SELECT {string.Join(", ", selectFields)} "
            + $"FROM {shareObject} "
            + $"WHERE {parentField} = '{recordId}'";

        Logger.Debug($"[ShareFetcher] Share query: {soql}");

        List<JsonObject> records;
        try
        {
            records = await _sf.QueryAllAsync(soql);
        }
        catch (InvalidOperationException exc)
        {
            Logger.Warning(
                $"[ShareFetcher] Share table query failed for {objectType}/{recordId}: {exc.Message}");
            return new List<ShareEntry>();
        }

        var entries = records
            .Where(r => !string.IsNullOrEmpty(r["UserOrGroupId"]?.GetValue<string>()))
            .Select(r => new ShareEntry(
                userOrGroupId: r["UserOrGroupId"]!.GetValue<string>(),
                rowCause: r["RowCause"]?.GetValue<string>(),
                accessLevel: !string.IsNullOrEmpty(accessLevelField)
                    ? r[accessLevelField]?.GetValue<string>()
                    : null))
            .ToList();

        Logger.Info(
            $"[ShareFetcher] {objectType}/{recordId} → {entries.Count} share entry/entries in {shareObject}  "
            + $"(parent_field={parentField}  access_level_field={(!string.IsNullOrEmpty(accessLevelField) ? accessLevelField : "n/a")})");
        return entries;
    }

    // ── Field discovery (describe-based, cached) ───────────────────────────────

    /// <summary>
    /// Return (parentField, accessLevelField) for *shareObject*.
    ///
    /// Both values are resolved once via sObject describe and then cached.
    /// A single describe call populates both caches simultaneously.
    /// </summary>
    private async Task<(string?, string?)> GetShareFieldsAsync(string objectType, string shareObject)
    {
        // Fast path – both already cached
        if (_parentFieldCache.ContainsKey(objectType) && _accessLevelFieldCache.ContainsKey(objectType))
        {
            return (
                _parentFieldCache[objectType],
                _accessLevelFieldCache[objectType]);
        }

        // Fetch describe once and extract both fields
        JsonObject describe;
        try
        {
            describe = await _sf.DescribeSObjectAsync(shareObject);
        }
        catch (InvalidOperationException exc)
        {
            Logger.Warning($"[ShareFetcher] describe({shareObject}) failed: {exc.Message}");
            _parentFieldCache[objectType] = await DiscoverParentFieldViaProbeAsync(objectType, shareObject);
            _accessLevelFieldCache[objectType] = null;
            return (
                _parentFieldCache[objectType],
                _accessLevelFieldCache[objectType]);
        }

        var fields = describe["fields"] as JsonArray ?? new JsonArray();

        // ── Parent reference field ─────────────────────────────────────────────
        string? parentField = null;
        // 1st pass – reference field whose referenceTo contains object_type
        foreach (var node in fields)
        {
            if (node is not JsonObject f)
                continue;
            if (f["type"]?.GetValue<string>() == "reference")
            {
                var refTo = f["referenceTo"] as JsonArray;
                if (refTo is not null && refTo.Any(n => n?.GetValue<string>() == objectType))
                {
                    parentField = f["name"]!.GetValue<string>();
                    break;
                }
            }
        }
        // 2nd pass – generic "ParentId"
        if (string.IsNullOrEmpty(parentField))
        {
            foreach (var node in fields)
            {
                if (node is JsonObject f && f["name"]?.GetValue<string>() == "ParentId")
                {
                    parentField = "ParentId";
                    break;
                }
            }
        }
        // 3rd pass – probe
        if (string.IsNullOrEmpty(parentField))
            parentField = await DiscoverParentFieldViaProbeAsync(objectType, shareObject);

        // ── Access level field ─────────────────────────────────────────────────
        // Convention: "<ObjectType>AccessLevel" for standard, "AccessLevel" for custom.
        // We find it via describe by looking for a picklist (or string) field whose
        // name ends with "AccessLevel" – no hard-coded names.
        string? accessLevelField = null;
        foreach (var node in fields)
        {
            if (node is not JsonObject f)
                continue;
            var fname = f["name"]?.GetValue<string>() ?? "";
            if (fname.EndsWith("AccessLevel"))
            {
                accessLevelField = fname;
                break;
            }
        }

        // Populate both caches
        _parentFieldCache[objectType] = parentField;
        _accessLevelFieldCache[objectType] = accessLevelField;

        Logger.Debug(
            $"[ShareFetcher] {shareObject} → parent_field={(!string.IsNullOrEmpty(parentField) ? parentField : "(none)")}  "
            + $"access_level_field={(!string.IsNullOrEmpty(accessLevelField) ? accessLevelField : "(none)")}");
        return (parentField, accessLevelField);
    }

    /// <summary>
    /// Last-resort probe: try "&lt;ObjectType&gt;Id" then "ParentId" with a LIMIT 1
    /// query to see which field exists on the share table.
    /// </summary>
    private async Task<string?> DiscoverParentFieldViaProbeAsync(string objectType, string shareObject)
    {
        foreach (var candidate in new[] { objectType + "Id", "ParentId" })
        {
            var probeSoql = $"SELECT {candidate} FROM {shareObject} LIMIT 1";
            try
            {
                await _sf.QueryAllAsync(probeSoql);
                Logger.Debug($"[ShareFetcher] Probe confirmed parent field {candidate} on {shareObject}");
                return candidate;
            }
            catch (InvalidOperationException)
            {
                continue;
            }
        }
        return null;
    }
}
