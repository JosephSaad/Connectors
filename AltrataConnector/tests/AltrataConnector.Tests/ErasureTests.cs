// Improvement round 3: per-subject erasure (DSAR) — resolve by id/email,
// dry-run vs confirm, full removal across inventory/crosswalk/path-index,
// tamper-evident ledger, durable suppression against re-delivery, reconciliation
// counting, un-suppress, and the seat invariant staying intact.

using AltrataConnector.Altrata;
using AltrataConnector.Commands;
using AltrataConnector.Config;
using AltrataConnector.Graph;
using AltrataConnector.Identity;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

// ---- subject-id extraction ----------------------------------------------------

public class SubjectIdTests
{
    private static FeedRecord Rec(string dataset, params (string K, string? V)[] f) => new()
    {
        Dataset = dataset,
        Fields = f.ToDictionary(x => x.K, x => x.V, StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public void PersonProfileIsItsOwnSubject()
    {
        Assert.Equal(new[] { "P1" }, ItemTransformer.SubjectIds(Rec(Datasets.PersonProfile, ("id", "P1"))));
    }

    [Fact]
    public void DerivedDatasetsCarryThePersonId()
    {
        Assert.Equal(new[] { "P1" },
            ItemTransformer.SubjectIds(Rec(Datasets.WealthIndicator, ("id", "W1"), ("person_id", "P1"))));
        Assert.Equal(new[] { "P1" },
            ItemTransformer.SubjectIds(Rec(Datasets.CareerHistory, ("id", "C1"), ("person_id", "P1"))));
    }

    [Fact]
    public void RelationshipPathConcernsBothEndpoints()
    {
        var subjects = ItemTransformer.SubjectIds(Rec(Datasets.RelationshipPath,
            ("id", "R1"), ("from_person_id", "P1"), ("to_person_id", "P2")));
        Assert.Equal(new[] { "P1", "P2" }, subjects);
    }

    [Fact]
    public void OrganizationHasNoPersonalSubject()
    {
        Assert.Empty(ItemTransformer.SubjectIds(Rec(Datasets.Organization, ("org_id", "O1"))));
    }
}

// ---- suppression list store ---------------------------------------------------

public class SuppressionStoreTests
{
    private static FileStateStore NewStore(out string root)
    {
        root = TestFixtures.NewTempDir("suppress");
        return new FileStateStore("AltrataTest",
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));
    }

    [Fact]
    public void AddContainsRemoveRoundTrip()
    {
        var store = NewStore(out _);
        Assert.False(store.IsSubjectSuppressed("P1"));
        store.AddSuppressedSubject("P1");
        store.AddSuppressedSubject("P1");  // idempotent
        Assert.True(store.IsSubjectSuppressed("P1"));
        Assert.Equal(new[] { "P1" }, store.ListSuppressedSubjects());
        store.RemoveSuppressedSubject("P1");
        Assert.False(store.IsSubjectSuppressed("P1"));
    }

    [Fact]
    public void SuppressionSurvivesAReopen()
    {
        var store = NewStore(out var root);
        store.AddSuppressedSubject("P9");
        var reopened = new FileStateStore("AltrataTest",
            logsDir: Path.Combine(root, "logs"), dataDir: Path.Combine(root, "data"));
        Assert.True(reopened.IsSubjectSuppressed("P9"));
    }
}

public class ItemSubjectsStoreTests : IDisposable
{
    private readonly SqliteIdentityStore _store;

    public ItemSubjectsStoreTests() =>
        _store = new SqliteIdentityStore(Path.Combine(TestFixtures.NewTempDir("itemsubj"), "identity.db"));

    public void Dispose() => _store.Dispose();

    [Fact]
    public void ReverseIndexFindsAllItemsForASubject()
    {
        _store.RecordItemSubjects("PersonProfile-P1", new[] { "P1" });
        _store.RecordItemSubjects("WealthIndicator-W1", new[] { "P1" });
        _store.RecordItemSubjects("RelationshipPath-R1", new[] { "P1", "P2" });
        _store.RecordItemSubjects("PersonProfile-P2", new[] { "P2" });

        Assert.Equal(new[] { "PersonProfile-P1", "RelationshipPath-R1", "WealthIndicator-W1" },
            _store.ListItemsForSubject("P1").OrderBy(x => x).ToArray());
        Assert.Equal(new[] { "PersonProfile-P2", "RelationshipPath-R1" },
            _store.ListItemsForSubject("P2").OrderBy(x => x).ToArray());
    }

    [Fact]
    public void RemovingAnItemDropsItsSubjectRows()
    {
        _store.RecordItemSubjects("WealthIndicator-W1", new[] { "P1" });
        _store.RemoveIngestedItem("WealthIndicator-W1");
        Assert.Empty(_store.ListItemsForSubject("P1"));
    }

    [Fact]
    public void RemoveCrosswalkDeletesTheRow()
    {
        _store.UpsertCrosswalk(new CrosswalkEntry("P1", "C1", "email", DateTime.UtcNow));
        _store.RemoveCrosswalk("P1");
        Assert.Null(_store.GetCrosswalk("P1"));
    }
}

// ---- tamper-evident erasure ledger --------------------------------------------

public class ErasureLedgerTests
{
    private static ErasureLedger NewLedger() =>
        new("AltrataTest", logsDir: Path.Combine(TestFixtures.NewTempDir("ledger"), "logs"));

    [Fact]
    public void EntriesChainAndVerify()
    {
        var ledger = NewLedger();
        var e1 = ledger.Append("joseph", ErasureActions.Erase, "P1", "p1@x.com", new[] { "PersonProfile-P1" });
        var e2 = ledger.Append("joseph", ErasureActions.Erase, "P2", null, new[] { "PersonProfile-P2" });

        Assert.Equal(1, e1.Seq);
        Assert.Equal(ErasureLedger.GenesisHash, e1.PrevHash);
        Assert.Equal(2, e2.Seq);
        Assert.Equal(e1.Hash, e2.PrevHash);   // chained
        Assert.True(ledger.Verify(out var broken));
        Assert.Equal(0, broken);
    }

    [Fact]
    public void TamperingWithAnEntryBreaksTheChain()
    {
        var ledger = NewLedger();
        ledger.Append("joseph", ErasureActions.Erase, "P1", null, new[] { "PersonProfile-P1" });
        ledger.Append("joseph", ErasureActions.Erase, "P2", null, new[] { "PersonProfile-P2" });

        // Rewrite the first line's subject id, keeping its stored hash.
        var lines = File.ReadAllLines(ledger.Path);
        lines[0] = lines[0].Replace("\"SubjectId\":\"P1\"", "\"SubjectId\":\"PX\"");
        File.WriteAllLines(ledger.Path, lines);

        Assert.False(ledger.Verify(out var broken));
        Assert.Equal(1, broken);
    }

    [Fact]
    public void DeletingAnEntryBreaksTheChain()
    {
        var ledger = NewLedger();
        ledger.Append("a", ErasureActions.Erase, "P1", null, Array.Empty<string>());
        ledger.Append("a", ErasureActions.Erase, "P2", null, Array.Empty<string>());
        ledger.Append("a", ErasureActions.Erase, "P3", null, Array.Empty<string>());

        var lines = File.ReadAllLines(ledger.Path).ToList();
        lines.RemoveAt(1);  // drop the middle entry
        File.WriteAllLines(ledger.Path, lines);

        Assert.False(ledger.Verify(out var broken));
        Assert.Equal(3, broken);  // P3's stored Seq (3) no longer matches its new position
    }

    [Fact]
    public void AppendIsAdditiveAcrossInstances()
    {
        var dir = Path.Combine(TestFixtures.NewTempDir("ledger2"), "logs");
        new ErasureLedger("AltrataTest", dir).Append("a", ErasureActions.Erase, "P1", null, Array.Empty<string>());
        var second = new ErasureLedger("AltrataTest", dir);
        second.Append("b", ErasureActions.Unsuppress, "P1", null, Array.Empty<string>());
        Assert.Equal(2, second.ReadAll().Count);
        Assert.True(second.Verify(out _));
    }
}

// ---- forget-subject command ---------------------------------------------------

public class ForgetSubjectTests
{
    private static (Runtime Runtime, FakeGraphClient Graph, string Root) Setup(
        Action<Runtime>? seed = null)
    {
        var root = TestFixtures.NewTempDir("forget");
        var graph = new FakeGraphClient();
        var runtime = TestFixtures.NewRuntime(TestFixtures.NewConfig(), graph, root);

        // Person P1 with a PersonProfile item + a derived WealthIndicator item.
        runtime.Identity.RecordIngestedItem(new IngestedItem("PersonProfile-P1", Datasets.PersonProfile, "h", DateTime.UtcNow));
        runtime.Identity.RecordItemSubjects("PersonProfile-P1", new[] { "P1" });
        runtime.Identity.RecordIngestedItem(new IngestedItem("WealthIndicator-W1", Datasets.WealthIndicator, "h", DateTime.UtcNow));
        runtime.Identity.RecordItemSubjects("WealthIndicator-W1", new[] { "P1" });
        runtime.Identity.UpsertCrosswalk(new CrosswalkEntry("P1", "C1", "email", DateTime.UtcNow));
        runtime.Identity.ReplacePathIndex(
            new[] { new PathEdge("P1", "P2", 1, 0) }, new[] { new PersonOrg("P1", "Acme") });
        // Unrelated person P2 stays untouched.
        runtime.Identity.RecordIngestedItem(new IngestedItem("PersonProfile-P2", Datasets.PersonProfile, "h", DateTime.UtcNow));
        runtime.Identity.RecordItemSubjects("PersonProfile-P2", new[] { "P2" });
        seed?.Invoke(runtime);
        return (runtime, graph, root);
    }

    [Fact]
    public async Task DryRunReportsWithoutMutating()
    {
        var (runtime, graph, _) = Setup();
        using var _1 = runtime;

        var result = await CommandRegistry.ForgetSubjectAsync(runtime, "P1", null, "joseph", confirm: false);

        Assert.Equal(true, result);
        Assert.Empty(graph.DeletedItems);                          // nothing withdrawn
        Assert.Equal(3, runtime.Identity.CountIngestedItems());    // inventory intact
        Assert.NotNull(runtime.Identity.GetCrosswalk("P1"));       // crosswalk intact
        Assert.False(runtime.State.IsSubjectSuppressed("P1"));     // not suppressed
        Assert.Empty(runtime.Erasure.ReadAll());                   // nothing ledgered
    }

    [Fact]
    public async Task ConfirmExecutesFullRemovalAcrossInventoryCrosswalkAndPathIndex()
    {
        var (runtime, graph, _) = Setup();
        using var _1 = runtime;

        var result = await CommandRegistry.ForgetSubjectAsync(runtime, "P1", null, "joseph", confirm: true);

        Assert.Equal(true, result);
        // Both of P1's items withdrawn from Graph + inventory; P2 untouched.
        Assert.Equal(new[] { "PersonProfile-P1", "WealthIndicator-W1" }, graph.DeletedItems.OrderBy(x => x).ToArray());
        Assert.Equal(1, runtime.Identity.CountIngestedItems());
        Assert.Empty(runtime.Identity.ListItemsForSubject("P1"));
        Assert.Null(runtime.Identity.GetCrosswalk("P1"));           // crosswalk removed
        Assert.Equal(0, runtime.Identity.CountPathEdges());         // path index dropped P1's edge
        Assert.True(runtime.State.IsSubjectSuppressed("P1"));       // suppressed
        var ledger = runtime.Erasure.ReadAll();
        Assert.Single(ledger);
        Assert.Equal("P1", ledger[0].SubjectId);
        Assert.Contains("PersonProfile-P1", ledger[0].ItemsRemoved);
        Assert.True(runtime.Erasure.Verify(out _));
    }

    [Fact]
    public async Task ResolvesSubjectByEmailViaTheCrosswalk()
    {
        var (runtime, graph, _) = Setup(rt =>
            rt.Identity.ReplaceCrmContacts(new[] { new CrmContact { Id = "C1", Email = "ada@x.com" } }));
        using var _1 = runtime;

        var result = await CommandRegistry.ForgetSubjectAsync(runtime, null, "ADA@x.com", "joseph", confirm: true);

        Assert.Equal(true, result);
        Assert.True(runtime.State.IsSubjectSuppressed("P1"));       // P1 resolved from the email's contact
        Assert.Contains("PersonProfile-P1", graph.DeletedItems);
    }

    [Fact]
    public async Task RequiresIdOrEmail()
    {
        var (runtime, _, _) = Setup();
        using var _1 = runtime;
        Assert.Equal(false, await CommandRegistry.ForgetSubjectAsync(runtime, null, null, "joseph", confirm: true));
    }

    [Fact]
    public async Task WithdrawalFailureStillSuppressesAndQueuesRetry()
    {
        var (runtime, graph, _) = Setup();
        using var _1 = runtime;
        graph.FailingDeletes.Add("WealthIndicator-W1");

        var result = await CommandRegistry.ForgetSubjectAsync(runtime, "P1", null, "joseph", confirm: true);

        Assert.Equal(false, result);                               // reported as incomplete
        Assert.True(runtime.State.IsSubjectSuppressed("P1"));      // durable regardless
        Assert.Single(runtime.Erasure.ReadAll());
        // The failed withdrawal is queued as a delete op for retry-failed.
        var dl = runtime.State.ReadDeadLetters();
        Assert.Contains(dl, r => r.ItemId == "WealthIndicator-W1" && r.Op == DeadLetterOps.Delete);
    }

    [Fact]
    public async Task SeatInvariantUnaffectedByErasure()
    {
        var (runtime, graph, _) = Setup();
        using var _1 = runtime;
        await CommandRegistry.ForgetSubjectAsync(runtime, "P1", null, "joseph", confirm: true);
        // Erasure only DELETEs — it never PUTs an item, so no ACL is ever authored.
        Assert.Empty(graph.PutItems);
    }
}

// ---- un-suppress --------------------------------------------------------------

public class UnsuppressTests
{
    [Fact]
    public async Task DryRunDoesNotLift()
    {
        var root = TestFixtures.NewTempDir("unsup");
        using var runtime = TestFixtures.NewRuntime(TestFixtures.NewConfig(), new FakeGraphClient(), root);
        runtime.State.AddSuppressedSubject("P1");

        await CommandRegistry.UnsuppressSubjectAsync(runtime, "P1", "joseph", confirm: false);
        Assert.True(runtime.State.IsSubjectSuppressed("P1"));
        Assert.Empty(runtime.Erasure.ReadAll());
    }

    [Fact]
    public async Task ConfirmLiftsAndLedgers()
    {
        var root = TestFixtures.NewTempDir("unsup2");
        using var runtime = TestFixtures.NewRuntime(TestFixtures.NewConfig(), new FakeGraphClient(), root);
        runtime.State.AddSuppressedSubject("P1");

        await CommandRegistry.UnsuppressSubjectAsync(runtime, "P1", "joseph", confirm: true);
        Assert.False(runtime.State.IsSubjectSuppressed("P1"));
        var ledger = runtime.Erasure.ReadAll();
        Assert.Single(ledger);
        Assert.Equal(ErasureActions.Unsuppress, ledger[0].Action);
    }
}

// ---- crawl respects suppression (durability against re-delivery) --------------

public class SuppressionCrawlTests
{
    [Fact]
    public async Task ReDeliveryOfASuppressedSubjectIsSkippedNotIngested()
    {
        using var harness = new CrawlHarness();
        harness.State.AddSuppressedSubject("P2");  // P2 was erased earlier
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile,
                TestFixtures.PersonJson(("P1", "Ada", null, null), ("P2", "Bob", null, null),
                                        ("P3", "Cara", null, null)), 3));

        var result = await harness.Engine.RunAsync(CrawlKind.Full);

        // P1 and P3 ingested; P2 skipped (suppressed), NOT dead-lettered.
        Assert.Equal(2, result.ItemsIngested);
        Assert.Equal(1, result.ItemsSuppressed);
        Assert.Equal(0, result.ItemsDeadLettered);
        Assert.DoesNotContain(harness.Graph.PutItems, i => i.Id == "PersonProfile-P2");
        Assert.Contains(harness.Graph.PutItems, i => i.Id == "PersonProfile-P1");

        // Reconciliation: ingested + suppressed == manifest ⇒ reconciled + processed.
        Assert.Equal(Reconciliation.StatusReconciled, result.Reconciliations[0].Status);
        Assert.Equal(1, result.Reconciliations[0].TotalSuppressed);
        Assert.True(harness.State.IsDeliveryProcessed("d1"));
    }

    [Fact]
    public async Task DerivedItemsOfASuppressedSubjectAreAlsoSkipped()
    {
        using var harness = new CrawlHarness();
        harness.State.AddSuppressedSubject("P1");
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("wealth.json", Datasets.WealthIndicator,
                """[{"id":"W1","person_id":"P1","net_worth_usd":"9"},{"id":"W2","person_id":"P9","net_worth_usd":"9"}]""", 2));

        var result = await harness.Engine.RunAsync(CrawlKind.Full);

        Assert.Equal(1, result.ItemsIngested);        // only P9's wealth item
        Assert.Equal(1, result.ItemsSuppressed);      // P1's wealth item skipped
        Assert.DoesNotContain(harness.Graph.PutItems, i => i.Id.StartsWith("WealthIndicator-W1"));
    }

    [Fact]
    public async Task UnsuppressedSubjectIsIngestedAgainOnTheNextCrawl()
    {
        using var harness = new CrawlHarness();
        harness.State.AddSuppressedSubject("P1");
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P1", "Ada", null, null)), 1));

        var first = await harness.Engine.RunAsync(CrawlKind.Full);
        Assert.Equal(1, first.ItemsSuppressed);
        Assert.DoesNotContain(harness.Graph.PutItems, i => i.Id == "PersonProfile-P1");

        // Lift the suppression and re-crawl (new delivery, incremental). Progress
        // counts accumulate on the shared engine, so assert on the Graph state:
        // P1 is now ingested where it was suppressed before.
        harness.State.RemoveSuppressedSubject("P1");
        TestFixtures.WriteDelivery(harness.FeedPath, "d2",
            ("persons.json", Datasets.PersonProfile, TestFixtures.PersonJson(("P1", "Ada", null, null)), 1));

        await harness.Engine.RunAsync(CrawlKind.Incremental);
        Assert.Contains(harness.Graph.PutItems, i => i.Id == "PersonProfile-P1");
    }

    [Fact]
    public async Task IngestRecordsTheItemSubjectReverseIndex()
    {
        using var harness = new CrawlHarness();
        TestFixtures.WriteDelivery(harness.FeedPath, "d1",
            ("wealth.json", Datasets.WealthIndicator,
                """[{"id":"W1","person_id":"P1","net_worth_usd":"9"}]""", 1));

        await harness.Engine.RunAsync(CrawlKind.Full);

        // The derived item is linked to its subject so erasure can find it.
        Assert.Contains("WealthIndicator-W1", harness.Identity.ListItemsForSubject("P1"));
    }
}
