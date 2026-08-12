# Connector.Chassis

The shared infrastructure project the connectors are consolidated onto — one
implementation of the foundation that each connector previously carried its own
copy of.

**All five connectors in `main` consume it**, via a `<ProjectReference>` to
`../../../Connector.Chassis/Connector.Chassis.csproj`. It is not a NuGet
package: there is no `PackageReference`, no pinned version and no feed, so a
clean clone of the repository builds every connector against the chassis
sources in the tree. Consolidation is deliberately partial — the components
listed under "Deliberately not shared" stay connector-side.

## Why

Every connector vendored the same foundation, so every foundation fix had to be
written five times. The repository history shows the cost directly — the
`Bank-grade hardening`, `Enterprise-grade hardening pack`, and
`Adversarial hardening rounds 7-10` commits each applied one set of fixes across
all five connectors. Consolidating means one fix, one review, one place to audit.

## What's in it

| Area | Types |
|---|---|
| Lifecycle | `Chassis` (identity), `ServiceHost`, `ServiceStop` |
| Logging | `Logging`, `StandardLogDialect`, `EventLogSink`, `LogPruner`, `Tracing` |
| Resilience | `CircuitBreaker`, `CircuitBreakerRegistry`, `HttpTransport`, `Alerting` |
| State | `SqlStateStore`, `SqlExecutor`, `SqlGateway`/`ISqlGateway`, `HaCoordinator` |
| Security | `SecretProvider`, `SecureDirectory`, `DecisionLedger` |
| Config | `EnvFlags`, `ConfigException` |
| Metrics | `MetricsRenderer` |

The chassis **references no connector-specific type**. Where it needs
connector-specific behaviour it exposes a hook the host assigns
(`Chassis.CorrelationIdProvider`, `Logging.HardenDirectoryHook`,
`Logging.EventLogMirrorHook`, `HaCoordinator.ClaimAcquiredHook` /
`ClaimReleasedHook`).

`MetricsRenderer` owns only the Prometheus *render mechanism*. Each connector
owns its own metric series in a local `Infrastructure/Metrics` facade, so adding
a connector never widens a shared type.

Connectors adopt it without touching call sites, by aliasing in the `.csproj`:

```xml
<ProjectReference Include="../../../Connector.Chassis/Connector.Chassis.csproj" />
<Using Include="Connector.Chassis.ServiceStop" Alias="ServiceStop" />
```

## Deliberately not shared

Consolidation stopped where the connectors had diverged into different-but-correct
implementations rather than duplicates — forcing those together would mean
rewriting working, tested behaviour. `DecisionLedger`, `HaCoordinator`,
`SqlStateStore`, `Alerting`, `EventLogSink`, `LogPruner`, `ServiceHost`,
`EnvFlags` and `CircuitBreaker` are therefore outside the consolidated surface —
the chassis carries them, but the per-type migrations left them connector-side.
Salesforce also keeps
its own CPython-style logging (bridged in via `Chassis.LoggerFactory`) and
Altrata its own `SecretProvider`. See `CONSOLIDATION.md` for the full shared
surface and the accepted divergences.

## Build

```bash
dotnet build Connector.Chassis/Connector.Chassis.csproj -c Release
```

CI (`.github/workflows/chassis.yml`, at the *repository* root — the only place
GitHub executes workflows from in this monorepo) builds it on Linux and Windows.
Windows Server is the primary deployment target and the chassis carries the
Windows-specific surface, so it has to compile there and not only on the Linux
CI and dev machines. Connectors pick the chassis up from the tree by project
reference, so nothing needs publishing for them to build.

## Status

Version **1.13.1**. All five connectors are migrated and green on both
`ubuntu-latest` and `windows-latest`, with `/metrics` output byte-identical to
before migration:

| Connector | Tests |
|---|---|
| Salesforce | 1206 |
| Seismic | 1017 |
| Hadoop | 983 |
| Clarizen | 878 |
| Altrata | 743 |

Total **4,827**.
