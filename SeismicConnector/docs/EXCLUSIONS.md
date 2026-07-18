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
classification **ACL-restriction** decision) is also appended to an
append-only, SHA-256 **hash-chained** ledger,
`decision_ledger_{CONNECTOR_ID}_{timestamp}.jsonl`. Each entry carries its
`seq`, the item id, decision, reason, the previous entry's hash and its own
hash, so any later edit, reorder, insertion or deletion breaks the chain and is
detected by the ledger's `Verify()` (tamper-*evident*; pair with off-box/WORM
shipping for a full guarantee). It is deliberately scoped to these low-volume
compliance decisions — it is **not** a per-ingest log.

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
