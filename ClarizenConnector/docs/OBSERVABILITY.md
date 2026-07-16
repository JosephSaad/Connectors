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

All metrics are prefixed `clarizen_connector_`:

| Metric | Type | Meaning |
|---|---|---|
| `items_ingested_total` | counter | externalItems successfully PUT |
| `items_failed_total` | counter | items that failed (dead-lettered) |
| `items_skipped_total` | counter | checkpoint-skipped / no-ACL skipped items |
| `crawls_started_total` / `crawls_completed_total` | counter | crawl cycles |
| `throttled_429_total` | counter | Graph 429 responses observed |
| `clarizen_api_calls_total` | counter | Clarizen REST calls issued |
| `attachments_extracted_total` | counter | attachments whose text was extracted and indexed |
| `attachments_skipped_total` | counter | attachments skipped (oversize / disallowed type / no text) |
| `webhook_events_received_total` | counter | webhook posts received (before validation) |
| `webhook_events_accepted_total` | counter | validated webhook events enqueued |
| `webhook_events_rejected_total` | counter | webhook posts rejected (bad signature / malformed / oversize) |
| `webhook_receiver_up` | gauge | 1 while the webhook receiver is bound and listening |
| `tracing_enabled` | gauge | 1 when the OpenTelemetry OTLP exporter is registered (`OTEL_EXPORTER_OTLP_ENDPOINT` set) |
| `circuit_breaker_state{dependency}` | gauge | per-dependency breaker state: 0 closed, 1 half-open, 2 open |
| `circuit_breaker_trips_total{dependency}` | counter | times a dependency's breaker tripped to open |
| `circuit_breaker_resets_total{dependency}` | counter | times a dependency's breaker recovered to closed |
| `api_budget_remaining` | gauge | remaining daily Clarizen API budget |
| `dead_letter_depth` | gauge | live dead-letter queue depth |
| `last_crawl_completed_timestamp_seconds` | gauge | unix time of last completed crawl |
| `uptime_seconds` | gauge | process uptime |

Useful alerts: `time() - last_crawl_completed_timestamp_seconds > 2 *
incremental_interval`, `dead_letter_depth > 0` for an hour,
`api_budget_remaining == 0` during business hours.

## Structured logs (`LOG_FORMAT=json`)

One JSON object per line: `{"timestamp", "level", "logger", "message",
"correlation_id"?}` — both in `logs/{prefix}_{timestamp}/connector.log` and on
the console. Default is human-readable text (correlation id shown as an
`[8-char]` prefix). Console shows WARNING+ unless `--verbose`; the log file
always captures everything. The `correlation_id` (present within a crawl cycle
or webhook event) ties a run together across logs, dead-letter records, and
trace spans — see `docs/TRACING.md`.

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
{"kind": "crawl_failed", "message": "...", "connector": "ClarizenAdaptiveWork",
 "timestamp": "2026-07-12T09:30:00Z", "data": {...}}
```

Alert kinds: `setup_failed`, `crawl_failed`, `crawl_failures` (per-item
failures), `dead_letter` (queue depth crossed `ALERT_DEADLETTER_THRESHOLD`).
Alert delivery failures are swallowed and logged — alerting can never break a
crawl.
