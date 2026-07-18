# Connectors

Microsoft 365 Copilot **Graph connectors** for enterprise source systems, built
on a shared, production-hardened C#/.NET 10 chassis. Each connector is
self-contained (its own solution, tests, config, docs, Docker image and CI) and
lives in its own top-level folder.

![Deployment & data flow — five connectors](Operator_Guides/Connectors_Architecture_Diagram.svg)

| Connector | Source system | Highlights | Tests |
|---|---|---|---|
| [SalesforceConnector](SalesforceConnector/) | Salesforce CRM | Sharing-model ACLs, standard + custom objects, sovereign-cloud ready | 899 |
| [ClarizenConnector](ClarizenConnector/) | Planview AdaptiveWork (Clarizen) | REST v2 + TDW bulk, financial-field governance, webhooks | 575 |
| [SeismicConnector](SeismicConnector/) | Seismic (sales enablement) | Version-aware, No-MNE exclusion filter, usage ranking | 474 |
| [AltrataConnector](AltrataConnector/) | Altrata (relationship & wealth intelligence) | Licensed feeds, seat-only entitlement, DSAR erasure | 449 |
| [HadoopConnector](HadoopConnector/) | BDH Hadoop data mart (nightly Salesforce mirror) | Filter-first at 150M+ scale, partition pruning, 24h-lag aware | 650 |

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
- Enterprise operations pack: Windows Event Log/SIEM integration, corporate
  proxy + TLS-inspection CA support, certificate-credential Graph auth,
  dead-letter payload redaction, threat model, runbooks, DR plan, Grafana
  dashboards + alert rules, SBOM + signed releases, MSI packaging (see each
  connector's "Enterprise operations" README section)

See each connector's own `README.md` for source-specific features, environment
variables, deployment and hardware guidance.

All five share one tenant's Graph quotas (30 connections, 50M indexed items):
[TENANT_GOVERNANCE.md](TENANT_GOVERNANCE.md) allocates connections, item
budgets, app registrations and crawl windows across the fleet.

Operator documentation (deploy / monitor / troubleshoot / support), written for
IT operators, lives in [Operator_Guides/](Operator_Guides/) — start with
`00_START_HERE.pdf`, then the Tenant & Common Concepts guide, then your
connector's guide.

## Build & test a connector

```bash
cd <Connector>            # e.g. SalesforceConnector
dotnet build
dotnet test
```

Requires the .NET 10 SDK. Deployment target is Windows Server (service mode);
the code is cross-platform for development.
