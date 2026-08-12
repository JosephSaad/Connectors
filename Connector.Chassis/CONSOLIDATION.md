# Connector.Chassis — consolidation status

Shared infrastructure project consumed by all five custom Microsoft 365 Copilot connectors via
`<ProjectReference>` to `../../../Connector.Chassis/Connector.Chassis.csproj`. Current version:
**1.13.1**.

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

Present in the chassis but **outside** the consolidated surface — the per-type migrations left
these connector-side: DecisionLedger · HaCoordinator · SqlStateStore · Alerting · EventLogSink ·
LogPruner · ServiceHost · EnvFlags · CircuitBreaker (+Registry).

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

## Drift guard

Version drift is structurally impossible: there is no package or feed, so every connector
compiles against the one copy of the chassis in the tree, and a change to it is built against
all five by CI. What CI does enforce is that the connectors still pass — six workflows at the
*repository root* (`.github/workflows/`: `chassis.yml` plus one per connector) are the only
workflows GitHub executes. The `<Connector>/.github/workflows/*.yml` files are inert leftovers
from when each connector was its own repository.

Baselines, green on both `ubuntu-latest` and `windows-latest` at chassis 1.13.1:

| Connector | Tests |
|---|---|
| Salesforce | 1206 |
| Seismic | 1017 |
| Hadoop | 983 |
| Clarizen | 878 |
| Altrata | 743 |
| **Total** | **4,827** |
