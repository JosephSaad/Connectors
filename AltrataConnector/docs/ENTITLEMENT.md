# ENTITLEMENT — seat model, PII posture, purge

Altrata data is licensed **per seat**. This connector's signature invariant:

> Every externalItem ACL grants ONLY the licensed seat principals.
> `everyone` / `everyoneExceptGuests` grants are structurally impossible.

## Seat sources

Exactly one of:

1. **`SEAT_GROUP_ID`** — a single Entra security group id. The group is the
   only ACL principal; Microsoft Search evaluates membership at query time,
   so seat management happens in Entra.
2. **Seat list file** — `config/seats.json` (or `SEAT_LIST_PATH`):
   ```json
   { "users": ["a@contoso.com", "3f2a1b7c-...-0b3c"], "groups": [] }
   ```
   Plain-array form is accepted too. UPNs vs object IDs are auto-detected
   (GUID → object id). Duplicates are removed case-insensitively.

## Enforcement (fails closed)

* `SeatAclBuilder.BuildAcl` throws `EntitlementViolationException` on an empty
  seat set — a crawl aborts **before any item is PUT**.
* `AssertNeverEveryone` re-checks every ACL at transform time and again in
  `retry-failed` replay (defence in depth).
* `retry-failed` never re-PUTs the ACL captured at dead-letter time: the ACL
  is **rebuilt from the current seats** before replay (empty seat list ⇒ the
  record stays queued — fail closed) and the hash of the ACL actually sent is
  recorded, so a seat removed after the failure is never re-granted and the
  re-ACL reconciliation stays truthful (docs/RETRY.md).
* An entitlement violation is never dead-lettered; it aborts the run and fires
  a `critical` alert.

## Seat changes → re-ACL pass

The seat set hash (SHA-256, order/case-insensitive) is stored per ingested
item and in the state store. On every crawl / `seat-sync`:

1. Seats are re-read and stored in the identity store.
2. Hash differs from the committed hash → every item whose stored ACL hash
   differs is re-ACLed via PATCH, and its registry row updated.
3. Only after the pass completes is the new hash committed — an interrupted
   pass re-triggers on the next run.

`altrata_seat_count` and `altrata_reacl_passes_total` surface this on
`/metrics`.

### Entitlement freshness — `IDENTITY_SYNC_ON_INCREMENTAL` (default true)

Seats are re-read on **every** crawl (full and incremental), so newly ingested
items always carry the current ACL and a seat change is *detected* each crawl.
`IDENTITY_SYNC_ON_INCREMENTAL` (default `true`) additionally runs the re-ACL
**sweep over existing items** on incremental crawls, so a seat change is
enforced at the incremental cadence rather than only on full crawls. Set it
`false` to defer the (potentially large) sweep to full crawls only.

**Residual lag is non-real-time.** A mid-cycle seat removal is enforced at the
**next** crawl, not instantly. Operationally: schedule incrementals frequently
(e.g. `ingest --incremental` hourly, or a dedicated `seat-sync` cadence) so the
worst-case exposure of a de-provisioned seat is bounded by that interval. For
hard, immediate cut-off of the most sensitive tier, combine with
`CLASSIFICATION_ENFORCE_ACL` (top-tier items locked to a small reviewer group).

### Why the cadence is the *only* connector-side lever

A Graph connector **cannot** re-evaluate entitlement per grounding call. Graph
trims results against the ACL **stored on the item** at ingestion time, and
offers no callback into Altrata at query time. There is no hook, anywhere in the
connector, that runs when Copilot grounds a query.

So authorisation staleness is bounded by exactly one thing: how often the seat
re-ACL sweep runs. That is the incremental cadence (when
`IDENTITY_SYNC_ON_INCREMENTAL` is true, the default), and nothing else.

### Sub-hour cadence — `--incremental-minutes`

`--incremental-hours` is an integer flag with a floor of 1, so it cannot express
anything below 60 minutes. `--incremental-minutes <1–10080>` can:

```
altrata-connector ingest --continuous --incremental-minutes 15
```

It **wins over** `--incremental-hours` when both are given (it is the more
specific unit). Unused, `--incremental-hours` behaves exactly as before — the
default is still 4 hours.

**The trade-off, honestly.** Every incremental re-reads the seat list and, if it
changed, re-ACLs every affected existing item: source API calls plus one Graph
write per item. A 5-minute cadence costs **12×** the sweeps of an hourly one,
against Graph throttling limits that are shared with ingestion. Tightening the
staleness budget is not free, and past some point it competes with the ingestion
it is protecting.

The scheduler wakes at most every 30 s (for graceful-stop responsiveness) but
never sleeps past a due crawl, so a sub-hour interval is honoured to within one
loop iteration rather than rounded up.

### Minute-level freshness — use `seat-sync` under an external scheduler

For tighter than the incremental crawl can sensibly go, run the standalone
command on its own schedule (cron / Task Scheduler):

```
altrata-connector seat-sync
```

It does the **seat sweep only** — load seats, and re-ACL existing items if the
seat hash changed — without dragging a full delivery-reconciliation crawl along
with it. That is the right shape for minute-level entitlement freshness: cheap
when nothing changed (a hash comparison), and it does not contend with the
ingestion schedule.

### Deferred, deliberately not built

* **Agent-layer / retrieval-time entitlement checks.** A check at grounding time
  belongs in Copilot Studio or MCP middleware — the layer that *sees* the query.
  It is not connector code, and building a half-version of it here would imply a
  guarantee the connector cannot make.
* **A redistribution marker sourced from the feed manifest.** We have not
  confirmed the vendor's manifest carries one. Reading a field that may not exist
  and stamping items from it would be inventing provenance, not recording it.
  Revisit once the manifest contract is confirmed.

## PII posture

* Every item carries `piiClassification` — the HIGHEST personal-data label for
  its dataset/fields: `Non-Personal` < `PII-Personal` < `PII-Sensitive-Wealth`
  (wealth/net-worth/income indicators always classify as sensitive).
* **Purpose-of-use audit**: every Altrata API lookup appends
  `{timestampUtc, actor, action, altrataId, purpose, billable}` to the
  append-only `logs/audit_{CONNECTOR_ID}.jsonl`. `ingest-item` requires
  `--purpose` (and accepts `--requested-by`).

## Purge (license-end obligation)

```
altrata-connector purge-all              # DRY RUN: counts only, deletes nothing
altrata-connector purge-all --confirm    # withdraw every item + wipe state
```

The dry run reports: items to withdraw, dead-letter depth, crosswalk links,
processed-delivery ledger size and the billable-lookup counter.

`--confirm` deletes every registered externalItem from Microsoft Graph
(idempotent; 404s are treated as already gone), then wipes the state store and
identity store. If any withdrawal fails, state is **kept** so the purge can be
re-run to completion. The audit log is intentionally retained — it documents
lawful use; delete it manually if your agreement requires that too.
