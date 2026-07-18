# Threat model — Altrata Copilot Connector

STRIDE per trust boundary. Voice: what attacks, what actually mitigates it in
THIS codebase (file references are load-bearing), what residual risk remains.
Scope: the connector process, its state, and its two external surfaces
(licensed Altrata feed/API, Microsoft Graph). The M365 tenant's own controls
(Conditional Access, Purview) are out of scope.

The data here is high-net-worth wealth and relationship intelligence — assume
every boundary is worth an attacker's time, and treat any entitlement or
erasure failure as a reportable incident, not a bug.

## Boundaries at a glance

```
Altrata (vendor) ──SFTP drop──> FEED_PATH ──> CrawlEngine ──$batch──> Microsoft Graph
Altrata REST API ──OAuth2────────────────────^                        (seat-only ACLs)
                                             │
              state DB (SQLite/SQL Server) ──┤── erasure ledger (hash chain, file)
              dead-letter queue ─────────────┤── suppression list (state backend)
              review queue ──────────────────┘── config/env (secrets)
```

## 1. Licensed feed drop (FEED_PATH, SFTP-delivered)

| STRIDE | Threat | Mitigation (actual) | Residual |
|---|---|---|---|
| T | Tampered/substituted feed file after delivery | Per-file SHA-256 manifest gate (`FeedReader.ValidateChecksums`), re-verified **on the same open handle** at read time (TOCTOU guard, `FeedReader.ReadRecords`); mismatch ⇒ delivery REJECTED + `delivery_rejected` critical alert, nothing ingested | The manifest itself arrives on the same channel — a full channel compromise forges both. Mitigate upstream: SFTP host keys, drop-dir ACLs (DEPLOYMENT_ENTERPRISE) |
| S | Rogue delivery directory (spoofed publisher) | Only the SFTP account can write FEED_PATH (file ACLs, deployment doc); no code-level publisher authentication | **Accepted**: the drop dir's write ACL is the publisher trust anchor |
| D | Oversized `.json` array (memory exhaustion) | `FEED_JSON_MAX_MB` cap (default 256 MB) rejects up front; `.jsonl`/`.csv` stream | — |
| I | Feed content leaking via logs/traces | Log lines carry ids/counts/hashes only; span tags allowlist-enforced (`Telemetry.SetTag` + `AllowedTagKeys`); no-PII tests over logs, spans, Event Log mirror, dead-letter file | — |
| E | Delta tombstone forging (targeted takedown of a rival's profile) | Tombstones pass the same manifest gate; withdrawal is idempotent and recoverable by re-crawl | A checksum-valid malicious delivery deletes items until the next full crawl restores them. **Accepted** (vendor-signed feeds don't exist) |

## 2. Microsoft Graph API (ingest, ACLs, withdrawal)

| STRIDE | Threat | Mitigation (actual) | Residual |
|---|---|---|---|
| E | Items visible beyond licensed seats | **Never-everyone invariant**: ACLs built exclusively from seats (`SeatAclBuilder.BuildAcl`), `AssertNeverEveryone` re-asserted at transform, single PUT, $batch PUT, ACL PATCH and retry-replay; empty seat list fails CLOSED; refusals counted (`altrata_entitlement_refusals_total`) and alerted (`entitlement_violation`) | Seat FILE content is trusted — a compromised seats.json grants access. File ACLs + change control (DEPLOYMENT_ENTERPRISE); group-based seats (`SEAT_GROUP_ID`) move membership into Entra governance |
| S | Token theft / credential misuse | Secret via `SECRET_*`/Key Vault, never logged; **certificate credential** (`GRAPH_CLIENT_CERT_*`) signs an RS256 client assertion — the private key never transits config; only the auth MODE is logged | Client secret mode remains supported; prefer the certificate (SECURITY.md rotation runbook) |
| T | MITM / TLS-inspection appliance | TLS everywhere; `CA_BUNDLE_PATH` adds private roots **additively** — OS trust still consulted, hostname mismatch never forgiven (`HttpConnectivity.ValidateWithAdditiveTrust`) | Custom-chain validation skips revocation (private PKI CRLs unreachable). **Accepted, documented** |
| D | 429 storms / throttling | Adaptive $batch concurrency, Retry-After honoured (60 s cap), circuit breaker pauses the crawl at a checkpoint instead of hammering | — |
| R | "Did we really withdraw item X?" | Withdrawals recorded in reconciliation reports; erasures in the hash-chained ledger; failed DELETEs dead-letter (op `delete`) and stay visible until replayed | — |

Least-privilege Graph permissions (application, admin-consented) — request
NOTHING beyond:

- `ExternalConnection.ReadWrite.OwnedBy` — manage only connections this app created
- `ExternalItem.ReadWrite.OwnedBy` — items in owned connections only

Explicitly NOT needed: `ExternalConnection.ReadWrite.All`, `ExternalItem.ReadWrite.All`,
any Directory/User permission. `SEAT_GROUP_ID` mode needs no Graph read either —
the group id is embedded in ACLs and evaluated by Microsoft Search at query time.

## 3. State DB (SQLite files / SQL Server)

| STRIDE | Threat | Mitigation (actual) | Residual |
|---|---|---|---|
| T | Inventory/crosswalk edit (hide an item from purge/erasure) | SQL: least-privilege login (`scripts/sql/create-login.sql`), file ACLs on SQLite (DEPLOYMENT_ENTERPRISE); `purge-all` verifies per-item and refuses to wipe state on failures | A DB admin can silently edit inventory. Compensate: periodic full crawl reconciles; Graph is the authoritative store of what is exposed |
| I | State DB contains profile data | Identity store holds crosswalk ids, seat list, path index; **dead-letter payloads are REDACTED by default** (`DEADLETTER_PAYLOAD_MODE`, below) | In `full` mode the queue holds profiles — see SECURITY.md data-at-rest table |
| D | State corruption | Atomic writes (temp+move), corrupt files warn LOUDLY once and degrade defined-safe (checkpoint→restart delivery; state→empty doc **including suppression list — restore from backup, see DR.md**) | Suppression-list loss re-exposes erased subjects until restore: DR.md classifies these files as the strictest backup tier |

## 4. Erasure ledger (logs/erasure_ledger_{CONNECTOR_ID}.jsonl)

| STRIDE | Threat | Mitigation (actual) | Residual |
|---|---|---|---|
| R/T | Edit/reorder/delete an erasure record (repudiate a DSAR) | SHA-256 hash chain (`ErasureLedger.ComputeHash`: each entry covers the previous hash); `Verify()` pinpoints the first broken seq; append-only file handle; **append REFUSES on a corrupt chain** (tamper can never be buried under fresh entries); verification runs after every erasure command and drives `altrata_erasure_ledger_broken` → SIEM **security incident** (SIEM.md) | Hash chain proves tampering happened, not who; whole-file replacement with a re-computed chain defeats it → ship ledger lines to the SIEM (append-only remote copy) and strict file ACLs |
| I | Ledger carries subject email (compliance record needs it) | Ledger file is data-at-rest tier 1 (SECURITY.md); log lines about the ledger carry ids only — never the email | — |

## 5. Dead-letter queue

| STRIDE | Threat | Mitigation (actual) | Residual |
|---|---|---|---|
| I | Full wealth profiles at rest outside the seat-gated index | **`DEADLETTER_PAYLOAD_MODE=redacted` is the DEFAULT** (ids/subject-hashes/error/attempts only); replay re-fetches from the checksum-verified feed delivery; `forget-subject` scrubs queued upserts for the erased subject; queue file covered by no-PII tests | `full` mode reintroduces the exposure — opt-in, documented trade-off (SECURITY.md) |
| E | Replay resurrecting an ERASED subject | Suppression guard in `retry-failed`: upserts for suppressed subjects are DROPPED (raw-id match in full mode, hash match in redacted, re-fetch re-check for legacy records); DELETE ops exempt — they complete erasures | Records queued by pre-1.0 builds carry no subject stamps AND a payload — drain or clear the queue before relying on the guard (RUNBOOKS) |
| T | Queue-file edit injecting a forged payload to PUT | Replay rebuilds the ACL from CURRENT seats (never the captured ACL) and re-asserts never-everyone; redacted mode ignores payloads entirely (re-fetch from manifest-verified feed) | In `full` mode forged PROPERTIES would be PUT under a correct ACL. File ACLs; prefer redacted (default) |

## 6. Review queue (logs/match_review_{CONNECTOR_ID}.jsonl)

| STRIDE | Threat | Mitigation (actual) | Residual |
|---|---|---|---|
| I | Match candidates leaking names/employers | Entries carry ids, scores and 16-hex SHA-256 hashes of the normalized values ONLY (`MatchReviewEntry`) — adjudicators dereference through the stores | — |
| T | Adjudication tampering (forced mislink) | Queue is advisory: links are applied by resolver rules, not by editing the queue | — |

## 7. Service account & host

| STRIDE | Threat | Mitigation (actual) | Residual |
|---|---|---|---|
| E | Service account over-privilege | Runs as a plain service account / gMSA: needs read on FEED_PATH, read/write on data+logs, NO local admin (DEPLOYMENT_ENTERPRISE "Service account"); SQL login granted only CRUD on `dbo.altrata_*` (`create-login.sql`) | LocalSystem is the MSI default for install simplicity — repoint post-install (documented) |
| S | Someone else's process writing our Event Log source | Source `AltrataConnector` created by the elevated installer; mirroring never throws and warns once when unavailable | Event-source spoofing by another local admin is a Windows platform property |
| I | Env files readable (secrets) | `env/.env.local.user` holds SECRET_* only; Key Vault mode removes secrets from disk; file ACLs (DEPLOYMENT_ENTERPRISE) | — |

## 8. Configuration & supply chain

| STRIDE | Threat | Mitigation (actual) | Residual |
|---|---|---|---|
| T | Malicious config flip (e.g. everyone-ACL) | Everyone-grants are structurally impossible regardless of config; bad enum values fail fast naming the setting (`DEADLETTER_PAYLOAD_MODE`, `PROXY_URL`, `CA_BUNDLE_PATH`, `GRAPH_CLIENT_CERT_*`) | Config controls WHICH seats: change control on seats.json is an operator duty |
| T | Build/dependency tampering | CI test gate on 2 OSes; release bundles carry SHA-256 checksums, optional Authenticode + cosign signatures; CycloneDX SBOM attached to every release | Signing is secret-gated: unsigned artifacts ship when secrets are absent (forks) |

## FIPS audit result (2026-07-18)

Grep across `src/`, `tests/`, `scripts/`, `config/` for MD5 / SHA-1 / DES /
3DES / RC2 / RC4: **zero hits**. Every cryptographic primitive in the
connector is FIPS-140-approved:

| Use | Primitive | Where |
|---|---|---|
| Feed manifest checksums | SHA-256 | `FeedReader.ComputeSha256` / `ReadRecords` |
| Erasure ledger hash chain | SHA-256 | `ErasureLedger.ComputeHash` |
| Item-id collision suffix | SHA-256 (12 hex) | `ItemTransformer.BuildItemId` |
| Seat-set hash | SHA-256 | `SeatAclBuilder.ComputeSeatHash` |
| Review-queue / dead-letter subject hashes | SHA-256 (16 hex) | `MatchReviewEntry.HashValue`, `DeadLetterPolicy.HashSubject` |
| Graph client assertion | RS256 (RSA-2048 + SHA-256, PKCS#1 v1.5) | `CertificateCredential.BuildClientAssertion` |
| TLS | platform SChannel/OpenSSL | all HTTP |

No migration is required. The ledger chain, `BuildItemId` and the subject
hashes are **identity/integrity-critical**: if a future change ever proposes
replacing their primitive, it must ship a dual-hash migration (recompute the
whole ledger chain under the new primitive in one auditable pass; re-derive
item ids only on a full re-crawl) — never silently, because existing item ids
and ledger links would stop verifying. On Windows,
`System.Security.Cryptography` maps to CNG, which honours FIPS policy
(DEPLOYMENT_ENTERPRISE "FIPS mode").

## DSAR / GDPR compliance posture

What `forget-subject --confirm` guarantees, in order:

1. **Suppression first** — the subject id lands on the durable suppression
   list in every target store (all shards + base) BEFORE any withdrawal, so a
   crawl racing the erasure re-checks after its PUT and withdraws (suppression
   wins in every interleaving; `CrawlEngine` post-PUT re-read + compensating
   DELETE, dead-lettered if it fails).
2. **Dead-letter scrub** — queued upserts/transform records for the subject
   are dropped from every queue (in `full` mode these held the profile at
   rest); queued DELETEs are kept to complete withdrawals.
3. **Withdrawal** — every inventoried item concerning the subject
   (item↔subject reverse index + the PersonProfile item id) is DELETEd from
   Graph; failures dead-letter as replayable DELETEs (`retry-failed` finishes
   the job; the suppression guard never blocks DELETEs).
4. **Local erasure** — crosswalk + path-index rows removed in every store.
5. **Ledger** — a tamper-evident entry per subject (actor, action, items
   removed, correlation id), then the chain is re-verified.

Completeness LIMITS an operator must disclose to the DPO:

- **Graph/Copilot propagation delay**: a DELETEd externalItem can remain in
  the Microsoft Search index and Copilot grounding for minutes to hours until
  Microsoft's index catches up. The erasure is complete on our side at ledger
  time; tenant-side visibility follows asynchronously.
- **Copilot chat history**: answers already generated for a seat holder
  persist in THEIR chat history; that store is Microsoft Purview's domain,
  not this connector's.
- **Feed re-delivery**: the vendor keeps sending the subject; suppression
  skips them (counted `altrata_items_suppressed_total`) — erasure at the
  SOURCE requires a request to Altrata (the licensee's duty under the DPA).
- **Backups**: state/ledger backups taken before the erasure still contain
  the subject (industry-standard backup carve-out); DR.md requires the
  suppression list to be restored WITH any state restore, which re-erases on
  the next crawl+retry cycle.
- **Legacy queue records** (pre-1.0, payload without subject stamps): not
  identifiable by the scrub — drain or clear the queue once after upgrade
  (RUNBOOKS "Dead-letter growth").
- The **ledger intentionally retains** the subject id (+ email when given):
  Art. 17(3)(b) compliance record. Document this in the RoPA.
