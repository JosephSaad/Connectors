# Clarizen Copilot Connector (C# / .NET 10)

A standalone Microsoft 365 Copilot **Graph connector for Clarizen (Planview
AdaptiveWork)**: crawls projects, tasks, milestones, issues, risks, timesheets,
resource assignments and discussion/attachment metadata through the Clarizen
REST API v2 (with a bulk TDW-export path for full crawls), resolves per-record
ACLs from Clarizen project membership/groups mapped to Entra ID identities, and
ingests everything as `externalItem`s into a Microsoft Graph external
connection.

Architecture and operational features mirror the Salesforce Copilot Connector
(same chassis: unified CLI, checkpointed crawls, dead-letter + retry, SQL/HA
backends, Key Vault, health/metrics/alerting, Windows-service mode). The shared
identity/seam init, `ServiceStop`, logging, secret provider, SQL
executor/gateway and metrics renderer come from the `Connector.Chassis` project
(1.13.1) at the repository root, consumed by `<ProjectReference>` to
`../../../Connector.Chassis/Connector.Chassis.csproj` — not as a NuGet package.
The connector-specific machinery (HA coordinator, SQL state store, decision
ledger, alerting, Event Log sink, log pruner, service host, circuit breakers)
still lives here.

## Layout

| Path | Contents |
|---|---|
| `src/ClarizenConnector/Clarizen/` | REST v2 client (session auth, CZQL paging, delta cursor), daily API budget/rate limiter, TDW bulk reader |
| `src/ClarizenConnector/Graph/` | Graph client (retry/backoff/jitter), connection + schema provisioning, ingest pipeline ($batch, checkpoints, dead-letter), identity stores (SQLite / SQL Server) |
| `src/ClarizenConnector/AclEngine/` | directory snapshot, project-membership/group/owner ACL resolver, Clarizen→Entra principal mapper, identity sync |
| `src/ClarizenConnector/Item/` | record → externalItem conversion, financial-field classification, attachment enrichment |
| `src/ClarizenConnector/Content/` | dependency-free text extraction (`IContentExtractor`: OOXML/PDF/text/html) |
| `src/ClarizenConnector/Webhook/` | event-driven incremental: receiver, HMAC validation, debouncer, processor |
| `src/ClarizenConnector/Config/` | env config, `schema.json` models, sync state (files or SQL) |
| `src/ClarizenConnector/Commands/` + `Program.cs` | CLI |
| `src/ClarizenConnector/Infrastructure/` | metrics, health endpoint, alerting, env loading, log pruning, Windows-service host, HA coordinator, decision ledger, OpenTelemetry tracing + correlation ids, circuit breakers + degraded mode (logging, Key Vault secrets and the SQL executor/gateway come from `Connector.Chassis`) |
| `config/` | `schema.json` (Clarizen object list, fields, financial fields, ACL modes), `graph-schema.json` (connection schema) |
| `env/.env.local.example` | every knob, documented |
| `docs/` | `HA.md`, `RETRY.md`, `OBSERVABILITY.md`, `SQL_CONTRACT.md`, `SHARDING.md`, `DELETION_SYNC.md`, `ATTACHMENTS.md`, `WEBHOOKS.md`, `TRACING.md`, `RESILIENCE.md` |
| `scripts/` | `install-windows-service.ps1`, `sql/create-database.sql` |
| `tests/ClarizenConnector.Tests/` | xUnit suite (878 tests, mock HTTP — no network) |
| `Dockerfile` / `docker-compose.yml` | container image (built with the repository root as context) + local SQL/HA dev topology |
| — | This connector carries no workflows of its own: GitHub only runs the workflows at the repository root — CI is `.github/workflows/clarizen.yml`, releases are `.github/workflows/release-clarizen.yml` |

## Requirements

- .NET 10 SDK (build) / .NET 10 runtime on the crawl host. Deployment target is
  Windows Server (service mode); the code is cross-platform for dev/test.
- Entra app registration with application permissions
  `ExternalConnection.ReadWrite.OwnedBy`, `ExternalItem.ReadWrite.OwnedBy`,
  `User.Read.All` (admin-consented).
- A Clarizen API user and your org's daily API quota
  (`CLARIZEN_API_CALLS_PER_DAY` budgets the connector's share; full crawls can
  bypass the API entirely via `TDW_EXPORT_PATH` bulk exports).

## Configure

```bash
cp env/.env.local.example env/.env.local     # non-secret config
# put SECRET_CLARIZEN_PASSWORD and SECRET_AAD_APP_CLIENT_SECRET
# into env/.env.local.user (never committed)
```

Review `config/schema.json` — it drives everything object-related: which
Clarizen entity types are crawled, field → Graph property mapping, which fields
are **financial** (budget/cost/rates/actuals/revenue), the ACL mode per object
(`projectMembers` | `ownerOnly` | `public`) and the parent-project reference
field. `config/graph-schema.json` must contain every Graph property the object
list produces.

This is **enforced at the write path, not advisory**. `ExternalItem.Properties`
is a checked bag: stamping a Graph property that `config/graph-schema.json` does
not declare throws `UndeclaredGraphPropertyException` at the point of the stamp,
naming the property — literal, `const` or runtime-computed makes no difference,
because the check is on the value of the name and not on where the stamp lives.
An undeclared property therefore can never reach a Graph `PUT` (Graph would
reject it, and the connector would be undeployable), and it is never silently
dropped either. A **blank or whitespace-only** property name is the same fault
with the same type — no connection schema can declare it. During a crawl these
are **not** treated as a poisoned source row: they escape the per-record and
per-object isolation catches and abort the run, because they are defects that
affect every record and would otherwise close a crawl "successfully" with 100%
of the data dead-lettered **and the sync cursor advanced past all of it**.

The escalation keys on the base type `GraphSchemaConfigurationException`, so it
covers the whole class of "the configuration cannot produce a deployable item" —
including a `config/graph-schema.json` that is missing, is not a JSON array, or
declares no usable names, which surfaces as `GraphSchemaUnavailableException`
from the first stamp. A blank Graph property name in a `selectedFields` mapping
is additionally rejected by `SchemaConfig.Load`, so that config never starts a
crawl at all.

`ExternalItem.ToJson()` re-checks the whole bag as a second layer, and that
re-check is unconditional: it is not relaxed by the internal
`GraphPropertyRegistry.SuspendEnforcement` scope that `StampedPropertyInventory`
uses to observe stamps.

`validate-config` reports drift as a **preflight** too — stamped-but-undeclared
is an error, declared-but-unstamped a warning — but the preflight's stamped-side
enumeration (`StampedPropertyInventory`) runs a fixed set of stamper call sites,
so it is a **best-effort early warning and not the guarantee**. A stamp added
somewhere it does not know about will be caught by the write path at crawl time,
not by the preflight. A degenerate `graph-schema.json` (empty array, or entries
whose `name` is empty) is itself a preflight **error**, since the connector reads
that same file at runtime to decide what it may stamp.

Add a `selectedFields` mapping to `schema.json` and you must add the matching
property to `graph-schema.json`.

## Usage

Run from the `ClarizenConnector/` directory (`logs/`, `data/`, `config/`,
`env/` resolve against the current directory):

```bash
dotnet run --project src/ClarizenConnector -- guide
dotnet run --project src/ClarizenConnector -- validate-config --strict
dotnet run --project src/ClarizenConnector -- identity-dry-run --save --verbose
dotnet run --project src/ClarizenConnector -- setup-connection
dotnet run --project src/ClarizenConnector -- full-deployment
dotnet run --project src/ClarizenConnector -- full-deployment --continuous --full-crawl-hours 24 --incremental-hours 4
dotnet run --project src/ClarizenConnector -- ingest --verbose
dotnet run --project src/ClarizenConnector -- ingest-object --type Project
dotnet run --project src/ClarizenConnector -- ingest-item --id /Task/1234567
dotnet run --project src/ClarizenConnector -- retry-failed --clear-on-success
dotnet run --project src/ClarizenConnector -- reconcile
dotnet run --project src/ClarizenConnector -- reconcile --type Project --fix
```

Full crawls prefer TDW bulk export files (`{ObjectName}.csv`/`.json` under
`TDW_EXPORT_PATH`) and fall back to the REST API per object; incremental crawls
always use the API with a `LastUpdatedOn > <last-sync>` delta cursor. Crawls
checkpoint per object type per chunk — a crash, Ctrl+C, service stop or API
budget exhaustion resumes exactly where it stopped. Failed items land in
`logs/failed_records_<CONNECTOR_ID>.jsonl` for `retry-failed`.

### Deletion sync & reconcile

Every confirmed put is tracked in an ingested-item inventory
(`data/{CONNECTOR_ID}_inventory.db`, or `dbo.ItemInventory` on SQL Server).
Full crawls then run an existence sweep: inventory ids missing from the source
are DELETEd from the Graph connection (tombstone sync — Clarizen has no
deletion feed). Two mass-deletion safety guards (`DELETION_SYNC_MAX_ITEMS`,
default 1000 absolute; `DELETION_SYNC_MAX_PERCENT`, default 25%) skip
implausible sweeps and raise a `deletion_sweep_skipped` alert. `reconcile [--type X] [--fix]` audits index-vs-source drift on demand —
reporting missing items and (with `--fix`) deleting stale ones. Details:
`docs/DELETION_SYNC.md`.

### Event-driven incremental (webhooks)

Polling (delta cursor) is the default and the correctness backstop. With
`CLARIZEN_WEBHOOK_PORT` + `CLARIZEN_WEBHOOK_SECRET` set, `--continuous` mode
also runs an HTTP receiver that turns Clarizen change notifications into
targeted work in near-real-time — upserts re-ingest by id (inventory-recorded,
shard-routed, attachment-enriched), deletes withdraw the item (reusing the
deletion machinery). Every post is **HMAC-SHA256 validated over the raw body
before it is parsed or enqueued** (constant-time compare); a port without a
secret **fails closed** (the receiver refuses to start). Events for the same
entity within `CLARIZEN_WEBHOOK_DEBOUNCE_MS` coalesce (last writer wins).
Anything the receiver misses is caught by the next incremental crawl. Off by
default. Details: `docs/WEBHOOKS.md`.

### Distributed tracing & correlation IDs

Setting `OTEL_EXPORTER_OTLP_ENDPOINT` turns on OpenTelemetry spans around the
crawl cycle, per-object crawl, source fetch (REST/TDW), transform, Graph batch
ingest, deletion sweep, and webhook events (correctly parent/child'd on one
`ClarizenConnector` ActivitySource). A **correlation id** — the trace id when a
span is active — is stamped on every structured log line, dead-letter record
(JSONL + `dbo.DeadLetter.CorrelationId`), and span, so one crawl is followable
end-to-end. Export is batched/fire-and-forget (a dead collector never stalls a
crawl); the standard `OTEL_*` env vars are honoured. **Unset = no exporter, no
listener, a genuine no-op** — `ActivitySource.StartActivity` returns null in
O(1), so default overhead is unchanged. `/metrics` exposes
`tracing_enabled` and `validate-config` reports the exporter target. Details:
`docs/TRACING.md`.

### Circuit breakers & degraded mode

A breaker per external dependency (`clarizen`, `graph`) fails fast during a
**sustained** outage — distinct from retry/backoff, which smooths transient
blips. Only real failures (`5xx`/timeout/connection) count; `4xx` and honoured
`429` (flow control) do not trip it. When a critical breaker opens the crawl
enters **degraded mode**: it pauses at a safe checkpoint boundary (in-flight
items are neither lost nor dead-lettered), does **not** advance the sync cursor,
and resumes cleanly on the next cycle — auto-recovering via the breaker's
half-open probe. `/ready` returns 503 while degraded (liveness stays 200);
`/metrics` exposes per-dependency state (0/1/2) + trip/reset counters. On by
default but inert on the happy path; `CIRCUIT_BREAKER=false` is a
pure-passthrough escape hatch. Details: `docs/RESILIENCE.md`.

### Attachment content ingestion

With `ATTACHMENT_INGESTION=true` the connector downloads attachment binaries
(object types with an `attachmentUrlField` in `config/schema.json`), extracts
their text dependency-free (docx/xlsx/pptx via zip+XmlReader, PDF text-layer
best-effort, text/csv/html), and appends it to the attachment item's content —
so Copilot can ground on what the file says, not just its name. Size-capped
(`ATTACHMENT_MAX_BYTES`, default 10 MiB) and type-allowlisted
(`ATTACHMENT_ALLOWED_TYPES`); oversize/disallowed/scanned files skip to
metadata-only with a logged reason, an `AttachmentExtractionStatus` property,
and the `attachments_skipped_total` metric. The attachment item still inherits
its parent's ACLs, is inventoried, and is swept on deletion — enrichment is
purely additive. Off by default. Details: `docs/ATTACHMENTS.md`.

### Content gate — prompt injection & malware

Ingested content **is** Copilot grounding context, so a malicious document is an
attack on every user whose query it grounds. With `CONTENT_GATE=true` two
scanners run behind one stage: a config-driven prompt-injection heuristic
(`config/content-gate.json` — imperative overrides, role reassignment,
exfiltration directives, hidden zero-width/bidi text, smuggled base64) over the
**final indexed text**, and an ICAP/HTTP malware scanner
(`CONTENT_GATE_ICAP_URL`) over attachment **binaries**.

The posture is **quarantine, not drop**: a positive verdict withholds the item
from the index and routes it to the existing dead-letter queue with reason
`content-gate:<category>`, writes a `quarantine` decision-ledger entry, stamps
`ContentGateStatus`, increments `content_gate_blocked_total` and raises the
alert webhook. `retry-failed` re-drives it unchanged.

Fail modes are **deliberately asymmetric**: binaries **fail closed** (never
index unscanned bytes), text **fails open** with a loud warning and metric (the
injection scanner is a heuristic, not a security boundary — blocking a whole
crawl on a heuristic outage is worse than the risk). A regex timeout is an
incomplete scan, not an outage, so it fails **safe**. Off by default and
byte-identical to before when unset. Details: `docs/CONTENT_GATE.md`.

### Financial data governance

Fields listed in `financialFields` get `ContainsFinancialData=true` and
`DataClassification="financial"` properties; `FINANCIAL_DATA_MODE` picks the
policy — `tag` (default), `filter` (strip the values from ingested items), or
`acl` (restrict those items to the `FINANCIAL_DATA_GROUP_ID` Entra group via
item-level ACL, denies preserved).

### ACLs / identity

The identity sync loads Clarizen users, groups (+membership) and per-project
resource links, resolves users to Entra by email/UPN and persists the mapping
in the identity store (SQLite `data/{CONNECTOR_ID}_identity.db` by default).
Groups map through `CLARIZEN_GROUP_MAPPING` (or get expanded to member users so
access is never silently dropped). Records that resolve to zero principals are
**skipped**, never ingested world-readable — set `FALLBACK_ACL_GROUP_ID` to
grant a default group instead.

## Optional operational knobs (all off by default)

| Env var | Effect | Docs |
|---|---|---|
| `LOG_RETENTION_DAYS=N` | Prune `logs/{prefix}_{timestamp}/` run dirs older than N days; state files never touched. | `docs/OBSERVABILITY.md` |
| `GRAPH_RETRY_JITTER=true` | ±20% jitter on computed Graph backoff (server `Retry-After` never jittered; every wait clamped to 60 s). Recommended in HA. | `docs/RETRY.md` |
| `GRAPH_BATCH_WORKERS` / `GRAPH_CONCURRENT_BATCHES` | Concurrent Graph `$batch` workers (default 8); adaptive — 429s dial it toward 1, clean windows dial it back. `GRAPH_CONCURRENT_BATCHES` wins if both set. | `docs/RETRY.md` |
| `GRAPH_BATCH_SIZE` / `INGEST_GRAPH_BATCH_SIZE` | Requests per `$batch` POST (≤ 20, API cap). | |
| `GRAPH_CONNECTION_SHARDS={...}` | Shard object types across N Graph connections — the throughput lever. | `docs/SHARDING.md` |
| `DELETION_SYNC` (default **true**) + `DELETION_SYNC_MAX_PERCENT=25` + `DELETION_SYNC_MAX_ITEMS=1000` | Full-crawl existence sweep withdraws items deleted in Clarizen; the percent and absolute-cap knobs are the mass-deletion safety guards. | `docs/DELETION_SYNC.md` |
| `ATTACHMENT_INGESTION=true` (+ `ATTACHMENT_MAX_BYTES`, `ATTACHMENT_ALLOWED_TYPES`) | Download + extract attachment text (dependency-free) into the attachment item's content. | `docs/ATTACHMENTS.md` |
| `CONTENT_GATE=true` (+ `CONTENT_GATE_ICAP_URL`, `CONTENT_GATE_FAIL_MODE[_BINARY\|_TEXT]`, `CONTENT_GATE_MAX_SCAN_MB`) | Prompt-injection heuristics over indexed text + ICAP malware scanning of attachment binaries; positive verdicts quarantine to dead-letter (re-drivable). Binary fails closed, text fails open. | `docs/CONTENT_GATE.md` |
| `GRAPH_BASE_URL` (+ `GRAPH_SCOPE`, `AAD_APP_OAUTH_AUTHORITY_HOST`) | Sovereign-cloud Graph endpoint (e.g. `https://graph.microsoft.us`); scope defaults to `<base>/.default`; authority host moves the token endpoint. | |
| `USE_SQL_SERVER=true` + `SQL_CONNECTION_STRING` | Move state (identity store, checkpoints, sync ts, dead-letter) to SQL Server. | `docs/SQL_CONTRACT.md` |
| `SQL_USE_MANAGED_IDENTITY=true` / `SQL_MAX_RETRIES=5` | Entra auth for SQL; transient-fault retry (AG failover). | `docs/RETRY.md` |
| `HA_MODE=true` | Active-active multi-node crawling (requires SQL backend); failed object claims are terminal and never block the crawl close. | `docs/HA.md` |
| `USE_KEY_VAULT=true` + `KEY_VAULT_URI` | Resolve `SECRET_*` from Azure Key Vault. | |
| `HEALTH_PORT=N` | Serve `/health`, `/ready`, `/metrics` (Prometheus). | `docs/OBSERVABILITY.md` |
| `LOG_FORMAT=json` / `LOG_LEVEL` | Structured one-object-per-line logs; level floor for every sink (hot-path debug gating). | `docs/OBSERVABILITY.md` |
| `ALERT_WEBHOOK_URL` + `ALERT_DEADLETTER_THRESHOLD` | POST alerts on crawl failure / dead-letter growth. | `docs/OBSERVABILITY.md` |
| `IDENTITY_SYNC_ON_INCREMENTAL=true` | Identity sync on incremental cycles too. | |

## Running as a Windows service

The connector is SCM-aware: started by the Service Control Manager it runs
under a hosted-service lifetime automatically (the service binary path carries
the normal CLI arguments). Stopping the service is graceful: the in-flight
chunk finishes, the pending Graph batch is flushed, the checkpoint is saved,
and the next start resumes where it left off.

```powershell
# 1. Publish (.NET 10, win-x64)
dotnet publish src/ClarizenConnector -c Release -r win-x64 -o C:\ClarizenConnector

# 2. Lay out runtime files next to the exe
Copy-Item -Recurse config C:\ClarizenConnector\config
Copy-Item -Recurse env    C:\ClarizenConnector\env      # .env.local + .env.local.user

# 3. Install + start (elevated PowerShell)
.\scripts\install-windows-service.ps1 -InstallDir C:\ClarizenConnector
Start-Service ClarizenConnector
```

The script registers the service (Automatic start, restart-on-crash) with
`full-deployment --continuous --full-crawl-hours 24 --incremental-hours 4` by
default — pass `-Arguments` to change it, `-Uninstall` to remove. Relative
paths resolve against `CLARIZEN_CONNECTOR_HOME`, which the script points at the
install directory.

## Docker

A multi-stage image (`mcr.microsoft.com/dotnet/sdk:10.0` build →
`mcr.microsoft.com/dotnet/runtime:10.0` runtime, non-root) ships in
`Dockerfile`; `docker-compose.yml` brings up a local SQL Server 2022 +
schema-provisioning one-shot + the connector wired to the SQL state backend
(dev topology — real deployments point at a hardened SQL Server/AG listener).

The build context is the **repository root**, not the connector directory: the
project references `../Connector.Chassis` and a build cannot reach outside its
context. A single root `.dockerignore` governs the build; `docker-compose.yml`
sets `context: ..` accordingly.

```bash
docker build -f ClarizenConnector/Dockerfile -t clarizenconnector:latest .   # from the repo root
docker compose up --build           # from ClarizenConnector/: SQL + connector, continuous crawl
```

CI is the repository-root workflow `.github/workflows/clarizen.yml`: it builds
and tests the solution on ubuntu-latest and windows-latest (Windows Server is
the deployment target) and builds the Docker image with the repository root as
context. Releases run from the repository root too: pushing a `clarizen-v*` tag
starts `.github/workflows/release-clarizen.yml` — see
[Releasing](../README.md#releasing).

## SQL Server backend & high availability

```
USE_SQL_SERVER=true
SQL_CONNECTION_STRING=Server=<AG-listener>;Database=ClarizenConnector;...
HA_MODE=true            # optional: multi-node active-active
```

Provision once with `scripts/sql/create-database.sql`. With `HA_MODE=true`,
nodes running the same `--continuous` command coordinate through SQL: one opens
each scheduled crawl, all claim object types as leased work items with
heartbeats, a dead node's claims expire and survivors resume from its
checkpoint, and exactly one node closes the crawl and writes the sync
timestamp. Details: `docs/HA.md`; schema contract: `docs/SQL_CONTRACT.md`.

## Enterprise operations

The enterprise hardening package: threat analysis, incident runbooks, DR,
SIEM wiring, managed rollout, and the security policy — plus the operational
features they document (Windows Event Log sink via `EVENTLOG_ENABLED`,
`PROXY_URL`/`CA_BUNDLE_PATH` outbound transport control, certificate Graph
auth via `GRAPH_CLIENT_CERT_PATH`/`_THUMBPRINT`, and
`DEADLETTER_PAYLOAD_MODE=redacted` payload protection). Ready-made monitoring
lives in `ops/` (`grafana-dashboard.json`, `prometheus-alerts.yml`,
`azure-monitor-alerts.kql`); versions are tracked in `CHANGELOG.md`.

| Doc | Covers |
|---|---|
| [`docs/THREAT_MODEL.md`](docs/THREAT_MODEL.md) | STRIDE per trust boundary, FIPS audit result, least-privilege Graph permissions |
| [`docs/RUNBOOKS.md`](docs/RUNBOOKS.md) | per alert/failure mode: symptom → diagnose → remediate → escalate |
| [`docs/DR.md`](docs/DR.md) | RPO/RTO, backup/restore (files + SQL), upgrade/rollback, state-schema versioning |
| [`docs/SIEM.md`](docs/SIEM.md) | Event Log ids/levels, Sentinel KQL, Splunk sketch, fields to index |
| [`docs/DEPLOYMENT_ENTERPRISE.md`](docs/DEPLOYMENT_ENTERPRISE.md) | SCCM/Intune MSI rollout, GPO/DSC config, proxy/TLS inspection, FIPS, service-account least privilege |
| [`SECURITY.md`](SECURITY.md) | supported versions, credential rotation runbooks, vuln reporting, data-at-rest inventory |

## Tests

```bash
dotnet test
```

878 tests (green on ubuntu-latest and windows-latest): CLI parsing, checkpoint
round-trip/resume, dead-letter write/retry shape **and concurrency
invariants** (16 parallel writers, zero corrupt lines),
retry/backoff math (numeric Retry-After, 60 s clamp, jitter), Graph client
throttling/hardening (mock HTTP), adaptive concurrency, connection-sharding
validation + end-to-end per-shard routing, sovereign-cloud endpoint/scope/
authority override, Clarizen client (session auth, paging, re-login, quota),
ACL mapping (mapper + resolver modes), financial-field tagging/filtering/ACL
restriction, delta-cursor query building, TDW CSV/JSON parsing, SQLite identity
store, SQL state store + HA coordinator incl. close-with-failed-claims
semantics (fake gateway), offline SQL script validation (ScriptDom grammar
parse, idempotency-by-construction, C#⇄schema drift, DacFx semantic binding),
hot-path log gating, worker-crash resilience, ingested-item inventory
(SQLite + SQL Server), deletion sweeps incl. the mass-deletion safety guard,
reconcile drift reporting/repair (sharding-aware), dependency-free content
extraction (docx/xlsx/pptx/pdf/text/csv/html), attachment enrichment
(size cap, type allowlist, download failure, ACL inheritance, inventory,
metrics, on/off toggle), webhook subsystem (HMAC signature accept/reject/
missing, event parse + field aliases, debounce/coalesce last-writer-wins,
receiver end-to-end over loopback HTTP incl. fail-closed missing-secret and
disabled toggle, processor upsert/delete/record-gone routing), OpenTelemetry
tracing (in-memory ActivityListener: parent/child spans + tags, correlation id
on logs/dead-letter/spans stable within a cycle, overhead-when-off = null spans,
OTEL env-var config, collector-unreachable never throws), circuit breakers
(state transitions closed→open→half-open→closed, window sampling, 5xx/timeout
trip vs 4xx/429 ignored, concurrency-safe counting, disabled passthrough,
client fail-fast, degraded-mode pause-at-checkpoint + clean resume with no
state loss, /ready flips while /health stays up), health
endpoint, alerting,
metrics, log pruning, env loading, Key Vault secret resolution, and an
end-to-end pipeline run against mocked Clarizen + Graph. No test touches the
network. Collections run serially (`xunit.runner.json`) because several tests
swap process-global seams (state paths, env vars).

## Deliberate scope notes

- **Attachments:** metadata always; full text content is opt-in
  (`ATTACHMENT_INGESTION=true`, `docs/ATTACHMENTS.md`). Scanned/image-only PDFs
  are not OCR'd — they stay metadata-only.
- **Deletion detection latency:** removals are withdrawn immediately by a
  webhook `delete` event when the receiver is enabled, otherwise by the next
  FULL crawl's existence sweep (or `reconcile --fix` on demand); the polling
  delta cursor itself has no tombstone feed. See `docs/DELETION_SYNC.md` (incl.
  the bootstrap note for pre-inventory deployments) and `docs/WEBHOOKS.md`.
- **Webhooks** are an accelerator, not a replacement — the polling cursor stays
  the correctness backstop and reconciles anything the receiver misses. The
  receiver terminates plain HTTP (put TLS at your ingress) and reuses the last
  full crawl's identity snapshot for upsert ACLs.
- **Tracing** exports only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set; metrics/
  logs are always available. Spans cover pipeline stages, not every HTTP call —
  the correlation id ties a run together across logs, dead-letter and traces.
- **Circuit breakers** are on by default but inert on the happy path; they trip
  only on sustained real failures, never on 4xx/429. Degraded mode reuses the
  checkpoint/resume path, so a paused crawl loses no state. `CIRCUIT_BREAKER=false`
  restores byte-identical unbreakered behaviour. See `docs/RESILIENCE.md`.
- The Clarizen entity/field names in `config/schema.json` are a sensible
  default set; adjust to your org's schema (custom fields fully supported by
  the config-driven mapping).
