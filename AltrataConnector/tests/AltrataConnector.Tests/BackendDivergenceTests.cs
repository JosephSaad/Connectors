// Tests for two cross-backend defects in the SQL state store:
//
//   B1  dbo.altrata_suppressed.subject_id inherited the DATABASE's default
//       collation. On a stock SQL Server install that is
//       SQL_Latin1_General_CP1_CI_AS — case- AND accent-insensitive — while the
//       file backend compares suppressed subject ids with StringComparer
//       .Ordinal. The two backends therefore disagreed about whether a subject
//       had been erased, on the DSAR erasure list.
//
//   B2  AddDeadLetter (and the batch/replace paths around it) were retried in
//       WHOLE by SqlStateStore.Execute on a transient SqlException, so a
//       transient fault raised AFTER the write committed duplicated the record
//       — the same defect class the CommitGuard closed for MutateValue.
//
// TESTING REACH — stated plainly, because it bounds what these tests prove.
// There is no SQL Server in this suite and SqlException cannot be constructed
// by a test, so nothing here executes a real query or a real retry. Following
// the approach the existing SqlScriptValidationTests and R4-5 tests already
// set, the SQL side is pinned three ways: the DDL is parsed under the real SQL
// Server 2019 grammar and its declared collation read off the AST; the
// comparison semantics that collation implies are executed against the SAME
// assertion matrix the file backend is run against for real; and the retry
// loop's decision function is driven directly. What is NOT proven is that a
// live server behaves as its declared collation says — that needs an
// integration environment.

using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

// ============================================================================
// B1 — suppression-list comparison semantics must match across backends
// ============================================================================

public class SuppressionCollationParityTests
{
    /// <summary>Subject-id pairs that a case/accent-insensitive collation folds
    /// together and ordinal comparison keeps apart. Each is a real erasure the
    /// insensitive backend would have swallowed.</summary>
    public static TheoryData<string, string> ConfusablePairs() => new()
    {
        { "ALT-9001", "alt-9001" },      // case
        { "ALT-9001", "Alt-9001" },      // mixed case
        { "person-A1", "person-a1" },
        { "MÜLLER-7", "MULLER-7" },      // accent (AS/AI collations differ here too)
        { "resume-42", "résumé-42" },
    };

    // ── the file backend, run for real ──────────────────────────────────────

    [Theory]
    [MemberData(nameof(ConfusablePairs))]
    public void FileBackendKeepsConfusableSubjectIdsDistinct(string first, string second)
    {
        var dir = TestFixtures.NewTempDir("suppress-file");
        var store = new FileStateStore("c1", dir, dir);

        store.AddSuppressedSubject(first);

        Assert.True(store.IsSubjectSuppressed(first));
        Assert.False(store.IsSubjectSuppressed(second));

        store.AddSuppressedSubject(second);
        Assert.Equal(2, store.ListSuppressedSubjects().Count);

        // Un-suppressing one must not un-suppress the other — the erasure-list
        // data loss the ordinal comparer exists to prevent.
        store.RemoveSuppressedSubject(second);
        Assert.True(store.IsSubjectSuppressed(first));
        Assert.False(store.IsSubjectSuppressed(second));
    }

    // ── the SQL backend's declared semantics, read off the parsed DDL ────────

    /// <summary>The collation SqlStateStore.SchemaScript declares for
    /// dbo.altrata_suppressed.subject_id, or null when the column declares none
    /// and therefore inherits the database default. Read from the AST of the
    /// real SQL Server 2019 parser, not by string matching, so a collation
    /// moved or removed is detected rather than merely reworded.</summary>
    private static string? DeclaredSubjectIdCollation()
    {
        var parser = new TSql150Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(SqlStateStore.SchemaScript);
        var fragment = parser.Parse(reader, out var errors);
        Assert.True(errors.Count == 0,
            "SchemaScript does not parse: " + string.Join("; ", errors.Select(e => e.Message)));

        var collector = new CreateTableCollector();
        fragment.Accept(collector);

        var table = collector.Tables.SingleOrDefault(t =>
            string.Equals(t.SchemaObjectName.BaseIdentifier.Value, "altrata_suppressed",
                StringComparison.OrdinalIgnoreCase));
        Assert.True(table != null, "SchemaScript no longer creates dbo.altrata_suppressed");

        var column = table!.Definition.ColumnDefinitions.SingleOrDefault(c =>
            string.Equals(c.ColumnIdentifier.Value, "subject_id", StringComparison.OrdinalIgnoreCase));
        Assert.True(column != null, "dbo.altrata_suppressed no longer has a subject_id column");

        return column!.Collation?.Value;
    }

    private sealed class CreateTableCollector : TSqlFragmentVisitor
    {
        public List<CreateTableStatement> Tables { get; } = new();
        public override void ExplicitVisit(CreateTableStatement node)
        {
            Tables.Add(node);
            base.ExplicitVisit(node);
        }
    }

    [Fact]
    public void TheSchemaDeclaresABinaryCollationForSubjectId()
    {
        var collation = DeclaredSubjectIdCollation();
        Assert.NotNull(collation);
        Assert.Equal(SqlStateStore.SubjectIdCollation, collation);
        Assert.EndsWith("_BIN2", collation!, StringComparison.Ordinal);
    }

    // ── DELETED: BothBackendsAgreeOnConfusableSubjectIds ─────────────────────
    //
    // It claimed to be the suite's cross-backend agreement test and was not.
    // It compared the file backend against ComparerForCollation() — a
    // hand-written twenty-line MODEL of SQL Server collation semantics, written
    // by the same author as the fix it was asserting. A model can only detect
    // divergences it already encodes, so it was structurally incapable of
    // failing on anything new; it stayed green through the entire unpaired-
    // surrogate defect, in which the two backends were storing DIFFERENT VALUES
    // and collation was not involved at all. It has been removed, along with
    // the model, rather than renamed: a test that cannot fail is worse than no
    // test, because it occupies the slot where a real one would be missed.
    //
    // What replaces it, in StateContractTests.cs:
    //   * the accepted input domain is EXECUTED against BOTH real store
    //     classes — FileStateStore for real, SqlStateStore with an unreachable
    //     connection string, which is sufficient because the contract is
    //     enforced before a connection is opened — and the two are asserted to
    //     accept and reject identically, per entry point, by reflection over
    //     IStateStore rather than by a remembered list;
    //   * the SQL half of every claim is read off the TSql150Parser AST of the
    //     shipped DDL, which is real evidence about the real schema.
    //
    // STILL NOT PROVEN, and there is no way to prove it on this host (no SQL
    // Server, and no docker/colima/podman to run one): that a LIVE server
    // behaves as its declared collation and declared column widths say. That
    // needs an integration environment. docs/SQL_CONTRACT.md says so too.

    // ── the comparison sites, not just the column ──────────────────────────

    [Fact]
    public void EverySubjectIdComparisonPinsTheOrdinalCollationExplicitly()
    {
        // The blocker asks for the schema AND the queries. A comparison that
        // relies on the column's collation is correct only against a migrated
        // table; every `subject_id = @param` in the store must name the
        // collation at the comparison site.
        var source = File.ReadAllText(Path.Combine(
            TestFixtures.RepoRoot(), "src", "AltrataConnector", "State", "SqlStateStore.cs"));

        var comparisons = Regex.Matches(source, @"subject_id\s*=\s*@\w+(\s+COLLATE\s+\{?(\w+)\}?)?");
        Assert.True(comparisons.Count >= 3,
            $"expected at least 3 subject_id comparisons (add/remove/is-suppressed), found {comparisons.Count}");

        foreach (Match comparison in comparisons)
        {
            Assert.True(comparison.Groups[1].Success,
                $"subject_id comparison without an explicit COLLATE: '{comparison.Value}'");
            var named = comparison.Groups[2].Value;
            Assert.True(named is "SubjectIdCollation" || named == SqlStateStore.SubjectIdCollation,
                $"subject_id comparison pins '{named}', not the ordinal collation");
        }
    }

    [Fact]
    public void AlreadyDeployedTablesAreMigratedToTheBinaryCollation()
    {
        // Changing the CREATE TABLE alone leaves every EXISTING deployment on
        // the insensitive collation, silently — the schema is only applied when
        // the table is absent. A guarded ALTER must carry them over.
        var schema = SqlStateStore.SchemaScript;

        Assert.Contains("ALTER COLUMN subject_id", schema, StringComparison.Ordinal);
        Assert.Contains("collation_name <> N'" + SqlStateStore.SubjectIdCollation + "'", schema,
            StringComparison.Ordinal);

        // Guarded on the CURRENT collation, so a re-run against an already
        // migrated table is a no-op — the script's re-run safety promise.
        var migration = schema[schema.IndexOf("IF EXISTS (SELECT 1 FROM sys.columns", StringComparison.Ordinal)..];
        Assert.Contains("DROP CONSTRAINT pk_altrata_suppressed", migration, StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT pk_altrata_suppressed PRIMARY KEY", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void ListSuppressedSubjectsIsOrdinallyOrderedOnBothBackends()
    {
        // Operators diff these two lists node-against-node during DR, so the
        // ORDER has to agree, not only the membership.
        var ids = new[] { "alt-2", "ALT-10", "ALT-2", "alt-10", "_alt", "Zalt" };

        var dir = TestFixtures.NewTempDir("suppress-order");
        var file = new FileStateStore("c1", dir, dir);
        foreach (var id in ids)
            file.AddSuppressedSubject(id);

        var expected = ids.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, file.ListSuppressedSubjects());

        // SQL side sorts client-side with the same comparer; pin that it does,
        // since the server's BIN2 ORDER BY is code-point and not code-unit.
        var source = File.ReadAllText(Path.Combine(
            TestFixtures.RepoRoot(), "src", "AltrataConnector", "State", "SqlStateStore.cs"));
        var body = SourceOf(source, "public IReadOnlyList<string> ListSuppressedSubjects");
        Assert.Contains("Sort(StringComparer.Ordinal)", body, StringComparison.Ordinal);
    }

    /// <summary>The source of ONE member, bounded at the start of the next
    /// member rather than by a fixed character count.
    ///
    /// A fixed window is why this helper is written out: at 2200 characters the
    /// slice for a short method runs past its own closing brace and into the
    /// following methods, so an assertion meant to pin THIS method's body
    /// passes on a neighbour's text. That made the AddDeadLetter fence pin
    /// vacuous — it stayed green against the unfenced version by matching
    /// AddDeadLetters' guard below it.</summary>
    internal static string SourceOf(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' not found in SqlStateStore.cs");

        // Members are declared at exactly one indent level inside the class, so
        // the next line beginning with 4 spaces + a member-introducing token
        // ends this member.
        var rest = source[(start + signature.Length)..];
        var next = Regex.Match(rest, @"\n    (?:public|private|internal|protected|///)\s");
        var body = next.Success ? rest[..next.Index] : rest;
        return signature + body;
    }
}

// ============================================================================
// B2 — retry-after-commit on the dead-letter write paths
// ============================================================================

public class DeadLetterCommitFenceTests
{
    private static string Source() => File.ReadAllText(Path.Combine(
        TestFixtures.RepoRoot(), "src", "AltrataConnector", "State", "SqlStateStore.cs"));

    /// <summary>Every write path in SqlStateStore whose replay is NOT a no-op.
    /// Mirrors the audit in the CommitGuard doc comment; a new relative write
    /// added without a fence should be added here and will fail until fenced.</summary>
    public static TheoryData<string> NonIdempotentWritePaths() => new()
    {
        "public string? MutateValue",
        "public long IncrementBillableLookups",
        "public void MutateDeadLetters",
        "public void AddDeadLetter(",
        "public void AddDeadLetters(",
    };

    [Theory]
    [MemberData(nameof(NonIdempotentWritePaths))]
    public void EveryNonIdempotentWritePathIsCommitFenced(string signature)
    {
        var body = SuppressionCollationParityTests.SourceOf(Source(), signature);
        Assert.Contains("guard.MarkCommitted()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceDeadLettersIsRoutedThroughTheAtomicFencedMutate()
    {
        // It used to be ClearDeadLetters() + a loop of AddDeadLetter() — N+1
        // independent Execute calls, so not atomic and duplicating on replay.
        var body = SuppressionCollationParityTests.SourceOf(Source(), "public void ReplaceDeadLetters");
        Assert.Contains("MutateDeadLetters", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearDeadLetters();", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBatchAppendIsOverriddenRatherThanInheritedAsALoop()
    {
        // IStateStore's default AddDeadLetters loops AddDeadLetter; on SQL that
        // is N lock acquisitions and N transactions, breaking the contiguity
        // the interface documents.
        var body = SuppressionCollationParityTests.SourceOf(Source(), "public void AddDeadLetters(");
        Assert.Contains("BeginTransaction", body, StringComparison.Ordinal);
        Assert.Contains("txn.Commit()", body, StringComparison.Ordinal);
    }

    // ── behavioural: the executor's replay decision, driven directly ────────

    /// <summary>Replays the executor's retry loop over an in-memory "table",
    /// using the REAL SqlStateStore.ShouldRetry as the decision function. The
    /// connection and the SqlException are the only things stubbed.</summary>
    private static int InsertsPerformedWhen(bool fenced, int maxRetries = 3)
    {
        var rows = 0;
        var attempt = 0;
        var faultsLeft = 1;   // one transient fault, raised AFTER the write commits

        while (true)
        {
            var guard = new SqlStateStore.CommitGuard();
            try
            {
                rows++;                                   // the INSERT commits (autocommit)
                if (fenced)
                    guard.MarkCommitted();
                if (faultsLeft-- > 0)
                    throw new InvalidOperationException("transient fault after commit");
                return rows;
            }
            catch (InvalidOperationException)
                when (SqlStateStore.ShouldRetry(attempt, maxRetries, isTransient: true, committed: guard.Committed))
            {
                attempt++;
            }
            catch (InvalidOperationException)
            {
                return rows;   // fails fast, as the real executor rethrows
            }
        }
    }

    [Fact]
    public void AnUnfencedAppendDuplicatesTheRecordOnAPostCommitTransientFault()
    {
        // The defect, reproduced: the row was already durable, the fault
        // surfaced during teardown, the executor replayed the whole operation
        // and a SECOND copy of the same failed item landed in the queue.
        Assert.Equal(2, InsertsPerformedWhen(fenced: false));
    }

    [Fact]
    public void AFencedAppendWritesTheRecordExactlyOnce()
    {
        Assert.Equal(1, InsertsPerformedWhen(fenced: true));
    }

    [Fact]
    public void FencingDoesNotSuppressRetriesForFaultsRaisedBeforeTheCommit()
    {
        // The fence must not cost the transient-fault tolerance it sits inside:
        // nothing committed, so a retry is still correct.
        Assert.True(SqlStateStore.ShouldRetry(0, 3, isTransient: true, committed: false));
        Assert.True(SqlStateStore.ShouldRetry(2, 3, isTransient: true, committed: false));
        Assert.False(SqlStateStore.ShouldRetry(0, 3, isTransient: true, committed: true));
    }
}
