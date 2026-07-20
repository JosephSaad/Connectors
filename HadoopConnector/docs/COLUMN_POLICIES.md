# Per-column policies (`columnPolicies`)

Per-**column** drop/mask enforcement for BDH objects, configured per object in
`config/schema.json`.

## Why this exists

This connector's sensitivity model is per-**object** (`sensitivityDefault`), and
its authorisation model is coarse — record owner, one flat Entra group, or
everyoneExceptGuests (see [`ACL_POSTURE.md`](ACL_POSTURE.md)). Neither is
column-aware. Before this control there was no way to say "index this record but
not its compensation figure": a restricted column reached the index for
everybody the record's ACL admitted, and Copilot could ground an answer on it.

`columnPolicies` is the enforcement **mechanism** for that. The policy **data**
is hand-authored in `schema.json` today and is expected to come from Apache
Ranger column-masking policies or the Salesforce FLS manifest later. The
mechanism is identical whatever the source, which is why it exists ahead of it.

## Configuration

```jsonc
{
  "objectName": "Employee",
  "selectedFields": {
    "Name": "Title",
    "Compensation__c": "Compensation",
    "SSN__c": "Ssn",
    "ReviewNotes__c": "_bdh_ReviewNotes"
  },
  "columnPolicies": {
    "Compensation__c": "drop",
    "SSN__c": "mask",
    "ReviewNotes__c": "drop"
  }
}
```

| Action | Effect |
| --- | --- |
| `drop` | The column is **never emitted** — no Graph property, no content line, no title. Neither its name nor its value reaches the index. |
| `mask` | The Graph property name and the content key are **retained**; the value is replaced with `[RESTRICTED]`. The value is never read, so it never enters the item. |

Column names are matched case-insensitively, matching `BdhRecord.Get` — BDH
exports vary in column casing, and a policy that missed on casing would silently
leave the column indexed.

Absent or empty `columnPolicies` means no column is restricted, and conversion
is byte-identical to a config without the key.

### Why `mask` keeps the key

A masked column emits its marker even when the source value is empty. If it did
not, the item's shape would reveal whether a restricted field was populated —
which is itself a disclosure. Keeping the key also makes the restriction visible
to a reader of the item rather than looking like missing data.

## Enforcement points

`ItemConverter` assembles an item in **two independent passes** over
`selectedFields`:

1. `Convert` — emits Graph **properties** (fields *not* prefixed `_bdh_`).
2. `BuildContent` — appends `_bdh_`-routed fields to the searchable **content
   body**.

A column travels through exactly one of these, decided by the `_bdh_` value
prefix. **A policy gated in only one loop leaks**: gate only the property loop
and a `_bdh_`-routed restricted column still lands in the content body Copilot
grounds on; gate only the content loop and the property still carries the value.
Both loops are gated, and `ColumnPolicyTests` asserts on *both* surfaces for
*both* routings — never only on the one the field happens to be mapped to.

There is a **third** emission path: the content body's opening title line reads
the `Name` column directly, outside both loops. It is gated identically —
`drop` falls back to the record id, `mask` emits the marker.

## The identity column (`Id`) cannot be restricted

A policy on `Id` is **rejected at config load**. That column is not merely
*emitted* by the connector — it is what the connector is *built on*:

| Route | Where the value lands |
| --- | --- |
| `externalItem.id` | the id Graph indexes the record under (`BdhRecord.ItemId`) |
| the `Url` property | the deep link composed from it (`ItemConverter.BuildUrl`) |
| content title line | the fallback when `Name` is dropped or absent |
| inventory / deletion sync | the key reconciliation and delete-detection use |
| dead-letter / `retry-failed` | the key a failed record is re-located by |

`drop` would strip only the Graph property while the value stayed in the index
by the other routes — and `validate-config` would announce "1 dropped" for a
restriction that was never real, which is precisely the silent-failure mode this
mechanism exists to prevent. `mask` is no better: it would collide every row of
the object onto one id. Genuine enforcement is not available either, because an
item without an id is not indexable, reconcilable or retryable at all.

So the rejection is the honest option, and the error message says which routes
survive and what to do instead:

- to keep the id out of the **property map**, remove it from `selectedFields`
  (no policy is needed — an unselected column is never read);
- to restrict **who can see** the record, narrow its `aclMode`
  ([`ACL_POSTURE.md`](ACL_POSTURE.md));
- to stop indexing the record **at all**, filter it out in `filters.json`.

Matching is case-insensitive (`Id`, `id`, `ID`), because `BdhRecord.RawId` reads
the column that way — otherwise the rejection would be side-steppable by
changing one letter.

## Validation

`SchemaConfig.Validate` rejects at config load:

- an **unknown action** (anything but `drop` / `mask`);
- a policy naming a **column not in that object's `selectedFields`** — such a
  policy restricts nothing while the column it was meant to cover stays fully
  indexed, which is the worst failure mode for a control like this;
- a policy on the **identity column `Id`**, which cannot be honoured (above);
- two policy keys **differing only in case**, which name the same column under
  case-insensitive lookup and would make the winner dictionary-order luck;
- a policy on a column whose **name is the Graph property another column maps
  to** — see below;
- a policy on a column whose **name is one of the seven properties the connector
  emits on every item by itself** — see below.

`SchemaConfig.Validate` also rejects, independently of any policy:

- a `selectedFields` entry with an **empty column name** or an **empty Graph
  property name** (which is what a JSON `null` value is read as);
- **two columns mapped to the same Graph property** — see below;
- a `selectedFields` entry **mapped onto one of those seven always-emitted
  properties** — see below.

### A policed column named after another column's Graph property

```jsonc
"selectedFields": { "Id": "RecordId", "RecordId": "Other" },
"columnPolicies": { "RecordId": "drop" }
```

This policy is applied correctly: column `RecordId` is not emitted. But every
report of it names the *column*, so preflight prints `dropped=[RecordId]` while
a fully populated Graph property called `RecordId` carries on reaching the index
from column `Id`. Nobody reading that line can tell which of the two the
restriction covered — and the likeliest reason to write it is meaning the
property rather than the column, in which case the restriction the operator
believes is in force is not.

So it is **rejected at load**, for the same reason as the two rejections above:
the failure mode of a restriction control is the one where it reports success
and restricts nothing visible. Fix it by renaming the Graph property the other
column maps to, or by policing that other column if it was the real target.

Two cases are deliberately **not** rejected:

- a column mapped to a property of **its own name** (`"RecordId": "RecordId"`) —
  the ordinary case, where the report names one thing only;
- a collision whose colliding property is **itself dropped**, so it never
  reaches the index and the report is accurate. `mask` does not qualify: a
  masked column still emits a property of that name.

Matching is case-insensitive on both sides, like every other lookup here.

### Two columns mapped to the same Graph property

```jsonc
"selectedFields": { "Id": "Id", "Salary": "Comp", "Bonus": "Comp" },
"columnPolicies": { "Salary": "mask" }
```

Only one of `Salary` and `Bonus` can occupy `properties["Comp"]` on the item, and
the winner is whichever the dictionary enumerates last. Above, the mask was
written into `Comp` and then **overwritten** by Bonus's real value, while
preflight went on printing `masked=[Salary]`. The masked value did not leak — but
the report claimed a restriction the item did not deliver, which is the same
failure family as the three rejections above.

So the **mapping** is rejected at load: no two `selectedFields` columns of an
object may map to the same Graph property, whether or not a policy is involved.
That also closes the cases nobody would think to test — the drop that gets
overwritten, three columns onto one property, a casing-only difference, and the
plain silent data loss with no policy at all. Give each column its own property
name; property names match case-insensitively and are compared trimmed.

`_bdh_` values are exempt: they are not property names but markers routing the
column into the **content body** under its own column name, so two columns
carrying the same placeholder produce two distinct lines and overwrite nothing.

### The seven always-emitted properties

Two code paths write Graph properties on every item **outside**
`selectedFields`, seven names between them:

| emitter | properties | when |
| --- | --- | --- |
| `ItemConverter.Convert` | `ObjectName`, `Url`, `IconUrl`, `SourceSystem`, `DataAsOf` | always (`IconUrl` / `DataAsOf` only when non-empty) |
| `SensitivityClassifier.Classify` | `SensitivityLabel`, `DetectedCategories` | whenever `CLASSIFICATION=true` |

The validation reads **`AlwaysEmittedProperties.Names`** — the aggregate each
emitter contributes its own list to, never a copy. That indirection is the fix
for a real defect: the check used to read `ItemConverter.StandardPropertyNames`
directly, which was the *correct* symbol for that emitter and still missed the
classifier's two entirely. `Classify` runs *after* `Convert`, so a column mapped
onto `SensitivityLabel` was converted, then silently overwritten with the label
on every record, with preflight green throughout.

Conditional emission is no excuse for leaving a name out of the set: whether
`IconUrl`, `DataAsOf` or the classifier's two are populated depends on the
record or the deployment, so a config colliding with one is broken in *some*
deployments and not others — worse than being broken in all of them.

`selectedFields` cannot safely name any of the seven, in **either** direction,
and both are rejected at load:

```jsonc
// (a) a policy named after a standard property
"selectedFields": { "Url": "UrlCol", "Email": "Email" },
"columnPolicies": { "Url": "drop" }
```

The drop is applied to the *column* — but preflight prints `dropped=[Url]` while
a populated, indexed `Url` property (the deep link) rides on every item. Exactly
the same reading problem as the previous section, with the colliding property
coming from the converter rather than from another `selectedFields` entry. The
restricted column's **value** does not leak; the report cannot be read as true.

```jsonc
// (b) a column mapped ONTO a standard property
"selectedFields": { "Comp": "Url", "Email": "Email" }
```

Loop 1 of `Convert` runs *after* the standard properties are set, so the mapped
column **overwrites** one. With no `Comp` column in the record this replaced the
deep link with null and every item lost its `Url` entirely; with
`"columnPolicies": {"Comp": "mask"}` every item's `Url` became `[RESTRICTED]`
instead. Both were preflight-green. Same family as *two columns mapped to the
same Graph property* — only one value can occupy the property and the loser is
discarded silently — except here the loser is a structural property nobody wrote
in `selectedFields`, so nothing in the config hints at what was lost.

Matching is case-insensitive and trimmed, like every other property comparison
here. `_bdh_` values are exempt for the usual reason: they are content-body
markers, not property names.

### `null` values

`"selectedFields": { "Comp__c": null }` is legal JSON and is read as an empty
Graph property name, which is rejected at load naming the column. The rule for
`null` across the whole config file is one rule, documented in
[`CONFIG_NULL_SEMANTICS.md`](CONFIG_NULL_SEMANTICS.md).

`validate-config` reports the posture on every run as a `POSTURE:` line —
per-object dropped/masked counts and column names, and an explicit statement
when **no** object restricts any column. It is informational and never gates
`--strict`: a configured restriction is posture, not a finding.

One case is a real `WARNING`: masking a column whose Graph property is declared
non-`String` in `graph-schema.json`. The marker is a string, so Graph rejects
every such item and the records are not indexed at all — use `drop`, or
redeclare the property as `String`.

Column policies also interact with the `graph-schema.json` cross-check
(`validate-config` derives the produced property set from the converter
itself): a **`drop`**ped column emits nothing, so its mapped Graph property
does not need declaring — if it stays declared, that is reported as a harmless
dead-schema notice. A **`mask`**ed column still emits its property (carrying
the marker), so its declaration is still **required**; removing it is a
preflight ERROR, not a way to retire the column.

## Dead-letter payloads are already covered

`DEADLETTER_PAYLOAD_MODE=full` stores the failed request body verbatim, which
raises the obvious question of whether a dropped/masked column re-appears there.
It does not, structurally: every dead-letter request body is
`ExternalItem.ToJson()` — the **converted** item, taken after both enforcement
loops have run (`Graph/Ingest.cs`, `SendOneAsync`). The raw `BdhRecord` is never
handed to the dead-letter writer by any call site, so a dropped column is absent
and a masked one carries `[RESTRICTED]` before the redaction mode is even
consulted. No separate enforcement is needed or wanted — a second, independent
copy of the gate is exactly the kind of thing that drifts out of lock-step.

`RestrictionBypassProbeTests.DeadLetterPayloadInFullMode_CarriesNoRestrictedColumnValue`
pins this down under `full` mode, so a future change that dead-letters the raw
record fails there rather than in production. Note that item **ids** are kept in
both modes by design — `retry-failed` re-locates the record by
`item_id` + `object_type` — which is another reason a policy on `Id` cannot be
honoured (above).

## Deliberately out of scope

**No action rewrites the item ACL.** An action like `restrictToGroup` would
interact with the classification ACL-rewrite ordering in
`IngestPipeline.ApplyClassification`, and is deferred until the Ranger work
settles the ACL model. Both actions here change only what is *emitted* for a
column; neither changes who the item is granted to.

A column policy is also not a substitute for narrowing `aclMode` — it reduces
*what* is over-shared, not *to whom*. See [`ACL_POSTURE.md`](ACL_POSTURE.md).

Related: [`ACL_POSTURE.md`](ACL_POSTURE.md),
[`CLASSIFICATION.md`](CLASSIFICATION.md), [`THREAT_MODEL.md`](THREAT_MODEL.md).
