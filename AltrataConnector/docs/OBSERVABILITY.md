# OBSERVABILITY — health, metrics, logs, tracing, alerts

## Distributed tracing (OpenTelemetry) + correlation ids

Off by default. Set **`OTEL_EXPORTER_OTLP_ENDPOINT`** (e.g.
`http://collector:4317`) and the connector registers an OTLP TracerProvider and
exports spans; leave it unset and **nothing is registered** — `StartActivity`
returns null, every `Span(...)` is a no-op, and default behaviour/overhead are
unchanged. Standard `OTEL_*` env vars are honoured (`OTEL_EXPORTER_OTLP_PROTOCOL`,
`OTEL_EXPORTER_OTLP_HEADERS`, …); `OTEL_SERVICE_NAME` defaults to the connector
display name. The OTLP exporter batches on a bounded background queue — an
**unreachable collector drops spans silently and never blocks or fails a crawl**.

Spans (ActivitySource `AltrataConnector`), parented correctly:

```
crawl
├── seat.sync                 (seat.count, seat.hash, seat.changed)
├── seat.reacl                (items.count, reacl.updated, reacl.failed)
├── entity.load               (crm.contacts)
├── path.index.build          (path.edges, path.memberships)
└── delivery                  (delivery.id, delivery.files, delivery.status)
    ├── manifest.validate     (manifest.files)
    └── dataset.ingest        (dataset, records.{count,ingested,deleted,suppressed,deadlettered})
        └── graph.batch        (graph.op, graph.batch.{size,ok,failed})   [GraphClient]
api.lookup                     (subject.id, api.billable_total)           [enrichment]
forget-subject                 (subject.resolved_by, subject.count, items.{count,withdrawn,failed})
```

Outbound Graph and Altrata-API requests carry the **W3C `traceparent`** header
so the collector links them to the crawl span.

**Correlation id** — a stable 32-hex id per crawl / erasure cycle (the root
span's trace id when tracing is on, else a generated id). It is stamped on:
every structured **JSON log line** (`correlationId`), **dead-letter records**,
**reconciliation reports**, **erasure-ledger entries** (inside the hash chain),
and **span tags** (`altrata.correlation_id`) — so one cycle is followable
end-to-end by correlation id even with OTLP export off.

### PII CAUTION — no personal data in traces

Trace exhaust is a leakage surface for a licensed-PII connector, so spans and
tags carry **only opaque ids, counts, hashes and enums** — never names, emails,
wealth figures or profile content. Enforcement is structural: **every tag goes
through `Telemetry.SetTag`, which drops any key not on `AllowedTagKeys`** (all
of which are id/count/hash/enum keys). A rogue `altrata.person.name` tag is
silently dropped. Tested two ways: the allowlist contains no PII-shaped key, and
a crawl fed a record with name/email/wealth emits **zero** spans/tags containing
those literals.

## Health endpoint (`HEALTH_PORT`)

Off by default; `HEALTH_PORT=9090` serves on a background thread:

| Route | Response |
|---|---|
| `GET /health` | `200 OK` — liveness (stays up even in degraded mode) |
| `GET /ready` | `200 READY`, or **`503 NOT READY`** when a critical dependency breaker is Open (docs/RESILIENCE.md) |
| `GET /metrics` | Prometheus text (0.0.4) |

Wildcard bind is attempted first (`http://+:port/`); without an admin URL ACL
on Windows it falls back to localhost-only and logs a warning.

## Metrics

| Metric | Type | Meaning |
|---|---|---|
| `altrata_items_ingested_total` | counter | items successfully PUT |
| `altrata_items_failed_total` | counter | items dead-lettered |
| `altrata_items_deleted_total` | counter | items withdrawn (delta tombstones, purge, retry-failed delete replays) |
| `altrata_graph_requests_total` / `altrata_graph_retries_total` | counter | Graph traffic / retries |
| `altrata_graph_throttle_429_total` | counter | 429 throttle events (single-request + $batch ladder + adaptive dial-downs) |
| `altrata_deadletter_depth` | gauge | live queue depth (refreshed per scrape; sums across shard queues under GRAPH_CONNECTION_SHARDS) |
| `altrata_deliveries_processed_total` / `altrata_deliveries_rejected_total` | counter | reconciled / checksum-rejected deliveries |
| `altrata_api_billable_lookups_total` | counter | lifetime billable API lookups (persisted in state, survives restarts) |
| `altrata_seat_count` | gauge | current seat principals |
| `altrata_reacl_passes_total` | counter | completed seat-change re-ACL passes |
| `altrata_path_index_edges` | gauge | relationship-path index edges after the last rebuild (RELATIONSHIP_PATHS) |
| `altrata_subjects_erased_total` | counter | subjects erased via forget-subject (DSAR) |
| `altrata_items_erased_total` | counter | items withdrawn via per-subject erasure |
| `altrata_items_suppressed_total` | counter | records skipped at crawl time (subject on the suppression list) |
| `altrata_suppression_list_size` | gauge | erased subject ids currently suppressed from re-ingestion |
| `altrata_tracing_enabled` | gauge | 1 when OTLP tracing is exporting, else 0 |
| `altrata_tracing_info{endpoint="…"}` | gauge | labeled info line with the OTLP exporter target |
| `altrata_breaker_open` | gauge | 1 when a critical dependency breaker is open (degraded) |
| `altrata_breaker_state{dependency="…"}` | gauge | per-dependency breaker state (0 closed / 1 open / 2 half-open) |
| `altrata_breaker_trips_total{dependency="…"}` / `…_resets_total{…}` | counter | per-dependency opens / recoveries |
| `altrata_crawl_in_progress` | gauge | 1 during a crawl |
| `altrata_last_full_crawl_timestamp_seconds` / `..._incremental_...` | gauge | unix time of last completed crawl |

## Logging

* Per-run directory `logs/{command}_{yyyyMMdd_HHmmss}/connector.log` — file
  captures everything; console shows WARNING+ (INFO too with `--verbose`).
* `LOG_FORMAT=json` → one-line JSON records `{timestamp, level, logger,
  message, exception}` on both sinks.
* `LOG_RETENTION_DAYS=N` prunes run directories older than N days at command
  start (the active run dir is never pruned).
* `LOG_LEVEL=debug` enables DEBUG lines. Off by default — and hot-path debug
  messages (e.g. serializing full $batch responses) are gated behind
  `IAppLogger.IsDebugEnabled`, so their formatting cost is only paid when
  DEBUG is actually enabled (the reference connector's isEnabledFor gate).
* Every JSON line carries `correlationId` (the current crawl/erasure cycle id),
  so `LOG_FORMAT=json` logs join to spans, dead-letter records and reports.
* Fixed-name operational files next to the run dirs: checkpoint, dead-letter
  JSONL, reconciliation reports, append-only audit JSONL.

## Alerts (`ALERT_WEBHOOK_URL`)

JSON POST: `{connector, severity, event, message, timestamp, details}`.

| Event | Severity | Trigger |
|---|---|---|
| `delivery_rejected` | critical | manifest checksum mismatch / missing file |
| `reconciliation_mismatch` | warning | ingested + dead-lettered ≠ manifest count |
| `entitlement_violation` | critical | empty seat list / forbidden ACL attempt |
| `reacl_incomplete` | warning | a seat-change re-ACL pass left items on the previous ACL (hash not committed; re-runs next crawl) |
| `deadletter_threshold` | warning | depth ≥ `ALERT_DEADLETTER_THRESHOLD` |

Alert delivery failures are logged and swallowed — alerting never breaks
ingestion.
