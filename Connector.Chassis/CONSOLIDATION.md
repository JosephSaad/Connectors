# Connector.Chassis — consolidation status

Shared infrastructure project consumed by all five custom Microsoft 365 Copilot connectors via
`<ProjectReference>` to `../../../Connector.Chassis/Connector.Chassis.csproj`. Current version:
**1.19.0**.

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

Present in the chassis but **outside** the consolidated surface — the per-type migrations left
these connector-side: DecisionLedger · HaCoordinator · SqlStateStore · Alerting · EventLogSink ·
LogPruner · ServiceHost · CircuitBreaker (+Registry).

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

## Not yet decided (measured, not argued)

Six components carry copies but appear in neither the shared list nor the accepted
divergences above, so the register records them without saying whether they are debt. These
were measured against the chassis rather than judged by eye — lines in common after stripping
comments, blanks and the namespace:

| Component | Copies | Overlap with the chassis | Reading |
|---|---|---|---|
| `ServiceHost` (+`CommandWorker`, same file) | 4 | **40–55 of ~60–69 lines** | Real duplication. The strongest remaining candidate: 8 of the 64 register rows, and the connectors agree on most of the mechanism. |
| `SqlStateStore` | 4 | 9–18 lines (Altrata is 585 lines against the chassis's 173) | **Not** duplication. These are different implementations of the same idea; consolidating means choosing one and rewriting three, which is a design decision, not cleanup. |

The distinction matters because "64 divergences" reads as one backlog and is not: most rows are
deliberate, `SqlStateStore` is divergent-but-correct, and `ServiceHost` is the part that would
actually shrink by sharing. Note also that `ServiceHost` is Windows-service lifetime and SCM
integration — the area where this repository's Windows-only defects have historically hidden —
so it wants the two-OS CI gate, not a local-only pass.

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

Baselines, green on both `ubuntu-latest` and `windows-latest` at chassis 1.19.0:

| Connector | Tests |
|---|---|
| Salesforce | 1213 |
| Seismic | 1025 |
| Hadoop | 1021 |
| Clarizen | 929 |
| Altrata | 764 |
| Chassis | 578 |
| **Total** | **5,530** |
