// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// acl_engine/resolver.py
// -----------------------
// Step 1: Main ACL resolution interface.
//
// This is the single public entry point for the entire ACL engine.
// It orchestrates all the sub-steps and returns an AclResult for a single record.
//
// Full pipeline
// -------------
//   resolve(object_type, record_id)
//     │
//     ├─ Step 2: OWDFetcher.get_owd(object_type)
//     │           ├─ is_public?            → AclResult(is_public=True)  ← DONE
//     │           ├─ is_controlled_by_parent? → recurse to parent record ← DONE
//     │           └─ requires_private_acl? → continue ↓
//     │
//     └─ Step 3: _resolve_private_acl(object_type, record_id)
//                 │
//                 ├─ 3.1  ShareFetcher.get_owner_id()
//                 │         └─ _resolve_principal(owner_id)
//                 │               ├─ User (005...)  → UserHandler.resolve()
//                 │               └─ Group/other    → GroupHandler.resolve()
//                 │
//                 ├─ 3.2  ShareFetcher.get_share_entries()
//                 │
//                 └─ 3.3  For each share entry's UserOrGroupId:
//                           _resolve_principal(id)
//                             ├─ User (005...)  → UserHandler.resolve()
//                             │                    → {user_id} or {}
//                             └─ Group/other    → GroupHandler.resolve()
//                                                  ├─ 3.3.1 Role    → RoleHandler
//                                                  ├─ 3.3.2 Territory → TerritoryHandler
//                                                  └─ 3.3.3 Queue/Manager → QueueHandler
//
// Controlled-by-parent resolution
// --------------------------------
// When a record's OWD is "ControlledByParent", the resolver:
//   1. Discovers the controlling master-detail (or lookup) field via Tooling API.
//   2. Fetches the parent record ID.
//   3. Calls _resolve_internal on the parent (recursively, up to _MAX_PARENT_DEPTH).
// If the parent chain cannot be resolved, falls back to private ACL on the
// original record.

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.AclEngine;

/// <summary>
/// Resolves the effective set of Salesforce User Ids that can access a record.
///
/// Usage
/// -----
/// ::
///     var client = new SalesforceClient(
///         instanceUrl: "https://myorg.my.salesforce.com",
///         apiVersion: "60.0",
///         accessToken: "&lt;Bearer token&gt;");
///     var resolver = new AclResolver(client);
///
///     // Synchronous (blocks on the async pipeline internally)
///     var result = resolver.Resolve("Account", "001xxxxxxxxxxxx");
///
///     // Async (preferred inside an async context)
///     var result = await resolver.ResolveAsync("Account", "001xxxxxxxxxxxx");
///
///     if (result.IsPublic)
///         Console.WriteLine("Visible to everyone");
///     else
///         Console.WriteLine($"Visible to: {result.UserIds}");
///
/// Parameters
/// ----------
/// sfClient    : A configured SalesforceClient instance.
/// owdFieldMap : Pre-built {objectName: owdField}; defaults to loading from config/.
/// parentMap   : Pre-built {objectName: (parentFieldName, parentObjectName)}; defaults to loading from config/.
/// </summary>
public class AclResolver
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector.acl_engine");

    // Track object types for which we've already logged the missing-parent warning
    // so it fires at most once per object type instead of once per record.
    // Guarded by WarnedNoParentLock: the set is mutated during concurrent record
    // resolution and unsynchronized HashSet.Add can throw.
    private static readonly HashSet<string> WarnedNoParent = new();
    private static readonly object WarnedNoParentLock = new();

    // Maximum depth when following ControlledByParent chains to prevent runaway
    // recursion on misconfigured orgs or circular references.
    private const int DefaultMaxParentDepth = 5;

    private readonly SalesforceClient _sf;
    private readonly OWDFetcher _owdFetcher;
    private readonly ShareFetcher _shareFetcher;
    private readonly UserHandler _userHandler;
    private readonly GroupHandler _groupHandler;
    private readonly Dictionary<string, (string, string)> _parentMap;
    private readonly int _maxParentDepth;

    // Cross-module accessors mirroring Python's `resolver._owd_fetcher` /
    // `resolver._share_fetcher` usage from graph/ingest.py.
    internal OWDFetcher OwdFetcher => _owdFetcher;
    internal ShareFetcher ShareFetcher => _shareFetcher;

    public AclResolver(
        SalesforceClient sfClient,
        Dictionary<string, string>? owdFieldMap = null,
        Dictionary<string, (string, string)>? parentMap = null,
        Dictionary<string, string>? owdOverrides = null,
        int maxParentDepth = DefaultMaxParentDepth,
        bool useEntityDefinitionOwd = false,
        List<string>? objectNames = null)
    {
        _sf = sfClient;
        _owdFetcher = new OWDFetcher(
            sfClient,
            owdFieldMap: owdFieldMap,
            owdOverrides: owdOverrides,
            useEntityDefinitionOwd: useEntityDefinitionOwd,
            objectNames: objectNames);
        _shareFetcher = new ShareFetcher(sfClient);
        _userHandler = new UserHandler(sfClient);
        _groupHandler = new GroupHandler(sfClient);
        // Share the same UserHandler instance so prewarm is shared
        _groupHandler.Uh = _userHandler;
        _parentMap = LoadParentMap(parentMap);
        _maxParentDepth = maxParentDepth;
    }

    /// <summary>
    /// Return {objectName: (parentFieldName, parentObjectName)}
    /// for every object that has a 'parentObjectName' entry.
    ///
    /// parentFieldName is derived as '{parentObjectName}Id' (standard Salesforce
    /// convention, e.g. Contact → AccountId) unless the schema entry explicitly
    /// provides a 'parentFieldName' key.
    /// </summary>
    private static Dictionary<string, (string, string)> LoadParentMap(
        Dictionary<string, (string, string)>? parentMap = null)
    {
        if (parentMap is not null)
            return new Dictionary<string, (string, string)>(parentMap);
        return Settings.BuildParentMap();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Synchronous entry point.
    ///
    /// Internally blocks on the async pipeline.  Do not call this from inside
    /// an already-running async context – use ResolveAsync instead.
    /// </summary>
    public AclResult Resolve(string objectType, string recordId)
    {
        return ResolveAsync(objectType, recordId).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Bulk pre-warm all reference data for a chunk of records.
    ///
    /// Call this ONCE before launching the concurrent resolution of the chunk.
    /// Eliminates per-record SOQL queries for owners, share entries, groups,
    /// and role/user mappings.
    ///
    /// SOQL calls fired:
    ///   - 1 call:  SELECT &lt;owd_fields&gt; FROM Organization  (once ever, primed here)
    ///   - 2 × ceil(N/200) calls: bulk owner + bulk share-table per batch
    ///   - 1 call:  SELECT Id, ParentRoleId FROM UserRole  (once ever)
    ///   - 1 call:  SELECT Id, UserRoleId FROM User        (once ever)
    ///   - 1 call:  SELECT Id, Type, RelatedId FROM Group  (once ever)
    /// </summary>
    public async Task PrewarmChunkAsync(string objectType, List<string> recordIds)
    {
        // Per-chunk: owner IDs and share entries (batch_size=200 = safe SOQL IN limit)
        await _shareFetcher.PrewarmChunkAsync(objectType, recordIds, batchSize: 200);
        // One-time: all groups + roles + users + territories + group-members
        await _groupHandler.PrewarmAsync();
    }

    /// <summary>
    /// Async entry point – preferred when called from an async context.
    /// </summary>
    public async Task<AclResult> ResolveAsync(string objectType, string recordId)
    {
        Logger.Info(
            $"[AclResolver] START  object_type={objectType,-20}  record_id={recordId}");

        var result = await ResolveInternalAsync(objectType, recordId, depth: 0);

        Logger.Info(
            $"[AclResolver] DONE   is_public={result.IsPublic,-5}  user_count={result.UserIds.Count}");
        return result;
    }

    // ── Internal pipeline ─────────────────────────────────────────────────────

    /// <summary>
    /// Recursive core of the ACL pipeline.
    ///
    /// Evaluates the OWD for *objectType* and routes to the appropriate path:
    /// public grant, ControlledByParent recursion, or private ACL resolution.
    ///
    /// Parameters
    /// ----------
    /// objectType : Salesforce object API name (e.g. "Account").
    /// recordId   : 18-char Salesforce record Id.
    /// depth      : Current recursion depth (guards ControlledByParent chains).
    ///
    /// Returns
    /// -------
    /// AclResult with IsPublic / UserIds populated.
    /// </summary>
    private async Task<AclResult> ResolveInternalAsync(
        string objectType,
        string recordId,
        int depth)
    {
        var result = new AclResult(objectType: objectType, recordId: recordId);

        // ── Step 2: Fetch OWD and decide path ─────────────────────────────────
        var owd = await _owdFetcher.GetOwdAsync(objectType);
        result.Owd = owd;

        if (OWDFetcher.IsPublic(owd))
        {
            Logger.Info(
                $"[AclResolver] OWD={owd} is public → granting org-wide access for {objectType}/{recordId}");
            result.IsPublic = true;
            result.UserIds = new HashSet<string> { Models.PublicSentinel };
            return result;
        }

        if (OWDFetcher.IsControlledByParent(owd))
        {
            Logger.Info(
                $"[AclResolver] OWD={owd} → ControlledByParent for {objectType}/{recordId} (depth={depth})");
            return await ResolveControlledByParentAsync(objectType, recordId, depth);
        }

        // ── Step 3: Private ACL resolution ────────────────────────────────────
        Logger.Info(
            $"[AclResolver] OWD={owd} → private ACL required for {objectType}/{recordId}");
        result.UserIds = await ResolvePrivateAclAsync(objectType, recordId);
        return result;
    }

    // ── Step 3 orchestration ──────────────────────────────────────────────────

    /// <summary>
    /// Core private ACL pipeline for a single record.
    ///
    /// Steps
    /// -----
    /// 3.1  Fetch the record's OwnerId and resolve it.
    /// 3.2  Fetch all share table entries for the record.
    /// 3.3  Expand each UserOrGroupId to a set of User Ids.
    /// </summary>
    private async Task<HashSet<string>> ResolvePrivateAclAsync(string objectType, string recordId)
    {
        var allUserIds = new HashSet<string>();

        // ── 3.1  Owner ────────────────────────────────────────────────────────
        var ownerId = await _shareFetcher.GetOwnerIdAsync(objectType, recordId);
        if (!string.IsNullOrEmpty(ownerId))
        {
            Logger.Info($"[AclResolver] 3.1 Owner → {ownerId}");
            var ownerUsers = await ResolvePrincipalAsync(ownerId);
            if (ownerUsers.Contains(Models.PublicSentinel))
                return ownerUsers;  // org-owned record
            allUserIds.UnionWith(ownerUsers);
        }

        // ── 3.2  Share table entries ──────────────────────────────────────────
        var shareEntries = await _shareFetcher.GetShareEntriesAsync(objectType, recordId);
        Logger.Info($"[AclResolver] 3.2 Share entries: {shareEntries.Count}");

        // ── 3.3  Expand each UserOrGroupId ────────────────────────────────────
        foreach (var entry in shareEntries)
        {
            var principalId = entry.UserOrGroupId;
            Logger.Debug($"[AclResolver] 3.3 Expanding principal {principalId} (RowCause={entry.RowCause})");

            var users = await ResolvePrincipalAsync(principalId);

            if (users.Contains(Models.PublicSentinel))
            {
                // Organisation-wide share – no need to enumerate individuals
                Logger.Info(
                    $"[AclResolver] 3.3 Principal {principalId} resolved to PUBLIC_SENTINEL → "
                    + "short-circuiting with org-wide access");
                return new HashSet<string> { Models.PublicSentinel };
            }

            allUserIds.UnionWith(users);
        }

        Logger.Info(
            $"[AclResolver] 3.3 Total resolved users for {objectType}/{recordId}: {allUserIds.Count}");
        return allUserIds;
    }

    /// <summary>
    /// Route a single UserOrGroupId to the correct handler.
    ///
    /// - User (starts with "005") → UserHandler.ResolveAsync
    /// - Everything else          → GroupHandler.ResolveAsync (dispatches to
    ///                              Role / Territory / Queue handlers)
    /// </summary>
    private async Task<HashSet<string>> ResolvePrincipalAsync(string principalId)
    {
        if (UserHandler.IsUserId(principalId))
            return await _userHandler.ResolveAsync(principalId);
        return await _groupHandler.ResolveAsync(principalId);
    }

    // ── ControlledByParent resolution ─────────────────────────────────────────

    /// <summary>
    /// Inherit ACL from the controlling parent record.
    ///
    /// Guards
    /// ------
    /// * Depth limit (_MAX_PARENT_DEPTH) – fall back to private ACL on the
    ///   original record if the chain is too deep.
    /// * Missing parent info – fall back gracefully.
    /// </summary>
    private async Task<AclResult> ResolveControlledByParentAsync(
        string objectType,
        string recordId,
        int depth)
    {
        if (depth >= _maxParentDepth)
        {
            Logger.Warning(
                $"[AclResolver] Max parent depth ({_maxParentDepth}) reached for {objectType}/{recordId}; "
                + "falling back to private ACL on this record");
            var result = new AclResult(objectType: objectType, recordId: recordId, owd: "ControlledByParent");
            result.UserIds = await ResolvePrivateAclAsync(objectType, recordId);
            return result;
        }

        var (parentField, parentType) = await GetControllingParentInfoAsync(objectType);

        if (string.IsNullOrEmpty(parentField) || string.IsNullOrEmpty(parentType))
        {
            bool firstWarn;
            lock (WarnedNoParentLock)
            {
                firstWarn = WarnedNoParent.Add(objectType);
            }
            if (firstWarn)
            {
                Logger.Warning(
                    $"[AclResolver] Cannot determine controlling parent for {objectType}; "
                    + "falling back to private ACL (this warning fires once per object type)");
            }
            var result = new AclResult(objectType: objectType, recordId: recordId, owd: "ControlledByParent");
            result.UserIds = await ResolvePrivateAclAsync(objectType, recordId);
            return result;
        }

        var parentId = await FetchFieldValueAsync(objectType, recordId, parentField);

        if (string.IsNullOrEmpty(parentId))
        {
            Logger.Warning(
                $"[AclResolver] Parent record not found for {objectType}/{recordId} (field={parentField}); "
                + "falling back to private ACL");
            var result = new AclResult(objectType: objectType, recordId: recordId, owd: "ControlledByParent");
            result.UserIds = await ResolvePrivateAclAsync(objectType, recordId);
            return result;
        }

        Logger.Info(
            $"[AclResolver] {objectType}/{recordId} → parent {parentType}/{parentId} via field {parentField} (depth={depth + 1})");
        return await ResolveInternalAsync(parentType, parentId, depth + 1);
    }

    /// <summary>
    /// Return the controlling parent field and object type for *objectType*
    /// by looking up the 'parentObjectName' / 'parentFieldName' entries in
    /// schema.json (loaded once at construction time into _parentMap).
    ///
    /// parentFieldName defaults to '{parentObjectName}Id' when not explicitly
    /// set in schema.json (standard Salesforce naming convention).
    ///
    /// Returns (fieldApiName, parentObjectType) or (null, null) when the
    /// object has no parent entry in schema.json.
    /// </summary>
    // Internal so tests can exercise the warn-dedup path under concurrency.
    internal Task<(string?, string?)> GetControllingParentInfoAsync(string objectType)
    {
        if (_parentMap.TryGetValue(objectType, out var entry))
        {
            var (parentField, parentType) = entry;
            Logger.Info(
                $"[AclResolver] Parent info for {objectType}: field={parentField} → {parentType} (from schema.json)");
            return Task.FromResult<(string?, string?)>((parentField, parentType));
        }

        bool firstNoParentWarn;
        lock (WarnedNoParentLock)
        {
            firstNoParentWarn = WarnedNoParent.Add(objectType);
        }
        if (firstNoParentWarn)
        {
            Logger.Warning(
                $"[AclResolver] No parentObjectName in schema.json for {objectType} – "
                + "cannot follow ControlledByParent chain (this warning fires once per object type)");
        }
        return Task.FromResult<(string?, string?)>((null, null));
    }

    /// <summary>Read a single field value from a record.</summary>
    private async Task<string?> FetchFieldValueAsync(
        string objectType, string recordId, string fieldName)
    {
        var soql = $"SELECT {fieldName} FROM {objectType} WHERE Id = '{recordId}' LIMIT 1";
        List<JsonObject> records;
        try
        {
            records = await _sf.QueryAllAsync(soql);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        if (records.Count == 0)
            return null;
        return records[0][fieldName]?.GetValue<string>();
    }
}
