# BDH Hadoop Copilot Connector (C# / .NET 10)

A standalone Microsoft 365 Copilot **Graph connector for BDH**, a Hadoop data
mart holding **Salesforce data synchronized nightly**: it scans BDH's
Hive-partitioned CSV/JSONL exports over WebHDFS (or a mounted export
directory), prunes and filters its 150M+ rows down to the records worth
indexing, resolves coarse per-record ACLs from record ownership mapped to
Entra ID identities, and ingests everything as `externalItem`s into a
Microsoft Graph external connection.

Architecture and operational features mirror the Salesforce Copilot Connector
(same chassis: unified CLI, checkpointed crawls, dead-letter + retry, SQL/HA
backends, Key Vault, health/metrics/alerting, Windows-service mode) — carried
here as an independent, self-contained copy.

## Why BDH? The cheap-path trade-off

Reading the live Salesforce org is expensive: API capacity is metered and
shared with every other integration. BDH mirrors Salesforce into Hadoop every
night, so this connector indexes the same records **without spending a single
Salesforce API call** — at the cost of freshness and ACL fidelity:

| | Live Salesforce connector | This connector (BDH) |
|---|---|---|
| Source cost | Salesforce API calls (metered, shared) | HDFS reads (effectively free) |
| Freshness | near-live | up to **24 h behind** (nightly load); every item carries a `DataAsOf` refinable property |
| ACL fidelity | full sharing model (role hierarchy, sharing rules, teams) | **coarse by design**: `ownerOnly` \| `group` \| `public` per object — BDH mirrors the data but **not** the Salesforce sharing tables |
| Scale control | delta cursors on the API | config-driven filter layer over 150M+ rows (partition pruning + record predicates + row cap) |
| Item ids | Salesforce record Id | **the same Salesforce record Id** — the two connectors are swappable |

> **WARNING — use a SEPARATE Graph connection id.** Both connectors emit the
> same external item ids (the Salesforce record Id). Never point this
> connector and the live Salesforce connector at the same Graph connection:
> their crawls and deletion sweeps would fight each other. Give each its own
> `CONNECTOR_ID`.

## Layout

| Path | Contents |
|---|---|
| `src/HadoopConnector/Hdfs/` | BDH source access: WebHDFS REST client (LISTSTATUS/OPEN, retry ladder, breaker), local-path source, Hive partition scanner (dt watermark + partition-filter pruning), hardened streaming CSV/JSONL parser (bounded reads), fetcher (fail-closed scale guard, row cap) |
| `src/HadoopConnector/Filters/` | the filter layer: `config/filters.json` models + strict loader, predicate engine (partition keys + streamed record predicates) |
| `src/HadoopConnector/Graph/` | Graph client (retry/backoff/jitter), connection + schema provisioning, ingest pipeline ($batch, checkpoints, dead-letter, deletion sweep), reconciler, identity stores (SQLite / SQL Server), item inventory |
| `src/HadoopConnector/AclEngine/` | ownerOnly/group/public ACL resolver, Salesforce-user → Entra principal mapper, identity sync from the BDH User export |
| `src/HadoopConnector/Item/` | record → externalItem conversion (SourceSystem/DataAsOf freshness properties), sensitivity classifier, classification manifest |
| `src/HadoopConnector/Content/` | dependency-free content classifier (PII/PCI+Luhn/Secret regex set, timeout-bounded) |
| `src/HadoopConnector/Config/` | env config, `schema.json` models, filters path, sync state (files or SQL) |
| `src/HadoopConnector/Commands/` + `Program.cs` | CLI |
| `src/HadoopConnector/Infrastructure/` | logging, metrics, health endpoint, alerting, Key Vault secrets, env loading, log pruning, Windows-service host, SQL executor, HA coordinator, OpenTelemetry tracing + correlation ids, circuit breakers + degraded mode |
| `config/` | `schema.json` (BDH object list, field → property map, ACL modes, sensitivity defaults), `filters.json` (**the scale control**), `graph-schema.json` (connection schema), `classification.json` (classifier patterns) |
| `env/.env.local.example` | every knob, documented |
| `docs/` | `FILTERS.md`, `HA.md`, `RETRY.md`, `OBSERVABILITY.md`, `SQL_CONTRACT.md`, `SHARDING.md`, `DELETION_SYNC.md`, `TRACING.md`, `RESILIENCE.md`, `CLASSIFICATION.md` |
| `scripts/` | `install-windows-service.ps1`, `sql/create-database.sql` |
| `tests/HadoopConnector.Tests/` | xUnit suite (mock HTTP / temp dirs — no network) |
| `Dockerfile` / `docker-compose.yml` | container image + local SQL/HA dev topology |
| `.github/workflows/` | `ci.yml`, `codeql.yml`, `release.yml` |

## Requirements

- .NET 10 SDK (build) / .NET 10 runtime on the crawl host. Deployment target is
  Windows Server (service mode); the code is cross-platform for dev/test.
- Entra app registration with application permissions
  `ExternalConnection.ReadWrite.OwnedBy`, `ExternalItem.ReadWrite.OwnedBy`,
  `User.Read.All` (admin-consented).
- Access to the BDH cluster: a WebHDFS endpoint (`HDFS_NAMENODE_URL`, simple
  auth via `HDFS_USER` and/or an out-of-band delegation token) **or** a
  local/SMB-mounted export directory (`HDFS_MODE=localpath` +
  `BDH_EXPORT_PATH`). Kerberos/SPNEGO is out of scope — front a kerberized
  cluster with an Apache Knox gateway and point `HDFS_NAMENODE_URL` at it.

## Configure

```bash
cp env/.env.local.example env/.env.local     # non-secret config
# put SECRET_AAD_APP_CLIENT_SECRET (and, if used,
# SECRET_HDFS_DELEGATION_TOKEN) into env/.env.local.user (never committed)
```

Review the three config files:

- `config/schema.json` — which Salesforce-shaped BDH objects are crawled,
  BDH column → Graph property mapping, the ACL mode per object
  (`ownerOnly` | `group` | `public`, plus `ownerField`/`ownerEmailField` or
  `aclGroupId`), the optional `sourcePath` under the BDH root, and the
  per-object `sensitivityDefault` classification floor.
- `config/filters.json` — **the scale control** (`docs/FILTERS.md`): partition
  pruning + record predicates per object. An object with no filter refuses to
  crawl (fail-closed) unless explicitly exempted.
- `config/graph-schema.json` — must contain every Graph property the object
  list produces (including `SourceSystem`, `DataAsOf` and, when classification
  is on, `SensitivityLabel`/`DetectedCategories`).

## Usage

Run from the repository root (`logs/`, `data/`, `config/`, `env/` resolve
against the current directory):

```bash
dotnet run --project src/HadoopConnector -- guide
dotnet run --project src/HadoopConnector -- validate-config --strict
dotnet run --project src/HadoopConnector -- identity-dry-run --save --verbose
dotnet run --project src/HadoopConnector -- setup-connection
dotnet run --project src/HadoopConnector -- full-deployment
dotnet run --project src/HadoopConnector -- full-deployment --continuous --full-crawl-hours 24 --incremental-hours 4
dotnet run --project src/HadoopConnector -- ingest --verbose
dotnet run --project src/HadoopConnector -- ingest-object --type Contact
dotnet run --project src/HadoopConnector -- ingest-item --id 0035e00000abcde --object-type Contact
dotnet run --project src/HadoopConnector -- retry-failed --clear-on-success
dotnet run --project src/HadoopConnector -- reconcile
dotnet run --project src/HadoopConnector -- reconcile --type Contact --fix
```

| Command | Purpose |
|---|---|
| `guide` | Print the end-to-end setup and usage guide. |
| `validate-config [--strict]` | Preflight: env vars, config files, filter sanity, shard map, connectivity. `--strict` fails on warnings (incl. unfiltered objects) and requires BDH + Graph connectivity. |
| `identity-dry-run [--save]` | Resolve BDH (Salesforce) users to Entra identities and report; `--save` persists to the identity store. |
| `setup-connection` | Create/verify the Graph external connection(s) and register the schema (no ingestion). |
| `full-deployment [--continuous ...]` | Connection → schema → identity sync → full crawl (optionally scheduled full + incremental cycles). |
| `ingest [--continuous ...]` | Content crawl only (connection & schema must exist). |
| `ingest-object --type X` | Ingest all records of one BDH object type. |
| `ingest-item --id X --object-type Y` | Ingest a single record by Salesforce id (newest-first partition lookup). |
| `retry-failed [--file path] [--clear-on-success]` | Re-ingest dead-lettered records (each is re-located in BDH for fresh fields + fresh ACL). |
| `reconcile [--type X] [--fix]` | Index-vs-source drift report; `--fix` deletes stale items. |

Failed items land in `logs/failed_records_<CONNECTOR_ID>.jsonl` (or
`dbo.DeadLetter` on SQL Server) for `retry-failed`. Crawls checkpoint per
object type per chunk — a crash, Ctrl+C or service stop resumes exactly where
it stopped.

### The filter layer (signature feature)

BDH holds 150M+ rows; indexing all of it is neither possible nor useful. The
filter layer (`config/filters.json`, `docs/FILTERS.md`) cuts the crawl down in
three stages, in order:

1. **Partition pruning — zero I/O.** Predicates on Hive partition keys
   (`dt=...`, `region=...`) are evaluated on directory names during the
   partition walk; a pruned directory is never listed further and none of its
   files are opened.
2. **Streamed record predicates.** Rows are parsed one at a time (never
   materialized per file) and evaluated against an OR of AND-groups; rejected
   rows never reach ACL resolution or Graph.
3. **Row cap** (`BDH_MAX_RECORDS_PER_OBJECT`, default 500 000) — a safety
   valve, never a silent truncation: hitting it logs a warning, raises a
   `row_cap_hit` alert and marks the crawl **partial** for that object (its
   deletion sweep is skipped).

```jsonc
{
  "objects": {
    "Opportunity": {
      "partition": [
        { "key": "dt", "op": "withinLastDays", "value": "450" }
      ],
      "anyOf": [
        { "allOf": [ { "field": "StageName", "op": "notIn", "values": ["Closed Lost"] } ] },
        { "allOf": [
            { "field": "StageName", "op": "equals", "value": "Closed Lost" },
            { "field": "CloseDate", "op": "withinLastDays", "value": "180" }
        ] }
      ]
    }
  },
  "fullScanAllowed": []
}
```

**Fail-closed guard:** an object with *no* filter refuses to crawl
(`FullScanRefusedException`) unless it is listed under `fullScanAllowed` or
`ALLOW_FULL_SCAN=true` is set — at this scale an accidental unfiltered scan is
an outage. `validate-config --strict` catches unfiltered objects before
deployment. Per-stage counts (partitions scanned/pruned, records
scanned/filtered/matched) are logged per object and exported on `/metrics`.

### Partition layout, freshness & the incremental watermark

BDH lays every object out Hive-style under the data-mart root:

```
{BDH_ROOT_PATH}/{object}/dt=YYYY-MM-DD/part-00000.csv
{BDH_ROOT_PATH}/{object}/region=EMEA/dt=YYYY-MM-DD/part-*.jsonl   # extra keys allowed, any order
```

Files are CSV (RFC-4180 style, header row), JSONL, or small `.json` arrays;
`_SUCCESS`, dotfiles and `.tmp` files are ignored. Every file read is bounded
(`BDH_MAX_FILE_BYTES`, default 1 GiB) and parsed as a stream.

- **Freshness marker:** every item carries `SourceSystem="BDH-Hadoop"` and
  `DataAsOf` (the partition `dt`, or the file sync timestamp when `dt` is
  absent) as refinable Graph properties, and a "Data as of" line in its
  content — so Copilot answers can surface the up-to-24 h staleness of the
  cheap path. `ITEM_URL_BASE` should point at the **live** Salesforce org so
  result cards deep-link to the real (current) record; the ids are identical.
- **Incremental watermark:** an incremental crawl only reads `dt` partitions
  newer than `last sync − BDH_LAG_HOURS` (default 24). The overlap window
  deliberately re-reads the newest partitions so a late-arriving nightly load
  is never missed; re-ingesting an unchanged record is an idempotent PUT.
  Full crawls read every partition the filters allow.

### ACLs / identity — coarse by design

BDH mirrors the Salesforce *data* but **not** the sharing tables, so full
sharing-model resolution is impossible here; that is part of the cheap-path
trade-off. Per-object `aclMode` in `config/schema.json`:

| Mode | Grant |
|---|---|
| `ownerOnly` (default) | the record's owner only: `OwnerId` (`ownerField`) resolved through the identity store, with the owner email (`ownerEmailField`, default `OwnerEmail`) as fallback |
| `group` | a fixed Entra group per object (`aclGroupId` required) |
| `public` | `everyoneExceptGuests` — explicit operator opt-in |

The identity sync loads the Salesforce user directory from the BDH `User`
object export (`BDH_IDENTITY_OBJECT`, default `User` — mirrored nightly like
every other object), resolves each user to an Entra object id by email through
Microsoft Graph, and persists the mapping in the identity store (SQLite
`data/{CONNECTOR_ID}_identity.db`, or `dbo.Principals` on SQL Server). It runs
before every full crawl (and on incrementals with
`IDENTITY_SYNC_ON_INCREMENTAL=true`); `identity-dry-run` previews resolution.

Records that resolve to **zero principals are skipped** — never ingested
world-readable. `FALLBACK_ACL_GROUP_ID` grants a default group *only* when
nothing resolved (it never widens a resolved ACL); `BDH_ADMIN_GROUP_ID`
appends an operators grant to every resolved non-public ACL.

### Deletion sync & reconcile

Every confirmed put is tracked in an ingested-item inventory
(`data/{CONNECTOR_ID}_inventory.db`, or `dbo.ItemInventory` on SQL Server).
Full crawls then run an existence sweep: inventory ids missing from the
filtered full-crawl source are DELETEd from the Graph connection (tombstone
sync — BDH has no deletion feed). Three guards protect the sweep: the
mass-deletion caps (`DELETION_SYNC_MAX_ITEMS`, default 1000 absolute;
`DELETION_SYNC_MAX_PERCENT`, default 25%) skip implausible sweeps with a
`deletion_sweep_skipped` alert, and a **row-cap-truncated fetch never sweeps**
(its source id set is incomplete). `reconcile [--type X] [--fix]` audits
index-vs-source drift on demand — and likewise refuses stale-fixing from a
truncated fetch. Details: `docs/DELETION_SYNC.md`.

### Distributed tracing & correlation IDs

Setting `OTEL_EXPORTER_OTLP_ENDPOINT` turns on OpenTelemetry spans around the
crawl cycle, per-object crawl, BDH source fetch, transform, Graph batch ingest
and deletion sweep (parent/child'd on one `HadoopConnector` ActivitySource,
tagged `bdh.*`). A **correlation id** — the trace id when a span is active —
is stamped on every structured log line, dead-letter record (JSONL +
`dbo.DeadLetter.CorrelationId`), and span, so one crawl is followable
end-to-end. Export is batched/fire-and-forget (a dead collector never stalls a
crawl); the standard `OTEL_*` env vars are honoured. **Unset = no exporter, no
listener, a genuine no-op** — `ActivitySource.StartActivity` returns null in
O(1), so default overhead is unchanged. `/metrics` exposes `tracing_enabled`
and `validate-config` reports the exporter target. Details: `docs/TRACING.md`.

### Circuit breakers & degraded mode

A breaker per external dependency (`hdfs`, `graph`) fails fast during a
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

### Unified data classification (optional)

With `CLASSIFICATION=true` every item gets a `SensitivityLabel`
(Public/Internal/Confidential/Restricted) and `DetectedCategories`
(PII/PCI/Secret) derived from a dependency-free, timeout-bounded regex scan
(`config/classification.json`) floored at the object's `sensitivityDefault`;
`CLASSIFICATION_MANIFEST=true` additionally writes a per-crawl,
Purview-aligned JSONL export. Off by default — no properties are added when
off. Details: `docs/CLASSIFICATION.md`.

## Optional operational knobs (all off by default)

| Env var | Effect | Docs |
|---|---|---|
| `LOG_RETENTION_DAYS=N` | Prune `logs/{prefix}_{timestamp}/` run dirs older than N days; state files never touched. | `docs/OBSERVABILITY.md` |
| `GRAPH_RETRY_JITTER=true` | ±20% jitter on computed Graph backoff (server `Retry-After` never jittered; every wait clamped to 60 s). Recommended in HA. | `docs/RETRY.md` |
| `GRAPH_BATCH_WORKERS` / `GRAPH_CONCURRENT_BATCHES` | Concurrent Graph `$batch` workers (default 8); adaptive — 429s dial it toward 1, clean windows dial it back. `GRAPH_CONCURRENT_BATCHES` wins if both set. | `docs/RETRY.md` |
| `GRAPH_BATCH_SIZE` / `INGEST_GRAPH_BATCH_SIZE` | Requests per `$batch` POST (≤ 20, API cap). | |
| `GRAPH_CONNECTION_SHARDS={...}` | Shard object types across N Graph connections — the throughput lever, essential at BDH scale. | `docs/SHARDING.md` |
| `ALLOW_FULL_SCAN=true` | Disable the fail-closed full-scan guard globally (prefer per-object `fullScanAllowed`). | `docs/FILTERS.md` |
| `BDH_MAX_RECORDS_PER_OBJECT` / `BDH_MAX_FILE_BYTES` | Row-cap safety valve (default 500 000; 0 disables) and per-file read bound (default 1 GiB). | `docs/FILTERS.md` |
| `BDH_LAG_HOURS=24` | Incremental watermark overlap for BDH's nightly-load lag. | |
| `DELETION_SYNC` (default **true**) + `DELETION_SYNC_MAX_PERCENT=25` + `DELETION_SYNC_MAX_ITEMS=1000` | Full-crawl existence sweep withdraws items deleted in BDH; the percent and absolute-cap knobs are the mass-deletion safety guards. | `docs/DELETION_SYNC.md` |
| `CLASSIFICATION=true` (+ `CLASSIFICATION_MANIFEST=true`) | Sensitivity labeling + detected categories (+ per-crawl JSONL manifest). | `docs/CLASSIFICATION.md` |
| `GRAPH_BASE_URL` (+ `GRAPH_SCOPE`, `AAD_APP_OAUTH_AUTHORITY_HOST`) | Sovereign-cloud Graph endpoint (e.g. `https://graph.microsoft.us`); scope defaults to `<base>/.default`; authority host moves the token endpoint. | |
| `USE_SQL_SERVER=true` + `SQL_CONNECTION_STRING` | Move state (identity store, checkpoints, sync ts, dead-letter, inventory) to SQL Server. | `docs/SQL_CONTRACT.md` |
| `SQL_USE_MANAGED_IDENTITY=true` / `SQL_MAX_RETRIES=5` | Entra auth for SQL; transient-fault retry (AG failover). | `docs/RETRY.md` |
| `HA_MODE=true` | Active-active multi-node crawling (requires SQL backend); failed object claims are terminal and never block the crawl close. | `docs/HA.md` |
| `USE_KEY_VAULT=true` + `KEY_VAULT_URI` | Resolve `SECRET_*` from Azure Key Vault. | |
| `HEALTH_PORT=N` | Serve `/health`, `/ready`, `/metrics` (Prometheus). | `docs/OBSERVABILITY.md` |
| `LOG_FORMAT=json` / `LOG_LEVEL` | Structured one-object-per-line logs; level floor for every sink (hot-path debug gating). | `docs/OBSERVABILITY.md` |
| `ALERT_WEBHOOK_URL` + `ALERT_DEADLETTER_THRESHOLD` | POST alerts on crawl failure / row-cap hits / skipped sweeps / dead-letter growth. | `docs/OBSERVABILITY.md` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OpenTelemetry span export + correlation ids. | `docs/TRACING.md` |
| `CIRCUIT_BREAKER_*` | Breaker thresholds/windows for the `hdfs` and `graph` dependencies. | `docs/RESILIENCE.md` |
| `IDENTITY_SYNC_ON_INCREMENTAL=true` | Identity sync on incremental cycles too. | |

## Running as a Windows service

The connector is SCM-aware: started by the Service Control Manager it runs
under a hosted-service lifetime automatically (the service binary path carries
the normal CLI arguments). Stopping the service is graceful: the in-flight
chunk finishes, the pending Graph batch is flushed, the checkpoint is saved,
and the next start resumes where it left off.

```powershell
# 1. Publish (.NET 10, win-x64)
dotnet publish src/HadoopConnector -c Release -r win-x64 -o C:\HadoopConnector

# 2. Lay out runtime files next to the exe
Copy-Item -Recurse config C:\HadoopConnector\config
Copy-Item -Recurse env    C:\HadoopConnector\env      # .env.local + .env.local.user

# 3. Install + start (elevated PowerShell)
.\scripts\install-windows-service.ps1 -InstallDir C:\HadoopConnector
Start-Service HadoopConnector
```

The script registers the service (Automatic start, restart-on-crash) with
`full-deployment --continuous --full-crawl-hours 24 --incremental-hours 4` by
default — pass `-Arguments` to change it, `-Uninstall` to remove.

## Docker

A multi-stage image (`mcr.microsoft.com/dotnet/sdk:10.0` build →
`mcr.microsoft.com/dotnet/runtime:10.0` runtime, non-root) ships in
`Dockerfile`; `docker-compose.yml` brings up a local SQL Server 2022 +
schema-provisioning one-shot + the connector wired to the SQL state backend
(dev topology — real deployments point at a hardened SQL Server/AG listener):

```bash
docker build -t hadoopconnector:latest .
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
SQL_CONNECTION_STRING=Server=<AG-listener>;Database=HadoopConnector;...
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

601 tests: CLI parsing, checkpoint round-trip/resume, dead-letter write/retry
shape and concurrency invariants, retry/backoff math (numeric Retry-After,
60 s clamp, jitter), Graph client throttling/hardening (mock HTTP), adaptive
concurrency, connection-sharding validation + end-to-end per-shard routing,
sovereign-cloud endpoint/scope/authority override, WebHDFS client (URI
building, LISTSTATUS/OPEN, retry ladder, RemoteException surfacing, breaker
classification), partition scanner (dt watermark + key pruning, depth cap),
streaming parser hardening (bounded reads, malformed rows, CSV quoting),
filter config validation (unknown operators/keys are errors) + predicate
evaluation, fail-closed scale guard, row-cap truncation + sweep skip, ACL
resolver modes + fallback/admin grants, identity sync + principal mapping,
SQLite identity store, SQL state store + HA coordinator incl.
close-with-failed-claims semantics (fake gateway), offline SQL script
validation (grammar parse, idempotency-by-construction, C#⇄schema drift),
hot-path log gating, worker-crash resilience, ingested-item inventory
(SQLite + SQL Server), deletion sweeps incl. both mass-deletion guards and the
truncated-fetch skip, reconcile drift reporting/repair (sharding-aware,
truncation-safe), content classifier (Luhn, match timeout) + sensitivity
labeling + manifest, OpenTelemetry tracing (parent/child spans + `bdh.*` tags,
correlation ids, overhead-when-off), circuit breakers (state machine,
5xx/timeout trip vs 4xx/429 ignored, degraded-mode pause + clean resume),
health endpoint, alerting, metrics, log pruning, env loading, Key Vault secret
resolution, and an end-to-end pipeline run against a fake BDH source + mocked
Graph. No test touches the network. Collections run serially
(`xunit.runner.json`) because several tests swap process-global seams (state
paths, env vars).

## Deliberate scope notes

- **ACL fidelity:** ownership is the only per-record principal signal BDH
  carries. If a record must be visible to more than its owner, use
  `aclMode=group` (or, consciously, `public`) — do not expect sharing-rule
  semantics from this connector; that is what the live Salesforce connector
  is for.
- **Freshness:** answers can lag the live org by up to 24 h (`BDH_LAG_HOURS`).
  The `DataAsOf` property and the content's "Data as of" line make the lag
  visible instead of hiding it.
- **Kerberos:** the WebHDFS client speaks simple auth (`user.name`) and
  optional delegation tokens only. For kerberized clusters, terminate SPNEGO
  at an Apache Knox gateway and point `HDFS_NAMENODE_URL` at Knox.
- **Deletion detection latency:** removals are withdrawn by the next FULL
  crawl's existence sweep (or `reconcile --fix` on demand); incremental crawls
  and row-cap-truncated fetches never sweep. See `docs/DELETION_SYNC.md`
  (incl. the bootstrap note for pre-inventory deployments).
- **Tracing** exports only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set; metrics/
  logs are always available. Spans cover pipeline stages, not every HTTP call —
  the correlation id ties a run together across logs, dead-letter and traces.
- **Circuit breakers** are on by default but inert on the happy path; they trip
  only on sustained real failures, never on 4xx/429. Degraded mode reuses the
  checkpoint/resume path, so a paused crawl loses no state. `CIRCUIT_BREAKER=false`
  restores byte-identical unbreakered behaviour. See `docs/RESILIENCE.md`.
- The object/field names in `config/schema.json` and the filters in
  `config/filters.json` are a sensible default set; adjust them to your BDH
  export (custom columns are fully supported by the config-driven mapping).
