# Salesforce Copilot Connector (C#)

A complete C#/.NET 8 port of Microsoft's [Salesforce-Custom-Copilot-Connector](https://github.com/microsoft/Salesforce-Custom-Copilot-Connector)
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
| `tests/SalesforceCopilotConnector.Tests/` | `tests/` — full pytest suite as xUnit (450 tests) |
| `config/` | schema.json, graph-schema.json, template.json (same files) |

## Requirements

- .NET 8 SDK
- The same environment variables as the Python version (see `env/README.md`);
  `.env.local` / `env/.env.local` files are loaded the same way.

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
```

(Help text intentionally still reads `run.py` — the CLI parser tests assert
byte-identical output with the Python original.)

## Tests

```bash
dotnet test
```

450 tests, a 1:1 port of the Python suite. Test collections run serially
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
