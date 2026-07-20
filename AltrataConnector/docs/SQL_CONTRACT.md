# SQL_CONTRACT — SQL Server state backend

`USE_SQL_SERVER=true` + `SQL_CONNECTION_STRING` moves **all** connector state
into SQL Server. This is the contract the connector creates and relies on
(schema is auto-created on first use; idempotent `IF OBJECT_ID ... IS NULL`).

## Canonical DDL & offline validation

`scripts/sql/create-database.sql` is the canonical, sqlcmd-runnable copy of
the DDL (used by docker-compose's `mssql-init` and by ops for pre-created
databases); `scripts/sql/create-login.sql` provisions an optional
least-privilege login. The embedded runtime constants
(`SqlStateStore.SchemaScript`, `SqlServerIdentityStore.SchemaScript`) must
stay byte-equivalent — the offline validation suite
(`tests/SqlScriptValidationTests.cs`) enforces this without a live server:

1. the script parses cleanly under the real SQL Server 2019 grammar
   (`TSql150Parser`);
2. every DDL statement is idempotent by construction (existence-guarded
   CREATEs) — the re-run/upgrade safety CI also proves live by provisioning
   the schema twice;
3. no drift between the script and the embedded constants, and every
   `dbo.altrata_*` table the C# touches exists in the script;
4. a DacFx semantic model builds and validates the declarative schema.

## Connection

* `SQL_USE_MANAGED_IDENTITY=true` appends
  `Authentication=Active Directory Default` when the connection string does
  not already specify an auth mode.
* `SQL_MAX_RETRIES` (default 3) wraps every command in a transient-fault retry
  loop (error numbers −2, 4060, 40197, 40501, 40613, 49918-49920, 11001;
  exponential backoff capped at 30 s).

## Tables (all scoped by `connector_id` so connectors can share a database)

State (`SqlStateStore`):

| Table | Purpose |
|---|---|
| `dbo.altrata_checkpoint` | one row per connector: crawl resume position |
| `dbo.altrata_deadletter` | dead-letter queue (payload JSON replayable by retry-failed; `op` distinguishes upsert vs delete replays; `correlation_id` carries the cycle id `CrawlEngine.StampCorrelation` stamps — guarded ALTERs migrate v1/v2 tables) |
| `dbo.altrata_kv` | seat-list hash, billable-lookup counter, last-sync timestamps, per-delivery processed timestamps |
| `dbo.altrata_deliveries` | processed-delivery ledger |
| `dbo.altrata_leases` | HA lease table (lease_name PK, owner, expires_utc) |
| `dbo.altrata_suppressed` | erased subject ids — durable suppression against re-delivery (forget-subject); `subject_id` is `COLLATE Latin1_General_100_BIN2`, see *Collation* below |

Identity (`SqlServerIdentityStore`):

| Table | Purpose |
|---|---|
| `dbo.altrata_id_seats` | licensed seat principals (kind, value) |
| `dbo.altrata_id_crm_contacts` | normalized CRM contacts for entity resolution (incl. `role_normalized` fuzzy-tier hint; guarded ALTER migrates v1 tables) |
| `dbo.altrata_id_crosswalk` | altrata_id ↔ crm_contact_id (+ match rule, linked_utc) |
| `dbo.altrata_id_items` | ingested-item registry (item_id, dataset, acl_hash, last_ingested_utc) — drives re-ACL and purge |
| `dbo.altrata_id_path_edges` | relationship-path adjacency (RELATIONSHIP_PATHS) — rebuilt per crawl |
| `dbo.altrata_id_path_orgs` | person→org memberships feeding topConnectedOrgs |
| `dbo.altrata_id_item_subjects` | item↔subject reverse index — finds every item for a person during erasure |

## Semantics

* Upserts are `MERGE` statements keyed on connector_id (+ natural key).
* The billable-lookup counter increments atomically in SQL
  (`TRY_CAST` + MERGE), so parallel nodes never lose counts.
* `purge-all --confirm` deletes only this connector's rows
  (`WHERE connector_id = @cid`); other connectors sharing the database are
  untouched.
* File-mode equivalents (JSON/JSONL/SQLite under `logs/` and `data/`) carry
  identical semantics **only over the values both backends can store, return
  and compare identically** — which is NOT every .NET string. Read *Value
  domain* below before switching: operator-supplied subject ids are validated
  at the erase-subject entry point, but the other divergences there are OPEN.
  Switching backends is a config change, not a code change — but state does
  NOT migrate automatically between backends.


## Value domain

The two backends are interchangeable only over the values both can store,
return and compare identically. That set is **smaller than "any .NET string"**,
and the difference is absorbed silently by whichever backend is configured:

| Property | File backend | SQL backend |
|---|---|---|
| Unpaired UTF-16 surrogate | `System.Text.Json` rewrites it to **U+FFFD** | `NVARCHAR` is UCS-2 and stores it **verbatim** |
| Length | unbounded | `NVARCHAR(n)`; overflow raises **error 8152**, which is not transient and is not retried |
| Trailing spaces / empty string | ordinal — `ALT-1` ≠ `ALT-1 `, `''` ≠ `' '` | `=` blank-pads — `ALT-1` **=** `ALT-1 `, `''` **=** `' '` |
| `DateTimeKind` on a timestamp | round-trips `Utc` through ISO-8601 `o` | `DATETIME2` carries no Kind; `GetDateTime` returns `Unspecified` |

### Where each divergence stands

For **subject ids** the surrogate and length rows are now guarded — **at the
erase-subject entry point, not on state writes**: `forget-subject` validates
the operator-supplied `--id` (well-formed UTF-16; length against the DDL's
declared `subject_id` width, parsed from the shipped schema at runtime) at the
very start of the command, before any mutation, and refuses with an actionable
error (`Commands/SubjectIdPolicy.cs`). Ids replayed from stored state — the
crosswalk via `--email`, legacy suppression entries, every dead-letter field —
are deliberately **not** validated, and the state stores themselves still
validate nothing. Do not read this as the earlier write-side validation
reinstated; that validation was withdrawn because it was a regression worse
than the defects it closed:

> Reads are deliberately unfiltered — a dead-letter line written before the
> validation existed must still read back, or a legacy value becomes a LOST
> failure record. But every WRITE canonicalised the whole batch all-or-nothing.
> A queue holding one legacy out-of-domain record — which the file backend,
> having no length bound, had accepted — could therefore be READ but not
> WRITTEN BACK UNCHANGED, and every read-modify-write over it was wedged.
> `forget-subject`'s dead-letter scrub threw before writing anything, so the
> erased subject's payload stayed on disk while `AddSuppressedSubject` had
> already marked the subject suppressed: **a DSAR erasure left half-applied**.
> Nothing under `src/` caught `StateContractViolation`. `retry-failed`'s
> finalize and attempt-bump wedged identically.

Issue status, per shape:

| # | Issue | Status |
|---|---|---|
| **(a)** | An unpaired UTF-16 surrogate in a **subject id** is silently rewritten to U+FFFD by the **file** backend (`System.Text.Json` on save): the erasure would be filed under a **different id**, `IsSubjectSuppressed` with the same string would return **false**, and the subject would stay ingestible while SQL stored the code unit verbatim and answered **true**. | **Closed for operator input**: `forget-subject` refuses an ill-formed `--id` before any mutation. The **store-level rewrite still exists** for ids replayed from stored state (pinned by `SuppressionSurrogateTests.TheStoreItselfStillToleratesWhatTheCommandRefuses`), and the delivery-ledger/KV namespaces share the same JSON-backend shape with no guard. |
| **(b)** | `NVARCHAR` bounds on SQL, **no bound on file**: an over-long value succeeds on file and raises **SQL error 8152** on SQL (not in `TransientErrorNumbers`, rethrown without retry). | **Closed for operator-supplied subject ids**: `forget-subject` refuses an `--id` over the DDL's declared `subject_id` width before any mutation. **OPEN for everything else** — `item_id`, `delivery_id`, `[key]`, `dataset` and replayed subject ids remain unvalidated on every path. |
| **(c)** | Blank padding: SQL's `=` folds trailing spaces, ordinal comparison does not. | **OPEN.** `ALT-1` and `ALT-1 ` may be one subject on SQL and are always two on file; `''` and `' '` likewise. Operator-typed ids are trimmed by the commands (a normalisation, not a rejection); values already in state are untouched. |

Upstream normalisation is still worth doing for ids that reach the connector by
**replay** rather than by an operator (`--email` resolution files whatever the
crosswalk stored). Verifying an erasure with `list-suppressed-subjects` remains
good practice; with the entry-point validation in place the listed id is the id
you submitted, byte for byte, or the command refused.

What DOES still happen at the boundary, and cannot fail:

* **Free text is normalised.** The `NVARCHAR(MAX)` fields (`error`,
  `payload_json`, KV values) have unpaired surrogates replaced with U+FFFD —
  exactly what the file backend's JSON writer already did — so the SQL backend
  persists the same replacement and the two agree. A dead-letter write must not
  be able to throw over an error *message*: that would turn a recoverable item
  failure into a lost failure record.
* **Timestamps are stamped `Utc`** on write and on read by both backends.
* **Reads are not filtered.** State written by any earlier build reads back.

The `StateContract` constants remain, and every bounded `NVARCHAR` column in
the schema must still have a matching one, read off the parsed AST, so widening
a column without widening the constant fails the suite. Note what this is and
is not: the constants **document** the SQL schema and are checked against it.
They are not enforced against values on any state write. The one place a bound
IS enforced — the erase-subject operator entry — parses its width off the
shipped DDL itself (`SubjectIdPolicy.MaxLength`), and a test pins it equal to
`StateContract.SubjectIdMax`, so the validator cannot drift from the schema.

### What is NOT verified here

There is no SQL Server in the test environment and no container runtime to
start one, so **no test executes a query against a live server**. The SQL side
of every claim above is verified against the parsed DDL and the parsed
statements — real artefacts. The file backend is executed for real, on disk:
the read-modify-write regression above is covered per rejected clause by
`LegacyStateReadModifyWriteTests`; the entry-point validation is covered by
`SuppressionSurrogateTests.FIXED_DEFECT_A` / `FIXED_DEFECT_B` (refusal, per
clause, with byte-identical state proven after a rejected erase) and the
store-level tolerance by `TheStoreItselfStillToleratesWhatTheCommandRefuses`,
so this document stays checkable.

What remains unproven: that a live server behaves as its declared collation and
declared widths say, and **the SQL half of issues (a), (b) and (c)** — the
verbatim-surrogate storage, the 8152 rethrow, and blank padding are all stated
from SQL Server semantics and the declared DDL, not executed. That needs an
integration environment.

## Collation

**Every column this store compares by equality** declares an explicit binary
collation (`Latin1_General_100_BIN2`) instead of inheriting the database
default: `altrata_suppressed.subject_id`, `altrata_kv.[key]`,
`altrata_deliveries.delivery_id`, `altrata_leases.lease_name`, and the
`connector_id` of every table (it is in the `WHERE` of essentially every
statement, so it decides identity as much as the natural key beside it).
Comparisons against `subject_id` additionally name the collation at the
comparison site (`subject_id = @s COLLATE Latin1_General_100_BIN2`); the other
columns rely on the column collation, which `EnsureSchema` migrates before any
command can run.

This is a correctness requirement, not a preference. The file backend compares
suppressed subject ids with `StringComparer.Ordinal`; a stock SQL Server
install defaults to `SQL_Latin1_General_CP1_CI_AS`, which is **case-insensitive**.
With the default, the two backends disagreed about whether a subject had been
erased — and this is the DSAR erasure list:

* `ALT-9001` and `alt-9001` are two subjects on file and one on SQL. Filing the
  second erasure hit the case-insensitive `IF NOT EXISTS` guard, was dropped
  with no error, and that subject stayed ingestible.
* A subject nobody erased could answer "suppressed" on SQL, so a fleet running
  both backends produced different ingest sets from one erasure ledger.

**Upgrading.** The schema carries a guarded `ALTER COLUMN` that migrates tables
provisioned before the collation was pinned; it runs automatically via
`EnsureSchema` and is a no-op once the column is BIN2. The direction is always
safe — a case-insensitive primary key could never have held two rows differing
only by case, so tightening cannot raise a duplicate-key error. It requires
`ALTER` on the table (already needed by the existing `altrata_deadletter`
migrations).

> The migration does **not** recover erasures the insensitive key silently
> swallowed at insert time. After upgrading, re-file suppressions from the
> tamper-evident erasure ledger (`docs/ERASURE.md`) to close that gap.

`ListSuppressedSubjects` re-sorts ordinally in the client: BIN2 orders by code
point and the file backend by UTF-16 code unit, which differ only for
supplementary characters, and operators diff the two lists node-against-node
during DR.

## Known residuals

* **Ambiguous commit.** Non-idempotent writes (`MutateValue`,
  `IncrementBillableLookups`, `MutateDeadLetters`, `AddDeadLetter`,
  `AddDeadLetters`) are fenced by `CommitGuard`, so a transient fault raised
  *after* a successful commit no longer replays the operation. If the commit
  call **itself** throws, the outcome is genuinely ambiguous — the server may
  have committed before the connection dropped — and no client-side flag can
  resolve it. Closing this needs a transactional idempotency key per write;
  `dbo.altrata_deadletter` is keyed by a bare `IDENTITY` today, so that is a
  schema change with its own migration.

  **Assessed and deliberately deferred.** The residual is now confined to
  `AddDeadLetter` (singular), which runs in autocommit with no transaction, and
  to the `Commit()` call itself on the transactional paths. Its blast radius is
  a *duplicate* dead-letter row: the queue an operator triages is inflated and
  one failed item can consume two of the queue's bounded slots. It is not data
  loss, and it is not a correctness defect on the erasure path. A dedupe token
  would have to be caller-supplied and stable across a retry — which means
  threading it from `CrawlEngine` through `DeadLetterPolicy` — plus a unique
  index and a migration, to convert a rare over-count into a rare no-op. The
  cost is not justified by the exposure, and the exposure is stated here rather
  than being quietly absorbed. Revisit if operators report duplicate queue
  entries in practice.
* **Collation migration of two inline primary keys.**
  `altrata_checkpoint.connector_id` and `altrata_leases.lease_name` declare the
  binary collation in `CREATE TABLE`, so **new** deployments are correct, but
  they carry INLINE UNNAMED `PRIMARY KEY` constraints — migrating an already
  deployed table needs dynamic SQL to discover the auto-generated constraint
  name, which cannot be tested here. Existing deployments keep the database
  default on those two columns. Impact is bounded: both are
  connector/deployment-supplied single values, so a case variant is a
  misconfiguration rather than a data-driven divergence. The other five
  comparison-key columns (`altrata_kv`, `altrata_deliveries`,
  `altrata_suppressed`, `altrata_deadletter.connector_id`) DO carry guarded
  migrations.
* **Rollback-after-commit — fixed in source; guarded at the IL level, not
  behaviourally.** Two dead-letter write paths ran
  `catch { txn.Rollback(); throw; }`. `SqlTransaction.Rollback` throws
  `InvalidOperationException` on a completed or broken transaction, so a fault
  raised *by* `Commit()` had its `SqlException` replaced — matching neither
  handler in `Execute`, meaning the retry decision was never made (an
  uncommitted batch was **not** retried and the dead letters were lost) and the
  failure was never logged. Both now use `using var txn` with no catch, as
  `MutateValue` always did: `Dispose` rolls back an uncommitted transaction and
  is a no-op on a completed one.

  This entry previously read "Rollback-after-commit, closed." and sat beside
  residuals whose limits are spelled out, which overstated it. The limits, in
  full:

  * The **source** is correct: no executable `Rollback` call remains anywhere
    in `src/`, and both dead-letter paths use `using var txn` with no catch.
  * The **guard** is `RollbackMaskingIlGuardTests`, which asserts on the
    compiled IL — an exhaustive scan of every byte offset of every method in
    `SqlStateStore` (closure classes included) for a `call`/`callvirt` to
    `Rollback`, plus a per-path assertion of zero catch clauses and zero
    exception filters in the compiled transactional body. IL carries no
    formatting, so no reformatting of the defect can evade it; this was
    confirmed by reinstating the defect in three layouts and observing red.
  * Until this round the **only** barrier was an evadable source regex
    (`@"^\s*\w+\.Rollback\(\)"`, `Multiline`) that matched only a `Rollback`
    beginning a line. The one-line form of the identical defect left the full
    suite green. That is why "closed" was the wrong word.
  * **Still not covered:** nothing executes `AddDeadLetters` or
    `MutateDeadLetters`. They run through `Execute`, which opens a real
    `SqlConnection`; there is no SQL Server on the build host, no container
    runtime to start one, and `SqlException` cannot be constructed by a test.
    So the operational property — that a transient `SqlException` reaches the
    executor unmasked and `ShouldRetry` is consulted — is **not** verified
    end to end against `SqlClient`. It is verified that the code shape which
    broke it cannot return. The masking mechanism itself is demonstrated on
    `Microsoft.Data.Sqlite` in `TransactionRollbackMaskingTests`, whose
    transaction semantics reproduce it but which is a different provider.

## Minimum permissions

`CREATE TABLE` on first run (or pre-create the tables above), then
`SELECT / INSERT / UPDATE / DELETE` on the eight tables, plus `ALTER` on
`dbo.altrata_suppressed` and `dbo.altrata_deadletter` for the guarded column
migrations.
