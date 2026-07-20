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

## FIPS posture

**Current state: no broken hash primitive is called anywhere in `src/`.**

Grep of `src/` for MD5 / SHA-1 / DES / RC4 / 3DES returns **no hits**. Every
hash in the connector is SHA-256:

| Use | Location |
|---|---|
| Field-cache instance key (`instance_hash` / `@InstanceHash`) | `Graph/IdentityStore.cs`, `Graph/SqlServerIdentityStore.cs` — `InstanceHash` |
| Client-assertion certificate binding (`x5t#S256`) | `Graph/GraphAuth.cs` |
| Dead-letter redaction hashes | `Config/DeadLetterRedaction.cs` |
| Decision-ledger hash chain | `Graph/DecisionLedger.cs` |
| CA chain building (X509, SHA-256 certs), RSA signing | `Infrastructure/HttpClientFactory.cs`, `Graph/GraphAuth.cs` |

On a FIPS-enforced host (Windows FIPS local-security policy, or
`DOTNET_SYSTEM_SECURITY_CRYPTOGRAPHY_FIPS`-style enforcement) the connector has
no call that maps to a non-validated provider.

This posture is enforced by test, not just by review:
`tests/.../TestGraph/FipsInstanceHashTests.cs` (`FipsSourceContractTests`)
greps `src/` on every run and fails the build if `MD5.`, `SHA1.`, the legacy
`*CryptoServiceProvider` types, or `HashAlgorithmName.MD5/SHA1` reappear.

### `InstanceHash` — MD5 → SHA-256 (WP-SF-5)

Through 1.0.0 the field-cache instance key was an 8-hex-char **MD5** prefix of
the Salesforce instance URL. It was identity-critical (it keys persisted cache
rows) but never security-relevant — the input is non-secret config and the
value is used for bucketing, not integrity or authentication. It is now the
first 8 chars of the lowercase-hex **SHA-256** of the same input.

**The output shape is unchanged — 8 lowercase hex characters — so there is no
DDL change.** `field_cache PRIMARY KEY (object_type, instance_hash)`,
`PK_FieldCache`, and `dbo.FieldCache.InstanceHash nvarchar(16)` are all still
valid; `scripts/sql/create-database.sql` was not touched.

### Upgrade consequence: one-time field-cache rebuild

The field cache is a **pure cache**. It exists only to skip the `INVALID_FIELD`
field-discovery retry loop (`Salesforce/ApiClient.cs`). A miss re-runs that loop
and rewrites the row. **No data loss, no manual step required.**

On the first crawl after upgrade:

- every `field_cache` / `dbo.FieldCache` row written under the old MD5 key is
  unreachable — a benign cache miss;
- the discovery loop runs once per object type and writes fresh rows under the
  SHA-256 key;
- expect one slower crawl (a handful of extra `INVALID_FIELD` round-trips per
  object type), then steady state.

The old rows are **left in place on purpose**. They are not deleted
automatically because:

- they are harmless — a few rows (one per object type per instance), a few KB,
  never read, and inert with respect to correctness; and
- they cannot be distinguished from another instance's *live* rows without
  recomputing the retired MD5 key, which would defeat the purpose of removing
  it. A single database legitimately holds cache rows for more than one
  Salesforce instance (sandbox + production), so a blanket "delete every row
  whose hash isn't the current one" would destroy the other instance's live
  cache.

**Optional one-time operator cleanup.** If you want the orphans gone, clear the
whole field cache once after upgrading, on a maintenance window of your
choosing. Use the existing `ClearFieldCache()` entry point with **no arguments**
(`IIdentityStore.ClearFieldCache` — `IdentityStore` / `SqlServerIdentityStore`;
SQL backend: `EXEC dbo.usp_ClearFieldCache` with both parameters `NULL`). The
no-argument form truncates the table, orphans included; that is safe precisely
because the cache is rebuildable. Note that the per-instance form,
`ClearFieldCache(instanceUrl)`, keys off the *current* algorithm and therefore
cannot reach a legacy row.

Doing nothing is a supported outcome.
