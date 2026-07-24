# Connector.Chassis

The shared infrastructure package the connectors are being consolidated onto —
one implementation of the foundation that each connector previously carried its
own copy of.

**Nothing in `main` consumes this yet.** This folder and its CI job land first,
on their own, so the package has a home and a build before any connector is
changed to depend on it. Each connector in `main` still carries its own copy of
the foundation; those are replaced one connector at a time, in later changes.

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
| Logging | `Logging`, `EventLogSink`, `LogPruner`, `Tracing` |
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
<PackageReference Include="Connector.Chassis" Version="1.11.0" />
<Using Include="Connector.Chassis.ServiceStop" Alias="ServiceStop" />
```

## Deliberately not shared

Consolidation stopped where the connectors had diverged into different-but-correct
implementations rather than duplicates — forcing those together would mean
rewriting working, tested behaviour. Clarizen keeps its own `DecisionLedger`
(already SHA-256 hash-chained and tamper-evident), `CircuitBreaker`,
`HaCoordinator` and `DeadLetterRedactor`. See `CONSOLIDATION.md` for the full
shared surface and the accepted divergences.

## Build

```bash
dotnet build Connector.Chassis/Connector.Chassis.csproj -c Release
dotnet pack  Connector.Chassis/Connector.Chassis.csproj -c Release -o artifacts
```

CI (`.github/workflows/chassis.yml`) builds it on Linux and Windows — Windows
Server is the primary deployment target and the chassis carries the
Windows-specific surface — then packs it and publishes the `.nupkg` as a build
artifact.

## Status

Version **1.11.0**. Verified green on the three connectors migrated so far, on
their branches, with `/metrics` output byte-identical to before migration:

| Connector | Tests | Branch |
|---|---|---|
| Seismic | 1017 | `seismic-connector` |
| Clarizen | 877 | `clarizen-connector` |
| Hadoop | 981 | `hadoop-connector` |

Altrata and Salesforce are not migrated yet.
