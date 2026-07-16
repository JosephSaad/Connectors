# SQL Server state backend — schema contract

Activated by `USE_SQL_SERVER=true` + `SQL_CONNECTION_STRING`. All connector
state moves from files/SQLite into one shared database, provisioned once with
`scripts/sql/create-database.sql` (idempotent). `Encrypt=True` is forced unless
the connection string sets `Encrypt` explicitly. `SQL_USE_MANAGED_IDENTITY=true`
swaps connection-string credentials for an Entra Managed Identity token
(audience `https://database.windows.net/.default`).

## Tables

| Table | Replaces | Key |
|---|---|---|
| `dbo.SyncTimestamps` | `logs/sync_state.json` | `ConnectorId` |
| `dbo.Checkpoints` | `logs/checkpoint_{id}.json` | `(ConnectorId, ObjectType)` |
| `dbo.DeadLetter` | `logs/failed_records_{id}.jsonl` | `Id` (identity) |
| `dbo.Principals` | `data/{id}_identity.db` (SQLite) | `(ConnectorId, ClarizenId)` |
| `dbo.ItemInventory` | `data/{id}_inventory.db` (SQLite) | `(ConnectorId, ItemId)` |
| `dbo.CrawlRuns` | — (HA only) | `CrawlKey` |
| `dbo.ObjectClaims` | — (HA only) | `(CrawlKey, ObjectType)` |

## Semantics the code depends on

- **Checkpoints:** a write with a different `SinceIso` first deletes the
  connector's rows (new run boundary), then MERGEs; `ChunkIndex` only ever
  advances (`IIF(@chunk > ChunkIndex, ...)`).
- **Dead letter:** append-only inserts; `ReadDeadLetter` orders by `Id`;
  request/response bodies stored as JSON text; `CorrelationId` (nullable, added
  in round 4 via an idempotent migration `ALTER`) ties each failure to the
  crawl cycle that produced it — see `docs/TRACING.md`.
- **Principals:** MERGE upsert keyed on `(ConnectorId, ClarizenId)`;
  `EntraId IS NOT NULL AND <> ''` defines "resolved".
- **ItemInventory:** MERGE upsert keyed on `(ConnectorId, ItemId)`; rows are
  inserted only for CONFIRMED Graph puts and removed only after a successful
  (or 404) Graph DELETE — the deletion sweep and `reconcile` trust this
  invariant.
- **ObjectClaims:** the claim/heartbeat/takeover statements compare
  `HeartbeatUtc` against `SYSUTCDATETIME()` **on the server**, so node clock
  skew cannot corrupt leases. `Status` transitions: `claimed → completed` or
  `claimed → failed` (worker crash); both are TERMINAL — never reclaimed, and
  `failed` never blocks the crawl close.
- **CrawlRuns:** `Status` transitions `open → closed` (or `open → failed` when
  any claim failed) via a guarded UPDATE; `ClosedBy` stamps the winner so a
  commit-ack-loss retry stays stable — the single-winner close is what makes
  the delta-sync stamp exactly-once.

## Operator surface

- `dbo.vActiveCrawls` — open crawls with per-claim heartbeat age.
- `dbo.usp_PruneHistory @RetentionDays` — deletes closed/failed crawl history
  and old dead-letter rows (no-op for `@RetentionDays <= 0`).

## Least-privilege login

The app needs `SELECT/INSERT/UPDATE/DELETE` on the six tables, `SELECT` on the
view, and `EXECUTE` on `usp_PruneHistory` — nothing else (no DDL after
provisioning).
