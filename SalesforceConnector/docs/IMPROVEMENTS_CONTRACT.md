# Improvements Contract (14-item hardening pass)

Binding coordination doc for the parallel hardening work. Repo:
/Users/joseph/Teams/SalesforceCopilotConnector. Read CONVENTIONS.md and
docs/SQL_CONTRACT.md first.

## Golden rule: OFF BY DEFAULT
Every feature here is gated by an env var and is a strict no-op when unset. With
no new env vars set, behavior/logs/state must be **byte-identical** to today and
all 526 existing tests must still pass untouched. New features get NEW tests.

## File ownership (Wave 1 — agents must stay in their lane)
Do NOT edit files outside your list. In particular NOBODY in Wave 1 edits:
`README.md`, `env/README.md`, `Commands/CommandRegistry.cs`, `Commands/Deploy.cs`,
`Commands/IngestCommand.cs`, or `Graph/Ingest.cs` — the orchestrator wires those
in Wave 2. Instead, RETURN the env-var rows and the exact integration call you
need, and the orchestrator will place them.

If you add docs for your feature, create a NEW file under `docs/` (e.g.
`docs/OBSERVABILITY.md`) — do not append to README/env README.

Only ONE agent adds NuGet packages (the Key Vault agent). If you think you need a
package, use a BCL alternative instead (HttpListener, System.Diagnostics.Metrics,
Microsoft.Data.SqlClient's built-in ConfigurableRetryLogic which is already
referenced). Say so in your report if you truly cannot.

## Standard env-var names (use EXACTLY these)
| Feature | Env var | Default | Meaning |
|---|---|---|---|
| Key Vault secrets (#7) | `USE_KEY_VAULT` | `false` | Resolve `SECRET_*` from Key Vault instead of env. |
| | `KEY_VAULT_URI` | — | Vault URL. Secret name = env var name lowercased, `_`→`-`. |
| SQL Managed Identity (#7/#1) | `SQL_USE_MANAGED_IDENTITY` | `false` | SQL auth via Entra token instead of connstring creds. |
| SQL resilience (#1/#8) | `SQL_MAX_RETRIES` | `5` | Transient-fault retry count for SQL ops. |
| Structured logs (#10) | `LOG_FORMAT` | `text` | `json` → one JSON object per log line. |
| Health/metrics (#9) | `HEALTH_PORT` | `0` | `>0` → serve `/health`, `/ready`, `/metrics` on that port. |
| Alerting (#11) | `ALERT_WEBHOOK_URL` | — | POST JSON on crawl failure / dead-letter threshold. |
| | `ALERT_DEADLETTER_THRESHOLD` | `0` | `>0` → alert when dead-letter depth exceeds it. |
| Incremental identity (#12) | `IDENTITY_SYNC_ON_INCREMENTAL` | `false` | Run identity sync on incremental crawls too. |
| Connection sharding (#2) | `GRAPH_CONNECTION_SHARDS` | — | JSON `{ "<connectionId>": ["Account","Contact"], ... }`. When set, each shard is its own Graph connection + schema, ingesting only its object types. |

## Required seams (public entry points Wave 2 calls)
- #7 → `Infrastructure/SecretProvider.cs`: `static string? GetSecret(string envVarName)`
  — returns Key Vault value when `USE_KEY_VAULT=true`, else `Environment.GetEnvironmentVariable`.
  `Salesforce/Settings.cs` secret reads route through it (you own that edit).
- #1/#8 → `Infrastructure/SqlExecutor.cs`: helpers the SQL callers use to (a) build a
  hardened `SqlConnection` (force `Encrypt=True` unless the connstring already sets Encrypt;
  add MI auth when `SQL_USE_MANAGED_IDENTITY=true`) and (b) execute with transient-fault
  retry (`SQL_MAX_RETRIES`, exponential backoff, honor SqlException.IsTransient / known
  transient numbers incl. AG failover 40197/40501/40613/49918/10928/10929/1205/-2/64/233).
  Edit SqlServerIdentityStore.cs, SqlStateStore.cs, HaCoordinator.cs to route every
  connection-open/execute through it. Behavior identical on the happy path.
- #9/#10/#11 → `Infrastructure/HealthEndpoint.cs`: `static IDisposable? StartIfConfigured(AppConfig config)`
  (null when `HEALTH_PORT=0`). Serves liveness/readiness + a `/metrics` text exposition
  (Prometheus format) sourced from a static `Metrics` registry you add and from dead-letter
  depth (via `SyncState.ReadFailedRecords`/SQL). `Infrastructure/Alerting.cs`:
  `static Task RaiseAsync(string kind, string message, object? data)`. Do NOT edit Ingest.cs;
  read from IngestionStats snapshots / SyncState. Logging JSON is env-switched inside Logging.cs.
- #12 → extend `Graph/Identity.cs` (or AclEngine identity handler) with an incremental path,
  exposed so Wave 2 can call it on incremental crawls (e.g. an overload/param on
  `RunIdentitySyncAsync`). Use the identity store's last-session watermark. Own only identity files.
- #2 → `Salesforce/ShardingConfig.cs`: parse+validate `GRAPH_CONNECTION_SHARDS`
  (`TryLoad(out shards, out error)`; every schema object assigned to exactly one shard, no
  unknowns, no dups). Provide `AppConfig ForShard(AppConfig baseConfig, string connectionId,
  IReadOnlyList<string> objectTypes)` that clones the config with that connection id and an
  object-type restriction the ingest pipeline already honors (study how object list / schema
  drives ingestion; reuse the existing single-object mechanism if that's the clean seam). Do
  NOT edit command files or Ingest.cs — the orchestrator writes the per-shard loop.
- #13 → `Commands/ValidateConfig.cs`: `static Task<bool> RunAsync(ParsedArgs args)` — validate
  env (required vars present), config files parse, `schema.json` objects/fields shape, OWD/parent
  map sanity, Graph + Salesforce auth reachable (best-effort, network optional: if creds absent
  just check presence). Print a clear PASS/FAIL report. NOT registered in the parser (Wave 2 does).

## Tests
xUnit; put env-touching tests in the existing `"EnvVars"` collection with save/restore.
SQL/network tests self-skip unless the relevant `*_TEST_*` env var is set (match the existing
SqlServerIdentityStoreTests skip pattern). Do not weaken existing tests.

## Build hygiene
Parallel `dotnet build`/`test` can transiently contend on obj/bin — if a build fails oddly,
retry once. The orchestrator runs the authoritative build/test/stress in Wave 2.
