# Coarse-ACL posture and the `coarseAclAcknowledged` attestation

<!-- The attestation is BOUND to the posture it signed (coarseAclAcknowledgedFor);
     see "The attestation is bound to the posture it signed" below. -->


**Status: INTERIM control, pending the Apache Ranger integration.**

> This control makes an over-sharing risk **visible and signed-off**.
> It does **not** reduce that risk. Nothing on this page narrows a single
> grant, filters a single column, or hides a single row. An object with an
> attestation is exposed exactly as widely as the same object without one —
> the only difference is that a named human has accepted it and
> `validate-config --strict` will proceed.

## The exposure

BDH mirrors the Salesforce **data** but not the Salesforce **sharing tables**.
Full sharing-model resolution (role hierarchy, sharing rules, teams,
row-level and field-level security) is therefore impossible in this connector.
The per-object `aclMode` in `config/schema.json` offers three grants, and two
of them are *coarse* — they grant access wider than the record's owner and
cannot express what the source system enforces:

| `aclMode` | Grant | Coarse? |
|---|---|---|
| `ownerOnly` (default) | the record's owner only, resolved through the identity store | no |
| `group` | **one flat Entra group for every row of the object**, regardless of who could see that row in Salesforce | **yes** |
| `public` | **`everyoneExceptGuests`** — every non-guest user in the tenant | **yes** |

On a mart mirroring 150M+ Salesforce rows, that is a material over-sharing
exposure: a row that only three people could read in Salesforce becomes
readable by an entire group — or by the whole tenant — once it is indexed in
Microsoft Search / Copilot.

### The worst case: coarse **and** unfiltered

`filters.json` bounds *which* rows get ingested. An object that is coarse-ACL
**and** not effectively filtered has no bound on either side: the **entire**
object is indexed, and every row of it is exposed at the coarse grant.
`validate-config` states this case far more strongly (`WORST CASE: …`) than
coarse-but-filtered, and it is the case to fix before production.

"Effectively filtered" reuses the fail-closed scale guard's own predicate
(`ObjectFilter.IsEffectivelyFiltered`), so it agrees with the crawl: a record
predicate counts, a `dt` partition predicate counts, but a non-`dt` partition
predicate or a `dt isNotNull` counts for nothing, because neither prunes.

Note that `fullScanAllowed` and `ALLOW_FULL_SCAN=true` do **not** suppress this
finding. They exempt an object from the *scale* guard, which if anything means
*more* rows reach the coarse grant.

## The control

`validate-config` reports the posture of **every** `group`/`public` object on
every run, naming the object, its `aclMode`, and whether it is effectively
filtered. Sign-off is recorded in `config/schema.json`, per object:

```jsonc
{
  "objectName": "Account",
  "aclMode": "group",
  "aclGroupId": "e7b1…",
  "coarseAclAcknowledged": true,                 // ← reviewed; exposure accepted
  "coarseAclAcknowledgedFor": "group:e7b1…",     // ← …and WHICH exposure was reviewed
  "selectedFields": { "…": "…" }
}
```

| Object state | `validate-config` | `validate-config --strict` |
|---|---|---|
| `ownerOnly` | no finding | passes |
| coarse, no attestation | **WARNING** | **ERROR — fails** |
| coarse, `coarseAclAcknowledged: false` | **WARNING** | **ERROR — fails** |
| coarse, `coarseAclAcknowledged: true`, no `coarseAclAcknowledgedFor` | **WARNING** (unbound sign-off, not accepted) | **ERROR — fails** |
| coarse, `coarseAclAcknowledgedFor` **`null`, `""` or whitespace** | as above — identical to the key being absent | **ERROR — fails** |
| coarse, attestation bound to a **different** posture | **config load ERROR** — the connector refuses to run | **config load ERROR** |
| coarse, attestation bound to the **effective** posture | **WARNING (acknowledged)** | passes |

### The attestation is bound to the posture it signed

`coarseAclAcknowledged` on its own is an **unbound bool**: it records that
*someone signed*, never *what they signed*. That makes it survive every widening
of the exposure it supposedly covers — flip `group` → `public`, or swap
`aclGroupId` for a group ten times the size, and the stale `true` silently
pre-approves an exposure nobody reviewed while `--strict` keeps passing.

`coarseAclAcknowledgedFor` records the **posture token** that was reviewed:

| `aclMode` | posture token |
|---|---|
| `public` | `public` |
| `group` | `group:<aclGroupId>` |

Rules, all enforced in `SchemaConfig.Load` unless stated:

- A token that **does not match** the object's effective posture is a hard
  **config-load error** — not merely a `--strict` failure. The crawl reads the
  same file, so a config asserting something false about its own authorisation
  posture must not run at all. The error names both the attested and the
  effective posture and the exact string to write once the new posture has been
  re-reviewed.
- **Any** posture change voids the sign-off, in **both** directions. Narrowing
  is not exempt: this connector cannot know whether one Entra group is broader
  than another, so it never guesses — it asks for a fresh review.
- Group ids compare **case-insensitively** (Entra object ids are GUIDs), so a
  casing difference is not a posture change.
- A binding **without** `coarseAclAcknowledged: true` is a half-deleted
  attestation and is rejected; set both or remove both.
- A binding on an `ownerOnly` object is rejected for the same reason the bare
  flag is (below).
- A bare `coarseAclAcknowledged: true` with no binding still **parses** (older
  configs load) but is **not accepted as an attestation**: `validate-config`
  reports it as an unbound sign-off and `--strict` fails.
- A binding of `null`, `""` or whitespace is treated **exactly like an absent
  one** — unbound, loads, fails `--strict` with the message naming the token to
  record. That is not a special case for this key: a JSON `null` is read as the
  EMPTY value of whatever key it is written on, for the whole config model — see
  [`CONFIG_NULL_SEMANTICS.md`](CONFIG_NULL_SEMANTICS.md). No config shape may
  crash the load. A **non-empty** token that is not `public` or `group:<id>` remains a hard
  load error, so real typos are still caught rather than silently downgraded.

Three further properties are deliberate:

- **The warning never goes away.** An attested object still prints a `WARNING
  (acknowledged): …` line on every run. Accepting an exposure is not the same
  as removing it, and the operator should keep seeing it. Internally these are
  kept in `Result.AcknowledgedWarnings`, apart from `Result.Warnings`, so
  "accepted" can never be silently counted as "clean".
- **An explicit `false` is not an attestation.** It behaves exactly like an
  absent property, so "we looked and said no" can never read as sign-off.
- **The shipped `config/schema.json` does not pre-set the flag.** A fresh clone
  *fails* `validate-config --strict` on `Account` (`aclMode: group`). That is
  intended: a pre-checked attestation copied from a sample would sign off an
  exposure nobody reviewed.

### An attestation on an `ownerOnly` object is a config error

`SchemaConfig.Load` throws. The flag attests to nothing on a non-coarse
object today, and a stale `true` left behind would **pre-approve** the exposure
the moment someone widens that object to `group`/`public` — `--strict` would
pass and the review this control exists to force would never happen. The fix is
to delete the line.

### What it does not do

The attestation does not touch the crawl, the ACL resolver, or the fail-closed
scale guard. An attested-but-unfiltered object still trips the full-scan guard
and still refuses to crawl. A *valid* attestation gates one thing only: the
coarse-ACL finding's severity in `validate-config --strict`. (A *stale* one is
different — a binding that no longer matches the posture fails config load, so
it stops the crawl too.)

## Reducing (not just accepting) the exposure

In order of preference:

1. **Use `ownerOnly`** where ownership is a defensible proxy for visibility.
2. **Narrow the group.** If `group` is unavoidable, make `aclGroupId` the
   smallest group that is genuinely entitled to every row of the object — not a
   convenient umbrella group.
3. **Filter harder.** A tight `filters.json` shrinks the row set that reaches
   the coarse grant. It is a scale control that doubles as a blast-radius
   control.
4. **Restrict columns.** Two column-level levers: leaving a field out of
   `selectedFields` entirely (never read, never indexed), and `columnPolicies`
   (`drop` / `mask`) for a column that must stay selected but must not reach the
   index in full — see [`COLUMN_POLICIES.md`](COLUMN_POLICIES.md). Neither
   changes *who* the item is granted to, only *what* they get.
5. **`CLASSIFICATION_ENFORCE_ACL=true`** narrows `Restricted`-classified items
   to a single group (`CLASSIFICATION_RESTRICTED_GROUP_ID`) — partial and
   heuristic, but it does reduce exposure on the most sensitive rows. See
   [`CLASSIFICATION.md`](CLASSIFICATION.md).

## Exit criteria (when this control retires)

This is an interim measure until the connector resolves authorisation from
**Apache Ranger** policies instead of a per-object flat grant. Once Ranger
row-filter and column-masking policies are honoured per record, coarse
`group`/`public` modes stop being the only option above `ownerOnly`, and the
attestation should be replaced by real enforcement rather than kept as
permanent paperwork.

Related: [`FILTERS.md`](FILTERS.md) (the scale control and
`IsEffectivelyFiltered`), [`THREAT_MODEL.md`](THREAT_MODEL.md),
[`CLASSIFICATION.md`](CLASSIFICATION.md).
