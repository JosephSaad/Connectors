# Enterprise deployment

Fleet-scale deployment on managed Windows Server estates: packaging via
SCCM/Intune, configuration via GPO/DSC, proxied/TLS-inspected egress, FIPS
hosts, least-privilege service accounts, and file ACLs. Baseline single-host
install: `README.md` "Running as a Windows service".

## Packaging & distribution (SCCM / Intune)

Artifacts per release (all from `release.yml`, checksummed, SBOM attached):

| Artifact | Use |
|---|---|
| `HadoopConnector-<ver>-win-x64.zip` (+ `.sha256`, optional `.sig`) | canonical: script-deployed layout, works for SCCM Package or Intune Win32 (wrap with the Content Prep Tool) |
| `HadoopConnector-<ver>-win-x64.msi` | EXPERIMENTAL WiX v5 MSI (`packaging/msi/`) — lays down binaries+templates only; service registration stays with the ps1 script |
| `hadoop-connector.cdx.json` | CycloneDX SBOM for supply-chain review |

- **Detection rule:** MSI ProductVersion, or file version on
  `HadoopConnector.exe` (assembly `<Version>`).
- **Install command (zip/Win32):** expand to `C:\Program Files\HadoopConnector`
  (binaries) and prepare `C:\HadoopConnector` (runtime home: `config\`,
  `env\`, `logs\`, `data\`), then
  `powershell -ExecutionPolicy Bypass -File scripts\install-windows-service.ps1 -InstallDir C:\HadoopConnector`
  — the script is idempotent (re-runs update the binary path) and registers
  the `HadoopConnector` event-log source for `EVENTLOG_ENABLED`.
- **Uninstall:** `install-windows-service.ps1 -Uninstall` then remove the
  directories (decide deliberately whether `logs\`/`data\` state goes with it —
  see `docs/DR.md`).
- Verify downloads: `sha256sum -c`, `cosign verify-blob` when signatures are
  published, Authenticode signature on the exe when the signing secret is
  configured (`Get-AuthenticodeSignature`).

## Configuration management (GPO / DSC / Intune)

Config enters the process three ways — pick per-item, in this precedence
order (process env always wins over files):

1. **Machine environment variables** (GPO Preferences / DSC
   `xEnvironmentVariable` / Intune remediation): best for per-node values
   (`NODE_ID`, `PROXY_URL`, `EVENTLOG_ENABLED`, `USE_KEY_VAULT`). The service
   reads machine env; changes need a service restart.
2. **`env\.env.local`** deployed as a managed file (DSC `File` resource /
   SCCM): the bulk, versioned in the config repo. Never contains secrets.
3. **`env\.env.local.user` or Key Vault**: secrets only
   (`SECRET_AAD_APP_CLIENT_SECRET`, `SECRET_HDFS_DELEGATION_TOKEN`,
   `GRAPH_CLIENT_CERT_PASSWORD`). Prefer `USE_KEY_VAULT=true` +
   `KEY_VAULT_URI` with a managed identity — then no secret file exists at
   all.

Change control invariant: any change to `config\filters.json`,
`config\schema.json` (ACL modes!), `ALLOW_FULL_SCAN`, or the
`DELETION_SYNC_*` caps goes through review + `validate-config --strict` in
the pipeline — filters are a security control (`docs/THREAT_MODEL.md` §7).
DSC/SCCM compliance baselines should flag drift on those files' hashes.

## Proxy & TLS inspection

- `PROXY_URL=http://proxy.corp.local:8080` routes all outbound HTTP(S)
  (Graph, token endpoint, alert webhook, WebHDFS) through the explicit proxy;
  `PROXY_BYPASS=*.hadoop.corp.local;namenode1;10.*` sends the on-prem
  cluster direct (wildcards on hosts; `;`/`,` separated). Typical enterprise
  shape: Graph via proxy, WebHDFS bypassed.
- **TLS-inspecting proxies** re-sign Graph traffic with the corporate CA: put
  that CA chain in a PEM file and set `CA_BUNDLE_PATH`. Trust is ADDITIVE —
  public roots keep working, hostname mismatches still fail.
- **WebHDFS with internal TLS — the common case.** Namenodes/datanodes (or the
  Knox gateway) almost always present certificates from a PRIVATE enterprise
  CA. Put that CA (and any intermediates) in the same `CA_BUNDLE_PATH` bundle
  instead of imaging the machine trust store. Datanode redirects must present
  certs from the same CA — include the whole issuing chain.
- Both knobs fail fast at startup naming the setting; a proxy outage
  mid-flight surfaces as transport retries then the `hdfs`/`graph` breaker
  (`docs/RUNBOOKS.md`).
- Key Vault (`USE_KEY_VAULT`) uses the Azure SDK's own transport: honour
  corporate egress for it with the standard `HTTPS_PROXY`/`NO_PROXY` machine
  environment variables.

## FIPS-mode hosts

Supported unmodified with the Windows FIPS local security policy enabled
(`System cryptography: Use FIPS compliant algorithms...`). The connector's own
cryptography is SHA-256/RSA/TLS-through-SChannel only — the 2026-07-18 audit
found zero MD5/SHA-1/DES/RC4/3DES usages (`docs/THREAT_MODEL.md`, FIPS
section). Notes:

- `GRAPH_CLIENT_CERT_THUMBPRINT` (CNG store) is the preferred credential on
  FIPS hosts — the key stays in the OS key store.
- PFX files for `GRAPH_CLIENT_CERT_PATH` must use modern PBE (AES) — ancient
  RC2-protected PFX exports fail to load under FIPS by design; re-export.

## Service account & least privilege

Run the service as a **gMSA** (preferred — no password to rotate) or a
dedicated low-privilege domain/local account. NOT LocalSystem.

| Grant | Where | Why |
|---|---|---|
| Log on as a service | host | run the service |
| Read | install dir, `config\`, `env\` | binaries + config |
| Modify | `logs\`, `data\` | state, run logs, dead-letter |
| Private-key Read | the Graph client certificate (certlm → key permissions) | `GRAPH_CLIENT_CERT_THUMBPRINT` |
| Key Vault: get secrets (RBAC "Key Vault Secrets User") | vault | `USE_KEY_VAULT` via managed identity on Azure VMs / Arc |
| SQL: the least-privilege login of `docs/SQL_CONTRACT.md` | state DB | no db_owner |

**HDFS side — read-only principal, non-negotiable.** The connector only ever
issues LISTSTATUS/GETFILESTATUS/OPEN; give its principal (simple-auth
`HDFS_USER`, the Knox identity, or the delegation token's owner) READ on
`{BDH_ROOT_PATH}` and nothing else — no write/delete anywhere, no access
outside the mart. A connector compromise must not be able to touch the data
mart it indexes. Delegation tokens (`SECRET_HDFS_DELEGATION_TOKEN`) are
obtained and renewed out-of-band; scope them to the same read-only principal
and rotate per `SECURITY.md`.

No interactive logon, no local admin. The one elevation the lifecycle needs —
event-log source + service registration — happens at INSTALL time via the ps1
script, never at runtime.

## File ACLs (runtime home, e.g. `C:\HadoopConnector`)

| Path | ACL |
|---|---|
| `config\` | Administrators/deploy tooling: Full; service account: Read; everyone else: none. Filters are a security control — write access here IS the ability to weaken it. |
| `env\.env.local` | as `config\` |
| `env\.env.local.user` | SYSTEM + service account: Read; Administrators: Full; **no other principal** — or eliminate the file with Key Vault |
| `logs\`, `data\` | service account: Modify; operators: Read. These hold record ids, error text and — with `DEADLETTER_PAYLOAD_MODE=full` — failed record payloads; classify accordingly or set `redacted` (`docs/THREAT_MODEL.md` §5) |
| certificate PFX (if file-based) | prefer the cert store instead; a PFX on disk gets the `.env.local.user` treatment |

Disable inheritance on the runtime home and audit-log ACL changes on
`config\` (SACL) — that is the tamper alarm for the filter-as-security-control
story.

## Fleet topology notes

- **HA fleet:** identical binaries + config on every node;
  `USE_SQL_SERVER=true`, `HA_MODE=true`, per-node `NODE_ID` via machine env;
  `GRAPH_RETRY_JITTER=true` on all nodes; one node per schedule is enough —
  add nodes for takeover speed and object-level parallelism (`docs/HA.md`).
- **Sharding:** `GRAPH_CONNECTION_SHARDS` is fleet-wide config (same map
  everywhere) — see `docs/SHARDING.md`.
- **Health:** expose `HEALTH_PORT` only on the management network; the
  endpoint is unauthenticated by design (liveness/readiness/metrics) —
  firewall it, scrape it with Prometheus, alert via
  `ops/prometheus-alerts.yml`.
- **Event log + SIEM:** `EVENTLOG_ENABLED=true` fleet-wide; collectors and
  queries in `docs/SIEM.md`.
