# Threat model

STRIDE analysis per trust boundary, mapped to the mitigations that actually
exist in this codebase (file references inline), with residual risk stated
honestly. Scope: one connector process (or HA fleet) reading the BDH Hadoop
data mart and writing a Microsoft Graph external connection.

Data classification of what flows through here: **Salesforce business records**
(accounts, contacts, opportunities, cases — routinely PII). Treat every store
this document lists under "data at rest" (see also `SECURITY.md`) at that
classification.

## Trust boundaries

```
[BDH cluster: WebHDFS namenode + datanodes]──(1)──┐
[localpath export mount (SMB/NFS)]─────────(2)──┤
                                                  ▼
                                        [connector process]──(3)──[Microsoft Graph / Entra]
                                            │        │
                                     (4) [state DB: SQLite files / SQL Server]
                                     (5) [dead-letter queue (JSONL / dbo.DeadLetter)]
                                     (6) [service account + host]
                                     (7) [config: filters.json / schema.json / env]
```

## (1) WebHDFS source (namenode + datanode redirects)

| STRIDE | Threat | Mitigation (actual) | Residual |
|---|---|---|---|
| S | Rogue endpoint impersonates the namenode/datanodes | TLS with `CA_BUNDLE_PATH` additive private-CA trust — hostname mismatches always fail (`Infrastructure/HttpTransport.cs`); Kerberos clusters terminate SPNEGO at Knox and the connector talks TLS to Knox | Plain-`http` namenode URLs are accepted; an operator choosing http gets no transport authentication — use https or a Knox front |
| T | Tampered/malformed exports poison the index | Streaming parser hardening: malformed rows counted + skipped, CSV quoting rules, bounded reads; id-less crawls refused outright (`ValidateSourceRecordIds`, `Graph/Ingest.cs`) — an export missing its Id column cannot silently sweep the inventory | A syntactically valid export with WRONG values indexes wrong values; no content authentication exists in BDH |
| R | "Who read what from HDFS?" | Every WebHDFS call counted (`hdfs_calls_total`), run logs per crawl with correlation ids; HDFS-side audit belongs to the cluster | — |
| I | Delegation token leaks via logs/URLs | The token rides only the query string, and log lines deliberately print `uri.AbsolutePath` only — never the query (`Hdfs/WebHdfsClient.cs`); the SIEM doc ships a canary search proving log cleanliness (`docs/SIEM.md`) | The token IS in the URL on the wire — WebHDFS protocol shape; TLS protects transit, proxies must not log full URLs |
| D | Slow/flapping namenode stalls crawls | Retry ladder with 60 s clamp, `hdfs` circuit breaker + degraded-mode pause at safe boundaries (`docs/RESILIENCE.md`) | — |
| E | Connector account over-privileged on HDFS | Deployment guidance mandates a **read-only** HDFS principal (`docs/DEPLOYMENT_ENTERPRISE.md`); the client issues only LISTSTATUS/GETFILESTATUS/OPEN | Enforcement lives in the cluster's authz, not here |

## (2) localpath export mount (`HDFS_MODE=localpath`)

| STRIDE | Threat | Mitigation | Residual |
|---|---|---|---|
| T/E | Path escape out of the export root via crafted names | Sibling-directory escape fixed and pinned by tests: the resolved path is compared against the path-separator-suffixed root, so `/mnt/bdh-export-evil` can never satisfy a `/mnt/bdh-export` root check (`LocalPathSource.Resolve`, `Hdfs/IBdhSource.cs`) | — |
| T | Planted oversize/hostile files | Per-file byte bound enforced by `BoundedStream` (`Hdfs/BdhFileParser.cs`) — the read is *aborted mid-stream* when the bound is exceeded, not merely size-checked up front; oversize skips mark the fetch Incomplete → deletion sweep suppressed | — |
| I | Mount readable/writable too broadly | File-ACL guidance in `docs/DEPLOYMENT_ENTERPRISE.md` (read-only mount for the service account) | OS-level control, not enforced by the connector |
| D | Unbounded directory trees | Partition walk is depth-capped (8 levels) and prunes before I/O (`Hdfs/PartitionScanner.cs`) | — |

## (3) Microsoft Graph / Entra ID

| STRIDE | Threat | Mitigation | Residual |
|---|---|---|---|
| S | Token endpoint spoofing / TLS interception | HTTPS to `AAD_APP_OAUTH_AUTHORITY_HOST`; TLS-inspection proxies supported explicitly via `CA_BUNDLE_PATH` instead of ad-hoc trust hacks | An inspection proxy is by definition a member of the TCB — govern it |
| T | Wrong-connection writes clobbering a sibling connector | Reserved-prefix + shape validation on `CONNECTOR_ID`; README mandates a SEPARATE connection id from the live Salesforce connector (same item-id space) | Two connectors misconfigured onto one id still fight — organisational control |
| R | "Who ingested/deleted what?" | Ingested-item inventory records confirmed puts only; deletion sweeps log per-object counts; correlation id ties log ↔ dead-letter ↔ trace | — |
| I | Over-broad Graph permissions | **Least-privilege set**: `ExternalConnection.ReadWrite.OwnedBy` + `ExternalItem.ReadWrite.OwnedBy` (OwnedBy — only its own connections) + `User.Read.All` (identity sync read); nothing else. Certificate credential (`GRAPH_CLIENT_CERT_*`) preferred over secrets; secret/assertion material never logged (auth MODE only — `Graph/GraphClient.cs`) | `User.Read.All` is tenant-wide read of user profiles; scoping below that is not possible for email→objectId resolution |
| I | ACL widening — a record visible to the wrong people | Zero-principal records are SKIPPED, never world-readable; `FALLBACK_ACL_GROUP_ID` applies only when nothing resolved; identity-directory incomplete → sync REFUSES loudly rather than coarsening ACLs across the crawl (`AclEngine/IdentitySync.cs`) | ACLs are coarse BY DESIGN (owner/group/public) — BDH lacks sharing tables; do not index objects whose sharing model matters beyond ownership |
| D | 429 storms / self-inflicted throttling | Numeric-only Retry-After honoured, 60 s clamp, adaptive `$batch` concurrency dialling 1..max, `graph` breaker + degraded mode | — |
| E | Item-id injection into Graph URLs | `BdhRecord.IsSafeItemId` allows `[A-Za-z0-9_-]{1,128}` only — a hostile `Id` column value cannot become a path traversal in the PUT URL | — |

## (4) State database (SQLite files / SQL Server)

| STRIDE | Threat | Mitigation | Residual |
|---|---|---|---|
| T | SQL injection via record-derived values | Every statement is parameterized (`Infrastructure/SqlExecutor.cs`, `Config/SqlStateStore.cs`); offline ScriptDom validation pins the schema scripts | — |
| T | Corrupted state files redirect crawls | Corruption is detected, WARNED, and handled fail-safe: corrupt sync-state → treated never-synced (wider re-read, never silent), corrupt checkpoint → restart from chunk 0 (idempotent PUTs), torn dead-letter line → that line only (`Config/SyncState.cs`); see `docs/RUNBOOKS.md` "State corruption" | A watermark reset widens the next crawl — cost, not integrity |
| I | State DB readable by others (it holds record ids, error text, possibly payloads) | Least-privilege SQL login documented in `docs/SQL_CONTRACT.md`; file ACLs in `docs/DEPLOYMENT_ENTERPRISE.md`; local state dirs created **owner-only** (0700 POSIX / owner+admins on Windows, best-effort) at startup (`Infrastructure/SecureDirectories.cs`); `DEADLETTER_PAYLOAD_MODE` defaults to `redacted` so record values never reach dead-letter storage | `full` mode is an explicit opt-in; a host where 0700 cannot be set logs a warning (set ACLs out of band) |
| E | HA nodes trusting each other's rows | Claims are leases keyed by node with heartbeats; terminal states never reclaimed; single-row atomic transitions (`Infrastructure/HaCoordinator.cs`) | All HA nodes are one trust domain — a hostile node IS the service account |
| R | Access decisions (item exclusions / ACL restrictions) later disputed or quietly altered | Immutable, append-only, SHA-256 hash-chained **decision ledger** (`Infrastructure/DecisionLedger.cs`, `logs/decisions_<id>.jsonl`) records every EXCLUSION and ACL_RESTRICTION with `Verify()`; any edit, deletion or reorder is detectable | Node-local file — protect it with the same 0700/NTFS ACLs; whoever can delete the whole file loses the audit (detectable as a gap) |

## (5) Dead-letter queue

| STRIDE | Threat | Mitigation | Residual |
|---|---|---|---|
| I | Second copy of business data accumulating outside the index's ACLs | `DEADLETTER_PAYLOAD_MODE` **defaults to `redacted`**: record values are stripped pre-write (ids, object type, error, names, sizes, SHA-256 hashes kept — `Config/DeadLetterRedaction.cs`); an unknown mode **FAILS FAST at config load**; retry-failed re-fetches from BDH so redaction costs no capability (incl. the oversize-inconclusive keep rule) | `full` is an explicit opt-in for fast payload diagnosis — choose it only where the queue's storage is protected like the source |
| T | Poisoned queue entries steering retries | Entries only carry id + object type into the retry; the record is re-read from BDH and re-ACL'd — a tampered payload is never replayed | Whoever can write the queue can add ids to retry; they gain nothing not already granted by (4) |
| D | Unbounded growth | Depth gauge + `dead_letter` webhook alert (`ALERT_DEADLETTER_THRESHOLD`), runbook for drain/triage | Growth is a symptom; the alert exists precisely because there is no silent auto-purge |

## (6) Service account & host

| STRIDE | Threat | Mitigation | Residual |
|---|---|---|---|
| S/E | Interactive or over-privileged service identity | Runs as a non-admin service account (gMSA or virtual account — `docs/DEPLOYMENT_ENTERPRISE.md`); the event-log SOURCE is registered at install time (elevated) so the runtime account needs no registry rights (`scripts/install-windows-service.ps1`) | Host compromise = full connector compromise; that is the platform's boundary |
| I | Secrets on disk / in logs | Secrets only via `SECRET_*` env layering (`.env.local.user`, never committed) or Key Vault; CLI takes no secrets in argv (safe to log args — `Program.cs`); cert credential keeps even the client secret out of existence | `.env.local.user` file ACLs are an operator responsibility |
| R | No trace of the process lifecycle | Event Log lifecycle events 1000/1001 + Warning/Error mirroring (`EVENTLOG_ENABLED`, `docs/SIEM.md`) | Off by default; enable it in production |

## (7) Configuration — and `filters.json` as a SECURITY control

Most config is availability-tuning. **`config/filters.json` is not: it is a
security control** and must be change-managed like one.

- **A weakened or deleted filter is a cost/DoS exposure.** BDH holds 150M+
  rows. Drop a `dt withinLastDays` partition predicate and the next full crawl
  reads years of partitions: hours of HDFS I/O, Graph write quota burned
  against a ~25 items/s connection, row caps tripping, sweeps suppressed —
  a self-inflicted outage with a one-line diff.
- **Fail-closed backstop.** An object with no *effective* filter refuses to
  crawl (`FullScanRefusedException`, `Hdfs/BdhFetcher.cs`). The
  **effectively-filtered rule** closes the loophole: a filter whose only
  predicate is `dt isNotNull` (matches every partition, prunes nothing) does
  NOT count as filtered (`ObjectFilter.IsEffectivelyFiltered`,
  `Filters/FilterConfig.cs`). Bypass requires an explicit, reviewable act:
  `fullScanAllowed` (preferred, per-object, versioned) or `ALLOW_FULL_SCAN=true`
  (global — treat any diff adding it as a red flag).
- **Strict parsing.** Unknown operators/keys, malformed operands, `anyOf`+`allOf`
  together — all fatal at load (`docs/FILTERS.md`). A typo cannot silently
  become an unfiltered scan.
- **Who may change it:** the connector operations owner, via reviewed PR only —
  the same people who may touch `ALLOW_FULL_SCAN` and the deletion-sweep caps.
  Filter edits also move the deletion sweep's source set (an over-tightened
  filter looks like mass deletion; the sweep guards catch it, but review is
  the first line).
- **In change control:** run `validate-config --strict` in the deployment
  pipeline — it errors on unfiltered objects, malformed filters, shard-map
  problems, and (with connectivity) bad endpoints, before production does.
- **Detection:** a `guard_refusals_total` spike or a `sweeps_suppressed_total`
  increment after a config rollout is the canonical "a filter regressed"
  signal (`ops/prometheus-alerts.yml`, `docs/SIEM.md`).

`config/schema.json` (ACL modes! `aclMode: public` is an explicit exposure
decision) and `env` files are change-managed the same way; secrets never live
in either.

## Deletion sweep — mass-deletion defence in depth

The sweep can DELETE from the index, so it is triple-guarded
(`Graph/Ingest.cs`, `docs/DELETION_SYNC.md`):

1. absolute cap `DELETION_SYNC_MAX_ITEMS` (default 1000) — engaged at any
   inventory size;
2. percent guard `DELETION_SYNC_MAX_PERCENT` (default 25%) — engaged at
   meaningful inventory sizes **and whenever a full crawl returned zero rows**
   (the empty-source outage signature), so a small inventory cannot be wiped
   100% unguarded;
3. **Incomplete suppression** — any incomplete fetch (row cap hit OR an
   oversize file skipped) never sweeps: its source id set is partial and every
   unread record would look deleted.

`reconcile --fix` applies the same truncation safety.

## FIPS 140 posture

Audited 2026-07-18 (`grep` over `src/`, `tools/`, `tests/` for
MD5/SHA-1/DES/RC4/TripleDES/RC2/HMACSHA1): **zero hits — no weak crypto
anywhere**, and no identity-critical legacy hashes to grandfather (item ids
are Salesforce record Ids used verbatim, not digests). Cryptography in use:
TLS via the OS stack (FIPS-validated when the host runs in FIPS mode), SHA-256
for dead-letter redaction hashes and the `x5t#S256` claim, RSA-2048+
PKCS#1-SHA-256 for the Graph client assertion, and SQL Server TDE/SQLite file
ACLs for at-rest (platform-level). The connector runs unmodified on Windows
hosts with the FIPS local-security policy enabled.

## Least-privilege summary

| Identity | Needs exactly | Explicitly not |
|---|---|---|
| HDFS principal | read (LISTSTATUS/OPEN) on `{BDH_ROOT_PATH}` | write/delete anywhere; access outside the mart root |
| Entra app | `ExternalConnection.ReadWrite.OwnedBy`, `ExternalItem.ReadWrite.OwnedBy`, `User.Read.All` | `.All` connection variants, Directory.*, Sites.*, Mail.* |
| SQL login | CRUD on the connector schema (`docs/SQL_CONTRACT.md`) | db_owner, server roles |
| Windows service account | read on install+config dirs, write on `logs/`/`data/`, private-key read when using `GRAPH_CLIENT_CERT_THUMBPRINT` | local admin, interactive logon |
