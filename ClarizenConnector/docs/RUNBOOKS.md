# Runbooks

One entry per alert / failure mode: **Symptom** (the exact log line, alert
kind or metric) → **Diagnose** → **Remediate** → **Escalate**. Alert rules in
`ops/prometheus-alerts.yml` and `ops/azure-monitor-alerts.kql` link to the
anchors on this page. Metrics are on `/metrics` (`HEALTH_PORT`), alert kinds
are the `kind` field of `ALERT_WEBHOOK_URL` posts, log lines are grep-exact.

**Escalation ladder** (referenced below): L1 operator → L2 connector owner →
L3 vendor support (Planview for Clarizen-side, Microsoft for Graph-side).

---

## Clarizen breaker open

- **Symptom**: log `Circuit 'clarizen': TRIPPED (open) — failing fast for Ns.`;
  metric `clarizen_connector_circuit_breaker_state{dependency="clarizen"} == 2`;
  usually followed by the `degraded_mode` alert.
- **Diagnose**: the source API returned sustained 5xx/timeouts (4xx and
  honoured 429 never trip it). Check preceding `clarizen_connector.clarizen`
  errors for the status; check Planview status page; `curl` the
  `CLARIZEN_BASE_URL` from the crawl host (proxy in scope — see
  [proxy problems](#token--auth-failure)).
- **Remediate**: nothing to do on the connector — it fails fast, retains the
  checkpoint, does not advance the cursor, and half-open probes auto-recover
  (`Circuit 'clarizen': recovered (half-open→closed).`). If the outage is a
  local network/proxy change, fix that; the next probe closes the breaker.
- **Escalate**: > 1 h open → L2; Planview-side outage confirmed → L3 vendor
  ticket. Do NOT set `CIRCUIT_BREAKER=false` to "fix" it — that just restores
  hammering.

## Graph breaker open

- **Symptom**: log `Circuit 'graph': TRIPPED (open) — failing fast for Ns.`;
  metric `clarizen_connector_circuit_breaker_state{dependency="graph"} == 2`;
  ingest responses log `circuit open (Graph); failing fast`.
- **Diagnose**: distinct from Clarizen-side — this is the Graph API or the
  Entra token endpoint (a 5xx from the token endpoint counts as a Graph
  failure by design). Check `Failed to acquire Graph token (HTTP 5xx)` lines
  vs batch-level 5xx; check the M365 service health dashboard.
- **Remediate**: items in flight were neither lost nor dead-lettered; the
  crawl resumes from the checkpoint when the breaker closes. If the token
  endpoint is the failing party with 4xx (not 5xx), that is
  [token / auth failure](#token--auth-failure) instead — fix credentials.
- **Escalate**: > 1 h open with healthy M365 dashboard → L2 (suspect proxy /
  TLS inspection / egress rules); confirmed M365 incident → track vendor.

## Degraded mode

- **Symptom**: alert `degraded_mode` (`crawl paused in degraded mode (circuit
  open for ...)`); `/ready` returns
  `503 DEGRADED: circuit open for <dependency>` while `/health` stays 200.
- **Diagnose**: one of the two breaker runbooks above — the alert names the
  open dependency.
- **Remediate**: none needed for the pipeline itself: checkpoint retained,
  sync cursor NOT advanced, next continuous cycle is scheduled on a bounded
  backoff and auto-recovers. Verify recovery: `/ready` back to 200 and
  `crawls_completed_total` advancing.
- **Escalate**: repeated flapping (trip/recover cycles over hours) → L2 with
  the trip/reset counters (`circuit_breaker_trips_total`).

## Crawl aborts with a Graph-schema configuration error

Three symptom shapes, raised by the two subclasses of
`GraphSchemaConfigurationException` (`UndeclaredGraphPropertyException` covers
the first two). All three mean **connector/config defect, not bad source data**,
and all three abort
on purpose: they hit every record, so dead-lettering them one at a time would
report a completed run with an empty index *and advance the sync cursor past
data that was never indexed*. Nothing is PUT for the object in progress, and no
property is ever silently dropped from an item that ships anyway.

### `UndeclaredGraphPropertyException` — undeclared name

- **Symptom**:
  `Graph property 'X' is not declared in the connection schema ('...graph-schema.json')`.
- **Meaning**: the code stamped a property `config/graph-schema.json` does not
  declare; Graph would reject every item carrying it.
- **Remediate**: add the named property to `config/graph-schema.json` (name,
  type, `is*` flags), re-`deploy` the connection schema, and re-run. Or, if the
  stamp was unintended, remove it. `validate-config` may not have flagged this —
  its stamped-side enumeration is best effort (see above).
- **Escalate**: if the property name is one nobody recognises, treat it as an
  unreviewed code change to a stamper and go to L2.

### `UndeclaredGraphPropertyException` — blank name

- **Symptom**: `Graph property name '' is blank or whitespace-only.`
- **Meaning**: almost always a `selectedFields` entry in `config/schema.json`
  mapping a field onto an empty Graph property name. No connection schema can
  declare it, so every record of that object type would fail.
- **Remediate**: give the mapping a property name, or remove the field.
  `SchemaConfig.Load` rejects this shape at startup, so reaching it at crawl time
  means the blank name came from somewhere other than `selectedFields` — go to
  L2 with the object type in the message.

### `GraphSchemaUnavailableException` — declaration unusable

- **Symptom**: `The Graph connection schema '...' could not be read` or
  `Could not locate config/graph-schema.json`.
- **Meaning**: the declaration is missing, is not a JSON array, is unparseable,
  or declares no usable names. Without it the connector cannot know whether
  *any* item is deployable.
- **Remediate**: restore/repair `config/graph-schema.json` and run
  `validate-config` before re-running the crawl. Check the working directory —
  the file is probed relative to it and to the install directory.

## Dead-letter growth

- **Symptom**: alert `dead_letter` (`Dead-letter depth N exceeded threshold
  ...`); metric `clarizen_connector_dead_letter_depth` rising;
  `items_failed_total` advancing.
- **Diagnose**: `logs/failed_records_<CONNECTOR_ID>.jsonl` (or
  `dbo.DeadLetter`) — group by `error`. Typical buckets: schema mismatch
  (`HTTP 400` naming a property → fix `config/graph-schema.json`; run
  `validate-config` first — it reports most such drift offline before a crawl,
  though its stamped-side enumeration covers a fixed set of stamper call sites,
  so a clean preflight is not proof of no drift),
  transform
  crashes (`[transform chunk N]` → source data issue), auth (`HTTP 401/403` →
  [token / auth failure](#token--auth-failure)). `correlation_id` ties each
  record to its crawl cycle in the logs. In `DEADLETTER_PAYLOAD_MODE=redacted`
  compare the per-field `sha256:` hashes across records to spot "same payload
  keeps failing".
- **Remediate**: fix the cause, then `retry-failed --clear-on-success` — every
  record is re-fetched fresh from Clarizen (works identically in redacted
  mode). Records whose source row is gone are dropped automatically
  (`no longer exists in Clarizen; dropping from dead-letter.`).
- **Escalate**: same error bucket persisting after remediation + retry → L2
  with one full dead-letter record and its correlated log window.

## Deletion-sweep guard trip

- **Symptom**: alert `deletion_sweep_skipped`; log
  `<Object>: deletion sweep SKIPPED — ... above the DELETION_SYNC_MAX_ITEMS=N
  absolute safety cap.` or `... (P%) of the inventory would be deleted, above
  the DELETION_SYNC_MAX_PERCENT=N safety guard.`
- **Diagnose**: is the drop REAL (mass archive/cleanup in Clarizen, object
  type removed from `config/schema.json`) or a false signal (source outage,
  truncated TDW export — an empty source result is the classic signature)?
  Compare the alert's `stale`/`inventory` counts with what Clarizen shows.
- **Remediate**: real → temporarily raise the guard (or set
  `DELETION_SYNC_MAX_ITEMS=0` / `_MAX_PERCENT=0` for ONE run), re-run the full
  crawl, restore the guards. False → fix the source problem (TDW export path,
  API health) and re-run; nothing was deleted.
- **Escalate**: uncertain whether the drop is real → L2 BEFORE loosening
  guards. The guard exists precisely for this moment.

## Webhook flood / 401 spike

- **Symptom**: metric `clarizen_connector_webhook_events_rejected_total`
  climbing; logs `Webhook: rejected a post with an invalid or missing
  'X-Clarizen-Signature' signature.` (401 spike), `Webhook: rejected an
  oversize body.` (413 flood), or raw request-rate at the ingress.
- **Diagnose**: 401s from the legitimate sender → secret mismatch (mid
  rotation? see `SECURITY.md` § webhook secret). 401s from unknown IPs →
  probing/forgery attempts — the validate-before-parse boundary means nothing
  was parsed or enqueued. Oversize/high-rate from one IP → flood.
- **Remediate**: forgery/flood → block source at the ingress/firewall (the
  listener has no rate limiter by design); rotation mismatch → complete the
  rotation (brief-reject window is documented — resend/wait, polling
  reconciles anything missed). Receiver down entirely
  (`webhook_receiver_up == 0`): check `could not bind` logs (URL ACL, port
  collision); polling remains the correctness backstop either way.
- **Escalate**: sustained targeted forgery attempts → security team with
  ingress logs; rotate the secret regardless.

## TDW export malformed

- **Symptom**: log `TDW export '<path>' for object '<Object>' failed to parse`
  (crawl continues via REST for that object); or a sweep-guard trip right
  after a TDW-based full crawl (truncated export = mass-delete signature).
- **Diagnose**: inspect the file — truncation (size vs previous run),
  encoding, header drift (renamed columns), partial write while the export
  job was still running.
- **Remediate**: re-run the upstream export; ensure the export job finishes
  before the crawl starts (schedule gap); REST fallback already kept the crawl
  correct, only slower/budget-heavier.
- **Escalate**: recurring truncation → owner of the TDW export job; column
  drift → L2 to update `config/schema.json` mappings.

## Financial-governance classification failure

- **Symptom**: startup failure `Invalid configuration: FINANCIAL_DATA_MODE=acl
  requires FINANCIAL_DATA_GROUP_ID.` / `FINANCIAL_DATA_MODE must be one of tag
  | filter | acl.`; or items dead-lettered with `[transform chunk N]` errors
  on objects with `financialFields`; or an audit finds financial values
  visible to the wrong audience.
- **Diagnose**: config errors are fail-fast and name the setting. For
  visibility findings: check the mode actually deployed (`validate-config`),
  and whether the field is listed in `financialFields` in
  `config/schema.json` (an unlisted field is NOT governed). For `acl` mode,
  confirm `FINANCIAL_DATA_GROUP_ID` is the intended Entra group.
- **Remediate**: fix mode/group/field list; re-run a FULL crawl — items are
  re-PUT with corrected properties/ACLs (idempotent). For dead-letter
  hygiene under governance, run `DEADLETTER_PAYLOAD_MODE=redacted` (a
  redacted record never contains a financial value — tested).
- **Escalate**: any confirmed exposure of financial data to unintended
  readers → security/compliance immediately, with the crawl window and the
  affected object types.

## HA failover

- **Symptom**: a node stops heartbeating (host down); logs on survivors show
  object claims being taken over; gauge `clarizen_connector_ha_claims_held`
  drops to 0 on the dead node / rises on survivors; crawl still closes
  (`Crawl '<key>' closed by node <node>`).
- **Diagnose**: this is the DESIGNED path — a dead node's claims expire after
  `HA_CLAIM_TIMEOUT_SECONDS` (default 300) and survivors resume from that
  object's checkpoint. Verify: crawl row reaches `closed`/`failed`, sync
  timestamp written by exactly one node.
- **Remediate**: restart/replace the dead node; it rejoins the next cycle.
  Nothing to clean up — claims are single-row, parameterized, idempotent.
- **Escalate**: crawl does NOT close after all claims are terminal →
  [lease stuck](#lease-stuck).

## Lease stuck

- **Symptom**: crawl never closes; `dbo.ObjectClaims` row pinned in
  `Status='claimed'` with a fresh heartbeat but no progress (checkpoint chunk
  index not advancing); `ha_claims_held` flat > 0 on one node across cycles.
- **Diagnose**: a live-but-wedged worker (breaker open on that node only?
  network partition where the node can reach SQL but not Clarizen/Graph?).
  Distinguish from normal long objects via the checkpoint: `SELECT * FROM
  dbo.Checkpoints WHERE ConnectorId=...` — advancing chunk = just slow.
- **Remediate**: stop the wedged node gracefully (SCM stop — finishes the
  chunk, saves the checkpoint). Its heartbeat stops, the lease expires after
  `HA_CLAIM_TIMEOUT_SECONDS`, a survivor takes over from the checkpoint. Only
  if the process is unkillable: kill it — checkpoints make a hard kill safe.
  Never hand-edit `dbo.ObjectClaims` while nodes are running.
- **Escalate**: recurring wedge on one host → L2 with that node's run dir
  (`logs/<prefix>_<timestamp>/connector.log`).

## State corruption

- **Symptom**: log `State file '<path>' exists but could not be parsed (...) —
  <consequence>.` where consequence is `treating as no previous sync` (delta
  cursor lost), `resuming without a checkpoint` (chunks re-sent), or
  `starting a fresh completed-chunk map`; dead-letter reader logs
  `line N is not valid JSON (...) — skipping it. Raw line: ...`.
- **Diagnose**: usually a crash mid-write or disk-full. Corruption is
  contained by design: cursor loss ⇒ the next incremental behaves like a
  first run (idempotent PUTs — cost, not correctness); torn dead-letter lines
  are skipped per line with the raw line preserved in the log.
- **Remediate**: free disk / fix the volume; optionally restore state from
  backup (`docs/DR.md`) to avoid a full re-crawl; for a torn dead-letter line,
  recover the failure detail from the logged raw line if needed.
- **Escalate**: corruption without a crash/disk event → L2 (suspect two
  processes sharing one state dir — check for a second service instance).

## 429 storm

- **Symptom**: metric `clarizen_connector_throttled_429_total` climbing fast;
  logs `Graph API transient error 429 for PUT ... — retrying in Ns` and
  possibly `Retry-After of Ns exceeds cap; clamping to 60s`.
- **Diagnose**: throughput exceeds the tenant's Graph budget — check what
  changed: first full crawl (expected!), raised `GRAPH_CONCURRENT_BATCHES`,
  another workload sharing the tenant quota, or an M365 advisory.
- **Remediate**: the connector already self-heals — honoured Retry-After,
  60 s clamp, adaptive worker dial-down toward 1. For a persistent storm:
  lower `GRAPH_CONCURRENT_BATCHES`, enable `GRAPH_RETRY_JITTER=true` (always,
  in HA), spread continuous schedules across shards/tenants off-peak. Never
  trips the breaker (429 is flow control, not an outage) — do not "fix" it
  with breaker knobs.
- **Escalate**: sustained storms at modest concurrency → Microsoft support
  with tenant id + time window (quota review).

## Token / auth failure

- **Symptom**: log `Failed to acquire Graph token (HTTP 400/401)` (Graph
  side), `Clarizen login failed (HTTP 401)` (source side), or startup
  `Invalid configuration: Missing SECRET_...`; alert `crawl_failed` with the
  same text. Certificate mode: `GRAPH_CLIENT_CERT_PATH '<path>' could not be
  loaded (...)` or `GRAPH_CLIENT_CERT_THUMBPRINT '<tp>' was not found`.
- **Diagnose**: 4xx from the token endpoint is credentials/config, NOT an
  outage (5xx is — that's the [Graph breaker](#graph-breaker-open)). Expired
  client secret, revoked cert, wrong tenant id, Clarizen password rotated
  out-of-band, Key Vault access lost (look for `Failed to fetch secret ...
  from Key Vault; falling back to environment variable`). The Graph auth mode
  in the log (`Graph auth mode: certificate (...)` / `client secret (...)`)
  tells you which credential is actually in play.
- **Remediate**: rotate/replace per `SECURITY.md` runbooks (secret, cert,
  Clarizen password each have one); `validate-config --strict` proves the fix
  before restarting the service.
- **Escalate**: credentials verified correct but still rejected → L2, then
  vendor (conditional-access / service-principal policy changes are the usual
  culprit).
