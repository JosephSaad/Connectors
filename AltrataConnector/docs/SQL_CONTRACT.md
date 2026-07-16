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
| `dbo.altrata_deadletter` | dead-letter queue (payload JSON replayable by retry-failed; `op` distinguishes upsert vs delete replays — a guarded ALTER migrates v1 tables) |
| `dbo.altrata_kv` | seat-list hash, billable-lookup counter, last-sync timestamps, per-delivery processed timestamps |
| `dbo.altrata_deliveries` | processed-delivery ledger |
| `dbo.altrata_leases` | HA lease table (lease_name PK, owner, expires_utc) |
| `dbo.altrata_suppressed` | erased subject ids — durable suppression against re-delivery (forget-subject) |

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
  identical semantics; switching backends is a config change, not a code
  change — but state does NOT migrate automatically between backends.

## Minimum permissions

`CREATE TABLE` on first run (or pre-create the tables above), then
`SELECT / INSERT / UPDATE / DELETE` on the eight tables.
