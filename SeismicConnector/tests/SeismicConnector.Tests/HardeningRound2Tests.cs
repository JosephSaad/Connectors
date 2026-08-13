// Bank-grade hardening round 2 — focused coverage for:
//   #3  restrictive filesystem permissions (owner-only state dirs)
//   #5  entitlement freshness (incremental identity sync default)
//   #6b classification-enforced ACL (advisory by default, enforce path)
//   #8  stale-index expiry (GRAPH_ITEM_TTL_DAYS → expirationDateTime)
//   #10 webhook anti-replay (fresh / stale / replay / missing-timestamp)
//   #11 immutable hash-chained decision ledger (chain links, Verify tamper)

using System.Text;
using System.Text.Json.Nodes;
using SeismicConnector.Config;
using SeismicConnector.Graph;
using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector.Tests;

// ── #3  Restrictive filesystem permissions ───────────────────────────────────

public class SecureDirectoryTests
{
    [Fact]
    public void EnsureHardened_CreatesDirectory_OwnerOnlyOnPosix()
    {
        var dir = Path.Combine(Path.GetTempPath(), "seismic-secdir-" + Guid.NewGuid().ToString("N"));
        try
        {
            SecureDirectory.EnsureHardened(dir);
            Assert.True(Directory.Exists(dir));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(SecureDirectory.OwnerOnly, File.GetUnixFileMode(dir));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EnsureHardened_TightensAnExistingLooseDirectory()
    {
        if (OperatingSystem.IsWindows())
            return;  // POSIX-mode assertion only
        var dir = Path.Combine(Path.GetTempPath(), "seismic-secdir-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.SetUnixFileMode(dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);  // 0755

            SecureDirectory.EnsureHardened(dir);           // must tighten
            SecureDirectory.EnsureHardened(dir);           // idempotent

            Assert.Equal(SecureDirectory.OwnerOnly, File.GetUnixFileMode(dir));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}

// ── #5  Entitlement freshness: incremental identity sync default ──────────────

public class IncrementalIdentitySyncDefaultTests : IDisposable
{
    public IncrementalIdentitySyncDefaultTests() =>
        Environment.SetEnvironmentVariable("IDENTITY_SYNC_ON_INCREMENTAL", null);

    public void Dispose() =>
        Environment.SetEnvironmentVariable("IDENTITY_SYNC_ON_INCREMENTAL", null);

    [Fact]
    public void IdentitySyncOnIncremental_DefaultsTrue_WhenUnset()
    {
        Environment.SetEnvironmentVariable("IDENTITY_SYNC_ON_INCREMENTAL", null);
        Assert.True(EnvFlags.IdentitySyncOnIncremental);
    }

    [Fact]
    public void IdentitySyncOnIncremental_CanBeDisabledExplicitly()
    {
        Environment.SetEnvironmentVariable("IDENTITY_SYNC_ON_INCREMENTAL", "false");
        Assert.False(EnvFlags.IdentitySyncOnIncremental);
    }
}

// ── #8  Stale-index expiry (TTL → expirationDateTime) ─────────────────────────

public class ItemTtlTests
{
    private static AclResult Acl() =>
        new(new[] { AclEntry.GrantUser("entra-user-1") }, 0, Skipped: false);

    [Fact]
    public void NoTtl_OmitsExpirationDateTime()
    {
        var item = new ItemTransformer().Transform(TestContent.Make("c1"), null, null, Acl());
        Assert.Null(item["expirationDateTime"]);
    }

    [Fact]
    public void TtlConfigured_StampsExpirationRoughlyNowPlusTtl()
    {
        var before = DateTime.UtcNow;
        var item = new ItemTransformer(ttlDays: 30).Transform(TestContent.Make("c1"), null, null, Acl());

        var stampNode = item["expirationDateTime"];
        Assert.NotNull(stampNode);
        var stamp = DateTimeOffset.Parse(
            stampNode!.GetValue<string>(),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

        var expected = before.AddDays(30);
        Assert.True((stamp.UtcDateTime - expected).Duration() < TimeSpan.FromMinutes(5),
            $"expiration {stamp:o} not ~ now+30d ({expected:o})");
    }
}

// ── #6b  Classification-enforced ACL ──────────────────────────────────────────

public class ClassificationEnforceAclTests
{
    // A body that classifies as Restricted (PII.Email escalates to top tier).
    private const string RestrictedBody = "Please contact bob@example.com about the deal.";

    [Fact]
    public async Task Default_ClassificationIsAdvisoryOnly_AclUnchanged()
    {
        using var harness = new PipelineHarness(classification: true /* enforce OFF */);
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1"));
        harness.Seismic.Payloads["c1"] = Encoding.UTF8.GetBytes(RestrictedBody);

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        var put = harness.LastPutBody("c1")!;
        Assert.Equal("Restricted", put["properties"]!["advisorySensitivity"]!.GetValue<string>());

        // ACL is the resolved user grant — NOT restricted to any group.
        var acl = put["acl"]!.AsArray();
        Assert.Contains(acl, e => e!["type"]!.GetValue<string>() == "user"
            && e["value"]!.GetValue<string>() == "entra-user-1");
        Assert.DoesNotContain(acl, e => e!["type"]!.GetValue<string>() == "group");

        // No restriction decision recorded.
        Assert.DoesNotContain(harness.Pipeline.Ledger.Entries,
            e => e.Decision == DecisionLedger.DecisionAclRestrict);
    }

    [Fact]
    public async Task EnforceOn_RestrictedItem_AclLockedToGroup_AndLedgered()
    {
        using var harness = new PipelineHarness(
            classification: true,
            classificationEnforceAcl: true,
            classificationEnforceGroup: "entra-restricted-group");
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1"));
        harness.Seismic.Payloads["c1"] = Encoding.UTF8.GetBytes(RestrictedBody);

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        var put = harness.LastPutBody("c1")!;
        Assert.Equal("Restricted", put["properties"]!["advisorySensitivity"]!.GetValue<string>());

        // ACL is now a single group grant to the enforcement group.
        var acl = put["acl"]!.AsArray();
        Assert.Single(acl);
        Assert.Equal("group", acl[0]!["type"]!.GetValue<string>());
        Assert.Equal("entra-restricted-group", acl[0]!["value"]!.GetValue<string>());
        Assert.Equal("grant", acl[0]!["accessType"]!.GetValue<string>());

        // The restriction decision is in the immutable ledger.
        Assert.Contains(harness.Pipeline.Ledger.Entries,
            e => e.Decision == DecisionLedger.DecisionAclRestrict && e.ItemId == "c1");
    }

    [Fact]
    public async Task EnforceOn_NonRestrictedItem_AclUnchanged()
    {
        using var harness = new PipelineHarness(
            classification: true,
            classificationEnforceAcl: true,
            classificationEnforceGroup: "entra-restricted-group");
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1"));
        harness.Seismic.Payloads["c1"] = Encoding.UTF8.GetBytes("Nothing sensitive in this deck.");

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        var put = harness.LastPutBody("c1")!;
        Assert.NotEqual("Restricted", put["properties"]!["advisorySensitivity"]!.GetValue<string>());
        var acl = put["acl"]!.AsArray();
        Assert.DoesNotContain(acl, e => e!["type"]!.GetValue<string>() == "group"
            && e["value"]!.GetValue<string>() == "entra-restricted-group");
    }
}

// ── #6b  Classification lock survives the re-ACL sweep ─────────────────────────
// Regression coverage for the verified gap: a Restricted item locked to the
// enforcement group at ingest must NOT be re-widened to its resolved source
// principals by PERMISSION_REACL / the `reacl` command on a source-permission
// drift. The lock is persisted on the tracked item and honoured by the re-ACL
// path, which always targets the enforcement group and never the source ACL.
public class ClassificationLockReAclTests
{
    private const string RestrictedBody = "Please contact bob@example.com about the deal.";
    private const string Group = "entra-restricted-group";

    private static List<SeismicPermission> Perms(params string[] principalIds) =>
        principalIds.Select(id => new SeismicPermission { PrincipalId = id, PrincipalType = "user" }).ToList();

    private static PipelineHarness LockedHarness(bool permissionReacl)
    {
        var harness = new PipelineHarness(
            permissionReacl: permissionReacl,
            classification: true,
            classificationEnforceAcl: true,
            classificationEnforceGroup: Group);
        // A second mappable principal so the SOURCE ACL can widen.
        harness.Store.UpsertPrincipal(
            new PrincipalMapping("seismic-user-2", "user", "bob@contoso.com", "entra-user-2", "Bob"));
        return harness;
    }

    /// <summary>Ingest a Restricted item locked to the group and return the harness.</summary>
    private static async Task IngestLocked(PipelineHarness harness)
    {
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", versionId: "v1", permissions: Perms("seismic-user-1")));
        harness.Seismic.Payloads["c1"] = Encoding.UTF8.GetBytes(RestrictedBody);
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        var tracked = harness.Store.GetTrackedItem("c1")!;
        Assert.True(tracked.ClassificationLocked);          // persisted lock
        Assert.NotNull(tracked.AclFingerprint);             // = group fingerprint
    }

    /// <summary>Drift the source permissions (widen) WITHOUT changing the version.</summary>
    private static void DriftSourcePermissions(PipelineHarness harness)
    {
        harness.Seismic.ContentsByTeamsite["ts1"].Clear();
        harness.AddContent(TestContent.Make("c1", versionId: "v1",
            permissions: Perms("seismic-user-1", "seismic-user-2")));
        harness.Seismic.Payloads["c1"] = Encoding.UTF8.GetBytes(RestrictedBody);
    }

    [Fact]
    public async Task PermissionReacl_DoesNotWiden_ALockedRestrictedItem()
    {
        using var harness = LockedHarness(permissionReacl: true);
        await IngestLocked(harness);
        var lockedFp = harness.Store.GetTrackedItem("c1")!.AclFingerprint;

        DriftSourcePermissions(harness);
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        // The lock holds: no widening PATCH, no drift, ACL never re-sent to source.
        Assert.DoesNotContain("c1", harness.AclPatchedItemIds);
        Assert.Equal(0, harness.Pipeline.Stats.AclDrift);
        Assert.Equal(0, harness.Pipeline.Stats.ReAcled);
        var tracked = harness.Store.GetTrackedItem("c1")!;
        Assert.True(tracked.ClassificationLocked);
        Assert.Equal(lockedFp, tracked.AclFingerprint);     // still the group fingerprint
        // The content was PUT exactly once (crawl 1); no source-principal re-widen.
        Assert.Equal(1, harness.PutItemIds().Count(id => id == "c1"));
    }

    [Fact]
    public async Task ReAclSweep_DoesNotWiden_ALockedRestrictedItem()
    {
        using var harness = LockedHarness(permissionReacl: false);
        await IngestLocked(harness);
        DriftSourcePermissions(harness);

        var sweep = new ReAclSweep(harness.Config, harness.Seismic, harness.Store, harness.Pipeline);
        var summary = await sweep.RunAsync(dryRun: false);

        // The documented `reacl` command must also honour the lock.
        Assert.Equal(0, summary.ReAcledCount);
        Assert.DoesNotContain("c1", harness.AclPatchedItemIds);
        Assert.True(harness.Store.GetTrackedItem("c1")!.ClassificationLocked);
    }

    [Fact]
    public async Task GroupReconfigured_RelocksToNewGroup_NeverWidensToSource()
    {
        using var harness = LockedHarness(permissionReacl: true);
        await IngestLocked(harness);

        // Operator points enforcement at a DIFFERENT group; source also drifts.
        DriftSourcePermissions(harness);
        var config = TestConfig.Build(
            permissionReacl: true, classification: true,
            classificationEnforceAcl: true, classificationEnforceGroup: "entra-new-group");
        var pipeline = new IngestPipeline(config, harness.Seismic, harness.Graph, harness.Store);
        Assert.True(await pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        // Re-locked to the NEW group only — never to the source principals.
        Assert.Contains("c1", harness.AclPatchedItemIds);
        var patch = harness.AclPatches["c1"]!.AsArray();
        Assert.Single(patch);
        Assert.Equal("group", patch[0]!["type"]!.GetValue<string>());
        Assert.Equal("entra-new-group", patch[0]!["value"]!.GetValue<string>());
        Assert.True(harness.Store.GetTrackedItem("c1")!.ClassificationLocked);
    }

    [Fact]
    public async Task EnforcementDisabled_RevertsToSourceAcl_AndClearsLock()
    {
        using var harness = LockedHarness(permissionReacl: true);
        await IngestLocked(harness);
        DriftSourcePermissions(harness);

        // Operator turns enforcement OFF: the item returns to its source ACL.
        var config = TestConfig.Build(
            permissionReacl: true, classification: true, classificationEnforceAcl: false);
        var pipeline = new IngestPipeline(config, harness.Seismic, harness.Graph, harness.Store);
        Assert.True(await pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        Assert.Contains("c1", harness.AclPatchedItemIds);
        var values = harness.AclPatches["c1"]!.AsArray()
            .Select(e => e!["value"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("entra-user-1", values);
        Assert.Contains("entra-user-2", values);            // now the resolved source set
        Assert.False(harness.Store.GetTrackedItem("c1")!.ClassificationLocked);
    }
}

// ── #10  Webhook anti-replay ──────────────────────────────────────────────────

public class WebhookAntiReplayTests
{
    private const string Secret = "s3cr3t-shared-key";

    private static byte[] Body() =>
        Encoding.UTF8.GetBytes("""{"type":"contentPublished","contentId":"c1"}""");

    private static WebhookAntiReplay Build(bool requireTimestamp, DateTimeOffset now) =>
        new(new SignatureValidator(Secret),
            TimeSpan.FromMinutes(5),
            requireTimestamp,
            clock: () => now);

    [Fact]
    public void FromEnv_DefaultsToRequireTimestamp()
    {
        Environment.SetEnvironmentVariable(WebhookAntiReplay.RequireTimestampEnvVar, null);
        var ar = WebhookAntiReplay.FromEnv(new SignatureValidator(Secret));
        Assert.True(ar.RequireTimestamp);
        Assert.Equal(WebhookAntiReplay.DefaultWindowSeconds, (int)ar.Window.TotalSeconds);
    }

    [Fact]
    public void FreshSignedTimestamp_Accepted()
    {
        var now = DateTimeOffset.UtcNow;
        var ar = Build(requireTimestamp: true, now);
        var body = Body();
        var ts = now.ToUnixTimeSeconds().ToString();
        var sig = new SignatureValidator(Secret).ComputeHex(ts, body);

        Assert.Equal(WebhookVerdict.Accepted, ar.Check(body, ts, sig));
    }

    [Fact]
    public void StaleTimestamp_Rejected()
    {
        var now = DateTimeOffset.UtcNow;
        var ar = Build(requireTimestamp: true, now);
        var body = Body();
        var staleTs = (now.ToUnixTimeSeconds() - 600).ToString();   // 10 min old, window 5 min
        var sig = new SignatureValidator(Secret).ComputeHex(staleTs, body);

        Assert.Equal(WebhookVerdict.StaleTimestamp, ar.Check(body, staleTs, sig));
    }

    [Fact]
    public void Replay_WithinWindow_Rejected()
    {
        var now = DateTimeOffset.UtcNow;
        var ar = Build(requireTimestamp: true, now);
        var body = Body();
        var ts = now.ToUnixTimeSeconds().ToString();
        var sig = new SignatureValidator(Secret).ComputeHex(ts, body);

        Assert.Equal(WebhookVerdict.Accepted, ar.Check(body, ts, sig));       // first time
        Assert.Equal(WebhookVerdict.ReplayDetected, ar.Check(body, ts, sig)); // identical replay
    }

    [Fact]
    public void MissingTimestamp_RequireOn_Rejected()
    {
        var now = DateTimeOffset.UtcNow;
        var ar = Build(requireTimestamp: true, now);
        var body = Body();
        var bodyOnlySig = new SignatureValidator(Secret).ComputeHex(body);

        Assert.Equal(WebhookVerdict.MissingTimestamp, ar.Check(body, timestampHeader: null, bodyOnlySig));
    }

    [Fact]
    public void MissingTimestamp_RequireOff_FallsBackToBodyHmac()
    {
        var now = DateTimeOffset.UtcNow;
        var ar = Build(requireTimestamp: false, now);
        var body = Body();
        var bodyOnlySig = new SignatureValidator(Secret).ComputeHex(body);

        Assert.Equal(WebhookVerdict.Accepted, ar.Check(body, timestampHeader: null, bodyOnlySig));
        // A bad body-only signature is still rejected in migration mode.
        Assert.Equal(WebhookVerdict.InvalidSignature, ar.Check(body, null, "sha256=deadbeef"));
    }

    [Fact]
    public void InvalidSignatureOverTimestampedPayload_Rejected()
    {
        var now = DateTimeOffset.UtcNow;
        var ar = Build(requireTimestamp: true, now);
        var body = Body();
        var ts = now.ToUnixTimeSeconds().ToString();
        // Signature computed over body ONLY (not timestamp.body) → invalid here.
        var wrong = new SignatureValidator(Secret).ComputeHex(body);

        Assert.Equal(WebhookVerdict.InvalidSignature, ar.Check(body, ts, wrong));
    }
}

// ── #11  Immutable hash-chained decision ledger ───────────────────────────────

public class DecisionLedgerTests
{
    [Fact]
    public void Chain_LinksEachEntryToThePrevious()
    {
        var ledger = new DecisionLedger();
        var e0 = ledger.Append("c1", DecisionLedger.DecisionExclude, "mne-flag: flagged MNE");
        var e1 = ledger.Append("c2", DecisionLedger.DecisionAclRestrict, "classification=Restricted");
        var e2 = ledger.Append("c3", DecisionLedger.DecisionExclude, "restricted-library");

        Assert.Equal(0, e0.Seq);
        Assert.Equal(DecisionLedger.GenesisHash, e0.PrevHash);
        Assert.Equal(e0.Hash, e1.PrevHash);
        Assert.Equal(e1.Hash, e2.PrevHash);
        Assert.True(ledger.Verify().Valid);
    }

    [Fact]
    public void Verify_DetectsTamperedReason()
    {
        var ledger = new DecisionLedger();
        ledger.Append("c1", DecisionLedger.DecisionExclude, "mne-flag");
        ledger.Append("c2", DecisionLedger.DecisionExclude, "restricted-library");

        var entries = ledger.Entries.ToList();
        entries[0] = entries[0] with { Reason = "TAMPERED" };  // rewrite history

        var v = DecisionLedger.Verify(entries);
        Assert.False(v.Valid);
        Assert.Equal(0, v.FirstBrokenSeq);
    }

    [Fact]
    public void Verify_DetectsDeletedEntry()
    {
        var ledger = new DecisionLedger();
        ledger.Append("c1", DecisionLedger.DecisionExclude, "r1");
        ledger.Append("c2", DecisionLedger.DecisionExclude, "r2");
        ledger.Append("c3", DecisionLedger.DecisionExclude, "r3");

        var entries = ledger.Entries.ToList();
        entries.RemoveAt(1);  // drop the middle record

        Assert.False(DecisionLedger.Verify(entries).Valid);
    }

    [Fact]
    public void Verify_DetectsReorderedEntries()
    {
        var ledger = new DecisionLedger();
        ledger.Append("c1", DecisionLedger.DecisionExclude, "r1");
        ledger.Append("c2", DecisionLedger.DecisionExclude, "r2");

        var entries = ledger.Entries.ToList();
        (entries[0], entries[1]) = (entries[1], entries[0]);

        Assert.False(DecisionLedger.Verify(entries).Valid);
    }

    [Fact]
    public void FileBacked_RoundTrips_AndVerifies()
    {
        var path = Path.Combine(Path.GetTempPath(), "ledger-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            using (var ledger = new DecisionLedger(path))
            {
                ledger.Append("c1", DecisionLedger.DecisionExclude, "mne-flag");
                ledger.Append("c2", DecisionLedger.DecisionAclRestrict, "Restricted");
            }

            var reread = DecisionLedger.ReadFile(path);
            Assert.Equal(2, reread.Count);
            Assert.True(DecisionLedger.Verify(reread).Valid);

            // A tampered persisted line is caught on re-verification.
            reread[1] = reread[1] with { ItemId = "cX" };
            Assert.False(DecisionLedger.Verify(reread).Valid);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task Pipeline_RecordsExclusionDecisions()
    {
        using var harness = new PipelineHarness(exclusions: new ExclusionRules
        {
            ExcludedFlags = new List<string> { "MNE" },
            FlagProperties = new List<string> { "classification" },
        });
        harness.AddTeamsite("ts1");
        harness.AddContent(TestContent.Make("c1", properties: new List<SeismicProperty>
        {
            new() { Name = "classification", Value = "MNE" },
        }));
        harness.AddContent(TestContent.Make("c2"));  // clean

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        var excluded = harness.Pipeline.Ledger.Entries
            .Where(e => e.Decision == DecisionLedger.DecisionExclude).ToList();
        Assert.Contains(excluded, e => e.ItemId == "c1");
        Assert.DoesNotContain(excluded, e => e.ItemId == "c2");
        Assert.True(harness.Pipeline.Ledger.Verify().Valid);
    }
}
