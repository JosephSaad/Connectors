# Changelog

All notable changes to the Salesforce Copilot Connector (C#) are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Enterprise hardening package.

### Changed — release automation now runs, and the tag format changed

The release pipeline is wired to the repository root and produces releases for
real. It had been authored under
`SalesforceConnector/.github/workflows/release.yml` — one level below the only
directory GitHub executes workflows from — so no tag had ever triggered it and
no bundle, GHCR image or SBOM was ever published by it. The logic now lives once
in `.github/workflows/release-connector.yml`, called by
`.github/workflows/release-salesforce.yml`.

- **Release tags are now `salesforce-v1.2.0`, not `v1.2.0`.** The five
  connectors share one repository and version independently; an unprefixed tag
  would start all five pipelines against a single tag, and only the first to
  finish could create the release.
- Signing secrets moved to the fleet-wide set: `AUTHENTICODE_PFX_BASE64` /
  `AUTHENTICODE_PFX_PASSWORD` for the win-x64 binary, `COSIGN_PRIVATE_KEY` /
  `COSIGN_PASSWORD` for the image — replacing this connector's
  `SIGNING_CERT_PFX_BASE64` / `SIGNING_COSIGN_PRIVATE_KEY` spellings, which were
  never read by anything. All optional; signing steps skip with a notice and the
  release ships unsigned.
- The unported file gated five steps on `secrets.*` inside a step-level `if:`,
  where the `secrets` context is not available. Signing availability is now
  resolved once into a step output, which is valid.
- `workflow_dispatch` runs the identical build → smoke-test → package path as a
  dry run: nothing is pushed and no release is created.

### Security
- **Field-level security enforcement (WP-SF-2)** — `FLS_ENFORCEMENT` (**default ON**),
  `FLS_MODE=strict|permissive` (**default `strict`**), `FLS_CACHE_TTL_HOURS` (default 24).
  Previously the connector reproduced Salesforce's *record*-level sharing faithfully
  but evaluated *field*-level security not at all: FLS existed only as a hand-maintained
  per-object `flsFields` list in `config/schema.json`, so a user permitted to see a
  record saw **every** indexed field on it — including fields Salesforce would hide.
  Field read permissions are now fetched from `FieldPermissions` (with a `describe`
  fallback for orgs that deny access to it) through the existing `SalesforceClient`,
  cached per org+object in the identity store (`fls_cache` / `dbo.FlsCache`, both
  backends in lockstep), and enforced in **both** item-assembly loops.
  - **Bug fixed:** the pre-existing `flsFields` precedent gated only the *property*
    loop — it nulled the Graph property and left the value sitting verbatim in the
    searchable content body that Copilot grounds on. Both loops (and their nested
    relationship sub-field paths) are now gated.
  - **Decision rule:** an item carries one property set shared by every principal on
    its ACL, so visibility is per-*item*, not per-user. A field readable by some but
    not all principals in scope is therefore **dropped** (least-privilege union) under
    `strict`. `permissive` drops only fields no principal can read and **can expose a
    field to a principal Salesforce would deny** — a documented, weaker escape hatch.
  - The manual `flsFields` list still applies and is **unioned** with the fetched
    permissions; a fetch can never silently shrink an operator's explicit list.
  - Audit artifact `logs/fls_manifest_{connectorId}.json` records per object which
    fields were dropped and why (stable format; a sibling connector consumes it).
  - `FLS_ENFORCEMENT=false` restores the previous behaviour byte-identically. (Two
    WP-SF-4 corrections apply in **both** states and are therefore outside that
    guarantee: an `flsFields` entry that is not a declared Graph schema property no
    longer emits an undeclared null property, and `flsFields` matching is
    case-insensitive.)
  - See `docs/FLS.md`.
- **Compound-field FLS residual closed (WP-SF-4)** — the leak WP-SF-3 documented as an
  "accepted limitation" was **live on the shipped `config/schema.json`** and worse than
  the docs stated. A restricted component was not merely unable to drop its compound —
  it was **never evaluated at all**: `FlsPolicy.ComputeDrops` is fed
  `handler.SelectedFields.Keys`, which held only the compound (`BillingAddress`), never
  the component (`BillingStreet`). With every Billing component readable by nobody in
  `FLS_MODE=strict`, the computed drop set was `[Parent.Name]` — **zero address drops**
  — and the component values landed verbatim in the **searchable content body**.
  Reproduced on Contact/`MailingAddress` and Lead/`Address`; all five shipped compounds
  map to `_sf_` placeholders and so all five took the content-body route.
  - **Compounds are no longer queried.** `config/schema.json` now selects the address
    **components individually** (`BillingStreet`, `BillingCity`, `BillingState`,
    `BillingPostalCode`, `BillingCountry`, and the equivalents for `ShippingAddress` /
    `MailingAddress` / `OtherAddress` / Lead `Address`) and declares the reassembly in a
    new per-object `addressFields` map. Every selected field therefore carries real
    `FieldPermissions` evidence and is gated by **literal name** — no inference
    anywhere, which is what made the deleted `CompoundFields` feature unsound.
  - **Anything still compound-shaped fails CLOSED.** Config alone would fix only the
    five shipped compounds; the structural rule closes the class. A compound value is
    indexed by **no** route — not as a Graph property, not flattened into the body —
    because it can carry no FLS evidence. Covers custom address compounds, geolocation
    compounds, Person-Account address compounds, an operator config that still lists a
    compound, and any object added later. Declared relationship sub-objects are exempt.
  - **Output shape preserved, and improved.** The assembled address text is
    byte-identical to the old compound serialisation when nothing is restricted; on the
    shipped ingest path it replaces a flattened `BillingAddress.street: …` blob with a
    single readable address. A restricted component now simply vanishes from it.
  - `FlsCompoundNotIndexableTests` sweeps **all 32 subsets** of the five components
    across **both** assembly routings, plus `ShippedSchemaCompoundTests` re-running the
    original probe against the real `config/schema.json`.
- **FLS alias closure — `__System.*` drops now gate every property the field feeds.**
  `IsSystemUserColumnRestricted`'s docstring claimed a drop "can legitimately be spelled
  three ways … All three must gate". It held in two directions only: the
  `__System.User.CreatedBy.Id` spelling suppressed the system column but **not**
  `CreatedByUrl`, which embeds the same user Id. `IsFlsRestricted` now matches a field's
  whole **alias closure** — its Salesforce name, its `selectedFields` property, its
  metadata property and its `__System.*` property — computed from declared maps only.
  This generalises beyond the two reported columns to every field with more than one
  spelling (e.g. `OwnerId` / `OwnerUrl`). Not live on the shipped ingest path
  (`CreatedByUrl` is absent from the shipped `graph-schema.json`), fixed because the
  docstring stated an invariant the code did not hold and the shipped config is not the
  only config.
- **`flsFields` casing is no longer a silent no-op.** The documented operator lever
  matched with `StringComparer.Ordinal`, so a hand-typed entry differing only in case
  dropped **nothing** while looking, in config, exactly like a drop — `billingaddress`
  leaked `BillingAddress.street: …` in full where `BillingAddress` dropped it cleanly.
  Matching is now case-insensitive (a typo **works**) and a startup warning names the
  declared spelling (a typo is also **visible**). An entry matching nothing at all warns
  too. Salesforce forbids two fields on one object whose API names differ only by case,
  so this cannot over-match.
- **`flsFields` no longer emits undeclared Graph properties.** The retained legacy line
  `props[flsField] = null` wrote the key unconditionally, so listing a non-property
  field (a compound, a relationship path, a typo) posted an **undeclared** null property
  to Graph — and `Graph/Ingest.cs` performs no schema-conformance filter before push.
  The key is now written only when it is a declared Graph schema property, resolved
  case-insensitively to the **schema's** spelling so a casing typo cannot mint a ghost
  property alongside the real one. `AccountUrl` is likewise no longer synthesised from a
  nulled `AccountId`.
- **FLS leak closure (WP-SF-3)** — four over-sharing holes left open by WP-SF-2,
  each with a regression test in
  `tests/.../TestAclEngine/FlsLeakRegressionTests.cs`.
  - **Nested sub-fields, Graph-property spelling.** The content loop's nested
    `Parent.Child` gate passed `null` for the property name while its property-loop
    counterpart passed the real mapped name. Since a drop matches *either* the
    Salesforce field key *or* the Graph property, a drop written in Graph-property
    spelling gated the property and then **leaked the value into the searchable
    body** as `"Parent.Child: <value>"`. Both gates now pass `MappedPropertyName`.
    WP-SF-2 claimed this asymmetry was closed; `docs/FLS.md` is corrected.
  - **`__System.User.*.Id` columns.** Written outside *both* assembly loops with no
    FLS check at all — dead on the ingest path, live on the direct-converter path
    where `BuildSchemaProperties` unions those names in. Both writes are now gated.
  - **Compound fields — NOT FIXED at this point; the attempted fix was removed.** See
    the "Removed" entry below. Recorded then as an accepted, documented limitation.
    **Superseded by WP-SF-4 above**, which closed it — and which found the WP-SF-3
    description understated it: the component was never evaluated at all, and the
    recommended `flsFields` workaround was itself case-sensitive and silent.
  - **Cross-object relationship fields.** Dotted `selectedFields` keys
    (`Contact.Phone` on Case — 14 of them in the shipped config) were matched
    against `GovernedFields`, which holds only bare own-object names, and so were
    **never FLS-evaluated**. New `FLS_RELATIONSHIP_FIELDS=evaluate|drop|ignore`
    (**default `evaluate`**) resolves each relationship through a new
    operator-declared per-object `relationshipObjects` map in
    `config/schema.json` and evaluates the sub-field against that object's
    permissions; undeclared or unfetched targets **fail closed**. Targets are
    declared rather than inferred because a wrong guess under-drops. Declared
    targets join the existing permission fetch — no new query shape.
- **Zero-row `FieldPermissions` is no longer silent** — a successful query
  returning no rows was indistinguishable from "this org governs nothing", i.e. a
  fail-**open** with no exception to catch. Now recorded as
  `FlsObjectPermissions.FieldPermissionRowsSeen` / `IsSuspectedFailOpen`, logged as
  an ERROR naming the over-sharing risk, and surfaced in the audit manifest as a
  top-level `"suspectedFailOpen"` array (additive; manifest stays version 1).
  Deliberately changes **no** drop decision — treating it as "drop everything"
  would false-drop every field of a genuinely ungoverned org.

### Removed
- **Compound-field FLS propagation (`AclEngine/CompoundFields.cs`) — removed as
  unsound.** It inferred a compound's components **from field names**
  (`BillingAddress` → `Billing`+`Street`/`City`/…, `Name` →
  `Salutation`/`FirstName`/`LastName`/…) and propagated drops in both directions.
  Adversarial verification proved two consequences:
  - **Catastrophic data loss on a shipped config.** On a **Person-Accounts** org,
    `Account.Salutation`/`FirstName`/`LastName` carry real `FieldPermissions` rows,
    so restricting any one of them dropped `Account.Name` for **every account** —
    including business accounts, whose `Name` is unrelated plain text. The account
    name vanished from the Graph properties **and** the searchable content body.
  - **It never delivered the protection it claimed.** The derivation early-returned
    on any name containing `.`, so it never applied to relationship sub-fields.

  Salesforce publishes compound membership authoritatively via **describe**
  metadata; a name-guessing implementation cannot be made correct by patching, so
  the feature was deleted rather than salvaged — the class, the propagation in
  `SalesforceObjectHandler.IsFlsRestricted`, and the component expansion in
  `FlsPolicy.ComputeDrops`. Every field is now judged **only on its own evidence**.

  **Residual, accepted and documented:** a restricted component no longer drops the
  compound, so a hidden `BillingStreet` can still reach the index inside an indexed
  `BillingAddress`. Documented in `docs/FLS.md` (dedicated section + "Known limits")
  and pinned by `FlsCompoundRemovalTests` / `FlsCompoundRemovalPolicyTests`, which
  assert both that `Account.Name` survives a `Salutation` restriction and that the
  residual leak is present rather than silently assumed away. Restoring the feature
  means reading `compoundFieldName` from describe, not extending the name rules.

### Added
- **Windows Event Log sink** (`EVENTLOG_ENABLED=true`, `EVENTLOG_LEVEL=info` opt-in):
  WARNING/ERROR records and service lifecycle events mirrored to the Application
  log (source `SalesforceConnector`, event ids 1000/2000/3000); strict no-op off
  Windows; the sink never throws. `install-windows-service.ps1` now creates the
  event source idempotently. See `docs/SIEM.md`.
- **Proxy + custom CA support**: `PROXY_URL` / `PROXY_BYPASS` explicit proxy
  config (`HTTPS_PROXY` et al. still honored by default), and `CA_BUNDLE_PATH`
  PEM of additional trusted roots for TLS-inspection environments (additive to
  the system store; broken config fails fast naming the setting). Applied to
  every outbound HttpClient. See `docs/DEPLOYMENT_ENTERPRISE.md`.
- **Certificate credential for Graph auth**: `GRAPH_CLIENT_CERT_PATH` (PFX,
  optional `GRAPH_CLIENT_CERT_PASSWORD`) or `GRAPH_CLIENT_CERT_THUMBPRINT`
  (CurrentUser/LocalMachine `My` store) switch token requests to the
  `client_assertion` flow (RS256 JWT, `x5t#S256`, aud = tenant token endpoint,
  jti/nbf/exp). Certificate wins over client secret; mode logged at startup;
  key material never logged. See `SECURITY.md` for rotation.
- **Dead-letter payload protection**: `DEADLETTER_PAYLOAD_MODE=full|redacted`
  (default `full`). Redacted mode strips item property values/content from
  dead-letter records (keeps ids, object type, error, timestamps, ACLs, field
  names, and sha256 hashes of removed values) so CRM PII does not sit on disk;
  the trade-off note is embedded in each record. `retry-failed` is unaffected —
  it re-fetches items from Salesforce.
- New `/metrics` gauges: `adaptive_concurrency_level`, `ha_claims_held`, and
  per-object `object_records_total{object_type}` / `object_records_fetched{object_type}`.
- Ops artifacts: `ops/grafana-dashboard.json`, `ops/prometheus-alerts.yml`,
  `ops/azure-monitor-alerts.kql` (each alert names its `docs/RUNBOOKS.md` anchor).
- Docs: `docs/THREAT_MODEL.md` (STRIDE per trust boundary + FIPS audit),
  `docs/RUNBOOKS.md` (per-alert runbooks), `docs/DR.md` (RPO/RTO, backup/restore,
  upgrade/rollback), `docs/SIEM.md` (Event Log/Sentinel/Splunk ingestion),
  `docs/DEPLOYMENT_ENTERPRISE.md` (SCCM/Intune/GPO, proxy/TLS, least privilege),
  `SECURITY.md` (supported versions, secret rotation, data-at-rest inventory).
- CI: code-coverage gate (line ≥ 47.9%; measured 52.9% at introduction) and a
  perf-smoke job (20k items; floors ≥ 3,000 items/s, < 500 MB RSS). Both live in
  the connector's `ci.yml`, which was deleted in the consolidation; neither
  gates a build today.
- Release: CycloneDX SBOM attached to releases; Authenticode (win-x64 binary)
  and cosign (container image) signing steps, gated on `SIGNING_*` secrets and
  skipped with a notice when absent; experimental WiX v5 MSI job
  (`packaging/msi/`).
- StressHarness `--summary-json FILE` for machine-readable perf results.

### Changed
- `SalesforceCopilotConnector.csproj` now carries `<Version>1.0.0</Version>`.
- All outbound HTTP clients are constructed through `Infrastructure/HttpClientFactory`
  (behavior unchanged when the new env vars are unset).
- **Moved into the `JosephSaad/Connectors` monorepo and onto the shared chassis.**
  The connector consumes `Connector.Chassis` (1.13.1) through a
  `<ProjectReference>` to `../../../Connector.Chassis/Connector.Chassis.csproj`
  — it is not a NuGet package, so there is no version pin, no feed and no
  `nuget.config`. `Chassis` (identity/seams), `ServiceStop` and `SecretProvider`
  now come from the chassis; logging stays connector-local (the CPython-style
  formatter the port depends on) and bridges through `Chassis.LoggerFactory`.
  `DecisionLedger`, `HaCoordinator`, `SqlStateStore`, `SqlExecutor`, `Alerting`,
  `EventLogSink`, `LogPruner`, `ServiceHost` and `EnvFlags` remain this
  connector's own.
- **CI runs from the repository root** — `.github/workflows/salesforce.yml`
  (build + test on `ubuntu-latest` and `windows-latest`, plus a Docker image
  job) is the workflow GitHub executes, alongside `release-salesforce.yml`.
  The connector's own `.github/workflows/*.yml`, inert since the consolidation,
  have been deleted. Suite: 1206 tests, green on both operating systems.
- **Docker builds use the repository root as their build context**
  (`docker build -f SalesforceConnector/Dockerfile .`; compose sets
  `context: ..`), because the project references `../Connector.Chassis` and a
  build cannot reach outside its context. One root `.dockerignore` governs it.

### Security
- **FIPS 140-3: identity-critical MD5 retired.** The field-cache instance key
  (`InstanceHash` in `Graph/IdentityStore.cs` and `Graph/SqlServerIdentityStore.cs`)
  moved from an MD5 prefix to a **SHA-256** prefix. `src/` now contains **no**
  MD5/SHA-1/DES/RC4/3DES call at all; every hash in the connector is SHA-256.
  A source-contract test (`FipsSourceContractTests`) greps `src/` on every run
  and fails the build if a broken primitive reappears.
  - **No schema change.** The output shape is unchanged (8 lowercase hex chars),
    so `field_cache PRIMARY KEY (object_type, instance_hash)`, `PK_FieldCache`
    and `dbo.FieldCache.InstanceHash nvarchar(16)` all stay valid;
    `scripts/sql/create-database.sql` is untouched.
  - **Migration: automatic, no operator action, no data loss.** The field cache
    is a pure cache (it only skips the `INVALID_FIELD` discovery loop). On the
    first crawl after upgrade, rows keyed by the old MD5 value are missed and
    rebuilt under the new key — expect one slightly slower crawl, then steady
    state. Pre-upgrade rows are left in place on purpose (inert, few KB; one
    database may hold live rows for several Salesforce instances, so no
    automatic deletion rule is safe). Optional one-time cleanup: call
    `ClearFieldCache()` with no arguments once after upgrading. See
    `SECURITY.md` and `docs/THREAT_MODEL.md`.

## [1.0.0] - 2026-07-17

First stable release: a complete, state-compatible C#/.NET 10 port of
Microsoft's Python Salesforce → Microsoft 365 Copilot connector, hardened for
production service on Windows Server and Linux.

### Added
- Full command surface: `guide`, `setup-connection`, `full-deployment` (with
  `--continuous` scheduling), `ingest`, `ingest-item`, `ingest-object`,
  `retry-failed`, `identity-dry-run`, `validate-config [--strict]`,
  `reconcile [--type X] [--fix]`.
- Both ACL engines (legacy resolver and the modular `AclEngine/` with OWD,
  share fetcher, group/role/territory/queue handlers, principal mapper) and the
  identity crawl/publisher pipeline.
- Byte-compatible on-disk state with the Python original: sync-state JSON,
  checkpoints, dead-letter JSONL, SQLite identity store — plus a switchable
  SQL Server state backend (`USE_SQL_SERVER`) with schema/procs in `scripts/sql/`.
- Active-active HA (`HA_MODE=true`): SQL-coordinated crawl open/join, atomic
  object claims with heartbeats, dead-node reclaim, exactly-one crawl close.
- Connection sharding (`GRAPH_CONNECTION_SHARDS`), including intra-object hash
  sharding for the Graph item quota.
- Deletion sync: inventory-backed existence sweep with a mass-deletion guard
  (`DELETION_SYNC`, `DELETION_SYNC_MAX_PERCENT`), plus `reconcile --fix` drift
  repair.
- Observability: `/health` `/ready` `/metrics` (Prometheus) via `HEALTH_PORT`,
  `LOG_FORMAT=json` structured logs, webhook alerting
  (`ALERT_WEBHOOK_URL` / `ALERT_DEADLETTER_THRESHOLD`), log retention pruning.
- Stress hardening: adaptive Graph concurrency (dials down on 429s), retry
  jitter (`GRAPH_RETRY_JITTER`), checkpoint/resume at chunk boundaries, a
  5-scenario stress harness (`tools/StressHarness`) with 44 correctness
  invariants wired into CI.
- Diagnosability: silent-drop fixes across the pipeline (dead-letter capture on
  every failure path), per-phase timing logs, run summaries, corrupt
  state-file diagnostics naming file and line.
- Windows service mode (SCM-aware, graceful chunk-boundary stop) with
  `scripts/install-windows-service.ps1`; Docker image and compose file.
- 845-test suite (1:1 port of the Python suite + C# additions), test-gated
  releases with self-contained win-x64/linux-x64 bundles and a GHCR container
  image.

[Unreleased]: https://github.com/JosephSaad/Connectors/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/JosephSaad/Connectors/releases/tag/v1.0.0
