# High Availability (Active-Active) Operations Guide

Run two or more connector nodes against one Microsoft 365 connection with
coordinated crawls, shared state in SQL Server, and automatic takeover when a
node dies. Companion to [SQL_CONTRACT.md](SQL_CONTRACT.md) (binding contract).

## Topology

```
        ┌──────────────────────┐          ┌──────────────────────┐
        │  Node 1 (VM/Server)  │          │  Node 2 (VM/Server)  │
        │  run ingest          │          │  run ingest          │
        │    --continuous      │          │    --continuous      │
        │  HA_MODE=true        │          │  HA_MODE=true        │
        │  NODE_ID=sfc-node-1  │          │  NODE_ID=sfc-node-2  │
        └──────────┬───────────┘          └──────────┬───────────┘
                   │      SQL_CONNECTION_STRING      │
                   └──────────────┬──────────────────┘
                                  ▼
                    ┌──────────────────────────┐
                    │   AG Listener (SQL AG)   │
                    │  SalesforceConnector DB  │
                    │  sync state, checkpoints,│
                    │  dead-letter, claims     │
                    └──────────────────────────┘
                   Both nodes also talk directly to
                   Salesforce + Microsoft Graph.
```

Every node runs the **same** `--continuous` command. Coordination is entirely
in SQL Server:

1. When a cycle is due, every node calls `usp_OpenOrJoinCrawl` — exactly one
   creates the crawl (one pending claim per Salesforce object type), the rest
   join it.
2. Each node loops `usp_ClaimNextObject` → ingests that object type through
   the normal per-object pipeline (checkpointing each chunk in SQL) →
   `usp_CompleteClaim` (`done`, or `failed` on exception) → claims the next,
   until no claims remain.
3. While a node works an object, a background heartbeat refreshes the claim
   every `HA_HEARTBEAT_SECONDS`.
4. The node whose `usp_CloseCrawlIfComplete` call performs the close is the
   **only** one that clears the shared checkpoint, writes the last-sync
   timestamp, and records the content-crawl session. All others skip it.

## Environment variables

| Variable | Default | Notes |
|---|---|---|
| `USE_SQL_SERVER` | `false` | Must be `true` for HA. Moves sync state, checkpoints and dead-letter into SQL Server. |
| `SQL_CONNECTION_STRING` | — | Required. Point at the **AG listener**, not an individual replica. |
| `HA_MODE` | `false` | `true` enables crawl coordination. Startup fails fast if `USE_SQL_SERVER` is not also enabled. |
| `NODE_ID` | machine name | Stable, unique per node. Shows up in `ObjectClaims`/`vActiveCrawls`. |
| `HA_CLAIM_TIMEOUT_SECONDS` | `300` | Claim with a heartbeat older than this is reclaimable. Must be comfortably larger than `HA_HEARTBEAT_SECONDS`. |
| `HA_HEARTBEAT_SECONDS` | `60` | Heartbeat interval per active claim. |
| `GRAPH_BATCH_WORKERS` | — | **Divide by node count** — see quota note below. |
| `GRAPH_RETRY_JITTER` | `false` | **Set `true` in HA.** Adds ±20% jitter to computed retry backoff so throttled nodes don't retry in lockstep (server `Retry-After` is still honoured exactly). See [RETRY.md](RETRY.md). |
| `LOG_RETENTION_DAYS` | `0` | `> 0` → prune `logs/` run dirs (per node) and SQL history (`usp_PruneHistory`) older than N days at each command/cycle start. |

## Scheduling & cycle dedup

Each node keeps its own `--continuous` timer (`--incremental-hours` /
`--full-crawl-hours`). Duplicate cycles are prevented in two layers:

- `usp_OpenOrJoinCrawl` runs under an applock — concurrent nodes never create
  two crawls; laggards join the open one and help finish it.
- A node whose cycle comes due after the crawl already closed finds **no open
  crawl** and a **last-sync timestamp fresher than its due time** → it skips
  the cycle entirely (log: `[HA] Skipping cycle — last sync in SQL is fresher
  than this node's due time.`).

Joining nodes adopt the crawl's stored `SinceIso` boundary so all checkpoints
line up regardless of which node opened the crawl.

## Failure modes

| Failure | What happens |
|---|---|
| Node dies mid-object | Its heartbeat stops. After `HA_CLAIM_TIMEOUT_SECONDS`, another node's `usp_ClaimNextObject` reclaims the object and **resumes from the object's SQL checkpoint** — already-completed chunks are skipped, nothing is lost. |
| Node dies between objects | Remaining pending claims are simply picked up by the surviving nodes. |
| Graceful stop (service stop / Ctrl+X) | The node finishes the in-flight chunk, writes its checkpoint, and leaves its current claim held. The claim expires after the timeout and another node resumes it. |
| All nodes die | The crawl stays `open` with stale claims. The next scheduled cycle on any restarted node joins it and reclaims all stale objects. |
| Object worker throws | The claim completes with status `failed`; the crash is dead-lettered (`WORKER_CRASH`). The crawl still closes (failed claims are terminal), so investigate the dead-letter table and re-run. |
| SQL Server failover | The AG listener redirects connections. In-flight calls error and are retried at the next chunk/claim boundary; heartbeats tolerate transient failures until the claim timeout. |

Monitoring: query `vActiveCrawls` for live crawl progress and the per-node
claim breakdown, and `vDeadLetterSummary` for failure hotspots.

## Graph 429 quota (per connection!)

Microsoft Graph throttles **per external connection**, and every node shares
that one budget. Two nodes at full speed will each spend half their time in
429 retries. Size the Graph push concurrency per node as:

```
GRAPH_BATCH_WORKERS (per node) = single-node value ÷ node count
```

e.g. a single-node value of 8 becomes 4 on each of two nodes. Adaptive
concurrency still dials down automatically on residual 429s; retries stay
per-node. Also set `GRAPH_RETRY_JITTER=true` on every node: nodes throttled by
the same quota event otherwise compute identical backoff ladders and retry in
lockstep (details in [RETRY.md](RETRY.md)).

## Deployment checklist

1. ☐ Deploy the DB: run the `scripts/sql` DDL against the `SalesforceConnector`
   database on the Availability Group primary; verify the AG listener resolves
   from every node.
2. ☐ Grant each node's service account EXECUTE on the `dbo.usp_*` procedures
   (and SELECT on the `v*` views for monitoring).
3. ☐ On every node set: `USE_SQL_SERVER=true`, `SQL_CONNECTION_STRING=<listener>`,
   `HA_MODE=true`, a unique `NODE_ID`, and the halved `GRAPH_BATCH_WORKERS`.
4. ☐ Keep `HA_HEARTBEAT_SECONDS` (60) well below `HA_CLAIM_TIMEOUT_SECONDS` (300).
5. ☐ Start node 1 with the usual `full-deployment` to create the connection and
   schema; then start the same `--continuous` command on all nodes.
6. ☐ Verify in `vActiveCrawls` that one crawl is open and both `NODE_ID`s are
   claiming objects.
7. ☐ Failover drill: kill one node mid-crawl and confirm the other reclaims its
   object after the claim timeout and the crawl closes with a single last-sync
   write.
8. ☐ Optionally set `LOG_RETENTION_DAYS` to prune old run dirs and SQL history.
