# Salesforce Copilot Connector (C#)

A complete C#/.NET 10 port of Microsoft's [Salesforce-Custom-Copilot-Connector](https://github.com/microsoft/Salesforce-Custom-Copilot-Connector)
(Python) — a production-tested template for building Microsoft 365 Copilot custom
connectors for Salesforce CRM, covering standard + custom objects and fields with
all permission models enabled.

This is a faithful 1:1 behavioral port: same commands and flags, same env vars,
same Graph/Salesforce API calls, same retry/backoff/throttling behavior, and
byte-compatible on-disk state (sync-state JSON, checkpoints, dead-letter JSONL,
SQLite identity store) so it can pick up where the Python version left off.

## Layout

| Path | Ported from |
|---|---|
| `src/SalesforceCopilotConnector/Salesforce/` | `salesforce/` — settings, REST client, sharing model, item transformer |
| `src/SalesforceCopilotConnector/Graph/` | `graph/` — Graph client, connection/schema, ingest pipeline, identity store/publisher, legacy ACL resolver |
| `src/SalesforceCopilotConnector/AclEngine/` | `acl_engine/` — OWD, share fetcher, group/role/territory/queue handlers, principal mapper, identity sync |
| `src/SalesforceCopilotConnector/Item/` | `item/` — record → externalItem conversion |
| `src/SalesforceCopilotConnector/Config/` | `config/sync_state.py` — checkpoints & dead-letter files |
| `src/SalesforceCopilotConnector/Commands/` + `Program.cs` | `commands/` + `run.py` — CLI (argparse replica) |
| `src/SalesforceCopilotConnector/Dashboard.cs` | `dashboard.py` (rich → Spectre.Console) |
| `tests/SalesforceCopilotConnector.Tests/` | `tests/` — full pytest suite as xUnit + C# additions (665 tests) |
| `config/` | schema.json, graph-schema.json, template.json (same files) |

## Install from a release

Prebuilt self-contained bundles (no .NET SDK or runtime required on the box) are
published on the [releases page](https://github.com/JosephSaad/SalesforceCopilotConnector/releases)
for `win-x64` (includes `scripts/install-windows-service.ps1` for service mode)
and `linux-x64`. Each zip extracts to a ready-to-run directory — binary plus
`config/`, `env/`, `docs/`, `scripts/sql/` — laid out the way the app resolves
paths at runtime. Verify, extract, configure, run:

```bash
sha256sum -c SalesforceCopilotConnector-<version>-linux-x64.zip.sha256
unzip SalesforceCopilotConnector-<version>-linux-x64.zip
cd SalesforceCopilotConnector-<version>-linux-x64
cp env/.env.local.example env/.env.local    # fill in credentials
./SalesforceCopilotConnector validate-config
```

Releases build automatically from `v*` tags (test-gated, checksummed — see
`.github/workflows/release.yml`); each release also pushes a container image to
`ghcr.io/josephsaad/salesforce-copilot-connector` (`:<version>` and `:latest`).
Building from source instead only needs the requirements below.

## Requirements

- .NET 10 SDK
- The same environment variables as the Python version (see `env/README.md`);
  `.env.local` / `env/.env.local` files are loaded the same way.

Optional operational knobs (all off by default — behaviour is unchanged when unset).
Full reference: `env/.env.local.example`.

| Env var | Effect | Docs |
|---|---|---|
| `LOG_RETENTION_DAYS=N` | Prune `logs/{prefix}_{timestamp}/` run dirs (and SQL history via `usp_PruneHistory`) older than N days; root state files never touched. | |
| `GRAPH_RETRY_JITTER=true` | ±20% jitter on computed Graph retry backoff (`Retry-After` still honoured exactly). Recommended in HA. | `docs/RETRY.md` |
| `USE_SQL_SERVER=true` + `SQL_CONNECTION_STRING` | Move state (identity store, checkpoints, sync ts, dead-letter) to SQL Server. | `docs/SQL_CONTRACT.md` |
| `SQL_USE_MANAGED_IDENTITY=true` / `SQL_MAX_RETRIES=5` | Entra auth for SQL; transient-fault retry (AG failover). | `docs/RETRY.md` |
| `HA_MODE=true` | Active-active multi-node crawling (requires SQL backend). | `docs/HA.md` |
| `USE_KEY_VAULT=true` + `KEY_VAULT_URI` | Resolve `SECRET_*` from Azure Key Vault instead of env. | |
| `HEALTH_PORT=N` | Serve `/health`, `/ready`, `/metrics` (Prometheus). | `docs/OBSERVABILITY.md` |
| `LOG_FORMAT=json` | Structured one-object-per-line logs. | `docs/OBSERVABILITY.md` |
| `ALERT_WEBHOOK_URL` + `ALERT_DEADLETTER_THRESHOLD` | POST an alert on crawl failure / dead-letter growth. | `docs/OBSERVABILITY.md` |
| `IDENTITY_SYNC_ON_INCREMENTAL=true` | Run the (incremental) identity crawl on incremental cycles too. | |
| `GRAPH_CONNECTION_SHARDS={...}` | Shard objects across N Graph connections — the throughput lever. | `docs/SHARDING.md` |
| `GRAPH_BASE_URL` (+ `GRAPH_SCOPE`) | Sovereign-cloud Graph endpoint (e.g. `https://graph.microsoft.us`); scope defaults to `<base>/.default`. | |

New preflight command: `validate-config [--strict]` checks env, config files, schema
shape, and (best-effort) Salesforce/Graph connectivity before a long crawl.

## Usage

Run from the repository root (paths like `logs/` and `config/` resolve against the
current directory, exactly like the Python version):

```bash
dotnet run --project src/SalesforceCopilotConnector -- guide
dotnet run --project src/SalesforceCopilotConnector -- setup-connection --verbose
dotnet run --project src/SalesforceCopilotConnector -- full-deployment
dotnet run --project src/SalesforceCopilotConnector -- full-deployment --continuous --full-crawl-hours 24 --incremental-hours 4
dotnet run --project src/SalesforceCopilotConnector -- ingest --verbose
dotnet run --project src/SalesforceCopilotConnector -- ingest-item --id 001dN00000sh4neQAA
dotnet run --project src/SalesforceCopilotConnector -- ingest-object --type Case
dotnet run --project src/SalesforceCopilotConnector -- retry-failed --clear-on-success
dotnet run --project src/SalesforceCopilotConnector -- identity-dry-run --save --verbose
dotnet run --project src/SalesforceCopilotConnector -- validate-config --strict
```

(Help text intentionally still reads `run.py` — the CLI parser tests assert
byte-identical output with the Python original.)

## Running as a Windows service

The connector is SCM-aware: when the process is started by the Windows Service
Control Manager it automatically runs under a hosted-service lifetime (no extra
flags — the service's binary path just carries the normal CLI arguments).
Stopping the service is graceful and equivalent to the dashboard's Ctrl+X: the
in-flight chunk finishes, the pending Graph batch is flushed, and the checkpoint
is saved, so the next start resumes where it left off. Crash recovery is the
same story — state is checkpointed on disk.

Deploy:

```powershell
# 1. Publish
dotnet publish src/SalesforceCopilotConnector -c Release -r win-x64 -o C:\SFConnector

# 2. Lay out runtime files next to the exe
Copy-Item -Recurse config C:\SFConnector\config
Copy-Item -Recurse env    C:\SFConnector\env      # .env.local + .env.local.user

# 3. Install + start (elevated PowerShell)
.\scripts\install-windows-service.ps1 -InstallDir C:\SFConnector
Start-Service SalesforceCopilotConnector
```

The script registers the service (Automatic start, restart-on-crash) with
`full-deployment --continuous --full-crawl-hours 24 --incremental-hours 4` by
default — pass `-Arguments` to change the command/schedule, `-ServiceName` to
rename, `-Uninstall` to remove. Relative paths (`config/`, `env/`, `logs/`,
`data/`) resolve against `SFCONNECTOR_HOME`, which the script points at the
install directory. Logs stay in `SFCONNECTOR_HOME\logs\` — service mode
suppresses nothing; it writes the same log files as console mode.

## SQL Server backend & high availability

By default all state is file/SQLite based (byte-compatible with the Python
original). For production/HA, the state backend is switchable to Microsoft SQL
Server — identity store, checkpoints, sync timestamps and the dead-letter queue
all move to a shared database:

```
USE_SQL_SERVER=true
SQL_CONNECTION_STRING=Server=<AG-listener>;Database=SalesforceConnector;...
HA_MODE=true            # optional: multi-node active-active
```

Provision once with `scripts/sql/create-database.sql` (tables, views, stored
procedures) and `scripts/sql/create-login.sql` (least-privilege app login).

With `HA_MODE=true`, two or more nodes run the same `--continuous` command
against the same database (point the connection string at an Always On AG
listener). Nodes coordinate through SQL: one opens each scheduled crawl, the
rest join, and all of them claim Salesforce object types as atomic work items
with heartbeats — a dead node's claims expire and survivors resume from that
object's checkpoint. Exactly one node closes the crawl and writes the sync
timestamp. Details, failure modes and a deployment checklist: `docs/HA.md`;
schema/proc contract: `docs/SQL_CONTRACT.md`; retry/throttling behavior:
`docs/RETRY.md`; sustainable throughput vs Graph API limits: `docs/CAPACITY.md`.

## Tests

```bash
dotnet test
```

665 tests — a 1:1 port of the Python suite plus C#-side additions (SQL/HA,
log pruning, retry jitter). Test collections run serially
(`xunit.runner.json`) because several tests swap process-global seams
(ingest hooks, sync-state paths, HTTP session, env vars), mirroring the
Python suite's monkeypatching.

## Library mapping

| Python | C# |
|---|---|
| `requests` | `HttpClient` (async throughout) |
| `azure-identity` | `Azure.Identity` |
| `python-dotenv` | `DotNetEnv` |
| `rich` | `Spectre.Console` |
| `sqlite3` | `Microsoft.Data.Sqlite` |
| `pytest` | xUnit |

Original code © Microsoft Corporation, MIT License (see `LICENSE`/`NOTICE`).
Port conventions and deviations are documented in `CONVENTIONS.md`.
