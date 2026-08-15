# Connector-applied data classification (advisory tag)

Optional, off by default. When enabled, every ingested item is stamped with a
single classification taxonomy so Copilot/search admins can refine, audit and
(optionally) enforce on it — and, optionally, a per-crawl manifest is written
for catalog/DLP ingestion. Implementation: `Content/ContentClassifier.cs`
(pattern scan), `Item/SensitivityClassifier.cs` (label derivation),
`Item/ClassificationManifest.cs` (export), wired in `Graph/Ingest.cs`.

> **This is an advisory, connector-applied TAG — not a Purview label.**
> `advisorySensitivity` is a value the connector computes and stamps as a Graph
> refiner property. It is **not** a Microsoft Purview-enforced sensitivity
> label: on its own it does **not** encrypt content and does **not** gate
> access. To make the tag actually restrict access, opt into
> `CLASSIFICATION_ENFORCE_ACL` (below). The wire property name stays
> `advisorySensitivity` on the wire; the internal C# enum keeps the name `advisorySensitivity`.

> **Off = untouched.** With `CLASSIFICATION` unset/false the classifier is
> never constructed, no properties are added, and behaviour is byte-identical
> to a build without the feature.

## Knobs

| Env var | Default | Effect |
|---|---|---|
| `CLASSIFICATION` | `false` | `true` → every converted item gets the advisory `advisorySensitivity` + `DetectedCategories` properties, derived from a content scan + the per-object default. |
| `CLASSIFICATION_MANIFEST` | `false` | `true` (with `CLASSIFICATION=true`) → additionally write a per-crawl classification JSONL under `logs/` (advisory catalog/DLP export; **no** live Purview call). Without `CLASSIFICATION` it has no effect. |
| `CLASSIFICATION_ENFORCE_ACL` | `false` | `true` (with a group configured) → **enforce** the tag: top-tier (`Restricted`) items have their ACL narrowed to the configured group so the tag gates retrieval. Non-`Restricted` items are untouched. Off = advisory only (non-breaking). |
| `CLASSIFICATION_RESTRICTED_GROUP_ID` | _(unset)_ | The Entra group object id `Restricted` items are limited to when enforcement is on. |

## Enforcement (optional)

With `CLASSIFICATION_ENFORCE_ACL=true` **and** `CLASSIFICATION_RESTRICTED_GROUP_ID`
set, any item classified `Restricted` (PII/PCI/Secret detected, or a
`sensitivityDefault` of `Restricted`) has its resolved ACL **replaced** with a
single grant to that group before it is PUT — so the advisory tag becomes a real
access boundary. Items below `Restricted` keep their normally-resolved ACL.

Each enforced restriction is written to the **immutable decision ledger**
(`logs/decisions_<CONNECTOR_ID>.jsonl`, `ACL_RESTRICTION`) so the WHO-can-see-WHAT
change is auditable and tamper-evident. `validate-config` fails when
`CLASSIFICATION_ENFORCE_ACL=true` without a group id, and warns when it is set
without `CLASSIFICATION=true` (nothing would be classified, so nothing narrowed).

## The taxonomy

`advisorySensitivity` is a single ordered scale:

```
Public (0) < Internal (1) < Confidential (2) < Restricted (3)
```

Derivation, highest wins:

1. **Detected PII / PCI / Secret** (content scan, below) ⇒ `Restricted`.
2. **Per-object baseline** — `sensitivityDefault` in `config/schema.json`
   (`Public` | `Internal` | `Confidential` | `Restricted`, case-insensitive).
   Empty/unknown ⇒ `Internal`.

The per-object default is a **floor**: detections can only raise the label
(to `Restricted`), never lower it. E.g. `Contact` ships with
`sensitivityDefault: "Confidential"` — a contact with no detections is
`Confidential`; one whose text contains a card number that passes Luhn becomes
`Restricted`.

`DetectedCategories` is the (possibly empty) sorted set of category names that
matched: out of the box `PII`, `PCI`, `Secret`.

## The pattern set (`config/classification.json`)

Dependency-free (System.Text.RegularExpressions only, no network). Each
category has named regex patterns matched case-insensitively against the
item's scannable text — the content body plus every string / string-array
property value (the two taxonomy properties themselves are excluded):

```jsonc
{
  "categories": [
    {
      "name": "PII",
      "patterns": [
        { "name": "email",  "regex": "[A-Za-z0-9._%+-]+@..." },
        { "name": "phone",  "regex": "..." },
        { "name": "usSsn",  "regex": "\\b\\d{3}-\\d{2}-\\d{4}\\b" },
        { "name": "ukNino", "regex": "..." }
      ]
    },
    {
      "name": "PCI",
      "luhn": true,
      "patterns": [ { "name": "cardNumber", "regex": "\\b(?:\\d[ -]?){13,19}\\b" } ]
    },
    {
      "name": "Secret",
      "patterns": [
        { "name": "awsAccessKey",    "regex": "\\bAKIA[0-9A-Z]{16}\\b" },
        { "name": "bearerToken",     "regex": "..." },
        { "name": "privateKeyBlock", "regex": "-----BEGIN ... PRIVATE KEY-----" }
      ]
    }
  ]
}
```

- **`luhn: true`** — a regex hit is only a *candidate*: the digit run
  (spaces/dashes allowed) must additionally pass the **Luhn checksum** and be
  13–19 digits long. This is why PCI does not false-positive on ordinary long
  numbers (record ids, phone numbers, invoice numbers).
- Edit patterns freely — categories and patterns are pure config; adding a
  category (e.g. `PHI`) requires no code change. Note that only PII/PCI/Secret
  force `Restricted`; a custom category appears in `DetectedCategories` but
  does not raise the label.
- An **invalid regex is skipped** (the category keeps its valid patterns);
  a category with no valid patterns is dropped. A bad *pattern* can degrade the
  scan but never crashes the connector.
- A structurally **wrong-typed** value — `"categories"` as an object or a
  string, a `null` or non-object entry in `categories` or `patterns`, a
  non-string `name`/`regex`, a non-object document — is a **load error**
  (`InvalidDataException`) naming this file and the JSON path of the offending
  value. It is reported by `validate-config` as
  `classification.json invalid: <message>`.
- A JSON **`null` is read as that key's empty value**, the same single rule the
  rest of the config follows ([`CONFIG_NULL_SEMANTICS.md`](CONFIG_NULL_SEMANTICS.md)):
  `"categories": null` and `"patterns": null` load as *empty*, not as an error.
- Because empty then means *nothing is ever detected*, `validate-config` raises
  a **WARNING** when `CLASSIFICATION=true` and the file yields no usable
  category — classification would otherwise be on in name only.
- `classification.json` is in the preflight-validated set alongside
  `schema.json`, `graph-schema.json` and `filters.json`: no config in any of
  those four files can pass `validate-config --strict` green and then fail when
  the crawl loads it.

> Caveat, stated plainly: preflight is what closes the gap. If an operator skips
> `validate-config` entirely, a structurally invalid `classification.json` still
> ends the run at `IngestPipeline` construction — now with an
> `InvalidDataException` naming the file and the JSON path, but still via the
> CLI's generic unhandled-exception backstop, which prints a stack. Routing this
> file through the `Runtime.LoadConfigFile` clean-exit path (as `schema.json` and
> `filters.json` are) has not been done.

## Hardening (`Content/ContentClassifier.cs`)

The patterns run against attacker-influenced text (record fields), so the
scan is bounded on both axes:

- **`MatchTimeout` = 2 s per pattern evaluation.** Every `Regex` is compiled
  with a hard match timeout; a catastrophic-backtracking pattern/input pair
  logs a warning (`Classifier pattern for category 'X' timed out ... treated
  as no-match`) and counts as **no match** instead of hanging the crawl. This
  applies to both plain matching and Luhn-candidate enumeration.
- **`MaxScanChars` = 1 MiB per item.** Longer text is truncated for the scan.
- One hit per category is enough — matching short-circuits per category.
- `Detect()` never throws.

## Manifest (`CLASSIFICATION_MANIFEST=true`)

A per-crawl advisory JSONL export for catalog/DLP ingestion — file-based like
the reconciliation reports; **no live Purview API call**, and the label it
records is the connector-applied advisory tag, not a Purview label. Written on
crawl completion (`Flush()`), thread-safe against concurrent object/batch workers.

File: `logs/classification_{CONNECTOR_ID}_{yyyyMMdd_HHmmss}.jsonl`

```json
{"item_id":"0035e00000abcde","object_type":"Contact","sensitivity_label":"Restricted","categories":["PII"]}
...
{"kind":"summary","connector":"BdhHadoopMart","total":1234,"counts":{"Confidential":900,"Internal":300,"Restricted":34},"timestamp":"2026-07-17T09:30:00Z"}
```

- One line per classified item: `item_id` (the Salesforce record id),
  `object_type`, `sensitivity_label`, `categories` (array, may be empty).
- The final line is a `kind:"summary"` object with the total and per-label
  counts.
- Write failures are logged and swallowed — the manifest can never fail a
  crawl.

## Graph schema & metrics

`config/graph-schema.json` declares both taxonomy properties as queryable,
retrievable **refiners**:

```json
{ "name": "advisorySensitivity",   "type": "String",           "isQueryable": true, "isRetrievable": true, "isRefinable": true },
{ "name": "DetectedCategories", "type": "StringCollection", "isQueryable": true, "isRetrievable": true, "isRefinable": true }
```

Keep them in the schema even when classification is off (absent properties on
items are fine; a schema change requires re-provisioning).

Both names are **reserved**: `Classify` writes them on every item it sees, after
`ItemConverter.Convert` has run, so a `selectedFields` entry mapped onto either
would be overwritten on every record and a `columnPolicies` entry named after
either would report a restriction the item does not deliver. `SchemaConfig`
rejects both directions at load, whatever `CLASSIFICATION` is currently set to —
see [the always-emitted properties](COLUMN_POLICIES.md#the-seven-always-emitted-properties).

`/metrics` (labelled families appear once something is counted):

| Metric | Meaning |
|---|---|
| `hadoop_connector_items_classified_total{label=...}` | items classified, by resulting label |
| `hadoop_connector_sensitive_detections_total{category=...}` | detections, by category |

A sudden jump in `{category="PCI"}` on a source that should not contain card
data is worth an alert — it usually means either a real data-handling problem
upstream or a pattern regression after an edit to `classification.json`.
