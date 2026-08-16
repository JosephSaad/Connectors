# Security Policy

Security posture, credential rotation, reporting, and the data-at-rest
inventory for the Salesforce Copilot Connector. Deeper analysis:
[docs/THREAT_MODEL.md](docs/THREAT_MODEL.md); hardened deployment:
[docs/DEPLOYMENT_ENTERPRISE.md](docs/DEPLOYMENT_ENTERPRISE.md).

## Supported versions

| Version | Supported |
|---|---|
| 1.x (latest release) | Yes — security fixes ship as the next 1.x patch release |
| 1.x (older patches) | Upgrade to latest 1.x; fixes are not backported within 1.x |
| pre-1.0 builds | No |

State schema is additive within 1.x ([docs/DR.md](docs/DR.md)), so upgrading to
the latest patch is a binary swap + service restart — there is no technical
reason to stay behind.

## Secret rotation runbook

Four credentials exist. All rotations below are **zero-downtime**: the
connector reads credentials at token-request time (crawl start / token
refresh), so the pattern is always *add new → point config at new → restart
service (graceful, resumes from checkpoint) → retire old*. A restart takes
seconds; a running crawl checkpoints and resumes.

### 1. Entra client secret (`SECRET_AAD_APP_CLIENT_SECRET`)

1. App registration → Certificates & secrets → **add** a second secret (old one
   stays valid).
2. Update the value: `env/.env.local.user` on each node, or the Key Vault
   secret (`secret-aad-app-client-secret`) once — see Key Vault flow below.
3. Restart the service per node (`Restart-Service SalesforceCopilotConnector`).
4. Verify: `validate-config --strict`; startup log shows
   `Graph auth mode: default credential chain`.
5. **Delete the old secret** in Entra after all nodes restart. Calendar the
   expiry of the new one.

### 2. Graph certificate credential (`GRAPH_CLIENT_CERT_PATH` / `GRAPH_CLIENT_CERT_THUMBPRINT`)

Certificate mode wins over the secret when both are set — which also gives the
rotation escape hatch: a still-valid client secret keeps working if a cert
rotation goes sideways (unset the cert vars to fall back).

1. Generate/obtain the new certificate (keep the key non-exportable in the
   machine store where possible).
2. App registration → Certificates & secrets → **upload the new public key**
   (both certs now registered).
3. Per node: import to `LocalMachine\My`, grant the service account private-key
   read (certlm → Manage private keys), then update
   `GRAPH_CLIENT_CERT_THUMBPRINT` to the new thumbprint (or replace the PFX at
   `GRAPH_CLIENT_CERT_PATH` + `GRAPH_CLIENT_CERT_PASSWORD`).
4. Restart the service. Verify the startup line:
   `Graph auth mode: client certificate (… expires YYYY-MM-DD)` shows the new
   date. Key material is never logged.
5. Remove the old certificate from Entra and the node stores.

### 3. Salesforce Connected App consumer secret (`SECRET_SALESFORCE_CLIENT_SECRET`)

Salesforce regenerates in place (no dual-secret window): rotate off-peak.

1. Connected App → Manage Consumer Details → **Generate** new secret (old one
   dies immediately).
2. Update env file / Key Vault (`secret-salesforce-client-secret`) right away.
3. Restart the service. A crawl that raced the rotation fails one token call
   and the next cycle recovers; items are never lost (checkpoint + dead-letter).

### 4. Key Vault rotation flow (`USE_KEY_VAULT=true`)

With Key Vault, steps "update every node" collapse to one write:

1. Add the new credential at the provider (Entra dual-secret / cert upload).
2. `az keyvault secret set --vault-name <vault> --name secret-aad-app-client-secret --value <new>`
   — new **version** of the same secret; the connector always reads latest.
3. Restart services (they cache resolved secrets for the process lifetime).
4. Retire the old provider-side credential.
5. Vault hygiene: the connector's identity needs `get` only; rotation
   automation needs `set`; nobody needs `purge`.

Also rotate on: staff departure with vault/env access, any node compromise, and
the release-signing CI secrets (`AUTHENTICODE_PFX_BASE64` /
`AUTHENTICODE_PFX_PASSWORD`, `COSIGN_PRIVATE_KEY` / `COSIGN_PASSWORD`) on
repository-access changes — see `.github/workflows/release-connector.yml`, where
signing is skipped with a notice, never failed, while a secret is absent.

## Vulnerability reporting

- **Do not open a public GitHub issue for vulnerabilities.**
- Use GitHub **private vulnerability reporting** (Security → Report a
  vulnerability) on the repository, or email the maintainer listed in the
  repo profile with `[SECURITY]` in the subject.
- Include: version (`SalesforceCopilotConnector --help` header / file
  version), deployment mode (files vs SQL, HA, service), reproduction, and
  impact. Expect acknowledgement within 72 hours; fixes ship as a patch
  release with a `CHANGELOG.md` **Security** entry (no embargoed details until
  the fix is out).
- Upstream code inherited from Microsoft's Python original: report here first;
  we coordinate upstream if it applies there too.
- Dependency CVEs: Dependabot (`.github/dependabot.yml`) and CodeQL
  (`.github/workflows/codeql.yml`) both run from the repository root and cover
  this connector. The CycloneDX SBOM (`salesforce-connector.cdx.json`) is
  produced by the release pipeline and attached to every `salesforce-v*`
  release — see [Releasing](../README.md#releasing).

## Data-at-rest inventory

What the connector persists, what's inside, and how to protect it. Paths are
under `SFCONNECTOR_HOME` (service) / the working directory; on the SQL backend
the same state lives in the `SalesforceConnector` database instead.

| Store | Contents | Sensitivity | Protection options |
|---|---|---|---|
| `data/{id}_identity.db` (SQLite) / SQL identity tables | Salesforce user/group/role/territory → Entra id mappings, emails/UPNs, group memberships, field cache | Directory data (PII: emails), **no CRM record content** | File ACLs (`logs`/`data` M for service account only — [DEPLOYMENT_ENTERPRISE.md](docs/DEPLOYMENT_ENTERPRISE.md) §5), BitLocker/volume encryption; SQL: TDE + least-privilege login |
| `data/{id}_inventory.db` / `dbo.ItemInventory` | Ingested item **ids** per object type | Low (ids only) | Same as above |
| `logs/failed_records_{id}.jsonl` / `dbo.DeadLetter` | Failed item ids, object type, error text, timestamps. **Default `DEADLETTER_PAYLOAD_MODE=redacted`**: property values/content are sha256-hashed (field names kept, note embedded); only the opt-in `DEADLETTER_PAYLOAD_MODE=full` writes raw Graph request/response payloads (CRM field values) | Low by default (hashes + field names only); **High only if you opt in to `full`** (customer CRM data at rest) | Keep the redacted default where CRM data is sensitive; an unrecognized mode fails config-load; `logs`/`data` created owner-only (POSIX 0700, #3) + file ACLs; `LOG_RETENTION_DAYS` does **not** prune this file (state, not logs) — drain it with `retry-failed --clear-on-success` |
| `logs/decision_ledger_{id}.jsonl` (opt-in) | Append-only, SHA-256 hash-chained record of exclusion + ACL-restriction decisions (item id, decision, reason, seq) when `DECISION_LEDGER=true` (#11) | Low (ids + decision metadata, no CRM content) | Owner-only dir (#3); tamper-evident by design (`Verify()` detects edits); retain as the compliance record |
| `logs/checkpoint_{id}.json`, `logs/sync_state.json` | Chunk positions, sync timestamps | None | File ACLs |
| `logs/{prefix}_{ts}/*.log` (+ summaries) | Operational logs: ids, counts, errors, URLs; record field **names**; single-item debug modes (`ingest-item`, `DEBUG_ITEM_ID`) log one full record on purpose | Medium (low if debug modes unused) | File ACLs, `LOG_RETENTION_DAYS=N` pruning, ship-and-delete via your collector ([docs/SIEM.md](docs/SIEM.md)) |
| `env/.env.local.user` | Client secrets | **Critical** | Prefer Key Vault (`USE_KEY_VAULT=true`) so the file doesn't exist; else ACL to the service account read-only |
| `config/*.json` | Object/field selection, schema | Low (reveals what you index) | Standard file ACLs |

Not persisted anywhere: access tokens (memory only), client-assertion JWTs
(built per token request, 10-minute lifetime), Key Vault values (resolved into
process memory), certificate private keys (stay in the OS store / PFX you
supplied).

## Cryptographic posture (FIPS 140-3)

**No MD5, SHA-1, DES, RC4, or TripleDES call exists anywhere in `src/`.** Every
hash the connector computes is SHA-256:

| Use | Location |
|---|---|
| Field-cache instance key (`instance_hash` / `@InstanceHash`) | `Graph/IdentityStore.cs`, `Graph/SqlServerIdentityStore.cs` — `InstanceHash` |
| Client-assertion certificate binding (`x5t#S256`) | `Graph/GraphAuth.cs` |
| Dead-letter redaction hashes | `Config/DeadLetterRedaction.cs` |
| Decision-ledger hash chain | `Graph/DecisionLedger.cs` |

TLS, RSA signing, and X509 chain building use the platform providers, so on a
FIPS-enforced host (Windows FIPS local-security policy) nothing maps to a
non-validated path. `GRAPH_CLIENT_CERT_THUMBPRINT` against the machine store
with a non-exportable key is the FIPS-friendly credential — prefer it over PFX
files and client secrets in FIPS estates
([docs/DEPLOYMENT_ENTERPRISE.md](docs/DEPLOYMENT_ENTERPRISE.md) §4).

The posture is regression-guarded, not just reviewed: a source-contract test
(`FipsSourceContractTests` in
`tests/SalesforceCopilotConnector.Tests/TestGraph/FipsInstanceHashTests.cs`)
greps `src/` on every test run and fails if a broken primitive reappears.

### Upgrading from 1.0.0 — one-time field-cache rebuild

Through 1.0.0 the field-cache instance key was an MD5 prefix. It is now a
SHA-256 prefix, with the **same 8-lowercase-hex output shape**, so no schema
migration is needed — `scripts/sql/create-database.sql` is unchanged and both
primary keys stay valid.

The field cache is a pure cache (it only skips the `INVALID_FIELD` field
discovery loop). On the first crawl after upgrade, rows keyed by the old MD5
value are simply missed and rebuilt under the new key. **No data loss and no
operator action — expect one slightly slower crawl, then steady state.**

Pre-upgrade rows are deliberately left in place rather than auto-deleted: they
are inert and a few KB, and one database can legitimately hold live cache rows
for several Salesforce instances (sandbox + production), so no automatic
deletion rule can tell an orphan from another instance's live row. If you want
them gone, run the existing `ClearFieldCache()` **with no arguments** once,
after upgrading (SQL backend: `EXEC dbo.usp_ClearFieldCache` with both
parameters `NULL`). Full rationale in
[docs/THREAT_MODEL.md](docs/THREAT_MODEL.md#fips-posture).
