# Tenant governance & capacity plan

Five connectors share one Microsoft 365 tenant's Graph-connector quotas. Each
connector deploys and runs independently, so nothing in code coordinates them
— this document is that coordination. Re-run this plan whenever a connector is
added, an object scope widens, or Microsoft raises the published limits.

## Hard limits that bind the fleet (Microsoft-published)

| Limit | Value | Scope |
|---|---|---|
| Connections | 30 | per tenant |
| Items | 5,000,000 | per connection |
| Indexed items | 50,000,000 | per tenant (all connections) |
| Schema properties | 128 | per connection |
| Item payload | 4 MB | per item |
| `$batch` | 20 requests | per call |
| Ingest throughput | throttled per app + per tenant | dynamic |

## Connection & item allocation

Shard counts are set per connector via `GRAPH_CONNECTION_SHARDS`. Budget:

| Connector | Connections (shards) | Item budget | Rationale |
|---|---|---|---|
| SalesforceConnector | 4 | 12M | Largest live object scope; 4 shards keeps each under the 5M/connection cap with headroom |
| HadoopConnector (BDH) | 4 | 16M | 150M+ source rows **must** be filter-reduced (see its `config/filters.json`); the fail-closed scale guard is the enforcement point — treat any `ALLOW_FULL_SCAN` use as a capacity-plan change |
| ClarizenConnector | 2 | 6M | Projects/tasks/financial objects |
| SeismicConnector | 1 | 3M | Content library scale |
| AltrataConnector | 1 | 2M | Licensed-seat-scoped profiles |
| **Total** | **12 / 30** | **39M / 50M** | 18 connections and ~11M items of tenant headroom |

Rules:
- A connector never borrows another's shards. Adding shards = editing this table first.
- The 50M tenant quota is the true ceiling — approaching 80% (40M) triggers a
  scope review (tighten Hadoop filters first; it has the most elastic scope).
- Hadoop and Salesforce index overlapping records (BDH mirrors Salesforce,
  24h lag). They must use **separate connections** and distinct item-id
  namespaces (they do — same SF record Id, different connection). Search
  dedup/preference is a Copilot-side ranking concern; do not point both at one
  connection.

## Throttling isolation

- **One Entra app registration per connector** (five total). Graph throttles
  per app-per-tenant, so separate registrations isolate each connector's 429
  budget — one connector's burst cannot starve the others. This also gives
  least-privilege consent and per-connector credential rotation (see each
  connector's `SECURITY.md`).
- Every connector already honors `Retry-After` exactly (60s clamp) with
  adaptive concurrency; under tenant-wide throttling events all five back off
  independently and recover without coordination.

## Crawl scheduling (stagger plan)

Full crawls are the expensive events. Recommended stagger (server local time):

| Window | Connector | Mode |
|---|---|---|
| 00:30 | HadoopConnector | Full/incremental after the nightly BDH sync lands (24h-lag watermark makes earlier runs pointless) |
| 02:30 | SalesforceConnector | Incremental (full: monthly, first Saturday) |
| 04:00 | ClarizenConnector | Incremental |
| 05:00 | SeismicConnector | Incremental (webhooks carry the day) |
| 05:30 | AltrataConnector | Feed-driven (runs when a delivery lands; nightly window is the norm) |

Two full crawls must not overlap by schedule. If a full crawl overruns into
another's window the adaptive throttling makes it safe, just slower — but
recurring overlap means the stagger needs re-planning.

## Monitoring the shared budget

Each connector exposes its own `/metrics` (see each `ops/` folder for
dashboards and alert rules). Fleet-level watch items:

- Sum of per-connector item counts vs the 50M tenant quota (alert at 40M).
- Tenant-wide 429 spikes hitting all five simultaneously = tenant-level
  throttling event (not a single connector's fault) — check M365 service
  health before touching connector config.
- Any use of `ALLOW_FULL_SCAN` (Hadoop) or a deletion-guard override — these
  are capacity events, not routine ops.

## Change control

Treat this file as config: changes to shard counts, item budgets, crawl
windows, or app registrations go through the same review as code. Each
connector's `docs/DEPLOYMENT_ENTERPRISE.md` covers its own rollout; this file
governs what they share.
