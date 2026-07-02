// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Graph/SqlServerIdentityStore.cs
// --------------------------------
// SQL Server-backed implementation of IIdentityStore (docs/SQL_CONTRACT.md).
//
// Selected with USE_SQL_SERVER=true + SQL_CONNECTION_STRING (point at the
// Always On Availability Group listener for HA).  All access goes through the
// contract's stored procedures via Microsoft.Data.SqlClient; the only direct
// SQL is a SELECT against the dbo.vGroupMemberCounts view (GetStats).
//
// Semantics are identical to the SQLite IdentityStore:
//
//   * Same diff behavior (ComputeDiff reads fresh state per call — another
//     HA node may write groups concurrently, so nothing is cached).
//   * Same Python-isoformat timestamp strings in returned session data.
//   * Same field-cache JSON encoding (Python ``json.dumps`` separators).
//
// Connections are opened per operation; SqlClient pooling handles reuse.

using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using SalesforceCopilotConnector.Config;

namespace SalesforceCopilotConnector.Graph;

/// <summary>
/// SQL Server-backed state store for external group membership.
///
/// Parameters
/// ----------
/// connectionString : SQL Server connection string (AG listener for HA).
/// connectionId     : The Graph external connection ID this store tracks.
/// </summary>
public class SqlServerIdentityStore : IIdentityStore
{
    private readonly string _connectionString;
    private readonly string _connectionId;
    private readonly string _dbPath;

    public SqlServerIdentityStore(string connectionString, string connectionId)
    {
        _connectionString = connectionString;
        _connectionId = connectionId;
        var builder = new SqlConnectionStringBuilder(connectionString);
        var database = string.IsNullOrEmpty(builder.InitialCatalog) ? "SalesforceConnector" : builder.InitialCatalog;
        _dbPath = string.IsNullOrEmpty(builder.DataSource) ? database : $"{builder.DataSource}/{database}";
    }

    // ── Connection helpers ────────────────────────────────────────────────────

    /// <summary>Open a pooled connection for one operation.</summary>
    private SqlConnection OpenConnection()
    {
        var conn = new SqlConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static SqlCommand Proc(SqlConnection conn, string procName)
    {
        return new SqlCommand(procName, conn) { CommandType = CommandType.StoredProcedure };
    }

    // ── Group CRUD ────────────────────────────────────────────────────────────

    /// <summary>Return all group IDs tracked for this connection.</summary>
    public HashSet<string> GetAllGroupIds()
    {
        var result = new HashSet<string>();
        using var conn = OpenConnection();
        using var cmd = Proc(conn, "dbo.usp_GetGroups");
        cmd.Parameters.AddWithValue("@ConnectionId", _connectionId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>Check if a group is already tracked.</summary>
    public bool GroupExists(string groupId)
    {
        // Fresh read (no caching): another HA node may create groups concurrently.
        return GetAllGroupIds().Contains(groupId);
    }

    /// <summary>Insert or update a group record.</summary>
    public void UpsertGroup(string groupId, string displayName = "", string description = "")
    {
        using var conn = OpenConnection();
        using var cmd = Proc(conn, "dbo.usp_UpsertGroup");
        cmd.Parameters.AddWithValue("@ConnectionId", _connectionId);
        cmd.Parameters.AddWithValue("@GroupId", groupId);
        cmd.Parameters.AddWithValue("@DisplayName", displayName);
        cmd.Parameters.AddWithValue("@Description", description);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Delete a group and all its members (CASCADE).</summary>
    public void DeleteGroup(string groupId)
    {
        using var conn = OpenConnection();
        using var cmd = Proc(conn, "dbo.usp_DeleteGroup");
        cmd.Parameters.AddWithValue("@ConnectionId", _connectionId);
        cmd.Parameters.AddWithValue("@GroupId", groupId);
        cmd.ExecuteNonQuery();
    }

    // ── Member CRUD ───────────────────────────────────────────────────────────

    /// <summary>Return all current members of a group.</summary>
    public HashSet<MemberEntry> GetMembers(string groupId)
    {
        var result = new HashSet<MemberEntry>();
        using var conn = OpenConnection();
        using var cmd = Proc(conn, "dbo.usp_GetMembers");
        cmd.Parameters.AddWithValue("@ConnectionId", _connectionId);
        cmd.Parameters.AddWithValue("@GroupId", groupId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(new MemberEntry(memberId: reader.GetString(0), memberType: reader.GetString(1), identitySource: reader.GetString(2)));
        return result;
    }

    /// <summary>Add a single member to a group.</summary>
    public void AddMember(string groupId, MemberEntry member)
    {
        using var conn = OpenConnection();
        using var cmd = Proc(conn, "dbo.usp_AddMember");
        cmd.Parameters.AddWithValue("@ConnectionId", _connectionId);
        cmd.Parameters.AddWithValue("@GroupId", groupId);
        cmd.Parameters.AddWithValue("@MemberId", member.MemberId);
        cmd.Parameters.AddWithValue("@MemberType", member.MemberType);
        cmd.Parameters.AddWithValue("@IdentitySource", member.IdentitySource);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Remove a single member from a group.</summary>
    public void RemoveMember(string groupId, MemberEntry member)
    {
        using var conn = OpenConnection();
        using var cmd = Proc(conn, "dbo.usp_RemoveMember");
        cmd.Parameters.AddWithValue("@ConnectionId", _connectionId);
        cmd.Parameters.AddWithValue("@GroupId", groupId);
        cmd.Parameters.AddWithValue("@MemberId", member.MemberId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Atomically replace all members of a group.
    ///
    /// Used after a successful full-group publish to ensure the store
    /// matches the state that was pushed to Graph.
    /// </summary>
    public void ReplaceMembers(string groupId, HashSet<MemberEntry> members)
    {
        var json = new JsonArray();
        foreach (var m in members)
        {
            json.Add(new JsonObject
            {
                ["memberId"] = m.MemberId,
                ["memberType"] = m.MemberType,
                ["identitySource"] = m.IdentitySource,
            });
        }
        using var conn = OpenConnection();
        using var cmd = Proc(conn, "dbo.usp_ReplaceGroupMembers");
        cmd.Parameters.AddWithValue("@ConnectionId", _connectionId);
        cmd.Parameters.AddWithValue("@GroupId", groupId);
        cmd.Parameters.AddWithValue("@MembersJson", json.ToJsonString());
        cmd.ExecuteNonQuery();
    }

    // ── Diff computation ──────────────────────────────────────────────────────

    /// <summary>
    /// Compare <paramref name="newGroups"/> against the stored state and return a list of diffs.
    ///
    /// Identical change-detection logic to <see cref="IdentityStore.ComputeDiff"/>:
    /// - Group in new but not in store → "create"
    /// - Group in both, members changed → "update"
    /// - Group in both, members identical → "unchanged"
    /// - Group in store but not in new → "delete"
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
                // Existing group — diff members (fresh read per group)
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

    /// <summary>Create a new sync session record. Returns the session ID.</summary>
    public string StartSession(string crawlType = "identity", string syncType = "full")
    {
        var sessionId = Guid.NewGuid();
        using var conn = OpenConnection();
        using var cmd = Proc(conn, "dbo.usp_StartSession");
        cmd.Parameters.Add("@SessionId", SqlDbType.UniqueIdentifier).Value = sessionId;
        cmd.Parameters.AddWithValue("@ConnectionId", _connectionId);
        cmd.Parameters.AddWithValue("@CrawlType", crawlType);
        cmd.Parameters.AddWithValue("@SyncType", syncType);
        cmd.ExecuteNonQuery();
        return sessionId.ToString();
    }

    /// <summary>Finalise a sync session with aggregate stats.</summary>
    public void CompleteSession(string sessionId, SyncSessionStats stats, string status = "completed")
    {
        using var conn = OpenConnection();
        using var cmd = Proc(conn, "dbo.usp_CompleteSession");
        cmd.Parameters.Add("@SessionId", SqlDbType.UniqueIdentifier).Value = Guid.Parse(sessionId);
        cmd.Parameters.AddWithValue("@Status", status);
        cmd.Parameters.AddWithValue("@StatsJson", SerializeStats(stats));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Return the most recent completed sync session, or null.</summary>
    public Dictionary<string, object?>? GetLastSession(string? crawlType = null)
    {
        using var conn = OpenConnection();
        using var cmd = Proc(conn, "dbo.usp_GetLastSession");
        cmd.Parameters.AddWithValue("@ConnectionId", _connectionId);
        cmd.Parameters.AddWithValue("@CrawlType", string.IsNullOrEmpty(crawlType) ? DBNull.Value : crawlType);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        // Columns: SessionId, CrawlType, SyncType, StartedUtc, CompletedUtc, Status, StatsJson
        var statsJson = reader.IsDBNull(6) ? null : reader.GetString(6);
        JsonObject? stats = null;
        if (!string.IsNullOrEmpty(statsJson))
        {
            try
            {
                stats = JsonNode.Parse(statsJson) as JsonObject;
            }
            catch (JsonException)
            {
                // pass — treat unparsable stats as absent
            }
        }

        return new Dictionary<string, object?>
        {
            ["session_id"] = reader.GetGuid(0).ToString(),
            ["crawl_type"] = reader.GetString(1),
            ["sync_type"] = reader.GetString(2),
            ["started_at"] = PyIso(reader.GetDateTime(3)),
            ["completed_at"] = reader.IsDBNull(4) ? null : PyIso(reader.GetDateTime(4)),
            ["status"] = reader.GetString(5),
            ["groups_created"] = StatInt(stats, "groups_created"),
            ["groups_updated"] = StatInt(stats, "groups_updated"),
            ["groups_deleted"] = StatInt(stats, "groups_deleted"),
            ["groups_unchanged"] = StatInt(stats, "groups_unchanged"),
            ["members_added"] = StatInt(stats, "members_added"),
            ["members_removed"] = StatInt(stats, "members_removed"),
            ["api_calls_made"] = StatInt(stats, "api_calls_made"),
            ["errors"] = StatInt(stats, "errors"),
            ["content_total_fetched"] = StatInt(stats, "content_total_fetched"),
            ["content_success"] = StatInt(stats, "content_success"),
            ["content_failed"] = StatInt(stats, "content_failed"),
            ["content_deleted"] = StatInt(stats, "content_deleted"),
            ["content_acl_engine"] = StatString(stats, "content_acl_engine"),
        };
    }

    /// <summary>
    /// Return the start timestamp of the last successful content crawl, or
    /// null if no content crawl has completed.  Mirrors the SQLite store:
    /// StartedUtc of the row with the newest CompletedUtc.
    /// </summary>
    public DateTime? GetLastSuccessfulContentCrawlTime()
    {
        using var conn = OpenConnection();
        using var cmd = Proc(conn, "dbo.usp_GetLastSuccessfulContentCrawl");
        cmd.Parameters.AddWithValue("@ConnectionId", _connectionId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        if (reader.IsDBNull(0))
            return null;
        return DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc);
    }

    // ── Field cache ─────────────────────────────────────────────────────────

    /// <summary>
    /// Return the cached field list for <paramref name="objectType"/> at
    /// <paramref name="instanceUrl"/>, or null if no cache entry exists.
    /// </summary>
    public string[]? GetCachedFields(string instanceUrl, string objectType)
    {
        var instHash = InstanceHash(instanceUrl);
        using var conn = OpenConnection();
        using var cmd = Proc(conn, "dbo.usp_GetCachedFields");
        cmd.Parameters.AddWithValue("@InstanceHash", instHash);
        cmd.Parameters.AddWithValue("@ObjectType", objectType);
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
                // pass
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
        using var conn = OpenConnection();
        using var cmd = Proc(conn, "dbo.usp_SaveCachedFields");
        cmd.Parameters.AddWithValue("@InstanceHash", InstanceHash(instanceUrl));
        cmd.Parameters.AddWithValue("@ObjectType", objectType);
        cmd.Parameters.AddWithValue("@FieldsJson", DumpsJsonList(fields));
        cmd.ExecuteNonQuery();
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
        using var conn = OpenConnection();
        using var cmd = Proc(conn, "dbo.usp_ClearFieldCache");
        cmd.Parameters.AddWithValue("@InstanceHash",
            string.IsNullOrEmpty(instanceUrl) ? DBNull.Value : InstanceHash(instanceUrl));
        cmd.Parameters.AddWithValue("@ObjectType",
            string.IsNullOrEmpty(instanceUrl) || string.IsNullOrEmpty(objectType) ? DBNull.Value : objectType);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    public string ConnectionId => _connectionId;

    /// <summary>Backing-store location: "server/database" (interface parity with the SQLite file path).</summary>
    public string DbPath => _dbPath;

    /// <summary>Return current counts of groups and members.</summary>
    public Dictionary<string, int> GetStats()
    {
        using var conn = OpenConnection();
        // The one non-proc query: an aggregate over the contract's
        // vGroupMemberCounts view (the app login has SELECT on views).
        using var cmd = new SqlCommand(
            "SELECT COUNT(*), ISNULL(SUM(MemberCount), 0) FROM dbo.vGroupMemberCounts WHERE ConnectionId = @ConnectionId",
            conn);
        cmd.Parameters.AddWithValue("@ConnectionId", _connectionId);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new Dictionary<string, int>
        {
            ["groups"] = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
            ["members"] = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Close the store. Connections are per-operation, so this is a no-op.</summary>
    public void Close()
    {
        // pass — SqlClient pooling owns the physical connections.
    }

    public void Dispose() => Close();

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Serialize <see cref="SyncSessionStats"/> as PyJson with snake_case keys.</summary>
    private static string SerializeStats(SyncSessionStats stats)
    {
        var obj = new JsonObject
        {
            ["session_id"] = stats.SessionId,
            ["sync_type"] = stats.SyncType,
            ["groups_created"] = stats.GroupsCreated,
            ["groups_updated"] = stats.GroupsUpdated,
            ["groups_deleted"] = stats.GroupsDeleted,
            ["groups_unchanged"] = stats.GroupsUnchanged,
            ["members_added"] = stats.MembersAdded,
            ["members_removed"] = stats.MembersRemoved,
            ["api_calls_made"] = stats.ApiCallsMade,
            ["errors"] = stats.Errors,
            ["content_total_fetched"] = stats.ContentTotalFetched,
            ["content_success"] = stats.ContentSuccess,
            ["content_failed"] = stats.ContentFailed,
            ["content_deleted"] = stats.ContentDeleted,
            ["content_acl_engine"] = stats.ContentAclEngine,
        };
        return PyJson.Dumps(obj);
    }

    private static int StatInt(JsonObject? stats, string key)
    {
        if (stats?[key] is JsonValue value && value.TryGetValue(out int parsed))
            return parsed;
        return 0;
    }

    private static string StatString(JsonObject? stats, string key)
    {
        if (stats?[key] is JsonValue value && value.TryGetValue(out string? parsed))
            return parsed ?? "";
        return "";
    }

    /// <summary>Format a UTC datetime2 value like Python <c>datetime.isoformat()</c>.</summary>
    private static string PyIso(DateTime value)
    {
        var utc = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        var format = utc.Ticks % TimeSpan.TicksPerSecond == 0
            ? "yyyy-MM-dd'T'HH:mm:ss"
            : "yyyy-MM-dd'T'HH:mm:ss.ffffff";
        return utc.ToString(format, CultureInfo.InvariantCulture) + "+00:00";
    }

    /// <summary>Return a short hash of the Salesforce instance URL for cache keying.</summary>
    private static string InstanceHash(string instanceUrl)
    {
        var digest = MD5.HashData(Encoding.UTF8.GetBytes(instanceUrl));
        return Convert.ToHexString(digest).ToLowerInvariant()[..8];
    }

    /// <summary>Serialise a field list exactly like Python <c>json.dumps(list(fields))</c> (", " separators).</summary>
    private static string DumpsJsonList(IEnumerable<string> fields)
    {
        return "[" + string.Join(", ", fields.Select(f => JsonSerializer.Serialize(f))) + "]";
    }
}
