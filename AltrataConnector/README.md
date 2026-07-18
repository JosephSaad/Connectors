# Altrata Copilot Connector (C#)

A standalone Microsoft 365 Copilot Graph connector for **Altrata**
relationship & wealth intelligence (BoardEx / WealthEngine / Wealth-X style
data). Primary ingestion is licensed **bulk file feeds** (SFTP drop directory
with checksummed manifests); secondary is the **Altrata REST API** for
on-demand, billable profile lookups. Results are visible **only to licensed
seats** — never to everyone.

.NET 10, cross-platform for build/test, deploys to Windows Server as a
Windows service (SCM-aware, graceful chunk-boundary stop).

## Layout

```
AltrataConnector.sln
src/AltrataConnector/          the connector CLI
  Altrata/                     feeds, manifests, reconciliation, API client,
                               PII classifier, item transformer, audit, retention
  Commands/                    CLI parser + all commands, composition root
  Config/                      env layering, typed AppConfig
  Entitlement/                 seat model, seat-only ACL builder (never-everyone)
  Graph/                       Graph external-connections client, retry/jitter
  Identity/                    SQLite/SQL Server identity store, entity resolution
  Infrastructure/              logging, metrics, health endpoint, alerts,
                               Key Vault secrets, HA leases, Windows service host
  Ingestion/                   crawl engine (checkpoint, dead-letter, re-ACL)
  State/                       file + SQL Server state stores
tests/AltrataConnector.Tests/  xUnit suite (incl. offline SQL DDL validation)
config/                        schema.json, graph-schema.json, seats.json (example)
docs/                          FEEDS, ENTITLEMENT, ERASURE, MATCHING,
                               RELATIONSHIP_PATHS, RETRY, RESILIENCE, SHARDING,
                               HA, OBSERVABILITY, SQL_CONTRACT
env/                           .env.local.example (+ layering README)
scripts/                       install-windows-service.ps1, sql/ (canonical DDL)
Dockerfile, docker-compose.yml container image + local SQL/HA dev topology
.github/workflows/             ci.yml (ubuntu+windows), codeql.yml,
                               release.yml (test-gated, checksummed bundles)
```

## Requirements

* .NET 10 SDK
* Entra app registration with **application** Graph permissions
  (admin-consented): `ExternalConnection.ReadWrite.OwnedBy`,
  `ExternalItem.ReadWrite.OwnedBy`
* The Altrata feed drop directory reachable as a local path (`FEED_PATH`)
* A seat source: `config/seats.json` / `SEAT_LIST_PATH` or `SEAT_GROUP_ID`

## Setup

```bash
cp env/.env.local.example env/.env.local     # edit non-secret values
echo 'SECRET_AAD_APP_CLIENT_SECRET=...' > env/.env.local.user
dotnet build
dotnet run --project src/AltrataConnector -- validate-config --strict
```

## Usage

```bash
dotnet run --project src/AltrataConnector -- <command> [--verbose]

guide                # full operator guide
setup-connection     # create connection + register schema (no ingestion)
full-deployment      # connection → schema → full crawl
full-deployment --continuous --full-crawl-hours 24 --incremental-hours 4
ingest               # full crawl only
ingest --incremental # only deliveries not yet processed
ingest-object --type WealthIndicator
ingest-item --id P123456 --purpose "RFP for client X" [--requested-by joseph]
retry-failed [--clear-on-success] [--retire-unreplayable]
                     # shard-aware; replays upserts (ACL rebuilt from CURRENT
                     # seats) AND tombstone deletes; --retire-unreplayable
                     # drops transform-failure entries (docs/RETRY.md)
identity-dry-run [--save]
seat-sync            # refresh seats; changed list → re-ACL pass
validate-config [--strict]
forget-subject --id P123456 [--email e] [--confirm]  # DSAR erase + suppress
unsuppress-subject --id P123456 --confirm            # lift an erasure block
purge-all            # dry-run report (counts only)
purge-all --confirm  # license-end purge: withdraw all items + wipe state
```

Exit codes: success 0, failure 1, usage error 2. Ctrl+C stops gracefully
(current chunk finishes, checkpoint saved); a second Ctrl+C force-quits.

## The seat entitlement model (signature feature)

Every externalItem ACL is built **exclusively** from the licensed seat list
(UPNs/object IDs in `config/seats.json`, or one Entra group via
`SEAT_GROUP_ID`). An empty seat list refuses to ingest (fails closed); an
`everyone` grant is structurally impossible and asserted at every layer. Seat
list changes are detected by hash and trigger a re-ACL pass over every
ingested item. See **docs/ENTITLEMENT.md** (including `purge-all`, the
purpose-of-use audit log and PII classification labels).

## Feeds, checksums, reconciliation, deltas

Deliveries under `FEED_PATH` carry `manifest.json` with per-file SHA-256 and
record counts. Checksum mismatch ⇒ the delivery is rejected and alerted,
nothing ingested. **Delta deliveries** are supported: records marked
`op: delete` (or `is_deleted: true`) are tombstones — the externalItem is
withdrawn via $batch DELETE instead of upserted, so incremental crawls of
delta drops fully maintain the index without full re-crawls. After
processing, `ingested + deleted + dead-lettered` must equal the manifest
count; the JSONL + summary report lands in
`logs/reconciliation_{CONNECTOR_ID}_{deliveryId}.jsonl`. Failed withdrawals
dead-letter as `op: delete` and `retry-failed` replays them as DELETEs.
`RETENTION_DAYS` archives/deletes feed files after successful processing.
See **docs/FEEDS.md**.

## Entity resolution (tiered)

Deterministic first — exact email, then normalized name+employer — with an
opt-in scored fuzzy tier beneath (`ENTITY_FUZZY_MATCHING=true`): name-token
overlap 0.6 + employer 0.3 + role hint 0.1. Scores at/above
`ENTITY_MATCH_THRESHOLD` (0.85) auto-link; scores in
[`ENTITY_REVIEW_FLOOR`, threshold) land in the human review queue
`logs/match_review_{CONNECTOR_ID}.jsonl`. Every linked item carries
provenance: `crmMatchRule` (`email` / `name+employer` / `fuzzy`) and
`crmMatchConfidence`. See **docs/MATCHING.md**.

## Per-subject erasure (DSAR / right-to-erasure)

`forget-subject --id <altrataId> | --email <e> [--confirm]` erases one person:
it resolves the subject (by id, or by email through the crosswalk), withdraws
every externalItem concerning them — PersonProfile **and** derived items, via
an item↔subject reverse index built at ingest — removes them from the
inventory, crosswalk and relationship-path index, and records the erasure in a
tamper-evident (hash-chained) erasure ledger. The subject id goes on a durable
**suppression list**, so a later feed delivery re-introducing the person is
**skipped, not re-ingested** (counted as `suppressed` in reconciliation, never
dead-lettered) until `unsuppress-subject --confirm` lifts it. Dry-run by
default; `--confirm` executes (mirrors `purge-all`). Erasure only ever DELETEs,
so the seat invariant is untouched. See **docs/ERASURE.md**.

## Relationship-path materialization

`RELATIONSHIP_PATHS=true` precomputes bounded per-person path summaries from
the `RelationshipPath` / `BoardMembership` / `Organization` datasets and
materializes them onto `PersonProfile` items as searchable/refinable
properties: `firstDegreeCount`, `secondDegreeCount`, `pathCount`,
`topConnectedOrgs` (capped by `RELATIONSHIP_TOP_ORGS`) and a human-readable
`strongestPathSummary`. The adjacency index is rebuilt per crawl in the
identity store (deterministic, reconcilable, delta-tombstone-aware — a
withdrawn person drops from everyone's counts). Path properties are metadata
only and never widen ACLs; items stay seat-only. See
**docs/RELATIONSHIP_PATHS.md**.

## Running as a Windows service

```powershell
# 1. Publish (on the build machine)
dotnet publish src/AltrataConnector -c Release -r win-x64 -o C:\AltrataConnector

# 2. Lay out runtime files next to the exe
#    C:\AltrataConnector\config\  (schema.json, graph-schema.json, seats.json)
#    C:\AltrataConnector\env\     (.env.local, .env.local.user)

# 3. Install + start (elevated PowerShell)
.\scripts\install-windows-service.ps1 -InstallDir "C:\AltrataConnector"
Start-Service AltrataConnector
```

The service runs `full-deployment --continuous` by default (override with
`-Arguments`). SCM **stop is graceful**: the in-flight chunk finishes, the
pending Graph batch is flushed and the checkpoint is saved, so the next start
resumes exactly where it left off. `ALTRATA_CONNECTOR_HOME` (set by the
installer) anchors `config/`, `env/`, `logs/`, `data/`.

## SQL Server backend & high availability

```bash
USE_SQL_SERVER=true
SQL_CONNECTION_STRING="Server=...;Database=AltrataConnector;..."
SQL_USE_MANAGED_IDENTITY=true    # optional
HA_MODE=true                     # optional, multi-node (lease per delivery)
GRAPH_RETRY_JITTER=true          # recommended with HA
```

All state (identity store, checkpoints, sync timestamps, dead-letter, ledger,
billable counter, leases) moves to SQL Server — see **docs/SQL_CONTRACT.md**
and **docs/HA.md** (including the pinned close-with-failed-claims semantics:
exactly one node closes a crawl and records sync state, failed deliveries
close the crawl as `failed` instead of wedging it open). The canonical DDL
lives in `scripts/sql/create-database.sql`, statically validated by the test
suite (ScriptDom grammar + idempotency guards + DacFx semantic model).

## Throughput: $batch, workers, sharding, sovereign clouds

Bulk ingest and the re-ACL pass ship through Graph **$batch** (≤20 requests
per call). `GRAPH_BATCH_SIZE` sets the sub-batch size, `GRAPH_BATCH_WORKERS`
(alias `GRAPH_CONCURRENT_BATCHES`, which wins) the concurrent workers —
adaptive: 429s dial concurrency down, sustained success dials it back up.
Inside a batch only 429/503 items are re-sent; `Retry-After` raises the wait
(60 s hard cap on every retry wait). See **docs/RETRY.md**.

`GRAPH_CONNECTION_SHARDS` shards the datasets across N Graph connections for
N× write capacity (**docs/SHARDING.md**); `GRAPH_BASE_URL` / `GRAPH_SCOPE`
retarget sovereign clouds (US Gov / 21Vianet). The never-everyone seat
invariant is asserted on the batched paths too.

## Docker

```bash
docker build -t altrata-connector .
docker compose up --build     # SQL Server 2022 + schema init + continuous crawl
```

`docker-compose.yml` is a local/dev topology (throwaway SA password, loopback
port only) with a commented-out second node for HA experiments.

## Observability

`HEALTH_PORT=9090` → `/health`, `/ready`, `/metrics` (Prometheus).
`LOG_FORMAT=json`, `LOG_RETENTION_DAYS`, `ALERT_WEBHOOK_URL`,
`ALERT_DEADLETTER_THRESHOLD`.

**Circuit breakers / degraded mode** (`CIRCUIT_BREAKER_*`, on by default):
each external dependency has its own breaker (Graph = critical, Altrata API =
non-critical). Sustained 5xx/timeout/connection failures trip it (4xx/429 do
not); while Open, calls fail fast. When the Graph breaker opens, the crawl
**pauses at a safe checkpoint** (no dead-letters) and auto-recovers once it
probes successfully — `/ready` returns 503 while degraded, `/health` stays up.
Erasure remains durable (suppress + ledger + dead-letter) even with Graph down.
See **docs/RESILIENCE.md**.

**Distributed tracing (OpenTelemetry)**: set `OTEL_EXPORTER_OTLP_ENDPOINT` to
export spans (crawl → delivery → dataset ingest → graph batch, plus entity
resolution, path-index build, enrichment lookup, forget-subject, seat-sync/
re-ACL); outbound Graph/API calls carry W3C `traceparent`. Unset = inert, zero
overhead. A **correlation id** per crawl/erasure cycle is stamped on JSON logs,
dead-letter records, reconciliation reports, erasure-ledger entries and span
tags — followable end-to-end. **PII caution**: spans tag only ids/counts/
hashes/enums (allowlist-enforced) — never names, emails or wealth figures. See
**docs/OBSERVABILITY.md**.

## Tests

```bash
dotnet test        # 387 tests: CLI parsing, checkpoint resume, dead-letter
                   # (incl. 16-writer concurrency corruption stress),
                   # retry/backoff math (Retry-After honoured exactly + 60s clamp,
                   # ±20% jitter), $batch pipeline (429 ladder, adaptive
                   # concurrency, seat invariants on the batched path),
                   # sovereign-endpoint override, sharding validation/routing,
                   # manifest checksums + reconciliation math, entity resolution,
                   # seat ACLs (never-everyone), seat-change re-ACL (batched,
                   # failure leaves hash uncommitted), HA close-with-failed-claims,
                   # purge dry-run, billable-cost persistence, SQLite identity
                   # store, rate limiter, offline SQL DDL validation
                   # (ScriptDom + DacFx), crawl engine end-to-end (no network)
```

CI (`.github/workflows/ci.yml`) runs the suite on ubuntu **and windows**
(Windows Server is the deployment target), provisions the SQL schema twice
against a live SQL Server 2022 container (idempotency proof), and validates
the Docker image build. Releases are test-gated with SHA-256 checksums on
every bundle.

## Environment variables

Every knob is documented inline in **env/.env.local.example**; the layering
rules (process env > env/.env.local > env/.env.local.user) are in
**env/README.md**.
