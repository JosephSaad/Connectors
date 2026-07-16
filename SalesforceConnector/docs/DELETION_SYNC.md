# Deletion / tombstone sync & `reconcile`

Salesforce gives the connector no reliable deletion feed. Its native deletion
signal — `IsDeleted` surfaced through the `queryAll` endpoint — only covers the
**Recycle Bin**, which retains deleted records for roughly **15 days** (and less
under storage pressure). Records that were hard-deleted, purged from the Recycle
Bin, deleted while the connector was offline for longer than that window, or
moved out of a crawl's `filterCondition` scope leave **no** trace `queryAll` can
find. The connector closes that gap with an **existence sweep** built on an
**ingested-item inventory**.

## The inventory

Every CONFIRMED Graph put (batch or single) records its external item id in the
inventory; ids are removed only after a successful (or HTTP `404`) Graph DELETE.
Failed puts are never recorded, so the inventory is always a lower bound on what
the index actually contains. The Graph external item id equals the Salesforce
record `Id`, so the inventory and the live source are compared in one id space.

| Backend | Location |
|---|---|
| SQLite (default) | `data/{CONNECTOR_ID}_inventory.db`, table `items` |
| SQL Server (`USE_SQL_SERVER=true`) | `dbo.ItemInventory`, keyed `(ConnectorId, ItemId)` |

Under connection sharding each shard keeps its own inventory (keyed by the shard
connection id), exactly like checkpoints and the dead-letter queue.

## The sweep (full crawls only)

After a **full** crawl finishes and its sync state is recorded, the connector
runs one automatic sweep per object type. For each type it fetches a **fresh,
id-only, same-filter** query of Salesforce —

```sql
SELECT Id FROM {ObjectType} [WHERE {FilterCondition}]
```

— paginated to completion, and compares that live id set against the inventory.
Ids in the inventory but **absent from the fresh source** were deleted in
Salesforce, so they are DELETEd from the Graph connection (HTTP `404` counts as
already-gone) and dropped from the inventory. Failed deletes stay in the
inventory and are retried by the next sweep.

Two properties make this safe:

- **The source set is a fresh query at sweep time, never "the ids ingested this
  run."** An item that still exists in Salesforce but merely *failed to ingest*
  this crawl (a transient Graph/ACL error) is present in the fresh query and is
  therefore **never** swept. Only genuine absence from the live source deletes.
- **The same `filterCondition` the content crawl uses is applied**, so records
  intentionally filtered out of the crawl are never mistaken for deletions.

Incremental crawls **never** sweep — they only see records changed since the
last cursor, so absence proves nothing. The sweep runs exactly once per full
crawl: in single-node deployments always, and in HA only on the node that closes
the crawl (the same exactly-once gate used for recording sync state).

Metric: `salesforce_connector_items_deleted_total`. A sweep failure is logged as
a warning and **never fails the crawl** — the next full crawl (or `reconcile
--fix`) retries.

`DELETION_SYNC=false` disables the sweep entirely; the inventory is still
maintained so `reconcile` keeps working.

### Mass-deletion safety guard

A Salesforce outage, an authentication blip, a wrong `filterCondition`, or a
partial id fetch could make a large fraction of the index look stale at once.
When more than `DELETION_SYNC_MAX_PERCENT` (default **25**) of an object type's
inventory would be deleted in one sweep — **and** the inventory holds at least
**20** items (`MinInventoryForSafetyGuard`, so a small or brand-new connection is
never blocked from pruning) — the sweep for that object is **skipped**:

- a warning is logged naming stale / indexed / percent / threshold,
- a `deletion_sweep_skipped` webhook alert fires (payload: `objectType`,
  `stale`, `inventory`, `threshold`) when `ALERT_WEBHOOK_URL` is set,
- the object type is listed in the crawl summary (`stats.SweepSkipped`),
- **nothing is deleted** for that object.

If the drop is real, raise `DELETION_SYNC_MAX_PERCENT` (or set it to `0` / `100`
to disable the guard) and re-run, or repair on demand with `reconcile --fix`.
The floor and percentage are independent: the guard engages only when *both* the
20-item floor and the percentage threshold are exceeded.

## `reconcile [--type X] [--fix]`

The on-demand equivalent of the sweep. It compares the same three views (live
Salesforce source, the inventory, and — through the inventory — the index) and
reports two drift classes:

- **MISSING** — in Salesforce, not indexed (never ingested / dead-lettered / new
  since the last crawl). Reported only; the next crawl or `retry-failed` ingests
  them.
- **STALE** — indexed, gone from Salesforce. With `--fix`, DELETEd from the
  connection and dropped from the inventory.

`reconcile --fix` **intentionally has no mass-deletion guard** — running it is an
explicit, attended operator action and its own consent. The guard exists only on
the *automatic* sweep, which fires unattended on every full crawl. Both paths
share the same delete helper, so a `404` is treated as already-gone and hard
failures are kept for retry identically.

The exit code is `0` only when no drift remains (after any `--fix` pass), so the
command slots directly into a monitoring cron:

```bash
run.py reconcile                 # report drift, exit 1 if any
run.py reconcile --type Case     # one object type
run.py reconcile --fix           # also delete stale items (no guard)
```

## Bootstrapping an existing deployment

Deployments created before the inventory existed have indexed items the
inventory does not know about. The first full crawl re-puts every live record
(repopulating the inventory), after which sweeps are accurate. Items deleted in
Salesforce *before* that first inventory-aware crawl are invisible to the sweep —
remove them once with `reconcile --fix` after a full crawl, with the Graph API,
or by recreating the connection.

## Configuration

| Env var | Default | Effect |
|---|---|---|
| `DELETION_SYNC` | `true` | `false`/`0`/`no` disables the automatic sweep (inventory still maintained). |
| `DELETION_SYNC_MAX_PERCENT` | `25` | Mass-deletion guard threshold; `0` or `>= 100` disables the guard. |
