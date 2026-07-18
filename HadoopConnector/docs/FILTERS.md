# The filter layer (`config/filters.json`)

BDH holds **150M+ Salesforce-shaped rows**. The filter layer is the
connector's signature feature: a strong, per-object-type, config-driven
mechanism that minimizes what is read, parsed and ingested — and **fails
closed** when an object has no filter at all. Implementation:
`Filters/FilterConfig.cs` (models + strict loader), `Filters/FilterEngine.cs`
(evaluation), `Hdfs/PartitionScanner.cs` (pruning), `Hdfs/BdhFetcher.cs`
(guard + row cap + accounting).

## Evaluation order

Stages run strictly in this order, each cheaper than the one it protects:

1. **Partition pruning — zero file I/O.** During the breadth-first walk of
   `{root}/{object}/...`, every `key=value` directory segment is checked
   against (a) the **dt watermark** (incremental crawls skip `dt` partitions
   older than `last sync − BDH_LAG_HOURS`) and (b) the object's `partition`
   predicates. A pruned directory is never listed further; none of its files
   are opened. A predicate on a key that is **absent** at the current level
   never prunes (the key may appear deeper); pruning happens only on a
   present-key mismatch. The walk is depth-capped (8 levels) so a pathological
   layout cannot recurse forever.
2. **Streamed record predicates.** Surviving files are parsed as streams
   (CSV/JSONL rows yielded one at a time, bounded by `BDH_MAX_FILE_BYTES`) and
   each row is evaluated against the object's `anyOf`/`allOf` predicates.
   Rejected rows are counted and dropped before ACL resolution, conversion, or
   any Graph traffic.
3. **Row cap** (`BDH_MAX_RECORDS_PER_OBJECT`, default 500 000; `0` disables).
   A safety valve, never a silent truncation: hitting it stops the fetch,
   logs a warning, raises a `row_cap_hit` webhook alert and marks the crawl
   **PARTIAL** for that object — the object is listed in the crawl summary and
   its **deletion sweep is skipped** (the source id set is incomplete;
   sweeping would mass-delete live records). `reconcile` likewise refuses
   stale detection from a truncated fetch.

## File format

```jsonc
{
  "objects": {
    "<ObjectName>": {
      "partition": [ /* predicates on Hive partition KEYS */ ],
      "anyOf":     [ { "allOf": [ /* record predicates */ ] }, ... ],
      // OR (shorthand for a single anyOf group — never both):
      "allOf":     [ /* record predicates */ ],
      "notes": "free text, ignored"
    }
  },
  "fullScanAllowed": [ "<ObjectName>", ... ]
}
```

- `objects` — one entry per object type (names matched case-insensitively
  against `config/schema.json`).
- `partition` — an implicit AND of predicates over partition **keys**
  (`dt`, `region`, ...). Evaluated on directory names only. Date operators
  are typically used on `dt` (strict `yyyy-MM-dd`).
- `anyOf` — record-level logic: an **OR of AND-groups**; each entry must be
  `{"allOf": [ ...predicates ]}`. A row matches when at least one group's
  predicates all pass. A top-level `allOf` is shorthand for one group. No
  groups at all → every row matches (partition filters may still apply).
- `fullScanAllowed` — object names explicitly exempted from the fail-closed
  guard (see below).
- `notes` — documentation only.

**Validation is strict at load time.** An unknown operator, an unknown key in
an object entry (e.g. a typo like `"anyof"` or `"filters"`), a predicate
missing its `field`/`key` or `op`, missing/malformed operands (`in` without
`values`, `between` without exactly two values, a non-integer
`withinLastDays`), or both `anyOf` and `allOf` on one object — all are
**config errors** (`InvalidDataException` at startup / `validate-config`),
never silently ignored. A dropped filter at this scale is an outage. Field
lookup at evaluation time is case-insensitive; string comparison is
case-insensitive; numeric/date parsing is invariant-culture.

A predicate is:

```json
{ "field": "Status", "op": "equals", "value": "Active" }
{ "key": "region",   "op": "in",     "values": ["EMEA", "NA"] }
```

(`field` is used in record predicates, `key` in partition predicates; the
loader accepts either spelling.)

## Operator reference

| Operator | Operand | Semantics | Example |
|---|---|---|---|
| `equals` / `notEquals` | `value` (string) | case-insensitive string equality | `{"field":"Status","op":"equals","value":"Active"}` |
| `in` / `notIn` | `values` (non-empty array) | case-insensitive membership | `{"field":"Type","op":"notIn","values":["Archived","Dormant"]}` |
| `prefix` | `value` | case-insensitive starts-with | `{"field":"CaseNumber","op":"prefix","value":"UK-"}` |
| `contains` | `value` | case-insensitive substring | `{"field":"Company","op":"contains","value":"Ltd"}` |
| `gte` (alias `>=`) / `lte` (alias `<=`) | `value` (numeric) | numeric compare; a **non-numeric field value never matches** | `{"field":"AnnualRevenue","op":"gte","value":"100000"}` |
| `between` | `values` = `[low, high]` (exactly two) | numeric `low <= x <= high` (inclusive) | `{"field":"Probability","op":"between","values":["25","100"]}` |
| `withinLastDays` | `value` (non-negative integer) | date value within the last N days of the crawl's clock (UTC) | `{"key":"dt","op":"withinLastDays","value":"450"}` |
| `after` / `before` | `value` (date) | strict date comparison | `{"field":"CloseDate","op":"after","value":"2025-01-01"}` |
| `isNull` / `isNotNull` | none | value absent/empty vs present | `{"field":"Email","op":"isNotNull"}` |

Dates parse as `yyyy-MM-dd` or full ISO-8601 date-times (UTC-assumed). Every
value-comparing operator **fails** on a missing/empty field value (only
`isNull` matches those). Note that `value` is always a JSON string in the
file — `"value": "100000"` — numeric/date typing happens at evaluation.

## The fail-closed full-scan guard

An object in `config/schema.json` that has **no** partition predicate and
**no** record predicate refuses to crawl: `BdhFetcher.GuardFullScan` throws
`FullScanRefusedException`, the object is recorded as failed in the crawl
summary, and the crawl continues with the other objects. To allow a genuine
full scan you must be explicit, in one of two ways:

- list the object under `fullScanAllowed` in `filters.json` (preferred:
  per-object, reviewable, versioned), or
- set `ALLOW_FULL_SCAN=true` (global escape hatch — avoid).

A **missing** `filters.json` yields an empty filter set, so *every* object
trips the guard until filters (or exemptions) exist. `BDH_FILTERS_PATH`
overrides the file location.

`validate-config` reports unfiltered objects as warnings;
`validate-config --strict` turns them into **errors** — run it in your
deployment pipeline so an unfiltered 150M-row object never reaches
production. (The identity sync's read of the `User` export bypasses the guard
by design: it is a bounded directory read, still subject to the row cap and
file-size bounds.)

## Per-stage metrics

Per-object counts are logged after every fetch
(`partitions X scanned / Y pruned; records A scanned / B filtered / C matched`)
and exported on `/metrics` (prefix `hadoop_connector_`):

| Metric | Meaning |
|---|---|
| `partitions_scanned_total` | leaf partition directories whose files were read |
| `partitions_pruned_total` | directories pruned before any file I/O (dt watermark or partition filters) |
| `records_scanned_total` | rows read from source files |
| `records_filtered_total{stage="predicate"}` | rows rejected by record predicates while streaming |
| `records_matched_total` | rows that passed the filter layer and entered the pipeline |

The same numbers ride the `source.fetch` trace span as `bdh.records_scanned`,
`bdh.records_matched` and `bdh.partitions_pruned` tags (`docs/TRACING.md`).
A healthy filter shows a large `partitions_pruned_total` (cheap) and a
`records_matched_total` well below `records_scanned_total`.

## Sizing guidance at 150M rows

- **Prune on partitions first.** A record predicate still costs a full read
  and parse of every surviving file; a partition predicate costs one directory
  listing. If a constraint *can* be expressed on a partition key (`dt`,
  `region`, ...), express it there. The single highest-leverage filter is a
  `dt withinLastDays` partition predicate — it bounds the crawl to a window of
  nightly loads regardless of table size.
- **Aim well under the row cap.** The default cap (500 000 rows per object per
  crawl) is a failsafe, not a target: a truncated object is partial, alerted,
  and un-swept. If an object legitimately needs more, raise
  `BDH_MAX_RECORDS_PER_OBJECT` deliberately — and expect proportionally longer
  crawls and more Graph write quota (see `docs/SHARDING.md` for the
  throughput lever).
- **Mind the Graph side too.** Every matched record becomes an idempotent PUT
  on a rate-limited connection (~25 items/s planning figure). 500 000 matched
  rows is hours of ingestion on one connection; filter tighter or shard.
- **Prefer `in`/`equals` over `contains`.** All record predicates are cheap,
  but selective ones drop rows earlier in the OR-of-ANDs evaluation; put the
  most selective predicate first in each `allOf` group.
- **Re-validate after every edit** (`validate-config --strict`): the loader
  treats malformed filters as fatal precisely so a typo cannot silently turn
  into an unfiltered scan — and remember an **over-tightened** filter shrinks
  the source set, which the deletion sweep interprets as deletions (the
  mass-deletion guards exist for exactly that mistake, see
  `docs/DELETION_SYNC.md`).

## Worked examples

Active EMEA/NA contacts from recent partitions:

```json
{
  "objects": {
    "Contact": {
      "partition": [
        { "key": "region", "op": "in", "values": ["EMEA", "NA"] },
        { "key": "dt", "op": "withinLastDays", "value": "120" }
      ],
      "allOf": [
        { "field": "Email", "op": "isNotNull" }
      ]
    }
  }
}
```

Open pipeline plus recent closed-lost (OR of AND-groups):

```json
{
  "objects": {
    "Opportunity": {
      "partition": [ { "key": "dt", "op": "withinLastDays", "value": "450" } ],
      "anyOf": [
        { "allOf": [ { "field": "StageName", "op": "notIn", "values": ["Closed Lost"] } ] },
        { "allOf": [
            { "field": "StageName", "op": "equals", "value": "Closed Lost" },
            { "field": "CloseDate", "op": "withinLastDays", "value": "180" }
        ] }
      ]
    }
  }
}
```

A small reference object that genuinely needs everything:

```json
{
  "objects": {},
  "fullScanAllowed": ["Pricebook"]
}
```

## Troubleshooting

**"Why is my object refused?"** — the fail-closed guard:
`'X' has no filter configured in config/filters.json...`. The object has no
`partition` and no `anyOf`/`allOf` entry (or `filters.json` is missing /
pointed elsewhere by `BDH_FILTERS_PATH`), and it is not in `fullScanAllowed`.
Add a filter or an explicit exemption. Check the object name spelling —
lookup is case-insensitive but must otherwise match `schema.json`'s
`objectName`.

**"Why did startup/validate fail on my filters?"** — strict validation:
the error message names the object, field and problem (unknown operator,
missing operand, unknown key, both `anyOf` and `allOf`, non-object predicate).
Fix the config; there is deliberately no "skip bad filter" mode.

**"Why zero records?"** — work down the per-object fetch log line:

1. *Partitions 0 scanned* — everything pruned. On an incremental crawl the dt
   watermark may be ahead of the newest partition (did BDH's nightly load
   run? is `BDH_LAG_HOURS` big enough?); or a `partition` predicate
   mismatches the actual directory keys (`region=EMEA` vs `Region=emea` is
   fine — matching is case-insensitive — but `region=EU` vs `EMEA` is not).
   Also check `sourcePath`/`BDH_ROOT_PATH`: a wrong root lists nothing.
2. *Records scanned but all filtered* — record predicates too tight, or the
   field name doesn't exist in the export (a predicate on a missing field
   fails for every row unless the op is `isNull`). Remember `gte`/`lte`
   never match non-numeric values.
3. *Records matched but dropped with an id error* — the export lacks an `Id`
   column; the pipeline refuses an id-less crawl outright (it would corrupt
   deletion sync).
4. *Files skipped oversize* — the file exceeds `BDH_MAX_FILE_BYTES`
   (logged per file).

**"The crawl was marked PARTIAL"** — the row cap fired (`row_cap_hit` alert).
Tighten the filter (preferred) or raise `BDH_MAX_RECORDS_PER_OBJECT`. Until a
full, untruncated crawl completes, deletions for that object are not swept.
