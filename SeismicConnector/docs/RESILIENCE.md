# RESILIENCE — circuit breakers & degraded mode

The connector protects itself against **sustained** dependency outages with a
per-dependency circuit breaker and a fail-safe degraded mode. This is distinct
from the retry/backoff ladder (`docs/RETRY.md`): retries absorb transient blips
*within* a single call; the breaker fails fast *across* calls once a dependency
is clearly down, so the connector stops hammering it and pauses cleanly instead.

## Breakers

Each external dependency has its own breaker, registered by name:

| Dependency | Breaker name | Covers |
| --- | --- | --- |
| Seismic API | `seismic` | OAuth token, list teamsites/content, download, analytics, LiveDoc fields |
| Microsoft Graph | `graph` | connection/schema, `$batch` ingest, ACL PATCH, delete |

Both are **critical** — an open critical breaker flips `/ready` to not-ready and
triggers degraded mode.

### States

* **Closed (0)** — calls flow; tripping failures are counted in a sliding window.
* **Open (1)** — calls fail fast with `CircuitOpenException` *without touching
  the dependency*, for `CIRCUIT_BREAKER_OPEN_SECONDS`.
* **Half-open (2)** — after the open period, up to
  `CIRCUIT_BREAKER_HALF_OPEN_TRIALS` probe calls are allowed; one reachable
  result closes the breaker, one unreachable result re-opens it.

### What trips it

A breaker opens on **sustained real outages** — HTTP 5xx, request timeouts, and
connection errors — once `CIRCUIT_BREAKER_FAILURE_THRESHOLD` of them occur
within `CIRCUIT_BREAKER_WINDOW_SECONDS`. It does **not** trip on:

* **4xx / validation errors** — the service is up; the request was bad.
* **honored 429 (Retry-After)** — that is flow control, not an outage; the
  retry ladder already handles it.

The breaker wraps the *retry-included* send, so one "failure" is one fully
retried operation that still failed — a genuine sustained-failure signal, not a
single blip. A caller-requested cancellation (graceful stop) is neutral and
never penalises a dependency.

## Degraded mode (fail-safe pause + auto-recover)

When a critical breaker is open, the crawl **pauses at a safe checkpoint
boundary** rather than grinding against a dead dependency:

1. The in-flight chunk is finished and flushed, and its checkpoint saved (reusing
   the graceful-stop machinery) — no in-flight work is lost.
2. No new teamsite/chunk is started; the crawl returns without writing the
   sync timestamp or clearing the checkpoint, so the **next cycle resumes
   exactly where it paused**, with no state loss or duplication.
3. In HA the crawl session is left open and claims are left held, so another
   node (or the next start) reclaims and resumes.
4. A later scheduled cycle finds the breaker half-open and **probes** for
   recovery; a reachable probe closes the breaker and the crawl proceeds
   normally. The connector never hammers a dead dependency — it probes.

Any items that were mid-flight against an open Graph breaker are dead-lettered
(re-drivable with `retry-failed`), so the dead-letter queue and checkpoint stay
consistent across the transition.

A degraded pause logs a clear `DEGRADED MODE` warning, increments
`degraded_pauses_total`, and raises a best-effort `degraded_mode` webhook alert.

## Observability

* `/metrics`:
  * `circuit_breaker_state{dependency="seismic|graph"}` — 0 closed / 1 open / 2 half-open.
  * `circuit_breaker_trips_total{dependency}` / `circuit_breaker_resets_total{dependency}`.
  * `degraded_pauses_total`.
* `/health` — **liveness stays 200** in degraded mode (a paused connector is
  alive and must not be restarted).
* `/ready` — **503 not-ready** while any critical breaker is open (so a load
  balancer / orchestrator routes away), 200 otherwise.
* `validate-config` prints the breaker thresholds (or warns when disabled).

## Configuration

All `CIRCUIT_BREAKER_*` knobs are documented in `env/.env.local.example`.
Breakers are enabled by default and inert on the happy path (a closed breaker
just records success). `CIRCUIT_BREAKER=false` is a pure-passthrough escape
hatch that disables fail-fast and degraded-mode protection entirely.
