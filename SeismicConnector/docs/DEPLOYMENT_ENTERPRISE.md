# DEPLOYMENT — enterprise rollout

Fleet deployment (SCCM/Intune), configuration management (GPO/DSC), locked-down
networks (proxy / TLS inspection), FIPS mode, and service-account least
privilege. Baseline install mechanics are in README ("Windows service") and
`scripts/install-windows-service.ps1`; this doc covers doing it at org scale.

## Artifact choice

| Artifact | Use when | Notes |
| --- | --- | --- |
| Release zip bundle | Default; SCCM/Intune script deploys | Checksummed (`.zip.sha256`), cosign `.sig` when org signing is enabled. The SBOM is a separate release asset (`seismic-connector.cdx.json`), one per release rather than a copy inside each bundle — download it alongside the zip |
| MSI (`packaging/msi/`, **experimental**) | MSI-only software catalogs | Registers the service but never starts it; zip remains the supported path until the MSI graduates |
| Container image (GHCR) | Linux container estates | Same env contract; webhook/health ports published explicitly |

Verify before distribution: `sha256sum -c *.zip.sha256`; if cosign is enabled,
`cosign verify-blob --key cosign.pub --signature <zip>.sig <zip>`; Windows
Authenticode: `Get-AuthenticodeSignature SeismicConnector.exe` (signed only
when the org configured signing secrets — SECURITY.md).

## SCCM / Intune (Win32)

1. Package the extracted bundle (or MSI) as a Win32 app.
2. Install command (zip path):
   `powershell -ExecutionPolicy Bypass -File scripts\install-windows-service.ps1 -InstallDir "C:\Program Files\SeismicConnector"`
   — elevated by the deployment agent; also creates the Event Log source
   idempotently.
3. Do **not** ship `env\.env.local.user` inside the package — deliver secrets
   per-node via your secret channel, or use Key Vault (`USE_KEY_VAULT=true` +
   Managed Identity / federated identity) so nothing secret lands in the
   package at all.
4. Detection rule: service `SeismicConnector` exists AND
   `HKLM\SYSTEM\CurrentControlSet\Services\SeismicConnector` ImagePath matches
   the target version path (or MSI product code for the MSI path).
5. First start is a deliberate post-config action (service is registered
   `start=auto` but the app fails fast until config exists):
   `validate-config --strict` in the install script's success path is the
   cheapest health gate.

## GPO / DSC configuration management

The connector reads plain environment variables (process > env files), so any
config manager that can lay files + set machine env works:

* **GPO**: machine environment variables for non-secrets
  (`CONNECTOR_ID`, `HEALTH_PORT`, `EVENTLOG_ENABLED=true`, `PROXY_URL`, …);
  Files/GPP to place `config\*.json`. Never put secrets in GPO.
* **DSC** (sketch): `Environment` resources for the same vars,
  `File` resources for `config\`, `Service` resource ensuring
  `SeismicConnector` running, plus a `Script` resource asserting
  `[System.Diagnostics.EventLog]::SourceExists("SeismicConnector")`.
* Config drift: `config\exclusions.json` is compliance policy — manage it as
  code (reviewed repo → pipeline → GPP/DSC), not by hand-editing nodes.
  A changed rule set takes effect next crawl; the late-exclusion pass
  withdraws content flagged since the last ingest.

## Proxy and TLS inspection

Set once per node (env file or machine env):

```
PROXY_URL=http://proxy.contoso.com:8080
PROXY_BYPASS=*.contoso.local;10.*
CA_BUNDLE_PATH=C:\ProgramData\pki\contoso-inspection-roots.pem
```

* Every outbound client (Seismic token+API, Graph token+API, alert webhook)
  honours these. Inbound listeners (webhook, health) are unaffected.
* `CA_BUNDLE_PATH` is **additive** trust for TLS-inspection/private roots;
  hostname mismatches are never excused, and a bad path/PEM fails startup
  naming the setting (deliberate: a half-trusted transport must not limp).
  Treat the bundle file as a root-of-trust artifact (docs/THREAT_MODEL.md).
* Alternative to `CA_BUNDLE_PATH` on Windows: push the inspection root into
  the machine trust store via GPO — then system trust already passes.
* OTLP trace export (`TRACING_*`) uses the standard .NET proxy env vars
  (`HTTPS_PROXY`), not `PROXY_URL`.
* Firewall allow-list (defaults): `auth.seismic.com`, `api.seismic.com`,
  `login.microsoftonline.com`, `graph.microsoft.com`, plus Key Vault URI if
  used. Sovereign clouds: your configured `GRAPH_BASE_URL`/authority host.

## FIPS mode

* Enable Windows FIPS enforcement by policy
  (`System cryptography: Use FIPS compliant algorithms…`); .NET maps to
  CNG/platform crypto.
* The connector is FIPS-clean by audit — HMAC-SHA256, SHA-256, RS256 only
  (docs/THREAT_MODEL.md, "FIPS 140 audit result"). No stored fingerprint or
  id derives from a non-FIPS hash, so enabling FIPS mode requires **no state
  migration** and no config change.
* Validate post-enable: run `validate-config --strict` and one
  `ingest-item` — failures would surface immediately as CryptographicException
  (none expected).

## Service account — least privilege

Replace the LocalSystem default (MSI) / configure via
`sc.exe config SeismicConnector obj= CONTOSO\svc-seismic password= …` or a
**Group Managed Service Account** (preferred, no password to rotate):

| Need | Grant | Nothing more |
| --- | --- | --- |
| Run as a service | "Log on as a service" right | No interactive logon |
| Read binaries + config | Read on install dir | No write on binaries/config (tamper surface) |
| State + logs | Modify on `logs\` and `data\` only | |
| Event Log | Write to existing source `SeismicConnector` | Source creation stays with the elevated installer |
| Wildcard http.sys binds (health/webhook ports) | `netsh http add urlacl url=http://+:8080/ user=CONTOSO\svc-seismic` (repeat per port) | Without it the listener falls back to localhost-only — fine when the scraper is local |
| Outbound 443 | Proxy/firewall egress to the allow-list above | No inbound except the chosen webhook/health ports |
| SQL backend | The app login from `scripts/sql/create-login.sql` (db-scoped, least privilege) | Never `sa`, never `db_owner` |
| Key Vault | `get` on secrets via Managed Identity/gMSA federation | No list/set/delete |

## Rollout order (per environment)

1. Lab: bundle verify → install → `validate-config --strict` →
   `full-deployment` against a sandbox connection → `reconcile` clean.
2. Prod pilot (one node): real config, `EVENTLOG_ENABLED=true`, dashboards
   from `ops/` wired, one full + one incremental crawl observed green.
3. Fleet: staged SCCM/Intune rings. HA estates: bring nodes up one at a time
   (they join the open crawl session cleanly).
4. Post-deploy evidence: keep the first reconciliation report + run summary
   with the change record.

Upgrades/rollbacks: docs/DR.md. Failure triage: docs/RUNBOOKS.md.
