# RETRY — Graph backoff policy

## Single-request path (`GraphClient.SendWithRetryAsync`)

| Situation | Behaviour |
|---|---|
| HTTP 429, 500, 502, 503, 504 | retry up to `GRAPH_MAX_RETRIES` (default 4) |
| `Retry-After` header present | wait **exactly** that long — never jittered — **clamped to the 60 s hard cap** (a clamp is logged) |
| No `Retry-After` | computed backoff `GRAPH_RETRY_BACKOFF_BASE · 2^attempt`, capped at 60 s |
| Transport error (DNS, reset) | same computed backoff |
| Other 4xx | fail immediately (no retry) — dead-letter for item PUTs |

## $batch path (bulk ingest + re-ACL — `PutItemsBatchAsync` / `UpdateItemAclsBatchAsync`)

Superchunks of `GRAPH_BATCH_SIZE × GRAPH_BATCH_WORKERS` records are split into
≤20-request `$batch` calls and dispatched in **adaptive waves**:

* **AdaptiveConcurrency** starts at `GRAPH_BATCH_WORKERS`, dials DOWN one step
  per throttled batch (floor 1), and dials back UP after 3 consecutive
  clean batches (cap `GRAPH_BATCH_WORKERS`).
* Within a batch, the per-item retry ladder re-sends **only** 429s (throttle
  signal) and 503s (transient outage — no throttle signal, adaptive
  concurrency is not penalised). Other statuses are per-item permanent
  failures with the Graph error extracted.
* The wait before a retry round is `max(computed backoff, Retry-After from
  the first 429 response)`, hard-capped at 60 s. Jitter applies only to the
  computed component.
* Retry payloads are renumbered `0..n-1`; items missing from a `$batch`
  response and empty responses are failures, never silent drops; items still
  throttled after `GRAPH_MAX_RETRIES` rounds become permanent
  `HTTP 429: throttled after all retries` failures.
* The **seat entitlement invariant is asserted on the batched path too**:
  any `everyone` grant throws before a single request is sent.

## Jitter (`GRAPH_RETRY_JITTER=true`, default false)

COMPUTED delays get a uniform ±20% jitter: `delay · (0.8 + 0.4·u)`, `u∈[0,1)`.
Server-provided `Retry-After` values never pass through the jitter helper.

Why: in HA deployments all nodes share one per-connection Graph 429 quota.
Without jitter, nodes throttled together retry in lockstep and collide again.
Enable jitter on every node when `HA_MODE=true` (see docs/HA.md); sharding
(docs/SHARDING.md) is the lever that raises the quota itself.

## Sovereign clouds

`GRAPH_BASE_URL` (e.g. `https://graph.microsoft.us`,
`https://microsoftgraph.chinacloudapi.cn`) moves every Graph call to the
sovereign endpoint; the token audience follows automatically
(`{GRAPH_BASE_URL}/.default`) unless `GRAPH_SCOPE` overrides it explicitly.
`GRAPH_API_VERSION` selects `v1.0` (default) or `beta`. All three are read
live so each cycle/shard sees current env state.

## Interaction with dead-letter

A record that fails transform or exhausts Graph retries is appended to the
dead-letter queue (batch-append, corruption-safe under concurrent writers)
with the fully transformed item payload; `retry-failed` replays it through
the same retry pipeline. `altrata_graph_retries_total` counts every retry;
`altrata_graph_throttle_429_total` counts throttle events;
`altrata_items_failed_total` counts records that exhausted retries.

The Altrata REST API client is separate: it is throttled client-side
(`ALTRATA_API_CALLS_PER_MINUTE`, sliding window) *before* the call, because
every successful call is billable — retrying blindly costs money.
