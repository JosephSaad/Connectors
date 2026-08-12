# Changelog

All notable changes to the Clarizen Copilot Connector are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versioning follows [SemVer](https://semver.org/).

## [Unreleased]

Enterprise hardening package.

### Fixed
- **A blank Graph property name is now as loud as an undeclared one.**
  `GraphPropertyRegistry.AssertDeclared` rejected a blank/whitespace-only name
  with `ArgumentException`, not `UndeclaredGraphPropertyException` — so the
  crawl's per-record `catch(Exception)` demoted it to a dead-lettered bad row.
  The crawl then **closed normally and advanced the sync cursor past records
  that never reached the index**, which is exactly the failure shape the
  write-path enforcement claims to have closed. Observed with
  `selectedFields = {"Name": ""}` over 4 records: no crawl exception,
  `ingested=0 failed=4 failedObjects=0`, 0 Graph calls carrying a body,
  `lastSync written = True`, 4 records dead-lettered. Two layers fixed:
  - the blank check now raises `UndeclaredGraphPropertyException`, so the crawl
    aborts (`lastSync` unwritten, nothing dead-lettered, no PUT, no `$batch`);
  - `SchemaConfig.Validate` now rejects a blank `selectedFields` property value
    at config load, so a config that cannot possibly work never starts a crawl.
    The existing reserved-name rejection is unchanged and still fires.

  Existing mitigations are preserved: `validate-config` still reports it (via
  the generic "Could not determine which Graph properties the connector stamps"
  error, because the blank check deliberately runs ahead of the inventory's
  enforcement-suspension scope), and `summary.Failed > 0` still makes
  `IngestObject` return false.
- **The same "fatal config error disguised as a bad row" existed for the
  declaration load itself** (found by the audit the blank-name fix prompted). A
  `config/graph-schema.json` that is missing, is not a JSON array, is
  unparseable, or declares no usable names throws from inside `AssertDeclared` —
  i.e. on the per-record stamp path. Observed with an override pointing at `[]`
  over 4 records: no crawl exception, `ingested=0 failed=4`,
  `lastSync written = True`. Those loads are now wrapped in a new
  `GraphSchemaUnavailableException`. Both it and
  `UndeclaredGraphPropertyException` derive from a new
  `GraphSchemaConfigurationException`, and `IngestPipeline`'s two isolation
  catches key on that base — so the whole class escalates rather than a single
  leaf type. `GraphPropertyRegistry.ReadDeclaredNames` is unchanged and still
  throws `InvalidDataException` for `validate-config`'s benefit.
- **`ExternalItem.ToJson`'s documented layer-2 guarantee is now true.** Inside
  `GraphPropertyRegistry.SuspendEnforcement` (internal, so reachable from
  anywhere in the assembly) `ToJson` serialized an undeclared property with no
  error: `item.Properties["HV10_SENTINEL_TOJSON"] = "HV10_LEAK"` produced
  `{"id":"x","properties":{"HV10_SENTINEL_TOJSON":"HV10_LEAK"},...}`. `ToJson`
  now calls a new `AssertDeclaredIgnoringSuspension`, so the re-check holds
  regardless of suspension. Safe because the only production caller of
  `SuspendEnforcement`, `StampedPropertyInventory.Capture`, reads property
  *names* off the item and never serializes or sends it — its behaviour is
  unchanged and still observes undeclared stamps. Not live data escape before
  the fix, for the same reason; it was a documentation overstatement plus a
  defence-in-depth gap.
- **An undeclared property stamp is no longer demoted to a bad source row.**
  `GraphPropertyBag` already threw `UndeclaredGraphPropertyException` at the line
  that stamped an undeclared name, from any call site. But the crawl's per-record
  and per-object isolation catches treated that throw exactly like a poisoned
  Clarizen row: dead-letter it, continue. So a stamp added anywhere outside the
  five call sites `StampedPropertyInventory` invokes left the build-time drift
  test **and** the `validate-config` preflight green while the crawl
  dead-lettered 100% of records and closed as a completed run with an empty
  index. Proven by mutation — adding
  `item.Properties["CrawlBatchIdProbeB"] = chunkIndex.ToString();` to the
  per-record crawl loop in `Graph/Ingest.cs` gave
  `ingested=0 failed=1 puts=0 deadletter=1` with the drift probes at
  `Failed: 0, Passed: 5` and no preflight finding at all.
  `IngestPipeline` now rethrows `UndeclaredGraphPropertyException` from both
  isolation catches, so the run **aborts naming the property**. The property is
  never silently dropped and no item ships without it — the failure is a throw,
  not a skip. Ordinary per-row and per-object isolation is unchanged.
  Scope note: the enforcement is the write path, which needs no registration for
  a new stamp to be covered. `StampedPropertyInventory` is retained only as an
  earlier, best-effort warning and is now documented as such rather than as the
  guard; no replacement inventory was introduced.
- **A degenerate `graph-schema.json` is now a preflight error, not silence.**
  `AddSchemaDriftFindings` wrapped both file loads in a blanket
  `catch { return; }` justified by the claim that a load failure "was already
  reported as a parse/validation error above". For a `graph-schema.json` of `[]`
  — or one whose entries all have an empty `name` — it was not: the earlier
  array/field checks accept those, so the swallowed `InvalidDataException`
  produced no finding and `validate-config --strict` reported
  `errors=[] warnings=[] Ok(strict)=true` for a file that makes the first
  property stamp of the crawl throw and every record dead-letter. The two loads
  are now caught separately and each one **checks** that the earlier block
  actually recorded a finding for that file before staying quiet; when it did
  not, the drift check records the error itself. Verified not to double-report on
  inputs the earlier checks do catch (unparseable JSON, non-array).
- **Schema drift: an undeclared Graph property can no longer be stamped at all.**
  The round-6 drift guard compared `config/graph-schema.json` against two
  hand-curated symbol lists. A property stamped with a **bare string literal** —
  the exact coding style that caused the original `ContentGateStatus` defect —
  appeared in no list, so the guard stayed green while the property travelled all
  the way into the Graph `PUT` body and shipped undeployable. Proven by mutation:
  stamping an undeclared literal in `ItemConverter.Convert` left the guard
  reporting `Failed: 0, Passed: 5`. A guard that depends on a developer
  *remembering* to register a name cannot catch the mistake of not registering a
  name, so it is closed structurally rather than by extending the list:
  - `ExternalItem.Properties` is now a **`GraphPropertyBag`**, not a
    `Dictionary`. Every write goes through `GraphPropertyRegistry`, which reads
    the declared names from `config/graph-schema.json` — the same file that is
    `PATCH`ed to Graph, so the registry cannot drift from what Graph accepts. An
    undeclared name throws `UndeclaredGraphPropertyException` **at the line that
    stamped it**, naming the property. Literal, `const` or runtime-computed makes
    no difference: the check is on the value of the name, not its provenance.
  - `ExternalItem.ToJson()` re-checks the whole bag as defence in depth, so any
    future route that assembles properties some other way still cannot produce a
    body Graph would reject. Both layers were verified independently
    load-bearing by neutering each one in turn.
  - The guard's own enumeration (`StampedPropertyInventory`) **executes** stamper
    call sites over a synthetic record instead of reading a symbol list, and
    takes the **union across every `FINANCIAL_DATA_MODE`** and both icon variants
    — `filter` mode removes the financial properties the converter just stamped,
    so a single-mode pass would under-report them. It reads nothing from the
    environment, so the answer is identical on every host.
    **Corrected in round 9:** this entry originally claimed the inventory
    executes "every stamper". It does not — it invokes a hard-coded set of call
    sites, which is a maintained list of a different shape, and it is an early
    warning rather than the guarantee. See the round-9 entry below.
- **Schema drift is now a runtime preflight, not only a build-time test.** The
  comparison existed **only** in the test suite, so an operator running
  `validate-config --strict` against a hand-edited `graph-schema.json` on a
  deployment host got no drift signal whatsoever — the first symptom was Graph
  rejecting items mid-crawl. `ValidateConfig.ValidateCore` now reports
  stamped-but-undeclared as an **error** and declared-but-unstamped as a
  **warning** (promoted to failure under `--strict`).
- **The drift guard's self-test was decorative and has been replaced.** `D4`
  mutated a local `HashSet` and asserted set arithmetic on it — it exercised no
  production code and could not fail however broken the guard was, which reads as
  coverage while providing none. It now drives a genuinely undeclared property
  through the real property bag and asserts the connector refuses it.
- **ContentGate: a disabled gate can no longer stamp a verdict.** Latent, not
  reachable in production: passing a `ContentGateStage` to `IngestPipeline` while
  `CONTENT_GATE=false` made `ScanItem` return its no-op `Pass`, which the
  pipeline would stamp as `ContentGateStatus=clean` on every item with **nothing
  having been scanned**. `ContentGateStage.Stamp` now takes the enabled state as
  a **required** argument and throws when it is false (for every outcome, not
  just `Clean`), the instance form `StampVerdict` takes it from the stage itself,
  and the pipeline's item-level pass returns early for a disabled stage.
- **ContentGate: a second incomplete reason no longer replaces the first.** An
  item carrying `incomplete:malware-unscannable` whose later item-level scan was
  *also* incomplete had the first category overwritten. The value stayed
  `incomplete:`, so there was never false assurance, but the operator lost which
  reason applied. Both are now kept —
  `incomplete:malware-unscannable+injection.scan_truncated` — de-duplicated and
  in first-appearance order. A **blocked** item scan still overrides a carried
  incomplete, since blocked is strictly more severe and quarantines the item.
- **ContentGate: `CONTENT_GATE_MAX_SCAN_MB` now means bytes, as documented.**
  The cap was derived from a byte budget but compared against `text.Length` in
  **characters**, so multibyte UTF-8 content was scanned up to ~3x the configured
  MiB. Permissive in size only — it could not mis-label a verdict, because text
  past the cap is reported `incomplete:injection.scan_truncated` and never
  `clean`. The budget is now divided by the worst-case UTF-8 cost of one UTF-16
  char; `docs/CONTENT_GATE.md` records the consequence for ASCII text.

### Fixed
- **ContentGate: `ContentGateStatus` is now declared in the Graph connection
  schema.** The gate stamped `ContentGateStatus` on every item, but the property
  was missing from `config/graph-schema.json`. Microsoft Graph rejects
  properties that the registered connection schema does not declare, so the
  entire content-gate feature was **undeployable as shipped** — and invisibly
  so, because nothing compared what the code stamps against what the schema
  declares. The property is now declared (`String`, retrievable / queryable /
  refinable, matching the sibling `AttachmentExtractionStatus`), and a **drift
  guard** test compares the declaration against the properties the connector
  stamps. *Correction (see the schema-drift entry above): as originally written
  that guard enumerated two hand-curated symbol lists
  (`SchemaConfig.ReservedPropertyNames`, `ItemConverter.StandardPropertyNames`)
  plus the non-`_cz_` `selectedFields` mappings, so it could only see names
  somebody had remembered to register — a property stamped with a bare string
  literal was invisible to it. The claim that it enumerated **every** property
  the connector can stamp was not true. It is now.*
- **ContentGate: unscannable BINARY content is no longer stamped `clean`.** The
  binary half of the "fail-open must not report clean" defect survived the
  text-half fix: with `CONTENT_GATE_FAIL_MODE_BINARY=open`, an attachment whose
  bytes no scanner ever read (AV outage, no `CONTENT_GATE_ICAP_URL`, or over
  `CONTENT_GATE_MAX_SCAN_MB`) returned `GateVerdict.Pass`, so the item was
  indexed **and reported clean**. It now returns `Incomplete`, exactly as the
  text path does: the attachment enricher stamps
  `ContentGateStatus=incomplete:malware-unscannable`, and the later item-level
  text scan — which cannot know anything about the unscanned bytes — no longer
  overwrites that verdict with `clean`. The documented fail mode is unchanged:
  binary still fails **closed** by default (quarantine), fail-open still indexes
  and still increments `content_gate_scan_unavailable_total{kind="binary"}`.
- **ContentGate: oversize text is no longer stamped `clean`.** Content larger
  than `CONTENT_GATE_MAX_SCAN_MB` was scanned as a truncated prefix and, finding
  nothing there, reported **clean** — a false assurance about a tail nobody had
  looked at, and a bypass at a fully supported setting (drop the payload past
  the cap). A truncated scan is now **incomplete**, reusing the same fail-mode
  machinery as the blind-scanner path: `CONTENT_GATE_FAIL_MODE_TEXT=open`
  indexes it but stamps `ContentGateStatus=incomplete:injection.scan_truncated`,
  logs a warning and increments
  `content_gate_scan_unavailable_total{kind="text"}`; `closed` quarantines it.
  A hit *inside* the scanned prefix still blocks — truncation only affects the
  absence of evidence. `ContentGateStatus` gains the `incomplete:<category>`
  value; `clean` now means "read in full and nothing matched".
- **ContentGate: a source field mapped onto a reserved property name no longer
  escapes the item scan.** `ScanItem` delegated to the classifier's scannable-
  text definition, which deliberately skips `SensitivityLabel` and
  `DetectedCategories` (its own outputs). A `selectedFields` mapping onto either
  name therefore published source-controlled text as grounding context that the
  gate never saw. The gate now scans **every** string / string[] property
  (`ScanText(item, includeTaxonomyProperties: true)`) — the classifier's own
  exclusion is unchanged — **and** `config/schema.json` now rejects a mapping
  onto any connector-computed property name (`SensitivityLabel`,
  `DetectedCategories`, `DataClassification`, `ContainsFinancialData`,
  `ContentGateStatus`, `AttachmentExtractionStatus`) as a hard load error.
- **`retry-failed`: item ids with an underscored object type parse correctly.**
  `ParseItemId` split `{ObjectType}_{LocalId}` on the FIRST underscore, so a
  Clarizen custom entity such as `Custom_Entity_1234567` yielded object type
  `Custom` and local id `Entity_1234567` and the retry re-fetched a record that
  does not exist. The split is now bounded by the schema-resolved object name
  when known, falling back to the LAST underscore.

### Added
- **ContentGate stage (CS-1)** — `CONTENT_GATE` (default **off**). Ingested
  content is Copilot grounding context, so a malicious document is an attack on
  every user whose query it grounds. Two independent scanners behind one stage:
  a config-driven prompt-injection heuristic (`config/content-gate.json`:
  imperative overrides, role reassignment, exfiltration directives, zero-width /
  bidi hidden text, long base64 runs) over the **final indexed text**, and an
  ICAP/HTTP malware scanner (`CONTENT_GATE_ICAP_URL`) over attachment
  **binaries**. Patterns are compiled once with a per-pattern match timeout;
  a timeout **fails safe** (suspicious/incomplete), never "no match".
  Quote-aware matching keeps prose that merely *quotes* an attack phrase clean.
  Posture is **quarantine, not drop**: a positive verdict withholds the item
  from the index, dead-letters it with reason `content-gate:<category>`, writes
  a new `quarantine` decision-ledger kind, stamps `ContentGateStatus`,
  increments `content_gate_blocked_total{category}` and raises the alert
  webhook — and `retry-failed` re-drives it. Gated on both per-item entry
  points, so the webhook re-ingest path is covered too. Fail modes are
  deliberately asymmetric: binary **closed**, text **open** with a loud warning
  and `content_gate_scan_unavailable_total{kind}` (configurable via
  `CONTENT_GATE_FAIL_MODE[_BINARY|_TEXT]`). With `CONTENT_GATE` unset the
  connector is byte-identical to before: no scanning, no new properties, no
  metric families, no cost. `docs/CONTENT_GATE.md`.
- **Windows Event Log sink** (`EVENTLOG_ENABLED`, `EVENTLOG_LEVEL=info`):
  mirrors Error/Warning (and opt-in Info) plus service start/stop lifecycle to
  the Application log, source `ClarizenConnector`, stable event ids
  1000/1001/1002/2000/3000. No-op off Windows; the sink never throws.
  `install-windows-service.ps1` creates the source idempotently. `docs/SIEM.md`.
- **Proxy + custom trust roots** for every outbound HTTP client (Clarizen,
  Graph, alert webhooks): `PROXY_URL`, `PROXY_BYPASS`, `CA_BUNDLE_PATH`
  (additive PEM roots for TLS-inspecting proxies). Invalid values fail fast at
  startup naming the setting. `docs/DEPLOYMENT_ENTERPRISE.md`.
- **Certificate credential for Graph auth**: `GRAPH_CLIENT_CERT_PATH`
  (+`GRAPH_CLIENT_CERT_PASSWORD`) or `GRAPH_CLIENT_CERT_THUMBPRINT` (Windows
  store) build an RS256 `client_assertion` (x5t#S256, aud/jti/nbf/exp) instead
  of the client secret; the certificate wins when both are set. The auth mode
  is logged, key material never is. `SECURITY.md` has the rotation runbook.
- **Dead-letter payload protection**: `DEADLETTER_PAYLOAD_MODE=redacted`
  strips property/content values and response bodies from dead-letter records
  (ids, object type, error and per-field SHA-256 hashes are kept), covering
  the financial-classification paths; `retry-failed` re-fetches from source so
  redaction never reduces retryability. Unknown mode values fail fast.
- **HA lease gauge**: `clarizen_connector_ha_claims_held` on `/metrics`.
- **CI**: coverage job with an enforced line-coverage floor (72%, from 77.1%
  measured) and a perf-smoke job running both stress-test classes on a
  generous wall-clock budget.
- **Release**: CycloneDX SBOM attached to releases; Authenticode + cosign
  signing steps gated on repository secrets (skipped gracefully when absent);
  experimental WiX v5 MSI (`packaging/msi/`) built on a windows runner with
  ServiceInstall/ServiceControl and Event Log source registration.
- **Docs**: `docs/THREAT_MODEL.md` (STRIDE per trust boundary + FIPS audit),
  `docs/RUNBOOKS.md` (per alert/failure mode), `docs/DR.md` (RPO/RTO, backup/
  restore, upgrade/rollback, state-schema versioning), `docs/SIEM.md`
  (Event Log ids, Sentinel KQL, Splunk sketch), `docs/DEPLOYMENT_ENTERPRISE.md`
  (SCCM/Intune, GPO/DSC, proxy/TLS, least privilege), root `SECURITY.md`
  (supported versions, rotation runbooks, vuln reporting, data-at-rest
  inventory).
- **Ops artifacts**: `ops/grafana-dashboard.json`,
  `ops/prometheus-alerts.yml`, `ops/azure-monitor-alerts.kql` matching the
  RUNBOOKS anchors.

### Changed
- `SECRET_AAD_APP_CLIENT_SECRET` is now required only when no Graph client
  certificate is configured.
- Test suite grown from 516 to 575+ offline tests; two xUnit analyzer warnings
  in existing tests fixed (build is warning-clean on full rebuild).
- **Shared chassis.** The connector now consumes `Connector.Chassis` (1.13.1)
  via `<ProjectReference>` to
  `../../../Connector.Chassis/Connector.Chassis.csproj` for its identity/seam
  init, `ServiceStop`, logging, secret provider, SQL executor/gateway and
  metrics renderer; the connector-local copies of those were removed. It is
  **not** a NuGet package — no `PackageReference`, no version pin, no feed, and
  the connector's `nuget.config` (which hardcoded an absolute local path) was
  deleted. The decision ledger, HA coordinator, SQL state store, alerting,
  Event Log sink, log pruner, service host, env flags and circuit breaker
  remain connector-local.
- **CI moved to the repository root.** GitHub only executes workflows found in
  the root `.github/workflows/`; the Clarizen job is `clarizen.yml` there
  (build + test on ubuntu-latest and windows-latest, plus a Docker image
  build). The `ci.yml`, `codeql.yml` and `release.yml` files under
  `ClarizenConnector/.github/workflows/` are inert leftovers from when this
  connector was its own repository and are left in place untouched.
- **Docker builds from the repository root.** Because the project references
  `../Connector.Chassis` and a build cannot reach outside its context, the
  image is built as `docker build -f ClarizenConnector/Dockerfile .` from the
  repo root; `docker-compose.yml` sets `context: ..`. A single root
  `.dockerignore` governs the build (the per-connector one was removed).
- Test suite at 878 offline tests, green on ubuntu-latest and windows-latest.

### Security
- FIPS audit: no MD5/SHA1/DES/RC4/3DES anywhere in the codebase; HMAC-SHA256
  webhook validation and SHA-256 hashing throughout (see
  `docs/THREAT_MODEL.md` § FIPS).

## [1.0.0] - 2026-07-17

Baseline release: the complete connector as shipped before the enterprise
hardening package.

### Added
- Clarizen REST v2 client (session auth, CZQL paging, transparent re-login,
  daily API budget/rate limiter) and TDW bulk-export reader for full crawls.
- Graph external-connection provisioning, `$batch` ingest with adaptive
  concurrency, 429-hardened retry/backoff/jitter (60 s clamp), checkpointed
  resumable crawls, dead-letter queue + `retry-failed`, deletion sync with
  mass-deletion guards, `reconcile [--fix]`.
- ACL engine: Clarizen users/groups/project membership resolved to Entra
  principals; `projectMembers` / `ownerOnly` / `public` modes; fail-closed
  zero-principal skip; `FALLBACK_ACL_GROUP_ID`.
- Financial-field governance (`FINANCIAL_DATA_MODE=tag|filter|acl`), unified
  classification + sensitivity labeling, attachment content ingestion
  (dependency-free extraction, size/type caps).
- Webhook receiver (HMAC-SHA256 validate-before-parse, fail-closed secret,
  1 MiB body cap, debounce/coalesce) for event-driven incremental.
- State backends: files/SQLite or SQL Server (`USE_SQL_SERVER`), active-active
  HA (`HA_MODE`) with leased object claims, connection sharding.
- Operations: health/ready/metrics endpoints (Prometheus), webhook alerting,
  OpenTelemetry tracing + correlation ids, circuit breakers + degraded mode,
  structured JSON logs, log pruning, Key Vault secrets, Windows-service mode,
  Docker/compose, CI (ubuntu + windows + live SQL provisioning + CodeQL) and
  test-gated releases with checksummed bundles + GHCR image.
- 516 offline tests (mock HTTP, no network).

[Unreleased]: https://github.com/cloudsconnected/clarizen-connector/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/cloudsconnected/clarizen-connector/releases/tag/v1.0.0
