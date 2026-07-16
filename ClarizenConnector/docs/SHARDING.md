# Connection Sharding (`GRAPH_CONNECTION_SHARDS`)

Multi-connection sharding is the throughput lever: a single Microsoft Graph
external connection is rate-limited (documented planning figure ~25 items/sec,
per connection), so spreading the Clarizen object types across **N**
connections multiplies aggregate write capacity ≈ **N ×** the single-connection
ceiling.

> **Off by default.** When `GRAPH_CONNECTION_SHARDS` is unset the connector
> behaves exactly as before — one connection ingesting every object type. No
> code path, log line, or state file changes until you opt in.

## How it works

Each **shard** is one Graph external connection paired with the set of Clarizen
object types it owns. A shard maps 1:1 to a connection in the Microsoft 365
admin center: its own connection id, its own schema, and its own index quota.

When the env var is set:

1. `setup-connection` / `full-deployment` provision **every** shard's
   connection + schema (`RuntimeContext.ProvisionConnectionsAsync`).
2. The crawl loops the shards: each shard's object types are fetched, ACL'd and
   ingested **into that shard's connection** (`IngestPipeline.RunAsync`).
3. All state is per shard connection id: checkpoints
   (`checkpoint_<shardId>.json`), dead-letter (`failed_records_<shardId>.jsonl`),
   delta-sync timestamps, and — in HA — crawl/claim rows. The health endpoint's
   dead-letter depth is the sum across shards.
4. `ingest-item` / `retry-failed` route each record to the shard that owns its
   object type.

## Env format

```bash
GRAPH_CONNECTION_SHARDS='{
  "clarizenWorkA": ["Project", "Task", "Milestone"],
  "clarizenWorkB": ["Issue", "Risk", "Timesheet", "RegularResourceLink", "Discussion", "Attachment"]
}'
```

## Validation

`ShardingConfig.TryLoad` reports **every** problem through its error out-param
(it never throws for user input) and `validate-config` runs it in preflight:

- valid JSON object of `connectionId -> [objectTypes...]`, at least one shard;
- each connection id passes the connector-id rules (3–32 alphanumeric, no
  reserved Microsoft prefix) and is unique;
- each shard maps to a non-empty array of non-empty strings;
- every listed object type exists in `config/schema.json`;
- the shards form an **exact partition** of the schema object list —
  unassigned objects and objects claimed by two shards are both reported.

A misconfigured shard map aborts the crawl before any item is written — a
partial or overlapping crawl is never run. Adding a new object type to
`config/schema.json` intentionally fails validation until you assign it to
exactly one shard.

## HA interaction

Shards coordinate independently: HA crawl rows, claims and checkpoints are all
keyed per connection id, so the nodes working shard A never contend with those
on shard B. HA still buys **availability** (N nodes divide one connection's
quota); sharding is what **multiplies** capacity. They compose cleanly — size
`GRAPH_BATCH_WORKERS` per node with the per-connection quota in mind.

## Operational caveats

- Each shard is a real connection in the M365 admin center (own schema, own
  quota) — provision and monitor each one, and mind the ~30-connections-per-
  tenant platform limit.
- Treat "N shards ≈ N × 25 items/s" as a planning assumption; validate against
  real `429 Retry-After` behaviour at your target shard count (the adaptive
  concurrency dial and `clarizen_connector_throttled_429_total` metric show
  you where the real ceiling is).
- Object types are partitioned, so a record lives in exactly one connection —
  no cross-shard duplicates as long as the partition is respected (enforced).
