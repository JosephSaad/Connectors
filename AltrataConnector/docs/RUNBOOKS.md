# Runbooks — Altrata Copilot Connector

One section per failure mode: Symptom → Diagnose → Remediate → Escalate.
Alert rules in `ops/prometheus-alerts.yml` / `ops/azure-monitor-alerts.kql`
link here by anchor — renaming a heading breaks a pager link.

Conventions used below:

- `$EXE` = however you run the connector (`dotnet run --project src/AltrataConnector --`,
  the published `AltrataConnector`, or the Windows service binary).
- Metrics come from `HEALTH_PORT` `/metrics`; log lines from
  `logs/<command>_<timestamp>/connector.log` (JSON when `LOG_FORMAT=json`) and,
  with `EVENTLOG_ENABLED=true`, the Windows Application log source
  `AltrataConnector` (ids 1000/2000/3000 — docs/SIEM.md).
- Erasure/entitlement incidents are SECURITY escalations, not just ops.

---

## Breaker open / degraded mode

**Symptom** — `/ready` returns 503; `altrata_breaker_open == 1`; log
`Circuit breaker 'graph' OPEN — failing fast`; crawls report
`PAUSED (degraded — Graph breaker open)`; ingest throughput flat.

**Diagnose**

1. Which breaker: `/metrics` + log line names it (`graph` = critical,
   `altrata-api` = non-critical; only `graph` degrades readiness).
2. Root cause class: repeated 5xx/timeouts trip it — 4xx/429 do NOT. Check the
   preceding `Graph 5xx`/`transport error` warnings, then Microsoft 365 service
   health for Graph incidents.
3. Confirm nothing else broke: the crawl pauses AT A CHECKPOINT (no
   dead-letter flood is expected — if `altrata_deadletter_depth` also spiked,
   work that failed BEFORE the trip is queued; handle under
   [Dead-letter growth](#dead-letter-growth)).

**Remediate**

1. Usually nothing: after `CIRCUIT_BREAKER_OPEN_SECONDS` (default 30) the
   breaker half-opens and probes; the NEXT crawl (or the continuous loop)
   resumes from the saved checkpoint. Log: `Circuit breaker 'graph' CLOSED`.
2. Outage over but service idle between scheduled crawls: run
   `$EXE ingest --incremental` to resume immediately.
3. Corporate egress broke (proxy/TLS change): fix `PROXY_URL` /
   `CA_BUNDLE_PATH` (startup fails fast naming the bad setting) and restart.

**Escalate** — breaker flapping (open→half-open→open repeatedly, rising
`Trips` in `/health`) for >30 min with Microsoft reporting healthy: network
team (proxy/TLS-inspection appliance is the usual culprit). Include the
breaker snapshot and 3–4 of the preceding 5xx/timeout warnings.

---

## Dead-letter growth

**Symptom** — `altrata_deadletter_depth` rising / `deadletter_threshold`
webhook alert; crawl summaries show `dead-lettered N`.

**Diagnose**

1. Depth counts REPLAYABLE records only. `altrata_deadletter_unreplayable`
   counts transform failures separately — those need a FEED fix, not a replay.
2. Read the queue (file mode: `logs/failed_records_{CONNECTOR_ID}.jsonl`; SQL:
   `dbo.altrata_deadletter`). Group by `Error`:
   - `HTTP 429 … after all retries` → throughput, see [429 storm](#429-storm)
   - `HTTP 4xx` schema/property errors → schema drift; fix `config/graph-schema.json`
   - `erasure-race withdrawal` / `DeliveryId == "erasure"` → DELETEs completing
     an erasure — replay these FIRST (they finish a DSAR)
3. Redacted mode note (`DEADLETTER_PAYLOAD_MODE=redacted`, the default):
   replay re-fetches from the feed delivery, so the delivery must still be
   under `FEED_PATH`. Error `no longer under FEED_PATH` means retention
   archived/deleted it first.

**Remediate**

1. `$EXE retry-failed` (add `--clear-on-success` to delete a fully drained
   queue file). Shard-aware; rebuilds ACLs from CURRENT seats; refuses
   upserts for erased subjects (`dropped (erased subject)` in the summary —
   that is correct behaviour, not loss).
2. Un-replayable transform failures: fix the source feed, re-run ingest, then
   `$EXE retry-failed --retire-unreplayable` to drop the tombstoned entries.
3. `no longer under FEED_PATH`: restore the delivery from `FEED_PATH/archive/`
   (retention `archive` mode moves it there) and re-run `retry-failed`; if
   retention mode is `delete`, request re-delivery from Altrata or re-ingest
   from source. Prevention: drain the queue before retention fires, or raise
   `RETENTION_DAYS`.
4. After an upgrade from a pre-1.0 build: old queue records carry no subject
   stamps — drain the queue once (`retry-failed`) so the DSAR suppression
   guard has full coverage.

**Escalate** — same error class persists after two replay attempts: open a
ticket with the queue's error histogram. 4xx property errors → whoever owns
the Graph schema; repeated 5xx on specific items → Microsoft support with the
item ids and correlation ids from the records.

---

## Feed manifest mismatch

**Symptom** — critical alert `delivery_rejected`; log
`Delivery 'X' REJECTED: Checksum mismatch for '<file>'` (or
`missing file ... listed in the manifest`); `altrata_deliveries_rejected_total`
increments. The delivery ingested NOTHING (by design — all-or-nothing gate).

**Diagnose**

1. Transfer truncation vs tamper: re-compute
   (`shasum -a 256 <file>` / `Get-FileHash`) and compare with `manifest.json`
   and with the vendor's published size. A short file = truncated transfer.
2. Check the SFTP transfer log for the delivery window; partial upload while
   the crawl ran is the common benign cause.
3. Mismatch with CORRECT size, or a file that changed AFTER a successful gate
   (`REJECTED at read time` — the TOCTOU re-verify fired): treat as possible
   tampering, not corruption.

**Remediate**

1. Truncated/corrupt: re-transfer the delivery (or have Altrata re-publish),
   then `$EXE ingest --incremental` — the delivery is not in the processed
   ledger, so it is picked up automatically.
2. Arrange SFTP uploads to a temp name + atomic rename so half-written
   deliveries never carry a final `manifest.json`.

**Escalate** — any mismatch you cannot attribute to transfer corruption is a
SECURITY escalation (possible feed tampering — THREAT_MODEL boundary 1):
preserve the delivery directory read-only, capture the SFTP host's auth log,
notify the security team and Altrata support together.

---

## Seat-file parse failure

**Symptom** — every ingest command fails fast; log
`Seat list at '<path>' is not valid (...)` or
`Seat source yielded zero principals — refusing to sync an empty seat list`
or `ENTITLEMENT: Seat list is empty`; `altrata_entitlement_refusals_total`
increments; alert `entitlement_violation`.

This is **entitlement failing CLOSED**. Data visibility is governed by that
file; the connector stopping is the designed outcome, not the incident.

**What operators must NOT do**

- Do NOT "unblock" by adding a placeholder/wildcard principal, a broad
  all-staff group, or anyone not on the Altrata license — that is an
  entitlement breach the moment the next crawl re-ACLs every item.
- Do NOT restore an old seats.json from backup without checking leavers: a
  stale file re-grants access to people whose seats were revoked.
- Do NOT delete the state store to "reset" the seat hash — the re-ACL pass
  exists precisely to reconcile items to the CURRENT seat list.

**Diagnose**

1. `$EXE identity-dry-run` — shows the seat source, parse result, seat count,
   stored vs computed hash, and whether a re-ACL would trigger. Never touches
   ACLs.
2. File mode: validate JSON shape — plain array of UPNs/object IDs, or
   `{"users":[...],"groups":[...]}`. `SEAT_GROUP_ID` mode: confirm the group
   id and that the group still exists.

**Remediate**

1. Fix the JSON (or repoint `SEAT_LIST_PATH`); re-run `identity-dry-run`
   until counts look right; then `$EXE seat-sync` (a changed hash triggers the
   batched re-ACL pass; the hash commits only after a COMPLETE pass).
2. Zero seats because the license lapsed: leave it failed-closed and follow
   the license-end path (`purge-all`) with the business owner.

**Escalate** — seat file changed without a change ticket: SECURITY (the file
is the entitlement boundary; unexplained edits = possible unauthorized-access
attempt). Include file mtime, diff vs last known-good, host audit log.

---

## Forget-subject failure mid-flight

**Symptom** — `forget-subject --confirm` exits nonzero with
`N item(s) withdrawn, M queued for retry (dead-lettered DELETE)`; log
`Erasure withdrawal failed for <itemId> ... DELETE dead-lettered; suppression
stays durable`; `altrata_deadletter_depth` up by M.

**What already held, even though the command "failed"**

- Suppression was committed FIRST (before any withdrawal) in every store —
  crawls will NOT re-ingest the subject (`altrata_items_suppressed_total`
  counts skips), and any racing in-flight PUT self-corrects via the post-PUT
  suppression re-check (compensating withdrawal).
- The ledger entry was appended and the chain re-verified.
- Queued upserts for the subject were scrubbed from the dead-letter queue.

What is OUTSTANDING: the M Graph DELETEs — the subject's items are still
searchable until they run. Treat as an open DSAR clock.

**Exact recovery steps**

1. Fix Graph connectivity (see [Breaker open](#breaker-open--degraded-mode) /
   [Auth failure](#auth-failure) as applicable).
2. `$EXE retry-failed` — erasure DELETEs replay first-class; the suppression
   guard never blocks DELETE ops (they COMPLETE the erasure). Expect
   `M replayed`.
3. Verify completion:
   `$EXE forget-subject --id <subjectId>` (dry-run) must report
   `Items to withdraw : 0` and `Already suppressed : 1 / 1`.
4. Record the retry-failed timestamp against the DSAR ticket; the ledger
   entry (step 0) plus this drain is the completion evidence.

**Escalate** — DELETEs still failing after 24 h: Microsoft support with the
item ids + correlation id; inform the DPO the erasure-completion SLA is at
risk (tenant search visibility persists until the DELETEs land + index
propagation, THREAT_MODEL "DSAR posture").

---

## Ledger Verify failure

**Symptom** — `altrata_erasure_ledger_broken == 1`; log
`Erasure ledger '<path>' FAILED verification: chain broken at seq N (...)`;
or an erasure command refuses with `REFUSING to append ... to a corrupt chain`.

**Diagnose — tamper vs torn line. The distinction decides the escalation.**

Open the ledger (`logs/erasure_ledger_{CONNECTOR_ID}.jsonl`) at line N:

- **Torn line** (crash/disk-full during append): the LAST line of the file is
  truncated/unparseable JSON; every line before it verifies. The break is at
  the tail and the file ends mid-record. Verify reports
  `line does not parse as a ledger entry`.
- **Tamper**: the broken seq is NOT the final line, or the line parses fine
  but hashes/links mismatch (`sequence/link/hash mismatch — the entry or an
  earlier one was edited, reordered or deleted`), or the file shrank vs the
  SIEM's shipped copy. Any edit anywhere breaks EVERY subsequent link — a
  mid-file break is edit/reorder/delete, full stop.

Cross-check against the append-only copy your SIEM ingested (docs/SIEM.md
ships every ledger append): `diff` the local file against the SIEM export.

**Remediate**

- Torn tail: restore the ledger from the last backup (DR.md tier 1) or, if
  only the final line is torn AND the SIEM copy confirms no entry is missing,
  remove the torn final line, re-run verify, then RE-RUN the erasure command
  that was interrupted (its entry was never durably appended; suppression is
  in the state store and survived independently).
- Tamper: do NOT edit, do NOT delete, do NOT append. Preserve the file and
  filesystem timestamps; restore a verified copy from backup to a NEW path
  only after forensics has imaged the original.

**Escalate — the paths are DIFFERENT**

- **Torn line = ops incident**: normal severity, fix-and-verify, note in the
  shift log.
- **Tamper = SECURITY INCIDENT** (severity critical, class security — the
  ledger is the DSAR compliance record; an edit is evidence destruction):
  security team + DPO immediately, host forensics (who could write the file —
  cross-check the strict ACLs from DEPLOYMENT_ENTERPRISE), preserve SIEM
  copies, treat every erasure after the broken seq as unproven until
  reconciled against the SIEM export and `dbo.altrata_suppressed` /
  the state suppression list.

---

## Review-queue growth

**Symptom** — `altrata_match_review_depth` climbing;
`logs/match_review_{CONNECTOR_ID}.jsonl` growing across crawls.

**Diagnose**

1. Queue entries are BELOW-threshold fuzzy candidates awaiting a human — they
   auto-link nothing, so growth is a staffing/tuning signal, not data risk.
2. Entries are deduplicated (id + candidate + value hashes): steady growth
   means genuinely NEW candidates — check whether `ENTITY_REVIEW_FLOOR` is too
   low (noise) or a CRM import changed employers/names en masse.
3. Entries carry ids/scores/hashes only; adjudicators dereference ids through
   the feed and identity store.

**Remediate**

1. Staff the adjudication pass; the queue file is append-only JSONL — process,
   then archive the file (a fresh one seeds dedup from what remains).
2. Tune: raise `ENTITY_REVIEW_FLOOR` toward `ENTITY_MATCH_THRESHOLD` to admit
   fewer low-confidence candidates; confirmed pairs belong in the CRM export
   so the deterministic email / name+employer tiers catch them next crawl.
3. Wrong matches slipping through instead (queue too EMPTY): lower the floor /
   raise the threshold — never auto-link by editing the queue.

**Escalate** — sudden 10x spike after a specific delivery: data-quality issue
in that feed drop; raise with Altrata support (delivery id + a handful of
ALTRATA ids from the entries, never the hashes' preimages).

---

## HA failover

**Symptom** — a node stops crawling (host down, service crash); other nodes
log `Delivery 'X' is leased by another node — skipping` for units the dead
node held; possibly `[HA] Crawl ... closed by another node`.

**Diagnose**

1. HA state lives in `dbo.altrata_leases` (shared SQL). Inspect:
   `SELECT lease_name, owner, expires_utc FROM dbo.altrata_leases ORDER BY expires_utc;`
   Owner format is `HA_NODE_ID` (default `machine:pid`).
2. Leases have a 5-minute TTL. A crashed node's leases simply EXPIRE; the next
   crawl on any surviving node picks the unit up. Expected recovery, no action.
3. `altrata_ha_leases_held` per node shows who holds what right now.

**Remediate**

1. Nothing, usually: failover is lease expiry + the next scheduled crawl. To
   force immediate takeover, run `$EXE ingest --incremental` on a survivor
   after the TTL lapses.
2. Restarted node rejoining is safe: PUTs are idempotent, the checkpoint is
   per-connector in shared SQL, and crawl close is single-winner
   (`crawl-close:<id>` lease; a crawl with failed claims closes as `failed`,
   never wedges open).
3. Verify post-failover: `altrata_last_full_crawl_timestamp_seconds` /
   `..._incremental_...` advance on schedule again.

**Escalate** — split-brain symptoms (two nodes BOTH claiming the same
delivery lease inside one TTL window) indicate clock skew > TTL or a SQL
failover glitch: check NTP on both hosts, review the SQL AG failover log.
See [Lease stuck](#lease-stuck) for the wedge case.

---

## Lease stuck

**Symptom** — a delivery is skipped by EVERY node crawl after crawl
(`leased by another node — skipping`), but no node is processing it; the
lease row's `expires_utc` keeps moving into the future, or sits expired while
nodes still skip (clock skew).

**Diagnose**

1. `SELECT * FROM dbo.altrata_leases WHERE lease_name = N'delivery:<id>';`
   — is the owner a LIVE process? (`HA_NODE_ID` default embeds machine + pid.)
2. Owner alive but hung: thread dump / service state on that node — the lease
   is honest, the WORKER is stuck (likely a Graph call inside the breaker;
   check that node's log).
3. Owner dead but `expires_utc` in the future beyond TTL: clock skew — a node
   with a fast clock wrote a future expiry. Compare `SYSUTCDATETIME()` with
   both hosts' UTC now.
4. `[HA] Lease acquire FAILED` errors instead: the shared SQL backend is the
   problem, not the lease — treat as SQL connectivity
   ([State corruption](#state-corruption) has the connection checklist).

**Remediate**

1. Hung owner: stop the service on that node gracefully (SCM stop finishes
   the chunk + saves the checkpoint). Its leases release on process exit or
   expire on TTL; the next crawl elsewhere resumes from the checkpoint.
2. Dead owner + future expiry (skew): fix NTP first. Only then, if the wedge
   persists, delete the single stale row:
   `DELETE FROM dbo.altrata_leases WHERE lease_name = N'delivery:<id>' AND owner = N'<dead-node>';`
   Never truncate the table while any node runs.
3. Crawl-close leases (`crawl-close:<id>`, 24 h TTL) are meant to persist —
   do not "clean them up"; they pin single-winner close semantics.

**Escalate** — recurring skew-wedges: infra ticket for host time sync (the
lease protocol assumes clocks within seconds). Repeated hung workers on one
node: collect that node's log + breaker snapshot and open an internal bug.

---

## State corruption

**Symptom** — loud one-time log lines:
`State file '<path>' is unreadable ... continuing with an EMPTY state document`
/ `Checkpoint file ... treating as NO checkpoint` /
`Dead-letter queue ... has N malformed line(s)` /
`Erasure ledger ... unreadable` — or SQL mode connection/`Execute` failures.

**Diagnose** — which file decides the blast radius:

| File | Loss means | Recoverable by re-crawl? |
|---|---|---|
| `logs/checkpoint_{ID}.json` | interrupted delivery restarts from record 0 | YES (idempotent PUTs) |
| `data/{ID}_state.json` | sync timestamps, delivery ledger, billable counter, **suppression list** | timestamps/ledger YES; **suppression list NO** |
| `logs/failed_records_{ID}.jsonl` | pending replays/erasure-completion DELETEs | mostly (next full crawl re-PUTs; queued DELETEs are LOST — verify erasures) |
| `logs/erasure_ledger_{ID}.jsonl` | DSAR compliance record | **NO** |
| `data/{ID}_identity.db` | crosswalk, inventory, reverse index, seats | largely (full crawl + seat-sync rebuild; purge/erasure completeness verify needed) |

**Remediate**

1. Checkpoint: nothing — re-ingest is safe by design.
2. State doc: restore `data/{ID}_state.json` from backup (DR.md). If NO
   backup exists, the suppression list is gone — rebuild it from the ledger:
   every `erase` entry without a later `unsuppress` re-enters via
   `forget-subject --id <subject> --confirm` (idempotent; items already gone
   withdraw nothing, suppression + a fresh ledger entry land). Do this BEFORE
   the next crawl, or erased subjects re-ingest.
3. Dead-letter malformed lines: the parseable records still replay
   (`retry-failed`); the warning names the count lost. Reconcile erasures:
   dry-run `forget-subject` for recently erased subjects must show 0 items.
4. Ledger: see [Ledger Verify failure](#ledger-verify-failure) — decide
   torn-vs-tamper BEFORE touching the file.
5. SQL mode: standard connectivity triage (login, AG listener, firewall);
   the connector retries transient errors and fails fast otherwise.

**Escalate** — disk-level corruption recurring on a host: infra (storage).
Suppression list rebuilt from the ledger: notify the DPO with the rebuilt
subject list so DSAR records reconcile.

---

## 429 storm

**Symptom** — `altrata_graph_throttle_429_total` climbing fast; log
`Graph 429 throttling — concurrency reduced to N` repeatedly;
`Retrying X throttled items in Ys`; throughput sagging; at the extreme,
dead-letters with `HTTP 429: throttled after all retries`.

**Diagnose**

1. Normal adaptive behaviour first: occasional 429s dial concurrency down and
   it ramps back after 3 clean batches — no action while depth ≈ 0.
2. A STORM (sustained floor concurrency, retry-exhausted 429 dead-letters)
   means the tenant-wide Graph connector quota is contended: check what else
   is ingesting (other connectors in this tenant, a sibling connector's full
   crawl, another shard of THIS connector).
3. `Retry-After ... exceeds cap; clamping to 60s` in bulk = Microsoft asking
   for longer waits than our ladder gives — clear overload signal.

**Remediate**

1. Reduce pressure: lower `GRAPH_BATCH_WORKERS` (or `GRAPH_CONCURRENT_BATCHES`)
   and/or `GRAPH_BATCH_SIZE`; in HA set `GRAPH_RETRY_JITTER=true` so nodes
   desynchronize.
2. Reschedule: move the full crawl (`--full-crawl-hours` window) off the
   other tenant ingest peaks.
3. After the storm: `$EXE retry-failed` drains the 429 dead-letters.
4. Chronic: shard datasets across connections (`GRAPH_CONNECTION_SHARDS`,
   docs/SHARDING.md) — per-connection quota multiplies.

**Escalate** — sustained storms with modest volume and no tenant contention:
Microsoft support with timestamps, request counts
(`altrata_graph_requests_total` deltas) and sample correlation ids.

---

## Auth failure

**Symptom** — commands fail with `Token request failed (400/401)`; or every
Graph call returns 401/403; `validate-config --strict` fails connectivity.
With `EVENTLOG_ENABLED`, error-id 3000 events carry the same lines.

**Diagnose**

1. Which mode is active: the run log's one-time line
   `Graph auth mode: client secret` or
   `Graph auth mode: certificate (...) (certificate thumbprint <T>)`.
   Certificate WINS whenever `GRAPH_CLIENT_CERT_PATH`/`_THUMBPRINT` is set.
2. Secret mode, `invalid_client`/400: expired or rotated
   `SECRET_AAD_APP_CLIENT_SECRET` (Key Vault mode: check the vault secret +
   the service's managed-identity access).
3. Certificate mode:
   - startup `ConfigurationError` naming `GRAPH_CLIENT_CERT_PATH` /
     `_THUMBPRINT` — file missing/wrong password/no private key/not found in
     store (the message states which);
   - AAD `invalid_client` with a VALID local cert — the cert is not
     registered on the app (or was rotated in AAD only): compare the logged
     thumbprint with Entra → App registrations → Certificates & secrets;
   - `700024` = assertion expired: host clock skew (assertions live 9 min) —
     fix NTP.
4. 403 (token OK, call refused): admin consent missing for
   `ExternalConnection.ReadWrite.OwnedBy` + `ExternalItem.ReadWrite.OwnedBy`,
   or the connection was created by a DIFFERENT app id (`OwnedBy` scoping).
5. Altrata API auth is separate (`Altrata token request failed`): its breaker
   is non-critical — feeds keep ingesting; fix `ALTRATA_CLIENT_ID` /
   `SECRET_ALTRATA_CLIENT_SECRET` when convenient.

**Remediate**

1. Rotate per SECURITY.md runbooks (secret and certificate rotations are
   written there step-by-step, including the no-downtime cert overlap).
2. Then `$EXE validate-config --strict` before restarting continuous mode.
3. A crawl paused by auth failure (breaker OPEN on repeated failures) resumes
   from its checkpoint once tokens flow — no data loss.

**Escalate** — credentials correct + consent present + still 401/403:
Entra sign-in logs for the app id (Conditional Access blocking the service
principal is the usual finding); tenant security team owns that policy.
