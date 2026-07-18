# Changelog

All notable changes to the Altrata Copilot Connector. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[SemVer](https://semver.org/). Version bumps touch THREE files together:
`src/AltrataConnector/AltrataConnector.csproj` (`<Version>`),
`packaging/msi/Package.wxs` (`Package/@Version` — drift is test-enforced),
and this file.

## [1.0.0] - 2026-07-18

First GA release: the full connector chassis plus the enterprise hardening
package.

### Added — enterprise hardening package

- **Windows Event Log mirroring** (`EVENTLOG_ENABLED=true`): WARNING/ERROR
  lines and lifecycle markers mirrored to the Application log, source
  `AltrataConnector`, stable event ids 1000/2000/3000 (docs/SIEM.md). PII-safe
  by construction (same message text as the file sink; enforced by tests).
  Idempotent event-source creation in `scripts/install-windows-service.ps1`.
- **Proxy + custom CA** (`PROXY_URL`, `PROXY_BYPASS`, `CA_BUNDLE_PATH`): all
  connector HTTP (Graph, Entra token, Altrata API, alert webhook) honours an
  explicit forward proxy; a PEM bundle adds TLS-inspection/private-PKI roots
  ADDITIVELY (system trust keeps working; hostname mismatches never forgiven).
  Bad input fails fast naming the setting.
- **Certificate credential for Graph** (`GRAPH_CLIENT_CERT_PATH` +
  `GRAPH_CLIENT_CERT_PASSWORD`, or `GRAPH_CLIENT_CERT_THUMBPRINT`): RS256
  client-assertion auth (x5t#S256 / aud / jti / nbf / exp); certificate WINS
  over `SECRET_AAD_APP_CLIENT_SECRET`, which becomes optional; only the auth
  MODE is logged.
- **Dead-letter payload protection** (`DEADLETTER_PAYLOAD_MODE`, default
  `redacted` — decision record in SECURITY.md): the queue carries ids /
  subject-hashes / error / attempts only; `retry-failed` re-fetches redacted
  upserts from the checksum-verified feed delivery. `forget-subject` scrubs
  queued upserts/transforms for the erased subject; `retry-failed` refuses to
  replay upserts for suppressed subjects (erasure-completion DELETEs exempt);
  replays now restore the item↔subject reverse index.
- **New operational metrics**: `altrata_graph_throttle_429_total`,
  `altrata_entitlement_refusals_total`, `altrata_erasure_ledger_broken`,
  `altrata_match_review_depth`, `altrata_ha_leases_held`.
- **Ops pack**: `ops/grafana-dashboard.json`, `ops/prometheus-alerts.yml`,
  `ops/azure-monitor-alerts.kql` (ledger-tamper alerting classed as a
  SECURITY incident).
- **Enterprise docs**: `docs/THREAT_MODEL.md` (STRIDE + FIPS audit + DSAR
  posture), `docs/RUNBOOKS.md`, `docs/DR.md`, `docs/SIEM.md`,
  `docs/DEPLOYMENT_ENTERPRISE.md`, root `SECURITY.md`.
- **CI/CD**: CycloneDX SBOM on releases; Authenticode + cosign signing gated
  on repo secrets (graceful skip); coverage gate (measured 70.19% line at
  authoring, threshold 65.19%); perf-smoke job over the stress suites;
  experimental WiX v5 MSI job (`packaging/msi/`).

### Chassis (carried into 1.0.0)

- Seat-only entitlement (never-everyone, fail-closed), batched re-ACL on seat
  changes; licensed feed ingestion with per-file SHA-256 manifests, TOCTOU-safe
  reads, reconciliation reports; delta tombstones; per-subject DSAR erasure
  with durable suppression list and tamper-evident hash-chained ledger;
  entity resolution with review queue; relationship-path materialization;
  Graph $batch pipeline with adaptive concurrency and 429 ladder; circuit
  breakers + degraded-mode pause; SQL Server backend + HA leases
  (close-with-failed-claims); OpenTelemetry tracing with a PII-safe tag
  allowlist; correlation ids end-to-end; Windows service host with graceful
  chunk-boundary stop; Docker + compose topology.

[1.0.0]: https://example.com/releases/tag/v1.0.0
