// Commands/Guide.cs
// -----------------
// `guide` — prints the end-to-end setup and usage guide.

namespace HadoopConnector.Commands;

public static class Guide
{
    public static Task<object?> RunAsync(ParsedArgs args)
    {
        Console.WriteLine(Text);
        return Task.FromResult<object?>(null);
    }

    public const string Text = """
        =====================================================================
         BDH (Hadoop data mart) → Microsoft 365 Copilot Graph connector
        =====================================================================

        BDH holds SALESFORCE data synchronized nightly (up to 24h behind the
        live org). This connector is the CHEAP indexing path: it reads BDH's
        Hive-partitioned CSV/JSONL exports instead of burning Salesforce API
        capacity. Trade-offs (documented in README):
          • freshness — every item carries DataAsOf; answers can lag 24h.
          • ACLs — BDH has no Salesforce sharing tables, so per-object
            aclMode is ownerOnly | group | public (coarser than the live
            Salesforce connector's sharing-model ACLs).
          • ids — external item ids ARE the Salesforce record ids, so this
            connector is swappable with the live Salesforce connector. Use a
            SEPARATE Graph connection id (never point both at one connection).

        1. PREREQUISITES
           • An Entra app registration with APPLICATION Graph permissions
             (admin-consented): ExternalConnection.ReadWrite.OwnedBy and
             ExternalItem.ReadWrite.OwnedBy (User.Read.All for identity sync).
           • WebHDFS access to the BDH cluster (HDFS_NAMENODE_URL, optional
             HDFS_USER simple auth / SECRET_HDFS_DELEGATION_TOKEN), or a
             mounted export directory (HDFS_MODE=localpath + BDH_EXPORT_PATH).
             Kerberos/SPNEGO is out of scope — front a kerberized cluster
             with an Apache Knox gateway and point HDFS_NAMENODE_URL at it.
           • .NET runtime on the crawl host (Windows Server for service mode).

        2. CONFIGURE
           cp env/.env.local.example env/.env.local     # non-secret settings
           #   put SECRET_* lines into env/.env.local.user (never committed)
           # Review config/schema.json   (objects, fields, aclMode per object)
           #        config/filters.json  (THE scale control — partition +
           #                              record filters per object; objects
           #                              with no filter refuse to crawl)
           #        config/graph-schema.json (Graph connection schema)

        3. VALIDATE
           HadoopConnector validate-config --strict
           # --strict fails on unfiltered objects, malformed filters, and
           # missing BDH/Graph connectivity.

        4. IDENTITY DRY RUN (optional but recommended)
           HadoopConnector identity-dry-run --verbose     # add --save to persist

        5. DEPLOY
           HadoopConnector setup-connection               # connection + schema only
           HadoopConnector full-deployment                # connection + schema + crawl
           HadoopConnector full-deployment --continuous --full-crawl-hours 24 --incremental-hours 4

        6. OPERATE
           HadoopConnector ingest                         # content only
           HadoopConnector ingest-object --type Contact
           HadoopConnector ingest-item --id 0035e00000abcde --object-type Contact
           HadoopConnector retry-failed --clear-on-success
           HadoopConnector reconcile                      # index-vs-source drift report
           HadoopConnector reconcile --fix                # also delete stale items
           # Filters: config/filters.json prunes partitions (zero I/O) and
           # streams record predicates; per-stage counts are logged and on
           # /metrics (docs/FILTERS.md).
           # Watermark: incrementals only read dt partitions newer than the
           # last sync minus BDH_LAG_HOURS (default 24) — the overlap absorbs
           # BDH's late-arriving nightly loads.
           # Deletions: full crawls sweep the ingested-item inventory against
           # the source and withdraw removed records (docs/DELETION_SYNC.md).
           # Tracing: OTEL_EXPORTER_OTLP_ENDPOINT exports OpenTelemetry spans +
           # a correlation id on every log/dead-letter/span (docs/TRACING.md);
           # unset = a no-op with unchanged overhead.
           # Resilience: circuit breakers (CIRCUIT_BREAKER_*) fail fast on a
           # sustained hdfs/graph outage and pause the crawl at a safe
           # checkpoint (degraded mode), auto-recovering (docs/RESILIENCE.md).
           # Health/metrics: set HEALTH_PORT=8080 → /health /ready /metrics
           # Alerts: ALERT_WEBHOOK_URL + ALERT_DEADLETTER_THRESHOLD

        7. WINDOWS SERVICE
           dotnet publish src/HadoopConnector -c Release -r win-x64 -o C:\HadoopConnector
           Copy-Item -Recurse config,env C:\HadoopConnector
           .\scripts\install-windows-service.ps1 -InstallDir C:\HadoopConnector
           Start-Service HadoopConnector
           # SCM stop is graceful: in-flight chunk finishes, batch flushes,
           # checkpoint saves; the next start resumes where it left off.

        8. SCALE OUT (optional)
           USE_SQL_SERVER=true + SQL_CONNECTION_STRING moves all state to SQL
           Server (scripts/sql/create-database.sql provisions it) and
           HA_MODE=true enables active-active multi-node crawling (docs/HA.md).
           GRAPH_CONNECTION_SHARDS shards object types across N Graph
           connections for N-fold write throughput — essential at BDH scale
           (docs/SHARDING.md).
           Containers: Dockerfile + docker-compose.yml (SQL + connector dev
           topology).

        Full environment reference: env/.env.local.example
        Docs: docs/FILTERS.md, docs/HA.md, docs/RETRY.md,
              docs/OBSERVABILITY.md, docs/SQL_CONTRACT.md, docs/SHARDING.md,
              docs/DELETION_SYNC.md, docs/TRACING.md, docs/RESILIENCE.md,
              docs/CLASSIFICATION.md
        """;
}
