# SIEM Integration

Getting the connector's telemetry into Sentinel / Splunk / any Windows-event
pipeline. Three feeds, use any combination:

1. **Windows Event Log** (`EVENTLOG_ENABLED=true`) — WARNING+/lifecycle only;
   the feed for agent-based Windows fleets (SCOM, Sentinel AMA, Splunk UF).
2. **JSON log files** (`LOG_FORMAT=json`) — everything (INFO+), one JSON object
   per line in `logs/{prefix}_{timestamp}/*.log`; the feed for file-tail
   collectors.
3. **Webhook alerts** (`ALERT_WEBHOOK_URL`) — pre-digested alert envelopes
   (kinds `crawl_failed`, `dead_letter`, `deletion_sweep_skipped`);
   see [OBSERVABILITY.md](OBSERVABILITY.md).

Prometheus metrics ([OBSERVABILITY.md](OBSERVABILITY.md)) are for dashboards/
alerting (`ops/`), not SIEM — don't ship scrapes into the SIEM.

## 1. Windows Event Log

Enabled with `EVENTLOG_ENABLED=true` (Windows only; a strict no-op elsewhere).
Source registration is done by `scripts/install-windows-service.ps1` or the MSI
— the service itself never needs admin.

| Property | Value |
|---|---|
| Log | `Application` |
| Source | `SalesforceConnector` |
| Mirrored by default | WARNING, ERROR/CRITICAL, and service lifecycle (start command, stop, exit code) |
| `EVENTLOG_LEVEL=info` | additionally mirrors INFO (verbose — file logs are usually the better home) |

Event ids are stable — key rules on id + source, not message text:

| Event id | Level | Meaning |
|---|---|---|
| `1000` | Information | Lifecycle / opted-in INFO (service started with command line, command finished with exit code) |
| `2000` | Warning | Any WARNING (429 throttling, sweep guard trips, heartbeat misses, …) |
| `3000` | Error | Any ERROR/CRITICAL (crawl failures, unhandled exceptions, dead-letter write failures) |

The message body is the same formatted line the log file gets (timestamp,
logger name, level, message, full exception with stack). Messages over the
event-log size cap are truncated with `…[truncated]` — the log file always has
the full text.

Baseline detections:

- `Id == 3000` → incident, route to [RUNBOOKS.md](RUNBOOKS.md) by message
  (auth → token/auth failure; `deletion sweep SKIPPED` → sweep guard trip).
- `Id == 1000 AND message has "Command finished with exit code 1"` → the
  service command failed and will be restarted by SCM — investigate before the
  restart loop burns retries ([RUNBOOKS.md](RUNBOOKS.md#crawl-stalled)).
- Absence of any event for > (crawl interval) on a node that has
  `EVENTLOG_LEVEL=info` → stalled/stopped.

## 2. JSON log ingestion

With `LOG_FORMAT=json` each record is one single-line object:

```json
{"timestamp":"2026-07-17 04:12:33,412","level":"ERROR","logger":"salesforce_connector","message":"Graph API transient error 429 for PUT ...","exception":{"type":"...","message":"...","stack":"..."}}
```

Fields: `timestamp` (local, `yyyy-MM-dd HH:mm:ss,fff`), `level`
(`DEBUG|INFO|WARNING|ERROR|CRITICAL`), `logger` (dotted module name), `message`,
optional `exception.type|message|stack`. Progress-console output stays bare and
never lands in the files as JSON noise.

**Index these fields**: `level`, `logger`, `timestamp`, plus host and the
run-dir name (carries command + start time). `message` stays searchable text.
Do not index `exception.stack` as terms (multiline, high-cardinality).

### Microsoft Sentinel

Collect `logs/*/*.log` with the AMA custom-text-log route into a custom table
(say `SfConnector_CL` with the raw line in `RawData`). Parse:

```kusto
// Parse the JSON lines
let SfConnector = () ->
SfConnector_CL
| extend j = parse_json(RawData)
| where isnotempty(j.level)          // skip any non-JSON banner lines
| project TimeGenerated,
          Host = Computer,
          Level = tostring(j.level),
          Logger = tostring(j.logger),
          Message = tostring(j.message),
          ExceptionType = tostring(j.exception.type),
          LogTime = todatetime(replace_string(tostring(j.timestamp), ",", "."));
```

Top error signatures (triage view):

```kusto
SfConnector()
| where Level in ("ERROR", "CRITICAL")
| extend Signature = extract(@"^([^:{(\[]{0,80})", 1, Message)   // stable prefix
| summarize Count = count(), Sample = any(Message), LastSeen = max(TimeGenerated)
    by Signature, ExceptionType
| order by Count desc
| take 20
```

Alert-worthy patterns (scheduled analytics rules):

```kusto
// 1. Auth failures — page (runbook: token/auth failure)
SfConnector()
| where Message has_any ("AuthenticationFailedException", "CredentialUnavailableException",
                         "InvalidAuthenticationToken", "invalid_client", "invalid_grant")
| summarize count() by Host, bin(TimeGenerated, 15m)
| where count_ > 0

// 2. 429 storm — warn when sustained (runbook: 429 storm)
SfConnector()
| where Message has "Graph API transient error 429"
| summarize Throttles = count() by Host, bin(TimeGenerated, 15m)
| where Throttles > 100

// 3. Deletion-sweep guard trip — warn (runbook: deletion-sweep guard trip)
SfConnector()
| where Message has "deletion sweep SKIPPED"

// 4. Dead-letter write failures — data-loss risk, page
SfConnector()
| where Message has "UNRECORDED FAILURE" or Message has "Failed to write" and Message has "dead-letter"

// 5. Crawl crash — page (runbook: crawl stalled)
SfConnector()
| where Message has "crashed with an unhandled exception" or Message has "Service command failed"
```

Webhook alternative: point `ALERT_WEBHOOK_URL` at a Logic App / data collection
endpoint and you get kinds 1, 3 and dead-letter *depth* without log parsing.

### Splunk

Universal Forwarder monitors the run dirs; JSON lines index cleanly.

`inputs.conf`:

```ini
[monitor://C:\SFConnector\logs\*\*.log]
sourcetype = sfconnector:json
index = app_sfconnector
disabled = false
```

`props.conf` (indexer/HF):

```ini
[sfconnector:json]
KV_MODE = json
SHOULD_LINEMERGE = false
LINE_BREAKER = ([\r\n]+)
TIME_PREFIX = \"timestamp\":\"
TIME_FORMAT = %Y-%m-%d %H:%M:%S,%3N
MAX_TIMESTAMP_LOOKAHEAD = 30
TRUNCATE = 100000
```

`transforms.conf` sketch — route DEBUG chatter to the null queue if you must
collect with `EVENTLOG_LEVEL`-style economy:

```ini
# props.conf addition
[sfconnector:json]
TRANSFORMS-drop_debug = sfconnector_drop_debug

# transforms.conf
[sfconnector_drop_debug]
REGEX = \"level\":\"DEBUG\"
DEST_KEY = queue
FORMAT = nullQueue
```

Starter searches (mirror the Sentinel rules):

```
index=app_sfconnector level=ERROR | cluster showcount=t | sort -cluster_count
index=app_sfconnector "Graph API transient error 429" | timechart span=15m count
index=app_sfconnector "deletion sweep SKIPPED"
```

Windows Event Log route instead: collect
`WinEventLog://Application` with `whitelist = SourceName="SalesforceConnector"`
and alert on `EventCode=3000`.

## 3. Which feed for what

| Question | Feed |
|---|---|
| "Is the service up / did it crash?" | Event Log (ids 1000/3000) or metrics `uptime_seconds` |
| "Why did item X fail?" | JSON logs (+ the dead-letter record — mind `DEADLETTER_PAYLOAD_MODE`) |
| "Are we being throttled?" | metrics (`throttled_429_total`) for trend; JSON logs for evidence |
| "Page me when the queue grows" | webhook `dead_letter` alert, or `ops/prometheus-alerts.yml` |
| Compliance trail of service starts/stops | Event Log (id 1000) |
