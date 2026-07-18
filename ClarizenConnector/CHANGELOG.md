# Changelog

All notable changes to the Clarizen Copilot Connector are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versioning follows [SemVer](https://semver.org/).

## [Unreleased]

Enterprise hardening package.

### Added
- **Windows Event Log sink** (`EVENTLOG_ENABLED`, `EVENTLOG_LEVEL=info`):
  mirrors Error/Warning (and opt-in Info) plus service start/stop lifecycle to
  the Application log, source `ClarizenConnector`, stable event ids
  1000/1001/1002/2000/3000. No-op off Windows; the sink never throws.
  `install-windows-service.ps1` creates the source idempotently. `docs/SIEM.md`.
- **Proxy + custom trust roots** for every outbound HTTP client (Clarizen,
  Graph, alert webhooks): `PROXY_URL`, `PROXY_BYPASS`, `CA_BUNDLE_PATH`
  (additive PEM roots for TLS-inspecting proxies). Invalid values fail fast at
  startup naming the setting. `docs/DEPLOYMENT_ENTERPRISE.md`.
- **Certificate credential for Graph auth**: `GRAPH_CLIENT_CERT_PATH`
  (+`GRAPH_CLIENT_CERT_PASSWORD`) or `GRAPH_CLIENT_CERT_THUMBPRINT` (Windows
  store) build an RS256 `client_assertion` (x5t#S256, aud/jti/nbf/exp) instead
  of the client secret; the certificate wins when both are set. The auth mode
  is logged, key material never is. `SECURITY.md` has the rotation runbook.
- **Dead-letter payload protection**: `DEADLETTER_PAYLOAD_MODE=redacted`
  strips property/content values and response bodies from dead-letter records
  (ids, object type, error and per-field SHA-256 hashes are kept), covering
  the financial-classification paths; `retry-failed` re-fetches from source so
  redaction never reduces retryability. Unknown mode values fail fast.
- **HA lease gauge**: `clarizen_connector_ha_claims_held` on `/metrics`.
- **CI**: coverage job with an enforced line-coverage floor (72%, from 77.1%
  measured) and a perf-smoke job running both stress-test classes on a
  generous wall-clock budget.
- **Release**: CycloneDX SBOM attached to releases; Authenticode + cosign
  signing steps gated on repository secrets (skipped gracefully when absent);
  experimental WiX v5 MSI (`packaging/msi/`) built on a windows runner with
  ServiceInstall/ServiceControl and Event Log source registration.
- **Docs**: `docs/THREAT_MODEL.md` (STRIDE per trust boundary + FIPS audit),
  `docs/RUNBOOKS.md` (per alert/failure mode), `docs/DR.md` (RPO/RTO, backup/
  restore, upgrade/rollback, state-schema versioning), `docs/SIEM.md`
  (Event Log ids, Sentinel KQL, Splunk sketch), `docs/DEPLOYMENT_ENTERPRISE.md`
  (SCCM/Intune, GPO/DSC, proxy/TLS, least privilege), root `SECURITY.md`
  (supported versions, rotation runbooks, vuln reporting, data-at-rest
  inventory).
- **Ops artifacts**: `ops/grafana-dashboard.json`,
  `ops/prometheus-alerts.yml`, `ops/azure-monitor-alerts.kql` matching the
  RUNBOOKS anchors.

### Changed
- `SECRET_AAD_APP_CLIENT_SECRET` is now required only when no Graph client
  certificate is configured.
- Test suite grown from 516 to 575+ offline tests; two xUnit analyzer warnings
  in existing tests fixed (build is warning-clean on full rebuild).

### Security
- FIPS audit: no MD5/SHA1/DES/RC4/3DES anywhere in the codebase; HMAC-SHA256
  webhook validation and SHA-256 hashing throughout (see
  `docs/THREAT_MODEL.md` § FIPS).

## [1.0.0] - 2026-07-17

Baseline release: the complete connector as shipped before the enterprise
hardening package.

### Added
- Clarizen REST v2 client (session auth, CZQL paging, transparent re-login,
  daily API budget/rate limiter) and TDW bulk-export reader for full crawls.
- Graph external-connection provisioning, `$batch` ingest with adaptive
  concurrency, 429-hardened retry/backoff/jitter (60 s clamp), checkpointed
  resumable crawls, dead-letter queue + `retry-failed`, deletion sync with
  mass-deletion guards, `reconcile [--fix]`.
- ACL engine: Clarizen users/groups/project membership resolved to Entra
  principals; `projectMembers` / `ownerOnly` / `public` modes; fail-closed
  zero-principal skip; `FALLBACK_ACL_GROUP_ID`.
- Financial-field governance (`FINANCIAL_DATA_MODE=tag|filter|acl`), unified
  classification + sensitivity labeling, attachment content ingestion
  (dependency-free extraction, size/type caps).
- Webhook receiver (HMAC-SHA256 validate-before-parse, fail-closed secret,
  1 MiB body cap, debounce/coalesce) for event-driven incremental.
- State backends: files/SQLite or SQL Server (`USE_SQL_SERVER`), active-active
  HA (`HA_MODE`) with leased object claims, connection sharding.
- Operations: health/ready/metrics endpoints (Prometheus), webhook alerting,
  OpenTelemetry tracing + correlation ids, circuit breakers + degraded mode,
  structured JSON logs, log pruning, Key Vault secrets, Windows-service mode,
  Docker/compose, CI (ubuntu + windows + live SQL provisioning + CodeQL) and
  test-gated releases with checksummed bundles + GHCR image.
- 516 offline tests (mock HTTP, no network).

[Unreleased]: https://github.com/cloudsconnected/clarizen-connector/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/cloudsconnected/clarizen-connector/releases/tag/v1.0.0
