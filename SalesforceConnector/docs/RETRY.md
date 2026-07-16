# Graph 429 / Transient-Error Retry Behaviour

Audit of the two retry paths (2026-07-01). Companion to
[CAPACITY.md](CAPACITY.md) and [HA.md](HA.md).

## Layers

| Layer | Trigger | Backoff | Cap | Config knob |
|---|---|---|---|---|
| `Graph/Client.cs` `RequestAsync` (whole HTTP request) | 429, 500, 502, 503, 504 | Numeric `Retry-After` honoured exactly when present; otherwise `base · 2^attempt` (attempt = 0, 1, …) | 60 s (applies to both computed backoff and `Retry-After`) | `GRAPH_MAX_RETRIES` (attempts), `GRAPH_RETRY_BACKOFF_BASE` (base, s) |
| `Graph/Ingest.cs` `$batch` sub-response ladder (per item) | Per-item `429` (signals throttle) and `503` (retried without a throttle signal) | `max(base · 2^(attempt−1), Retry-After of first 429)` — the server value is a floor, never reduced | 60 s | Same two knobs (via `config.Tuning`) |
| `Graph/Ingest.cs` `AdaptiveConcurrency` | Any 429 in a sub-batch → −1 worker (min 1); 3 clean sub-batches → +1 (max = configured value) | n/a (concurrency, not delay) | 1 … configured max | `GRAPH_CONCURRENT_BATCHES` (alias: `GRAPH_BATCH_WORKERS`), default 8 |

Both ladders take their retry count and backoff base from
`GRAPH_MAX_RETRIES` / `GRAPH_RETRY_BACKOFF_BASE` through `AppConfig.Tuning`
(every command constructs `GraphClient` with them; `identity-dry-run` uses the
client defaults `4`/`2`, matching the Python original).

## Audit findings

- **Retry-After**: honoured on both paths. `Client.cs` uses it verbatim
  (numeric seconds; an HTTP-date value falls back to computed backoff, as in
  the Python original). The batch ladder takes the first 429's header as a
  floor under the computed backoff.
- **Cap**: the batch ladder always capped waits at 60 s. `Client.cs` had **no
  cap** (inherited from the Python original) — a hostile or misconfigured
  `Retry-After: 3600` would have stalled the whole run for an hour. Fixed: the
  same 60 s clamp (with the same warning text) now applies in `RequestAsync`.
- **Knob naming**: the docs (`env/README.md`, `HA.md`, `CAPACITY.md`) tell
  operators to size `GRAPH_BATCH_WORKERS`, but the code (like the Python
  original) only read `GRAPH_CONCURRENT_BATCHES` — the documented knob was
  silently ignored. Fixed: `GRAPH_BATCH_WORKERS` is now accepted as an alias
  (`GRAPH_CONCURRENT_BATCHES` wins when both are set; default stays 8).
- **AdaptiveConcurrency**: reacts to 429 only (503 deliberately does not
  penalise concurrency), dials 1…max, ramps up after 3 clean sub-batches.

## Optional jitter (`GRAPH_RETRY_JITTER`)

`GRAPH_RETRY_JITTER=true` (default `false` — default delays stay
bit-identical) applies a uniform **±20 %** jitter to *computed* backoff delays
in both ladders. Server-provided `Retry-After` values are never jittered —
they are honoured exactly (the batch ladder still uses them as a floor). The
60 s cap applies after jitter. Implementation: `Graph/RetryDelay.cs`.

**HA note:** Graph's 429 quota is **per external connection**, shared by all
nodes. Two consequences:

1. Size `GRAPH_BATCH_WORKERS` per node as *(single-node value ÷ node count)* —
   see [HA.md](HA.md) and [CAPACITY.md](CAPACITY.md).
2. Nodes throttled by the same quota event compute identical backoff ladders
   and — without jitter — retry in lockstep, colliding again on every attempt.
   **Enable `GRAPH_RETRY_JITTER=true` on all nodes in HA mode** so retries
   de-synchronise.
