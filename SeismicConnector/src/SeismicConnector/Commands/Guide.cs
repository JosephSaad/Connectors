// Commands/Guide.cs — prints the setup & usage guide.

namespace SeismicConnector.Commands;

public static class Guide
{
    public static Task<object?> CmdGuide(ParsedArgs args)
    {
        Console.WriteLine(Text);
        return Task.FromResult<object?>(true);
    }

    internal const string Text = """
        ================================================================================
          SEISMIC → MICROSOFT 365 COPILOT GRAPH CONNECTOR — SETUP & USAGE GUIDE
        ================================================================================

        1. PREREQUISITES
           * A Seismic tenant with an OAuth2 client-credentials app
             (Seismic Admin → Integrations → API clients). Scopes: library,
             teamsite and user read access.
           * An Entra ID app registration with APPLICATION Graph permissions
             (admin-consented):
               ExternalConnection.ReadWrite.OwnedBy
               ExternalItem.ReadWrite.OwnedBy
               User.Read.All, Group.Read.All        (identity mapping)
           * .NET 10 runtime on the crawl host (Windows Server for service mode).

        2. CONFIGURE
           * Copy env/.env.local.example → env/.env.local and fill in the
             non-secret values (CONNECTOR_ID, SEISMIC_TENANT, ...).
           * Put SECRET_SEISMIC_CLIENT_SECRET and SECRET_AAD_APP_CLIENT_SECRET in
             env/.env.local.user (never committed), or use Azure Key Vault
             (USE_KEY_VAULT=true + KEY_VAULT_URI).
           * Review config/schema.json (objects), config/graph-schema.json
             (searchable properties) and config/exclusions.json (the No-MNE
             filter — restricted libraries and MNE classification flags).

        3. VALIDATE
             seismic-connector validate-config            # env + files + connectivity
             seismic-connector identity-dry-run --verbose # check Entra mappings

        4. DEPLOY
             seismic-connector setup-connection           # connection + schema only
             seismic-connector full-deployment            # end-to-end, one shot
             seismic-connector full-deployment --continuous \
                 --full-crawl-hours 24 --incremental-hours 4

        5. OPERATE
             seismic-connector ingest                     # re-ingest (existing connection)
             seismic-connector ingest-object --type ContentItem
             seismic-connector ingest-item --id <contentId> [--teamsite <id>]
             seismic-connector retry-failed --clear-on-success
             seismic-connector reconcile [--repair]   # full-inventory drift sweep
             seismic-connector reacl [--dry-run]       # permission-change re-ACL
             * Dead-letter queue: logs/failed_records_<CONNECTOR_ID>.jsonl
             * Reconciliation (No-MNE audit): reconciliation_*.jsonl in each run dir
             * Health/metrics: set HEALTH_PORT (Prometheus text on /metrics)
             * Tracing: set OTEL_EXPORTER_OTLP_ENDPOINT to export spans (OTLP);
               a correlation id stamps every JSON log/dead-letter/report line
               per crawl regardless. Unset = tracing inert, overhead unchanged.
             * Resilience: per-dependency circuit breakers fail fast on a
               SUSTAINED outage and the crawl pauses into degraded mode at a
               checkpoint boundary (no state loss), auto-recovering on a
               half-open probe. /ready flips to 503 while a critical breaker is
               open; /metrics shows breaker state. CIRCUIT_BREAKER=false opts out.
             * Webhook (near-real-time): set SEISMIC_WEBHOOK_PORT and point a
               Seismic webhook subscription at POST /webhook. Polling remains
               the fallback.
             * Engagement ranking: SEISMIC_ENRICH_USAGE=true indexes
               views/downloads/shares as refinable ranking properties.
             * LiveDoc fields: LIVEDOC_FIELD_INDEXING=true indexes a LiveDoc
               template's personalization inputs (field names/labels) so it is
               searchable by its inputs. LiveDoc items only; best-effort.
             * Drift audit: schedule `reconcile` (add --repair to converge);
               drift_report_*.jsonl lands next to the run logs.
             * Permission re-ACL: PERMISSION_REACL=true refreshes stale ACLs
               each crawl when teamsite/profile permissions change (content is
               never re-sent). `reacl [--dry-run]` audits/repairs on demand.

        6. WINDOWS SERVICE
             pwsh scripts/install-windows-service.ps1 -BinPath <publish dir> `
                 -Arguments "full-deployment --continuous"
             SCM stop → the in-flight chunk finishes, the Graph batch flushes,
             the checkpoint is saved, then the service exits.

        7. COMPLIANCE GUARANTEES
             * "No MNE": content flagged with a configured MNE classification or
               living in a restricted library is never ingested; late flags are
               withdrawn on the next crawl. Every skip/withdrawal is written to
               the reconciliation report.
             * Expiry: content with a past expiry date is withdrawn automatically.
             * Versioning: only the current published version is indexed; a new
               version updates the same externalItem in place.
        ================================================================================
        """;
}
