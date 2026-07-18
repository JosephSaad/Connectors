// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// AclEngine/PrincipalMapper.cs
// ----------------------------
// Converts raw Salesforce User IDs (output of AclResolver) into
// Graph-API-ready ACL entries.
//
// Pipeline
// --------
//   AclResult.user_ids  (set of Salesforce User IDs)
//         │
//         ▼  Step 1 – bulk SOQL fetch (single query)
//   {user_id: {FederationIdentifier, UserName, Email}}
//         │
//         ▼  Step 2 – pick best identifier per user
//   FederationIdentifier  →  UserName  →  Email
//         │
//         ▼  Step 3 – resolve to AAD GUID (if GraphClient present)
//   GET /users/<identifier>?$select=id
//   or filter by userPrincipalName / mail
//         │
//         ▼  Step 4 – format as Graph ACL entry
//   {"accessType": "grant", "type": "user", "value": "<aad_guid_or_upn>"}
//
// PUBLIC_SENTINEL handling
// ------------------------
// When AclResult.is_public is True (or PUBLIC_SENTINEL is in user_ids),
// returns a single tenant-wide grant entry:
//   {"accessType": "grant", "type": "everyone", "value": "<tenant_id>"}
//
// Batch size
// ----------
// The SOQL IN clause is capped at _BATCH_SIZE (100) IDs per query to avoid
// hitting Salesforce's per-query limit.

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Graph;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.AclEngine;

/// <summary>
/// Maps Salesforce User IDs → Graph-API-ready ACL list entries.
///
/// Parameters
/// ----------
/// sfClient     : <see cref="SalesforceClient"/> – used for bulk user identity SOQL.
/// graphClient  : Optional GraphClient (connector.graph.GraphClient).
///                When provided, identifiers are resolved to AAD Object GUIDs.
///                When null, FederationIdentifier / UserName / Email are used
///                directly as ACL values (works when UPN == SF identifier).
/// tenantId     : Azure tenant ID used in "everyone" ACL entries.
/// batchSize    : Maximum IDs per SOQL IN clause (from config).
/// </summary>
public class PrincipalMapper
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector.acl_engine");

    private readonly SalesforceClient _sf;
    private readonly GraphClient? _graphClient;
    private readonly string _tenantId;
    private readonly int _batchSize;
    // identifier (FedId/UPN/email) → resolved AAD GUID or null
    // Internal so tests can seed/inspect it (Python `mapper._principal_cache`).
    // ConcurrentDictionary because this instance is shared across parallel
    // object workers (Ingest launches up to N ToAclEntriesAsync tasks at once);
    // a plain Dictionary written concurrently can throw or return torn reads.
    internal readonly ConcurrentDictionary<string, string?> _principalCache = new();
    // In-flight dedup: identifier → the single running resolution Task, so N
    // concurrent misses for the same identifier collapse into ONE Graph lookup
    // instead of each firing its own.  Entries are transient — removed once the
    // lookup completes and its result has landed in _principalCache (which stays
    // the authoritative resolved-value cache read by tests / identity_publisher).
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inFlightResolves = new();
    // user IDs already warned about missing M365 principal (suppress duplicates)
    // Protected by a lock because 3 parallel object workers share this instance.
    private readonly HashSet<string> _warnedMissingPrincipals = new();
    private readonly object _warnedLock = new();
    // Pre-warm cache: user_id → {FederationIdentifier, UserName, Email}
    // Internal so tests can seed/inspect it (Python `mapper._user_details_cache`).
    // ConcurrentDictionary for the same shared-instance reason as _principalCache.
    internal readonly ConcurrentDictionary<string, JsonObject> _userDetailsCache = new();
    // Cross-module accessor mirroring Python's `mapper._user_details_cache`
    // usage from graph/identity_publisher.py.
    internal IReadOnlyDictionary<string, JsonObject> UserDetailsCache => _userDetailsCache;

    public PrincipalMapper(
        SalesforceClient sfClient,
        GraphClient? graphClient = null,
        string tenantId = "everyone",
        int batchSize = 100)
    {
        _sf = sfClient;
        _graphClient = graphClient;
        _tenantId = tenantId;
        _batchSize = batchSize;
    }

    // ── Bulk pre-warm (call once per chunk with all owner/share user IDs) ──────

    /// <summary>
    /// Bulk-fetch FederationIdentifier / UserName / Email for all <paramref name="userIds"/>
    /// that are not already in the cache.  After this, ToAclEntriesAsync() fires
    /// zero SOQL for identity lookups.
    /// </summary>
    public async Task PrewarmUsersAsync(HashSet<string> userIds, int batchSize = 200)
    {
        var missing = userIds.Where(uid => !_userDetailsCache.ContainsKey(uid)).ToList();
        if (missing.Count == 0)
            return;

        for (var i = 0; i < missing.Count; i += batchSize)
        {
            var batch = missing.Skip(i).Take(batchSize).ToList();
            var quoted = string.Join(", ", batch.Select(uid => $"'{uid}'"));
            var soql =
                $"SELECT Id, FederationIdentifier, UserName, Email " +
                $"FROM User WHERE Id IN ({quoted}) AND IsActive = true";
            try
            {
                var rows = await _sf.QueryAllAsync(soql);
                foreach (var r in rows)
                {
                    var uid = (string?)r["Id"];
                    if (!string.IsNullOrEmpty(uid))
                        _userDetailsCache[uid] = r;
                }
            }
            catch (InvalidOperationException exc)
            {
                Logger.Warning($"[PrincipalMapper] User details prewarm failed for batch: {exc.Message}");
            }
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Convert one AclResult into a Graph-API-ready ACL list.
    ///
    /// Returns
    /// -------
    /// list[dict]  e.g.
    ///     [{"accessType": "grant", "type": "user", "value": "&lt;aad_guid&gt;"}]
    /// or the public sentinel:
    ///     [{"accessType": "grant", "type": "everyone", "value": "&lt;tenant_id&gt;"}]
    /// or a deny-all if no users could be resolved:
    ///     [{"accessType": "deny",  "type": "everyone", "value": "&lt;tenant_id&gt;"}]
    /// </summary>
    public async Task<List<Dictionary<string, string>>> ToAclEntriesAsync(AclResult aclResult)
    {
        if (aclResult.IsPublic || aclResult.UserIds.Contains(Models.PublicSentinel))
            return PublicAcl();

        if (aclResult.UserIds.Count == 0)
            return DenyAllAcl();

        // Bulk-fetch identity fields for all user IDs in a single SOQL call
        // Use pre-warm cache when available to avoid per-record SOQL
        // (TryGetValue avoids a ContainsKey/indexer race with concurrent prewarm writes)
        var cached = new Dictionary<string, JsonObject>();
        foreach (var uid in aclResult.UserIds)
        {
            if (_userDetailsCache.TryGetValue(uid, out var details))
                cached[uid] = details;
        }
        var uncached = new HashSet<string>(aclResult.UserIds);
        uncached.ExceptWith(cached.Keys);
        Dictionary<string, JsonObject> userDetails;
        if (uncached.Count > 0)
        {
            var fetched = await FetchUserDetailsBulkAsync(uncached);
            userDetails = new Dictionary<string, JsonObject>(cached);
            foreach (var (k, v) in fetched)
                userDetails[k] = v;
        }
        else
        {
            userDetails = cached;
        }

        var entries = new List<Dictionary<string, string>>();
        var seen = new HashSet<string>();

        foreach (var userId in aclResult.UserIds.OrderBy(x => x, StringComparer.Ordinal))
        {
            var details = userDetails.GetValueOrDefault(userId);
            if (details is null)
            {
                Logger.Debug($"[PrincipalMapper] User {userId} not found in bulk fetch; skipped");
                continue;
            }

            var principal = await ResolvePrincipalAsync(details);
            if (string.IsNullOrEmpty(principal))
            {
                bool alreadyWarned;
                lock (_warnedLock)
                {
                    alreadyWarned = _warnedMissingPrincipals.Contains(userId);
                    if (!alreadyWarned)
                        _warnedMissingPrincipals.Add(userId);
                }
                if (!alreadyWarned)
                {
                    var identifierText = FirstNonEmpty(
                        (string?)details["Email"],
                        (string?)details["UserName"]) ?? "no-identifier";
                    Logger.Warning(
                        $"[PrincipalMapper] User {userId} ({identifierText}): no M365 principal found");
                }
                continue;
            }

            var normalized = principal.ToLowerInvariant();
            if (seen.Contains(normalized))
                continue;
            seen.Add(normalized);
            entries.Add(new Dictionary<string, string>
            {
                ["accessType"] = "grant",
                ["type"] = "user",
                ["value"] = principal,
            });
        }

        Logger.Info(
            $"[PrincipalMapper] {aclResult.ObjectType}/{aclResult.RecordId} → {entries.Count} ACL entries from {aclResult.UserIds.Count} user ID(s)");
        return entries.Count > 0 ? entries : DenyAllAcl();
    }

    // ── Bulk user identity fetch ──────────────────────────────────────────────

    /// <summary>
    /// Fetch FederationIdentifier, UserName, and Email for all <paramref name="userIds"/>
    /// in batches of _BATCH_SIZE using a single SOQL per batch.
    ///
    /// Returns {user_id: {field: value, ...}}.
    ///
    /// curl equivalent (one batch):
    ///     GET /services/data/v60.0/query
    ///         ?q=SELECT+Id%2CFederationIdentifier%2CUserName%2CEmail
    ///            +FROM+User+WHERE+Id+IN+('005...','005...')
    ///            +AND+IsActive=true
    /// </summary>
    private async Task<Dictionary<string, JsonObject>> FetchUserDetailsBulkAsync(
        HashSet<string> userIds)
    {
        var idList = userIds.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var batches = new List<List<string>>();
        for (var i = 0; i < idList.Count; i += _batchSize)
            batches.Add(idList.Skip(i).Take(_batchSize).ToList());

        var allDetails = new Dictionary<string, JsonObject>();

        foreach (var batch in batches)
        {
            var quoted = string.Join(", ", batch.Select(uid => $"'{uid}'"));
            var soql =
                $"SELECT Id, FederationIdentifier, UserName, Email " +
                $"FROM User " +
                $"WHERE Id IN ({quoted}) AND IsActive = true";
            List<JsonObject> records;
            try
            {
                records = await _sf.QueryAllAsync(soql);
            }
            catch (InvalidOperationException exc)
            {
                Logger.Warning($"[PrincipalMapper] Bulk user fetch failed: {exc.Message}");
                continue;
            }

            foreach (var r in records)
            {
                var uid = (string?)r["Id"];
                if (!string.IsNullOrEmpty(uid))
                    allDetails[uid] = r;
            }
        }

        Logger.Debug(
            $"[PrincipalMapper] Bulk fetch: {userIds.Count} requested, {allDetails.Count} returned");
        return allDetails;
    }

    // ── Principal resolution ──────────────────────────────────────────────────

    /// <summary>
    /// Pick the best identifier and resolve to an AAD GUID if possible.
    ///
    /// Priority: FederationIdentifier → UserName → Email
    /// For UserName we also try stripping the Salesforce-appended org suffix
    /// (e.g. ``john@nokia.com.cape2104`` → ``john@nokia.com``) because SF
    /// UserNames are globally unique but AAD UPNs are not suffixed.
    /// If GraphClient is available, attempts a Graph API lookup for each
    /// until one succeeds.
    /// </summary>
    // Internal: graph/identity_publisher.py calls Python's private `_resolve_principal`.
    internal async Task<string?> ResolvePrincipalAsync(JsonObject userDetails)
    {
        var seen = new HashSet<string>();
        var candidates = new List<string>();

        foreach (var field in new[] { "FederationIdentifier", "UserName", "Email" })
        {
            var identifier = ((string?)userDetails[field] ?? "").Trim();
            if (string.IsNullOrEmpty(identifier) || seen.Contains(identifier))
                continue;
            seen.Add(identifier);
            candidates.Add(identifier);
            // For UserName (and FederationIdentifier), also try stripping the
            // extra domain segment that Salesforce appends to make usernames
            // globally unique (e.g. "@company.com.sandboxSuffix" → "@company.com")
            if (field is "UserName" or "FederationIdentifier")
            {
                var stripped = StripSfUsernameSuffix(identifier);
                if (!string.IsNullOrEmpty(stripped) && !seen.Contains(stripped))
                {
                    seen.Add(stripped);
                    candidates.Add(stripped);
                }
            }
        }

        foreach (var candidate in candidates)
        {
            var resolved = await ResolveIdentifierAsync(candidate);
            if (!string.IsNullOrEmpty(resolved))
                return resolved;
        }
        return null;
    }

    /// <summary>
    /// Return the AAD GUID (or the identifier itself if it already looks like
    /// a GUID or if no Graph client is available).
    ///
    /// ``virtual`` so tests can substitute it, mirroring the Python tests'
    /// mocking of ``mapper._resolve_identifier``.
    /// </summary>
    internal virtual async Task<string?> ResolveIdentifierAsync(string identifier)
    {
        // Cache hit — resolved value already known.
        if (_principalCache.TryGetValue(identifier, out var cachedValue))
            return cachedValue;

        // Miss: collapse concurrent misses for the same identifier onto a single
        // resolution Task so the (potentially expensive) Graph lookup runs once.
        // Lazy<T> with the default ExecutionAndPublication mode guarantees the
        // factory body runs exactly once even if several threads race on GetOrAdd.
        var lazy = _inFlightResolves.GetOrAdd(
            identifier,
            key => new Lazy<Task<string?>>(() => ResolveUncachedAsync(key)));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            // The result is now in _principalCache, so future callers hit the fast
            // path; drop the in-flight entry to bound the map's size.
            _inFlightResolves.TryRemove(identifier, out _);
        }
    }

    /// <summary>
    /// Perform the actual (uncached) resolution for <paramref name="identifier"/> and
    /// record the result in <see cref="_principalCache"/>.  Only ever invoked once per
    /// identifier at a time via the in-flight dedup in <see cref="ResolveIdentifierAsync"/>.
    /// </summary>
    private async Task<string?> ResolveUncachedAsync(string identifier)
    {
        string? resolved;
        if (LooksLikeGuid(identifier))
        {
            // Already an AAD GUID — use it directly.
            resolved = identifier;
        }
        else if (_graphClient is null)
        {
            // No Graph client – use the raw identifier (UPN / email).
            resolved = identifier;
        }
        else
        {
            // Attempt Graph lookup.
            resolved = await LookupGraphUserIdAsync(identifier).ConfigureAwait(false);
        }

        _principalCache[identifier] = resolved;
        return resolved;
    }

    /// <summary>
    /// Resolve <paramref name="identifier"/> (UPN / email / FederationIdentifier) to an AAD
    /// Object GUID via two Graph API attempts:
    ///
    /// 1. Direct path:  GET /users/&lt;encoded_identifier&gt;?$select=id
    /// 2. Filter query: GET /users?$filter=userPrincipalName eq '...' or mail eq '...'
    ///
    /// Returns the GUID string, or null if not found.
    /// </summary>
    // Internal: tests exercise Python's private `_lookup_graph_user_id` directly.
    internal async Task<string?> LookupGraphUserIdAsync(string identifier)
    {
        // Attempt 1 – direct lookup by UPN / object ID
        var directPath = $"/users/{Uri.EscapeDataString(identifier)}?$select=id";
        try
        {
            var payload = await _graphClient!.GetAsync(directPath);
            if (payload is JsonObject payloadObj && !string.IsNullOrEmpty((string?)payloadObj["id"]))
            {
                Logger.Info(
                    $"[PrincipalMapper] Graph direct lookup ✓ {identifier} → {(string?)payloadObj["id"]}");
                return (string?)payloadObj["id"];
            }
        }
        catch (GraphApiError exc)
        {
            Logger.Debug($"[PrincipalMapper] Graph direct lookup 404/400 for {identifier} (status={exc.StatusCode})");
            if (exc.StatusCode is not (400 or 403 or 404))
                throw;
        }
        catch (Exception exc)
        {
            // Not a Graph API status — a transport/parse fault. Warn (not Debug):
            // the identifier will be reported "No AAD user found" below, and without
            // this line that reads as a missing account rather than an outage.
            Logger.Warning(
                $"[PrincipalMapper] Graph direct lookup failed for {identifier} "
                + $"({exc.GetType().Name}: {exc.Message}) — trying filter lookup");
        }

        // Attempt 2 – filter by UPN, mail, on-premises UPN, or employeeId.
        // IMPORTANT: Graph $filter with 'or' across properties is an "advanced"
        // query that requires ConsistencyLevel: eventual + $count=true, otherwise
        // the API returns 400 or silently returns an empty result set.
        // employeeId is purely alphanumeric, so only include it in the filter
        // when the identifier contains no special characters (no @, dots, etc.).
        var safeId = identifier.Replace("'", "''");
        var filterClauses =
            $"userPrincipalName eq '{safeId}'" +
            $" or mail eq '{safeId}'" +
            $" or onPremisesUserPrincipalName eq '{safeId}'";
        if (safeId.Length > 0 && safeId.All(char.IsLetterOrDigit))
            filterClauses += $" or employeeId eq '{safeId}'";
        var filterPath = $"/users?$select=id&$top=1&$count=true&$filter={filterClauses}";
        var eventualHeaders = new Dictionary<string, string> { ["ConsistencyLevel"] = "eventual" };
        try
        {
            var payload = await _graphClient!.GetAsync(filterPath, headers: eventualHeaders);
            var values = payload is JsonObject po ? po["value"] as JsonArray ?? new JsonArray() : new JsonArray();
            if (values.Count > 0 && values[0] is JsonObject first && !string.IsNullOrEmpty((string?)first["id"]))
            {
                var guid = (string)first["id"]!;
                Logger.Info(
                    $"[PrincipalMapper] Graph filter lookup ✓ {identifier} → {guid}");
                return guid;
            }
        }
        catch (GraphApiError exc)
        {
            Logger.Debug($"[PrincipalMapper] Graph filter lookup error for {identifier} (status={exc.StatusCode}): {exc.Message}");
            if (exc.StatusCode is not (400 or 403 or 404))
                throw;
        }
        catch (Exception exc)
        {
            // See the direct-lookup catch above: transport/parse fault, not a 404.
            Logger.Warning(
                $"[PrincipalMapper] Graph filter lookup failed for {identifier} "
                + $"({exc.GetType().Name}: {exc.Message}) — treating identifier as unresolved");
        }

        Logger.Warning($"[PrincipalMapper] No AAD user found for '{identifier}' (tried direct + filter on UPN/mail/onPremisesUPN/employeeId)");
        return null;
    }

    // ── ACL entry helpers ─────────────────────────────────────────────────────

    /// <summary>Return a single tenant-wide grant entry for publicly visible records.</summary>
    private List<Dictionary<string, string>> PublicAcl()
    {
        return new List<Dictionary<string, string>>
        {
            new() { ["accessType"] = "grant", ["type"] = "everyone", ["value"] = "everyone" },
        };
    }

    /// <summary>Return a deny-all entry when no users could be resolved.</summary>
    private List<Dictionary<string, string>> DenyAllAcl()
    {
        return new List<Dictionary<string, string>>
        {
            new() { ["accessType"] = "deny", ["type"] = "everyone", ["value"] = "everyone" },
        };
    }

    /// <summary>Mirror Python's ``a or b`` for identifier fallback in log text.</summary>
    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrEmpty(v))
                return v;
        }
        return null;
    }

    // ── Standalone helpers ────────────────────────────────────────────────────

    /// <summary>Return True if <paramref name="value"/> is already an AAD Object GUID.</summary>
    internal static bool LooksLikeGuid(string value)
    {
        var parts = value.Trim().Split('-');
        if (parts.Length != 5)
            return false;
        int[] expected = { 8, 4, 4, 4, 12 };
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length != expected[i] || !parts[i].All(c => "0123456789abcdefABCDEF".Contains(c)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Salesforce makes every username globally unique by appending an extra label
    /// to the domain portion (e.g. ``john@acme.com.sandboxSuffix``).  This helper
    /// returns the version with that trailing label stripped so the caller can try
    /// it as a normal corporate UPN / email against AAD.
    ///
    /// Rules:
    /// - Must contain exactly one ``@``.
    /// - The domain part must have **more than two** dot-separated labels
    ///   (``acme.com`` has two labels and should not be stripped).
    /// - Returns ``null`` (no stripping done) when the conditions are not met.
    ///
    /// Example::
    ///
    ///     StripSfUsernameSuffix("john@nokia.com.cape2104")  // → "john@nokia.com"
    ///     StripSfUsernameSuffix("john@nokia.com")           // → null
    /// </summary>
    internal static string? StripSfUsernameSuffix(string username)
    {
        if (!username.Contains('@'))
            return null;
        var atIdx = username.LastIndexOf('@');
        var local = username[..atIdx];
        var domain = username[(atIdx + 1)..];
        var labels = domain.Split('.');
        // Need at least 3 labels to strip one (e.g. "nokia.com.suffix" → "nokia.com")
        if (labels.Length <= 2)
            return null;
        var strippedDomain = string.Join(".", labels[..^1]);
        return $"{local}@{strippedDomain}";
    }
}
