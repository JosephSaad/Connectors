# Changelog

All notable changes to the Altrata Copilot Connector. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[SemVer](https://semver.org/). Version bumps touch THREE files together:
`src/AltrataConnector/AltrataConnector.csproj` (`<Version>`),
`packaging/msi/Package.wxs` (`Package/@Version` — drift is test-enforced),
and this file.

## [Unreleased]

### Changed — release automation now runs, and the tag format changed

The release pipeline is wired to the repository root and produces releases for
real. It had been authored under `AltrataConnector/.github/workflows/release.yml`
— one level below the only directory GitHub executes workflows from — so no tag
had ever triggered it and no bundle, GHCR image or SBOM was ever published by
it. The logic now lives once in `.github/workflows/release-connector.yml`,
called by `.github/workflows/release-altrata.yml`.

- **Release tags are now `altrata-v1.2.0`, not `v1.2.0`.** The five connectors
  share one repository and version independently; an unprefixed tag would start
  all five pipelines against a single tag, and only the first to finish could
  create the release.
- Signing secrets moved to the fleet-wide set: `AUTHENTICODE_PFX_BASE64` /
  `AUTHENTICODE_PFX_PASSWORD` for the win-x64 binary, `COSIGN_PRIVATE_KEY` /
  `COSIGN_PASSWORD` for the image — this connector spelled the first
  `AUTHENTICODE_PFX_B64`. All optional; signing steps skip with a notice and the
  release ships unsigned.
- The MSI build passes no version define, because `packaging/msi/Package.wxs`
  carries its own `Package/@Version` and the drift against the csproj is
  test-enforced (see the header of this file).
- `workflow_dispatch` runs the identical build → smoke-test → package path as a
  dry run: nothing is pushed and no release is created.

### Changed — the connector consumes the shared `Connector.Chassis` project

This connector now lives in the `JosephSaad/Connectors` monorepo alongside the
other four (Salesforce, Clarizen, Seismic, Hadoop) and the chassis itself.

* **Project reference, not a package.** `src/AltrataConnector/AltrataConnector.csproj`
  carries a `<ProjectReference>` to `../../../Connector.Chassis/Connector.Chassis.csproj`
  (chassis **1.13.1**). There is no `PackageReference`, no version pin, no pack
  step and no feed; the connector's `nuget.config` — which hardcoded an absolute
  local path and made the repository unbuildable by anyone else — is deleted.
* **What is shared:** the `Chassis` / `ChassisIdentity` seams, `ServiceStop`, and
  logging (`Logging` / `IAppLogger`), with `AltrataLogDialect` preserved as a
  custom dialect over the chassis `StandardLogDialect`. Altrata keeps its own
  secret provider; the decision ledger, HA coordinator, SQL state store,
  alerting, Event Log sink, log pruner, service host, env flags and circuit
  breaker all remain connector-local.
* **CI moved to the repository root**: `.github/workflows/altrata.yml` builds and
  runs the suite on `ubuntu-latest` **and** `windows-latest` (743 tests green on
  both) and builds the container image. Releases are `release-altrata.yml`
  there too. The files under this connector's own `.github/workflows/`, inert
  since the consolidation, have been deleted.
* **Docker build context is the repository root**, because the image has to reach
  `../Connector.Chassis`: `docker build -f AltrataConnector/Dockerfile .`.
  `docker-compose.yml` sets `context: ..`, and a single `.dockerignore` at the
  repository root governs the build (the per-connector one was removed — Docker
  only reads the one at the context root).

### Fixed — refusal messages cap the rendered id at 64 units on BOTH branches

The hostile verifier fed `SubjectIdPolicy.Explain` an ill-formed id of 100,001
units and got a 100,800-character error message: the ill-formed branch rendered
the offending id uncapped while the over-long branch already truncated at 64.
Escaping held (no raw surrogate reached the console), so this was log bloat,
not a leak — but an error message whose size is attacker-controlled is still
wrong. Both branches now render `Render(subjectId, 64)`; pinned red-then-green
by `SuppressionSurrogateTests.ARefusalMessageStaysSmallHoweverLargeTheOffendingId`
(both clauses).

### Fixed — subject-id validation at the erase-subject ENTRY POINT (closes the re-opened (a)/(b) below)

The two subject-id divergences the revert below re-opened are closed again —
**at the operator entry point, not on state writes**. This is not the withdrawn
write-side validation reinstated; the placement is the whole fix:

* **Where it validates**: `forget-subject` checks the operator-supplied `--id`
  (`Commands/SubjectIdPolicy.cs`) at the **very start of the command, before
  any state mutation** — before the suppression add, the dead-letter scrub,
  the withdrawals and the ledger append — so a refused erasure leaves every
  store **byte-identical** (test-proven per clause:
  `SuppressionSurrogateTests.ARejectedEraseLeavesTheStoreByteIdentical`). The
  dry-run refuses too, so a preview cannot promise an erasure the real run
  would reject.
* **What it validates, and no more**: ill-formed UTF-16 (an unpaired surrogate
  would be rewritten to U+FFFD by the file backend, filing the erasure under a
  different id while SQL stored it verbatim — a silent cross-backend
  divergence on DSAR evidence), and length against the DDL's declared
  `subject_id` width. The bound is **parsed from the shipped
  `SqlStateStore.SchemaScript` at runtime**, not hardcoded a second time, and
  a test pins it equal to `StateContract.SubjectIdMax` (itself AST-paired to
  the DDL). Whitespace is trimmed (as the command always did), not refused.
  Well-formed non-BMP ids (surrogate pairs) remain valid operator input.
* **Where it deliberately does NOT validate** — the round-8 lesson: ids
  resolved from the crosswalk via `--email` (replay of stored state; a legacy
  out-of-domain id must remain erasable or that person's DSAR can never
  complete), `unsuppress-subject` / `RemoveSuppressedSubject` (an operator
  must be able to remove a legacy bad entry), `IsSubjectSuppressed`,
  `ListSuppressedSubjects`, every dead-letter path, and the state stores'
  write methods themselves. `LegacyStateReadModifyWriteTests` still pins all
  of that tolerance, and the store-level U+FFFD rewrite — now unreachable from
  operator input — stays pinned by
  `SuppressionSurrogateTests.TheStoreItselfStillToleratesWhatTheCommandRefuses`.
* **The refusal is actionable**: it names the offending code unit and index
  (or the length and the column bound, including the 8152 consequence),
  renders the id escaped — no raw unpaired surrogate is ever printed — and
  tells the operator how to proceed with the DSAR.
* The former `OPEN_DEFECT_A` / `OPEN_DEFECT_B` pins are **rewritten** as
  `FIXED_DEFECT_A` / `FIXED_DEFECT_B`, asserting the refusal end-to-end
  through `CommandRegistry.ForgetSubjectAsync`.

Still open, unchanged by this: blank padding of values already in state
(divergence (c)), the length bounds on `item_id` / `delivery_id` / `[key]` /
`dataset` (connector-generated or replayed, not operator-typed), the
connector-id case divergence on unmigrated inline primary keys, and the IL-only
scope of the rollback-masking guard. `docs/SQL_CONTRACT.md` and
`docs/ERASURE.md` carry the updated status.

### Reverted — the state-backend boundary validation (it was a regression)

The previous round added write-side validation in `State/StateContract.cs`: an
identifier or key outside the SQL columns' accepted domain (bounded length, no
unpaired UTF-16 surrogates, no blank padding) was REJECTED with a thrown
`StateContractViolation` at the boundary of both backends, before any I/O.
**That has been withdrawn in full.** It made DSAR erasure worse than the defects
it closed.

Reads were deliberately unfiltered, so legacy state still read back — but every
WRITE canonicalised the whole batch all-or-nothing. A dead-letter queue holding
a single legacy out-of-domain record (which the file backend, having no length
bound, had accepted) could be READ but not WRITTEN BACK UNCHANGED, so every
read-modify-write over it was wedged:

* `forget-subject`'s dead-letter scrub threw before writing anything, leaving
  the erased subject's payload **on disk** — while `AddSuppressedSubject` had
  already run, so the erasure was left **HALF-APPLIED**: subject marked
  suppressed, payload still at rest.
* No file under `src/` caught `StateContractViolation`, so this surfaced as an
  unhandled throw.
* `retry-failed`'s finalize and attempt-bump wedged the same way.

Withdrawn rather than replaced: a DSAR erasure that cannot complete is worse
than a silent divergence. `StateContractViolation`, `StateContractReasons` and
every rejecting entry point are gone. What remains in `StateContract.cs` cannot
throw — the column-width constants (documentation of the schema, still pinned to
the DDL by test), free-text U+FFFD normalisation for the `NVARCHAR(MAX)`
columns, and `DateTimeKind` stamping.

### Known — issues this revert RE-OPENED at the time

Documented at length in `docs/SQL_CONTRACT.md`. **Status update: (a) and (b)
are closed again for operator-supplied subject ids by the entry-point
validation above; (c) remains open**, as do the length bounds for identifiers
other than the erase-subject `--id`. The text below records what the revert
re-opened when it shipped.

* **(a) An unpaired UTF-16 surrogate in a subject id is silently rewritten to
  U+FFFD by the FILE backend** (`System.Text.Json` on save), so a DSAR erasure
  is filed under a different id and **the subject stays ingestible**:
  `AddSuppressedSubject` then `IsSubjectSuppressed` with the SAME string returns
  **false** on the same store instance. SQL stores the code unit verbatim and
  answers **true**.
* **(b) `subject_id` and `item_id` are `NVARCHAR(256)` on SQL with no bound on
  file**, so an over-long id erases successfully on file and raises **SQL error
  8152** on SQL. 8152 is not in `TransientErrorNumbers`, so it is rethrown
  without retry and **the erasure FAILS**.
* **(c) Blank padding.** SQL's `=` folds trailing spaces; ordinal comparison
  does not. `ALT-1` / `ALT-1 ` and `''` / `' '` may be one key on SQL and are
  two on file.

Mitigation is operational: normalise subject ids upstream of the connector, and
verify an erasure with `list-suppressed-subjects` — under (a) the id listed is
not the id submitted.

Also known and open, predating the revert: the **connector-id identity
divergence** — file-mode state paths are built from `CONNECTOR_ID` with no
sanitisation, so on a case-insensitive filesystem two ids differing only by
case share one state set (and one's `WipeAll()` destroys the other's DSAR
suppression state) while SQL's BIN2 `connector_id` keeps them separate, and an
id containing a path separator escapes the data directory. See *Known
residuals* in `docs/SQL_CONTRACT.md`.

Covered by test: the regression itself, per rejected clause and per write path
(`LegacyStateReadModifyWriteTests`). (a) and (b) were pinned as
`SuppressionSurrogateTests.OPEN_DEFECT_A` / `OPEN_DEFECT_B`, asserting the
then-defective behaviour; with the entry-point fix above those tests are
rewritten as `FIXED_DEFECT_A` / `FIXED_DEFECT_B`, and the store-level
tolerance they documented is pinned separately
(`TheStoreItselfStillToleratesWhatTheCommandRefuses`).

### Fixed — kept from the previous round (NOT reverted)

**`DeadLetterRecord.CorrelationId` is no longer discarded by SQL Server.** It
was persisted by the file backend and dropped by SQL — no column, no binding,
no read. `CrawlEngine.StampCorrelation` stamps every dead letter with it and
`CommandRegistry.DeadLetterIdentityKey` feeds it into the identity key
`retry-failed` uses to finalise its snapshot, so switching backends lost the
trace link and collapsed that key component to the empty string. Added
`correlation_id NVARCHAR(128) NULL` with a guarded `ALTER` for deployed tables,
bound on insert, read on select. A test compares the table's column list
against the INSERT's and the SELECT's — all three read off the SQL AST — so a
member persisted by one backend and not the other fails rather than shipping.

**The fenced dead-letter paths no longer mask the fault they were fenced
against.** `AddDeadLetters` and `MutateDeadLetters` ran
`catch { txn.Rollback(); throw; }`, citing `MutateValue` as the model —
`MutateValue` has no such catch, and the catch made them **strictly less safe**:
`SqlTransaction.Rollback` is documented to throw `InvalidOperationException` on
a completed or broken transaction, so a fault raised *by* `Commit()` had its
`SqlException` replaced. `Execute`'s two handlers both filter on
`catch (SqlException)`, so the masked exception matched neither — `ShouldRetry`
was never consulted (a genuinely uncommitted batch was **not** retried, so the
dead letters were **lost**, not duplicated) and the "SQL state operation
FAILED" diagnostic never fired. Both now use `using var txn` with no catch:
`Dispose` rolls back an uncommitted transaction and is a no-op on a completed
one.

*Correction — the guard this entry previously claimed did not exist.* It said
"a test asserts **no** explicit `Rollback()` anywhere in the class, so a third
path added later cannot reintroduce it". That was **false**. The only guard was
a regex over the source text, `@"^\s*\w+\.Rollback\(\)"` with
`RegexOptions.Multiline`, which matches a `Rollback` call only where it *begins
a line*. Reinstating the exact defect with the catch collapsed onto one line —
`} catch { txn.Rollback(); throw; }` — left the full suite green (measured on
the pre-fix tree: Failed: 0, Passed: 724, Total: 724). The guard tested layout, not behaviour.

What is guaranteed now, stated precisely:

* `RollbackMaskingIlGuardTests` asserts on the **compiled IL** of
  `SqlStateStore`, which carries no whitespace or line breaks, so no formatting
  of the defect can evade it. Two detectors: an exhaustive scan of every IL byte
  offset of every method (including compiler-generated lambda bodies) for a
  `call`/`callvirt` to a method named `Rollback`; and, per write path, an
  assertion that the compiled transactional body carries zero catch clauses and
  zero exception filters. Verified by reinstating the defect in three
  formattings — the one-line catch in `MutateDeadLetters`, the one-line catch in
  `AddDeadLetters`, and a fully collapsed single-line `catch when (…)` filter —
  each of which turned the guard red.
* **Not** guaranteed: no test executes `SqlStateStore.AddDeadLetters` or
  `MutateDeadLetters`. Both go through `Execute`, which opens a real
  `Microsoft.Data.SqlClient` connection; there is no SQL Server on the build
  host and no container runtime to start one, and `SqlException` cannot be
  constructed by a test. The runtime *mechanism* — that `Rollback` after
  `Commit` throws and that the throw replaces the original exception — is
  demonstrated separately against a real ADO.NET provider
  (`Microsoft.Data.Sqlite`) in `TransactionRollbackMaskingTests`, not against
  `SqlClient`.
* The old regex test is kept, renamed to
  `TheSqlStoreSourceHasNoRollbackCallAtTheStartOfALine`, so its name no longer
  claims more than it checks.

**Binary collation swept across the class.** `docs/SQL_CONTRACT.md` recorded
`altrata_kv.[key]` and `altrata_deliveries.delivery_id` inheriting the
database default (case-insensitive on a stock install) as a known-but-unfixed
residual — the same divergence as the suppression list, one table over. Every
column compared by equality now declares `Latin1_General_100_BIN2`, including
`connector_id` (in the `WHERE` of nearly every statement, so two connector ids
differing only by case shared state on SQL and not on file). Guarded migrations
carry over the four tables whose primary keys are named;
`altrata_checkpoint.connector_id` and `altrata_leases.lease_name` have inline
UNNAMED primary keys and are **not** migrated on existing deployments — that
needs dynamic SQL to discover the auto-generated constraint name, which cannot
be tested here. Recorded in `docs/SQL_CONTRACT.md` residuals rather than left
silent.

**`DateTimeKind` normalised.** The file backend returned `Kind=Utc` and
`SqlDataReader.GetDateTime` on `DATETIME2` returns `Unspecified`; both now
stamp `Utc` on write and on read, including for checkpoint/dead-letter files
written by earlier builds whose timestamps have no `Z`. Latent — no
Kind-sensitive consumer was found — but the same class.

### Removed — a test that could not fail

`BothBackendsAgreeOnConfusableSubjectIds` was the suite's only claim of
cross-backend agreement and was not one: it compared the file backend against
`ComparerForCollation()`, a hand-written twenty-line **model** of SQL Server
collation semantics authored alongside the fix it asserted. A model can only
detect divergences it already encodes, and it stayed green through the entire
unpaired-surrogate defect — in which the two backends stored *different values*
and collation was not involved. Removed along with the model rather than
renamed.

It has not been replaced by an equivalent claim. The rejection tests that
briefly stood in its place went with the validation they asserted (see
*Reverted*, above), and the cross-backend agreement it purported to show is
**open**, not proven — the divergences are enumerated under *Known* and in
`docs/SQL_CONTRACT.md`. What executes today is the file backend on disk (the
read-modify-write regression per clause, and the two open defects pinned to
their current behaviour) plus the SQL half of every SCHEMA claim, read off the
`TSql150Parser` AST of the shipped DDL and the shipped statements.

**Stated limit:** there is no SQL Server on the build host and no container
runtime to start one, so nothing here executes a query against a live server.
That a live server behaves as its declared collation and declared widths say is
**not proven**, and neither is the SQL half of open issues (a), (b) and (c) —
verbatim surrogate storage, the 8152 rethrow and blank padding are stated from
SQL Server semantics and the declared DDL, not executed. Needs an integration
environment. `docs/SQL_CONTRACT.md`
says the same under *What is NOT verified here*.

### Fixed — SQL/file backend divergence on the DSAR suppression list

- **`dbo.altrata_suppressed.subject_id` now carries an explicit binary
  collation** (`Latin1_General_100_BIN2`). It inherited the database default,
  which on a stock SQL Server install (`SQL_Latin1_General_CP1_CI_AS`) is
  **case-insensitive**, while the file backend compares suppressed subject ids
  with `StringComparer.Ordinal`. The two backends therefore disagreed about
  whether a subject had been erased — on the erasure list, where disagreement
  means an erased subject is re-ingested on one backend and not the other.
  Filing `alt-9001` after `ALT-9001` hit the case-insensitive `IF NOT EXISTS`
  guard and was **dropped with no error**, leaving that subject ingestible.
- Binary collation rather than normalised key storage: the suppression list is
  DSAR evidence and `ListSuppressedSubjects` must return the id exactly as the
  erasure was filed. BIN2 is equality-identical to `StringComparer.Ordinal`
  while leaving the stored value byte-exact.
- **Every comparison names the collation at the comparison site**
  (`subject_id = @s COLLATE …`), pinned on the parameter so the primary key
  stays seekable, and correct even against a table not yet migrated.
- **A guarded `ALTER COLUMN` migrates existing deployments** — changing the
  `CREATE TABLE` alone would have left every deployed table silently on the
  insensitive collation, since the schema is only applied when the table is
  absent. Safe in this direction and a no-op on re-run. It does **not** recover
  erasures the insensitive key already swallowed; re-file those from the
  erasure ledger after upgrading (`docs/SQL_CONTRACT.md`).
- `ListSuppressedSubjects` re-sorts ordinally client-side: BIN2 orders by code
  point, the file backend by UTF-16 code unit.

### Fixed — residual retry-after-commit on the dead-letter write paths

- **`AddDeadLetter` is commit-fenced**, like `MutateValue` before it. The
  executor retries the WHOLE operation on a transient `SqlException`, so a
  fault raised *after* the row committed appended a **second copy** of the same
  failed item — inflating the queue an operator triages and letting one item
  consume two of the queue's bounded slots.
- **`ReplaceDeadLetters` now routes through the atomic, fenced
  `MutateDeadLetters`.** It was `ClearDeadLetters()` plus a loop of
  `AddDeadLetter()` — N+1 *independent* `Execute` calls, so not atomic at all: a
  transient fault partway through left the queue holding a **prefix** of the
  replacement, silently dropping every record after the failure point. That is
  how a drain/requeue writes surviving records back, so the tail was lost
  outright.
- **`AddDeadLetters` is overridden rather than inherited.** `IStateStore`'s
  default loops `AddDeadLetter`, which on SQL is N lock acquisitions and N
  transactions, breaking the contiguity the interface documents. Now one
  transaction, one commit, fenced once.
- **Audited every write path in `SqlStateStore`** and recorded the result in the
  `CommitGuard` doc comment: five paths are relative/caller-supplied and need
  the fence; the other eight (`SaveCheckpoint`, `ClearCheckpoint`, `SetValue`,
  `MarkDeliveryProcessed`, `ClearDeadLetters`, `AddSuppressedSubject`,
  `RemoveSuppressedSubject`, `WipeAll`) are absolute writes whose replay
  reproduces the same state and are safe unguarded.
- Residual, unchanged and now documented: if the commit call *itself* throws,
  the outcome is genuinely ambiguous and no client-side flag can resolve it.
  That needs a transactional idempotency key — a schema change, deferred.

### Added — usage metering with enforceable ceilings (WP-AL-4)

- **A ceiling that can REFUSE a billable lookup** (`ALTRATA_MAX_LOOKUPS_PER_DAY`,
  and `ALTRATA_MAX_LOOKUPS_PER_WINDOW` + `ALTRATA_USAGE_WINDOW_HOURS` for a
  rolling window; all default **unset = no ceiling = byte-identical behaviour**).
  The connector already counted billable lookups and already paced them
  (`ALTRATA_API_CALLS_PER_MINUTE`), but neither can decline — the counter is a
  post-hoc tally and the rate limiter only makes the caller *wait*, so a runaway
  or abusive workload was billable without bound. This is the part that says no.
- **Fail-closed refusal, modelled on the purpose veto**: PII-safe audit entry
  (`Decision="deny"`, `Billable=false`), the dedicated metric
  `altrata_usage_denied_total`, and a typed `UsageBudgetExceededException` that
  names the knob to change. Nothing billed, and **no HTTP request enqueued** —
  not even the OAuth token fetch.
- **Order is load-bearing**: the check sits AFTER the purpose veto and BEFORE the
  rate limiter, token fetch, HTTP call and billable counter. After the veto, so a
  disallowed purpose can never *consume* budget — otherwise refused requests
  alone could exhaust the day's ceiling and deny service to the legitimate
  workload. Pinned by `PurposeVetoPrecedesTheBudgetCheck`.
- **Durable, atomic counters** in the existing key/value state facility (state key
  `usage_budget`, keyed by TIME only — never by subject). New
  `IStateStore.MutateValue` does a read-modify-write inside ONE lock acquisition:
  `FileStateStore` under its process lock, `SqlStateStore` under
  `UPDLOCK, HOLDLOCK` in a transaction. Without it, N concurrent lookups would
  each read the same "used" figure and overshoot the limit by up to N−1.
- **Reserve/release**: the reservation is taken before the call and given back if
  the lookup never became billable (breaker open, 5xx, timeout, graceful stop),
  so a flapping upstream cannot burn the allowance without producing a result. A
  crash between the two leaves it consumed — conservative by design.
- **Scope is documented, including where it bites**: the ceiling is per state
  store. On the default file backend that means **per host** (M hosts sharing a
  `CONNECTOR_ID` enforce M × the number); `USE_SQL_SERVER=true` makes it genuinely
  fleet-wide. Connection sharding does NOT multiply it today because
  `ingest-item` is not shard-aware — flagged in-code for whoever adds a
  shard-aware caller. See **docs/USAGE_CONTROLS.md**.

### Added — entitlement-freshness cadence (WP-AL-4 part 2)

- **`--incremental-minutes <1–10080>`** on the continuous commands, making
  sub-hour re-ACL cadence expressible (`--incremental-hours` is an int with a
  floor of 1 and cannot go below 60 minutes). Wins over `--incremental-hours`
  when both are given; unused, nothing changes. The scheduler's 30 s wake cap
  never sleeps past a due crawl, so the interval is honoured to within one loop
  iteration; the cadence log line now formats minutes instead of printing "0h".
- **Documented honestly** (docs/ENTITLEMENT.md): a Graph connector cannot
  re-evaluate entitlement per grounding call — Graph trims against the ACL stored
  on the item, with no callback into Altrata — so the sweep cadence is the *only*
  connector-side lever, and a 5-minute cadence costs 12× the sweeps (source API
  calls + Graph ACL writes) of an hourly one. `seat-sync` under an external
  scheduler is documented as the right tool for minute-level freshness.
- **Deferred, deliberately not built**: agent-layer / retrieval-time entitlement
  checks (Copilot Studio / MCP middleware, not connector code) and a
  redistribution marker from the feed manifest (unconfirmed that the vendor's
  manifest carries one — reading it would invent provenance).

### Added — ContentGate (chassis component CS-1)

- **Prompt-injection screening of the indexed text** (`CONTENT_GATE`, default
  **off**). Ingested content becomes Copilot grounding context, so a poisoned
  record is an attack on every user whose query it grounds. `ContentGate` +
  `InjectionScanner` scan the FINAL indexed text — the assembled body AND every
  string property — for imperative overrides, role reassignment, exfiltration
  directives, hidden-character obfuscation (zero-width / bidi, with a normalized
  second pass) and long base64-dense blobs.
- **Quarantine, not drop.** A hit routes the item to the EXISTING dead-letter
  queue with reason `content-gate:<category>`, appends a **new `quarantine`
  decision-ledger kind** (alongside `exclude` / `acl-restrict` — not overloaded),
  stamps `contentScanStatus` on the item, increments
  `altrata_content_gate_blocked_total` and raises the existing alert path
  (`content_gate_blocked`). The record stays replayable; `retry-failed`
  re-drives it and **re-runs the gate**, so draining the queue cannot silently
  bypass a quarantine.
- **Config-driven patterns**, compiled once, with a per-pattern match timeout
  (`CONTENT_GATE_PATTERN_TIMEOUT_MS`, default 250 ms). A timeout **fails safe**
  as an INCOMPLETE scan, never as "no match". `CONTENT_GATE_PATTERNS_PATH`
  replaces the table; `config/content-gate-patterns.example.json` ships as a
  byte-for-byte copy of the built-ins (drift pinned by a test).
- **Deliberately asymmetric fail modes** (`CONTENT_GATE_FAIL_MODE`, or the
  per-kind `CONTENT_GATE_TEXT_FAIL_MODE` / `CONTENT_GATE_BINARY_FAIL_MODE`):
  text/injection fails **OPEN** (a heuristic outage must not halt a crawl — the
  item proceeds loudly with a warning, `altrata_content_gate_incomplete_total`
  and `contentScanStatus=incomplete`); binary/malware fails **CLOSED**.
- **No malware scanner in this connector, deliberately.** Altrata ingests no
  binary content: `FeedReader` accepts `.json`/`.jsonl`/`.csv` only, item content
  type is always `text`, and there is no attachment/blob path. File integrity is
  already the SHA-256 manifest gate (`FeedReader.ValidateChecksums`).
  `CONTENT_GATE_ICAP_URL` is parsed for fleet parity and logged as INERT; the
  binary fail mode still defaults to CLOSED so a future binary path starts safe.
- **PII contract extended to verdicts**: a verdict carries the item id and a
  fixed-vocabulary category ONLY — never matched text, a snippet or a field
  value. A test drives a quarantine on a record loaded with names/emails/
  net-worth figures and asserts the run log, the decision ledger, the
  dead-letter queue file and the alert payload are all clean. The dead-letter
  default stays `redacted`.
- `CONTENT_GATE_MAX_SCAN_MB` (default 4) bounds per-item scan cost; beyond it the
  scan is INCOMPLETE (never clean).
- New schema property `contentScanStatus` (`config/graph-schema.json`), stamped
  only when the gate is on. With `CONTENT_GATE` unset the wire output, item
  properties and per-item cost are byte-identical to before (test-enforced).
- 60 new tests (517 → 577).

## [1.0.0] - 2026-07-18

First GA release: the full connector chassis plus the enterprise hardening
package.

### Added — enterprise hardening package

- **Windows Event Log mirroring** (`EVENTLOG_ENABLED=true`): WARNING/ERROR
  lines and lifecycle markers mirrored to the Application log, source
  `AltrataConnector`, stable event ids 1000/2000/3000 (docs/SIEM.md). PII-safe
  by construction (same message text as the file sink; enforced by tests).
  Idempotent event-source creation in `scripts/install-windows-service.ps1`.
- **Proxy + custom CA** (`PROXY_URL`, `PROXY_BYPASS`, `CA_BUNDLE_PATH`): all
  connector HTTP (Graph, Entra token, Altrata API, alert webhook) honours an
  explicit forward proxy; a PEM bundle adds TLS-inspection/private-PKI roots
  ADDITIVELY (system trust keeps working; hostname mismatches never forgiven).
  Bad input fails fast naming the setting.
- **Certificate credential for Graph** (`GRAPH_CLIENT_CERT_PATH` +
  `GRAPH_CLIENT_CERT_PASSWORD`, or `GRAPH_CLIENT_CERT_THUMBPRINT`): RS256
  client-assertion auth (x5t#S256 / aud / jti / nbf / exp); certificate WINS
  over `SECRET_AAD_APP_CLIENT_SECRET`, which becomes optional; only the auth
  MODE is logged.
- **Dead-letter payload protection** (`DEADLETTER_PAYLOAD_MODE`, default
  `redacted` — decision record in SECURITY.md): the queue carries ids /
  subject-hashes / error / attempts only; `retry-failed` re-fetches redacted
  upserts from the checksum-verified feed delivery. `forget-subject` scrubs
  queued upserts/transforms for the erased subject; `retry-failed` refuses to
  replay upserts for suppressed subjects (erasure-completion DELETEs exempt);
  replays now restore the item↔subject reverse index.
- **New operational metrics**: `altrata_graph_throttle_429_total`,
  `altrata_entitlement_refusals_total`, `altrata_erasure_ledger_broken`,
  `altrata_match_review_depth`, `altrata_ha_leases_held`.
- **Ops pack**: `ops/grafana-dashboard.json`, `ops/prometheus-alerts.yml`,
  `ops/azure-monitor-alerts.kql` (ledger-tamper alerting classed as a
  SECURITY incident).
- **Enterprise docs**: `docs/THREAT_MODEL.md` (STRIDE + FIPS audit + DSAR
  posture), `docs/RUNBOOKS.md`, `docs/DR.md`, `docs/SIEM.md`,
  `docs/DEPLOYMENT_ENTERPRISE.md`, root `SECURITY.md`.
- **CI/CD**: CycloneDX SBOM on releases; Authenticode + cosign signing gated
  on repo secrets (graceful skip); coverage gate (measured 70.19% line at
  authoring, threshold 65.19%); perf-smoke job over the stress suites;
  experimental WiX v5 MSI job (`packaging/msi/`).

### Chassis (carried into 1.0.0)

- Seat-only entitlement (never-everyone, fail-closed), batched re-ACL on seat
  changes; licensed feed ingestion with per-file SHA-256 manifests, TOCTOU-safe
  reads, reconciliation reports; delta tombstones; per-subject DSAR erasure
  with durable suppression list and tamper-evident hash-chained ledger;
  entity resolution with review queue; relationship-path materialization;
  Graph $batch pipeline with adaptive concurrency and 429 ladder; circuit
  breakers + degraded-mode pause; SQL Server backend + HA leases
  (close-with-failed-claims); OpenTelemetry tracing with a PII-safe tag
  allowlist; correlation ids end-to-end; Windows service host with graceful
  chunk-boundary stop; Docker + compose topology.

[1.0.0]: https://example.com/releases/tag/v1.0.0
