# Connection Sharding (`GRAPH_CONNECTION_SHARDS`)

Multi-connection sharding is the throughput lever from `docs/CAPACITY.md`: a single
Microsoft Graph external connection is rate-limited, so spreading the Salesforce
object types across **N** connections multiplies write capacity. This document
covers how it works, the env format, the capacity math, the HA interaction, and the
operational caveats.

> **Off by default.** When `GRAPH_CONNECTION_SHARDS` is unset the connector behaves
> exactly as it does today — a single connection ingesting every object type. No code
> path, log line, or state file changes. This is a strict no-op until you opt in.
> (Improvements-contract item #2 — see `docs/IMPROVEMENTS_CONTRACT.md`.)

## 1. How it works

Each **shard** is one Graph external connection paired with the set of Salesforce
object types it owns. A shard maps 1:1 to a connection in the Microsoft 365 admin
center: it has its own connection id, its own schema, its own ACL/identity groups,
and its own index quota.

When `GRAPH_CONNECTION_SHARDS` is set, the deployment flow that today sets up **one**
connection and ingests **all** object types instead:

1. Parses and validates the shard map (see §2, §4).
2. For **each** shard: creates/ensures that shard's connection, registers its schema,
   and configures its search settings — the same STEP&nbsp;2–5 work a single
   deployment does today, but targeted at the shard's connection id.
3. Ingests **only that shard's object types** into that shard's connection, then
   aggregates the per-shard `IngestionStats` into one combined summary.

The building blocks live in `Salesforce/ShardingConfig.cs`:

| Member | Purpose |
|---|---|
| `bool IsEnabled` | `true` iff `GRAPH_CONNECTION_SHARDS` is set (cheap gate; no JSON parse). |
| `bool TryLoad(AppConfig baseConfig, out IReadOnlyList<Shard> shards, out string? error)` | Parse + validate. `false` + `null` error when disabled; `false` + populated `error` on any validation failure; `true` on success. Never throws for bad input. |
| `AppConfig ForShard(AppConfig baseConfig, Shard shard)` | Clone the config bound to the shard's connection id (and, for a single-object shard, restrict ingestion to that object). Use it to set up the shard's connection + schema. |
| `AppConfig ForShardObject(AppConfig baseConfig, Shard shard, string objectType)` | Clone bound to the shard's connection **and** restricted to one object type — the unit the ingest loop calls. |
| `void Accumulate(IngestionStats target, IngestionStats source)` | Fold a shard/object result into a combined summary. |

A `Shard` is `record Shard(string ConnectionId, IReadOnlyList<string> ObjectTypes)`.

### Object-restriction mechanism (why the ingest pipeline is unchanged)

`Graph/Ingest.cs :: IngestContentAsync` decides which object types to fetch and
ingest from `ApiClient.ObjectConfigs` (a process-wide static list) **unless**
`AppConfig.DebugObjectType` is set, in which case it restricts to that **single**
object type — the same seam the `ingest-object` command uses. It does **not** read
`AppConfig.ObjectNames` to decide what to ingest.

So the only per-config object restriction the ingest pipeline honors *without any edit
to `Ingest.cs`* is a single object type. `ForShardObject` sets exactly that field
(`DebugObjectType`), and the per-shard loop restricts a multi-object shard by
**iterating its object types** — one `IngestContentAsync` call per object type, each
scoped to the shard's connection. This is fully honored today.

The Wave-2 per-shard loop (sketch — the orchestrator owns the actual wiring in the
command/`Ingest` layer; `ShardingConfig` supplies the building blocks):

```csharp
if (ShardingConfig.TryLoad(baseConfig, out var shards, out var error))
{
    var combined = new IngestionStats();
    foreach (var shard in shards)
    {
        // 1. Set up THIS shard's connection + schema + search settings.
        var shardBase = ShardingConfig.ForShard(baseConfig, shard);
        await Connection.EnsureConnectionAsync(shardBase, client, ts);
        await Schema.EnsureSchemaAsync(shardBase, client);
        await Connection.SetSearchSettingsAsync(shardBase, client);

        // 2. Ingest only this shard's object types, aggregating stats.
        foreach (var objectType in shard.ObjectTypes)
        {
            var perObject = ShardingConfig.ForShardObject(baseConfig, shard, objectType);
            var s = await Ingest.IngestContentAsync(perObject, client, since, dashboard);
            ShardingConfig.Accumulate(combined, s);
        }
    }
}
else if (error != null)
{
    // Refuse to run on a misconfigured shard map — print `error` and abort.
}
// else: disabled → today's single-connection path, unchanged.
```

> **Optional single-call optimization (not required).** If you would rather issue one
> ingest call per shard instead of one per object type, add a nullable
> `IReadOnlyList<string>? ShardObjectTypes` field to `AppConfig` (default `null` ⇒
> byte-identical default behavior) and read it in three one-line spots that prefer the
> shard set when non-null: the `activeTypes` assignment in
> `Graph/Ingest.cs :: IngestContentAsync` ("Determine active object types"), and the
> `activeConfigs` filters in `Salesforce/ApiClient.cs :: GetAllItemsFromApiAsync` and
> `:: GetObjectCountsAsync`. `ShardingConfig` cannot add that field itself (it lives on
> `AppConfig` in `Settings.cs`, which item #2 must not edit). Until then, the
> per-object iteration above is the self-contained, zero-edit path.

## 2. Env format

`GRAPH_CONNECTION_SHARDS` is a JSON object mapping each **connection id** to the list
of **object types** it owns:

```bash
GRAPH_CONNECTION_SHARDS='{
  "salesforceCrmA": ["Account", "Contact", "Opportunity", "Lead", "Campaign", "CampaignMember", "FeedItem"],
  "salesforceCrmB": ["Case", "CaseComment", "Task", "Event", "Product2", "OpportunityLineItem"],
  "salesforceCrmC": ["Order", "OrderItem", "Quote", "QuoteLineItem", "CollaborationGroup"]
}'
```

Every object type must match an object in the base `config/schema.json` object list,
and the shards must **partition** that list — together they cover every schema object,
with no object in two shards.

## 3. Capacity math (cite `docs/CAPACITY.md`)

Per `docs/CAPACITY.md` §1–2, the binding constraint is the **per-connection** item
ingestion rate — **25 items/sec** (last documented Nov 2024; treat as a planning
assumption, validate against real 429s). Because that ceiling is *per connection*,
running **N** shards ≈ **N × 25 items/sec**:

| Shards (N) | Aggregate ceiling (documented) | Aggregate ceiling (derated ~70%) | Items/hour (documented) |
|---|---|---|---|
| 1 | 25 items/s | ~17.5 items/s | 90,000 |
| 2 | 50 items/s | ~35 items/s | 180,000 |
| 3 | 75 items/s | ~52.5 items/s | 270,000 |
| N | N × 25 items/s | N × ~17.5 items/s | N × 90,000 |

Batching (`$batch`) does **not** multiply this — each PUT in a batch counts
individually against the per-connection rate (`docs/CAPACITY.md` limit #13). Sharding
adds connections, which is the mechanism that actually raises aggregate throughput
(`docs/CAPACITY.md` §6, "Multiple connections"). The global per-app limit
(130,000 requests / 10 s across all tenants, limit #11) still applies far above any
realistic shard count.

## 4. Validation

`TryLoad` reports **every** problem it finds through the `error` out-param (it never
throws for user-input problems). It enforces:

- **Valid JSON object** — the value parses as a JSON object of `connectionId -> [types]`.
- **At least one shard.**
- **Valid connection ids** — each id passes `Settings.ValidateConnectorId` (length
  3–32, alphanumeric only, not a reserved Microsoft/system prefix such as `SharePoint`,
  `Teams`, `Exchange`, …), and connection ids are unique across shards.
- **Non-empty string arrays** — each shard maps to a non-empty JSON array of
  non-empty strings.
- **Known object types** — every listed type exists in the base schema object list.
- **Exact partition** — every schema object is assigned to **exactly one** shard;
  unassigned objects and objects assigned to more than one shard are both reported.

On any failure, the caller should print `error` and abort rather than run a partial
or overlapping crawl.

## 5. HA interaction

Each shard is an **independent Graph connection**, and the HA crawl coordinator keys
on the **connector id** (see `docs/SQL_CONTRACT.md` / `docs/HA.md` — the coordinator's
crawl rows, checkpoints, and object claims are all scoped per connection id).
Therefore:

- **Shards coordinate independently.** In HA (active-active) mode, the nodes working
  shard A's connection coordinate among themselves on shard A's connection id, and the
  nodes on shard B coordinate on B's — the two never contend, because a claim, a
  checkpoint, and a crawl row for connection A are distinct rows from those for
  connection B. Sharding and HA are orthogonal and compose cleanly.
- **HA still buys availability, not throughput** (`docs/CAPACITY.md` §5): within a
  single connection, N HA nodes *divide* the 25 items/s quota. Sharding is the lever
  that *multiplies* it. Use HA for crawl-window resilience and sharding for capacity;
  they stack.
- **Per-node worker sizing** (`GRAPH_BATCH_WORKERS`) is still per node and per
  connection. If you run both HA and sharding, size workers with the *per-connection*
  quota in mind, not the aggregate.

## 6. Operational caveats

- **Each shard is a real connection in the M365 admin center.** It shows up
  separately, has its own schema (which is provisioned asynchronously and cannot be
  deleted — `docs/CAPACITY.md` §1 notes), and its own ACL/identity groups. Provision
  and monitor each one.
- **The 30-connections-per-tenant limit applies** (last documented, `docs/CAPACITY.md`
  limit #2). Every shard counts against it — budget your shard count accordingly and
  leave headroom for other connectors in the tenant.
- **Scaling behavior is undocumented.** Microsoft delisted the platform rate/limit
  table in May 2025 (`docs/CAPACITY.md` caveat). Treat "N shards ≈ N × 25 items/s" as
  a planning assumption and **validate against real `429 Retry-After` behavior** at
  your target shard count.
- **Search relevance / duplicates.** Object types are partitioned across connections,
  so a given record lives in exactly one connection — no cross-shard duplication as
  long as the partition is respected (which `TryLoad` enforces).
- **Schema drift.** Adding a new object type to `config/schema.json` means it is now
  unassigned; `TryLoad` will fail until you add it to exactly one shard. This is
  intentional — it prevents silently dropping an object type from the crawl.
