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
