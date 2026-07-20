# Field-Level Security (FLS) — WP-SF-2

## What this fixes

The connector reproduces Salesforce's **record-level** sharing model faithfully
(org-wide defaults, role hierarchy, sharing rules, manual shares, territories —
see `docs/ACL_PERMISSION_DFD.md`). Until WP-SF-2 it evaluated **field-level**
security not at all: FLS existed only as a hand-maintained per-object
`flsFields` array in `config/schema.json`.

A user permitted to see a record therefore saw **every indexed field on it**,
including fields Salesforce itself would hide — compensation, margin, and
similar. That is a live over-sharing exposure, and it propagates: the Hadoop BDH
connector mirrors the same Salesforce records under a coarser ACL model and
consumes the manifest this work package emits.

## The dual-loop hazard

`SalesforceObjectHandler.BuildItemPropertiesAndContent`
(`src/SalesforceCopilotConnector/Item/Converter.cs`) walks each record **twice**:

1. the **property loop** writes Graph properties (via `AddSchemaPropertyForField`);
2. the **content loop** appends every field that did *not* become a property into
   the searchable body as `"key: value"`.

**A restriction applied to only one loop leaks.** The value disappears from the
Graph property and survives verbatim in the body text Copilot grounds on — or
vice versa. That is precisely what the old `flsFields` precedent did: it ran
`props[flsField] = null` and never touched the content loop.

Both loops are now gated, plus the nested-relationship sub-field paths in each.
`tests/.../TestAclEngine/FieldLevelSecurityTests.cs` asserts a restricted field
appears in **neither** the properties **nor** the content body; disabling either
gate individually fails that test.

> **WP-SF-3 correction.** From WP-SF-2 until WP-SF-3 this section overstated the
> guarantee. Both loops *were* gated, but not equivalently: the nested
> "`Parent.Child`" gate in the **content** loop passed `null` for the property
> name, while its counterpart in the property loop passed the real mapped name.
> Since `IsFlsRestricted` matches **either** the Salesforce field key **or** the
> Graph property name, a drop on a nested sub-field expressed in *Graph-property*
> spelling gated the property and then leaked the value into the body as
> `"Parent.Child: <value>"`. Both gates now pass `MappedPropertyName(...)`.
>
> Two further gaps closed at the same time, both regression-tested in
> `tests/.../TestAclEngine/FlsLeakRegressionTests.cs`:
>
> * the two `__System.User.*.Id` properties are written **outside both loops** and
>   carried no gate at all. Harmless on the ingest path (the deployed Graph schema
>   never contains those names) but live on the direct-converter path, where
>   `Converter.BuildSchemaProperties` unions them in;
> * compound fields — **not** fixed at that point. WP-SF-4 has since closed them
>   entirely, by not querying compounds at all. See "Compound fields — CLOSED"
>   below; the section that used to sit there described the residual as merely a
>   compound that could not be dropped, when in truth the component was never
>   evaluated at all.
>
> **WP-SF-4 correction.** The `__System.*` bullet above was also only half true.
> `__System.User.CreatedBy.Id` gated the system column but did **not** gate
> `CreatedByUrl`, which embeds the same user Id — so a drop written in that
> spelling shipped the value anyway. `IsFlsRestricted` now matches the field's
> whole **alias closure** (its Salesforce name, its mapped Graph property, its
> metadata property, and its `__System.*` property), computed from declared maps
> only. The three spellings are now genuinely interchangeable, in both directions,
> for `CreatedById` and `LastModifiedById` alike — and for every other field with
> more than one spelling.

## Compound fields — CLOSED (WP-SF-4)

> **THIS SECTION REPLACES AN EARLIER ONE THAT DESCRIBED A LIVE LEAK AS AN ACCEPTED
> LIMITATION, AND UNDERSTATED IT.** If you configured around the old text, read on:
> the workaround it recommended is no longer needed, `config/schema.json` has changed
> shape, and the leak it described was worse than it said. Nothing about this is
> silent.

A Salesforce **compound** field is one API field whose value Salesforce assembles
from several ordinary component fields:

```
BillingAddress = { street, city, state, postalCode, country }
```

`FieldPermissions` governs the **components** (`BillingStreet`, `BillingCity`, …).
**The compound itself carries no rows of its own** — which means a compound value can
never be field-level-security checked at all.

### What the old text said, and what was actually happening

The previous text said a restricted **component** did not cause the **compound** to be
dropped, and pointed operators at the manual `flsFields` list. That understated the
defect in two ways, both proven on the **shipped** `config/schema.json`:

1. **The component was never evaluated — at all.** `FlsPolicy.ComputeDrops` is fed
   `handler.SelectedFields.Keys`, which on the shipped schema contained only the
   compound (`BillingAddress`) and never the component (`BillingStreet`). So the
   policy was never even *offered* the field whose permissions it needed to read. With
   every Billing component readable by **nobody**, in `FLS_MODE=strict`, the computed
   drop set was `[Parent.Name]` — **zero address drops**.
2. **The values then landed in the SEARCHABLE CONTENT BODY**, verbatim:
   `BillingAddress.street: <secret>, BillingAddress.city: <secret>, …`. All five
   shipped compounds (Account `BillingAddress` + `ShippingAddress`, Contact
   `MailingAddress` + `OtherAddress`, Lead `Address`) map to `_sf_` placeholders, so
   none is a Graph property and **all five** took the content-body route on the
   shipped ingest path.

### The fix: compounds are never queried

The connector no longer selects compound fields. `config/schema.json` selects the
**components individually** and declares how to reassemble them:

```jsonc
{
  "objectName": "Account",
  "selectedFields": {
    "BillingStreet":     "_sf_BillingStreet",
    "BillingCity":       "_sf_BillingCity",
    "BillingState":      "_sf_BillingState",
    "BillingPostalCode": "_sf_BillingPostalCode",
    "BillingCountry":    "_sf_BillingCountry"
  },
  "addressFields": {
    "BillingAddress": {
      "property": "_sf_BillingAddress",
      "components": {
        "street": "BillingStreet", "city": "BillingCity", "state": "BillingState",
        "postalCode": "BillingPostalCode", "country": "BillingCountry"
      }
    }
  }
}
```

Every selected field now carries **real `FieldPermissions` evidence** and is gated by
**literal name**. There is no inference anywhere — which is exactly what made the
deleted name-guessing `CompoundFields` feature unsound (see below). A restricted
component simply does not appear in the assembled address.

The assembled text is byte-identical to what the compound used to serialise to when
nothing is restricted (`"1 Market St, San Francisco, CA - 94105, US"`), and it is an
improvement on the shipped ingest path, which previously emitted the address as a
flattened `BillingAddress.street: …, BillingAddress.city: …` blob.

### Anything still compound-shaped fails CLOSED

Config alone would fix only the five shipped compounds. The structural rule closes the
class:

> **A compound value is not indexed by any route — not as a Graph property, not
> flattened into the searchable body — because it can carry no FLS evidence.**

`SalesforceObjectHandler.IsUnindexableCompound` detects the shape (Salesforce writes
compound slots in camelCase — `street`, `city`, `postalCode`, `latitude` — while
relationship sub-objects use API field names) and both assembly loops skip it,
logging a warning that names the field and points at `addressFields`. Declared
relationship sub-objects (`CreatedBy`, `Parent`, …) are exempt: those are real objects
whose sub-fields have their own permissions and their own gates.

This covers shapes that were never individually reported — custom address compounds
(`Custom_Address__c`), geolocation compounds, Person-Account address compounds
(`PersonMailingAddress`), an operator config that still lists a compound, and any
object added to the config later. Suppression is gated on `FLS_ENFORCEMENT`, so the
documented escape hatch still reaches the old behaviour.

### Why the earlier name-inferring fix was removed rather than patched

The removed implementation inferred a compound's components **from field names**
(`BillingAddress` → `Billing` + `Street`/`City`/…; `Name` →
`Salutation`/`FirstName`/`MiddleName`/`LastName`/`Suffix`). Adversarial verification
proved two consequences:

* **Catastrophic data loss on a shipped config.** On a **Person-Accounts** org,
  `Account.Salutation` / `FirstName` / `LastName` carry real `FieldPermissions` rows.
  Restricting any one of them therefore dropped `Account.Name` for **every account in
  the org** — including business accounts, whose `Name` is unrelated plain text. The
  account name vanished from the Graph properties **and** the content body.
* **It never delivered what it claimed.** The derivation early-returned on any name
  containing `.`, so it never applied to relationship sub-fields
  (`Contact.MailingStreet`) — precisely the case the feature was meant to cover.

The current fix has no inference in it, so there is no mechanism by which that failure
could return. `FlsCompoundNotIndexableTests` asserts `Account.Name` survives a
`Salutation` restriction, on both assembly routings.

### The operator's lever

Naming a field in `flsFields` still works, and now reaches **into** an address: listing
`BillingPostalCode` withholds the postal code and keeps the rest of the address
assembled. Listing the compound itself is no longer necessary — and no longer does
anything, because the compound is not indexed in the first place.

`flsFields` matching is **case-insensitive**: an entry that differs only in case takes
effect and logs a warning naming the declared spelling. Previously the comparison was
ordinal, so a casing typo (`billingaddress`) was a **silent no-op** that leaked the
field in full while looking, in config, exactly like a drop. An entry matching nothing
at all also warns.

An `flsFields` entry only nulls a Graph property when it **is** one, resolved against
the deployed schema. Previously the key was written unconditionally, so listing a
non-property field posted an **undeclared** null property to Graph — which applies no
schema-conformance filter before push.

### Test coverage

`FlsCompoundNotIndexableTests` and `ShippedSchemaCompoundTests` in
`tests/.../TestAclEngine/FlsLeakRegressionTests.cs` sweep **all 32 subsets** of the
five address components across **both** assembly routings, assert every undeclared
compound shape is indexed by no route, assert the assembled text matches the old
serialisation exactly when nothing is restricted, and re-run the original probe
against the real `config/schema.json`.
## The decision rule

An indexed Graph external item carries **one** property set and **one** content
body, shared by every principal on that item's ACL. Field visibility is therefore
**per-item, not per-user**: there is no mechanism to show a property to one ACL
principal and hide it from another.

Consequently:

> **A field readable by SOME but not ALL principals on an item's ACL must be
> DROPPED** — the least-privilege union.

This is `FLS_MODE=strict`, the **default**.

`FLS_MODE=permissive` is a documented, deliberately weaker escape hatch: it drops
only fields that **no** principal on the ACL can read. Stated plainly:

> **Permissive mode can expose a field to a principal Salesforce would deny.**

Use it only when strict over-drops badly enough to break grounding, and treat it
as an accepted risk rather than a default.

Two facts keep strict from degenerating into "drop everything":

1. Salesforce's `FieldPermissions` table only holds rows for fields whose FLS is
   **configurable**. A field with no rows at all (`Id`, `Name`, …) is not governed
   by FLS and is never dropped on FLS grounds.
2. "Principals in scope" is not every permission set in the org — it is the set of
   permission-set parents (profiles and permission sets) that are actually
   **assigned to an active user** *and* grant read on the object. Those are exactly
   the principals whose FLS can matter for that object's items.

`FlsPolicy.ComputeDrops` also accepts an explicit `principalScope`, so a caller
that can enumerate the principals on a *specific* item's ACL can narrow the union
to that item. When no scope is supplied the object-wide scope is used, which
over-drops rather than under-drops.

## Cross-object relationship fields

A dotted `selectedFields` key indexes a field of a **different** object:
`"Contact.Phone": "_sf_ContactPhone"` on Case puts a contact's phone number into
the Case item. Its field-level security lives in **Contact's** `FieldPermissions`,
not Case's — but `ComputeDrops` matched candidates against `GovernedFields`, which
only ever holds **bare field names from the object being crawled**. Every dotted
key therefore missed, silently, and was never FLS-evaluated at all. The shipped
`config/schema.json` has 14 of them, including `Case → Contact.Phone`.

Resolution is **operator-declared**, never inferred, via a per-object
`relationshipObjects` map in `config/schema.json`:

```jsonc
{
  "objectName": "Case",
  "selectedFields": { "Contact.Phone": "_sf_ContactPhone", "Parent.CaseNumber": "_sf_ParentCaseNumber" },
  "relationshipObjects": { "Contact": "Contact", "Parent": "Case" }
}
```

Guessing the target from the relationship name would be easy and wrong: a wrong
guess evaluates the field against the **wrong object's** permissions and
under-drops, which is the exact failure this work package exists to close.
`Parent` alone is enough to make the point — it is `Case` on Case and `Account` on
Account. An **undeclared** relationship is not resolved and therefore fails
closed. Declared targets join the permission fetch automatically; since every
target in the shipped config is itself a configured object, this costs no extra
queries.

| `FLS_RELATIONSHIP_FIELDS` | Behaviour |
| --- | --- |
| `evaluate` (**default**) | Resolve via `relationshipObjects` and evaluate the sub-field against that object's permissions. Unresolvable or unfetched ⇒ **dropped**. |
| `drop` | Drop every dotted field unconditionally. Maximally conservative. |
| `ignore` | Do not evaluate dotted fields at all — the pre-WP-SF-3 behaviour. **Re-opens a known over-sharing gap**; escape hatch only. |

Anything unrecognised means `evaluate`, so a typo fails towards the safe setting.

Under the describe fallback dotted keys are handled by the fallback rule instead:
describe lists `ContactId`, never `Contact.Phone`, so they are dropped — which is
both the pre-existing behaviour and the conservative one.

## Data sources

All reads go through the existing `SalesforceClient` — no new HTTP path, and
therefore the existing token refresh, pagination and error handling.

| Purpose | Query |
| --- | --- |
| Field permissions | `SELECT Field, SobjectType, PermissionsRead, ParentId FROM FieldPermissions WHERE SobjectType IN (…)` |
| Principal scope | `SELECT PermissionSetId FROM PermissionSetAssignment WHERE PermissionSetId IN (SELECT ParentId FROM ObjectPermissions WHERE SObjectType = '…' AND PermissionsRead = true) AND Assignee.IsActive = true` |
| Fallback | `SalesforceClient.DescribeSObjectAsync` |

The describe fallback exists because some orgs deny the integration user access
to `FieldPermissions`. Describe reflects the **running user's** FLS, so a field
Salesforce hides from the integration user simply does not appear in the payload
— absence is itself the signal, and any candidate field missing from describe is
dropped.

## Failure posture

Fail **closed** in-band:

* scope query fails → empty scope → strict drops every governed field on that object;
* `FieldPermissions` unreadable → describe fallback;
* describe also unreadable → nothing is provably readable → every candidate field dropped;
* relationship target undeclared or unfetched → the dotted field is dropped.

### The one case that cannot fail closed: zero-row `FieldPermissions`

A `FieldPermissions` query that succeeds and returns **no rows** is, on its face,
indistinguishable from "this org governs nothing": `GovernedFields` is empty,
nothing matches, nothing is dropped, and every field ships. That is a silent
fail-**open**, and unlike the cases above there is no exception to catch — it is a
200 with an empty result set.

It cannot simply be treated as "drop everything", because an org genuinely *can*
have no configurable FLS on the objects being crawled, and false-dropping every
field of every object is its own outage. So the response is a **signal, not a
drop**:

* `FlsObjectPermissions.FieldPermissionRowsSeen` records whether the query
  returned any rows **at all** — deliberately a query-level fact, not per-object,
  because an object with no rows in an org that has plenty is genuinely
  ungoverned, whereas zero rows across the whole query means we probably cannot
  see them;
* `IsSuspectedFailOpen` combines that with "this object has no governed fields";
* the fetch logs an **ERROR** naming the over-sharing risk in as many words;
* the audit manifest gains a top-level `"suspectedFailOpen": [...]` array.

No drop decision reads any of it. The point is only that an operator can tell
*"nothing is governed"* apart from *"we could not see what is governed"* — which
before was impossible from either the logs or the manifest.

One deliberate exception, at the ingest call site
(`Graph/Ingest.cs`): if FLS resolution throws unexpectedly (e.g. the Salesforce
token cannot be obtained at all), the crawl **logs an ERROR naming the
over-sharing risk and proceeds** rather than halting all ingestion. The manual
`flsFields` list still applies on that run; the fetched restrictions do not.
Halting every crawl on an FLS metadata hiccup was judged the worse operational
hazard — but the log line is explicit that the run may index fields Salesforce
would hide, and it is worth alerting on.

## Configuration

| Variable | Default | Meaning |
| --- | --- | --- |
| `FLS_ENFORCEMENT` | **`true`** | Opt-out. `false`/`0`/`no`/`off` restores the pre-WP-SF-2 behaviour, including the historical content-body leak of the manual list, and re-enables indexing of raw compound values. Two deliberate exceptions, which apply in **both** states: an `flsFields` entry that is not a declared Graph schema property no longer emits an undeclared null property, and `flsFields` matching is case-insensitive. |
| `FLS_MODE` | **`strict`** | `strict` (least-privilege union) or `permissive` (see the warning above). Anything unrecognised means `strict`. |
| `FLS_RELATIONSHIP_FIELDS` | **`evaluate`** | `evaluate`, `drop` or `ignore` for dotted cross-object fields — see above. Anything unrecognised means `evaluate`. |
| `FLS_CACHE_TTL_HOURS` | `24` | Lifetime of a cached permission snapshot before re-query. |

The per-object `flsFields` array in `config/schema.json` still works and is
**unioned** with the fetched permissions. A fetch that returns "everything is
readable" can never clear an operator's explicit entry.

## Caching

Fetched permissions are cached per org + object in the identity store, keyed
exactly like the existing field cache (same `InstanceHash`):

* SQLite — `fls_cache (object_type, instance_hash, permissions, cached_at)`, created
  by the idempotent `CREATE TABLE IF NOT EXISTS` DDL that `InitSchema` runs on every
  open, so pre-existing databases gain the table on first connect;
* SQL Server — `dbo.FlsCache` plus `usp_GetCachedFls` / `usp_SaveCachedFls` /
  `usp_ClearFlsCache`, guarded by `IF OBJECT_ID` / `CREATE OR ALTER` (see
  `docs/SQL_CONTRACT.md`).

A corrupt cache row is treated as absent and re-fetched, matching the field-cache
contract.

## Audit manifest

Every crawl writes `logs/fls_manifest_{connectorId}.json`. The shape is a
contract — a sibling connector consumes it:

```json
{
  "version": 1,
  "connectorId": "SalesforceCRM",
  "generatedAt": "2026-07-19T12:00:00.0000000Z",
  "enforcement": true,
  "mode": "strict",
  "objects": {
    "Account": {
      "principalsInScope": 2,
      "dropped": [
        { "field": "Compensation__c", "reason": "strict: readable by 1 of 2 principal(s) in scope" }
      ]
    }
  },
  "suspectedFailOpen": []
}
```

Objects and dropped fields are sorted so successive runs diff meaningfully.

`suspectedFailOpen` is additive (the manifest stays version 1) and lists objects
whose `FieldPermissions` came back empty in a way consistent with a silent
fail-open — see "Failure posture". A non-empty array means **no field was dropped
on FLS grounds for those objects**, and is worth alerting on.

## Known limits

* ~~**Compound-field components do not propagate.**~~ **CLOSED in WP-SF-4.** This
  limit no longer exists, and while it did it was worse than stated here: the
  component was never FLS-evaluated at all, and its value reached the searchable
  content body verbatim on the shipped config. Compounds are no longer queried;
  addresses are assembled from individually-selected, individually-gated components,
  and anything still compound-shaped is indexed by no route. See "Compound fields —
  CLOSED" above.
* **Compound fields other than addresses have no assembly path.** The `addressFields`
  mechanism reassembles address compounds. Any other compound an operator selects is
  suppressed (fail-closed) rather than reassembled, and logs a warning. This is a
  deliberate **over-drop**: the shipped config selects no such field.
* **Scope granularity.** The default evaluation scopes the union to every principal
  that can read the *object*, not the principals on a *specific item's* ACL. This
  over-drops on records shared narrowly. `FlsPolicy.ComputeDrops(..., principalScope:)`
  is the seam for narrowing it.
* **Metadata columns are out of scope.** `Id`, `OwnerId`, `CreatedDate` and the other
  standard metadata columns are not evaluated: they are not FLS-governed in practice
  and the ACL engine depends on them. A drop naming one is still honoured if an
  operator lists it — including the two `__System.User.*.Id` properties, which
  accept the Salesforce field (`CreatedById`), the metadata property
  (`CreatedByUrl`) or the system property itself as spellings. All three spellings
  gate **all** the properties that field feeds, in every direction. (Until WP-SF-4
  the `__System.*` spelling gated only that property and left `CreatedByUrl`
  publishing the same user Id.)
* **Multi-hop relationship paths.** `Owner.UserRole.Id` would need the permissions of
  an object two joins away. Those are not resolved and fail closed under
  `evaluate`. No such path appears in `selectedFields` in the shipped config.
* **Relationship targets must be declared.** `evaluate` mode can only judge a dotted
  field whose relationship appears in the object's `relationshipObjects` map;
  anything undeclared is dropped. Two tests in
  `FlsRelationshipWiringTests` fail if the shipped config ever adds a dotted key
  without a declared — and itself configured — target.
* **Freshness.** Permissions are re-read at most every `FLS_CACHE_TTL_HOURS`. A field
  restricted in Salesforce mid-window stays indexed until the next fetch —
  the same bounded-lag property the entitlement sync has.
