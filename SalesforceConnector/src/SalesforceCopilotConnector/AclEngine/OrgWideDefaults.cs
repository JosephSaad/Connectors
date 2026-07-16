// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// acl_engine/owd.py
// -----------------
// Step 2: Org-Wide Default (OWD) fetching and interpretation.
//
// How OWD is fetched
// ------------------
// A single ``SELECT <owd_fields> FROM Organization`` is executed once per
// OWDFetcher lifetime.  The SELECT field list is built dynamically from
// schema.json's ``owdField`` entries – no field names are ever hard-coded here.
// Adding a new object to schema.json with an ``owdField`` is all that is needed.
//
// For objects that have no ``owdField`` in schema.json (e.g. Contact, which
// inherits from its parent Account), ``get_owd`` returns "Private" as the safe
// default, and the resolver's ControlledByParent path handles inheritance.
//
// Predicates
// ----------
//     IsPublic(owd)              → skip ACL work, grant everyone
//     IsControlledByParent(owd)  → recurse into parent record
//     RequiresPrivateAcl(owd)    → proceed to Step 3 (record-level ACL)

using System.Text.Json;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.AclEngine;

/// <summary>
/// Fetches Org-Wide Default sharing settings for a given Salesforce object type.
///
/// A single ``SELECT &lt;all_owd_fields&gt; FROM Organization`` is fired on the
/// first call to ``GetOwdAsync`` and every subsequent call is served from memory.
/// The SELECT list is derived entirely from schema.json's ``owdField`` values.
///
/// For objects not present in schema.json, ``GetOwdAsync`` returns "Private".
///
/// Parameters
/// ----------
/// sfClient      : SalesforceClient instance.
/// owdFieldMap   : Pre-built {objectName: owdField} dict.  If omitted,
///                 loaded from config/schema.json.
/// </summary>
public class OWDFetcher
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector.acl_engine");

    // OWD values that mean "any authenticated user in the org can read the record"
    private static readonly HashSet<string> PublicValues = new()
    {
        OWDVisibility.PublicRead.Value(),                // "Read"
        OWDVisibility.PublicReadWrite.Value(),           // "Edit"
        OWDVisibility.PublicReadWriteTransfer.Value(),   // "ReadEditTransfer"
        OWDVisibility.All.Value(),                       // "All"
    };

    // OWD values that mean "inherit sharing from the controlling parent record"
    private static readonly HashSet<string> ControlledByParentValues = new()
    {
        OWDVisibility.ControlledByParent.Value(),
        OWDVisibility.ControlledByCampaign.Value(),
        OWDVisibility.ControlledByLeadOrContact.Value(),
    };

    // ── EntityDefinition.InternalSharingModel → OWDVisibility value mapping ──────
    // EntityDefinition returns different string literals than the Organization table
    // fields.  This dict normalises them to the OWDVisibility values the rest of
    // the engine already understands.
    // Internal so tests can verify mapping completeness (Python `_ENTITY_DEF_TO_OWD_VALUE`).
    internal static readonly Dictionary<string, string> EntityDefToOwdValue = new()
    {
        ["Private"] = OWDVisibility.Private.Value(),
        ["Read"] = OWDVisibility.PublicRead.Value(),
        ["ReadSelect"] = OWDVisibility.PublicRead.Value(),
        ["ReadWrite"] = OWDVisibility.PublicReadWrite.Value(),
        ["ReadWriteTransfer"] = OWDVisibility.PublicReadWriteTransfer.Value(),
        ["FullAccess"] = OWDVisibility.All.Value(),
        ["ControlledByParent"] = OWDVisibility.ControlledByParent.Value(),
        ["ControlledByCampaign"] = OWDVisibility.ControlledByCampaign.Value(),
        ["ControlledByLeadOrContact"] = OWDVisibility.ControlledByLeadOrContact.Value(),
    };

    private readonly SalesforceClient _sf;
    // {objectName → owdField}  e.g. {"Account": "DefaultAccountAccess"}
    private readonly Dictionary<string, string> _owdFieldMap;
    // Populated on the first GetOwdAsync call; {objectName → owd_value}
    private Dictionary<string, string>? _orgOwdCache;
    // Optional overrides from config (e.g. {"Account": "Private"})
    private readonly Dictionary<string, string> _owdOverrides;

    // ── EntityDefinition flight ──────────────────────────────────────────
    // Internal so wiring tests can inspect them (Python `fetcher._use_entity_definition_owd`).
    internal readonly bool _useEntityDefinitionOwd;
    // All object names from schema.json (used for the EntityDefinition query)
    internal readonly List<string> _objectNames;
    // Populated once; {objectName → OWDVisibility value string}
    private Dictionary<string, string>? _entityDefCache;
    private readonly SemaphoreSlim _entityDefLock = new(1, 1);
    // ── End EntityDefinition flight ──────────────────────────────────────

    // The async lock prevents the thundering-herd problem where hundreds of
    // concurrent coroutines all see _orgOwdCache=null and each fire the
    // same failing OWD query.  Only ONE coroutine primes the cache; the
    // rest wait on the lock and find the cache already populated.
    private readonly SemaphoreSlim _primeLockAsync = new(1, 1);
    // The sync lock guards cross-thread reads of the cache dict.
    private readonly object _primeLock = new();

    public OWDFetcher(
        SalesforceClient sfClient,
        Dictionary<string, string>? owdFieldMap = null,
        Dictionary<string, string>? owdOverrides = null,
        bool useEntityDefinitionOwd = false,
        List<string>? objectNames = null)
    {
        _sf = sfClient;
        _owdFieldMap = LoadOwdFieldMap(owdFieldMap);
        _orgOwdCache = null;
        _owdOverrides = owdOverrides ?? new Dictionary<string, string>();
        _useEntityDefinitionOwd = useEntityDefinitionOwd;
        _objectNames = useEntityDefinitionOwd ? LoadObjectNames(objectNames) : new List<string>();
        _entityDefCache = null;
    }

    /// <summary>
    /// Return ``{objectName: owdField}`` for every object that declares an ``owdField``.
    ///
    /// Objects without ``owdField`` (e.g. Contact, which is ControlledByParent)
    /// are intentionally excluded – the resolver's parent-chain path handles them.
    /// </summary>
    private static Dictionary<string, string> LoadOwdFieldMap(Dictionary<string, string>? owdFieldMap = null)
    {
        if (owdFieldMap is not null)
            return new Dictionary<string, string>(owdFieldMap);
        Dictionary<string, string> mapping;
        try
        {
            mapping = Settings.BuildOwdFieldMap();
        }
        catch (Exception exc) when (exc is IOException or JsonException)
        {
            Logger.Warning($"[OWD] Cannot load schema.json: {exc.Message}");
            return new Dictionary<string, string>();
        }

        Logger.Debug($"[OWD] Loaded owdField map from schema.json: {ReprDict(mapping)}");
        return mapping;
    }

    /// <summary>Return all object names from schema.json.</summary>
    private static List<string> LoadObjectNames(List<string>? objectNames = null)
    {
        if (objectNames is not null)
            return new List<string>(objectNames);
        try
        {
            return Settings.BuildObjectNameList();
        }
        catch (Exception exc) when (exc is IOException or JsonException)
        {
            Logger.Warning($"[OWD] Cannot load object names from schema.json: {exc.Message}");
            return new List<string>();
        }
    }

    // ── Main fetch ────────────────────────────────────────────────────────────

    /// <summary>
    /// Return the OWD string for *objectType*.
    ///
    /// When ``useEntityDefinitionOwd`` is enabled:
    ///   1. Queries ``EntityDefinition`` for all objects in schema.json (once).
    ///   2. Maps ``InternalSharingModel`` to the canonical ``OWDVisibility`` value.
    ///   3. Falls back to the Organisation-table approach for any object not
    ///      found in the EntityDefinition result set.
    ///
    /// When disabled: behaves exactly as before (Organisation table query).
    ///
    /// Parameters
    /// ----------
    /// objectType : Salesforce API name, e.g. "Account", "MyCustomObj__c".
    ///
    /// Returns
    /// -------
    /// string – one of the OWDVisibility values (e.g. "Private", "Read", …).
    /// </summary>
    public async Task<string> GetOwdAsync(string objectType)
    {
        string? owd = null;

        // ── NEW PATH: EntityDefinition ────────────────────────────────────────
        if (_useEntityDefinitionOwd)
        {
            if (_entityDefCache is null)
            {
                await _entityDefLock.WaitAsync();
                try
                {
                    if (_entityDefCache is null)
                        _entityDefCache = await PrimeEntityDefinitionCacheAsync();
                }
                finally
                {
                    _entityDefLock.Release();
                }
            }

            if (_entityDefCache is not null && _entityDefCache.ContainsKey(objectType))
            {
                owd = _entityDefCache[objectType];
                Logger.Debug($"[OWD] {objectType} → {owd} (via EntityDefinition)");
            }
            else
            {
                Logger.Debug(
                    $"[OWD] {objectType} not found in EntityDefinition result; falling back to Organization query");
            }
        }
        // ── END NEW PATH ──────────────────────────────────────────────────────

        // ── OLD PATH (fallback): Organization table ───────────────────────────
        if (owd is null)
        {
            if (!_owdFieldMap.ContainsKey(objectType))
            {
                Logger.Debug(
                    $"[OWD] {objectType} has no owdField in schema.json → defaulting to Private");
                owd = OWDVisibility.Private.Value();
            }
            else
            {
                if (_orgOwdCache is null)
                {
                    await _primeLockAsync.WaitAsync();
                    try
                    {
                        if (_orgOwdCache is null)
                        {
                            var candidate = await PrimeOrgOwdCacheAsync();
                            lock (_primeLock)
                            {
                                _orgOwdCache = candidate;
                            }
                        }
                    }
                    finally
                    {
                        _primeLockAsync.Release();
                    }
                }

                var owdField = _owdFieldMap[objectType];
                owd = _orgOwdCache is not null && _orgOwdCache.TryGetValue(objectType, out var cachedOwd)
                    ? cachedOwd
                    : OWDVisibility.Private.Value();
                Logger.Debug($"[OWD] {objectType} (Organization.{owdField}) → {owd}");
            }
        }
        // ── END OLD PATH ──────────────────────────────────────────────────────

        // ── OWD OVERRIDE (from OWD_OVERRIDES config) ─────────────────────────
        if (_owdOverrides.ContainsKey(objectType))
        {
            Logger.Warning(
                $"[OWD] ⚠️  OWD OVERRIDE: {objectType} OWD forced from '{owd}' → '{_owdOverrides[objectType]}' (via OWD_OVERRIDES config)");
            owd = _owdOverrides[objectType];
        }
        // ── END OWD OVERRIDE ──────────────────────────────────────────────────

        return owd;
    }

    // ── EntityDefinition query (new path) ─────────────────────────────────────

    /// <summary>
    /// Query ``EntityDefinition`` for all objects in schema.json and return a
    /// cache dict ``{objectName: OWDVisibility value}``.
    ///
    /// Fires:
    ///     SELECT QualifiedApiName, InternalSharingModel
    ///     FROM EntityDefinition
    ///     WHERE QualifiedApiName IN ('Account', 'Contact', …)
    ///
    /// ``InternalSharingModel`` values are normalised to ``OWDVisibility``
    /// string values via ``EntityDefToOwdValue``.  Unknown values
    /// default to ``Private`` (principle of least privilege).
    /// </summary>
    private async Task<Dictionary<string, string>> PrimeEntityDefinitionCacheAsync()
    {
        if (_objectNames.Count == 0)
        {
            Logger.Warning("[OWD] No object names available for EntityDefinition query");
            return new Dictionary<string, string>();
        }

        var quoted = string.Join(", ", _objectNames.Select(name => $"'{name}'"));
        var soql =
            "SELECT QualifiedApiName, InternalSharingModel "
            + "FROM EntityDefinition "
            + $"WHERE QualifiedApiName IN ({quoted})";
        Logger.Info($"[OWD] Fetching OWD via EntityDefinition: {soql}");

        List<JsonObject> records;
        try
        {
            records = await _sf.QueryAllAsync(soql, tooling: true);
        }
        catch (InvalidOperationException exc)
        {
            Logger.Warning(
                $"[OWD] EntityDefinition query failed ({exc.Message}); will fall back to Organization query");
            return new Dictionary<string, string>();
        }

        var cache = new Dictionary<string, string>();
        foreach (var row in records)
        {
            var apiName = row["QualifiedApiName"]?.GetValue<string>();
            var rawModel = row["InternalSharingModel"]?.GetValue<string>();
            if (string.IsNullOrEmpty(apiName))
                continue;
            var owdValue = EntityDefToOwdValue.TryGetValue(rawModel ?? "", out var mapped)
                ? mapped
                : OWDVisibility.Private.Value();
            cache[apiName] = owdValue;
            Logger.Info(
                $"[OWD] EntityDefinition: {apiName} → InternalSharingModel='{rawModel}' → OWD='{owdValue}'");
        }

        // Log objects that were queried but not returned by EntityDefinition
        var missing = _objectNames.Where(n => !cache.ContainsKey(n)).ToList();
        if (missing.Count > 0)
            Logger.Warning($"[OWD] EntityDefinition returned no rows for: {ReprSet(missing)}");

        Logger.Info(
            $"[OWD] EntityDefinition cache primed for {cache.Count}/{_objectNames.Count} object(s)");
        return cache;
    }

    // ── Organization query (primes the in-memory cache) ───────────────────────

    /// <summary>
    /// Execute ONE ``SELECT &lt;owd_fields&gt; FROM Organization`` and return a
    /// cache dict keyed by objectName.
    ///
    /// Returns the dict — caller is responsible for assigning to _orgOwdCache
    /// under the sync lock.  This allows the await to happen outside the
    /// lock, preventing the deadlock that occurs when an awaiting coroutine
    /// holds a lock and suspends, blocking all other coroutines.
    /// </summary>
    private async Task<Dictionary<string, string>> PrimeOrgOwdCacheAsync()
    {
        var owdFields = _owdFieldMap.Values.Distinct().ToList();
        var soql = $"SELECT {string.Join(", ", owdFields)} FROM Organization";

        Logger.Info($"[OWD] Fetching OWD with: {soql}");

        List<JsonObject> records;
        try
        {
            records = await _sf.QueryAllAsync(soql);
        }
        catch (InvalidOperationException exc)
        {
            Logger.Warning(
                $"[OWD] Bulk Organization query failed ({exc.Message}); retrying per-field (defaulting to Private on failure)");
            return await FetchOwdPerFieldAsync();
        }

        if (records.Count == 0)
        {
            Logger.Warning("[OWD] Organization returned no rows; all objects default to Private");
            return new Dictionary<string, string>();
        }

        var orgRow = records[0];  // There is always exactly one Organization record

        var cache = new Dictionary<string, string>();
        foreach (var (objName, owdField) in _owdFieldMap)
        {
            var raw = orgRow[owdField]?.GetValue<string>();
            cache[objName] = !string.IsNullOrEmpty(raw) ? raw : OWDVisibility.Private.Value();
        }

        Logger.Info($"[OWD] Cache primed for {cache.Count} object(s): {ReprDict(cache)}");
        return cache;
    }

    /// <summary>
    /// Fall back: query each owdField individually against the Organization table.
    ///
    /// Multiple objects can share the same owdField (e.g. both Account and
    /// Opportunity might use ``DefaultAccountAccess``), so we deduplicate the
    /// field names and fan the result back out to all affected objects.
    ///
    /// Any field whose query fails defaults to ``Private`` — never to a
    /// permissive value — to preserve the principle of least privilege.
    /// </summary>
    private async Task<Dictionary<string, string>> FetchOwdPerFieldAsync()
    {
        // Invert map: owdField → [objectName, ...]
        var fieldToObjects = new Dictionary<string, List<string>>();
        foreach (var (objName, owdField) in _owdFieldMap)
        {
            if (!fieldToObjects.TryGetValue(owdField, out var list))
            {
                list = new List<string>();
                fieldToObjects[owdField] = list;
            }
            list.Add(objName);
        }

        var cache = new Dictionary<string, string>();
        foreach (var (owdField, objNames) in fieldToObjects)
        {
            var soql = $"SELECT {owdField} FROM Organization";
            string value;
            try
            {
                var records = await _sf.QueryAllAsync(soql);
                if (records.Count > 0)
                {
                    var raw = records[0][owdField]?.GetValue<string>();
                    value = !string.IsNullOrEmpty(raw) ? raw : OWDVisibility.Private.Value();
                }
                else
                {
                    Logger.Warning($"[OWD] Per-field query for {owdField} returned no rows; defaulting to Private");
                    value = OWDVisibility.Private.Value();
                }
            }
            catch (InvalidOperationException exc)
            {
                Logger.Warning(
                    $"[OWD] Per-field query for {owdField} failed ({exc.Message}); defaulting to Private");
                value = OWDVisibility.Private.Value();
            }

            foreach (var objName in objNames)
            {
                cache[objName] = value;
                Logger.Info($"[OWD] {objName} (Organization.{owdField}) → {value} (per-field fallback)");
            }
        }

        return cache;
    }

    // ── Predicates ────────────────────────────────────────────────────────────

    /// <summary>
    /// True when OWD grants read access to every user in the org.
    /// The resolver should emit a tenant-wide grant and skip further processing.
    /// </summary>
    public static bool IsPublic(string owd)
    {
        return PublicValues.Contains(owd);
    }

    /// <summary>
    /// True when the record's visibility is governed by its parent record.
    /// The resolver should fetch the parent record and repeat the OWD check.
    /// </summary>
    public static bool IsControlledByParent(string owd)
    {
        return ControlledByParentValues.Contains(owd);
    }

    /// <summary>
    /// True when OWD is Private (or unrecognised).
    /// The resolver must proceed to Step 3: record-level share table analysis.
    /// </summary>
    public static bool RequiresPrivateAcl(string owd)
    {
        return !PublicValues.Contains(owd) && !ControlledByParentValues.Contains(owd);
    }

    // ── Repr helpers (match Python dict/set log formatting) ────────────────────

    private static string ReprDict(Dictionary<string, string> d)
        => "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";

    private static string ReprSet(IEnumerable<string> items)
        => "{" + string.Join(", ", items.Select(i => $"'{i}'")) + "}";
}
