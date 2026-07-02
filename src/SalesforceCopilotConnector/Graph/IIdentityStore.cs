// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Graph/IIdentityStore.cs
// ------------------------
// State-store abstraction for Identity Crawl group membership, sync sessions
// and the Salesforce field cache.
//
// Two implementations exist:
//
//   * IdentityStore          — SQLite file per connection (the default).
//   * SqlServerIdentityStore — SQL Server via stored procedures
//                              (USE_SQL_SERVER=true; see docs/SQL_CONTRACT.md).
//
// The interface is the exact public surface of the original SQLite store that
// its consumers (IdentityPublisher, Identity, Salesforce.ApiClient, the
// identity-dry-run command) rely on; both implementations share identical
// semantics — including diff behavior and the Python-isoformat timestamp
// strings in returned data.

namespace SalesforceCopilotConnector.Graph;

/// <summary>State store for external group membership, sync sessions and field cache.</summary>
public interface IIdentityStore : IDisposable
{
    // ── Group CRUD ────────────────────────────────────────────────────────────

    /// <summary>Return all group IDs tracked for this connection.</summary>
    HashSet<string> GetAllGroupIds();

    /// <summary>Check if a group is already tracked.</summary>
    bool GroupExists(string groupId);

    /// <summary>Insert or update a group record.</summary>
    void UpsertGroup(string groupId, string displayName = "", string description = "");

    /// <summary>Delete a group and all its members (CASCADE).</summary>
    void DeleteGroup(string groupId);

    // ── Member CRUD ───────────────────────────────────────────────────────────

    /// <summary>Return all current members of a group.</summary>
    HashSet<MemberEntry> GetMembers(string groupId);

    /// <summary>Add a single member to a group.</summary>
    void AddMember(string groupId, MemberEntry member);

    /// <summary>Remove a single member from a group.</summary>
    void RemoveMember(string groupId, MemberEntry member);

    /// <summary>Atomically replace all members of a group.</summary>
    void ReplaceMembers(string groupId, HashSet<MemberEntry> members);

    // ── Diff computation ──────────────────────────────────────────────────────

    /// <summary>Compare the desired state against the stored state and return a list of diffs.</summary>
    List<GroupDiff> ComputeDiff(Dictionary<string, (string DisplayName, HashSet<MemberEntry> Members)> newGroups);

    // ── Sync session tracking ─────────────────────────────────────────────────

    /// <summary>Create a new sync session record. Returns the session ID.</summary>
    string StartSession(string crawlType = "identity", string syncType = "full");

    /// <summary>Finalise a sync session with aggregate stats.</summary>
    void CompleteSession(string sessionId, SyncSessionStats stats, string status = "completed");

    /// <summary>Return the most recent completed sync session, or null.</summary>
    Dictionary<string, object?>? GetLastSession(string? crawlType = null);

    /// <summary>Return the start time (UTC) of the last successful content crawl, or null.</summary>
    DateTime? GetLastSuccessfulContentCrawlTime();

    // ── Field cache ───────────────────────────────────────────────────────────

    /// <summary>Return the cached field list for an org/object, or null if not cached.</summary>
    string[]? GetCachedFields(string instanceUrl, string objectType);

    /// <summary>Persist the working field list for an org/object.</summary>
    void SaveCachedFields(string instanceUrl, string objectType, string[] fields);

    /// <summary>Clear cached field entries. Returns the number of entries deleted.</summary>
    int ClearFieldCache(string? instanceUrl = null, string? objectType = null);

    // ── Utility ───────────────────────────────────────────────────────────────

    /// <summary>The Graph external connection ID this store tracks.</summary>
    string ConnectionId { get; }

    /// <summary>Human-readable backing-store location (SQLite file path or SQL Server database).</summary>
    string DbPath { get; }

    /// <summary>Return current counts of groups and members.</summary>
    Dictionary<string, int> GetStats();

    /// <summary>Close the underlying store.</summary>
    void Close();
}
