# Connectors

Microsoft 365 Copilot **Graph connectors** for enterprise source systems, built
on a shared, production-hardened C#/.NET 10 chassis. Each connector is
self-contained (its own solution, tests, config, docs, Docker image and CI) and
lives in its own top-level folder.

| Connector | Source system | Highlights | Tests |
|---|---|---|---|
| [SalesforceConnector](SalesforceConnector/) | Salesforce CRM | Sharing-model ACLs, standard + custom objects, sovereign-cloud ready | 819 |
| [ClarizenConnector](ClarizenConnector/) | Planview AdaptiveWork (Clarizen) | REST v2 + TDW bulk, financial-field governance, webhooks | 480 |
| [SeismicConnector](SeismicConnector/) | Seismic (sales enablement) | Version-aware, No-MNE exclusion filter, usage ranking | 406 |
| [AltrataConnector](AltrataConnector/) | Altrata (relationship & wealth intelligence) | Licensed feeds, seat-only entitlement, DSAR erasure | 375 |

## Shared chassis

Every connector carries its own copy of the same foundation — no shared
components:

- Unified CLI (`guide`, `setup-connection`, `full-deployment`, `ingest`,
  `reconcile`, `validate-config`, …)
- Checkpointed full + incremental crawls with crash/stop resume
- Dead-letter queue + `retry-failed`; Graph `$batch` ingest with adaptive
  concurrency and exact `Retry-After` handling; connection sharding
- SQLite / SQL Server state, active-active HA leases, Azure Key Vault secrets
- `/health` `/ready` `/metrics`, structured JSON logs, OpenTelemetry tracing
- Circuit breakers with degraded-mode fail-safe
- Unified data classification & sensitivity labeling (Public → Restricted)
- SCM-aware Windows service, Docker image, GitHub Actions CI/CodeQL/release

See each connector's own `README.md` for source-specific features, environment
variables, deployment and hardware guidance.

## Build & test a connector

```bash
cd <Connector>            # e.g. SalesforceConnector
dotnet build
dotnet test
```

Requires the .NET 10 SDK. Deployment target is Windows Server (service mode);
the code is cross-platform for development.
