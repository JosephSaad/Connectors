# Enterprise Deployment

Fleet deployment (SCCM/Intune), managed configuration (GPO/DSC), locked-down
egress (proxy/TLS inspection), FIPS, and service-account least privilege.
Baseline single-box deployment is in [RUNBOOK.md](RUNBOOK.md) §1 and the README;
this doc is the hardened variant. Threats these controls answer:
[THREAT_MODEL.md](THREAT_MODEL.md).

## 1. MSI deployment via SCCM / Intune

The MSI (`packaging/msi/`, built by the experimental `msi` job in
`release.yml`) installs per-machine to
`%ProgramFiles%\SalesforceCopilotConnector`: exe, `config\` templates,
`env\.env.local.example`, the Windows service (Automatic **delayed**, NOT
started), `SFCONNECTOR_HOME`, and the `SalesforceConnector` event-log source.
It deliberately ships **no credentials and no .env.local** — config is a
separate managed step (below), so one MSI serves every environment.

- **SCCM**: standard MSI application model.
  - Install: `msiexec /i SalesforceCopilotConnector-<ver>-x64.msi /qn /L*v %WINDIR%\Temp\sfc-msi.log`
  - Detection: MSI product code (stable per version) or file version of the exe
    (`<Version>` is stamped — 1.0.0+).
  - Uninstall: `msiexec /x {ProductCode} /qn` (stops/removes the service;
    leaves `logs\`/`data\` state behind by design — see [DR.md](DR.md)).
  - Supersedence: MSI MajorUpgrade — deploy the new version, it replaces the
    old in place; the service resumes from its checkpoint.
- **Intune**: Win32 app (`.intunewin`-wrap the MSI) rather than LOB, so you get
  requirement rules + a config PowerShell in the same package. Assign to a
  device group; system context.
- **Deploy order per node**: 1) MSI, 2) configuration (next section),
  3) `Start-Service SalesforceCopilotConnector` (script step or a second
  scheduled deployment), 4) verify `validate-config --strict` exit 0 in the
  install dir.
- Crash-restart policy is not in the MSI tables — add the documented post-step
  to your config script:
  `sc.exe failure SalesforceCopilotConnector reset= 86400 actions= restart/30000/restart/60000/restart/300000`
- No-MSI fleets: the zip bundle + `scripts/install-windows-service.ps1` remains
  fully supported and does the same things (including the event source).

## 2. Managed configuration: GPO / DSC, registry vs env file

The connector reads **process environment first, then `env/.env.local` /
`env/.env.local.user`** (process env wins). That gives you two managed channels:

| Channel | How | Pros | Cons |
|---|---|---|---|
| **Env files** (recommended) | DSC `File`/SCCM CI drops `env\.env.local` (non-secret) into `SFCONNECTOR_HOME`; secrets via Key Vault (`USE_KEY_VAULT=true`) so `.env.local.user` doesn't exist at all | Diffable, versionable, identical across Windows/Linux/containers, same file the docs/examples use; drift detection is a file hash | File ACLs are on you (see §5); a copy step per change |
| **Registry** (service-scoped env) | GPP Registry writes the service's `Environment` REG_MULTI_SZ under `HKLM\SYSTEM\CurrentControlSet\Services\SalesforceCopilotConnector` (one `NAME=value` per string — the MSI already seeds `SFCONNECTOR_HOME` there) | Pure GPO, no files, per-service isolation (not machine-wide env), survives reinstalls | REG_MULTI_SZ editing is clumsy in GPP; easy to clobber the whole list; Windows-only; service restart required; secrets in registry are still plaintext to admins |

Practical split: **registry for the two bootstrap values** (`SFCONNECTOR_HOME`,
optionally `USE_KEY_VAULT`/`KEY_VAULT_URI`), **env file for the ~20 functional
knobs**, **Key Vault for every `SECRET_*`**. Never machine-wide env vars
(`setx /M`) — they leak into every process on the box.

DSC sketch (the parts that matter):

```powershell
File SfcEnvFile {
    DestinationPath = 'C:\Program Files\SalesforceCopilotConnector\env\.env.local'
    SourcePath      = '\\configshare\sfconnector\prod\.env.local'
    Checksum        = 'SHA-256'; MatchSource = $true; Ensure = 'Present'
}
Registry SfcRestartPolicyMarker { ... }   # or a Script resource running sc.exe failure
Service SfcService {
    Name = 'SalesforceCopilotConnector'; StartupType = 'Automatic'; State = 'Running'
    DependsOn = '[File]SfcEnvFile'
}
```

Full knob reference: `env/.env.local.example` (enterprise vars included).

## 3. Proxy / TLS-inspection egress

Destinations needed outbound (default clouds): your
`SALESFORCE_INSTANCE_URL`, `login.salesforce.com`, `graph.microsoft.com`,
`login.microsoftonline.com`, plus Key Vault (`*.vault.azure.net`) when used.

- **Proxy**: .NET already honors `HTTPS_PROXY`/`HTTP_PROXY`/`NO_PROXY` and OS
  proxy settings — if your fleet manages those, set nothing. For per-app proxy
  policy set `PROXY_URL=http://proxy.corp.example:8080` (optional
  `user:pass@`; store that variant in the secret channel) and
  `PROXY_BYPASS=host1;.suffix2` for internal exceptions. `PROXY_URL` applies to
  every connector HttpClient (Salesforce, Graph, sharing model, webhook).
  Azure SDK traffic (token endpoints, Key Vault) uses the SDK transport — it
  follows `HTTPS_PROXY`, so set both when everything must ride the proxy.
- **TLS inspection**: point `CA_BUNDLE_PATH` at the PEM bundle of your
  inspection root(s):
  `CA_BUNDLE_PATH=C:\ProgramData\pki\corp-tls-inspection-roots.pem`.
  Trust is **additive** — the system store keeps working, hostname checks stay
  enforced, and nothing is ever set to "trust all". A missing/unparseable
  bundle fails startup naming `CA_BUNDLE_PATH` (fail-closed on config errors).
  Rotate the bundle file in place + restart the service.
- Both values are logged at startup (proxy address + bypass count, root count)
  so a `validate-config --strict` run doubles as the egress smoke test.

## 4. FIPS mode

On Windows with the FIPS local-security policy enabled, .NET maps its crypto to
CNG/validated providers; the connector's TLS, SHA-256 usage (client-assertion
`x5t#S256`, dead-letter redaction hashes, decision-ledger chain, field-cache
instance key) and RSA signing all comply. **No MD5 or SHA-1 call remains in
`src/`** — the last one, the field-cache `instance_hash` key, moved to SHA-256
in WP-SF-5 and is enforced by a source-contract test. Note the one-time
field-cache rebuild on upgrade from 1.0.0 (automatic, no data loss): full
posture and the optional operator cleanup are in
[THREAT_MODEL.md](THREAT_MODEL.md#fips-posture). Graph
certificate auth (`GRAPH_CLIENT_CERT_THUMBPRINT` + machine store,
non-exportable key) is the FIPS-friendly credential — prefer it over PFX files
and client secrets in FIPS estates.

## 5. Service account least privilege

The MSI defaults to LocalSystem so a zero-config install works. Harden to a
dedicated principal — ideally a **gMSA** (no password to rotate), else a
virtual account or plain domain service account:

1. **Logon as a service**: grant via GPO
   (`SeServiceLogonRight`) to the account; set it on the service
   (`sc.exe config SalesforceCopilotConnector obj= DOMAIN\gmsa-sfconnector$`).
2. **No admin, no interactive logon** (`SeDenyInteractiveLogonRight`). The
   connector needs admin for nothing at runtime — the event source is
   pre-created by MSI/script.
3. **File ACLs under `SFCONNECTOR_HOME`** (state and dead-letter dirs hold the
   sensitive residue — [THREAT_MODEL.md](THREAT_MODEL.md) §3-4):

   ```powershell
   $home = 'C:\Program Files\SalesforceCopilotConnector'
   $svc  = 'DOMAIN\gmsa-sfconnector$'
   # Service account: modify on the writable dirs only
   foreach ($d in 'logs','data') {
     $p = Join-Path $home $d; New-Item $p -ItemType Directory -Force | Out-Null
     icacls $p /inheritance:r /grant "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" "${svc}:(OI)(CI)M"
   }
   # env\ (secrets): read-only for the service, no other principals
   icacls (Join-Path $home 'env') /inheritance:r /grant "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" "${svc}:(OI)(CI)R"
   # Binaries/config: read+execute only for the service (no self-modification)
   icacls $home /grant "${svc}:(OI)(CI)RX"
   ```

4. **SQL backend**: the same account maps to the least-privilege SQL login
   from `scripts/sql/create-login.sql` (or `SQL_USE_MANAGED_IDENTITY=true` on
   Azure). Never `db_owner`.
5. **Key Vault**: grant the node's managed identity / the account `get` on
   secrets only.
6. **Network**: outbound 443 to §3's destinations; inbound only
   `HEALTH_PORT` from the monitoring subnet (or leave it unset). Remember the
   non-admin URL-ACL note in [OBSERVABILITY.md](OBSERVABILITY.md) if the
   service account should bind non-localhost:
   `netsh http add urlacl url=http://+:9090/ user=DOMAIN\gmsa-sfconnector$`.
