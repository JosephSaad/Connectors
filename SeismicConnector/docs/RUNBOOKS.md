# RUNBOOKS — symptom → diagnose → remediate → escalate

Operator procedures per failure mode. Metric names are exact
(`seismic_connector_` prefix elided); alert rules in `ops/prometheus-alerts.yml`
and `ops/azure-monitor-alerts.kql` reference these anchors. Event ids are the
Windows Event Log ids (docs/SIEM.md).

Escalation ladder (referenced below as L1/L2/L3):
L1 = platform on-call (this runbook) · L2 = connector owner + Seismic admin ·
L3 = vendor tickets (Seismic support / Microsoft 365 admin sev).

---

## breaker-open

**Symptom** `/ready` → 503 `NOT READY (circuit open: seismic|graph)`;
`circuit_breaker_state{dependency} == 1`; `degraded_pauses_total` rising;
log `DEGRADED MODE` (event id 2000).

**Diagnose**
1. Which dependency: the `/ready` body / metric label names it.
2. Real outage vs. local: `validate-config` probes both APIs from the node;
   check the provider status page for the named dependency.
3. Rule out transport: recent proxy/TLS-inspection change → see log for
   `CA_BUNDLE_PATH`/`PROXY_URL` fail-fast errors at startup.

**Remediate**
* Nothing to force — degraded mode is the correct state while the dependency
  is down. The crawl paused at a checkpoint and the next cycle probes
  (half-open) and resumes automatically; `circuit_breaker_resets_total`
  confirms recovery.
* If the dependency is fine and only this node fails: fix the local cause
  (DNS/proxy/firewall), then restart or wait for the next cycle.
* Do NOT restart-loop the service: liveness stays 200 by design; a paused
  connector is healthy.

**Escalate** L2 after 30 min of confirmed provider outage; L3 with the
provider once org-side causes are excluded.

---

## dead-letter-growth

**Symptom** `dead_letter_depth` climbing across crawls; `dead_letter` webhook
alert (`ALERT_DEADLETTER_THRESHOLD`); run summary `Items failed` > 0.

**Diagnose**
1. Read the queue: `logs/failed_records_{CONNECTOR_ID}.jsonl` (or
   `dbo.DeadLetter`) — group by `error`.
2. One error class (429/503) → downstream incident; mixed per-item 4xx →
   payload/schema issue (property too large, invalid ACL principal).
3. `DEADLETTER_PAYLOAD_MODE=redacted` hides payloads by design — the
   `error` + ids + hash stubs remain; reproduce one item with
   `ingest-item --id <contentId>` for a full local look.

**Remediate**
* Transient causes: `retry-failed --clear-on-success` after the incident.
* Payload/schema causes: fix (schema, exclusion, mapping), then `retry-failed`.
* Never hand-edit the JSONL to "clear" it — `retry-failed` removes only what
  succeeds; `ClearFailedRecords` semantics are reserved for the command.

**Escalate** L2 if the same items dead-letter across 3 consecutive crawls.

---

## webhook-401-spike

**Symptom** `webhook_rejected_total` rising; log warnings
`rejected request with invalid/missing signature (remote …)` (event id 2000).

**Diagnose — distinguish the two causes; they look identical per-request:**
1. **Secret rotation mismatch** (benign): rejections start exactly at a
   rotation timestamp, come from the KNOWN sender IPs, and are ~100% of that
   sender's traffic (`webhook_accepted_total` from it drops to zero).
2. **Forgery/probing attempt**: unknown remote endpoints, scattered timing,
   odd body sizes (the log line carries remote + body size), often alongside
   404s on other paths, while known-sender traffic still validates.

**Remediate**
* Rotation mismatch: complete the rotation — update the sender to the new
  `SEISMIC_WEBHOOK_SECRET` (single-secret validator: there is NO dual-accept
  window; see SECURITY.md rotation order). Rejected-window events are healed
  by the next crawl — no data loss.
* Forgery: confirm nothing was enqueued (rejects never act — by design),
  block the source at the firewall/LB, keep the secret (it held). Rotate it
  anyway if the source is credible.

**Escalate** Forgery pattern → security team with log excerpts (remote
endpoints, never signature values). Rotation confusion → L2.

---

## webhook-queue-drops

**Symptom** `webhook_dropped_total` rising; log
`queue at capacity (10000); dropped N oldest event(s)` (event id 2000);
`webhook_queue_depth` pinned near 10,000.

**Diagnose** Sender burst vs. stalled drain: if `webhook_queue_depth` never
falls, the continuous loop is not draining (crawl wedged? breaker open?);
if it saw-tooths, it is just burst pressure.

**Remediate** Drops are safe by design (drop-oldest; polling reconciles on
the next crawl). For chronic bursts: shorten `--incremental-hours`, or batch
sender events. For a stalled drain: treat as breaker-open / crawl-stuck.

**Escalate** L2 only if drops persist across a full crawl cycle.

---

## no-mne-reconciliation-drift

**Symptom** `last_drift_findings` > 0 after `reconcile`; reconciliation
report (`reconciliation_*.jsonl`) lists excluded-but-indexed or
orphaned items; compliance review flags an MNE item in Copilot answers.

**Diagnose**
1. Open the latest reconciliation report — each finding names the item, the
   rule, and the action taken/needed.
2. Excluded-but-indexed usually means the exclusion rule was added AFTER
   ingest and the late-exclusion pass has not run yet (it runs on the next
   incremental), or `reconcile` was run without `--repair`.

**Remediate**
* `reconcile --repair` — withdraws orphaned/excluded, re-ingests missing.
* For an urgent single item: `SEISMIC_QUERY_LIMIT` untouched, just fix
  `config/exclusions.json`, then run `reconcile --repair`; verify with the
  report that the withdrawal happened (it is auditable evidence).

**Escalate** Any confirmed MNE exposure → compliance officer immediately with
the reconciliation report; that file is the audit artifact.

---

## re-acl-storm

**Symptom** `items_reacled_total` and `acl_drift_detected_total` jump;
crawl duration up; run summary `Re-ACLed:` large.

**Diagnose** Expected after a real permission change (teamsite re-permission,
group remap). Suspicious when no change is known — then check identity churn:
`identity-dry-run` diff, and whether a stale identity store caused fingerprint
flapping (same items re-ACL every crawl).

**Remediate**
* Legitimate storm: let it finish — ACL-only PATCHes, no content re-send;
  throttling is handled by the ladder.
* Flapping fingerprints: run the identity crawl (`identity-dry-run --save`),
  then `reacl --dry-run` to confirm drift count returns to ~0.
* Emergency stop: the storm is resumable — graceful stop finishes the chunk.

**Escalate** L2 if drift persists across two crawls with no known change
(possible tampering — see THREAT_MODEL boundary 5).

---

## version-churn-resume

**Symptom** After a crash/stop during heavy publishing, operators ask whether
the resumed crawl skipped versions or double-ingested.

**Diagnose** Checkpoint (`checkpoint_{CONNECTOR_ID}.json` / SQL) stores the
`since` boundary + completed chunks; resume reuses the ORIGINAL `since`, so a
version published mid-crash is re-listed next incremental.

**Remediate** Nothing — the externalItem id is the content id; a superseded
version PUT is an idempotent in-place update. If assurance is demanded:
`reconcile` (no `--repair`) reports superseded/missing counts as evidence.

**Escalate** Only if `reconcile` shows persistent superseded items across
crawls (then treat as ingest failure → dead-letter-growth).

---

## ha-failover

**Symptom** A node dies mid-crawl; claims sit in `dbo.CrawlClaims` with a
stale heartbeat; `ha_claims_held` on survivors flat while work remains.

**Diagnose** `SELECT * FROM dbo.CrawlClaims WHERE Status='claimed'` — stale =
`HeartbeatUtc` older than `HA_CLAIM_TIMEOUT_SECONDS` (default 300).

**Remediate** Nothing — surviving nodes steal stale claims automatically
(guarded UPDATE, no double ownership) and the crawl closes normally
(`ha_claims_acquired_total` on survivors shows the steals). Restart the dead
node whenever convenient; it will join the next session.

**Escalate** L2 if claims stay `claimed` past 2× the timeout — that means no
survivor is crawling (all nodes down or SQL unreachable).

---

## ha-lease-stuck

**Symptom** A crawl session never closes: `dbo.CrawlSessions.Status='open'`
long after work finished; next cycle keeps joining the old crawl.

**Diagnose**
1. `SELECT Status, COUNT(*) FROM dbo.CrawlClaims WHERE CrawlId=@id GROUP BY Status`
   — rows still `claimed` block the close (by design).
2. Stale-heartbeat `claimed` rows with all nodes healthy → the claim owner
   crashed BETWEEN heartbeats and nothing re-claimed (no work left to trigger
   a steal-visit).

**Remediate**
* Preferred: start a crawl cycle on any node — visiting the resource steals
  the stale claim, completes it, and the close proceeds.
* Manual (last resort, all nodes idle): mark the stale claim failed —
  `UPDATE dbo.CrawlClaims SET Status='failed' WHERE CrawlId=@id AND Status='claimed' AND HeartbeatUtc < DATEADD(second,-@timeout,SYSUTCDATETIME())`
  — the next TryCloseCrawl closes the session as `failed`; failures are
  re-driven by `retry-failed`, never lost.

**Escalate** L2 if sessions wedge repeatedly (clock skew between nodes and
SQL is the usual root cause — check it explicitly).

---

## state-corruption

**Symptom** Startup/crawl errors parsing `sync_state.json` /
`checkpoint_*.json`; `malformed record skipped` warnings on the dead-letter
file; SQLite `database disk image is malformed`.

**Diagnose** Which artifact? JSON state files are self-healing (unparseable →
treated as absent). Dead-letter tolerates torn lines (skips + warns, keeps the
rest). The SQLite identity DB is the only artifact needing recovery.

**Remediate**
* `sync_state.json` lost → next crawl is a full crawl (correct, just slower).
* Checkpoint lost → the in-progress run restarts its chunks (idempotent PUTs).
* Identity DB corrupt → restore per docs/DR.md, or delete
  `data/{CONNECTOR_ID}_identity.db` and rerun `identity-dry-run --save`
  + `full-deployment` (rebuilds tracked items + fingerprints from source).
* SQL backend → standard SQL restore (docs/DR.md), then `reconcile --repair`.

**Escalate** L2 with the corrupt file preserved (copy, don't delete) if
corruption recurs — indicates disk trouble, not app trouble.

---

## decision-ledger-damaged

**Symptom** `Decision ledger ...: N region(s) of DESTROYED bytes` or
`... trailing byte(s) are DAMAGED` at startup; or `ReadFile` refusing the ledger
with `malformed interior record` / `the final line ends in DAMAGED bytes`.

**Diagnose** The ledger is deliberately *noisy* about physical damage, so first
establish whether anything was actually lost — damage and loss are different
things here.

* Read it with `ReadFile(path, out var damage)`. `damage.GluedLines` and
  `damage.ResyncedRegions` mean the file is mangled but every record was
  recovered; `Verify()` on the returned chain then tells you whether the chain
  itself is intact.
* `Verify()` reporting a **seq gap** means damage landed inside a record and
  destroyed it. That record is not recoverable — it is gone from the evidence,
  and the gap is the record of its going.
* `ReadFile` **throwing** means damage the chain could not have shown you: an
  unparseable interior line, or a final line that is not a record — either
  invalid bytes, or complete JSON the record contract rejects. A seq gap cannot
  exist behind the *last* record, so for that one the refusal is the only
  signal there is. The bytes are still on disk, which is the point.
* **Known blind spot.** Damage confined to a record's *final* byte — the
  closing brace — overwritten by whitespace or deleted outright is byte-for-byte
  identical to a partially flushed write, so it is truncated as a crash-tail and
  the record is dropped *quietly* — no gap, no refusal, no damage flag. Measured
  post-fix over a real 265-byte final record: 4 of 67,840 single-byte
  replacements (all 256 values at all 265 offsets), all at that one offset, plus
  deleting that same byte; 0 of 68,096 single-byte insertions and 0 of 265
  truncations. If a record count is one short of what you expect and every
  signal is clean, this is the shape; reconcile against the off-box / WORM copy.
  (A previous release put this at "2 of 265 offsets" and included the closing
  quote of `Hash`; that figure came from a five-value replacement alphabet and
  missed a backslash case. See CHANGELOG.)

**Remediate**
* **Copy the file before doing anything.** It is audit evidence, damaged or not,
  and the connector will never delete it for you.
* The connector keeps appending to a damaged ledger rather than starting a new
  chain — a fork would be worse. New entries chain onto the last *readable*
  record, so the file stays usable going forward.
* Reconcile against your off-box / WORM copy to establish what the destroyed
  records said. This is the situation that copy exists for.
* `GluedLines` on a file with **no** crash in its history is not a crash shape:
  it is the signature of an append-time forgery glued onto an existing line
  (docs/THREAT_MODEL.md). Treat as a security event, not a disk event.

**Escalate** L2 immediately if `Verify()` shows a seq gap or a broken link
(evidence was destroyed), or if `GluedLines` appears without a corresponding
crash. Recurring damage with a clean chain is disk trouble — escalate per
state-corruption above.

---

## graph-429-storm

**Symptom** `throttled_429_total` climbing fast; crawls slow; logs full of
`Graph transient error 429 — retrying in Ns`.

**Diagnose** Tenant-wide Graph throttling vs. self-inflicted concurrency:
did `GRAPH_CONCURRENT_BATCHES`/`INGEST_CHUNK_SIZE` change? Are other Graph
workloads (other connectors) storming the same tenant?

**Remediate**
* The connector already backs off: Retry-After honored exactly, adaptive
  concurrency dials workers down on throttle signals. Let it ride unless SLAs
  are missed.
* Persistent: lower `GRAPH_CONCURRENT_BATCHES` (or set `GRAPH_RETRY_JITTER=true`
  to de-synchronize nodes), stagger crawl schedules across connectors.
* Never raise `GRAPH_MAX_RETRIES` to "push through" a storm — that amplifies it.

**Escalate** L3 (Microsoft) only with sustained 429s at minimal concurrency
and no competing workload.

---

## oauth-failure

**Symptom** Crawl fails immediately; log `token request failed`
(Seismic 400/401 or AAD `invalid_client`/`AADSTS7000222` expired secret);
event id 3000.

**Diagnose**
1. Which side: the error names the endpoint (Seismic tenant token URL vs.
   `login.microsoftonline.com`).
2. `validate-config` reproduces both token calls without a crawl.
3. Expired/rotated secret vs. wrong tenant/client id vs. revoked cert:
   AADSTS error codes are explicit; check secret/cert expiry dates first.
4. Certificate mode: `Graph auth mode: certificate` in the log confirms the
   cert path is active; a wrong/missing file fails fast naming
   `GRAPH_CLIENT_CERT_PATH`/`_THUMBPRINT`.

**Remediate** Rotate/replace per SECURITY.md (Seismic creds, Graph secret or
certificate). Key Vault deployments: fix the vault secret; the provider
retries the vault each cycle (failures are never cached).

**Escalate** L2 for credential owners; L3 if the IdP itself errors.

---

## eventlog-silent

**Symptom** `EVENTLOG_ENABLED=true` but no entries in the Application log.

**Diagnose** Source missing (installer not run elevated) or account lacks
write. The sink never throws — check the run log still shows the mirrored
warnings; on the node run
`[System.Diagnostics.EventLog]::SourceExists("SeismicConnector")`.

**Remediate** Re-run `scripts/install-windows-service.ps1` elevated (creates
the source idempotently), or `New-EventLog -LogName Application -Source SeismicConnector` once, then restart the service.

**Escalate** —
