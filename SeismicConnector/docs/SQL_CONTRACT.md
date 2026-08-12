# SQL_CONTRACT — SQL Server state backend

`USE_SQL_SERVER=true` + `SQL_CONNECTION_STRING` moves **all** connector state
out of local files/SQLite into a shared SQL Server database (required for
`HA_MODE`). `SQL_USE_MANAGED_IDENTITY=true` swaps connection-string
credentials for an Entra Managed Identity token
(`https://database.windows.net/.default`). `Encrypt=True` is forced unless the
connection string sets `Encrypt` explicitly.

## Schema

Provision ahead of time with `scripts/sql/create-database.sql` (idempotent —
safe to re-run; every statement is guarded, and the offline SQL suite proves
it by construction) plus `scripts/sql/create-login.sql` for a least-privilege
app login. The connector also auto-provisions the same tables on first use
(`SqlExecutor.SchemaDdl`); the offline SQL validation suite fails the build if
the two drift.

| Table | Replaces | Key |
| --- | --- | --- |
| `dbo.SyncTimestamps` | `logs/sync_state.json` | `ConnectorId` |
| `dbo.Checkpoints` | `logs/checkpoint_{id}.json` | `ConnectorId, ObjectType` |
| `dbo.DeadLetter` | `logs/failed_records_{id}.jsonl` | identity `Id` |
| `dbo.Principals` | SQLite `principals` | `ConnectorId, SeismicId` |
| `dbo.TrackedItems` | SQLite `tracked_items` | `ConnectorId, ItemId` |
| `dbo.CrawlSessions` | (HA only — no file equivalent) | `CrawlId` |
| `dbo.CrawlClaims` | (HA only — no file equivalent) | `CrawlId, ResourceKey` |

Notes:

* Every row is scoped by `ConnectorId`, so several connectors can share one
  database.
* `Checkpoints.ChunkIndex` only ever increases for a given `SinceIso`
  boundary; a new boundary resets it (same semantics as the JSON file).
* `DeadLetter` keeps full request/response bodies (`NVARCHAR(MAX)`) for
  debugging, mirroring the JSONL records, plus a `CorrelationId` column tying
  each record to its crawl's distributed trace (added by a guarded
  `ALTER TABLE ... ADD` so an older database upgrades in place).
* `TrackedItems.AclFingerprint` stores a stable hash of each item's resolved
  ACL principal set, powering permission-change re-ACL detection
  (`PERMISSION_REACL` / the `reacl` command). It is added by a guarded
  `ALTER TABLE ... ADD` so an older database upgrades in place; NULL means
  "unknown", which forces one baseline re-resolve.
* `dbo.CrawlSessions.Status` is `open` → `closed` | `failed`; a filtered
  unique index allows exactly one open session per connector, and `ClosedBy`
  records the single node that performed the close (the pinned
  close-with-failed-claims semantics — see docs/HA.md).
* `dbo.CrawlClaims.Status` is `claimed` | `done` | `failed`; a stale
  heartbeat makes a `claimed` row stealable.

## Transient-fault retry

All SQL operations retry on the classic transient error numbers (deadlock
1205, Azure throttling 49918/49919/49920/10928/10929, AG failover
40613/40197/40501/4060, timeout -2, broken connection 10054/10053/233/64) with
exponential backoff, up to `SQL_MAX_RETRIES` (default 5).

## Sizing

State volume is tiny (one row per principal, tracked item and dead-letter
record). A basic S0/S1 Azure SQL database or any on-prem AG is sufficient;
storage is dominated by `DeadLetter` bodies — prune with your own retention
job if you never run `retry-failed --clear-on-success`.
