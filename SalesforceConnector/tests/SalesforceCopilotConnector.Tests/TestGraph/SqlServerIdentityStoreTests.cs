// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Integration tests for graph.SqlServerIdentityStore — the SQL Server state
// store (docs/SQL_CONTRACT.md).  Mirrors the core IdentityStoreTests coverage
// (upsert / replace / diff / sessions / field cache).
//
// These tests require a provisioned database (scripts/sql/create-database.sql)
// and SKIP cleanly (early return) unless the environment variable
// SQLSERVER_TEST_CONNECTION_STRING is set, e.g.:
//
//     SQLSERVER_TEST_CONNECTION_STRING="Server=localhost;Database=SalesforceConnector;User Id=sa;Password=...;TrustServerCertificate=true" dotnet test

using System.Data;
using Microsoft.Data.SqlClient;
using SalesforceCopilotConnector.Graph;
using Xunit.Abstractions;

namespace SalesforceCopilotConnector.Tests.TestGraph;

/// <summary>Shared gate for the SQL Server integration tests.</summary>
public static class SqlServerTestEnv
{
    public const string EnvVar = "SQLSERVER_TEST_CONNECTION_STRING";

    public static string? ConnectionString => Environment.GetEnvironmentVariable(EnvVar);

    public static bool Available => !string.IsNullOrEmpty(ConnectionString);
}

/// <summary>
/// Reports (via test output) whether the SQL Server integration tests ran or
/// were skipped, so a green run without a server is not mistaken for coverage.
/// </summary>
public class SqlServerIdentityStoreSkipReport
{
    private readonly ITestOutputHelper _output;

    public SqlServerIdentityStoreSkipReport(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ReportSqlServerTestAvailability()
    {
        if (SqlServerTestEnv.Available)
        {
            _output.WriteLine("SQL Server integration tests ENABLED (SQLSERVER_TEST_CONNECTION_STRING is set).");
        }
        else
        {
            _output.WriteLine(
                "SQL Server integration tests SKIPPED: set SQLSERVER_TEST_CONNECTION_STRING "
                + "to a database provisioned with scripts/sql/create-database.sql to run them.");
        }
    }
}

/// <summary>
/// Fixture: a fresh SqlServerIdentityStore with a unique connection id per
/// test, cleaned up afterwards.  When the env var is unset every fact body
/// early-returns via <see cref="Skip"/>.
/// </summary>
public abstract class SqlServerIdentityStoreTestBase : IDisposable
{
    protected readonly string TestConnectionId = "sqltest-" + Guid.NewGuid().ToString("N")[..12];
    private readonly SqlServerIdentityStore? _store;
    // Unique per fixture so FieldCache rows never collide across tests/runs.
    protected readonly string OrgUrl;

    protected SqlServerIdentityStoreTestBase()
    {
        OrgUrl = $"https://{Guid.NewGuid():N}.my.salesforce.com";
        if (SqlServerTestEnv.Available)
            _store = new SqlServerIdentityStore(SqlServerTestEnv.ConnectionString!, TestConnectionId);
    }

    /// <summary>True → the test must return without asserting (no server configured).</summary>
    protected bool Skip => _store is null;

    protected SqlServerIdentityStore Store => _store!;

    protected SqlServerIdentityStore OpenStore(string connectionId)
        => new(SqlServerTestEnv.ConnectionString!, connectionId);

    public virtual void Dispose()
    {
        if (_store is not null)
        {
            // Best-effort cleanup of everything this fixture created.
            try
            {
                _store.ClearFieldCache(OrgUrl);
                using var conn = new SqlConnection(SqlServerTestEnv.ConnectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "DELETE FROM dbo.Groups WHERE ConnectionId = @c;"      // members cascade
                    + "DELETE FROM dbo.SyncSessions WHERE ConnectionId = @c;";
                cmd.Parameters.AddWithValue("@c", TestConnectionId);
                cmd.ExecuteNonQuery();
            }
            catch (SqlException)
            {
                // best-effort cleanup
            }
            _store.Close();
        }
        GC.SuppressFinalize(this);
    }
}

// ── Group CRUD ────────────────────────────────────────────────────────────────

public class SqlServerGroupCrudTests : SqlServerIdentityStoreTestBase
{
    [Fact]
    public void GroupExistsFalseInitially()
    {
        if (Skip) return;
        Assert.False(Store.GroupExists("G1"));
    }

    [Fact]
    public void UpsertAndExists()
    {
        if (Skip) return;
        Store.UpsertGroup("G1", "Group One");
        Assert.True(Store.GroupExists("G1"));
    }

    [Fact]
    public void UpsertUpdatesDisplayName()
    {
        if (Skip) return;
        Store.UpsertGroup("G1", "Old Name");
        Store.UpsertGroup("G1", "New Name");
        // Still only one group
        Assert.Equal(new HashSet<string> { "G1" }, Store.GetAllGroupIds());
    }

    [Fact]
    public void GetAllGroupIds()
    {
        if (Skip) return;
        Store.UpsertGroup("G1");
        Store.UpsertGroup("G2");
        Store.UpsertGroup("G3");
        Assert.Equal(new HashSet<string> { "G1", "G2", "G3" }, Store.GetAllGroupIds());
    }

    [Fact]
    public void DeleteGroup()
    {
        if (Skip) return;
        Store.UpsertGroup("G1");
        Store.DeleteGroup("G1");
        Assert.False(Store.GroupExists("G1"));
    }

    [Fact]
    public void DeleteGroupCascadesMembers()
    {
        if (Skip) return;
        Store.UpsertGroup("G1");
        Store.AddMember("G1", new MemberEntry("005A", "user"));
        Store.AddMember("G1", new MemberEntry("005B", "user"));
        Store.DeleteGroup("G1");
        Assert.Equal(new HashSet<MemberEntry>(), Store.GetMembers("G1"));
    }

    [Fact]
    public void IsolationByConnectionId()
    {
        if (Skip) return;
        var otherId = TestConnectionId + "-b";
        var storeB = OpenStore(otherId);
        try
        {
            Store.UpsertGroup("G1", "A's group");
            storeB.UpsertGroup("G2", "B's group");
            Assert.Equal(new HashSet<string> { "G1" }, Store.GetAllGroupIds());
            Assert.Equal(new HashSet<string> { "G2" }, storeB.GetAllGroupIds());
        }
        finally
        {
            storeB.DeleteGroup("G2");
            storeB.Close();
        }
    }
}

// ── Member CRUD ───────────────────────────────────────────────────────────────

public class SqlServerMemberCrudTests : SqlServerIdentityStoreTestBase
{
    [Fact]
    public void AddAndGetMembers()
    {
        if (Skip) return;
        Store.UpsertGroup("G1");
        var m1 = new MemberEntry("005A", "user");
        var m2 = new MemberEntry("GRP-Child", "externalGroup");
        Store.AddMember("G1", m1);
        Store.AddMember("G1", m2);
        var members = Store.GetMembers("G1");
        Assert.Equal(2, members.Count);
        Assert.Contains(m1, members);
        Assert.Contains(m2, members);
    }

    [Fact]
    public void AddDuplicateMemberIsIgnored()
    {
        if (Skip) return;
        Store.UpsertGroup("G1");
        var m = new MemberEntry("005A", "user");
        Store.AddMember("G1", m);
        Store.AddMember("G1", m);  // Should not raise
        Assert.Single(Store.GetMembers("G1"));
    }

    [Fact]
    public void RemoveMember()
    {
        if (Skip) return;
        Store.UpsertGroup("G1");
        var m = new MemberEntry("005A", "user");
        Store.AddMember("G1", m);
        Store.RemoveMember("G1", m);
        Assert.Empty(Store.GetMembers("G1"));
    }

    [Fact]
    public void RemoveNonexistentMemberIsSafe()
    {
        if (Skip) return;
        Store.UpsertGroup("G1");
        Store.RemoveMember("G1", new MemberEntry("005X", "user"));  // No error
    }

    [Fact]
    public void ReplaceMembers()
    {
        if (Skip) return;
        Store.UpsertGroup("G1");
        Store.AddMember("G1", new MemberEntry("005OLD", "user"));
        Store.AddMember("G1", new MemberEntry("005KEEP", "user"));

        var newMembers = new HashSet<MemberEntry>
        {
            new("005KEEP", "user"),
            new("005NEW", "user"),
        };
        Store.ReplaceMembers("G1", newMembers);

        var members = Store.GetMembers("G1");
        Assert.Equal(2, members.Count);
        var memberIds = members.Select(m => m.MemberId).ToHashSet();
        Assert.Contains("005KEEP", memberIds);
        Assert.Contains("005NEW", memberIds);
        Assert.DoesNotContain("005OLD", memberIds);
    }

    [Fact]
    public void ReplaceMembersPreservesIdentitySource()
    {
        if (Skip) return;
        Store.UpsertGroup("G1");
        Store.ReplaceMembers("G1", new HashSet<MemberEntry>
        {
            new("11111111-2222-3333-4444-555555555555", "user", "azureActiveDirectory"),
            new("GRP-Child", "externalGroup", "external"),
        });
        var bySource = Store.GetMembers("G1").ToDictionary(m => m.MemberId, m => m.IdentitySource);
        Assert.Equal("azureActiveDirectory", bySource["11111111-2222-3333-4444-555555555555"]);
        Assert.Equal("external", bySource["GRP-Child"]);
    }

    [Fact]
    public void EmptyGroupHasNoMembers()
    {
        if (Skip) return;
        Store.UpsertGroup("G1");
        Assert.Equal(new HashSet<MemberEntry>(), Store.GetMembers("G1"));
    }
}

// ── Diff computation ──────────────────────────────────────────────────────────

public class SqlServerComputeDiffTests : SqlServerIdentityStoreTestBase
{
    [Fact]
    public void AllNewGroups()
    {
        if (Skip) return;
        var newGroups = new Dictionary<string, (string DisplayName, HashSet<MemberEntry> Members)>
        {
            ["G1"] = ("Group 1", new HashSet<MemberEntry> { new("005A", "user") }),
            ["G2"] = ("Group 2", new HashSet<MemberEntry> { new("005B", "user") }),
        };
        var diffs = Store.ComputeDiff(newGroups);
        var actions = diffs.ToDictionary(d => d.GroupId, d => d.Action);
        Assert.Equal(
            new Dictionary<string, string> { ["G1"] = "create", ["G2"] = "create" },
            actions);
    }

    [Fact]
    public void AllStaleGroups()
    {
        if (Skip) return;
        Store.UpsertGroup("STALE1");
        Store.UpsertGroup("STALE2");
        var diffs = Store.ComputeDiff(new Dictionary<string, (string, HashSet<MemberEntry>)>());
        var actions = diffs.ToDictionary(d => d.GroupId, d => d.Action);
        Assert.Equal(
            new Dictionary<string, string> { ["STALE1"] = "delete", ["STALE2"] = "delete" },
            actions);
    }

    [Fact]
    public void UnchangedGroup()
    {
        if (Skip) return;
        Store.UpsertGroup("G1");
        Store.AddMember("G1", new MemberEntry("005A", "user"));

        var newGroups = new Dictionary<string, (string, HashSet<MemberEntry>)>
        {
            ["G1"] = ("G1", new HashSet<MemberEntry> { new("005A", "user") }),
        };
        var diffs = Store.ComputeDiff(newGroups);
        Assert.Single(diffs);
        Assert.Equal("unchanged", diffs[0].Action);
        Assert.False(diffs[0].HasChanges);
    }

    [Fact]
    public void MembersAdded()
    {
        if (Skip) return;
        Store.UpsertGroup("G1");
        Store.AddMember("G1", new MemberEntry("005A", "user"));

        var newGroups = new Dictionary<string, (string, HashSet<MemberEntry>)>
        {
            ["G1"] = ("G1", new HashSet<MemberEntry>
            {
                new("005A", "user"),
                new("005B", "user"),
            }),
        };
        var diffs = Store.ComputeDiff(newGroups);
        Assert.Single(diffs);
        Assert.Equal("update", diffs[0].Action);
        Assert.Single(diffs[0].MembersToAdd);
        Assert.Equal("005B", diffs[0].MembersToAdd[0].MemberId);
        Assert.Empty(diffs[0].MembersToRemove);
    }

    [Fact]
    public void MembersRemoved()
    {
        if (Skip) return;
        Store.UpsertGroup("G1");
        Store.AddMember("G1", new MemberEntry("005A", "user"));
        Store.AddMember("G1", new MemberEntry("005B", "user"));

        var newGroups = new Dictionary<string, (string, HashSet<MemberEntry>)>
        {
            ["G1"] = ("G1", new HashSet<MemberEntry> { new("005A", "user") }),
        };
        var diffs = Store.ComputeDiff(newGroups);
        Assert.Single(diffs);
        Assert.Equal("update", diffs[0].Action);
        Assert.Single(diffs[0].MembersToRemove);
        Assert.Equal("005B", diffs[0].MembersToRemove[0].MemberId);
    }

    [Fact]
    public void MixedCreateUpdateDeleteUnchanged()
    {
        if (Skip) return;
        Store.UpsertGroup("KEEP_SAME");
        Store.AddMember("KEEP_SAME", new MemberEntry("005A", "user"));

        Store.UpsertGroup("WILL_UPDATE");
        Store.AddMember("WILL_UPDATE", new MemberEntry("005X", "user"));

        Store.UpsertGroup("WILL_DELETE");

        var newGroups = new Dictionary<string, (string, HashSet<MemberEntry>)>
        {
            ["KEEP_SAME"] = ("Same", new HashSet<MemberEntry> { new("005A", "user") }),
            ["WILL_UPDATE"] = ("Updated", new HashSet<MemberEntry> { new("005Y", "user") }),
            ["BRAND_NEW"] = ("New", new HashSet<MemberEntry> { new("005Z", "user") }),
        };
        var diffs = Store.ComputeDiff(newGroups);
        var actions = diffs.ToDictionary(d => d.GroupId, d => d.Action);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["KEEP_SAME"] = "unchanged",
                ["WILL_UPDATE"] = "update",
                ["BRAND_NEW"] = "create",
                ["WILL_DELETE"] = "delete",
            },
            actions);
    }
}

// ── Sync sessions ─────────────────────────────────────────────────────────────

public class SqlServerSyncSessionTests : SqlServerIdentityStoreTestBase
{
    [Fact]
    public void StartAndCompleteSession()
    {
        if (Skip) return;
        var sid = Store.StartSession();
        Assert.False(string.IsNullOrEmpty(sid));
        var stats = new SyncSessionStats
        {
            GroupsCreated = 2,
            GroupsUpdated = 1,
            GroupsDeleted = 0,
            GroupsUnchanged = 5,
            MembersAdded = 10,
            MembersRemoved = 3,
            ApiCallsMade = 16,
            Errors = 0,
        };
        Store.CompleteSession(sid, stats);

        var last = Store.GetLastSession();
        Assert.NotNull(last);
        Assert.Equal(sid, last!["session_id"]);
        Assert.Equal("completed", last["status"]);
        Assert.Equal("identity", last["crawl_type"]);
        Assert.Equal(2, last["groups_created"]);
        Assert.Equal(10, last["members_added"]);
        Assert.Equal(16, last["api_calls_made"]);
        // Timestamps come back in Python isoformat, like the SQLite store.
        Assert.EndsWith("+00:00", (string)last["started_at"]!);
        Assert.EndsWith("+00:00", (string)last["completed_at"]!);
    }

    [Fact]
    public void NoCompletedSessionReturnsNone()
    {
        if (Skip) return;
        Assert.Null(Store.GetLastSession());
    }

    [Fact]
    public void FailedSessionNotReturnedAsLast()
    {
        if (Skip) return;
        var sid = Store.StartSession();
        Store.CompleteSession(sid, new SyncSessionStats(), status: "failed");
        Assert.Null(Store.GetLastSession());
    }

    [Fact]
    public void FilterByCrawlType()
    {
        if (Skip) return;
        var sid1 = Store.StartSession(crawlType: "identity");
        Store.CompleteSession(sid1, new SyncSessionStats { GroupsCreated = 5 });

        var sid2 = Store.StartSession(crawlType: "content");
        Store.CompleteSession(sid2, new SyncSessionStats { ContentTotalFetched = 100, ContentSuccess = 95 });

        var identityLast = Store.GetLastSession(crawlType: "identity");
        Assert.NotNull(identityLast);
        Assert.Equal("identity", identityLast!["crawl_type"]);
        Assert.Equal(5, identityLast["groups_created"]);

        var contentLast = Store.GetLastSession(crawlType: "content");
        Assert.NotNull(contentLast);
        Assert.Equal("content", contentLast!["crawl_type"]);
        Assert.Equal(100, contentLast["content_total_fetched"]);
        Assert.Equal(95, contentLast["content_success"]);
    }

    [Fact]
    public void ContentCrawlStats()
    {
        if (Skip) return;
        var sid = Store.StartSession(crawlType: "content");
        var stats = new SyncSessionStats
        {
            ContentTotalFetched = 50,
            ContentSuccess = 48,
            ContentFailed = 2,
            ContentDeleted = 3,
            ContentAclEngine = "GROUP",
            Errors = 2,
        };
        Store.CompleteSession(sid, stats);

        var last = Store.GetLastSession(crawlType: "content");
        Assert.Equal(50, last!["content_total_fetched"]);
        Assert.Equal(48, last["content_success"]);
        Assert.Equal(2, last["content_failed"]);
        Assert.Equal(3, last["content_deleted"]);
        Assert.Equal("GROUP", last["content_acl_engine"]);
    }

    [Fact]
    public void SyncTypeStored()
    {
        if (Skip) return;
        var sid = Store.StartSession(crawlType: "content", syncType: "incremental");
        Store.CompleteSession(sid, new SyncSessionStats { SyncType = "incremental", ContentTotalFetched = 10 });
        var last = Store.GetLastSession(crawlType: "content");
        Assert.Equal("incremental", last!["sync_type"]);
        Assert.Equal(10, last["content_total_fetched"]);
    }

    [Fact]
    public void GetLastSuccessfulContentCrawlTime()
    {
        if (Skip) return;
        // No sessions yet
        Assert.Null(Store.GetLastSuccessfulContentCrawlTime());

        // Add a completed content session
        var sid = Store.StartSession(crawlType: "content", syncType: "full");
        Store.CompleteSession(sid, new SyncSessionStats { ContentTotalFetched = 50 });

        var result = Store.GetLastSuccessfulContentCrawlTime();
        Assert.NotNull(result);
        Assert.Equal(DateTimeKind.Utc, result!.Value.Kind);
        // Recent — started within the last few minutes.
        Assert.True((DateTime.UtcNow - result.Value).Duration() < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void GetLastSuccessfulContentCrawlTimeIgnoresIdentity()
    {
        if (Skip) return;
        // Only identity session exists
        var sid = Store.StartSession(crawlType: "identity");
        Store.CompleteSession(sid, new SyncSessionStats { GroupsCreated = 5 });

        Assert.Null(Store.GetLastSuccessfulContentCrawlTime());
    }
}

// ── Sync session idempotency (commit-ack-loss retry) ──────────────────────────

public class SqlServerStartSessionIdempotencyTests : SqlServerIdentityStoreTestBase
{
    [Fact]
    public void StartSessionSameSessionIdTwiceDoesNotThrowOrDuplicate()
    {
        if (Skip) return;
        // SqlExecutor retries the whole unit on a transient fault, and
        // StartSession generates its SessionId OUTSIDE the retry lambda — so a
        // retry after a committed-but-unacked INSERT re-runs usp_StartSession
        // with the SAME @SessionId. That second call must be a no-op (IF NOT
        // EXISTS guard), not a 2627 PK violation that fails the sync run.
        var sessionId = Guid.NewGuid();
        for (var call = 0; call < 2; call++)
        {
            using var conn = new SqlConnection(SqlServerTestEnv.ConnectionString);
            conn.Open();
            using var cmd = new SqlCommand("dbo.usp_StartSession", conn)
            {
                CommandType = CommandType.StoredProcedure,
            };
            cmd.Parameters.AddWithValue("@SessionId", sessionId);
            cmd.Parameters.AddWithValue("@ConnectionId", TestConnectionId);
            cmd.Parameters.AddWithValue("@CrawlType", "content");
            cmd.Parameters.AddWithValue("@SyncType", "full");
            cmd.ExecuteNonQuery();  // the second call must not throw
        }

        using var check = new SqlConnection(SqlServerTestEnv.ConnectionString);
        check.Open();
        using var count = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.SyncSessions WHERE SessionId = @SessionId", check);
        count.Parameters.AddWithValue("@SessionId", sessionId);
        Assert.Equal(1, Convert.ToInt32(count.ExecuteScalar()));
    }
}

// ── Stats ─────────────────────────────────────────────────────────────────────

public class SqlServerStatsTests : SqlServerIdentityStoreTestBase
{
    [Fact]
    public void EmptyStoreStats()
    {
        if (Skip) return;
        var stats = Store.GetStats();
        Assert.Equal(
            new Dictionary<string, int> { ["groups"] = 0, ["members"] = 0 },
            stats);
    }

    [Fact]
    public void StatsAfterPopulation()
    {
        if (Skip) return;
        Store.UpsertGroup("G1");
        Store.UpsertGroup("G2");
        Store.AddMember("G1", new MemberEntry("005A", "user"));
        Store.AddMember("G1", new MemberEntry("005B", "user"));
        Store.AddMember("G2", new MemberEntry("005C", "user"));

        var stats = Store.GetStats();
        Assert.Equal(
            new Dictionary<string, int> { ["groups"] = 2, ["members"] = 3 },
            stats);
    }
}

// ── Field cache ───────────────────────────────────────────────────────────────

public class SqlServerFieldCacheTests : SqlServerIdentityStoreTestBase
{
    [Fact]
    public void NoCacheReturnsNone()
    {
        if (Skip) return;
        Assert.Null(Store.GetCachedFields(OrgUrl, "Account"));
    }

    [Fact]
    public void SaveAndLoad()
    {
        if (Skip) return;
        Store.SaveCachedFields(OrgUrl, "Account", new[] { "Id", "Name", "Industry" });
        Assert.Equal(new[] { "Id", "Name", "Industry" }, Store.GetCachedFields(OrgUrl, "Account"));
    }

    [Fact]
    public void DifferentObjectsIsolated()
    {
        if (Skip) return;
        Store.SaveCachedFields(OrgUrl, "Account", new[] { "Id", "Name" });
        Store.SaveCachedFields(OrgUrl, "Lead", new[] { "Id", "Email" });
        Assert.Equal(new[] { "Id", "Name" }, Store.GetCachedFields(OrgUrl, "Account"));
        Assert.Equal(new[] { "Id", "Email" }, Store.GetCachedFields(OrgUrl, "Lead"));
    }

    [Fact]
    public void DifferentOrgsIsolated()
    {
        if (Skip) return;
        var otherUrl = OrgUrl + ".other";
        try
        {
            Store.SaveCachedFields(OrgUrl, "Account", new[] { "Id", "Name" });
            Store.SaveCachedFields(otherUrl, "Account", new[] { "Id", "Website" });
            Assert.Equal(new[] { "Id", "Name" }, Store.GetCachedFields(OrgUrl, "Account"));
            Assert.Equal(new[] { "Id", "Website" }, Store.GetCachedFields(otherUrl, "Account"));
        }
        finally
        {
            Store.ClearFieldCache(otherUrl);
        }
    }

    [Fact]
    public void UpsertOverwrites()
    {
        if (Skip) return;
        Store.SaveCachedFields(OrgUrl, "Account", new[] { "Id", "Name", "Phone" });
        Store.SaveCachedFields(OrgUrl, "Account", new[] { "Id", "Name" });
        Assert.Equal(new[] { "Id", "Name" }, Store.GetCachedFields(OrgUrl, "Account"));
    }

    [Fact]
    public void ClearSpecificObject()
    {
        if (Skip) return;
        Store.SaveCachedFields(OrgUrl, "Account", new[] { "Id" });
        Store.SaveCachedFields(OrgUrl, "Lead", new[] { "Id" });
        var deleted = Store.ClearFieldCache(OrgUrl, "Account");
        Assert.Equal(1, deleted);
        Assert.Null(Store.GetCachedFields(OrgUrl, "Account"));
        Assert.Equal(new[] { "Id" }, Store.GetCachedFields(OrgUrl, "Lead"));
    }

    [Fact]
    public void ClearAllForOrg()
    {
        if (Skip) return;
        Store.SaveCachedFields(OrgUrl, "Account", new[] { "Id" });
        Store.SaveCachedFields(OrgUrl, "Lead", new[] { "Id" });
        var deleted = Store.ClearFieldCache(OrgUrl);
        Assert.Equal(2, deleted);
        Assert.Null(Store.GetCachedFields(OrgUrl, "Account"));
        Assert.Null(Store.GetCachedFields(OrgUrl, "Lead"));
    }
}

// ── CreateStore factory (no SQL Server needed) ────────────────────────────────

/// <summary>
/// Unit tests for <see cref="IdentityStore.CreateStore"/> backend selection.
/// Mutates process-global env vars, so it joins the "EnvVars" collection.
/// </summary>
[Collection("EnvVars")]
public class SqlServerCreateStoreTests
{
    /// <summary>Run <paramref name="action"/> with USE_SQL_SERVER / SQL_CONNECTION_STRING set, restoring afterwards.</summary>
    private static void WithEnv(string? useSqlServer, string? connectionString, Action action)
    {
        var savedUse = Environment.GetEnvironmentVariable("USE_SQL_SERVER");
        var savedConn = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
        try
        {
            Environment.SetEnvironmentVariable("USE_SQL_SERVER", useSqlServer);
            Environment.SetEnvironmentVariable("SQL_CONNECTION_STRING", connectionString);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("USE_SQL_SERVER", savedUse);
            Environment.SetEnvironmentVariable("SQL_CONNECTION_STRING", savedConn);
        }
    }

    [Fact]
    public void CreateStoreThrowsWhenSqlServerEnabledWithoutConnectionString()
    {
        WithEnv("true", null, () =>
        {
            var ex = Assert.Throws<ArgumentException>(() => IdentityStore.CreateStore("test-conn"));
            Assert.Contains("SQL_CONNECTION_STRING", ex.Message);
        });
    }

    [Fact]
    public void CreateStoreReturnsSqlServerStoreWhenEnabled()
    {
        // Construction is lazy (connection per operation) — no server needed.
        WithEnv("true", "Server=listener.example;Database=SalesforceConnector;Integrated Security=true", () =>
        {
            using var store = IdentityStore.CreateStore("test-conn");
            Assert.IsType<SqlServerIdentityStore>(store);
            Assert.Equal("test-conn", store.ConnectionId);
        });
    }

    [Fact]
    public void CreateStoreDefaultsToSqlite()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "sf-createstore-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            WithEnv(null, null, () =>
            {
                using var store = IdentityStore.CreateStore("test-conn", dataDir: tmp);
                Assert.IsType<IdentityStore>(store);
            });
        }
        finally
        {
            try
            {
                Directory.Delete(tmp, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
        }
    }
}
