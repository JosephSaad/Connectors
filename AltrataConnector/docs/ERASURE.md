# ERASURE — per-subject right-to-erasure (DSAR)

`forget-subject` is the individual-record complement to `purge-all`: it removes
one person from the connector and keeps them removed, on-theme for Altrata's
PII posture. `purge-all` ends the licence for the whole dataset; `forget-subject`
honours a data-subject access request (DSAR / right-to-erasure) for one person.

```
altrata-connector forget-subject --id P123456                  # dry-run report
altrata-connector forget-subject --id P123456 --confirm        # execute
altrata-connector forget-subject --email ada@contoso.com --confirm
altrata-connector unsuppress-subject --id P123456 --confirm    # lift the block
```

## What it does

1. **Resolve the subject** — by Altrata id (`--id`), or by `--email`: the email's
   CRM contact is looked up, and every Altrata id linked to that contact through
   the entity-resolution crosswalk is erased.
2. **Withdraw every externalItem for the person** — not just the PersonProfile
   item but all derived items (WealthIndicator, BoardMembership, CareerHistory,
   RelationshipPath). This uses an **item↔subject reverse index** populated at
   ingest time (`RecordItemSubjects`): each item is linked to the person id(s)
   it concerns (a relationship path links both endpoints), so erasure finds them
   all. Withdrawal is a Graph `$batch`-free per-item DELETE (idempotent).
3. **Remove local traces** — the ingested-item inventory, the entity-resolution
   crosswalk row, and the relationship-path index (round-2) entries for the
   person.
4. **Suppress re-ingestion** (the durability guarantee, below).
5. **Record it** in the tamper-evident erasure ledger (below).

`--confirm` is required to execute; without it (or with `--dry-run`) a report of
exactly what *would* be removed — subject id(s), the item-id list, crosswalk and
suppression status — is printed and nothing is mutated. Same safety pattern as
`purge-all`.

### Sharding (`GRAPH_CONNECTION_SHARDS`)

`forget-subject` and `unsuppress-subject` are **shard-aware**, like `retry-failed`
and `purge-all`. Under connection sharding a subject's items, reverse index,
crosswalk and suppression list live in *each shard's own store*, so erasure
resolves the subject across every shard, withdraws each shard's items on that
shard's own Graph connection, and suppresses the subject in every shard's state
(plus the base). A single authoritative ledger entry per subject is written to the
base ledger. `--email` resolution scans every shard's crosswalk, since an email may
only be linked in the shard that ingested that person. (Without this, an erasure
under sharding would withdraw and suppress nothing, and the subject would be
re-ingested on the next crawl.)

## Durability against re-delivery (the key property)

Erased subject ids go on a persisted **suppression list** in the state store
(`altrata_suppressed` in SQL Server; part of the state JSON in file mode). Every
subsequent crawl checks each upsert record's subject id(s) against the list and
**skips suppressed subjects** — a later feed delivery that re-introduces the
person does *not* re-ingest them. Skips are counted in reconciliation as
**suppressed** (not dead-lettered): `ingested + deleted + suppressed +
deadLettered == manifest count`, so a delivery full of erased people still
reconciles cleanly and is marked processed. Derived items of a suppressed person
(their wealth, board roles, paths) are skipped too.

`unsuppress-subject --id X --confirm` lifts the block (and ledgers it) so the
person may be ingested again on the next crawl.

## Tamper-evident erasure ledger

Separate from the purpose-of-use audit log (which records lawful *use*), the
erasure ledger records *removal*: `logs/erasure_ledger_{CONNECTOR_ID}.jsonl`,
append-only, one entry per erase / un-suppress:

```json
{"Seq":1,"TimestampUtc":"…","Actor":"joseph","Action":"erase",
 "SubjectId":"P123456","SubjectEmail":"ada@contoso.com",
 "ItemsRemoved":["PersonProfile-P123456","WealthIndicator-W1"],
 "PrevHash":"0000…","Hash":"9f86…"}
```

Entries form a **SHA-256 hash chain**: each `Hash` covers the entry's fields
plus the previous entry's `Hash`. Editing, reordering or deleting any entry
breaks every later link, so `ErasureLedger.Verify()` detects tampering without a
trusted external store. The ledger is retained across `purge-all` (the removal
record is the compliance artefact).

## Failure handling

If a Graph withdrawal fails, the subject is **still** suppressed and ledgered
(erasure is durable regardless of transient Graph errors), the item is removed
from the local inventory, and the failed DELETE is dead-lettered with `op:
delete` so `retry-failed` completes the Graph-side removal. The command reports
the incomplete count and returns non-zero.

## Seat invariant

Erasure only ever DELETEs — it never PUTs an item, so no ACL is authored on any
erasure path; the never-everyone seat invariant is untouched. Suppressed records
are skipped before transform, so they never reach ACL construction either.

## Metrics

| Metric | Type | Meaning |
|---|---|---|
| `altrata_subjects_erased_total` | counter | subjects erased via forget-subject |
| `altrata_items_erased_total` | counter | items withdrawn via erasure |
| `altrata_items_suppressed_total` | counter | records skipped at crawl time (suppressed subject) |
| `altrata_suppression_list_size` | gauge | erased subject ids currently suppressed |
