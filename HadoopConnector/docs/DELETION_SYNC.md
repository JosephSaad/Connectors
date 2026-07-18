# Deletion / tombstone sync & `reconcile`

BDH has no deletion feed: a record deleted in Salesforce simply stops
appearing in subsequent nightly loads, and the incremental dt-watermark never
surfaces removals. The connector therefore detects deletions with an
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
type's inventory against the full source id set it just fetched from BDH
(partition scan + filters). Ids in the inventory but absent from the source
were deleted upstream → they are DELETEd from the Graph connection (`$batch`
of up to 20 DELETEs; `404` counts as already-gone) and dropped from the
inventory. Failed deletes stay in the inventory and are retried by the next
sweep.

Two situations **never** sweep:

- **Incremental crawls** — they only read partitions inside the watermark
  window, so absence proves nothing.
- **Row-cap-truncated fetches** — when `BDH_MAX_RECORDS_PER_OBJECT` stopped
  the fetch early, the source id set is incomplete and a sweep would
  mass-delete live records. The object is marked partial (`row_cap_hit`
  alert), listed in the crawl summary, and its sweep is skipped with a
  warning until an untruncated full crawl completes.

Metrics: `hadoop_connector_items_deleted_total`.

`DELETION_SYNC=false` disables the sweep entirely (the inventory is still
maintained so `reconcile` keeps working).

A related refusal happens even earlier: if every predicate-matched row in a
fetch lacked a usable `Id` column, the crawl for that object is aborted as a
hard error — an id-less source set would make every inventory id look stale
and corrupt the sweep.

### Mass-deletion safety guards

A source outage, a failed/truncated nightly load, or an over-tightened
`filters.json` could make the entire index look stale. Two guards protect the
sweep; tripping either one **skips** it, logs a warning, fires a
`deletion_sweep_skipped` webhook alert, and lists the object type in the crawl
summary:

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
  object type — an empty source is the classic outage / missing-nightly-load
  signature, so a tiny all-stale inventory can no longer be wiped 100 %
  unguarded. `0` or `100` disables the percent guard explicitly; values outside
  `[0, 100]` are a misconfiguration and fall back to the default with a
  warning (a negative value never silently disables the guard).

If the drop is real, raise the threshold/cap (or disable them as above) and
re-run, or use `reconcile --fix`. If the drop is *not* real, check the BDH
side first: did the nightly load run, and did a recent `filters.json` edit
shrink the match set (`docs/FILTERS.md`)?

## `reconcile [--type X] [--fix]`

On-demand drift audit between three views (source, inventory, and — via the
inventory contract — the index). The source view is the same filtered
full-crawl fetch the crawler uses (partition pruning + record predicates +
row cap):

- **MISSING** — in BDH, not indexed (never ingested / dead-lettered / new
  since the last crawl). Reported only; the next crawl or `retry-failed`
  ingests them.
- **STALE** — indexed, gone from BDH (deleted in Salesforce). With `--fix`,
  DELETEd from the connection and dropped from the inventory. `--fix`
  intentionally bypasses the mass-deletion guards — an explicit operator
  action is its own consent.
- **Truncated fetch** — when the source fetch for an object hits the row cap,
  stale detection (and therefore `--fix`) is **skipped for that object**: a
  truncated source set would flag live records as stale. The report still
  shows the source/indexed counts; tighten filters or raise
  `BDH_MAX_RECORDS_PER_OBJECT` and re-run.

Sharding-aware: each object type reconciles against the connection that owns
it, with that connection's inventory. Exit code is `0` only when no drift
remains, so the command slots directly into a monitoring cron:

```bash
HadoopConnector reconcile                 # report drift, exit 1 if any
HadoopConnector reconcile --type Contact  # one object type
HadoopConnector reconcile --fix           # also delete stale items
```

Note that `retry-failed` also interacts with deletions: a dead-lettered
record that can no longer be found in BDH (deleted upstream, or pruned out of
the filtered partitions) is dropped from the queue rather than retried
forever — the record is re-located via the targeted newest-first partition
scan before each retry.

## Bootstrapping an existing deployment

Deployments created before the inventory existed have indexed items the
inventory does not know about. The first full crawl re-puts every live record
(repopulating the inventory), after which sweeps are accurate. Items deleted
upstream *before* that first inventory-aware crawl are invisible to the
sweep — remove them once with the Graph API or by recreating the connection.
