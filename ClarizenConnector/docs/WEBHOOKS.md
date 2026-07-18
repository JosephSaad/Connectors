# Event-driven incremental (webhooks)

By default the connector is **poll-driven**: full crawls on `--full-crawl-hours`
and incremental crawls (delta cursor `LastUpdatedOn > last-sync`) on
`--incremental-hours`. With a webhook receiver enabled it also reacts to
Clarizen change notifications in near-real-time, turning each event into a
**targeted** unit of work instead of waiting for the next incremental poll.

> **Off by default.** With `CLARIZEN_WEBHOOK_PORT` unset, nothing listens and
> behaviour is unchanged. The receiver only runs in `--continuous`
> full-deployment / ingest.

## Polling ↔ webhook interplay

Webhooks are an **accelerator, not a replacement**. The polling cursor stays
the source of truth and the backstop:

- Every event the receiver drops (during a restart, a network partition, a
  brief outage, or a signature it couldn't validate) is still picked up by the
  next incremental crawl — the cursor never advances past unprocessed changes.
- Deletions detected by a `delete` event are withdrawn immediately; anything
  missed is still caught by the full-crawl existence sweep (`docs/DELETION_SYNC.md`).
- The receiver reuses the pipeline built from the most recent full crawl's
  identity snapshot, so webhook-driven upserts resolve ACLs against the last
  crawled directory. A full crawl refreshes it.

Run webhooks for latency; keep polling for completeness. They compose.

## How it works

```
Clarizen ──POST──▶ WebhookReceiver ──validate sig──▶ parse ──▶ EventDebouncer
                                                                     │
                                                          (coalesce per entity)
                                                                     ▼
                                                             WebhookProcessor
                                                    upsert → RetrieveAsync + IngestSingle
                                                    delete → WithdrawSingle (tombstone)
```

1. **Receive** — `POST {CLARIZEN_WEBHOOK_PATH}` (default `/webhook`) on
   `CLARIZEN_WEBHOOK_PORT`. Body size-capped at 1 MiB.
2. **Validate** (security boundary — see below) — HMAC-SHA256 over the raw
   body. Invalid/missing → `401`, nothing parsed or enqueued.
3. **Parse** — created/updated → **upsert**, deleted → **delete**. Malformed /
   no recognisable events → `400`.
4. **Debounce/coalesce** — events for the same entity within
   `CLARIZEN_WEBHOOK_DEBOUNCE_MS` (default 2000 ms) collapse to one, **last
   writer wins** (a delete after an upsert applies the delete, and vice-versa).
5. **Apply** — upsert re-ingests the record by id (reusing `IngestSingleAsync`:
   inventory-recorded, shard-routed, attachment-enriched); a record that no
   longer exists in Clarizen is withdrawn instead. Delete withdraws the item
   from Graph and the inventory (reusing the round-1 deletion machinery). One
   event's failure never stalls the queue — the polling cursor reconciles it.

Accepted body shapes (tolerant, field aliases case-insensitive):

```json
{"events": [{"entityType": "Task", "id": "/Task/123", "operation": "update"}]}
{"objectType": "Project", "entityId": "9", "action": "deleted"}
[{"type": "Risk", "objectId": "/Risk/7", "changeType": "modified"}]
```

## Security

- **Shared-secret HMAC.** The sender computes `HMAC-SHA256(CLARIZEN_WEBHOOK_SECRET,
  "{timestamp}.{raw-body}")` and sends it in `CLARIZEN_WEBHOOK_SIGNATURE_HEADER`
  (default `X-Clarizen-Signature`), hex (optionally `sha256=`-prefixed) or
  base64, alongside the timestamp in `CLARIZEN_WEBHOOK_TIMESTAMP_HEADER` (default
  `X-Clarizen-Timestamp`, Unix seconds or ISO-8601).
- **Anti-replay.** Because the timestamp is bound INTO the HMAC it cannot be
  altered. The receiver rejects (fail-closed) a request whose timestamp is
  outside `CLARIZEN_WEBHOOK_TIMESTAMP_TOLERANCE_SECONDS` (default 300s), and
  rejects a duplicate signature seen again within that window (a bounded,
  short-lived, self-pruning cache — it cannot grow unbounded). With
  `CLARIZEN_WEBHOOK_REQUIRE_TIMESTAMP=true` (the default) a post without a
  timestamp is rejected; set it to `false` only to migrate legacy senders, which
  then fall back to body-only HMAC **without** replay protection.
- **Validate before act.** The signature (and timestamp) is checked over the
  exact raw bytes **before** any JSON parse or enqueue — an unvalidated payload
  is never interpreted.
- **Constant-time compare** (`CryptographicOperations.FixedTimeEquals`) — no
  timing oracle on the MAC.
- **Fail-closed.** If `CLARIZEN_WEBHOOK_PORT` is set but
  `CLARIZEN_WEBHOOK_SECRET` is missing, the receiver **refuses to start** and
  the connector falls back to polling only — an unauthenticated endpoint is
  never exposed. `validate-config` reports this as an error.
- Only `POST {path}` is accepted (405 otherwise); bodies over 1 MiB are
  rejected (413).
- **Entity-id sanitization.** After normalization ("/Task/123" → "123") the
  local id must be a plain `[A-Za-z0-9._-]` token containing at least one
  letter/digit — anything else (spaces, quotes, `OR 1=1`, `..`, encoded
  traversal) is dropped at parse time with a warning, because the id flows
  into a CZQL `WHERE SYSID = {id}` lookup and a Graph DELETE URL. The
  Clarizen client independently refuses to build a `RetrieveAsync` query for
  a non-token id (defense in depth).
- Terminate TLS at your ingress/reverse proxy; the receiver speaks plain HTTP
  on loopback/behind the proxy, like the health endpoint.

## Configuration

| Var | Default | Meaning |
|---|---|---|
| `CLARIZEN_WEBHOOK_PORT` | unset (off) | Port to listen on. >0 enables the receiver in `--continuous`. |
| `CLARIZEN_WEBHOOK_SECRET` | unset | HMAC shared secret. **Required** when the port is set. |
| `CLARIZEN_WEBHOOK_PATH` | `/webhook` | Path the receiver accepts POSTs on. |
| `CLARIZEN_WEBHOOK_SIGNATURE_HEADER` | `X-Clarizen-Signature` | Header carrying the signature. |
| `CLARIZEN_WEBHOOK_TIMESTAMP_HEADER` | `X-Clarizen-Timestamp` | Header carrying the signed timestamp (Unix seconds or ISO-8601). |
| `CLARIZEN_WEBHOOK_REQUIRE_TIMESTAMP` | `true` | Strict anti-replay: reject posts with no timestamp. `false` = migration (body-only, no replay protection). |
| `CLARIZEN_WEBHOOK_TIMESTAMP_TOLERANCE_SECONDS` | `300` | Freshness window; older/newer requests rejected fail-closed. |
| `CLARIZEN_WEBHOOK_DEBOUNCE_MS` | `2000` | Coalesce window per entity. |
| `CLARIZEN_WEBHOOK_MAX_PENDING` | `100000` | Cap on distinct pending entities (drop-oldest back-pressure). |

## Observability

- `webhook_events_received_total` — posts received (before validation).
- `webhook_events_accepted_total` — validated events enqueued.
- `webhook_events_rejected_total` — bad/missing signature, missing/stale timestamp, replay, malformed, or oversize.
- `webhook_receiver_up` — gauge, 1 while the receiver is bound and listening.

The health endpoint's `/metrics` route surfaces all four, so the receiver's
status is visible alongside the rest of the connector.
