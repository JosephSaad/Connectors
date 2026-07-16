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

A missing `exclusions.json` means **no rules** — `validate-config` warns about
this loudly.

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
