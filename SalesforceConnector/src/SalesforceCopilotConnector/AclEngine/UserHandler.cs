// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// acl_engine/user_handler.py
// --------------------------
// Step 3.2: Handle User-type principals.
//
// A Salesforce User ID always begins with the key-prefix "005".
//
// Responsibilities
// ----------------
// * Identify whether a given UserOrGroupId is a plain User (vs. a Group).
// * Validate that the user is active before adding to the allow list
//   (inactive / deactivated users must never appear in the final ACL).
// * Fetch full user detail when needed via the REST sobjects endpoint.
//
// Two fetch modes
// ---------------
// ResolveAsync(userId)
//     Lightweight SOQL: validates IsActive only.  Used in the main ACL pipeline
//     where we only need to know *whether* the user should be in the allow list.
//
// GetDetailsAsync(userId)
//     Full REST call to ``GET /sobjects/User/<user_id>``.  Returns the complete
//     user record as a dict.  Useful for downstream M365 principal mapping.
//
//     curl equivalent:
//         GET /services/data/v60.0/sobjects/User/<user_id>
//         Authorization: Bearer <access_token>

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.AclEngine;

/// <summary>
/// Resolves a single Salesforce User principal.
///
/// Parameters
/// ----------
/// sfClient : SalesforceClient instance.
/// </summary>
public class UserHandler
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector.acl_engine");

    public const string UserIdPrefix = "005";

    private readonly SalesforceClient _sf;
    // Pre-warm cache: set of all active Salesforce user IDs (null = not yet fetched)
    private HashSet<string>? _activeUsers;
    private readonly object _prewarmLock = new();

    public UserHandler(SalesforceClient sfClient)
    {
        _sf = sfClient;
        _activeUsers = null;
    }

    // ── Bulk pre-warm (once per run) ───────────────────────────────────────

    /// <summary>
    /// Fetch ALL active Salesforce user IDs in one SOQL call.
    /// After this, ResolveAsync() is a pure O(1) set lookup — no SOQL per user.
    /// </summary>
    public async Task PrewarmAsync()
    {
        if (_activeUsers is not null)
            return;

        var active = new HashSet<string>();
        try
        {
            var rows = await _sf.QueryAllAsync(
                "SELECT Id FROM User WHERE IsActive = true");
            active = rows
                .Select(r => r["Id"]?.GetValue<string>())
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!)
                .ToHashSet();
            Logger.Info($"[UserHandler] Pre-warmed {active.Count} active user(s)");
        }
        catch (InvalidOperationException exc)
        {
            Logger.Warning(
                $"[UserHandler] Active user prewarm failed: {exc.Message}; will fall back to per-user SOQL");
        }

        lock (_prewarmLock)
        {
            if (_activeUsers is null)
                _activeUsers = active;
        }
    }

    // ── Type detection ────────────────────────────────────────────────────────

    /// <summary>
    /// Return True when *principalId* is a Salesforce User record ID.
    ///
    /// Salesforce assigns key-prefix "005" to all User records, making this
    /// a reliable O(1) check before any network call is needed.
    ///
    /// If you are ever unsure whether the prefix is correct for your org,
    /// the GroupHandler's fallback path (query the Group table) will catch it.
    /// </summary>
    public static bool IsUserId(string principalId)
    {
        return !string.IsNullOrEmpty(principalId) && principalId.StartsWith(UserIdPrefix);
    }

    // ── ACL resolution (lightweight) ─────────────────────────────────────────

    /// <summary>Return ``{user_id}`` if the user is active, else an empty set.</summary>
    public async Task<HashSet<string>> ResolveAsync(string userId)
    {
        // Fast path — bulk cache hit
        if (_activeUsers is not null)
            return _activeUsers.Contains(userId) ? new HashSet<string> { userId } : new HashSet<string>();

        // Slow path — per-user SOQL fallback
        var soql =
            $"SELECT Id FROM User "
            + $"WHERE Id = '{userId}' AND IsActive = true "
            + $"LIMIT 1";
        List<JsonObject> records;
        try
        {
            records = await _sf.QueryAllAsync(soql);
        }
        catch (InvalidOperationException exc)
        {
            Logger.Warning($"[UserHandler] Could not validate user {userId}: {exc.Message}");
            return new HashSet<string>();
        }
        return records.Count > 0 ? new HashSet<string> { userId } : new HashSet<string>();
    }

    // ── Full user detail fetch (REST sobjects) ────────────────────────────────

    /// <summary>
    /// Fetch the complete User record via the sObject REST endpoint.
    ///
    /// This is the REST equivalent of:
    ///     GET /services/data/v60.0/sobjects/User/&lt;user_id&gt;
    ///
    /// Returns the full user payload dict (all standard + custom fields), or
    /// null on any error.
    ///
    /// When to use this vs. ResolveAsync()
    /// --------------------------------
    /// * ``ResolveAsync()`` is used in the ACL pipeline (fast, batch-friendly).
    /// * ``GetDetailsAsync()`` is for downstream operations that need field values
    ///   such as FederationIdentifier, Email, or UserName for M365 mapping.
    ///
    /// Parameters
    /// ----------
    /// userId : Salesforce User Id (18-char or 15-char).
    ///
    /// Returns
    /// -------
    /// JsonObject? - Raw Salesforce User record, or null on failure.
    /// </summary>
    public async Task<JsonObject?> GetDetailsAsync(string userId)
    {
        try
        {
            var details = await _sf.GetSObjectAsync(sobjectName: "User", recordId: userId);
            Logger.Debug(
                $"[UserHandler] Fetched details for user {userId}: "
                + $"IsActive={details["IsActive"]?.GetValue<bool>()} Email={details["Email"]?.GetValue<string>()}");
            return details;
        }
        catch (InvalidOperationException exc)
        {
            Logger.Warning($"[UserHandler] Could not fetch details for user {userId}: {exc.Message}");
            return null;
        }
    }
}
