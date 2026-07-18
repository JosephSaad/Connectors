// StressHarness2Tests.cs
// ----------------------
// Round-2 stress harness: new dimensions on top of StressHarnessTests.cs
// (round 1: HMAC flood, queue cap, ACL never-widens, identity-store gate,
// No-MNE at scale, breaker slots, dead-letter concurrency, $batch cap).
//
// Everything is in-process/offline (fakes, temp dirs) — no network, and no
// loopback listeners (so no membership in the "LoopbackWebhook" collection is
// needed; webhook load is driven through the queue/pipeline seams directly).
// Real measured numbers are appended to the shared StressLog.
//
// Round-2 dimensions:
//   1. Version-aware crawl at scale — 50k+ docs with version churn arriving
//      mid-crawl; exactly-one-latest-version, no stale-version resurrection
//      across a checkpoint pause/resume, measured versions/s + skip ratio.
//   2. Permission re-ACL fingerprint churn — flips across a large item set
//      re-ACL exactly the changed items (no misses, no storms).
//   3. No-MNE × version × ACL interaction — an item that becomes MNE-flagged
//      mid-crawl must not survive resume via a stale checkpoint.
//   4. HA lease contention + state concurrency — claim/steal/close storms on
//      the pure decision seams; multi-node SQLite failover; checkpoint storm.
//   5. Usage-ranking at scale — 120k analytics events folded deterministically
//      with bounded memory.
//   6. Long soak — sustained mixed crawl + webhook + re-ACL cycles at 3x
//      round-1 volume with flat managed memory (peak RSS captured).

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using SeismicConnector.Config;
using SeismicConnector.Graph;
using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector.Tests;

internal static class Stress2
{
    /// <summary>Replace a content item in the fake source, preserving list position.</summary>
    public static SeismicContent Bump(
        PipelineHarness harness, string teamsiteId, string id, string newVersion,
        List<SeismicProperty>? properties = null,
        List<SeismicPermission>? permissions = null)
    {
        var list = harness.Seismic.ContentsByTeamsite[teamsiteId];
        var index = list.FindIndex(c => c.Id == id);
        var old = list[index];
        var replaced = TestContent.Make(
            id, teamsiteId: teamsiteId, versionId: newVersion,
            properties: properties ?? old.Properties,
            permissions: permissions ?? old.Permissions,
            modifiedAt: DateTime.UtcNow);
        list[index] = replaced;
        harness.Seismic.Payloads[id] = System.Text.Encoding.UTF8.GetBytes($"payload {id} {newVersion}");
        return replaced;
    }

    /// <summary>All content-item PUTs (id, versionId) in wire order, parsed once.</summary>
    public static List<(string Id, string Version)> PutVersions(PipelineHarness harness) =>
        harness.GraphHandler.Requests
            .Where(r => r.Method == HttpMethod.Post && r.Url.Contains("/$batch"))
            .SelectMany(r => JsonNode.Parse(r.Body!)!["requests"]!.AsArray().Select(req => (
                Id: req!["id"]!.GetValue<string>(),
                Version: req["body"]!["properties"]!["versionId"]!.GetValue<string>())))
            .ToList();
}

// ── regression: checkpoint resume must reconcile, not reap ───────────────────
//
// DEFECT (round-2 discovery): a paused full crawl (graceful stop / degraded
// pause) resumes by index-skipping checkpoint-completed chunks WITHOUT
// touching their tracked items' last-seen. The withdrawal pass at the end of
// the resumed crawl then reaps every one of those items as "not-in-source" —
// wholesale index data loss for all completed work — and a version or MNE
// flag that landed during the pause is never reconciled.

public class Stress2_ResumeReconcile
{
    [Fact]
    public async Task PausedFullCrawl_ResumeDoesNotReapCompletedChunks_AndReconcilesChurn()
    {
        var rules = new ExclusionRules
        {
            ExcludedFlags = { "MNE" },
            FlagProperties = { "complianceFlag" },
        };
        using var harness = new PipelineHarness(
            exclusions: rules, objects: new[] { "ContentItem" });
        harness.AddTeamsite("ts1");
        for (var i = 0; i < 30; i++)  // chunk size 10 → 3 chunks
            harness.AddContent(TestContent.Make($"c{i:D2}", versionId: "v01"));

        // Degrade once chunk 1 (10 items) is PUT; a flag lets crawl 2 resume.
        var resumed = false;
        harness.Pipeline.CriticalBreakerOpenProbe =
            () => !resumed && harness.PutItemIds().Count >= 10;

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));
        Assert.Equal(10, harness.PutItemIds().Count);
        Assert.NotNull(SyncState.ReadCheckpoint(harness.Config.Connector.Id));

        // ── the world moves while the crawl is paused ────────────────────────
        await Task.Delay(15);
        // c03 (already ingested, chunk 1) publishes a NEW version.
        Stress2.Bump(harness, "ts1", "c03", "v02");
        // c05 (already ingested, chunk 1) gets an MNE compliance flag WITHOUT a
        // version bump — the goal-3 interaction case.
        Stress2.Bump(harness, "ts1", "c05", "v01",
            properties: new List<SeismicProperty> { new() { Name = "complianceFlag", Value = "MNE" } });
        await Task.Delay(15);

        // ── resume ───────────────────────────────────────────────────────────
        resumed = true;
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));

        // THE regression: not one completed-chunk item was reaped as
        // "not-in-source". The ONLY withdrawal is the late MNE flag.
        Assert.Equal(new[] { "c05" }, harness.DeletedItemIds);

        // MNE-mid-crawl (goal 3): the flagged item did NOT survive the stale
        // checkpoint — withdrawn and tracked as excluded.
        var c05 = harness.Store.GetTrackedItem("c05");
        Assert.NotNull(c05);
        Assert.Equal("excluded", c05!.Status);

        // Stale-version reconcile: c03's new version was re-ingested on resume.
        var puts = Stress2.PutVersions(harness);
        var c03Puts = puts.Where(p => p.Id == "c03").Select(p => p.Version).ToList();
        Assert.Equal(new[] { "v01", "v02" }, c03Puts);
        Assert.Equal("v02", harness.Store.GetTrackedItem("c03")!.VersionId);

        // Everything else survived exactly once, still tracked as ingested.
        foreach (var i in Enumerable.Range(0, 30).Where(i => i != 5))
        {
            var tracked = harness.Store.GetTrackedItem($"c{i:D2}");
            Assert.NotNull(tracked);
            Assert.Equal("ingested", tracked!.Status);
        }
        var putCounts = puts.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(30, putCounts.Count);
        Assert.All(putCounts, kv => Assert.Equal(kv.Key == "c03" ? 2 : 1, kv.Value));

        // Completed crawl: checkpoint cleared, sync stamped.
        Assert.Null(SyncState.ReadCheckpoint(harness.Config.Connector.Id));
        Assert.NotNull(SyncState.ReadLastSync(harness.Config.Connector.Id));

        StressLog.Record("RESUME-RECONCILE",
            $"items=30 paused_after=10 churned_versions=1 late_mne=1 " +
            $"wrongful_withdrawals=0 targeted_withdrawals={harness.DeletedItemIds.Count} " +
            $"stale_version_reingested=true");
    }
}

// ── regression: withdrawal pass scope under HA claim denial ──────────────────
//
// DEFECT (round-2 discovery): the full-crawl withdrawal pass reaped every
// tracked item whose last-seen predated this node's crawl start as long as
// its teamsite appeared in the listing — including teamsites whose claim this
// node did NOT hold (another HA node's scope, or a claim-blocked resource).
// The reaper must only reap items this crawl can vouch for: teamsites this
// node actually processed, or teamsites gone from the source entirely.

public class Stress2_HaWithdrawalScope
{
    /// <summary>In-memory coordinator: claim outcomes are scripted per resource.</summary>
    private sealed class FakeHa : HaCoordinator
    {
        public HashSet<string> DeniedResources { get; } = new(StringComparer.Ordinal);
        public List<string> Claimed { get; } = new();

        public FakeHa() : base("SeismicSales", "node-A") { }

        public override HaCrawlHandle OpenOrJoinCrawl(string crawlKind, string? sinceIso) =>
            new(Guid.NewGuid(), Created: true);

        public override bool TryClaim(Guid crawlId, string resource)
        {
            if (DeniedResources.Contains(resource))
                return false;
            Claimed.Add(resource);
            return true;
        }

        public override void Heartbeat(Guid crawlId, string resource) { }

        public override void CompleteClaim(Guid crawlId, string resource, bool succeeded) { }

        public override HaCloseResult TryCloseCrawl(Guid crawlId) => HaCloseResult.ClosedByThisNode;
    }

    [Fact]
    public async Task ClaimDeniedTeamsite_IsNeverReapedByThisNodesWithdrawalPass()
    {
        var ha = new FakeHa();
        using var harness = new PipelineHarness(objects: new[] { "ContentItem" }, ha: ha);
        harness.AddTeamsite("ts1");
        harness.AddTeamsite("ts2");
        for (var i = 0; i < 3; i++)
        {
            harness.AddContent(TestContent.Make($"a{i}", teamsiteId: "ts1"));
            harness.AddContent(TestContent.Make($"b{i}", teamsiteId: "ts2"));
        }

        // Crawl 1: this node claims everything — all 6 items tracked.
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));
        Assert.Equal(6, harness.PutItemIds().Distinct().Count());

        await Task.Delay(15);

        // Crawl 2: ANOTHER node holds ts2 (claim denied here). This node's
        // withdrawal pass must not touch ts2's items — their last-seen belongs
        // to whichever node owns the claim.
        ha.DeniedResources.Add("teamsite:ts2");
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));
        Assert.Empty(harness.DeletedItemIds);
        foreach (var id in new[] { "b0", "b1", "b2" })
        {
            var tracked = harness.Store.GetTrackedItem(id);
            Assert.NotNull(tracked);
            Assert.Equal("ingested", tracked!.Status);
        }

        await Task.Delay(15);

        // Crawl 3: legitimate reaping still works — a0 vanishes from ts1's
        // listing (processed teamsite), and the WHOLE ts2 teamsite vanishes
        // from Seismic (gone teamsite): both paths must reap.
        ha.DeniedResources.Clear();
        harness.Seismic.ContentsByTeamsite["ts1"].RemoveAll(c => c.Id == "a0");
        harness.Seismic.Teamsites.RemoveAll(t => t.Id == "ts2");
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));

        Assert.Equal(
            new[] { "a0", "b0", "b1", "b2" },
            harness.DeletedItemIds.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Null(harness.Store.GetTrackedItem("a0"));
        Assert.Null(harness.Store.GetTrackedItem("b0"));
        Assert.NotNull(harness.Store.GetTrackedItem("a1"));

        StressLog.Record("HA-REAP-SCOPE",
            "teamsites=2 claim_denied_reaps=0 processed_teamsite_reaps=1 gone_teamsite_reaps=3");
    }
}

// ── 1. Version-aware crawl at scale (50k+ docs, churn mid-crawl) ─────────────

public class Stress2_VersionChurnAtScale
{
    private const int Teamsites = 8;
    private const int DocsPerTeamsite = 6_500;
    private const int TotalDocs = Teamsites * DocsPerTeamsite;  // 52,000

    [Fact]
    public async Task LargeLibrary_VersionChurnAcrossPauseResume_ExactlyOneLatestVersionPerDoc()
    {
        using var harness = new PipelineHarness(
            objects: new[] { "ContentItem" },
            chunkSize: 500, graphBatchSize: 20, batchWorkers: 4);
        for (var t = 0; t < Teamsites; t++)
        {
            harness.AddTeamsite($"ts{t}");
            var list = harness.Seismic.ContentsByTeamsite[$"ts{t}"];
            for (var i = 0; i < DocsPerTeamsite; i++)
            {
                var id = $"d{t:D1}-{i:D5}";
                list.Add(TestContent.Make(id, teamsiteId: $"ts{t}", versionId: "v001"));
                harness.Seismic.Payloads[id] = System.Text.Encoding.UTF8.GetBytes($"payload {id}");
            }
        }

        // Pause once the first 4 teamsites (26,000 docs = 1,300 envelopes of
        // 20) are fully ingested — checked at chunk boundaries (quiescent).
        var resumed = false;
        harness.Pipeline.CriticalBreakerOpenProbe =
            () => !resumed && harness.GraphHandler.Requests.Count >= TotalDocs / 2 / 20;

        var sw1 = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));
        sw1.Stop();
        var putsBeforeResume = harness.GraphHandler.Requests.Count * 20;
        Assert.Equal(TotalDocs / 2, putsBeforeResume);  // exactly ts0..ts3
        var skippedAfterCrawl1 = harness.Pipeline.Stats.Skipped;

        // ── heavy version churn while paused: every 4th doc, ALL teamsites ──
        var currentVersion = new Dictionary<string, string>(StringComparer.Ordinal);
        var churnedCrawled = 0;
        var churnedUncrawled = 0;
        await Task.Delay(15);
        for (var t = 0; t < Teamsites; t++)
        {
            var list = harness.Seismic.ContentsByTeamsite[$"ts{t}"];
            for (var i = 0; i < DocsPerTeamsite; i++)
            {
                var id = $"d{t:D1}-{i:D5}";
                if (i % 4 == 0)
                {
                    var old = list[i];
                    list[i] = TestContent.Make(id, teamsiteId: $"ts{t}", versionId: "v002",
                        permissions: old.Permissions, modifiedAt: DateTime.UtcNow);
                    currentVersion[id] = "v002";
                    if (t < Teamsites / 2)
                        churnedCrawled++;
                    else
                        churnedUncrawled++;
                }
                else
                {
                    currentVersion[id] = "v001";
                }
            }
        }
        await Task.Delay(15);

        // ── resume: reconcile completed chunks + ingest the rest ─────────────
        resumed = true;
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));
        sw2.Stop();

        var puts = Stress2.PutVersions(harness);

        // Exactly-one-latest-version: the LAST PUT of every doc is its current
        // source version, and per-doc version sequences never regress (no
        // stale-version resurrection).
        var lastPut = new Dictionary<string, string>(StringComparer.Ordinal);
        var putCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var regressions = 0;
        foreach (var (id, version) in puts)
        {
            if (lastPut.TryGetValue(id, out var previous)
                && string.CompareOrdinal(version, previous) < 0)
            {
                regressions++;
            }
            lastPut[id] = version;
            putCounts[id] = putCounts.GetValueOrDefault(id) + 1;
        }
        Assert.Equal(0, regressions);
        Assert.Equal(TotalDocs, lastPut.Count);
        foreach (var (id, version) in currentVersion)
            Assert.Equal(version, lastPut[id]);

        // PUT-count law: churned docs in the crawled half were sent twice
        // (v001 then v002); everything else exactly once.
        var expectedPuts = TotalDocs + churnedCrawled;
        Assert.Equal(expectedPuts, puts.Count);
        var doublePuts = putCounts.Count(kv => kv.Value == 2);
        Assert.Equal(churnedCrawled, doublePuts);
        Assert.Equal(0, putCounts.Count(kv => kv.Value > 2));

        // No wrongful withdrawal of completed work (the round-2 regression).
        Assert.Empty(harness.DeletedItemIds);

        // Tracked store agrees with the source, doc for doc.
        var trackedAll = harness.Store.GetAllTrackedItems();
        Assert.Equal(TotalDocs, trackedAll.Count);
        var trackedMismatches = trackedAll.Count(t =>
            t.Status != "ingested" || t.VersionId != currentVersion[t.ItemId]);
        Assert.Equal(0, trackedMismatches);

        // Re-crawl skip ratio: unchanged docs in completed chunks were touched,
        // not re-sent.
        var reconcileSkips = harness.Pipeline.Stats.Skipped - skippedAfterCrawl1;
        Assert.Equal(TotalDocs / 2 - churnedCrawled, reconcileSkips);

        var versionsPerSecond = puts.Count / (sw1.Elapsed + sw2.Elapsed).TotalSeconds;
        StressLog.Record("VERSION-CHURN-50K",
            $"docs={TotalDocs} churned={churnedCrawled + churnedUncrawled} " +
            $"puts={puts.Count} double_puts={doublePuts} regressions=0 wrongful_withdrawals=0 " +
            $"reconcile_skips={reconcileSkips} skip_ratio={(double)reconcileSkips / (TotalDocs / 2):P1} " +
            $"crawl1={sw1.Elapsed.TotalSeconds:F1}s resume={sw2.Elapsed.TotalSeconds:F1}s " +
            $"versions/s={versionsPerSecond:F0}");
    }
}

// ── 2. Permission re-ACL fingerprint churn (exact-set repair, no storms) ─────

public class Stress2_ReAclFingerprintChurn
{
    private const int Teamsites = 6;
    private const int DocsPerTeamsite = 4_000;
    private const int TotalDocs = Teamsites * DocsPerTeamsite;  // 24,000
    private const int Principals = 40;

    private static List<SeismicPermission> PermFor(int principal) => new()
    {
        new SeismicPermission
        {
            PrincipalId = $"p{principal:D2}",
            PrincipalType = "user",
            Email = $"p{principal:D2}@contoso.com",
        },
    };

    [Fact]
    public async Task RapidPermissionFlips_ReAclExactlyTheChangedItems()
    {
        using var harness = new PipelineHarness(
            permissionReacl: true,
            objects: new[] { "ContentItem" },
            chunkSize: 500, graphBatchSize: 20, batchWorkers: 4);
        for (var p = 0; p < Principals; p++)
        {
            harness.Store.UpsertPrincipal(new PrincipalMapping(
                $"p{p:D2}", "user", $"p{p:D2}@contoso.com", $"entra-p{p:D2}", $"P{p}"));
        }
        for (var t = 0; t < Teamsites; t++)
        {
            harness.AddTeamsite($"ts{t}");
            var list = harness.Seismic.ContentsByTeamsite[$"ts{t}"];
            for (var i = 0; i < DocsPerTeamsite; i++)
            {
                var global = t * DocsPerTeamsite + i;
                var id = $"r{global:D5}";
                list.Add(TestContent.Make(id, teamsiteId: $"ts{t}",
                    permissions: PermFor(global % Principals)));
                harness.Seismic.Payloads[id] = System.Text.Encoding.UTF8.GetBytes($"payload {id}");
            }
        }

        // Crawl 1: baseline — every doc ingested with its ACL fingerprint.
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));
        Assert.Equal(TotalDocs, harness.PutItemIds().Distinct().Count());
        Assert.Empty(harness.AclPatchedItemIds);

        // ── flip permissions on every 7th item (content untouched) ───────────
        var expectedReAcl = new HashSet<string>(StringComparer.Ordinal);
        var newPrincipalById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var t = 0; t < Teamsites; t++)
        {
            var list = harness.Seismic.ContentsByTeamsite[$"ts{t}"];
            for (var i = 0; i < DocsPerTeamsite; i++)
            {
                var global = t * DocsPerTeamsite + i;
                if (global % 7 != 0)
                    continue;
                var id = $"r{global:D5}";
                var flipped = (global + 13) % Principals;   // always a different principal
                var old = list[i];
                list[i] = TestContent.Make(id, teamsiteId: $"ts{t}",
                    versionId: old.CurrentVersionId,        // version UNCHANGED
                    permissions: PermFor(flipped),
                    modifiedAt: old.ModifiedAt);
                expectedReAcl.Add(id);
                newPrincipalById[id] = flipped;
            }
        }

        // Crawl 2 under concurrent identity-store read pressure (the same
        // _gate the crawl's ACL resolution contends on).
        var putsAfterBaseline = harness.GraphHandler.Requests
            .Count(r => r.Method == HttpMethod.Post && r.Url.Contains("/$batch"));
        long concurrentResolves = 0;
        var stopReaders = false;
        var readers = Enumerable.Range(0, 4).Select(reader => Task.Run(() =>
        {
            var rng = new Random(reader + 42);
            var n = 0;
            while (!Volatile.Read(ref stopReaders))
            {
                _ = harness.Store.GetEntraObjectId($"p{rng.Next(Principals):D2}");
                Interlocked.Increment(ref concurrentResolves);
                // Paced: keep real lock contention on the store _gate without
                // starving the crawl (an unthrottled spin monopolizes the
                // Monitor and turns the test into a scheduler benchmark).
                if (++n % 32 == 0)
                    Thread.Sleep(1);
            }
        })).ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool ok;
        try
        {
            ok = await harness.Pipeline.RunCrawlAsync(fullCrawl: true);
        }
        finally
        {
            sw.Stop();
            Volatile.Write(ref stopReaders, true);
        }
        await Task.WhenAll(readers);
        Assert.True(ok);

        // THE exactness invariant, both directions:
        //   * no missed re-ACL → no stale access lingers;
        //   * no extra re-ACL → no full re-ACL storm.
        var patched = harness.AclPatchedItemIds.ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expectedReAcl, patched);
        Assert.Equal(expectedReAcl.Count, harness.AclPatchedItemIds.Count);  // no duplicates

        // Content was never re-sent for a pure permission change.
        var putsInCrawl2 = harness.GraphHandler.Requests
            .Count(r => r.Method == HttpMethod.Post && r.Url.Contains("/$batch")) - putsAfterBaseline;
        Assert.Equal(0, putsInCrawl2);
        Assert.Equal(expectedReAcl.Count, harness.Pipeline.Stats.AclDrift);
        Assert.Equal(expectedReAcl.Count, harness.Pipeline.Stats.ReAcled);

        // The PATCHed ACL carries the NEW principal, and the stored fingerprint
        // now matches a fresh resolution (sampled across the flipped set).
        foreach (var (id, principal) in newPrincipalById.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                     .Where((_, index) => index % 250 == 0))
        {
            var acl = harness.AclPatches[id]!.AsArray();
            Assert.Equal($"entra-p{principal:D2}", acl[0]!["value"]!.GetValue<string>());
            var tracked = harness.Store.GetTrackedItem(id)!;
            var mapper = new AclMapper(harness.Store, "skip", "tenant-guid");
            Assert.Equal(mapper.Resolve(PermFor(principal)).Fingerprint(), tracked.AclFingerprint);
        }

        // Crawl 3: nothing flipped — a stable fingerprint set must produce
        // ZERO re-ACLs (no storm on a quiet corpus).
        var patchesBeforeCrawl3 = harness.AclPatchedItemIds.Count;
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));
        Assert.Equal(patchesBeforeCrawl3, harness.AclPatchedItemIds.Count);

        StressLog.Record("REACL-CHURN-24K",
            $"items={TotalDocs} flips_expected={expectedReAcl.Count} reacls_patched={patched.Count} " +
            $"missed=0 extra=0 content_resends=0 quiet_crawl_patches=0 " +
            $"crawl2={sw.Elapsed.TotalSeconds:F1}s items/s={TotalDocs / sw.Elapsed.TotalSeconds:F0} " +
            $"concurrent_store_reads={concurrentResolves}");
    }
}

// ── 4a. HA lease contention: claim/steal/close storms on the pure seams ──────
//
// The claim table is an in-memory model of dbo.CrawlClaims with SQL-row
// atomicity (guarded compare-and-swap under a lock, mirroring the guarded
// UPDATE ... WHERE NodeId = @PrevNode AND HeartbeatUtc = @PrevHeartbeat).
// All DECISIONS go through the real HaCoordinator seams (TryDecide /
// CloseDecision) — this stresses the shipping contention logic, not a copy.

public class Stress2_HaLeaseContentionStorm
{
    private sealed record ClaimRow(string Owner, DateTime Heartbeat, string Status);

    [Fact]
    public async Task FailoverStorm_NoLiveSteal_NoDoubleCrawl_ExactlyOneCloser()
    {
        const int nodes = 8;
        const int resources = 240;
        const int deadNodes = 4;                 // nodes 0,2,4,6 crash mid-hold
        const int claimTimeoutSeconds = 30;

        var table = new Dictionary<int, ClaimRow>();
        var gate = new object();
        // Logical clock: FROZEN during the contention storm (a fresh heartbeat
        // can never be stale, so any takeover of a live row is a hard bug),
        // then advanced ONCE past the timeout for the failover phase. That
        // keeps the live-steal/stale-steal invariants deterministic under full
        // thread contention.
        var epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var clockSeconds = 0L;
        DateTime Now() => epoch + TimeSpan.FromSeconds(Interlocked.Read(ref clockSeconds));

        var doneBy = new ConcurrentDictionary<int, string>();       // resource → completing node
        var abandonedResources = new ConcurrentDictionary<int, string>();  // resource → dead owner
        long attempts = 0, granted = 0, denied = 0, casLost = 0;
        long liveSteals = 0, staleSteals = 0, doubleCompletions = 0;

        // One claim/steal attempt through the REAL decision seam + guarded CAS
        // (the SQL shape: snapshot → TryDecide → UPDATE ... WHERE row-unchanged).
        // Returns null when denied/lost; otherwise the previous row.
        (bool Won, ClaimRow? Previous) TryClaimOnce(int resource, string nodeId)
        {
            Interlocked.Increment(ref attempts);
            ClaimRow? snapshot;
            lock (gate)
                snapshot = table.TryGetValue(resource, out var row) ? row : null;
            if (snapshot is { Status: "done" })
            {
                Interlocked.Increment(ref denied);
                return (false, snapshot);
            }
            var lease = snapshot is null
                ? ((string, DateTime)?)null
                : (snapshot.Owner, snapshot.Heartbeat);
            var decideNow = Now();
            if (!HaCoordinator.TryDecide(lease, nodeId, decideNow, claimTimeoutSeconds))
            {
                Interlocked.Increment(ref denied);
                return (false, snapshot);
            }
            lock (gate)
            {
                var current = table.TryGetValue(resource, out var row) ? row : null;
                var won = snapshot is null
                    ? current is null
                    : current is not null
                        && current.Owner == snapshot.Owner
                        && current.Heartbeat == snapshot.Heartbeat
                        && current.Status == snapshot.Status;
                if (!won)
                {
                    Interlocked.Increment(ref casLost);
                    return (false, current);
                }
                if (snapshot is not null && snapshot.Owner != nodeId)
                {
                    // A takeover happened. Legal ONLY when the heartbeat was
                    // genuinely past the timeout at decision time.
                    if ((decideNow - snapshot.Heartbeat).TotalSeconds > claimTimeoutSeconds)
                        Interlocked.Increment(ref staleSteals);
                    else
                        Interlocked.Increment(ref liveSteals);
                }
                table[resource] = new ClaimRow(nodeId, Now(), "claimed");
                Interlocked.Increment(ref granted);
                return (true, snapshot);
            }
        }

        void CompleteOnce(int resource, string nodeId)
        {
            lock (gate)
            {
                // CompleteClaim mirror: only the current live owner marks done.
                if (table[resource].Owner == nodeId && table[resource].Status == "claimed")
                {
                    table[resource] = table[resource] with { Status = "done" };
                    if (!doneBy.TryAdd(resource, nodeId))
                        Interlocked.Increment(ref doubleCompletions);
                }
            }
        }

        // ── phase 1: 8 nodes storm the resources, clock frozen ───────────────
        // Live nodes walk every open resource in node-specific orders, so each
        // resource sees concurrent claim attempts and the CAS admits one.
        // Dead-to-be nodes (0,2,4,6) each claim a dedicated resource from the
        // top of the range and crash mid-hold — a structural guarantee that
        // the failover phase has exactly `deadNodes` stale claims to steal.
        var openResources = resources - deadNodes;
        await Task.WhenAll(Enumerable.Range(0, nodes).Select(n => Task.Run(() =>
        {
            var nodeId = $"node-{n}";
            if (n % 2 == 0 && n < deadNodes * 2)
            {
                var reserved = openResources + n / 2;
                var (wonReserved, _) = TryClaimOnce(reserved, nodeId);
                Assert.True(wonReserved);
                abandonedResources.TryAdd(reserved, nodeId);
                // Crash mid-hold: claim left 'claimed', heartbeat frozen.
                return;
            }
            var order = Enumerable.Range(0, openResources)
                .OrderBy(r => (r * 7919 + n * 104729) % openResources).ToList();
            foreach (var resource in order)
            {
                if (doneBy.Count >= openResources)
                    break;
                var (won, _) = TryClaimOnce(resource, nodeId);
                if (won)
                    CompleteOnce(resource, nodeId);
            }
        })));

        // Frozen clock ⇒ nothing was stale ⇒ any steal in phase 1 was a live
        // steal — the double-crawl bug. There must be none, and the 4 dead
        // nodes' abandoned claims must still be held by them.
        Assert.Equal(0, Interlocked.Read(ref liveSteals));
        Assert.Equal(0, Interlocked.Read(ref staleSteals));
        Assert.Equal(deadNodes, abandonedResources.Count);
        Assert.Equal(resources - deadNodes, doneBy.Count);
        foreach (var (resource, owner) in abandonedResources)
        {
            lock (gate)
            {
                Assert.Equal(owner, table[resource].Owner);
                Assert.Equal("claimed", table[resource].Status);
            }
        }

        // ── phase 2: failover — clock passes the timeout, survivors steal ───
        Interlocked.Add(ref clockSeconds, claimTimeoutSeconds + 1);
        await Task.WhenAll(Enumerable.Range(0, nodes).Where(n => n % 2 == 1).Select(n => Task.Run(() =>
        {
            var nodeId = $"node-{n}";
            foreach (var resource in abandonedResources.Keys.OrderBy(r => (r + n) % resources))
            {
                var (won, _) = TryClaimOnce(resource, nodeId);
                if (won)
                    CompleteOnce(resource, nodeId);
            }
        })));

        // Every abandoned claim was stolen EXACTLY once (survivors raced; the
        // guarded CAS admitted one) and completed by a live node.
        Assert.Equal(resources, doneBy.Count);
        Assert.Equal(deadNodes, Interlocked.Read(ref staleSteals));
        Assert.Equal(0, Interlocked.Read(ref liveSteals));
        Assert.Equal(0, Interlocked.Read(ref doubleCompletions));
        foreach (var (resource, deadOwner) in abandonedResources)
            Assert.NotEqual(deadOwner, doneBy[resource]);

        // ── close storm: 100 nodes race the open→closed UPDATE ───────────────
        var sessionStatus = "open";
        string? closedBy = null;
        var closeGate = new object();
        var closers = new ConcurrentBag<string>();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(k => Task.Run(() =>
        {
            var nodeId = $"closer-{k}";
            string status;
            string? by;
            lock (closeGate)
            {
                status = sessionStatus;
                by = closedBy;
            }
            var (perform, finalStatus, result) =
                HaCoordinator.CloseDecision(false, false, status, by, nodeId);
            if (perform)
            {
                lock (closeGate)
                {
                    if (sessionStatus == "open")   // the guarded UPDATE
                    {
                        sessionStatus = finalStatus;
                        closedBy = nodeId;
                    }
                    else
                    {
                        result = closedBy == nodeId
                            ? HaCloseResult.ClosedByThisNode
                            : HaCloseResult.ClosedElsewhere;
                    }
                }
            }
            if (result == HaCloseResult.ClosedByThisNode)
                closers.Add(nodeId);
        })));
        Assert.Single(closers);
        Assert.Equal("closed", sessionStatus);

        // Commit-ack-loss retry: the winner re-runs the close and must STILL
        // report ClosedByThisNode; a bystander must not.
        var winner = closers.Single();
        var retry = HaCoordinator.CloseDecision(false, false, sessionStatus, closedBy, winner);
        Assert.Equal(HaCloseResult.ClosedByThisNode, retry.Result);
        var bystander = HaCoordinator.CloseDecision(false, false, sessionStatus, closedBy, "node-x");
        Assert.Equal(HaCloseResult.ClosedElsewhere, bystander.Result);

        StressLog.Record("HA-LEASE-STORM",
            $"nodes={nodes} resources={resources} attempts={attempts} granted={granted} " +
            $"denied={denied} cas_lost={casLost} stale_steals={staleSteals} live_steals=0 " +
            $"abandoned_by_dead={abandonedResources.Count} double_completions=0 " +
            $"close_racers=100 closers=1 ack_loss_retry_ok=true");
    }
}

// ── 4b. Multi-node SQLite + state-file concurrency under failover ────────────

public class Stress2_SqliteMultiNodeFailover
{
    [Fact]
    public async Task FourNodesOneDb_FailoverStorm_NoLockErrors_NoLostWrites()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "seismic-manode-" + Guid.NewGuid().ToString("N") + ".db");
        const int nodes = 4;
        const int keysPerNode = 40;
        const int versions = 30;
        const int reconnectEvery = 10;  // failover: reopen the connection mid-stream

        var errors = new ConcurrentBag<string>();
        long writes = 0, reads = 0;
        var failovers = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var writers = Enumerable.Range(0, nodes).Select(n => Task.Run(() =>
            {
                SqliteIdentityStore? store = null;
                try
                {
                    store = new SqliteIdentityStore(dbPath);
                    for (var v = 1; v <= versions; v++)
                    {
                        // Failover storm: this node "crashes" and a replacement
                        // opens a fresh connection to the same DB.
                        if (v % reconnectEvery == 0)
                        {
                            store.Dispose();
                            store = new SqliteIdentityStore(dbPath);
                            Interlocked.Increment(ref failovers);
                        }
                        for (var k = 0; k < keysPerNode; k++)
                        {
                            var key = $"n{n}-k{k:D2}";
                            store.UpsertPrincipal(new PrincipalMapping(
                                key, "user", $"{key}@c.com", $"entra-{key}-v{v:D2}", $"v{v}"));
                            store.UpsertTrackedItem(new TrackedItem(
                                $"item-{key}", $"v{v:D2}", $"ts{n}", null, DateTime.UtcNow, "ingested"));
                            Interlocked.Add(ref writes, 2);
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"writer-{n}: {ex}");
                }
                finally
                {
                    store?.Dispose();
                }
            })).ToList();

            // Cross-node readers on their own connections, live during writes.
            var stopReaders = false;
            var readerTasks = Enumerable.Range(0, 4).Select(r => Task.Run(() =>
            {
                try
                {
                    using var store = new SqliteIdentityStore(dbPath);
                    var rng = new Random(r + 1);
                    while (!Volatile.Read(ref stopReaders))
                    {
                        var key = $"n{rng.Next(nodes)}-k{rng.Next(keysPerNode):D2}";
                        _ = store.GetEntraObjectId(key);
                        _ = store.GetTrackedItem($"item-{key}");
                        Interlocked.Add(ref reads, 2);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"reader-{r}: {ex}");
                }
            })).ToList();

            await Task.WhenAll(writers);
            Volatile.Write(ref stopReaders, true);
            await Task.WhenAll(readerTasks);
            sw.Stop();

            Assert.Empty(errors);   // in particular: no unhandled "database is locked"

            // No lost writes: every key holds its FINAL version from whichever
            // connection incarnation wrote last.
            using (var verify = new SqliteIdentityStore(dbPath))
            {
                for (var n = 0; n < nodes; n++)
                {
                    for (var k = 0; k < keysPerNode; k++)
                    {
                        var key = $"n{n}-k{k:D2}";
                        Assert.Equal($"entra-{key}-v{versions:D2}", verify.GetEntraObjectId(key));
                        Assert.Equal($"v{versions:D2}", verify.GetTrackedItem($"item-{key}")!.VersionId);
                    }
                }
                Assert.Equal(nodes * keysPerNode, verify.CountMappedPrincipals());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }

        // ── checkpoint state concurrency (file backend, shared writer lock) ──
        using var state = new TempStateDir();
        const string connectorId = "Stress2State";
        const int checkpointWriters = 16;
        const int writesPerWriter = 150;
        await Task.WhenAll(Enumerable.Range(0, checkpointWriters).Select(w => Task.Run(() =>
        {
            for (var i = 1; i <= writesPerWriter; i++)
            {
                // Interleaved object keys; chunk indexes climb — the merged
                // checkpoint must keep the MAX per key and stay parseable.
                SyncState.WriteCheckpoint(connectorId, "2026-01-01T00:00:00", $"obj{w % 8}", i);
            }
        })));
        var checkpoint = SyncState.ReadCheckpoint(connectorId);
        Assert.NotNull(checkpoint);
        for (var o = 0; o < 8; o++)
            Assert.Equal(writesPerWriter, checkpoint!["completed"]![$"obj{o}"]!.GetValue<int>());

        StressLog.Record("SQLITE-MULTINODE",
            $"nodes={nodes} keys={nodes * keysPerNode} writes={writes} reads={reads} " +
            $"failovers={failovers} lock_errors=0 lost_writes=0 " +
            $"ops/s={(writes + reads) / sw.Elapsed.TotalSeconds:F0} " +
            $"checkpoint_writers={checkpointWriters} checkpoint_writes={checkpointWriters * writesPerWriter} checkpoint_max_ok=true");
    }
}

// ── 5. Usage-ranking pipeline at scale (120k events → deterministic rank) ────

public class Stress2_UsageRankingAtScale
{
    private const int Events = 120_000;
    private const int DistinctIds = 100_000;   // the last 20k events re-rank earlier ids
    private const int PageSize = 500;

    private static SeismicClient BuildClient(FakeHttpHandler handler) =>
        new(new SeismicSettings
        {
            Tenant = "contoso",
            ApiBaseUrl = "https://api.seismic.local",
            TokenUrl = "https://auth.seismic.local/token",
            ClientId = "client",
            ClientSecret = "secret",
            PageSize = PageSize,
        }, handler)
        {
            OverrideAccessToken = "token",
        };

    /// <summary>Serves the analytics feed page by page from the deterministic generator.</summary>
    private static FakeHttpHandler UsageFeed(Action? onPage = null)
    {
        var handler = new FakeHttpHandler();
        handler.When(
            request => request.Method == HttpMethod.Get
                && request.RequestUri!.ToString().Contains("/analytics/contentUsage", StringComparison.Ordinal),
            (request, _) =>
            {
                onPage?.Invoke();
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
                var limit = int.Parse(query["limit"]!);
                var offset = int.Parse(query["offset"]!);
                var items = new JsonArray();
                for (var i = offset; i < Math.Min(offset + limit, Events); i++)
                {
                    items.Add(new JsonObject
                    {
                        ["contentId"] = $"u{i % DistinctIds:D6}",
                        ["viewCount"] = i % 17,
                        ["downloadCount"] = i % 5,
                        ["shareCount"] = i % 3,
                    });
                }
                return FakeHttpHandler.Json(
                    HttpStatusCode.OK, new JsonObject { ["items"] = items }.ToJsonString());
            });
        return handler;
    }

    private static ulong RankHash(Dictionary<string, SeismicContentUsage> map)
    {
        // FNV-1a over the sorted (id, popularity) pairs — any fold difference
        // (missed event, wrong overwrite order, lost id) changes the hash.
        var hash = 14695981039346656037UL;
        foreach (var (id, usage) in map.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            foreach (var ch in $"{id}:{usage.PopularityScore};")
            {
                hash ^= ch;
                hash *= 1099511628211UL;
            }
        }
        return hash;
    }

    [Fact]
    public async Task FoldingEventsIntoRank_IsDeterministic_AndMemoryBounded()
    {
        var pages = 0;
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var map = await BuildClient(UsageFeed(() => Interlocked.Increment(ref pages)))
            .GetContentUsageAsync();
        sw.Stop();

        var retained = GC.GetTotalMemory(forceFullCollection: true) - baseline;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        // Folded, not accumulated: memory scales with DISTINCT ids, not events.
        Assert.Equal(DistinctIds, map.Count);
        Assert.True(retained < 150_000_000,
            $"fold retained {retained / 1_000_000} MB for {DistinctIds} ids — not bounded");
        Assert.Equal(Events / PageSize + 1, pages);  // exact paging, no re-reads

        // Fold law: the LAST event for an id wins (ids 0..19,999 were re-ranked
        // by events 100,000..119,999).
        foreach (var k in new[] { 0, 7, 13, 19_999, 20_000, 99_999 })
        {
            var source = k < Events - DistinctIds ? DistinctIds + k : k;
            var usage = map[$"u{k:D6}"];
            Assert.Equal(source % 17, usage.ViewCount);
            Assert.Equal(source % 5, usage.DownloadCount);
            Assert.Equal(source % 3, usage.ShareCount);
            Assert.Equal(source % 17 + 2 * (source % 5) + 3 * (source % 3), usage.PopularityScore);
        }

        // Determinism: an independent second fold produces the identical rank
        // surface — same aggregate hash, same top-10.
        var map2 = await BuildClient(UsageFeed()).GetContentUsageAsync();
        Assert.Equal(RankHash(map), RankHash(map2));
        List<string> Top10(Dictionary<string, SeismicContentUsage> m) => m
            .OrderByDescending(kv => kv.Value.PopularityScore)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(10).Select(kv => kv.Key).ToList();
        Assert.Equal(Top10(map), Top10(map2));

        // The rank signal lands on the externalItem exactly as folded.
        var transformer = new ItemTransformer();
        var acl = new AclResult(new[] { AclEntry.GrantUser("entra-x") }, 0, Skipped: false);
        for (var k = 0; k < 500; k++)
        {
            var id = $"u{k * 199:D6}";
            var item = transformer.Transform(
                TestContent.Make(id), teamsite: null, payload: null, acl: acl, usage: map[id]);
            Assert.Equal(map[id].PopularityScore,
                item["properties"]!["popularityScore"]!.GetValue<long>());
        }

        StressLog.Record("USAGE-RANK-120K",
            $"events={Events} distinct_ids={map.Count} pages={pages} " +
            $"fold={sw.Elapsed.TotalSeconds:F1}s events/s={Events / sw.Elapsed.TotalSeconds:F0} " +
            $"retained_mb={retained / 1_000_000} allocated_mb={allocated / 1_000_000} " +
            $"rank_hash_stable=true top10_stable=true transform_checks=500");
    }
}

// ── 6. Long soak: mixed crawl + webhook + re-ACL at 3x round-1 volume ────────

public class Stress2_LongSoakMixed
{
    private const int Teamsites = 4;
    private const int DocsPerTeamsite = 1_500;
    private const int TotalDocs = Teamsites * DocsPerTeamsite;  // 6,000
    private const int Cycles = 12;
    private const int PrincipalCount = 60;
    private const int DeletesPerCycle = 60;

    private static string IdOf(int g) => $"s{g:D4}";

    private static (string TeamsiteId, int Index) Locate(int g) =>
        ($"ts{g / DocsPerTeamsite}", g % DocsPerTeamsite);

    private static List<SeismicPermission> PermFor(int principal) => new()
    {
        new SeismicPermission
        {
            PrincipalId = $"p{principal:D2}",
            PrincipalType = "user",
            Email = $"p{principal:D2}@contoso.com",
        },
    };

    [Fact]
    public async Task SustainedMixedWorkload_StaysConsistent_WithFlatManagedMemory()
    {
        using var harness = new PipelineHarness(
            permissionReacl: true,
            objects: new[] { "ContentItem" },
            chunkSize: 250, graphBatchSize: 20, batchWorkers: 3);
        for (var p = 0; p < PrincipalCount; p++)
        {
            harness.Store.UpsertPrincipal(new PrincipalMapping(
                $"p{p:D2}", "user", $"p{p:D2}@contoso.com", $"entra-p{p:D2}", $"P{p}"));
        }

        // Source-of-truth model the fake Seismic is driven from.
        var version = new int[TotalDocs];
        var principal = new int[TotalDocs];
        var flips = new int[TotalDocs];
        for (var t = 0; t < Teamsites; t++)
            harness.AddTeamsite($"ts{t}");
        for (var g = 0; g < TotalDocs; g++)
        {
            version[g] = 1;
            principal[g] = g % PrincipalCount;
            var (teamsiteId, _) = Locate(g);
            harness.Seismic.ContentsByTeamsite[teamsiteId].Add(TestContent.Make(
                IdOf(g), teamsiteId: teamsiteId, versionId: $"v{version[g]:D3}",
                permissions: PermFor(principal[g])));
            harness.Seismic.Payloads[IdOf(g)] = System.Text.Encoding.UTF8.GetBytes($"payload {IdOf(g)}");
        }

        void ReplaceDoc(int g, bool bumpModified)
        {
            var (teamsiteId, _) = Locate(g);
            var list = harness.Seismic.ContentsByTeamsite[teamsiteId];
            var index = list.FindIndex(c => c.Id == IdOf(g));
            var old = index >= 0 ? list[index] : null;
            var doc = TestContent.Make(
                IdOf(g), teamsiteId: teamsiteId, versionId: $"v{version[g]:D3}",
                permissions: PermFor(principal[g]),
                modifiedAt: bumpModified ? DateTime.UtcNow : old?.ModifiedAt);
            if (index >= 0)
                list[index] = doc;
            else
                list.Add(doc);
        }

        var process = System.Diagnostics.Process.GetCurrentProcess();
        var managedStart = GC.GetTotalMemory(forceFullCollection: true);
        long peakRss = 0, rssStart = 0;
        void SampleRss()
        {
            process.Refresh();
            peakRss = Math.Max(peakRss, process.WorkingSet64);
            if (rssStart == 0)
                rssStart = process.WorkingSet64;
        }
        SampleRss();

        long crawlVisits = 0, webhookEvents = 0, versionBumps = 0, permFlips = 0, reAdds = 0;
        var queuePeak = 0;
        long queueEnqueues = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (var cycle = 0; cycle < Cycles; cycle++)
        {
            // ── churn: version bumps (content) + permission flips (ACL only) ─
            for (var g = 0; g < TotalDocs; g++)
            {
                if (g % Cycles == cycle)
                {
                    version[g]++;
                    versionBumps++;
                    ReplaceDoc(g, bumpModified: true);
                }
                if (g % 10 == cycle % 10)
                {
                    flips[g]++;
                    principal[g] = (g + flips[g] * 17) % PrincipalCount;
                    permFlips++;
                    ReplaceDoc(g, bumpModified: false);
                }
            }

            // ── crawl (full every 4th cycle and on the last one) ─────────────
            var full = cycle % 4 == 0 || cycle == Cycles - 1;
            var visitsBefore = harness.Pipeline.Stats.Ingested + harness.Pipeline.Stats.Skipped;
            Assert.True(
                await harness.Pipeline.RunCrawlAsync(fullCrawl: full),
                $"cycle {cycle} crawl failed");
            crawlVisits += harness.Pipeline.Stats.Ingested + harness.Pipeline.Stats.Skipped - visitsBefore;

            // ── webhook queue pressure: 3x the round-1 flood per cycle ───────
            var queue = new ConcurrentQueue<ContentEvent>();
            for (var i = 0; i < 12_500; i++)
            {
                WebhookReceiver.EnqueueCapped(
                    queue, new ContentEvent { Type = "contentPublished", ContentId = $"q{i}" }, 500);
                queueEnqueues++;
                if (queue.Count > queuePeak)
                    queuePeak = queue.Count;
            }

            // ── webhook processing: deletes + publish refreshes ──────────────
            var deleteSet = new HashSet<int>();
            for (var k = 0; k < DeletesPerCycle; k++)
                deleteSet.Add((cycle * 61 + k * 97) % TotalDocs);
            var events = new List<ContentEvent>();
            foreach (var g in deleteSet)
            {
                var (teamsiteId, _) = Locate(g);
                harness.Seismic.ContentsByTeamsite[teamsiteId].RemoveAll(c => c.Id == IdOf(g));
                events.Add(new ContentEvent
                {
                    Type = "contentDeleted", ContentId = IdOf(g), TeamsiteId = teamsiteId,
                });
            }
            for (var k = 0; k < 240; k++)
            {
                var g = (cycle * 31 + 600 + k * 89) % TotalDocs;
                if (deleteSet.Contains(g))
                    continue;
                events.Add(new ContentEvent
                {
                    Type = "contentPublished", ContentId = IdOf(g), TeamsiteId = Locate(g).TeamsiteId,
                });
            }
            await harness.Pipeline.ProcessEventsAsync(events);
            webhookEvents += events.Count;

            // Deleted docs are republished at cycle end as a fresh version.
            foreach (var g in deleteSet)
            {
                version[g]++;
                reAdds++;
                ReplaceDoc(g, bumpModified: true);
            }

            SampleRss();
        }

        // Final quiet full crawl reconciles the last cycle's republishes/flips.
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true));
        sw.Stop();
        SampleRss();

        // ── end-state consistency: source == tracked == index ────────────────
        var tracked = harness.Store.GetAllTrackedItems();
        Assert.Equal(TotalDocs, tracked.Count);
        var mapper = new AclMapper(harness.Store, "skip", "tenant-guid");
        var versionMismatches = 0;
        var fingerprintMismatches = 0;
        foreach (var item in tracked)
        {
            var g = int.Parse(item.ItemId[1..]);
            if (item.Status != "ingested" || item.VersionId != $"v{version[g]:D3}")
                versionMismatches++;
            if (item.AclFingerprint != mapper.Resolve(PermFor(principal[g])).Fingerprint())
                fingerprintMismatches++;
        }
        Assert.Equal(0, versionMismatches);
        Assert.Equal(0, fingerprintMismatches);

        // The index heard about every doc's latest version, never a regression.
        var lastPut = new Dictionary<string, string>(StringComparer.Ordinal);
        var regressions = 0;
        foreach (var (id, v) in Stress2.PutVersions(harness))
        {
            if (lastPut.TryGetValue(id, out var previous) && string.CompareOrdinal(v, previous) < 0)
                regressions++;
            lastPut[id] = v;
        }
        Assert.Equal(0, regressions);
        for (var g = 0; g < TotalDocs; g++)
            Assert.Equal($"v{version[g]:D3}", lastPut[IdOf(g)]);

        // Every delete event withdrew exactly once; nothing else was withdrawn.
        Assert.Equal(Cycles * DeletesPerCycle, harness.DeletedItemIds.Count);
        Assert.Empty(SyncState.ReadFailedRecords(harness.Config.Connector.Id));
        Assert.True(queuePeak <= 500, $"webhook queue peak {queuePeak} exceeded cap");

        // Flat managed memory after sustained mixed load (RSS peak reported).
        var managedEnd = GC.GetTotalMemory(forceFullCollection: true);
        var managedGrowth = managedEnd - managedStart;
        Assert.True(managedGrowth < 100_000_000,
            $"managed heap grew {managedGrowth / 1_000_000} MB over the soak");

        StressLog.Record("LONG-SOAK-3X",
            $"cycles={Cycles} crawls={Cycles + 1} crawl_visits={crawlVisits} " +
            $"webhook_events={webhookEvents} queue_enqueues={queueEnqueues} queue_peak={queuePeak} " +
            $"version_bumps={versionBumps} republishes={reAdds} perm_flips={permFlips} " +
            $"reacl_patches={harness.AclPatchedItemIds.Count} withdrawals={harness.DeletedItemIds.Count} " +
            $"puts={lastPut.Count} dead_letter=0 elapsed={sw.Elapsed.TotalSeconds:F1}s " +
            $"visits/s={crawlVisits / sw.Elapsed.TotalSeconds:F0} " +
            $"managed_growth_mb={managedGrowth / 1_000_000} rss_start_mb={rssStart / 1_000_000} rss_peak_mb={peakRss / 1_000_000}");
    }
}
