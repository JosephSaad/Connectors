# Enterprise deployment

Fleet rollout (SCCM/Intune), config at scale (GPO/DSC), locked-down networks
(proxy/TLS inspection), FIPS hosts, and least-privilege service accounts.
Baseline install paths stay in the README ("Running as a Windows service");
this doc is the delta for managed estates.

## MSI rollout (SCCM / Intune)

`packaging/msi/ClarizenConnector.wxs` (WiX v5, **experimental** — the zip
bundle + `install-windows-service.ps1` remain canonical) builds
`ClarizenConnector.msi` on the release pipeline's windows runner. The MSI:

- installs to `%ProgramFiles%\ClarizenConnector`, registers the
  **ClarizenConnector** service (auto-start, default arguments
  `full-deployment --continuous --full-crawl-hours 24 --incremental-hours 4`)
  but does **not start it** — secrets are never shipped in the MSI;
- registers the `ClarizenConnector` Event Log source (Application log) so
  `EVENTLOG_ENABLED=true` works without a separate elevated step;
- sets machine-wide `CLARIZEN_CONNECTOR_HOME` to the install dir;
- ships `config\*.json` templates and `env\env.local.example` only.

Rollout sequence per host: deploy MSI → drop `env\.env.local` (+
`.env.local.user` or Key Vault settings) via your config channel → ACL the
directories (table below) → `Start-Service ClarizenConnector`.

- **SCCM**: standard MSI application, detection = MSI product code;
  supersedence per version (MajorUpgrade handles in-place upgrades).
- **Intune**: Win32 app (`.intunewin` around the MSI), install
  `msiexec /i ClarizenConnector.msi /qn`, uninstall by product code; assign to
  the crawl-host device group. A PowerShell script step (or Proactive
  Remediation) delivers env files and starts the service after config lands.

Verify the Authenticode signature before distribution when release signing
secrets are configured (`SECURITY.md` § release integrity).

## Configuration at scale (GPO / DSC)

The connector reads plain environment variables (layered over
`env/.env.local` + `.env.local.user`). Two managed patterns:

- **GPO**: machine environment variables via Group Policy Preferences
  (Computer Configuration → Preferences → Windows Settings → Environment) for
  the non-secret knobs (`PROXY_URL`, `HEALTH_PORT`, `LOG_FORMAT`,
  `EVENTLOG_ENABLED`, `USE_SQL_SERVER`, …). Machine-level variables reach the
  service after a restart. Keep secrets OUT of GPO — use Key Vault
  (`USE_KEY_VAULT=true` + `KEY_VAULT_URI`, managed identity) or a
  tightly-ACL'd `.env.local.user`.
- **DSC / Azure Machine Configuration**: `Environment` resources for the same
  knobs, `File` resources for `config\schema.json` / `graph-schema.json`
  (drift-corrected), `Service` resource asserting `ClarizenConnector` running.
  Pair with `validate-config --strict` as a compliance check command.

Config precedence reminder: process/machine env wins over `env/.env.local`;
`env/.env.local.user` (secrets) layers over both files. One file per host, or
env vars entirely from management — both work.

## Proxy and TLS inspection

All outbound HTTP (Clarizen client, Graph client, alert webhook POSTs) honours
(`Infrastructure/HttpClientFactory.cs`):

| Var | Meaning |
|---|---|
| `PROXY_URL` | `http(s)://[user:pass@]host:port` — corporate egress proxy (credentials become basic proxy auth; prefer unauthenticated egress). |
| `PROXY_BYPASS` | comma/semicolon/space host list that goes DIRECT: exact host, `*.suffix`, `.suffix`, or bare domain (also matches subdomains). Example: `*.corp.local, localhost`. |
| `CA_BUNDLE_PATH` | PEM bundle of ADDITIONAL trusted roots for TLS-inspecting proxies / private CAs. **Additive**: platform trust keeps working; only chain-trust failures are re-validated against the bundle — hostname mismatches are never overridden. Revocation is not checked on the custom-root path. |

All three fail fast at startup naming the setting on a bad value (bad URL,
missing file, unparseable PEM) — no silent fallback. The two inbound
listeners (health endpoint, webhook receiver) are servers: no proxy involved;
TLS for the webhook listener belongs at your ingress (`docs/WEBHOOKS.md`).
ACL the CA bundle like a secret: whoever can append a root can inspect
traffic (`docs/THREAT_MODEL.md`).

Egress allow-list for locked-down networks: `CLARIZEN_BASE_URL` host,
`GRAPH_BASE_URL` host, `AAD_APP_OAUTH_AUTHORITY_HOST` host, Key Vault URI
host (when used), `ALERT_WEBHOOK_URL` host (when used), OTLP endpoint (when
used). Port 443 only.

## FIPS

Audited clean — no MD5/SHA1/DES/RC4/3DES anywhere; HMAC-SHA256, SHA-256 and
RS256 only (full result: `docs/THREAT_MODEL.md` § FIPS). The connector runs
unmodified on hosts with the Windows FIPS security policy
(`System cryptography: Use FIPS compliant algorithms...`) enabled: .NET maps
the SHA-256/HMAC/RSA primitives to the platform CNG providers. TLS cipher
policy is the host's (SChannel) — enforce via your OS baseline. No connector
knob is needed.

## Service account and file ACLs

The MSI default (`LocalSystem`) is for install simplicity — production should
run a **gMSA or virtual service account** (`NT SERVICE\ClarizenConnector`)
with exactly:

| Resource | Access | Why |
|---|---|---|
| install dir (exe, `config\`) | Read | binaries + schema config |
| `env\` | Read (Administrators: Full; no other principals) | secrets live here on file-based deployments |
| `logs\`, `data\` | Modify | state files, run dirs, SQLite DBs |
| `CA_BUNDLE_PATH`, `GRAPH_CLIENT_CERT_PATH` files | Read (ACL like `env\`) | trust root / private key material |
| HTTP URL ACLs | `netsh http add urlacl url=http://+:<HEALTH_PORT>/ user="NT SERVICE\ClarizenConnector"` (and the webhook port) | wildcard bind without admin; otherwise the listeners fall back to localhost-only with a logged warning |
| Windows cert store (thumbprint mode) | private-key Read on the cert in `LocalMachine\My` | `GRAPH_CLIENT_CERT_THUMBPRINT` |
| SQL Server (SQL backend) | db_datareader + db_datawriter on the connector DB (or the vendored role in `scripts/sql/create-database.sql`); prefer `SQL_USE_MANAGED_IDENTITY=true` on Azure | state backend |
| outbound 443 | per the egress allow-list above | — |

No local admin, no interactive logon right (`Log on as a service` only), no
write access to `config\` or the binaries (tamper surface). Set the same ACLs
on backup copies (`docs/DR.md`).

Change the service account after MSI install with
`sc.exe config ClarizenConnector obj= "DOMAIN\gmsa$"` (gMSA: trailing `$`, no
password) or via your DSC baseline.

## Recommended hardening baseline

`EVENTLOG_ENABLED=true` (SIEM feed, `docs/SIEM.md`) ·
`LOG_FORMAT=json` + `LOG_RETENTION_DAYS=30` ·
`USE_KEY_VAULT=true` (or ACL'd `.env.local.user`) ·
certificate Graph auth over client secret ·
`DEADLETTER_PAYLOAD_MODE=redacted` (now the default) ·
`FINANCIAL_DATA_MODE=filter` (now the default) or `acl` under financial-field governance ·
`DECISION_LEDGER=true` (default) with the ledger stored/backed off-host ·
`GRAPH_ITEM_TTL_DAYS` set comfortably above the full-crawl cadence ·
`HEALTH_PORT` scraped by Prometheus/Azure Monitor with the shipped rules
(`ops/`) · webhook listener only behind a TLS ingress with rate limiting,
strict anti-replay (`CLARIZEN_WEBHOOK_REQUIRE_TIMESTAMP=true`, the default).

### Entitlement freshness

`IDENTITY_SYNC_ON_INCREMENTAL=true` (now the default) re-resolves
Clarizen→Entra entitlements on every incremental crawl, so an entitlement
change propagates at the incremental cadence rather than only on full crawls.
A residual, **non-real-time** lag remains between the change in Clarizen/Entra
and the next incremental crawl. To bound it tighter, run incrementals on a
short cadence and schedule a periodic full crawl (which re-applies every item's
ACL — the effective re-ACL sweep) on a cadence matched to your entitlement-SLA.
