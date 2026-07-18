# OBSERVABILITY — health, metrics, logs, alerts

## Health endpoint (`HEALTH_PORT`)

`HEALTH_PORT=8080` serves on a background thread:

| Route | Response |
| --- | --- |
| `GET /health` | `200 OK` — liveness (process is up) |
| `GET /ready` | `200 READY`, or `503 NOT READY` while a critical circuit breaker is open (docs/RESILIENCE.md) |
| `GET /metrics` | Prometheus text exposition (v0.0.4) |

The wildcard bind (`http://+:port/`) is preferred; without an admin URL ACL on
Windows it falls back to `http://localhost:port/` with a warning. The endpoint
never throws into the crawl.

## Metrics (`/metrics`)

All metric names are prefixed `seismic_connector_`:

| Metric | Type | Meaning |
| --- | --- | --- |
| `items_ingested_total` | counter | externalItems successfully PUT |
| `items_failed_total` | counter | items that failed after retries (dead-lettered) |
| `items_deleted_total` | counter | withdrawals (No-MNE, expiry, unpublish, not-in-source) |
| `items_skipped_total` | counter | unchanged versions / checkpointed chunks |
| `crawls_started_total` / `crawls_completed_total` | counter | crawl lifecycle |
| `throttled_429_total` | counter | Graph 429 responses observed |
| `items_reacled_total` | counter | items whose ACL was refreshed after a permission change (re-ACL) |
| `acl_drift_detected_total` | counter | items whose resolved ACL drifted from the indexed ACL |
| `dead_letter_depth` | gauge | current queue depth (refreshed on scrape; summed across shards when GRAPH_CONNECTION_SHARDS is set) |
| `last_crawl_completed_timestamp_seconds` | gauge | unix time of last completed crawl |
| `last_drift_findings` | gauge | finding count of the last `reconcile` drift sweep (0 = in sync / never run) |
| `extraction_attempts_total{format=...}` | counter | content-extraction attempts per payload format |
| `extraction_success_total{format=...}` | counter | extractions that produced non-empty text, per format — scrape-side success ratios reveal formats degrading to metadata-only indexing. The synthetic `format="livedoc-fields"` label tracks LiveDoc field-metadata fetches (LIVEDOC_FIELD_INDEXING) |
| `uptime_seconds` | gauge | process uptime |
| `tracing_enabled{exporter_endpoint,service_name}` | gauge | 1 when OTLP trace export is active, 0 otherwise; the OTLP endpoint and service.name ride as labels |
| `circuit_breaker_state{dependency}` | gauge | per-dependency breaker state: 0 closed, 1 open, 2 half-open (`seismic`, `graph`) |
| `circuit_breaker_trips_total{dependency}` | counter | times a dependency breaker opened |
| `circuit_breaker_resets_total{dependency}` | counter | times a dependency breaker recovered (half-open→closed) |
| `degraded_pauses_total` | counter | crawls that paused into degraded mode (a critical breaker was open) |
| `webhook_accepted_total` | counter | webhook requests accepted with a valid HMAC signature (202) |
| `webhook_rejected_total` | counter | webhook requests rejected 401 (invalid/missing HMAC signature) — see docs/RUNBOOKS.md#webhook-401-spike |
| `webhook_dropped_total` | counter | webhook events shed by the drop-oldest queue cap (healed by the next crawl) |
| `webhook_queue_depth` | gauge | current undrained webhook event queue depth (cap 10,000) |
| `ha_claims_acquired_total` | counter | HA crawl-resource claims acquired by this node (including steals) |
| `ha_claims_held` | gauge | HA claims currently held by this node (node-local view) |

## Logs

* Every command writes a run directory `logs/{prefix}_{timestamp}/` containing
  the full log (INFO+, line-rotating at 100k lines) and a run summary.
* Console shows WARNING+ by default, INFO+ with `--verbose`; progress
  milestones always print.
* `LOG_FORMAT=json` switches file/console records to one JSON object per line
  (`timestamp`, `level`, `logger`, `message`, optional `exception`).
* `LOG_RETENTION_DAYS=N` prunes run directories older than N days at the start
  of every command and each continuous cycle. Root state files
  (`sync_state.json`, `checkpoint_*.json`, `failed_records_*.jsonl`) are never
  touched.
* The **reconciliation report** (`reconciliation_*.jsonl`, see
  docs/EXCLUSIONS.md) lives in the same run directory.
* `EVENTLOG_ENABLED=true` (Windows) mirrors WARNING+ records and lifecycle
  start/stop marks to the Application Event Log, source `SeismicConnector`,
  stable event ids 1000/1100/2000/3000 — collection queries in docs/SIEM.md.

Ready-made Grafana dashboard and Prometheus / Azure Monitor alert rules
(matching the docs/RUNBOOKS.md anchors) ship in `ops/`.

## Alerts (`ALERT_WEBHOOK_URL`)

Fire-and-forget JSON POSTs — alerting can never break a crawl:

```json
{"kind":"crawl_failed","message":"...","connector":"SeismicSales","timestamp":"...","data":{...}}
```

Kinds: `crawl_failed`, `setup_failed`, `dead_letter` (fires when the queue
depth exceeds `ALERT_DEADLETTER_THRESHOLD` > 0).

## Distributed tracing & correlation IDs

The connector is an OpenTelemetry trace source (`ActivitySource` name
`SeismicConnector`). Two layers, deliberately separated:

**Correlation IDs (always on, near-zero cost).** A correlation id is minted per
crawl cycle — the W3C trace id when a span is active, else a fresh id — and
flows via `AsyncLocal` to every child task. It is stamped on:

* every `LOG_FORMAT=json` log line (`correlation_id`),
* every dead-letter record (`failed_records_*.jsonl` and the SQL
  `dbo.DeadLetter.CorrelationId` column),
* every reconciliation / drift / reacl report entry,
* the `crawl.cycle` span tag.

So one crawl is greppable end to end by its id (also printed in the "Starting …
crawl" progress line).

**Spans (OTLP export, opt-in).** Meaningful stages are spanned with correct
parent/child nesting: `crawl.cycle` → `seismic.list_teamsites`,
`crawl.libraries`, `crawl.teamsite` → `seismic.list_contents`,
`content.extract`, `item.transform`, `graph.batch_ingest`, `crawl.withdrawal`;
plus `reconcile.sweep`, `reacl.sweep`, and `webhook.process` → `webhook.event`.
The Seismic and Graph HTTP transports open `seismic.http` / `graph.http` client
spans and inject the W3C `traceparent` header on outbound requests so downstream
services join the trace.

Export is gated on `OTEL_EXPORTER_OTLP_ENDPOINT`. With it unset, no
`TracerProvider` (and no `ActivityListener`) is registered, so
`ActivitySource.StartActivity` returns `null` — the spans compile away to a
cheap null-check and overhead is unchanged. When set, the OTLP exporter uses a
batched, bounded background queue: a broken or unreachable collector can never
fail or stall a crawl (export is fire-and-forget; drops are silent). Standard
`OTEL_*` env vars are honoured; `OTEL_SERVICE_NAME` defaults to the connector
name. `/metrics` and `validate-config` both report the tracing state and export
target.

## Dashboard

`--continuous` in an interactive console renders a Spectre.Console live table:
phase, ingested/failed/skipped/excluded/withdrawn counters, 429s, dead-letter
depth, webhook queue and uptime. Suppressed automatically when stdout is
redirected (service mode).
