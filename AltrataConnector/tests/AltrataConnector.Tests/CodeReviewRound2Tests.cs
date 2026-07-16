// Regression coverage for code-review round 2 findings:
//
//   HIGH  — retry-failed replayed the STALE ACL captured at dead-letter time
//           while stamping the CURRENT seat hash, so a seat removed after the
//           failure was silently re-granted and the re-ACL reconciliation
//           (ListItemsWithAclHashOtherThan) could never correct it.
//   MED-1 — BuildItemId's sanitization ('anything else' → '-') was not
//           injective: 'acct:12/3' and 'acct-12-3' folded onto one item id, so
//           a PUT could overwrite another subject's item and a DSAR tombstone
//           could mis-target.
//   MED-2 — the fuzzy match review queue logged raw name+employer (PII) to
//           logs/match_review_*.jsonl, outside the ids/counts/hashes allowlist.
//   LOW-1 — .json array feeds were fully materialized (unbounded memory).
//   LOW-2 — checksums were validated on one open of the file and records
//           parsed from a second open (TOCTOU window for a swapped file).
//   LOW-3 — transform failures dead-lettered with no payload: retry-failed
//           threw "no payload captured" forever, inflating the depth alert.

using AltrataConnector.Altrata;
using AltrataConnector.Commands;
using AltrataConnector.Entitlement;
using AltrataConnector.Graph;
using AltrataConnector.Identity;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

// ---- HIGH: retry-failed must rebuild the ACL from the CURRENT seats ---------------

public class RetryFailedSeatDriftTests
{
    private static string UpsertPayloadGranting(string itemId, params string[] users) =>
        System.Text.Json.JsonSerializer.Serialize(new ExternalItem
        {
            Id = itemId,
            Acl = users.Select(u => new AclEntry { Type = "user", Value = u }).ToList(),
            Properties = new Dictionary<string, object?> { ["title"] = itemId },
        });

    [Fact]
    public async Task ReplayNeverReGrantsARemovedSeat_AndRecordsTheHashOfTheAclSent()
    {
        var root = TestFixtures.NewTempDir("retryseatdrift");
        var graph = new FakeGraphClient();
        using var runtime = TestFixtures.NewRuntime(TestFixtures.NewConfig(), graph, root);

        // The item dead-lettered while bob was still seated (captured ACL grants
        // alice AND bob); bob has since been de-licensed.
        runtime.State.AddDeadLetter(new DeadLetterRecord
        {
            ItemId = "PersonProfile-P1",
            Dataset = Datasets.PersonProfile,
            PayloadJson = UpsertPayloadGranting("PersonProfile-P1",
                "alice@contoso.com", "bob@contoso.com"),
        });
        var currentSeats = new List<SeatPrincipal>
        {
            new(SeatPrincipalKind.UserUpn, "alice@contoso.com"),
        };
        runtime.Identity.ReplaceSeats(currentSeats);

        var result = await CommandRegistry.RetryFailedAsync(runtime, clearOnSuccess: true);

        Assert.Equal(true, result);
        var put = Assert.Single(graph.PutItems);
        // The replayed ACL is REBUILT from the current seats — the removed seat
        // is NOT re-granted, and no everyone-grant can ever appear.
        Assert.DoesNotContain(put.Acl, e => e.Value == "bob@contoso.com");
        Assert.Contains(put.Acl, e => e.Type == "user" && e.Value == "alice@contoso.com");
        Assert.DoesNotContain(put.Acl, e => e.Type is "everyone" or "everyoneExceptGuests");

        // The recorded hash matches the ACL actually sent, so the re-ACL
        // reconciliation sees nothing stale (and would catch any future drift).
        var currentHash = SeatAclBuilder.ComputeSeatHash(currentSeats);
        var ingested = Assert.Single(runtime.Identity.ListIngestedItems());
        Assert.Equal(currentHash, ingested.AclHash);
        Assert.Empty(runtime.Identity.ListItemsWithAclHashOtherThan(currentHash));
    }

    [Fact]
    public async Task ReplayFailsClosedWhenTheSeatListIsEmpty()
    {
        var root = TestFixtures.NewTempDir("retrynoseats");
        var graph = new FakeGraphClient();
        using var runtime = TestFixtures.NewRuntime(TestFixtures.NewConfig(), graph, root);
        runtime.State.AddDeadLetter(new DeadLetterRecord
        {
            ItemId = "PersonProfile-P1",
            Dataset = Datasets.PersonProfile,
            PayloadJson = UpsertPayloadGranting("PersonProfile-P1", "ghost@contoso.com"),
        });
        // No seats in the identity store: the replay must NOT fall back to the
        // captured ACL — it stays queued (fail closed, never fall open).

        var result = await CommandRegistry.RetryFailedAsync(runtime, clearOnSuccess: false);

        Assert.Equal(false, result);
        Assert.Empty(graph.PutItems);
        var remaining = Assert.Single(runtime.State.ReadDeadLetters());
        Assert.Equal(2, remaining.Attempts);
        Assert.Contains("seat", remaining.Error, StringComparison.OrdinalIgnoreCase);
    }
}

// ---- MED-1: collision-free item ids ------------------------------------------------

public class ItemIdCollisionTests
{
    [Fact]
    public void SanitizedIdsNeverCollide()
    {
        // Both raw ids sanitized to 'acct-12-3' under the old scheme.
        var dirty = ItemTransformer.BuildItemId(Datasets.PersonProfile, "acct:12/3");
        var clean = ItemTransformer.BuildItemId(Datasets.PersonProfile, "acct-12-3");
        Assert.NotEqual(dirty, clean);

        // Two DIFFERENT dirty ids with the same sanitized shape stay distinct too.
        var otherDirty = ItemTransformer.BuildItemId(Datasets.PersonProfile, "acct.12.3");
        Assert.NotEqual(dirty, otherDirty);

        // Graph-safe ids keep the legacy shape (existing indexed items unaffected).
        Assert.Equal("PersonProfile-acct-12-3", clean);
    }

    [Fact]
    public async Task ErasureStillFindsItemsWithSanitizedIds()
    {
        // The transformer and the erasure path must derive the SAME id from the
        // same raw subject id, or forget-subject would miss the item.
        var rawId = "P:1/x";
        var transformer = new ItemTransformer();
        var record = new FeedRecord
        {
            Dataset = Datasets.PersonProfile,
            Fields = new Dictionary<string, string?> { ["id"] = rawId, ["person_name"] = "Test Subject" },
        };
        var acl = new[] { new AclEntry { Type = "user", Value = "alice@contoso.com" } };
        var item = transformer.Transform(record, acl);
        Assert.Equal(ItemTransformer.BuildItemId(Datasets.PersonProfile, rawId), item.Id);

        var root = TestFixtures.NewTempDir("erasesanitized");
        var graph = new FakeGraphClient();
        using var runtime = TestFixtures.NewRuntime(TestFixtures.NewConfig(), graph, root);
        runtime.Identity.RecordIngestedItem(new IngestedItem(
            item.Id, Datasets.PersonProfile, "h", DateTime.UtcNow));
        runtime.Identity.RecordItemSubjects(item.Id, new[] { rawId });

        var result = await CommandRegistry.ForgetSubjectAsync(
            runtime, altrataId: rawId, email: null, actor: "joseph", confirm: true);

        Assert.Equal(true, result);
        Assert.Contains(item.Id, graph.DeletedItems);
        Assert.Equal(0, runtime.Identity.CountIngestedItems());
        Assert.True(runtime.State.IsSubjectSuppressed(rawId));
    }
}

// ---- MED-2: no raw PII in the match review queue -----------------------------------

public class ReviewQueuePiiTests : IDisposable
{
    private readonly string _root = TestFixtures.NewTempDir("reviewpii");
    private readonly SqliteIdentityStore _store;

    public ReviewQueuePiiTests()
    {
        _store = new SqliteIdentityStore(Path.Combine(_root, "identity.db"));
        _store.ReplaceCrmContacts(new[]
        {
            new CrmContact
            {
                Id = "C1", Email = "ada@contoso.com", Name = "Ada Lovelace",
                Employer = "Analytical Engines", Role = "Chief Scientist",
            },
        });
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public void ReviewQueueFileContainsIdsScoresAndHashesOnly()
    {
        var queue = new MatchReviewQueue("AltrataPiiTest", logsDir: Path.Combine(_root, "logs"));
        var resolver = new EntityResolver(_store, new FuzzyOptions
        {
            Enabled = true, Threshold = 0.85, ReviewFloor = 0.6, Review = queue,
        });

        // name 1.0 (.6) + employer 2/3 (.2) = 0.8 → [floor, threshold) → review.
        var match = resolver.Resolve("P42", "unknown@elsewhere.example",
            "Ada Lovelace", "Analytical Engines International");
        Assert.Null(match);

        var raw = File.ReadAllText(queue.Path);
        // Raw personal values must never reach the log file.
        Assert.DoesNotContain("Lovelace", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ada", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Analytical", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("elsewhere.example", raw, StringComparison.OrdinalIgnoreCase);

        // The adjudicator gets ids + scores + stable dedup hashes instead.
        var entry = Assert.Single(queue.ReadAll());
        Assert.Equal("P42", entry.AltrataId);
        Assert.Equal("C1", entry.CandidateContactId);
        Assert.Equal(0.8, entry.Score, 2);
        Assert.Equal(MatchReviewEntry.HashValue(EntityNormalizer.NormalizeName("Ada Lovelace")),
            entry.NameHash);
        Assert.Equal(MatchReviewEntry.HashValue(EntityNormalizer.NormalizeEmployer(
            "Analytical Engines International")), entry.EmployerHash);
    }
}

// ---- LOW-1: .json array size guard --------------------------------------------------

public class FeedJsonSizeGuardTests
{
    [Fact]
    public void OversizedJsonArrayIsRefusedButJsonlStreams()
    {
        var dir = TestFixtures.NewTempDir("jsonsize");
        var records = string.Join(",", Enumerable.Range(1, 50)
            .Select(i => $$"""{"id":"P{{i}}","person_name":"Person {{i}}"}"""));
        var jsonPath = Path.Combine(dir, "persons.json");
        File.WriteAllText(jsonPath, $"[{records}]");
        var jsonlPath = Path.Combine(dir, "persons.jsonl");
        File.WriteAllLines(jsonlPath, Enumerable.Range(1, 50)
            .Select(i => $$"""{"id":"P{{i}}","person_name":"Person {{i}}"}"""));

        FeedReader.JsonMaxBytesOverride = 128;
        try
        {
            var exc = Assert.Throws<FeedFileTooLargeException>(() =>
                FeedReader.ReadRecords(jsonPath, Datasets.PersonProfile));
            Assert.Contains(FeedReader.JsonMaxMbEnvVar, exc.Message);
            Assert.Contains(".jsonl", exc.Message);

            // The cap applies only to in-memory .json arrays; .jsonl streams.
            Assert.Equal(50, FeedReader.ReadRecords(jsonlPath, Datasets.PersonProfile).Count);
        }
        finally
        {
            FeedReader.JsonMaxBytesOverride = null;
        }

        // Under the cap the same .json parses normally.
        Assert.Equal(50, FeedReader.ReadRecords(jsonPath, Datasets.PersonProfile).Count);
    }

    [Fact]
    public void JsonCapIsEnvConfigurableWithSaneDefault()
    {
        var previous = Environment.GetEnvironmentVariable(FeedReader.JsonMaxMbEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(FeedReader.JsonMaxMbEnvVar, "7");
            Assert.Equal(7L * 1024 * 1024, FeedReader.JsonMaxBytes());
            Environment.SetEnvironmentVariable(FeedReader.JsonMaxMbEnvVar, "not-a-number");
            Assert.Equal(FeedReader.DefaultJsonMaxMb * 1024 * 1024, FeedReader.JsonMaxBytes());
            Environment.SetEnvironmentVariable(FeedReader.JsonMaxMbEnvVar, null);
            Assert.Equal(FeedReader.DefaultJsonMaxMb * 1024 * 1024, FeedReader.JsonMaxBytes());
        }
        finally
        {
            Environment.SetEnvironmentVariable(FeedReader.JsonMaxMbEnvVar, previous);
        }
    }

    [Fact]
    public async Task OversizedJsonDeliveryIsRejectedNotCrashed()
    {
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile,
                TestFixtures.PersonJson(("P1", "Ada Lovelace", "ada@x.com", "Acme")), 1));

        FeedReader.JsonMaxBytesOverride = 16;
        try
        {
            var result = await harness.Engine.RunAsync(CrawlKind.Full);
            Assert.Equal(1, result.DeliveriesRejected);
            Assert.Equal(0, result.ItemsIngested);
            Assert.Empty(harness.Graph.PutItems);
            Assert.False(harness.State.IsDeliveryProcessed("d1"));
            Assert.Contains(harness.Alerts.Alerts, a => a.Event == "delivery_rejected");
        }
        finally
        {
            FeedReader.JsonMaxBytesOverride = null;
        }
    }
}

// ---- LOW-2: hash + parse from a single open handle ----------------------------------

public class VerifiedReadTests
{
    [Fact]
    public void ReadRecordsVerifiesTheManifestHashOnTheHandleItParses()
    {
        var dir = TestFixtures.NewTempDir("verifiedread");
        var path = Path.Combine(dir, "persons.json");
        File.WriteAllText(path, """[{"id":"P1","person_name":"A"}]""");
        var correctSha = TestFixtures.Sha256Of(path);

        // Matching hash → records parsed from the same open stream.
        var records = FeedReader.ReadRecords(path, Datasets.PersonProfile, correctSha);
        Assert.Single(records);
        Assert.Equal("P1", records[0].Id);

        // A file whose content no longer matches the manifest (e.g. swapped
        // after the upfront gate) is rejected at read time, never parsed.
        File.WriteAllText(path, """[{"id":"EVIL","person_name":"B"}]""");
        var exc = Assert.Throws<ChecksumMismatchException>(() =>
            FeedReader.ReadRecords(path, Datasets.PersonProfile, correctSha));
        Assert.Equal("persons.json", exc.FileName);
    }

    [Fact]
    public void VerifiedReadCoversStreamedFormatsToo()
    {
        var dir = TestFixtures.NewTempDir("verifiedread2");
        var path = Path.Combine(dir, "persons.jsonl");
        File.WriteAllLines(path, new[]
        {
            """{"id":"P1","person_name":"A"}""",
            """{"id":"P2","person_name":"B"}""",
        });
        var sha = TestFixtures.Sha256Of(path);
        Assert.Equal(2, FeedReader.ReadRecords(path, Datasets.PersonProfile, sha).Count);
        Assert.Throws<ChecksumMismatchException>(() =>
            FeedReader.ReadRecords(path, Datasets.PersonProfile, new string('0', 64)));
    }
}

// ---- LOW-3: transform failures are retirable, not stuck ------------------------------

public class UnreplayableDeadLetterTests
{
    [Fact]
    public async Task TransformFailureDeadLettersWithTheUnreplayableOp()
    {
        using var harness = new CrawlHarness();
        // One good record, one with no id (transform failure).
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile,
                """[{"id":"P1","person_name":"A"},{"person_name":"NoId"}]""", 2));

        var result = await harness.Engine.RunAsync(CrawlKind.Full);

        Assert.Equal(1, result.ItemsIngested);
        Assert.Equal(1, result.ItemsDeadLettered);
        var deadLetter = Assert.Single(harness.State.ReadDeadLetters());
        Assert.Equal(DeadLetterOps.Transform, deadLetter.Op);
        Assert.False(deadLetter.IsReplayable);
        // Reconciliation still accounts for every manifest record.
        Assert.Equal(Reconciliation.StatusReconciled, result.Reconciliations[0].Status);
    }

    [Fact]
    public void OnlyReplayableRecordsCountTowardTheAlertDepth()
    {
        var root = TestFixtures.NewTempDir("dldepth");
        using var runtime = TestFixtures.NewRuntime(TestFixtures.NewConfig(),
            new FakeGraphClient(), root);
        runtime.State.AddDeadLetter(new DeadLetterRecord
        {
            ItemId = "a", Op = DeadLetterOps.Transform, PayloadJson = "",
        });
        runtime.State.AddDeadLetter(new DeadLetterRecord
        {
            ItemId = "legacy-transform", Op = DeadLetterOps.Upsert, PayloadJson = "",
        });
        runtime.State.AddDeadLetter(new DeadLetterRecord
        {
            ItemId = "b", Op = DeadLetterOps.Delete,
        });

        // Only the DELETE is replayable; the transform failures (new op and
        // legacy empty-payload upsert) are excluded from the alert depth.
        Assert.Equal(1, runtime.DeadLetterDepth());
    }

    [Fact]
    public async Task RetryFailedKeepsUnreplayableEntriesWithoutBumpingAttempts()
    {
        var root = TestFixtures.NewTempDir("retryunrep");
        var graph = new FakeGraphClient();
        using var runtime = TestFixtures.NewRuntime(TestFixtures.NewConfig(), graph, root);
        runtime.State.AddDeadLetter(new DeadLetterRecord
        {
            ItemId = "PersonProfile-NoId", Dataset = Datasets.PersonProfile,
            Op = DeadLetterOps.Transform, Error = "record has no id", PayloadJson = "",
        });
        // Legacy shape (pre-op queue file): upsert with an empty payload used to
        // throw "no payload captured" and stick forever.
        runtime.State.AddDeadLetter(new DeadLetterRecord
        {
            ItemId = "PersonProfile-Legacy", Dataset = Datasets.PersonProfile,
            Op = DeadLetterOps.Upsert, PayloadJson = "",
        });

        var result = await CommandRegistry.RetryFailedAsync(runtime, clearOnSuccess: false);

        // Nothing replayable is failing → success; the un-replayable entries
        // are kept verbatim (attempts NOT bumped — replaying can't fix them).
        Assert.Equal(true, result);
        Assert.Empty(graph.PutItems);
        var kept = runtime.State.ReadDeadLetters();
        Assert.Equal(2, kept.Count);
        Assert.All(kept, r => Assert.Equal(1, r.Attempts));
    }

    [Fact]
    public async Task RetireUnreplayableDropsThemFromTheQueue()
    {
        var root = TestFixtures.NewTempDir("retryretire");
        var graph = new FakeGraphClient();
        using var runtime = TestFixtures.NewRuntime(TestFixtures.NewConfig(), graph, root);
        runtime.Identity.ReplaceSeats(TestFixtures.DefaultSeats());
        runtime.State.AddDeadLetter(new DeadLetterRecord
        {
            ItemId = "PersonProfile-NoId", Dataset = Datasets.PersonProfile,
            Op = DeadLetterOps.Transform, Error = "record has no id", PayloadJson = "",
        });
        // A replayable neighbour must still replay normally in the same pass.
        runtime.State.AddDeadLetter(new DeadLetterRecord
        {
            ItemId = "PersonProfile-P1", Dataset = Datasets.PersonProfile,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new ExternalItem
            {
                Id = "PersonProfile-P1",
                Acl = new[] { new AclEntry { Type = "user", Value = "alice@contoso.com" } },
                Properties = new Dictionary<string, object?>(),
            }),
        });

        var result = await CommandRegistry.RetryFailedAsync(
            runtime, clearOnSuccess: true, retireUnreplayable: true);

        Assert.Equal(true, result);
        Assert.Single(graph.PutItems);
        Assert.Empty(runtime.State.ReadDeadLetters());
    }

    [Fact]
    public void RetireUnreplayableFlagParses()
    {
        var parsed = CommandRegistry.BuildParser()
            .ParseArgs(new[] { "retry-failed", "--retire-unreplayable" });
        Assert.True(parsed.GetFlag("--retire-unreplayable"));
        Assert.False(parsed.GetFlag("--clear-on-success"));
    }
}
