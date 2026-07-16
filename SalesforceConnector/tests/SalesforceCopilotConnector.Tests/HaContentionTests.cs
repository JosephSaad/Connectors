// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Contention tests for the active-active HA stored procedures. The existing
// HaCoordinatorTests exercise every proc sequentially; these run the same
// procs under REAL concurrency — N tasks on N connections racing the applock,
// UPDLOCK/READPAST claim scan, and the open→closed transition — because
// "exactly one creator / one owner per object / one closer" are claims about
// contention, and contention is the one thing a sequential test never creates.
//
// Like the other SQL integration tests they need a real SQL Server with the
// contract schema deployed (scripts/sql) and SKIP (early return) unless
// SQLSERVER_TEST_CONNECTION_STRING is set. In CI they run in the
// sql-integration job.

using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Tests;

[Collection("IngestGlobalState")]
public class HaContentionTests
{
    private const int Nodes = 8;

    private static readonly string[] TwelveObjects =
    {
        "Account", "Contact", "Lead", "Opportunity", "Case", "Task",
        "Event", "Campaign", "Contract", "Order", "Product2", "Asset",
    };

    /// <summary>
    /// Raw usp_ClaimNextObject call with a caller-controlled idempotency token —
    /// the coordinator generates its token internally, and the token semantics
    /// under test live in the proc itself.
    /// </summary>
    private static async Task<string?> ClaimWithTokenAsync(
        Guid crawlId, string nodeId, Guid claimToken, int claimTimeoutSeconds = 300)
    {
        await using var connection = new SqlConnection(SqlTestSupport.TestConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "dbo.usp_ClaimNextObject";
        command.Parameters.AddWithValue("@CrawlId", crawlId);
        command.Parameters.AddWithValue("@NodeId", nodeId);
        command.Parameters.AddWithValue("@ClaimTimeoutSeconds", claimTimeoutSeconds);
        command.Parameters.AddWithValue("@ClaimToken", claimToken);
        var result = await command.ExecuteScalarAsync();
        return result is string objectType && objectType.Length > 0 ? objectType : null;
    }

    /// <summary>
    /// Backdate a claim's heartbeat so it reads as abandoned — the deterministic
    /// stand-in for "the node died N minutes ago" (no sleeping, and the staleness
    /// window can stay wide so a slow runner can't turn one dead node into two).
    /// </summary>
    private static async Task BackdateHeartbeatAsync(Guid crawlId, string objectType, int minutes)
    {
        await using var connection = new SqlConnection(SqlTestSupport.TestConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE dbo.ObjectClaims SET HeartbeatUtc = DATEADD(minute, -@Minutes, SYSUTCDATETIME()) " +
            "WHERE CrawlId = @CrawlId AND ObjectType = @ObjectType";
        command.Parameters.AddWithValue("@Minutes", minutes);
        command.Parameters.AddWithValue("@CrawlId", crawlId);
        command.Parameters.AddWithValue("@ObjectType", objectType);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    /// <summary>Run one task per simulated node, released simultaneously.</summary>
    private static async Task<T[]> RaceAsync<T>(Func<int, Task<T>> perNode)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, Nodes)
            .Select(i => Task.Run(async () => { await gate.Task; return await perNode(i); }))
            .ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    private static async Task DrainAndCloseAsync(Guid crawlId)
    {
        while (await HaCoordinator.ClaimNextObjectAsync(crawlId, "drain-node") is { } objectType)
            await HaCoordinator.CompleteClaimAsync(crawlId, objectType, "done", "drain-node");
        await HaCoordinator.CloseCrawlIfCompleteAsync(crawlId);
    }

    [Fact]
    public async Task ConcurrentOpenOrJoinHasExactlyOneCreatorAndOneCrawl()
    {
        if (string.IsNullOrEmpty(SqlTestSupport.TestConnectionString))
            return;  // SKIP: no SQL Server available
        using var scope = SqlTestSupport.SqlScope(("HA_MODE", "true"));
        var connectorId = SqlTestSupport.UniqueConnectorId("hacon-open");

        var results = await RaceAsync(i => HaCoordinator.OpenOrJoinCrawlAsync(
            connectorId, "full", null, TwelveObjects, nodeId: $"node{i}"));

        try
        {
            Assert.All(results, r => Assert.NotNull(r));
            // The applock serializes the open: however the race lands, exactly one
            // node may observe Created (it performs the creator-only reset) and
            // every node must end up inside the same crawl.
            Assert.Equal(1, results.Count(r => r!.Created));
            Assert.Single(results.Select(r => r!.CrawlId).Distinct());
        }
        finally
        {
            var crawlId = results.FirstOrDefault(r => r != null)?.CrawlId;
            if (crawlId != null)
                await DrainAndCloseAsync(crawlId.Value);
        }
    }

    [Fact]
    public async Task ConcurrentWorkersNeverClaimTheSameObjectTwice()
    {
        if (string.IsNullOrEmpty(SqlTestSupport.TestConnectionString))
            return;  // SKIP: no SQL Server available
        using var scope = SqlTestSupport.SqlScope(("HA_MODE", "true"));
        var connectorId = SqlTestSupport.UniqueConnectorId("hacon-claim");

        var crawl = await HaCoordinator.OpenOrJoinCrawlAsync(
            connectorId, "full", null, TwelveObjects, nodeId: "opener");
        Assert.NotNull(crawl);

        // Every node drains the queue as fast as it can — the UPDLOCK/READPAST
        // scan must hand each pending row to exactly one of the racing
        // connections.
        var grants = new ConcurrentBag<(string ObjectType, string Node)>();
        await RaceAsync<object?>(async i =>
        {
            var node = $"node{i}";
            while (await HaCoordinator.ClaimNextObjectAsync(crawl!.CrawlId, node) is { } objectType)
            {
                grants.Add((objectType, node));
                await HaCoordinator.CompleteClaimAsync(crawl.CrawlId, objectType, "done", node);
            }
            return null;
        });

        var duplicates = grants.GroupBy(g => g.ObjectType).Where(g => g.Count() > 1).ToList();
        Assert.True(duplicates.Count == 0,
            "objects granted to more than one worker:\n" + string.Join("\n",
                duplicates.Select(d => $"  {d.Key}: {string.Join(", ", d.Select(x => x.Node))}")));
        Assert.Equal(TwelveObjects.OrderBy(t => t), grants.Select(g => g.ObjectType).OrderBy(t => t));

        // All work done — every node races the close; ClosedBy makes it
        // exactly-once even when all eight hit the transition together.
        var closed = await RaceAsync(i => HaCoordinator.CloseCrawlIfCompleteAsync(crawl!.CrawlId, $"node{i}"));
        Assert.Equal(1, closed.Count(c => c));
    }

    [Fact]
    public async Task ClaimTokenRetryGetsItsCommittedClaimBackNotASecondObject()
    {
        if (string.IsNullOrEmpty(SqlTestSupport.TestConnectionString))
            return;  // SKIP: no SQL Server available
        using var scope = SqlTestSupport.SqlScope(("HA_MODE", "true"));
        var connectorId = SqlTestSupport.UniqueConnectorId("hacon-token");

        var crawl = await HaCoordinator.OpenOrJoinCrawlAsync(
            connectorId, "full", null, new[] { "Account", "Contact", "Lead" }, nodeId: "nodeA");
        Assert.NotNull(crawl);

        try
        {
            var token = Guid.NewGuid();
            var first = await ClaimWithTokenAsync(crawl!.CrawlId, "nodeA", token);
            Assert.NotNull(first);

            // A commit-ack-loss retry presents the same token: it must get the
            // committed claim back, never a second object (which would strand the
            // first until the claim timeout and stall the crawl close a cycle).
            var retry = await ClaimWithTokenAsync(crawl.CrawlId, "nodeA", token);
            Assert.Equal(first, retry);

            // A genuinely new call (fresh token) moves on to different work.
            var second = await ClaimWithTokenAsync(crawl.CrawlId, "nodeA", Guid.NewGuid());
            Assert.NotNull(second);
            Assert.NotEqual(first, second);
        }
        finally
        {
            await DrainAndCloseAsync(crawl!.CrawlId);
        }
    }

    [Fact]
    public async Task CrawlWithFailedClaimStillClosesExactlyOnce()
    {
        if (string.IsNullOrEmpty(SqlTestSupport.TestConnectionString))
            return;  // SKIP: no SQL Server available
        using var scope = SqlTestSupport.SqlScope(("HA_MODE", "true"));
        var connectorId = SqlTestSupport.UniqueConnectorId("hacon-failclose");

        var crawl = await HaCoordinator.OpenOrJoinCrawlAsync(
            connectorId, "full", null, new[] { "Account", "Contact" }, nodeId: "nodeA");
        Assert.NotNull(crawl);

        // One object crashes (terminal 'failed', recovery is the WORKER_CRASH
        // dead-letter marker — Python parity), the other succeeds. 'failed' must
        // count toward completion: if it didn't, a crashed object would wedge the
        // crawl open forever, since a failed claim is never reclaimed.
        var first = await HaCoordinator.ClaimNextObjectAsync(crawl!.CrawlId, "nodeA");
        var second = await HaCoordinator.ClaimNextObjectAsync(crawl.CrawlId, "nodeA");
        Assert.NotNull(first);
        Assert.NotNull(second);
        await HaCoordinator.CompleteClaimAsync(crawl.CrawlId, first!, "failed", "nodeA");

        // Not closable while the healthy object is still claimed.
        Assert.False(await HaCoordinator.CloseCrawlIfCompleteAsync(crawl.CrawlId, "nodeA"));

        await HaCoordinator.CompleteClaimAsync(crawl.CrawlId, second!, "done", "nodeA");

        // done + failed = nothing pending/claimed → the crawl closes, and the
        // exactly-one-closer guarantee holds for the mixed-outcome case too.
        Assert.True(await HaCoordinator.CloseCrawlIfCompleteAsync(crawl.CrawlId, "nodeA"));
        Assert.False(await HaCoordinator.CloseCrawlIfCompleteAsync(crawl.CrawlId, "nodeB"));
    }

    [Fact]
    public async Task StaleClaimIsReclaimedByExactlyOneRacingNode()
    {
        if (string.IsNullOrEmpty(SqlTestSupport.TestConnectionString))
            return;  // SKIP: no SQL Server available
        using var scope = SqlTestSupport.SqlScope(("HA_MODE", "true"));
        var connectorId = SqlTestSupport.UniqueConnectorId("hacon-reclaim");

        var crawl = await HaCoordinator.OpenOrJoinCrawlAsync(
            connectorId, "full", null, new[] { "Account" }, nodeId: "nodeA");
        Assert.NotNull(crawl);

        // Node A claims the only object and died 10 minutes ago (backdated
        // heartbeat) — far past the 5-minute staleness window the racers use,
        // while their own fresh heartbeats stay far inside it.
        Assert.Equal("Account", await HaCoordinator.ClaimNextObjectAsync(crawl!.CrawlId, "nodeA"));
        await BackdateHeartbeatAsync(crawl.CrawlId, "Account", minutes: 10);

        // Eight nodes race the reclaim. If the stale-row scan takes no update
        // lock, several racers observe the same stale heartbeat and all "win" —
        // this is the double-ingest race the UPDLOCK exists to prevent.
        var reclaims = await RaceAsync(i =>
            ClaimWithTokenAsync(crawl!.CrawlId, $"node{i}", Guid.NewGuid(), claimTimeoutSeconds: 300));

        var winners = reclaims.Where(r => r != null).ToList();
        Assert.True(winners.Count == 1,
            $"expected exactly one reclaim winner, got {winners.Count} " +
            $"({string.Join(", ", reclaims.Select((r, i) => r == null ? null : $"node{i}").Where(x => x != null))})");
        Assert.Equal("Account", winners[0]);

        var winnerIndex = Array.FindIndex(reclaims, r => r != null);
        await HaCoordinator.CompleteClaimAsync(crawl.CrawlId, "Account", "done", $"node{winnerIndex}");
        Assert.True(await HaCoordinator.CloseCrawlIfCompleteAsync(crawl.CrawlId, "closer"));
    }
}
