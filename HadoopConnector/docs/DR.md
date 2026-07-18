# Disaster recovery

The connector holds **no primary data**. BDH is the source of truth for
records; the Graph connection is a rebuildable index; connector state
(watermark, checkpoints, inventory, identity map, dead-letter) is an
optimization. Everything below follows from one fact: **a full crawl rebuilds
the index and every piece of state from the source** — DR here is about
bounding re-crawl COST, not preventing data loss.

## RPO / RTO

| Asset | RPO | RTO | Why |
|---|---|---|---|
| Indexed content | 24 h behind the live org REGARDLESS of any disaster — BDH loads nightly, and every item carries `DataAsOf`. A connector outage adds only its own duration on top. | one full crawl after service restoration | Re-crawl recovers everything; nothing indexed is unrecoverable from BDH |
| Sync watermark (`sync_state.json` / `dbo.SyncTimestamps`) | zero-loss not required: losing it means the next incremental reads WITHOUT a watermark (wide re-read), never data loss | immediate (fail-safe already built in) | `ReadLastSync` treats corrupt/missing state as never-synced, loudly |
| Checkpoints | disposable | immediate | restart from chunk 0 is idempotent (PUTs) |
| Ingested-item inventory | rebuilt by the next full crawl's confirmed puts | one full crawl | until rebuilt, deletion sweeps are the risk area — see below |
| Identity store | rebuilt by the next identity sync | minutes | `identity-dry-run --save` rebuilds on demand |
| Dead-letter queue | the ONLY state whose loss loses information (which items failed) | n/a | failures are also in run logs (and the event log); a lost queue means re-discovering failures on the next crawl |

**Fleet RTO:** cold standby is sufficient. Restore = install bundle/MSI +
config from change control + secrets from Key Vault + (SQL mode) database
restore, then run `validate-config --strict` and start the service. With SQL
HA, surviving nodes already ARE the DR — a dead node's claims expire and work
resumes without operator action (`docs/HA.md`).

## What to back up (and what not to bother with)

| Backing store | Back up? | How |
|---|---|---|
| `config/` (schema, filters, graph-schema, classification) | YES — but the system of record is the git repo/change control, not host backups | versioned config repo; the deployed copy is a checkout |
| `env/.env.local` (+ per-node values) | YES (config repo / deployment tooling) | secrets are NOT in it — they live in `.env.local.user`/Key Vault |
| `env/.env.local.user` / Key Vault secrets | Key Vault: yes (soft-delete + purge protection). File mode: recreate from the issuing systems (Entra, HDFS) rather than backing up plaintext secrets | rotation runbooks in `SECURITY.md` |
| SQL Server database (`USE_SQL_SERVER=true`) | YES — normal SQL backup regime (it also holds the dead-letter queue and inventory) | FULL nightly + log backups to taste; AG replicas already give HA |
| `logs/` state files + `data/*.db` (file/SQLite mode) | Optional — cheap insurance that avoids one wide re-read/full crawl | file-level backup while the service is STOPPED (SQLite files are not crash-consistent to copy hot) |
| Run logs (`logs/{prefix}_{ts}/`) | per your log-retention policy (SIEM usually has them already) | `LOG_RETENTION_DAYS` prunes locally |
| The Graph connection itself | NO — it is the thing we rebuild | `setup-connection` recreates connection + schema |

## Restore procedures

**Single node, file/SQLite state (typical Windows Server):**

1. Provision host, install the release bundle (or MSI) — `README.md` Windows
   service section.
2. Lay down `config/` + `env/` from change control; secrets from Key Vault or
   re-issue.
3. Optionally restore `logs/` state files + `data/*.db` from backup (skip →
   first crawl is a full re-baseline; that is fine, just slower).
4. `validate-config --strict` → `Start-Service HadoopConnector`.

**SQL/HA fleet:** restore the database (or fail over the AG), then start
nodes; nothing else is node-local except config. A restored-from-backup
database may contain a crawl marked open by nodes that no longer exist —
claims expire after `HA_CLAIM_TIMEOUT_SECONDS` and are taken over; no manual
table surgery.

**After any restore that lost or rewound the inventory:** the first full
crawl's deletion sweep compares live BDH against a stale/empty inventory —
exactly the situation the sweep guards exist for. Leave
`DELETION_SYNC_MAX_ITEMS` / `DELETION_SYNC_MAX_PERCENT` at their defaults
during recovery (an empty inventory sweeps nothing — see the bootstrap note in
`docs/DELETION_SYNC.md`); if a sweep is skipped with the
`deletion_sweep_skipped` alert, follow that runbook rather than raising caps
reflexively.

## Upgrade

1. Read `CHANGELOG.md` for the target version (breaking changes are called out
   there; state-file formats are stable within a major version).
2. SQL mode: run `scripts/sql/create-database.sql` for the target version —
   it is idempotent by construction (CI proves the re-run path against a live
   SQL Server 2022) and additive within a major version.
3. Stop the service (graceful — finishes the chunk, saves the checkpoint),
   swap binaries (bundle unzip or MSI `MajorUpgrade` in place), start.
4. `validate-config --strict` before re-enabling schedules; watch
   `/ready` + the first cycle's crawl summary.
5. HA: rolling — upgrade one node, let it rejoin, proceed. Nodes of adjacent
   versions may briefly coexist because the SQL contract is
   backward-compatible within a major version (`docs/SQL_CONTRACT.md`).

## Rollback

- Binaries: reinstall the previous bundle (zips are versioned and checksummed;
  MSI `MajorUpgrade` refuses downgrades — uninstall then install the older
  MSI, or use the zip).
- State written by the newer version remains readable by the older within a
  major version (formats are append/ignore-unknown). If a newer MAJOR version
  migrated state, restore the pre-upgrade state backup (or accept a full
  re-baseline crawl — always safe).
- Graph side: the connection schema is additive-registered; an older binary
  simply ignores newer properties. Never delete the connection to roll back —
  that discards the tenant's index and forces a cold rebuild.

## Schema versioning

Three schemas, three contracts:

| Schema | Versioning rule |
|---|---|
| SQL state schema (`scripts/sql/create-database.sql`) | Idempotent, additive within a major version; every table/proc create is guarded so re-runs are safe. Offline ScriptDom tests pin grammar + C#⇄schema drift; the CI `sql-provisioning` job re-runs it twice against live SQL 2022. |
| State files (`sync_state.json`, `checkpoint_*.json`, `failed_records_*.jsonl`) | Stable key/line formats shared with the sibling connectors; readers ignore unknown fields, writers never remove fields within a major version. Corruption handling is fail-safe by design (see "State corruption" in `docs/RUNBOOKS.md`). |
| Graph connection schema (`config/graph-schema.json`) | Registration is additive: new properties may be added and registered; removing/retyping a property is a BREAKING change requiring a new connection (Graph limitation) — plan it as a re-baseline, ideally on a fresh `CONNECTOR_ID` swapped in after its first full crawl. |

Version identity: assembly `<Version>` in the csproj, release tags `v*`,
`CHANGELOG.md` per release; the MSI carries the same version for SCCM/Intune
detection rules.
