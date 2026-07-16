using System.Text.Json;
using AltrataConnector.Altrata;
using AltrataConnector.Commands;
using AltrataConnector.Identity;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

public class PurgeAllTests
{
    private static (Commands.Runtime Runtime, FakeGraphClient Graph, string Root) Setup()
    {
        var root = TestFixtures.NewTempDir("purge");
        var graph = new FakeGraphClient();
        var runtime = TestFixtures.NewRuntime(TestFixtures.NewConfig(), graph, root);

        runtime.Identity.RecordIngestedItem(new IngestedItem("i1", "PersonProfile", "h", DateTime.UtcNow));
        runtime.Identity.RecordIngestedItem(new IngestedItem("i2", "WealthIndicator", "h", DateTime.UtcNow));
        runtime.Identity.UpsertCrosswalk(new CrosswalkEntry("P1", "C1", "email", DateTime.UtcNow));
        runtime.State.AddDeadLetter(new DeadLetterRecord { ItemId = "i3" });
        runtime.State.MarkDeliveryProcessed("d1", DateTime.UtcNow);
        runtime.State.IncrementBillableLookups(5);
        return (runtime, graph, root);
    }

    [Fact]
    public async Task DryRunReportsCountsWithoutDeleting()
    {
        var (runtime, graph, _) = Setup();
        using var _1 = runtime;

        var result = await CommandRegistry.PurgeAllAsync(runtime, confirm: false);

        Assert.Equal(true, result);
        Assert.Empty(graph.DeletedItems);                       // nothing withdrawn
        Assert.Equal(2, runtime.Identity.CountIngestedItems()); // registry intact
        Assert.Single(runtime.State.ReadDeadLetters());         // state intact
        Assert.Equal(5, runtime.State.GetBillableLookupCount());
    }

    [Fact]
    public async Task ConfirmWithdrawsAllItemsAndWipesState()
    {
        var (runtime, graph, _) = Setup();
        using var _1 = runtime;

        var result = await CommandRegistry.PurgeAllAsync(runtime, confirm: true);

        Assert.Equal(true, result);
        Assert.Equal(new[] { "i1", "i2" }, graph.DeletedItems.OrderBy(x => x).ToArray());
        Assert.Equal(0, runtime.Identity.CountIngestedItems());
        Assert.Empty(runtime.Identity.ListCrosswalk());
        Assert.Empty(runtime.State.ReadDeadLetters());
        Assert.Empty(runtime.State.ListProcessedDeliveries());
        Assert.Equal(0, runtime.State.GetBillableLookupCount());
    }

    [Fact]
    public async Task WithdrawFailureKeepsStateForRerun()
    {
        var (runtime, graph, _) = Setup();
        using var _1 = runtime;
        graph.FailWhen = null;
        graph.FailingDeletes.Add("i2");

        var result = await CommandRegistry.PurgeAllAsync(runtime, confirm: true);

        Assert.Equal(false, result);
        // i1 withdrawn and deregistered; i2 still tracked; state NOT wiped.
        Assert.Equal(1, runtime.Identity.CountIngestedItems());
        Assert.Single(runtime.State.ReadDeadLetters());
    }
}

public class RateLimiterTests
{
    [Fact]
    public void UnderTheLimitNoDelay()
    {
        var limiter = new RateLimiter(3);
        var t0 = new DateTime(2026, 7, 12, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(TimeSpan.Zero, limiter.ReserveDelay(t0));
        Assert.Equal(TimeSpan.Zero, limiter.ReserveDelay(t0.AddSeconds(1)));
        Assert.Equal(TimeSpan.Zero, limiter.ReserveDelay(t0.AddSeconds(2)));
    }

    [Fact]
    public void OverTheLimitWaitsForWindowToSlide()
    {
        var limiter = new RateLimiter(2);
        var t0 = new DateTime(2026, 7, 12, 10, 0, 0, DateTimeKind.Utc);
        limiter.ReserveDelay(t0);
        limiter.ReserveDelay(t0.AddSeconds(10));

        var wait = limiter.ReserveDelay(t0.AddSeconds(20));
        Assert.Equal(TimeSpan.FromSeconds(40), wait);  // first call ages out at t0+60
    }

    [Fact]
    public void WindowExpiryFreesSlots()
    {
        var limiter = new RateLimiter(1);
        var t0 = new DateTime(2026, 7, 12, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(TimeSpan.Zero, limiter.ReserveDelay(t0));
        Assert.Equal(TimeSpan.Zero, limiter.ReserveDelay(t0.AddSeconds(61)));
    }
}

public class AltrataApiClientTests
{
    private static (AltrataApiClient Client, FileStateStore State, AuditLog Audit, List<TimeSpan> Delays, ScriptedHandler Handler)
        Setup(int callsPerMinute = 60)
    {
        var root = TestFixtures.NewTempDir("api");
        var config = new AltrataConnector.Config.AppConfig
        {
            ConnectorId = "AltrataTest",
            ConnectorName = "t",
            ConnectorDescription = "t",
            AadClientId = "c",
            AadTenantId = "t",
            AadClientSecret = "s",
            AltrataApiBaseUrl = "https://api.altrata.test/v1",
            AltrataTokenUrl = "https://auth.altrata.test/oauth/token",
            AltrataClientId = "api-client",
            AltrataClientSecret = "api-secret",
            AltrataApiCallsPerMinute = callsPerMinute,
        };
        var state = new FileStateStore("AltrataTest",
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));
        var audit = new AuditLog("AltrataTest", logsDir: Path.Combine(root, "logs"));
        var delays = new List<TimeSpan>();
        var handler = new ScriptedHandler();
        var client = new AltrataApiClient(config, state, audit, handler,
            (wait, _) => { delays.Add(wait); return Task.CompletedTask; });
        return (client, state, audit, delays, handler);
    }

    [Fact]
    public async Task LookupIncrementsBillableCounterAndAuditsPurpose()
    {
        var (client, state, audit, _, handler) = Setup();
        handler.EnqueueJson(200, """{"access_token":"tok","expires_in":3600}""");
        handler.EnqueueJson(200, """{"id":"P77","person_name":"Ada Lovelace","net_worth_usd":"1000000"}""");

        var record = await client.LookupPersonAsync("P77", "joseph", "RFP for client X");

        Assert.Equal("P77", record.Id);
        Assert.Equal(Datasets.PersonProfile, record.Dataset);
        Assert.Equal(1, state.GetBillableLookupCount());

        var entries = audit.ReadAll();
        Assert.Single(entries);
        Assert.Equal("joseph", entries[0].Actor);
        Assert.Equal("api_lookup", entries[0].Action);
        Assert.Equal("P77", entries[0].AltrataId);
        Assert.Equal("RFP for client X", entries[0].Purpose);
        Assert.True(entries[0].Billable);
    }

    [Fact]
    public async Task FailedLookupIsNotBillable()
    {
        var (client, state, audit, _, handler) = Setup();
        handler.EnqueueJson(200, """{"access_token":"tok","expires_in":3600}""");
        handler.EnqueueJson(404, """{"error":"not found"}""");

        await Assert.ThrowsAsync<AltrataApiException>(
            () => client.LookupPersonAsync("P404", "joseph", "test"));

        Assert.Equal(0, state.GetBillableLookupCount());
        Assert.Empty(audit.ReadAll());
    }

    [Fact]
    public async Task ThrottleDelaysWhenOverCallsPerMinute()
    {
        var (client, _, _, delays, handler) = Setup(callsPerMinute: 1);
        handler.EnqueueJson(200, """{"access_token":"tok","expires_in":3600}""");
        handler.EnqueueJson(200, """{"id":"P1"}""");
        handler.EnqueueJson(200, """{"id":"P2"}""");

        await client.LookupPersonAsync("P1", "joseph", "a");
        await client.LookupPersonAsync("P2", "joseph", "b");

        Assert.Single(delays);
        Assert.True(delays[0] > TimeSpan.FromSeconds(50), $"expected ~60s wait, got {delays[0]}");
    }
}

public class AuditLogTests
{
    [Fact]
    public void AppendIsAdditiveAcrossInstances()
    {
        var logs = TestFixtures.NewTempDir("audit");
        var audit = new AuditLog("AltrataTest", logsDir: logs);
        audit.Append(new AuditEntry { Actor = "a", Action = "api_lookup", Purpose = "p1", Billable = true });

        var second = new AuditLog("AltrataTest", logsDir: logs);
        second.Append(new AuditEntry { Actor = "b", Action = "purge_all", Purpose = "p2" });

        var entries = second.ReadAll();
        Assert.Equal(2, entries.Count);
        Assert.Equal("a", entries[0].Actor);
        Assert.Equal("b", entries[1].Actor);

        // JSONL on disk: one object per line.
        var lines = File.ReadAllLines(second.Path).Where(l => !string.IsNullOrWhiteSpace(l));
        Assert.All(lines, line => JsonDocument.Parse(line));
    }
}
