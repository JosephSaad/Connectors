# HA — active-active multi-node crawling

`HA_MODE=true` lets several connector nodes crawl the same feed concurrently.
Requires `USE_SQL_SERVER=true`: all state (checkpoints, ledger, dead-letter,
KV, identity) must live in the shared SQL backend, and the lease table
coordinates work.

## Lease model

Table `dbo.altrata_leases (lease_name PK, owner, expires_utc)`.

* Work unit = one feed delivery (`delivery:{deliveryId}`).
* Before processing, a node MERGEs the lease under `HOLDLOCK`; it wins when
  the lease is free, expired, or already its own (re-entrant).
* Default TTL 5 minutes; a crashed node's lease simply expires and another
  node picks the delivery up — the shared checkpoint makes the takeover
  resume-correct.
* `Release` deletes the lease after the delivery completes.
* Node identity: `HA_NODE_ID` (default `machine:pid`).

## Crawl close — pinned close-with-failed-claims semantics

At the end of a crawl (`HaCoordinator.TryCloseCrawl`, mirroring the reference
connector's `usp_CloseCrawlIfComplete`):

* **Exactly one node wins the close** for a given crawl id
  (`crawl-close:{kind}:{lastDeliveryId}` lease) — that node records the sync
  timestamp and runs the retention pass; every other node skips both
  (`CrawlResult.ClosedByThisNode == false`).
* **The win is pinned**: the close lease is re-entrant for its owner, so a
  retry by the winning node (e.g. after a lost ack) still reports true —
  it never flips to another node.
* **Failed claims still close the crawl.** A rejected (checksum) or
  unreconciled delivery does NOT wedge the crawl open; it closes with status
  `failed` instead of `closed`, recorded in shared state under
  `crawl_status_{crawlId}` (with `crawl_closed_by_{crawlId}`) so operators
  can see which node closed what, and with what outcome.

## Operational guidance

* Point every node at the same `FEED_PATH` (shared mount) and the same
  `SQL_CONNECTION_STRING`.
* Set `GRAPH_RETRY_JITTER=true` on all nodes — they share one Graph 429
  quota per connection (docs/RETRY.md).
* HA buys availability, not throughput: N nodes *divide* one connection's
  quota. Connection sharding (docs/SHARDING.md) *multiplies* it; they stack,
  and shards coordinate independently (all coordination rows are scoped per
  connection id).
* Size `GRAPH_BATCH_WORKERS` per node per connection.
* `/metrics` is per-node; aggregate in Prometheus.

## What HA_MODE does NOT do

* No leader election, no work stealing mid-delivery: a delivery is processed
  end-to-end by the node holding its lease.
* File-mode (`USE_SQL_SERVER=false`) multi-node operation is unsupported —
  startup fails with a configuration error.
