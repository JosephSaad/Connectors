// Commands/Guide.cs
// -----------------
// `guide` — prints the end-to-end setup and usage guide.

namespace ClarizenConnector.Commands;

public static class Guide
{
    public static Task<object?> RunAsync(ParsedArgs args)
    {
        Console.WriteLine(Text);
        return Task.FromResult<object?>(null);
    }

    public const string Text = """
        =====================================================================
         Clarizen (Planview AdaptiveWork) → Microsoft 365 Copilot connector
        =====================================================================

        1. PREREQUISITES
           • An Entra app registration with APPLICATION Graph permissions
             (admin-consented): ExternalConnection.ReadWrite.OwnedBy and
             ExternalItem.ReadWrite.OwnedBy (User.Read.All for identity sync).
           • A Clarizen API user (login + password) with read access to the
             work items you want to index, and awareness of your org's daily
             API quota (CLARIZEN_API_CALLS_PER_DAY leaves headroom for people).
           • .NET 8 runtime on the crawl host (Windows Server for service mode).

        2. CONFIGURE
           cp env/.env.local.example env/.env.local     # non-secret settings
           #   put SECRET_* lines into env/.env.local.user (never committed)
           # Review config/schema.json  (objects, fields, financial fields, ACL modes)
           #        config/graph-schema.json (Graph connection schema)

        3. VALIDATE
           ClarizenConnector validate-config --strict

        4. IDENTITY DRY RUN (optional but recommended)
           ClarizenConnector identity-dry-run --verbose     # add --save to persist

        5. DEPLOY
           ClarizenConnector setup-connection               # connection + schema only
           ClarizenConnector full-deployment                # connection + schema + crawl
           ClarizenConnector full-deployment --continuous --full-crawl-hours 24 --incremental-hours 4

        6. OPERATE
           ClarizenConnector ingest                         # content only
           ClarizenConnector ingest-object --type Project
           ClarizenConnector ingest-item --id /Task/1234567
           ClarizenConnector retry-failed --clear-on-success
           ClarizenConnector reconcile                      # index-vs-source drift report
           ClarizenConnector reconcile --fix                # also delete stale items
           # Deletions: full crawls sweep the ingested-item inventory against
           # the source and withdraw removed records (docs/DELETION_SYNC.md).
           # Attachments: ATTACHMENT_INGESTION=true downloads + extracts file
           # text into the attachment item content (docs/ATTACHMENTS.md).
           # Webhooks: CLARIZEN_WEBHOOK_PORT + CLARIZEN_WEBHOOK_SECRET add
           # near-real-time event-driven incremental in --continuous mode;
           # polling stays the fallback (docs/WEBHOOKS.md).
           # Tracing: OTEL_EXPORTER_OTLP_ENDPOINT exports OpenTelemetry spans +
           # a correlation id on every log/dead-letter/span (docs/TRACING.md);
           # unset = a no-op with unchanged overhead.
           # Resilience: circuit breakers (CIRCUIT_BREAKER_*) fail fast on a
           # sustained dependency outage and pause the crawl at a safe
           # checkpoint (degraded mode), auto-recovering (docs/RESILIENCE.md).
           # Health/metrics: set HEALTH_PORT=8080 → /health /ready /metrics
           # Alerts: ALERT_WEBHOOK_URL + ALERT_DEADLETTER_THRESHOLD

        7. WINDOWS SERVICE
           dotnet publish src/ClarizenConnector -c Release -r win-x64 -o C:\ClarizenConnector
           Copy-Item -Recurse config,env C:\ClarizenConnector
           .\scripts\install-windows-service.ps1 -InstallDir C:\ClarizenConnector
           Start-Service ClarizenConnector
           # SCM stop is graceful: in-flight chunk finishes, batch flushes,
           # checkpoint saves; the next start resumes where it left off.

        8. SCALE OUT (optional)
           USE_SQL_SERVER=true + SQL_CONNECTION_STRING moves all state to SQL
           Server (scripts/sql/create-database.sql provisions it) and
           HA_MODE=true enables active-active multi-node crawling (docs/HA.md).
           GRAPH_CONNECTION_SHARDS shards object types across N Graph
           connections for N-fold write throughput (docs/SHARDING.md).
           CLARIZEN_WEBHOOK_PORT + CLARIZEN_WEBHOOK_SECRET add event-driven
           incremental alongside polling (docs/WEBHOOKS.md).
           Containers: Dockerfile + docker-compose.yml (SQL + connector dev
           topology).

        Full environment reference: env/.env.local.example
        Docs: docs/HA.md, docs/RETRY.md, docs/OBSERVABILITY.md,
              docs/SQL_CONTRACT.md, docs/SHARDING.md, docs/DELETION_SYNC.md,
              docs/ATTACHMENTS.md, docs/WEBHOOKS.md, docs/TRACING.md,
              docs/RESILIENCE.md
        """;
}
