# DR — backup, restore, upgrade, rollback

The design premise that makes DR cheap: **Seismic is the source of truth and
every ingest is idempotent** (externalItem id = content id; PUTs are in-place
upserts; withdrawals tolerate 404). Losing connector state never loses data —
it only costs re-crawl time.

## Objectives

| Objective | Value | Why |
| --- | --- | --- |
| RPO (connector state) | 0 for correctness — all state is rebuildable from Seismic + Graph; ≤ 24 h for *convenience* state with daily backups | State loss ⇒ re-crawl, not data loss |
| RPO (index freshness) | ≤ incremental interval (`--incremental-hours`, default 4 h) during normal ops | Freshness window = crawl cadence + webhook near-real-time |
| RTO (single node) | ≤ 30 min: redeploy bundle/MSI + restore env + start | Stateless binary, one env dir |
| RTO (with full state loss) | 30 min + one full-crawl duration | Full crawl rebuilds identity + tracked items + index convergence |
| RTO (HA set) | ~0 — surviving nodes steal stale claims automatically (docs/RUNBOOKS.md#ha-failover) | Active-active |

## What state exists, and what each artifact costs to lose

| Artifact | Location (file backend) | SQL backend | Lose it ⇒ |
| --- | --- | --- | --- |
| Sync cursor | `logs/sync_state.json` | `dbo.SyncState` | Next crawl is FULL instead of incremental (slower, correct) |
| Checkpoint | `logs/checkpoint_{id}.json` | `dbo.Checkpoints` | In-progress run restarts its chunks (idempotent) |
| Dead-letter | `logs/failed_records_{id}.jsonl` | `dbo.DeadLetter` | Pending retries forgotten — `reconcile --repair` re-converges |
| Identity map + tracked items (ACL fingerprints, versions, expiry) | `data/{id}_identity.db` (SQLite) | `dbo.*` identity/tracked tables | Full crawl + identity crawl rebuilds; until then re-ACL baselines reset |
| Secrets/config | `env/.env.local(.user)`, `config/*.json` | same (files) | Connector cannot start — THE artifact to back up properly |
| Run logs / reconciliation reports | `logs/*/` | files | Audit evidence gone — retention policy decision |

## Backup

* **Config + secrets** (the only irreplaceable set):
  `env/.env.local`, `env/.env.local.user` (or nothing, if Key Vault),
  `config/*.json`. Back up on every change, store like credentials.
* **File backend**: snapshot `logs/sync_state.json`, `logs/checkpoint_*.json`,
  `logs/failed_records_*.jsonl`, `data/*.db` daily. SQLite: copy while the
  service is stopped, or use `sqlite3 .backup` online.
* **SQL backend**: the connector database rides the org's standard SQL
  backup (FULL daily + log backups per org RPO). All state incl. HA
  sessions lives there; nothing node-local matters except env/config.
* **Reconciliation reports** (`logs/*/reconciliation_*.jsonl`): retain per
  compliance policy — they are the No-MNE audit trail.

## Restore

1. Deploy the matching bundle version (zip or MSI) to a clean node.
2. Restore `env/` + `config/` (or point `USE_KEY_VAULT=true` at the vault).
3. Restore state:
   * File backend: drop the backed-up `logs/` state files + `data/*.db` in place.
   * SQL backend: standard SQL restore; then `scripts/sql/create-database.sql`
     is safe to re-run (idempotent) to verify schema.
4. `validate-config --strict` — both APIs must probe green.
5. Start the service. If state was lost or is stale beyond one full-crawl
   interval: run `reconcile --repair` once after the first crawl to force
   convergence (withdraws orphans, re-ingests missing).

**Restore-into-empty-state shortcut** (no backups at all): steps 1–2, then
`full-deployment` — identity crawl + full crawl + withdrawal pass rebuild
everything from source truth. Expect one full-crawl duration.

## Upgrade

1. Read CHANGELOG.md for the target version; SQL contract changes ship as
   idempotent, guarded DDL in `scripts/sql/create-database.sql`.
2. Stop the service (graceful — finishes the chunk, saves the checkpoint).
   HA: rolling — one node at a time; the others keep crawling.
3. Replace binaries (zip: overwrite dir; MSI: MajorUpgrade in place).
4. SQL backend: run `scripts/sql/create-database.sql` (safe re-run — proven
   twice-run in CI every build).
5. Start; verify `/health`, `/ready`, one `validate-config`, and the next
   run summary.

State files are **forward-compatible by policy**: new fields are additive
(e.g. the `acl_fingerprint` column migrates in-place on open of an older
SQLite DB; unknown JSON keys are ignored).

## Rollback

1. Stop the service.
2. Reinstall the previous bundle/MSI (MSI: uninstall newer, install older —
   downgrade-in-place is refused by design).
3. State written by the newer version is readable by the older one as long as
   the schema is additive (the norm). If a release ever notes a breaking
   state change in CHANGELOG.md, rollback = restore the pre-upgrade state
   backup taken in Upgrade step 2, then `reconcile --repair`.
4. Worst case (no compatible state): delete local state / re-provision SQL,
   `full-deployment` — RTO = full crawl, never data loss.

## Schema versioning

* **SQL**: `scripts/sql/create-database.sql` is the single versioned contract
  (docs/SQL_CONTRACT.md); every statement is guarded (`IF NOT EXISTS`) so any
  older database upgrades by re-running the current script. CI provisions it
  twice per build to prove re-run safety; the offline suite pins script ⇄
  in-code DDL ⇄ contract-doc agreement in all directions.
* **SQLite**: opened databases migrate in code
  (`MigrateAclFingerprintColumn`-style additive migrations) — no manual step.
* **JSON state**: parse-tolerant; unknown keys ignored, missing files treated
  as empty. No version stamp needed while changes stay additive; a breaking
  change would introduce a `schema_version` key and a CHANGELOG gate first.

## DR test (do this yearly, before you need it)

Clone a sandbox tenant config → restore last night's backup to a fresh VM →
start → `reconcile` (no repair) → finding count should be ≈ content changed
since the backup. Time it; that number is your evidence-based RTO.
