// StateContractTests.cs
// ---------------------
// What survives of the state-backend contract suite, and what it now covers.
//
// The write-side VALIDATION this file was built around has been withdrawn — it
// rejected out-of-domain values at the boundary of both backends, which wedged
// every read-modify-write over legacy state and left DSAR erasures HALF-APPLIED.
// The tests asserting that rejection are gone with it. What remains:
//
//   1. The SQL schema's side of every claim — column existence, declared width,
//      declared collation, the INSERT's column list, the SELECT's column list —
//      read off the TSql150Parser AST of the SHIPPED DDL and the SHIPPED
//      statements. Real evidence about real artefacts.
//
//   2. The file backend run for real, end to end, on disk: the normalisations
//      that remain (free text, DateTimeKind), and the legacy-read guarantees.
//
//   3. The two subject-id divergences the withdrawal re-opened — unpaired
//      surrogate, over-long id — are now CLOSED at the OPERATOR ENTRY POINT:
//      `forget-subject` validates `--id` before any state mutation
//      (Commands/SubjectIdPolicy.cs) and refuses with an actionable error.
//      The STORE stays non-validating (replay tolerance), pinned by
//      LegacyStateReadModifyWriteTests. See
//      SuppressionSurrogateTests.FIXED_DEFECT_A / FIXED_DEFECT_B.
//
// There is NO SQL Server on this host and no container runtime to start one, so
// no test here executes a query against a live server. What is NOT proven: that
// a live SQL Server behaves as its declared collation and declared widths say,
// and the SQL half of both open divergences. That needs an integration
// environment, and docs/SQL_CONTRACT.md records it as such.
//
// Explicitly NOT done here: comparing production against a hand-written model
// of SQL Server semantics. The predecessor test that did that
// (BothBackendsAgreeOnConfusableSubjectIds) could not detect any divergence its
// own model did not already encode, and stayed green through the entire
// unpaired-surrogate defect. It has been deleted.

using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using AltrataConnector.Altrata;
using AltrataConnector.Commands;
using AltrataConnector.Identity;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

public class DeadLetterContractTests
{
    private static FileStateStore File_(string prefix)
    {
        var dir = TestFixtures.NewTempDir(prefix);
        return new FileStateStore("c1", Path.Combine(dir, "logs"), Path.Combine(dir, "data"));
    }

    private static DeadLetterRecord Base() => new()
    {
        ItemId = "person-alt-1", Dataset = "person", DeliveryId = "d-1",
        Error = "boom", Op = DeadLetterOps.Upsert, CorrelationId = "corr-1",
        PayloadJson = "{}", FailedUtc = DateTime.UtcNow, Attempts = 1,
        SubjectIds = new[] { "ALT-1" }, SubjectHashes = new[] { "deadbeefdeadbeef" },
    };

    // ── the round trip: what the file backend returns is what was stored ─────

    [Fact]
    public void TheFileRoundTripIsAFixpointOfTheContract()
    {
        // The invariant, executed on the backend that can be executed: for any
        // record, FileRoundTrip(Storable(r)) == Storable(r). The SQL backend
        // inserts the members of Storable(r) directly (one parameter per
        // column, checked against the parsed DDL in SqlDeadLetterColumnTests),
        // so both backends persist the SAME canonical record — which is the
        // whole invariant.
        var file = File_("dl-fixpoint");
        var record = Base() with
        {
            Error = "boom \uD83D and \uDC00",           // normalised to U+FFFD
            PayloadJson = "{\"a\":\"\uD800\"}",          // normalised to U+FFFD
            FailedUtc = new DateTime(2026, 7, 19, 1, 2, 3, DateTimeKind.Unspecified),
        };

        var storable = StateContract.Storable(record);

        file.AddDeadLetter(record);
        var back = file.ReadDeadLetters().Single();

        // Member by member, driven by reflection over the record rather than a
        // remembered list — the record's own Equals compares the two
        // IReadOnlyList members by REFERENCE, so `Assert.Equal(storable, back)`
        // would fail on identical content and pass on nothing useful.
        foreach (var property in typeof(DeadLetterRecord)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.Name != nameof(DeadLetterRecord.IsReplayable)))
        {
            var expected = property.GetValue(storable);
            var actual = property.GetValue(back);
            if (expected is IReadOnlyList<string> expectedList)
                Assert.Equal(expectedList, (IReadOnlyList<string>)actual!);
            else
                Assert.Equal(expected, actual);
        }

        // Idempotent: canonicalising the canonical form changes nothing, so the
        // file backend re-writing a record it read back cannot drift.
        var twice = StateContract.Storable(storable);
        Assert.Equal(storable.Error, twice.Error);
        Assert.Equal(storable.PayloadJson, twice.PayloadJson);
        Assert.Equal(storable.FailedUtc, twice.FailedUtc);

        Assert.Equal(DateTimeKind.Utc, back.FailedUtc.Kind);
        Assert.Equal(new DateTime(2026, 7, 19, 1, 2, 3, DateTimeKind.Utc), back.FailedUtc);
    }

    [Fact]
    public void CorrelationIdSurvivesTheFileRoundTrip()
    {
        var file = File_("dl-corr");
        file.AddDeadLetter(Base() with { CorrelationId = "ZZSENTINEL9" });
        Assert.Equal("ZZSENTINEL9", file.ReadDeadLetters().Single().CorrelationId);

        // and null stays null, rather than becoming "" on one backend
        var file2 = File_("dl-corr-null");
        file2.AddDeadLetter(Base() with { CorrelationId = null });
        Assert.Null(file2.ReadDeadLetters().Single().CorrelationId);
    }
}

// ============================================================================
// The SQL half, read off the AST of the shipped DDL and the shipped statements
// ============================================================================

public class SqlDeadLetterColumnTests
{
    private static TSqlFragment Parse(string sql)
    {
        var parser = new TSql150Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        Assert.True(errors.Count == 0,
            "does not parse under the SQL Server 2019 grammar: " +
            string.Join("; ", errors.Select(e => e.Message)));
        return fragment;
    }

    private sealed class Collector : TSqlFragmentVisitor
    {
        public List<CreateTableStatement> Created { get; } = new();
        public List<AlterTableAddTableElementStatement> Added { get; } = new();
        public List<InsertStatement> Inserts { get; } = new();
        public List<SelectStatement> Selects { get; } = new();
        public override void ExplicitVisit(CreateTableStatement n) { Created.Add(n); base.ExplicitVisit(n); }
        public override void ExplicitVisit(AlterTableAddTableElementStatement n) { Added.Add(n); base.ExplicitVisit(n); }
        public override void ExplicitVisit(InsertStatement n) { Inserts.Add(n); base.ExplicitVisit(n); }
        public override void ExplicitVisit(SelectStatement n) { Selects.Add(n); base.ExplicitVisit(n); }
    }

    private static Collector Schema()
    {
        var collector = new Collector();
        Parse(SqlStateStore.SchemaScript).Accept(collector);
        return collector;
    }

    private static CreateTableStatement Table(string name) =>
        Schema().Created.Single(t => string.Equals(
            t.SchemaObjectName.BaseIdentifier.Value, name, StringComparison.OrdinalIgnoreCase));

    private static ColumnDefinition Column(string table, string column) =>
        Table(table).Definition.ColumnDefinitions.Single(c => string.Equals(
            c.ColumnIdentifier.Value, column, StringComparison.OrdinalIgnoreCase));

    /// <summary>The declared NVARCHAR width of a column, read off the AST.</summary>
    private static int DeclaredWidth(string table, string column)
    {
        var type = Assert.IsType<SqlDataTypeReference>(Column(table, column).DataType);
        Assert.Equal(SqlDataTypeOption.NVarChar, type.SqlDataTypeOption);
        var parameter = Assert.Single(type.Parameters);
        return int.Parse(parameter.Value);
    }

    // ── MAJOR 2: the column exists, is written, and is read back ────────────

    [Fact]
    public void TheDeadLetterTableHasACorrelationIdColumn()
    {
        // It did not. DeadLetterRecord.CorrelationId was persisted by the file
        // backend and silently discarded by SQL: no column, no binding, no
        // read. CrawlEngine.StampCorrelation stamps every dead letter with it
        // and CommandRegistry.DeadLetterIdentityKey feeds it into the identity
        // key retry-failed uses to finalise its snapshot, so on SQL that key
        // component collapsed to the empty string for every record.
        var column = Column("altrata_deadletter", "correlation_id");
        Assert.Equal(StateContract.CorrelationIdMax, DeclaredWidth("altrata_deadletter", "correlation_id"));

        // NULLable — the record's member is string?, and a NOT NULL column
        // would turn "no correlation" into "" on SQL and null on file.
        Assert.Contains(column.Constraints.OfType<NullableConstraintDefinition>(), c => c.Nullable);
    }

    [Fact]
    public void AlreadyDeployedDeadLetterTablesAreMigrated()
    {
        // The CREATE TABLE is guarded by IF OBJECT_ID(...) IS NULL, so changing
        // it alone leaves every existing deployment without the column and the
        // INSERT fails outright. A guarded ALTER has to carry them over — the
        // same lesson the collation migration recorded.
        var added = Schema().Added.SelectMany(a => a.Definition.ColumnDefinitions)
            .Select(c => c.ColumnIdentifier.Value)
            .ToList();
        Assert.Contains("correlation_id", added, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("IF COL_LENGTH(N'dbo.altrata_deadletter', N'correlation_id') IS NULL",
            SqlStateStore.SchemaScript, StringComparison.Ordinal);
    }

    /// <summary>Every column of dbo.altrata_deadletter that the connector owns
    /// must be written by the INSERT and read by the SELECT. Both statement
    /// texts are parsed, so this compares three real artefacts against each
    /// other rather than three descriptions of them.</summary>
    [Fact]
    public void TheInsertAndSelectCoverEveryDeadLetterColumn()
    {
        var declared = Table("altrata_deadletter").Definition.ColumnDefinitions
            .Select(c => c.ColumnIdentifier.Value)
            .Where(n => !string.Equals(n, "id", StringComparison.OrdinalIgnoreCase))   // IDENTITY, server-assigned
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var insertCollector = new Collector();
        Parse(SqlStateStore.InsertDeadLetterSql).Accept(insertCollector);
        var inserted = insertCollector.Inserts.Single().InsertSpecification.Columns
            .Select(c => c.MultiPartIdentifier.Identifiers.Last().Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var selectCollector = new Collector();
        Parse(SqlStateStore.SelectDeadLetterSql).Accept(selectCollector);
        var query = (QuerySpecification)selectCollector.Selects.Single().QueryExpression;
        var selected = query.SelectElements.OfType<SelectScalarExpression>()
            .Select(e => ((ColumnReferenceExpression)e.Expression).MultiPartIdentifier.Identifiers.Last().Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // connector_id is the WHERE predicate, not a projected column.
        selected.Add("connector_id");

        Assert.True(declared.SetEquals(inserted),
            "columns declared but not INSERTed: " + string.Join(", ", declared.Except(inserted)) +
            " | INSERTed but not declared: " + string.Join(", ", inserted.Except(declared)));
        Assert.True(declared.SetEquals(selected),
            "columns declared but not SELECTed (persisted and never read back — the same defect with " +
            "the halves swapped): " + string.Join(", ", declared.Except(selected)) +
            " | SELECTed but not declared: " + string.Join(", ", selected.Except(declared)));
    }

    // ── the widths the contract claims are the widths the DDL declares ──────

    public static TheoryData<string, string, int> BoundedColumns() => new()
    {
        { "altrata_suppressed",  "subject_id",     StateContract.SubjectIdMax },
        { "altrata_suppressed",  "connector_id",   StateContract.ConnectorIdMax },
        { "altrata_deadletter",  "item_id",        StateContract.ItemIdMax },
        { "altrata_deadletter",  "dataset",        StateContract.DatasetMax },
        { "altrata_deadletter",  "delivery_id",    StateContract.DeliveryIdMax },
        { "altrata_deadletter",  "op",             StateContract.OpMax },
        { "altrata_deadletter",  "correlation_id", StateContract.CorrelationIdMax },
        { "altrata_deadletter",  "connector_id",   StateContract.ConnectorIdMax },
        { "altrata_checkpoint",  "delivery_id",    StateContract.DeliveryIdMax },
        { "altrata_checkpoint",  "dataset",        StateContract.DatasetMax },
        { "altrata_checkpoint",  "file_name",      StateContract.FileNameMax },
        { "altrata_checkpoint",  "connector_id",   StateContract.ConnectorIdMax },
        { "altrata_kv",          "key",            StateContract.KeyMax },
        { "altrata_kv",          "connector_id",   StateContract.ConnectorIdMax },
        { "altrata_deliveries",  "delivery_id",    StateContract.DeliveryIdMax },
        { "altrata_deliveries",  "connector_id",   StateContract.ConnectorIdMax },
        // The HA lease table. SQL-ONLY — there is no file-backed lease, so
        // there is no cross-backend divergence to close here; the constants
        // exist so the completeness sweep below has something to match and a
        // future file-backed lease starts from the right bound.
        { "altrata_leases",      "lease_name",     StateContract.LeaseNameMax },
        { "altrata_leases",      "owner",          StateContract.LeaseOwnerMax },
    };

    [Theory]
    [MemberData(nameof(BoundedColumns))]
    public void EveryBoundedColumnMatchesTheContractConstant(string table, string column, int expected)
    {
        // The bound the connector enforces and the bound the server enforces
        // must be the SAME number. Widen one without the other and the
        // divergence reopens — silently on the file backend, as SQL error 8152
        // on the other.
        Assert.Equal(expected, DeclaredWidth(table, column));
    }

    /// <summary>Every column this store compares by EQUALITY, and therefore
    /// every column whose collation decides identity. connector_id appears in
    /// the WHERE of essentially every statement, so it is as much a comparison
    /// key as the natural key beside it.</summary>
    public static TheoryData<string, string> ComparisonKeyColumns() => new()
    {
        { "altrata_suppressed", "subject_id" },
        { "altrata_suppressed", "connector_id" },
        { "altrata_kv", "key" },
        { "altrata_kv", "connector_id" },
        { "altrata_deliveries", "delivery_id" },
        { "altrata_deliveries", "connector_id" },
        { "altrata_deadletter", "connector_id" },
        { "altrata_checkpoint", "connector_id" },
        { "altrata_leases", "lease_name" },
    };

    [Theory]
    [MemberData(nameof(ComparisonKeyColumns))]
    public void EveryComparisonKeyColumnDeclaresTheBinaryCollation(string table, string column)
    {
        // The suppression list's collation defect, swept across the class
        // instead of fixed at the one table it was reported on. A stock SQL
        // Server default is case- and accent-INSENSITIVE, while every
        // file-backend counterpart is an ordinal Dictionary/SortedSet — so a
        // column left on the default makes 'last_sync_Full' and
        // 'last_sync_full' one key on SQL and two on file.
        Assert.Equal(SqlStateStore.IdentifierCollation, Column(table, column).Collation?.Value);
    }

    [Fact]
    public void MigratableComparisonKeyColumnsHaveAGuardedAlter()
    {
        // The CREATE TABLE is existence-guarded, so a collation added to it
        // alone reaches new deployments only. Every table whose PRIMARY KEY is
        // NAMED can be migrated with static DDL and must be.
        var schema = SqlStateStore.SchemaScript;
        foreach (var table in new[] { "altrata_kv", "altrata_deliveries", "altrata_suppressed" })
        {
            Assert.Contains($"ALTER TABLE dbo.{table} DROP CONSTRAINT pk_{table}", schema,
                StringComparison.Ordinal);
            Assert.Contains($"ADD CONSTRAINT pk_{table} PRIMARY KEY", schema, StringComparison.Ordinal);
        }
        // altrata_deadletter's connector_id is in no key, so a plain guarded
        // ALTER COLUMN carries it over.
        var flat = System.Text.RegularExpressions.Regex.Replace(schema, @"\s+", " ");
        Assert.Contains(
            "ALTER TABLE dbo.altrata_deadletter ALTER COLUMN connector_id NVARCHAR(64) " +
            "COLLATE Latin1_General_100_BIN2 NOT NULL;", flat, StringComparison.Ordinal);

        // altrata_checkpoint and altrata_leases carry INLINE UNNAMED primary
        // keys, so migrating a deployed one needs dynamic SQL to discover the
        // auto-generated constraint name — untestable here and therefore not
        // attempted. This assertion pins that the gap is the one documented,
        // and fails if somebody names those constraints without adding the
        // migration (or adds the migration without updating the docs).
        Assert.DoesNotContain("DROP CONSTRAINT pk_altrata_checkpoint", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP CONSTRAINT pk_altrata_leases", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryBoundedNvarcharColumnInTheSchemaIsInTheTable()
    {
        // Completeness, not anecdote: a NEW bounded column added to the schema
        // without a matching contract constant fails here rather than becoming
        // the next round's divergence.
        var tabled = BoundedColumns()
            .Select(row => $"{row[0]}.{row[1]}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var schema = Schema();
        var missing = new List<string>();
        foreach (var table in schema.Created)
        {
            var tableName = table.SchemaObjectName.BaseIdentifier.Value;
            if (!tableName.StartsWith("altrata_", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var column in table.Definition.ColumnDefinitions)
            {
                if (column.DataType is not SqlDataTypeReference
                    { SqlDataTypeOption: SqlDataTypeOption.NVarChar } type)
                    continue;
                if (type.Parameters.Count != 1 || !int.TryParse(type.Parameters[0].Value, out _))
                    continue;   // NVARCHAR(MAX) — unbounded, nothing to pin
                var key = $"{tableName}.{column.ColumnIdentifier.Value}";
                if (!tabled.Contains(key))
                    missing.Add(key);
            }
        }

        Assert.True(missing.Count == 0,
            "bounded NVARCHAR columns with no StateContract constant pinned to them: " +
            string.Join(", ", missing.OrderBy(m => m, StringComparer.Ordinal)));
    }
}

// ============================================================================
// MAJOR 3 — the fenced paths must not mask the fault they were fenced against
// ============================================================================

public class TransactionRollbackMaskingTests
{
    private static string Source() => File.ReadAllText(Path.Combine(
        TestFixtures.RepoRoot(), "src", "AltrataConnector", "State", "SqlStateStore.cs"));

    /// <summary>Executed against a REAL ADO.NET provider (Microsoft.Data.Sqlite).
    ///
    /// CAVEAT, stated because it bounds the claim: this is not
    /// Microsoft.Data.SqlClient — there is no SQL Server on this host and no
    /// container runtime to start one. What it demonstrates is that a
    /// DbTransaction.Rollback() after a successful Commit() throws
    /// InvalidOperationException, and that catching-then-rolling-back therefore
    /// REPLACES the original exception. SqlClient's SqlTransaction.Rollback is
    /// DOCUMENTED to throw InvalidOperationException on a completed transaction
    /// and on a broken connection, so the shape holds there by contract.</summary>
    private static (string FromBody, string FromCommit) WhatReachesTheExecutor(bool withExplicitRollback)
    {
        static string Run(bool explicitRollback, bool faultAtCommit)
        {
            using var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();
            using (var create = new SqliteCommand("CREATE TABLE dl(x INTEGER)", conn))
                create.ExecuteNonQuery();

            try
            {
                using var txn = conn.BeginTransaction();
                try
                {
                    using (var insert = new SqliteCommand("INSERT INTO dl VALUES (1)", conn, txn))
                        insert.ExecuteNonQuery();

                    if (!faultAtCommit)
                        throw new InvalidProgramException("transient fault raised by the BODY");

                    txn.Commit();
                    // The transient fault raised BY the commit — the precise
                    // case the commit fence exists to reason about.
                    throw new InvalidProgramException("transient fault raised by the COMMIT");
                }
                catch when (explicitRollback)
                {
                    txn.Rollback();   // the shape that shipped
                    throw;
                }
            }
            catch (Exception exc)
            {
                return exc.GetType().Name;
            }
        }

        return (Run(withExplicitRollback, faultAtCommit: false),
                Run(withExplicitRollback, faultAtCommit: true));
    }

    [Fact]
    public void TheOldExplicitRollbackMasksAFaultRaisedByTheCommit()
    {
        // The defect, reproduced against a real transaction. InvalidProgramException
        // stands in for the SqlException the executor's two handlers filter on;
        // what matters is that a DIFFERENT type comes out.
        var (fromBody, fromCommit) = WhatReachesTheExecutor(withExplicitRollback: true);

        Assert.Equal(nameof(InvalidProgramException), fromBody);          // pre-commit: fine
        Assert.Equal(nameof(InvalidOperationException), fromCommit);      // post-commit: MASKED

        // Which is why it mattered: Execute's handlers are
        // `catch (SqlException) when (ShouldRetry(...))` and `catch (SqlException)`.
        // A masked exception matches NEITHER — so ShouldRetry is never
        // consulted (a genuinely uncommitted batch is not retried and the dead
        // letters are LOST, not duplicated) and the "SQL state operation
        // FAILED" diagnostic never fires.
        Assert.Contains("catch (SqlException exc) when (ShouldRetry", Source(), StringComparison.Ordinal);
        Assert.Contains("SQL state operation FAILED", Source(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheUsingOnlyShapeLetsTheOriginalFaultThrough()
    {
        // The shipped shape, and MutateValue's model: `using var txn`, no
        // catch. Dispose rolls back an uncommitted transaction and is a no-op
        // on a completed one, so the ORIGINAL exception reaches the executor
        // whichever side of the commit it came from.
        var (fromBody, fromCommit) = WhatReachesTheExecutor(withExplicitRollback: false);

        Assert.Equal(nameof(InvalidProgramException), fromBody);
        Assert.Equal(nameof(InvalidProgramException), fromCommit);
    }

    [Fact]
    public void TheSqlStoreSourceHasNoRollbackCallAtTheStartOfALine()
    {
        // A LAYOUT check, and named as one. It is NOT the guard against the
        // rollback-after-commit masking defect and must not be relied on as
        // such: the regex is anchored with RegexOptions.Multiline, so it sees
        // only a Rollback call that BEGINS a line. Reinstating the exact defect
        // with the catch collapsed onto one line —
        //     } catch { txn.Rollback(); throw; }
        // — leaves this test GREEN. Measured on this tree before the IL guard
        // existed: the full suite reported Failed: 0, Passed: 724, Total: 724
        // with that defect in place.
        //
        // The real guard is RollbackMaskingIlGuardTests, which asserts on the
        // COMPILED IL and therefore cannot be evaded by any formatting. This
        // test is kept only because it costs nothing and reports the common
        // layout early; its scope is exactly what its name says.
        var source = Source();
        var rollbacks = System.Text.RegularExpressions.Regex.Matches(source, @"^\s*\w+\.Rollback\(\)",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        Assert.True(rollbacks.Count == 0,
            $"SqlStateStore's source has {rollbacks.Count} Rollback() call(s) at the start of a line. " +
            "Rollback throws InvalidOperationException on a completed or broken transaction and " +
            "REPLACES the SqlException the executor filters on. Use `using var txn` instead — " +
            "Dispose rolls back an uncommitted transaction and is a no-op on a completed one.");
    }

    [Fact]
    public void EveryTransactionInTheSqlStoreIsDisposedByUsing()
    {
        // The other half: dropping the catch is only safe because the
        // transaction is still disposed on the exceptional path.
        var source = Source();
        var begins = System.Text.RegularExpressions.Regex.Matches(source, @"\w+\.BeginTransaction\(");
        Assert.True(begins.Count >= 3, $"only {begins.Count} BeginTransaction call(s) found — scan broken?");

        var usingBegins = System.Text.RegularExpressions.Regex.Matches(
            source, @"using var \w+ = \w+\.BeginTransaction\(");
        Assert.Equal(begins.Count, usingBegins.Count);
    }
}

// ============================================================================
// The checkpoint, and DateTimeKind
// ============================================================================

public class CheckpointContractTests
{
    private static FileStateStore File_(string prefix)
    {
        var dir = TestFixtures.NewTempDir(prefix);
        return new FileStateStore("c1", Path.Combine(dir, "logs"), Path.Combine(dir, "data"));
    }

    private static SqlStateStore Sql() => new(
        "Server=127.0.0.1,1;Database=altrata_test;User Id=u;Password=p;" +
        "Connect Timeout=1;Encrypt=false;TrustServerCertificate=true", "c1", maxRetries: 0);

    private static CrawlCheckpoint Base() => new()
    { DeliveryId = "d-1", Dataset = "person", FileName = "people.json", RecordIndex = 3 };


    [Fact]
    public void CheckpointTimestampsComeBackAsUtcOnTheFileBackend()
    {
        // DATETIME2 carries no Kind and SqlDataReader.GetDateTime returns
        // Unspecified, so the SQL backend stamps Utc on read; the file backend
        // must return the same Kind or the two return DateTimes that compare
        // unequal despite identical ticks. Latent today — no Kind-sensitive
        // consumer was found — but it is the same class and costs one call.
        var file = File_("cp-kind");
        file.SaveCheckpoint(Base() with
        { UpdatedUtc = new DateTime(2026, 7, 19, 5, 6, 7, DateTimeKind.Unspecified) });

        var back = file.GetCheckpoint()!;
        Assert.Equal(DateTimeKind.Utc, back.UpdatedUtc.Kind);
        Assert.Equal(new DateTime(2026, 7, 19, 5, 6, 7, DateTimeKind.Utc), back.UpdatedUtc);

        Assert.Contains("StateContract.Utc(reader.GetDateTime(4))",
            File.ReadAllText(Path.Combine(TestFixtures.RepoRoot(), "src", "AltrataConnector",
                "State", "SqlStateStore.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void ACheckpointFileWrittenBeforeThisContractStillReadsBackAsUtc()
    {
        // The write path normalises, so the read path is only load-bearing for
        // a file written by an EARLIER build — whose timestamp has no 'Z' and
        // deserialises as Unspecified. Without the read-side stamp such a file
        // returns Unspecified on the file backend and Utc on SQL, which is the
        // divergence with the fix applied only to new writes.
        var store = File_("cp-legacy");
        Directory.CreateDirectory(Path.GetDirectoryName(store.CheckpointPath)!);
        File.WriteAllText(store.CheckpointPath, """
            {
              "DeliveryId": "d-1",
              "Dataset": "person",
              "FileName": "people.json",
              "RecordIndex": 3,
              "UpdatedUtc": "2026-07-19T05:06:07"
            }
            """);

        var back = store.GetCheckpoint()!;
        Assert.Equal(DateTimeKind.Utc, back.UpdatedUtc.Kind);
        Assert.Equal(new DateTime(2026, 7, 19, 5, 6, 7, DateTimeKind.Utc), back.UpdatedUtc);
    }

    [Fact]
    public void ADeadLetterLineWrittenBeforeThisContractStillReadsBackAsUtc()
    {
        var store = File_("dl-legacy");
        Directory.CreateDirectory(Path.GetDirectoryName(store.DeadLetterPath)!);
        File.WriteAllText(store.DeadLetterPath,
            """{"ItemId":"i-1","Dataset":"person","FailedUtc":"2026-07-19T05:06:07"}""" + "\n");

        var back = store.ReadDeadLetters().Single();
        Assert.Equal(DateTimeKind.Utc, back.FailedUtc.Kind);
        Assert.Equal(new DateTime(2026, 7, 19, 5, 6, 7, DateTimeKind.Utc), back.FailedUtc);

        // And a legacy line that is OUTSIDE the domain is still READABLE — the
        // contract is a write-path gate, not a read-path filter. Refusing to
        // read a legacy record would turn an old value into a lost failure
        // record, which is the opposite of the point.
        var store2 = File_("dl-legacy-bad");
        Directory.CreateDirectory(Path.GetDirectoryName(store2.DeadLetterPath)!);
        File.WriteAllText(store2.DeadLetterPath,
            "{\"ItemId\":\"i-" + new string('x', 400) + "\",\"FailedUtc\":\"2026-07-19T05:06:07Z\"}\n");
        Assert.Single(store2.ReadDeadLetters());
    }
}

// ============================================================================
// MAJOR 1, end to end: the exact reported id — CLOSED AT THE OPERATOR ENTRY
// ============================================================================
//
// These tests were OPEN_DEFECT_A / OPEN_DEFECT_B, pinning the then-current
// DEFECTIVE behaviour: an unpaired-surrogate subject id silently rewritten to
// U+FFFD by the file backend (erasure filed under a different id, subject
// still ingestible) and an over-long id accepted on file while SQL would raise
// un-retried error 8152. They are REWRITTEN, not deleted, to assert the FIXED
// behaviour: `forget-subject` validates the operator-supplied `--id` at the
// COMMAND entry point — before any state mutation — and refuses with an
// actionable error. The STORE stays deliberately non-validating (replay
// tolerance over legacy state — LegacyStateReadModifyWriteTests below), so a
// store-level fact here still asserts acceptance: the store's behaviour is
// unchanged; what changed is that no operator-typed id can reach it
// unvalidated.

public class SuppressionSurrogateTests
{
    private static FileStateStore File_(string prefix)
    {
        var dir = TestFixtures.NewTempDir(prefix);
        return new FileStateStore("c1", Path.Combine(dir, "logs"), Path.Combine(dir, "data"));
    }

    private static (Runtime Runtime, FakeGraphClient Graph, string Root) NewRuntime(string prefix)
    {
        var root = TestFixtures.NewTempDir(prefix);
        var graph = new FakeGraphClient();
        var runtime = TestFixtures.NewRuntime(TestFixtures.NewConfig(), graph, root);
        return (runtime, graph, root);
    }

    /// <summary>Swap Console.Error for the duration of a refusal so the test
    /// can assert the message an operator actually sees (Logger.Error writes
    /// ERROR lines to stderr). Restored in Dispose.</summary>
    private sealed class StderrCapture : IDisposable
    {
        private readonly TextWriter _original = Console.Error;
        private readonly StringWriter _writer = new();
        public StderrCapture() => Console.SetError(_writer);
        public string Text => _writer.ToString();
        public void Dispose() => Console.SetError(_original);
    }

    // The ill-formed ids are BUILT IN THE TEST BODY and their code units are
    // asserted before use: xUnit serialises theory arguments and a lone
    // surrogate does not survive that round trip (it arrives rewritten to
    // U+FFFD, i.e. well-formed), so a theory row carrying the VALUE would
    // silently exercise the wrong input. Clause NAMES travel; values do not.
    private static string OperatorId(string clause) => clause switch
    {
        "lone-high" => "ALT-\uD83D-9001",
        "lone-low" => "ALT-\uDC00-9001",
        "over-long" => new string('A', SubjectIdPolicy.MaxLength + 48),
        _ => throw new ArgumentOutOfRangeException(nameof(clause), clause, "unmapped clause"),
    };

    [Fact]
    public async Task FIXED_DEFECT_A_ForgetSubjectRefusesAnUnpairedSurrogateIdBeforeAnyMutation()
    {
        // The exact reported id. Prove the input is what it claims to be:
        // code unit 4 is a HIGH surrogate with no low surrogate after it.
        var id = "ALT-\uD83D-9001";
        Assert.Equal(0xD83D, id[4]);
        Assert.False(StateContract.IsWellFormedUtf16(id));

        var (runtime, graph, _) = NewRuntime("fixed-a");
        using var _1 = runtime;
        runtime.Identity.RecordIngestedItem(new IngestedItem("PersonProfile-X", Datasets.PersonProfile, "h", DateTime.UtcNow));

        using var stderr = new StderrCapture();
        var result = await CommandRegistry.ForgetSubjectAsync(runtime, id, null, "joseph", confirm: true);

        Assert.Equal(false, result);                              // command refused
        Assert.Empty(runtime.State.ListSuppressedSubjects());     // nothing filed — not under U+FFFD either
        Assert.False(runtime.State.IsSubjectSuppressed(id));
        Assert.False(runtime.State.IsSubjectSuppressed("ALT-�-9001"));
        Assert.Empty(runtime.Erasure.ReadAll());                  // nothing ledgered
        Assert.Empty(graph.DeletedItems);                         // nothing withdrawn

        // The error is ACTIONABLE and renders the id SAFELY: it names the
        // offending code unit and position, escapes it, and tells the operator
        // what to do — and no RAW unpaired surrogate reaches the console.
        var message = stderr.Text;
        Assert.Contains("forget-subject refused", message);
        Assert.Contains("0xD83D", message);
        Assert.Contains("index 4", message);
        Assert.Contains("\\uD83D", message);                      // escaped rendering
        Assert.DoesNotContain('\uD83D', message);                 // never raw
        Assert.Contains("re-run forget-subject", message);        // remediation
    }

    [Fact]
    public async Task FIXED_DEFECT_B_ForgetSubjectRefusesAnOverLongIdBeforeAnyMutation()
    {
        // Over the declared NVARCHAR width of altrata_suppressed.subject_id.
        // On SQL this id would raise error 8152 (not transient, not retried);
        // on file it would erase "successfully" — a cross-backend divergence.
        // Now neither happens: the command refuses it up front, identically on
        // both backends, before anything is written.
        var id = new string('A', SubjectIdPolicy.MaxLength + 48);

        var (runtime, graph, _) = NewRuntime("fixed-b");
        using var _1 = runtime;

        using var stderr = new StderrCapture();
        var result = await CommandRegistry.ForgetSubjectAsync(runtime, id, null, "joseph", confirm: true);

        Assert.Equal(false, result);
        Assert.Empty(runtime.State.ListSuppressedSubjects());
        Assert.Empty(runtime.Erasure.ReadAll());
        Assert.Empty(graph.DeletedItems);

        var message = stderr.Text;
        Assert.Contains("forget-subject refused", message);
        Assert.Contains($"{id.Length} UTF-16 code units", message);
        Assert.Contains($"NVARCHAR({SubjectIdPolicy.MaxLength})", message);
        Assert.Contains("8152", message);
        Assert.Contains("re-run forget-subject", message);
    }

    /// <summary>Both refusal branches cap the rendered id at 64 units. The
    /// hostile verifier fed Explain an ill-formed id of 100,001 units and got a
    /// 100,800-character error message: the ill-formed branch called Render
    /// with no cap while the over-long branch capped at 64. The message was
    /// safely escaped — this is log bloat, not a leak — but an error message
    /// whose size is attacker-controlled is still wrong. Kills the mutant
    /// "Render(subjectId)" (uncapped) on either branch.</summary>
    [Theory]
    [InlineData("ill-formed")]
    [InlineData("over-long")]
    public void ARefusalMessageStaysSmallHoweverLargeTheOffendingId(string clause)
    {
        var id = clause switch
        {
            // Built inline, not via MemberData: a lone surrogate does not
            // survive xUnit's theory-argument serialisation.
            "ill-formed" => "X\uD83D" + new string('Q', 100_000),
            _ => new string('Q', 100_000),
        };

        var message = SubjectIdPolicy.Explain(id);

        Assert.NotNull(message);
        Assert.True(
            message!.Length < 1_500,
            $"refusal message is {message.Length} chars for a {id.Length}-unit id; " +
            "the rendered id must be truncated, not echoed whole");
        Assert.Contains("truncated", message);
        Assert.DoesNotContain('\uD83D', message);                 // never raw
    }

    /// <summary>The ORDERING requirement, per clause: a rejected erase-subject
    /// leaves EVERY file of the store byte-identical — no half-applied
    /// suppression, no ledger entry, no scrubbed queue. Round 8's failure left
    /// an erasure half-applied because AddSuppressedSubject had run before a
    /// later step threw; validation now precedes ALL mutation.</summary>
    [Theory]
    [InlineData("lone-high")]
    [InlineData("lone-low")]
    [InlineData("over-long")]
    public async Task ARejectedEraseLeavesTheStoreByteIdentical(string clause)
    {
        var id = OperatorId(clause);
        if (clause is "lone-high") Assert.True(char.IsHighSurrogate(id[4]));
        if (clause is "lone-low") Assert.True(char.IsLowSurrogate(id[4]));
        if (clause is "over-long") Assert.True(id.Length > SubjectIdPolicy.MaxLength);

        var (runtime, _, root) = NewRuntime("byte-identical");
        using var _1 = runtime;
        // A store with real content at rest, including a dead-letter record
        // the scrub WOULD have rewritten had the command proceeded.
        runtime.Identity.RecordIngestedItem(new IngestedItem("PersonProfile-X", Datasets.PersonProfile, "h", DateTime.UtcNow));
        runtime.State.AddSuppressedSubject("ALT-ALREADY-ERASED");
        runtime.State.AddDeadLetter(new DeadLetterRecord
        {
            ItemId = "queued-item", Dataset = "person", DeliveryId = "d-1",
            Op = DeadLetterOps.Upsert, Error = "boom", PayloadJson = "{\"x\":1}",
            FailedUtc = DateTime.UtcNow,
        });

        var before = Snapshot(root);
        using (new StderrCapture())
        {
            var result = await CommandRegistry.ForgetSubjectAsync(runtime, id, null, "joseph", confirm: true);
            Assert.Equal(false, result);
        }
        var after = Snapshot(root);

        Assert.Equal(before.Keys.OrderBy(k => k, StringComparer.Ordinal),
                     after.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (path, bytes) in before)
            Assert.True(bytes.AsSpan().SequenceEqual(after[path]),
                $"file changed by a REJECTED erase-subject: {path}");
    }

    private static Dictionary<string, byte[]> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, ReadAllBytesShared, StringComparer.Ordinal);

    /// <summary>
    /// Read a file that another handle may still hold open. The runtime under test
    /// keeps its SQLite connection open, and on Windows — which enforces share
    /// modes, unlike POSIX — <see cref="File.ReadAllBytes"/> opens as
    /// <see cref="FileShare.Read"/>, which does not permit that writer's access, so
    /// snapshotting the store threw "the process cannot access the file".
    /// </summary>
    private static byte[] ReadAllBytesShared(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    [Fact]
    public async Task TheDryRunRefusesTooRatherThanPreviewingAnErasureThatCannotExecute()
    {
        // Dry-run is how an operator previews a DSAR erasure; a preview that
        // says "would erase" for an id the real run refuses wastes the legal
        // clock. Validation sits before the dry-run branch.
        var (runtime, _, _) = NewRuntime("dry-refuse");
        using var _1 = runtime;

        using var stderr = new StderrCapture();
        var result = await CommandRegistry.ForgetSubjectAsync(
            runtime, OperatorId("lone-high"), null, "joseph", confirm: false);

        Assert.Equal(false, result);
        Assert.Contains("forget-subject refused", stderr.Text);
    }

    [Fact]
    public void AWellFormedSupplementaryIdStillRoundTripsExactly()
    {
        // The fix must not have closed the door on legitimate astral-plane
        // ids — the domain excludes ILL-FORMED UTF-16, not non-BMP characters.
        var store = File_("surrogate-ok");
        var id = "ALT-😀-9001";

        store.AddSuppressedSubject(id);

        Assert.True(store.IsSubjectSuppressed(id));
        Assert.Equal(id, Assert.Single(store.ListSuppressedSubjects()));
    }

    [Fact]
    public async Task AWellFormedSupplementaryIdErasesEndToEnd()
    {
        // And through the whole command: a surrogate PAIR is valid operator
        // input, passes validation, and files exactly.
        var id = "ALT-😀-9001";
        Assert.True(StateContract.IsWellFormedUtf16(id));

        var (runtime, _, _) = NewRuntime("astral-ok");
        using var _1 = runtime;
        var result = await CommandRegistry.ForgetSubjectAsync(runtime, id, null, "joseph", confirm: true);

        Assert.Equal(true, result);
        Assert.True(runtime.State.IsSubjectSuppressed(id));
        Assert.Equal(id, Assert.Single(runtime.State.ListSuppressedSubjects()));
    }

    [Fact]
    public async Task AnEmailResolvedLegacyIdIsReplayOfStoredStateAndIsNotValidated()
    {
        // The caller-audit line that decides WHERE the validation lives:
        // `forget-subject --email` resolves subject ids from the CROSSWALK —
        // state written at ingest time, not operator input. A legacy
        // out-of-domain id stored there must remain erasable, or the DSAR for
        // that person can never complete (the round-8 wedge, one layer up).
        var legacyId = new string('L', SubjectIdPolicy.MaxLength + 44);
        var (runtime, _, _) = NewRuntime("email-replay");
        using var _1 = runtime;
        runtime.Identity.ReplaceCrmContacts(new[] { new CrmContact { Id = "C1", Email = "ada@x.com" } });
        runtime.Identity.UpsertCrosswalk(new CrosswalkEntry(legacyId, "C1", "email", DateTime.UtcNow));

        var result = await CommandRegistry.ForgetSubjectAsync(runtime, null, "ada@x.com", "joseph", confirm: true);

        Assert.Equal(true, result);                                // erasure COMPLETED
        Assert.True(runtime.State.IsSubjectSuppressed(legacyId));  // filed as stored
        Assert.Single(runtime.Erasure.ReadAll());
    }

    [Fact]
    public async Task UnsuppressSubjectCanRemoveALegacyOutOfDomainEntry()
    {
        // RemoveSuppressedSubject must never validate: inspecting and removing
        // a legacy bad entry is the operator's only way OUT of one.
        var legacyId = new string('L', SubjectIdPolicy.MaxLength + 44);
        var (runtime, _, _) = NewRuntime("unsuppress-legacy");
        using var _1 = runtime;
        runtime.State.AddSuppressedSubject(legacyId);              // store tolerates (replay shape)
        Assert.True(runtime.State.IsSubjectSuppressed(legacyId));

        var result = await CommandRegistry.UnsuppressSubjectAsync(runtime, legacyId, "joseph", confirm: true);

        Assert.Equal(true, result);
        Assert.False(runtime.State.IsSubjectSuppressed(legacyId));
    }

    [Fact]
    public async Task APaddedOperatorIdIsTrimmedNotRefused()
    {
        // The whitespace DECISION, pinned: forget-subject has always trimmed
        // `--id`, so padding is normalised rather than refused — there is no
        // whitespace clause in the policy, and no padded variant is ever filed
        // from operator input. Blank padding of LEGACY state stays open
        // (SQL_CONTRACT.md divergence (c)).
        var (runtime, _, _) = NewRuntime("trim");
        using var _1 = runtime;

        var result = await CommandRegistry.ForgetSubjectAsync(runtime, "  ALT-TRIM-1  ", null, "joseph", confirm: true);

        Assert.Equal(true, result);
        Assert.Equal("ALT-TRIM-1", Assert.Single(runtime.State.ListSuppressedSubjects()));
    }

    [Fact]
    public async Task AnIdAtExactlyTheColumnBoundIsAcceptedAndOneOverIsNot()
    {
        // The boundary itself, both sides: NVARCHAR(n) holds exactly n UTF-16
        // code units, so an id of exactly MaxLength must erase and one of
        // MaxLength + 1 must be refused. Also pins that the LENGTH checked is
        // the TRIMMED id's — the stored form — not the padded raw argument:
        // the at-bound id is passed wrapped in spaces.
        var atBound = new string('B', SubjectIdPolicy.MaxLength);
        var (runtime, _, _) = NewRuntime("bound-exact");
        using var _1 = runtime;

        var ok = await CommandRegistry.ForgetSubjectAsync(
            runtime, "  " + atBound + "  ", null, "joseph", confirm: true);
        Assert.Equal(true, ok);
        Assert.True(runtime.State.IsSubjectSuppressed(atBound));

        var oneOver = new string('B', SubjectIdPolicy.MaxLength + 1);
        using var stderr = new StderrCapture();
        var refused = await CommandRegistry.ForgetSubjectAsync(
            runtime, oneOver, null, "joseph", confirm: true);
        Assert.Equal(false, refused);
        Assert.False(runtime.State.IsSubjectSuppressed(oneOver));
        Assert.Contains("forget-subject refused", stderr.Text);
    }

    [Fact]
    public void ThePolicyBoundIsTheDdlsBoundNotASecondConstant()
    {
        // SubjectIdPolicy.MaxLength is parsed from SqlStateStore.SchemaScript
        // at runtime. StateContract.SubjectIdMax is pinned to the same DDL via
        // the ScriptDom AST (EveryBoundedColumnMatchesTheContractConstant), so
        // equality here chains policy == constant == shipped DDL: widening the
        // column cannot leave the validator behind.
        Assert.Equal(StateContract.SubjectIdMax, SubjectIdPolicy.MaxLength);
    }

    [Fact]
    public void TheStoreItselfStillToleratesWhatTheCommandRefuses()
    {
        // The store-level halves of the ORIGINAL open-defect pins, kept
        // deliberately: IStateStore.AddSuppressedSubject remains
        // NON-VALIDATING (replay tolerance — see
        // LegacyStateReadModifyWriteTests), so the file backend still rewrites
        // an unpaired surrogate to U+FFFD and still accepts an over-long id.
        // That behaviour is now UNREACHABLE from operator input — the command
        // layer refuses first — but pinning it keeps the tolerance deliberate
        // rather than accidental. SQL (NVARCHAR, UCS-2) would store the lone
        // surrogate verbatim; that half needs a live server and is NOT
        // executed here.
        var store = File_("store-tolerates");
        var surrogateId = "ALT-\uD83D-9001";
        Assert.False(StateContract.IsWellFormedUtf16(surrogateId));

        store.AddSuppressedSubject(surrogateId);                   // accepted, rewritten
        Assert.Equal("ALT-�-9001", Assert.Single(store.ListSuppressedSubjects()));
        Assert.False(store.IsSubjectSuppressed(surrogateId));      // inherent JSON limit, store-level

        var overLongId = new string('A', SubjectIdPolicy.MaxLength + 48);
        store.AddSuppressedSubject(overLongId);                    // accepted, unbounded
        Assert.True(store.IsSubjectSuppressed(overLongId));
    }
}

// ============================================================================
// The REGRESSION the withdrawn validation caused: read-modify-write over legacy
// state. These are the tests that go RED if the write-side validation is
// reinstated, in whole or in any single clause.
// ============================================================================

public class LegacyStateReadModifyWriteTests
{
    private static FileStateStore File_(string prefix)
    {
        var dir = TestFixtures.NewTempDir(prefix);
        return new FileStateStore("c1", Path.Combine(dir, "logs"), Path.Combine(dir, "data"));
    }

    // Clause names only. The VALUES are built inside the test from the name.
    //
    // This is not cosmetic. Passing the values through [MemberData] silently
    // broke the ill-formed cases: xUnit serialises theory arguments, and a lone
    // UTF-16 surrogate does not survive that — it arrives at the test body
    // already rewritten to U+FFFD, i.e. WELL-FORMED. Measured, not assumed:
    // a probe theory declaring "legacy-\uD83D-item" received a value for which
    // StateContract.IsWellFormedUtf16 returned TRUE and which compared UNEQUAL
    // to the same literal written in the test body. Every ill-formed row was
    // therefore exercising a well-formed string.
    private static string Value(string clause) => clause switch
    {
        "too-long" => new string('L', StateContract.ItemIdMax + 44),
        "ill-formed-lone-high" => "legacy-\uD83D-item",
        "ill-formed-lone-low" => "\uDC00-legacy-item",
        "padded-trailing" => "legacy-item ",
        "padded-leading" => " legacy-item",
        "empty" => "",
        _ => throw new ArgumentOutOfRangeException(nameof(clause), clause, "unmapped clause"),
    };

    /// <summary>The clauses a legacy dead-letter QUEUE FILE can actually hold,
    /// one per clause, so reinstating any SINGLE clause of the withdrawn
    /// validation turns a test red rather than the set being covered only by
    /// whichever shape is checked first.
    ///
    /// The ill-formed clauses are deliberately absent HERE and covered in
    /// <see cref="TheKeyedWritePathsAcceptOutOfDomainValues"/> instead: the file
    /// backend physically cannot write a lone surrogate into the JSONL, so no
    /// legacy queue file can contain one. Measured — System.Text.Json emits the
    /// six-character escape \uFFFD for it, and the line reads back
    /// well-formed — rather than assumed.</summary>
    public static TheoryData<string> QueueFileClauses() => new()
    { "too-long", "padded-trailing", "padded-leading", "empty" };

    /// <summary>Every clause, for the ports that take the value straight from
    /// the caller and so can see an ill-formed one.</summary>
    public static TheoryData<string> AllClauses() => new()
    {
        "too-long", "ill-formed-lone-high", "ill-formed-lone-low",
        "padded-trailing", "padded-leading", "empty",
    };

    private static DeadLetterRecord Legacy(string itemId) => new()
    {
        ItemId = itemId, Dataset = "person", DeliveryId = "d-legacy",
        Op = DeadLetterOps.Upsert, Error = "queued before the contract existed",
        PayloadJson = "{\"legacy\":true}", FailedUtc = DateTime.UtcNow,
    };

    private static DeadLetterRecord Victim() => new()
    {
        ItemId = "ALT-VICTIM", Dataset = "person", DeliveryId = "d-victim",
        Op = DeadLetterOps.Upsert, Error = "boom",
        PayloadJson = "{\"netWorth\":\"1200000000\"}", FailedUtc = DateTime.UtcNow,
        SubjectIds = new[] { "ALT-SUBJ-1" },
        SubjectHashes = new[] { DeadLetterPolicy.HashSubject("ALT-SUBJ-1") },
    };

    /// <summary>Seed a queue file DIRECTLY, the way a build predating any
    /// validation left it — bypassing the write path, because the point is that
    /// such a file exists on operators' disks.</summary>
    private static void SeedLegacyQueue(FileStateStore store, params DeadLetterRecord[] records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(store.DeadLetterPath)!);
        File.WriteAllLines(store.DeadLetterPath,
            records.Select(r => System.Text.Json.JsonSerializer.Serialize(r)));
    }

    /// <summary>THE REGRESSION. A queue holding one legacy out-of-domain record
    /// must not wedge the forget-subject dead-letter scrub: the scrub is an
    /// atomic read-modify-write, so validating the write side while leaving the
    /// read side unfiltered made the whole queue unwritable and left the erased
    /// subject's payload AT REST while the subject was already marked
    /// suppressed.</summary>
    [Theory]
    [MemberData(nameof(QueueFileClauses))]
    public void TheForgetSubjectScrubCompletesOverALegacyQueue(string clause)
    {
        var legacyItemId = Value(clause);
        var store = File_("scrub-legacy");
        SeedLegacyQueue(store, Legacy(legacyItemId), Victim());

        // The seeded record really does carry the out-of-domain value: the file
        // round trip preserves it, so the test cannot pass by testing nothing.
        Assert.Equal(2, store.ReadDeadLetters().Count);
        Assert.Contains(store.ReadDeadLetters(), r => r.ItemId == legacyItemId);
        Assert.Contains("1200000000", File.ReadAllText(store.DeadLetterPath));

        // The erasure's two halves, in the order CommandRegistry runs them.
        store.AddSuppressedSubject("ALT-SUBJ-1");

        var erasedHashes = new[] { DeadLetterPolicy.HashSubject("ALT-SUBJ-1") }
            .ToHashSet(StringComparer.Ordinal);
        store.MutateDeadLetters(current => current
            .Where(q => !(q.Op != DeadLetterOps.Delete
                          && (q.SubjectHashes.Any(erasedHashes.Contains)
                              || q.SubjectIds.Contains("ALT-SUBJ-1"))))
            .ToList());

        // The erasure COMPLETED: the payload is gone from DISK, not merely from
        // the returned list.
        Assert.DoesNotContain("1200000000", File.ReadAllText(store.DeadLetterPath));
        Assert.True(store.IsSubjectSuppressed("ALT-SUBJ-1"));

        // ...and the unrelated legacy record was neither lost nor mangled.
        Assert.Equal(legacyItemId, Assert.Single(store.ReadDeadLetters()).ItemId);
    }

    /// <summary>The same shape through the other batch write paths, since
    /// retry-failed's finalize goes through ReplaceDeadLetters and its attempt
    /// bump through MutateDeadLetters. A record READ from the queue must be
    /// writable back UNCHANGED — that is the asymmetry that wedged.</summary>
    [Theory]
    [MemberData(nameof(QueueFileClauses))]
    public void ALegacyRecordSurvivesEveryWriteBackPath(string clause)
    {
        var legacyItemId = Value(clause);
        foreach (var port in new[] { "Replace", "Mutate", "Add" })
        {
            var store = File_("writeback-" + port);
            SeedLegacyQueue(store, Legacy(legacyItemId));
            var read = store.ReadDeadLetters();
            Assert.Equal(legacyItemId, Assert.Single(read).ItemId);

            switch (port)
            {
                case "Replace": store.ReplaceDeadLetters(read); break;
                case "Mutate": store.MutateDeadLetters(_ => read); break;
                case "Add": store.ClearDeadLetters(); store.AddDeadLetters(read); break;
            }

            Assert.Equal(legacyItemId, Assert.Single(store.ReadDeadLetters()).ItemId);
        }
    }

    /// <summary>The keyed write paths the same validation guarded, per clause
    /// and per port, so reinstating it on any ONE of them is caught. These take
    /// the value straight from the caller, so the ill-formed clauses are
    /// reachable here.</summary>
    [Theory]
    [MemberData(nameof(AllClauses))]
    public void TheKeyedWritePathsAcceptOutOfDomainValues(string clause)
    {
        var value = Value(clause);
        var store = File_("keyed");

        store.AddSuppressedSubject(value);
        store.RemoveSuppressedSubject(value);
        store.MarkDeliveryProcessed(value, DateTime.UtcNow);
        store.SetValue(value, "v");
        store.MutateValue(value, _ => "v2");
        store.SetLastSync(value, DateTime.UtcNow);
        store.SaveCheckpoint(new CrawlCheckpoint
        { DeliveryId = value, Dataset = value, FileName = value, RecordIndex = 1 });

        // Reaching here at all is the guarantee under test: NONE of those seven
        // write paths refuses the value. Each one threw before the revert.

        // The query side must not short-circuit to "absent" on the value's SHAPE
        // either — that short-circuit was only sound while the write side
        // refused the value, and it is what would make a legacy entry
        // invisible. It is asserted only for values the FILE backend can
        // actually store: for the ill-formed clauses the backend rewrites the
        // key to U+FFFD on save, so the value genuinely is not there under the
        // id it was filed with. That store-level rewrite is inherent to the
        // JSON backend and remains — the delivery-ledger and KV namespaces
        // have the same shape as the suppression list — but for SUBJECT IDS it
        // is no longer reachable from operator input: forget-subject validates
        // `--id` at the command entry (SubjectIdPolicy). The store tolerance
        // itself is pinned in
        // SuppressionSurrogateTests.TheStoreItselfStillToleratesWhatTheCommandRefuses,
        // not silently assumed here.
        if (StateContract.IsWellFormedUtf16(value))
        {
            Assert.True(store.IsDeliveryProcessed(value));
            Assert.NotNull(store.GetLastSync(value));
            Assert.Equal("v2", store.GetValue(value));
        }
        else
        {
            Assert.False(store.IsDeliveryProcessed(value));   // inherent JSON-backend rewrite (unguarded namespace)
        }
    }
}
