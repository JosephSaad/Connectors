# HA — active-active multi-node crawling

`HA_MODE=true` lets several nodes run the **same** `--continuous` command
against the **same** SQL Server database and split the crawl between them.
`USE_SQL_SERVER=true` is required — startup fails fast otherwise.

## How coordination works

* When a cycle is due each node calls **OpenOrJoinCrawl**: exactly one node
  INSERTs the `dbo.CrawlSessions` row (a filtered unique index allows one
  open session per connector), the rest join it.
* Crawlable resources (each Seismic teamsite, plus the Library object) are
  rows in `dbo.CrawlClaims`, keyed `(CrawlId, "{CONNECTOR_ID}:teamsite:{id}")`.
* A node **claims** a resource by inserting the row (`Status='claimed'`);
  while working it refreshes `HeartbeatUtc` every `HA_HEARTBEAT_SECONDS`
  (default 60) at each chunk boundary, and marks the claim `done` or
  `failed` on completion.
* A claim whose heartbeat is older than `HA_CLAIM_TIMEOUT_SECONDS`
  (default 300) is considered dead and is **stolen** with a guarded UPDATE
  (only wins if the row still matches what the stealer read — no double
  ownership). Claims already `done`/`failed` this crawl are never re-claimed.
* Nodes skip resources another node holds, so a full crawl is naturally
  sharded by teamsite.

All other state (checkpoints, sync timestamps, dead-letter, identity store,
tracked items) already lives in the shared database, so any node can resume
any teamsite.

## Crawl close — the pinned close-with-failed-claims semantics

When a node finishes its share it calls **TryCloseCrawl**:

1. Claims still `claimed` (work in flight elsewhere) → the crawl stays open;
   nobody records sync state yet.
2. No claims in flight → the crawl **closes even if some claims failed** —
   the session status becomes `failed` instead of `closed`. Failures are
   dead-lettered and re-driven with `retry-failed`; they never wedge the
   crawl open.
3. Exactly **one** node wins the open→closed/failed UPDATE; its id is
   recorded in `ClosedBy`, and only that node writes the sync timestamp and
   clears the checkpoint.
4. The close result is derived from `ClosedBy = <this node>`, so a transient
   retry of a close whose COMMIT succeeded but whose ack was lost still
   reports "closed by me" — the winner stays exactly-one under concurrency.
5. A graceful stop (SCM / Ctrl+C) leaves the session open and the node's
   claims held; their heartbeats expire and another node (or the next start)
   resumes and eventually closes the crawl.

These semantics are pinned by unit tests over the pure `CloseDecision`
function (`HaCloseDecisionTests`).

## Deployment rules

1. Point `SQL_CONNECTION_STRING` at the **Always On AG listener**, not a replica.
2. Set a stable, unique `NODE_ID` per node (mandatory in containers).
3. Set `GRAPH_RETRY_JITTER=true` on every node (see docs/RETRY.md).
4. Divide `GRAPH_BATCH_WORKERS` by the node count — the Graph 429 quota is per
   connection, not per node.
5. Keep `HA_CLAIM_TIMEOUT_SECONDS` comfortably above `HA_HEARTBEAT_SECONDS`
   (5× is the default ratio).

## Failure behaviour

* Node crash mid-teamsite → its claim goes stale, another node steals it and
  resumes from the shared checkpoint (completed chunks are skipped).
* Withdrawal ("not seen") reaping only considers items in teamsites the
  current node actually crawled, so a node never reaps another node's items.
* Graceful stop (SCM / Ctrl+C) finishes the in-flight chunk, flushes the
  Graph batch, saves the checkpoint and leaves its claims for reclaim.
