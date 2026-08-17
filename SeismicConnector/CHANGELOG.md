# Changelog

All notable changes to the Seismic Copilot Connector. Versions follow
[SemVer](https://semver.org); the assembly version is pinned in
`src/SeismicConnector/SeismicConnector.csproj`.

## Unreleased — bank-grade hardening

### Changed — release automation now runs, and the tag format changed

The release pipeline is wired to the repository root and produces releases for
real. It had been authored under `SeismicConnector/.github/workflows/release.yml`
— one level below the only directory GitHub executes workflows from — so no tag
had ever triggered it and no bundle, GHCR image or SBOM was ever published by
it. The logic now lives once in `.github/workflows/release-connector.yml`,
called by `.github/workflows/release-seismic.yml`.

- **Release tags are now `seismic-v1.2.0`, not `v1.2.0`.** The five connectors
  share one repository and version independently; an unprefixed tag would start
  all five pipelines against a single tag, and only the first to finish could
  create the release.
- Signing secrets are the fleet-wide set, which is this connector's existing
  spelling: `AUTHENTICODE_PFX_BASE64` / `AUTHENTICODE_PFX_PASSWORD` for the
  win-x64 binary, `COSIGN_PRIVATE_KEY` / `COSIGN_PASSWORD` for the image. All
  optional; signing steps skip with a notice and the release ships unsigned.
- The SBOM is now built by a dedicated job and attached to the release as its
  own asset, rather than generated inside the package job and copied into each
  zip. One SBOM per release instead of one per bundle, and it is validated as
  parseable JSON before it can be attached. **Operators who scanned the SBOM
  from inside an extracted bundle now download it beside the zip**
  (`docs/DEPLOYMENT_ENTERPRISE.md`).
- The detached `cosign` bundle signature (`<bundle>.zip.sig`) is unchanged and
  still produced when `COSIGN_PRIVATE_KEY` is configured.
- `workflow_dispatch` runs the identical build → smoke-test → package path as a
  dry run: nothing is pushed and no release is created.

### Fixed — decision-ledger durability, round 10

* **BLOCKER — the backslash case: an altered byte disguised as a torn write.**
  A single `0x5c` landing on the last hex character of the final record's `Hash`
  turned the closing quote into an **escaped** quote, so the string ran on and
  the record parsed as an *incomplete* JSON value — which is exactly the
  discriminator the ledger used for "interrupted write, safe to truncate". On a
  real 3-record ledger the acknowledged record was destroyed, its `seq` reissued
  to a different record, and every signal reported clean:
  `ReadFile` returned 2 records with `damage.IsClean=True` and
  `Verify.Valid=True`; the resume reported `ResumedDamage.IsClean=True`.
  The fix addresses the **class**, not the byte. An interrupted write can only
  stop early — it can never add or change a byte — so whatever it leaves must be
  a byte-for-byte **prefix of what the writer could have emitted**. Trailing
  bytes are now discarded only when they are such a prefix
  (`IsPlausibleWritePrefix`), which an altered byte is not. Genuine torn writes
  are unaffected: all 265 truncations of a real final record are still healed.

  **Re-measured exhaustively** over a real 265-byte final record — all 256 byte
  values at all 265 offsets, plus delete, insert and truncate at every position:

  | Damage | Combinations | Recovered | Refused | Dropped quietly |
  | --- | --- | --- | --- | --- |
  | Replace | 67,840 (265 no-op) | 16,675 | 50,896 | **4** |
  | Delete | 265 | 179 | 85 | **1** |
  | Insert | 68,096 | 17,775 | 50,321 | **0** |
  | Truncate | 265 | 265 healed as torn writes | 0 | **0** |

  The residue is **one offset**: the closing brace (offset 264), overwritten by
  one of the four JSON whitespace bytes (`0x09`, `0x0a`, `0x0d`, `0x20`) or
  deleted. After the trailing-whitespace trimming the format performs anyway,
  those bytes are byte-for-byte what a write stopping one byte short leaves, so
  no reader can tell them apart. Pre-fix the figure was **3 offsets / 228 of
  67,840 combinations**, not the "2 of 265" three documents claimed.

* **The sweep's five-value replacement alphabet is gone.**
  `DamagingAnyByteOfTheFinalRecord_OnlyEverDropsItAtTheTerminator` swept `X`,
  `0xff`, space, comma and NUL — and read as exhaustive while it was not. That
  sample is what concealed the backslash case for a whole round. It now sweeps
  **all 256 byte values**, and delete/insert/truncate sweeps sit beside it.

* **MAJOR — `Seq` overflow at `long.MaxValue - 1`.** The range guard rejected
  `long.MaxValue` but not `long.MaxValue - 1`, which is equally overflow-inducing:
  `anchor=9223372036854775806` gave `baseSeq=9223372036854775807`, the first
  `Append` issued `9223372036854775807` and the second issued
  `-9223372036854775808` — a negative seq, the exact state the guard exists to
  prevent — with `Verify()` still returning `True`. No anchor value alone can fix
  this, because an accepted anchor has an unbounded number of appends ahead of
  it, so the bound is now enforced at the point of **issue** as well: `MaxSeq`
  (`long.MaxValue - 1`) is the largest seq `Append` will ever return, and it
  throws rather than wrap or reuse. `long.MaxValue` is reserved so that
  `last.Seq + 1` in `ResumeTail` cannot overflow either.

* **Three documents carried the false measured number** (`docs/THREAT_MODEL.md`,
  `docs/RUNBOOKS.md`, `docs/EXCLUSIONS.md`), as did the `ResumeTail` comment, this
  changelog and a test's own doc comment — six places, one of them copied from
  another. All now cite the single re-measurement above.

### Changed (safe-default flips — operators please note)

* **The decision ledger moved to a stable, continuous path.**
  `DECISION_LEDGER=true` now writes **one** append-only file at the logs root,
  `logs/decision_ledger_{CONNECTOR_ID}.jsonl`, instead of a new
  `logs/{run}/decision_ledger_{CONNECTOR_ID}_{timestamp}.jsonl` per run. Every
  run **resumes** that file: `seq` keeps climbing and each run's first entry
  links to the previous run's last, so the hash chain now proves continuity
  *between* runs, not just within one. This closes three problems at once — the
  ledger is no longer inside the directories `LOG_RETENTION_DAYS` deletes; a
  deleted run's decisions are now detectable (previously every run restarted the
  chain from genesis, so removing an entire run's ledger left no trace); and the
  layout matches the rest of the connector fleet. **Existing per-run ledgers are
  left untouched and are not continued** — each is its own chain; the connector
  logs their paths at startup so they can be archived alongside the new file.
  Assumes a single active connector process per `CONNECTOR_ID`, as the sync
  cursor, checkpoints and dead-letter queue in the same directory already do.

* **No-MNE exclusions now fail closed.** A missing / empty / empty-object /
  `null` / malformed / rule-less `config/exclusions.json` is a hard startup
  error naming the file (was: silently "no rules"). To run rule-less on purpose
  set `{ "acknowledgeNoExclusions": true }`; `validate-config --strict` still
  FAILs a rule-less posture (docs/EXCLUSIONS.md).
* **Dead-letter payloads default to `redacted`.** `DEADLETTER_PAYLOAD_MODE`
  now defaults to `redacted` (was `full`); set `full` explicitly to keep
  verbatim payloads. An unrecognized value now fails fast at startup.
* **Incremental identity sync on by default.** `IDENTITY_SYNC_ON_INCREMENTAL`
  now defaults to `true`, so entitlements re-sync every incremental crawl
  (shrinks ACL-staleness lag to the incremental cadence). Residual lag is still
  non-real-time; pair with `PERMISSION_REACL` / scheduled `reacl` for unchanged
  content. Set the var to `false` to restore full-crawl-only sync.

### Added

* **ContentGate (CS-1) — malware + prompt-injection scanning of grounding
  content** (`CONTENT_GATE`, **default off**). Ingested content becomes Copilot
  grounding context, so a malicious document is an attack on every user whose
  query it grounds. Two independent scanners behind one stage: an
  `IMalwareScanner` (ICAP/HTTP gateway + test fake; a live scanner is never
  required to build or test) over the downloaded binary payload, and a
  config-driven, compiled, timeout-guarded prompt-injection heuristic over the
  FINAL indexed text. Posture is **quarantine, not drop**: a blocked item still
  indexes its metadata, goes to the existing dead-letter queue with reason
  `content-gate:<category>`, gets a new `quarantine` decision-ledger kind, bumps
  `content_gate_blocked_total`, raises the existing alert and is stamped with a
  `contentGateStatus` property — and `retry-failed` re-drives it unchanged.
  Fail modes are **deliberately asymmetric**: binary fails CLOSED (never index
  unscanned bytes), text fails OPEN with a loud warning + metric (it is a
  heuristic, not a security boundary, and blocking a whole crawl on a heuristic
  outage is worse than the risk). A regex match timeout is a pathological
  *document*, not an outage, so it always fails safe. Both modes configurable
  (`CONTENT_GATE_FAIL_MODE`, `_BINARY`, `_TEXT`); see `docs/CONTENT_GATE.md`.
  With `CONTENT_GATE` unset, behaviour is byte-identical: nothing is
  constructed, no rules file is read, no property is stamped.
* **Restrictive state-directory permissions** — `logs/` and `data/` are created
  owner-only at startup (POSIX `0700`; best-effort owner+admins NTFS ACL on
  Windows). Never fatal if it cannot be set (logs a warning).
* **Webhook anti-replay** — a signed timestamp bound into the HMAC
  (`timestamp + "." + body`) with a freshness window
  (`SEISMIC_WEBHOOK_REPLAY_WINDOW_SECONDS`, default 300s) plus duplicate-signature
  rejection within the window. Required by default
  (`SEISMIC_WEBHOOK_REQUIRE_TIMESTAMP=true`); set false to migrate legacy
  senders (body-only HMAC). Validate-before-parse preserved.
* **Immutable decision ledger** — `DECISION_LEDGER=true` writes an append-only,
  SHA-256 hash-chained audit of exclusion, ACL-restriction and quarantine
  decisions with an offline-verifiable chain (tamper-evident), as one continuous
  chain per connector at `logs/decision_ledger_{CONNECTOR_ID}.jsonl` (see
  *Changed* for the path/continuity details).
* **Stale-index TTL** — `GRAPH_ITEM_TTL_DAYS>0` stamps each item with a rolling
  `expirationDateTime = now + TTL` so the index self-expires if crawling stops.
* **Optional classification-enforced ACL** — `CLASSIFICATION_ENFORCE_ACL=true`
  (+ `CLASSIFICATION_ENFORCE_GROUP`) locks top-tier (Restricted) items' Graph
  ACL to an Entra group. Default off.

### Fixed / clarified

* **A mangled FINAL decision record was destroyed silently.** One overwritten
  byte inside the last record's key names — `"Seq"` → `"Xeq"` — leaves a
  **complete, valid** JSON object that simply is not a decision record. The
  residue classifier called that shape "junk, safely discardable", so the
  auditor dropped the record while reporting `Verify().Valid=True` and
  `LedgerFileDamage.IsClean=True`, the next resume **truncated** the damaged
  bytes off disk, and the freed seq was re-issued to a different item. No trace
  survived. The previous round's reasoning — "damage inside a record is surfaced
  as a seq gap from `Verify()`" — holds for every record except the last, where
  there is nothing behind it for a gap to appear in. Fixed by narrowing what
  counts as a discardable crash-tail to what a partial flush can actually
  produce: an **incomplete** JSON value. Bytes that form a complete JSON value
  the record contract rejects are damage — kept on disk, `ResumedDamage`
  non-clean, `ReadFile` refuses the file. A sweep damaging every byte of a real
  final record with five replacement values is part of the suite.
  **That limit was restated in the next round — see "the backslash case" below;
  the "2 of 265 offsets" figure this release shipped was measured with a
  five-value alphabet and was wrong.**

* **`IsCompleteRecord` did not validate `Seq` at all**, contrary to what the
  previous round claimed. A line carrying `Seq=-4` was accepted as a record and
  became the resume anchor, so the chain went on to issue **negative** seqs; a
  line carrying `long.MaxValue` anchored a chain whose next seq **overflowed**
  to `long.MinValue` (`last.Seq + 1`). Both now fail the record contract, per
  clause, with the accepting boundary (`0` and `long.MaxValue - 1`) pinned too.
  `[JsonRequired]` already covered omitted and explicitly-null `Seq` correctly —
  that part of the contract was accurate.

* **The two ledger readers disagreed about damage on a lone-CR separator.**
  `ReadFile` went through `File.ReadLines`, which breaks a line on a lone `\r`,
  so a file whose records were separated by `\r` instead of the `\n` the writer
  emits read as `IsClean=True` while the resume scan reported `GluedLines=1`.
  The writer only ever writes `\n`, so a lone CR there is an **overwritten**
  separator. `ReadFile` now splits on the newline byte alone (stripping a BOM as
  before), and the two readers agree. CRLF files are unaffected — the CR is
  still trimmed as trailing whitespace.

* **`DECISION_LEDGER=true` was silently a no-op on every command except
  `full-deployment`.** Only `full-deployment` (and `--continuous`) opened the
  file-backed ledger; `ingest`, `ingest-object`, `ingest-item`, `retry-failed`,
  `reconcile` and `reacl` opened the reconciliation report and the classification
  manifest but never the ledger, so they fell back to the in-memory no-op ledger
  and wrote **no audit file at all** — with no warning. Every exclusion,
  ACL-restriction and quarantine decision made by those runs was lost. All
  pipeline-running commands now open it, so the flag means the same thing
  everywhere.
* **Log retention deleted the decision ledgers.** `LOG_RETENTION_DAYS` deletes
  whole `logs/{run}/` directories, and the ledger was written inside them — so
  the retention setting destroyed the tamper-evident audit records it was never
  meant to touch. The ledger now lives at the logs root (see *Changed*), and
  `LogPruner` additionally **refuses** to delete any run directory that still
  contains a `decision_ledger_*.jsonl`, naming it in a warning so a legacy ledger
  can be archived rather than silently lost.
* **A crash mid-append no longer poisons the ledger.** Re-opening a ledger
  discards a torn, uncommitted trailing fragment before appending (never a
  complete record), so the continued chain stays readable by `ReadFile`;
  interior corruption is still preserved and reported rather than papered over.
* **Two further ledger tear shapes.** The crash-tail handling covered a partial
  final *fragment* but mishandled two others. (1) A **newline-boundary tear** —
  the final record is complete on disk but lost its `\n` — was treated as an
  uncommitted fragment and **truncated away**, destroying an already-written,
  already-acknowledged audit record; because the surviving prefix and the next
  run's entries still chain correctly, `Verify()` then reported the ledger
  **CLEAN**, i.e. silent evidence loss with a passing verification. Such a
  record is now kept and its terminator restored. (2) A **blank-line tear** — a
  torn line whose newline landed, followed by a blank line — was marked
  *committed* by the blank line, so it survived the resume, the next append put
  a real record after it, and `ReadFile` then refused the file as interior
  corruption **for the life of the file**. A blank tail no longer commits
  unresolved bytes, so the torn line is discarded like any other crash-tail.
  Genuine interior corruption (a malformed line with real records after it) is
  still preserved and still surfaced.
* **A third tear shape, with a bigger blast radius: the interior-newline tear.**
  The rescue above only applied when the complete record was the *exact last
  bytes* of the file (`needsTerminator && committedBytes == totalBytes`). Lose
  or mangle ONE interior `\n` and two flushed records glue into a single line
  that no longer parses; that line was written off as junk, the committed mark
  stayed *behind* it, and resume **truncated away every complete record from the
  damaged boundary to EOF** — not just the last one. As with the newline-boundary
  tear, the surviving prefix plus the next run's entries still chained, so
  `Verify()` reported **CLEAN** over a ledger that had silently lost records
  (proven with three records where one interior terminator was dropped: two
  acknowledged records were destroyed and the chain re-used their seqs). A line
  is now split back into the records it holds, so the committed mark advances
  *through* glued records; a line that is record(s) followed by a torn fragment
  is cut at the fragment's exact byte offset and the terminator restored.
  `ReadFile` splits the same way, so the recovered records are reachable by an
  auditor instead of the file reading as corrupt forever. Both paths warn: the
  evidence survives, but the file is damaged and says so.
* **Unhandled `NullReferenceException` on the quarantine path.** Resume treated
  any line that deserialized to a non-null `DecisionLedgerEntry` as a complete
  record. `{}`, or an object missing fields, deserializes fine and leaves the
  non-nullable string members **null** at runtime with no compiler warning; that
  null `Hash` became the resume anchor and the next `Append` dereferenced it in
  `ComputeHash` → `AppendField` (`value.Length`). Since ContentGate records a
  quarantine via `Append`, one malformed byte in an audit file could take down
  the malware / prompt-injection quarantine of an entire crawl. A line is now a
  record only if every hashed member is present — on the resume path and in
  `ReadFile`, which could otherwise hand `Verify()` an entry that detonated the
  same way.
* **A fourth tear shape, and the structural change that ends the series: the
  MANGLED separator.** The fix above handles a separator that was **lost**
  (records butted together). It did nothing for one that was **overwritten** —
  replaced by any byte that is not JSON whitespace — because the scan stopped at
  the first byte that was not the start of a record. Everything from that offset
  to EOF was then read as an uncommitted crash-tail and **truncated**, and once
  again the surviving prefix plus the next run's entries chained correctly, so
  `Verify()` reported **CLEAN** over a ledger that had lost audit evidence.
  Proven on a 10-record ledger with a single NUL byte injected: 2734 bytes →
  1098, 10 records → 4, seven acknowledged records destroyed, the next run
  re-using their seqs, and `ReadFile` + `Verify` returning `Valid=True`.
  Reproduced for NUL, `X`, `0xFF`, half a UTF-8 sequence, `,`, VT, FF, U+2028
  and NBSP, terminated and unterminated — 18/18 — while the lost-separator
  control lost nothing. NUL is the ordinary shape of this: a crash leaves an
  allocated-but-unwritten block, and APFS/ext4 hand it back zero-filled.

  Two structural rules replace the shape-by-shape rescues, and between them they
  are meant to close the family rather than its fourth member:

  1. **The scan resynchronises.** A parse failure inside a line no longer ends
     the scan — it steps forward to the next plausible record start and keeps
     committing. No quantity of destroyed separator can put a later record
     behind the truncation cliff. A record the damage landed *inside* is
     genuinely unrecoverable, and skipping it leaves a **seq gap that `Verify()`
     reports** rather than a silent deletion.
  2. **Only interrupted writes are ever truncated.** Trailing bytes are
     discarded only when they are an *incomplete* JSON value (the signature of a
     partial flush) or a complete JSON value that was never a record. Bytes that
     are **invalid where they sit** cannot be produced by an interrupted write —
     *(superseded below: the "complete JSON value that was never a record" half
     of that rule was wrong and destroyed mangled final records; only an
     incomplete JSON value is discarded now.)*
     something overwrote already-flushed data — so they are kept as evidence,
     and `ReadFile` now **refuses** a file whose final line ends in them. That
     refusal is deliberate and narrow: it fires exactly when the damage would
     otherwise be invisible to `Verify()`. Damage the scan can resynchronise
     over is *not* refused, so a damaged ledger stays readable and never bricks.
* **A ledger could omit `Seq` and re-issue live seqs.** The completeness guard
  checked every string member for null but could not see a missing `Seq` at all:
  it is a non-nullable `long` that default-fills to `0`. A final line carrying no
  `Seq` became the resume anchor with seq 0 and reset the chain's next seq to 1,
  **re-issuing seqs that live records already held** (`Verify: Valid=False, seq
  out of order at index 3 (got 0, expected 3)`). Presence is now enforced at the
  contract — every member of `DecisionLedgerEntry` is `[JsonRequired]` — so it
  covers `Seq`, covers any member added later, and cannot be forgotten in a
  hand-maintained list. The null checks remain for the case the attribute cannot
  catch: a member that is present and explicitly `null`.
* **Physical ledger damage is now reported, not just logged.**
  `DecisionLedger.ReadFile(path, out LedgerFileDamage damage)` and the new
  `ResumedDamage` / `ResumedRecordCount` properties surface glued lines,
  resynchronised (overwritten) regions and a damaged tail. A ledger can be
  physically mangled and still verify perfectly — every record intact and
  correctly linked — which is the right outcome but not one an integrity monitor
  should have to infer from log text. This also closes an **append-smuggling**
  blind spot: a forged but correctly chained record glued onto the *end* of an
  existing line adds no new physical line and so hid from a line-count or
  tail-based WORM monitor. It is still accepted — rejecting glued records is
  exactly what destroyed acknowledged evidence in the two rounds above, and the
  chain never defended against append access in the first place (see
  docs/THREAT_MODEL.md) — but it is no longer invisible: any line holding more
  than one record is reported as damage.
* **A UTF-8 BOM made the ledger's two readers disagree.** `ReadFile` goes
  through `File.ReadLines`, which strips a BOM; the resume scan reads raw bytes
  and did not, so the two disagreed about record 1 — the resume reported one
  record fewer than the file held, and once the scan started resynchronising it
  would have flagged the BOM itself as destroyed bytes. The resume scan now
  skips a BOM at offset 0 and leaves it in the file.
* **The ContentGate text channel could be silently inert.** Only categories
  prefixed `Injection.` can produce a signal (that prefix is what routes a hit
  to quarantine), but the scanner reported itself healthy whenever *any* pattern
  compiled. An operator `config/content-gate.json` whose categories lacked the
  prefix therefore loaded cleanly, matched text, signalled nothing, and the gate
  stamped every document — live injections included — `clean`. A ruleset with
  zero usable categories now reads as an **unavailable** text scanner, so it
  takes the documented fail mode instead, and the condition is reported at
  config load naming the offending categories.
* **`LOG_RETENTION_DAYS` with an enormous value crashed every command.** The
  retention cutoff was computed outside `Prune`'s guard, so `LOG_RETENTION_DAYS=1000000`
  (or `int.MaxValue`) threw `ArgumentOutOfRangeException` out of a method
  documented never to throw — at the start of every command and every
  `--continuous` cycle. A window longer than any log can be old is now clamped
  to "keep everything" and warned about.

* **Classification honesty** — the `sensitivityLabel` property is documented
  everywhere as an ADVISORY, connector-applied classification tag (Purview-*aligned*
  in naming only), NOT a Purview-enforced sensitivity label. The shipped Graph
  schema property name is unchanged (wire back-compat).

### Build

* **The connector consumes the shared `Connector.Chassis` project (1.13.1).**
  The reusable infrastructure — logging, secrets, `SqlExecutor`/`SqlStateStore`,
  alerting, `HaCoordinator`, `LogPruner`, the decision ledger, the circuit
  breaker and `ServiceHost`/`ServiceStop` — now lives in one project at the
  repository root instead of a per-connector copy. It is referenced by
  `<ProjectReference>`, **not** as a NuGet package: there is no feed, no pack
  step and no version to keep in sync, so a clean clone builds as-is. Two
  consequences for anyone building or shipping this connector:
  * **Container builds need the repository root as their context** — a Docker
    build cannot reach outside its context, and the project reference points
    above the connector directory. Use
    `docker build -f SeismicConnector/Dockerfile .` from the repository root;
    `docker-compose.yml` already sets `context: ..` so `docker compose up
    --build` is unchanged.
  * **CI runs from the repository root.** `.github/workflows/seismic.yml` at
    the root builds and tests on ubuntu-latest and windows-latest (1017 tests
    green on both) and builds the container image. Releases are
    `release-seismic.yml` there too. The connector's own `.github/workflows/`
    files, inert since the consolidation, have been deleted.

## 1.0.0 — 2026-07-18

First versioned release: the connector chassis plus the enterprise-grade
hardening package.

### Added

* **Windows Event Log sink** — `EVENTLOG_ENABLED=true` mirrors WARNING+
  (INFO with `EVENTLOG_LEVEL=info`) and lifecycle start/stop marks to the
  Application log, source `SeismicConnector`, stable event ids 1000/1100/2000/3000
  (docs/SIEM.md). Source created idempotently by
  `scripts/install-windows-service.ps1`. Non-Windows: no-op. Never throws.
* **Proxy + custom CA trust** — `PROXY_URL` / `PROXY_BYPASS` route every
  outbound client (Seismic OAuth2+API, Graph, alert webhooks) through a
  forward proxy; `CA_BUNDLE_PATH` adds PEM roots (TLS inspection / private
  PKI) via additive `X509Chain` CustomRootTrust. Hostname mismatches are never
  excused; misconfiguration fails fast naming the setting.
* **Graph certificate credential** — `GRAPH_CLIENT_CERT_PATH` (+`_PASSWORD`)
  or `GRAPH_CLIENT_CERT_THUMBPRINT` switch Graph auth from client secret to an
  RS256 `client_assertion` JWT (x5t#S256 binding, fresh jti, 10-minute
  lifetime). Certificate wins over secret; only the auth MODE is logged.
* **Dead-letter payload protection** — `DEADLETTER_PAYLOAD_MODE=redacted`
  strips indexed content and property values from dead-letter records (file
  and SQL backends), keeping ids, teamsite/version, error/attempt metadata,
  ACL entries and SHA-256 stubs. `retry-failed` is unaffected — it re-fetches
  from Seismic.
* **Webhook + HA observability** — new metrics
  `webhook_accepted_total`, `webhook_rejected_total`, `webhook_dropped_total`,
  `webhook_queue_depth`, `ha_claims_acquired_total`, `ha_claims_held`.
* **Ops pack** — `ops/grafana-dashboard.json`,
  `ops/prometheus-alerts.yml`, `ops/azure-monitor-alerts.kql` wired to the
  runbook anchors.
* **Enterprise docs** — docs/THREAT_MODEL.md, docs/RUNBOOKS.md, docs/DR.md,
  docs/SIEM.md, docs/DEPLOYMENT_ENTERPRISE.md, SECURITY.md.
* **Release engineering** — CycloneDX SBOM per release; Authenticode and
  cosign signing (gracefully skipped until signing secrets are configured);
  experimental WiX v5 MSI (`packaging/msi/`, artifact-only); CI coverage gate
  (line coverage ≥ 57%; measured 62.0% at introduction) and perf-smoke job
  over the stress classes.

### Security

* FIPS audit: the codebase uses only SHA-256-family primitives
  (HMAC-SHA256 webhook authentication, SHA-256 ACL fingerprints/redaction
  stubs). No MD5/SHA-1/DES/RC4/3DES anywhere — no migration required
  (docs/THREAT_MODEL.md).
