# High availability (active-active)

`HA_MODE=true` lets two or more nodes run the **same** `--continuous` command
against the **same** SQL Server database and share one crawl. Requires the SQL
state backend (`USE_SQL_SERVER=true` + `SQL_CONNECTION_STRING`); startup fails
fast otherwise. For real HA, point the connection string at an Always On AG
**listener**, not an individual replica.

## How nodes coordinate

Everything goes through two tables (`scripts/sql/create-database.sql`):

1. **Open/join** — at each schedule slot every node computes the same crawl key
   `{connector}|{kind}|{yyyyMMddHHmm}` and races a guarded INSERT into
   `dbo.CrawlRuns`. The winner "opens" the crawl; the rest join the same key.
2. **Claim** — each BDH object type is an atomic work item in
   `dbo.ObjectClaims`. A node claims it with a single guarded UPDATE/INSERT; a
   claim carries the owning `NodeId` and a heartbeat timestamp.
3. **Heartbeat** — while a node works an object it refreshes `HeartbeatUtc`
   every `HA_HEARTBEAT_SECONDS` (default 60).
4. **Takeover** — a claim whose heartbeat is older than
   `HA_CLAIM_TIMEOUT_SECONDS` (default 300) is reclaimable: a survivor's UPDATE
   takes it over and resumes **from that object type's checkpoint** (chunks the
   dead node completed are skipped).
5. **Close** — when every object type is `completed`, the nodes race a guarded
   UPDATE on `dbo.CrawlRuns`; exactly one wins, closes the crawl, and writes the
   sync timestamp (the incremental watermark). Losers skip the stamp, so the
   cursor is written once.

## Deployment checklist

- [ ] `scripts/sql/create-database.sql` run against the shared database.
- [ ] Same `CONNECTOR_ID`, `config/`, and schedule flags on every node.
- [ ] `NODE_ID` set explicitly per node in containers (machine name is random).
- [ ] `GRAPH_RETRY_JITTER=true` on every node (shared 429 quota — see
      docs/RETRY.md).
- [ ] `HA_CLAIM_TIMEOUT_SECONDS` comfortably larger than
      `HA_HEARTBEAT_SECONDS` (5× is a good default).
- [ ] `GRAPH_BATCH_WORKERS` sized per node with the shared per-connection 429
      quota in mind (single-node value ÷ node count is the starting point; the
      adaptive dial finds the rest).
- [ ] Every node can reach the BDH source: the same `HDFS_NAMENODE_URL` (or
      Knox gateway) and, in `localpath` mode, the same mounted
      `BDH_EXPORT_PATH` on every node. Object claims fan the WebHDFS read
      load out across the fleet.
- [ ] Monitor `dbo.vActiveCrawls` — `HeartbeatAgeSeconds` near the claim
      timeout means a node died mid-object.

## Failure modes

| Failure | Behaviour |
|---|---|
| Node crashes mid-object | Its claim heartbeat goes stale; a survivor reclaims after the timeout and resumes from the checkpoint. |
| Node crashes between objects | Nothing to take over; remaining objects are claimed normally. |
| Object worker throws | The claim completes with status `failed` (terminal — never reclaimed); the crash is dead-lettered (`WORKER_CRASH`). The crawl **still closes** — the crawl row records `failed` instead of `closed`, the closer still writes the sync timestamp, and `retry-failed` / the dead-letter table is the recovery path. |
| All nodes die | The crawl stays open; the next scheduled slot opens a new crawl and the checkpoint/sync watermark guarantee no loss. |
| Close race / lost commit-ack | Exactly one node ever wins the open→closed transition (`ClosedBy` stamps the winner, so the winner's retry still reports true and every other node false — the sync timestamp is written exactly once). |
| SQL failover | `SqlExecutor` transient retry rides it out (docs/RETRY.md). |
| Clock skew between nodes | Heartbeats/expiry use `SYSUTCDATETIME()` on the SQL server — node clocks are irrelevant. |

## Sharding interaction

With `GRAPH_CONNECTION_SHARDS` (docs/SHARDING.md) each shard coordinates
independently — crawl rows, claims and checkpoints are keyed per shard
connection id, so HA and sharding compose without contention.
