# Retry & throttling behaviour

## Microsoft Graph

Every Graph request goes through `GraphClient.SendWithRetryAsync` (429-hardened
to match the Salesforce connector):

- **Retryable statuses:** exactly `{429, 500, 502, 503, 504}`. Everything else
  returns immediately (4xx client errors are dead-lettered per item, never
  retried in-place; 501 is not transient).
- **Attempts:** `GRAPH_MAX_RETRIES` (default 4) retries after the initial call.
  The bearer token is re-resolved per attempt so a long ladder never rides an
  expired token.
- **Server `Retry-After`:** only a NUMERIC delta-seconds value is trusted; an
  HTTP-date or garbage value falls back to computed backoff (a poisoned header
  must not park the crawl).
- **Hard cap — 60 s on every wait.** Server value or computed, any wait above
  60 s is clamped, with a warning (`Retry-After of Ns exceeds cap; clamping`).
- **Computed backoff** (no usable `Retry-After`):
  `GRAPH_RETRY_BACKOFF_BASE * 2^attempt`, capped at 60 s
  (base 2 → 2 s, 4 s, 8 s, 16 s).
- **Jitter** (`GRAPH_RETRY_JITTER=true`): computed delays get a uniform ±20%
  jitter (`delay × [0.8, 1.2)`). Off by default. **Turn it on for every node
  in HA** — all nodes share the per-connection 429 quota, and without jitter
  nodes throttled together retry in lockstep and collide again. A server
  Retry-After is never jittered.
- Network-level failures (`HttpRequestException`) retry on the computed
  backoff schedule.
- Every observed 429 increments `clarizen_connector_throttled_429_total`.

### Adaptive concurrency

`$batch` sub-batches are dispatched in parallel windows sized by an adaptive
dial (`AdaptiveConcurrency`): it starts at `GRAPH_CONCURRENT_BATCHES` /
`GRAPH_BATCH_WORKERS` (default 8; the former wins when both are set), any 429
in a window steps it down toward 1, and three consecutive clean windows step
it back up. This rides the real per-connection quota instead of a guess —
under sharding each connection gets the full dial range.

Implementation: `Graph/RetryDelay.cs` (pure, unit-tested), `Graph/GraphClient.cs`,
`Graph/Ingest.cs` (`AdaptiveConcurrency`).

## Clarizen REST API

- Same retry shape: 429/5xx retried up to 4 times, Retry-After honoured with
  the same 60 s clamp, exponential backoff otherwise.
- **Session expiry** (`401` or `errorCode: SessionTimeout`) triggers exactly one
  transparent re-login and replay per request.
- **Daily API budget** (`CLARIZEN_API_CALLS_PER_DAY`, default 25 000): a
  client-side governor counts every HTTP call. When exhausted the crawl raises
  `ClarizenQuotaExceededException`, the pipeline saves its checkpoint and
  returns; the next scheduled cycle resumes after the UTC-midnight reset.
- **Pacing** (`CLARIZEN_MAX_CALLS_PER_MINUTE`, default 60): a minimum interval
  of `60/rate` seconds between calls smooths bursts so interactive Clarizen
  users are not starved.

## SQL Server (state backend)

`SqlExecutor` retries the well-known transient error numbers (deadlock 1205,
failover 4060/40613, throttling 10928/10929/40501, timeouts) with exponential
backoff `2^attempt` capped at 30 s, up to `SQL_MAX_RETRIES` (default 5) —
enough to ride out an Always On AG failover.
