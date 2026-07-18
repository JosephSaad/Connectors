# SIEM integration

Two feeds: the structured JSON log file (`LOG_FORMAT=json` — one object per
line in `logs/<prefix>_<timestamp>/connector.log`) and the Windows Event Log
mirror (`EVENTLOG_ENABLED=true`). Metrics-based alerting lives in
`ops/prometheus-alerts.yml` / `ops/azure-monitor-alerts.kql`; every detection
below links a runbook anchor in `docs/RUNBOOKS.md`.

## Windows Event Log contract

Source **`ClarizenConnector`**, log **`Application`**. The source is created
idempotently by `scripts/install-windows-service.ps1` (or the MSI's registry
component). `EVENTLOG_ENABLED=true` turns the mirror on; default level mirrors
Warning+Error; `EVENTLOG_LEVEL=info` adds Info. Debug is never mirrored. The
sink never throws and disables itself after a write failure (one stderr line).

Event ids are STABLE (pinned in `Infrastructure/EventLogSink.cs` — do not
renumber):

| Event id | Level | Meaning |
|---|---|---|
| 1000 | Information | mirrored `Logger.Info` (only with `EVENTLOG_LEVEL=info`) |
| 1001 | Information | service lifecycle: SCM start (`Running as a Windows service: ...`) — always mirrored when enabled |
| 1002 | Information | service lifecycle: stop requested / command finished — always mirrored when enabled |
| 2000 | Warning | mirrored `Logger.Warning` |
| 3000 | Error | mirrored `Logger.Error` (message includes the exception text) |

Detection hints: a 1001 without a matching prior 1002 = crash-restart loop
(pair with SCM 7031/7034 from Service Control Manager); 3000 bursts = crawl
failures (correlate with the JSON log via timestamp).

## JSON log schema (fields to index)

`LOG_FORMAT=json` emits per line:

| Field | Notes |
|---|---|
| `timestamp` | ISO-8601 UTC |
| `level` | `DEBUG` / `INFO` / `WARNING` / `ERROR` |
| `logger` | component: `clarizen_connector`, `.clarizen`, `.graph`, `.webhook`, `.ha`, `.service`, `.cli` |
| `message` | text; errors append the exception + stack |
| `correlation_id` | present inside a crawl cycle / webhook event — the pivot key across logs, dead-letter records and traces |

Index all five; `correlation_id` and `logger` are the high-value pivots.
Dead-letter JSONL (`failed_records_<id>.jsonl`) is also ingestible: fields
`item_id`, `object_type`, `error`, `timestamp`, `correlation_id`.

## Microsoft Sentinel (KQL)

Assumes ingestion into a custom table `ClarizenConnector_CL` with `RawData`
holding the JSON line (adjust the table/column to your ingestion path).

Parse once:

```kusto
let Connector =
    ClarizenConnector_CL
    | extend d = parse_json(RawData)
    | project TimeGenerated,
              Level = tostring(d.level),
              Logger = tostring(d.logger),
              Message = tostring(d.message),
              CorrelationId = tostring(d.correlation_id);
```

Error-signature top-N (what is actually breaking, ranked):

```kusto
Connector
| where Level == "ERROR"
| extend Signature = extract(@"^([^:\(]{0,80})", 1, Message)   // coarse head of the message
| summarize Count = count(), Sample = any(Message), LastSeen = max(TimeGenerated)
    by Signature
| top 10 by Count desc
```

Webhook-forgery spike (runbook: `RUNBOOKS.md#webhook-flood--401-spike`):

```kusto
Connector
| where Logger == "clarizen_connector.webhook"
| where Message has "rejected a post with an invalid or missing"
| summarize Rejected = count() by bin(TimeGenerated, 5m)
| where Rejected > 20          // tune: legitimate senders should never fail HMAC
```

Breaker trips and degraded windows (runbooks: clarizen/graph breaker open):

```kusto
Connector
| where Message has "TRIPPED (open)" or Message has "recovered (half-open"
| parse Message with "Circuit '" Dependency "': " Transition
| project TimeGenerated, Dependency, Transition
| order by TimeGenerated asc
```

Follow one crawl end-to-end from any error line:

```kusto
let cid = "<correlation_id from the error>";
Connector | where CorrelationId == cid | order by TimeGenerated asc
```

Event Log channel (via the Windows Security Events / custom WEC pipeline):

```kusto
Event
| where Source == "ClarizenConnector"
| summarize count() by EventID, bin(TimeGenerated, 15m)
// alert: EventID 3000 spike, or 1001 repeating without an intervening 1002
```

## Splunk sketch

```
# props.conf
[clarizen:connector]
INDEXED_EXTRACTIONS = json
TIMESTAMP_FIELDS = timestamp
KV_MODE = none

# inputs.conf (crawl host)
[monitor://C:\ClarizenConnector\logs\*\connector.log]
sourcetype = clarizen:connector
[monitor://C:\ClarizenConnector\logs\failed_records_*.jsonl]
sourcetype = clarizen:deadletter
```

Searches: error top-N
`sourcetype=clarizen:connector level=ERROR | stats count by message | sort -count | head 10`;
forgery spike
`sourcetype=clarizen:connector logger=clarizen_connector.webhook "rejected a post" | timechart span=5m count`;
crawl trace `sourcetype=clarizen:connector correlation_id=<cid> | sort _time`.
The Event Log arrives via the standard `WinEventLog://Application` input
filtered on `SourceName=ClarizenConnector`.

## What to alert on vs what to dashboard

Alert (SIEM or Prometheus — rules shipped in `ops/`): breaker open,
degraded `/ready`, dead-letter depth, 401/413 webhook spikes, 429 storm,
deletion-sweep guard, token failures, service crash-restart loop.
Dashboard only: throughput counters, budget gauge, uptime, HA lease counts —
see `ops/grafana-dashboard.json`.
