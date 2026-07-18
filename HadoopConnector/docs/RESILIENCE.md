# Circuit breakers & degraded mode

Retry/backoff (`docs/RETRY.md`) smooths **transient** blips — one call hiccups,
we retry it. Circuit breakers handle **sustained** outages: when a dependency
has been failing for a while, stop hammering it, fail fast, and pause the crawl
safely until it recovers. The two layers compose — retry inside an operation,
the breaker across operations.

> **On by default, inert on the happy path.** `CIRCUIT_BREAKER` defaults to
> true, but a healthy connector never trips a breaker, so behaviour and overhead
> are unchanged. `CIRCUIT_BREAKER=false` is a pure-passthrough escape hatch
> (breakers never engage).

## The breaker

A three-state breaker (`Infrastructure/CircuitBreaker.cs`) guards each external
dependency:

- **Closed** — calls allowed. REAL failures within the rolling `WINDOW` are
  counted; at `FAILURE_THRESHOLD` the breaker trips to Open. A success clears
  the window (intermittent errors that recover never accumulate to a trip).
- **Open** — calls fail fast (no network, no timeout wait) for `OPEN_SECONDS`,
  then the breaker moves to Half-Open.
- **Half-Open** — up to `HALF_OPEN_TRIALS` probe calls are admitted.
  `HALF_OPEN_TRIALS` successes close the breaker (recovered); any failure
  re-opens it.

Only **real failures** count: `5xx`, timeouts, and connection failures. `4xx`
/validation and honoured `429`-with-`Retry-After` are **ignored** — a 429 is
flow control, not an outage, so it never trips the breaker. An ignored outcome
still releases its half-open probe slot (it just doesn't count toward closing
or re-opening), so a run of 429/4xx probes can never wedge the breaker open.
The breaker is clock-injectable (deterministic tests) and thread-safe (crawls
run concurrent object/batch workers).

## Which dependencies are breakered

Two critical breakers (`Infrastructure/Breakers.cs`):

| Breaker | Guards |
|---|---|
| `hdfs` | the BDH source client (WebHDFS LISTSTATUS/OPEN, localpath reads) |
| `graph` | the Microsoft Graph client (token + ingest/delete) |

Both are critical — a sustained outage of either degrades the crawl.

## Degraded mode (fail-safe)

When a critical breaker is Open, the pipeline enters **degraded mode**, reusing
the graceful-stop machinery so no state is lost:

1. It pauses at a **safe boundary** — no new object/chunk is started; an
   in-flight batch that hits the open breaker fails fast (items are **not**
   dead-lettered, since they are not real failures).
2. The **checkpoint is retained** and the **sync cursor is not advanced**, so
   the next cycle resumes exactly where it paused. A chunk interrupted
   mid-flight is not checkpointed and is retried whole on resume (PUT is
   idempotent, so re-sending the few already-succeeded items is safe).
3. In `--continuous` mode the next cycle is scheduled after a bounded backoff
   (the breaker's `OPEN_SECONDS`, clamped to 10s–5m) rather than the full
   interval, so recovery is timely — and the breaker's Half-Open probe on that
   cycle's first call auto-recovers when the dependency is back.

A `degraded_mode` webhook alert fires on entry.

## Observability

- `/metrics`: per-dependency `circuit_breaker_state{dependency=...}` (0 closed /
  1 half-open / 2 open), `circuit_breaker_trips_total`, `circuit_breaker_resets_total`.
- `/ready`: returns **503 not-ready** while a critical breaker is open (so
  traffic/scale-in decisions see the connector as temporarily out); `/health`
  (liveness) stays **200** so an orchestrator does not kill a connector that is
  merely waiting out an outage.
- `validate-config` prints the breaker thresholds (or "disabled").

## Configuration

| Var | Default | Meaning |
|---|---|---|
| `CIRCUIT_BREAKER` | `true` | Master switch. `false` = pure passthrough. |
| `CIRCUIT_BREAKER_FAILURE_THRESHOLD` | `5` | Real failures within the window to trip. |
| `CIRCUIT_BREAKER_WINDOW_SECONDS` | `60` | Rolling sampling window. |
| `CIRCUIT_BREAKER_OPEN_SECONDS` | `30` | How long the breaker stays open before probing. |
| `CIRCUIT_BREAKER_HALF_OPEN_TRIALS` | `2` | Probe successes needed to recover (and concurrent probes admitted). |
