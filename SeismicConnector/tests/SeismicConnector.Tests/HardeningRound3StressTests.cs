// Bank-grade hardening — try/catch review + adversarial stress (round 3).
//
// Focus: the NEW hardening paths that had no dedicated concurrency/adversarial
// round yet —
//   * DecisionLedger  : torn-final-line tolerant read (crash-tail), and many
//                       concurrent Append()s keeping the chain valid.
//   * WebhookAntiReplay: seen-signature cache stays BOUNDED under a signed
//                       flood while a later replay of a cached event is still
//                       rejected; future timestamps handled at the window edge.
//   * Classification lock (#6b): many Restricted items locked to the enforce
//                       group survive a source-permission drift across the
//                       CONCURRENT $batch workers without a single re-widen.

using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using SeismicConnector.Graph;
using SeismicConnector.Infrastructure;
using SeismicConnector.Seismic;

namespace SeismicConnector.Tests;

// ── DecisionLedger: torn-final-line tolerant reader (crash-tail) ──────────────
public class DecisionLedgerTornLineTests
{
    // A file-backed ledger flushes one JSON line per Append. A crash mid-append
    // (or a disk-full that writes a partial last line) leaves a TORN final line.
    // ReadFile must survive it — skip the crash-tail, return the intact prefix,
    // and Verify() must still validate the surviving chain. (The alternative —
    // ReadFile throwing — means an offline auditor can never read a ledger whose
    // process died mid-write, which is exactly when you most need the audit.)
    [Fact]
    public void ReadFile_ToleratesTornFinalLine_AndStillVerifies()
    {
        var path = Path.Combine(Path.GetTempPath(), "ledger-torn-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            using (var ledger = new DecisionLedger(path))
            {
                ledger.Append("c1", DecisionLedger.DecisionExclude, "mne-flag");
                ledger.Append("c2", DecisionLedger.DecisionAclRestrict, "Restricted");
                ledger.Append("c3", DecisionLedger.DecisionExclude, "restricted-library");
            }

            // Simulate a crash mid-append: a partial, unterminated JSON line with
            // no trailing newline appended after the last good record.
            File.AppendAllText(path, """{"Seq":3,"ItemId":"c4","Decision":"exclude","Rea""");

            var entries = DecisionLedger.ReadFile(path);   // must NOT throw

            Assert.Equal(3, entries.Count);                // the 3 intact records
            Assert.True(DecisionLedger.Verify(entries).Valid);
            Assert.Equal(new[] { "c1", "c2", "c3" }, entries.Select(e => e.ItemId).ToArray());
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // A blank/whitespace crash-tail (partial write that flushed only a newline)
    // must be tolerated the same way.
    [Fact]
    public void ReadFile_ToleratesBlankTail()
    {
        var path = Path.Combine(Path.GetTempPath(), "ledger-blank-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            using (var ledger = new DecisionLedger(path))
                ledger.Append("c1", DecisionLedger.DecisionExclude, "mne-flag");
            File.AppendAllText(path, "   \n");

            var entries = DecisionLedger.ReadFile(path);
            Assert.Single(entries);
            Assert.True(DecisionLedger.Verify(entries).Valid);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // Interior corruption (a broken line that is NOT the crash-tail) is real
    // tamper/damage, not a clean crash — it must remain detectable, never be
    // silently swallowed so the chain reads as "valid".
    [Fact]
    public void ReadFile_InteriorCorruption_IsNotSilentlySwallowed()
    {
        var path = Path.Combine(Path.GetTempPath(), "ledger-interior-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var lines = new List<string>();
            using (var ledger = new DecisionLedger(path))
            {
                ledger.Append("c1", DecisionLedger.DecisionExclude, "r1");
                ledger.Append("c2", DecisionLedger.DecisionExclude, "r2");
                ledger.Append("c3", DecisionLedger.DecisionExclude, "r3");
            }
            lines = File.ReadAllLines(path).ToList();
            // Corrupt the MIDDLE line (interior), keep a valid last line.
            lines[1] = "{ this is not valid json";
            File.WriteAllText(path, string.Join('\n', lines) + "\n");

            // Either it throws, OR it returns a chain that Verify rejects — what
            // it must NOT do is return a "valid" 2-entry chain that hides the
            // damaged interior record.
            var damageObservable = false;
            try
            {
                var entries = DecisionLedger.ReadFile(path);
                damageObservable = !DecisionLedger.Verify(entries).Valid;
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                damageObservable = true;
            }
            Assert.True(damageObservable, "interior corruption must not read back as a valid chain");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // INSERTION variant: a malformed line inserted BETWEEN two valid entries (the
    // originals all survive). This is the more dangerous case than replacement — if
    // the reader silently DROPPED the inserted junk, the surrounding entries would
    // still form a chain Verify() accepts, hiding the tamper entirely. It must stay
    // observable (ReadFile throws, or Verify rejects the returned chain).
    [Fact]
    public void ReadFile_InteriorCorruption_InsertedBetweenValidEntries_IsNotSilentlySwallowed()
    {
        var path = Path.Combine(Path.GetTempPath(), "ledger-insert-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            using (var ledger = new DecisionLedger(path))
            {
                ledger.Append("c1", DecisionLedger.DecisionExclude, "r1");
                ledger.Append("c2", DecisionLedger.DecisionExclude, "r2");
                ledger.Append("c3", DecisionLedger.DecisionExclude, "r3");
            }
            var lines = File.ReadAllLines(path).ToList();
            // Insert a malformed line BETWEEN the first and second valid entries;
            // all three original records remain intact around it.
            lines.Insert(1, "{ this is not valid json");
            File.WriteAllText(path, string.Join('\n', lines) + "\n");

            var damageObservable = false;
            try
            {
                var entries = DecisionLedger.ReadFile(path);
                damageObservable = !DecisionLedger.Verify(entries).Valid;
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                damageObservable = true;
            }
            Assert.True(
                damageObservable,
                "interior corruption inserted between valid entries must not read back as a valid chain");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}

// ── DecisionLedger: concurrent Append() under batch workers ───────────────────
public class DecisionLedgerConcurrencyTests
{
    [Fact]
    public async Task ConcurrentAppends_ChainStaysValid_NoTornLines_OnDiskToo()
    {
        var path = Path.Combine(Path.GetTempPath(), "ledger-conc-" + Guid.NewGuid().ToString("N") + ".jsonl");
        const int workers = 16;
        const int perWorker = 400;   // 6,400 appends racing the shared file
        try
        {
            using (var ledger = new DecisionLedger(path))
            {
                var tasks = Enumerable.Range(0, workers).Select(w => Task.Run(() =>
                {
                    for (var i = 0; i < perWorker; i++)
                        ledger.Append($"w{w}-i{i}", DecisionLedger.DecisionExclude, $"reason {w}/{i}");
                }));
                await Task.WhenAll(tasks);

                Assert.Equal(workers * perWorker, ledger.Count);
                Assert.True(ledger.Verify().Valid);
            }

            // Re-read from disk: every line is intact JSON (no interleaving), the
            // chain validates, and seqs are exactly 0..N-1 with no gaps/dupes.
            var reread = DecisionLedger.ReadFile(path);
            Assert.Equal(workers * perWorker, reread.Count);
            Assert.True(DecisionLedger.Verify(reread).Valid);
            Assert.Equal(
                Enumerable.Range(0, workers * perWorker).Select(i => (long)i).ToArray(),
                reread.Select(e => e.Seq).ToArray());
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}

// ── WebhookAntiReplay: bounded cache under flood + window-edge freshness ───────
public class WebhookAntiReplayStressTests
{
    private const string Secret = "s3cr3t-shared-key";

    private static WebhookAntiReplay Build(DateTimeOffset now) =>
        new(new SignatureValidator(Secret), TimeSpan.FromMinutes(5), requireTimestamp: true, clock: () => now);

    private static int SeenCount(WebhookAntiReplay ar)
    {
        var field = typeof(WebhookAntiReplay).GetField("_seen", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((IDictionary)field.GetValue(ar)!).Count;
    }

    [Fact]
    public void SignedFlood_KeepsCacheBounded_AndStillRejectsACachedReplay()
    {
        var now = DateTimeOffset.UtcNow;
        var ar = Build(now);
        var validator = new SignatureValidator(Secret);
        var ts = now.ToUnixTimeSeconds().ToString();

        // Flood MaxCacheEntries + overflow DISTINCT, validly-signed, fresh events.
        const int overflow = 50;
        var total = WebhookAntiReplay.MaxCacheEntries + overflow;
        byte[] firstBody = Encoding.UTF8.GetBytes("""{"type":"contentPublished","contentId":"c0"}""");
        var firstSig = validator.ComputeHex(ts, firstBody);
        Assert.Equal(WebhookVerdict.Accepted, ar.Check(firstBody, ts, firstSig));  // c0 cached first

        for (var i = 1; i < total; i++)
        {
            var body = Encoding.UTF8.GetBytes($$"""{"type":"contentPublished","contentId":"c{{i}}"}""");
            var sig = validator.ComputeHex(ts, body);
            Assert.Equal(WebhookVerdict.Accepted, ar.Check(body, ts, sig));
        }

        // Bounded: the cache never grew past the hard cap despite the overflow.
        Assert.Equal(WebhookAntiReplay.MaxCacheEntries, SeenCount(ar));

        // A replay of an early, still-cached event is still rejected.
        Assert.Equal(WebhookVerdict.ReplayDetected, ar.Check(firstBody, ts, firstSig));
    }

    [Fact]
    public void FutureTimestamp_BeyondWindow_Rejected()
    {
        var now = DateTimeOffset.UtcNow;
        var ar = Build(now);
        var body = Encoding.UTF8.GetBytes("""{"type":"contentPublished","contentId":"c1"}""");
        var futureTs = (now.ToUnixTimeSeconds() + 600).ToString();   // +10 min, window 5 min
        var sig = new SignatureValidator(Secret).ComputeHex(futureTs, body);

        Assert.Equal(WebhookVerdict.StaleTimestamp, ar.Check(body, futureTs, sig));
    }

    [Fact]
    public void FutureTimestamp_WithinWindow_Accepted()
    {
        var now = DateTimeOffset.UtcNow;
        var ar = Build(now);
        var body = Encoding.UTF8.GetBytes("""{"type":"contentPublished","contentId":"c1"}""");
        var skewTs = (now.ToUnixTimeSeconds() + 60).ToString();      // +1 min clock skew
        var sig = new SignatureValidator(Secret).ComputeHex(skewTs, body);

        Assert.Equal(WebhookVerdict.Accepted, ar.Check(body, skewTs, sig));
    }
}

// ── #6b classification lock under CONCURRENT $batch workers ───────────────────
public class ClassificationLockConcurrencyTests
{
    private const string RestrictedBody = "Please contact bob@example.com about the deal.";
    private const string Group = "entra-restricted-group";

    private static List<SeismicPermission> Perms(params string[] principalIds) =>
        principalIds.Select(id => new SeismicPermission { PrincipalId = id, PrincipalType = "user" }).ToList();

    // Many Restricted items, small $batch size + several workers so the tracked-
    // item upserts (which persist the lock) race across pool threads. After a
    // source-permission WIDEN on every item, not one may be re-widened.
    [Fact]
    public async Task ManyLockedItems_AcrossWorkers_NeverRewidenedOnSourceDrift()
    {
        const int itemCount = 60;
        using var harness = new PipelineHarness(
            permissionReacl: true,
            classification: true,
            classificationEnforceAcl: true,
            classificationEnforceGroup: Group,
            chunkSize: 60, graphBatchSize: 4, batchWorkers: 6);
        harness.Store.UpsertPrincipal(
            new PrincipalMapping("seismic-user-2", "user", "bob@contoso.com", "entra-user-2", "Bob"));
        harness.AddTeamsite("ts1");
        for (var i = 0; i < itemCount; i++)
        {
            harness.AddContent(TestContent.Make($"c{i}", versionId: "v1", permissions: Perms("seismic-user-1")));
            harness.Seismic.Payloads[$"c{i}"] = Encoding.UTF8.GetBytes(RestrictedBody);
        }

        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        // Every item ingested locked to the group, ACL a single group grant.
        for (var i = 0; i < itemCount; i++)
        {
            var tracked = harness.Store.GetTrackedItem($"c{i}")!;
            Assert.True(tracked.ClassificationLocked);
            var acl = harness.LastPutBody($"c{i}")!["acl"]!.AsArray();
            Assert.Single(acl);
            Assert.Equal(Group, acl[0]!["value"]!.GetValue<string>());
        }

        // Widen the SOURCE permissions on every item (version unchanged) and re-crawl.
        harness.Seismic.ContentsByTeamsite["ts1"].Clear();
        for (var i = 0; i < itemCount; i++)
        {
            harness.AddContent(TestContent.Make($"c{i}", versionId: "v1",
                permissions: Perms("seismic-user-1", "seismic-user-2")));
            harness.Seismic.Payloads[$"c{i}"] = Encoding.UTF8.GetBytes(RestrictedBody);
        }
        Assert.True(await harness.Pipeline.RunCrawlAsync(fullCrawl: true, objectTypeFilter: "ContentItem"));

        // Not a single lock leaked: no PATCH widened to source, all still locked,
        // and no item's ACL ever carried a source principal.
        Assert.Empty(harness.AclPatchedItemIds);
        Assert.Equal(0, harness.Pipeline.Stats.AclDrift);
        Assert.Equal(0, harness.Pipeline.Stats.ReAcled);
        for (var i = 0; i < itemCount; i++)
        {
            Assert.True(harness.Store.GetTrackedItem($"c{i}")!.ClassificationLocked);
            Assert.Equal(1, harness.PutItemIds().Count(id => id == $"c{i}"));  // never re-PUT to source
            var acl = harness.LastPutBody($"c{i}")!["acl"]!.AsArray();
            Assert.DoesNotContain(acl, e => e!["value"]!.GetValue<string>() is "entra-user-1" or "entra-user-2");
        }
    }
}
