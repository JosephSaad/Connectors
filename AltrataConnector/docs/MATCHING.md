# MATCHING — tiered entity resolution

Altrata person records are linked to internal CRM contacts
(`CRM_CONTACTS_PATH`: JSON or CSV with `id,email,name,employer[,role/title]`).
Every link carries **provenance** on the externalItem: `crmContactId`,
`crmMatchRule` and `crmMatchConfidence`, and the same rule is stored in the
crosswalk.

## Tier 1 — deterministic (always on)

| Rule | Provenance | Confidence |
|---|---|---|
| exact case-insensitive email | `email` | `1.00` |
| normalized name AND normalized employer equal | `name+employer` | `1.00` |

Normalization: lower-case, diacritics folded, punctuation dropped, corporate
suffixes (Inc, Ltd, GmbH, …) stripped from employers.

## Tier 2 — scored fuzzy (opt-in: `ENTITY_FUZZY_MATCHING=true`)

Runs only when both deterministic rules miss. Every CRM contact is scored:

```
score = 0.6 · jaccard(name tokens)
      + 0.3 · jaccard(employer tokens)
      + 0.1 · jaccard(role tokens)      # role/title hint, when both sides have one
```

* A candidate with **zero name overlap is never considered** — employer/role
  evidence alone can't create a link.
* `score >= ENTITY_MATCH_THRESHOLD` (default 0.85) → auto-link with
  `crmMatchRule = fuzzy` and `crmMatchConfidence = <score>`. The strict
  default means a fuzzy link needs a near-exact name PLUS corroborating
  employer and/or role evidence.
* `ENTITY_REVIEW_FLOOR <= score < threshold` (default floor 0.6) → **no
  link**; the best candidate is appended to the review queue
  `logs/match_review_{CONNECTOR_ID}.jsonl`:

  ```json
  {"TimestampUtc":"…","AltrataId":"P42","CandidateContactId":"C1",
   "Score":0.8,"NameScore":1.0,"EmployerScore":0.67,"RoleScore":0.0,
   "NameHash":"1f0c2e…","EmployerHash":"9ab41d…"}
  ```

  **PII**: the queue is a log file and follows the connector-wide
  ids/counts/hashes-only rule — it carries ids, scores and short SHA-256
  hashes of the *normalized* name/employer (dedup keys), never the raw
  personal values. An adjudicator dereferences both sides through the ids:
  the Altrata record via the feed and the CRM contact via the identity store.

  Review workflow: confirm a candidate by adding the pair to your CRM export
  (or fixing the email), then re-crawl — the deterministic tier picks it up.
* `score < floor` → silently unmatched (no queue noise).

## Knobs

| Env var | Default | Meaning |
|---|---|---|
| `ENTITY_FUZZY_MATCHING` | `false` | enable tier 2 |
| `ENTITY_MATCH_THRESHOLD` | `0.85` | auto-link floor, range (0, 1] |
| `ENTITY_REVIEW_FLOOR` | `0.6` | review-queue floor, must be < threshold |

Validation fails closed: an out-of-range threshold or a floor ≥ threshold is
a configuration error at startup.

## Notes

* The fuzzy tier never touches ACLs — entitlement is entirely separate
  (docs/ENTITLEMENT.md).
* The candidate pool is loaded once per crawl from the identity store
  (`role_normalized` column added in v2; existing SQLite/SQL Server stores
  migrate automatically).
* `ingest-item` (API enrichment) uses the same tiers and provenance.
