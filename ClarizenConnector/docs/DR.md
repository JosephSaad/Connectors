# Disaster recovery

What state exists, what losing it costs, and the tested paths back. Companion:
`docs/RUNBOOKS.md` § state corruption, `SECURITY.md` § data at rest,
`docs/SQL_CONTRACT.md` (schema), `docs/HA.md` (multi-node).

## Objectives

| | Target | Rationale |
|---|---|---|
| **RPO (index)** | one crawl interval | the Graph index is REBUILDABLE from Clarizen — worst case is a full re-crawl; nothing in the index is the system of record. |
| **RPO (state)** | 24 h (daily backup) | losing state costs re-crawl time + a temporary deletion-sync gap, never data. |
| **RTO (single node)** | < 30 min | reinstall bundle/MSI + restore env + start service; resumes from checkpoint. |
| **RTO (full rebuild, no state)** | one full-crawl duration | fresh deployment re-provisions the connection and re-ingests; PUTs are idempotent. |

The one asset that is NOT rebuildable from source: **credentials/config**
(`env/.env.local.user` or Key Vault contents). Protect those first.

## State inventory and loss impact

| State | Location (file backend) | SQL backend | Lose it → |
|---|---|---|---|
| Delta sync cursor | `logs/sync_state.json` | `dbo.SyncTimestamps` | next incremental behaves like a first run (full fetch; idempotent PUTs — cost only) |
| Crawl checkpoint | `logs/checkpoint_<id>.json` | `dbo.Checkpoints` | interrupted crawl restarts from the beginning of each object |
| Dead-letter queue | `logs/failed_records_<id>.jsonl` | `dbo.DeadLetter` | pending failures lost — re-caught by the next full crawl/reconcile |
| Ingested-item inventory | `data/<id>_inventory.db` | `dbo.ItemInventory` | deletion sync blind until rebuilt; the FIRST post-restore full crawl repopulates it, deletions catch up on the SECOND (bootstrap note in `docs/DELETION_SYNC.md`) |
| Identity map | `data/<id>_identity.db` | `dbo.PrincipalMappings` | rebuilt by the next identity sync (full crawl or `identity-dry-run --save`) |
| HA coordination | — (SQL only) | `dbo.CrawlRuns`, `dbo.ObjectClaims` | transient by nature; a fresh crawl recreates rows |
| Config + secrets | `config/`, `env/` | same + Key Vault | NOT rebuildable — back up config, vault the secrets |

## Backup

**SQLite/file backend** — stop the service (SCM stop is graceful: checkpoint
saved), then copy `logs/sync_state.json`, `logs/checkpoint_*.json`,
`logs/failed_records_*.jsonl`, `data/*.db`, `config/`, `env/` (env files
secured like the secrets they are). Copying `data/*.db` while the service runs
is NOT crash-consistent — stop first, or accept an inventory rebuild on
restore. Daily is plenty (see RPO).

**SQL Server backend** — normal database backup discipline
(FULL daily + log backups per your DBA policy; AG replicas already give HA).
The connector needs nothing special: every table is in
`scripts/sql/create-database.sql`.

**Key Vault** — soft-delete + purge protection on; secrets are re-enterable
from the rotation runbooks in `SECURITY.md` if the vault is lost.

## Restore

1. Install the same version (bundle zip / MSI), lay out `config/` + `env/`
   from backup, or point the service at a restored `CLARIZEN_CONNECTOR_HOME`.
2. File backend: restore `logs/` state files + `data/*.db`.
   SQL backend: restore the DB, run `scripts/sql/create-database.sql`
   (idempotent — safe on a restored DB; also the schema-upgrade path).
3. `validate-config --strict` — proves credentials, config and connectivity
   before any crawl.
4. Start the service. It resumes from the restored checkpoint/cursor. If
   state was lost entirely: run a full crawl, and remember the deletion-sync
   bootstrap (deletions are trustworthy from the SECOND full crawl).
5. `reconcile` afterwards audits index-vs-source drift; `reconcile --fix`
   repairs it.

## Upgrade / rollback

- **Upgrade**: stop service (graceful) → deploy new bundle/MSI over the
  install dir (config/env preserved; MSI MajorUpgrade handles replacement) →
  run `scripts/sql/create-database.sql` on SQL backends (idempotent
  re-run IS the upgrade path — proven twice against live SQL in CI) → start.
  HA: upgrade node-by-node; claims from the stopped node expire and survivors
  carry the crawl.
- **Rollback**: redeploy the previous bundle. State written by a newer
  version is governed by the schema-versioning policy below; if the newer
  version added state the old one does not know, the old version ignores
  unknown fields/tables (additive-only rule) — rollback is deploy-and-start.
  Roll back within one release step; further back, prefer restore-from-backup.

## State-schema versioning policy

- **Additive-only within a major version.** New state = new JSON fields, new
  columns (nullable/defaulted), or new tables. Existing readers must tolerate
  unknown fields (JSON) and never `SELECT *`-depend on column sets.
- **`scripts/sql/create-database.sql` is the single migration vehicle** —
  idempotent by construction (guarded `CREATE`/`ALTER`), re-run on every
  upgrade, validated offline (ScriptDom grammar + idempotency + C#⇄schema
  drift tests) and live (CI provisions twice).
- **Breaking state changes require a major version** and an explicit migration
  note in `CHANGELOG.md` with a tested fallback (documented rebuild path at
  minimum). Item-id shape is pinned forever — see `docs/THREAT_MODEL.md`
  § FIPS for why ids must never be silently re-derived.
- Corrupt-state behaviour is part of the contract: a state file that exists
  but cannot be parsed logs `State file '<path>' exists but could not be
  parsed` and degrades to the documented safe default (first-run cursor,
  checkpointless resume, per-line dead-letter skip) — see
  `docs/RUNBOOKS.md` § state corruption.
