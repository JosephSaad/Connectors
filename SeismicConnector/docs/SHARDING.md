# SHARDING — multiple Graph connections (GRAPH_CONNECTION_SHARDS)

The Graph external-connection ingestion rate limit is **per connection**.
Sharding the schema object types across several connections therefore
multiplies write throughput: k shards ≈ k × the single-connection rate.

Off by default: with `GRAPH_CONNECTION_SHARDS` unset, nothing changes — one
connection, one schema, one dead-letter queue.

## Configuration

A JSON object mapping each Graph connection id to the schema object types it
owns:

```bash
GRAPH_CONNECTION_SHARDS={"seismicContent":["ContentItem"],"seismicLibs":["Library"]}
```

Validation (all problems reported at once; `validate-config` checks it too):

1. the value parses as a JSON object with at least one shard;
2. every connection id passes the normal connector-id rules (3–32 chars,
   alphanumeric, no reserved Microsoft prefix) and is unique;
3. every listed object type exists in the **enabled** objects of
   `config/schema.json`;
4. every enabled schema object is assigned to **exactly one** shard —
   unassigned and doubly-assigned objects both fail validation.

## Behaviour when enabled

* `setup-connection` / `full-deployment` create **every shard's** connection
  and register the schema on each.
* Each crawl cycle loops the shards: a per-shard pipeline ingests only that
  shard's object types into that shard's connection. Crawl counters, the
  reconciliation report and the No-MNE filter are shared across shards.
* The shard that owns `ContentItem` also runs the withdrawal pass (expiry,
  not-in-source, late-exclusion) — tracked items live in the shared identity
  store, and their externalItems live in that shard's connection.
* Dead-lettering is per shard connection id
  (`logs/failed_records_<shardConnectionId>.jsonl` / `dbo.DeadLetter` rows);
  the `/metrics` dead-letter depth is the **sum across shards**. Use
  `retry-failed --file logs/failed_records_<shard>.jsonl` to re-drive one
  shard's queue.
* Checkpoints and sync timestamps are keyed by shard connection id as well,
  so each shard resumes independently.

## Sizing guidance

* Give each shard its own `GRAPH_BATCH_WORKERS` budget — the 429 quota is per
  connection, so shards do not steal each other's throughput.
* In HA_MODE the same division applies per node per shard; keep
  `GRAPH_RETRY_JITTER=true`.
* Content search results are unaffected: Copilot queries all connections the
  tenant has enabled.
