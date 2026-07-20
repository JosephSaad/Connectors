# JSON `null` in `config/schema.json`

One rule, applied to the whole config model, and the mechanism that enforces it.

## The rule

> **A JSON `null` is read as that key's EMPTY value.**
> `null` string → `""`. `null` collection → empty collection. `null` dictionary
> *value* → `""`. Whether *empty* is legal is then decided by ordinary
> validation, with the same message a config that wrote `""` would have got.

The rule is the same in `config/classification.json` (`"categories": null` and
`"patterns": null` read as empty, and a `null` *entry* in either list is a load
error naming its JSON path), so an operator learns it once. The reflective
mechanism below is specific to `schema.json`'s deserialized model; the
classifier's loader applies the same rule by hand because it reads a
`JsonDocument` rather than binding to a model. See
[`CLASSIFICATION.md`](CLASSIFICATION.md).

Consequences, all of them intentional:

| You write | It is read as | Outcome |
| --- | --- | --- |
| `"objectList": null` | `[]` | load error — *has an empty objectList* |
| `"objectList": [null]` | — | load error naming `$.objectList[0]` (see below) |
| `"selectedFields": null` | `{}` | load error — *object 'X' has no selectedFields* |
| `"selectedFields": {"A__c": null}` | `{"A__c": ""}` | load error naming column `A__c` |
| `"columnPolicies": null` | `{}` | loads: no column is restricted |
| `"columnPolicies": {"A__c": null}` | `{"A__c": ""}` | load error — *invalid columnPolicies action* |
| `"objectName": null` | `""` | load error — *objectName missing* |
| `"aclMode": null` | `""` | load error — *invalid aclMode* |
| `"displayName" / "ownerField" / "ownerEmailField" / "sourcePath" / "iconUrl" / "sensitivityDefault": null` | `""` | loads; the empty-value default applies |
| `"coarseAclAcknowledgedFor": null` | `""` | loads as an UNBOUND attestation; `--strict` fails ([`ACL_POSTURE.md`](ACL_POSTURE.md)) |
| `"coarseAclAcknowledged": null` | — | load error naming the key (a bool has no empty form) |

### `null` vs. an absent key

For every key whose default *is* its empty value, `null` and "key omitted" are
indistinguishable — which is all of them but one.

**The exception is `aclMode`**, the only key whose default is not empty
(`ownerOnly`). Omitting it selects `ownerOnly`; writing `null` yields the *empty*
aclMode, which is rejected. That is deliberate and it is the fail-closed
direction: an explicit `null` on the key that decides who can see the data should
stop the load and make somebody look, not silently select a default.

### Null *elements* of the object list

`"objectList": [null]` has no meaningful empty form — an all-defaults object has
no `objectName`, no `selectedFields` and no `aclMode`, so "repairing" it would be
a guess at intent. It is a load error naming the exact JSON path
(`$.objectList[0]`), so a long list points at the offending entry.

### Values of the wrong TYPE

A value that cannot bind to its member at all (`"coarseAclAcknowledged": "yes"`,
`"selectedFields": []`, a truncated file) is reported as an
`InvalidDataException` quoting the JSON path, exactly like a validation failure.
An operator should never have to tell a parse error from a validation error by
the exception type.

## Why a rule rather than per-key guards

`System.Text.Json` does **not** treat a C# property initializer as a default.
Given

```csharp
public List<ObjectConfig> ObjectList { get; set; } = new();
```

the JSON `{"objectList": null}` does not leave `new()` in place: the deserializer
calls the setter with `null` and overwrites it. So **every** reference-typed
member of the config model — every string, every collection, every dictionary
value — is a potential null the moment somebody types `null` in `schema.json`,
regardless of what the C# type says.

Guarding the members that happened to be reported does not close that. An
earlier round guarded exactly one property at its setter; five more shapes were
still live, and one of them (`selectedFields` with a `null` value) passed
`validate-config --strict` with zero errors and zero warnings, then threw a
`NullReferenceException` on **every record** at conversion time. Because
ingestion catches per record, the result was a silent 100% dead-letter of that
object rather than a loud crash — at 150M-row scale, a total indexing failure
for the object plus a dead-letter queue the size of it.

The rule is therefore enforced by `Config/ConfigNullNormalizer.cs`, which
**walks** the deserialized model reflectively and repairs every null before any
validation or consumer runs. There is no list of property names in it. A member
added to the config model next year is covered the day it is added, and
`ConfigNullSweepTests` generates its null case automatically from the model's own
metadata.

## What an operator sees

Every rejection above is an `InvalidDataException` naming the object and the
offending key, surfaced two ways:

- `validate-config` reports it as `schema.json invalid: <message>`;
- on the crawl path the CLI prints the message and exits **2** — not a stack
  trace. The full record still goes to the run log.
