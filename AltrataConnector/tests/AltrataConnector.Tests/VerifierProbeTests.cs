// VerifierProbeTests.cs
// ---------------------
// ADVERSARIAL VERIFIER probes — independent confirmation of the hardening
// claims. These do NOT replace the implementer's tests; they attack the
// safe-default flips from angles the implementer's tests did not cover
// (production env-driven wiring, end-to-end crawl enforcement, state-dir
// hardening, realistic PII-safety of ledger reasons).

using System.Runtime.InteropServices;
using AltrataConnector.Altrata;
using AltrataConnector.Config;
using AltrataConnector.Entitlement;
using AltrataConnector.Graph;
using AltrataConnector.Infrastructure;
using AltrataConnector.Ingestion;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

// Serial: these toggle process-global env vars that the production default
// AltrataApiClient reads via PurposePolicy.FromEnv.
[Collection("verifier-probes-serial")]
public class VerifierPurposeEnvProbe
{
    [Fact]
    public async Task ProductionPathReadsAllowlistFromEnvAndDeniesFailClosed()
    {
        var previous = Environment.GetEnvironmentVariable(PurposePolicy.AllowlistEnvVar);
        var root = TestFixtures.NewTempDir("probe_purpose_env");
        try
        {
            Environment.SetEnvironmentVariable(PurposePolicy.AllowlistEnvVar, "RFP,KYC");
            var config = new AppConfig
            {
                ConnectorId = "AltrataProbe", ConnectorName = "t", ConnectorDescription = "t",
                AadClientId = "c", AadTenantId = "t", AadClientSecret = "s",
                AltrataApiBaseUrl = "https://api.altrata.test/v1",
                AltrataTokenUrl = "https://auth.altrata.test/oauth/token",
                AltrataClientId = "api-client", AltrataClientSecret = "api-secret",
            };
            var state = new FileStateStore("AltrataProbe",
                logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));
            var audit = new AuditLog("AltrataProbe", logsDir: Path.Combine(root, "logs"));
            var handler = new ScriptedHandler();   // NO responses enqueued
            // NOTE: purpose NOT injected — exercises PurposePolicy.FromEnv, the
            // real production wiring in CommandRegistry.ingest-item.
            var client = new AltrataApiClient(config, state, audit, handler,
                (_, _) => Task.CompletedTask);

            await Assert.ThrowsAsync<PurposeDeniedException>(
                () => client.LookupPersonAsync("P1", "mallory", "sell the list"));
            Assert.Equal(0, state.GetBillableLookupCount());   // never billed

            // And an allowlisted purpose proceeds through the same env-driven client.
            handler.EnqueueJson(200, """{"access_token":"tok","expires_in":3600}""");
            handler.EnqueueJson(200, """{"id":"P1","person_name":"Ada"}""");
            var rec = await client.LookupPersonAsync("P1", "joseph", "kyc");  // case-insensitive
            Assert.Equal("P1", rec.Id);
            Assert.Equal(1, state.GetBillableLookupCount());
        }
        finally
        {
            Environment.SetEnvironmentVariable(PurposePolicy.AllowlistEnvVar, previous);
        }
    }
}

// #3 — state directory hardening (not just logs).
[Collection("verifier-probes-serial")]
public class VerifierDirectoryHardeningProbe
{
    [Fact]
    public void HardenStartupDirectoriesTightensTheStateDir()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;   // POSIX-mode assertion only
        var root = TestFixtures.NewTempDir("probe_harden");
        var logs = Path.Combine(root, "logs");
        var data = Path.Combine(root, "data");
        var prevLogs = Environment.GetEnvironmentVariable("LOGS_DIR");
        var prevData = Environment.GetEnvironmentVariable("DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("LOGS_DIR", logs);
            Environment.SetEnvironmentVariable("DATA_DIR", data);
            DirectoryHardening.HardenStartupDirectories();
            Assert.True(Directory.Exists(logs));
            Assert.True(Directory.Exists(data));
            Assert.Equal(DirectoryHardening.OwnerOnly, File.GetUnixFileMode(logs));
            Assert.Equal(DirectoryHardening.OwnerOnly, File.GetUnixFileMode(data));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGS_DIR", prevLogs);
            Environment.SetEnvironmentVariable("DATA_DIR", prevData);
        }
    }
}

// #6b end-to-end — a full crawl with classification enforcement actually locks a
// Restricted item's ACL to the group AND ledgers the decision (PII-safe).
public class VerifierEnforceAclCrawlProbe
{
    [Fact]
    public async Task FullCrawlLocksRestrictedItemAclToGroupAndLedgersDecision()
    {
        using var harness = new CrawlHarness(configure: c => c with
        {
            Classification = true,
            ClassificationEnforceAcl = true,
            ClassificationEnforceGroupId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
        });
        var decisions = new DecisionLedger(harness.Config.ConnectorId,
            logsDir: Path.Combine(harness.Root, "logs"));
        var engine = new CrawlEngine(harness.Config, harness.Graph, harness.State,
            harness.Identity, harness.Seats, harness.Alerts, ha: null, decisions: decisions);

        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile,
                TestFixtures.PersonJson(("P1", "Ada Lovelace", null, null)), 1));

        var result = await engine.RunAsync(CrawlKind.Full);
        Assert.Equal(1, result.ItemsIngested);

        // The PUT that Graph received must carry the GROUP acl, not the seat acl,
        // and never an everyone-grant.
        var put = Assert.Single(harness.Graph.PutItems);
        var ace = Assert.Single(put.Acl);
        Assert.Equal("group", ace.Type);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", ace.Value);
        Assert.DoesNotContain(put.Acl, e => e.Type is "everyone" or "everyoneExceptGuests");

        // Decision ledger: one ACL-restriction summary decision, PII-safe, verifies.
        var entries = decisions.ReadAll();
        var restrict = Assert.Single(entries);
        Assert.Equal(DecisionActions.RestrictAcl, restrict.Decision);
        Assert.True(decisions.Verify(out _));
        var blob = File.ReadAllText(decisions.Path);
        Assert.DoesNotContain("Ada Lovelace", blob);   // no personal value leaked
    }
}

// #11 — decision-ledger reason strings stay PII-safe on a realistic rejection.
public class VerifierExclusionReasonPiiProbe
{
    [Fact]
    public async Task RejectedDeliveryLedgerReasonCarriesNoProfileValue()
    {
        using var harness = new CrawlHarness();
        var decisions = new DecisionLedger(harness.Config.ConnectorId,
            logsDir: Path.Combine(harness.Root, "logs"));
        var engine = new CrawlEngine(harness.Config, harness.Graph, harness.State,
            harness.Identity, harness.Seats, harness.Alerts, ha: null, decisions: decisions);

        var delivery = TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile,
                TestFixtures.PersonJson(("P1", "Ada Lovelace", null, null)), 1));
        // Corrupt the file AFTER the manifest is written → checksum mismatch.
        File.AppendAllText(Path.Combine(delivery.Directory, "persons.json"), "tampered-bytes");

        var result = await engine.RunAsync(CrawlKind.Full);
        Assert.Equal(1, result.DeliveriesRejected);

        var blob = File.ReadAllText(decisions.Path);
        Assert.DoesNotContain("Ada Lovelace", blob);   // reason is checksum/file only
        Assert.Contains("d1", blob);
    }
}
