# Connector.Chassis — consolidation status

Shared infrastructure package consumed by the Microsoft 365 Copilot connectors via
`PackageReference` (local feed `/Users/joseph/Teams/local-nuget`). Current version: **1.8.0**.

The chassis owns the **mechanism**; connectors keep their **domain vocabulary**. Not every
infrastructure file is consolidated — components that diverged into different-but-correct
implementations are kept connector-side by design (see "Accepted divergences"). Drift is
guarded by `chassis-parity-check.py` (run in CI): every connector must be on the same chassis
version, and a type consumed from the chassis must have no local shadow copy.

## Shared components (in the chassis)

Logging · EventLogSink · LogPruner · SecureDirectory · SecretProvider · ServiceHost ·
ServiceStop · DecisionLedger · CircuitBreaker (+Registry) · SqlExecutor (static) · SqlGateway
(instance `ISqlGateway`) · SqlStateStore · EnvFlags · Tracing · ConfigException · HttpTransport ·
Alerting · Metrics · HaCoordinator · Chassis (identity).

## Consumption by connector

| Connector | Consumes from chassis | Migration mechanic |
|---|---|---|
| **Seismic** (reference) | All of the above (Phase A) | Global `using Connector.Chassis` |
| **Clarizen** | ServiceStop, SecretProvider, Logging (+IAppLogger, LogLevel), Metrics, ISqlGateway + SqlGateway (aliased as `SqlExecutor`) | Per-type `<Using Alias>` — deletes the local copy with zero call-site edits |

Superset design keeps both connectors' behaviour byte-identical: **Logging** and **Metrics** carry
each connector's format/series behind a mode switch + hooks (`HardenDirectoryHook`,
`EventLogMirrorHook`, `CircuitBreakerRenderHook`, `TracingEnabledHook`,
`Chassis.CorrelationIdProvider`) so the chassis references no connector type. **SqlGateway** is
Clarizen's mockable text-command seam living beside Seismic's static `SqlExecutor`.

## Accepted divergences (kept connector-side, by design)

These are **not** drift — they are different-but-correct implementations where consolidation
would be large churn (call-site rewrites + test re-authoring) for no security or correctness
gain. Recorded here so the decision is explicit and auditable.

| Component (Clarizen) | Why kept local |
|---|---|
| **DecisionLedger** | Already SHA-256 hash-chained + tamper-evident (equivalent security posture to the chassis ledger; the chassis's is only more *robust*). Decision vocabulary (exclusion/acl_restriction/quarantine) is connector-specific. |
| **CircuitBreaker** | Different paradigm — imperative `TryAcquire`/`OnSuccess`/`OnFailure` vs the chassis's functional `ExecuteAsync` wrapper. Incompatible APIs, no gain. |
| **Breakers** | Connector-specific facade naming Clarizen's own dependencies (Clarizen API, Graph); analogous to Seismic's `SeismicBreakerNames`. Feeds the chassis `Metrics` via `CircuitBreakerRenderHook`. |
| **HaCoordinator** | Injected-`ISqlGateway` design (mockable) — arguably better than the chassis's static-SQL version; adopting the chassis one would *regress* testability. |
| DeadLetterRedactor, EnvLoader, DirectoryHardening, EventLogSink, HttpClientFactory, LogPruner, Tracing, CorrelationContext, HealthEndpoint | Divergent-but-correct or connector-specific; not a clean shareable win. Wired to the chassis where needed via the hooks above. |

## Drift guard

`chassis-parity-check.py` (fleet root) — run per connector in CI. Fails the build on version
drift or a local shadow of a consumed type. Baselines: Seismic **1017**, Clarizen **877** tests,
both green on 1.8.0.
