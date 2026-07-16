// Webhook parsing, HA claim decision, settings/env-file loading, log pruning,
// alerting envelope.

using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector.Tests;

public class WebhookTests
{
    [Fact]
    public void ParseBody_SingleEvent()
    {
        var events = WebhookReceiver.ParseBody(
            """{"type":"contentPublished","contentId":"c1","teamsiteId":"ts1"}""");
        var evt = Assert.Single(events);
        Assert.Equal("contentPublished", evt.Type);
        Assert.Equal("c1", evt.ContentId);
        Assert.Equal("ts1", evt.TeamsiteId);
        Assert.False(evt.IsDelete);
    }

    [Fact]
    public void ParseBody_ArrayOfEvents()
    {
        var events = WebhookReceiver.ParseBody("""
            [{"type":"contentDeleted","contentId":"c1"},
             {"type":"contentUnpublished","contentId":"c2"}]
            """);
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.True(e.IsDelete));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("42")]
    public void ParseBody_GarbageIsIgnored(string body)
    {
        Assert.Empty(WebhookReceiver.ParseBody(body));
    }

    [Fact]
    public void Receiver_DisabledWhenPortIsZero()
    {
        Assert.Null(WebhookReceiver.StartIfConfigured(0));
    }
}

public class HaCoordinatorTests
{
    private static readonly DateTime Now = new(2026, 7, 12, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UnclaimedResource_CanBeClaimed()
    {
        Assert.True(HaCoordinator.TryDecide(null, "node-a", Now, 300));
    }

    [Fact]
    public void OwnClaim_IsReentrant()
    {
        Assert.True(HaCoordinator.TryDecide(("node-a", Now.AddSeconds(-30)), "node-a", Now, 300));
    }

    [Fact]
    public void LiveClaimByOtherNode_IsRespected()
    {
        Assert.False(HaCoordinator.TryDecide(("node-b", Now.AddSeconds(-60)), "node-a", Now, 300));
    }

    [Fact]
    public void StaleClaim_IsStolen()
    {
        Assert.True(HaCoordinator.TryDecide(("node-b", Now.AddSeconds(-301)), "node-a", Now, 300));
    }

    [Fact]
    public void BoundaryHeartbeat_IsStillLive()
    {
        // Exactly at the timeout is NOT stale (must be strictly older).
        Assert.False(HaCoordinator.TryDecide(("node-b", Now.AddSeconds(-300)), "node-a", Now, 300));
    }

    [Fact]
    public void Disabled_HaMode_ClaimsAlwaysSucceed()
    {
        Environment.SetEnvironmentVariable("HA_MODE", null);
        var coordinator = new HaCoordinator("Conn", "node-a");
        var handle = coordinator.OpenOrJoinCrawl("full", null);  // no SQL touched when disabled
        Assert.True(handle.Created);
        Assert.True(coordinator.TryClaim(handle.CrawlId, "teamsite:ts1"));
        coordinator.Heartbeat(handle.CrawlId, "teamsite:ts1");
        coordinator.CompleteClaim(handle.CrawlId, "teamsite:ts1", succeeded: true);
        Assert.Equal(HaCloseResult.ClosedByThisNode, coordinator.TryCloseCrawl(handle.CrawlId));
    }
}

public class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "seismic-env-" + Guid.NewGuid().ToString("N"));

    public SettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SEISMIC_TEST_KEY", null);
        Environment.SetEnvironmentVariable("SEISMIC_TEST_QUOTED", null);
        Environment.SetEnvironmentVariable("SEISMIC_TEST_EXISTING", null);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void LoadEnvFile_ParsesAndRespectsProcessEnv()
    {
        Environment.SetEnvironmentVariable("SEISMIC_TEST_EXISTING", "process-wins");
        var path = Path.Combine(_dir, ".env.local");
        File.WriteAllLines(path, new[]
        {
            "# comment",
            "",
            "SEISMIC_TEST_KEY=value1",
            "SEISMIC_TEST_QUOTED=\"quoted value\"",
            "SEISMIC_TEST_EXISTING=file-loses",
            "NOT A KV LINE",
        });

        Settings.LoadEnvFile(path);
        Assert.Equal("value1", Environment.GetEnvironmentVariable("SEISMIC_TEST_KEY"));
        Assert.Equal("quoted value", Environment.GetEnvironmentVariable("SEISMIC_TEST_QUOTED"));
        Assert.Equal("process-wins", Environment.GetEnvironmentVariable("SEISMIC_TEST_EXISTING"));
    }

    [Theory]
    [InlineData("SeismicSales")]
    [InlineData("Abc")]
    public void ValidConnectorIds_Pass(string id) => Settings.ValidateConnectorId(id);

    [Theory]
    [InlineData("ab")]                                    // too short
    [InlineData("ThisConnectorIdIsWayTooLongToBeValid1")] // > 32
    [InlineData("has-hyphен")]                            // non-alphanumeric
    [InlineData("SharePointClone")]                       // reserved prefix
    [InlineData("microsoftThing")]                        // reserved, case-insensitive
    public void InvalidConnectorIds_Throw(string id) =>
        Assert.Throws<ConfigException>(() => Settings.ValidateConnectorId(id));

    [Fact]
    public void BoolEnv_TruthySemantics()
    {
        Environment.SetEnvironmentVariable("SEISMIC_TEST_KEY", "YES");
        Assert.True(Settings.BoolEnv("SEISMIC_TEST_KEY"));
        Environment.SetEnvironmentVariable("SEISMIC_TEST_KEY", "0");
        Assert.False(Settings.BoolEnv("SEISMIC_TEST_KEY"));
        Environment.SetEnvironmentVariable("SEISMIC_TEST_KEY", null);
        Assert.True(Settings.BoolEnv("SEISMIC_TEST_KEY", fallback: true));
    }
}

public class LogPrunerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "seismic-logs-" + Guid.NewGuid().ToString("N"));

    public LogPrunerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LOG_RETENTION_DAYS", null);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void RetentionUnset_PrunesNothing()
    {
        Environment.SetEnvironmentVariable("LOG_RETENTION_DAYS", null);
        Directory.CreateDirectory(Path.Combine(_dir, "ingest_20200101_000000"));
        Assert.Equal(0, LogPruner.Prune(_dir));
        Assert.True(Directory.Exists(Path.Combine(_dir, "ingest_20200101_000000")));
    }

    [Fact]
    public void OldRunDirs_ArePruned_StateFilesAreNot()
    {
        Environment.SetEnvironmentVariable("LOG_RETENTION_DAYS", "7");
        var now = new DateTime(2026, 7, 12, 12, 0, 0);
        var oldDir = Path.Combine(_dir, "ingest_20260601_090000");
        var newDir = Path.Combine(_dir, $"ingest_{now.AddDays(-1):yyyyMMdd_HHmmss}");
        var unrelated = Path.Combine(_dir, "not-a-run-dir");
        Directory.CreateDirectory(oldDir);
        Directory.CreateDirectory(newDir);
        Directory.CreateDirectory(unrelated);
        var stateFile = Path.Combine(_dir, "sync_state.json");
        File.WriteAllText(stateFile, "{}");

        Assert.Equal(1, LogPruner.Prune(_dir, now));
        Assert.False(Directory.Exists(oldDir));
        Assert.True(Directory.Exists(newDir));
        Assert.True(Directory.Exists(unrelated));
        Assert.True(File.Exists(stateFile));
    }
}

public class AlertingTests : IDisposable
{
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ALERT_WEBHOOK_URL", null);
        Environment.SetEnvironmentVariable("ALERT_DEADLETTER_THRESHOLD", null);
        Alerting.ConnectorId = null;
    }

    [Fact]
    public void BuildEnvelope_ContainsKindMessageAndConnector()
    {
        Alerting.ConnectorId = "SeismicSales";
        var json = Alerting.BuildEnvelope("crawl_failed", "boom", new Dictionary<string, object?> { ["n"] = 3 });
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        Assert.Equal("crawl_failed", node["kind"]?.GetValue<string>());
        Assert.Equal("boom", node["message"]?.GetValue<string>());
        Assert.Equal("SeismicSales", node["connector"]?.GetValue<string>());
        Assert.Equal(3, node["data"]?["n"]?.GetValue<int>());
        Assert.NotNull(node["timestamp"]);
    }

    [Fact]
    public async Task RaiseAsync_NoUrl_IsNoop()
    {
        Environment.SetEnvironmentVariable("ALERT_WEBHOOK_URL", null);
        await Alerting.RaiseAsync("test", "no-op");  // must not throw
    }

    [Fact]
    public async Task DeadLetterAlert_RespectsThreshold()
    {
        Environment.SetEnvironmentVariable("ALERT_DEADLETTER_THRESHOLD", null);
        await Alerting.MaybeAlertDeadLetterAsync("Conn", 1000);  // disabled → no-op

        Environment.SetEnvironmentVariable("ALERT_DEADLETTER_THRESHOLD", "50");
        Environment.SetEnvironmentVariable("ALERT_WEBHOOK_URL", null);  // still no-op (no URL)
        await Alerting.MaybeAlertDeadLetterAsync("Conn", 51);
    }
}
