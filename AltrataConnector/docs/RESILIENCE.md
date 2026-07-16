# RESILIENCE — circuit breakers + degraded mode / fail-safe

Disaster-recovery layer for **sustained** dependency outages. This is distinct
from the retry/backoff layer (docs/RETRY.md), which rides out transient blips
inside a single call: a **circuit breaker** watches failures *across* calls and,
once a dependency is clearly down, **fails fast** so the connector stops
hammering it, pauses ingestion at a safe boundary, and auto-recovers.

## The breaker

`Infrastructure/CircuitBreaker.cs` — reusable, thread-safe, clock-injectable:

```
Closed ──(≥ threshold failures within window)──▶ Open
Open   ──(OpenDuration elapsed)──▶ HalfOpen
HalfOpen ──(HalfOpenTrials succeed)──▶ Closed
HalfOpen ──(a trial fails)──▶ Open
```

While **Open** every call fails fast with `CircuitOpenException` (the action is
never invoked). After `OpenDuration` the breaker goes **HalfOpen** and admits up
to `HalfOpenTrials` probe calls; enough successes close it, a single failure
reopens it.

**What trips it** is the caller's decision, so the two dependency clients only
count *real* outages:

| Outcome | Trips? |
|---|---|
| HTTP 5xx (500/502/503/504) after retries | **yes** |
| Connection refused / reset / DNS (`HttpRequestException`) | **yes** |
| Timeout (`TaskCanceledException`) | **yes** |
| HTTP 4xx / validation | no (dependency is responding) |
| HTTP 429 (honored Retry-After) | no (throttling, not an outage) |
| Graceful-stop cancellation (`ct` requested) | no |

## Breakered dependencies

| Breaker | Wraps | Critical? |
|---|---|---|
| `graph` | the Microsoft Graph client — ingest / withdraw / re-ACL / connection / schema (every HTTP call funnels through one choke point) | **yes** — drives readiness + degraded pause |
| `altrata-api` | the enrichment REST client — OAuth token **and** profile lookup | no |

The **feed path is local files** (an SFTP drop dir), so there is no breaker
there — a file read has no sustained-outage failure mode. Resilience during a
delivery is about the *downstream* dependency (Graph); if it's down we degrade.

## Degraded mode (fail-safe)

When the Graph breaker is **Open**, the crawl **pauses at a safe boundary**
instead of dead-lettering everything:

* Before each delivery, and before each superchunk, the engine checks the Graph
  breaker; if Open it **saves the checkpoint and stops** (`CrawlResult.Degraded
  = true`). A breaker that opens mid-superchunk surfaces as `CircuitOpenException`,
  which the crawl treats as the same graceful pause (the checkpoint was already
  saved at the superchunk boundary) — **never** as per-item dead-letters.
* No state is lost or corrupted: the next crawl resumes from the checkpoint.
  In continuous mode this **auto-recovers** — after `OpenDuration` the breaker
  half-opens, the next crawl's first Graph call probes it, and success closes it
  and resumes ingestion. This is "probe, don't hammer".

### Erasure durability is preserved across degraded transitions

`forget-subject` never partially-applies an erasure. Even with Graph down, the
subject is **suppressed and ledgered** (the durable, fail-safe guarantees from
docs/ERASURE.md), the failed withdrawals are dead-lettered as `op: delete`, and
`retry-failed` completes the Graph-side removal once it recovers. Suppression
holds across every degraded transition, so a re-delivery of an erased subject is
still skipped.

## Observability

* **/health** (liveness) stays `200 OK` in degraded mode — the process is fine.
* **/ready** returns **`503 NOT READY`** when a *critical* breaker (graph) is
  Open, naming the dependency; back to `200 READY` when it recovers.
* **/metrics**: `altrata_breaker_open` gauge (1 when a critical breaker is open),
  plus labeled per-dependency lines:
  `altrata_breaker_state{dependency="graph"}` (0 closed / 1 open / 2 half-open),
  `altrata_breaker_trips_total{…}`, `altrata_breaker_resets_total{…}`.
* **validate-config** prints the thresholds and which breaker is critical.

## Config (`CIRCUIT_BREAKER_*`)

| Env var | Default | Meaning |
|---|---|---|
| `CIRCUIT_BREAKER` | `true` | master switch; `false` = pure passthrough (no fail-fast) |
| `CIRCUIT_BREAKER_FAILURE_THRESHOLD` | `5` | failures within the window that open a breaker |
| `CIRCUIT_BREAKER_WINDOW_SECONDS` | `60` | rolling failure window |
| `CIRCUIT_BREAKER_OPEN_SECONDS` | `30` | how long a breaker stays open before probing |
| `CIRCUIT_BREAKER_HALFOPEN_TRIALS` | `2` | probe successes needed to close |

On the happy path the breaker is **inert**: successes never trip it, and
`CIRCUIT_BREAKER=false` removes it from the path entirely (zero locking), so
default behaviour and overhead are unchanged. The seat never-everyone invariant
is untouched — the breaker only gates transport, never ACL construction, and
degraded resume re-runs the same seat-only ingestion.
