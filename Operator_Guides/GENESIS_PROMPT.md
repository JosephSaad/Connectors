# The Genesis Prompt

A single compiled prompt that — given to an AI coding agent with shell, .NET 10
SDK, git/GitHub access, and sub-agent orchestration — would reproduce this
entire programme: the five connectors, their hardening, the documentation set,
the diagrams, and the programme artefacts. Reconstructed from the actual
session history (the work was done iteratively; this is the one-shot form).

---

## THE PROMPT

You are building a production suite of **Microsoft 365 Copilot Graph
connectors** for a large bank's AI-agent architecture, plus everything an
organisation needs to deploy, operate, staff and govern them. Work
methodically; verify everything yourself; use one sub-agent per connector for
parallel work, and re-verify every agent's build/test claims by running
`dotnet test` yourself before trusting them. Push to GitHub repo
`<owner>/Connectors` as a monorepo of sibling top-level folders.

### 1. Build five connectors (C#/.NET 10, one folder each) on ONE shared chassis

Every connector has its own solution, tests, config, docs, Dockerfile and
LICENSE/NOTICE, and all five consume one shared **chassis** project
(`Connector.Chassis`, v1.18.0) by `<ProjectReference>` — never consumed as a
NuGet package, so no version pin and no feed. (Chassis CI still runs
`dotnet pack` to produce an inspectable artifact; nothing consumes it, and it
is pushed nowhere.) Because each project
references `../Connector.Chassis`, images build with the repository root as
the context (`docker build -f <Connector>/Dockerfile .`), governed by a single
root `.dockerignore`. Sharing is partial and tracked, not estimated. Nine of the chassis's 23 modules
have no local copy anywhere — identity/seams (`Chassis`), `ServiceStop`,
`SecretProvider`, `MetricsRenderer`, `SqlGateway`, `CircuitBreakerRegistry`,
`ConfigException`, `LogRedaction` and `StandardLogDialect`. Alerting and
logging reach four of five apiece (Altrata keeps its own alert sink, whose
`IAlertSink` carries a severity the chassis envelope does not; Salesforce keeps
its CPython-style logging and bridges via `Chassis.LoggerFactory`). The
remaining modules — decision ledger, Event Log sink, HA coordinator, log
pruner, service host and SQL state store at four copies each, circuit breaker
and env flags at three, tracing at two — are still per connector.

That is **67 declared divergences** (Salesforce 22, Clarizen 16, Hadoop 16,
Altrata 13, Seismic 0), each recorded with a reason in
`.github/conformance/divergences.tsv`. The register is a ratchet: CI fails on an
undeclared copy and equally on a declared copy that no longer exists, so the
count cannot drift or quietly grow. Where a connector must keep its own
contract the chassis exposes a seam rather than forking — `Chassis.LoggerFactory`,
`Logging.Dialect`, `Alerting.HandlerFactory`.
The common capability set:

- Unified CLI (`guide`, `setup-connection`, `full-deployment`, `ingest`,
  `ingest-item`, `reconcile`, `retry-failed`, `validate-config`, …)
- Checkpointed full + incremental crawls with crash/stop resume
- Dead-letter queue (JSONL + SQL backend) + `retry-failed`; per-record/
  per-file/per-event failure isolation (one bad input never kills a crawl)
- Graph `$batch` ingest (≤20 requests), adaptive concurrency, exact
  `Retry-After` honouring with a 60-second clamp + jitter, connection sharding
  (`GRAPH_CONNECTION_SHARDS`)
- State store: SQLite by default → SQL Server (offline-validated DDL), with
  checkpoints, item inventory, dead-letter, HA lease tables
- Active-active HA leases (close-with-failed-claims semantics)
- Azure Key Vault secrets; Graph auth via client secret OR certificate
  credential (client_assertion RS256, x5t#S256, PFX/PEM path or Windows store
  thumbprint; cert wins when both set)
- Corporate proxy + TLS-inspection support (`PROXY_URL`, `PROXY_BYPASS`,
  `CA_BUNDLE_PATH` additive PEM trust; hostname mismatch never forgiven)
- `/health` `/ready` `/metrics` endpoints; structured JSON logs; Windows
  Event Log sink (`EVENTLOG_ENABLED`, stable event ids, never throws);
  OpenTelemetry tracing (OTLP, zero-overhead when unset)
- Circuit breakers (closed/open/half-open, clock-injectable) + degraded mode
  pausing at safe checkpoint boundaries; `/ready` 503 while open
- Data classification & sensitivity labelling (Public/Internal/Confidential/
  Restricted + PII/PCI/Secret content classifier with regex timeouts)
- Dead-letter payload redaction (`DEADLETTER_PAYLOAD_MODE=full|redacted` —
  redacted keeps ids/hashes only; retry re-fetches from source)
- SCM-aware Windows service + idempotent install script; WiX v5 MSI packaging
- CI: all workflows live at the repository root (`.github/workflows/`) — one per
  connector plus the chassis, CodeQL, the conformance gate, and the release
  pipeline (one reusable workflow + a caller per connector, on per-connector
  `<name>-v*` tags): CycloneDX SBOM + Authenticode/cosign signing gated on
  secrets; CHANGELOG + semver. The connectors carry no workflows of their own

The five connectors and their signature features:

1. **SalesforceConnector** — live Salesforce CRM via REST + SOQL (Connected
   App, OAuth2 client-credentials). Crown jewel: reproduce the full
   **sharing model** as per-item ACLs — org-wide defaults, role hierarchy,
   sharing rules, public groups, queues, territories, ControlledByParent —
   with group-graph cycle detection, a nesting depth cap (fail-closed), and
   memoised resolution (no exponential blow-up on cyclic graphs). Standard +
   custom objects; sovereign-cloud Graph endpoint override; deletion sweep
   with mass-delete guard. NO circuit breaker by design (adaptive concurrency
   + exact Retry-After is its resilience model).
2. **ClarizenConnector** (Planview AdaptiveWork) — REST v2 + CZQL session
   auth with a daily API-budget governor; TDW warehouse bulk export for full
   crawls; **financial-field governance** (`FINANCIAL_DATA_MODE=tag|filter|acl`,
   including content-mapped fields); HMAC-SHA256 webhooks (validate-before-
   parse, constant-time compare, fail-closed, body/queue caps) with polling
   backstop; attachment extraction with decompression-bomb caps; deletion
   sweep; dual breakers (source-side + Graph-side).
3. **SeismicConnector** — OAuth2 client-credentials; **version-aware ingest**
   (supersede on new version, withdraw on unpublish/expiry, resume
   reconciles rather than blind-skips); **No-MNE exclusion filter**
   (ingest-time exclusion of restricted/MNPI content, config-driven, with
   reconciliation sweep); **permission re-ACL by fingerprint** (ACL-only
   PATCH, never re-sends content, never widens on unresolved identity);
   HMAC webhooks; dependency-free OOXML/PDF/XLSX text extraction; usage-
   signal ranking properties.
4. **AltrataConnector** (wealth/relationship intelligence) — licensed file
   feed (SFTP drop) validated by manifest SHA-256 + count reconciliation;
   delta feeds with tombstones; **seat-only entitlement** (ACLs solely from
   the licensed seat list, "everyone" forbidden, fails closed, seat-hash
   change → re-ACL); **DSAR right-to-be-forgotten** (`forget-subject`:
   suppress-before-withdraw interlock, item↔subject reverse index, durable
   suppression list, tamper-evident hash-chained erasure ledger, compensating
   withdrawals, `unsuppress-subject`); **PII-safe by construction** (names/
   employers only as hashes in logs/traces/review queue — enforce with
   tests); entity resolution with optional fuzzy tier + bounded review queue;
   collision-free ASCII-strict item ids; dead-letter defaults REDACTED here.
5. **HadoopConnector (BDH)** — reads a nightly Salesforce mirror on a Hadoop
   data mart (~24h lag; cheaper than live Salesforce). WebHDFS (delegation
   token never logged) or local mount; Hive-partitioned layout (`dt=`,
   `region=`). Signature: **filter-first design** for 150M+ rows —
   `config/filters.json` per object type with partition pruning + 14 record-
   predicate operators (equals/notEquals/in/notIn/prefix/contains/gte/lte/
   between/withinLastDays/after/before/isNull/isNotNull) as OR-of-AND groups
   + per-object row cap; a **fail-closed full-scan guard** (an object without
   an *effectively pruning* filter refuses to crawl unless `fullScanAllowed`/
   `ALLOW_FULL_SCAN`; `validate-config --strict` errors; guard covers
   targeted lookups too); 24h dt-watermark incrementals (`BDH_LAG_HOURS`);
   oversize-file skips mark the crawl partial and suppress the deletion
   sweep; owner/group/public ACL model (documented trade-off vs live
   Salesforce sharing); item id = Salesforce record Id → MUST use a separate
   Graph connection.

### 2. Harden until nothing is left (iterate: review → fix → re-review)

- Run **adversarial code reviews** (one agent per connector) ranking findings
  by severity with file:line + failure scenario; fix Criticals, then Highs,
  then Mediums/Lows; **re-review after fixing** — expect to find incomplete
  fixes (e.g. a breaker fix that still leaks the half-open slot when a budget
  check throws outside the try).
- Run **stress round 1** (component level, no network, one agent per
  connector): breaker slot accounting on every exit path, HMAC floods,
  decompression bombs, dead-letter concurrency, checkpoint crash-resume,
  $batch caps + 429 ladders, ACL cycle graphs vs brute-force oracles, memory
  bounds at 10⁵–10⁶ scale. Capture REAL numbers; every defect gets a
  red→green regression test.
- Run **stress round 2** (interaction level — where the worst bugs live):
  resume × churn, DSAR forget × in-flight crawl (resurrection windows),
  HA × withdrawal scope, one-poisoned-row × whole-crawl, guard-matrix
  fuzzing across config permutations, ledger tamper detection, unicode/
  normalisation adversaries on id generation, seat churn × shards, watermark
  edge storms. Fix everything; keep every suite green; verify counts
  yourself.
- Run a **diagnosability audit** (one agent per connector): classify every
  catch block (log-with-context / dead-letter / legitimately-silent-with-
  comment / SWALLOWS→fix); failure logs must name the subject (object/item/
  file/partition/shard/attempt) and preserve exception type+stack (JSON log
  mode included); top-level CLI handlers log command+args+stack with exit 1;
  torn dead-letter lines must not kill retry/metrics; never log secrets or
  delegation-token query strings; Altrata additions must stay PII-safe.
  Logging only — no behavioural change.

### 2b. Adversarial assurance — fix, then have someone else try to break it (rounds 7–10)

The reviews and stress rounds above make the code *pass its own tests*; they do
not prove a control *holds*. They don't: two adversarial passes found real
defects in **10 of 10** connector-controls examined while every suite was green
and builds were at 0 warnings. So run further **adversarial assurance rounds**
until each control survives a hostile attempt to break it. Per round:

- **Split fixer from verifier.** One agent FIXES; a separate, INDEPENDENT agent
  VERIFIES, shown only the *claim* — never the fixer's reasoning. Verification
  written by a fix's author cannot see its blind spots (a real example: the
  only test asserting cross-backend agreement compared production against a
  20-line model the fixer wrote himself, and a rollback guard asserted on
  compiled source layout the defect trivially reformatted around).
- **Claim audit first.** The verifier rules every claim TRUE / FALSE /
  OVERSTATED / UNVERIFIABLE with an executed proof *before* probing — a false
  claim about code (an untested fix; a doc guarantee a single flipped byte
  disproves) is invisible to a code probe. This caught 8–10 false/overstated
  claims per late round.
- **Attack with NEW probe code**, never the existing suite. Techniques that
  repeatedly worked here: the **dual-loop trap** (items are assembled twice —
  Graph properties AND the searchable content body — so a gate in one loop
  leaks through the other; probe both); **sentinel probes** (put a unique token
  in a restricted field, assert it appears nowhere in the fully serialized
  item); **mutation testing** (reinstate the exact defect, confirm a test goes
  red; then reformat/relocate it to defeat any source-text or IL assertion);
  **exhaustive, not sampled, sweeps** (a five-value byte alphabet hid a
  backslash-JSON-escape data-loss bug for a whole round — sweep all 256 × every
  offset or state precisely what you skipped); **config-reality checks** (does
  the SHIPPED config actually exercise the fixed path?).
- **Fix the CLASS, not the instance.** Rounds that patched reported instances
  one at a time did not converge (Seismic yielded a 3rd tear shape, then a 4th,
  then a 5th; a null fix was applied to one of six vulnerable properties). The
  controls that closed did so structurally: Salesforce **removed** name-derived
  compound-field inference outright (it dropped `Account.Name` for every account
  on Person-Accounts orgs *and* leaked) and now selects address components
  individually so each carries real `FieldPermissions`; Seismic replaced
  shape-by-shape ledger-tear rescues with **resynchronisation + a byte-prefix
  write discriminator** (an interrupted write can only stop early, never alter a
  byte); Hadoop closed null-handling across **every** config file, not one. The
  recurring lesson: **subtraction beats addition** — every connector that added
  machinery kept a hole; the one that removed a feature closed cleanly first.
- **Mind the failure posture where it matters.** DSAR erasure validates
  operator input at the **entry point only** — reject a malformed (unpaired
  surrogate) or over-long `--id` before any mutation, but *tolerate replayed
  legacy state*; a prior round validated on every write, which wedged the
  forget-subject scrub and left an erasure half-applied, and had to be reverted.
- **Disclose, don't bury.** Carry a **known-open ledger** in every release; a
  revert that re-opens a defect documents it as OPEN in the tree; correct any
  doc that overstates a guarantee. Re-measure every quantitative claim after a
  fix — the number changes when you fix things, and a copied number becomes
  three false claims in three documents.

### 3. Enterprise pack (per connector, uniform names)

`docs/THREAT_MODEL.md` (STRIDE per trust boundary, citing real mitigations,
FIPS audit — leave identity-critical legacy hashes unchanged but documented),
`docs/RUNBOOKS.md` (symptom → diagnose → remediate → escalate for every
alert), `docs/DR.md` (RPO/RTO; state loss = re-crawl cost — except Altrata's
suppression list + ledger, the only non-recrawlable state, RPO-0 tier),
`docs/SIEM.md` (Event Log id contract, Sentinel KQL incl. a delegation-token-
leak canary and ledger-tamper as a security incident, Splunk sketch),
`docs/DEPLOYMENT_ENTERPRISE.md` (SCCM/Intune, GPO/DSC, proxy/TLS-inspection,
FIPS, least-privilege service accounts, read-only source principals),
`SECURITY.md` (rotation runbooks for every credential, data-at-rest
inventory), `ops/grafana-dashboard.json` + `ops/prometheus-alerts.yml` +
`ops/azure-monitor-alerts.kql` using ACTUAL metric names (add metrics where
dashboards need them), `packaging/msi/` WiX. Fleet-level:
`TENANT_GOVERNANCE.md` at repo root — Graph hard limits (30 connections/
tenant, 5M items/connection, 50M/tenant, 128 props, 4MB item, 20/$batch),
per-connector connection/item budgets, one app registration per connector for
throttling isolation, crawl-stagger windows, 80% review trigger, change
control.

### 4. Documentation for non-senior IT operators (Word + PDF)

Written so an operator with NO Graph/OAuth/Copilot background can deploy,
monitor, troubleshoot, support, and know exactly what to request from which
role. One shared formatting library so all documents render identically
(navy/blue identity, metadata title page, confidentiality box, callouts:
Note/Important/Tip/**Information to request**, glossary layout).

- `00_Tenant_and_Common_Concepts_Guide.docx` — teach every shared concept
  once: why connectors exist, Copilot & the semantic index, Graph/external
  connections/items, identity & ACL trimming (the security heart), the
  chassis, security & auth, tenant quotas, deployment, monitoring, DR,
  RACI + master intake checklist, ~40-term glossary where every entry gives
  **what it is** AND **why it matters**.
- One guide per connector (`01`–`05`) — the source system explained, how the
  connector connects (protocols, auth, network), how data + the source
  permission model map into the Copilot index, signature features in depth,
  intake checklist with blank "Value / provided by" column, step-by-step
  setup incl. the two-differently-permissioned-users ACL proof, day-to-day
  operation, monitoring (real metric names), troubleshooting table from the
  runbooks, full env-var reference, source-specific glossary (shared terms
  deferred to the Common Guide — no repetition).
- One-page **quick-reference PDF per connector** + a one-page
  `00_START_HERE.pdf` folder index (what/read-order/universal pre-flight).
- A single-canvas **architecture SVG**: five sources (incl. the nightly
  Salesforce→BDH mirror), five connector boxes with signature features +
  test counts, webhook paths, ingest bus → Graph API → semantic index →
  Copilot → users, Entra identity resolution + query-time ACL trim, tenant-
  governance strip, chassis/observability/Key Vault/state bottom band,
  legend. Embed it in the repo root README.

### 5. Programme artefacts (organisation-ready)

- **Staffing model**: core team (Platform Lead/Architect, .NET engineers,
  M365/Graph engineer, DevOps/Windows, QA, Ops analysts) + fractional bank
  roles (liaisons per source, InfoSec, DPO, network, DBA, change manager);
  RACI across the lifecycle; hire-vs-existing guidance; FTE translation per
  connector and phase (≈7.4 FTE build → ≈4.2 FTE steady state; shared
  platform layer ~22% forever; ops-heaviest in steady state are the
  compliance/scale connectors, not the biggest codebase).
- **Project plan** (indented Excel + MS Project MSPDI XML, forward-pass
  scheduled): phases Initiation/Foundations/Pilot/Waves/Ops-readiness/
  Closure; every task with WBS, duration, FS dependencies (+lags), a
  colour-coded **Gate column** (BANK RESOURCE queues — hiring with notice
  periods, procurement, infra/PKI/identity/firewall/DBA/SIEM/source-admin
  lead times; SECURITY GATE — threat-model queue+review+remediation, pen
  test with pre-booked diary, exposure reviews, per-connector sign-offs,
  DPIA+legal; CAB/CHANGE — ARB cycle, per-connector CAB submissions with
  board-cycle lags, change windows); bank calendar with statutory holidays
  and a **15-Dec→08-Jan blackout where nothing progresses**; pilot proves
  the pattern, wave intakes parallel, wave CABs gate on pilot production
  success; interim contractor covers dev until the permanent hire clears
  their notice period, with explicit knowledge transfer. Adjust scope to
  what the bank will actually implement (e.g. defer the live-Salesforce
  connector when BDH carries the same data; keep a revisit checkpoint).

### 6. Working practices (non-negotiable)

- One sub-agent per connector for builds/reviews/stress/audits/docs; run
  them in parallel; when an agent dies mid-flight (API drop, session limit),
  resume it from its transcript rather than restarting.
- **Never trust an agent's claim**: re-run the full test suite yourself
  after every wave; investigate any flake until deterministic (loopback
  port TOCTOU class); keep builds at 0 warnings.
- Two working copies: standalone clones for agent work, rsync (excluding
  .git/bin/obj/logs/data) into the monorepo for commit; keep README test
  counts current everywhere (root table + per-connector); repo About
  up to date; push at each explicit checkpoint.
- Every defect fix ships with a regression test proven red→green. Every
  bounded scope (top-N, sampling) is logged, never silent.
- **Never trust an agent's *claim* either, not just its build.** For hardening
  work, separate the fixer from an independent verifier shown only the claim;
  run a claim audit (TRUE/FALSE/OVERSTATED) before any code probe; mutation-test
  every fix (a guard the defect can reformat around is not a test); sweep
  exhaustively rather than sampling; and treat an overstated doc or a
  copied-not-measured number as a defect.

---

*Compiled 18-Jul-2026 from the session that produced: 5 connectors,
3,389 green tests, 3 stress rounds + a try/catch review & crash-safety round (incl. a CRITICAL DSAR resurrection
window), a 74-fix diagnosability audit, the enterprise pack, a bank-grade
hardening round (12 prioritized fixes: safe-default flips — dead-letter
redacted, entitlement re-sync on incrementals, Clarizen financial=filter,
Seismic MNPI fail-closed, webhook signed-timestamp anti-replay — plus opt-in
classification ACL enforcement, item TTL, purpose-of-use authorization and a
tamper-evident decision ledger), 12 operator documents, the architecture SVG,
the FTE model and the bank-realistic programme plan.*

*Refreshed 20-Jul-2026. Since genesis the session ran **adversarial assurance
rounds 7–10** (fix → independent hostile verify → claim audit → mutation test →
re-attack), taking the fleet from 3,389 to **4,824 green tests at 0 warnings** (as of that wave; the fleet now stands at **5,445** — see the consolidation section)
(Salesforce 1,206 · Clarizen 877 · Seismic 1,017 · Altrata 743 · Hadoop 981).
Defects were closed as CLASSES: Salesforce field-level security with
name-derived compound inference **removed** (components indexed individually);
Clarizen schema-drift enforcement moved to the write path (an undeclared
property aborts the crawl with zero PUTs) and unscannable binaries stamped
`incomplete:`, never `clean`; Seismic decision-ledger tear shapes closed by
resynchronisation with a byte-prefix discriminator, plus a Seq-range guard —
the guarantee is "nothing is ever lost quietly", not "nothing is ever lost";
Altrata DSAR subject-id validation added at the erase-subject entry point (after
reverting a write-side attempt that wedged the scrub), with the surrogate/length
and connector-id divergences documented OPEN; Hadoop null-handling closed across
all four config files and the `validate-config --strict` verdict made invariant
under the `CLASSIFICATION` flag, with a produced-but-undeclared Graph property
now a preflight ERROR. Documentation refreshed to match: operator guides v1.2,
release notes v2.0, quick-reference PDFs, architecture SVG v4. Each connector
carries a disclosed known-open ledger rather than a silent gap.*
