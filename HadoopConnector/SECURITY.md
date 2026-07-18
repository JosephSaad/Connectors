# Security

Security posture, supported versions, credential-rotation runbooks,
vulnerability reporting, and the data-at-rest inventory for the BDH Hadoop
Copilot Connector. Deep dives: `docs/THREAT_MODEL.md` (STRIDE per boundary),
`docs/DEPLOYMENT_ENTERPRISE.md` (hardening), `docs/SIEM.md` (detection).

## Supported versions

| Version | Supported |
|---|---|
| 1.0.x | security fixes |
| < 1.0 (pre-release builds) | not supported — upgrade |

The dependency graph ships as a CycloneDX SBOM with every release
(`hadoop-connector.cdx.json`); CodeQL (security-and-quality) runs weekly and
on every push/PR.

## Reporting a vulnerability

Report privately to the repository owners (GitHub private vulnerability
reporting / security advisories on this repository — do NOT open a public
issue with exploit detail). Include version, config shape (redacted), and
reproduction. Acknowledgement target: 5 business days. Please treat anything
that lands credential material in logs/telemetry as in-scope and severity-high
(the delegation-token canary in `docs/SIEM.md` exists for exactly that class).

## Credential inventory & rotation runbooks

No credential appears on the command line or in logs (auth MODE is logged,
material never). All `SECRET_*` values resolve from `env/.env.local.user` or
Azure Key Vault (`USE_KEY_VAULT=true`; vault name mapping
`SECRET_X_Y` → `secret-x-y`).

### Rotate the Entra client secret (`SECRET_AAD_APP_CLIENT_SECRET`)

1. Entra portal → the app registration → add a NEW client secret (overlap
   window; do not delete the old one yet).
2. Stage the new value: Key Vault secret update
   (`secret-aad-app-client-secret`) or `env/.env.local.user` on each node.
3. Restart the service per node (rolling in HA). The token cache refreshes on
   next acquisition; no state migration.
4. Verify: `validate-config --strict` (Graph connectivity), then delete the
   OLD secret in Entra.
5. Note: if `GRAPH_CLIENT_CERT_*` is configured the certificate WINS and the
   secret is unused — rotate it anyway if it exists, or remove it from the app
   registration entirely (preferred end-state).

### Rotate the Graph client certificate (`GRAPH_CLIENT_CERT_*`)

1. Issue/renew the certificate (internal CA or self-signed per policy;
   RSA ≥ 2048).
2. Upload the NEW public cert to the app registration (both certs valid
   during the overlap).
3. Stage it node-side: install into the machine store and update
   `GRAPH_CLIENT_CERT_THUMBPRINT`, or replace the PFX at
   `GRAPH_CLIENT_CERT_PATH` (+ `GRAPH_CLIENT_CERT_PASSWORD`). Grant the
   service account private-key Read (store mode).
4. Rolling restart; verify the `Graph auth mode: certificate` log line and a
   successful crawl; remove the OLD cert from the app registration.

### Rotate HDFS credentials

- **Simple auth (`HDFS_USER`)**: it is an identity assertion, not a secret;
  rotating means changing the principal — coordinate with the cluster's authz
  (keep it read-only on `{BDH_ROOT_PATH}`, `docs/DEPLOYMENT_ENTERPRISE.md`).
- **Delegation token (`SECRET_HDFS_DELEGATION_TOKEN`)**: obtained and RENEWED
  out-of-band (`hdfs fetchdt` or the platform's token service). Stage the new
  token in Key Vault (`secret-hdfs-delegation-token`) or `.env.local.user`,
  restart (rolling). Expired-token symptom: WebHDFS 401/403 →
  `docs/RUNBOOKS.md`. Tokens are scoped to the read-only principal and never
  logged (query-string never printed — `Hdfs/WebHdfsClient.cs`).
- **Keytab/Kerberos (via Knox)**: keytabs live with Knox, not this connector —
  rotate there per platform runbook; the connector only sees the Knox TLS
  endpoint (private CA via `CA_BUNDLE_PATH`).

### Rotate SQL credentials (`SQL_CONNECTION_STRING`)

Prefer `SQL_USE_MANAGED_IDENTITY=true` (nothing to rotate). Otherwise: create
the new login/password server-side, update the connection string in
`.env.local.user`/Key Vault, rolling restart, drop the old login. Keep the
least-privilege grants of `docs/SQL_CONTRACT.md`.

### Key Vault access itself

`DefaultAzureCredential` — use a managed identity on Azure/Arc machines so
there is no bootstrap secret. If a service-principal secret is used for vault
access, rotate it like the Entra secret above (it is the root of trust for
every other secret — shortest lifetime of all).

## Data at rest inventory

Classify these stores at the level of the source data (Salesforce business
records, routinely PII). Encrypt at the platform layer (BitLocker/TDE);
ACLs per `docs/DEPLOYMENT_ENTERPRISE.md`.

| Store | Location (file mode / SQL mode) | Contains | Sensitivity notes |
|---|---|---|---|
| Sync watermark | `logs/sync_state.json` / `dbo.SyncTimestamps` | connector id + timestamp | low |
| Checkpoints | `logs/checkpoint_<id>.json` / `dbo.Checkpoints` | object names, chunk indexes | low |
| Dead-letter queue | `logs/failed_records_<id>.jsonl` / `dbo.DeadLetter` | item ids, errors, correlation ids — and with `DEADLETTER_PAYLOAD_MODE=full` (default) the FULL failed record payloads | **highest-risk store**; set `redacted` where queue storage is less protected than the source (`docs/THREAT_MODEL.md` §5) |
| Ingested-item inventory | `data/<id>_inventory.db` / `dbo.ItemInventory` | item ids + object types + timestamps | ids reveal existence, not content |
| Identity store | `data/<id>_identity.db` / `dbo.Principals` | Salesforce user id ↔ email ↔ Entra object id | personal data (emails) — in scope for DSAR/retention policy |
| Run logs | `logs/{prefix}_{ts}/connector.log` | operational text; record VALUES only in rare error paths; never credentials | prune with `LOG_RETENTION_DAYS`; SIEM retention governs the shipped copy |
| Classification manifest (opt-in) | `logs/classification_<id>_<stamp>.jsonl` | item ids + labels + detected categories (never matched text) | metadata about sensitivity, not the sensitive text |
| Config | `config/*.json`, `env/.env.local` | topology, filters, ACL modes | no secrets by contract; filters are a security control |
| Secrets | `env/.env.local.user` / Key Vault | see credential inventory | never committed; file ACLs or vault RBAC |
| The Graph connection | tenant-side | the indexed content + per-item ACLs | governed by M365; deletion sweep + `reconcile --fix` keep it faithful to BDH |

## Hard guarantees the code makes (tested)

- Delegation tokens / query strings never reach a log line; SIEM canary
  provided (`docs/SIEM.md`).
- Auth material (secret, assertion, PFX password) never logged — mode only.
- Zero-ACL records are skipped, never ingested world-readable; an incomplete
  identity directory fails the sync loudly instead of coarsening ACLs.
- Unfiltered objects refuse to crawl (fail-closed, effectively-filtered rule);
  deletion sweeps are triple-guarded (caps + incomplete suppression).
- Item ids are validated (`[A-Za-z0-9_-]{1,128}`) before reaching Graph URLs;
  localpath resolution refuses root escapes; every SQL statement is
  parameterized.
- FIPS: no MD5/SHA-1/DES/RC4/3DES anywhere (audited 2026-07-18); SHA-256/RSA
  only.
