// WP-AL-4 — enforceable usage ceilings + the entitlement-freshness cadence knob.
//
// The Feature Catalog requires "usage controls with query and volume metering"
// for this licensed source. The connector already counted billable lookups and
// already smoothed the call rate; neither could REFUSE one. These tests pin the
// ceiling that can.
//
// The PII contract is the same one the purpose veto is held to: a refusal may
// carry the opaque altrata id, the actor, the purpose and the decision — never a
// name, an employer or a net-worth figure. Since a refused lookup never issues
// an HTTP request, the connector cannot have those values at refusal time; the
// tests below prove they cannot leak in by any other route either.

using System.Text.Json;
using AltrataConnector.Altrata;
using AltrataConnector.Commands;
using AltrataConnector.Config;
using AltrataConnector.Infrastructure;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

// ============================================================================
// UsageMeter — the durable counter itself
// ============================================================================

public class UsageMeterTests
{
    private static (UsageMeter Meter, FileStateStore State, string Root) NewMeter(
        UsageBudgetOptions options, string? root = null)
    {
        root ??= TestFixtures.NewTempDir("usage_meter");
        var state = new FileStateStore("AltrataTest",
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));
        return (new UsageMeter(state, options), state, root);
    }

    private static readonly DateTime Noon = new(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UnsetCeilingIsNotEnforcingAndAlwaysAllows()
    {
        var (meter, state, _) = NewMeter(new UsageBudgetOptions());

        Assert.False(meter.Enforcing);
        for (var i = 0; i < 1000; i++)
            Assert.True(meter.TryReserve(Noon).Allowed);

        // Byte-identical to "no ceiling": not one byte of ledger is written.
        Assert.Null(state.GetValue(UsageMeter.StateKey));
    }

    [Fact]
    public void DailyCeilingAllowsExactlyTheLimitThenRefuses()
    {
        var (meter, _, _) = NewMeter(new UsageBudgetOptions { MaxPerDay = 3 });

        for (var i = 0; i < 3; i++)
            Assert.True(meter.TryReserve(Noon).Allowed);

        var refused = meter.TryReserve(Noon);
        Assert.False(refused.Allowed);
        Assert.Equal("daily-ceiling", refused.Reason);
        Assert.Equal(3, refused.Used);
        Assert.Equal(3, refused.Limit);
        Assert.Null(refused.Reservation);
    }

    [Fact]
    public void CountersSurviveARestart()
    {
        // Restart = a brand new store + meter over the SAME data directory,
        // which is exactly what a service restart produces.
        var root = TestFixtures.NewTempDir("usage_restart");
        var options = new UsageBudgetOptions { MaxPerDay = 2 };

        var (before, _, _) = NewMeter(options, root);
        Assert.True(before.TryReserve(Noon).Allowed);
        Assert.True(before.TryReserve(Noon).Allowed);

        var (after, _, _) = NewMeter(options, root);
        Assert.Equal(2, after.Peek(Noon).DayUsed);
        var refused = after.TryReserve(Noon);
        Assert.False(refused.Allowed);
        Assert.Equal("daily-ceiling", refused.Reason);
    }

    [Fact]
    public void DayRolloverResetsTheDailyCounter()
    {
        var (meter, _, _) = NewMeter(new UsageBudgetOptions { MaxPerDay = 2 });

        Assert.True(meter.TryReserve(Noon).Allowed);
        Assert.True(meter.TryReserve(Noon).Allowed);
        Assert.False(meter.TryReserve(Noon).Allowed);

        // 00:00 UTC the next day — the calendar window has rolled.
        var tomorrow = Noon.AddDays(1).Date;
        Assert.True(meter.TryReserve(tomorrow).Allowed);
        Assert.Equal(1, meter.Peek(tomorrow).DayUsed);
    }

    [Fact]
    public void JustBeforeMidnightIsStillTheOldDay()
    {
        var (meter, _, _) = NewMeter(new UsageBudgetOptions { MaxPerDay = 1 });
        Assert.True(meter.TryReserve(new DateTime(2026, 7, 19, 0, 0, 1, DateTimeKind.Utc)).Allowed);
        Assert.False(meter.TryReserve(new DateTime(2026, 7, 19, 23, 59, 59, DateTimeKind.Utc)).Allowed);
    }

    [Fact]
    public void RollingWindowRefusesThenRecoversAsBucketsAgeOut()
    {
        // 1h rolling window, cap 2. Buckets are window/60 = 60s wide.
        var options = new UsageBudgetOptions { MaxPerWindow = 2, WindowHours = 1 };
        var (meter, _, _) = NewMeter(options);

        Assert.True(meter.TryReserve(Noon).Allowed);
        Assert.True(meter.TryReserve(Noon.AddMinutes(10)).Allowed);

        var refused = meter.TryReserve(Noon.AddMinutes(20));
        Assert.False(refused.Allowed);
        Assert.Equal("rolling-1h-ceiling", refused.Reason);
        Assert.Equal(2, refused.Limit);

        // Still refused at +59m: both charges are inside the trailing hour.
        Assert.False(meter.TryReserve(Noon.AddMinutes(59)).Allowed);

        // At +61m the first charge has aged out; one slot is free again.
        Assert.True(meter.TryReserve(Noon.AddMinutes(61)).Allowed);
    }

    [Fact]
    public void BothCeilingsApplyAndTheDailyOneIsNotChargedWhenTheWindowRefuses()
    {
        // Window cap (1) bites before the daily cap (10). A refusal must not
        // consume the daily allowance — otherwise the two ceilings interact and
        // a throttled workload silently burns the day's budget.
        var options = new UsageBudgetOptions { MaxPerDay = 10, MaxPerWindow = 1, WindowHours = 1 };
        var (meter, _, _) = NewMeter(options);

        Assert.True(meter.TryReserve(Noon).Allowed);
        for (var i = 0; i < 5; i++)
            Assert.False(meter.TryReserve(Noon.AddMinutes(1)).Allowed);

        Assert.Equal(1, meter.Peek(Noon).DayUsed);   // exactly the one granted call
    }

    [Fact]
    public void ReleaseGivesTheReservationBack()
    {
        var (meter, _, _) = NewMeter(new UsageBudgetOptions { MaxPerDay = 1 });

        var granted = meter.TryReserve(Noon);
        Assert.True(granted.Allowed);
        Assert.NotNull(granted.Reservation);
        Assert.False(meter.TryReserve(Noon).Allowed);

        meter.Release(granted.Reservation!);
        Assert.Equal(0, meter.Peek(Noon).DayUsed);
        Assert.True(meter.TryReserve(Noon).Allowed);
    }

    [Fact]
    public void ReleaseNeverDrivesACounterNegative()
    {
        var (meter, _, _) = NewMeter(new UsageBudgetOptions { MaxPerDay = 2 });
        var granted = meter.TryReserve(Noon);

        for (var i = 0; i < 5; i++)
            meter.Release(granted.Reservation!);

        var snapshot = meter.Peek(Noon);
        Assert.Equal(0, snapshot.DayUsed);
        Assert.Equal(0, snapshot.WindowUsed);
    }

    [Fact]
    public void ConcurrentReservationsNeverExceedTheCeiling()
    {
        // The whole point of reserving through an ATOMIC read-modify-write: a
        // GetValue+SetValue pair would let all 64 racers read the same "used"
        // figure and each conclude there was room.
        const int limit = 10;
        var (meter, _, _) = NewMeter(new UsageBudgetOptions { MaxPerDay = limit });

        var granted = 0;
        Parallel.For(0, 64, _ =>
        {
            if (meter.TryReserve(Noon).Allowed)
                Interlocked.Increment(ref granted);
        });

        Assert.Equal(limit, granted);
        Assert.Equal(limit, meter.Peek(Noon).DayUsed);
    }

    [Fact]
    public void ChangingTheWindowLengthResetsTheRollingCounter()
    {
        var root = TestFixtures.NewTempDir("usage_window_change");
        var (oneHour, _, _) = NewMeter(new UsageBudgetOptions { MaxPerWindow = 1, WindowHours = 1 }, root);
        Assert.True(oneHour.TryReserve(Noon).Allowed);
        Assert.False(oneHour.TryReserve(Noon).Allowed);

        // Re-configured to a different window: the old buckets measure a
        // different span, so they are discarded (documented behaviour).
        var (twoHour, _, _) = NewMeter(new UsageBudgetOptions { MaxPerWindow = 1, WindowHours = 2 }, root);
        Assert.True(twoHour.TryReserve(Noon).Allowed);
    }

    [Fact]
    public void CorruptLedgerFailsIntoAFreshEnforceableWindowNotIntoNoCeiling()
    {
        var root = TestFixtures.NewTempDir("usage_corrupt");
        var (meter, state, _) = NewMeter(new UsageBudgetOptions { MaxPerDay = 1 }, root);
        state.SetValue(UsageMeter.StateKey, "{ this is not json");

        Assert.True(meter.TryReserve(Noon).Allowed);
        Assert.False(meter.TryReserve(Noon).Allowed);   // ceiling still enforced
    }

    [Fact]
    public void PeekDoesNotCharge()
    {
        var (meter, _, _) = NewMeter(new UsageBudgetOptions { MaxPerDay = 1 });
        for (var i = 0; i < 10; i++)
            Assert.Equal(0, meter.Peek(Noon).DayUsed);
        Assert.True(meter.TryReserve(Noon).Allowed);
    }
}

// ============================================================================
// IStateStore.MutateValue — the atomicity the ceiling rests on
// ============================================================================

public class MutateValueTests
{
    [Fact]
    public void MutateValueSeesTheCurrentValueAndPersistsTheNewOne()
    {
        var root = TestFixtures.NewTempDir("mutate_kv");
        var state = new FileStateStore("AltrataTest",
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));

        Assert.Null(state.MutateValue("k", current => { Assert.Null(current); return null; }));
        state.SetValue("k", "1");
        Assert.Equal("2", state.MutateValue("k", current => (int.Parse(current!) + 1).ToString()));
        Assert.Equal("2", state.GetValue("k"));
    }

    [Fact]
    public void MutateValueReturningNullDeletesTheKey()
    {
        var root = TestFixtures.NewTempDir("mutate_kv_del");
        var state = new FileStateStore("AltrataTest",
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));
        state.SetValue("k", "v");
        state.MutateValue("k", _ => null);
        Assert.Null(state.GetValue("k"));
    }

    [Fact]
    public void ConcurrentMutateValueIncrementsLoseNothing()
    {
        var root = TestFixtures.NewTempDir("mutate_kv_conc");
        var state = new FileStateStore("AltrataTest",
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));

        Parallel.For(0, 200, _ =>
            state.MutateValue("counter", current => (int.Parse(current ?? "0") + 1).ToString()));

        Assert.Equal("200", state.GetValue("counter"));
    }

    [Fact]
    public void MutateValueDoesNotDisturbOtherStateInTheSameDocument()
    {
        var root = TestFixtures.NewTempDir("mutate_kv_iso");
        var state = new FileStateStore("AltrataTest",
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));

        state.IncrementBillableLookups(7);
        state.SetLastSync("full", new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        state.AddSuppressedSubject("P-erased");
        state.MutateValue(UsageMeter.StateKey, _ => "{}");

        Assert.Equal(7, state.GetBillableLookupCount());
        Assert.NotNull(state.GetLastSync("full"));
        Assert.True(state.IsSubjectSuppressed("P-erased"));
    }
}

// ============================================================================
// AltrataApiClient — the ceiling as an actual refusal
// ============================================================================

public class UsageCeilingApiTests
{
    private static AppConfig ApiConfig(int maxPerDay = 0, int maxPerWindow = 0, int windowHours = 24) => new()
    {
        ConnectorId = "AltrataTest", ConnectorName = "t", ConnectorDescription = "t",
        AadClientId = "c", AadTenantId = "t", AadClientSecret = "s",
        AltrataApiBaseUrl = "https://api.altrata.test/v1",
        AltrataTokenUrl = "https://auth.altrata.test/oauth/token",
        AltrataClientId = "api-client", AltrataClientSecret = "api-secret",
        AltrataMaxLookupsPerDay = maxPerDay,
        AltrataMaxLookupsPerWindow = maxPerWindow,
        AltrataUsageWindowHours = windowHours,
    };

    private static (AltrataApiClient Client, FileStateStore State, AuditLog Audit, ScriptedHandler Handler)
        Setup(AppConfig config, IPurposePolicy? purpose = null, string? root = null)
    {
        root ??= TestFixtures.NewTempDir("usage_api");
        var state = new FileStateStore(config.ConnectorId,
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));
        var audit = new AuditLog(config.ConnectorId, logsDir: Path.Combine(root, "logs"));
        var handler = new ScriptedHandler();
        var client = new AltrataApiClient(config, state, audit, handler,
            (_, _) => Task.CompletedTask, purpose: purpose ?? new PurposePolicy(null));
        return (client, state, audit, handler);
    }

    /// <summary>A full successful lookup: token response + profile response.</summary>
    private static void ScriptOneLookup(ScriptedHandler handler, string id)
    {
        handler.EnqueueJson(200, """{"access_token":"tok","expires_in":3600}""");
        handler.EnqueueJson(200, $$"""{"id":"{{id}}","person_name":"Ada Lovelace","employer":"Analytical Engines","net_worth_usd":"9000000"}""");
    }

    [Fact]
    public async Task CeilingRefusesFailClosedWithZeroBillableAndZeroHttp()
    {
        var (client, state, audit, handler) = Setup(ApiConfig(maxPerDay: 1));

        ScriptOneLookup(handler, "P1");
        await client.LookupPersonAsync("P1", "joseph", "RFP");
        Assert.Equal(1, state.GetBillableLookupCount());
        var requestsAfterFirst = handler.Requests.Count;

        // Second lookup is over the ceiling. Nothing is enqueued for it, so a
        // leaked HTTP call would surface as "Unexpected request" rather than
        // UsageBudgetExceededException.
        await Assert.ThrowsAsync<UsageBudgetExceededException>(
            () => client.LookupPersonAsync("P2", "joseph", "RFP"));

        Assert.Equal(1, state.GetBillableLookupCount());          // never billed
        Assert.Equal(requestsAfterFirst, handler.Requests.Count); // not one request enqueued

        var denied = audit.ReadAll().Last();
        Assert.Equal("deny", denied.Decision);
        Assert.False(denied.Billable);
        Assert.Equal("api_lookup", denied.Action);
        Assert.Equal("P2", denied.AltrataId);
    }

    [Fact]
    public async Task RefusalIsRepeatedAndNeverDrifts()
    {
        var (client, state, _, handler) = Setup(ApiConfig(maxPerDay: 1));
        ScriptOneLookup(handler, "P1");
        await client.LookupPersonAsync("P1", "joseph", "RFP");

        for (var i = 0; i < 25; i++)
        {
            await Assert.ThrowsAsync<UsageBudgetExceededException>(
                () => client.LookupPersonAsync($"P{i}", "joseph", "RFP"));
        }
        Assert.Equal(1, state.GetBillableLookupCount());
    }

    [Fact]
    public async Task DenyAuditIsPiiSafe()
    {
        var (client, _, audit, handler) = Setup(ApiConfig(maxPerDay: 1));
        ScriptOneLookup(handler, "P1");
        await client.LookupPersonAsync("P1", "joseph", "RFP");

        await Assert.ThrowsAsync<UsageBudgetExceededException>(
            () => client.LookupPersonAsync("P2", "joseph", "RFP"));

        // The WHOLE audit file, not just the deny line: the granted lookup
        // fetched a profile carrying a name, an employer and a net-worth figure,
        // and none of it may have been written anywhere.
        var blob = File.ReadAllText(audit.Path);
        foreach (var pii in new[] { "Ada", "Lovelace", "Analytical Engines", "9000000", "net_worth", "access_token", "tok" })
            Assert.DoesNotContain(pii, blob, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("\"Decision\":\"deny\"", blob);
    }

    [Fact]
    public async Task DenyExceptionMessageIsPiiSafe()
    {
        var (client, _, _, handler) = Setup(ApiConfig(maxPerDay: 1));
        ScriptOneLookup(handler, "P1");
        await client.LookupPersonAsync("P1", "joseph", "RFP");

        var exc = await Assert.ThrowsAsync<UsageBudgetExceededException>(
            () => client.LookupPersonAsync("P2", "joseph", "RFP"));

        foreach (var pii in new[] { "Ada", "Lovelace", "Analytical Engines", "9000000" })
            Assert.DoesNotContain(pii, exc.Message, StringComparison.OrdinalIgnoreCase);
        // It must still be actionable: name the knob the operator has to change.
        Assert.Contains(UsageBudgetOptions.MaxPerDayEnvVar, exc.Message);
    }

    [Fact]
    public async Task UsageStateIsPiiSafe()
    {
        // The durable ledger is keyed by TIME, never by subject.
        var root = TestFixtures.NewTempDir("usage_state_pii");
        var (client, state, _, handler) = Setup(ApiConfig(maxPerDay: 1), root: root);
        ScriptOneLookup(handler, "P1");
        await client.LookupPersonAsync("P1", "joseph", "RFP");
        await Assert.ThrowsAsync<UsageBudgetExceededException>(
            () => client.LookupPersonAsync("P2", "joseph", "RFP"));

        var blob = File.ReadAllText(state.StatePath);
        foreach (var pii in new[] { "Ada", "Lovelace", "Analytical Engines", "9000000" })
            Assert.DoesNotContain(pii, blob, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PurposeVetoPrecedesTheBudgetCheck()
    {
        // ORDER MATTERS. A disallowed purpose must never even CONSUME budget:
        // otherwise anyone who can invoke the connector can exhaust the day's
        // ceiling with calls that were never going to be permitted, denying
        // service to the legitimate workload.
        var (client, state, audit, handler) = Setup(
            ApiConfig(maxPerDay: 2), purpose: new PurposePolicy(new[] { "RFP" }));

        for (var i = 0; i < 20; i++)
        {
            await Assert.ThrowsAsync<PurposeDeniedException>(
                () => client.LookupPersonAsync($"P{i}", "mallory", "scrape everyone"));
        }
        Assert.Empty(handler.Requests);

        // The full ceiling is still available to the allowed purpose.
        ScriptOneLookup(handler, "A1");
        await client.LookupPersonAsync("A1", "joseph", "RFP");
        handler.EnqueueJson(200, """{"id":"A2"}""");
        await client.LookupPersonAsync("A2", "joseph", "RFP");
        Assert.Equal(2, state.GetBillableLookupCount());

        // ...and only NOW does the ceiling bite.
        await Assert.ThrowsAsync<UsageBudgetExceededException>(
            () => client.LookupPersonAsync("A3", "joseph", "RFP"));

        Assert.Equal(20, audit.ReadAll().Count(e => e.Decision == "deny" && e.Purpose == "scrape everyone"));
    }

    [Fact]
    public async Task CeilingSurvivesARestart()
    {
        var root = TestFixtures.NewTempDir("usage_api_restart");
        var config = ApiConfig(maxPerDay: 1);

        var (before, _, _, handlerBefore) = Setup(config, root: root);
        ScriptOneLookup(handlerBefore, "P1");
        await before.LookupPersonAsync("P1", "joseph", "RFP");

        // New client, new state store, same data directory = a restart.
        var (after, state, _, handlerAfter) = Setup(config, root: root);
        await Assert.ThrowsAsync<UsageBudgetExceededException>(
            () => after.LookupPersonAsync("P2", "joseph", "RFP"));

        Assert.Empty(handlerAfter.Requests);
        Assert.Equal(1, state.GetBillableLookupCount());
    }

    [Fact]
    public async Task UnsetCeilingIsByteIdenticalToTheOldBehaviour()
    {
        // No ceiling configured: 50 lookups, all billed, all audited "allow",
        // and no usage ledger written at all.
        var (client, state, audit, handler) = Setup(ApiConfig());

        handler.EnqueueJson(200, """{"access_token":"tok","expires_in":3600}""");
        for (var i = 0; i < 50; i++)
            handler.EnqueueJson(200, $$"""{"id":"P{{i}}"}""");

        for (var i = 0; i < 50; i++)
            await client.LookupPersonAsync($"P{i}", "joseph", "RFP");

        Assert.Equal(50, state.GetBillableLookupCount());
        Assert.Null(state.GetValue(UsageMeter.StateKey));
        var entries = audit.ReadAll();
        Assert.Equal(50, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.Equal("allow", e.Decision);
            Assert.True(e.Billable);
        });
    }

    [Fact]
    public async Task AFailedLookupDoesNotPermanentlyConsumeBudget()
    {
        // The ceiling meters BILLABLE lookups. A 503 is not billable, so the
        // reservation is released — otherwise a flapping upstream would burn the
        // day's allowance without a single usable result.
        var (client, state, _, handler) = Setup(ApiConfig(maxPerDay: 2));

        handler.EnqueueJson(200, """{"access_token":"tok","expires_in":3600}""");
        handler.EnqueueJson(503, """{"error":"upstream"}""");
        await Assert.ThrowsAsync<AltrataApiException>(
            () => client.LookupPersonAsync("P1", "joseph", "RFP"));
        Assert.Equal(0, state.GetBillableLookupCount());

        // Both slots are still there.
        handler.EnqueueJson(200, """{"id":"P2"}""");
        await client.LookupPersonAsync("P2", "joseph", "RFP");
        handler.EnqueueJson(200, """{"id":"P3"}""");
        await client.LookupPersonAsync("P3", "joseph", "RFP");
        Assert.Equal(2, state.GetBillableLookupCount());

        await Assert.ThrowsAsync<UsageBudgetExceededException>(
            () => client.LookupPersonAsync("P4", "joseph", "RFP"));
    }

    [Fact]
    public async Task RollingWindowCeilingRefusesThroughTheApiClient()
    {
        var (client, state, _, handler) = Setup(ApiConfig(maxPerWindow: 1, windowHours: 1));

        ScriptOneLookup(handler, "P1");
        await client.LookupPersonAsync("P1", "joseph", "RFP");

        var exc = await Assert.ThrowsAsync<UsageBudgetExceededException>(
            () => client.LookupPersonAsync("P2", "joseph", "RFP"));
        Assert.Contains(UsageBudgetOptions.MaxPerWindowEnvVar, exc.Message);
        Assert.Equal(1, state.GetBillableLookupCount());
    }

    [Fact]
    public async Task DenyIncrementsItsOwnMetricNotThePurposeOne()
    {
        var before = Metrics.Get("altrata_usage_denied_total");
        var purposeBefore = Metrics.Get("altrata_purpose_denied_total");

        var (client, _, _, handler) = Setup(ApiConfig(maxPerDay: 1));
        ScriptOneLookup(handler, "P1");
        await client.LookupPersonAsync("P1", "joseph", "RFP");
        await Assert.ThrowsAsync<UsageBudgetExceededException>(
            () => client.LookupPersonAsync("P2", "joseph", "RFP"));

        Assert.True(Metrics.Get("altrata_usage_denied_total") > before);
        Assert.Equal(purposeBefore, Metrics.Get("altrata_purpose_denied_total"));
    }
}

// ============================================================================
// Config validation
// ============================================================================

public class UsageBudgetConfigTests
{
    private static AppConfig BaseConfig() => new()
    {
        ConnectorId = "AltrataTest", ConnectorName = "t", ConnectorDescription = "t",
        AadClientId = "c", AadTenantId = "t", AadClientSecret = "s",
    };

    [Fact]
    public void DefaultsAreNoCeiling()
    {
        var options = UsageBudgetOptions.FromConfig(BaseConfig());
        Assert.False(options.Enforcing);
        Assert.Equal(0, options.MaxPerDay);
        Assert.Equal(0, options.MaxPerWindow);
        Assert.Equal(24, options.WindowHours);
    }

    [Fact]
    public void OptionsAreCarriedFromAppConfig()
    {
        var options = UsageBudgetOptions.FromConfig(BaseConfig() with
        {
            AltrataMaxLookupsPerDay = 500,
            AltrataMaxLookupsPerWindow = 100,
            AltrataUsageWindowHours = 6,
        });
        Assert.True(options.Enforcing);
        Assert.Equal(500, options.MaxPerDay);
        Assert.Equal(100, options.MaxPerWindow);
        Assert.Equal(6, options.WindowHours);
        Assert.Equal(6 * 3600, options.WindowSeconds);
        Assert.Equal(6 * 3600 / 60, options.BucketSeconds);
        Assert.Equal(60, options.BucketCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(24)]
    [InlineData(168)]
    public void BucketGranularityStaysBoundedAcrossTheWholeWindowRange(int hours)
    {
        var options = new UsageBudgetOptions { MaxPerWindow = 1, WindowHours = hours };
        Assert.True(options.BucketSeconds >= 1);
        Assert.True(options.BucketCount <= 60);
    }

    [Fact]
    public void NegativeAndOutOfRangeValuesAreRejectedByLoad()
    {
        // Validation lives in AppConfig.Load's errors block, so a typo fails
        // validate-config / startup rather than mid-crawl.
        var errors = LoadErrorsWith(
            (UsageBudgetOptions.MaxPerDayEnvVar, "-1"),
            (UsageBudgetOptions.MaxPerWindowEnvVar, "-5"),
            (UsageBudgetOptions.WindowHoursEnvVar, "0"));

        Assert.Contains(UsageBudgetOptions.MaxPerDayEnvVar, errors);
        Assert.Contains(UsageBudgetOptions.MaxPerWindowEnvVar, errors);
        Assert.Contains(UsageBudgetOptions.WindowHoursEnvVar, errors);
    }

    [Fact]
    public void WindowHoursAboveTheRangeIsRejected()
    {
        Assert.Contains(UsageBudgetOptions.WindowHoursEnvVar,
            LoadErrorsWith((UsageBudgetOptions.WindowHoursEnvVar, "169")));
    }

    [Fact]
    public void ValidValuesLoadCleanly()
    {
        var errors = LoadErrorsWith(
            (UsageBudgetOptions.MaxPerDayEnvVar, "1000"),
            (UsageBudgetOptions.MaxPerWindowEnvVar, "250"),
            (UsageBudgetOptions.WindowHoursEnvVar, "12"));

        Assert.DoesNotContain(UsageBudgetOptions.MaxPerDayEnvVar, errors);
        Assert.DoesNotContain(UsageBudgetOptions.MaxPerWindowEnvVar, errors);
        Assert.DoesNotContain(UsageBudgetOptions.WindowHoursEnvVar, errors);
    }

    /// <summary>Run AppConfig.Load with the given env overrides and return the
    /// aggregated error text (Load reports every problem in one message, so the
    /// unrelated "Missing CONNECTOR_NAME" noise is harmless here).</summary>
    private static string LoadErrorsWith(params (string Key, string Value)[] overrides)
    {
        var saved = overrides.Select(o => (o.Key, Old: Environment.GetEnvironmentVariable(o.Key))).ToList();
        try
        {
            foreach (var (key, value) in overrides)
                Environment.SetEnvironmentVariable(key, value);
            try
            {
                AppConfig.Load();
                return "";
            }
            catch (ConfigurationError exc)
            {
                return exc.Message;
            }
        }
        finally
        {
            foreach (var (key, old) in saved)
                Environment.SetEnvironmentVariable(key, old);
        }
    }
}

// ============================================================================
// WP-AL-4 part 2 — entitlement-freshness cadence
// ============================================================================

public class EntitlementCadenceTests
{
    private static ParsedArgs Parse(params string[] args) =>
        CommandRegistry.BuildParser().ParseArgs(new[] { "ingest" }.Concat(args).ToArray());

    [Fact]
    public void SubHourCadenceIsExpressible()
    {
        Assert.Equal(TimeSpan.FromMinutes(15),
            CommandRegistry.ResolveIncrementalInterval(Parse("--incremental-minutes", "15")));
        Assert.Equal(TimeSpan.FromMinutes(1),
            CommandRegistry.ResolveIncrementalInterval(Parse("--incremental-minutes", "1")));
    }

    [Fact]
    public void MinutesWinOverHoursWhenBothAreGiven()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), CommandRegistry.ResolveIncrementalInterval(
            Parse("--incremental-hours", "12", "--incremental-minutes", "5")));
    }

    [Fact]
    public void HoursStillWorkAndTheDefaultIsUnchanged()
    {
        Assert.Equal(TimeSpan.FromHours(12),
            CommandRegistry.ResolveIncrementalInterval(Parse("--incremental-hours", "12")));
        Assert.Equal(TimeSpan.FromHours(4), CommandRegistry.ResolveIncrementalInterval(Parse()));
    }

    [Fact]
    public void CadenceBoundsAreEnforcedByTheParser()
    {
        Assert.Throws<ArgumentParserExit>(() => Parse("--incremental-minutes", "0"));
        Assert.Throws<ArgumentParserExit>(() => Parse("--incremental-minutes", "10081"));
        Assert.Throws<ArgumentParserExit>(() => Parse("--incremental-minutes", "abc"));
    }

    [Fact]
    public void TheOptionIsOfferedOnEveryContinuousCommand()
    {
        // The continuous-mode commands are exactly `ingest` and `full-deployment`
        // (ingest-object is a one-shot dataset dump and has no scheduler).
        var parser = CommandRegistry.BuildParser();
        foreach (var name in new[] { "ingest", "full-deployment" })
        {
            var command = parser.Commands.Single(c => c.Name == name);
            Assert.Contains(command.Options, o => o.Name == "--incremental-minutes");
        }
    }

    [Fact]
    public void SchedulerHonoursASubHourCadenceWithoutOvershooting()
    {
        // Drive the real sleep computation forward from t0 and prove the
        // incremental fires at +15m, not rounded up by the 30s wake cap.
        var start = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var nextFull = start.AddHours(24);
        var nextIncremental = start.AddMinutes(15);

        var now = start;
        var iterations = 0;
        while (now < nextIncremental && iterations++ < 1000)
        {
            var sleep = CommandRegistry.SchedulerSleep(now, nextFull, nextIncremental);
            Assert.True(sleep > TimeSpan.Zero, "scheduler must not spin");
            Assert.True(sleep <= TimeSpan.FromSeconds(30), "graceful-stop responsiveness cap");
            now += sleep;
        }

        Assert.Equal(nextIncremental, now);   // lands exactly, never late
    }

    [Fact]
    public void SchedulerReturnsZeroWhenACrawlIsAlreadyDue()
    {
        var now = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(TimeSpan.Zero,
            CommandRegistry.SchedulerSleep(now, now.AddHours(1), now.AddMinutes(-1)));
    }

    [Fact]
    public void CadenceIsLoggedInAUnitThatCannotReadAsZero()
    {
        // The old log line formatted hours with "0", so a 15-minute cadence
        // would have printed "every 0h".
        Assert.Equal("15m", CommandRegistry.FormatCadence(TimeSpan.FromMinutes(15)));
        Assert.Equal("1m", CommandRegistry.FormatCadence(TimeSpan.FromMinutes(1)));
        Assert.Equal("4h", CommandRegistry.FormatCadence(TimeSpan.FromHours(4)));
        Assert.Equal("1.5h", CommandRegistry.FormatCadence(TimeSpan.FromMinutes(90)));
    }
}
