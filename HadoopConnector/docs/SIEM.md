# SIEM integration

Two feeds, use either or both:

1. **Windows Event Log** (`EVENTLOG_ENABLED=true`) — Warning/Error mirrored to
   the Application log, source `HadoopConnector`, plus lifecycle events.
   Collected by the Azure Monitor Agent / Splunk UF like any Windows event.
   The source is registered by `scripts/install-windows-service.ps1`
   (idempotent, elevated once per host).
2. **Structured run logs** (`LOG_FORMAT=json`) — one JSON object per line
   (`timestamp`, `level`, `logger`, `message`, `correlation_id?`) in
   `logs/{prefix}_{timestamp}/connector.log`; ship with a file-tail collector
   for full-fidelity (Info/Debug included) analytics.

Metrics-based alerting (Prometheus/Grafana) is the third leg —
`ops/prometheus-alerts.yml`, `ops/grafana-dashboard.json`. Every alert below
links a runbook anchor in `docs/RUNBOOKS.md`.

## Event ids (stable contract — `Infrastructure/EventLogSink.cs`)

| EventID | Level | Meaning |
|---|---|---|
| 1000 | Information | lifecycle: connector starting (pid) |
| 1001 | Information | lifecycle: connector stopping (pid) |
| 1100 | Information | mirrored Info line (only with `EVENTLOG_LEVEL=info`) |
| 2000 | Warning | mirrored Warning line (`logger: message`) |
| 3000 | Error | mirrored Error line (`logger: message`, exception text included) |

Dispatch rules: Error→3000, Warning→2000, Info→1100 (opt-in), Debug never.
The event message is `logger: message` — the `logger` prefix
(`hadoop_connector.webhdfs`, `.graph`, `.fetch`, `.ingest`, `.ha`) is the
cheapest classifier a SIEM gets; parse it.

## Microsoft Sentinel (KQL over the `Event` table)

Baseline filter — put it in a function `HadoopConnectorEvents`:

```kql
Event
| where EventLog == "Application" and Source == "HadoopConnector"
| extend Logger = extract(@"^([\w\.]+):", 1, RenderedDescription)
| project TimeGenerated, Computer, EventID, EventLevelName, Logger, RenderedDescription
```

**Guard-refusal spike** (filters.json regression reaching production —
runbook: "Guard refusal (unfiltered object)"):

```kql
HadoopConnectorEvents
| where EventID == 3000 and RenderedDescription contains "has no filter configured"
| summarize Refusals = count(), Objects = make_set(extract(@"'([^']+)' has no filter", 1, RenderedDescription)) by Computer, bin(TimeGenerated, 1h)
| where Refusals > 0
```

**Sweep-suppressed alert** (deletion sweep skipped — incomplete fetch or a
mass-deletion guard; runbook: "Oversize-skip partial crawl (sweep
suppressed)"):

```kql
HadoopConnectorEvents
| where EventID == 2000 and RenderedDescription contains "deletion sweep"
    and (RenderedDescription contains "skipped" or RenderedDescription contains "SKIPPED")
| project TimeGenerated, Computer, RenderedDescription
```

**Degraded mode / breaker open** (runbook: "WebHDFS flapping / breaker open"):

```kql
HadoopConnectorEvents
| where EventID == 2000 and RenderedDescription contains "Degraded mode"
| summarize count() by Computer, bin(TimeGenerated, 15m)
```

**Lifecycle watch** (a 1000 with no matching 1001 and no fresh heartbeat =
crash; pair with service-restart events 7031/7034 from Service Control
Manager):

```kql
HadoopConnectorEvents
| where EventID in (1000, 1001)
| order by TimeGenerated asc
```

**Delegation-token-leak canary — proves the logs STAY clean.** The WebHDFS
client logs `uri.AbsolutePath` only, never the query string that carries
`?delegation=` (`Hdfs/WebHdfsClient.cs`); dead-letter and event-log text
inherit that. This scheduled query must return ZERO rows — a single hit means
a regression leaked credential material into telemetry and is itself a
security incident:

```kql
HadoopConnectorEvents
| where RenderedDescription contains "delegation=" or RenderedDescription matches regex @"[?&]delegation="
| project TimeGenerated, Computer, EventID, RenderedDescription
// Alert threshold: > 0 rows. Expected steady state: zero, forever.
```

Run the same canary over the file feed if you ingest `connector.log`
(`| where RawData contains "delegation="`) — the file feed carries
Debug/Info too, so it is the stricter check.

## Splunk sketch

`inputs.conf` (UF on the crawl host):

```ini
[WinEventLog://Application]
whitelist = $XmlRegex = <Provider Name='HadoopConnector'/>
index = hadoop_connector

[monitor://C:\HadoopConnector\logs\*\connector.log]
sourcetype = hadoop_connector:json
index = hadoop_connector
```

`props.conf`:

```ini
[hadoop_connector:json]
KV_MODE = json
TIME_PREFIX = "timestamp":"
MAX_TIMESTAMP_LOOKAHEAD = 40
```

Searches (mirror the KQL): guard refusals
`index=hadoop_connector "has no filter configured"`; sweep suppressed
`index=hadoop_connector "deletion sweep" ("skipped" OR "SKIPPED")`; token
canary (alert on ANY result)
`index=hadoop_connector "delegation="`.

## Index fields worth extracting

| Field | Source | Why |
|---|---|---|
| `EventID` | event log | the five-id contract above |
| `Logger` (message prefix / JSON `logger`) | both feeds | subsystem routing: `.webhdfs` `.graph` `.fetch` `.ingest` `.ha` `.identity_sync` |
| `correlation_id` | JSON feed (and `[8-char]` prefix in text logs) | one crawl cycle end-to-end across logs ↔ dead-letter ↔ traces (`docs/TRACING.md`) |
| `level` | JSON feed | file feed carries Info/Debug the event log never sees |
| object name in `'X' has no filter` / `X: deletion sweep` / `[X] worker crashed` | message extraction | per-object aggregation for the noisy failure modes |
| `Computer`/host | collector | HA: which node; pair with `NODE_ID` if set |

## Alert-to-runbook map (keep in sync with ops/)

| Alert | Feed | Runbook anchor (`docs/RUNBOOKS.md`) |
|---|---|---|
| GuardRefusalSpike | metrics `guard_refusals_total` / KQL above | guard-refusal-unfiltered-object |
| SweepSuppressed | metrics `sweeps_suppressed_total` / KQL above | oversize-skip-partial-crawl-sweep-suppressed |
| WatermarkStale (>26 h) | metrics `last_crawl_completed_timestamp_seconds` | dt-watermark-gaps-missing-partitions |
| DeadLetterGrowth | metrics `dead_letter_depth` + webhook `dead_letter` | dead-letter-growth |
| BreakerOpen | metrics `circuit_breaker_state` + KQL degraded | webhdfs-flapping--breaker-open-degraded-mode |
| Throttle429Storm | metrics `throttled_429_total` | 429-storm-graph-throttling |
| DelegationTokenCanary | KQL/Splunk canary | (security incident — not operational) |
| IdentityDirectoryIncomplete | webhook `identity_directory_incomplete` / EventID 3000 | identity-directory-incomplete-sync-fails-loud--intentional |
