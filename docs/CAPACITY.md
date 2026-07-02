# Capacity & Throughput Specification

Sizing guidance for this connector against Microsoft Graph external-connection
(Microsoft 365 Copilot connectors) service limits. All Microsoft figures below
were verified against learn.microsoft.com on **2026-07-01**. Anything not
traceable to a cited source is explicitly labeled **assumption**.

> **Documentation caveat (important).** On 2025-05-14 Microsoft removed the
> platform capacity/throughput table from the connectors API limits page
> (commit ["Removing platform limits from documentation"](https://github.com/microsoftgraph/microsoft-graph-docs-contrib/commit/e3a29a48d3)).
> The current live page publishes **no ingestion rate limit and no
> connection/item-count caps**. Figures marked *(last documented, Nov 2024)*
> come from the final published revision of that table
> ([source, git rev 51e5fb545a](https://github.com/microsoftgraph/microsoft-graph-docs-contrib/blob/51e5fb545a/concepts/connecting-external-content-api-limits.md))
> and should be treated as planning assumptions, not contractual limits.
> Observed 429 `Retry-After` behavior is the only ground truth at runtime.

## 1. Constraint table

| # | Limit | Value | Status | Source (accessed 2026-07-01) |
|---|---|---|---|---|
| 1 | Item ingestion throughput per connection | **25 items/sec** | *Last documented, Nov 2024; removed from live docs May 2025* | [api-limits @ 51e5fb545a](https://github.com/microsoftgraph/microsoft-graph-docs-contrib/blob/51e5fb545a/concepts/connecting-external-content-api-limits.md) |
| 2 | Connections per M365 tenant | 30 | *Last documented, Nov 2024; removed May 2025* | same as #1 |
| 3 | Items per connection | 5,000,000 (default; up to 50M on request per MS Q&A, pre-2025 quota model) | *Last documented; quota model since retired — see note below* | same as #1; [MS Q&A 2152278](https://learn.microsoft.com/en-us/answers/questions/2152278/how-can-i-get-access-to-the-default-graph-index-qu) |
| 4 | Items per tenant / connection byte size | 50,000,000 / 500 GB | *Last documented, Nov 2024; removed May 2025* | same as #1 |
| 5 | Item size (parsed text, PUT body) | **4 MB** | Current | [connecting-external-content-api-limits](https://learn.microsoft.com/en-us/graph/connecting-external-content-api-limits) |
| 6 | Schema properties per connection | 128 | Current | same as #5 |
| 7 | External groups per tenant | 100,000 | Current | same as #5 |
| 8 | Group administration throttling threshold | 1,000 requests/sec | Current | same as #5 |
| 9 | External groups per user (query time) | 10,000 | Current | same as #5 |
| 10 | externalActivity throttling threshold | 20 activities per call | Current | same as #5 |
| 11 | Global Graph limit, any request, per app across all tenants | 130,000 requests / 10 sec | Current | [throttling-limits](https://learn.microsoft.com/en-us/graph/throttling-limits) |
| 12 | `$batch` size | 20 sub-requests max | Current | [json-batching](https://learn.microsoft.com/en-us/graph/json-batching) |
| 13 | `$batch` vs throttling | Sub-requests **count individually**; batching does not raise quota | Current | [json-batching](https://learn.microsoft.com/en-us/graph/json-batching), [throttling #throttling-and-batching](https://learn.microsoft.com/en-us/graph/throttling#throttling-and-batching) |
| 14 | 429 handling | `Retry-After` header returned; retry after that delay | Current | [throttling](https://learn.microsoft.com/en-us/graph/throttling) |

Verbatim, on batching (json-batching, current): *"Requests in a batch are
evaluated individually against the applicable throttling limits and if any
request exceeds the limits, it fails with a status of 429."* The batch
envelope itself returns 200; SDK auto-retry does **not** apply to throttled
sub-requests inside a batch (this connector retries them itself, honoring
`Retry-After`).

Ambiguities the docs do not resolve:

- The 25 items/sec figure never stated whether it is per connection total or
  per app+connection; we assume **per connection total** (conservative).
- The service-specific throttling page has **no connectors section at all**;
  there is no documented per-app+tenant write rate for `externalItem`.
- As of 2026 the index-quota licensing page redirects to
  [connector prerequisites](https://learn.microsoft.com/en-us/microsoft-365/copilot/connectors/prerequisites),
  which says synced-connector indexing *"incurs no extra cost"* — the
  per-license quota model appears retired; item-count caps are undocumented.
- Schema is provisioned asynchronously and only in `Draft` state; refinable
  attributes cannot be added by update; schema cannot be deleted
  ([manage-connections](https://learn.microsoft.com/en-us/graph/connecting-external-content-manage-connections),
  [manage-schema](https://learn.microsoft.com/en-us/graph/connecting-external-content-manage-schema)).
  No registration duration is documented.

## 2. Derived sustainable throughput (one connection)

Binding constraint: **25 items/sec** (limit #1). Each record is one
`PUT externalItem` sub-request; per limit #13 a full `$batch` of 20 PUTs
consumes 20 units of the item-rate quota, so **batching reduces HTTP
round-trips but does not multiply throughput**.

| Metric | Arithmetic | Value |
|---|---|---|
| Items/sec | documented rate | **25** |
| Items/hour | 25 × 3,600 | **90,000** |
| Items/day | 25 × 86,400 | **2,160,000** |
| Derated planning rate (assumption: 70% — 429 backoff, retries, ACL/identity calls sharing headroom) | 25 × 0.7 | ~17.5/s ≈ 63k/h ≈ 1.51M/day |

## 3. Time-to-index

Initial full crawl, one connection:

| Corpus | @ 25 items/s (documented) | @ 17.5 items/s (derated, assumption) |
|---|---|---|
| 100,000 | 1.1 h | 1.6 h |
| 500,000 | 5.6 h | 7.9 h |
| 1,000,000 | 11.1 h | 15.9 h |
| 4,000,000 | 44.4 h (~1.9 days) | 63.5 h (~2.6 days) |
| 5,000,000 (last-documented connection cap) | 55.6 h (~2.3 days) | 79.4 h (~3.3 days) |

Steady-state incremental (records changed/day vs. wall-clock needed at 25/s):

| Changed records/day | Incremental window required | Fits default 4 h cycle? |
|---|---|---|
| 10,000 | ~7 min | Yes |
| 50,000 | ~33 min | Yes |
| 100,000 | ~67 min | Yes |
| 360,000 | ~4 h | At the edge |
| >2.16M | > 24 h | No — connection is saturated; shard or reduce scope |

## 4. Pipeline ceiling vs. Graph ceiling

Measured with `tools/StressHarness` (fake Graph endpoint, 25 ms/batch):
**~12,600–12,900 items/s at ~150 MB RSS** on a developer box; **~126,000
items/s** pipeline-side on 16 vCPU / 16 GB (local measurements, not a
Microsoft figure).

Local ceiling ÷ Graph ceiling = 12,600 ÷ 25 ≈ **500× (~2.7 orders of
magnitude)**. This workload is **Graph-bound**; CPU/RAM on the connector node
is never the constraint. Hardware guidance: a **4 vCPU / 8–16 GB** node (VM or
modest Windows Server) is more than sufficient; larger nodes buy nothing.
Network egress at 25 items/s with 4 MB worst-case items is ≤ 100 MB/s
worst-case, realistically < 1 MB/s for typical Salesforce records.

## 5. HA (active-active) sizing note

Per `docs/SQL_CONTRACT.md`, all HA nodes ingest into **one shared connection**,
and Graph throttling is **per connection** — N nodes divide, not multiply, the
25 items/sec quota. HA buys **availability and crawl-window resilience**
(node death → claim reclaimed, crawl resumes from checkpoint), **not
throughput**. Operator rule: set per-node `GRAPH_BATCH_WORKERS` =
(single-node value ÷ node count), e.g. 4 → 2+2 on two nodes. The per-worker
adaptive concurrency (dials 1..8 on 429s) will self-correct misconfiguration,
but starting divided avoids a 429 storm at cycle start.

## 6. Levers when quota-bound

| Lever | Effect | Caveat |
|---|---|---|
| Multiple connections (shard Salesforce object types across connections) | Rate limit #1 is per connection, so k connections ≈ k × 25 items/s | Up to 30 connections/tenant (last documented); global app limit #11 still applies; per-connection schemas/ACL groups must be provisioned per shard; **scaling behavior undocumented — validate against real 429s** |
| Incremental-first strategy | Full crawl once, then only deltas (`--incremental-hours 4`); daily volume drops from corpus size to change rate | Requires reliable `SystemModstamp` coverage (already the design) |
| Trim schema/payload | Fewer properties (≤128 cap), smaller `content`, fewer searchable/retrievable flags → smaller PUTs, fewer 4 MB rejections | Does **not** raise the items/sec rate; helps latency and error rate only |
| Microsoft quota increase | Historically item-count quota could be raised (5M → up to 50M) on request via Microsoft form/support | Applies to *capacity*, not *rate*; no documented mechanism to raise the write rate — engage Microsoft support and re-measure |

**Bottom line:** plan one connection at **25 items/s ≈ 90k/h ≈ 2.16M/day**
(documented Nov 2024, since delisted); a 1M-record org full-crawls in ~11-16 h;
the .NET pipeline outruns Graph by ~500×, so run small nodes and spend effort
on incremental scope and (if needed) connection sharding, not hardware.
