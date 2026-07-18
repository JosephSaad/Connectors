# Observability

## Health endpoint (`HEALTH_PORT`)

Set `HEALTH_PORT=<port>` to serve, on a background thread:

| Route | Response |
|---|---|
| `GET /health` | `200 OK` — liveness (process is up; stays 200 even when degraded) |
| `GET /ready` | `200 READY` — readiness; `503 DEGRADED` while a critical circuit breaker is open (see `docs/RESILIENCE.md`) |
| `GET /metrics` | Prometheus text exposition (v0.0.4) |

The listener prefers the wildcard bind (`http://+:{port}/`) and falls back to
`localhost` when the wildcard is denied (no admin URL ACL on Windows — reserve
one with `netsh http add urlacl` to bind all interfaces). Unset or `<=0`
disables the endpoint entirely.

## Metrics

All metrics are prefixed `hadoop_connector_`:

| Metric | Type | Meaning |
|---|---|---|
| `items_ingested_total` | counter | externalItems successfully PUT |
| `items_failed_total` | counter | items that failed (dead-lettered) |
| `items_deleted_total` | counter | items withdrawn by the deletion sweep / `reconcile --fix` |
| `items_skipped_total` | counter | checkpoint-skipped / no-ACL skipped items |
| `crawls_started_total` / `crawls_completed_total` | counter | crawl cycles |
| `throttled_429_total` | counter | Graph 429 responses observed |
| `hdfs_calls_total` | counter | WebHDFS REST calls issued (per HTTP attempt, incl. retries) |
| `partitions_scanned_total` | counter | BDH partition directories whose files were read |
| `partitions_pruned_total` | counter | partition directories pruned with zero file I/O (dt watermark / partition filters) |
| `records_scanned_total` | counter | BDH rows read from source files |
| `records_filtered_total{stage}` | counter | rows removed by the filter layer, by stage (`predicate`) |
| `records_matched_total` | counter | rows that passed the filter layer and entered the pipeline |
| `items_classified_total{label}` | counter | items classified, by sensitivity label (`CLASSIFICATION=true`) |
| `sensitive_detections_total{category}` | counter | sensitive-data detections, by category |
| `tracing_enabled` | gauge | 1 when the OpenTelemetry OTLP exporter is registered (`OTEL_EXPORTER_OTLP_ENDPOINT` set) |
| `circuit_breaker_state{dependency}` | gauge | per-dependency (`hdfs`, `graph`) breaker state: 0 closed, 1 half-open, 2 open |
| `circuit_breaker_trips_total{dependency}` | counter | times a dependency's breaker tripped to open |
| `circuit_breaker_resets_total{dependency}` | counter | times a dependency's breaker recovered to closed |
| `guard_refusals_total` | counter | fail-closed scale-guard refusals (unfiltered object refused — `docs/RUNBOOKS.md`) |
| `partial_objects_total` | counter | object crawls marked PARTIAL (row cap hit or oversize file skipped) |
| `sweeps_suppressed_total` | counter | deletion sweeps suppressed (incomplete fetch or a mass-deletion guard) |
| `dead_letter_depth` | gauge | live dead-letter queue depth |
| `last_crawl_completed_timestamp_seconds` | gauge | unix time of last completed crawl |
| `ha_claims_held` | gauge | HA object-claim leases currently held by this node (0 outside HA) |
| `uptime_seconds` | gauge | process uptime |

The labelled families (`records_filtered_total`, `items_classified_total`,
`sensitive_detections_total`) only appear once something has been counted, so
the exposition stays clean when the features are idle/off.

Useful alerts: `time() - last_crawl_completed_timestamp_seconds > 2 *
incremental_interval`, `dead_letter_depth > 0` for an hour,
`circuit_breaker_state == 2` for more than a few minutes, and
`rate(records_matched_total) == 0` across a full-crawl window (filters or the
source went dark — see `docs/FILTERS.md` troubleshooting). Ready-made rules
(incl. guard-refusal, sweep-suppressed and 26-hour watermark-staleness alerts)
ship in `ops/prometheus-alerts.yml` and `ops/azure-monitor-alerts.kql`, keyed
to `docs/RUNBOOKS.md`; a Grafana dashboard ships in
`ops/grafana-dashboard.json`.

## Structured logs (`LOG_FORMAT=json`)

One JSON object per line: `{"timestamp", "level", "logger", "message",
"correlation_id"?}` — both in `logs/{prefix}_{timestamp}/connector.log` and on
the console. Default is human-readable text (correlation id shown as an
`[8-char]` prefix). Console shows WARNING+ unless `--verbose`; the log file
always captures everything. The `correlation_id` (present within a crawl
cycle) ties a run together across logs, dead-letter records, and trace spans —
see `docs/TRACING.md`.

`LOG_LEVEL` (DEBUG|INFO|WARNING|ERROR, default DEBUG) raises the floor for
every sink, and the ingest hot path gates per-item debug string construction
on `IsEnabledFor(Debug)` (Python `isEnabledFor` semantics) — with the level
raised, the expensive per-item log formatting is skipped entirely, not just
filtered.

`LOG_RETENTION_DAYS=N` prunes run directories older than N days at the start of
every command and each continuous cycle. Root state files (`sync_state.json`,
`checkpoint_*.json`, `failed_records_*.jsonl`) are never touched. With the SQL
backend, run `EXEC dbo.usp_PruneHistory @RetentionDays = N` on a schedule for
the server-side equivalent.

## Webhook alerts

`ALERT_WEBHOOK_URL` enables fire-and-forget JSON POSTs (Teams/Slack-compatible
via a small relay, or any HTTP sink):

```json
{"kind": "row_cap_hit", "message": "...", "connector": "BdhHadoopMart",
 "timestamp": "2026-07-17T09:30:00Z", "data": {...}}
```

Alert kinds: `setup_failed`, `crawl_failed`, `crawl_failures` (per-item
failures), `row_cap_hit` (an object hit `BDH_MAX_RECORDS_PER_OBJECT`; crawl
partial, sweep skipped), `deletion_sweep_skipped` (a mass-deletion guard
fired — see `docs/DELETION_SYNC.md`), `degraded_mode` (a critical circuit
breaker opened), `dead_letter` (queue depth crossed
`ALERT_DEADLETTER_THRESHOLD`). Alert delivery failures are swallowed and
logged — alerting can never break a crawl.
