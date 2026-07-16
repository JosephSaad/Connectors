# Deletion / tombstone sync & `reconcile`

Clarizen's REST API has no deletion feed and the `LastUpdatedOn` delta cursor
never surfaces removed records, so the connector detects deletions with an
**existence sweep** built on an **ingested-item inventory**.

## The inventory

Every CONFIRMED Graph put (batch or single) records its external item id in the
inventory; ids are removed only after a successful (or `404`) Graph DELETE.
Failed puts are never recorded, so the inventory is always a lower bound on
what the index actually contains.

| Backend | Location |
|---|---|
| SQLite (default) | `data/{CONNECTOR_ID}_inventory.db`, table `items` |
| SQL Server (`USE_SQL_SERVER=true`) | `dbo.ItemInventory`, keyed `(ConnectorId, ItemId)` |

Under connection sharding each shard keeps its own inventory (keyed by the
shard connection id), exactly like checkpoints and the dead-letter queue.

## The sweep (full crawls only)

After a **full** crawl finishes an object type, the pipeline compares that
type's inventory against the full source id set it just fetched (TDW export or
REST API). Ids in the inventory but absent from the source were deleted in
Clarizen → they are DELETEd from the Graph connection (`$batch` of up to 20
DELETEs; `404` counts as already-gone) and dropped from the inventory. Failed
deletes stay in the inventory and are retried by the next sweep.

Incremental crawls **never** sweep — they only see changed records, so absence
proves nothing. Metrics: `clarizen_connector_items_deleted_total`.

`DELETION_SYNC=false` disables the sweep entirely (the inventory is still
maintained so `reconcile` keeps working).

### Mass-deletion safety guards

A source outage, a truncated TDW export or a wrong `filterCondition` could make
the entire index look stale. Two guards protect the sweep; tripping either one
**skips** it, logs a warning, fires a `deletion_sweep_skipped` webhook alert,
and lists the object type in the crawl summary:

- **Absolute cap** — `DELETION_SYNC_MAX_ITEMS` (default 1000): never
  auto-delete more than this many items of one object type in a single sweep,
  regardless of inventory size. This is the guard that also covers small
  object types. Set it to `0` to disable explicitly; a negative value is a
  misconfiguration and falls back to the default with a warning.
- **Percentage guard** — `DELETION_SYNC_MAX_PERCENT` (default 25): when more
  than this percentage of an object type's inventory would be deleted in one
  sweep the sweep is skipped. The guard engages once the inventory holds at
  least 20 items (a percentage is meaningless on a handful of rows) **or**,
  at any size, whenever the full crawl's source returned **zero rows** for the
  object type — an empty source is the classic outage / truncated-export
  signature, so a tiny all-stale inventory can no longer be wiped 100 %
  unguarded. `0` or `100` disables the percent guard explicitly; values outside
  `[0, 100]` are a misconfiguration and fall back to the default with a
  warning (a negative value never silently disables the guard).

If the drop is real, raise the threshold/cap (or disable them as above) and
re-run, or use `reconcile --fix`.

## `reconcile [--type X] [--fix]`

On-demand drift audit between three views (source, inventory, and — via the
inventory contract — the index):

- **MISSING** — in Clarizen, not indexed (never ingested / dead-lettered / new
  since the last crawl). Reported only; the next crawl or `retry-failed`
  ingests them.
- **STALE** — indexed, gone from Clarizen. With `--fix`, DELETEd from the
  connection and dropped from the inventory. `--fix` intentionally bypasses
  the mass-deletion guard — an explicit operator action is its own consent.

Exit code is `0` only when no drift remains, so the command slots directly
into a monitoring cron:

```bash
ClarizenConnector reconcile                 # report drift, exit 1 if any
ClarizenConnector reconcile --type Project  # one object type
ClarizenConnector reconcile --fix           # also delete stale items
```

## Bootstrapping an existing deployment

Deployments created before the inventory existed have indexed items the
inventory does not know about. The first full crawl re-puts every live record
(repopulating the inventory), after which sweeps are accurate. Items deleted
in Clarizen *before* that first inventory-aware crawl are invisible to the
sweep — remove them once with the Graph API or by recreating the connection.
