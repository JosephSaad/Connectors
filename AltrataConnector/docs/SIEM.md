# SIEM integration — Altrata Copilot Connector

Two feed paths, use either or both:

1. **Windows Event Log** (`EVENTLOG_ENABLED=true`): WARNING/ERROR lines +
   lifecycle markers mirrored to the **Application** log, source
   **`AltrataConnector`** — collect with the Azure Monitor Agent / WEF /
   Splunk UF like any Windows source. The event source is registered by
   `scripts/install-windows-service.ps1` (idempotent; MSI-only deployments:
   `New-EventLog -LogName Application -Source AltrataConnector` once).
2. **Structured JSON file logs** (`LOG_FORMAT=json`):
   `logs/<command>_<timestamp>/connector.log`, one JSON object per line —
   richer (includes INFO and `correlationId`); tail with AMA custom text
   logs / Splunk UF.

Everything on BOTH paths is PII-safe by the chassis discipline: opaque ids,
counts, hashes, enums — never names, emails or wealth figures (test-enforced,
including the Event Log mirror and the dead-letter queue file).

## Event ids (stable contract)

| EventID | Type | Meaning |
|---|---|---|
| **1000** | Information | Lifecycle: `Run started: <command> …`, `Service command starting/finished …` |
| **2000** | Warning | Mirror of every `WARNING` log line |
| **3000** | Error | Mirror of every `ERROR` log line (exception summary appended as `[Type: message]`) |

Semantics live in the MESSAGE — SIEM rules key on `EventID` + the stable
substrings below (they are literal strings from the code; tests keep the
no-PII property, treat the substrings as an interface):

| Signal | Match substring | Severity / class |
|---|---|---|
| **Ledger tamper / torn line** | `FAILED verification: chain broken at seq` or `REFUSING to append` | **critical / SECURITY INCIDENT** (triage tamper-vs-torn per RUNBOOKS "Ledger Verify failure") |
| **Entitlement refusal (fail-closed)** | `ENTITLEMENT:` or `refusing to build an ACL` or `is forbidden: Altrata items must` or `Seat source yielded zero principals` | high / security-relevant |
| **Erasure-race withdrawal** | `Erasure-race withdrawal` or `DSAR race: withdrew` | medium / privacy-ops (normal correct behaviour; alert on volume) |
| **Erased-subject replay refused** | `concerns an erased (suppressed) subject` | info / privacy-ops evidence |
| **Erasure queue scrub** | `Erasure scrubbed` | info / privacy-ops evidence |
| Delivery rejected (checksum) | `REJECTED` (logger `altrata_connector.crawl`) | high (possible feed tampering) |
| Breaker open | `Circuit breaker 'graph' OPEN` | medium / availability |
| Erasure withdrawal queued | `DELETE dead-lettered; suppression stays durable` | medium — open DSAR clock |
| Mirroring broken | `Windows Event Log mirroring failed` (stderr/file only, once) | the Event Log channel itself is down — alert on silence instead (see below) |

Webhook alerts (`ALERT_WEBHOOK_URL`) carry the same taxonomy in the `event`
field: `delivery_rejected`, `entitlement_violation`,
`reconciliation_mismatch`, `reacl_incomplete`, `deadletter_threshold`.

## Microsoft Sentinel (KQL)

Mirrored Event Log path (`Event` table; adjust to `SecurityEvent`/custom DCR
naming as collected). Ready-to-import versions of these three live in
`ops/azure-monitor-alerts.kql`.

**1. Ledger tamper — SECURITY incident class.** Any hit is an incident, not a
metric alert: the erasure ledger is the DSAR compliance record.

```kusto
Event
| where Source == "AltrataConnector" and EventID == 3000
| where RenderedDescription has "FAILED verification: chain broken at seq"
     or RenderedDescription has "REFUSING to append"
| extend BrokenSeq = extract(@"chain broken at seq (\d+)", 1, RenderedDescription)
| project TimeGenerated, Computer, BrokenSeq, RenderedDescription
// Analytics rule: severity High, tactic Impact / DefenseEvasion,
// incident class SECURITY, runbook link: docs/RUNBOOKS.md#ledger-verify-failure
```

**2. Entitlement-violation refusals.** Fail-closed events — the connector
REFUSED to widen visibility. One is a config break; a burst is someone
probing the seat boundary.

```kusto
Event
| where Source == "AltrataConnector" and EventID == 3000
| where RenderedDescription has "ENTITLEMENT:"
     or RenderedDescription has "refusing to build an ACL"
     or RenderedDescription has "is forbidden: Altrata items must"
| summarize Refusals = count(), Sample = any(RenderedDescription)
    by Computer, bin(TimeGenerated, 15m)
| where Refusals >= 1
// severity Medium; >=3 in 15m => High. Runbook:
// docs/RUNBOOKS.md#seat-file-parse-failure
```

**3. Erasure-race withdrawals.** Correct-by-design compensations (a PUT
landed while a DSAR completed; the connector withdrew it). Alert on VOLUME —
a spike means erasures are racing bulk ingest windows.

```kusto
Event
| where Source == "AltrataConnector" and EventID == 2000
| where RenderedDescription has "Erasure-race withdrawal"
     or RenderedDescription has "DSAR race: withdrew"
| summarize Withdrawals = count() by Computer, bin(TimeGenerated, 1h)
| where Withdrawals > 5
// severity Low/informational; evidence for the DSAR file either way.
```

**Silence detector** (mirror channel died — the one failure the channel
cannot report about itself): expect 1000-id lifecycle events from every host
running continuous mode at least daily:

```kusto
Event
| where Source == "AltrataConnector" and EventID == 1000
| summarize LastSeen = max(TimeGenerated) by Computer
| where LastSeen < ago(26h)
```

JSON-file path instead (custom table `AltrataConnectorLogs_CL` with the raw
line in `RawData`): same substrings, plus the correlation id for pivoting:

```kusto
AltrataConnectorLogs_CL
| extend d = parse_json(RawData)
| project TimeGenerated, level = tostring(d.level), logger = tostring(d.logger),
          correlationId = tostring(d.correlationId), message = tostring(d.message)
| where message has "chain broken at seq"
```

## Splunk sketch

Inputs (Universal Forwarder on the host):

```ini
# inputs.conf — Event Log path
[WinEventLog://Application]
whitelist1 = SourceName="AltrataConnector"
index = altrata

# inputs.conf — JSON file path
[monitor://D:\AltrataConnector\logs\*\connector.log]
sourcetype = altrata:connector:json
index = altrata
```

```ini
# props.conf
[altrata:connector:json]
KV_MODE = json
TIME_PREFIX = "timestamp":"
MAX_TIMESTAMP_LOOKAHEAD = 40
SHOULD_LINEMERGE = false
```

Searches (mirror the three Sentinel rules):

```spl
# Ledger tamper — notable event, security domain
index=altrata (source="WinEventLog:Application" SourceName="AltrataConnector" EventCode=3000)
  ("FAILED verification: chain broken at seq" OR "REFUSING to append")

# Entitlement refusals per 15m
index=altrata sourcetype=altrata:connector:json level=ERROR
  ("ENTITLEMENT:" OR "refusing to build an ACL")
| bin _time span=15m | stats count by _time host

# Erasure-race volume
index=altrata sourcetype=altrata:connector:json level=WARNING
  ("Erasure-race withdrawal" OR "DSAR race: withdrew")
| timechart span=1h count
```

## Index fields (all PII-safe)

Extract/keep these; drop nothing else from the message (it is already safe):

| Field | Source | Notes |
|---|---|---|
| `timestamp`, `level`, `logger`, `message` | JSON log line | logger names are stable (`altrata_connector.crawl`, `.graph`, `.erasure_ledger`, `.entitlement`, `.breaker`, `.ha`, …) |
| `correlationId` | JSON log line / dead-letter / reconciliation / ledger entries | 32-hex; joins one crawl or erasure cycle end-to-end |
| `EventID`, `SourceName`, `Computer` | Event Log path | ids 1000/2000/3000 |
| delivery id | extract `Delivery '([^']+)'` | opaque |
| item id | extract `item ([A-Za-z0-9-]+(_[0-9a-f]{12})?)` | `{dataset}-{recordId}` shape; opaque vendor id |
| subject id | extract `subject '([^']+)'` | opaque Altrata person id (an id, not a name — the chassis never logs names/emails/wealth) |
| broken seq | extract `chain broken at seq (\d+)` | ledger triage |
| breaker name | extract `Circuit breaker '([a-z-]+)'` | `graph` / `altrata-api` |

Metric-side alerting (Prometheus/Azure Monitor for `/metrics` scrapes) is in
`ops/prometheus-alerts.yml` and `ops/azure-monitor-alerts.kql`; the
ledger-tamper rule there (`altrata_erasure_ledger_broken == 1`) carries
`severity: critical, class: security` and must page the SECURITY rotation,
not only ops — keep both paths (metric + log) enabled; they cross-check each
other.
