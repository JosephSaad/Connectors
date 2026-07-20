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
//   3. The two divergences the withdrawal RE-OPENED, pinned as tests that
//      assert the defective behaviour they currently have, so the doc
//      describing them as OPEN stays checkable — see
//      SuppressionSurrogateTests.OPEN_DEFECT_A / OPEN_DEFECT_B.
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
// MAJOR 1, end to end: the exact reported id
// ============================================================================

public class SuppressionSurrogateTests
{
    private static FileStateStore File_(string prefix)
    {
        var dir = TestFixtures.NewTempDir(prefix);
        return new FileStateStore("c1", Path.Combine(dir, "logs"), Path.Combine(dir, "data"));
    }

    [Fact]
    public void OPEN_DEFECT_A_AnUnpairedSurrogateSubjectIdIsSilentlyRewrittenOnFile()
    {
        // KNOWN, ACCEPTED, OPEN — see StateContract.cs (a) and
        // docs/SQL_CONTRACT.md. A round that closed this by REJECTING the id at
        // the write boundary wedged DSAR erasure over legacy state and was
        // withdrawn. This test pins the defect's CURRENT observable shape so it
        // cannot change without someone noticing, and so the documentation
        // describing it stays checkable.
        //
        // System.Text.Json cannot encode a lone surrogate and substitutes
        // U+FFFD, so the id filed is not the id asked for.
        var store = File_("surrogate-open");
        var id = "ALT-\uD83D-9001";

        store.AddSuppressedSubject(id);          // accepted, no longer refused

        var listed = Assert.Single(store.ListSuppressedSubjects());
        Assert.NotEqual(id, listed);             // filed under a DIFFERENT id
        Assert.Equal("ALT-\uFFFD-9001", listed);

        // The consequence: the erasure reports success, the subject stays
        // ingestible, on the SAME store instance.
        Assert.False(store.IsSubjectSuppressed(id));

        // SQL (NVARCHAR, UCS-2) would store the lone surrogate verbatim and
        // answer true. That half needs a live server and is NOT executed here.
    }

    [Fact]
    public void OPEN_DEFECT_B_AnOverLongSubjectIdIsAcceptedByTheFileBackend()
    {
        // KNOWN, ACCEPTED, OPEN — see StateContract.cs (b). subject_id is
        // NVARCHAR(256) on SQL; the file backend has no bound, so this erasure
        // succeeds here and raises SQL error 8152 on the SQL backend, which is
        // not in TransientErrorNumbers and is therefore rethrown unretried.
        var store = File_("overlong-open");
        var id = new string('A', StateContract.SubjectIdMax + 48);

        store.AddSuppressedSubject(id);

        Assert.True(store.IsSubjectSuppressed(id));
        Assert.Equal(id, Assert.Single(store.ListSuppressedSubjects()));
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
        // id it was filed with. That is open defect (a) — the delivery-ledger
        // and KV namespaces have the same shape as the suppression list — and
        // it is pinned in SuppressionSurrogateTests, not silently tolerated
        // here.
        if (StateContract.IsWellFormedUtf16(value))
        {
            Assert.True(store.IsDeliveryProcessed(value));
            Assert.NotNull(store.GetLastSync(value));
            Assert.Equal("v2", store.GetValue(value));
        }
        else
        {
            Assert.False(store.IsDeliveryProcessed(value));   // open defect (a)
        }
    }
}
