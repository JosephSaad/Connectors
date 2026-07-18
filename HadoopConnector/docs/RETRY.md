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
- Every observed 429 increments `hadoop_connector_throttled_429_total`.

### Adaptive concurrency

`$batch` sub-batches are dispatched in parallel windows sized by an adaptive
dial (`AdaptiveConcurrency`): it starts at `GRAPH_CONCURRENT_BATCHES` /
`GRAPH_BATCH_WORKERS` (default 8; the former wins when both are set), any 429
in a window steps it down toward 1, and three consecutive clean windows step
it back up. This rides the real per-connection quota instead of a guess —
under sharding each connection gets the full dial range.

Implementation: `Graph/RetryDelay.cs` (pure, unit-tested), `Graph/GraphClient.cs`,
`Graph/Ingest.cs` (`AdaptiveConcurrency`).

## WebHDFS (BDH source)

`WebHdfsClient` applies the same retry policy as the Graph client to the JSON
operations (`LISTSTATUS`, `GETFILESTATUS`):

- **Retryable:** `429` and all `5xx`, up to 4 retries (fixed). A server
  `Retry-After` (delta-seconds or HTTP-date) is honoured; **every wait is
  clamped to 60 s**. Without a usable header the backoff is `2 × 2^attempt`
  seconds, same clamp.
- **Network-level failures** (`HttpRequestException`) retry on the computed
  backoff schedule; exhausting the ladder on a transport failure counts as a
  real failure for the `hdfs` circuit breaker (`docs/RESILIENCE.md`).
- **`OPEN` (file streaming) is a single attempt** — there is no retry ladder
  around an in-flight stream. A failed open/read surfaces as an
  `HdfsException`; the enclosing object worker dead-letters the crash
  (`WORKER_CRASH`) and the crawl continues with the next object type, so a
  flaky datanode never kills a run. The namenode's 307 redirect to a datanode
  is followed transparently.
- **Error surfacing:** a WebHDFS `RemoteException` body is unwrapped into the
  exception message (never a silent empty listing); a malformed `LISTSTATUS`
  envelope is an error, not an empty result.
- **Breaker classification:** terminal `5xx`/transport → failure; `4xx`,
  honoured throttles and cancellation → ignored; `2xx` → success. When the
  `hdfs` breaker is open, calls fail fast with `CircuitOpenException` and the
  crawl degrades (checkpoint retained).

There is no client-side call budget or pacing: BDH is the cheap path — reads
hit the Hadoop cluster (or a Knox gateway), not the metered Salesforce API.
Read volume is bounded structurally instead, by partition pruning, the
`BDH_MAX_FILE_BYTES` per-file bound and the `BDH_MAX_RECORDS_PER_OBJECT` row
cap (`docs/FILTERS.md`).

## SQL Server (state backend)

`SqlExecutor` retries the well-known transient error numbers (deadlock 1205,
failover 4060/40613, throttling 10928/10929/40501, timeouts) with exponential
backoff `2^attempt` capped at 30 s, up to `SQL_MAX_RETRIES` (default 5) —
enough to ride out an Always On AG failover.
