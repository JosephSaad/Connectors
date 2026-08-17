# Disaster recovery — Altrata Copilot Connector

The index in Microsoft Graph is a REBUILDABLE cache of the licensed feed; the
process is stateless between commands. DR therefore reduces to: protect the
few files that are NOT recoverable by re-crawling, restore them first, and let
idempotent crawls rebuild everything else.

## What is, and is not, recoverable by re-crawl

| State | Where (file mode / SQL mode) | Lost ⇒ | Re-crawl recovers? |
|---|---|---|---|
| Crawl checkpoint | `logs/checkpoint_{ID}.json` / `dbo.altrata_checkpoint` | interrupted delivery restarts at record 0 | **YES** (PUTs idempotent) |
| Sync timestamps, processed-delivery ledger | `data/{ID}_state.json` / `dbo.altrata_kv` + `dbo.altrata_deliveries` | full crawl re-processes everything once | **YES** |
| Ingested-item inventory, crosswalk, path index, seats | `data/{ID}_identity.db` / `dbo.altrata_id_*` | purge/erasure completeness unknown until rebuilt | **YES** (full crawl + `seat-sync`), with a verify pass |
| Dead-letter queue | `logs/failed_records_{ID}.jsonl` / `dbo.altrata_deadletter` | pending replays lost; queued ERASURE DELETEs lost | mostly (next full crawl re-PUTs failures; erasure DELETEs must be re-verified — see below) |
| Billable-lookup counter | `data/{ID}_state.json` / `dbo.altrata_kv` | cost accounting resets | NO (restore from backup; vendor invoice is the fallback source) |
| **Erasure suppression list** | inside `data/{ID}_state.json` / `dbo.altrata_suppressed` | **erased subjects RE-INGEST on the next delivery** | **NO** |
| **Erasure ledger** | `logs/erasure_ledger_{ID}.jsonl` (a FILE in BOTH modes) | DSAR compliance record gone | **NO** |
| Purpose-of-use audit log | `logs/audit_{ID}.jsonl` | lawful-use record gone | NO |
| Reconciliation reports, run logs | `logs/…` | evidence trail thins | N/A (historical) |

**CRITICAL — the two erasure files.** A lost suppression list is not an ops
inconvenience; it is a COMPLIANCE FAILURE IN WAITING: the vendor keeps
delivering erased subjects, and without the list the next crawl re-ingests
them. A lost ledger destroys the proof that erasures happened. These two are
therefore backed up on a STRICTER tier than everything else:

### Tier 1 (strict): suppression list + erasure ledger (+ audit log)

- Back up **after every erasure command**, not on a nightly cycle: wrap
  `forget-subject`/`unsuppress-subject` in your job runner so a copy of
  `data/{ID}_state.json` (file mode) or a `dbo.altrata_suppressed` export
  (SQL mode) AND `logs/erasure_ledger_{ID}.jsonl` is taken immediately after
  a nonzero-change run. Both are small (KBs); copy cost is nil.
- Ship every ledger append to the SIEM as well (docs/SIEM.md) — that
  append-only remote copy is both the tamper cross-check and a last-resort
  restore source.
- Retain per your DSAR evidence policy (years), immutable/WORM storage
  preferred.
- RPO for tier 1: **0 erasures** — no completed erasure may be unprotected.
  The per-command copy gives exactly that.

### Tier 2 (standard): identity DB, state doc, dead-letter queue

- Nightly backup is sufficient (file mode: copy `data/` + the two `logs/`
  queue/checkpoint files while the service is stopped or via filesystem
  snapshot; SQL mode: the normal SQL Server backup chain covers everything).
- RPO: 24 h. Anything lost inside the window is reconstructed by one full
  crawl; the verify pass below closes the erasure gap.

### Not backed up

- `FEED_PATH` deliveries — the vendor is the source of truth; `archive/`
  retention already keeps processed drops (needed for redacted dead-letter
  replay: keep archives until `altrata_deadletter_depth == 0`).
- The Graph index itself — rebuilt by `full-deployment`/`ingest`.

## Restore procedure (file mode; SQL mode = restore DB then step 4 on)

1. Stop the service (SCM stop is graceful).
2. Restore tier 1 FIRST: `data/{ID}_state.json` (or at minimum merge the
   `SuppressedSubjects` array from backup into the current file) and
   `logs/erasure_ledger_{ID}.jsonl`. Run the ledger check: any erasure
   command performs `Verify()` — or check `altrata_erasure_ledger_broken`
   after the first command.
3. Restore tier 2: `data/{ID}_identity.db`, `logs/failed_records_{ID}.jsonl`,
   `logs/checkpoint_{ID}.json` (checkpoint optional — omitting it just
   re-ingests the interrupted delivery).
4. **Erasure reconciliation (mandatory whenever state older than the last
   erasure was restored):** for every ledger `erase` entry without a later
   `unsuppress`, dry-run `forget-subject --id <subject>`:
   - `Items to withdraw: 0` + `Already suppressed: 1/1` → consistent;
   - anything else → re-run with `--confirm` (idempotent: it re-suppresses,
     withdraws stragglers, appends a fresh ledger entry).
   Do this BEFORE re-enabling scheduled crawls if the suppression list could
   be stale; a crawl with a stale list re-ingests erased subjects (the later
   reconciliation still erases them, but they were searchable in between —
   log the exposure window for the DPO).
5. `validate-config --strict`, then one supervised `ingest --incremental`,
   then re-enable continuous mode.

## RPO / RTO summary

| Scenario | RPO | RTO |
|---|---|---|
| Host loss, backups intact (file mode) | tier 1: 0 erasures; tier 2: ≤24 h of counters/queue | ~30 min restore + one incremental crawl (index itself was never lost) |
| SQL backend loss (SQL/HA mode) | SQL backup chain (typ. ≤15 min log backups); tier 1 additionally per-erasure exports | SQL restore + step 4 verify; surviving nodes resume on lease expiry (docs/RUNBOOKS.md "HA failover") |
| Graph connection/index deleted | 0 (nothing authoritative lost) | one `full-deployment` (hours, volume-dependent, schema registration ~minutes) |
| Total loss of tier-1 files AND their backups | unacceptable — this configuration must not exist | rebuild suppression from SIEM ledger copies; disclose to DPO |

## Upgrade / rollback

- Versioning: SemVer `1.0.0` pinned in `AltrataConnector.csproj` `<Version>`,
  `packaging/msi/Package.wxs` (test-enforced match) and `CHANGELOG.md`.
- **Upgrade** (zip bundle): stop service (graceful) → replace binaries →
  keep `config/`, `env/`, `data/`, `logs/` in place → `validate-config
  --strict` → start. MSI upgrades (experimental) do the same via
  MajorUpgrade. In HA, upgrade node-by-node; mixed versions tolerate each
  other for the duration (state contracts are additive — see below).
- **Schema versioning** (SQL): DDL is idempotent, guarded, and ADDITIVE-ONLY
  (`IF OBJECT_ID/COL_LENGTH` guards; e.g. 1.0.0 adds
  `dbo.altrata_deadletter.redacted/subject_ids/subject_hashes` with
  defaults). The connector auto-provisions on first touch; running
  `scripts/sql/create-database.sql` by hand is equivalent (the test suite pins
  double-run idempotency offline; the live double-provision job was lost with
  this connector's own `ci.yml` in the move into the connector monorepo, and
  has not been reinstated at the root). Columns are never dropped or retyped in a minor/patch
  release — that is what makes rollback safe.
- **File-state versioning**: JSON/JSONL records tolerate unknown fields on
  read (older binaries ignore new fields; newer binaries default missing
  ones) — e.g. pre-1.0 dead-letter lines remain readable (`op` defaults to
  `upsert`, unstamped records are simply exempt from the new DSAR replay
  guard until drained; RUNBOOKS "Dead-letter growth").
- **Rollback**: reinstall the previous bundle over the same directories.
  Additive SQL columns/file fields from the newer version sit inert.
  Ledger/suppression semantics are unversioned constants (hash chain format
  is frozen). One caveat: dead-letter records written in REDACTED mode
  (empty payload) replay only on ≥1.0.0 — drain the queue BEFORE rolling
  back below 1.0.0, or re-ingest the affected deliveries after rollback.
- Rollback of an MSI install: uninstall (service removed; `config/`, `data/`,
  `logs/` under Program Files are left behind by design) then install the
  older MSI/zip and repoint at the same data.
