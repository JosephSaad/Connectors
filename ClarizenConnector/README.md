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
backends, Key Vault, health/metrics/alerting, Windows-service mode) — carried
here as an independent, self-contained copy.

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
| `src/ClarizenConnector/Infrastructure/` | logging, metrics, health endpoint, alerting, Key Vault secrets, env loading, log pruning, Windows-service host, SQL executor, HA coordinator, OpenTelemetry tracing + correlation ids, circuit breakers + degraded mode |
| `config/` | `schema.json` (Clarizen object list, fields, financial fields, ACL modes), `graph-schema.json` (connection schema) |
| `env/.env.local.example` | every knob, documented |
| `docs/` | `HA.md`, `RETRY.md`, `OBSERVABILITY.md`, `SQL_CONTRACT.md`, `SHARDING.md`, `DELETION_SYNC.md`, `ATTACHMENTS.md`, `WEBHOOKS.md`, `TRACING.md`, `RESILIENCE.md` |
| `scripts/` | `install-windows-service.ps1`, `sql/create-database.sql` |
| `tests/ClarizenConnector.Tests/` | xUnit suite (491 tests, mock HTTP — no network) |
| `Dockerfile` / `docker-compose.yml` | container image + local SQL/HA dev topology |
| `.github/workflows/` | `ci.yml` (ubuntu + windows, SQL provisioning, docker), `codeql.yml`, `release.yml` (test-gated, checksummed bundles + GHCR image) |

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

## Usage

Run from the repository root (`logs/`, `data/`, `config/`, `env/` resolve
against the current directory):

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
(dev topology — real deployments point at a hardened SQL Server/AG listener):

```bash
docker build -t clarizenconnector:latest .
docker compose up --build           # SQL + connector, continuous crawl
```

CI (`.github/workflows/ci.yml`) builds + tests on ubuntu and windows (Windows
Server is the deployment target), provisions the SQL schema twice against a
live SQL Server 2022 (proving the idempotent re-run path), and validates the
Docker image build. `release.yml` is test-gated and publishes checksummed
self-contained bundles (win-x64/linux-x64) plus a GHCR container image on
`v*` tags; `codeql.yml` runs the security-and-quality suite weekly and on
every push/PR.

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

## Tests

```bash
dotnet test
```

491 tests: CLI parsing, checkpoint round-trip/resume, dead-letter write/retry
shape **and concurrency invariants** (16 parallel writers, zero corrupt lines),
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
