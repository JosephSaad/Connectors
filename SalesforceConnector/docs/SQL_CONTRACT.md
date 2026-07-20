# SQL Server Backend & HA Contract

Binding contract for the SQL Server state backend and active-active HA. All
implementations (DDL scripts, C# providers, HA coordinator) conform to this
document. Database name default: `SalesforceConnector`. All timestamps are
UTC `datetime2(7)`. All access from C# goes through `Microsoft.Data.SqlClient`
using the stored procedures below (no inline SQL in app code, except
`sp_getapplock` wrappers).

## Environment variables

| Variable | Default | Meaning |
|---|---|---|
| `USE_SQL_SERVER` | `false` | `true` → identity store, checkpoints, sync state and dead-letter all use SQL Server; SQLite/files otherwise. |
| `SQL_CONNECTION_STRING` | — | Required when `USE_SQL_SERVER=true`. Point at the AG listener for HA. |
| `HA_MODE` | `false` | `true` → multi-node active-active crawl coordination (requires `USE_SQL_SERVER=true`; startup error otherwise). |
| `NODE_ID` | machine name | Stable identifier of this node in claims/heartbeats. |
| `HA_CLAIM_TIMEOUT_SECONDS` | `300` | A claim whose heartbeat is older than this is considered abandoned and reclaimable. |
| `HA_HEARTBEAT_SECONDS` | `60` | Heartbeat interval while a node works an object claim. |
| `LOG_RETENTION_DAYS` | `0` | `> 0` → prune `logs/` run directories (and, in SQL mode, dead-letter/session/crawl history) older than N days at command start and each continuous cycle. |

## Tables (schema `dbo`)

```
Groups          (ConnectionId nvarchar(64), GroupId nvarchar(128), DisplayName nvarchar(512),
                 Description nvarchar(max), CreatedUtc, UpdatedUtc,
                 PK (ConnectionId, GroupId))
GroupMembers    (ConnectionId nvarchar(64), GroupId nvarchar(128), MemberId nvarchar(256),
                 MemberType nvarchar(32), IdentitySource nvarchar(32) DEFAULT 'external',
                 PK (ConnectionId, GroupId, MemberId, MemberType))
SyncSessions    (SessionId uniqueidentifier PK, ConnectionId, CrawlType nvarchar(32),
                 SyncType nvarchar(16), Status nvarchar(16), StartedUtc, CompletedUtc NULL,
                 StatsJson nvarchar(max) NULL)   -- serialized SyncSessionStats, PyJson format
FieldCache      (InstanceHash nvarchar(16), ObjectType nvarchar(128), FieldsJson nvarchar(max),
                 UpdatedUtc, PK (InstanceHash, ObjectType))
FlsCache        (InstanceHash nvarchar(16), ObjectType nvarchar(128), PermissionsJson nvarchar(max),
                 UpdatedUtc, PK (InstanceHash, ObjectType))   -- WP-SF-2 field-level security
SyncState       (ConnectorId nvarchar(64) PK, LastSyncUtc)
Checkpoints     (ConnectorId nvarchar(64), ObjectType nvarchar(128), ChunkIndex int,
                 SinceIso nvarchar(64) NULL, UpdatedUtc,
                 PK (ConnectorId, ObjectType))
DeadLetter      (Id bigint IDENTITY PK, ConnectorId, ItemId nvarchar(64), ObjectType nvarchar(128),
                 Error nvarchar(max), RequestBody nvarchar(max) NULL, ResponseBody nvarchar(max) NULL,
                 FailedUtc, RetriedUtc NULL,
                 BatchId uniqueidentifier NULL,  -- idempotency key of the usp_AppendDeadLetter call
                 INDEX IX_DeadLetter_Connector (ConnectorId, RetriedUtc),
                 INDEX IX_DeadLetter_Batch (BatchId))
CrawlSessions   (CrawlId uniqueidentifier PK, ConnectorId, CrawlKind nvarchar(16),  -- full|incremental
                 Status nvarchar(16),  -- open|closed|failed
                 SinceIso nvarchar(64) NULL, StartedUtc, ClosedUtc NULL, CreatedBy nvarchar(128),
                 ClosedBy nvarchar(128) NULL)  -- NodeId that won the open→closed transition
ObjectClaims    (CrawlId uniqueidentifier, ObjectType nvarchar(128), NodeId nvarchar(128) NULL,
                 Status nvarchar(16),  -- pending|claimed|done|failed
                 ClaimedUtc NULL, HeartbeatUtc NULL, CompletedUtc NULL,
                 ClaimToken uniqueidentifier NULL,  -- idempotency key of the claiming usp_ClaimNextObject call
                 PK (CrawlId, ObjectType))
ItemInventory   (ConnectorId nvarchar(64), ItemId nvarchar(256), ObjectType nvarchar(128),
                 LastSeenUtc,  -- ingested-item inventory behind `reconcile`; shared across HA nodes
                 PK (ConnectorId, ItemId),
                 INDEX IX_ItemInventory_Object (ConnectorId, ObjectType))
```

Existing databases migrate in place: `create-database.sql` is idempotent
(CREATE-if-missing tables, `CREATE OR ALTER` views/procs) and adds the
post-v1 columns above via guarded ALTERs
(`IF COL_LENGTH(...) IS NULL ALTER TABLE ... ADD ...`) — re-running the
script upgrades a v1 database without touching data.

### Commit-ack-loss idempotency

`SqlExecutor` retries a whole unit (open + body) on a transient fault. When a
proc's COMMIT succeeds but the ack is lost (connection dies at exactly the
wrong moment), the retry re-runs a committed operation — so every mutating
proc whose re-run is not naturally harmless carries a client-generated
idempotency key: `usp_StartSession` guards on `@SessionId`,
`usp_AppendDeadLetter` on `@BatchId`, `usp_ClaimNextObject` on `@ClaimToken`,
and `usp_OpenOrJoinCrawl` / `usp_CloseCrawlIfComplete` derive their
`Created` / `Closed` flags from persisted state (`CreatedBy` / `ClosedBy`)
instead of "this call changed a row". C# callers generate each key OUTSIDE
the `SqlExecutor` retry lambda so every retry of the unit presents the same
key.

## Views

- `vGroupMemberCounts` — per connection/group: member count, last update.
- `vLastSessions` — most recent SyncSession per (ConnectionId, CrawlType).
- `vDeadLetterSummary` — per connector/object type: unretried count, oldest/newest failure.
- `vActiveCrawls` — open CrawlSessions joined with claim progress (pending/claimed/done counts, per-node breakdown).

## Stored procedures

Identity store (mirror the SQLite semantics exactly):
- `usp_UpsertGroup(@ConnectionId, @GroupId, @DisplayName, @Description)`
- `usp_DeleteGroup(@ConnectionId, @GroupId)` — cascades members.
- `usp_GetGroups(@ConnectionId)` / `usp_GetMembers(@ConnectionId, @GroupId)`
- `usp_ReplaceGroupMembers(@ConnectionId, @GroupId, @MembersJson)` — transactional
  delete+insert; `@MembersJson` = `[{"memberId":..., "memberType":..., "identitySource":...}]` via OPENJSON.
- `usp_AddMember` / `usp_RemoveMember`
- `usp_StartSession(@SessionId, @ConnectionId, @CrawlType, @SyncType)` — idempotent
  on the client-generated `@SessionId` (`IF NOT EXISTS` guard): a commit-ack-loss
  retry is a no-op, not a 2627 PK violation.
- `usp_CompleteSession(@SessionId, @Status, @StatsJson)`
- `usp_GetLastSession(@ConnectionId, @CrawlType = NULL)` — latest completed row.
- `usp_GetLastSuccessfulContentCrawl(@ConnectionId)` — returns CompletedUtc.
- `usp_GetCachedFields(@InstanceHash, @ObjectType)` / `usp_SaveCachedFields(...)` / `usp_ClearFieldCache(@InstanceHash = NULL, @ObjectType = NULL)`
- `usp_GetCachedFls(@InstanceHash, @ObjectType)` / `usp_SaveCachedFls(@InstanceHash, @ObjectType, @PermissionsJson)` /
  `usp_ClearFlsCache(@InstanceHash = NULL, @ObjectType = NULL)` — WP-SF-2 field-level
  security cache. Same key and same semantics as the field cache above; the payload is
  `AclEngine.FlsObjectPermissions.ToJson()`.

State (mirror `config/sync_state.py` file semantics):
- `usp_ReadLastSync(@ConnectorId)` / `usp_WriteLastSync(@ConnectorId, @LastSyncUtc)`
- `usp_ReadCheckpoints(@ConnectorId)` — all rows; C# reassembles the `{completed: {obj: chunk}}` shape.
- `usp_WriteCheckpoint(@ConnectorId, @ObjectType, @ChunkIndex, @SinceIso)` (MERGE upsert)
- `usp_ClearCheckpoints(@ConnectorId)`
- `usp_AppendDeadLetter(@ConnectorId, @RecordsJson, @BatchId = NULL)` — bulk insert via
  OPENJSON (atomic; replaces the file-lock approach). `@BatchId` is generated by the
  caller per append (stored on the rows); when a batch with that id already exists the
  whole insert is skipped, so a commit-ack-loss retry cannot duplicate the batch.
  The `@RecordsJson` record shape is unchanged.
- `usp_ReadDeadLetter(@ConnectorId)` — unretried rows. `usp_MarkDeadLetterRetried(@Ids)` / `usp_ClearDeadLetter(@ConnectorId)`

Ingested-item inventory (mirror `Graph/ItemInventory` SQLite semantics; the shared table lets HA nodes and `reconcile` see one inventory):
- `usp_RecordInventoryItems(@ConnectorId, @ItemsJson, @SeenUtc)` — MERGE `WITH (HOLDLOCK)` upsert
  keyed on (ConnectorId, ItemId); `@ItemsJson` = `[{"itemId":..., "objectType":...}]` via OPENJSON.
  A matched row has ObjectType refreshed and LastSeenUtc re-stamped, else a row is inserted.
- `usp_RemoveInventoryItems(@ConnectorId, @ItemIdsJson)` — delete the listed ids
  (`@ItemIdsJson` = `["id1", "id2"]` via OPENJSON); a missing id is a no-op.
- `usp_GetInventoryByObject(@ConnectorId, @ObjectType)` — inventoried item ids for one object type, ordered by ItemId.
- `usp_GetInventoryAll(@ConnectorId)` — (ItemId, ObjectType) rows for the connector, ordered by ItemId; C# groups by ObjectType.
- `usp_CountInventory(@ConnectorId)` — total inventoried item count.

HA coordination:
- `usp_OpenOrJoinCrawl(@ConnectorId, @CrawlKind, @SinceIso, @NodeId, @ObjectTypesJson)` —
  under `sp_getapplock('SFC_<connector>', 'Exclusive')`: return the open crawl if one exists,
  else create CrawlSessions row + one pending ObjectClaims row per object type
  (`SELECT DISTINCT` over the JSON, so a duplicated objectName in schema.json cannot
  violate the claims PK). Creating a `full` crawl also deletes the connector's
  `Checkpoints` rows in the same transaction (the C# creator-side clear stays and
  becomes a harmless no-op); DeadLetter is NOT cleared here — client behavior differs
  per command. Returns CrawlId + `Created` derived as
  `CreatedBy = @NodeId AND Status = 'open'` (not "this call inserted"), so a
  commit-ack-loss retry by the creator still reports Created = 1 and the
  creator-only reset is not skipped.
- `usp_ClaimNextObject(@CrawlId, @NodeId, @ClaimTimeoutSeconds, @ClaimToken = NULL)` —
  atomically (`UPDLOCK, READPAST`) claim one `pending` claim, or reclaim a `claimed`
  row whose HeartbeatUtc is stale; returns ObjectType or NULL. `@ClaimToken` is
  generated by the caller per call (per call, NOT per node — one node runs several
  concurrent workers) and persisted on the claimed row; before claiming anew the proc
  returns any row in the crawl already carrying the token, so a commit-ack-loss retry
  gets its committed claim back instead of double-claiming (which would strand the
  first object until the claim timeout — and stall the crawl close a cycle if it was
  the last one).
- `usp_HeartbeatClaim(@CrawlId, @ObjectType, @NodeId)`
- `usp_CompleteClaim(@CrawlId, @ObjectType, @NodeId, @Status)` — done|failed.
- `usp_CloseCrawlIfComplete(@CrawlId, @NodeId = NULL)` — when no pending/claimed rows
  remain, close the session, recording `@NodeId` in `ClosedBy`; returns Closed = 1 iff
  the crawl is closed AND `ClosedBy = @NodeId` (that node writes the sync timestamp).
  Exactly one node ever wins the open→closed UPDATE, so exactly one gets 1 under
  concurrency — and, because the result derives from `ClosedBy` rather than
  `@@ROWCOUNT`, a commit-ack-loss retry by the closer still gets 1 (no node would
  otherwise record last-sync / clear the checkpoint / log the content crawl, leaving a
  stale incremental watermark). `@NodeId = NULL` (legacy) keeps the old
  `@@ROWCOUNT` semantics.
- `usp_PruneHistory(@RetentionDays)` — deletes DeadLetter (retried), SyncSessions,
  CrawlSessions + ObjectClaims older than the cutoff. Open crawls are never pruned;
  SyncSessions rows still `running` past the cutoff are zombies from crashed runs and
  ARE pruned.

## HA semantics (active-active)

- All nodes run the same `--continuous` command. When a cycle is due, every node calls
  `usp_OpenOrJoinCrawl`; exactly one creates, the rest join. Nodes loop `usp_ClaimNextObject`
  → ingest that object type (existing per-object pipeline, checkpoint per object in SQL) →
  `usp_CompleteClaim` → claim next, until NULL. The node whose
  `usp_CloseCrawlIfComplete(@CrawlId, @NodeId)` returns 1 (recorded in `ClosedBy`)
  writes the last-sync timestamp.
- Heartbeat runs on a background task per active claim. Node death → heartbeat goes stale →
  another node reclaims via `usp_ClaimNextObject`, resuming from the object's checkpoint.
- Graph 429 quota is per connection, shared by all nodes: in HA_MODE, operators should size
  `GRAPH_BATCH_WORKERS` per node as (single-node value ÷ node count). Retries stay per-node.
