# Connector.Chassis — consolidation status

Shared infrastructure project consumed by all five custom Microsoft 365 Copilot connectors via
`<ProjectReference>` to `../../../Connector.Chassis/Connector.Chassis.csproj`. Current version:
**1.20.0**.

The chassis owns the **mechanism**; connectors keep their **domain vocabulary**. Not every
infrastructure file is consolidated — components that diverged into different-but-correct
implementations are kept connector-side by design (see "Accepted divergences"). There is no
package, feed or pinned version to keep in step: every connector compiles against the same
chassis sources in the tree, so version drift between connectors cannot happen.

## Shared components (in the chassis)

Consumed from the chassis, with the number of connectors on the shared version:

| Component | Connectors |
|---|---|
| Chassis (identity/seams) · ServiceStop | all 5 |
| Logging (+`IAppLogger`, `LogLevel`) | 4 — Salesforce keeps its own |
| SecretProvider | 4 — Altrata keeps its own |
| SqlExecutor (static) · MetricsRenderer | 3 — Seismic, Clarizen, Hadoop |
| SqlGateway (instance `ISqlGateway`) | 2 — Clarizen, Hadoop |
| Tracing | 2 — Seismic, Altrata |
| StandardLogDialect | default for Clarizen and Hadoop; Altrata supplies a custom dialect |
| EnvFlags | all 5 — the last connector copies were retired (see below) |
| ServiceHost (+`CommandWorker`) | all 5 — the four local copies were retired (see below) |
| LogLevels · LogRecord | all 5 — Salesforce's copies were byte-identical (see below) |
| EventLogEntryKind | all 5 — Clarizen's copy was byte-identical (see below) |

### Single-consumer modules — in the chassis, but shared by nobody

Nine chassis modules have exactly **one** consumer: Seismic. They are in the chassis because the
chassis is largely Seismic's extracted infrastructure, not because the fleet adopted them. The
distinction matters for a reason that is easy to miss: a change to one of these is validated
against **one** connector, while its location implies fleet-wide validation.

| Module | Consumers | Keep in the chassis because |
|---|---|---|
| `HttpTransport` | Seismic — **plus** every host of `Alerting` | Not single-consumer in effect: `Alerting` (4 connectors) falls back to `HttpTransport.CreateHandler` when `HandlerFactory` is unset. Removing it leaves `Alerting` with no default transport. |
| `CircuitBreakerRegistry` | Seismic — **plus** every host of `MetricsRenderer` | Not single-consumer in effect: `MetricsRenderer` (3 connectors) calls `CircuitBreakerRegistry.All` directly, not through a hook. Removing it breaks `/metrics` for Clarizen, Seismic and Hadoop. |
| `DecisionLedger` | Seismic | The chassis ledger is the more *robust* implementation and is the migration target if a connector's own ledger ever needs replacing. Covered by chassis tests. |
| `LogPruner` | Seismic | The chassis version is the superset (195 lines against Altrata's 101 — run-dir regex, `RetentionDays`, ledger-file pruning). It is what a connector would migrate **to**. Covered by chassis tests. |
| `CircuitBreaker` (+`Options`/`State`/`Exception`) | Seismic | Functional `ExecuteAsync` paradigm; the reference the three imperative copies are measured against. Covered by chassis tests. |
| `EventLogSink` | Seismic | The `LogHandler`-instance model the four static copies are measured against. |
| `HaCoordinator` | Seismic | The per-crawl claim model the per-object and async copies are measured against. |
| `SecureDirectory` | Seismic | Reference for the renamed equivalents (`DirectoryHardening`, `SecureDirectories`). |

Deleting these would not remove a line of duplicated code — it would only remove the thing the
duplication is measured **against**, and the gate detects a local copy by collision with a type
the chassis declares. With no chassis declaration there is nothing to collide with, and the
register goes quiet while five copies persist. That is precisely the failure the gate's own
header records: *"with nothing asserting ABSENCE, the gate was green while 70 local copies sat in
four connectors."*

**`SqlStateStore` was the exception, and has been moved out.** It failed every test above: one
consumer, **zero** chassis tests, and no reachable migration target — all four other connectors
have recorded, permanent reasons not to adopt it (two need the injectable `ISqlGateway` seam their
tests mock, Altrata's carries DSAR-suppression and billable-lookup operations, Salesforce's is
bound to its own stored-procedure contract). A reference implementation nobody can migrate to is
not a reference. It now lives in `SeismicConnector/Config/SqlStateStore.cs`, and the five
implementations are tracked as `kind=duplicated` so the duplication stays counted rather than
vanishing with the chassis declaration.

Present in the chassis but **outside** the consolidated surface — the per-type migrations left
these connector-side: DecisionLedger · HaCoordinator · Alerting · EventLogSink · LogPruner ·
CircuitBreaker (+Registry).

### EnvFlags: why this one was not an accepted divergence

The other rows below are different-but-correct. EnvFlags was not: Clarizen, Hadoop and
Salesforce were carrying the **pre-hardening** parser. "EnvFlags: one boolean vocabulary, and a
typo can no longer flip a gate" (#56) fixed `Connector.Chassis` and nothing else, so the two
failures that commit reproduced were still live in the three connectors that read their own
copy — `CLASSIFICATION_ENFORCE_ACL="true "` disabling ACL enforcement while `validate-config`
reported success, and `CIRCUIT_BREAKER=on` leaving every breaker in passthrough.

That is the case for consolidation the general argument lacks: a security-relevant fix had to
be written once and reached two connectors of five. The copies are gone, the connector-specific
remainder lives under a connector-specific name (`SalesforceFlags`), and the behaviour is pinned
by `BooleanVocabularyRegressionTests` in Clarizen and Hadoop, each of which fails against the
code they replaced.

## Consumption by connector

| Connector | Consumes from chassis | Migration mechanic |
|---|---|---|
| **Seismic** (reference) | The whole chassis surface | Global `using Connector.Chassis` |
| **Clarizen** | ServiceStop, SecretProvider, Logging (+IAppLogger, LogLevel), MetricsRenderer, ISqlGateway + SqlGateway (aliased as `SqlExecutor`) | Per-type `<Using Alias>` — deletes the local copy with zero call-site edits |
| **Hadoop** | ServiceStop, SecretProvider, Logging (+IAppLogger, LogLevel), MetricsRenderer, ISqlGateway + SqlGateway (aliased as `SqlExecutor`) | Per-type `<Using Alias>` |
| **Altrata** | ServiceStop, Logging (+IAppLogger), Tracing | Per-type `<Using Alias>`; supplies its own `AltrataLogDialect` |
| **Salesforce** | ServiceStop, SecretProvider | Per-type `<Using Alias>`; keeps its CPython-style logging and bridges it in via `Chassis.LoggerFactory` |

Superset design keeps every connector's behaviour byte-identical: **Logging** and
**MetricsRenderer** carry each connector's format/series behind a mode switch + hooks
(`HardenDirectoryHook`, `EventLogMirrorHook`, `CircuitBreakerRenderHook`, `TracingEnabledHook`,
`Chassis.CorrelationIdProvider`, `Chassis.LoggerFactory`) so the chassis references no connector
type. **SqlGateway** is Clarizen's mockable text-command seam living beside Seismic's static
`SqlExecutor`.

## Accepted divergences (kept connector-side, by design)

These are **not** drift — they are different-but-correct implementations where consolidation
would be large churn (call-site rewrites + test re-authoring) for no security or correctness
gain. Recorded here so the decision is explicit and auditable. The reasoning below is written
against Clarizen, which was the first per-type migration; Hadoop, Altrata and Salesforce keep
their own copies of the same components for the same reasons, plus the two cases listed at the
end of the table.

| Component | Why kept local |
|---|---|
| **DecisionLedger** | Already SHA-256 hash-chained + tamper-evident (equivalent security posture to the chassis ledger; the chassis's is only more *robust*). Decision vocabulary (exclusion/acl_restriction/quarantine) is connector-specific. |
| **CircuitBreaker** | Different paradigm — imperative `TryAcquire`/`OnSuccess`/`OnFailure` vs the chassis's functional `ExecuteAsync` wrapper. Incompatible APIs, no gain. |
| **Breakers** | Connector-specific facade naming Clarizen's own dependencies (Clarizen API, Graph); analogous to Seismic's `SeismicBreakerNames`. Feeds the chassis `Metrics` via `CircuitBreakerRenderHook`. |
| **HaCoordinator** | Injected-`ISqlGateway` design (mockable) — arguably better than the chassis's static-SQL version; adopting the chassis one would *regress* testability. |
| DeadLetterRedactor, EnvLoader, DirectoryHardening, EventLogSink, HttpClientFactory, LogPruner, Tracing, CorrelationContext, HealthEndpoint | Divergent-but-correct or connector-specific; not a clean shareable win. Wired to the chassis where needed via the hooks above. |
| **Logging** (Salesforce) | Deliberately mirrors CPython `logging` semantics (console at WARNING+ unless `--verbose`, log file always at all levels), because the connector is a port of the Python original. Kept local; chassis components resolve loggers through it via `Chassis.LoggerFactory`. |
| **SecretProvider** (Altrata) | Instance type behind `ISecretProvider` with an injectable Key Vault fetch seam, vs the chassis's `static class SecretProvider`. Incompatible shapes; adopting the chassis one would *regress* testability. |

### ServiceHost: mechanism shared, Event Log wording kept

The measurement that justified this one: the four copies shared **40–55 of ~60–69 lines** with
the chassis. They were identical in mechanism — SCM handshake, working directory, graceful
chunk-boundary stop — and differed in exactly two things.

**Identity.** The chassis version hardcoded `SEISMIC_CONNECTOR_HOME`, which any other connector
adopting it would have read as unset, silently running the service in `%WINDIR%\System32` where
`config/`, `env/`, `logs/` and `data/` do not exist. The home variable and the SCM service name
are now `ChassisIdentity.HomeEnvVar` and `.ServiceName`. The five spellings are kept rather than
unified because they appear in deployed service definitions and operator runbooks — renaming
them would break every existing installation.

**Event Log wording.** The four disagreed about what to tell the Windows Event Log, and three of
them own a local `EventLogSink`, so calling the chassis sink from the shared host would have
routed service lifecycle events through a different sink than the rest of the connector uses:

| | Emitted on service start/stop |
|---|---|
| Salesforce, Hadoop | nothing at all |
| Altrata | `Service command starting: …` / `…finished with exit code N` |
| Clarizen | its own `ServiceLifecycle(message, starting:)`, different wording again |
| Seismic | the chassis sink, different wording again |

So the mechanism moved and the vocabulary stayed, behind `ServiceHost.OnStarting`,
`OnStopRequested`, `OnFinished` and `OnStopped`. `OnFinished` and `OnStopped` are deliberately
separate: Seismic's "Service stopped" was emitted from the worker's `finally` and so survived an
unhandled exception, while Clarizen's and Altrata's "finished" events were emitted inside the
`try` and did not. Collapsing them would have changed one connector's Event Log on its failure
path — the path an operator is most likely to be reading. A hook left null emits nothing, which
is what Salesforce and Hadoop rely on.

### Byte-identical types: consolidated because they were literally the same

Three types were **textually identical** to the chassis version after stripping comments and
whitespace, and depended on nothing that diverges. They are now consumed from the chassis:
`LogLevels` and `LogRecord` (Salesforce), `EventLogEntryKind` (Clarizen).

They sat *inside* files whose main types are accepted divergences — Salesforce's CPython-style
`Logging.cs`, Clarizen's `EventLogSink.cs` — which is why they had never been noticed: the file
was correctly marked as divergent, so everything in it was assumed to be.

**Two more looked identical and were left alone.** `StreamHandler` and
`LineRotatingFileHandler` are byte-identical too, but both are declared
`: LogHandler` — and `LogHandler` is one of the diverged types. Identical text, different base
class, therefore a different contract: consolidating them would have silently reparented
Salesforce's handlers onto the chassis hierarchy. Text identity is necessary and not sufficient;
what matters is whether the type's dependencies are also shared.


## What the register cannot see: duplication across connectors

`conformance.py` detects a local type whose name collides with one the chassis **declares**. A
capability the connectors built independently and the chassis never acquired has nothing to
collide with, so it was invisible — and the gate reported "none undeclared" while it was true.

The gate now also reports the other question, and the answer is large: **91 types are declared
by two or more connectors with no chassis equivalent, across 280 rows.** Most of that is not
debt. Five connectors each declaring `Program`, `AppConfig`, `GraphClient`, `Dashboard` and
`Metrics` is five connectors, not five pieces of duplication.

It is therefore **reported, not enforced**, exactly like `kind=renamed` and for the same reason
this file's own gate header gives: telling shared capability from per-connector design needs
semantic comparison, and a gate that guesses gets switched off. What *is* enforced is that a
`kind=duplicated` row must not outlive the thing it records.

Nine rows are declared today, all one capability — the content gate, whose disposition is a
recorded decision rather than an open item:

### DECISION — the content gate stays three implementations, for now

**Status:** decided, and deliberately not scheduled. **Revisit when** the trigger below fires.

**Scope.** `ContentGate` (Seismic, Altrata) / `ContentGateStage` (Clarizen),
`ContentGateCategories`, `InjectionScanner`, `InjectionPattern` — roughly 1,900 lines of source
and 2,900 lines of tests across Clarizen, Seismic and Altrata.

**Decision.** Do **not** consolidate into the chassis at this time. Record the duplication in the
register (`kind=duplicated`, nine rows) so it is visible and counted, and leave the three
implementations in place.

**Why.** Three reasons, in order of weight:

1. **It is a contract unification, not an extraction.** `ServiceHost` was four copies of one
   mechanism differing in two strings — the shared part was obvious and the varying part became a
   seam. These three differ in *substance*: verdict shape, fail-mode model, category vocabulary.
   There is no equivalent of the `EnvFlags` argument either, where one copy was provably stale and
   consolidation *was* the fix; here no implementation is demonstrably wrong.
2. **The category vocabulary is an index-visible data contract.** The categories are stamped into
   the Graph-declared `ContentGateStatus` property, whose documented grammar is `clean` /
   `incomplete:<category>` / `blocked:<category>`. Unifying the vocabulary changes values already
   written into the index, which this repository treats as a re-baseline (a Graph property cannot
   be retyped in place — see each connector's `docs/DR.md` on schema compatibility), not a
   refactor that CI can prove safe.
3. **The seam's shape depends on a decision that has not been made.** This is TOGAF P0 gap **G1**,
   and the assessment lists the bank's malware-scanning integration contract — ICAP endpoint,
   Defender API, or an internal gateway — as outstanding. That contract determines what the
   chassis abstraction must expose. Designing it first and learning the contract afterwards
   produces a sixth implementation, not one.

**What this costs.** A fix to injection detection has to be written up to three times, and the two
connectors with no gate at all (Salesforce, Hadoop BDH) gain nothing from the work already done.
That is the accepted price, and it is the reason the trigger below is not "someday".

**Revisit trigger — any one of:**
- the malware-scanning integration contract is agreed (this is G1's own blocker, and the
  assessment's recommendation is then to lift the existing stage into the chassis rather than
  write two more copies);
- Salesforce or Hadoop BDH is scheduled to gain content inspection;
- a defect is found in one connector's scanner that also applies to the others — the `EnvFlags`
  situation, where duplication is what let a fix miss two connectors of five.

**Explicitly not blocked on:** tidiness, the divergence count, or the size of the duplication.
None of those are reasons to change an index-visible contract.

## Drift guard

Version drift is structurally impossible: there is no package or feed, so every connector
compiles against the one copy of the chassis in the tree, and a change to it is built against
all five by CI. What CI does enforce is that the connectors still pass — the workflows at the
*repository root* (`.github/workflows/`: `chassis.yml` plus one per connector, CodeQL, the
conformance gate, and the release pipeline) are the only workflows GitHub executes. The
connectors carry none of their own.

**What the guard does not see.** `conformance.py` detects a local type whose name matches one
the chassis declares. A capability the connectors share but the chassis does *not* have is
therefore invisible to it — `ContentGate` exists in Clarizen, Seismic and Altrata under three
shapes and has never appeared in the register, because there is no chassis `ContentGate` for it
to collide with. The register measures divergence from the chassis, not duplication across
connectors, and those are not the same question.

Baselines, green on both `ubuntu-latest` and `windows-latest` at chassis 1.20.0:

| Connector | Tests |
|---|---|
| Salesforce | 1213 |
| Seismic | 1025 |
| Hadoop | 1021 |
| Clarizen | 929 |
| Altrata | 764 |
| Chassis | 591 |
| **Total** | **5,543** |
