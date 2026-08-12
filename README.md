# Connectors

Microsoft 365 Copilot **Graph connectors** for enterprise source systems, built
on a shared, production-hardened C#/.NET 10 chassis. Each connector has its own
solution, tests, config, docs and Docker image and lives in its own top-level
folder; all five reference the one `Connector.Chassis/` project in this repo,
and CI runs from the workflows at the repository root.

![Deployment & data flow — five connectors](Operator_Guides/Connectors_Architecture_Diagram.svg)

| Connector | Source system | Highlights | Tests |
|---|---|---|---|
| [SalesforceConnector](SalesforceConnector/) | Salesforce CRM | Sharing-model ACLs, standard + custom objects, sovereign-cloud ready, large-group ACL scale guard | 1206 |
| [ClarizenConnector](ClarizenConnector/) | Planview AdaptiveWork (Clarizen) | REST v2 + TDW bulk, financial-field governance (filter by default), webhooks with anti-replay | 878 |
| [SeismicConnector](SeismicConnector/) | Seismic (sales enablement) | Version-aware, fail-closed No-MNE exclusion filter, usage ranking, webhook anti-replay | 1017 |
| [AltrataConnector](AltrataConnector/) | Altrata (relationship & wealth intelligence) | Licensed feeds, seat-only entitlement, purpose-of-use authz, DSAR erasure | 743 |
| [HadoopConnector](HadoopConnector/) | BDH Hadoop data mart (nightly Salesforce mirror) | Filter-first at 150M+ scale, partition pruning, 24h-lag aware | 983 |

## Shared chassis

`Connector.Chassis/` (v1.13.1) is one real shared project: all five connectors
consume it via `<ProjectReference>`. It is not a NuGet package — there is no
version pinning and no feed. From it they take the identity/seams core and
ServiceStop (all five), Logging (four — Salesforce keeps its own logging and
bridges through `Chassis.LoggerFactory`), SecretProvider (four — Altrata keeps
its own), SqlExecutor and MetricsRenderer (Seismic, Clarizen, Hadoop),
SqlGateway (Clarizen, Hadoop) and Tracing (Seismic, Altrata). DecisionLedger,
HaCoordinator, SqlStateStore, Alerting, EventLogSink, LogPruner, ServiceHost,
EnvFlags and CircuitBreaker are still implemented separately in each connector.

Every connector ships the same foundation:

- Unified CLI (`guide`, `setup-connection`, `full-deployment`, `ingest`,
  `reconcile`, `validate-config`, …)
- Checkpointed full + incremental crawls with crash/stop resume
- Dead-letter queue + `retry-failed`; Graph `$batch` ingest with adaptive
  concurrency and exact `Retry-After` handling; connection sharding
- SQLite / SQL Server state, active-active HA leases, Azure Key Vault secrets
- `/health` `/ready` `/metrics`, structured JSON logs, OpenTelemetry tracing
- Circuit breakers with degraded-mode fail-safe
- Unified data classification & sensitivity tagging (Public → Restricted) — an
  advisory connector-applied tag, with optional ACL enforcement of the top tier
- SCM-aware Windows service, Docker image, GitHub Actions CI (one root
  workflow per connector, plus one for the chassis)
- Enterprise operations pack: Windows Event Log/SIEM integration, corporate
  proxy + TLS-inspection CA support, certificate-credential Graph auth,
  threat model, runbooks, DR plan, Grafana dashboards + alert rules (see each
  connector's "Enterprise operations" README section). SBOM generation, MSI
  packaging and release signing are authored in each connector's
  `.github/workflows/release.yml`, but those files sit below the repository
  root, so GitHub never runs them — the pipeline is written, not yet wired.
- Bank-grade hardening — privacy- and compliance-safe defaults out of the box:
  dead-letter payload redaction by default, entitlement re-sync on incremental
  crawls, owner-only (0700) state directories, stale-index item TTL
  (`expirationDateTime`), signed-timestamp webhook anti-replay, purpose-of-use
  authorization (Altrata), and a tamper-evident hash-chained decision ledger

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

Docker images build with the repository root as the build context, because each
connector references `../Connector.Chassis`:

```bash
docker build -f <Connector>/Dockerfile .
```
