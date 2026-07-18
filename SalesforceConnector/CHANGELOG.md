# Changelog

All notable changes to the Salesforce Copilot Connector (C#) are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Enterprise hardening package.

### Added
- **Windows Event Log sink** (`EVENTLOG_ENABLED=true`, `EVENTLOG_LEVEL=info` opt-in):
  WARNING/ERROR records and service lifecycle events mirrored to the Application
  log (source `SalesforceConnector`, event ids 1000/2000/3000); strict no-op off
  Windows; the sink never throws. `install-windows-service.ps1` now creates the
  event source idempotently. See `docs/SIEM.md`.
- **Proxy + custom CA support**: `PROXY_URL` / `PROXY_BYPASS` explicit proxy
  config (`HTTPS_PROXY` et al. still honored by default), and `CA_BUNDLE_PATH`
  PEM of additional trusted roots for TLS-inspection environments (additive to
  the system store; broken config fails fast naming the setting). Applied to
  every outbound HttpClient. See `docs/DEPLOYMENT_ENTERPRISE.md`.
- **Certificate credential for Graph auth**: `GRAPH_CLIENT_CERT_PATH` (PFX,
  optional `GRAPH_CLIENT_CERT_PASSWORD`) or `GRAPH_CLIENT_CERT_THUMBPRINT`
  (CurrentUser/LocalMachine `My` store) switch token requests to the
  `client_assertion` flow (RS256 JWT, `x5t#S256`, aud = tenant token endpoint,
  jti/nbf/exp). Certificate wins over client secret; mode logged at startup;
  key material never logged. See `SECURITY.md` for rotation.
- **Dead-letter payload protection**: `DEADLETTER_PAYLOAD_MODE=full|redacted`
  (default `full`). Redacted mode strips item property values/content from
  dead-letter records (keeps ids, object type, error, timestamps, ACLs, field
  names, and sha256 hashes of removed values) so CRM PII does not sit on disk;
  the trade-off note is embedded in each record. `retry-failed` is unaffected —
  it re-fetches items from Salesforce.
- New `/metrics` gauges: `adaptive_concurrency_level`, `ha_claims_held`, and
  per-object `object_records_total{object_type}` / `object_records_fetched{object_type}`.
- Ops artifacts: `ops/grafana-dashboard.json`, `ops/prometheus-alerts.yml`,
  `ops/azure-monitor-alerts.kql` (each alert names its `docs/RUNBOOKS.md` anchor).
- Docs: `docs/THREAT_MODEL.md` (STRIDE per trust boundary + FIPS audit),
  `docs/RUNBOOKS.md` (per-alert runbooks), `docs/DR.md` (RPO/RTO, backup/restore,
  upgrade/rollback), `docs/SIEM.md` (Event Log/Sentinel/Splunk ingestion),
  `docs/DEPLOYMENT_ENTERPRISE.md` (SCCM/Intune/GPO, proxy/TLS, least privilege),
  `SECURITY.md` (supported versions, secret rotation, data-at-rest inventory).
- CI: code-coverage gate (line ≥ 47.9%; measured 52.9% at introduction) and a
  perf-smoke job (20k items; floors ≥ 3,000 items/s, < 500 MB RSS).
- Release: CycloneDX SBOM attached to releases; Authenticode (win-x64 binary)
  and cosign (container image) signing steps, gated on `SIGNING_*` secrets and
  skipped with a notice when absent; experimental WiX v5 MSI job
  (`packaging/msi/`).
- StressHarness `--summary-json FILE` for machine-readable perf results.

### Changed
- `SalesforceCopilotConnector.csproj` now carries `<Version>1.0.0</Version>`.
- All outbound HTTP clients are constructed through `Infrastructure/HttpClientFactory`
  (behavior unchanged when the new env vars are unset).

### Security
- FIPS audit of `src/`: no SHA-1/DES/RC4/3DES. MD5 appears only as the
  `instance_hash` cache key in the identity stores — identity-critical
  (state-compatible with the Python original), retained and documented with a
  migration note in `docs/THREAT_MODEL.md`. All new hashing is SHA-256.

## [1.0.0] - 2026-07-17

First stable release: a complete, state-compatible C#/.NET 10 port of
Microsoft's Python Salesforce → Microsoft 365 Copilot connector, hardened for
production service on Windows Server and Linux.

### Added
- Full command surface: `guide`, `setup-connection`, `full-deployment` (with
  `--continuous` scheduling), `ingest`, `ingest-item`, `ingest-object`,
  `retry-failed`, `identity-dry-run`, `validate-config [--strict]`,
  `reconcile [--type X] [--fix]`.
- Both ACL engines (legacy resolver and the modular `AclEngine/` with OWD,
  share fetcher, group/role/territory/queue handlers, principal mapper) and the
  identity crawl/publisher pipeline.
- Byte-compatible on-disk state with the Python original: sync-state JSON,
  checkpoints, dead-letter JSONL, SQLite identity store — plus a switchable
  SQL Server state backend (`USE_SQL_SERVER`) with schema/procs in `scripts/sql/`.
- Active-active HA (`HA_MODE=true`): SQL-coordinated crawl open/join, atomic
  object claims with heartbeats, dead-node reclaim, exactly-one crawl close.
- Connection sharding (`GRAPH_CONNECTION_SHARDS`), including intra-object hash
  sharding for the Graph item quota.
- Deletion sync: inventory-backed existence sweep with a mass-deletion guard
  (`DELETION_SYNC`, `DELETION_SYNC_MAX_PERCENT`), plus `reconcile --fix` drift
  repair.
- Observability: `/health` `/ready` `/metrics` (Prometheus) via `HEALTH_PORT`,
  `LOG_FORMAT=json` structured logs, webhook alerting
  (`ALERT_WEBHOOK_URL` / `ALERT_DEADLETTER_THRESHOLD`), log retention pruning.
- Stress hardening: adaptive Graph concurrency (dials down on 429s), retry
  jitter (`GRAPH_RETRY_JITTER`), checkpoint/resume at chunk boundaries, a
  5-scenario stress harness (`tools/StressHarness`) with 44 correctness
  invariants wired into CI.
- Diagnosability: silent-drop fixes across the pipeline (dead-letter capture on
  every failure path), per-phase timing logs, run summaries, corrupt
  state-file diagnostics naming file and line.
- Windows service mode (SCM-aware, graceful chunk-boundary stop) with
  `scripts/install-windows-service.ps1`; Docker image and compose file.
- 845-test suite (1:1 port of the Python suite + C# additions), test-gated
  releases with self-contained win-x64/linux-x64 bundles and a GHCR container
  image.

[Unreleased]: https://github.com/JosephSaad/SalesforceCopilotConnector/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/JosephSaad/SalesforceCopilotConnector/releases/tag/v1.0.0
