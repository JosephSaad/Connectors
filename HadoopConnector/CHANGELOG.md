# Changelog

All notable changes to the BDH Hadoop Copilot Connector. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[SemVer](https://semver.org/). Assembly version: `<Version>` in
`src/HadoopConnector/HadoopConnector.csproj`; release tags are
`hadoop-v<version>`.

## [Unreleased]

Bank-grade hardening follow-ups. Two safe-default flips (operators should note
them); everything else is additive and off/unchanged by default.

### Changed — the Windows-service host is now the shared chassis one

This connector no longer carries its own `Infrastructure/ServiceHost.cs`. The four copies were
identical in mechanism — SCM start/stop handshake, working directory, graceful stop at the next
chunk boundary — and differed only in two identity strings and in what each told the Windows
Event Log, so the mechanism moved to `Connector.Chassis.ServiceHost` and the wording stayed here.

- The home directory variable (`HADOOP_CONNECTOR_HOME`) and the SCM service name (`HadoopConnector`) are now supplied as
  `ChassisIdentity.HomeEnvVar` / `.ServiceName`. **Both keep their existing values**, because they
  appear in deployed service definitions and operator runbooks — nothing to change on an installed
  host.
- This connector emitted **no** Windows Event Log entry for service start/stop, and still does
  not: the shared host's lifecycle hooks are left unset rather than the chassis emitting one on
  its behalf.

No behaviour change is intended: same service name, same home variable, same log lines, same Event
Log entries, same graceful-stop semantics and 90-second shutdown budget.

### Changed — release automation now runs, and the tag format changed

The release pipeline is wired to the repository root and produces releases for
real. It had been authored under `HadoopConnector/.github/workflows/release.yml`
— one level below the only directory GitHub executes workflows from — so no tag
had ever triggered it and no bundle, GHCR image or SBOM was ever published by
it. The logic now lives once in `.github/workflows/release-connector.yml`,
called by `.github/workflows/release-hadoop.yml`.

- **Release tags are now `hadoop-v1.2.0`, not `v1.2.0`.** The five connectors
  share one repository and version independently; an unprefixed tag would start
  all five pipelines against a single tag, and only the first to finish could
  create the release.
- Signing secrets are one fleet-wide set: `AUTHENTICODE_PFX_BASE64` /
  `AUTHENTICODE_PFX_PASSWORD` for the win-x64 binary, `COSIGN_PRIVATE_KEY` /
  `COSIGN_PASSWORD` for the image. All optional — signing steps skip with a
  notice and the release ships unsigned.
- `workflow_dispatch` runs the identical build → smoke-test → package path as a
  dry run: nothing is pushed and no release is created.

### Security (bypass fixes — action may be required)

- **A typo in a protective env var no longer switches it off.** This connector
  carried its own `EnvFlags`, holding the parser from *before* the fleet's
  boolean vocabulary was hardened in the chassis: it did not trim, and read any
  unrecognised value as `false`. The hardening landed in `Connector.Chassis`
  only, so the two failures it was written to fix were still live here:
  `CLASSIFICATION_ENFORCE_ACL="true "` (one trailing space, as a `.env` line or
  folded YAML scalar produces) read as **off** while `validate-config` — reading
  the same flag through the same parser — reported success; and
  `CIRCUIT_BREAKER=on` put the Hdfs and Graph breakers into **passthrough**.

  The local copy is deleted and the name now binds to
  `Connector.Chassis.EnvFlags` through a csproj alias, leaving every call site
  unchanged. The protective defaults that shared the contradiction — the circuit
  breaker, the decision ledger and `DELETION_SYNC` — are now spelled
  `!EnvFlags.IsFalse(...)`, so only an explicit `false`/`0`/`no` disables them
  and an unrecognised value warns once and leaves the default standing.
  `ALLOW_FULL_SCAN` is unchanged in direction (default OFF) but now equally
  immune to a typo opening it — treat any use of it as a capacity-plan change,
  per `TENANT_GOVERNANCE.md`. `BooleanVocabularyRegressionTests` pins all of
  this, and fails against the code it replaced.

- **The coarse-ACL attestation is now BOUND to the posture it signed.**
  `coarseAclAcknowledged` was an unbound bool: it recorded that *someone* signed,
  never *what* they signed, so it survived every widening of the exposure it was
  meant to cover — `group` → `public` reused the old sign-off and `--strict`
  still passed, as did swapping `aclGroupId` for a far broader group. A coarse
  object must now also set **`coarseAclAcknowledgedFor`** naming the posture
  verbatim (`"public"` or `"group:<aclGroupId>"`). A binding that does not match
  the effective posture is a **hard config-load error** — in both directions,
  because the connector cannot know whether one Entra group is broader than
  another, so any change forces a fresh review rather than a guess. A bare
  `coarseAclAcknowledged: true` with no binding still parses (older configs load)
  but is **no longer accepted as an attestation**: `validate-config` reports it
  as an unbound sign-off and `--strict` fails. **Action:** add
  `coarseAclAcknowledgedFor` to every attested `group`/`public` object.
  (`Config/SchemaConfig.cs`, `Commands/ValidateConfig.cs`, `docs/ACL_POSTURE.md`)
- **Fixed a crash in that binding check:** `"coarseAclAcknowledgedFor": null` is
  valid JSON and deserialized the property to a null reference, which was
  dereferenced unguarded in **two** places (`ValidateAttestationBinding` and
  `HasBoundCoarseAclAttestation`, the latter running later from
  `validate-config`), so the config load died with a `NullReferenceException`
  instead of a validation message. A JSON `null` is how JSON spells "absent",
  and an absent binding is deliberately not a load error, so `null` now behaves
  exactly like absent, `""` and whitespace: it loads, it is **not** accepted as
  an attestation, and `--strict` fails with the same unbound-sign-off message
  naming the posture token to record. Non-empty garbage tokens remain a hard
  load error. (`Config/SchemaConfig.cs`, `docs/ACL_POSTURE.md`)
- **A JSON `null` anywhere in `schema.json` can no longer crash the connector,
  and the model is null-safe by construction.** `System.Text.Json` does not treat
  a C# property initializer as a default: it *overwrites* `= new()` /
  `= string.Empty` with `null` whenever the JSON says `null`, so **every**
  reference-typed member of the config model was a potential null. The worst
  shape passed preflight green and then destroyed the crawl:
  `"selectedFields": {"Comp__c": null}` loaded cleanly, `validate-config
  --strict` reported **0 errors and 0 warnings**, and conversion then threw a
  `NullReferenceException` on *every* record — which, because ingestion catches
  per record, produced a **silent 100% dead-letter of the object** rather than a
  loud crash. At the documented 150M-row scale that is a total indexing failure
  for an object plus a dead-letter queue its size. Four more shapes
  (`"objectList": null`, `"objectList": [null]`, `"selectedFields": null`,
  `"columnPolicies": null`) crashed at config *load* with a bare
  `NullReferenceException` stack. A previous round guarded exactly one property
  at its setter; the reasoning was right and the scope was wrong. The fix is now
  a single mechanism — `Config/ConfigNullNormalizer.cs` reflectively **walks**
  the deserialized model and repairs every null before validation or any consumer
  runs — so no property name is enumerated anywhere and a member added later is
  covered the day it is added. The semantics are decided once for the whole
  model: **a JSON `null` is read as that key's EMPTY value**, and whether empty is
  legal is decided by ordinary validation with the ordinary message. Values of the
  wrong *type* (`"coarseAclAcknowledged": null`, a truncated file) are likewise
  reported as an `InvalidDataException` quoting the JSON path instead of a raw
  parse error. New: [`docs/CONFIG_NULL_SEMANTICS.md`](docs/CONFIG_NULL_SEMANTICS.md).
  (`Config/ConfigNullNormalizer.cs`, `Config/SchemaConfig.cs`,
  `Item/ItemConverter.cs`)
- **Two `selectedFields` columns mapped to the same Graph property are now
  rejected at config load.** Only one can occupy `properties[name]` on the item
  and the winner is dictionary-order luck, so the loser's value — *and any
  `columnPolicies` drop/mask protecting it* — was silently discarded while
  preflight went on reporting the policy as applied:
  `{"Id":"Id","Salary":"Comp","Bonus":"Comp"}` with `{"Salary":"mask"}` printed
  `masked=[Salary]` over an item whose `Comp` property held Bonus's real value
  and no `[RESTRICTED]` marker. The masked value did not leak, but the report
  claimed a restriction the item did not deliver — the same family as the
  identity-column and colliding-property rejections. Rejecting the **mapping**
  rather than the policy also closes the shapes nobody enumerated: the
  overwritten `drop`, three columns onto one property, casing-only differences,
  and plain silent data loss with no policy at all. `_bdh_` placeholders are
  exempt (they are content-body markers keyed by column name, not property
  names). Also rejected: a `selectedFields` entry with an empty column name or an
  empty Graph property name. (`Config/SchemaConfig.cs`, `docs/COLUMN_POLICIES.md`)
- **A `columnPolicies` entry on the identity column `Id` is now rejected at
  config load.** It previously validated cleanly and `validate-config` announced
  it as dropped, while the value still reached the index three ways —
  `externalItem.id`, the `Url` deep link built from it
  (`ItemConverter.BuildUrl`), and the content title fallback when `Name` was
  dropped too. The id is structurally load-bearing (Graph cannot index an item
  without it; inventory, deletion sync, the dead-letter queue and `retry-failed`
  all key on it), so it cannot be enforced — and a control that reports a
  restriction it is not delivering is worse than no control. The error names the
  surviving routes and the real alternatives: remove the column from
  `selectedFields`, narrow the `aclMode`, or filter the record out. Matching is
  case-insensitive, mirroring `BdhRecord.RawId`.
  (`Config/SchemaConfig.cs`, `docs/COLUMN_POLICIES.md`)
- **A `columnPolicies` entry on a column named after ANOTHER column's Graph
  property is now rejected at config load.** With
  `selectedFields: {"Id": "RecordId", "RecordId": "Other"}` and
  `columnPolicies: {"RecordId": "drop"}` the config loaded cleanly and
  `validate-config` printed `dropped=[RecordId]` — while a fully populated Graph
  property called `RecordId` carried on reaching the index from column `Id`. The
  policy applied, but the report could not be read as true, and the likeliest
  reason to write it is meaning the property rather than the column. Same family
  as the two rejections above. Not rejected: a column mapped to a property of its
  own name (the ordinary case), or a collision whose colliding property is itself
  `drop`ped and so never reaches the index — `mask` does not qualify, since a
  masked column still emits a property of that name.
  (`Config/SchemaConfig.cs`, `docs/COLUMN_POLICIES.md`)
- **Dead-letter payloads: investigated, no change needed.** Every dead-letter
  request body is `ExternalItem.ToJson()` — the *converted* item — so column
  policies already cover it and a dropped/masked column cannot re-appear there
  even under `DEADLETTER_PAYLOAD_MODE=full`. A second, independent copy of the
  gate would only drift out of lock-step; the property is pinned by a regression
  test instead. (`RestrictionBypassProbeTests`)

### Changed (safe-default flips — action may be required)

- **Dead-letter payload mode now defaults to `redacted`** (was `full`):
  `DEADLETTER_PAYLOAD_MODE` unset no longer stores record VALUES in the
  dead-letter queue — only ids, object type, error, property names, sizes and
  SHA-256 hashes. Set `DEADLETTER_PAYLOAD_MODE=full` to restore the verbatim
  payloads for fast diagnosis. An **unrecognized value now fails fast at config
  load** (a typo can no longer silently pick a mode). `validate-config` reports
  it too. (`Config/DeadLetterRedaction.cs`, `Config/AppConfig.cs`)
- **`IDENTITY_SYNC_ON_INCREMENTAL` now defaults to ON**: the entitlement
  (BDH→Entra) mapping re-syncs on incremental crawls too, shrinking entitlement
  lag to the incremental cadence. Set it `false` to restrict identity sync to
  full crawls. Residual, non-real-time lag documented (an item's ACL is only
  re-emitted when its source record changes — schedule full crawls at your
  entitlement-freshness SLA). (`Infrastructure/EnvFlags.cs`)

### Added

- **`validate-config` now cross-checks `graph-schema.json` against what the
  object list actually produces.** Previously nothing enforced the two files
  staying in step (the README said so): `graph-schema.json =
  [{"name":"Irrelevant","type":"String"}]` with a `schema.json` mapping onto
  `TotallyUndeclaredProp` passed `--strict` **green**, naming neither that
  property nor any always-emitted one — and Graph would then reject every item
  at push time. The produced set is now derived from **production symbols, not
  a restated rule copy** (a duplicated rule set drifts — that is exactly how
  the earlier "five standard properties" defect happened):
  `Item/ProducedGraphProperties.cs` executes `ItemConverter.Convert` over a
  synthetic record per object (so a `drop`ped column emits nothing and needs no
  declaration, a `mask`ed column still emits and does, and a `_bdh_`
  placeholder is not a Graph property) and unions
  `AlwaysEmittedProperties.Names` — **unconditionally, flag on or off**,
  because gating `SensitivityLabel`/`DetectedCategories` on `CLASSIFICATION`
  would recreate the flag-flip asymmetry fixed in this same round. A
  produced-but-undeclared property is an **ERROR** (the config cannot work);
  declared-but-unproduced is an informational notice, never gating
  (pre-declaring ahead of a rollout is encouraged, and a one-sided rename is
  still made visible). Degenerate `graph-schema.json` shapes — empty array,
  empty/whitespace name — are now preflight ERRORs too (bare `null`,
  non-string/duplicate names and malformed JSON already were), and the drift
  diff deliberately does not run over a file that failed to parse, so nothing
  is double-reported. Adapted from the Clarizen connector's drift check
  *including its failure history*: no blanket `catch {}` around the
  computation — a failure to compute the produced set is itself an ERROR
  (pinned by an injected-failure test). The shipped
  `config/schema.json`/`config/graph-schema.json` pair has zero drift in
  either direction, flag on and off (pinned by
  `ShippedConfigPair_HasZeroDrift_FlagOnOrOff`).
  (`Commands/ValidateConfig.cs`, `Item/ProducedGraphProperties.cs`, `README.md`,
  `docs/COLUMN_POLICIES.md`)
- **The empty-categories finding is no longer gated on `CLASSIFICATION`.** The
  warning for a `classification.json` that yields no usable categories was
  guarded by `classificationEnabled &&` — one line below the round-10 comment
  explaining why flag-conditional validation is a time bomb — so
  `{"categories": []}` was `--strict` **green** with the flag unset and **red**
  with it set. The flag is what operators flip last: that asymmetry converts a
  green change ticket into a silent-detection-gap deployment on the day
  classification is enabled. The finding is now emitted whenever the file
  exists, whatever the flag says; it stays a **warning** (empty categories is
  valid JSON describing a useless configuration — the crawl does not crash on
  it — and `--strict` already turns warnings into failures, which is what
  restores flag-flip invariance). The `--strict` verdict is now pinned
  invariant under the flag for every file-present shape
  (`StrictVerdict_IsInvariantUnderClassificationFlag_WhenFileExists`); an
  *absent* file remains the one flag-judged case, because absence with the
  feature off is a valid deployment and enabling the feature means authoring
  the file in that same change. (`Commands/ValidateConfig.cs`)
- **A bad config FILE on the crawl path now prints an actionable message, not a
  stack trace.** `Runtime.Create` loaded `schema.json` and `filters.json` with no
  `try`/`catch`, so any mistake in either escaped to `Program`'s final backstop —
  which prints the whole exception, frames included. Both loaders already produce
  a sentence naming the object, the key and the fix; burying it under a stack
  trains operators to stop reading it. Config-shaped failures (invalid data,
  malformed JSON, missing/unreadable file) now exit **2** with the message alone
  plus a pointer to `validate-config --strict`, and the full record still goes to
  the run log. Failures that are *not* config-shaped still reach the backstop
  with their stack, because that is a bug and the stack is the point. Env/
  `AppConfig` errors keep their existing exit-1 backstop contract.
  (`Commands/Runtime.cs`)
- **Restrictive filesystem permissions at startup**: the local state
  directories (logs / state / dead-letter) are created **owner-only** — POSIX
  `0700`; on Windows a best-effort `icacls` lock-down (owner + Administrators +
  SYSTEM, inheritance broken). Best-effort, never fatal.
  (`Infrastructure/SecureDirectories.cs`)
- **Optional classification ACL enforcement** (`CLASSIFICATION_ENFORCE_ACL` +
  `CLASSIFICATION_RESTRICTED_GROUP_ID`, default OFF): when on, top-tier
  (`Restricted`) items have their ACL narrowed to the configured Entra group so
  the classification tag actually gates retrieval. (`Graph/Ingest.cs`)
- **Stale-index expiry** (`GRAPH_ITEM_TTL_DAYS`, default unset): stamps ingested
  items with `expirationDateTime = now + TTL` so the index self-expires if
  crawling stops. (`Graph/Models.cs`, `Item/ItemConverter.cs`)
- **Immutable decision ledger** (`DECISION_LEDGER`, default ON): append-only,
  SHA-256 hash-chained audit of EXCLUSION and ACL_RESTRICTION decisions with a
  `Verify()` that detects any edit, deletion or reorder.
  (`Infrastructure/DecisionLedger.cs`)

- **Coarse-ACL posture is now surfaced and must be attested** (WP-HD-2, INTERIM
  control pending the Apache Ranger integration). `validate-config` reports
  every object whose `aclMode` is `group` or `public` on **every** run, naming
  the object, its mode, and whether it is effectively filtered — with a
  materially stronger message for the worst case (coarse **and** unfiltered:
  the entire object indexed at a flat grant). A new per-object property in
  `config/schema.json`, **`coarseAclAcknowledged: true`**, records explicit
  human sign-off; without it `validate-config --strict` **FAILS** for that
  object, with it `--strict` passes but the warning is still printed
  (`WARNING (acknowledged): …`, tracked separately in
  `Result.AcknowledgedWarnings`). `ownerOnly` objects are unaffected; the
  attestation on an `ownerOnly` object is a **config error** so a stale `true`
  can never pre-approve a later widening. The shipped `config/schema.json`
  deliberately does **not** pre-set the flag, so a fresh clone fails
  `--strict` on `Account` until reviewed. **This control makes the exposure
  visible and signed-off — it does not reduce it.**
  (`Commands/ValidateConfig.cs`, `Config/SchemaConfig.cs`, `config/schema.json`,
  `docs/ACL_POSTURE.md`)
- **Per-column `drop`/`mask` policies** — new optional `columnPolicies` map per
  object in `config/schema.json` (BDH column → `drop` | `mask`). Until now
  sensitivity was per-**OBJECT** only, so a restricted column (compensation,
  margin, personal identifiers) reached the index for everyone the record's
  coarse ACL admitted. `drop` never emits the column; `mask` keeps the property
  name and content key and replaces the value with `[RESTRICTED]` (the value is
  never read). Enforced in **BOTH** conversion passes — the Graph **property**
  loop and the searchable **content body** loop — plus the content title line,
  which reads `Name` outside both loops; a column gated in only one path would
  still be indexed in the other. Column matching is case-insensitive (as
  `BdhRecord.Get` is). An **unknown action**, a policy naming a column **not in
  that object's `selectedFields`**, or two keys differing only in case all
  **fail config load**, because a policy that silently restricts nothing is
  worse than none. `validate-config` prints a `POSTURE:` line per object with
  the dropped/masked counts and column names — and states explicitly when no
  object restricts any column; masking a column mapped to a non-`String` Graph
  property is a **warning** (the marker is a string, so Graph would reject the
  item). No action rewrites the item ACL: an ACL-rewriting action would
  interact with the classification ACL-rewrite ordering in
  `IngestPipeline.ApplyClassification` and is deliberately deferred until the
  Ranger work settles the ACL model. Absent `columnPolicies` ⇒ conversion is
  byte-identical to before. (`Config/SchemaConfig.cs`, `Item/ItemConverter.cs`,
  `Commands/ValidateConfig.cs`, `config/schema.json`,
  `docs/COLUMN_POLICIES.md`)

### Documentation / honesty

- **`docs/ACL_POSTURE.md`** (new): the coarse `group`/`public` over-sharing
  exposure stated plainly, what the `coarseAclAcknowledged` attestation does and
  does not buy, and the ways to actually *reduce* the exposure (narrower
  `aclMode`/`aclGroupId`, tighter `filters.json`, fewer `selectedFields` /
  `columnPolicies`, `CLASSIFICATION_ENFORCE_ACL`) pending Ranger.
- **`docs/COLUMN_POLICIES.md`** (new): what `drop`/`mask` do, why `mask` keeps
  the key even for an empty value, the two-pass conversion model and why a gate
  in only one pass leaks, the load-time rejections, and why an ACL-rewriting
  action is out of scope.
- Classification naming/docs corrected: `SensitivityLabel` is a
  connector-applied **advisory tag** (a Graph refiner), **not** a Microsoft
  Purview-enforced label — it does not encrypt or gate access on its own (the
  wire schema property name is unchanged for back-compat).

### Build & repository layout

- **The connector now consumes the shared `Connector.Chassis` project (1.13.1)
  instead of carrying its own copies.** The connector lives in a monorepo
  alongside the Salesforce, Clarizen, Seismic and Altrata connectors and the
  chassis, and references it as a sibling project
  (`<ProjectReference Include="..\..\..\Connector.Chassis\Connector.Chassis.csproj" />`)
  — **not** a NuGet package: there is no feed, no `nuget.config` and no version
  pin. Taken from the chassis: logging, secret provider, SQL executor / SQL
  gateway, metrics renderer, service-stop seam and the chassis identity seam.
  Still this connector's own: decision ledger, HA coordinator, SQL state store,
  alerting, event-log sink, log pruner, service host, env flags, circuit
  breaker. No behaviour change. (`src/HadoopConnector/HadoopConnector.csproj`,
  `src/HadoopConnector/Program.cs`)
- **CI now actually runs.** The live pipeline is
  `.github/workflows/hadoop.yml` at the **repository root** — GitHub executes
  only root workflows — building and testing on ubuntu-latest and
  windows-latest and building the container image. CodeQL
  (`.github/workflows/codeql.yml`) and releases
  (`.github/workflows/release-hadoop.yml`) run from the root too; this connector
  keeps no workflows of its own.
- **Docker builds from the repository root.** The image needs
  `Connector.Chassis` inside its build context, so
  `docker build -f HadoopConnector/Dockerfile .` from the root replaces the old
  `docker build .`; `docker-compose.yml` sets `context: ..` for the same
  reason, and a single root `.dockerignore` governs the build.
- Test count is now **983** (green on both ubuntu-latest and windows-latest).

## [1.0.0] — 2026-07-18

First production release: the full connector chassis plus the enterprise
hardening package.

### Core connector

- BDH source access over WebHDFS (LISTSTATUS/OPEN, retry ladder with exact
  Retry-After handling, circuit breaker) or a mounted export directory
  (`HDFS_MODE=localpath`); Hive-partition scanner with dt-watermark and
  partition-filter pruning; hardened streaming CSV/JSONL parser with bounded
  reads (`BDH_MAX_FILE_BYTES`).
- The filter layer (`config/filters.json`): partition pruning → streamed
  record predicates → row cap, strict load-time validation, per-stage
  accounting, and the **fail-closed scale guard** (an object with no effective
  filter refuses to crawl; `dt isNotNull`-only filters do not count as
  effective).
- Graph ingestion: $batch with adaptive concurrency, checkpointed resume,
  dead-letter + `retry-failed`, deletion sweep with mass-deletion guards
  (absolute cap + percent guard + empty-source engagement) and sweep
  suppression on ANY incomplete fetch (row cap or oversize skip); reconcile
  with the same truncation safety; connection sharding; sovereign-cloud
  endpoints.
- Coarse ACL engine (ownerOnly/group/public), identity sync from the BDH User
  export with **fail-loud incomplete-directory refusal**, SQLite/SQL Server
  identity stores.
- Operations: unified CLI, Windows-service mode with graceful stop, SQL state
  backend + active-active HA (leased object claims, close-with-failed-claims),
  health/readiness/metrics endpoints, webhook alerting, OpenTelemetry tracing
  with correlation ids, circuit breakers + degraded mode, optional content
  classification + sensitivity labeling, Key Vault secret resolution.

### Enterprise hardening package (this release)

- **Windows Event Log sink** (`EVENTLOG_ENABLED`, source `HadoopConnector`,
  log `Application`): mirrors Warning/Error (+Info with `EVENTLOG_LEVEL=info`)
  and lifecycle start/stop events with stable event ids for SIEM collection
  (`docs/SIEM.md`); never throws; no-op off-Windows; idempotent source
  registration in `scripts/install-windows-service.ps1`.
- **Enterprise egress**: `PROXY_URL`/`PROXY_BYPASS` outbound proxy with
  wildcard bypass, and `CA_BUNDLE_PATH` additive PEM trust (private CAs on
  WebHDFS / TLS-inspecting proxies) via a custom-root-trust chain rebuild —
  both fail fast naming the setting; wired into the WebHDFS, Graph and
  alerting clients.
- **Certificate credential for Graph** (`GRAPH_CLIENT_CERT_PATH` /
  `GRAPH_CLIENT_CERT_PASSWORD` / `GRAPH_CLIENT_CERT_THUMBPRINT`): RFC 7523
  client-assertion JWT (RS256, `x5t#S256`, aud/jti/nbf/exp), certificate wins
  over a configured client secret, auth MODE logged only.
- **Dead-letter payload protection** (`DEADLETTER_PAYLOAD_MODE=full|redacted`):
  redacted mode strips record values before either backend writes, keeping
  ids, object type, error, property names, sizes and SHA-256 hashes;
  `retry-failed` (including the oversize-inconclusive keep rule) is unaffected;
  unknown mode values fail toward redaction.
- **FIPS posture**: audited — no MD5/SHA-1/DES/RC4/3DES anywhere; all hashing
  and signing added by this release is SHA-256/RSA (`docs/THREAT_MODEL.md`).
- **Ops pack**: new `guard_refusals_total`, `partial_objects_total`,
  `sweeps_suppressed_total` counters and `ha_claims_held` gauge;
  `ops/grafana-dashboard.json`, `ops/prometheus-alerts.yml`,
  `ops/azure-monitor-alerts.kql` keyed to `docs/RUNBOOKS.md`.
- **CI/CD**: coverage gate (measured 79.87% line at 650 tests; floor 74.87%),
  perf-smoke job on the StressHarness filter-scale scenario (≥100k rows/s,
  <500 MB RSS), CycloneDX SBOM on releases, Authenticode + cosign signing
  gated on secrets (graceful skip), experimental WiX v5 MSI
  (`packaging/msi/`).
- **Docs**: `docs/THREAT_MODEL.md`, `docs/RUNBOOKS.md`, `docs/DR.md`,
  `docs/SIEM.md`, `docs/DEPLOYMENT_ENTERPRISE.md`, `SECURITY.md`.

### Test suite

650 offline tests (no network); StressHarness scenarios (`--scenario all`)
cover 10^5–10^6-row behaviour of the real pipeline components.
