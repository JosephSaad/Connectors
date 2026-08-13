// BankGradeHardeningTests.cs
// --------------------------
// Focused tests for the prioritized bank-grade hardening fixes:
//   #3  restrictive filesystem permissions at startup (0700 on POSIX)
//   #5  entitlement freshness — IDENTITY_SYNC_ON_INCREMENTAL default TRUE
//   #6  classification honesty (advisory tag) + optional ACL enforcement
//   #7  purpose-based authorization / per-action veto (fail-closed, audited)
//   #8  stale-index expiry — GRAPH_ITEM_TTL_DAYS stamps expirationDateTime
//   #11 immutable decision ledger (exclusion / ACL-restriction), hash-chained

using System.Runtime.InteropServices;
using AltrataConnector.Altrata;
using AltrataConnector.Config;
using AltrataConnector.Entitlement;
using AltrataConnector.Graph;
using AltrataConnector.Infrastructure;
using AltrataConnector.Ingestion;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

// ============================================================================
// #3 — restrictive filesystem permissions
// ============================================================================

public class DirectoryHardeningTests
{
    [Fact]
    public void EnsureSecureDirectoryCreatesOwnerOnlyDirectory()
    {
        var root = TestFixtures.NewTempDir("harden");
        var target = Path.Combine(root, "state");

        DirectoryHardening.EnsureSecureDirectory(target);

        Assert.True(Directory.Exists(target));
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // POSIX: created (or tightened) to rwx------ (0700).
            Assert.Equal(DirectoryHardening.OwnerOnly, File.GetUnixFileMode(target));
        }
    }

    [Fact]
    public void EnsureSecureDirectoryIsIdempotentAndTightensExisting()
    {
        var root = TestFixtures.NewTempDir("harden2");
        var target = Path.Combine(root, "logs");
        Directory.CreateDirectory(target);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        DirectoryHardening.EnsureSecureDirectory(target);   // pre-existing, world-readable

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Assert.Equal(DirectoryHardening.OwnerOnly, File.GetUnixFileMode(target));
    }

    [Fact]
    public void EnsureSecureDirectoryNeverThrowsOnBadPath()
    {
        // Empty / whitespace path is a no-op, never an exception.
        DirectoryHardening.EnsureSecureDirectory("");
        DirectoryHardening.EnsureSecureDirectory("   ");
    }
}

// ============================================================================
// #7 — purpose-based authorization (unit)
// ============================================================================

public class PurposePolicyTests
{
    [Fact]
    public void UnsetAllowlistIsRecordOnly()
    {
        var policy = new PurposePolicy(null);
        Assert.False(policy.Enforcing);
        Assert.True(policy.Evaluate("joseph", "anything at all").Allowed);
    }

    [Fact]
    public void ConfiguredAllowlistEnforcesCaseInsensitively()
    {
        var policy = new PurposePolicy(new[] { "RFP", "Due Diligence" });
        Assert.True(policy.Enforcing);
        Assert.True(policy.Evaluate("j", "rfp").Allowed);            // case-insensitive
        Assert.True(policy.Evaluate("j", "  Due Diligence ").Allowed); // trimmed
        var denied = policy.Evaluate("j", "marketing list build");
        Assert.False(denied.Allowed);
        Assert.Equal("not-in-allowlist", denied.Reason);
    }

    [Fact]
    public void EmptyPurposeIsDeniedWhenEnforcing()
    {
        var policy = new PurposePolicy(new[] { "RFP" });
        Assert.False(policy.Evaluate("j", "").Allowed);
        Assert.False(policy.Evaluate("j", "   ").Allowed);
    }

    [Fact]
    public void FromEnvParsesCommaSeparatedList()
    {
        var previous = Environment.GetEnvironmentVariable(PurposePolicy.AllowlistEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(PurposePolicy.AllowlistEnvVar, "RFP, Due Diligence ,KYC");
            var policy = PurposePolicy.FromEnv();
            Assert.True(policy.Enforcing);
            Assert.True(policy.Evaluate("j", "KYC").Allowed);
            Assert.True(policy.Evaluate("j", "Due Diligence").Allowed);
            Assert.False(policy.Evaluate("j", "nope").Allowed);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PurposePolicy.AllowlistEnvVar, previous);
        }
    }
}

// ============================================================================
// #7 — purpose enforcement at the billable lookup (LookupPersonAsync)
// ============================================================================

public class PurposeEnforcementApiTests
{
    private static (AltrataApiClient Client, FileStateStore State, AuditLog Audit, ScriptedHandler Handler)
        Setup(IPurposePolicy purpose)
    {
        var root = TestFixtures.NewTempDir("purpose_api");
        var config = new AppConfig
        {
            ConnectorId = "AltrataTest", ConnectorName = "t", ConnectorDescription = "t",
            AadClientId = "c", AadTenantId = "t", AadClientSecret = "s",
            AltrataApiBaseUrl = "https://api.altrata.test/v1",
            AltrataTokenUrl = "https://auth.altrata.test/oauth/token",
            AltrataClientId = "api-client", AltrataClientSecret = "api-secret",
        };
        var state = new FileStateStore("AltrataTest",
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));
        var audit = new AuditLog("AltrataTest", logsDir: Path.Combine(root, "logs"));
        var handler = new ScriptedHandler();
        var client = new AltrataApiClient(config, state, audit, handler,
            (_, _) => Task.CompletedTask, purpose: purpose);
        return (client, state, audit, handler);
    }

    [Fact]
    public async Task AllowedPurposeProceedsAndIsBillable()
    {
        var (client, state, audit, handler) = Setup(new PurposePolicy(new[] { "RFP for client X" }));
        handler.EnqueueJson(200, """{"access_token":"tok","expires_in":3600}""");
        handler.EnqueueJson(200, """{"id":"P1","person_name":"Ada"}""");

        var record = await client.LookupPersonAsync("P1", "joseph", "RFP for client X");

        Assert.Equal("P1", record.Id);
        Assert.Equal(1, state.GetBillableLookupCount());
        var entries = audit.ReadAll();
        Assert.Single(entries);
        Assert.Equal("allow", entries[0].Decision);
        Assert.True(entries[0].Billable);
    }

    [Fact]
    public async Task DisallowedPurposeIsDeniedFailClosedAndAudited()
    {
        // No HTTP responses enqueued: a billable call would throw "Unexpected
        // request" — proving the veto happens BEFORE any billable work.
        var (client, state, audit, _) = Setup(new PurposePolicy(new[] { "RFP" }));

        await Assert.ThrowsAsync<PurposeDeniedException>(
            () => client.LookupPersonAsync("P9", "mallory", "scrape everyone"));

        Assert.Equal(0, state.GetBillableLookupCount());   // never billed
        var entries = audit.ReadAll();
        Assert.Single(entries);
        Assert.Equal("deny", entries[0].Decision);
        Assert.False(entries[0].Billable);
        Assert.Equal("scrape everyone", entries[0].Purpose);
        Assert.Equal("P9", entries[0].AltrataId);
    }

    [Fact]
    public async Task NoAllowlistPreservesRecordOnlyBehaviour()
    {
        var (client, state, audit, handler) = Setup(new PurposePolicy(null));  // enforcement off
        handler.EnqueueJson(200, """{"access_token":"tok","expires_in":3600}""");
        handler.EnqueueJson(200, """{"id":"P2"}""");

        await client.LookupPersonAsync("P2", "joseph", "any purpose whatsoever");

        Assert.Equal(1, state.GetBillableLookupCount());   // back-compat: proceeds
        Assert.Equal("allow", audit.ReadAll()[0].Decision);
    }

    [Fact]
    public async Task DeniedAuditIsPiiSafe()
    {
        var (client, _, audit, _) = Setup(new PurposePolicy(new[] { "RFP" }));
        await Assert.ThrowsAsync<PurposeDeniedException>(
            () => client.LookupPersonAsync("P9", "mallory", "nope"));

        var blob = File.ReadAllText(audit.Path);
        // Only ids/actor/purpose/decision — never a profile value.
        Assert.DoesNotContain("access_token", blob);
        Assert.Contains("\"Decision\":\"deny\"", blob);
    }
}

// ============================================================================
// #6 — classification honesty (advisory) + optional ACL enforcement
// ============================================================================

public class ClassificationEnforcementTests
{
    private static readonly IReadOnlyList<AclEntry> SeatAcl = new[]
    {
        new AclEntry { Type = "user", Value = "alice@contoso.com" },
        new AclEntry { Type = "user", Value = "bob@contoso.com" },
    };

    private static FeedRecord Person(string id) => new()
    {
        Dataset = Datasets.PersonProfile,
        Fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = id, ["person_name"] = "Ada Lovelace", ["net_worth_usd"] = "9000000",
        },
    };

    private static FeedRecord Org(string id) => new()
    {
        Dataset = Datasets.Organization,
        Fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["org_id"] = id, ["organization_name"] = "Acme Capital",
        },
    };

    [Fact]
    public void AdvisoryByDefaultLeavesTheSeatAclUntouched()
    {
        // Classification ON, enforcement OFF (default): the label is stamped but
        // it is advisory — the ACL is unchanged (same seat ACL reference).
        var transformer = new ItemTransformer(
            classification: new ClassificationOptions { Enabled = true });
        var item = transformer.Transform(Person("P1"), SeatAcl);

        Assert.Same(SeatAcl, item.Acl);
        Assert.Equal("Restricted", item.Properties[ItemTransformer.AdvisorySensitivityProp]);
        // Wire back-compat: the shipped Graph schema property name is unchanged.
        Assert.Equal("advisorySensitivity", ItemTransformer.AdvisorySensitivityProp);
    }

    [Fact]
    public void EnforceAclRestrictsRestrictedItemsToTheGroup()
    {
        var transformer = new ItemTransformer(classification: new ClassificationOptions
        {
            Enabled = true,
            EnforceAcl = true,
            EnforceGroupId = "11111111-2222-3333-4444-555555555555",
        });
        var item = transformer.Transform(Person("P1"), SeatAcl);

        Assert.Equal("Restricted", item.Properties[ItemTransformer.AdvisorySensitivityProp]);
        var acl = Assert.Single(item.Acl);
        Assert.Equal("group", acl.Type);
        Assert.Equal("11111111-2222-3333-4444-555555555555", acl.Value);
        // Never an everyone-grant.
        Assert.DoesNotContain(item.Acl, e => e.Type is "everyone" or "everyoneExceptGuests");
    }

    [Fact]
    public void EnforceAclLeavesNonTopTierItemsOnTheSeatAcl()
    {
        var transformer = new ItemTransformer(classification: new ClassificationOptions
        {
            Enabled = true,
            EnforceAcl = true,
            EnforceGroupId = "11111111-2222-3333-4444-555555555555",
        });
        var item = transformer.Transform(Org("O1"), SeatAcl);

        // Organization baseline is Confidential (not top tier) → seat ACL intact.
        Assert.Equal("Confidential", item.Properties[ItemTransformer.AdvisorySensitivityProp]);
        Assert.Same(SeatAcl, item.Acl);
    }

    [Fact]
    public void EnforceAclWithoutGroupDoesNotRestrict()
    {
        // Group unset → nothing to lock to; ACL stays seat-only (defensive; the
        // config layer also rejects this combination at load time).
        var transformer = new ItemTransformer(classification: new ClassificationOptions
        {
            Enabled = true, EnforceAcl = true, EnforceGroupId = null,
        });
        var item = transformer.Transform(Person("P1"), SeatAcl);
        Assert.Same(SeatAcl, item.Acl);
    }
}

// ============================================================================
// #8 — stale-index expiry (GRAPH_ITEM_TTL_DAYS → expirationDateTime)
// ============================================================================

public class ItemTtlTests
{
    private static readonly IReadOnlyList<AclEntry> SeatAcl =
        new[] { new AclEntry { Type = "user", Value = "alice@contoso.com" } };

    private static FeedRecord Person(string id) => new()
    {
        Dataset = Datasets.PersonProfile,
        Fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["id"] = id },
    };

    [Fact]
    public void ExpirationStampedWhenTtlConfigured()
    {
        var now = new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc);
        var transformer = new ItemTransformer(ttlDays: 30, clock: () => now);
        var item = transformer.Transform(Person("P1"), SeatAcl);
        Assert.Equal(now.AddDays(30), item.ExpirationDateTime);
    }

    [Fact]
    public void ExpirationAbsentWhenTtlUnset()
    {
        var item = new ItemTransformer().Transform(Person("P1"), SeatAcl);
        Assert.Null(item.ExpirationDateTime);
    }

    [Fact]
    public void NonPositiveTtlIsTreatedAsOff()
    {
        var item = new ItemTransformer(ttlDays: 0).Transform(Person("P1"), SeatAcl);
        Assert.Null(item.ExpirationDateTime);
    }
}

// ============================================================================
// #11 — immutable decision ledger (exclusion / ACL-restriction), hash-chained
// ============================================================================

public class DecisionLedgerTests
{
    private static DecisionLedger NewLedger() =>
        new("AltrataTest", logsDir: Path.Combine(TestFixtures.NewTempDir("decision"), "logs"));

    [Fact]
    public void EntriesChainAndVerify()
    {
        var ledger = NewLedger();
        var e1 = ledger.RecordExclusion("d1", "delivery rejected: checksum mismatch");
        var e2 = ledger.RecordAclRestriction("PersonProfile-P1", "Restricted → group G");

        Assert.Equal(1, e1.Seq);
        Assert.Equal(DecisionLedger.GenesisHash, e1.PrevHash);
        Assert.Equal(DecisionActions.Exclude, e1.Decision);
        Assert.Equal(2, e2.Seq);
        Assert.Equal(DecisionActions.RestrictAcl, e2.Decision);
        Assert.Equal(e1.Hash, e2.PrevHash);        // chained
        Assert.True(ledger.Verify(out var broken));
        Assert.Equal(0, broken);
    }

    [Fact]
    public void TamperingWithAnEntryBreaksTheChain()
    {
        var ledger = NewLedger();
        ledger.RecordExclusion("d1", "rejected");
        ledger.RecordAclRestriction("PersonProfile-P1", "Restricted → group G");

        var lines = File.ReadAllLines(ledger.Path);
        lines[0] = lines[0].Replace("\"Decision\":\"exclude\"", "\"Decision\":\"acl-restrict\"");
        File.WriteAllLines(ledger.Path, lines);

        Assert.False(ledger.Verify(out var broken));
        Assert.Equal(1, broken);
    }

    [Fact]
    public void DeletingAnEntryBreaksTheChain()
    {
        var ledger = NewLedger();
        ledger.RecordExclusion("d1", "r");
        ledger.RecordExclusion("d2", "r");
        ledger.RecordExclusion("d3", "r");

        var lines = File.ReadAllLines(ledger.Path).ToList();
        lines.RemoveAt(1);
        File.WriteAllLines(ledger.Path, lines);

        Assert.False(ledger.Verify(out var broken));
        Assert.Equal(3, broken);
    }

    [Fact]
    public void AppendIsAdditiveAcrossInstances()
    {
        var dir = Path.Combine(TestFixtures.NewTempDir("decision2"), "logs");
        new DecisionLedger("AltrataTest", dir).RecordExclusion("d1", "r");
        var second = new DecisionLedger("AltrataTest", dir);
        second.RecordAclRestriction("PersonProfile-P1", "r");
        Assert.Equal(2, second.ReadAll().Count);
        Assert.True(second.Verify(out _));
    }

    [Fact]
    public void VerifySetsAndClearsTheBrokenGauge()
    {
        var logs = TestFixtures.NewTempDir("decision_gauge");
        var ledger = new DecisionLedger("GaugeTest", logs);
        ledger.RecordExclusion("d1", "rejected");
        Assert.True(ledger.Verify(out _));
        Assert.Equal(0, Metrics.Get("altrata_decision_ledger_broken"));

        var text = File.ReadAllText(ledger.Path).Replace("\"ItemId\":\"d1\"", "\"ItemId\":\"d2\"");
        File.WriteAllText(ledger.Path, text);
        var fresh = new DecisionLedger("GaugeTest", logs);
        Assert.False(fresh.Verify(out var brokenAt));
        Assert.True(brokenAt > 0);
        Assert.Equal(1, Metrics.Get("altrata_decision_ledger_broken"));
    }

    [Fact]
    public void DecisionLedgerMetricIsRegistered()
    {
        Assert.Contains("# TYPE altrata_decision_ledger_broken", Metrics.RenderPrometheus());
    }
}

// ============================================================================
// #5 / #11 — crawl-level behaviour (entitlement freshness + exclusion audit)
// ============================================================================

public class EntitlementFreshnessCrawlTests
{
    [Fact]
    public async Task IncrementalCrawlDefersReAclWhenSyncDisabled()
    {
        using var harness = new CrawlHarness(
            configure: c => c with { IdentitySyncOnIncremental = false });
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P1", "A", null, null)), 1));

        await harness.Engine.RunAsync(CrawlKind.Full);
        var originalHash = harness.State.GetValue(StateKeys.SeatListHash);

        // Seat change + new delivery, then an INCREMENTAL crawl.
        harness.WriteSeats("alice@contoso.com", "bob@contoso.com", "carol@contoso.com");
        TestFixtures.WriteDelivery(harness.FeedPath, "d2",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P2", "B", null, null)), 1));
        await harness.Engine.RunAsync(CrawlKind.Incremental);

        // Deferred: no re-ACL sweep, hash NOT advanced.
        Assert.Empty(harness.Graph.AclUpdates);
        Assert.Equal(originalHash, harness.State.GetValue(StateKeys.SeatListHash));

        // A FULL crawl performs the deferred sweep and commits.
        await harness.Engine.RunAsync(CrawlKind.Full);
        Assert.Contains(harness.Graph.AclUpdates, u => u.ItemId == "PersonProfile-P1");
        Assert.NotEqual(originalHash, harness.State.GetValue(StateKeys.SeatListHash));
    }

    [Fact]
    public async Task DeliveryRejectionIsRecordedInTheDecisionLedger()
    {
        using var harness = new CrawlHarness();
        var decisions = new DecisionLedger(harness.Config.ConnectorId,
            logsDir: Path.Combine(harness.Root, "logs"));
        var engine = new CrawlEngine(harness.Config, harness.Graph, harness.State,
            harness.Identity, harness.Seats, harness.Alerts, ha: null, decisions: decisions);

        var delivery = TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P1", "A", null, null)), 1));
        File.AppendAllText(Path.Combine(delivery.Directory, "persons.json"), "tampered");

        var result = await engine.RunAsync(CrawlKind.Full);

        Assert.Equal(1, result.DeliveriesRejected);
        var entries = decisions.ReadAll();
        var exclusion = Assert.Single(entries);
        Assert.Equal("d1", exclusion.ItemId);
        Assert.Equal(DecisionActions.Exclude, exclusion.Decision);
        Assert.True(decisions.Verify(out _));
    }
}

// ============================================================================
// config knobs — new env vars + pinned default flip (#5)
// ============================================================================

public class BankGradeConfigTests : IDisposable
{
    public BankGradeConfigTests()
    {
        foreach (var (k, v) in new[]
                 {
                     ("CONNECTOR_ID", "AltrataBgTest"), ("CONNECTOR_NAME", "t"),
                     ("CONNECTOR_DESCRIPTION", "t"), ("AAD_APP_CLIENT_ID", "c"),
                     ("AAD_APP_TENANT_ID", "t"), ("SECRET_AAD_APP_CLIENT_SECRET", "s"),
                 })
            Environment.SetEnvironmentVariable(k, v);
    }

    public void Dispose()
    {
        foreach (var k in new[]
                 {
                     "CONNECTOR_ID", "CONNECTOR_NAME", "CONNECTOR_DESCRIPTION", "AAD_APP_CLIENT_ID",
                     "AAD_APP_TENANT_ID", "SECRET_AAD_APP_CLIENT_SECRET",
                     "IDENTITY_SYNC_ON_INCREMENTAL", "GRAPH_ITEM_TTL_DAYS",
                     "CLASSIFICATION", "CLASSIFICATION_ENFORCE_ACL", "CLASSIFICATION_ENFORCE_GROUP_ID",
                 })
            Environment.SetEnvironmentVariable(k, null);
    }

    [Fact]
    public void IdentitySyncOnIncrementalDefaultsTrue()
    {
        Assert.True(AppConfig.Load().IdentitySyncOnIncremental);       // via Load
        Assert.True(TestFixtures.NewConfig().IdentitySyncOnIncremental); // record default
    }

    [Fact]
    public void IdentitySyncOnIncrementalCanBeDisabled()
    {
        Environment.SetEnvironmentVariable("IDENTITY_SYNC_ON_INCREMENTAL", "false");
        Assert.False(AppConfig.Load().IdentitySyncOnIncremental);
    }

    [Fact]
    public void ItemTtlDaysDefaultsOffAndParses()
    {
        Assert.Equal(0, AppConfig.Load().GraphItemTtlDays);
        Environment.SetEnvironmentVariable("GRAPH_ITEM_TTL_DAYS", "45");
        Assert.Equal(45, AppConfig.Load().GraphItemTtlDays);
    }

    [Fact]
    public void EnforceAclRequiresGroupId()
    {
        Environment.SetEnvironmentVariable("CLASSIFICATION", "true");
        Environment.SetEnvironmentVariable("CLASSIFICATION_ENFORCE_ACL", "true");
        var exc = Assert.Throws<ConfigurationError>(() => AppConfig.Load());
        Assert.Contains("CLASSIFICATION_ENFORCE_GROUP_ID", exc.Message);
    }

    [Fact]
    public void EnforceAclRequiresClassificationEnabled()
    {
        Environment.SetEnvironmentVariable("CLASSIFICATION", "false");
        Environment.SetEnvironmentVariable("CLASSIFICATION_ENFORCE_ACL", "true");
        Environment.SetEnvironmentVariable("CLASSIFICATION_ENFORCE_GROUP_ID", "g1");
        var exc = Assert.Throws<ConfigurationError>(() => AppConfig.Load());
        Assert.Contains("CLASSIFICATION=true", exc.Message);
    }

    [Fact]
    public void EnforceAclConfiguredCorrectlyLoads()
    {
        Environment.SetEnvironmentVariable("CLASSIFICATION", "true");
        Environment.SetEnvironmentVariable("CLASSIFICATION_ENFORCE_ACL", "true");
        Environment.SetEnvironmentVariable("CLASSIFICATION_ENFORCE_GROUP_ID",
            "11111111-2222-3333-4444-555555555555");
        var config = AppConfig.Load();
        Assert.True(config.ClassificationEnforceAcl);
        Assert.Equal("11111111-2222-3333-4444-555555555555", config.ClassificationEnforceGroupId);
    }
}
