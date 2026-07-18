# Security Policy

## Supported versions

| Version | Supported |
| --- | --- |
| 1.0.x | yes — security fixes land here |
| pre-1.0 snapshots | no — upgrade to 1.0.x |

## Reporting a vulnerability

Report privately via the repository's **GitHub Security Advisories** ("Report a
vulnerability") — not via public issues. Include version (`CHANGELOG.md` /
assembly version), deployment mode (service/container, file/SQL backend), and
reproduction. Acknowledgement target: 72 hours. Please do not include captured
credentials or tenant content in reports.

Out of scope: findings requiring an already-compromised host/service account
(see docs/THREAT_MODEL.md residuals), and volumetric DoS against the webhook
listener beyond the documented caps.

## Secrets inventory & rotation runbooks

All secrets resolve through `SecretProvider` — env file (`env/.env.local.user`)
or Azure Key Vault (`USE_KEY_VAULT=true`; vault fetch failures are never
cached, so a rotated vault value is picked up on the next cycle without
restart; env-file changes need a service restart).

### Seismic OAuth client credentials (`SEISMIC_CLIENT_ID` / `SECRET_SEISMIC_CLIENT_SECRET`)

1. Create the NEW secret for the API client in Seismic Admin (or a parallel
   client with identical read scopes).
2. Update the vault secret (`secret-seismic-client-secret`) or
   `env/.env.local.user` on every node.
3. Env-file mode: restart the service per node (graceful — finishes the
   chunk). Vault mode: no restart; the cached token (≤1 h) drains and the next
   token call uses the new secret.
4. Verify: `validate-config` green; then revoke the OLD secret in Seismic.
5. Emergency (leak): revoke first, accept the crawl pause (breaker +
   degraded mode handle it), then steps 2–4.

### Graph app credential — client secret (`SECRET_AAD_APP_CLIENT_SECRET`)

1. App registration → add a NEW client secret (two active secrets is the
   supported overlap window).
2. Roll the value out (vault / env files) exactly as above.
3. Verify a crawl, then DELETE the old secret in Entra.
4. Calendar the expiry — `oauth-failure` in docs/RUNBOOKS.md is the symptom
   of letting it lapse.

### Graph app credential — certificate (`GRAPH_CLIENT_CERT_PATH` / `GRAPH_CLIENT_CERT_THUMBPRINT`)

Preferred over the secret (assertion is short-lived, key never leaves the
node; the certificate WINS over the secret when both are configured).

1. Issue/renew the cert (org PKI or self-signed per policy), upload the
   PUBLIC key to the app registration — Entra accepts multiple certs, so add
   the new one alongside the old.
2. Distribute: PFX file (+`GRAPH_CLIENT_CERT_PASSWORD` via secret channel) or
   machine store install, then update the path/thumbprint env var.
3. Restart nodes rolling; log line `Graph auth mode: certificate` confirms.
4. Remove the old cert from the app registration after the fleet is over.
5. Leak: remove the cert from the app registration FIRST (kills assertions
   minted with it), then reissue.

### Webhook shared secret (`SEISMIC_WEBHOOK_SECRET`) — mind the reject window

The validator holds **one** secret; there is **no dual-accept** during
rotation. Honest consequence: between changing the receiver and changing the
sender, one side's requests are 401-rejected. Order it to make the window
harmless:

1. Update the RECEIVER first (vault value or env + restart). From now until
   step 2, the sender's events are rejected — that is safe by design: every
   rejected event is healed by the next incremental crawl (polling is the
   safety net; nothing is lost, freshness dips for ≤ the reject window).
2. Update the SENDER (Seismic webhook config) to the new secret immediately
   after.
3. Verify: `webhook_rejected_total` stops rising and `webhook_accepted_total`
   resumes. A rejection spike at exactly the rotation time from the known
   sender is the expected signature of this window — the
   `webhook-401-spike` runbook distinguishes it from forgery.
4. Keep the window short and rotate off-peak; if near-real-time matters, drop
   `--incremental-hours` temporarily instead of inventing dual-accept.

### Others

| Secret | Rotation |
| --- | --- |
| SQL app login (`SQL_CONNECTION_STRING`) | Rotate the login password per org SQL policy; update the connection string (vault/env) + rolling restart |
| Key Vault access | Managed Identity — nothing to rotate; SP-based access follows the SP's own rotation |
| Authenticode / cosign release keys | CI secrets (`AUTHENTICODE_PFX_*`, `COSIGN_*`); rotate in the repo secret store; releases before rotation stay verifiable against the old public key |
| CA bundle (`CA_BUNDLE_PATH`) | Not a secret but a root of trust: change-controlled like one; removing a cert from the bundle is the revocation lever (chain building there does no CRL/OCSP) |

## Data at rest inventory

What the connector persists, where, and its sensitivity — encrypt-at-rest and
ACL these paths accordingly (BitLocker/TDE per org policy):

| Data | Location | Sensitivity |
| --- | --- | --- |
| Secrets | `env/.env.local.user` (unless Key Vault) | **credential** — service-account-only ACL |
| Identity map (Seismic principal ↔ Entra object id, emails as keys) | `data/{id}_identity.db` or SQL | personal data (directory-grade) |
| Tracked items (ids, versions, expiry, ACL fingerprints — SHA-256, no principals recoverable) | same | metadata |
| Dead-letter records | `logs/failed_records_*.jsonl` or `dbo.DeadLetter` | **may contain full item payloads + ACLs** in default `full` mode; set `DEADLETTER_PAYLOAD_MODE=redacted` to strip values (hash stubs remain) |
| Run logs | `logs/{run}/` | operational text; item ids/names appear; no secrets, tokens, signature values, or content bodies by policy |
| Reconciliation reports | `logs/{run}/reconciliation_*.jsonl` | compliance evidence (item ids + rule + action) — retain per policy |
| Checkpoints / sync cursor | `logs/*.json` or SQL | timestamps + ids only |
| Indexed content + ACLs | Microsoft Graph (tenant-side) | governed by M365, not by this repo |

## Hardening quick list

Fail-closed webhook (no secret → no listener) · HMAC over raw bytes,
constant-time, validate-before-parse · body/queue caps · never-widen ACL ·
No-MNE fail-closed with auditable reconciliation · least-privilege Graph
scopes (OwnedBy) · FIPS-clean crypto (docs/THREAT_MODEL.md) · signed releases
when org keys are configured, SBOM always.
