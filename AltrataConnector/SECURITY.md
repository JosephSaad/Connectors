# Security policy — Altrata Copilot Connector

This connector moves licensed high-net-worth wealth/relationship intelligence
into a seat-gated Microsoft Graph index. Treat every credential and every
state file below as sensitive by default; the threat model is
`docs/THREAT_MODEL.md`, the operational alarms are `docs/RUNBOOKS.md`.

## Supported versions

| Version | Supported |
|---|---|
| 1.0.x | security fixes |
| < 1.0 (pre-release chassis) | not supported — upgrade; drain the dead-letter queue as part of the upgrade (pre-1.0 records predate DSAR subject stamping, `docs/RUNBOOKS.md#dead-letter-growth`) |

Runtime: .NET 10 (current LTS-track SDK pinned in CI). A CycloneDX SBOM
(`altrata-connector.cdx.json`) on every release and a CodeQL scan on every push
are both configured, but they live in this connector's own
`.github/workflows/`, which has been inert since the move into the connector
monorepo — GitHub executes only the repository-root workflows, and for this
connector that is `.github/workflows/altrata.yml` (build + test on ubuntu and
windows, plus the Docker image build).

## Reporting a vulnerability

Email **security@cloudsconnected.co.uk** (subject `ALTRATA-CONNECTOR VULN`).
No public issues for suspected vulnerabilities — especially anything touching
the seat/entitlement boundary or the erasure machinery. Include version,
deployment mode (file/SQL/HA), repro, impact. Acknowledgement ≤ 2 business
days; fix or mitigation plan ≤ 30 days for High/Critical. Anything enabling
cross-seat data exposure or erasure bypass is Critical by definition.

## Credential inventory & rotation runbooks

| Credential | Lives in | Rotate |
|---|---|---|
| `SECRET_AAD_APP_CLIENT_SECRET` | `env/.env.local.user` or Key Vault `secret-aad-app-client-secret` | ≤ 180 d (or move to certificate) |
| Graph client certificate (`GRAPH_CLIENT_CERT_PATH`/`_THUMBPRINT`) | PFX/PEM file or machine store | ≤ 12 mo |
| `SECRET_ALTRATA_CLIENT_SECRET` (+ `ALTRATA_CLIENT_ID`) | env file / Key Vault | per vendor contract |
| Altrata SFTP feed credentials (key or password) | SFTP client config on the drop host (outside this app) | ≤ 12 mo, and on any team departure |
| SQL login (`create-login.sql`) / managed identity | connection string / Entra | prefer managed identity (nothing to rotate) |
| `ALERT_WEBHOOK_URL` (bearer-style secret URL) | env file | on exposure; treat the URL as a secret |

### Rotation — Graph client secret (no downtime)

1. Entra → App registrations → the connector app → Certificates & secrets →
   **add** the new secret (old one stays valid — two live secrets during the
   window).
2. Update `SECRET_AAD_APP_CLIENT_SECRET` (env file on each node, or the Key
   Vault secret once — Key Vault mode caches per process, so restart to pick
   up).
3. `validate-config --strict` on one node, restart the service(s).
4. Tokens are cached ≤ 1 h in-process; after all nodes restart, **delete** the
   old secret in Entra. A missed node surfaces as
   `Token request failed (400/401)` — `docs/RUNBOOKS.md#auth-failure`.

### Rotation — Graph client certificate (no downtime)

1. Generate the new key pair + CSR per org PKI (RSA ≥ 2048 — the assertion is
   RS256). Upload the new PUBLIC cert to the Entra app (Certificates &
   secrets → Certificates). Both certs are now registered.
2. Per node: install the new PFX (`GRAPH_CLIENT_CERT_PATH` +
   `GRAPH_CLIENT_CERT_PASSWORD` in `env/.env.local.user`) or import to
   LocalMachine\My and update `GRAPH_CLIENT_CERT_THUMBPRINT`; grant the gMSA
   private-key read (`docs/DEPLOYMENT_ENTERPRISE.md`).
3. Restart node-by-node; the run log's one-time
   `Graph auth mode: certificate … (certificate thumbprint <T>)` line
   confirms which cert each node signs with (thumbprint only is ever logged).
4. When every node shows the new thumbprint, remove the OLD cert from the
   Entra app and destroy the old private key.
5. First-time adoption note: the certificate **wins** over the secret the
   moment `GRAPH_CLIENT_CERT_*` is set; once stable, delete
   `SECRET_AAD_APP_CLIENT_SECRET` entirely (it is optional under a cert).

### Rotation — Altrata feed (SFTP) credentials

1. Request the new key/password through the Altrata account manager; agree a
   cutover window between deliveries (deliveries are atomic directories —
   avoid rotating mid-upload).
2. Update the SFTP client/job that populates `FEED_PATH`; the connector
   itself needs nothing (it reads the local drop directory).
3. Confirm the next delivery lands AND passes the manifest gate
   (`altrata_deliveries_processed_total` advances, no `delivery_rejected`
   alert). A checksum failure right after rotation usually means a truncated
   first transfer — `docs/RUNBOOKS.md#feed-manifest-mismatch`.
4. Retire the old credential at the vendor.

### Rotation — Altrata API (enrichment) client

Update `ALTRATA_CLIENT_ID` / `SECRET_ALTRATA_CLIENT_SECRET`, restart, then
`validate-config` (probes the token endpoint). Non-critical path: feeds keep
ingesting while this is broken.

## Data-at-rest inventory

Tiers: **1** = licensed PII or compliance record — strictest ACLs
(`docs/DEPLOYMENT_ENTERPRISE.md`), tier-1 backup rules (`docs/DR.md`);
**2** = ids/hashes/operational metadata — service-account-only ACLs;
**3** = low sensitivity.

**Startup filesystem hardening.** The connector creates the logs + state
directories **owner-only** at startup — POSIX `chmod 0700`; on Windows a
best-effort `icacls` breaks inheritance and grants owner + Administrators only.
This is defence-in-depth beneath the deployment ACLs, not a replacement: it is
best-effort (a share/volume that cannot express the mode logs a WARNING and
continues), so still apply the tier ACLs above out of band.

| Store | Contents | Tier | Protection |
|---|---|---|---|
| Identity DB (`data/{ID}_identity.db` / `dbo.altrata_id_*`) | CRM contacts (names, emails, employers), altrata↔CRM crosswalk, ingested-item inventory + ACL hashes, item↔subject reverse index, path index, seat list | **1** | strict ACLs; SQL login least-privilege; backed up tier 2 cadence, erasure-verified on restore |
| Erasure ledger (`logs/erasure_ledger_{ID}.jsonl`) | subject ids (+email when given), items removed, actor, hash chain | **1** (compliance record — retained across purge-all by design) | append-only handle, hash chain + `Verify` gauge/SIEM alert, strict ACLs, per-erasure backup, SIEM copy |
| Decision ledger (`logs/decision_ledger_{ID}.jsonl`) | exclusion / ACL-restriction decisions: opaque item/delivery id, decision, PII-safe reason, actor, hash chain | **2** (ids/decisions only — no personal values, test-enforced) | append-only handle, hash chain + `Verify` (`altrata_decision_ledger_broken`), strict ACLs |
| Suppression list (inside `data/{ID}_state.json` / `dbo.altrata_suppressed`) | erased subject ids | **1** (erasure durability depends on it) | strict ACLs; **per-erasure backup — the ONLY state whose loss re-exposes erased subjects** (`docs/DR.md`) |
| Dead-letter queue (`logs/failed_records_{ID}.jsonl` / `dbo.altrata_deadletter`) | REDACTED mode (default): item/delivery ids, subject HASHES, error, attempts. FULL mode: + the complete transformed profile payload | **2** redacted / **1** full | strict ACLs either way; forget-subject scrubs the subject's queued upserts; replay guard refuses erased subjects; no-PII test over the file in redacted mode |
| Review queue (`logs/match_review_{ID}.jsonl`) | ids, scores, 16-hex hashes of normalized name/employer — never raw values | **2** | service-account ACLs; adjudicators dereference via stores |
| Audit log (`logs/audit_{ID}.jsonl`) | actor, action, subject id, purpose strings | **2** (purpose text is operator-authored — keep client names out of purposes) | ACLs; retained (lawful-use record) |
| State doc (rest of `data/{ID}_state.json` / `dbo.altrata_kv`) | sync timestamps, delivery ledger, billable counter, seat hash | **3** | ACLs |
| Run logs, reconciliation reports | ids/counts/hashes only (test-enforced), correlation ids | **3** | `LOG_RETENTION_DAYS` pruning |
| Feed drop (`FEED_PATH`, incl. `archive/`) | the licensed feed itself — full profiles | **1** | SFTP-account-only writes, service read; retention policy; keep archives until the dead-letter queue is drained (redacted replay re-fetches from them) |
| Windows Event Log (when enabled) | mirrored WARNING/ERROR/lifecycle — same PII-safe text as logs | **3** | standard Event Log ACLs; SIEM forwarding |

## DEADLETTER_PAYLOAD_MODE — decision record

**Decision: this connector defaults to `redacted`.** When this record was
written its siblings defaulted to `full` and Altrata deliberately deviated;
the hardening programme has since made `redacted` the fleet-wide default, so
the deviation is now the norm. The rationale below still stands.

Rationale:

1. **Highest data sensitivity of the family.** A dead-lettered PUT here is a
   complete wealth/relationship profile (net worth, relationships, board
   seats). In `full` mode that profile sits at rest OUTSIDE the seat-gated
   index — in a log-directory JSONL (or SQL table) that operators tail, copy
   into tickets, and back up with ordinary logs. The queue is the single
   worst PII-at-rest surface the chassis has; for this dataset that trade is
   wrong by default.
2. **Erasure completeness.** A DSAR must also purge queued copies. Redacted
   mode means there is nothing to purge (hashes only); the residual
   full-mode risk (operator exports, backups of the queue) disappears
   structurally rather than procedurally. The forget-subject scrub + replay
   guard exist for `full` mode and legacy records — redacted makes them
   belt-and-braces instead of load-bearing.
3. **The replay cost is real but bounded and visible.** Redacted replay
   re-fetches from the checksum-verified feed delivery, so the delivery must
   still exist under `FEED_PATH` (or its `archive/`). The failure is loud and
   actionable (`no longer under FEED_PATH`, kept on the queue), and the
   operator rule is one line: drain the queue before retention deletes
   deliveries (`docs/RUNBOOKS.md#dead-letter-growth`). Sites that need
   standalone replay (e.g. `RETENTION_MODE=delete` with short windows and no
   archive) opt into `DEADLETTER_PAYLOAD_MODE=full` consciously, accepting
   the tier-1 handling of the queue file documented above.

Both modes stamp subject hashes; both modes keep erasure-completion DELETEs
replayable; the never-everyone ACL invariant holds on every replay path
(ACLs are rebuilt from CURRENT seats, never replayed from capture).

## CONTENT_GATE — decision record

**Decision: ship the injection scanner, default it OFF, and fail OPEN on text.**

Ingested content becomes Copilot grounding context, so a poisoned record is an
attack on every user whose query it grounds — not only on the person it
describes. The gate (`Altrata/ContentGate.cs`) screens the FINAL indexed text
and QUARANTINES a hit into the existing dead-letter queue.

1. **Default OFF (`CONTENT_GATE` unset).** The bank's scanner contract is not
   agreed. With the switch unset the wire output, the item properties and the
   per-item cost are byte-identical to a build without the gate — no scanner is
   even constructed (test-enforced).
2. **Quarantine, not drop.** A blocked item keeps its evidence: dead-letter
   record with reason `content-gate:<category>`, a `quarantine` decision-ledger
   entry, `contentScanStatus` stamped, `altrata_content_gate_blocked_total`, and
   the normal alert webhook. It stays REPLAYABLE — `retry-failed` re-drives it.
   `retry-failed` re-runs the gate, so draining the queue with the gate still on
   cannot silently bypass a quarantine; the operator clears the gate (or fixes
   the source feed) deliberately.
3. **Text fails OPEN, binary fails CLOSED.** Deliberate asymmetry. The injection
   scanner is a bounded regex HEURISTIC, not a security boundary: halting a
   whole crawl because a heuristic could not run is worse than the residual
   risk, so an incomplete scan proceeds LOUDLY (warning + metric +
   `contentScanStatus=incomplete`). Malware on binary content is the opposite
   trade — never index unscanned bytes. Both are configurable
   (`CONTENT_GATE_FAIL_MODE`, or the per-kind knobs).
4. **A timeout is never "clean".** A per-pattern regex budget
   (`CONTENT_GATE_PATTERN_TIMEOUT_MS`, default 250 ms) and a size cap
   (`CONTENT_GATE_MAX_SCAN_MB`, default 4) both report an INCOMPLETE scan, which
   the fail mode then adjudicates. "No time to look" is never recorded as
   "nothing there".
5. **No malware scanner in THIS connector — deliberately.** Altrata ingests no
   binary content: `FeedReader.ReadRecords` accepts `.json`/`.jsonl`/`.csv` only
   and throws `NotSupportedException` otherwise, item content type is always
   `"text"`, and there is no attachment/blob path anywhere. File integrity is
   already covered by the SHA-256 manifest gate (`FeedReader.ValidateChecksums`,
   re-verified on the same open handle). `CONTENT_GATE_ICAP_URL` is parsed for
   fleet parity and logged as INERT. The binary fail mode still defaults to
   CLOSED so that if a binary path is ever added it starts safe.
6. **Known, documented evasion.** To avoid quarantining every compliance memo
   that *quotes* an injection phrase, a match wrapped in quotation marks or
   introduced by a citation cue ("the memo says…", "for example…") is treated as
   a MENTION, not a directive. An attacker can therefore prefix a payload with
   such a cue and slip past. That is an accepted trade — pinned by an explicit
   test — and is precisely why this is triage, not a boundary.

**PII contract (hard requirement here).** A verdict carries the item id and a
fixed-vocabulary category ONLY. Never the matched text, never a snippet, never
the field value. Enforced by a test that drives a quarantine on a record loaded
with names/emails/net-worth figures and asserts none of them reach the run log,
the decision ledger, the dead-letter queue file, or the alert payload.

## Hard security invariants (regression = vulnerability)

- No `everyone`/`everyoneExceptGuests` ACL can be constructed, transformed,
  batched, or replayed (`SeatAclBuilder.AssertNeverEveryone` at every layer;
  refusals metered as `altrata_entitlement_refusals_total`).
- Empty seat list ⇒ ingestion refuses (fails closed), never falls open.
- Suppression precedes withdrawal in `forget-subject`; crawls re-check
  suppression post-PUT; replays refuse erased subjects; DELETEs always
  complete.
- Ledger appends (erasure AND decision) refuse a corrupt chain; verification
  failures set `altrata_erasure_ledger_broken` / `altrata_decision_ledger_broken`
  and are a SECURITY incident (`docs/SIEM.md`). Both share one hash-chain
  implementation (`HashChainedLedger<T>`).
- Purpose-based authorization: when `PURPOSE_ALLOWLIST` is set, a disallowed
  purpose is DENIED **before** any billable/sensitive action, audited
  (`Decision=deny`) and metered (`altrata_purpose_denied_total`); unset =
  record-only (back-compat), logged so the posture is never silent.
- `sensitivityLabel` is an ADVISORY connector-applied classification TAG, not a
  Purview-enforced label. The only hard enforcement is `CLASSIFICATION_ENFORCE_ACL`
  (top-tier items locked to the reviewer group); that ACL restriction can never
  be an everyone-grant and is recorded in the decision ledger.
- Logs, spans, Event Log mirror, dead-letter file (redacted), review queue,
  decision ledger, purpose audit, **content-gate verdicts**: ids/counts/hashes/
  enums only — never names, emails, wealth figures (test-enforced).
- Content-gate verdicts never carry matched text or a snippet; an incomplete
  scan is never recorded as clean; a quarantine is a `quarantine` ledger kind of
  its own, never an overloaded `exclude`.
