// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Graph/IdentityStore.cs
// -----------------------
// SQLite-backed state store for Identity Crawl group membership.
//
// Maintains a persistent record of which external groups exist and who their
// members are.  On each crawl the publisher compares the new crawl result
// against this store to compute a minimal diff — only changed groups/members
// trigger Microsoft Graph API calls.
//
// Database location
// -----------------
// One SQLite file per connection, stored at:
//
//     {repo_root}/data/{connection_id}_identity.db
//
// The ``data/`` directory is created on first use.
//
// Schema
// ------
// ``groups``
//     One row per external group that has been published to Graph.
//
// ``group_members``
//     One row per member (user or nested group) of an external group.
//
// ``sync_sessions``
//     Audit log of every identity sync run with aggregate stats.
//
// Thread safety
// -------------
// SQLite in WAL mode — safe for the single-writer / multiple-reader pattern
// used by the connector.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Graph;

// ── Data containers ───────────────────────────────────────────────────────────

/// <summary>A single member of an external group.</summary>
public sealed class MemberEntry
{
    public string MemberId { get; }
    public string MemberType { get; }  // "user" or "externalGroup"
    public string IdentitySource { get; }  // "external" or "azureActiveDirectory"

    public MemberEntry(string memberId, string memberType, string identitySource = "external")
    {
        MemberId = memberId;
        MemberType = memberType;
        IdentitySource = identitySource;
    }

    public override bool Equals(object? other)
    {
        if (other is not MemberEntry entry)
            return false;
        return MemberId == entry.MemberId && MemberType == entry.MemberType;
    }

    public override int GetHashCode() => HashCode.Combine(MemberId, MemberType);
}

/// <summary>Change set for one external group.</summary>
public class GroupDiff
{
    public string GroupId { get; set; } = "";
    public string Action { get; set; } = "";  // "create", "update", "delete", "unchanged"
    public string DisplayName { get; set; } = "";
    public List<MemberEntry> MembersToAdd { get; set; } = new();
    public List<MemberEntry> MembersToRemove { get; set; } = new();

    public bool HasChanges => Action != "unchanged";

    /// <summary>Estimate number of Graph API calls this diff will produce.</summary>
    public int ApiCallsNeeded
    {
        get
        {
            if (Action == "unchanged")
                return 0;
            if (Action == "delete")
                return 1;
            var calls = 0;
            if (Action == "create")
                calls += 1;  // PUT group
            if (MembersToAdd.Count > 0)
                calls += MembersToAdd.Count;  // one POST per member
            if (MembersToRemove.Count > 0)
                calls += MembersToRemove.Count;  // one DELETE per member
            return calls;
        }
    }
}

/// <summary>
/// Aggregate stats for one sync session (identity or content crawl).
///
/// SessionId : Unique session identifier (UUID).
/// SyncType  : "full" or "incremental".
///
/// Identity crawl stats (Groups* / Members* / ApiCallsMade):
///     Populated by <c>IdentityPublisher.PublishAsync()</c> for identity crawl sessions.
///
/// Content crawl stats (Content*):
///     Populated by <c>RecordContentCrawl()</c> after <c>IngestContent()</c>.
///
/// Errors : Total error count across both identity and content phases.
/// </summary>
public class SyncSessionStats
{
    public string SessionId { get; set; } = "";
    public string SyncType { get; set; } = "full";  // "full" or "incremental"
    // Identity crawl stats
    public int GroupsCreated { get; set; }
    public int GroupsUpdated { get; set; }
    public int GroupsDeleted { get; set; }
    public int GroupsUnchanged { get; set; }
    public int MembersAdded { get; set; }
    public int MembersRemoved { get; set; }
    public int ApiCallsMade { get; set; }
    public int Errors { get; set; }
    // Content crawl stats
    public int ContentTotalFetched { get; set; }
    public int ContentSuccess { get; set; }
    public int ContentFailed { get; set; }
    public int ContentDeleted { get; set; }
    public string ContentAclEngine { get; set; } = "";
}

// ── Store class ───────────────────────────────────────────────────────────────

/// <summary>
/// SQLite-backed state store for external group membership.
///
/// Parameters
/// ----------
/// dbPath       : Path to the SQLite database file.
/// connectionId : The Graph external connection ID this store tracks.
/// </summary>
public class IdentityStore : IIdentityStore
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector.identity_store");

    // ── Schema DDL ────────────────────────────────────────────────────────────

    private const string SchemaDdl = @"
CREATE TABLE IF NOT EXISTS groups (
    group_id        TEXT PRIMARY KEY,
    connection_id   TEXT NOT NULL,
    display_name    TEXT NOT NULL DEFAULT '',
    description     TEXT NOT NULL DEFAULT '',
    created_at      TEXT NOT NULL,
    updated_at      TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS group_members (
    group_id        TEXT NOT NULL,
    member_id       TEXT NOT NULL,
    member_type     TEXT NOT NULL,
    identity_source TEXT NOT NULL DEFAULT 'external',
    added_at        TEXT NOT NULL,
    PRIMARY KEY (group_id, member_id),
    FOREIGN KEY (group_id) REFERENCES groups(group_id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_group_members_group
    ON group_members(group_id);

CREATE TABLE IF NOT EXISTS sync_sessions (
    session_id       TEXT PRIMARY KEY,
    connection_id    TEXT NOT NULL,
    crawl_type       TEXT NOT NULL DEFAULT 'identity',
    sync_type        TEXT NOT NULL DEFAULT 'full',
    started_at       TEXT NOT NULL,
    completed_at     TEXT,
    status           TEXT NOT NULL DEFAULT 'running',
    groups_created   INTEGER NOT NULL DEFAULT 0,
    groups_updated   INTEGER NOT NULL DEFAULT 0,
    groups_deleted   INTEGER NOT NULL DEFAULT 0,
    groups_unchanged INTEGER NOT NULL DEFAULT 0,
    members_added    INTEGER NOT NULL DEFAULT 0,
    members_removed  INTEGER NOT NULL DEFAULT 0,
    api_calls_made   INTEGER NOT NULL DEFAULT 0,
    errors           INTEGER NOT NULL DEFAULT 0,
    content_total_fetched INTEGER NOT NULL DEFAULT 0,
    content_success  INTEGER NOT NULL DEFAULT 0,
    content_failed   INTEGER NOT NULL DEFAULT 0,
    content_deleted  INTEGER NOT NULL DEFAULT 0,
    content_acl_engine TEXT NOT NULL DEFAULT ''
);

CREATE TABLE IF NOT EXISTS field_cache (
    object_type    TEXT NOT NULL,
    instance_hash  TEXT NOT NULL,
    fields         TEXT NOT NULL,
    cached_at      TEXT NOT NULL,
    PRIMARY KEY (object_type, instance_hash)
);

-- WP-SF-2: cached Salesforce field-level read permissions, keyed exactly like
-- field_cache above. Added via the existing idempotent CREATE TABLE IF NOT EXISTS
-- migration path (InitSchema runs the full DDL on every open, so pre-existing
-- databases gain the table on first connect without touching any other DDL).
CREATE TABLE IF NOT EXISTS fls_cache (
    object_type    TEXT NOT NULL,
    instance_hash  TEXT NOT NULL,
    permissions    TEXT NOT NULL,
    cached_at      TEXT NOT NULL,
    PRIMARY KEY (object_type, instance_hash)
);
";

    private readonly string _dbPath;
    private readonly string _connectionId;
    private readonly SqliteConnection _conn;

    public IdentityStore(string dbPath, string connectionId)
    {
        _dbPath = dbPath;
        _connectionId = connectionId;
        var parent = Path.GetDirectoryName(Path.GetFullPath(_dbPath));
        if (!string.IsNullOrEmpty(parent))
            SecureDirectory.EnsureOwnerOnly(parent);  // #3: state dir owner-only
        // Autocommit; we manage transactions explicitly.
        _conn = new SqliteConnection($"Data Source={_dbPath}");
        _conn.Open();
        WithBusyRetry(() =>
        {
            Execute("PRAGMA journal_mode=WAL");
            // Contended writes wait rather than failing immediately. WAL above
            // removes the SHARED->RESERVED upgrade that returns SQLITE_BUSY without
            // consulting the busy handler; this covers the write-write contention
            // that remains, which does honour it.
            Execute("PRAGMA busy_timeout=10000");
            Execute("PRAGMA foreign_keys=ON");
            InitSchema();
        });
    }

    /// <summary>
    /// Run the one-time open work, retrying while SQLite reports the file busy.
    /// </summary>
    /// <remarks>
    /// Neither half of opening this store is covered by busy_timeout, and both
    /// need a brief EXCLUSIVE lock: switching journal_mode to WAL, and the
    /// CREATE TABLE / ALTER TABLE that InitSchema runs on every open. Two
    /// processes opening the same identity database together — a service restart
    /// overlapping the outgoing process, an HA pair starting up — collide on
    /// either and get SQLITE_BUSY ("database is locked") thrown straight out of
    /// the constructor.
    ///
    /// busy_timeout cannot cover the journal_mode switch because the pragma that
    /// SETS busy_timeout has not run yet; it cannot cover the DDL because a
    /// journal_mode transition holds an exclusive lock that the busy handler is
    /// deliberately not consulted for. This is why the retry wraps the whole
    /// operation rather than one statement: an earlier fix elsewhere in the fleet
    /// wrapped only the pragmas and the DDL one statement later failed on a
    /// contended Windows runner in exactly the same way.
    ///
    /// Retrying is safe because every step is idempotent and one-way —
    /// journal_mode is persisted in the file, and the schema DDL is
    /// IF NOT EXISTS / additive — so whoever wins leaves the store ready and
    /// every later attempt simply confirms it.
    ///
    /// Windows enforces the locking that exposes this; POSIX hides it, so it only
    /// ever fails on Windows Server, the deployment target.
    /// </remarks>
    private static void WithBusyRetry(Action work)
    {
        const int Attempts = 20;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                work();
                return;
            }
            catch (SqliteException exc) when ((exc.SqliteErrorCode is 5 or 6) && attempt < Attempts)
            {
                // 5 = SQLITE_BUSY, 6 = SQLITE_LOCKED.
                Thread.Sleep(25);
            }
        }
    }

    /// <summary>Create tables if they don't exist, and migrate if needed.</summary>
    private void InitSchema()
    {
        Execute(SchemaDdl);
        Migrate();
    }

    /// <summary>Close the database connection.</summary>
    public void Close()
    {
        try
        {
            _conn.Close();
        }
        catch (Exception)
        {
            // pass (Python parity): best-effort close — SQLite teardown failures
            // have no operational consequence and nowhere useful to be reported.
        }
    }

    public void Dispose() => Close();

    /// <summary>Add columns that may be missing from an older schema version.</summary>
    private void Migrate()
    {
        var existing = new HashSet<string>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(sync_sessions)";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                existing.Add(reader.GetString(1));
        }
        var migrations = new (string ColName, string ColDef)[]
        {
            ("crawl_type", "TEXT NOT NULL DEFAULT 'identity'"),
            ("sync_type", "TEXT NOT NULL DEFAULT 'full'"),
            ("content_total_fetched", "INTEGER NOT NULL DEFAULT 0"),
            ("content_success", "INTEGER NOT NULL DEFAULT 0"),
            ("content_failed", "INTEGER NOT NULL DEFAULT 0"),
            ("content_deleted", "INTEGER NOT NULL DEFAULT 0"),
            ("content_acl_engine", "TEXT NOT NULL DEFAULT ''"),
        };
        foreach (var (colName, colDef) in migrations)
        {
            if (!existing.Contains(colName))
            {
                Execute($"ALTER TABLE sync_sessions ADD COLUMN {colName} {colDef}");
                Logger.Info($"[IdentityStore] Migrated: added column sync_sessions.{colName}");
            }
        }
    }

    // ── Group CRUD ────────────────────────────────────────────────────────────

    /// <summary>Return all group IDs tracked for this connection.</summary>
    public HashSet<string> GetAllGroupIds()
    {
        var result = new HashSet<string>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT group_id FROM groups WHERE connection_id = @p0";
        cmd.Parameters.AddWithValue("@p0", _connectionId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>Check if a group is already tracked.</summary>
    public bool GroupExists(string groupId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM groups WHERE group_id = @p0 AND connection_id = @p1";
        cmd.Parameters.AddWithValue("@p0", groupId);
        cmd.Parameters.AddWithValue("@p1", _connectionId);
        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }

    /// <summary>Insert or update a group record.</summary>
    public void UpsertGroup(string groupId, string displayName = "", string description = "")
    {
        var now = NowIso();
        Execute(
            @"
            INSERT INTO groups (group_id, connection_id, display_name, description, created_at, updated_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5)
            ON CONFLICT(group_id) DO UPDATE SET
                display_name = excluded.display_name,
                description  = excluded.description,
                updated_at   = excluded.updated_at
            ",
            groupId, _connectionId, displayName, description, now, now);
    }

    /// <summary>Delete a group and all its members (CASCADE).</summary>
    public void DeleteGroup(string groupId)
    {
        Execute("DELETE FROM groups WHERE group_id = @p0", groupId);
    }

    // ── Member CRUD ───────────────────────────────────────────────────────────

    /// <summary>Return all current members of a group.</summary>
    public HashSet<MemberEntry> GetMembers(string groupId)
    {
        var result = new HashSet<MemberEntry>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT member_id, member_type, identity_source FROM group_members WHERE group_id = @p0";
        cmd.Parameters.AddWithValue("@p0", groupId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(new MemberEntry(memberId: reader.GetString(0), memberType: reader.GetString(1), identitySource: reader.GetString(2)));
        return result;
    }

    /// <summary>Add a single member to a group.</summary>
    public void AddMember(string groupId, MemberEntry member)
    {
        Execute(
            @"
            INSERT OR IGNORE INTO group_members (group_id, member_id, member_type, identity_source, added_at)
            VALUES (@p0, @p1, @p2, @p3, @p4)
            ",
            groupId, member.MemberId, member.MemberType, member.IdentitySource, NowIso());
    }

    /// <summary>Remove a single member from a group.</summary>
    public void RemoveMember(string groupId, MemberEntry member)
    {
        Execute(
            "DELETE FROM group_members WHERE group_id = @p0 AND member_id = @p1",
            groupId, member.MemberId);
    }

    /// <summary>
    /// Atomically replace all members of a group.
    ///
    /// Used after a successful full-group publish to ensure the store
    /// matches the state that was pushed to Graph.
    /// </summary>
    public void ReplaceMembers(string groupId, HashSet<MemberEntry> members)
    {
        Execute("BEGIN");
        try
        {
            Execute("DELETE FROM group_members WHERE group_id = @p0", groupId);
            var now = NowIso();
            foreach (var m in members)
            {
                Execute(
                    @"
                    INSERT INTO group_members (group_id, member_id, member_type, identity_source, added_at)
                    VALUES (@p0, @p1, @p2, @p3, @p4)
                    ",
                    groupId, m.MemberId, m.MemberType, m.IdentitySource, now);
            }
            Execute("COMMIT");
        }
        catch (Exception)
        {
            Execute("ROLLBACK");
            throw;
        }
    }

    // ── Diff computation ──────────────────────────────────────────────────────

    /// <summary>
    /// Compare <paramref name="newGroups"/> against the stored state and return a list of diffs.
    ///
    /// Parameters
    /// ----------
    /// newGroups : <c>{group_id: (display_name, {MemberEntry, ...})}</c>
    ///     The complete desired state from the current crawl.
    ///
    /// Returns
    /// -------
    /// List&lt;GroupDiff&gt; — one entry per group that needs action.
    ///
    /// Change detection logic
    /// ----------------------
    /// - Group in new but not in store → "create" (PUT group + POST all members)
    /// - Group in both, members changed → "update" (POST adds + DELETE removes)
    /// - Group in both, members identical → "unchanged" (skip)
    /// - Group in store but not in new → "delete" (DELETE group)
    /// </summary>
    public List<GroupDiff> ComputeDiff(Dictionary<string, (string DisplayName, HashSet<MemberEntry> Members)> newGroups)
    {
        var diffs = new List<GroupDiff>();
        var existingIds = GetAllGroupIds();
        var newIds = new HashSet<string>(newGroups.Keys);

        // ── Groups to create or update ────────────────────────────────────────
        foreach (var (groupId, (displayName, desiredMembers)) in newGroups)
        {
            if (!existingIds.Contains(groupId))
            {
                // Brand-new group
                diffs.Add(new GroupDiff
                {
                    GroupId = groupId,
                    Action = "create",
                    DisplayName = displayName,
                    MembersToAdd = desiredMembers.ToList(),
                    MembersToRemove = new List<MemberEntry>(),
                });
            }
            else
            {
                // Existing group — diff members
                var currentMembers = GetMembers(groupId);
                var toAdd = new HashSet<MemberEntry>(desiredMembers);
                toAdd.ExceptWith(currentMembers);
                var toRemove = new HashSet<MemberEntry>(currentMembers);
                toRemove.ExceptWith(desiredMembers);

                if (toAdd.Count > 0 || toRemove.Count > 0)
                {
                    diffs.Add(new GroupDiff
                    {
                        GroupId = groupId,
                        Action = "update",
                        DisplayName = displayName,
                        MembersToAdd = toAdd.ToList(),
                        MembersToRemove = toRemove.ToList(),
                    });
                }
                else
                {
                    diffs.Add(new GroupDiff
                    {
                        GroupId = groupId,
                        Action = "unchanged",
                        DisplayName = displayName,
                    });
                }
            }
        }

        // ── Groups to delete ──────────────────────────────────────────────────
        foreach (var staleId in existingIds.Where(id => !newIds.Contains(id)))
        {
            diffs.Add(new GroupDiff
            {
                GroupId = staleId,
                Action = "delete",
            });
        }

        return diffs;
    }

    // ── Sync session tracking ─────────────────────────────────────────────────

    /// <summary>Create a new sync session record. Returns the session ID.
    ///
    /// Parameters
    /// ----------
    /// crawlType : What is being crawled.
    ///             "identity" — external group creation/update.
    ///             "content"  — Salesforce record ingestion.
    ///             "identity-dry-run" — preview without Graph calls.
    /// syncType  : How much data is crawled.
    ///             "full"        — all records from scratch.
    ///             "incremental" — only records modified since last crawl.
    /// </summary>
    public string StartSession(string crawlType = "identity", string syncType = "full")
    {
        var sessionId = Guid.NewGuid().ToString();
        Execute(
            @"
            INSERT INTO sync_sessions
                (session_id, connection_id, crawl_type, sync_type, started_at, status)
            VALUES (@p0, @p1, @p2, @p3, @p4, 'running')
            ",
            sessionId, _connectionId, crawlType, syncType, NowIso());
        return sessionId;
    }

    /// <summary>Finalise a sync session with aggregate stats.</summary>
    public void CompleteSession(string sessionId, SyncSessionStats stats, string status = "completed")
    {
        Execute(
            @"
            UPDATE sync_sessions SET
                completed_at     = @p0,
                status           = @p1,
                sync_type        = @p2,
                groups_created   = @p3,
                groups_updated   = @p4,
                groups_deleted   = @p5,
                groups_unchanged = @p6,
                members_added    = @p7,
                members_removed  = @p8,
                api_calls_made   = @p9,
                errors           = @p10,
                content_total_fetched = @p11,
                content_success  = @p12,
                content_failed   = @p13,
                content_deleted  = @p14,
                content_acl_engine = @p15
            WHERE session_id = @p16
            ",
            NowIso(), status, stats.SyncType,
            stats.GroupsCreated, stats.GroupsUpdated, stats.GroupsDeleted,
            stats.GroupsUnchanged, stats.MembersAdded, stats.MembersRemoved,
            stats.ApiCallsMade, stats.Errors,
            stats.ContentTotalFetched, stats.ContentSuccess,
            stats.ContentFailed, stats.ContentDeleted,
            stats.ContentAclEngine,
            sessionId);
    }

    /// <summary>Return the most recent completed sync session, or null.
    ///
    /// Parameters
    /// ----------
    /// crawlType : Filter by crawl type (e.g. "identity", "content").
    ///             If null, returns the latest session of any type.
    /// </summary>
    public Dictionary<string, object?>? GetLastSession(string? crawlType = null)
    {
        const string Select = @"
            SELECT session_id, crawl_type, sync_type, started_at, completed_at, status,
                   groups_created, groups_updated, groups_deleted, groups_unchanged,
                   members_added, members_removed, api_calls_made, errors,
                   content_total_fetched, content_success, content_failed,
                   content_deleted, content_acl_engine
            FROM sync_sessions
        ";
        using var cmd = _conn.CreateCommand();
        if (!string.IsNullOrEmpty(crawlType))
        {
            cmd.CommandText = Select + " WHERE connection_id = @p0 AND status = 'completed' AND crawl_type = @p1"
                + " ORDER BY completed_at DESC LIMIT 1";
            cmd.Parameters.AddWithValue("@p0", _connectionId);
            cmd.Parameters.AddWithValue("@p1", crawlType);
        }
        else
        {
            cmd.CommandText = Select + " WHERE connection_id = @p0 AND status = 'completed'"
                + " ORDER BY completed_at DESC LIMIT 1";
            cmd.Parameters.AddWithValue("@p0", _connectionId);
        }
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return new Dictionary<string, object?>
        {
            ["session_id"] = reader.GetString(0),
            ["crawl_type"] = reader.GetString(1),
            ["sync_type"] = reader.GetString(2),
            ["started_at"] = reader.GetString(3),
            ["completed_at"] = reader.IsDBNull(4) ? null : reader.GetString(4),
            ["status"] = reader.GetString(5),
            ["groups_created"] = reader.GetInt32(6),
            ["groups_updated"] = reader.GetInt32(7),
            ["groups_deleted"] = reader.GetInt32(8),
            ["groups_unchanged"] = reader.GetInt32(9),
            ["members_added"] = reader.GetInt32(10),
            ["members_removed"] = reader.GetInt32(11),
            ["api_calls_made"] = reader.GetInt32(12),
            ["errors"] = reader.GetInt32(13),
            ["content_total_fetched"] = reader.GetInt32(14),
            ["content_success"] = reader.GetInt32(15),
            ["content_failed"] = reader.GetInt32(16),
            ["content_deleted"] = reader.GetInt32(17),
            ["content_acl_engine"] = reader.GetString(18),
        };
    }

    /// <summary>
    /// Return the ``started_at`` timestamp of the last successful content
    /// crawl, or null if no content crawl has completed.
    ///
    /// Used by the incremental content crawl to determine the ``since``
    /// parameter — only Salesforce records modified after this timestamp
    /// are fetched.
    ///
    /// Returns
    /// -------
    /// DateTime (UTC) or null.
    /// </summary>
    public DateTime? GetLastSuccessfulContentCrawlTime()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT started_at FROM sync_sessions
            WHERE connection_id = @p0 AND crawl_type = 'content'
                  AND status = 'completed'
            ORDER BY completed_at DESC LIMIT 1
            ";
        cmd.Parameters.AddWithValue("@p0", _connectionId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        if (reader.IsDBNull(0))
            return null;
        if (DateTimeOffset.TryParse(
                reader.GetString(0), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed))
            return parsed.UtcDateTime;
        return null;
    }

    // ── Field cache ─────────────────────────────────────────────────────────

    /// <summary>
    /// Return the cached field list for <paramref name="objectType"/> at
    /// <paramref name="instanceUrl"/>, or null if no cache entry exists.
    ///
    /// Used by <c>Salesforce/ApiClient.cs</c> to skip the INVALID_FIELD retry
    /// loop on subsequent runs.
    /// </summary>
    public string[]? GetCachedFields(string instanceUrl, string objectType)
    {
        var instHash = InstanceHash(instanceUrl);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT fields FROM field_cache WHERE object_type = @p0 AND instance_hash = @p1";
        cmd.Parameters.AddWithValue("@p0", objectType);
        cmd.Parameters.AddWithValue("@p1", instHash);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            try
            {
                var data = JsonNode.Parse(reader.GetString(0));
                if (data is JsonArray array && array.Count > 0)
                    return array.Select(n => n!.GetValue<string>()).ToArray();
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentNullException)
            {
                // pass (Python parity): a corrupt/stale field-cache row is treated
                // as absent — the caller re-runs the field discovery loop and
                // overwrites the row, so nothing is lost.
            }
        }
        return null;
    }

    /// <summary>
    /// Persist the working field list for <paramref name="objectType"/> so the
    /// next run skips the INVALID_FIELD retry loop.
    /// </summary>
    public void SaveCachedFields(string instanceUrl, string objectType, string[] fields)
    {
        var instHash = InstanceHash(instanceUrl);
        Execute(
            @"
            INSERT INTO field_cache (object_type, instance_hash, fields, cached_at)
            VALUES (@p0, @p1, @p2, @p3)
            ON CONFLICT(object_type, instance_hash) DO UPDATE SET
                fields    = excluded.fields,
                cached_at = excluded.cached_at
            ",
            objectType, instHash, DumpsJsonList(fields), NowIso());
    }

    /// <summary>
    /// Clear cached field entries.
    ///
    /// * No args → clear all entries.
    /// * <paramref name="instanceUrl"/> only → clear all objects for that org.
    /// * Both → clear a specific object.
    ///
    /// Returns the number of entries deleted.
    /// </summary>
    public int ClearFieldCache(string? instanceUrl = null, string? objectType = null)
    {
        if (!string.IsNullOrEmpty(instanceUrl) && !string.IsNullOrEmpty(objectType))
        {
            return Execute(
                "DELETE FROM field_cache WHERE object_type = @p0 AND instance_hash = @p1",
                objectType, InstanceHash(instanceUrl));
        }
        if (!string.IsNullOrEmpty(instanceUrl))
        {
            return Execute(
                "DELETE FROM field_cache WHERE instance_hash = @p0",
                InstanceHash(instanceUrl));
        }
        return Execute("DELETE FROM field_cache");
    }

    // ── FLS cache (WP-SF-2) ───────────────────────────────────────────────────

    /// <summary>
    /// Return the cached field-level-security payload for <paramref name="objectType"/>
    /// at <paramref name="instanceUrl"/>, or null if no entry exists.
    /// </summary>
    public string? GetCachedFls(string instanceUrl, string objectType)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT permissions FROM fls_cache WHERE object_type = @p0 AND instance_hash = @p1";
        cmd.Parameters.AddWithValue("@p0", objectType);
        cmd.Parameters.AddWithValue("@p1", InstanceHash(instanceUrl));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? reader.GetString(0) : null;
    }

    /// <summary>Persist the field-level-security payload for an org/object.</summary>
    public void SaveCachedFls(string instanceUrl, string objectType, string permissionsJson)
    {
        Execute(
            @"
            INSERT INTO fls_cache (object_type, instance_hash, permissions, cached_at)
            VALUES (@p0, @p1, @p2, @p3)
            ON CONFLICT(object_type, instance_hash) DO UPDATE SET
                permissions = excluded.permissions,
                cached_at   = excluded.cached_at
            ",
            objectType, InstanceHash(instanceUrl), permissionsJson, NowIso());
    }

    /// <summary>
    /// Clear cached FLS entries (all / per-org / per-org+object), returning the
    /// number deleted. Same argument semantics as <see cref="ClearFieldCache"/>.
    /// </summary>
    public int ClearFlsCache(string? instanceUrl = null, string? objectType = null)
    {
        if (!string.IsNullOrEmpty(instanceUrl) && !string.IsNullOrEmpty(objectType))
        {
            return Execute(
                "DELETE FROM fls_cache WHERE object_type = @p0 AND instance_hash = @p1",
                objectType, InstanceHash(instanceUrl));
        }
        if (!string.IsNullOrEmpty(instanceUrl))
        {
            return Execute(
                "DELETE FROM fls_cache WHERE instance_hash = @p0",
                InstanceHash(instanceUrl));
        }
        return Execute("DELETE FROM fls_cache");
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    public string ConnectionId => _connectionId;

    public string DbPath => _dbPath;

    /// <summary>Return current counts of groups and members.</summary>
    public Dictionary<string, int> GetStats()
    {
        int groupCount;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM groups WHERE connection_id = @p0";
            cmd.Parameters.AddWithValue("@p0", _connectionId);
            groupCount = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        int memberCount;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = @"
            SELECT COUNT(*) FROM group_members gm
            JOIN groups g ON gm.group_id = g.group_id
            WHERE g.connection_id = @p0
            ";
            cmd.Parameters.AddWithValue("@p0", _connectionId);
            memberCount = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        return new Dictionary<string, int> { ["groups"] = groupCount, ["members"] = memberCount };
    }

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Create an <see cref="IIdentityStore"/> for the given connection.
    ///
    /// When env ``USE_SQL_SERVER=true`` returns a <see cref="SqlServerIdentityStore"/>
    /// backed by ``SQL_CONNECTION_STRING`` (see docs/SQL_CONTRACT.md); otherwise
    /// the SQLite-backed <see cref="IdentityStore"/>.
    ///
    /// Parameters
    /// ----------
    /// connectionId : The Graph external connection ID.
    /// dataDir      : Directory to store the SQLite DB file.  Defaults to
    ///                <c>{repo_root}/data/</c>.  Ignored in SQL Server mode.
    /// </summary>
    public static IIdentityStore CreateStore(string connectionId, string? dataDir = null)
    {
        var useSqlServer = (Environment.GetEnvironmentVariable("USE_SQL_SERVER") ?? "false").ToLowerInvariant();
        if (useSqlServer is "true" or "1" or "yes")
        {
            var connectionString = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
            if (string.IsNullOrEmpty(connectionString))
            {
                throw new ArgumentException(
                    "USE_SQL_SERVER=true requires SQL_CONNECTION_STRING to be set "
                    + "(point it at the Always On Availability Group listener for HA).");
            }
            return new SqlServerIdentityStore(connectionString: connectionString, connectionId: connectionId);
        }
        dataDir ??= Path.Combine(Settings.RepoRoot, "data");
        var dbPath = Path.Combine(dataDir, $"{connectionId}_identity.db");
        return new IdentityStore(dbPath: dbPath, connectionId: connectionId);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Execute a parameterised statement, returning the affected row count.</summary>
    private int Execute(string sql, params object[] args)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++)
            cmd.Parameters.AddWithValue($"@p{i}", args[i]);
        return cmd.ExecuteNonQuery();
    }

    private static string NowIso()
    {
        // Matches Python datetime.now(timezone.utc).isoformat():
        // microsecond precision with a "+00:00" offset; the fractional part is
        // omitted entirely when the microsecond component is exactly zero.
        var now = DateTime.UtcNow;
        var format = now.Ticks % TimeSpan.TicksPerSecond == 0
            ? "yyyy-MM-dd'T'HH:mm:ss"
            : "yyyy-MM-dd'T'HH:mm:ss.ffffff";
        return now.ToString(format, CultureInfo.InvariantCulture) + "+00:00";
    }

    /// <summary>
    /// Return a short hash of the Salesforce instance URL for cache keying.
    ///
    /// SHA-256 (WP-SF-5) — was MD5 through 1.0.0, replaced for FIPS 140-3: no
    /// broken primitive is called even on a FIPS-enforced host. The output shape
    /// is deliberately unchanged (first 8 chars of lowercase hex), so the
    /// <c>field_cache</c> primary key and the SQL <c>nvarchar(16)</c> column need
    /// no DDL change.
    ///
    /// Must stay byte-identical to <see cref="SqlServerIdentityStore.InstanceHash"/>.
    ///
    /// On upgrade, rows written under the old MD5 key become unreachable: that is
    /// a benign cache miss (the caller re-runs the INVALID_FIELD discovery loop
    /// and writes a fresh row). The orphans are left in place on purpose — see
    /// docs/THREAT_MODEL.md, "FIPS posture".
    /// </summary>
    internal static string InstanceHash(string instanceUrl)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(instanceUrl));
        return Convert.ToHexString(digest).ToLowerInvariant()[..8];
    }

    /// <summary>Serialise a field list exactly like Python <c>json.dumps(list(fields))</c> (", " separators).</summary>
    private static string DumpsJsonList(IEnumerable<string> fields)
    {
        return "[" + string.Join(", ", fields.Select(f => JsonSerializer.Serialize(f))) + "]";
    }
}
