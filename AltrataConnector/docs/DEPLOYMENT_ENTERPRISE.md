# Enterprise deployment — Altrata Copilot Connector

Fleet deployment on managed Windows Server (the primary target). Canonical
install = zip bundle + `scripts/install-windows-service.ps1`; the MSI
(`packaging/msi/`) is EXPERIMENTAL and exists for SCCM/Intune pilots.

## Layout (per host)

```
D:\AltrataConnector\            # keep OFF the system drive; exclude from AV real-time scans of logs\
  AltrataConnector.exe
  config\   schema.json, graph-schema.json, seats.json
  env\      .env.local  .env.local.user       # never in the image; laid down by config mgmt
  data\     {ID}_identity.db  {ID}_state.json # created at runtime
  logs\     run logs, checkpoint, dead-letter, audit, match_review,
            erasure_ledger_{ID}.jsonl         # created at runtime
```

## SCCM / Intune

**Zip bundle path (recommended)** — package these steps as an SCCM
application / Intune Win32 app (`.intunewin`):

```powershell
# Install.ps1 (runs elevated as SYSTEM)
Expand-Archive AltrataConnector-1.0.0-win-x64.zip -DestinationPath D:\AltrataConnector -Force
Copy-Item .\payload\config\* D:\AltrataConnector\config\   # org-managed seats.json etc.
Copy-Item .\payload\env\*    D:\AltrataConnector\env\      # .env.local (+ .user or Key Vault mode)
& D:\AltrataConnector\scripts\install-windows-service.ps1 -InstallDir D:\AltrataConnector
# The installer also registers the Event Log source (idempotent) — needed for EVENTLOG_ENABLED.
# Do NOT start the service in the package; first start is a controlled step:
#   validate-config --strict, then Start-Service AltrataConnector
```

- Detection rule: `D:\AltrataConnector\AltrataConnector.exe` file version
  `1.0.0` (CI stamps `-p:Version`).
- Uninstall: `install-windows-service.ps1 -ServiceName AltrataConnector -Uninstall`,
  then remove the directory — EXCEPT `logs\erasure_ledger_*.jsonl`,
  `logs\audit_*.jsonl` and your tier-1 backups (docs/DR.md): the compliance
  records outlive the install.
- Verify the bundle before packaging: `sha256sum -c *.zip.sha256`, and when
  release signing is enabled, `cosign verify-blob --key <org.pub>
  --signature <zip>.sig <zip>` + Authenticode (`Get-AuthenticodeSignature`)
  on the exe. SBOM (`altrata-connector.cdx.json` on the release) feeds your
  component inventory.

**MSI path (experimental)** — `AltrataConnector-<ver>.msi` from the release:
standard SCCM/Intune MSI deployment; MajorUpgrade replaces older versions.
It installs binaries + config templates + the service (NOT started; account
LocalSystem until repointed — see below) under
`%ProgramFiles%\AltrataConnector`. It does NOT create the Event Log source
(MSI runs without the script): push
`New-EventLog -LogName Application -Source AltrataConnector` via your
baseline. Config still arrives via your config-management layer.

## GPO / DSC baseline

Manage with either; keep these five controls in the baseline:

1. **Service hardening (GPO)** — System Services: `AltrataConnector` startup
   Automatic; permissions: Administrators + SYSTEM full, deny start/stop to
   Interactive. Recovery (the install script already sets restart/30s/60s/5min,
   daily reset) — re-assert via GPP if your baseline rewrites SCM recovery.
2. **File ACLs** — the strict set below, enforced continuously (GPP Security
   descriptors or the DSC fragment).
3. **Event Log** — Application log ≥ 64 MB retention on hosts with
   `EVENTLOG_ENABLED=true` (WARNING/ERROR volume is modest, lifecycle daily).
4. **FIPS mode** where mandated (below).
5. **Egress** — allow only the endpoints in the proxy section; block direct
   internet otherwise.

DSC fragment (sketch — service account + ACL drift correction):

```powershell
Configuration AltrataConnector {
    Service AltrataService {
        Name = 'AltrataConnector'; StartupType = 'Automatic'; State = 'Running'
        Credential = $gmsaCredential      # gMSA: password managed by AD
    }
    Script LedgerAcl {
        SetScript  = { icacls 'D:\AltrataConnector\logs' /inheritance:r /grant:r 'corp\gmsa-altrata$:(OI)(CI)M' 'BUILTIN\Administrators:(OI)(CI)F' 'NT AUTHORITY\SYSTEM:(OI)(CI)F' }
        TestScript = { -not ((icacls 'D:\AltrataConnector\logs').Contains('Users')) }
        GetScript  = { @{ Result = (icacls 'D:\AltrataConnector\logs') -join ';' } }
    }
}
```

## Service account — least privilege

Run as a **gMSA** (`corp\gmsa-altrata$`) — no password to rotate, no
interactive logon. The MSI defaults to LocalSystem for install simplicity;
repoint immediately (`sc.exe config AltrataConnector obj= corp\gmsa-altrata$`
or the DSC above). The account needs exactly:

| Resource | Right | Why |
|---|---|---|
| Install dir (`D:\AltrataConnector`) | Read/Execute | binaries + config |
| `data\`, `logs\` | Modify | state, queues, ledger, run logs |
| `FEED_PATH` | Read (+Modify ONLY when `RETENTION_DAYS` archives/deletes) | ingest; retention moves files |
| `env\` | Read | config; secrets live in `.env.local.user` or Key Vault |
| SQL DB (SQL/HA mode) | the login from `scripts/sql/create-login.sql` — CRUD on `dbo.altrata_*` only, no DDL beyond first-run provisioning, no server roles | state backend |
| Key Vault (`USE_KEY_VAULT=true`) | `secrets/get` via managed identity | `SECRET_*` resolution |
| Cert private key (`GRAPH_CLIENT_CERT_THUMBPRINT`) | Read on the key (certlm → Manage Private Keys) | client-assertion signing |
| Logon right | *Log on as a service* only | deny interactive/RDP in the GPO |

NOT granted: local admin, network shares beyond FEED_PATH, WinRM. Nothing in
the connector needs elevation at runtime (the installer needed it once, for
service + event source registration).

## STRICT file ACLs — ledger / suppression / state

These files are the entitlement and erasure trust anchors
(docs/THREAT_MODEL.md boundaries 3–5). Break inheritance; grant exactly:

| Path | gMSA | Administrators / SYSTEM | Everyone else |
|---|---|---|---|
| `logs\erasure_ledger_{ID}.jsonl` | **Modify (append)** | Full (break-glass + backup) | **none** — any extra writer invalidates the tamper-evidence story |
| `data\{ID}_state.json` (contains the suppression list) | Modify | Full | none |
| `data\{ID}_identity.db` | Modify | Full | none |
| `logs\failed_records_{ID}.jsonl` (dead-letter; in `full` mode holds profiles) | Modify | Full | none |
| `logs\audit_{ID}.jsonl` | Modify | Full | none |
| `config\seats.json` | **Read only** | Full (change-controlled writers only) | none — writers to this file GRANT DATA ACCESS |
| `env\.env.local.user` | Read | Full | none |

Auditing (SACL): enable object-access auditing for WRITE on the ledger and
seats.json — the SIEM ledger-tamper triage (RUNBOOKS) leans on knowing who
could write. SQL mode: the same reasoning maps to table permissions —
`dbo.altrata_suppressed` and `dbo.altrata_deadletter` writable by the
connector login only.

## Proxy / TLS inspection

All connector HTTP (Graph, Entra token endpoint, Altrata API, alert webhook)
honours:

```bash
PROXY_URL=http://proxy.corp.local:8080
PROXY_BYPASS=^https://otel\.corp\.local        # regex list, ';' or ',' separated
CA_BUNDLE_PATH=D:\pki\corp-tls-inspection.pem  # PEM, ADDITIVE trust
```

- Bad values fail FAST at startup naming the setting — a half-applied proxy
  config never silently falls back to direct.
- `CA_BUNDLE_PATH` is additive: OS-trusted endpoints keep working, the
  inspection root is consulted only when the OS chain fails, hostname
  mismatches are never forgiven. Revocation is not checked on the custom
  chain (documented residual, THREAT_MODEL boundary 2).
- Egress allow-list for the proxy team: `login.microsoftonline.com`,
  `graph.microsoft.com` (or the sovereign `GRAPH_BASE_URL`), the Altrata API
  host (`ALTRATA_API_BASE_URL` + `ALTRATA_TOKEN_URL`), the alert webhook
  host, Key Vault (`*.vault.azure.net`) when used, the OTLP collector when
  used.
- The OTLP exporter is separate plumbing: standard `OTEL_*` /`HTTPS_PROXY`
  env applies; on-prem collectors usually belong in `PROXY_BYPASS`.
- TLS-inspection note for the SECURITY reviewer: with inspection enabled the
  appliance sees bearer tokens and licensed profile payloads in flight —
  either exempt `graph.microsoft.com` + `login.microsoftonline.com` from
  inspection (Microsoft's recommendation) or extend the appliance's data
  handling approval to cover licensed PII.

## FIPS mode

Windows FIPS policy (`System cryptography: Use FIPS compliant algorithms…`)
is fully supported: every primitive is SHA-256 / RSA-2048(RS256) / platform
TLS, mapped to CNG on Windows (audit table in docs/THREAT_MODEL.md — zero
MD5/SHA-1/DES/RC4/3DES). No config flag is needed on the connector side.
Verify on a FIPS-enabled host once per release:
`validate-config --strict` + one supervised crawl + one `forget-subject`
dry-run — all three exercise SHA-256 and the TLS stack.

## First start (per host, after config lands)

```powershell
cd D:\AltrataConnector
.\AltrataConnector.exe validate-config --strict     # env, files, Graph/API reachability
.\AltrataConnector.exe identity-dry-run             # seat source sanity — BEFORE any ACL exists
Start-Service AltrataConnector                      # continuous full+incremental crawls
# Watch: /health on HEALTH_PORT, Event Log source AltrataConnector (id 1000 lifecycle),
# and the first reconciliation report under logs\.
```

HA fleets: bring up node 1, let a full crawl close, then join the rest
(docs/HA.md); upgrades roll node-by-node (docs/DR.md "Upgrade / rollback").
