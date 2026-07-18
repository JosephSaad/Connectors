# Changelog

All notable changes to the BDH Hadoop Copilot Connector. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[SemVer](https://semver.org/). Assembly version: `<Version>` in
`src/HadoopConnector/HadoopConnector.csproj`; release tags are `v<version>`.

## [Unreleased]

Bank-grade hardening follow-ups. Two safe-default flips (operators should note
them); everything else is additive and off/unchanged by default.

### Changed (safe-default flips — action may be required)

- **Dead-letter payload mode now defaults to `redacted`** (was `full`):
  `DEADLETTER_PAYLOAD_MODE` unset no longer stores record VALUES in the
  dead-letter queue — only ids, object type, error, property names, sizes and
  SHA-256 hashes. Set `DEADLETTER_PAYLOAD_MODE=full` to restore the verbatim
  payloads for fast diagnosis. An **unrecognized value now fails fast at config
  load** (a typo can no longer silently pick a mode). `validate-config` reports
  it too. (`Config/DeadLetterRedaction.cs`, `Config/AppConfig.cs`)
- **`IDENTITY_SYNC_ON_INCREMENTAL` now defaults to ON**: the entitlement
  (BDH→Entra) mapping re-syncs on incremental crawls too, shrinking entitlement
  lag to the incremental cadence. Set it `false` to restrict identity sync to
  full crawls. Residual, non-real-time lag documented (an item's ACL is only
  re-emitted when its source record changes — schedule full crawls at your
  entitlement-freshness SLA). (`Infrastructure/EnvFlags.cs`)

### Added

- **Restrictive filesystem permissions at startup**: the local state
  directories (logs / state / dead-letter) are created **owner-only** — POSIX
  `0700`; on Windows a best-effort `icacls` lock-down (owner + Administrators +
  SYSTEM, inheritance broken). Best-effort, never fatal.
  (`Infrastructure/SecureDirectories.cs`)
- **Optional classification ACL enforcement** (`CLASSIFICATION_ENFORCE_ACL` +
  `CLASSIFICATION_RESTRICTED_GROUP_ID`, default OFF): when on, top-tier
  (`Restricted`) items have their ACL narrowed to the configured Entra group so
  the classification tag actually gates retrieval. (`Graph/Ingest.cs`)
- **Stale-index expiry** (`GRAPH_ITEM_TTL_DAYS`, default unset): stamps ingested
  items with `expirationDateTime = now + TTL` so the index self-expires if
  crawling stops. (`Graph/Models.cs`, `Item/ItemConverter.cs`)
- **Immutable decision ledger** (`DECISION_LEDGER`, default ON): append-only,
  SHA-256 hash-chained audit of EXCLUSION and ACL_RESTRICTION decisions with a
  `Verify()` that detects any edit, deletion or reorder.
  (`Infrastructure/DecisionLedger.cs`)

### Documentation / honesty

- Classification naming/docs corrected: `SensitivityLabel` is a
  connector-applied **advisory tag** (a Graph refiner), **not** a Microsoft
  Purview-enforced label — it does not encrypt or gate access on its own (the
  wire schema property name is unchanged for back-compat).

## [1.0.0] — 2026-07-18

First production release: the full connector chassis plus the enterprise
hardening package.

### Core connector

- BDH source access over WebHDFS (LISTSTATUS/OPEN, retry ladder with exact
  Retry-After handling, circuit breaker) or a mounted export directory
  (`HDFS_MODE=localpath`); Hive-partition scanner with dt-watermark and
  partition-filter pruning; hardened streaming CSV/JSONL parser with bounded
  reads (`BDH_MAX_FILE_BYTES`).
- The filter layer (`config/filters.json`): partition pruning → streamed
  record predicates → row cap, strict load-time validation, per-stage
  accounting, and the **fail-closed scale guard** (an object with no effective
  filter refuses to crawl; `dt isNotNull`-only filters do not count as
  effective).
- Graph ingestion: $batch with adaptive concurrency, checkpointed resume,
  dead-letter + `retry-failed`, deletion sweep with mass-deletion guards
  (absolute cap + percent guard + empty-source engagement) and sweep
  suppression on ANY incomplete fetch (row cap or oversize skip); reconcile
  with the same truncation safety; connection sharding; sovereign-cloud
  endpoints.
- Coarse ACL engine (ownerOnly/group/public), identity sync from the BDH User
  export with **fail-loud incomplete-directory refusal**, SQLite/SQL Server
  identity stores.
- Operations: unified CLI, Windows-service mode with graceful stop, SQL state
  backend + active-active HA (leased object claims, close-with-failed-claims),
  health/readiness/metrics endpoints, webhook alerting, OpenTelemetry tracing
  with correlation ids, circuit breakers + degraded mode, optional content
  classification + sensitivity labeling, Key Vault secret resolution.

### Enterprise hardening package (this release)

- **Windows Event Log sink** (`EVENTLOG_ENABLED`, source `HadoopConnector`,
  log `Application`): mirrors Warning/Error (+Info with `EVENTLOG_LEVEL=info`)
  and lifecycle start/stop events with stable event ids for SIEM collection
  (`docs/SIEM.md`); never throws; no-op off-Windows; idempotent source
  registration in `scripts/install-windows-service.ps1`.
- **Enterprise egress**: `PROXY_URL`/`PROXY_BYPASS` outbound proxy with
  wildcard bypass, and `CA_BUNDLE_PATH` additive PEM trust (private CAs on
  WebHDFS / TLS-inspecting proxies) via a custom-root-trust chain rebuild —
  both fail fast naming the setting; wired into the WebHDFS, Graph and
  alerting clients.
- **Certificate credential for Graph** (`GRAPH_CLIENT_CERT_PATH` /
  `GRAPH_CLIENT_CERT_PASSWORD` / `GRAPH_CLIENT_CERT_THUMBPRINT`): RFC 7523
  client-assertion JWT (RS256, `x5t#S256`, aud/jti/nbf/exp), certificate wins
  over a configured client secret, auth MODE logged only.
- **Dead-letter payload protection** (`DEADLETTER_PAYLOAD_MODE=full|redacted`):
  redacted mode strips record values before either backend writes, keeping
  ids, object type, error, property names, sizes and SHA-256 hashes;
  `retry-failed` (including the oversize-inconclusive keep rule) is unaffected;
  unknown mode values fail toward redaction.
- **FIPS posture**: audited — no MD5/SHA-1/DES/RC4/3DES anywhere; all hashing
  and signing added by this release is SHA-256/RSA (`docs/THREAT_MODEL.md`).
- **Ops pack**: new `guard_refusals_total`, `partial_objects_total`,
  `sweeps_suppressed_total` counters and `ha_claims_held` gauge;
  `ops/grafana-dashboard.json`, `ops/prometheus-alerts.yml`,
  `ops/azure-monitor-alerts.kql` keyed to `docs/RUNBOOKS.md`.
- **CI/CD**: coverage gate (measured 79.87% line at 650 tests; floor 74.87%),
  perf-smoke job on the StressHarness filter-scale scenario (≥100k rows/s,
  <500 MB RSS), CycloneDX SBOM on releases, Authenticode + cosign signing
  gated on secrets (graceful skip), experimental WiX v5 MSI
  (`packaging/msi/`).
- **Docs**: `docs/THREAT_MODEL.md`, `docs/RUNBOOKS.md`, `docs/DR.md`,
  `docs/SIEM.md`, `docs/DEPLOYMENT_ENTERPRISE.md`, `SECURITY.md`.

### Test suite

650 offline tests (no network); StressHarness scenarios (`--scenario all`)
cover 10^5–10^6-row behaviour of the real pipeline components.
