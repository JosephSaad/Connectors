# RETRY — throttling & backoff contract

Applies to both API clients (Graph and Seismic).

## Rules

1. **Retryable**: HTTP 429 and every 5xx, plus transport-level
   `HttpRequestException`. Everything else (4xx) fails immediately.
2. **Server `Retry-After` is honoured EXACTLY.** A parseable numeric
   `Retry-After` header is used as-is — it is *never* jittered and never
   scaled. (Unparseable HTTP-date values fall back to computed backoff.)
3. **Computed backoff** (no `Retry-After`): `base * 2^attempt` seconds
   (`GRAPH_RETRY_BACKOFF_BASE`, default 2 → 2, 4, 8, 16 ...).
4. **Cap**: every wait — server-provided or computed — is capped at **60 s**.
5. **Attempts**: `GRAPH_MAX_RETRIES` / `SEISMIC_MAX_RETRIES` (default 4)
   retries after the initial attempt; then the error is raised and the item(s)
   dead-lettered.

## Jitter (`GRAPH_RETRY_JITTER=true`, default off)

When enabled, COMPUTED delays get uniform ±20% jitter:
`delay * (0.8 + 0.4 * U[0,1))`. Server `Retry-After` values still pass through
untouched.

Why: in HA deployments all nodes share one per-connection Graph 429 quota.
Without jitter, nodes throttled together retry in lockstep and collide again.
Enable it on **every** node when `HA_MODE=true`.

## $batch semantics — the per-item 429/503 retry ladder

Ingestion PUTs go through `POST /$batch` (≤ 20 requests). The envelope itself
follows the transport rules above; **per-item** statuses inside a 2xx
envelope are handled by a shrinking retry ladder:

* **429** sub-responses are re-sent in the next round (only the throttled
  items); the inter-round wait is `max(computed backoff, sub-response
  Retry-After)`, capped at 60s (a clamp warning is logged). Each 429 also
  feeds the throttle signal below.
* **503** sub-responses are retried the same way but never signal throttle —
  a transient outage is not a rate limit and must not shrink concurrency.
* Anything else non-2xx is a **permanent** per-item failure: dead-lettered
  immediately (with request/response bodies), never retried in-round.
* After `GRAPH_MAX_RETRIES` rounds the remaining throttled items are marked
  failed ("throttled/unavailable after all retries") and dead-lettered.

## Adaptive concurrency

`GRAPH_BATCH_WORKERS` (alias `GRAPH_CONCURRENT_BATCHES`, which WINS when both
are set) is the **maximum** number of concurrent $batch POSTs. The live
worker count adapts: every throttled batch steps it down by one (floor 1),
and three consecutive clean batches step it back up (cap max). Sub-batches
are dispatched in windows of the current width so a 429 immediately narrows
the next window. `GRAPH_BATCH_SIZE` (alias of `INGEST_GRAPH_BATCH_SIZE`,
which wins) sets requests per envelope, hard-capped at 20 by the API.

## Dead-letter queue

Failures that survive retries land in
`logs/failed_records_{CONNECTOR_ID}.jsonl` (or `dbo.DeadLetter` with the SQL
backend), including the request/response bodies for debugging. Re-drive them
with:

```
seismic-connector retry-failed [--file <path>] [--clear-on-success]
```

`ALERT_DEADLETTER_THRESHOLD` + `ALERT_WEBHOOK_URL` raise a webhook alert when
the queue depth crosses the threshold.
