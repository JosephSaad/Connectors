# Runbooks

One entry per failure mode: **Symptom → Diagnose → Remediate → Escalate**.
Alert rules in `ops/prometheus-alerts.yml` and `ops/azure-monitor-alerts.kql`
link to these anchors; event ids reference `docs/SIEM.md`. All log excerpts
are searchable in `logs/{prefix}_{timestamp}/connector.log` (JSON with
`LOG_FORMAT=json`) and — Warning/Error only — the Windows Application event
log when `EVENTLOG_ENABLED=true`.

General escalation path everywhere below: connector operations owner → (source
issues) BDH/Hadoop platform team → (index issues) M365 tenant admin.

## Guard refusal (unfiltered object)

- **Symptom:** `guard_refusals_total` increases; log
  `'X' has no filter configured in config/filters.json...`; object listed in
  the crawl summary's failed objects; alert `GuardRefusalSpike`.
- **Diagnose:** `validate-config --strict` names every refusing object. Diff
  the last change to `config/filters.json` (or `BDH_FILTERS_PATH` — is it
  pointing at the right file? a MISSING file refuses everything). Remember the
  effectively-filtered rule: a filter whose only predicate is `dt isNotNull`
  does not count.
- **Remediate — fix the filter (the normal case):** add a real `partition`
  (`dt withinLastDays` is the highest-leverage) or record predicate for the
  object; re-run `validate-config --strict`; redeploy. The refusal fails only
  that object — other objects kept crawling; no state repair is needed.
- **When ALLOW_FULL_SCAN is legitimate:** small reference objects
  (Pricebook-sized, bounded by nature) that genuinely need every row. Prefer
  the per-object `fullScanAllowed` list (reviewable, versioned) over the
  global `ALLOW_FULL_SCAN=true`; treat any diff adding the global flag as a
  security-relevant change (`docs/THREAT_MODEL.md` §7). Never use either to
  silence a refusal on a large object — that converts a guard into an outage.
- **Escalate:** if the filter was correct and the refusal is new, someone
  changed the schema object list or the filters file outside change control.

## Oversize-skip partial crawl (sweep suppressed)

- **Symptom:** `partial_objects_total` and `sweeps_suppressed_total` increase;
  log `skipping oversize file ...` then
  `deletion sweep skipped — an oversize file was skipped`; crawl summary marks
  the object PARTIAL.
- **Diagnose:** the per-file skip log names the exact file and size. Compare
  with `BDH_MAX_FILE_BYTES` (default 1 GiB). One-off export hiccup or a
  legitimately growing partition file?
- **Remediate — decide about the oversized file, don't just raise the bound:**
  (a) if the file is a malformed/duplicated export, have the BDH team re-land
  the partition — next crawl heals everything; (b) if rows legitimately
  outgrew the bound, either get the export re-partitioned into smaller part
  files (preferred — bounded files are the memory-safety contract) or raise
  `BDH_MAX_FILE_BYTES` deliberately and size host memory/time accordingly.
  Until an untruncated FULL crawl completes, deletions for that object are
  intentionally NOT swept (the unread file's records would all look deleted) —
  expect stale items to linger, then run `reconcile --type X --fix` after the
  first clean full crawl if you want the catch-up immediately.
- **Escalate:** recurring oversize skips on the identity object are a
  different, louder failure — see "Identity directory incomplete".

## Row-cap hit (crawl PARTIAL)

- **Symptom:** alert kind `row_cap_hit`; log
  `BDH_MAX_RECORDS_PER_OBJECT=N row cap hit — the crawl is PARTIAL`;
  `partial_objects_total` + `sweeps_suppressed_total` increase.
- **Diagnose:** per-object fetch accounting
  (`partitions X scanned / Y pruned; records A scanned / B filtered / C matched`):
  is the object simply unfiltered-ish (huge matched count) or did a filter
  regress? A cap hit right after a filters.json change is a regression signal.
- **Remediate:** tighten the filter (preferred — see `docs/FILTERS.md` sizing
  guidance) or raise `BDH_MAX_RECORDS_PER_OBJECT` deliberately, accepting
  proportionally longer crawls and Graph quota. The sweep stays suppressed
  until a full crawl completes under the cap — same catch-up note as above.
- **Escalate:** if matched counts jumped without a config change, the source
  grew or the export duplicated rows — BDH platform team.

## Identity directory incomplete (sync fails loud — intentional)

- **Symptom:** identity sync throws; alert `identity_directory_incomplete`;
  log `Identity directory load for 'User' is INCOMPLETE (...) Refusing the
  sync`. Crawls that require the sync do not proceed.
- **Diagnose:** the message states the cause: row cap
  (`BDH_MAX_RECORDS_PER_OBJECT`) or an oversize file
  (`BDH_MAX_FILE_BYTES`) while reading the identity object
  (`BDH_IDENTITY_OBJECT`, default `User`).
- **Why it's loud:** a silently-partial user directory would resolve every
  omitted user to fallback/coarse ACLs for the WHOLE crawl — an access-control
  integrity failure, not an availability blip. Refusing is the designed
  behaviour; do not "work around" it.
- **Remediate:** raise the row cap / file bound FOR the identity object's
  scale, or narrow the User export (it needs id/email/active columns, not
  every field). Re-run `identity-dry-run --save` to verify, then resume
  crawls.
- **Escalate:** if the User export shrank/oversized suddenly, the nightly BDH
  load is suspect — platform team, before ACL-bearing crawls resume.

## WebHDFS flapping / breaker open (degraded mode)

- **Symptom:** `/ready` returns 503 DEGRADED; `circuit_breaker_state{dependency="hdfs"}`
  = 2; alert kind `degraded_mode`; log `Degraded mode: pausing new object
  crawls — circuit open for hdfs`.
- **Diagnose:** breaker trips only on REAL failures (5xx/timeout/connection —
  never 4xx/429). Check namenode health, Knox, network path, and — after a
  proxy/TLS change — `PROXY_URL`/`CA_BUNDLE_PATH` (both fail fast at startup,
  but a mid-flight proxy outage looks like transport failures).
- **Remediate:** nothing to repair on the connector: it paused at a safe
  checkpoint boundary, kept the sync cursor, and resumes via the breaker's
  half-open probe when HDFS recovers. Fix the source; watch
  `circuit_breaker_resets_total` tick and `/ready` return 200.
- **Escalate:** flapping (trip/reset cycles) longer than an hour → Hadoop
  platform team with the retry/ladder warnings (they name HTTP statuses and
  paths).

## dt-watermark gaps (missing partitions)

- **Symptom:** incremental crawls report `Partitions 0 scanned` (or matched
  counts collapse); `time() - last_crawl_completed_timestamp_seconds` grows;
  alert `WatermarkStale` (staleness beyond 26 h — the nightly-load contract
  plus slack).
- **Diagnose:** three candidates, in order: (1) BDH's nightly load didn't run
  or landed late — list the object's directory: is there a fresh `dt=` for
  yesterday/today? (2) the watermark is ahead of reality (clock skew, or a
  sync-state edit) — `logs/sync_state.json` / `dbo.SyncTimestamps` vs the newest
  `dt`; (3) `BDH_LAG_HOURS` too small for how late loads actually land.
- **Remediate:** late load → nothing; the overlap window (`BDH_LAG_HOURS`,
  default 24) re-reads the newest partitions on the next cycle, and
  re-ingesting is an idempotent PUT. Persistent late loads → raise
  `BDH_LAG_HOURS`. Suspected skipped window → force a FULL crawl
  (`full-deployment` / `ingest` without an incremental window) to re-baseline;
  full crawls ignore the dt watermark.
- **Escalate:** loads that stopped landing at all are a BDH pipeline outage —
  platform team; the connector will simply keep finding nothing new.

## Dead-letter growth

- **Symptom:** `dead_letter_depth` climbs; alert kind `dead_letter` once past
  `ALERT_DEADLETTER_THRESHOLD`.
- **Diagnose:** read the queue (`logs/failed_records_<CONNECTOR_ID>.jsonl` or
  `dbo.DeadLetter`): the `error` field clusters fast. Typical clusters:
  schema/property errors (a new BDH column type the graph-schema lacks),
  conversion crashes (`[Convert] ...` names the record and file), Graph 4xx
  on specific items. `WORKER_CRASH` entries are whole-object failures — read
  the run log at that correlation id. With
  `DEADLETTER_PAYLOAD_MODE=redacted` you still have ids, object types, errors
  and hashes — diagnosis is unchanged; only record values are absent.
- **Remediate:** fix the cause (schema/config/mapping), then
  `retry-failed --clear-on-success`. Entries whose records vanished from BDH
  are dropped automatically on retry (see the next runbook for the exception).
- **Escalate:** a sudden cluster of identical Graph 4xx across objects is a
  connection/schema drift — tenant admin (was the connection or schema edited
  out-of-band?).

## retry-failed inconclusive (oversize-blinded)

- **Symptom:** `retry-failed` finishes with entries still queued whose error
  reads `lookup incomplete: an oversize file (> BDH_MAX_FILE_BYTES) was
  skipped while searching, so the record cannot be confirmed gone from BDH`;
  log warning `not found, but the lookup skipped an oversize file`.
- **Diagnose:** the record wasn't found, but the id-lookup scan had to skip an
  oversize file — the miss is unproven (the record may live in exactly that
  file). The entry is deliberately KEPT, not dropped: dropping would silently
  lose a possibly-live record.
- **Remediate:** resolve the oversized file first (see "Oversize-skip partial
  crawl"), then re-run `retry-failed`: with the file readable the lookup
  becomes conclusive — the record is found and re-ingested, or proven gone and
  dropped.
- **Escalate:** none — this is the safety behaving as designed; only the
  oversize condition itself escalates.

## HA failover (node death)

- **Symptom:** a node stops heartbeating; its objects sit `claimed` until
  `HA_CLAIM_TIMEOUT_SECONDS` (default 300) passes; survivors log a takeover
  and resume from the dead node's checkpoint. `ha_claims_held` on the dead
  node flatlines; survivors' counts rise.
- **Diagnose:** `SELECT * FROM dbo.ObjectClaims WHERE Status='claimed'` —
  stale `HeartbeatUtc` identifies the dead node (`NodeId`). Host down, process
  crash (check its last run log / event log 1001-less stop), or SQL
  connectivity from that node only?
- **Remediate:** usually nothing — takeover + checkpoint resume is automatic;
  idempotent PUTs make the overlap harmless. Restart or replace the node at
  leisure; it rejoins the next cycle.
- **Escalate:** repeated same-node deaths → host/platform issue; ALL nodes
  losing SQL → treat as a state-backend outage (crawls pause; nothing is
  lost).

## Lease stuck (claim never released)

- **Symptom:** an object type never progresses; its claim row stays `claimed`
  with a FRESH heartbeat (so no takeover fires); `ha_claims_held` on one node
  is stuck above its actual work.
- **Diagnose:** a live-but-wedged worker (heartbeat timer runs even while the
  object worker hangs — e.g. a stuck stream read). On the owning node, take a
  dump/stack of the process; the run log shows the last per-file progress line
  for that object.
- **Remediate:** stop the wedged node gracefully (SCM stop / Ctrl+C — the
  heartbeat stops, the claim expires after `HA_CLAIM_TIMEOUT_SECONDS`, a
  survivor takes over from the checkpoint). If the process won't stop, kill
  it; same expiry path applies. Do NOT hand-edit claim rows to 'completed' —
  that fabricates work that never happened; if a claim must be cleared
  manually, delete the row and let a node re-claim it.
- **Escalate:** recurring wedges on the same object/file → capture the file
  name from the log and treat as a hostile/oversize export case.

## State corruption

- **Symptom:** warnings naming the exact file:
  `Sync-state file ... is corrupt ... treating connector as never-synced`,
  `Checkpoint file ... is corrupt ... restarts from chunk 0`,
  `Dead-letter file ... line N is not valid JSON ... skipping that entry`.
- **Diagnose:** each message states the blast radius, which is deliberately
  contained: watermark reset → wider (costlier, never lossy) next incremental;
  checkpoint reset → idempotent re-processing from chunk 0; torn dead-letter
  line → that entry only, all intact lines still load.
- **Remediate:** usually nothing — the fail-safe already applied. Recover the
  named file from backup if the wider re-read is too expensive
  (`docs/DR.md`); repair a torn JSONL line by hand if that one entry matters.
  On SQL, state integrity is the database's job — restore per `docs/DR.md`.
- **Escalate:** corruption without a crash/kill in the timeline (disk-full,
  AV interference, shared-file writers) → host owner; check free space and
  exclusions first.

## 429 storm (Graph throttling)

- **Symptom:** `throttled_429_total` climbs fast; log
  `Graph 429 throttling — concurrency reduced to N`; throughput sags; alert
  `Throttle429Storm`.
- **Diagnose:** self-inflicted (batch workers × shards × HA nodes too high for
  the tenant), tenant-wide contention (other workloads), or a service
  incident. The adaptive dial already stepped concurrency toward 1;
  `Retry-After` is honoured (numeric, 60 s clamp).
- **Remediate:** persistent storms → lower `GRAPH_BATCH_WORKERS`, enable
  `GRAPH_RETRY_JITTER=true` (mandatory advice in HA), stagger continuous
  schedules across nodes, or shard connections (`docs/SHARDING.md`) for real
  throughput instead of hammering one connection. Never raise retries to
  "push through" a storm.
- **Escalate:** storms with LOW connector concurrency and no tenant activity →
  Microsoft service health / support with timestamps and request counts.

## Token failure (Graph auth)

- **Symptom:** `Failed to acquire Graph token (HTTP 4xx/5xx)`; with a 401
  invalid_client everything Graph-side stops; breaker may open on token 5xx.
- **Diagnose:** the logged auth MODE line (`Graph auth mode: certificate` vs
  `client secret`) tells you which credential path is active — material is
  never logged. 400/401 `invalid_client`: expired secret, wrong tenant/client
  id, or (certificate) the app registration lacks the cert's thumbprint. A
  fail-fast `Invalid configuration: GRAPH_CLIENT_CERT_*` at startup names the
  local cert problem (missing file, wrong password, store lookup miss). AADSTS
  codes in the response body are authoritative.
- **Remediate:** rotate/replace per the rotation runbooks in `SECURITY.md`
  (secret: new value in `.env.local.user`/Key Vault; certificate: upload new
  cert to the app registration, stage `GRAPH_CLIENT_CERT_*`, restart —
  remember certificate WINS over secret, so a stale cert config masks a fresh
  secret). Sovereign clouds: verify `AAD_APP_OAUTH_AUTHORITY_HOST` +
  `GRAPH_SCOPE` pair with `GRAPH_BASE_URL`.
- **Escalate:** tenant admin for consent/permission changes
  (`ExternalConnection.ReadWrite.OwnedBy`, `ExternalItem.ReadWrite.OwnedBy`,
  `User.Read.All` must stay admin-consented).
