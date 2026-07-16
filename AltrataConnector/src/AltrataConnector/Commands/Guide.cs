// Commands/Guide.cs
// -----------------
// The `guide` command: a condensed operator handbook printed to stdout.

namespace AltrataConnector.Commands;

public static class Guide
{
    public static Task<object?> RunAsync(ParsedArgs _)
    {
        Console.WriteLine(Text);
        return Task.FromResult<object?>(true);
    }

    public const string Text = """
        =====================================================================
        Altrata Copilot Connector — setup & usage guide
        =====================================================================

        WHAT THIS IS
          A Microsoft 365 Copilot Graph connector for Altrata relationship &
          wealth intelligence (BoardEx / WealthEngine / Wealth-X style data).
          Primary ingestion: licensed bulk file feeds dropped over SFTP into
          FEED_PATH. Secondary: on-demand (billable) Altrata REST API lookups.

        1. PREREQUISITES
          * Entra app registration with APPLICATION permissions (admin consent):
              ExternalConnection.ReadWrite.OwnedBy
              ExternalItem.ReadWrite.OwnedBy
          * The Altrata feed drop directory reachable as a local path (FEED_PATH).
          * The seat list: config/seats.json / SEAT_LIST_PATH (UPNs/object IDs)
            or SEAT_GROUP_ID (one Entra group). Items are ONLY visible to seats.

        2. CONFIGURE
          Copy env/.env.local.example to env/.env.local, put SECRET_* values in
          env/.env.local.user. Then run:
              altrata-connector validate-config --strict

        3. DEPLOY
              altrata-connector setup-connection        # connection + schema only
              altrata-connector full-deployment         # + full crawl
              altrata-connector full-deployment --continuous \
                  --full-crawl-hours 24 --incremental-hours 4

        4. DAY-2 OPERATIONS
              altrata-connector ingest                  # full crawl
              altrata-connector ingest --incremental    # unprocessed deliveries only
              altrata-connector ingest-object --type WealthIndicator
              altrata-connector ingest-item --id P123456 --purpose "RFP for client X"
              altrata-connector retry-failed --clear-on-success
                # shard-aware; replays upserts (PUT) and delta tombstones (DELETE)
              altrata-connector seat-sync               # refresh seats; re-ACLs on change
              altrata-connector identity-dry-run --save
              altrata-connector forget-subject --id P123456          # DSAR dry-run
              altrata-connector forget-subject --id P123456 --confirm # erase + suppress
              altrata-connector forget-subject --email ada@x.com --confirm
              altrata-connector unsuppress-subject --id P123456 --confirm
              altrata-connector purge-all               # dry-run report
              altrata-connector purge-all --confirm     # license-end purge

        5. FEEDS & RECONCILIATION
          Each delivery directory under FEED_PATH carries manifest.json with a
          SHA-256 and record count per file. A checksum mismatch REJECTS the
          delivery (alert fired, nothing ingested). Delta deliveries carry
          tombstones (op=delete / is_deleted=true) that WITHDRAW items instead
          of upserting them. After processing, ingested + deleted +
          dead-lettered must equal the manifest count; the reconciliation
          report is written to logs/reconciliation_{connector}_{delivery}.jsonl.

        5b. ENTITY RESOLUTION (docs/MATCHING.md)
          Deterministic email → name+employer, then the opt-in fuzzy tier
          (ENTITY_FUZZY_MATCHING=true; threshold ENTITY_MATCH_THRESHOLD).
          Near-misses queue in logs/match_review_{connector}.jsonl; every link
          carries crmMatchRule + crmMatchConfidence provenance.

        5c. RELATIONSHIP PATHS (docs/RELATIONSHIP_PATHS.md)
          RELATIONSHIP_PATHS=true precomputes bounded per-person path summaries
          from RelationshipPath/BoardMembership/Organization onto PersonProfile
          items: firstDegreeCount, secondDegreeCount, pathCount, topConnectedOrgs,
          strongestPathSummary. Rebuilt per crawl; withdrawn persons drop from
          the index; path properties never widen ACLs (items stay seat-only).

        5d. DATA CLASSIFICATION (docs/CLASSIFICATION.md)
          CLASSIFICATION=true stamps a unified sensitivityLabel (Public/Internal/
          Confidential/Restricted; Altrata is Confidential+) + detectedCategories
          on every item, folding the existing PII label in as one input.
          DATA_RESIDENCY tags residency (US/EU). CLASSIFICATION_MANIFEST=true
          writes logs/classification_{connector}_{delivery}.jsonl (counts +
          item→label ONLY; NO personal values, same allowlist rule as traces).

        6. ENTITLEMENT (READ THIS)
          Every item's ACL grants ONLY the licensed seats — never "everyone".
          An empty seat list stops ingestion (fails closed). Seat changes are
          detected by hash and trigger a re-ACL pass over all ingested items.

        6b. ERASURE / DSAR (docs/ERASURE.md)
          forget-subject --id X (or --email e) --confirm withdraws every item
          for a person, removes them from the inventory/crosswalk/path-index,
          and SUPPRESSES re-ingestion so a later re-delivery is skipped (counted
          as suppressed in reconciliation, not dead-lettered). Erasures are
          recorded in a tamper-evident (hash-chained) erasure ledger.
          unsuppress-subject --id X --confirm lifts the block.

        7. OBSERVABILITY & OPS
          HEALTH_PORT=9090   → /health /ready /metrics (Prometheus)
          LOG_FORMAT=json    → structured logs (+ correlationId per cycle)
          LOG_LEVEL=debug    → DEBUG lines (hot-path formatting gated off it)
          OTEL_EXPORTER_OTLP_ENDPOINT → OpenTelemetry span export (docs/
            OBSERVABILITY.md); off = inert. Spans tag ids/counts/hashes only,
            NEVER names/emails/wealth. Correlation id links logs↔spans↔reports.
          ALERT_WEBHOOK_URL  → alerts (checksum reject, dead-letter threshold)
          CIRCUIT_BREAKER    → fail-fast on sustained Graph/API outages
            (docs/RESILIENCE.md). Graph breaker open → crawl PAUSES at a safe
            checkpoint (no dead-letters), /ready→503, auto-recovers on probe.
            Erasure stays durable (suppress+ledger) even when Graph is down.
          USE_SQL_SERVER     → all state in SQL Server;  HA_MODE for multi-node
          USE_KEY_VAULT      → SECRET_* from Azure Key Vault

        7b. THROUGHPUT & SOVEREIGN CLOUDS
          GRAPH_BATCH_SIZE / GRAPH_BATCH_WORKERS → $batch size x workers
            (adaptive: 429 dials down, success dials back up; docs/RETRY.md)
          GRAPH_CONNECTION_SHARDS → shard datasets across N connections for
            N x write capacity (docs/SHARDING.md)
          GRAPH_BASE_URL / GRAPH_SCOPE → sovereign clouds (US Gov, 21Vianet)

        8. WINDOWS SERVICE
          See scripts/install-windows-service.ps1 and README.md. SCM stop is
          graceful: the current chunk finishes, the Graph batch flushes and the
          checkpoint is saved before exit.
        =====================================================================
        """;
}
