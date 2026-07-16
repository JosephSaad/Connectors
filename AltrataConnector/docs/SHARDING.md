# Connection Sharding (`GRAPH_CONNECTION_SHARDS`)

A single Microsoft Graph external connection is rate-limited (documented at
~25 items/sec per connection), so spreading the Altrata datasets across **N**
connections multiplies write capacity.

> **Off by default.** When `GRAPH_CONNECTION_SHARDS` is unset the connector
> behaves exactly as a single-connection deployment — no code path, log line
> or state file changes. Strict no-op until you opt in.

## 1. How it works

Each **shard** is one Graph external connection paired with the datasets it
owns. A shard maps 1:1 to a connection in the Microsoft 365 admin center: its
own connection id, schema, index quota — and, in this connector, its own
state key (checkpoint, dead-letter queue, delivery ledger, identity DB and
seat hash all live under the shard's connection id).

With the shard map set:

* `setup-connection` / `full-deployment` ensure **every** shard's connection
  and register its schema.
* Each crawl runs once per shard, restricted to that shard's datasets, into
  that shard's connection; per-shard results are aggregated into one summary.
* `seat-sync` and `purge-all` iterate every shard (each carries its own seat
  hash / item registry).
* `/metrics` dead-letter depth sums across shard queues.
* A **misconfigured shard map aborts the run** — the connector never crawls a
  partial or overlapping partition.

Building blocks: `Altrata/ShardingConfig.cs` (`IsEnabled`, `TryLoad`,
`TryParse`, `ForShard`) and `Runtime.CreateForShard`.

## 2. Env format

JSON object mapping each **connection id** to the **datasets** it owns:

```bash
GRAPH_CONNECTION_SHARDS='{
  "altrataPeopleA":  ["PersonProfile", "CareerHistory", "BoardMembership"],
  "altrataWealthB":  ["WealthIndicator", "RelationshipPath"],
  "altrataOrgsC":    ["Organization"]
}'
```

Validation (`TryLoad` reports EVERY problem; never throws on bad input):

- valid JSON object, at least one shard;
- connection ids valid (3-32 alphanumeric, no reserved Microsoft prefix) and
  unique;
- each shard maps to a non-empty array of known dataset names;
- **exact partition**: every dataset in exactly one shard — unassigned and
  doubly-assigned datasets are both reported.

## 3. Capacity math

The binding constraint is the per-connection ingestion rate, so N shards ≈
N × the single-connection ceiling. `$batch` does **not** multiply this — each
PUT in a batch counts individually against the per-connection rate; batching
reduces HTTP overhead, sharding adds quota. Watch real `429 Retry-After`
behaviour (`altrata_graph_throttle_429_total`) to validate at your shard count.
The ~30-connections-per-tenant limit applies — budget accordingly.

## 4. HA interaction

Shards coordinate independently: leases, checkpoints and ledgers are all
scoped per connection id, so nodes working shard A never contend with nodes
on shard B. HA buys availability (nodes *divide* one connection's quota);
sharding buys throughput (*multiplies* connections). They stack — size
`GRAPH_BATCH_WORKERS` per node per connection, and set
`GRAPH_RETRY_JITTER=true` on every node.

## 5. Operational caveats

- Every shard is a real connection in the M365 admin center — provision and
  monitor each one; schemas provision asynchronously.
- Datasets are partitioned, so a record lives in exactly one connection — no
  cross-shard duplicates as long as the partition is respected (enforced).
- Adding a new dataset to the connector makes it unassigned; `TryLoad` fails
  until you add it to exactly one shard. Intentional — prevents silently
  dropping a dataset from the crawl.
- `retry-failed` is natively shard-aware: it replays every shard's queue
  (upserts re-PUT, delta tombstones re-DELETE) against that shard's own
  connection, then the base queue — no `CONNECTOR_ID=<shardId>` workaround.
