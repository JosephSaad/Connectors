# Security

Operational security policy for the Clarizen Copilot Connector. Threat
analysis: `docs/THREAT_MODEL.md`. Host hardening:
`docs/DEPLOYMENT_ENTERPRISE.md`. Incident response: `docs/RUNBOOKS.md`.

## Supported versions

| Version | Supported |
|---|---|
| 1.x (latest release) | yes — security fixes land here |
| pre-1.0 builds | no — upgrade to the latest 1.x |

Only the newest 1.x release receives fixes; upgrading is deploy-over +
idempotent SQL script re-run (`docs/DR.md` § upgrade/rollback), so staying
current is cheap by design.

## Reporting a vulnerability

Email **security@cloudsconnected.co.uk** (subject `[clarizen-connector]`)
with reproduction detail. No public issues for security reports. Expect
acknowledgement within 2 business days, a triage verdict within 7, and
coordinated disclosure after a fix ships. No bounty program; credit given.

## Credential inventory & rotation runbooks

Every credential the connector holds, where it lives, and the tested rotation
path. `validate-config --strict` after any rotation proves the fix before the
service restarts a crawl.

### Clarizen API credentials (`CLARIZEN_USERNAME` + `SECRET_CLARIZEN_PASSWORD`)

1. Create/rotate the password on the dedicated Clarizen API user (least
   privilege: read access to the crawled objects + directory; nothing else).
2. Update `SECRET_CLARIZEN_PASSWORD` (Key Vault secret
   `secret-clarizen-password` when `USE_KEY_VAULT=true`, else
   `env/.env.local.user`).
3. Restart the service. Sessions are established per run — no dual-accept
   needed; an in-flight crawl on the old session finishes via its existing
   `sessionId` and the next login uses the new password.
4. Failure signature if step 2 was missed: `Clarizen login failed (HTTP 401)`
   → `docs/RUNBOOKS.md` § token / auth failure.

### Graph client secret (`SECRET_AAD_APP_CLIENT_SECRET`)

1. In Entra, ADD a second client secret on the app registration (both stay
   valid — Entra natively supports overlap; this is the zero-downtime window).
2. Update `SECRET_AAD_APP_CLIENT_SECRET` (vault name
   `secret-aad-app-client-secret`) and restart the service.
3. Verify a successful token (`Graph auth mode: client secret` + crawling),
   then DELETE the old secret in Entra.
4. Prefer moving to the certificate credential entirely (below) — secrets
   expire and leak more readily than keys.

### Graph client certificate (`GRAPH_CLIENT_CERT_PATH` / `GRAPH_CLIENT_CERT_THUMBPRINT`)

1. Generate the new key + certificate (PFX/PEM, RSA ≥ 2048).
2. Upload the new certificate's PUBLIC key to the Entra app registration —
   old and new certificates are BOTH valid during the overlap (zero-downtime).
3. Point `GRAPH_CLIENT_CERT_PATH` at the new file
   (+`GRAPH_CLIENT_CERT_PASSWORD` if encrypted), or install to the store and
   update `GRAPH_CLIENT_CERT_THUMBPRINT` (Windows store mode; grant the
   service account private-key read). Restart the service.
4. Verify `Graph auth mode: certificate (...)` in the log, then remove the
   old certificate from Entra and delete the old key file.
5. Precedence rules: certificate wins over secret; `PATH` wins over
   `THUMBPRINT`. The connector logs the mode, never key material.

### Webhook secret (`CLARIZEN_WEBHOOK_SECRET`)

The receiver holds ONE secret, read at service start — **dual-accept is not
supported by the current code**, so rotation has a brief reject window; it is
safe because polling is the correctness backstop (missed events are picked up
by the next incremental crawl). Honest procedure:

1. Generate the new secret (≥ 32 random bytes, e.g.
   `openssl rand -hex 32`).
2. Update `CLARIZEN_WEBHOOK_SECRET` and restart the connector (receiver now
   accepts only the NEW secret).
3. Immediately update the sender's signing secret.
4. Window between 2 and 3: sender posts fail with 401 (`Webhook: rejected a
   post with an invalid or missing 'X-Clarizen-Signature' signature.`) and a
   `webhook_events_rejected_total` blip — expected; the next incremental
   crawl reconciles anything missed. Keep the window short and schedule off
   change-heavy hours.
5. A 401 RATE that does not stop after step 3 is a forgery signal, not
   rotation residue → `docs/RUNBOOKS.md` § webhook flood / 401 spike.

### Other secrets

- **SQL** (`SQL_CONNECTION_STRING`): prefer `SQL_USE_MANAGED_IDENTITY=true`
  (nothing to rotate); otherwise rotate the SQL login and update the string.
- **Alert webhook** (`ALERT_WEBHOOK_URL` with embedded token): rotate at the
  receiving system, update the URL, restart. Alert delivery is best-effort by
  design — no window concern.
- **Key Vault access**: the connector uses `DefaultAzureCredential`
  (managed identity preferred) — rotate nothing; revoke by removing the
  identity's vault role.

## Data at rest inventory

What the connector persists, where, and its sensitivity — ACL/encrypt
accordingly (`docs/DEPLOYMENT_ENTERPRISE.md` has the ACL table; backup copies
inherit the same handling, `docs/DR.md`).

| Data | Location | Sensitivity |
|---|---|---|
| Secrets (env-file mode) | `env/.env.local.user` | HIGH — credentials |
| Graph client cert + key | `GRAPH_CLIENT_CERT_PATH` file or Windows store | HIGH — private key |
| Custom trust roots | `CA_BUNDLE_PATH` | HIGH — trust anchor (append = traffic inspection) |
| Dead-letter records | `logs/failed_records_<id>.jsonl` / `dbo.DeadLetter` | HIGH in `full` mode (real item payloads incl. financial values); LOW in `DEADLETTER_PAYLOAD_MODE=redacted` (ids + SHA-256 field hashes only) |
| Identity map | `data/<id>_identity.db` / `dbo.PrincipalMappings` | MEDIUM — personal data (names, emails, Entra ids) |
| Ingested-item inventory | `data/<id>_inventory.db` / `dbo.ItemInventory` | LOW — item ids + timestamps |
| Sync cursor / checkpoints | `logs/sync_state.json`, `checkpoint_*.json` / SQL | LOW |
| Logs | `logs/<prefix>_<timestamp>/connector.log` | MEDIUM — ids, errors; payload bodies are not logged (DEBUG logs sizes/counts) |
| Classification manifest (opt-in) | run dir JSONL | MEDIUM — per-item sensitivity labels |
| TDW exports (input) | `TDW_EXPORT_PATH` | HIGH — full source data; owned by the export job but ACL it like state |
| Graph external connection | Microsoft 365 tenant | governed by item ACLs the connector sets; financial-field policy per `FINANCIAL_DATA_MODE` |

SQLite files are not encrypted by the connector — rely on OS disk encryption
plus the ACL table. SQL Server deployments should enable TDE.

## Release integrity

`ClarizenConnector/.github/workflows/release.yml` describes the intended
release — a CycloneDX SBOM, SHA-256 checksums, and Authenticode (exe/MSI) plus
cosign signatures when the repository's signing secrets are configured (steps
skip gracefully when absent). That file is an **inert** leftover from when this
connector was its own repository: GitHub only executes the workflows at the
repository root, and there is no release workflow among them — so verify what
your channel actually publishes rather than assuming it was produced here.
Verify: `sha256sum -c <bundle>.zip.sha256`, `cosign verify-blob --key <pubkey>
--signature <artifact>.sig <artifact>`, `Get-AuthenticodeSignature` on
Windows binaries.
