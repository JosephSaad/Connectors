# SIEM — Event Log ids, Sentinel KQL, Splunk, index fields

What the connector emits, how to collect it, and the detection queries that
matter. Runbook anchors (docs/RUNBOOKS.md) are given per detection.

## Signal sources

| Source | Transport | Content |
| --- | --- | --- |
| Windows Event Log | `EVENTLOG_ENABLED=true` → Application log, source `SeismicConnector` | WARNING+ mirror (INFO with `EVENTLOG_LEVEL=info`) + lifecycle marks |
| Structured run logs | `LOG_FORMAT=json` → one JSON object/line in `logs/{run}/…` | Everything (INFO+), `correlation_id` per crawl |
| Prometheus `/metrics` | `HEALTH_PORT` scrape | Counters/gauges (exact names below) |
| Alert webhook | `ALERT_WEBHOOK_URL` POSTs | `{kind, message, connector, timestamp, data}` |
| Reconciliation reports | `logs/{run}/reconciliation_*.jsonl` | No-MNE audit evidence |

## Windows Event Log ids (stable contract)

| Event id | Level | Meaning |
| --- | --- | --- |
| 1000 | Information | Lifecycle: service/command start, stop requested, stopped (exit code) |
| 1100 | Information | Mirrored INFO record (only with `EVENTLOG_LEVEL=info`) |
| 2000 | Warning | Mirrored WARNING (throttle retries, webhook rejections, queue drops, degraded mode, fallbacks) |
| 3000 | Error | Mirrored ERROR (crawl failures, dead-letter write failures, unhandled service exception) |

Message format: `[<logger name>] <message>` — logger names are dotted
(`seismic_connector.webhook`, `seismic_connector.graph`, …) and greppable.
The source is created by `scripts/install-windows-service.ps1` (elevated,
idempotent); the service account only needs write.

## Structured log fields (LOG_FORMAT=json)

| Field | Notes |
| --- | --- |
| `timestamp` | `yyyy-MM-dd HH:mm:ss,fff` local |
| `level` | `DEBUG`/`INFO`/`WARNING`/`ERROR`/`CRITICAL` |
| `logger` | dotted module name |
| `message` | free text (no secrets, no signature values, no content bodies) |
| `correlation_id` | present during a crawl — joins a crawl end-to-end, incl. dead-letter records |
| `exception.type` / `exception.message` | when attached |

## Sentinel (KQL)

Assumes the AMA/Event collector for the Application log (`Event` table) and a
custom log/ingestion for the JSON run logs (`SeismicConnectorLogs_CL` with the
fields above). Ready-to-import alert versions live in
`ops/azure-monitor-alerts.kql`.

**Webhook forgery spike** (runbook: webhook-401-spike) — rejections WITHOUT a
matching rotation event, split by remote:

```kusto
Event
| where Source == "SeismicConnector" and EventID == 2000
| where RenderedDescription has "rejected request with invalid/missing signature"
| extend Remote = extract(@"remote ([^,\)]+)", 1, RenderedDescription)
| summarize Rejections = count(), FirstSeen = min(TimeGenerated), LastSeen = max(TimeGenerated)
    by Remote, bin(TimeGenerated, 15m)
| where Rejections > 20   // sustained, not a one-off misfire
// Rotation mismatch: ONE known Remote, starting at the rotation change window.
// Forgery: unknown/multiple Remotes — page security, not ops.
```

**ACL-widening refused** (never-widen invariant firing — investigate identity
store health, runbook: re-acl-storm):

```kusto
SeismicConnectorLogs_CL
| where level == "WARNING"
| where message has "not applied" and message has "unresolved"
    // AclMapper/Ingest refuse to widen an applied ACL on an unresolved mapping
| summarize Refusals = count() by connector = logger, bin(TimeGenerated, 1h)
| where Refusals > 10
```

**Dead-letter growth** (runbook: dead-letter-growth):

```kusto
Event
| where Source == "SeismicConnector" and EventID == 3000
| where RenderedDescription has "dead-letter" or RenderedDescription has "UNRECORDED FAILURE"
| summarize count() by bin(TimeGenerated, 1h)
```

**Service flapping** (lifecycle 1000 churn):

```kusto
Event
| where Source == "SeismicConnector" and EventID == 1000
| where RenderedDescription has "Service started"
| summarize Starts = count() by Computer, bin(TimeGenerated, 1h)
| where Starts > 3
```

**Degraded-mode entry** (runbook: breaker-open):

```kusto
Event
| where Source == "SeismicConnector" and EventID == 2000
| where RenderedDescription has "DEGRADED MODE"
```

## Splunk (sketch)

Inputs: `WinEventLog://Application` filtered `SourceName=SeismicConnector`;
a monitor on `.../logs/*/*.log` with `INDEXED_EXTRACTIONS=json` when
`LOG_FORMAT=json`; optional `/metrics` via the Prometheus add-on.

```spl
# Forgery vs rotation (webhook-401-spike)
index=wineventlog SourceName=SeismicConnector EventCode=2000 "invalid/missing signature"
| rex "remote (?<remote>[^,\)]+)"
| timechart span=15m count by remote

# Never-widen refusals
index=seismic_connector level=WARNING "not applied" "unresolved"
| stats count by logger, correlation_id

# Crawl failures with full context via correlation id
index=seismic_connector level=ERROR
| stats values(message) as errors by correlation_id
```

## Prometheus metric names (exact; prefix `seismic_connector_`)

Security/ops-relevant series for SIEM-side recording rules — the full table
is in docs/OBSERVABILITY.md:

`webhook_accepted_total`, `webhook_rejected_total`, `webhook_dropped_total`,
`webhook_queue_depth`, `items_ingested_total`, `items_failed_total`,
`items_deleted_total`, `items_reacled_total`, `acl_drift_detected_total`,
`dead_letter_depth`, `throttled_429_total`,
`circuit_breaker_state{dependency}`, `degraded_pauses_total`,
`ha_claims_acquired_total`, `ha_claims_held`,
`last_crawl_completed_timestamp_seconds`.

Alert rules matching the runbooks: `ops/prometheus-alerts.yml`.

## Recommended index fields

For whichever platform: `timestamp`, `host`, `connector` (= CONNECTOR_ID),
`logger`, `level`, `correlation_id`, `event_id` (Windows path), `remote`
(webhook rejections), `item_id`/`object_type` (dead-letter records). Keep
`message` searchable but NOT field-extracted wholesale — it is free text.

## What is deliberately absent from every signal

Secrets, tokens, signature values, certificate material, indexed content
bodies, and (with `DEADLETTER_PAYLOAD_MODE=redacted`) dead-letter payload
values. If a query needs content, that is a Seismic-side lookup by item id.
