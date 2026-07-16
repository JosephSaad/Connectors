// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Integration tests for Infrastructure/HaCoordinator (active-active crawl
// coordination). These need a real SQL Server with the contract schema
// deployed (scripts/sql) and SKIP (early return) unless the
// SQLSERVER_TEST_CONNECTION_STRING environment variable is set.

using System.Data;
using SalesforceCopilotConnector.Config;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Tests;

[Collection("IngestGlobalState")]
public class HaCoordinatorTests
{
    private static readonly string[] TwoObjects = { "Account", "Contact" };

    /// <summary>Drain a crawl so leftover open sessions never leak between tests.</summary>
    private static async Task DrainAndCloseAsync(Guid crawlId, string nodeId = "drain-node")
    {
        while (true)
        {
            var objectType = await HaCoordinator.ClaimNextObjectAsync(crawlId, nodeId);
            if (objectType == null)
                break;
            await HaCoordinator.CompleteClaimAsync(crawlId, objectType, "done", nodeId);
        }
        await HaCoordinator.CloseCrawlIfCompleteAsync(crawlId);
    }

    [Fact]
    public async Task OpenThenJoinReturnsSameCrawl()
    {
        if (string.IsNullOrEmpty(SqlTestSupport.TestConnectionString))
            return;  // SKIP: no SQL Server available
        using var scope = SqlTestSupport.SqlScope(("HA_MODE", "true"));
        var connectorId = SqlTestSupport.UniqueConnectorId("hatest-open");

        var first = await HaCoordinator.OpenOrJoinCrawlAsync(
            connectorId, "full", null, TwoObjects, nodeId: "nodeA");
        Assert.NotNull(first);
        Assert.True(first!.Created);

        var second = await HaCoordinator.OpenOrJoinCrawlAsync(
            connectorId, "full", null, TwoObjects, nodeId: "nodeB");
        Assert.NotNull(second);
        Assert.False(second!.Created);
        Assert.Equal(first.CrawlId, second.CrawlId);

        await DrainAndCloseAsync(first.CrawlId);
    }

    [Fact]
    public async Task ClaimsHandOutEachObjectExactlyOnce()
    {
        if (string.IsNullOrEmpty(SqlTestSupport.TestConnectionString))
            return;  // SKIP: no SQL Server available
        using var scope = SqlTestSupport.SqlScope(("HA_MODE", "true"));
        var connectorId = SqlTestSupport.UniqueConnectorId("hatest-claim");

        var crawl = await HaCoordinator.OpenOrJoinCrawlAsync(
            connectorId, "full", null, TwoObjects, nodeId: "nodeA");
        Assert.NotNull(crawl);

        var claimed = new List<string>();
        for (var claimA = await HaCoordinator.ClaimNextObjectAsync(crawl!.CrawlId, "nodeA");
             claimA != null;
             claimA = await HaCoordinator.ClaimNextObjectAsync(crawl.CrawlId, "nodeA"))
        {
            claimed.Add(claimA);
            await HaCoordinator.CompleteClaimAsync(crawl.CrawlId, claimA, "done", "nodeA");
        }

        Assert.Equal(TwoObjects.OrderBy(t => t), claimed.OrderBy(t => t));
        Assert.Null(await HaCoordinator.ClaimNextObjectAsync(crawl.CrawlId, "nodeB"));

        // Exactly one NODE performs the close (recorded in ClosedBy): the
        // closer reports true, every other node false.
        Assert.True(await HaCoordinator.CloseCrawlIfCompleteAsync(crawl.CrawlId, "nodeA"));
        Assert.False(await HaCoordinator.CloseCrawlIfCompleteAsync(crawl.CrawlId, "nodeB"));
    }

    [Fact]
    public async Task StaleClaimIsReclaimedAfterHeartbeatExpiry()
    {
        if (string.IsNullOrEmpty(SqlTestSupport.TestConnectionString))
            return;  // SKIP: no SQL Server available
        // 1-second claim timeout so a "dead" node's claim expires quickly.
        using var scope = SqlTestSupport.SqlScope(
            ("HA_MODE", "true"),
            ("HA_CLAIM_TIMEOUT_SECONDS", "1"));
        var connectorId = SqlTestSupport.UniqueConnectorId("hatest-expiry");

        var crawl = await HaCoordinator.OpenOrJoinCrawlAsync(
            connectorId, "full", null, new[] { "Account" }, nodeId: "nodeA");
        Assert.NotNull(crawl);

        // Node A claims and then "dies" (no heartbeat).
        Assert.Equal("Account", await HaCoordinator.ClaimNextObjectAsync(crawl!.CrawlId, "nodeA"));

        // Not yet stale — node B gets nothing.
        Assert.Null(await HaCoordinator.ClaimNextObjectAsync(crawl.CrawlId, "nodeB"));

        // Past the claim timeout — node B reclaims the abandoned object.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal("Account", await HaCoordinator.ClaimNextObjectAsync(crawl.CrawlId, "nodeB"));

        await HaCoordinator.CompleteClaimAsync(crawl.CrawlId, "Account", "done", "nodeB");
        Assert.True(await HaCoordinator.CloseCrawlIfCompleteAsync(crawl.CrawlId));
    }

    [Fact]
    public async Task HeartbeatKeepsClaimAlive()
    {
        if (string.IsNullOrEmpty(SqlTestSupport.TestConnectionString))
            return;  // SKIP: no SQL Server available
        using var scope = SqlTestSupport.SqlScope(
            ("HA_MODE", "true"),
            ("HA_CLAIM_TIMEOUT_SECONDS", "2"),
            ("HA_HEARTBEAT_SECONDS", "1"));
        var connectorId = SqlTestSupport.UniqueConnectorId("hatest-hb");

        var crawl = await HaCoordinator.OpenOrJoinCrawlAsync(
            connectorId, "full", null, new[] { "Account" }, nodeId: "nodeA");
        Assert.NotNull(crawl);
        Assert.Equal("Account", await HaCoordinator.ClaimNextObjectAsync(crawl!.CrawlId, "nodeA"));

        using (HaCoordinator.StartHeartbeat(crawl.CrawlId, "Account", "nodeA"))
        {
            // Well past the raw claim timeout, but the heartbeat keeps it fresh.
            await Task.Delay(TimeSpan.FromSeconds(4));
            Assert.Null(await HaCoordinator.ClaimNextObjectAsync(crawl.CrawlId, "nodeB"));
        }

        // Heartbeat stopped (node stop/death) — the claim goes stale and is reclaimed.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal("Account", await HaCoordinator.ClaimNextObjectAsync(crawl.CrawlId, "nodeB"));

        await HaCoordinator.CompleteClaimAsync(crawl.CrawlId, "Account", "done", "nodeB");
        Assert.True(await HaCoordinator.CloseCrawlIfCompleteAsync(crawl.CrawlId));
    }

    // ── Commit-ack-loss idempotency (retry of a committed-but-unacked unit) ──

    /// <summary>
    /// usp_ClaimNextObject with an explicit @ClaimToken — proc-level, mirroring
    /// a transient retry of ONE ClaimNextObjectAsync unit (which generates its
    /// token outside the retry lambda and therefore re-presents the same one).
    /// </summary>
    private static async Task<string?> ClaimWithTokenAsync(Guid crawlId, string nodeId, Guid claimToken)
    {
        return await SqlExecutor.ExecuteAsync(SqlStateStore.ConnectionString, async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandType = CommandType.StoredProcedure;
            command.CommandText = "dbo.usp_ClaimNextObject";
            command.Parameters.AddWithValue("@CrawlId", crawlId);
            command.Parameters.AddWithValue("@NodeId", nodeId);
            command.Parameters.AddWithValue("@ClaimTimeoutSeconds", 300);
            command.Parameters.AddWithValue("@ClaimToken", claimToken);
            var result = await command.ExecuteScalarAsync();
            return result is string objectType && objectType.Length > 0 ? objectType : null;
        });
    }

    [Fact]
    public async Task ClaimRetryWithTheSameTokenReturnsTheSameObject()
    {
        if (string.IsNullOrEmpty(SqlTestSupport.TestConnectionString))
            return;  // SKIP: no SQL Server available
        using var scope = SqlTestSupport.SqlScope(("HA_MODE", "true"));
        var connectorId = SqlTestSupport.UniqueConnectorId("hatest-token");

        var crawl = await HaCoordinator.OpenOrJoinCrawlAsync(
            connectorId, "full", null, TwoObjects, nodeId: "nodeA");
        Assert.NotNull(crawl);

        var token = Guid.NewGuid();
        var first = await ClaimWithTokenAsync(crawl!.CrawlId, "nodeA", token);
        Assert.NotNull(first);

        // A commit-ack-loss retry presents the SAME token and must get the
        // SAME object back — not double-claim a second one (which would strand
        // the first, ownerless, until the claim timeout).
        var retried = await ClaimWithTokenAsync(crawl.CrawlId, "nodeA", token);
        Assert.Equal(first, retried);

        // A fresh token (a genuinely new claim call — even from the same node,
        // e.g. a concurrent worker) claims the OTHER object.
        var second = await ClaimWithTokenAsync(crawl.CrawlId, "nodeA", Guid.NewGuid());
        Assert.NotNull(second);
        Assert.NotEqual(first, second);

        await HaCoordinator.CompleteClaimAsync(crawl.CrawlId, first!, "done", "nodeA");
        await HaCoordinator.CompleteClaimAsync(crawl.CrawlId, second!, "done", "nodeA");
        Assert.True(await HaCoordinator.CloseCrawlIfCompleteAsync(crawl.CrawlId, "nodeA"));
    }

    [Fact]
    public async Task CloseCrawlRepeatedByTheCloserStillReportsClosed()
    {
        if (string.IsNullOrEmpty(SqlTestSupport.TestConnectionString))
            return;  // SKIP: no SQL Server available
        using var scope = SqlTestSupport.SqlScope(("HA_MODE", "true"));
        var connectorId = SqlTestSupport.UniqueConnectorId("hatest-reclose");

        var crawl = await HaCoordinator.OpenOrJoinCrawlAsync(
            connectorId, "full", null, new[] { "Account" }, nodeId: "nodeA");
        Assert.NotNull(crawl);
        Assert.Equal("Account", await HaCoordinator.ClaimNextObjectAsync(crawl!.CrawlId, "nodeA"));
        await HaCoordinator.CompleteClaimAsync(crawl.CrawlId, "Account", "done", "nodeA");

        // nodeA performs the close…
        Assert.True(await HaCoordinator.CloseCrawlIfCompleteAsync(crawl.CrawlId, "nodeA"));
        // …and a commit-ack-loss retry by nodeA still reports Closed=1
        // (derived from ClosedBy, not @@ROWCOUNT), so exactly one node records
        // last-sync / clears the checkpoint / logs the content crawl.
        Assert.True(await HaCoordinator.CloseCrawlIfCompleteAsync(crawl.CrawlId, "nodeA"));
        // Every other node still reports 0.
        Assert.False(await HaCoordinator.CloseCrawlIfCompleteAsync(crawl.CrawlId, "nodeB"));
    }

    [Fact]
    public async Task CreatedFlagIsStableWhenTheCreatorReopens()
    {
        if (string.IsNullOrEmpty(SqlTestSupport.TestConnectionString))
            return;  // SKIP: no SQL Server available
        using var scope = SqlTestSupport.SqlScope(("HA_MODE", "true"));
        var connectorId = SqlTestSupport.UniqueConnectorId("hatest-created");

        var first = await HaCoordinator.OpenOrJoinCrawlAsync(
            connectorId, "full", null, TwoObjects, nodeId: "nodeA");
        Assert.NotNull(first);
        Assert.True(first!.Created);

        // Commit-ack-loss retry: the creator re-runs the call, finds the crawl
        // it just created, and must still see Created=1 (CreatedBy-derived) so
        // the creator-only reset is not skipped.
        var retried = await HaCoordinator.OpenOrJoinCrawlAsync(
            connectorId, "full", null, TwoObjects, nodeId: "nodeA");
        Assert.NotNull(retried);
        Assert.Equal(first.CrawlId, retried!.CrawlId);
        Assert.True(retried.Created);

        // A joining node still reports Created=0.
        var joined = await HaCoordinator.OpenOrJoinCrawlAsync(
            connectorId, "full", null, TwoObjects, nodeId: "nodeB");
        Assert.NotNull(joined);
        Assert.Equal(first.CrawlId, joined!.CrawlId);
        Assert.False(joined.Created);

        await DrainAndCloseAsync(first.CrawlId);
    }

    [Fact]
    public async Task DuplicateObjectTypesCreateOneClaimEach()
    {
        if (string.IsNullOrEmpty(SqlTestSupport.TestConnectionString))
            return;  // SKIP: no SQL Server available
        using var scope = SqlTestSupport.SqlScope(("HA_MODE", "true"));
        var connectorId = SqlTestSupport.UniqueConnectorId("hatest-dupes");

        // schema.json with a duplicated objectName must not blow up the
        // ObjectClaims PK on create (SELECT DISTINCT in usp_OpenOrJoinCrawl).
        var crawl = await HaCoordinator.OpenOrJoinCrawlAsync(
            connectorId, "full", null, new[] { "Account", "Account", "Contact" }, nodeId: "nodeA");
        Assert.NotNull(crawl);
        Assert.True(crawl!.Created);

        var claimed = new List<string>();
        for (var claim = await HaCoordinator.ClaimNextObjectAsync(crawl.CrawlId, "nodeA");
             claim != null;
             claim = await HaCoordinator.ClaimNextObjectAsync(crawl.CrawlId, "nodeA"))
        {
            claimed.Add(claim);
            await HaCoordinator.CompleteClaimAsync(crawl.CrawlId, claim, "done", "nodeA");
        }

        Assert.Equal(new[] { "Account", "Contact" }, claimed.OrderBy(t => t));
        Assert.True(await HaCoordinator.CloseCrawlIfCompleteAsync(crawl.CrawlId, "nodeA"));
    }

    [Fact]
    public async Task FullCrawlCreationClearsCheckpointsInTheSameTransaction()
    {
        if (string.IsNullOrEmpty(SqlTestSupport.TestConnectionString))
            return;  // SKIP: no SQL Server available
        using var scope = SqlTestSupport.SqlScope(("HA_MODE", "true"));
        var connectorId = SqlTestSupport.UniqueConnectorId("hatest-cpfull");

        SyncState.WriteCheckpoint(connectorId, null, "Account", 3);
        Assert.NotNull(SyncState.ReadCheckpoint(connectorId));

        // Creating a FULL crawl clears the connector's checkpoints inside the
        // create transaction, so a creator dying before its client-side clear
        // cannot leave a stale checkpoint behind.
        var crawl = await HaCoordinator.OpenOrJoinCrawlAsync(
            connectorId, "full", null, TwoObjects, nodeId: "nodeA");
        Assert.NotNull(crawl);
        Assert.True(crawl!.Created);
        Assert.Null(SyncState.ReadCheckpoint(connectorId));

        await DrainAndCloseAsync(crawl.CrawlId);
    }

    [Fact]
    public async Task IncrementalCrawlCreationKeepsCheckpoints()
    {
        if (string.IsNullOrEmpty(SqlTestSupport.TestConnectionString))
            return;  // SKIP: no SQL Server available
        using var scope = SqlTestSupport.SqlScope(("HA_MODE", "true"));
        var connectorId = SqlTestSupport.UniqueConnectorId("hatest-cpincr");

        const string sinceIso = "2026-07-01T00:00:00";
        SyncState.WriteCheckpoint(connectorId, sinceIso, "Account", 3);

        // Only the FULL-crawl create branch resets checkpoints; an incremental
        // create must leave them for the shared 'since' resume semantics.
        var crawl = await HaCoordinator.OpenOrJoinCrawlAsync(
            connectorId, "incremental", sinceIso, TwoObjects, nodeId: "nodeA");
        Assert.NotNull(crawl);
        Assert.True(crawl!.Created);
        Assert.NotNull(SyncState.ReadCheckpoint(connectorId));

        await DrainAndCloseAsync(crawl.CrawlId);
        SyncState.ClearCheckpoint(connectorId);
    }

    [Fact]
    public async Task CycleDedupSkipsWhenLastSyncIsFresh()
    {
        if (string.IsNullOrEmpty(SqlTestSupport.TestConnectionString))
            return;  // SKIP: no SQL Server available
        using var scope = SqlTestSupport.SqlScope(("HA_MODE", "true"));
        var connectorId = SqlTestSupport.UniqueConnectorId("hatest-dedup");

        // Another node "completed" the cycle a moment ago.
        SyncState.WriteLastSync(connectorId, DateTime.UtcNow);

        // Due earlier than the last sync → the joiner skips the cycle.
        var skipped = await HaCoordinator.OpenOrJoinCrawlAsync(
            connectorId, "incremental", "2026-07-01T00:00:00", TwoObjects,
            cycleDueUtc: DateTime.UtcNow.AddMinutes(-5), nodeId: "nodeB");
        Assert.Null(skipped);

        // Due later than the last sync → a genuinely new cycle opens.
        var opened = await HaCoordinator.OpenOrJoinCrawlAsync(
            connectorId, "incremental", "2026-07-01T00:00:00", TwoObjects,
            cycleDueUtc: DateTime.UtcNow.AddSeconds(5), nodeId: "nodeB");
        Assert.NotNull(opened);
        Assert.True(opened!.Created);

        await DrainAndCloseAsync(opened.CrawlId);
    }
}
