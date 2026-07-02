// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Integration tests for Infrastructure/HaCoordinator (active-active crawl
// coordination). These need a real SQL Server with the contract schema
// deployed (scripts/sql) and SKIP (early return) unless the
// SQLSERVER_TEST_CONNECTION_STRING environment variable is set.

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

        // Exactly one CloseCrawlIfComplete call performs the close.
        Assert.True(await HaCoordinator.CloseCrawlIfCompleteAsync(crawl.CrawlId));
        Assert.False(await HaCoordinator.CloseCrawlIfCompleteAsync(crawl.CrawlId));
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
