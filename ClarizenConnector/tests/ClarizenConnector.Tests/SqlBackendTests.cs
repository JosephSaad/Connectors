using System.Globalization;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Tests;

/// <summary>
/// Scriptable ISqlGateway: records every call; responses are dequeued per
/// method (Execute → affected-row counts, Scalar → values, Query → row sets).
/// </summary>
public sealed class FakeSqlGateway : ISqlGateway
{
    public List<(string Sql, (string Name, object? Value)[] Args)> Calls { get; } = new();

    public Queue<int> ExecuteResults { get; } = new();
    public Queue<object?> ScalarResults { get; } = new();
    public Queue<List<Dictionary<string, object?>>> QueryResults { get; } = new();

    public int Execute(string sql, params (string Name, object? Value)[] args)
    {
        Calls.Add((sql, args));
        return ExecuteResults.Count > 0 ? ExecuteResults.Dequeue() : 1;
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        Calls.Add((sql, args));
        return ScalarResults.Count > 0 ? ScalarResults.Dequeue() : 0;
    }

    public List<Dictionary<string, object?>> Query(string sql, params (string Name, object? Value)[] args)
    {
        Calls.Add((sql, args));
        return QueryResults.Count > 0
            ? QueryResults.Dequeue()
            : new List<Dictionary<string, object?>>();
    }

    public object? ArgValue(int callIndex, string name) =>
        Calls[callIndex].Args.First(a => a.Name == name).Value;
}

public class HaCoordinatorTests
{
    private static HaCoordinator Make(FakeSqlGateway sql) => new(sql, nodeId: "node-A");

    [Fact]
    public void IsClaimExpired_PureRule()
    {
        var now = new DateTime(2026, 7, 12, 10, 0, 0, DateTimeKind.Utc);
        Assert.True(HaCoordinator.IsClaimExpired(now.AddSeconds(-301), 300, now));
        Assert.False(HaCoordinator.IsClaimExpired(now.AddSeconds(-299), 300, now));
        Assert.False(HaCoordinator.IsClaimExpired(now, 300, now));
    }

    [Fact]
    public void OpenOrJoinCrawl_BuildsDeterministicKey()
    {
        var sql = new FakeSqlGateway();
        var slot = new DateTime(2026, 7, 12, 8, 0, 0, DateTimeKind.Utc);
        var key = Make(sql).OpenOrJoinCrawl("Conn", "full", slot);
        Assert.Equal("Conn|full|202607120800", key);
        var call = Assert.Single(sql.Calls);
        Assert.Contains("INSERT INTO dbo.CrawlRuns", call.Sql);
        Assert.Equal("node-A", sql.ArgValue(0, "@node"));
    }

    [Fact]
    public void TryClaimObject_UpdateWins()
    {
        var sql = new FakeSqlGateway();
        sql.ExecuteResults.Enqueue(1);  // UPDATE affected a row (fresh or expired takeover)
        Assert.True(Make(sql).TryClaimObject("key", "Project"));
        Assert.Single(sql.Calls);
    }

    [Fact]
    public void TryClaimObject_HeldByLiveNode_ReturnsFalse()
    {
        var sql = new FakeSqlGateway();
        sql.ExecuteResults.Enqueue(0);   // UPDATE touched nothing
        sql.ScalarResults.Enqueue(1);    // row exists → live claim elsewhere
        Assert.False(Make(sql).TryClaimObject("key", "Project"));
        Assert.Equal(2, sql.Calls.Count);
    }

    [Fact]
    public void TryClaimObject_AbsentRow_Inserts()
    {
        var sql = new FakeSqlGateway();
        sql.ExecuteResults.Enqueue(0);   // UPDATE: nothing
        sql.ScalarResults.Enqueue(0);    // row absent
        sql.ExecuteResults.Enqueue(1);   // INSERT succeeds
        Assert.True(Make(sql).TryClaimObject("key", "Project"));
        Assert.Contains(sql.Calls, c => c.Sql.Contains("INSERT INTO dbo.ObjectClaims"));
    }

    [Fact]
    public void TryCloseCrawl_BlockedByLiveClaim_OrMissingTerminalRows()
    {
        var sql = new FakeSqlGateway();
        sql.ScalarResults.Enqueue(1);    // a claim is still live
        Assert.False(Make(sql).TryCloseCrawl("key", new[] { "Project", "Task" }));

        sql.ScalarResults.Enqueue(0);    // no live claims...
        sql.ScalarResults.Enqueue(1);    // ...but only 1 of 2 terminal rows
        Assert.False(Make(sql).TryCloseCrawl("key", new[] { "Project", "Task" }));
    }

    [Fact]
    public void TryCloseCrawl_AllCompleted_ClosesWithStatusClosed()
    {
        var sql = new FakeSqlGateway();
        sql.ScalarResults.Enqueue(0);    // no live claims
        sql.ScalarResults.Enqueue(2);    // both terminal
        sql.ScalarResults.Enqueue(0);    // none failed
        sql.ExecuteResults.Enqueue(1);   // guarded UPDATE won
        Assert.True(Make(sql).TryCloseCrawl("key", new[] { "Project", "Task" }));

        var update = sql.Calls.Last(c => c.Sql.Contains("UPDATE dbo.CrawlRuns"));
        Assert.Equal("closed", update.Args.First(a => a.Name == "@status").Value);
    }

    [Fact]
    public void TryCloseCrawl_WithFailedClaims_StillCloses_AsFailed()
    {
        // Pinned close-with-failed-claims semantics: 'failed' is terminal, the
        // crawl still closes, and the crawl row records 'failed'.
        var sql = new FakeSqlGateway();
        sql.ScalarResults.Enqueue(0);    // no live claims
        sql.ScalarResults.Enqueue(2);    // completed + failed = 2 terminal rows
        sql.ScalarResults.Enqueue(1);    // one claim failed
        sql.ExecuteResults.Enqueue(1);   // guarded UPDATE won
        Assert.True(Make(sql).TryCloseCrawl("key", new[] { "Project", "Task" }));

        var update = sql.Calls.Last(c => c.Sql.Contains("UPDATE dbo.CrawlRuns"));
        Assert.Equal("failed", update.Args.First(a => a.Name == "@status").Value);
    }

    [Fact]
    public void TryCloseCrawl_LoserOfCloseRace_ReturnsFalse()
    {
        var sql = new FakeSqlGateway();
        sql.ScalarResults.Enqueue(0);    // no live claims
        sql.ScalarResults.Enqueue(2);    // all terminal
        sql.ScalarResults.Enqueue(0);    // none failed
        sql.ExecuteResults.Enqueue(0);   // another node already closed...
        sql.ScalarResults.Enqueue(0);    // ...and ClosedBy is not this node
        Assert.False(Make(sql).TryCloseCrawl("key", new[] { "Project", "Task" }));
    }

    [Fact]
    public void TryCloseCrawl_CommitAckLossRetryByWinner_StaysTrue()
    {
        // The winner's retry after a lost commit-ack must still report true —
        // ClosedBy makes the open→closed transition answer stable.
        var sql = new FakeSqlGateway();
        sql.ScalarResults.Enqueue(0);
        sql.ScalarResults.Enqueue(2);
        sql.ScalarResults.Enqueue(0);
        sql.ExecuteResults.Enqueue(0);   // UPDATE hits nothing (already closed)
        sql.ScalarResults.Enqueue(1);    // ClosedBy == this node
        Assert.True(Make(sql).TryCloseCrawl("key", new[] { "Project", "Task" }));
    }

    [Fact]
    public void FailObject_MarksClaimFailed_Terminal()
    {
        var sql = new FakeSqlGateway();
        Make(sql).FailObject("key", "Task");
        var call = Assert.Single(sql.Calls);
        Assert.Contains("UPDATE dbo.ObjectClaims", call.Sql);
        Assert.Equal("failed", call.Args.First(a => a.Name == "@status").Value);
        Assert.Contains("NodeId = @node", call.Sql);
    }

    [Fact]
    public void TryClaimObject_NeverReclaimsTerminalClaims()
    {
        // The takeover UPDATE only touches Status='claimed' rows — completed
        // and failed claims are terminal.
        var sql = new FakeSqlGateway();
        sql.ExecuteResults.Enqueue(0);
        sql.ScalarResults.Enqueue(1);
        Assert.False(Make(sql).TryClaimObject("key", "Task"));
        Assert.Contains("Status = 'claimed'", sql.Calls[0].Sql);
    }

    [Fact]
    public void Heartbeat_And_Complete_TargetOwnClaimOnly()
    {
        var sql = new FakeSqlGateway();
        var coordinator = Make(sql);
        coordinator.Heartbeat("key", "Task");
        coordinator.CompleteObject("key", "Task");
        Assert.All(sql.Calls, c => Assert.Contains("NodeId = @node", c.Sql));
    }
}

public class SqlStateStoreTests : IDisposable
{
    private readonly FakeSqlGateway _sql = new();

    public SqlStateStoreTests() => SqlStateStore.Gateway = _sql;

    public void Dispose() => SqlStateStore.ResetForTests();

    [Fact]
    public void ReadLastSync_MapsDateTimeToUtc()
    {
        _sql.ScalarResults.Enqueue(new DateTime(2026, 7, 1, 10, 0, 0));
        var read = SqlStateStore.ReadLastSync("Conn");
        Assert.Equal(DateTimeKind.Utc, read!.Value.Kind);

        _sql.ScalarResults.Enqueue(null);
        Assert.Null(SqlStateStore.ReadLastSync("Conn"));
    }

    [Fact]
    public void WriteLastSync_UsesMerge()
    {
        SqlStateStore.WriteLastSync("Conn", DateTime.UtcNow);
        var call = Assert.Single(_sql.Calls);
        Assert.Contains("MERGE dbo.SyncTimestamps", call.Sql);
    }

    [Fact]
    public void ReadCheckpoint_ReassemblesShape()
    {
        _sql.QueryResults.Enqueue(new List<Dictionary<string, object?>>
        {
            new() { ["ObjectType"] = "Project", ["SinceIso"] = "2026-07-01T00:00:00+00:00", ["ChunkIndex"] = 4 },
            new() { ["ObjectType"] = "Task", ["SinceIso"] = "2026-07-01T00:00:00+00:00", ["ChunkIndex"] = 9 },
        });
        var checkpoint = SqlStateStore.ReadCheckpoint("Conn");
        Assert.Equal("2026-07-01T00:00:00+00:00", checkpoint!["since"]!.GetValue<string>());
        Assert.Equal(4, checkpoint["completed"]!["Project"]!.GetValue<int>());
        Assert.Equal(9, checkpoint["completed"]!["Task"]!.GetValue<int>());

        _sql.QueryResults.Enqueue(new List<Dictionary<string, object?>>());
        Assert.Null(SqlStateStore.ReadCheckpoint("Conn"));
    }

    [Fact]
    public void WriteCheckpoint_ResetsOnNewSinceThenMerges()
    {
        SqlStateStore.WriteCheckpoint("Conn", "since-A", "Task", 3);
        Assert.Equal(2, _sql.Calls.Count);
        Assert.Contains("DELETE FROM dbo.Checkpoints", _sql.Calls[0].Sql);
        Assert.Contains("<> ISNULL(@since, '')", _sql.Calls[0].Sql);
        Assert.Contains("MERGE dbo.Checkpoints", _sql.Calls[1].Sql);
        Assert.Equal(3, _sql.ArgValue(1, "@chunk"));
    }

    [Fact]
    public void DeadLetter_RoundTripShape()
    {
        SqlStateStore.AppendDeadLetter(
            "Conn",
            new List<(string, string)> { ("Task_1", "HTTP 400") },
            "Task",
            requestBodies: null,
            responseBodies: null);
        Assert.Contains(_sql.Calls, c => c.Sql.Contains("INSERT INTO dbo.DeadLetter"));

        _sql.QueryResults.Enqueue(new List<Dictionary<string, object?>>
        {
            new()
            {
                ["ItemId"] = "Task_1", ["ObjectType"] = "Task", ["Error"] = "HTTP 400",
                ["RequestBody"] = """{"id": "Task_1"}""", ["ResponseBody"] = null,
                ["CreatedUtc"] = new DateTime(2026, 7, 12, 9, 0, 0, DateTimeKind.Utc),
            },
        });
        var entries = SqlStateStore.ReadDeadLetter("Conn");
        var entry = Assert.Single(entries);
        Assert.Equal("Task_1", entry["item_id"]!.GetValue<string>());
        Assert.Equal("Task_1", entry["request_body"]!["id"]!.GetValue<string>());
        Assert.Null(entry["response_body"]);
    }

    [Fact]
    public void SyncState_RoutesToSqlWhenEnabled()
    {
        using var scope = new EnvScope(
            ("USE_SQL_SERVER", "true"), ("SQL_CONNECTION_STRING", "Server=fake;Database=fake"));
        SyncState.ResetProviderCache();
        try
        {
            _sql.ScalarResults.Enqueue(new DateTime(2026, 7, 1, 0, 0, 0));
            var read = SyncState.ReadLastSync("Conn");
            Assert.NotNull(read);
            Assert.Contains(_sql.Calls, c => c.Sql.Contains("dbo.SyncTimestamps"));
        }
        finally
        {
            SyncState.ResetProviderCache();
        }
    }
}

public class SqlExecutorTests
{
    [Fact]
    public void EnsureEncrypt_ForcesEncryptWhenAbsent()
    {
        var result = SqlExecutor.EnsureEncrypt("Server=host;Database=db");
        Assert.Contains("Encrypt=True", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureEncrypt_RespectsExplicitSetting()
    {
        var result = SqlExecutor.EnsureEncrypt("Server=host;Database=db;Encrypt=False");
        Assert.Contains("Encrypt=False", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RetryDelayFor_ExponentialCappedAt30s()
    {
        Assert.Equal(1, SqlExecutor.RetryDelayFor(0).TotalSeconds, 3);
        Assert.Equal(2, SqlExecutor.RetryDelayFor(1).TotalSeconds, 3);
        Assert.Equal(16, SqlExecutor.RetryDelayFor(4).TotalSeconds, 3);
        Assert.Equal(30, SqlExecutor.RetryDelayFor(10).TotalSeconds, 3);
    }

    [Fact]
    public void TransientErrorNumbers_CoverKnownFailoverCodes()
    {
        foreach (var number in new[] { -2, 1205, 4060, 40613, 49918 })
            Assert.Contains(number, SqlExecutor.TransientErrorNumbers);
    }
}

public class SqlServerIdentityStoreTests
{
    [Fact]
    public void Upsert_Find_All_UseConnectorScopedSql()
    {
        var sql = new FakeSqlGateway();
        var store = new SqlServerIdentityStore("Conn", sql);

        store.Upsert(new PrincipalMapping("/User/1", "user", "a@x.com", "guid", DateTime.UtcNow));
        Assert.Contains("MERGE dbo.Principals", sql.Calls[0].Sql);
        Assert.Equal("Conn", sql.ArgValue(0, "@connector"));

        sql.QueryResults.Enqueue(new List<Dictionary<string, object?>>
        {
            new()
            {
                ["ClarizenId"] = "/User/1", ["PrincipalType"] = "user",
                ["Email"] = "a@x.com", ["EntraId"] = "guid",
                ["UpdatedUtc"] = new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc),
            },
        });
        var found = store.Find("/User/1");
        Assert.Equal("guid", found!.EntraId);

        sql.ScalarResults.Enqueue(5);
        Assert.Equal(5, store.Count());
        sql.ScalarResults.Enqueue(3);
        Assert.Equal(3, store.ResolvedCount());
    }
}
