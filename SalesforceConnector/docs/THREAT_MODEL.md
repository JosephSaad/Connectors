# Threat Model

STRIDE analysis of the connector's trust boundaries. Each row: the threat, the
mitigation that exists **in this codebase today** (with the file/feature that
implements it), and what risk remains. Companion docs:
[SECURITY.md](../SECURITY.md) (rotation, reporting, data-at-rest),
[DEPLOYMENT_ENTERPRISE.md](DEPLOYMENT_ENTERPRISE.md) (hardened deployment),
[SIEM.md](SIEM.md) (detection).

The connector is **poll-based**: it calls Salesforce and Graph outbound and
listens on nothing except the optional loopback-bindable health endpoint
(`HEALTH_PORT`). There is **no inbound webhook surface** — that whole boundary
is N/A here.

## Trust boundaries

```
 [Salesforce REST API] <--TLS-- connector --TLS--> [Microsoft Graph]
                                   |
             +---------------------+----------------------+
             |                     |                      |
        [state DB]         [dead-letter files]    [config / env files]
   (SQLite or SQL Server)  (logs/*.jsonl or SQL)  (env/.env.local[.user])
             |
        [service account]  --optional-->  [alert webhook (outbound)]
```

## 1. Salesforce API boundary

| Threat (STRIDE) | Existing mitigation | Residual risk |
|---|---|---|
| **S**poofed Salesforce endpoint | TLS via `HttpClient`; instance URL pinned by `SALESFORCE_INSTANCE_URL`; `CA_BUNDLE_PATH` keeps validation strict (additive trust, never disabled — `Infrastructure/HttpClientFactory.cs`) | DNS/OS trust-store compromise on the host |
| **T**ampered responses | TLS; per-field retry validates SOQL shape (`Salesforce/ApiClient.cs`) | A compromised org feeds poisoned content into the index — content is indexed as-is |
| **R**epudiation | Every request/failure logged with timestamps (`Infrastructure/Logging.cs`); Salesforce login history on the org side | — |
| **I**nfo disclosure (creds) | Client-credentials flow; consumer secret only in `env/.env.local.user` or Key Vault (`Infrastructure/SecretProvider.cs`); never logged | Secret file readable by anyone with file ACL access — lock down per [DEPLOYMENT_ENTERPRISE.md](DEPLOYMENT_ENTERPRISE.md) |
| **D**oS (self-inflicted) | Retry with backoff + cap, `SALESFORCE_QUERY_LIMIT`, chunked pagination (`docs/RETRY.md`) | Org API-limit exhaustion shared with other integrations |
| **E**levation | Integration user is read-only scoped (deployment checklist, `docs/RUNBOOK.md` §1b) | Over-permissioned integration user indexes objects you did not intend — the ACL engine will faithfully mirror them |

## 2. Microsoft Graph boundary

| Threat | Existing mitigation | Residual risk |
|---|---|---|
| Spoofed endpoint | TLS; endpoint fixed (`GRAPH_BASE_URL` for sovereign clouds only); token audience matches endpoint (`Graph/Client.cs`) | — |
| Credential theft | Client secret via env/Key Vault, or the stronger **certificate credential** (`GRAPH_CLIENT_CERT_PATH`/`_THUMBPRINT`, `Graph/GraphAuth.cs`): client_assertion JWT, key never leaves the host/store; mode logged at startup, key material never logged | A stolen PFX+password is a stolen credential — prefer thumbprint + non-exportable key in the machine store |
| Token replay | Assertions are short-lived (10 min), unique `jti`, aud = tenant token endpoint (`ClientAssertionJwt`) | — |
| Tampering (item injection) | Only this app's identity can write to its connection (`*.OwnedBy` permission model) | Anyone with the app credential can rewrite the index — rotate per [SECURITY.md](../SECURITY.md) |
| DoS / throttling | Adaptive concurrency dials down on 429 (`Graph/Ingest.cs` `AdaptiveConcurrency`), Retry-After honored exactly, 60s cap, jitter in HA (`docs/RETRY.md`) | Sustained tenant-wide throttling slows crawls (see `docs/CAPACITY.md`) |
| Over-permission via oversized ACL (per-item ceiling) | **ACL scale guard (#9, `Graph/AclScaleGuard.cs`)**: per-item ACE-count metric (`max_item_ace_count`) always exported; when `ACL_MAX_ACES` is set, an item exceeding it is warned (default) or dropped to a group ACL / deny-everyone fallback, protecting the Graph per-item-ACL / 4 MB ceiling that user-expansion can blow | Guard is off by default; the durable fix for large orgs is `USE_GROUP_ACL=true` (group-reference ACLs stay small) |
| Stale / over-permissioned index after an outage | **Item TTL (#8, `Graph/ItemExpiry.cs`)**: `GRAPH_ITEM_TTL_DAYS` stamps `expirationDateTime` so items self-expire if crawling stops; a healthy connector re-stamps every crawl | Off by default; set a TTL comfortably larger than the crawl cadence so a single missed crawl never expires live content |
| Weak/absent sensitivity control on top-tier records | **Classification enforcement (#6, `Graph/Classification.cs`)**: optional — `CLASSIFICATION_ENFORCE_ACL` narrows top-tier items' ACL to a configured Entra group (belt-and-braces on the Salesforce ACL) | The classification TAG is **advisory** (connector-applied from a Salesforce field), **not** a Purview label and not enforced unless enforcement + a group are configured |

### Least-privilege Graph permission review

Grant exactly these **application** permissions, nothing more:

| Permission | Why it is needed | Used by |
|---|---|---|
| `ExternalConnection.ReadWrite.OwnedBy` | Create/manage **only connections this app owns** (connection, schema, external groups) | `Graph/Connection.cs`, `Graph/Schema.cs`, `Graph/IdentityPublisher.cs` |
| `ExternalItem.ReadWrite.OwnedBy` | Write/delete items **only in owned connections** | `Graph/Ingest.cs`, `Graph/Reconciler.cs` |
| `User.Read.All` | Resolve Salesforce users to Entra object ids for ACLs (`GET /users/{upn}?$select=id`, filtered `/users?` lookups) | `Graph/LegacyAclResolver.cs`, `AclEngine/PrincipalMapper.cs` |

Do **not** grant `ExternalConnection.ReadWrite.All` / `ExternalItem.ReadWrite.All`
(cross-connection write), `Directory.Read.All` (superset of the user lookup),
or any delegated permission (the connector is a daemon). `User.Read.All` is the
directory-read floor for ACL mapping; if you run with public ACLs only
(`USE_GROUP_ACL=false` and no user grants), it can be dropped.

## 3. State DB (SQLite / SQL Server)

| Threat | Existing mitigation | Residual risk |
|---|---|---|
| Tampering (checkpoint/identity rewrite) | File ACLs (SQLite under `data/`), now created **owner-only** (POSIX 0700 / Windows owner+admins, `Infrastructure/SecureDirectory.cs`, #3); SQL login is least-privilege app principal (`scripts/sql/create-login.sql`); schema versioned (`docs/SQL_CONTRACT.md`) | Host admin can rewrite state → wrong ACLs served until the next identity/full crawl; alert on unexpected writes ([SIEM.md](SIEM.md)) |
| Repudiation (access decisions) | **Decision ledger (#11, `Graph/DecisionLedger.cs`)**: opt-in (`DECISION_LEDGER=true`) append-only SHA-256 hash-chained record of exclusion + ACL-restriction decisions; `Verify()` detects any edit/reorder/delete | Off by default; low-volume by design (records only deliberate access decisions, not ordinary ingests) |
| Info disclosure | Identity store holds SF user/group/role mappings (ids + emails — directory data, not item content); state directories created owner-only (#3); protection options in [SECURITY.md](../SECURITY.md) §data-at-rest | Unencrypted SQLite file readable with file access — use volume encryption / TDE |
| DoS (corruption) | Corrupt-state diagnostics name file+line; re-crawl rebuilds everything ([DR.md](DR.md): state loss = re-crawl cost, not data loss) | Crawl-length outage window |
| Spoofed SQL server | `Encrypt=True` forced unless explicitly set (`Config/SqlStateStore.cs`); managed identity auth option (`SQL_USE_MANAGED_IDENTITY`) | `TrustServerCertificate=true` in a connection string disables the protection — don't ship it to prod |

## 4. Dead-letter files

| Threat | Existing mitigation | Residual risk |
|---|---|---|
| Info disclosure (CRM PII at rest) | **`DEADLETTER_PAYLOAD_MODE=redacted` is now the DEFAULT** (`Config/DeadLetterRedaction.cs`): property values/content replaced by sha256 hashes; ids/error/ACLs/field names kept; trade-off note embedded in each record; `retry-failed` re-fetches from Salesforce so redaction costs nothing at retry time. An unrecognized mode value fails config-load naming the setting (never silently downgrades to full). Directories created owner-only (POSIX 0700, `Infrastructure/SecureDirectory.cs`, #3) | Opt in to `DEADLETTER_PAYLOAD_MODE=full` only for standalone debugging in a locked-down environment (raw request/response payloads then sit on disk); file ACLs per [DEPLOYMENT_ENTERPRISE.md](DEPLOYMENT_ENTERPRISE.md) |
| Tampering (record injection) | `retry-failed` treats records as **pointers** (item_id/object_type) and re-reads Salesforce — an injected record cannot inject content | An attacker with write access can delete records (losing retry work) — the same access already implies host compromise |
| DoS (unbounded growth) | Depth exposed as `salesforce_connector_dead_letter_depth`; `ALERT_DEADLETTER_THRESHOLD` webhook alert; growth runbook ([RUNBOOKS.md](RUNBOOKS.md#dead-letter-growth)) | — |

## 5. Config / env files

| Threat | Existing mitigation | Residual risk |
|---|---|---|
| Secret disclosure | Secret/non-secret split (`env/.env.local.user`), both gitignored; Key Vault option (`USE_KEY_VAULT`); secrets never logged | File ACLs are the last line — see [DEPLOYMENT_ENTERPRISE.md](DEPLOYMENT_ENTERPRISE.md) |
| Tampering (e.g. `GRAPH_BASE_URL` exfil redirect) | `validate-config --strict` preflight; startup logs name auth mode, proxy, CA bundle; broken `PROXY_URL`/`CA_BUNDLE_PATH` fail fast naming the setting | Config write access ≈ service-account compromise; alert on config changes (SCCM/DSC drift detection) |

## 6. Service account

| Threat | Existing mitigation | Residual risk |
|---|---|---|
| Elevation via service | Service needs no admin: logon-as-service + write to `SFCONNECTOR_HOME` only ([DEPLOYMENT_ENTERPRISE.md](DEPLOYMENT_ENTERPRISE.md) least-privilege section); event source pre-created by installer so runtime never needs admin (`scripts/install-windows-service.ps1`) | MSI default is LocalSystem for zero-config installs — switch to a virtual/gMSA account per the deployment doc |
| Repudiation | Service lifecycle events mirrored to the Windows Event Log when enabled (`Infrastructure/EventLogSink.cs`, ids 1000/2000/3000) | Event log mirroring is opt-in (`EVENTLOG_ENABLED`) |

## 7. Outbound alert webhook (`ALERT_WEBHOOK_URL`)

| Threat | Existing mitigation | Residual risk |
|---|---|---|
| Info disclosure | Alert envelope carries kind/message/connector/counts only — never item content or secrets (`Infrastructure/Alerting.cs`) | Webhook URL itself is a bearer capability — store it like a secret |
| SSRF-style misuse | URL is operator-set env config, not derived from data; 5s timeout; failures swallowed+logged | — |

Inbound webhooks: **N/A** (nothing listens; `HEALTH_PORT` serves only
`/health`, `/ready`, `/metrics` and should be firewalled to the scrape network).

## FIPS audit (2026-07)

Grep of `src/` for MD5 / SHA-1 / DES / RC4 / 3DES:

- **No** SHA-1, DES, RC4, or TripleDES usage anywhere in `src/`.
- **MD5 — two hits, retained deliberately**: `Graph/IdentityStore.cs` and
  `Graph/SqlServerIdentityStore.cs`, method `InstanceHash` — an 8-hex-char MD5
  prefix of the Salesforce instance URL used as the `instance_hash` **primary
  key of the field cache** (SQLite `field_cache` table / SQL `@InstanceHash`).
  This is identity-critical, not security-relevant: it keys persisted cache
  rows and is byte-compatible with the Python original's state files. Changing
  the algorithm silently would orphan every existing cache row on upgrade.
  **Risk**: none — the input (instance URL) is non-secret config; MD5 is used
  for bucketing, not integrity or authentication. On a FIPS-enforced host,
  `MD5.HashData` still works in .NET on Windows (maps to a non-FIPS-validated
  path); if your compliance program forbids the call itself, see the migration
  note below.
- Everything added by the hardening package uses **SHA-256**: `x5t#S256`
  assertion binding (`Graph/GraphAuth.cs`), dead-letter redaction hashes
  (`Config/DeadLetterRedaction.cs`), CA chain building (X509, SHA-256 certs).

**Migration note (only if MD5 must go):** bump the field-cache schema — add an
`instance_hash_v2` (first 8 hex of SHA-256) column, dual-read/single-write for
one release, then drop the MD5 column. The cache is *rebuildable* (it only
skips the field-retry loop), so the cheap alternative is: clear `field_cache`,
switch the algorithm, take the one-time field-discovery cost on the next crawl.
Coordinate with the Python original if both still share state.
