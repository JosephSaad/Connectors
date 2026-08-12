# Operations Runbook

Practical, checklist-driven operations for the Salesforce Copilot Connector.
Architecture context is in [ARCHITECTURE.md](ARCHITECTURE.md); this doc is about
*running* it.

Related: [HA.md](HA.md) (active-active), [SQL_CONTRACT.md](SQL_CONTRACT.md) (DB
contract), [RETRY.md](RETRY.md) (throttling), [CAPACITY.md](CAPACITY.md) (sizing),
[OBSERVABILITY.md](OBSERVABILITY.md) (health/metrics/alerts), env reference in
[../env/README.md](../env/README.md) and template
[../env/.env.local.example](../env/.env.local.example).

Commands below use `dotnet run --project src/SalesforceCopilotConnector -- <cmd>`
from the `SalesforceConnector/` directory. On a published/installed node substitute
`dotnet SalesforceCopilotConnector.dll <cmd>` from the install directory. All
relative paths (`config/`, `env/`, `logs/`, `data/`) resolve against the current
directory, or against `SFCONNECTOR_HOME` when it is set (service / container).

---

## 1. First-time deployment checklist

### 1a. Azure AD (Entra) app registration
- ☐ Register an application in Entra ID; note the **client ID**, **object ID**,
  **tenant ID**.
- ☐ Add these **application** Microsoft Graph permissions and **grant admin
  consent**:
  - `ExternalConnection.ReadWrite.OwnedBy`
  - `ExternalItem.ReadWrite.OwnedBy`
- ☐ Create a **client secret**; record the secret **value** (not its ID) — it
  goes in `SECRET_AAD_APP_CLIENT_SECRET` (or Key Vault).

### 1b. Salesforce connected app
- ☐ Create a Connected App with the **OAuth 2.0 client-credentials** flow enabled.
- ☐ Assign a run-as integration user with read access to every object in
  `config/schema.json` (and to sharing/role/group/territory metadata for ACLs).
- ☐ Record the **Consumer Key** (`SALESFORCE_CLIENT_ID`) and **Consumer Secret**
  (`SECRET_SALESFORCE_CLIENT_SECRET`).

### 1c. Environment files
- ☐ `cp env/.env.local.example env/.env.local` and fill in Core/Identity,
  Salesforce, and Azure AD values.
- ☐ Create `env/.env.local.user` with the two secrets only:
  `SECRET_SALESFORCE_CLIENT_SECRET`, `SECRET_AAD_APP_CLIENT_SECRET`.
- ☐ Confirm neither file is tracked by git (both are in `.gitignore`).
- ☐ (Optional, recommended) `dotnet run ... -- guide` and eyeball the required-var
  list against your `.env.local`.

### 1d. Provision + first crawl
- ☐ `setup-connection --verbose` → creates the external connection and registers
  the schema. Wait for "Ready" (polls up to `CONNECTION_TIMEOUT_SECONDS`, default
  600s).
- ☐ `full-deployment --verbose` → runs the identity crawl (if `USE_GROUP_ACL`) and
  the initial full content ingest. Sizing/time-to-index expectations:
  [CAPACITY.md](CAPACITY.md) §3 (roughly 11–16 h per 1M records on one
  connection).
- ☐ Verify items appear in Microsoft Search / Copilot and spot-check ACL trimming
  with a couple of non-admin users.

> `setup-connection` and `full-deployment` are idempotent — safe to re-run.

---

## 2. Running continuously

`--continuous` runs forever: full content crawl every `--full-crawl-hours`
(default 24, min 12), incremental every `--incremental-hours` (default 4, min 1).
Identity crawl runs full on full cycles (and on incrementals if
`IDENTITY_SYNC_ON_INCREMENTAL=true`).

```
full-deployment --continuous --full-crawl-hours 24 --incremental-hours 4
```

### 2a. Windows service
The connector is SCM-aware — when started by the Service Control Manager it runs
under a hosted-service lifetime automatically (no extra flag). Stop is graceful
(finishes the in-flight chunk, flushes the pending `$batch`, checkpoints — next
start resumes).

```powershell
# Publish + lay out runtime files
dotnet publish src/SalesforceCopilotConnector -c Release -r win-x64 -o C:\SFConnector
Copy-Item -Recurse config C:\SFConnector\config
Copy-Item -Recurse env    C:\SFConnector\env      # .env.local + .env.local.user

# Install + start (elevated)
.\scripts\install-windows-service.ps1 -InstallDir C:\SFConnector
Start-Service SalesforceCopilotConnector
```

The script registers the service (Automatic start, restart-on-crash) with
`full-deployment --continuous --full-crawl-hours 24 --incremental-hours 4`.
Flags: `-Arguments` (change command/schedule), `-ServiceName` (rename),
`-Uninstall` (remove). `SFCONNECTOR_HOME` is pointed at the install dir; logs stay
in `SFCONNECTOR_HOME\logs\`.

| Task | Command |
|---|---|
| Start | `Start-Service SalesforceCopilotConnector` |
| Stop (graceful) | `Stop-Service SalesforceCopilotConnector` |
| Status | `Get-Service SalesforceCopilotConnector` |
| Change args/schedule | re-run installer with `-Arguments '...'` |
| Uninstall | `.\scripts\install-windows-service.ps1 -Uninstall` |

### 2b. Docker
`Dockerfile` (multi-stage, non-root, `WORKDIR /app` = `SFCONNECTOR_HOME`) and
`docker-compose.yml` (SQL Server 2022 + connector, dev/test topology) are
provided. Mount `config/`, `env/`, `logs/`, `data/`.

The build context is the **repository root**, not the connector directory — the
project references `../Connector.Chassis` and a build cannot reach outside its
context (compose sets `context: ..` for the same reason). Build from the repo
root:

```bash
docker build -f SalesforceConnector/Dockerfile -t sfconnector:latest .
docker run --rm -e SFCONNECTOR_HOME=/app \
  -v "$PWD/config:/app/config" -v "$PWD/env:/app/env" \
  -v "$PWD/logs:/app/logs"     -v "$PWD/data:/app/data" \
  sfconnector:latest full-deployment --continuous
```

`docker compose up --build` brings up SQL + the connector (auto-provisions the DB
via the `mssql-init` service). The compose SA password is a **throwaway dev
credential** — never use it in production; point `SQL_CONNECTION_STRING` at a
hardened SQL Server / AG listener instead. Set `NODE_ID` explicitly in containers
(the default machine name is random).

---

## 3. HA bring-up + failover drill

Full detail in [HA.md](HA.md); the essentials:

### Bring-up
- ☐ Provision the DB: run `scripts/sql/create-database.sql` +
  `scripts/sql/create-login.sql` against the **AG primary**; confirm the **AG
  listener** resolves from every node.
- ☐ Grant each node's service account `EXECUTE` on `dbo.usp_*` and `SELECT` on the
  `v*` views.
- ☐ On **every** node set: `USE_SQL_SERVER=true`,
  `SQL_CONNECTION_STRING=<AG listener>`, `HA_MODE=true`, a unique `NODE_ID`,
  `GRAPH_RETRY_JITTER=true`, and `GRAPH_BATCH_WORKERS = single-node value ÷ node
  count`.
- ☐ Keep `HA_HEARTBEAT_SECONDS` (60) well below `HA_CLAIM_TIMEOUT_SECONDS` (300).
- ☐ Start node 1 with `full-deployment` to create the connection + schema, then
  start the **same** `--continuous` command on all nodes.
- ☐ Verify in `vActiveCrawls` that exactly one crawl is open and every `NODE_ID`
  is claiming objects.

### Failover drill (do this before you rely on it)
- ☐ Kill one node mid-crawl (stop the service / `docker kill`).
- ☐ Watch `vActiveCrawls`: after ≤ `HA_CLAIM_TIMEOUT_SECONDS` a surviving node
  reclaims the dead node's object and resumes from its SQL checkpoint.
- ☐ Confirm the crawl still closes with a **single** last-sync write (only the
  node whose `usp_CloseCrawlIfComplete` returns 1 writes it).
- ☐ (SQL failover) fail the AG over to a replica; in-flight calls error and retry
  at the next chunk/claim boundary — the crawl continues.

---

## 4. Monitoring

Primary signals — see [OBSERVABILITY.md](OBSERVABILITY.md) for the full metric
list and alert payloads.

| Signal | Where | Watch for |
|---|---|---|
| Liveness / readiness | `GET /health`, `/ready` on `HEALTH_PORT` (0 = off) | process up; ready = connection provisioned |
| Metrics | `GET /metrics` (Prometheus text) on `HEALTH_PORT` | ingest rate, dead-letter depth, 429 counts |
| Alerts | webhook `ALERT_WEBHOOK_URL` | crawl failure; dead-letter depth > `ALERT_DEADLETTER_THRESHOLD` |
| Logs | `logs/{prefix}_{yyyyMMdd_HHmmss}/` (set `LOG_FORMAT=json` for machine parsing) | WARN/ERROR; `[HA]` coordination lines |
| Crawl progress (SQL) | view `vActiveCrawls` | open crawl, per-node claim breakdown |
| Failure hotspots (SQL) | view `vDeadLetterSummary` | unretried counts per object type |
| Last sessions (SQL) | view `vLastSessions` / `vGroupMemberCounts` | last completed crawl, group sizes |

File-mode equivalents (no SQL): dead-letter depth = line count of
`logs/failed_records_{CONNECTOR_ID}.jsonl`; last sync = `logs/sync_state.json`;
crawl history = `sync_sessions` table in `data/{CONNECTOR_ID}_identity.db`.

---

## 5. Common failures & fixes

| Symptom | Likely cause | Fix |
|---|---|---|
| `ingest` errors that the connection isn't ready / 404 on external connection | `full-deployment`/`setup-connection` never completed; wrong `CONNECTOR_ID` | Run `setup-connection --verbose`; wait for Ready. Confirm `CONNECTOR_ID` matches the provisioned connection. |
| Startup: `Invalid configuration: Missing <VAR>` | required env var absent | Add it to `env/.env.local` (or secret to `.env.local.user`). Cross-check with `guide`. |
| Startup: `Connector ID cannot start with…` / length/char error | `CONNECTOR_ID` invalid | 3–32 alphanumerics, no reserved prefix (Settings.cs). |
| Startup: HA enabled but SQL isn't | `HA_MODE=true` without `USE_SQL_SERVER=true` | Set `USE_SQL_SERVER=true` + a valid `SQL_CONNECTION_STRING`. |
| Slow crawl, many 429s in logs | Graph throttle (per connection); workers too high | Lower `GRAPH_BATCH_WORKERS`; in HA divide by node count and set `GRAPH_RETRY_JITTER=true`. Adaptive concurrency also self-corrects. See [RETRY.md](RETRY.md). |
| Dead-letter queue growing | transient item failures / bad records | Inspect `vDeadLetterSummary` (or the JSONL). Re-drive: `retry-failed --clear-on-success`. Target a subset with `retry-failed --file <path>`. |
| Salesforce auth failures | expired/rotated Connected App secret; wrong instance URL | Update `SECRET_SALESFORCE_CLIENT_SECRET`; verify `SALESFORCE_INSTANCE_URL`/`SALESFORCE_API_VERSION`. |
| `INVALID_FIELD` from Salesforce | field in `config/schema.json` missing/renamed in the org, or API version too old | Fix the field in `schema.json`; bump `SALESFORCE_API_VERSION`; ensure the integration user can see it. |
| SQL errors during an AG failover | listener redirect; transient faults | Expected — calls retry at the next chunk/claim boundary (`SQL_MAX_RETRIES`). If persistent, check the listener and app-login grants. |
| Restart re-does work / seems stuck | checkpoint resume | Normal: completed chunks are skipped from the last checkpoint. To force a clean full crawl, clear checkpoints (`usp_ClearCheckpoints`, or delete `logs/checkpoint_{CONNECTOR_ID}.json` in file mode). |
| Missing/incorrect ACL trimming | identity crawl stale or `USE_GROUP_ACL` off | Run `identity-dry-run --verbose` to preview; re-run `full-deployment` (or an identity crawl). Consider `IDENTITY_SYNC_ON_INCREMENTAL=true`. |

### retry-failed quick reference
```
retry-failed                        # re-push all dead-lettered items (file kept)
retry-failed --clear-on-success     # wipe the queue only if every item succeeds
retry-failed --file logs/failed_records_<CONNECTOR_ID>.jsonl   # target one file
```

---

## 6. Logs, retention & data locations

| What | Location |
|---|---|
| Run logs | `logs/{prefix}_{yyyyMMdd_HHmmss}/` (per run; local to each node) |
| Last-sync / checkpoints / dead-letter (file mode) | `logs/sync_state.json`, `logs/checkpoint_{CONNECTOR_ID}.json`, `logs/failed_records_{CONNECTOR_ID}.jsonl` |
| Identity store (file mode) | `data/{CONNECTOR_ID}_identity.db` (SQLite) |
| Service/container home | `SFCONNECTOR_HOME` (paths resolve under it) |

**Retention** — `LOG_RETENTION_DAYS` (default `0` = keep forever). When `> 0`, at
the start of every command and each `--continuous` cycle the connector deletes
`logs/{prefix}_{timestamp}/` run directories older than N days and, in SQL mode,
prunes DeadLetter(retried)/SyncSessions/CrawlSessions history via
`usp_PruneHistory`. Root state files are **never** pruned. Set `LOG_FORMAT=json`
for structured, machine-ingestable logs.

---

## 7. Backup & DR (SQL Server backend)

The SQL database is the source of truth for identity, checkpoints, and dead-letter
in SQL mode — protect it like any production DB.

- ☐ Enable regular **full + log backups** of `SalesforceConnector` (RPO per your
  policy). Always On AG replicas provide HA, **not** backup — keep real backups.
- ☐ Store `scripts/sql/create-database.sql` + `create-login.sql` in source control
  as the schema-rebuild path (the contract is [SQL_CONTRACT.md](SQL_CONTRACT.md)).
- ☐ DR recovery order: restore/rebuild DB → re-grant the app login → point
  `SQL_CONNECTION_STRING` at the recovered listener → start nodes. If crawl state
  was lost, the next full crawl rebuilds the index from Salesforce (no Salesforce
  data is lost — the connector is a one-way sync).
- ☐ Losing the DB is recoverable but expensive (a full re-crawl); losing it does
  **not** corrupt the M365 index — a full crawl reconciles it.
- ☐ In **file mode** there is no DB: back up `data/` (identity SQLite) and the
  `logs/` root state files if you want to preserve incremental watermarks;
  otherwise a full crawl rebuilds everything.

---

## 8. Upgrade & rollback

- ☐ **Before upgrading:** back up the SQL DB (or `data/` + `logs/` root files in
  file mode). Note the running version/args.
- ☐ **Windows service:** `Stop-Service` (graceful — checkpoints), redeploy the
  published output over the install dir (keep `config/` + `env/`), `Start-Service`.
  Resumes from the last checkpoint.
- ☐ **Docker:** build/pull the new image, `docker compose up -d` (or restart the
  node). Volumes preserve `config/`/`env/`/`logs/`/`data/`.
- ☐ **HA:** upgrade **one node at a time** (rolling). Survivors keep the crawl
  going; the upgraded node rejoins and reclaims work. Only take all nodes down
  together if the SQL schema changed and requires it.
- ☐ **SQL schema changes** ship in `scripts/sql`; apply per [SQL_CONTRACT.md](SQL_CONTRACT.md)
  before starting upgraded nodes.
- ☐ **State compatibility:** file/SQLite state is byte-compatible with the Python
  original and across connector versions, so rollback to a prior build generally
  resumes cleanly. If a release notes a state-format change, that release's notes
  own the rollback caveat.
- ☐ **Rollback:** stop, redeploy the previous published output/image, start. State
  on disk/SQL is picked up as-is.
