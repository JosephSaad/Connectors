# FEEDS — bulk deliveries, manifests, reconciliation

Primary ingestion is licensed bulk file feeds. Files arrive via external SFTP
into `FEED_PATH`; the connector processes it as a local directory.

## Delivery layout

```
FEED_PATH/
├── 2026-07-10_full/
│   ├── manifest.json
│   ├── person_profile_001.json
│   ├── organizations_001.csv
│   └── wealth_indicators_001.json
├── 2026-07-11_incr/
│   └── ...
└── archive/                # retention target (RETENTION_MODE=archive)
```

Any subdirectory containing `manifest.json` is a delivery (the `archive/`
directory is skipped). Deliveries are processed in delivery-id order.

## manifest.json

```json
{
  "deliveryId": "2026-07-10_full",
  "deliveryType": "full",
  "generatedUtc": "2026-07-10T02:00:00Z",
  "files": [
    {
      "name": "person_profile_001.json",
      "dataset": "PersonProfile",
      "sha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
      "recordCount": 12000
    }
  ]
}
```

Datasets: `PersonProfile`, `Organization`, `BoardMembership`,
`RelationshipPath`, `WealthIndicator`, `CareerHistory` (see
`config/schema.json`). Data files are JSON arrays of objects, JSONL, or CSV
with a header row.

`.jsonl` and `.csv` files are streamed line-by-line (uncapped). A `.json`
ARRAY must be parsed in memory, so it is capped at `FEED_JSON_MAX_MB`
(default 256 MB) — an oversized `.json` rejects the delivery with guidance to
re-deliver the dataset as `.jsonl`.

## Checksum gate

Before anything is ingested, every file's SHA-256 is recomputed and compared
with the manifest. **Any mismatch (or missing file) rejects the entire
delivery**: nothing is ingested, a `critical` `delivery_rejected` alert fires,
`altrata_deliveries_rejected_total` increments, a `rejected` reconciliation
report is written, and the delivery is NOT marked processed (a corrected
re-drop with the same delivery id is picked up by the next crawl).

The gate is enforced **twice**: once up front over the whole delivery, and
again at read time — each data file's records are parsed from the **same open
handle** its SHA-256 was just recomputed on, so a file swapped between the
upfront gate and the read (TOCTOU) is rejected instead of being ingested
under the manifest's hash.

## Item ids

Every record becomes externalItem id `{dataset}-{recordId}`. Record ids that
are already Graph-safe (alphanumeric plus `-`) keep exactly that shape; ids
containing any other character are sanitized (char → `-`) **and suffixed with
a short stable SHA-256 of the raw id**, so two distinct raw ids can never
collide onto one item id (e.g. `acct:12/3` vs `acct-12-3` — a collision would
let one subject's PUT overwrite another's item, or a tombstone/DSAR erasure
mis-target). The mapping is deterministic: tombstones and `forget-subject`
recompute the same id from the same raw id.

## Delta deliveries & tombstones

A delivery may be a **delta** against an earlier baseline: instead of
re-shipping every record, it carries only adds/updates plus **tombstones**
for records deleted upstream. A record is a tombstone when it carries
`op` / `action` / `change_type` ∈ {`delete`, `deleted`, `remove`, `purge`}
or `is_deleted` / `deleted` = `true`:

```json
[{"id":"P1","person_name":"Ada Lovelace"},          // upsert
 {"id":"P9","op":"delete"}]                          // tombstone
```

* Upserts ship through the `$batch` PUT pipeline as usual.
* Tombstones ship through **$batch DELETE** — the externalItem is withdrawn
  from the index, removed from the ingested-item registry, and counted in
  `altrata_items_deleted_total` (a 404 counts as success; deletes are
  idempotent, so re-crawling an old delta is safe).
* A failed withdrawal is dead-lettered with `op: delete`; `retry-failed`
  replays it as a DELETE, not a PUT.
* Because processing is delta-aware, an incremental crawl of new delta
  deliveries fully maintains the index — no full re-crawl per delivery.
  Deliveries are processed in delivery-id order, so a later delta's
  tombstone always lands after the baseline's upsert.

## Crawl semantics

* **Full crawl** — processes every delivery on disk (PUTs/DELETEs are idempotent).
* **Incremental crawl** — only deliveries missing from the processed ledger.
* Per-object checkpointing: the position `{deliveryId, dataset, fileName,
  recordIndex}` is saved every superchunk (`GRAPH_BATCH_SIZE ×
  GRAPH_BATCH_WORKERS` records — the unit shipped through the Graph $batch
  pipeline, see docs/RETRY.md) and on graceful stop; a crash or stop resumes
  exactly there (PUTs are idempotent, so a partially shipped superchunk
  re-PUTs safely). Files before the checkpoint are counted as already
  ingested, not re-PUT.
* Records that exhaust Graph retries are dead-lettered
  (`logs/failed_records_{CONNECTOR_ID}.jsonl` or the SQL table) with the full
  item payload for `retry-failed` replay. Records that fail **transform** are
  dead-lettered with `op: transform` (no item payload exists) — they are
  un-replayable by design: fix the source feed and re-ingest, or drop them
  with `retry-failed --retire-unreplayable`; they never count toward the
  dead-letter alert depth (see docs/RETRY.md). Appends are batch-atomic and
  safe under concurrent $batch workers (process-wide per-file lock).

## Reconciliation

After a delivery finishes, per file:

```
ingested + deleted + suppressed + deadLettered == manifest recordCount  →  reconciled
anything else                                                           →  mismatch
```

(`deleted` = delta-tombstone withdrawals; `suppressed` = records skipped
because the subject was erased (docs/ERASURE.md). Every manifest record must be
accounted for as an upsert, a withdrawal, a suppression, or a dead-letter.)

The report is written to
`logs/reconciliation_{CONNECTOR_ID}_{deliveryId}.jsonl`: one JSONL line per
file (`"type":"file"`) followed by one summary line (`"type":"summary"` with
status `reconciled` / `mismatch` / `rejected` and aggregate counts).

Only a **reconciled** delivery is marked processed. A mismatch leaves the
delivery eligible for the next crawl and fires a `reconciliation_mismatch`
alert.

## Retention

`RETENTION_DAYS` > 0: deliveries that were successfully processed more than
that many days ago are moved to `FEED_PATH/archive/` (`RETENTION_MODE=archive`,
default) or deleted (`RETENTION_MODE=delete`) at the end of each crawl.
Retention never touches connector state, the identity store, reports or the
audit log, and never touches an unprocessed delivery.
