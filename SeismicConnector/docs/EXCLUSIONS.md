# EXCLUSIONS — the "No MNE" ingest-time filter

The connector's signature compliance feature: content classified as a
**material non-public event (MNE / MNPI)** — or residing in a restricted
library — is **never ingested** into the Graph connection, and content that
gains such a classification *after* ingestion is **withdrawn** on the next
crawl. Every skip and withdrawal is written to an auditable reconciliation
report.

## Rule set — `config/exclusions.json`

```json
{
  "excludedFlags":            ["MNE", "MaterialNonpublicEvent", "MNPI"],
  "flagProperties":           ["classification", "complianceFlags", "eventType"],
  "restrictedLibraries":      ["Restricted", "M&A Deal Room"],
  "restrictedTeamsiteIds":    [],
  "excludeRestrictedTeamsites": true,
  "propertyRules":            [{ "name": "confidentiality", "equals": "mnpi" }]
}
```

| Key | Meaning |
| --- | --- |
| `excludedFlags` | Flag values that exclude an item. Matched case-insensitively, whitespace-trimmed. |
| `flagProperties` | Which Seismic content properties are inspected for those flags (single- and multi-value). |
| `restrictedLibraries` | Teamsite/library **names** whose entire content tree is excluded (case-insensitive). |
| `restrictedTeamsiteIds` | Same, by teamsite **id** (immune to renames). |
| `excludeRestrictedTeamsites` | Also exclude any teamsite Seismic itself marks `isRestricted`. Default `true`. |
| `propertyRules` | Arbitrary `name` = `equals` pairs for bespoke taxonomies. |

### Fail-closed loading (MNPI safety)

The No-MNE gate **fails closed**. `exclusions.json` must exist and define at
least one rule; a **missing, empty (0-byte), empty-object (`{}`), `null`,
malformed, or rule-less** file is a hard `ConfigException` at startup naming the
path — it is *never* silently treated as "nothing excluded" (that would ingest
MNPI-flagged content on a config slip). To run rule-less **on purpose**, set the
explicit sentinel in the file:

```json
{ "acknowledgeNoExclusions": true }
```

With the sentinel the connector starts rule-less; `validate-config` still
surfaces it as a warning, and `validate-config --strict` escalates a rule-less
posture to a hard **FAIL**.

## Evaluation order

For every content item, on every crawl, **before anything is downloaded**:

1. **Teamsite id** in `restrictedTeamsiteIds` → excluded (`restricted-teamsite-id`).
2. **Library name** in `restrictedLibraries`, or the teamsite is
   Seismic-restricted → excluded (`restricted-library` / `seismic-restricted-teamsite`).
   Wholly-excluded teamsites are not even listed; their previously ingested
   items are withdrawn.
3. **Flags**: any `flagProperties` property carrying any `excludedFlags` value
   → excluded (`mne-flag`).
4. **Property rules** → excluded (`property-rule`).

## Late-flag withdrawal

The pipeline tracks every ingested item (id, version, status) in the identity
store. When an item that is currently `ingested` evaluates as excluded, the
connector:

1. `DELETE`s the externalItem from the Graph connection,
2. flips the tracked status to `excluded` (so it is never re-ingested and the
   not-seen reaper ignores it),
3. records a `withdrawn` event in the reconciliation report.

The same applies at library scope: marking a whole teamsite restricted
withdraws everything ever ingested from it.

**Incremental crawls withdraw late flags too.** An incremental only re-lists
content with `modifiedAt` ≥ the last sync, so a flag applied *without*
bumping `modifiedAt` would never be re-listed. Every incremental therefore
ends with a **late-exclusion pass**: tracked `ingested` items the crawl did
not visit are re-checked against the current rules (a metadata-only re-list
per affected teamsite — nothing is downloaded) and withdrawn if now excluded.
Items missing from the source listing are deliberately left to the full
crawl's not-in-source reaper (an incremental sees a partial world and must
not over-withdraw).

**The single-item path enforces teamsite rules and fails closed.** Webhook
events and `ingest-item` resolve the item's teamsite and run
`IsTeamsiteExcluded` before anything is downloaded: a restricted teamsite
(by id, name, or Seismic `isRestricted`) blocks the ingest and withdraws a
previously ingested copy. If the teamsite cannot be resolved at all, the
ingest is refused (fail closed) rather than indexed with unverifiable rules.

## Reconciliation report (auditable skip list)

Each crawl writes `reconciliation_{CONNECTOR_ID}_{timestamp}.jsonl` into the
run's log directory: one JSON line per exclusion/withdrawal plus a trailing
summary object:

```json
{"action":"excluded","item_id":"c42","teamsite_id":"ts1","rule":"mne-flag","reason":"property 'classification' carries excluded flag 'MNE'","timestamp":"..."}
{"action":"withdrawn","item_id":"c17","teamsite_id":null,"rule":"restricted-library","reason":"teamsite 'M&A Deal Room' became restricted","timestamp":"..."}
{"action":"summary","excluded_total":2,"withdrawn_total":1,"by_rule":{"mne-flag":2,"restricted-library":1},"timestamp":"..."}
```

Summary counts also appear in the run summary and on the live dashboard
("Excluded (No-MNE)").

## Immutable decision ledger (tamper-evident audit)

With `DECISION_LEDGER=true`, every **exclusion** decision (and every
classification **ACL-restriction** and content-gate **quarantine** decision) is
also appended to an append-only, SHA-256 **hash-chained** ledger,
`logs/decision_ledger_{CONNECTOR_ID}.jsonl`. Each entry carries its
`seq`, the item id, decision, reason, the previous entry's hash and its own
hash, so any later edit, reorder, insertion or deletion breaks the chain and is
detected by the ledger's `Verify()` (tamper-*evident*; pair with off-box/WORM
shipping for a full guarantee). It is deliberately scoped to these low-volume
compliance decisions — it is **not** a per-ingest log.

The flag means the same thing on **every** command that runs the pipeline:
`full-deployment`, `ingest`, `ingest-object`, `ingest-item`, `retry-failed`,
`reconcile` and `reacl`.

**One chain, not one per run.** The ledger is a single file at the logs root
that each run **resumes**: `seq` keeps climbing and each run's first entry links
back to the previous run's last, so the chain proves continuity *between* runs
as well as within them. Two consequences that matter for audit:

* `LOG_RETENTION_DAYS` cannot delete it — it is outside the `logs/{run}/`
  directories the pruner removes, and the pruner additionally **keeps** (and
  names in a warning) any run directory still holding an old per-run ledger.
* Deleting a whole run's decisions is *detectable*, because the surrounding
  entries' `prev_hash` links no longer join up.

### Crash damage, and what the ledger will and will not do about it

The ledger is flushed per line, so a crash can leave the file torn. Two rules
govern what happens on the next resume, and they are worth stating precisely
because the failure they exist to prevent is **silent** evidence loss — records
gone from the file while `Verify()` still reports the chain CLEAN, which is the
one failure mode a tamper-evident log must never have.

* **Bytes are discarded only when they are a plausible interrupted write** — an
  *incomplete* JSON value, and nothing else. The writer serializes one object
  and flushes it, so every prefix of a partial write stops mid-value. Those
  bytes were never acknowledged to anyone, so dropping them loses nothing.
* **Bytes that are not an incomplete JSON value are kept.** That covers bytes
  invalid where they sit *and* bytes that form a **complete** JSON value the
  record contract rejects — no interrupted write can produce either, so
  something overwrote data that had already been flushed (or wrote foreign
  content into the file). They are evidence, they stay in the file, and
  `ReadFile()` **throws** rather than reading the file as clean.

  The second half of that used to be missing, and it cost a record: a single
  overwritten byte in the **last** record's key names (`"Seq"` → `"Xeq"`) leaves
  a complete, valid JSON object that simply is not a record. It was classified
  "junk, safely discardable", so the auditor dropped it while reporting
  `Valid=True` and `IsClean=True`, the next resume truncated it off disk, and
  its seq was re-issued to a different item.

Between those, the reader **resynchronises**: a parse failure inside a line does
not end the scan, it steps forward to the next record and keeps going. So a
destroyed record separator — a NUL from an allocated-but-unwritten block, a
stray byte, a half-written UTF-8 sequence — costs you nothing; every record
behind it is still recovered, still chains, and still verifies. Damage that
lands *inside* a record does destroy it. When a later record survives, that
shows up as a **seq gap** that `Verify()` reports. When it is the **last**
record there is nothing behind it and no gap can ever appear, so the loudness
comes from the other side instead: the bytes are kept and `ReadFile()` refuses
the file. The guarantee is not that nothing is ever lost; it is that nothing is
ever lost **quietly**.

**The single exception, stated plainly.** Damage confined to a record's
final byte — the closing brace — *and only when that byte is overwritten by
whitespace or deleted outright* leaves an incomplete JSON value which, after the
trailing-whitespace trimming the format performs anyway, is byte-for-byte what a
write that stopped one byte short leaves. Nothing in the file tells the two
apart, so those bytes are treated as the crash-tail they are indistinguishable
from: the record is dropped and the tail truncated, quietly.

Measured post-fix by an exhaustive sweep over a real 265-byte final record — all
256 byte values at all 265 offsets, plus delete, insert and truncate at every
position:

| Damage | Combinations | Recovered | Refused | Dropped quietly |
| --- | --- | --- | --- | --- |
| Replace | 67,840 (265 no-op) | 16,675 | 50,896 | **4** |
| Delete | 265 | 179 | 85 | **1** |
| Insert | 68,096 | 17,775 | 50,321 | **0** |
| Truncate | 265 | 265 healed as torn writes | 0 | **0** |

All five quiet drops are the same byte: the closing brace at offset 264,
overwritten by one of the four JSON whitespace bytes (`0x09`, `0x0a`, `0x0d`,
`0x20`) or deleted. Your off-box / WORM copy is what covers it.

An earlier release stated this limit as "2 of 265 offsets" and named the closing
quote of `Hash` as one of them. That was measured with a five-value replacement
alphabet and was wrong in both directions: the true pre-fix figure was 3 offsets
(228 of 67,840 combinations), and the third was a `0x5c` **backslash** landing
inside the `Hash` *value*, which opened a JSON escape, swallowed the closing
quote and disguised the damage as a torn write. The reader now additionally
requires a discardable residue to be a byte-for-byte **prefix of what the writer
could have emitted**, which an altered byte is not.

`ReadFile(path, out LedgerFileDamage damage)` reports physical damage separately
from chain validity, because the two are independent: a file can be mangled and
still verify perfectly. Feed `damage` to your integrity monitor — it names glued
lines, resynchronised regions and a damaged tail. `DecisionLedger` exposes the
same for the file a run resumed, as `ResumedDamage`.

**What the chain does not defend against.** It is tamper-*evident*, not
tamper-proof, and specifically it has never defended against **append** access:
anyone who can append to the file can compute the next hash and add a
well-formed, correctly chained record, and it will verify. That is the residual
that off-box / WORM shipping exists to cover. One wrinkle worth knowing: a
forged record can be glued onto the *end* of an existing line, adding no new
physical line, which defeats a naive line-count or tail-based monitor. Such
records are still accepted — refusing them is what destroyed real evidence in
earlier releases — but they are now reported as `GluedLines` damage, so monitor
that field rather than the line count.

Earlier releases wrote one ledger per run
(`logs/{run}/decision_ledger_{CONNECTOR_ID}_{timestamp}.jsonl`), each starting a
fresh chain from genesis. Those files are left untouched and are **not**
continued by the current ledger; the connector logs their paths at startup so
they can be archived alongside it. A single active connector process per
`CONNECTOR_ID` is assumed, as it already is for the sync cursor, checkpoints and
the dead-letter queue in the same directory.

## Related withdrawals (not exclusion rules, same machinery)

* **Expiry**: items whose expiry date passes are withdrawn automatically
  (`expired`) — on every crawl, not just full ones.
* **Unpublished / deleted**: withdrawn (`unpublished` / `not-in-source`).
* **ACL-unmappable** (with `SEISMIC_FALLBACK_ACL=skip`): withdrawn
  (`acl-unmappable`) rather than left readable.

## Drift sweep backstop

Crawl-time enforcement is event-driven; `reconcile [--repair]` is the
periodic backstop: it diffs the full inventory against the index and flags
`excluded-drift` (ingested content that now matches an exclusion rule —
including rules edited between crawls) plus reinstatement of content whose
flags were removed. See the README "Drift reconciliation" section.
