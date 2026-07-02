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
SyncState       (ConnectorId nvarchar(64) PK, LastSyncUtc)
Checkpoints     (ConnectorId nvarchar(64), ObjectType nvarchar(128), ChunkIndex int,
                 SinceIso nvarchar(64) NULL, UpdatedUtc,
                 PK (ConnectorId, ObjectType))
DeadLetter      (Id bigint IDENTITY PK, ConnectorId, ItemId nvarchar(64), ObjectType nvarchar(128),
                 Error nvarchar(max), RequestBody nvarchar(max) NULL, ResponseBody nvarchar(max) NULL,
                 FailedUtc, RetriedUtc NULL, INDEX IX_DeadLetter_Connector (ConnectorId, RetriedUtc))
CrawlSessions   (CrawlId uniqueidentifier PK, ConnectorId, CrawlKind nvarchar(16),  -- full|incremental
                 Status nvarchar(16),  -- open|closed|failed
                 SinceIso nvarchar(64) NULL, StartedUtc, ClosedUtc NULL, CreatedBy nvarchar(128))
ObjectClaims    (CrawlId uniqueidentifier, ObjectType nvarchar(128), NodeId nvarchar(128) NULL,
                 Status nvarchar(16),  -- pending|claimed|done|failed
                 ClaimedUtc NULL, HeartbeatUtc NULL, CompletedUtc NULL,
                 PK (CrawlId, ObjectType))
```

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
- `usp_StartSession(@SessionId, @ConnectionId, @CrawlType, @SyncType)`
- `usp_CompleteSession(@SessionId, @Status, @StatsJson)`
- `usp_GetLastSession(@ConnectionId, @CrawlType = NULL)` — latest completed row.
- `usp_GetLastSuccessfulContentCrawl(@ConnectionId)` — returns CompletedUtc.
- `usp_GetCachedFields(@InstanceHash, @ObjectType)` / `usp_SaveCachedFields(...)` / `usp_ClearFieldCache(@InstanceHash = NULL, @ObjectType = NULL)`

State (mirror `config/sync_state.py` file semantics):
- `usp_ReadLastSync(@ConnectorId)` / `usp_WriteLastSync(@ConnectorId, @LastSyncUtc)`
- `usp_ReadCheckpoints(@ConnectorId)` — all rows; C# reassembles the `{completed: {obj: chunk}}` shape.
- `usp_WriteCheckpoint(@ConnectorId, @ObjectType, @ChunkIndex, @SinceIso)` (MERGE upsert)
- `usp_ClearCheckpoints(@ConnectorId)`
- `usp_AppendDeadLetter(@ConnectorId, @RecordsJson)` — bulk insert via OPENJSON (atomic; replaces the file-lock approach).
- `usp_ReadDeadLetter(@ConnectorId)` — unretried rows. `usp_MarkDeadLetterRetried(@Ids)` / `usp_ClearDeadLetter(@ConnectorId)`

HA coordination:
- `usp_OpenOrJoinCrawl(@ConnectorId, @CrawlKind, @SinceIso, @NodeId, @ObjectTypesJson)` —
  under `sp_getapplock('SFC_<connector>', 'Exclusive')`: return the open crawl if one exists,
  else create CrawlSessions row + one pending ObjectClaims row per object type. Returns CrawlId + created flag.
- `usp_ClaimNextObject(@CrawlId, @NodeId, @ClaimTimeoutSeconds)` — atomically
  (`UPDLOCK, READPAST`) claim one `pending` claim, or reclaim a `claimed` row whose
  HeartbeatUtc is stale; returns ObjectType or NULL.
- `usp_HeartbeatClaim(@CrawlId, @ObjectType, @NodeId)`
- `usp_CompleteClaim(@CrawlId, @ObjectType, @NodeId, @Status)` — done|failed.
- `usp_CloseCrawlIfComplete(@CrawlId)` — when no pending/claimed rows remain, close the
  session; returns 1 when this call performed the close (that caller writes the sync timestamp).
- `usp_PruneHistory(@RetentionDays)` — deletes DeadLetter (retried), SyncSessions,
  CrawlSessions + ObjectClaims older than the cutoff.

## HA semantics (active-active)

- All nodes run the same `--continuous` command. When a cycle is due, every node calls
  `usp_OpenOrJoinCrawl`; exactly one creates, the rest join. Nodes loop `usp_ClaimNextObject`
  → ingest that object type (existing per-object pipeline, checkpoint per object in SQL) →
  `usp_CompleteClaim` → claim next, until NULL. Whoever gets `usp_CloseCrawlIfComplete() == 1`
  writes the last-sync timestamp.
- Heartbeat runs on a background task per active claim. Node death → heartbeat goes stale →
  another node reclaims via `usp_ClaimNextObject`, resuming from the object's checkpoint.
- Graph 429 quota is per connection, shared by all nodes: in HA_MODE, operators should size
  `GRAPH_BATCH_WORKERS` per node as (single-node value ÷ node count). Retries stay per-node.
