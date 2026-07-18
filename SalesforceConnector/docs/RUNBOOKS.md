# Alert Runbooks

One runbook per alert / failure mode. Each follows **Symptom → Diagnose →
Remediate → Escalate**. Alert rules that page you (`ops/prometheus-alerts.yml`,
`ops/azure-monitor-alerts.kql`) name these anchors. General operations live in
[RUNBOOK.md](RUNBOOK.md); this file is for when something is on fire.

Metric names below are from `/metrics` (`HEALTH_PORT`, prefix
`salesforce_connector_`). Log lines are the **exact** strings the code emits —
grep for them verbatim. `<logdir>` = the current `logs/{prefix}_{timestamp}/`
run directory.

---

## Adaptive concurrency degradation

There is **no circuit breaker** in this connector — the equivalent signal is
adaptive concurrency pinned at 1. Ingestion never trips open; it degrades to a
single Graph worker and grinds.

- **Symptom**: gauge `adaptive_concurrency_level` at `1` for >15 min (healthy:
  your `GRAPH_BATCH_WORKERS`, default 8). Log shows repeated
  `Graph 429 throttling — concurrency reduced to 1` without the matching
  `Graph concurrency ramped up to N` recovery lines.
- **Diagnose**:
  - `curl -s localhost:$HEALTH_PORT/metrics | grep -E "adaptive_concurrency|throttled_429"`
  - Rising `throttled_429_total` alongside a pinned level = Graph is throttling
    you; flat 429s + pinned level = the crawl finished a burst and hasn't
    recovered yet (3 consecutive successes per step ramp back — see
    `AdaptiveConcurrency` in `Graph/Ingest.cs`).
  - Check for OTHER load on the same connection: another node (HA), another
    tenant app, or a second connector sharing the tenant Graph quota.
- **Remediate**:
  - Usually nothing — the ramp-up is automatic. Sustained: lower
    `GRAPH_BATCH_WORKERS` (per node; divide by node count in HA), enable
    `GRAPH_RETRY_JITTER=true` in HA, or shard connections
    (`GRAPH_CONNECTION_SHARDS`, `docs/SHARDING.md`).
  - Confirm no runaway `retry-failed` loop is competing with a scheduled crawl.
- **Escalate**: if 429s continue at concurrency 1 with a single node on one
  connection, open a Microsoft support case for tenant Graph throttling; attach
  a `/metrics` snapshot and `<logdir>` (see `docs/CAPACITY.md` for expected
  sustainable rates).

---

## 429 storm

- **Symptom**: `rate(throttled_429_total)` spike; log floods with
  `Graph API transient error 429 for PUT https://graph.microsoft.com/... — retrying in Ns (attempt x/y)`
  and `Graph 429 throttling — concurrency reduced to N`. Possibly
  `Retry-After of Ns exceeds cap; clamping to 60s`.
- **Diagnose**:
  - Who else is hammering the connection? In HA:
    `SELECT * FROM dbo.vActiveCrawls` — more nodes than planned?
  - Did someone raise `GRAPH_BATCH_WORKERS` / `INGEST_GRAPH_BATCH_SIZE`?
  - Cross-check the crawl schedule: full crawl + `retry-failed` + `reconcile --fix`
    running simultaneously is self-inflicted.
- **Remediate**: the client already backs off (Retry-After honored exactly,
  60s cap) and self-throttles. Stagger the jobs, restore worker defaults, set
  `GRAPH_RETRY_JITTER=true` on every HA node. Items that exhausted retries land
  in the dead-letter queue — run `retry-failed` after the storm passes.
- **Escalate**: as for adaptive-concurrency degradation.

---

## Dead-letter growth

- **Symptom**: gauge `dead_letter_depth` climbing across crawls; webhook alert
  kind `dead_letter` (`Dead-letter depth N exceeded threshold T for connector 'X'`)
  when `ALERT_DEADLETTER_THRESHOLD` is set.
- **Diagnose**:
  - File backend: `wc -l logs/failed_records_<CONNECTOR_ID>.jsonl`, then error
    signatures: `jq -r .error logs/failed_records_<CONNECTOR_ID>.jsonl | sort | uniq -c | sort -rn | head`
  - SQL backend: `SELECT TOP 20 Error, COUNT(*) c FROM dbo.DeadLetter WHERE Retried = 0 GROUP BY Error ORDER BY c DESC`
  - One signature dominating = systematic (schema property mismatch, an object
    hitting a Graph limit, expired auth on one shard). Long tail = transient
    (throttling remnants).
  - `DEADLETTER_PAYLOAD_MODE=redacted` note in records is expected — payloads
    are hashed; the `error` field is always kept.
- **Remediate**:
  - Transient tail: `retry-failed --clear-on-success` off-peak.
  - Systematic: fix the cause first (see the error text; `validate-config --strict`
    catches schema/config drift), then `retry-failed`. Still-failing items are
    rewritten to `retry_pending_<id>_<ts>.jsonl` — target that file with
    `retry-failed --file` on the next pass.
- **Escalate**: a signature that survives a fix + retry cycle → open an issue
  with 2-3 sample records (redact if in full mode) and the object's
  `config/schema.json` entry.

---

## Deletion-sweep guard trip

- **Symptom**: webhook alert kind `deletion_sweep_skipped`; warning
  `<Object>: deletion sweep SKIPPED — S/N (P%) of the inventory would be deleted, above the DELETION_SYNC_MAX_PERCENT=25 safety guard.`
  Nothing was deleted for that object type.
- **Diagnose**: is the mass disappearance REAL (org cleanup, record transfer,
  changed `filterCondition`) or FALSE (Salesforce outage / partial id-only
  fetch during the sweep)?
  - `reconcile --type <Object>` (no `--fix`) re-runs the comparison and prints
    MISSING/STALE counts now.
  - Check `<logdir>` for Salesforce errors during the sweep window; check org
    trust status.
- **Remediate**:
  - False positive (outage): do nothing — the next full crawl re-sweeps.
  - Real mass deletion: `reconcile --type <Object> --fix` (explicit operator
    intent, bypasses the guard) or raise `DELETION_SYNC_MAX_PERCENT` and re-run
    the full crawl; restore the default afterwards.
  - Changed filter/shape: expected — run `reconcile --fix` per affected object.
- **Escalate**: guard trips on several object types simultaneously with a
  healthy org → treat as a fetch-layer bug; capture `<logdir>` and open an issue.
  Details: `docs/DELETION_SYNC.md`.

---

## HA failover

- **Symptom**: a node stops heartbeating (host down, network partition). Other
  nodes log nothing at first — reclaim happens lazily when a survivor asks for
  work. Node-level: service down alert, or `ha_claims_held` for that node drops
  to 0 while a crawl is open.
- **Diagnose**:
  - `SELECT * FROM dbo.vActiveCrawls` — open crawls and node claims.
  - `SELECT ObjectType, NodeId, HeartbeatUtc FROM dbo.ObjectClaims WHERE CrawlId = '<id>'` —
    claims with `HeartbeatUtc` older than `HA_CLAIM_TIMEOUT_SECONDS` (default
    300) are reclaimable.
  - Survivor logs eventually show the object resuming from its checkpoint.
- **Remediate**: none required — this is the designed path. A survivor reclaims
  each expired claim and resumes from the shared checkpoint; exactly one node
  closes the crawl. Restart/replace the dead node whenever convenient; it
  rejoins the next cycle. Verify afterwards: `crawls_completed_total`
  incremented and the sync timestamp advanced.
- **Escalate**: crawl never closes after all objects complete → see
  [Lease stuck](#lease-stuck); two nodes claiming the SAME object concurrently
  (should be impossible — claims are atomic) → capture both nodes' `<logdir>`
  and the `ObjectClaims` history, open an issue. Details: `docs/HA.md`.

---

## Lease stuck

A claim that never expires and never completes — the object's ingestion is
wedged.

- **Symptom**: one object type shows no progress for > 2× its normal duration;
  `object_records_fetched{object_type="X"}` flat while the crawl is open;
  `dbo.ObjectClaims` row for X keeps a FRESH `HeartbeatUtc` (that's the wedge:
  heartbeats continue while the worker is stuck). Node log may repeat
  `[HA] Heartbeat failed for X ... claim expires after 300s without one` —
  that's the *healthy* expiry path instead.
- **Diagnose**:
  - On the claiming node: is the process alive but stalled? Grab the log tail —
    stuck on a giant Salesforce chunk, an unkillable SOQL, or a Graph operation
    poll loop?
  - `SELECT * FROM dbo.ObjectClaims WHERE CrawlId='<id>' AND ObjectType='X'` —
    note NodeId.
- **Remediate**:
  1. Graceful stop of the stuck node's service (`Stop-Service`) — the chunk
     boundary stop saves the checkpoint and stops heartbeating.
  2. Claim expires after `HA_CLAIM_TIMEOUT_SECONDS`; a survivor reclaims and
     resumes from the checkpoint.
  3. Node truly hung (won't stop): kill the process. Checkpoints make a hard
     kill safe; the claim expires on schedule.
  4. Never delete `ObjectClaims` rows by hand while nodes run — expiry is the
     supported path.
- **Escalate**: same object wedges on multiple nodes → the object itself is the
  problem (pathological record, per-field retry loop) — set `DEBUG_OBJECT_TYPE`
  on a dev box, reproduce with `ingest-object --type X --verbose`, open an
  issue with the last 200 log lines.

---

## State DB corruption

- **Symptom** (SQLite/files): startup or crawl fails with
  `Dead-letter file <path> contains a corrupt entry at line N: ...`, a
  `SqliteException` naming `data/<CONNECTOR_ID>_identity.db` /
  `_inventory.db`, or checkpoint JSON parse errors naming the file.
  (SQL Server: `DBCC CHECKDB` findings / AG health events instead.)
- **Diagnose**: the error names file and line — that is the diagnosis. Decide
  scope: dead-letter file (retry backlog only), checkpoint (resume position
  only), identity/inventory DB (ACL + deletion-sweep inputs).
- **Remediate** (state loss = re-crawl cost, not data loss — [DR.md](DR.md)):
  - Corrupt dead-letter line: cut the bad line
    (`sed -n 'Np'` to inspect, remove it), keep the rest, `retry-failed`.
  - Corrupt checkpoint: delete `logs/checkpoint_<id>.json` → next crawl starts
    that object from chunk 1 (idempotent upserts, no duplicates).
  - Corrupt identity/inventory DB: restore from backup ([DR.md](DR.md)) or
    delete the file and run a **full** crawl (identity crawl rebuilds
    mappings; inventory rebuilds during ingest — deletion sweep is
    automatically inert for an object until its inventory is warm again).
  - SQL Server: standard restore, then `reconcile` to measure drift.
- **Escalate**: recurring corruption on healthy storage → check for two
  non-HA processes sharing one state dir (the classic cause: two services
  pointed at the same `SFCONNECTOR_HOME` without `USE_SQL_SERVER`+`HA_MODE`).

---

## Token / auth failure

- **Symptom**: crawls abort immediately. Graph side: `GraphApiError` with 401
  `InvalidAuthenticationToken` or `Azure.Identity.AuthenticationFailedException`
  / `CredentialUnavailableException` in the log and console. Salesforce side:
  `invalid_client` / `invalid_grant` in the OAuth token response. Webhook alert
  kind `crawl_failed` fires (`Full deployment (full) failed: ...`).
- **Diagnose**:
  - `validate-config --strict` — probes both token endpoints and names the
    failing credential explicitly.
  - Which auth mode? Startup log line: `Graph auth mode: client certificate (...)`
    or `Graph auth mode: default credential chain (...)`.
  - Expired secret/cert? Check the app registration's credential expiry dates;
    for cert mode the startup line prints the certificate's `expires` date.
- **Remediate**: rotate per [../SECURITY.md](../SECURITY.md) (zero-downtime
  order: add new credential → update env/Key Vault → restart service → remove
  old). Key Vault users: confirm `KEY_VAULT_URI` reachability and the managed
  identity's `get` secret permission.
- **Escalate**: credentials verified good but tokens rejected → check tenant
  conditional-access/service-principal policies with your identity team; for
  sovereign clouds confirm `GRAPH_BASE_URL`/`GRAPH_SCOPE`/`AZURE_AUTHORITY_HOST`
  are consistent.

---

## Crawl stalled

- **Symptom**: `time() - last_crawl_completed_timestamp_seconds` exceeds the
  crawl interval + expected duration (alert `CrawlStalled`); or
  `crawls_started_total` advances but `crawls_completed_total` doesn't;
  `uptime_seconds` resetting repeatedly = crash-restart loop.
- **Diagnose**:
  - Service up? `Get-Service SalesforceCopilotConnector` / container status.
    Crash-looping → Windows Application event log (source
    `SalesforceConnector`, event id 3000) or `<logdir>` tail for the exception.
  - Alive but idle: is it *between* scheduled cycles? (`--full-crawl-hours` /
    `--incremental-hours` — a stall alert must allow for the schedule.)
  - Alive and mid-crawl: `object_records_fetched` advancing? Advancing = slow,
    not stalled (429 storm? giant object?). Flat → [Lease stuck](#lease-stuck)
    diagnosis applies even single-node (the wedge modes are the same).
  - HA: crawl open but no claims → all nodes think another node owns the work:
    `SELECT * FROM dbo.vActiveCrawls`.
- **Remediate**: crash loop → fix the named config/auth error (most common) —
  the service restart policy (30s/60s/5min) will then recover on its own.
  Wedged process → graceful stop, start; it resumes from the checkpoint. Clock
  skew in HA (claims "in the future") → fix NTP.
- **Escalate**: a reproducible wedge with a clean config → capture `<logdir>`,
  a dump if possible (`dotnet-dump collect -p <pid>`), and open an issue.
