# Disaster Recovery

Backup, restore, upgrade and rollback for the connector's state. Companion:
[RUNBOOKS.md](RUNBOOKS.md#state-db-corruption) (corruption triage),
[HA.md](HA.md) (node loss without DR), [SQL_CONTRACT.md](SQL_CONTRACT.md)
(schema contract).

## RPO / RTO statement

**The connector's state is a cache of work already done, not a system of
record.** Salesforce holds the data; the Graph connection holds the index; the
state DB holds bookkeeping (identity mappings, checkpoints, sync timestamps,
item inventory, dead-letter queue) that makes the next crawl *incremental*
instead of *full*.

- **RPO**: state loss = **re-crawl cost, not data loss.** Losing every state
  file means the next run is a full crawl + identity crawl (hours — see
  [CAPACITY.md](CAPACITY.md) for your org's size), plus the current dead-letter
  backlog is forgotten (those items are picked up again by the same full
  crawl). Nothing customer-visible is lost permanently.
- **RTO**: time to restore = service reinstall (minutes, [MSI/script](DEPLOYMENT_ENTERPRISE.md))
  + either state restore (minutes) or the full re-crawl window. If Copilot
  search continuity matters, note the **index itself survives** in the Graph
  connection regardless of connector state — a dead connector means a *stale*
  index, not an empty one.
- Practical target: back up state daily; accept ≤24h of incremental
  bookkeeping loss; treat anything worse as "run a full crawl".

## What to back up

| State | File backend (default) | SQL backend (`USE_SQL_SERVER=true`) |
|---|---|---|
| Identity store (ACL mappings, field cache, sessions) | `data/{CONNECTOR_ID}_identity.db` (+ `-wal`/`-shm`) | `SalesforceConnector` DB |
| Item inventory (deletion sweep / reconcile) | `data/{CONNECTOR_ID}_inventory.db` (+ `-wal`/`-shm`) | same DB |
| Sync timestamps | `logs/sync_state.json` | same DB |
| Checkpoints | `logs/checkpoint_{CONNECTOR_ID}.json` | same DB |
| Dead-letter queue | `logs/failed_records_{CONNECTOR_ID}.jsonl` | same DB (`dbo.DeadLetter`) |
| Config (no secrets) | `config/`, `env/.env.local` | same files |
| Secrets | `env/.env.local.user` — or nothing, if Key Vault holds them (preferred) | same |

Run logs (`logs/{prefix}_{timestamp}/`) are diagnostics, not state — back up
only if your retention policy wants them.

## Backup procedure — SQLite/files

The SQLite stores run in **WAL mode**, so a naive file copy taken mid-write can
miss the WAL tail. Two safe options:

1. **Cold copy** (simplest, recommended): stop the service (graceful — finishes
   the chunk, checkpoints), copy the whole `data/` + the four `logs/` state
   files, start the service. Seconds of downtime; crawls resume from
   checkpoint.
2. **Hot copy**: `sqlite3 data/{id}_identity.db ".backup 'backup/identity.db'"`
   (the online backup API is WAL-safe), same for the inventory DB; the JSON/
   JSONL files are single-writer append/replace and safe to copy live (a
   torn-in-flight dead-letter line is detected on read and fixable per
   [RUNBOOKS.md](RUNBOOKS.md#state-db-corruption)).

Restore: stop service → put the files back in place (`data/`, `logs/`) → start
service. The connector notices nothing; it resumes from the restored
checkpoint/timestamps, and the next incremental covers the gap since the
backup (`since` = restored sync timestamp).

## Backup procedure — SQL Server

Native tooling, nothing connector-specific:

```sql
BACKUP DATABASE SalesforceConnector TO DISK = N'...\SalesforceConnector.bak'
    WITH COMPRESSION, CHECKSUM;
```

- Point-in-time: FULL recovery model + log backups if you want it; SIMPLE +
  daily fulls is proportionate for re-crawlable state.
- Always On AG (the HA deployment): back up on the preferred replica as usual;
  an AG failover is **not** a DR event for the connector — retry logic rides
  through it (`SQL_MAX_RETRIES`, [RETRY.md](RETRY.md)).
- Restore: stop **all** nodes → `RESTORE DATABASE` → start nodes. After a
  restore to an earlier point, expect one crawl's worth of drift; run
  `reconcile` to measure and `reconcile --fix` (or wait for the next full
  crawl + deletion sweep) to repair.

## Upgrade procedure

1. **Read `CHANGELOG.md`** for the target version — migration notes live there.
2. **Stop the service** (`Stop-Service SalesforceCopilotConnector`, or SCM stop
   via SCCM/Intune). Stop is graceful: current chunk finishes, checkpoint
   saved.
3. **Back up state** (above). Cheap insurance; upgrades don't normally touch it.
4. **Binary swap**: install the new MSI (`packaging/msi/`, majorupgrade handles
   replace) or unzip the new bundle over the install dir / `dotnet publish`
   output. `config/` and `env/` are not overwritten by the zip layout — diff
   `env/.env.local.example` for new knobs.
5. **Migrations note**: there is no separate migration tool.
   - SQLite stores migrate **in place, additively**, on first open (column
     presence is probed — e.g. `PRAGMA table_info(sync_sessions)` — and missing
     columns/tables are added; never dropped).
   - SQL Server: re-run `scripts/sql/create-database.sql` for the target
     version — it is **idempotent** (CI proves the double-run on every build)
     and only creates/alters, never drops data.
6. **Start the service.** First run resumes from the existing checkpoint —
   an upgrade does not force a full crawl unless the changelog says so.
7. Verify: `validate-config --strict`, then watch `/metrics`
   (`crawls_completed_total` advancing, `dead_letter_depth` flat).

HA note: upgrade node-by-node only when the changelog marks the versions
state-compatible; otherwise stop all nodes, upgrade all, start all (claims from
the old version expire harmlessly).

## Rollback

1. Stop the service.
2. Reinstall the previous MSI / restore the previous binary directory (keep the
   last bundle zip next to the install for exactly this).
3. State: **schema is additive**, so a newer-schema state DB opens fine under
   the older binary in almost all cases (unknown columns are ignored). If the
   changelog for the skipped version flagged a breaking state change, restore
   the pre-upgrade state backup instead — and accept the incremental gap or run
   a full crawl.
4. Start, verify as in step 7 above.

## State-schema versioning / migration policy

- **Policy: additive-only.** New columns/tables may appear; existing ones are
  never renamed, retyped, or dropped within a major version. Readers must
  tolerate unknown columns; writers must supply defaults for new ones.
- **SQLite**: no explicit version number — presence-probing on open IS the
  migration (`Graph/IdentityStore.cs`). A file from any older 1.x opens under
  any newer 1.x, and (because additive) usually vice versa.
- **SQL Server**: `scripts/sql/create-database.sql` is the schema's single
  source of truth and the migration script in one — idempotent `CREATE IF NOT
  EXISTS`/`ALTER` steps, re-run per upgrade (contract in
  [SQL_CONTRACT.md](SQL_CONTRACT.md)).
- **Dead-letter / checkpoint / sync-state files**: byte-compatible with the
  Python original by design; new optional fields (e.g. the redaction note,
  `@redaction`) are additive JSON keys that old readers ignore.
- **Breaking changes** (reserved for a major version bump): shipped with an
  explicit migration section in `CHANGELOG.md` and, where feasible, a
  dual-read window. The identity-store MD5 `instance_hash` key is pinned by
  this policy — see the FIPS note in [THREAT_MODEL.md](THREAT_MODEL.md).
