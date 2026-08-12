// Dead-letter payload protection (DEADLETTER_PAYLOAD_MODE) and its interplay
// with per-subject erasure (forget-subject):
//
//   * REDACTED is this connector's DEFAULT (unlike its siblings — decision
//     record in SECURITY.md): the queue never holds wealth/relationship
//     profiles at rest; records carry ids / subject-hashes / error / attempts.
//   * retry-failed re-fetches redacted upserts from the (checksum-verified)
//     feed delivery and re-transforms them under the CURRENT seat ACL.
//   * forget-subject SCRUBS queued upserts/transforms for the erased subject
//     and retry-failed REFUSES to replay upserts for suppressed subjects —
//     while erasure-compensation DELETE records still replay (they COMPLETE
//     the erasure). Suppression wins on every path.

using System.Text.Json;
using AltrataConnector.Altrata;
using AltrataConnector.Commands;
using AltrataConnector.Config;
using AltrataConnector.Graph;
using AltrataConnector.Identity;
using AltrataConnector.Infrastructure;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

public class DeadLetterPolicyTests : IDisposable
{
    public void Dispose() =>
        Environment.SetEnvironmentVariable(DeadLetterPolicy.EnvVar, null);

    [Fact]
    public void RedactedIsTheDefaultMode()
    {
        Environment.SetEnvironmentVariable(DeadLetterPolicy.EnvVar, null);
        Assert.Equal(DeadLetterPolicy.Redacted, DeadLetterPolicy.Mode);
        Assert.True(DeadLetterPolicy.IsRedacted);

        Environment.SetEnvironmentVariable(DeadLetterPolicy.EnvVar, "FULL");
        Assert.Equal(DeadLetterPolicy.Full, DeadLetterPolicy.Mode);   // case-insensitive
    }

    [Fact]
    public void GarbageModeFailsFastNamingTheSetting()
    {
        Environment.SetEnvironmentVariable(DeadLetterPolicy.EnvVar, "banana");
        var exc = Assert.Throws<ConfigurationError>(() => DeadLetterPolicy.Mode);
        Assert.Contains("DEADLETTER_PAYLOAD_MODE", exc.Message);
    }

    [Fact]
    public void SubjectHashesAreStableShortAndNotTheRawId()
    {
        var hash = DeadLetterPolicy.HashSubject("P1");
        Assert.Equal(16, hash.Length);
        Assert.Equal(hash, DeadLetterPolicy.HashSubject("P1"));
        Assert.NotEqual(DeadLetterPolicy.HashSubject("P2"), hash);
        Assert.DoesNotContain("P1", hash);
    }
}

public class DeadLetterProtectionTests : IDisposable
{
    public DeadLetterProtectionTests() => ServiceStop.Reset();

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DeadLetterPolicy.EnvVar, null);
        ServiceStop.Reset();
    }

    private const string PiiPersons = """
        [{"id":"P1","person_name":"Ada Lovelace","email":"ada@contoso.com","net_worth_usd":"9000000","employer":"Analytical Engines"},
         {"id":"P2","person_name":"Grace Hopper","email":"grace@contoso.com","net_worth_usd":"7000000","employer":"Compilers Inc"}]
        """;

    private static readonly string[] PiiLiterals =
    {
        "Ada Lovelace", "ada@contoso.com", "9000000", "Analytical Engines",
        "Grace Hopper", "grace@contoso.com", "7000000", "Compilers Inc",
    };

    [Fact]
    public async Task RedactedCrawlKeepsProfilesOutOfTheQueueFile()
    {
        using var harness = new CrawlHarness();   // env unset ⇒ redacted default
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, PiiPersons, 2));
        harness.Graph.FailingItemIds.Add("PersonProfile-P2");

        var result = await harness.Engine.RunAsync(CrawlKind.Full);
        Assert.Equal(1, result.ItemsDeadLettered);

        var record = Assert.Single(harness.State.ReadDeadLetters());
        Assert.Equal("PersonProfile-P2", record.ItemId);
        Assert.True(record.Redacted);
        Assert.Empty(record.PayloadJson);                       // no profile at rest
        Assert.Empty(record.SubjectIds);                        // no raw ids either
        Assert.Contains(DeadLetterPolicy.HashSubject("P2"), record.SubjectHashes);
        Assert.True(record.IsReplayable);                       // replay = re-fetch

        // The no-PII assertion extended to the dead-letter path: the queue FILE
        // must not contain a single profile literal.
        var queueText = File.ReadAllText(harness.State.DeadLetterPath);
        foreach (var secret in PiiLiterals)
            Assert.DoesNotContain(secret, queueText);
    }

    [Fact]
    public async Task RetryFailedRefetchesFromTheFeedInRedactedMode()
    {
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, PiiPersons, 2));
        harness.Graph.FailingItemIds.Add("PersonProfile-P2");
        await harness.Engine.RunAsync(CrawlKind.Full);
        Assert.Single(harness.State.ReadDeadLetters());

        harness.Graph.FailingItemIds.Clear();                   // Graph recovered
        using var runtime = TestFixtures.NewRuntime(harness.Config, harness.Graph, harness.Root);
        var result = await CommandRegistry.RetryFailedAsync(runtime, clearOnSuccess: true);

        Assert.Equal(true, result);
        Assert.Empty(runtime.State.ReadDeadLetters());

        // The re-fetched item was re-transformed from the feed with its full
        // properties and the CURRENT seat ACL — not replayed from a stale copy.
        var item = harness.Graph.PutItems.Single(i => i.Id == "PersonProfile-P2");
        Assert.Equal("Grace Hopper", item.Properties["personName"]);
        Assert.Equal("7000000", item.Properties["netWorthUsd"]);
        Assert.Equal(2, item.Acl.Count);
        Assert.DoesNotContain(item.Acl, e => e.Type is "everyone" or "everyoneExceptGuests");

        // Erasure reverse index restored: forget-subject can find the item.
        Assert.Contains("PersonProfile-P2", runtime.Identity.ListItemsForSubject("P2"));
    }

    [Fact]
    public async Task RefetchFailsActionablyWhenTheDeliveryIsGone()
    {
        using var harness = new CrawlHarness();
        var delivery = TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, PiiPersons, 2));
        harness.Graph.FailingItemIds.Add("PersonProfile-P2");
        await harness.Engine.RunAsync(CrawlKind.Full);

        Directory.Delete(delivery.Directory, recursive: true);   // retention took it
        harness.Graph.FailingItemIds.Clear();
        using var runtime = TestFixtures.NewRuntime(harness.Config, harness.Graph, harness.Root);
        var result = await CommandRegistry.RetryFailedAsync(runtime, clearOnSuccess: false);

        Assert.Equal(false, result);
        var kept = Assert.Single(runtime.State.ReadDeadLetters());
        Assert.Equal(2, kept.Attempts);                          // bumped, still queued
        Assert.Contains("no longer under FEED_PATH", kept.Error);
    }

    [Fact]
    public async Task ForgetSubjectScrubsQueuedUpsertsAndKeepsDeletes()
    {
        // FULL mode: the queued upsert holds the whole profile — an erasure
        // that left it behind would be incomplete.
        Environment.SetEnvironmentVariable(DeadLetterPolicy.EnvVar, DeadLetterPolicy.Full);
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, PiiPersons, 2));
        harness.Graph.FailingItemIds.Add("PersonProfile-P2");
        await harness.Engine.RunAsync(CrawlKind.Full);

        // A queued DELETE for the same subject (e.g. an earlier failed
        // tombstone) must SURVIVE the scrub — it completes the withdrawal.
        harness.State.AddDeadLetter(new DeadLetterRecord
        {
            ItemId = "WealthIndicator-W2",
            Op = DeadLetterOps.Delete,
            Error = "tombstone failed",
            SubjectHashes = new[] { DeadLetterPolicy.HashSubject("P2") },
        });
        var queueTextBefore = File.ReadAllText(harness.State.DeadLetterPath);
        Assert.Contains("Grace Hopper", queueTextBefore);        // profile really was at rest

        using var runtime = TestFixtures.NewRuntime(harness.Config, harness.Graph, harness.Root);
        var result = await CommandRegistry.ForgetSubjectAsync(
            runtime, "P2", null, "joseph", confirm: true);
        Assert.Equal(true, result);

        var remaining = runtime.State.ReadDeadLetters();
        var keptDelete = Assert.Single(remaining);
        Assert.Equal("WealthIndicator-W2", keptDelete.ItemId);   // delete kept
        var queueTextAfter = File.ReadAllText(harness.State.DeadLetterPath);
        foreach (var secret in new[] { "Grace Hopper", "grace@contoso.com", "7000000" })
            Assert.DoesNotContain(secret, queueTextAfter);       // profile scrubbed from rest

        Assert.True(runtime.State.IsSubjectSuppressed("P2"));
        Assert.True(runtime.Erasure.Verify(out _));              // ledger intact
    }

    [Fact]
    public async Task RetryRefusesQueuedUpsertsForAnErasedSubject()
    {
        // Full-mode record with raw subject ids, suppressed AFTER queueing
        // (e.g. the erasure ran on another node so no local scrub happened):
        // replaying it would resurrect the erased subject.
        Environment.SetEnvironmentVariable(DeadLetterPolicy.EnvVar, DeadLetterPolicy.Full);
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, PiiPersons, 2));
        harness.Graph.FailingItemIds.Add("PersonProfile-P2");
        await harness.Engine.RunAsync(CrawlKind.Full);

        harness.State.AddSuppressedSubject("P2");                // erased elsewhere
        harness.Graph.FailingItemIds.Clear();
        using var runtime = TestFixtures.NewRuntime(harness.Config, harness.Graph, harness.Root);
        var result = await CommandRegistry.RetryFailedAsync(runtime, clearOnSuccess: true);

        Assert.Equal(true, result);                              // queue drained (dropped)
        Assert.Empty(runtime.State.ReadDeadLetters());
        Assert.DoesNotContain(harness.Graph.PutItems, i => i.Id == "PersonProfile-P2");
    }

    [Fact]
    public async Task RedactedRefetchRefusesAnErasedSubjectEvenWithoutStampedHashes()
    {
        // Legacy-shaped redacted record (no subject hashes — queued before
        // stamping existed): the guard must still hold, via the re-fetch path
        // discovering the ACTUAL subject and re-checking suppression.
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, PiiPersons, 2));
        harness.State.AddDeadLetter(new DeadLetterRecord
        {
            ItemId = "PersonProfile-P2",
            Dataset = Datasets.PersonProfile,
            DeliveryId = "d1",
            Error = "HTTP 503",
            Redacted = true,
            PayloadJson = "",
        });
        harness.State.AddSuppressedSubject("P2");
        TestFixtures.WriteSeatFile(Path.Combine(harness.Root, "seats.json"), "alice@contoso.com");
        harness.Identity.ReplaceSeats(TestFixtures.DefaultSeats());

        using var runtime = TestFixtures.NewRuntime(harness.Config, harness.Graph, harness.Root);
        var result = await CommandRegistry.RetryFailedAsync(runtime, clearOnSuccess: true);

        Assert.Equal(true, result);
        Assert.Empty(runtime.State.ReadDeadLetters());           // dropped, not kept
        Assert.DoesNotContain(harness.Graph.PutItems, i => i.Id == "PersonProfile-P2");
    }

    [Fact]
    public async Task ErasureCompensationDeleteSurvivesRedactionAndCompletesViaRetry()
    {
        // The explicit forget-subject interplay: a mid-erasure Graph failure
        // dead-letters the compensating DELETE (payload-free in EVERY mode);
        // retry-failed must complete it EVEN THOUGH the subject is suppressed
        // (delete ops are exempt from the suppression guard — they finish the
        // erasure, they never resurrect anyone).
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, PiiPersons, 2));
        await harness.Engine.RunAsync(CrawlKind.Full);
        Assert.Equal(2, harness.Graph.PutItems.Count);

        harness.Graph.FailingDeletes.Add("PersonProfile-P2");    // Graph down mid-erasure
        using var runtime = TestFixtures.NewRuntime(harness.Config, harness.Graph, harness.Root);
        var forgetResult = await CommandRegistry.ForgetSubjectAsync(
            runtime, "P2", null, "joseph", confirm: true);
        Assert.Equal(false, forgetResult);                       // queued for retry

        var pending = Assert.Single(runtime.State.ReadDeadLetters());
        Assert.Equal(DeadLetterOps.Delete, pending.Op);
        Assert.Empty(pending.PayloadJson);
        Assert.Contains(DeadLetterPolicy.HashSubject("P2"), pending.SubjectHashes);
        Assert.True(pending.IsReplayable);
        Assert.True(runtime.State.IsSubjectSuppressed("P2"));    // suppression already durable

        harness.Graph.FailingDeletes.Clear();
        var retryResult = await CommandRegistry.RetryFailedAsync(runtime, clearOnSuccess: true);
        Assert.Equal(true, retryResult);
        Assert.Contains("PersonProfile-P2", harness.Graph.DeletedItems);   // erasure completed
        Assert.Empty(runtime.State.ReadDeadLetters());
        Assert.DoesNotContain(runtime.Identity.ListIngestedItems(),
            i => i.ItemId == "PersonProfile-P2");
    }

    [Fact]
    public async Task FullModePayloadReplayStillRecordsTheSubjectReverseIndex()
    {
        // A replayed item must be erasable exactly like a first-pass ingest —
        // the reverse index entry may not be lost across dead-letter + retry.
        Environment.SetEnvironmentVariable(DeadLetterPolicy.EnvVar, DeadLetterPolicy.Full);
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, PiiPersons, 2));
        harness.Graph.FailingItemIds.Add("PersonProfile-P2");
        await harness.Engine.RunAsync(CrawlKind.Full);

        harness.Graph.FailingItemIds.Clear();
        using var runtime = TestFixtures.NewRuntime(harness.Config, harness.Graph, harness.Root);
        await CommandRegistry.RetryFailedAsync(runtime, clearOnSuccess: true);

        Assert.Contains("PersonProfile-P2", runtime.Identity.ListItemsForSubject("P2"));
    }
}
