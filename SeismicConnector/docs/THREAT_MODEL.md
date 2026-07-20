# THREAT MODEL — STRIDE per trust boundary

Scope: one connector node (or an HA set) holding Seismic + Graph credentials,
indexing Seismic content into a Microsoft 365 Copilot Graph connection.
Assets: the two API credential sets, the webhook shared secret, indexed
content + ACLs, the state database, dead-letter payloads, and the service
account. Residual risk is stated honestly — "accepted" means we ship with it.

## Boundary 1 — Seismic API (outbound, `api.seismic.com` + tenant token endpoint)

| STRIDE | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| S | Spoofed Seismic endpoint (DNS/MITM) feeds poisoned content | TLS with system trust; `CA_BUNDLE_PATH` is **additive** (CustomRootTrust chain build) and never excuses hostname mismatch (`HttpTransport.ValidateWithAdditiveTrust`) | A CA in the operator-supplied bundle can mint any cert — the bundle file is a root-of-trust artifact; protect it like a credential |
| T | Tampered content in transit | TLS end-to-end; proxy traversal still terminates TLS at the connector unless the org's inspection CA is deliberately added | TLS-inspection appliances see plaintext — org policy decision |
| R | "We never fetched/ingested X" disputes | Run logs (INFO+, rotated), per-crawl reconciliation report, correlation ids on every structured line, and the hash-chained decision ledger (`DECISION_LEDGER`) — one continuous chain across runs, written by every pipeline command and never deleted by log retention | `LOG_RETENTION_DAYS` still ages out run logs and reconciliation reports (the ledger is exempt); the ledger is tamper-*evident*, so a whole-file rewrite is only caught by off-box/WORM shipping. **APPEND access is likewise not defended by the chain**: an appender can compute the next hash and add a correctly chained record that verifies — including one glued onto the end of an existing line, which adds no new physical line and so defeats a line-count/tail-based monitor. Such lines are reported as `LedgerFileDamage.GluedLines` by `ReadFile`/`ResumedDamage`, so monitor that rather than the line count. Crash damage that destroys a record is surfaced, not hidden: the reader resynchronises past destroyed separators, and bytes it cannot read as a record are preserved rather than truncated, with `ReadFile` refusing the file. A destroyed record is **not recovered** — it shows up as a seq gap from `Verify()` when a later record survives it, and as a `ReadFile` refusal when it is the last record (no gap can exist behind the last record). **One case is irreducible and is not covered:** damage confined to a record's *final* byte (the closing brace), and only when that byte is overwritten by whitespace or deleted outright, leaves an incomplete JSON value that is byte-for-byte identical to a partially flushed write, so it is treated as a crash-tail — that record is dropped and the tail truncated, quietly. Measured post-fix by an exhaustive sweep over a real 265-byte final record: 4 of 67,840 single-byte replacements (all 256 values at all 265 offsets) are dropped quietly, all at that one offset (0x09, 0x0a, 0x0d, 0x20), plus deleting that same byte; 0 of 68,096 single-byte insertions and 0 of 265 truncations lose a record silently, and every other combination is recovered or refused. (An earlier release stated this as "2 of 265 offsets" including the closing quote of `Hash`; that came from a five-value replacement alphabet which missed a backslash landing in the `Hash` value — pre-fix the true figure was 3 offsets / 228 of 67,840 combinations.) Off-box/WORM shipping is what covers it. See docs/EXCLUSIONS.md |
| I | Seismic OAuth2 client secret leak | Secrets only in `env/.env.local.user` or Key Vault (`USE_KEY_VAULT`); never logged; never in URLs | Process memory holds the token; host compromise = credential compromise |
| D | Seismic outage / throttling grinds the connector | Retry ladder honoring Retry-After; `seismic` circuit breaker + degraded pause (docs/RESILIENCE.md) | Prolonged outage stalls freshness; RPO bounded by crawl cadence (docs/DR.md) |
| E | Over-scoped Seismic API client | Client-credentials client needs read-only library/teamsite/user scopes only | Seismic-side scoping is operator responsibility |

## Boundary 2 — Microsoft Graph API (outbound)

| STRIDE | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| S | Spoofed token endpoint / Graph endpoint | TLS + system trust (+ additive custom CA, hostname always enforced); sovereign-cloud endpoints are explicit config, not content-derived | — |
| T | Item/ACL tampering in transit | TLS; `$batch` bodies built from typed transforms only | — |
| R | "Who wrote/withdrew this item" | Run logs + correlation ids; withdrawal reasons recorded in the reconciliation report | — |
| I | AAD client secret leak | Prefer the **certificate credential** (`GRAPH_CLIENT_CERT_PATH`/`_THUMBPRINT`): RS256 client_assertion, x5t#S256-bound, 10-min lifetime, fresh `jti`; cert wins over secret; only the auth *mode* is logged | Secret mode remains supported; rotation runbook in SECURITY.md |
| D | Graph 429 storms | Retry-After honored exactly; adaptive `$batch` concurrency backs off on throttle signals; `graph` breaker for real outages | Sustained throttling slows crawls (by design) |
| E | Over-permissioned app registration | Least privilege: `ExternalConnection.ReadWrite.OwnedBy` + `ExternalItem.ReadWrite.OwnedBy` (OwnedBy — the app touches only its own connections) + `User.Read.All`, `Group.Read.All` (read-only, identity mapping). No Sites/Files/Mail scopes. | `User/Group.Read.All` is tenant-wide directory read — required for ACL mapping |

## Boundary 3 — HMAC webhook listener (inbound, `SEISMIC_WEBHOOK_PORT`)

The only unauthenticated-network-reachable write path into the connector.

| STRIDE | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| S | Forged events (fake publish/delete → index manipulation or withdrawal DoS) | `SignatureValidator`: HMAC-SHA256 over the **exact raw bytes**, validated **before any parse/enqueue**, constant-time compare (`CryptographicOperations.FixedTimeEquals`); missing/garbage signature → 401, nothing acted on | Secret-holder can forge; rotation window in SECURITY.md |
| S | No-secret misconfig exposes an open endpoint | **Fail-closed**: port set without `SEISMIC_WEBHOOK_SECRET` → receiver refuses to start; polling remains | — |
| T | Tampered body | Same HMAC — any byte change invalidates | — |
| R | Attack attribution | Every rejection logs remote endpoint + body size (never the signature value); `webhook_rejected_total` metric; SIEM pattern in docs/SIEM.md | HttpListener is plain HTTP — front with TLS/mTLS at the LB for transport privacy |
| I | Timing oracle on the compare | Constant-time compare in the length-matched path | — |
| D | Body bombs / queue exhaustion | 1 MiB body cap (declared AND streamed), 10,000-event drop-oldest queue cap; shed events healed by the next crawl (`webhook_dropped_total`) | A signed flood still costs CPU for HMAC — rate-limit upstream if exposed |
| E | Event triggers privileged action | Events only *schedule* targeted ingest/withdraw of the named content id; all data is re-fetched from Seismic — the event body is never trusted as content | — |

## Boundary 4 — Health endpoint (`HEALTH_PORT`)

| STRIDE | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| I | Metrics leak operational detail (counts, breaker states, dependency names) | No content, no ids, no secrets in `/metrics`; wildcard bind falls back to localhost without a URL ACL | Bind scope + firewalling is deployment policy — keep it on the management network |
| D | Scrape flood | Handlers are allocation-light; listener never throws into the crawl | Accepted (internal endpoint) |
| T/S | It is read-only | No mutating routes exist | — |

## Boundary 5 — State database (SQLite `data/` or SQL Server)

| STRIDE | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| T | Tampered identity mappings widen ACLs (map a principal to a broader Entra object) | DB write access = service account only; SQL backend uses a least-privilege app login (`scripts/sql/create-login.sql`); **never-widen** rule limits blast radius of *unresolved* mappings | A write-capable attacker CAN corrupt mappings — DB ACLs are the real control; alert on out-of-band writes |
| I | Tracked-item metadata discloses catalog shape | DB stores ids/fingerprints/timestamps, not content | Filenames/ids themselves are metadata — protect backups (docs/DR.md) |
| D | DB loss/corruption | Idempotent re-provisioning; full re-crawl rebuilds every table from source truth (docs/DR.md) | Rebuild time = full-crawl time |
| R | Who changed state | SQL auditing is available on the SQL backend | SQLite has no audit trail — accepted for single-node |

## Boundary 6 — Dead-letter queue (`failed_records_*.jsonl` / `dbo.DeadLetter`)

| STRIDE | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| I | Full request payloads (indexed content + ACLs) persisted at rest | `DEADLETTER_PAYLOAD_MODE=redacted` strips content + property values, keeping ids/teamsite/version/error/attempt + SHA-256 stubs; `retry-failed` re-fetches from Seismic, so redaction costs no fidelity | Default is `full` (debuggability); flip to `redacted` where dead-letter files outlive their incident |
| T | Injected records drive bogus retries | Retry re-fetches from source and re-applies exclusion + ACL rules — a forged record can only trigger work that the rules allow anyway | — |

## Boundary 7 — Service account / host

| STRIDE | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| E | Service account over-privilege | Runs as a low-privilege service account; needs only: install-dir read, `logs/`+`data/` write, Event Log **write** to an existing source (creation is done once, elevated, by the install script), outbound 443 | LocalSystem in the experimental MSI default — change the account per docs/DEPLOYMENT_ENTERPRISE.md |
| I | Secrets on disk | `.env.local.user` ACL'd to the service account, or Key Vault with Managed Identity (nothing on disk) | Host admin can read anything — host hardening is the control |

## Boundary 8 — Configuration (`config/*.json`, env files)

| STRIDE | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| T | Weakened `exclusions.json` leaks MNE content | **No-MNE filter is fail-closed**: excluded content is never ingested; late flags are withdrawn on the next incremental (late-exclusion pass); every decision lands in the auditable reconciliation report | Config write access = policy write access; review changes like code |
| T | `SEISMIC_FALLBACK_ACL=tenant` over-shares | Default is `skip` (not ingested); `tenant` only ever applies to content genuinely without principals; **never-widen**: an unresolved identity gap never replaces an applied ACL with a broader one | Choosing `tenant` is an explicit tenant-wide-visibility decision |

## Cross-cutting: compliance-safety invariants (mechanized, not aspirational)

* **Validate-before-parse** on the webhook boundary (`SignatureValidator` +
  raw-byte HMAC before any decode) — stress-tested under concurrent flood.
* **Fail-closed** webhook startup; **fail-fast** on bad transport/cert config.
* **Body/queue caps** bound inbound memory.
* **No-MNE**: excluded content never ingested; late-flagged content withdrawn;
  reconciliation report per crawl.
* **Never-widen ACL**: `AclResult.Unresolved` blocks replacing a real ACL with
  a fallback — stress-tested under identity churn.

## FIPS 140 audit result

Grep of the full source + test tree for `MD5|SHA1|DES|RC4|TripleDES`:
**no non-FIPS primitive is used anywhere.**

| Use | Primitive | FIPS-approved |
| --- | --- | --- |
| Webhook authentication | HMAC-SHA256 | yes |
| ACL fingerprint (`AclResult.Fingerprint`) | SHA-256 (truncated to 128 bits, hex) | yes (truncation of an approved hash) |
| Dead-letter redaction stubs | SHA-256 | yes |
| Graph client_assertion signing | RS256 (RSA-PKCS#1 v1.5 + SHA-256) | yes |
| TLS | platform (SChannel/OpenSSL) | inherits OS FIPS mode |

No stored fingerprint or item id derives from a non-FIPS hash, so **no
migration path is required** — enabling OS FIPS enforcement
(docs/DEPLOYMENT_ENTERPRISE.md) does not invalidate any stored state.
