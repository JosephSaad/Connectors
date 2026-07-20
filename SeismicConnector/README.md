# Seismic Copilot Connector (C#)

A standalone .NET 10 Microsoft 365 Copilot **Graph connector for Seismic**
(sales enablement content). It crawls Seismic libraries/teamsites, indexes the
**current published version** of every content item with per-item ACLs, and
enforces a config-driven **"No MNE"** compliance filter: material-nonpublic-event
content is never ingested, and late-flagged content is withdrawn automatically —
with an auditable reconciliation report per crawl.

## Layout

```
SeismicConnector.sln
config/
  schema.json            objects to ingest (ContentItem, Library)
  graph-schema.json      Graph external connection schema (searchable/refinable props)
  exclusions.json        the No-MNE rule set (docs/EXCLUSIONS.md)
docs/
  EXCLUSIONS.md  RETRY.md  HA.md  OBSERVABILITY.md  SQL_CONTRACT.md  SHARDING.md  RESILIENCE.md
env/
  .env.local.example     every knob, documented (copy to .env.local / .env.local.user)
scripts/
  install-windows-service.ps1
  sql/create-database.sql       idempotent schema provisioning (validated offline + live in CI)
  sql/create-login.sql          least-privilege app login
Dockerfile / docker-compose.yml container image + local SQL-backed dev topology
.github/workflows/              ci.yml (build/test on ubuntu+windows, live SQL
                                idempotency, docker validate), codeql.yml,
                                release.yml (test-gated, checksummed bundles + GHCR image)
src/SeismicConnector/
  Program.cs             unified CLI entry point (SCM-aware)
  Dashboard.cs           Spectre.Console live dashboard (--continuous)
  Commands/              guide, setup-connection, full-deployment, ingest,
                         ingest-object, ingest-item, retry-failed,
                         identity-dry-run, validate-config
  Config/                AppConfig (env layering), SyncState (checkpoints,
                         sync cursor, dead-letter), SqlStateStore
  Graph/                 GraphClient (retry/backoff/$batch), Connection,
                         Ingest pipeline, IdentityStore (SQLite/SQL Server),
                         IdentitySync, RetryDelay
  Infrastructure/        Logging, Metrics, Alerting, HealthEndpoint,
                         SecretProvider (Key Vault), SqlExecutor, HaCoordinator,
                         LogPruner, ServiceHost/ServiceStop
  Seismic/               SeismicClient (OAuth2 CC), Models, ExclusionFilter,
                         ReconciliationReport, AclMapper, ContentExtractor,
                         ItemTransformer, WebhookReceiver, Settings,
                         ShardingConfig (GRAPH_CONNECTION_SHARDS)
tests/SeismicConnector.Tests/   xUnit suite (no network; mocked HTTP)
```

Runtime state lands next to the executable: `logs/` (run logs, checkpoints,
`sync_state.json`, `failed_records_{CONNECTOR_ID}.jsonl`, reconciliation
reports) and `data/{CONNECTOR_ID}_identity.db` (SQLite identity + tracked-item
store).

## Requirements

* .NET 10 SDK (build) / runtime (run). Windows Server 2019+ for service mode;
  builds and runs cross-platform for development.
* A Seismic OAuth2 client-credentials API client (library/teamsite/user read scopes).
* An Entra app registration with application permissions
  `ExternalConnection.ReadWrite.OwnedBy`, `ExternalItem.ReadWrite.OwnedBy`,
  plus `User.Read.All` and `Group.Read.All` for identity mapping (admin-consented).

## Configuration

1. `cp env/.env.local.example env/.env.local` and fill in the non-secret values.
2. Put `SECRET_SEISMIC_CLIENT_SECRET` and `SECRET_AAD_APP_CLIENT_SECRET` in
   `env/.env.local.user` — or set `USE_KEY_VAULT=true` + `KEY_VAULT_URI`.
3. Review `config/exclusions.json` (restricted libraries, MNE flags) and
   `config/graph-schema.json`.

Process environment always beats the env files, so container/service env
blocks can override anything.

## Usage

```bash
dotnet run --project src/SeismicConnector -- guide
dotnet run --project src/SeismicConnector -- validate-config [--strict]
dotnet run --project src/SeismicConnector -- identity-dry-run [--save] --verbose
dotnet run --project src/SeismicConnector -- setup-connection
dotnet run --project src/SeismicConnector -- full-deployment
dotnet run --project src/SeismicConnector -- full-deployment --continuous \
    --full-crawl-hours 24 --incremental-hours 4
dotnet run --project src/SeismicConnector -- ingest [--continuous]
dotnet run --project src/SeismicConnector -- ingest-object --type ContentItem
dotnet run --project src/SeismicConnector -- ingest-item --id <contentId> [--teamsite <id>]
dotnet run --project src/SeismicConnector -- retry-failed [--file <jsonl>] [--clear-on-success]
dotnet run --project src/SeismicConnector -- reconcile [--repair]
dotnet run --project src/SeismicConnector -- reacl [--dry-run]
```

`--verbose` (any command) prints INFO logs to the console; the run log file
always captures everything.

### What a crawl does

1. **Full crawl** (first run / every `--full-crawl-hours`): identity sync,
   Library metadata items, every teamsite's published content, then a
   withdrawal pass (expired, deleted/unpublished, late-excluded items).
2. **Incremental crawl** (every `--incremental-hours`): only content with
   `modifiedAt` ≥ the last successful sync; expiry withdrawals still run, and
   a **late-exclusion pass** re-checks tracked items the incremental did NOT
   re-list against the current exclusion rules (metadata-only re-list, no
   downloads) — so a compliance flag applied *without* bumping `modifiedAt`
   is withdrawn on the next incremental, not deferred to the next full crawl.
3. **Webhook events** (`SEISMIC_WEBHOOK_PORT` > 0): near-real-time targeted
   ingest/withdrawal between cycles; polling remains the safety net. The
   HMAC-authenticated receiver caps the in-memory event queue (drop-oldest at
   10,000 undrained events), so a signed burst can't grow memory unbounded
   until the ~15 s drain — any shed event is healed by the next crawl.
4. Chunks are checkpointed — a crash or graceful stop resumes at the first
   incomplete chunk (`logs/checkpoint_{CONNECTOR_ID}.json`).

### Version awareness

The externalItem id is the Seismic content id. Only the current published
version is indexed; when a new version supersedes, the same externalItem is
updated in place. Unpublished/deleted content is withdrawn (DELETE), and
content whose expiry date passes is withdrawn automatically.

### Content text extraction

`Seismic/ContentExtractor.cs` is a pluggable `IContentExtractor` pipeline.
The shipped extractors are dependency-free by design: plain-text/HTML; DOCX,
PPTX (incl. slide notes) and XLSX (shared + inline strings) via OOXML zip
parsing; and a best-effort PDF text-layer parser covering `Tj`/`'`/`"`/`TJ`
show-text operators with literal strings (octal + symbol escapes, line
continuations) and hex strings, including UTF-16BE payloads. Scanned PDFs and
exotic encodings fall back to metadata-only indexing (name + description).
Payloads above `SEISMIC_MAX_EXTRACT_MB` (default 10 MB) are metadata-only.
Decompression is bounded **during** extraction (32 MB per inflated
PDF/OOXML stream, a 64 MB *aggregate* ceiling across all streams/parts of one
document, and 1M chars of accumulated text), so a high-ratio decompression
bomb inside an allowed-size download is truncated instead of inflating
unboundedly into memory — including a payload packed with many high-ratio
units that each emit no text (the aggregate ceiling stops it once cumulative
inflation crosses 64 MB). The PDF show-text operator scans additionally carry
a per-scan regex match timeout (default 2 s), so a hostile operator stream
cannot stall the serial crawl worker; on timeout the text gathered so far is
kept and extraction moves on (best-effort).
Per-format success/attempt counters are exported on `/metrics`
(`docs/OBSERVABILITY.md`). Swap in a richer library by adding an extractor to
`CompositeExtractor`.

### Engagement ranking (SEISMIC_ENRICH_USAGE)

With `SEISMIC_ENRICH_USAGE=true` each crawl pulls Seismic usage analytics
once and indexes `viewCount`, `downloadCount`, `shareCount` and a combined
`popularityScore` as refinable properties, so Copilot can prefer collateral
the field actually uses. Best-effort: an analytics failure logs a warning and
items index without the signals.

### LiveDoc field indexing (LIVEDOC_FIELD_INDEXING)

Seismic LiveDocs are document *templates* personalized at generation time by a
set of fields/variables (e.g. "client name", "product", "region"). With
`LIVEDOC_FIELD_INDEXING=true` the connector fetches those inputs for LiveDoc
content and indexes them: `isLiveDoc` (bool), `liveDocFieldNames`
(searchable/refinable string collection) and `liveDocFieldCount`, plus the
field labels appended to the content text — so a template becomes findable by
the inputs it exposes ("the deck parameterized by region"). The extra API
call is gated to LiveDoc items only, and only when enabled, so non-LiveDoc
content and disabled config incur zero new calls. Best-effort: a field-fetch
failure logs a warning and the item indexes without the metadata.
Per-item outcomes are counted on `/metrics`
(`extraction_{attempts,success}_total{format="livedoc-fields"}`).

### Drift reconciliation (`reconcile`)

Event-driven withdrawal can miss edges (events dropped while the connector
was down, state restored from backup, exclusion rules edited between crawls).
`reconcile` diffs the FULL Seismic inventory against the index and reports
every divergence — `orphaned-in-index`, `excluded-drift`, `expired-drift`,
`version-drift`, `missing-from-index` — to an auditable
`drift_report_*.jsonl` (findings + summary). `--repair` converges the index
(withdrawals + re-ingests). Exit code 1 when unrepaired drift remains, and an
`index_drift` webhook alert fires, so it slots straight into a scheduled
compliance job alongside the No-MNE reconciliation report.

### Permission-change re-ACL (`reacl`, PERMISSION_REACL)

An item's ACL used to be refreshed only when its content changed. If a
teamsite/content-profile permission changes but the content doesn't, the
indexed ACL goes stale — someone keeps or loses access incorrectly. The
connector now tracks a per-item **ACL fingerprint** (a stable hash of the
resolved Entra principal set) alongside the inventory. With
`PERMISSION_REACL=true`, every crawl re-resolves permissions for content whose
payload is unchanged and, when the fingerprint drifted, refreshes the ACL via
an **ACL-only Graph PATCH — the content is never re-sent**. The
`reacl [--dry-run]` command runs the same audit/repair on demand over the full
inventory regardless of the flag (`--dry-run` reports drift counts and writes
`reacl_report_*.jsonl` without changing anything; a genuine dry-run drift
exits 1). Compliance-safe throughout: if an item's permissions can't be
resolved — including when the source has principals but none currently maps
and `SEISMIC_FALLBACK_ACL=tenant` would otherwise hand back the
tenant-everyone fallback — its ACL is **left unchanged, never widened**, and
the event is logged. Metrics `items_reacled_total` / `acl_drift_detected_total` and an
`acl_drift` webhook alert surface the activity.

### ACLs

Seismic item permissions are resolved to Entra principals via the identity
store — users by email/UPN, groups by display name. Items with **no
item-level permissions inherit the teamsite's permissions** (read from the
teamsites listing); Library metadata items always carry the teamsite ACL.
Distribution restrictions (`internal-only` vs `client-approved`) are indexed
as a refinable `distribution` property, not as ACLs. When nothing maps,
`SEISMIC_FALLBACK_ACL` decides: `skip` (default, compliance-safe) or
`tenant`. The `tenant` fallback only ever applies to content that is
**genuinely without principals**: when the source *has* principals but none
currently maps (a stale/partial identity store), the item is **left unchanged
for that crawl** and re-resolved once identity recovers. This holds
regardless of the item's state — a previously indexed item keeps its ACL and
content, and a **brand-new or Library item is not ingested with the
everyone-ACL** (it is not "genuinely public", so the tenant fallback must not
stand in for a real resolution). Under `skip` the same guard means an
already-ingested item that transiently goes unresolved is **left in place**
rather than withdrawn-and-re-ingested when identity recovers (no churn). The
connector never widens an existing ACL to tenant-everyone on a transient
identity gap, neither on re-ACL nor on a version-change re-ingest. Run
`identity-dry-run` to audit the mapping before deploying.

## Running as a Windows service

```powershell
# 1. Publish
dotnet publish src/SeismicConnector -c Release -r win-x64 --self-contained false -o C:\SeismicConnector

# 2. Lay out runtime files next to the exe
#      C:\SeismicConnector\config\   schema.json, graph-schema.json, exclusions.json
#      C:\SeismicConnector\env\      .env.local, .env.local.user

# 3. Install + start (elevated PowerShell)
.\scripts\install-windows-service.ps1 -InstallDir "C:\SeismicConnector" `
    -Arguments "full-deployment --continuous --full-crawl-hours 24 --incremental-hours 4"
Start-Service SeismicConnector
```

The service resolves `config/`, `env/`, `logs/`, `data/` against
`SEISMIC_CONNECTOR_HOME` (set by the installer). **SCM stop is graceful**: the
in-flight chunk finishes, the pending Graph batch flushes, the checkpoint is
saved, then the process exits — identical to Ctrl+C in a console.

## SQL Server backend & high availability

`USE_SQL_SERVER=true` moves all state (identity store, tracked items,
checkpoints, sync cursor, dead-letter queue) into a shared SQL Server database
(`docs/SQL_CONTRACT.md`); provision with `scripts/sql/create-database.sql`
(idempotent — CI proves the re-run path live) or let the connector
auto-provision. `HA_MODE=true` (SQL required) adds active-active multi-node
crawling: nodes open/join a shared crawl session, claim teamsites with
heartbeats and stale-claim takeover, and the crawl **closes even when some
claims failed** (session status `failed`; exactly one node records sync
state — `docs/HA.md`). Enable `GRAPH_RETRY_JITTER=true` on every HA node
(`docs/RETRY.md`). `docker-compose.yml` spins up the full SQL-backed topology
locally.

## Throughput & sharding

`GRAPH_BATCH_WORKERS` (alias `GRAPH_CONCURRENT_BATCHES`, which wins) caps the
concurrent `$batch` workers; the live count adapts 1..max on 429 feedback.
`INGEST_GRAPH_BATCH_SIZE` (alias `GRAPH_BATCH_SIZE`) sets requests per
envelope (API cap 20). Throttled items are re-sent in shrinking retry rounds
that honour the per-item Retry-After (`docs/RETRY.md`). For rates beyond one
connection's quota, `GRAPH_CONNECTION_SHARDS` splits the schema objects
across multiple Graph connections (`docs/SHARDING.md`). Sovereign clouds:
`GRAPH_BASE_URL`, `GRAPH_SCOPE`, `GRAPH_API_VERSION` and
`AAD_APP_OAUTH_AUTHORITY_HOST` are read live from the environment.

## Observability

`HEALTH_PORT` serves `/health`, `/ready` and Prometheus `/metrics`;
`LOG_FORMAT=json` for structured logs; `LOG_RETENTION_DAYS` prunes old run
dirs; `ALERT_WEBHOOK_URL` + `ALERT_DEADLETTER_THRESHOLD` for webhook alerts.

**Distributed tracing & correlation IDs.** The connector is an OpenTelemetry
trace source. A correlation id is minted per crawl cycle (the W3C trace id when
tracing is on) and stamped on every JSON log line, dead-letter record and
reconciliation/reacl report entry — so a crawl is followable end to end by one
id, always, at near-zero cost. OTLP span **export** is opt-in and gated on
`OTEL_EXPORTER_OTLP_ENDPOINT`: unset means no `TracerProvider` is registered
and overhead is unchanged; set means spans (`crawl.cycle` → teamsite → fetch /
extract / transform / graph batch, plus the reconcile/reacl/webhook stages,
with `traceparent` propagated on outbound HTTP) are batched to the collector on
a background thread — a broken collector never fails or stalls a crawl.
`OTEL_SERVICE_NAME` defaults to the connector name; `/metrics` and
`validate-config` report the tracing state. See `docs/OBSERVABILITY.md`.

**Circuit breakers & degraded mode.** Each external dependency (Seismic API,
Microsoft Graph) sits behind its own circuit breaker. Distinct from
retry/backoff (transient blips), a breaker handles a *sustained* outage by
failing fast: after `CIRCUIT_BREAKER_FAILURE_THRESHOLD` real failures
(5xx/timeout/connection — 4xx and honored 429 do NOT trip) it opens and
short-circuits calls, and the crawl **pauses into degraded mode at a safe
checkpoint boundary** (finish/flush the in-flight batch, save checkpoint) with
no state loss, resuming automatically once a half-open probe finds the
dependency healthy. `/ready` returns 503 while a critical breaker is open
(liveness stays green); `/metrics` exposes per-dependency state and trip/reset
counters. Enabled by default and inert on the happy path;
`CIRCUIT_BREAKER=false` is a passthrough escape hatch. See
`docs/RESILIENCE.md`.

## Enterprise operations

The enterprise hardening pack — Windows Event Log mirroring
(`EVENTLOG_ENABLED`), proxy/TLS-inspection support (`PROXY_URL`,
`CA_BUNDLE_PATH`), Graph certificate credentials (`GRAPH_CLIENT_CERT_*`),
dead-letter payload redaction (`DEADLETTER_PAYLOAD_MODE`), signed + SBOM'd
releases, and the `ops/` dashboard/alert pack — is documented operator-first:

* [docs/CONTENT_GATE.md](docs/CONTENT_GATE.md) — malware + prompt-injection scanning of grounding content, quarantine posture, fail-mode asymmetry (`CONTENT_GATE`, default off)
* [docs/THREAT_MODEL.md](docs/THREAT_MODEL.md) — STRIDE per trust boundary, mitigations vs. residuals, FIPS audit result
* [docs/RUNBOOKS.md](docs/RUNBOOKS.md) — symptom → diagnose → remediate → escalate, per failure mode
* [docs/DR.md](docs/DR.md) — RPO/RTO, backup/restore, upgrade/rollback, schema versioning
* [docs/SIEM.md](docs/SIEM.md) — Event Log ids, Sentinel KQL + Splunk detections, index fields
* [docs/DEPLOYMENT_ENTERPRISE.md](docs/DEPLOYMENT_ENTERPRISE.md) — SCCM/Intune, GPO/DSC, proxy/TLS inspection, FIPS, least-privilege service account
* [SECURITY.md](SECURITY.md) — supported versions, credential rotation runbooks, vulnerability reporting, data-at-rest inventory

Grafana dashboard and Prometheus / Azure Monitor alert rules (wired to the
runbook anchors) live in [`ops/`](ops/).

## Tests

```bash
dotnet build
dotnet test
```

1017 xUnit tests, all offline (mock `HttpMessageHandler`, temp SQLite/state
dirs): CLI parsing, checkpoint resume, dead-letter write/retry and integrity
under concurrent writers, the Retry-After + jitter contract, the $batch
429/503 retry ladder and adaptive concurrency, sovereign-cloud endpoint
override, throughput knob aliases, connection-sharding validation and
routing, ACL mapping and fallback, the No-MNE filter (flag / library /
property rules, late-flag and library-level withdrawal), version supersede,
expiry and unpublish withdrawal, reconciliation report output, SQLite
identity store, extractors (PDF operator/encoding coverage, XLSX,
per-format metrics), usage enrichment, LiveDoc field/variable indexing
(detection, field→property mapping, content weaving, call gating and
failure resilience), the drift reconciliation sweep, permission-change re-ACL detection (ACL
fingerprint stability, drift detect/no-op, ACL-only PATCH without content
re-send, dry-run non-mutation, unresolved-never-widens, on/off toggle),
webhook parsing, the HA claim/steal decision and
the pinned close-with-failed-claims semantics, OpenTelemetry tracing
(span parent/child + tags via an in-memory ActivityListener, correlation ids
on logs/dead-letter/reports/spans stable within a cycle, the disabled path
proven inert, OTEL env parsing, and collector-unreachable never throwing), circuit breakers &
degraded mode (full state-machine transitions with an injected clock,
5xx/timeout trip but 4xx/429 do not, thread-safety, a real breaker failing
fast after a sustained outage, degraded-mode pause-at-checkpoint + resume with
no loss, readiness flip, and the disabled=passthrough escape hatch),
plus the offline SQL
validation suite (real T-SQL grammar parse, idempotency-by-construction,
code-DDL ⇄ script ⇄ contract-doc drift checks, DacFx semantic model).
