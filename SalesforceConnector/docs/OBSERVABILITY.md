# Observability

Health probes, Prometheus metrics, structured logs, and outbound alerting for
the Salesforce Copilot Connector. Everything here is **off by default** and a
strict no-op unless the relevant environment variable is set — with none set,
behavior and log output are byte-identical to a build without these features.

## Environment variables

| Env var | Default | Meaning |
|---|---|---|
| `HEALTH_PORT` | `0` | `>0` → serve `/health`, `/ready`, `/metrics` on this port. `<=0` disables the endpoint. |
| `LOG_FORMAT` | `text` | `json` → each log record is emitted as one JSON object. `text` (default) is the classic `asctime - name - level - message` line. |
| `ALERT_WEBHOOK_URL` | — | When set, alerts are POSTed as JSON to this URL. Unset → alerting disabled. |
| `ALERT_DEADLETTER_THRESHOLD` | `0` | `>0` → raise a `dead_letter` alert when the dead-letter depth exceeds this value. |

## Health / readiness / metrics endpoint (`HEALTH_PORT`)

When `HEALTH_PORT>0`, an HTTP listener runs on a background thread. It binds
`http://+:{port}/` (all interfaces) and automatically falls back to
`http://localhost:{port}/` if the wildcard bind is denied (i.e. no URL ACL
reservation for a non-admin process). Disposing the handle returned by
`HealthEndpoint.StartIfConfigured(config)` stops it cleanly.

Three routes (everything else returns `404`):

| Route | Meaning | Response |
|---|---|---|
| `GET /health` | Liveness — the process is up. | `200` `OK` |
| `GET /ready` | Readiness — configuration is loaded. | `200` `READY` |
| `GET /metrics` | Prometheus text exposition. | `200` metrics (see below) |

`/metrics` refreshes the live dead-letter depth from the state store
(`SyncState.ReadFailedRecords`) on each scrape; a state-store error is swallowed
and the last known gauge value is served instead.

### Sample curls

```console
$ curl -s http://localhost:9090/health
OK

$ curl -s http://localhost:9090/ready
READY

$ curl -s http://localhost:9090/metrics
# HELP salesforce_connector_items_ingested_total Total Salesforce items successfully ingested into the Graph connection.
# TYPE salesforce_connector_items_ingested_total counter
salesforce_connector_items_ingested_total 0
...
```

### Sample `/metrics` output

```text
# HELP salesforce_connector_items_ingested_total Total Salesforce items successfully ingested into the Graph connection.
# TYPE salesforce_connector_items_ingested_total counter
salesforce_connector_items_ingested_total 12045
# HELP salesforce_connector_items_failed_total Total items that failed to ingest.
# TYPE salesforce_connector_items_failed_total counter
salesforce_connector_items_failed_total 3
# HELP salesforce_connector_items_deleted_total Total items deleted from the Graph connection.
# TYPE salesforce_connector_items_deleted_total counter
salesforce_connector_items_deleted_total 27
# HELP salesforce_connector_items_skipped_total Total items skipped (e.g. already checkpointed).
# TYPE salesforce_connector_items_skipped_total counter
salesforce_connector_items_skipped_total 500
# HELP salesforce_connector_crawls_started_total Total crawl/ingestion runs started.
# TYPE salesforce_connector_crawls_started_total counter
salesforce_connector_crawls_started_total 4
# HELP salesforce_connector_crawls_completed_total Total crawl/ingestion runs completed.
# TYPE salesforce_connector_crawls_completed_total counter
salesforce_connector_crawls_completed_total 3
# HELP salesforce_connector_throttled_429_total Total HTTP 429 (throttling) responses observed from the Graph API.
# TYPE salesforce_connector_throttled_429_total counter
salesforce_connector_throttled_429_total 11
# HELP salesforce_connector_dead_letter_depth Current number of records in the dead-letter queue.
# TYPE salesforce_connector_dead_letter_depth gauge
salesforce_connector_dead_letter_depth 3
# HELP salesforce_connector_last_crawl_completed_timestamp_seconds Unix timestamp (seconds) of the last completed crawl; 0 if none yet.
# TYPE salesforce_connector_last_crawl_completed_timestamp_seconds gauge
salesforce_connector_last_crawl_completed_timestamp_seconds 1751457600
# HELP salesforce_connector_uptime_seconds Seconds since the connector process started.
# TYPE salesforce_connector_uptime_seconds gauge
salesforce_connector_uptime_seconds 842.317
```

All metric names are prefixed `salesforce_connector_`. `*_total` are monotonic
counters; the rest are gauges.

## Structured logging (`LOG_FORMAT=json`)

Set `LOG_FORMAT=json` to emit one JSON object per log record instead of the
classic text line. The default (`text`) is unchanged and byte-identical to
prior behavior. The `progress` logger's console output (bare milestone lines)
is deliberately left untouched in both modes.

### Sample record

```json
{"timestamp":"2026-07-02 14:03:11,254","level":"INFO","logger":"salesforce_connector","message":"Starting ingestion process..."}
```

When a log call carries an exception, an `exception` object is included:

```json
{"timestamp":"2026-07-02 14:03:11,254","level":"ERROR","logger":"salesforce_connector","message":"Failed to load 001xx","exception":{"type":"SalesforceCopilotConnector.Graph.GraphApiError","message":"HTTP 500"}}
```

The `timestamp` uses the exact same `yyyy-MM-dd HH:mm:ss,fff` format as the text
formatter.

## Alerting (`ALERT_WEBHOOK_URL`)

When `ALERT_WEBHOOK_URL` is set, `Alerting.RaiseAsync(kind, message, data)`
POSTs a JSON envelope. Alerting **never breaks a crawl**: a missing URL,
network error, non-2xx response, or timeout (5s) is swallowed and logged, never
thrown. `Alerting.MaybeAlertDeadLetter(connectorId, depth)` raises a
`dead_letter` alert when `depth > ALERT_DEADLETTER_THRESHOLD` (and the threshold
is a positive integer).

### Webhook JSON shape

```json
{
  "kind": "crawl_failed",
  "message": "Ingestion run failed: Graph auth error",
  "connector": "myConnector",
  "timestamp": "2026-07-02T14:03:11.2540000+00:00",
  "data": { "objectType": "Account", "attempt": 3 }
}
```

- `kind` — short machine-readable alert kind (e.g. `crawl_failed`, `dead_letter`).
- `message` — human-readable description.
- `connector` — the connector id (omitted when not configured).
- `timestamp` — ISO-8601 UTC (`DateTimeOffset.ToString("o")`).
- `data` — optional structured payload (omitted when `null`).

## Scraping with Prometheus

Point a Prometheus scrape job at the connector's `HEALTH_PORT`:

```yaml
scrape_configs:
  - job_name: salesforce-copilot-connector
    metrics_path: /metrics
    scrape_interval: 30s
    static_configs:
      - targets: ["connector-host:9090"]
```

Because `/metrics` is standard Prometheus text exposition (v0.0.4), no exporter
sidecar is required. Alertmanager rules can then alert on, for example,
`salesforce_connector_dead_letter_depth` or a stale
`salesforce_connector_last_crawl_completed_timestamp_seconds`.
