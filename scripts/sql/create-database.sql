-- Copyright (c) Microsoft Corporation.
-- Licensed under the MIT License.

-- scripts/sql/create-database.sql
-- --------------------------------
-- Idempotent provisioning script for the Salesforce Copilot Connector SQL
-- Server state backend (see docs/SQL_CONTRACT.md — this file implements that
-- contract exactly).  Safe to run repeatedly: tables are created only when
-- missing; views and stored procedures use CREATE OR ALTER.
--
-- Run in SQLCMD mode, e.g.:
--
--     sqlcmd -S <server> -i create-database.sql
--     sqlcmd -S <server> -v DatabaseName=MyConnectorDb -i create-database.sql
--
-- High availability
-- -----------------
-- For active-active HA, run this script against an Always On Availability
-- Group listener (the database must be joined to the AG).  The application
-- coordinates multi-node crawls with sp_getapplock and UPDLOCK/READPAST
-- claiming (usp_OpenOrJoinCrawl / usp_ClaimNextObject), both of which work
-- transparently on the AG primary — no additional configuration is required
-- beyond pointing SQL_CONNECTION_STRING at the listener.

:setvar DatabaseName SalesforceConnector

IF DB_ID(N'$(DatabaseName)') IS NULL
    CREATE DATABASE [$(DatabaseName)];
GO

USE [$(DatabaseName)];
GO

-- ═════════════════════════════════════════════════════════════════════════════
-- Tables (schema dbo)
-- ═════════════════════════════════════════════════════════════════════════════

-- One row per external group published to Microsoft Graph.
IF OBJECT_ID(N'dbo.Groups', N'U') IS NULL
CREATE TABLE dbo.Groups
(
    ConnectionId  nvarchar(64)   NOT NULL,
    GroupId       nvarchar(128)  NOT NULL,
    DisplayName   nvarchar(512)  NOT NULL CONSTRAINT DF_Groups_DisplayName DEFAULT (N''),
    Description   nvarchar(max)  NOT NULL CONSTRAINT DF_Groups_Description DEFAULT (N''),
    CreatedUtc    datetime2(7)   NOT NULL,
    UpdatedUtc    datetime2(7)   NOT NULL,
    CONSTRAINT PK_Groups PRIMARY KEY (ConnectionId, GroupId)
);
GO

-- One row per member (user or nested group) of an external group.
IF OBJECT_ID(N'dbo.GroupMembers', N'U') IS NULL
CREATE TABLE dbo.GroupMembers
(
    ConnectionId   nvarchar(64)   NOT NULL,
    GroupId        nvarchar(128)  NOT NULL,
    MemberId       nvarchar(256)  NOT NULL,
    MemberType     nvarchar(32)   NOT NULL,
    IdentitySource nvarchar(32)   NOT NULL CONSTRAINT DF_GroupMembers_IdentitySource DEFAULT (N'external'),
    CONSTRAINT PK_GroupMembers PRIMARY KEY (ConnectionId, GroupId, MemberId, MemberType),
    CONSTRAINT FK_GroupMembers_Groups FOREIGN KEY (ConnectionId, GroupId)
        REFERENCES dbo.Groups (ConnectionId, GroupId) ON DELETE CASCADE
);
GO

-- Audit log of every identity/content sync run.
-- StatsJson: serialized SyncSessionStats, PyJson format (snake_case keys).
IF OBJECT_ID(N'dbo.SyncSessions', N'U') IS NULL
CREATE TABLE dbo.SyncSessions
(
    SessionId    uniqueidentifier NOT NULL CONSTRAINT PK_SyncSessions PRIMARY KEY,
    ConnectionId nvarchar(64)     NOT NULL,
    CrawlType    nvarchar(32)     NOT NULL,
    SyncType     nvarchar(16)     NOT NULL,
    Status       nvarchar(16)     NOT NULL,
    StartedUtc   datetime2(7)     NOT NULL,
    CompletedUtc datetime2(7)     NULL,
    StatsJson    nvarchar(max)    NULL
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SyncSessions_Lookup' AND object_id = OBJECT_ID(N'dbo.SyncSessions'))
CREATE INDEX IX_SyncSessions_Lookup
    ON dbo.SyncSessions (ConnectionId, Status, CrawlType, CompletedUtc DESC);
GO

-- Per-org, per-object working field lists (skips the INVALID_FIELD retry loop).
IF OBJECT_ID(N'dbo.FieldCache', N'U') IS NULL
CREATE TABLE dbo.FieldCache
(
    InstanceHash nvarchar(16)  NOT NULL,
    ObjectType   nvarchar(128) NOT NULL,
    FieldsJson   nvarchar(max) NOT NULL,
    UpdatedUtc   datetime2(7)  NOT NULL,
    CONSTRAINT PK_FieldCache PRIMARY KEY (InstanceHash, ObjectType)
);
GO

-- Last successful sync timestamp per connector (mirrors config/sync_state files).
IF OBJECT_ID(N'dbo.SyncState', N'U') IS NULL
CREATE TABLE dbo.SyncState
(
    ConnectorId nvarchar(64) NOT NULL CONSTRAINT PK_SyncState PRIMARY KEY,
    LastSyncUtc datetime2(7) NOT NULL
);
GO

-- Per-object crawl resume checkpoints.
IF OBJECT_ID(N'dbo.Checkpoints', N'U') IS NULL
CREATE TABLE dbo.Checkpoints
(
    ConnectorId nvarchar(64)  NOT NULL,
    ObjectType  nvarchar(128) NOT NULL,
    ChunkIndex  int           NOT NULL,
    SinceIso    nvarchar(64)  NULL,
    UpdatedUtc  datetime2(7)  NOT NULL,
    CONSTRAINT PK_Checkpoints PRIMARY KEY (ConnectorId, ObjectType)
);
GO

-- Failed ingestion records (replaces the failed-records JSONL files).
IF OBJECT_ID(N'dbo.DeadLetter', N'U') IS NULL
CREATE TABLE dbo.DeadLetter
(
    Id           bigint IDENTITY (1, 1) NOT NULL CONSTRAINT PK_DeadLetter PRIMARY KEY,
    ConnectorId  nvarchar(64)  NOT NULL,
    ItemId       nvarchar(64)  NOT NULL,
    ObjectType   nvarchar(128) NOT NULL,
    Error        nvarchar(max) NOT NULL,
    RequestBody  nvarchar(max) NULL,
    ResponseBody nvarchar(max) NULL,
    FailedUtc    datetime2(7)  NOT NULL,
    RetriedUtc   datetime2(7)  NULL
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_DeadLetter_Connector' AND object_id = OBJECT_ID(N'dbo.DeadLetter'))
CREATE INDEX IX_DeadLetter_Connector ON dbo.DeadLetter (ConnectorId, RetriedUtc);
GO

-- One row per coordinated (multi-node) crawl cycle.
IF OBJECT_ID(N'dbo.CrawlSessions', N'U') IS NULL
CREATE TABLE dbo.CrawlSessions
(
    CrawlId     uniqueidentifier NOT NULL CONSTRAINT PK_CrawlSessions PRIMARY KEY,
    ConnectorId nvarchar(64)     NOT NULL,
    CrawlKind   nvarchar(16)     NOT NULL,  -- full|incremental
    Status      nvarchar(16)     NOT NULL,  -- open|closed|failed
    SinceIso    nvarchar(64)     NULL,
    StartedUtc  datetime2(7)     NOT NULL,
    ClosedUtc   datetime2(7)     NULL,
    CreatedBy   nvarchar(128)    NOT NULL
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CrawlSessions_Connector' AND object_id = OBJECT_ID(N'dbo.CrawlSessions'))
CREATE INDEX IX_CrawlSessions_Connector ON dbo.CrawlSessions (ConnectorId, Status);
GO

-- One row per object type within a coordinated crawl; nodes claim rows.
IF OBJECT_ID(N'dbo.ObjectClaims', N'U') IS NULL
CREATE TABLE dbo.ObjectClaims
(
    CrawlId      uniqueidentifier NOT NULL,
    ObjectType   nvarchar(128)    NOT NULL,
    NodeId       nvarchar(128)    NULL,
    Status       nvarchar(16)     NOT NULL,  -- pending|claimed|done|failed
    ClaimedUtc   datetime2(7)     NULL,
    HeartbeatUtc datetime2(7)     NULL,
    CompletedUtc datetime2(7)     NULL,
    CONSTRAINT PK_ObjectClaims PRIMARY KEY (CrawlId, ObjectType),
    CONSTRAINT FK_ObjectClaims_CrawlSessions FOREIGN KEY (CrawlId)
        REFERENCES dbo.CrawlSessions (CrawlId) ON DELETE CASCADE
);
GO

-- ═════════════════════════════════════════════════════════════════════════════
-- Views
-- ═════════════════════════════════════════════════════════════════════════════

-- Per connection/group: member count and last update.
CREATE OR ALTER VIEW dbo.vGroupMemberCounts
AS
SELECT g.ConnectionId,
       g.GroupId,
       g.DisplayName,
       COUNT(m.MemberId) AS MemberCount,
       g.UpdatedUtc      AS LastUpdatedUtc
FROM dbo.Groups g
LEFT JOIN dbo.GroupMembers m
    ON m.ConnectionId = g.ConnectionId AND m.GroupId = g.GroupId
GROUP BY g.ConnectionId, g.GroupId, g.DisplayName, g.UpdatedUtc;
GO

-- Most recent SyncSession per (ConnectionId, CrawlType).
CREATE OR ALTER VIEW dbo.vLastSessions
AS
SELECT SessionId, ConnectionId, CrawlType, SyncType, Status, StartedUtc, CompletedUtc, StatsJson
FROM (
    SELECT s.*,
           ROW_NUMBER() OVER (PARTITION BY s.ConnectionId, s.CrawlType
                              ORDER BY COALESCE(s.CompletedUtc, s.StartedUtc) DESC) AS rn
    FROM dbo.SyncSessions s
) ranked
WHERE ranked.rn = 1;
GO

-- Per connector/object type: unretried count and oldest/newest failure.
CREATE OR ALTER VIEW dbo.vDeadLetterSummary
AS
SELECT ConnectorId,
       ObjectType,
       SUM(CASE WHEN RetriedUtc IS NULL THEN 1 ELSE 0 END) AS UnretriedCount,
       MIN(FailedUtc) AS OldestFailedUtc,
       MAX(FailedUtc) AS NewestFailedUtc
FROM dbo.DeadLetter
GROUP BY ConnectorId, ObjectType;
GO

-- Open crawls joined with claim progress; one row per (crawl, node) with the
-- node's claim counts (NodeId NULL = unclaimed/pending rows).
CREATE OR ALTER VIEW dbo.vActiveCrawls
AS
SELECT cs.CrawlId,
       cs.ConnectorId,
       cs.CrawlKind,
       cs.SinceIso,
       cs.StartedUtc,
       cs.CreatedBy,
       oc.NodeId,
       SUM(CASE WHEN oc.Status = N'pending' THEN 1 ELSE 0 END) AS PendingCount,
       SUM(CASE WHEN oc.Status = N'claimed' THEN 1 ELSE 0 END) AS ClaimedCount,
       SUM(CASE WHEN oc.Status = N'done'    THEN 1 ELSE 0 END) AS DoneCount,
       SUM(CASE WHEN oc.Status = N'failed'  THEN 1 ELSE 0 END) AS FailedCount
FROM dbo.CrawlSessions cs
LEFT JOIN dbo.ObjectClaims oc
    ON oc.CrawlId = cs.CrawlId
WHERE cs.Status = N'open'
GROUP BY cs.CrawlId, cs.ConnectorId, cs.CrawlKind, cs.SinceIso, cs.StartedUtc, cs.CreatedBy, oc.NodeId;
GO

-- ═════════════════════════════════════════════════════════════════════════════
-- Stored procedures — identity store (mirror the SQLite semantics exactly)
-- ═════════════════════════════════════════════════════════════════════════════

-- Insert or update a group record.
CREATE OR ALTER PROCEDURE dbo.usp_UpsertGroup
    @ConnectionId nvarchar(64),
    @GroupId      nvarchar(128),
    @DisplayName  nvarchar(512) = N'',
    @Description  nvarchar(max) = N''
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
        UPDATE dbo.Groups WITH (UPDLOCK, SERIALIZABLE)
           SET DisplayName = @DisplayName,
               Description = @Description,
               UpdatedUtc  = SYSUTCDATETIME()
         WHERE ConnectionId = @ConnectionId AND GroupId = @GroupId;
        IF @@ROWCOUNT = 0
            INSERT INTO dbo.Groups (ConnectionId, GroupId, DisplayName, Description, CreatedUtc, UpdatedUtc)
            VALUES (@ConnectionId, @GroupId, @DisplayName, @Description, SYSUTCDATETIME(), SYSUTCDATETIME());
    COMMIT TRANSACTION;
END;
GO

-- Delete a group; members cascade via FK_GroupMembers_Groups.
CREATE OR ALTER PROCEDURE dbo.usp_DeleteGroup
    @ConnectionId nvarchar(64),
    @GroupId      nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
        DELETE FROM dbo.Groups
         WHERE ConnectionId = @ConnectionId AND GroupId = @GroupId;
    COMMIT TRANSACTION;
END;
GO

-- All groups tracked for a connection.
CREATE OR ALTER PROCEDURE dbo.usp_GetGroups
    @ConnectionId nvarchar(64)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT GroupId, DisplayName, Description, CreatedUtc, UpdatedUtc
    FROM dbo.Groups
    WHERE ConnectionId = @ConnectionId;
END;
GO

-- All current members of a group.
CREATE OR ALTER PROCEDURE dbo.usp_GetMembers
    @ConnectionId nvarchar(64),
    @GroupId      nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT MemberId, MemberType, IdentitySource
    FROM dbo.GroupMembers
    WHERE ConnectionId = @ConnectionId AND GroupId = @GroupId;
END;
GO

-- Transactional delete+insert of a group's full membership.
-- @MembersJson = [{"memberId": "...", "memberType": "...", "identitySource": "..."}]
CREATE OR ALTER PROCEDURE dbo.usp_ReplaceGroupMembers
    @ConnectionId nvarchar(64),
    @GroupId      nvarchar(128),
    @MembersJson  nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
        DELETE FROM dbo.GroupMembers
         WHERE ConnectionId = @ConnectionId AND GroupId = @GroupId;
        INSERT INTO dbo.GroupMembers (ConnectionId, GroupId, MemberId, MemberType, IdentitySource)
        SELECT @ConnectionId, @GroupId, j.MemberId, j.MemberType, ISNULL(j.IdentitySource, N'external')
        FROM OPENJSON(@MembersJson)
        WITH (
            MemberId       nvarchar(256) '$.memberId',
            MemberType     nvarchar(32)  '$.memberType',
            IdentitySource nvarchar(32)  '$.identitySource'
        ) j;
    COMMIT TRANSACTION;
END;
GO

-- Add a single member.  Mirrors the SQLite ``INSERT OR IGNORE`` keyed on
-- (group, member_id): a second insert for the same member id is a no-op
-- regardless of member type.
CREATE OR ALTER PROCEDURE dbo.usp_AddMember
    @ConnectionId   nvarchar(64),
    @GroupId        nvarchar(128),
    @MemberId       nvarchar(256),
    @MemberType     nvarchar(32),
    @IdentitySource nvarchar(32) = N'external'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
        IF NOT EXISTS (SELECT 1 FROM dbo.GroupMembers WITH (UPDLOCK, SERIALIZABLE)
                       WHERE ConnectionId = @ConnectionId AND GroupId = @GroupId AND MemberId = @MemberId)
            INSERT INTO dbo.GroupMembers (ConnectionId, GroupId, MemberId, MemberType, IdentitySource)
            VALUES (@ConnectionId, @GroupId, @MemberId, @MemberType, @IdentitySource);
    COMMIT TRANSACTION;
END;
GO

-- Remove a single member.  Mirrors SQLite: keyed on member id only.
CREATE OR ALTER PROCEDURE dbo.usp_RemoveMember
    @ConnectionId nvarchar(64),
    @GroupId      nvarchar(128),
    @MemberId     nvarchar(256)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
        DELETE FROM dbo.GroupMembers
         WHERE ConnectionId = @ConnectionId AND GroupId = @GroupId AND MemberId = @MemberId;
    COMMIT TRANSACTION;
END;
GO

-- Create a new sync session record (status 'running').
CREATE OR ALTER PROCEDURE dbo.usp_StartSession
    @SessionId    uniqueidentifier,
    @ConnectionId nvarchar(64),
    @CrawlType    nvarchar(32) = N'identity',
    @SyncType     nvarchar(16) = N'full'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
        INSERT INTO dbo.SyncSessions (SessionId, ConnectionId, CrawlType, SyncType, Status, StartedUtc)
        VALUES (@SessionId, @ConnectionId, @CrawlType, @SyncType, N'running', SYSUTCDATETIME());
    COMMIT TRANSACTION;
END;
GO

-- Finalise a sync session.  @StatsJson is the serialized SyncSessionStats
-- (PyJson format); sync_type inside it overrides the row's SyncType, mirroring
-- the SQLite UPDATE.
CREATE OR ALTER PROCEDURE dbo.usp_CompleteSession
    @SessionId uniqueidentifier,
    @Status    nvarchar(16) = N'completed',
    @StatsJson nvarchar(max) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
        UPDATE dbo.SyncSessions
           SET CompletedUtc = SYSUTCDATETIME(),
               Status       = @Status,
               StatsJson    = @StatsJson,
               SyncType     = COALESCE(JSON_VALUE(@StatsJson, '$.sync_type'), SyncType)
         WHERE SessionId = @SessionId;
    COMMIT TRANSACTION;
END;
GO

-- Latest completed session, optionally filtered by crawl type.
CREATE OR ALTER PROCEDURE dbo.usp_GetLastSession
    @ConnectionId nvarchar(64),
    @CrawlType    nvarchar(32) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (1) SessionId, CrawlType, SyncType, StartedUtc, CompletedUtc, Status, StatsJson
    FROM dbo.SyncSessions
    WHERE ConnectionId = @ConnectionId
      AND Status = N'completed'
      AND (@CrawlType IS NULL OR CrawlType = @CrawlType)
    ORDER BY CompletedUtc DESC;
END;
GO

-- Timestamps of the last successful content crawl.  The incremental crawl
-- uses StartedUtc as its "since" watermark (mirrors the SQLite store, which
-- returns started_at of the row with the newest completed_at).
CREATE OR ALTER PROCEDURE dbo.usp_GetLastSuccessfulContentCrawl
    @ConnectionId nvarchar(64)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (1) StartedUtc, CompletedUtc
    FROM dbo.SyncSessions
    WHERE ConnectionId = @ConnectionId
      AND CrawlType = N'content'
      AND Status = N'completed'
    ORDER BY CompletedUtc DESC;
END;
GO

-- Cached field list for one org/object, or empty result set.
CREATE OR ALTER PROCEDURE dbo.usp_GetCachedFields
    @InstanceHash nvarchar(16),
    @ObjectType   nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT FieldsJson
    FROM dbo.FieldCache
    WHERE InstanceHash = @InstanceHash AND ObjectType = @ObjectType;
END;
GO

-- Upsert the working field list for one org/object.
CREATE OR ALTER PROCEDURE dbo.usp_SaveCachedFields
    @InstanceHash nvarchar(16),
    @ObjectType   nvarchar(128),
    @FieldsJson   nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
        UPDATE dbo.FieldCache WITH (UPDLOCK, SERIALIZABLE)
           SET FieldsJson = @FieldsJson,
               UpdatedUtc = SYSUTCDATETIME()
         WHERE InstanceHash = @InstanceHash AND ObjectType = @ObjectType;
        IF @@ROWCOUNT = 0
            INSERT INTO dbo.FieldCache (InstanceHash, ObjectType, FieldsJson, UpdatedUtc)
            VALUES (@InstanceHash, @ObjectType, @FieldsJson, SYSUTCDATETIME());
    COMMIT TRANSACTION;
END;
GO

-- Clear cached field entries (all / per-org / per-org+object).
-- Returns the number of rows deleted as a single-row result set.
CREATE OR ALTER PROCEDURE dbo.usp_ClearFieldCache
    @InstanceHash nvarchar(16)  = NULL,
    @ObjectType   nvarchar(128) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
        DELETE FROM dbo.FieldCache
         WHERE (@InstanceHash IS NULL OR InstanceHash = @InstanceHash)
           AND (@ObjectType IS NULL OR ObjectType = @ObjectType);
        DECLARE @Deleted int = @@ROWCOUNT;
    COMMIT TRANSACTION;
    SELECT @Deleted AS Deleted;
END;
GO

-- ═════════════════════════════════════════════════════════════════════════════
-- Stored procedures — sync state / checkpoints / dead-letter
-- (mirror config/sync_state.py file semantics)
-- ═════════════════════════════════════════════════════════════════════════════

CREATE OR ALTER PROCEDURE dbo.usp_ReadLastSync
    @ConnectorId nvarchar(64)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT LastSyncUtc
    FROM dbo.SyncState
    WHERE ConnectorId = @ConnectorId;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_WriteLastSync
    @ConnectorId nvarchar(64),
    @LastSyncUtc datetime2(7)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
        MERGE dbo.SyncState WITH (HOLDLOCK) AS target
        USING (SELECT @ConnectorId AS ConnectorId) AS source
            ON target.ConnectorId = source.ConnectorId
        WHEN MATCHED THEN
            UPDATE SET LastSyncUtc = @LastSyncUtc
        WHEN NOT MATCHED THEN
            INSERT (ConnectorId, LastSyncUtc) VALUES (@ConnectorId, @LastSyncUtc);
    COMMIT TRANSACTION;
END;
GO

-- All checkpoint rows for a connector; C# reassembles {completed: {obj: chunk}}.
CREATE OR ALTER PROCEDURE dbo.usp_ReadCheckpoints
    @ConnectorId nvarchar(64)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT ObjectType, ChunkIndex, SinceIso, UpdatedUtc
    FROM dbo.Checkpoints
    WHERE ConnectorId = @ConnectorId;
END;
GO

-- MERGE upsert of one per-object checkpoint.
CREATE OR ALTER PROCEDURE dbo.usp_WriteCheckpoint
    @ConnectorId nvarchar(64),
    @ObjectType  nvarchar(128),
    @ChunkIndex  int,
    @SinceIso    nvarchar(64) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
        MERGE dbo.Checkpoints WITH (HOLDLOCK) AS target
        USING (SELECT @ConnectorId AS ConnectorId, @ObjectType AS ObjectType) AS source
            ON target.ConnectorId = source.ConnectorId AND target.ObjectType = source.ObjectType
        WHEN MATCHED THEN
            UPDATE SET ChunkIndex = @ChunkIndex,
                       SinceIso   = @SinceIso,
                       UpdatedUtc = SYSUTCDATETIME()
        WHEN NOT MATCHED THEN
            INSERT (ConnectorId, ObjectType, ChunkIndex, SinceIso, UpdatedUtc)
            VALUES (@ConnectorId, @ObjectType, @ChunkIndex, @SinceIso, SYSUTCDATETIME());
    COMMIT TRANSACTION;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_ClearCheckpoints
    @ConnectorId nvarchar(64)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
        DELETE FROM dbo.Checkpoints
         WHERE ConnectorId = @ConnectorId;
    COMMIT TRANSACTION;
END;
GO

-- Bulk-append failed records (atomic; replaces the file-lock approach).
-- @RecordsJson = [{"itemId": "...", "objectType": "...", "error": "...",
--                  "requestBody": "..."|null, "responseBody": "..."|null,
--                  "failedUtc": "ISO-8601"|null}]
-- failedUtc defaults to the current UTC time when omitted.
CREATE OR ALTER PROCEDURE dbo.usp_AppendDeadLetter
    @ConnectorId nvarchar(64),
    @RecordsJson nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
        INSERT INTO dbo.DeadLetter (ConnectorId, ItemId, ObjectType, Error, RequestBody, ResponseBody, FailedUtc)
        SELECT @ConnectorId,
               j.ItemId,
               j.ObjectType,
               j.Error,
               j.RequestBody,
               j.ResponseBody,
               COALESCE(TRY_CONVERT(datetime2(7), j.FailedUtc, 127), SYSUTCDATETIME())
        FROM OPENJSON(@RecordsJson)
        WITH (
            ItemId       nvarchar(64)  '$.itemId',
            ObjectType   nvarchar(128) '$.objectType',
            Error        nvarchar(max) '$.error',
            RequestBody  nvarchar(max) '$.requestBody',
            ResponseBody nvarchar(max) '$.responseBody',
            FailedUtc    nvarchar(64)  '$.failedUtc'
        ) j;
    COMMIT TRANSACTION;
END;
GO

-- Unretried dead-letter rows for a connector.
CREATE OR ALTER PROCEDURE dbo.usp_ReadDeadLetter
    @ConnectorId nvarchar(64)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, ItemId, ObjectType, Error, RequestBody, ResponseBody, FailedUtc
    FROM dbo.DeadLetter
    WHERE ConnectorId = @ConnectorId AND RetriedUtc IS NULL
    ORDER BY Id;
END;
GO

-- Mark rows as retried.  @Ids = JSON array of row ids, e.g. [1, 2, 3].
CREATE OR ALTER PROCEDURE dbo.usp_MarkDeadLetterRetried
    @Ids nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
        UPDATE dbo.DeadLetter
           SET RetriedUtc = SYSUTCDATETIME()
         WHERE Id IN (SELECT TRY_CONVERT(bigint, value) FROM OPENJSON(@Ids));
    COMMIT TRANSACTION;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_ClearDeadLetter
    @ConnectorId nvarchar(64)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
        DELETE FROM dbo.DeadLetter
         WHERE ConnectorId = @ConnectorId;
    COMMIT TRANSACTION;
END;
GO

-- ═════════════════════════════════════════════════════════════════════════════
-- Stored procedures — HA coordination (active-active)
-- ═════════════════════════════════════════════════════════════════════════════

-- Under sp_getapplock('SFC_<connector>', 'Exclusive'): return the open crawl
-- if one exists, else create a CrawlSessions row plus one pending ObjectClaims
-- row per object type.  @ObjectTypesJson = ["Account", "Case", ...]
-- Result set: CrawlId uniqueidentifier, Created bit.
CREATE OR ALTER PROCEDURE dbo.usp_OpenOrJoinCrawl
    @ConnectorId     nvarchar(64),
    @CrawlKind       nvarchar(16),
    @SinceIso        nvarchar(64) = NULL,
    @NodeId          nvarchar(128),
    @ObjectTypesJson nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @CrawlId  uniqueidentifier;
    DECLARE @Created  bit = 0;
    DECLARE @Resource nvarchar(255) = N'SFC_' + @ConnectorId;
    DECLARE @LockResult int;
    BEGIN TRANSACTION;
        EXEC @LockResult = sp_getapplock
            @Resource    = @Resource,
            @LockMode    = 'Exclusive',
            @LockOwner   = 'Transaction',
            @LockTimeout = 30000;
        IF @LockResult < 0
        BEGIN
            ROLLBACK TRANSACTION;
            THROW 50010, N'usp_OpenOrJoinCrawl: could not acquire the crawl coordination applock.', 1;
        END;
        SELECT TOP (1) @CrawlId = CrawlId
        FROM dbo.CrawlSessions
        WHERE ConnectorId = @ConnectorId AND Status = N'open'
        ORDER BY StartedUtc DESC;
        IF @CrawlId IS NULL
        BEGIN
            SET @CrawlId = NEWID();
            SET @Created = 1;
            INSERT INTO dbo.CrawlSessions (CrawlId, ConnectorId, CrawlKind, Status, SinceIso, StartedUtc, CreatedBy)
            VALUES (@CrawlId, @ConnectorId, @CrawlKind, N'open', @SinceIso, SYSUTCDATETIME(), @NodeId);
            INSERT INTO dbo.ObjectClaims (CrawlId, ObjectType, Status)
            SELECT @CrawlId, j.value, N'pending'
            FROM OPENJSON(@ObjectTypesJson) j;
        END;
    COMMIT TRANSACTION;  -- releases the applock
    -- SinceIso/CrawlKind come from the crawl row so JOINING nodes adopt the
    -- creator's incremental boundary (checkpoints must share one 'since').
    SELECT cs.CrawlId, @Created AS Created, cs.SinceIso, cs.CrawlKind
    FROM dbo.CrawlSessions cs
    WHERE cs.CrawlId = @CrawlId;
END;
GO

-- Atomically (UPDLOCK, READPAST) claim one pending row, or reclaim a claimed
-- row whose heartbeat is older than @ClaimTimeoutSeconds.
-- Result set: 0 or 1 rows with ObjectType.
CREATE OR ALTER PROCEDURE dbo.usp_ClaimNextObject
    @CrawlId             uniqueidentifier,
    @NodeId              nvarchar(128),
    @ClaimTimeoutSeconds int = 300
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @Cutoff datetime2(7) = DATEADD(SECOND, -@ClaimTimeoutSeconds, SYSUTCDATETIME());
    DECLARE @Claimed TABLE (ObjectType nvarchar(128));
    WITH candidate AS
    (
        SELECT TOP (1) Status, NodeId, ClaimedUtc, HeartbeatUtc, ObjectType
        FROM dbo.ObjectClaims WITH (UPDLOCK, READPAST, ROWLOCK)
        WHERE CrawlId = @CrawlId
          AND (Status = N'pending'
               OR (Status = N'claimed' AND COALESCE(HeartbeatUtc, ClaimedUtc) < @Cutoff))
        ORDER BY ObjectType
    )
    UPDATE candidate
       SET Status       = N'claimed',
           NodeId       = @NodeId,
           ClaimedUtc   = SYSUTCDATETIME(),
           HeartbeatUtc = SYSUTCDATETIME()
    OUTPUT inserted.ObjectType INTO @Claimed;
    SELECT ObjectType FROM @Claimed;
END;
GO

-- Refresh the heartbeat on an active claim owned by this node.
CREATE OR ALTER PROCEDURE dbo.usp_HeartbeatClaim
    @CrawlId    uniqueidentifier,
    @ObjectType nvarchar(128),
    @NodeId     nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.ObjectClaims
       SET HeartbeatUtc = SYSUTCDATETIME()
     WHERE CrawlId = @CrawlId AND ObjectType = @ObjectType
       AND NodeId = @NodeId AND Status = N'claimed';
END;
GO

-- Mark a claim done|failed.
CREATE OR ALTER PROCEDURE dbo.usp_CompleteClaim
    @CrawlId    uniqueidentifier,
    @ObjectType nvarchar(128),
    @NodeId     nvarchar(128),
    @Status     nvarchar(16)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @Status NOT IN (N'done', N'failed')
        THROW 50011, N'usp_CompleteClaim: @Status must be ''done'' or ''failed''.', 1;
    BEGIN TRANSACTION;
        UPDATE dbo.ObjectClaims
           SET Status       = @Status,
               CompletedUtc = SYSUTCDATETIME(),
               HeartbeatUtc = SYSUTCDATETIME()
         WHERE CrawlId = @CrawlId AND ObjectType = @ObjectType AND NodeId = @NodeId;
    COMMIT TRANSACTION;
END;
GO

-- Close the crawl when no pending/claimed rows remain ('failed' if any claim
-- failed).  Result set: Closed = 1 when THIS call performed the close (that
-- caller writes the sync timestamp), else 0.
CREATE OR ALTER PROCEDURE dbo.usp_CloseCrawlIfComplete
    @CrawlId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @Closed int = 0;
    BEGIN TRANSACTION;
        IF NOT EXISTS (SELECT 1 FROM dbo.ObjectClaims WITH (UPDLOCK, HOLDLOCK)
                       WHERE CrawlId = @CrawlId AND Status IN (N'pending', N'claimed'))
        BEGIN
            UPDATE dbo.CrawlSessions
               SET Status = CASE WHEN EXISTS (SELECT 1 FROM dbo.ObjectClaims
                                              WHERE CrawlId = @CrawlId AND Status = N'failed')
                                 THEN N'failed' ELSE N'closed' END,
                   ClosedUtc = SYSUTCDATETIME()
             WHERE CrawlId = @CrawlId AND Status = N'open';
            SET @Closed = @@ROWCOUNT;
        END;
    COMMIT TRANSACTION;
    SELECT @Closed AS Closed;
END;
GO

-- Prune history older than @RetentionDays: retried dead-letter rows, sync
-- sessions, and crawl sessions (+claims, via FK cascade).  Open/running rows
-- are never pruned.  Result set: rows deleted per table.
CREATE OR ALTER PROCEDURE dbo.usp_PruneHistory
    @RetentionDays int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @DeadLetterDeleted int = 0, @SyncSessionsDeleted int = 0, @CrawlSessionsDeleted int = 0;
    IF @RetentionDays IS NULL OR @RetentionDays <= 0
    BEGIN
        SELECT @DeadLetterDeleted AS DeadLetterDeleted,
               @SyncSessionsDeleted AS SyncSessionsDeleted,
               @CrawlSessionsDeleted AS CrawlSessionsDeleted;
        RETURN;
    END;
    DECLARE @Cutoff datetime2(7) = DATEADD(DAY, -@RetentionDays, SYSUTCDATETIME());
    BEGIN TRANSACTION;
        DELETE FROM dbo.DeadLetter
         WHERE RetriedUtc IS NOT NULL AND RetriedUtc < @Cutoff;
        SET @DeadLetterDeleted = @@ROWCOUNT;
        DELETE FROM dbo.SyncSessions
         WHERE StartedUtc < @Cutoff AND Status <> N'running';
        SET @SyncSessionsDeleted = @@ROWCOUNT;
        DELETE FROM dbo.CrawlSessions
         WHERE StartedUtc < @Cutoff AND Status <> N'open';
        SET @CrawlSessionsDeleted = @@ROWCOUNT;
    COMMIT TRANSACTION;
    SELECT @DeadLetterDeleted AS DeadLetterDeleted,
           @SyncSessionsDeleted AS SyncSessionsDeleted,
           @CrawlSessionsDeleted AS CrawlSessionsDeleted;
END;
GO
