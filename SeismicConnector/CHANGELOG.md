# Changelog

All notable changes to the Seismic Copilot Connector. Versions follow
[SemVer](https://semver.org); the assembly version is pinned in
`src/SeismicConnector/SeismicConnector.csproj`.

## Unreleased — bank-grade hardening

### Changed (safe-default flips — operators please note)

* **No-MNE exclusions now fail closed.** A missing / empty / empty-object /
  `null` / malformed / rule-less `config/exclusions.json` is a hard startup
  error naming the file (was: silently "no rules"). To run rule-less on purpose
  set `{ "acknowledgeNoExclusions": true }`; `validate-config --strict` still
  FAILs a rule-less posture (docs/EXCLUSIONS.md).
* **Dead-letter payloads default to `redacted`.** `DEADLETTER_PAYLOAD_MODE`
  now defaults to `redacted` (was `full`); set `full` explicitly to keep
  verbatim payloads. An unrecognized value now fails fast at startup.
* **Incremental identity sync on by default.** `IDENTITY_SYNC_ON_INCREMENTAL`
  now defaults to `true`, so entitlements re-sync every incremental crawl
  (shrinks ACL-staleness lag to the incremental cadence). Residual lag is still
  non-real-time; pair with `PERMISSION_REACL` / scheduled `reacl` for unchanged
  content. Set the var to `false` to restore full-crawl-only sync.

### Added

* **Restrictive state-directory permissions** — `logs/` and `data/` are created
  owner-only at startup (POSIX `0700`; best-effort owner+admins NTFS ACL on
  Windows). Never fatal if it cannot be set (logs a warning).
* **Webhook anti-replay** — a signed timestamp bound into the HMAC
  (`timestamp + "." + body`) with a freshness window
  (`SEISMIC_WEBHOOK_REPLAY_WINDOW_SECONDS`, default 300s) plus duplicate-signature
  rejection within the window. Required by default
  (`SEISMIC_WEBHOOK_REQUIRE_TIMESTAMP=true`); set false to migrate legacy
  senders (body-only HMAC). Validate-before-parse preserved.
* **Immutable decision ledger** — `DECISION_LEDGER=true` writes an append-only,
  SHA-256 hash-chained audit of exclusion and ACL-restriction decisions with an
  offline-verifiable chain (tamper-evident).
* **Stale-index TTL** — `GRAPH_ITEM_TTL_DAYS>0` stamps each item with a rolling
  `expirationDateTime = now + TTL` so the index self-expires if crawling stops.
* **Optional classification-enforced ACL** — `CLASSIFICATION_ENFORCE_ACL=true`
  (+ `CLASSIFICATION_ENFORCE_GROUP`) locks top-tier (Restricted) items' Graph
  ACL to an Entra group. Default off.

### Fixed / clarified

* **Classification honesty** — the `sensitivityLabel` property is documented
  everywhere as an ADVISORY, connector-applied classification tag (Purview-*aligned*
  in naming only), NOT a Purview-enforced sensitivity label. The shipped Graph
  schema property name is unchanged (wire back-compat).

## 1.0.0 — 2026-07-18

First versioned release: the connector chassis plus the enterprise-grade
hardening package.

### Added

* **Windows Event Log sink** — `EVENTLOG_ENABLED=true` mirrors WARNING+
  (INFO with `EVENTLOG_LEVEL=info`) and lifecycle start/stop marks to the
  Application log, source `SeismicConnector`, stable event ids 1000/1100/2000/3000
  (docs/SIEM.md). Source created idempotently by
  `scripts/install-windows-service.ps1`. Non-Windows: no-op. Never throws.
* **Proxy + custom CA trust** — `PROXY_URL` / `PROXY_BYPASS` route every
  outbound client (Seismic OAuth2+API, Graph, alert webhooks) through a
  forward proxy; `CA_BUNDLE_PATH` adds PEM roots (TLS inspection / private
  PKI) via additive `X509Chain` CustomRootTrust. Hostname mismatches are never
  excused; misconfiguration fails fast naming the setting.
* **Graph certificate credential** — `GRAPH_CLIENT_CERT_PATH` (+`_PASSWORD`)
  or `GRAPH_CLIENT_CERT_THUMBPRINT` switch Graph auth from client secret to an
  RS256 `client_assertion` JWT (x5t#S256 binding, fresh jti, 10-minute
  lifetime). Certificate wins over secret; only the auth MODE is logged.
* **Dead-letter payload protection** — `DEADLETTER_PAYLOAD_MODE=redacted`
  strips indexed content and property values from dead-letter records (file
  and SQL backends), keeping ids, teamsite/version, error/attempt metadata,
  ACL entries and SHA-256 stubs. `retry-failed` is unaffected — it re-fetches
  from Seismic.
* **Webhook + HA observability** — new metrics
  `webhook_accepted_total`, `webhook_rejected_total`, `webhook_dropped_total`,
  `webhook_queue_depth`, `ha_claims_acquired_total`, `ha_claims_held`.
* **Ops pack** — `ops/grafana-dashboard.json`,
  `ops/prometheus-alerts.yml`, `ops/azure-monitor-alerts.kql` wired to the
  runbook anchors.
* **Enterprise docs** — docs/THREAT_MODEL.md, docs/RUNBOOKS.md, docs/DR.md,
  docs/SIEM.md, docs/DEPLOYMENT_ENTERPRISE.md, SECURITY.md.
* **Release engineering** — CycloneDX SBOM per release; Authenticode and
  cosign signing (gracefully skipped until signing secrets are configured);
  experimental WiX v5 MSI (`packaging/msi/`, artifact-only); CI coverage gate
  (line coverage ≥ 57%; measured 62.0% at introduction) and perf-smoke job
  over the stress classes.

### Security

* FIPS audit: the codebase uses only SHA-256-family primitives
  (HMAC-SHA256 webhook authentication, SHA-256 ACL fingerprints/redaction
  stubs). No MD5/SHA-1/DES/RC4/3DES anywhere — no migration required
  (docs/THREAT_MODEL.md).
