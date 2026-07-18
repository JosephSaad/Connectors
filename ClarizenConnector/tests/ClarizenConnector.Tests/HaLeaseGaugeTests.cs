// HaLeaseGaugeTests.cs — the clarizen_connector_ha_claims_held gauge tracks
// leases the coordinator holds (claim → +1, complete/fail → −1) and renders in
// the Prometheus exposition (ops/grafana-dashboard.json panels key off it).

using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Tests;

public class HaLeaseGaugeTests : IDisposable
{
    public HaLeaseGaugeTests() => Metrics.ResetForTests();

    public void Dispose() => Metrics.ResetForTests();

    private static HaCoordinator Make(FakeSqlGateway sql) => new(sql, nodeId: "node-A");

    [Fact]
    public void ClaimAndComplete_MoveTheGauge()
    {
        var sql = new FakeSqlGateway();
        var ha = Make(sql);

        // UPDATE-takeover path: 1 row affected → we hold the lease.
        sql.ExecuteResults.Enqueue(1);
        Assert.True(ha.TryClaimObject("crawl-1", "Project"));
        Assert.Equal(1, Metrics.HaClaimsHeld);

        sql.ExecuteResults.Enqueue(1);
        Assert.True(ha.TryClaimObject("crawl-1", "Task"));
        Assert.Equal(2, Metrics.HaClaimsHeld);

        ha.CompleteObject("crawl-1", "Project");
        Assert.Equal(1, Metrics.HaClaimsHeld);
        ha.FailObject("crawl-1", "Task");   // failed is terminal too — lease released
        Assert.Equal(0, Metrics.HaClaimsHeld);
    }

    [Fact]
    public void InsertPath_CountsOnce_ReclaimIsIdempotent()
    {
        var sql = new FakeSqlGateway();
        var ha = Make(sql);

        // Fresh-claim path: UPDATE misses (0), no existing row (0), INSERT wins (1).
        sql.ExecuteResults.Enqueue(0);
        sql.ScalarResults.Enqueue(0);
        sql.ExecuteResults.Enqueue(1);
        Assert.True(ha.TryClaimObject("crawl-1", "Project"));
        Assert.Equal(1, Metrics.HaClaimsHeld);

        // Re-claiming a lease we already hold must not double-count.
        sql.ExecuteResults.Enqueue(1);
        Assert.True(ha.TryClaimObject("crawl-1", "Project"));
        Assert.Equal(1, Metrics.HaClaimsHeld);
    }

    [Fact]
    public void LostClaim_DoesNotMoveTheGauge()
    {
        var sql = new FakeSqlGateway();
        var ha = Make(sql);

        // UPDATE misses, a live row exists elsewhere → not ours.
        sql.ExecuteResults.Enqueue(0);
        sql.ScalarResults.Enqueue(1);
        Assert.False(ha.TryClaimObject("crawl-1", "Project"));
        Assert.Equal(0, Metrics.HaClaimsHeld);
    }

    [Fact]
    public void Gauge_RendersInThePrometheusExposition()
    {
        Metrics.SetHaClaimsHeld(3);
        var exposition = Metrics.RenderPrometheus();
        Assert.Contains("# TYPE clarizen_connector_ha_claims_held gauge", exposition);
        Assert.Contains("clarizen_connector_ha_claims_held 3", exposition);
    }
}
