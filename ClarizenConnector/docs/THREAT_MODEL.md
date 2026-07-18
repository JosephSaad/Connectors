# Threat model

STRIDE walk of every trust boundary the connector touches. Format per row:
threat → the mitigation that exists in the code today (named, so it can be
audited) → residual risk an operator must own. Companion docs:
`SECURITY.md` (rotation runbooks, data-at-rest inventory),
`docs/DEPLOYMENT_ENTERPRISE.md` (host hardening), `docs/RUNBOOKS.md`
(incident response).

## Trust boundaries

```
Clarizen REST v2 ─┐                                     ┌─ Microsoft Graph
TDW export files ─┤→ [connector process + service acct] ├─ Entra token endpoint
webhook senders ──┘        │            │               └─ alert webhook (out)
                     state DB/files  dead-letter files
                     (SQLite/SQL)    (JSONL / dbo.DeadLetter)
```

## 1. Clarizen REST v2 (outbound, session auth)

| Threat (STRIDE) | Existing mitigation | Residual |
|---|---|---|
| Spoofed endpoint / MITM (S,T) | TLS via platform trust; `CA_BUNDLE_PATH` is **additive-only** (`HttpClientFactory.ValidateWithAdditionalRoots`) — hostname mismatch and missing-certificate errors are never overridden, only chain trust is re-evaluated against the bundle. | A malicious root deliberately placed in the CA bundle can inspect traffic — the bundle file needs the same ACLs as secrets (`docs/DEPLOYMENT_ENTERPRISE.md`). |
| CZQL injection from record ids (T) | `RetrieveAsync` rejects any id outside `[A-Za-z0-9._-]` (`ClarizenClient.IsSafeSysId`) before interpolating into the WHERE clause. | None known — ids come only from Clarizen/webhook payloads and are validated. |
| Credential theft (I) | `SECRET_CLARIZEN_PASSWORD` via env file or Key Vault (`SecretProvider`, `USE_KEY_VAULT`); never logged; session id kept in memory only. | Env-file deployments rely on file ACLs; prefer Key Vault. Clarizen v2 login is password-based — no cert option exists upstream. |
| Quota exhaustion / runaway crawl (D) | Client-side daily budget + per-minute pacing (`ApiBudget`); `ClarizenQuotaExceededException` checkpoints and stops cleanly; TDW bulk export path avoids the API for full crawls. | Budget is client-side book-keeping — other consumers of the org quota are invisible to it. |
| Sustained outage hammering (D) | `clarizen` circuit breaker → degraded mode (pause at checkpoint, cursor not advanced), `docs/RESILIENCE.md`. | — |

## 2. TDW export files (`TDW_EXPORT_PATH`)

| Threat | Existing mitigation | Residual |
|---|---|---|
| Tampered/poisoned export rows (T,E) | Per-record transform isolation — a poisoned row is dead-lettered individually (`failed to transform — dead-lettered, continuing`), never aborts the crawl; ACLs are still resolved per record (no row can grant itself wider access than the directory allows); zero-principal records are skipped, never world-readable. | A tampered export can *omit* rows → the deletion sweep would see them as deleted; the mass-deletion guards (`DELETION_SYNC_MAX_ITEMS`/`_MAX_PERCENT`, `deletion_sweep_skipped` alert) cap the blast radius. |
| Malformed export (D) | `TDW export '<path>' for object '<name>' failed to parse` → REST fallback per object; crawl continues. | — |
| Export directory read by others (I) | — (out of connector scope) | Exports contain full source data incl. financials; ACL the directory like the state DB. |

## 3. Microsoft Graph + Entra token endpoint (outbound)

| Threat | Existing mitigation | Residual |
|---|---|---|
| Credential theft (S,I) | Client secret via Key Vault or env file; **certificate credential** preferred (`GRAPH_CLIENT_CERT_PATH`/`_THUMBPRINT` → RS256 `client_assertion`, x5t#S256, 10-min lifetime, fresh `jti` per request — `Graph/ClientAssertion.cs`). Auth mode is logged; key material never is. | Secret mode remains supported; rotate per `SECURITY.md`. PFX on disk needs file ACLs; Windows-store thumbprint avoids a key file entirely. |
| Token replay (S) | Assertions are short-lived (nbf −60 s / exp +10 min) with unique `jti`; tokens cached in memory only, refreshed 5 min pre-expiry. | — |
| Over-permissioned app (E) | Least-privilege permission set (below). | Admin must not consent to broader scopes "while at it". |
| Throttling/outage cascade (D) | 429-hardened retry with numeric Retry-After + 60 s clamp (`docs/RETRY.md`); adaptive `$batch` concurrency; `graph` breaker + degraded mode. | — |

### Least-privilege Graph permissions (application, admin-consented)

| Permission | Why | Note |
|---|---|---|
| `ExternalConnection.ReadWrite.OwnedBy` | create/manage the external connection + schema | `OwnedBy`: only connections created by THIS app registration |
| `ExternalItem.ReadWrite.OwnedBy` | PUT/DELETE items into those connections | same `OwnedBy` scoping |
| `User.Read.All` | resolve Clarizen users to Entra ids (email/UPN lookup) | read-only directory access |

Nothing else. No Sites/Files/Mail scopes; do not substitute the non-`OwnedBy`
variants.

## 4. HMAC webhook listener (inbound, `CLARIZEN_WEBHOOK_PORT`)

| Threat | Existing mitigation | Residual |
|---|---|---|
| Forged events (S,T) | HMAC-SHA256 **validated over the raw bytes before anything is parsed or enqueued** (`WebhookReceiver.HandleRequest`, `SignatureValidator` constant-time compare) → 401. | Secret strength is the whole game — ≥32 random bytes, rotate per `SECURITY.md`. |
| Unauthenticated exposure (E) | **Fail-closed**: a configured port without `CLARIZEN_WEBHOOK_SECRET` refuses to start (`refusing to start an unauthenticated webhook receiver`). | — |
| Flood / oversize body (D) | 1 MiB body cap → 413 (`MaxBodyBytes`); per-entity debounce/coalesce; polling remains the correctness backstop if the receiver is drowned. `webhook_events_rejected_total` + runbook. | No built-in rate limiting — put the listener behind an ingress that has it. |
| Replay of a captured valid post (T) | Upserts are idempotent re-ingests by id; deletes re-check the source on the next crawl; nothing is trusted from the payload beyond "look at this id again". | Replay burns API budget. The signature carries no timestamp (Clarizen sender defines the format) — mitigate with ingress TLS + secret rotation. |
| Eavesdropping (I) | Listener terminates plain HTTP **by design** — TLS belongs at the ingress in front of it (`docs/WEBHOOKS.md`). | Do not expose the raw port to untrusted networks. |

## 5. State DB (SQLite / SQL Server) + state files

| Threat | Existing mitigation | Residual |
|---|---|---|
| SQL injection (T) | Every statement is parameterized (`SqlStateStore`, `HaCoordinator`, `SqlServerIdentityStore` — single-row, parameterized, idempotent). | — |
| Corrupt state → wrong sync (T,D) | Corrupt-vs-missing distinction with explicit warnings (`State file '<path>' exists but could not be parsed`); corrupt cursor degrades to a first-run (safe, idempotent PUTs); torn dead-letter lines are isolated per line. | A lost delta cursor means a full re-crawl (cost, not correctness). |
| Identity-map poisoning (E) | Identity DB is written only from the Clarizen directory + Entra resolution; records resolving to zero principals are skipped, never world-readable. | Whoever can write `data/*.db` can widen ACLs on the next crawl — same ACLs as secrets. |
| Data at rest exposure (I) | Inventory in `SECURITY.md`; SQL Server backend supports TDE + Entra auth (`SQL_USE_MANAGED_IDENTITY`). | SQLite files are not encrypted by the connector — rely on disk encryption + ACLs. |

## 6. Dead-letter records (JSONL / `dbo.DeadLetter`)

| Threat | Existing mitigation | Residual |
|---|---|---|
| Source data (incl. financials) parked in files (I) | `DEADLETTER_PAYLOAD_MODE=redacted` strips property/content values and response bodies at the shared choke point (`SyncState.AppendFailedRecords` → `DeadLetterRedactor`), keeping ids/error/SHA-256 field hashes; covers the financial-classification paths (tested); `retry-failed` re-fetches from source so redaction costs nothing. Unknown mode values fail fast. | Default is `full` (diagnostic-rich). Tenants under FINANCIAL_DATA governance should run `redacted` — stated in `docs/DEPLOYMENT_ENTERPRISE.md`. The `error` string itself is kept verbatim. |
| Queue growth (D) | `dead_letter` alert at `ALERT_DEADLETTER_THRESHOLD`, `clarizen_connector_dead_letter_depth` gauge, runbook. | — |

## 7. Service account + host

| Threat | Existing mitigation | Residual |
|---|---|---|
| Over-privileged service (E) | Runs as a normal service account; needs only: read on install dir, write on `logs/`+`data/`, outbound 443, optional URL ACLs for the two listeners. `docs/DEPLOYMENT_ENTERPRISE.md` has the exact ACL table. | MSI default is LocalSystem for install simplicity — switch to a virtual/gMSA account per that doc. |
| Config tampering (T,E) | `validate-config --strict` preflight; fail-fast on invalid `PROXY_URL`/`CA_BUNDLE_PATH`/`DEADLETTER_PAYLOAD_MODE`/cert settings naming the setting; reserved connector-id prefixes rejected (`ValidateConnectorId`). | env files are the trust root on file-based deployments — ACL them. |
| Log exfiltration (I) | Logs carry ids/counts, not payload bodies; content is logged only at DEBUG char-count level. Event Log mirror carries the same log text, Warning+ by default. | `LOG_LEVEL=DEBUG` runs are chattier — treat run dirs as sensitive, prune with `LOG_RETENTION_DAYS`. |

## FIPS audit (2026-07)

`grep -riE 'md5|sha1|[^a-z]des[^a-z]|rc4|tripledes'` over `src/` and `tests/`:
**zero hits**. Algorithms actually in use:

| Use | Algorithm | FIPS 140 status |
|---|---|---|
| Webhook signature validation | HMAC-SHA256 (`SignatureValidator`) | approved |
| Graph client assertion | RS256 = RSA PKCS#1 v1.5 + SHA-256; `x5t#S256` = SHA-256 | approved |
| Dead-letter field hashes | SHA-256 (`DeadLetterRedactor`) | approved |
| TLS | platform stack (SChannel/OpenSSL) | inherit host FIPS policy |

Item ids and dedup keys are **plain composite strings** (`{ObjectType}_{SYSID}`)
— no hashing is involved anywhere in identity or dedup, so there is no legacy
weak-hash surface and **no migration is required**. If a future change ever
introduces hashed ids, they must not be silently re-hashed: existing Graph item
ids are immutable once ingested (a re-hash would orphan every ingested item —
full re-crawl + delete of the old connection would be the migration).

The connector runs unmodified with the Windows FIPS-mode policy enabled
(`docs/DEPLOYMENT_ENTERPRISE.md` § FIPS).
